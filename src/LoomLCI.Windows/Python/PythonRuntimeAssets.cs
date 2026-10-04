using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LoomLCI.Windows.Python;

internal sealed record PythonRuntimeManifest(
    string Version,
    string Architecture,
    string Distribution,
    string Url,
    string Sha256);

internal sealed record PythonRuntimeInstallation(
    string PythonExecutablePath,
    string WorkerScriptPath,
    PythonRuntimeManifest Manifest);

internal static class PythonRuntimeAssets
{
    private const string ManifestResourceName = "LoomLCI.Python.runtime.json";
    private const string WorkerResourceName = "LoomLCI.Python.worker.py";

    private static readonly Lazy<PythonRuntimeManifest> ManifestValue =
        new(LoadManifestCore, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<byte[]> WorkerValue =
        new(() => ReadResourceBytes(WorkerResourceName),
            LazyThreadSafetyMode.ExecutionAndPublication);

    public static PythonRuntimeManifest Manifest => ManifestValue.Value;

    public static byte[] WorkerBytes => WorkerValue.Value.ToArray();

    public static string WorkerSha256
        => Convert.ToHexStringLower(SHA256.HashData(WorkerValue.Value));

    public static async Task<string> MaterializeWorkerAsync(
        string loomRootDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loomRootDirectory);

        var directory = Path.Combine(
            loomRootDirectory,
            "assets",
            "python");
        Directory.CreateDirectory(directory);

        var finalPath = Path.Combine(
            directory,
            $"worker-{WorkerSha256[..16]}.py");

        if (File.Exists(finalPath))
        {
            return finalPath;
        }

        var temporaryPath =
            finalPath + $".tmp-{Guid.NewGuid():N}";

        try
        {
            await File.WriteAllBytesAsync(
                    temporaryPath,
                    WorkerValue.Value,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                File.Move(temporaryPath, finalPath);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                File.Delete(temporaryPath);
            }

            return finalPath;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static PythonRuntimeManifest LoadManifestCore()
    {
        var bytes = ReadResourceBytes(ManifestResourceName);
        var manifest = JsonSerializer.Deserialize<PythonRuntimeManifest>(
            bytes,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        if (manifest is null ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.Architecture) ||
            string.IsNullOrWhiteSpace(manifest.Distribution) ||
            string.IsNullOrWhiteSpace(manifest.Url) ||
            manifest.Sha256.Length != 64)
        {
            throw new InvalidOperationException(
                "Embedded Python runtime manifest is invalid.");
        }

        return manifest with
        {
            Sha256 = manifest.Sha256.ToLowerInvariant()
        };
    }

    private static byte[] ReadResourceBytes(string resourceName)
    {
        var assembly = typeof(PythonRuntimeAssets).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' was not found.");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
