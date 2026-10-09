using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LoomLCI.Core.Observability;

/// <summary>Local, opt-in, bounded diagnostics. Never serializes arbitrary event payloads.</summary>
public sealed record DiagnosticRecord(
    DateTimeOffset Timestamp,
    string Component,
    string Kind,
    string? Operation = null,
    string? Outcome = null,
    string? Code = null,
    long? Count = null,
    long? DurationMs = null);

public static class DiagnosticsLog
{
    public const int RetentionDays = 7;
    public const long MaxFileBytes = 4L * 1024 * 1024;
    public const long MaxComponentBytes = 16L * 1024 * 1024;
    public const long MaxCombinedBytes = 2 * MaxComponentBytes;

    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly object Sync = new();
    private static readonly Regex Symbol = new(
        "^[a-zA-Z][a-zA-Z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);

    public static string ConfigPath(string dataRoot) =>
        Path.Combine(dataRoot, "config", "diagnostics.json");

    public static string LogsPath(string dataRoot) =>
        Path.Combine(dataRoot, "logs", "diagnostics");

    public static bool IsEnabled(string dataRoot)
    {
        try
        {
            var path = ConfigPath(dataRoot);
            if (!File.Exists(path))
                return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("schemaVersion", out var schema) &&
                schema.ValueKind == JsonValueKind.Number &&
                schema.GetInt32() == 1 &&
                root.TryGetProperty("enabled", out var enabled) &&
                enabled.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or FormatException)
        {
            // Fail closed for missing/corrupt configuration.
            return false;
        }
    }

    public static void SetEnabled(string dataRoot, bool enabled)
    {
        var path = ConfigPath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, enabled
                ? "{\"schemaVersion\":1,\"enabled\":true}"
                : "{\"schemaVersion\":1,\"enabled\":false}", Utf8);
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public static bool TryAppend(string dataRoot, DiagnosticRecord record)
    {
        try
        {
            if (!IsEnabled(dataRoot))
                return false;
            if (record.Component is not ("host" or "launcher") ||
                !Valid(record.Kind) ||
                (record.Operation is not null && !Valid(record.Operation)) ||
                (record.Outcome is not null && !Valid(record.Outcome)) ||
                (record.Code is not null && !Valid(record.Code)) ||
                (record.Count is < 0) ||
                (record.DurationMs is < 0 or > 86_400_000))
                return false;

            var line = JsonSerializer.Serialize(new
            {
                timestamp = record.Timestamp,
                component = record.Component,
                kind = record.Kind,
                operation = record.Operation,
                outcome = record.Outcome,
                code = record.Code,
                count = record.Count,
                durationMs = record.DurationMs
            }) + "\n";
            var bytes = Utf8.GetByteCount(line);
            if (bytes > 2048)
                return false;

            lock (Sync)
            {
                var root = LogsPath(dataRoot);
                Directory.CreateDirectory(root);
                var stem = record.Component + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd") +
                    "-" + Environment.ProcessId;
                var part = 0;
                string path;
                while (true)
                {
                    path = Path.Combine(root, stem + "-" + part.ToString("D4") + ".jsonl");
                    if (!File.Exists(path) || new FileInfo(path).Length + bytes <= MaxFileBytes)
                        break;
                    part++;
                }
                Prune(root, record.Component, path, bytes);
                File.AppendAllText(path, line, Utf8);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            // Logging failures must never fail an MCP invocation or Launcher operation.
            return false;
        }
    }

    public static (int Files, long Bytes) GetUsage(string dataRoot)
    {
        var path = LogsPath(dataRoot);
        if (!Directory.Exists(path))
            return (0, 0);
        var files = ManagedFiles(path).ToArray();
        return (files.Length, files.Sum(file => file.Length));
    }

    public static IReadOnlyList<string> ReadTail(string dataRoot, int lines)
    {
        if (lines is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(lines));
        var path = LogsPath(dataRoot);
        if (!Directory.Exists(path))
            return [];
        var files = ManagedFiles(path).OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name,
            StringComparer.Ordinal).ToArray();
        var result = new Queue<string>();
        foreach (var file in files)
        {
            if (file.Length > MaxFileBytes)
                continue;
            foreach (var line in File.ReadLines(file.FullName, Utf8))
            {
                if (line.Length is < 2 or > 2048)
                    continue;
                try
                {
                    using var json = JsonDocument.Parse(line);
                    if (json.RootElement.ValueKind != JsonValueKind.Object ||
                        !json.RootElement.TryGetProperty("kind", out _))
                        continue;
                }
                catch (JsonException)
                {
                    continue;
                }
                result.Enqueue(line);
                if (result.Count > lines)
                    result.Dequeue();
            }
        }
        return result.ToArray();
    }

    public static int Clear(string dataRoot)
    {
        var path = LogsPath(dataRoot);
        if (!Directory.Exists(path))
            return 0;
        var count = 0;
        foreach (var file in ManagedFiles(path))
        {
            file.Delete();
            count++;
        }
        return count;
    }

    private static bool Valid(string value) => Symbol.IsMatch(value);

    private static IEnumerable<FileInfo> ManagedFiles(string root) =>
        new DirectoryInfo(root).EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly)
            .Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                (f.Name.StartsWith("host-", StringComparison.Ordinal) ||
                 f.Name.StartsWith("launcher-", StringComparison.Ordinal)));

    private static void Prune(string root, string component, string incoming, int newBytes)
    {
        var now = DateTime.UtcNow;
        var files = ManagedFiles(root)
            .Where(f => f.Name.StartsWith(component + "-", StringComparison.Ordinal))
            .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var file in files.Where(f => now - f.LastWriteTimeUtc > TimeSpan.FromDays(RetentionDays)).ToArray())
        {
            if (!string.Equals(file.FullName, incoming, StringComparison.OrdinalIgnoreCase))
                file.Delete();
            files.Remove(file);
        }
        long total = files.Sum(f => f.Length) + newBytes;
        foreach (var file in files)
        {
            if (total <= MaxComponentBytes)
                break;
            if (string.Equals(file.FullName, incoming, StringComparison.OrdinalIgnoreCase))
                continue;
            var removedBytes = file.Length;
            file.Delete();
            total -= removedBytes;
        }
        if (total > MaxComponentBytes)
            throw new IOException("Diagnostic quota exhausted.");
    }
}
