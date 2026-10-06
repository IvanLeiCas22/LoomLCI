using System.Text.Json;

namespace LoomLCI.Launcher;

public sealed record UpdateReleaseManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Channel { get; init; } = UpdateTrust.DefaultChannel;
    public required long Sequence { get; init; }
    public required string Version { get; init; }
    public string Platform { get; init; } = "win-x64";
    public int MinUpdateProtocol { get; init; } = 1;
    public required string PackageUrl { get; init; }
    public required long PackageSizeBytes { get; init; }
    public required string PackageSha256 { get; init; }
}

public static class UpdateReleaseManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static UpdateReleaseManifest Load(ReadOnlySpan<byte> json)
    {
        var manifest = JsonSerializer.Deserialize<UpdateReleaseManifest>(
            json,
            JsonOptions)
            ?? throw new InvalidDataException(
                "El manifest de update está vacío o es inválido.");

        Validate(manifest);
        return manifest;
    }

    public static void Validate(UpdateReleaseManifest manifest)
    {
        if (manifest.SchemaVersion != UpdateReleaseManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Schema de update no soportado: {manifest.SchemaVersion}.");
        }

        if (!string.Equals(
                manifest.Channel,
                UpdateTrust.DefaultChannel,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Canal de update no soportado: {manifest.Channel}.");
        }

        VersionName.Validate(manifest.Version);

        if (!string.Equals(
                manifest.Platform,
                "win-x64",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Plataforma de update no soportada: {manifest.Platform}.");
        }

        if (manifest.Sequence <= 0)
        {
            throw new InvalidDataException(
                "El manifest de update debe declarar sequence > 0.");
        }

        if (manifest.MinUpdateProtocol <= 0)
        {
            throw new InvalidDataException(
                "minUpdateProtocol debe ser mayor que cero.");
        }

        if (!Uri.TryCreate(
                manifest.PackageUrl,
                UriKind.Absolute,
                out var packageUri) ||
            !(string.Equals(
                  packageUri.Scheme,
                  Uri.UriSchemeHttps,
                  StringComparison.OrdinalIgnoreCase) ||
              (string.Equals(
                   packageUri.Scheme,
                   Uri.UriSchemeHttp,
                   StringComparison.OrdinalIgnoreCase) &&
               packageUri.IsLoopback)))
        {
            throw new InvalidDataException(
                "packageUrl debe usar HTTPS; HTTP sólo se admite en loopback para validación local.");
        }

        if (manifest.PackageSizeBytes <= 0)
        {
            throw new InvalidDataException(
                "packageSizeBytes debe ser mayor que cero.");
        }

        if (manifest.PackageSha256.Length != 64 ||
            manifest.PackageSha256.Any(ch =>
                !Uri.IsHexDigit(ch)))
        {
            throw new InvalidDataException(
                "packageSha256 debe ser un SHA-256 hexadecimal.");
        }
    }
}

public sealed record UpdateCheckResult(
    string CurrentVersion,
    long CurrentSequence,
    long HighestSequence,
    UpdateReleaseManifest Release,
    bool UpdateAvailable,
    bool RequiresNewInstaller);

public sealed record UpdateApplyResult(
    bool Changed,
    string ActiveVersion,
    long ActiveSequence,
    string? PreviousVersion,
    long PreviousSequence);
