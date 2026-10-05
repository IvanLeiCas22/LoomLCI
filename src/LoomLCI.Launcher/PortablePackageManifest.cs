using System.Text.Json;

namespace LoomLCI.Launcher;

public sealed record PortablePackageManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Version { get; init; }
    public string HostRelativePath { get; init; } = "payload/host";
}

public static class PortablePackageManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static PortablePackageManifest Load(string packageRoot)
    {
        var path = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Paquete inválido: falta {path}.");
        }

        var manifest = JsonSerializer.Deserialize<PortablePackageManifest>(
            File.ReadAllText(path),
            JsonOptions)
            ?? throw new InvalidDataException("package.json está vacío o es inválido.");

        if (manifest.SchemaVersion != PortablePackageManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Schema de package.json no soportado: {manifest.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidDataException("package.json no declara una versión.");
        }

        return manifest;
    }
}
