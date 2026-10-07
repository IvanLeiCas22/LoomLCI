using System.Reflection;
using System.Text.Json;

namespace LoomLCI.Windows.Python;

internal sealed record PythonPackageManagerManifest(
    string Version,
    string Architecture,
    string Url,
    string Sha256,
    string Executable);

internal sealed record PythonPackageManagerInstallation(
    string ExecutablePath,
    PythonPackageManagerManifest Manifest);

internal static class PythonPackageManagerAssets
{
    private const string ManifestResourceName =
        "LoomLCI.Python.uv.json";

    private static readonly Lazy<PythonPackageManagerManifest> ManifestValue =
        new(LoadManifestCore, LazyThreadSafetyMode.ExecutionAndPublication);

    public static PythonPackageManagerManifest Manifest
        => ManifestValue.Value;

    private static PythonPackageManagerManifest LoadManifestCore()
    {
        var assembly = typeof(PythonPackageManagerAssets).Assembly;
        using var stream = assembly.GetManifestResourceStream(
                ManifestResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ManifestResourceName}' was not found.");

        var manifest = JsonSerializer.Deserialize<PythonPackageManagerManifest>(
            stream,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        if (manifest is null ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.Architecture) ||
            string.IsNullOrWhiteSpace(manifest.Url) ||
            manifest.Sha256.Length != 64 ||
            string.IsNullOrWhiteSpace(manifest.Executable))
        {
            throw new InvalidOperationException(
                "Embedded Python package manager manifest is invalid.");
        }

        return manifest with
        {
            Sha256 = manifest.Sha256.ToLowerInvariant()
        };
    }
}
