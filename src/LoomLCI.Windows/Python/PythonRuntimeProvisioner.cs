using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using LoomLCI.Core;

namespace LoomLCI.Windows.Python;

internal interface IPythonRuntimeProvisioner
{
    Task<LoomResult<PythonRuntimeInstallation>> EnsureAsync(
        CancellationToken cancellationToken);
}

internal sealed class PythonRuntimeProvisioner : IPythonRuntimeProvisioner
{
    internal const long MaxArchiveBytes = 64L * 1024 * 1024;

    private static readonly SemaphoreSlim ProvisionGate = new(1, 1);
    private static readonly HttpClient SharedHttpClient = new();

    private readonly HttpClient _httpClient;
    private readonly string _loomRootDirectory;
    private readonly PythonRuntimeManifest _manifest;
    private readonly byte[]? _workerBytesOverride;

    public PythonRuntimeProvisioner()
        : this(
            SharedHttpClient,
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "LoomLCI"),
            PythonRuntimeAssets.Manifest,
            workerBytesOverride: null)
    {
    }

    internal PythonRuntimeProvisioner(
        HttpClient httpClient,
        string loomRootDirectory,
        PythonRuntimeManifest manifest,
        byte[]? workerBytesOverride = null)
    {
        _httpClient = httpClient;
        _loomRootDirectory = loomRootDirectory;
        _manifest = manifest;
        _workerBytesOverride = workerBytesOverride;
    }

    public async Task<LoomResult<PythonRuntimeInstallation>> EnsureAsync(
        CancellationToken cancellationToken)
    {
        await ProvisionGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(_loomRootDirectory);

            var workerPath = _workerBytesOverride is null
                ? await PythonRuntimeAssets.MaterializeWorkerAsync(
                        _loomRootDirectory,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await MaterializeWorkerOverrideAsync(
                        _workerBytesOverride,
                        cancellationToken)
                    .ConfigureAwait(false);

            var runtimeDirectory = RuntimeDirectory();
            if (IsInstalled(runtimeDirectory))
            {
                return LoomResult<PythonRuntimeInstallation>.Success(
                    Installation(runtimeDirectory, workerPath));
            }

            return await InstallAsync(
                    runtimeDirectory,
                    workerPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return LoomResult<PythonRuntimeInstallation>.Failure(
                LoomErrors.AccessDenied(
                    $"Could not provision private Python runtime: {ex.Message}"));
        }
        catch (Exception ex) when (
            ex is IOException or
            HttpRequestException or
            InvalidDataException or
            JsonException or
            CryptographicException or
            InvalidOperationException)
        {
            return LoomResult<PythonRuntimeInstallation>.Failure(
                LoomErrors.ExecutionFailed(
                    $"Could not provision private Python runtime: {ex.Message}"));
        }
        finally
        {
            ProvisionGate.Release();
        }
    }

    private async Task<LoomResult<PythonRuntimeInstallation>> InstallAsync(
        string runtimeDirectory,
        string workerPath,
        CancellationToken cancellationToken)
    {
        var parentDirectory = Path.GetDirectoryName(runtimeDirectory)
            ?? throw new InvalidOperationException(
                "Python runtime directory has no parent.");
        Directory.CreateDirectory(parentDirectory);

        var stagingDirectory = Path.Combine(
            parentDirectory,
            $".staging-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(
            stagingDirectory,
            "python-runtime.zip");
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
                return LoomResult<PythonRuntimeInstallation>.Failure(
                    LoomErrors.ExecutionFailed(
                        "Private Python runtime archive failed SHA-256 verification."));
            }

            Directory.CreateDirectory(payloadDirectory);
            ZipFile.ExtractToDirectory(
                archivePath,
                payloadDirectory,
                overwriteFiles: false);

            var pythonExecutable = Path.Combine(
                payloadDirectory,
                "python.exe");
            var pthFile = Path.Combine(
                payloadDirectory,
                PthFileName());
            if (!File.Exists(pythonExecutable) ||
                !File.Exists(pthFile))
            {
                return LoomResult<PythonRuntimeInstallation>.Failure(
                    LoomErrors.ExecutionFailed(
                        "Private Python runtime archive does not contain the expected embeddable layout."));
            }

            await WriteMarkerAsync(
                    payloadDirectory,
                    cancellationToken)
                .ConfigureAwait(false);

            PublishRuntime(
                payloadDirectory,
                runtimeDirectory);

            return LoomResult<PythonRuntimeInstallation>.Success(
                Installation(runtimeDirectory, workerPath));
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
                $"Runtime download returned HTTP {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength > MaxArchiveBytes)
        {
            throw new InvalidDataException(
                "Private Python runtime archive exceeds the download size limit.");
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
                    "Private Python runtime archive exceeds the download size limit.");
            }

            await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private bool IsInstalled(string runtimeDirectory)
    {
        var pythonExecutable = Path.Combine(
            runtimeDirectory,
            "python.exe");
        var markerPath = MarkerPath(runtimeDirectory);

        if (!File.Exists(pythonExecutable) ||
            !File.Exists(Path.Combine(
                runtimeDirectory,
                PthFileName())) ||
            !File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            var marker = JsonSerializer.Deserialize<PythonRuntimeManifest>(
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
                       marker.Distribution,
                       _manifest.Distribution,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       marker.Sha256,
                       _manifest.Sha256,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void PublishRuntime(
        string payloadDirectory,
        string runtimeDirectory)
    {
        string? backupDirectory = null;

        try
        {
            if (Directory.Exists(runtimeDirectory))
            {
                backupDirectory =
                    runtimeDirectory + $".old-{Guid.NewGuid():N}";
                Directory.Move(
                    runtimeDirectory,
                    backupDirectory);
            }

            Directory.Move(
                payloadDirectory,
                runtimeDirectory);

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
                !Directory.Exists(runtimeDirectory))
            {
                try
                {
                    Directory.Move(
                        backupDirectory,
                        runtimeDirectory);
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

    private async Task<string> MaterializeWorkerOverrideAsync(
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(bytes));
        var directory = Path.Combine(
            _loomRootDirectory,
            "assets",
            "python");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(
            directory,
            $"worker-{hash[..16]}.py");
        if (!File.Exists(path))
        {
            await File.WriteAllBytesAsync(
                    path,
                    bytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return path;
    }

    private async Task WriteMarkerAsync(
        string runtimeDirectory,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            _manifest,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        await File.WriteAllBytesAsync(
                MarkerPath(runtimeDirectory),
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

    private string RuntimeDirectory()
        => Path.Combine(
            _loomRootDirectory,
            "runtimes",
            "python",
            $"{_manifest.Version}-{_manifest.Architecture}");

    private string PthFileName()
    {
        var parts = _manifest.Version.Split('.');
        if (parts.Length < 2 ||
            !int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor))
        {
            throw new InvalidOperationException(
                $"Python runtime version '{_manifest.Version}' is invalid.");
        }

        return $"python{major}{minor}._pth";
    }

    private PythonRuntimeInstallation Installation(
        string runtimeDirectory,
        string workerPath)
        => new(
            Path.Combine(runtimeDirectory, "python.exe"),
            workerPath,
            _manifest);

    private static string MarkerPath(string runtimeDirectory)
        => Path.Combine(
            runtimeDirectory,
            ".loom-runtime.json");

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
