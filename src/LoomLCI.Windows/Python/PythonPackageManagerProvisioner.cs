using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using LoomLCI.Core;

namespace LoomLCI.Windows.Python;

internal interface IPythonPackageManagerProvisioner
{
    Task<LoomResult<PythonPackageManagerInstallation>> EnsureAsync(
        CancellationToken cancellationToken);
}

internal sealed class PythonPackageManagerProvisioner
    : IPythonPackageManagerProvisioner
{
    internal const long MaxArchiveBytes = 64L * 1024 * 1024;

    private static readonly SemaphoreSlim ProvisionGate = new(1, 1);
    private static readonly HttpClient SharedHttpClient = new();

    private readonly HttpClient _httpClient;
    private readonly string _loomRootDirectory;
    private readonly PythonPackageManagerManifest _manifest;

    public PythonPackageManagerProvisioner()
        : this(
            SharedHttpClient,
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "LoomLCI"),
            PythonPackageManagerAssets.Manifest)
    {
    }

    internal PythonPackageManagerProvisioner(
        HttpClient httpClient,
        string loomRootDirectory,
        PythonPackageManagerManifest manifest)
    {
        _httpClient = httpClient;
        _loomRootDirectory = loomRootDirectory;
        _manifest = manifest;
    }

    public async Task<LoomResult<PythonPackageManagerInstallation>> EnsureAsync(
        CancellationToken cancellationToken)
    {
        await ProvisionGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(_loomRootDirectory);

            var installDirectory = InstallDirectory();
            if (IsInstalled(installDirectory))
            {
                return LoomResult<PythonPackageManagerInstallation>.Success(
                    Installation(installDirectory));
            }

            return await InstallAsync(
                    installDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return LoomResult<PythonPackageManagerInstallation>.Failure(
                LoomErrors.AccessDenied(
                    $"Could not provision private Python package manager: {ex.Message}"));
        }
        catch (Exception ex) when (
            ex is IOException or
            HttpRequestException or
            InvalidDataException or
            JsonException or
            CryptographicException or
            InvalidOperationException)
        {
            return LoomResult<PythonPackageManagerInstallation>.Failure(
                LoomErrors.ExecutionFailed(
                    $"Could not provision private Python package manager: {ex.Message}"));
        }
        finally
        {
            ProvisionGate.Release();
        }
    }

    private async Task<LoomResult<PythonPackageManagerInstallation>> InstallAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var parentDirectory = Path.GetDirectoryName(installDirectory)
            ?? throw new InvalidOperationException(
                "Python package manager directory has no parent.");
        Directory.CreateDirectory(parentDirectory);

        var stagingDirectory = Path.Combine(
            parentDirectory,
            $".staging-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(
            stagingDirectory,
            "uv.zip");
        var payloadDirectory = Path.Combine(
            stagingDirectory,
            "payload");

        Directory.CreateDirectory(stagingDirectory);

        try
        {
            await DownloadArchiveAsync(
                    archivePath,
                    cancellationToken)
                .ConfigureAwait(false);

            var actualHash = await ComputeSha256Async(
                    archivePath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    actualHash,
                    _manifest.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return LoomResult<PythonPackageManagerInstallation>.Failure(
                    LoomErrors.ExecutionFailed(
                        "Private Python package manager archive failed SHA-256 verification."));
            }

            Directory.CreateDirectory(payloadDirectory);
            ZipFile.ExtractToDirectory(
                archivePath,
                payloadDirectory,
                overwriteFiles: false);

            var executablePath = Path.Combine(
                payloadDirectory,
                _manifest.Executable);
            if (!File.Exists(executablePath))
            {
                return LoomResult<PythonPackageManagerInstallation>.Failure(
                    LoomErrors.ExecutionFailed(
                        $"Private Python package manager archive does not contain '{_manifest.Executable}'."));
            }

            await WriteMarkerAsync(
                    payloadDirectory,
                    cancellationToken)
                .ConfigureAwait(false);

            Publish(
                payloadDirectory,
                installDirectory);

            return LoomResult<PythonPackageManagerInstallation>.Success(
                Installation(installDirectory));
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private async Task DownloadArchiveAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
                new Uri(_manifest.Url, UriKind.Absolute),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"Package manager download returned HTTP {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength > MaxArchiveBytes)
        {
            throw new InvalidDataException(
                "Private Python package manager archive exceeds the download size limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(
                cancellationToken)
            .ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);

        var buffer = new byte[64 * 1024];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(
                    buffer,
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaxArchiveBytes)
            {
                throw new InvalidDataException(
                    "Private Python package manager archive exceeds the download size limit.");
            }

            await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private bool IsInstalled(string installDirectory)
    {
        var executablePath = Path.Combine(
            installDirectory,
            _manifest.Executable);
        var markerPath = MarkerPath(installDirectory);

        if (!File.Exists(executablePath) ||
            !File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            var marker =
                JsonSerializer.Deserialize<PythonPackageManagerManifest>(
                    File.ReadAllBytes(markerPath),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

            return marker is not null &&
                   string.Equals(
                       marker.Version,
                       _manifest.Version,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       marker.Architecture,
                       _manifest.Architecture,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       marker.Url,
                       _manifest.Url,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       marker.Sha256,
                       _manifest.Sha256,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       marker.Executable,
                       _manifest.Executable,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void Publish(
        string payloadDirectory,
        string installDirectory)
    {
        string? backupDirectory = null;

        try
        {
            if (Directory.Exists(installDirectory))
            {
                backupDirectory =
                    installDirectory + $".old-{Guid.NewGuid():N}";
                Directory.Move(
                    installDirectory,
                    backupDirectory);
            }

            Directory.Move(
                payloadDirectory,
                installDirectory);

            if (backupDirectory is not null)
            {
                TryDeleteDirectory(backupDirectory);
                backupDirectory = null;
            }
        }
        catch
        {
            if (backupDirectory is not null &&
                Directory.Exists(backupDirectory) &&
                !Directory.Exists(installDirectory))
            {
                try
                {
                    Directory.Move(
                        backupDirectory,
                        installDirectory);
                    backupDirectory = null;
                }
                catch
                {
                }
            }

            throw;
        }
        finally
        {
            if (backupDirectory is not null)
            {
                TryDeleteDirectory(backupDirectory);
            }
        }
    }

    private async Task WriteMarkerAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            _manifest,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        await File.WriteAllBytesAsync(
                MarkerPath(directory),
                bytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(
                stream,
                cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private string InstallDirectory()
        => Path.Combine(
            _loomRootDirectory,
            "tools",
            "python",
            "uv",
            $"{_manifest.Version}-{_manifest.Architecture}");

    private PythonPackageManagerInstallation Installation(
        string installDirectory)
        => new(
            Path.Combine(
                installDirectory,
                _manifest.Executable),
            _manifest);

    private static string MarkerPath(string installDirectory)
        => Path.Combine(
            installDirectory,
            ".loom-uv.json");

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
