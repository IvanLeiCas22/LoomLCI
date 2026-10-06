using System.Text.Json;

namespace LoomLCI.Launcher;

public enum UpdateJournalStage
{
    Prepared = 0,
    Stopping = 1,
    RuntimeStopped = 2,
    Activated = 3,
    Starting = 4,
    RuntimeStarted = 5
}

public sealed record UpdateJournal
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Operation { get; init; }
    public required string OperationId { get; init; }
    public required UpdateJournalStage Stage { get; init; }
    public required MachineConfig OriginalConfig { get; init; }
    public required MachineConfig TargetConfig { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
}

public static class UpdateJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static UpdateJournal? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var journal = JsonSerializer.Deserialize<UpdateJournal>(
            File.ReadAllText(path),
            JsonOptions)
            ?? throw new InvalidDataException(
                "El journal de update está vacío o es inválido.");

        if (journal.SchemaVersion != UpdateJournal.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Schema de journal no soportado: {journal.SchemaVersion}.");
        }

        return journal;
    }

    public static void Save(string path, UpdateJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(journal, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var temp = path + ".tmp";
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }
    }
}
