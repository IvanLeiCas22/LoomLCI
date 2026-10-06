using System.Text.Json;

namespace LoomLCI.Launcher;

public sealed record PortablePackageManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Version { get; init; }
    public long Sequence { get; init; }
    public int UpdateProtocol { get; init; } = 1;
    public string Platform { get; init; } = "win-x64";
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

        VersionName.Validate(manifest.Version);

        if (manifest.Sequence < 0)
        {
            throw new InvalidDataException(
                "package.json declara sequence negativo.");
        }

        if (manifest.UpdateProtocol <= 0)
        {
            throw new InvalidDataException(
                "package.json declara updateProtocol inválido.");
        }

        if (!string.Equals(
                manifest.Platform,
                "win-x64",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"package.json declara plataforma no soportada: {manifest.Platform}.");
        }

        return manifest;
    }
}
