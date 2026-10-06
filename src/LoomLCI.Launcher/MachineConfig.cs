using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoomLCI.Launcher;

public sealed record MachineConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string ActiveVersion { get; init; }
    public long ActiveSequence { get; init; }
    public string? PreviousVersion { get; init; }
    public long PreviousSequence { get; init; }
    public long HighestSequence { get; init; }
    public required string TunnelId { get; init; }
    public required string Alias { get; init; }
    public required string ProfileName { get; init; }
    public required string TunnelClientVersion { get; init; }
    public required string TunnelClientArchiveSha256 { get; init; }
}

public static class MachineConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static MachineConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"LoomLCI no está configurado. Falta {path}.");
        }

        var config = JsonSerializer.Deserialize<MachineConfig>(
            File.ReadAllText(path),
            JsonOptions)
            ?? throw new InvalidDataException("machine.json está vacío o es inválido.");

        if (config.SchemaVersion != MachineConfig.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Schema de machine.json no soportado: {config.SchemaVersion}.");
        }

        VersionName.Validate(config.ActiveVersion);
        if (!string.IsNullOrWhiteSpace(config.PreviousVersion))
        {
            VersionName.Validate(config.PreviousVersion);
        }

        if (config.ActiveSequence < 0 ||
            config.PreviousSequence < 0 ||
            config.HighestSequence < config.ActiveSequence ||
            config.HighestSequence < config.PreviousSequence)
        {
            throw new InvalidDataException(
                "machine.json contiene secuencias de update inválidas.");
        }

        return config;
    }

    public static void Save(string path, MachineConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
