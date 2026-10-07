using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LoomLCI.Core.Filesystem;
using LoomLCI.Core.VisualFiles;

namespace LoomLCI.Core.Python;

public sealed class PythonFilesystemBridgeModule : IPythonBridgeModule
{
    private static readonly string[] SupportedMethods =
    [
        "fs.apply_patch",
        "fs.find_paths",
        "fs.list_tree",
        "fs.manage_directory",
        "fs.read_files",
        "fs.read_pdf",
        "fs.search_text"
    ];

    private static readonly JsonSerializerOptions InputJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    private static readonly JsonSerializerOptions ResultJsonOptions =
        CreateResultJsonOptions();

    private readonly FilesystemCapability _filesystem;
    private readonly VisualFilesCapability _visualFiles;

    public PythonFilesystemBridgeModule(
        FilesystemCapability filesystem,
        VisualFilesCapability visualFiles)
    {
        _filesystem = filesystem;
        _visualFiles = visualFiles;
    }

    public IReadOnlyList<string> Methods => SupportedMethods;

    public Task<LoomResult<JsonElement>> DispatchAsync(
        WorkId workId,
        PythonBridgeCall call,
        CancellationToken cancellationToken)
        => call.Method switch
        {
            "fs.list_tree" => ListTreeAsync(
                workId,
                call.Arguments,
                cancellationToken),
            "fs.find_paths" => FindPathsAsync(
                workId,
                call.Arguments,
                cancellationToken),
            "fs.search_text" => SearchTextAsync(
                workId,
                call.Arguments,
                cancellationToken),
            "fs.read_files" => ReadFilesAsync(
                workId,
                call.Arguments,
                cancellationToken),
            "fs.apply_patch" => ApplyPatchAsync(
                workId,
                call.Arguments,
                cancellationToken),
            "fs.manage_directory" => ManageDirectoryAsync(
                workId,
                call.Arguments,
                cancellationToken),
            "fs.read_pdf" => ReadPdfAsync(
                workId,
                call.Arguments,
                cancellationToken),
            _ => Task.FromResult(
                LoomResult<JsonElement>.Failure(
                    LoomErrors.Unsupported(
                        $"Python filesystem bridge method '{call.Method}' is not supported.")))
        };

    private async Task<LoomResult<JsonElement>> ListTreeAsync(
        WorkId workId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parsed = Deserialize<ListTreeArguments>(
            arguments,
            "fs.list_tree");
        if (!parsed.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(parsed.Error!);
        }

        var value = parsed.Value!;
        var result = await _filesystem.ListTreeAsync(
                value.Path,
                workId,
                value.IncludeGenerated,
                value.ExcludeDirectories,
                value.MaxDepth,
                value.MaxEntries,
                value.Cursor,
                cancellationToken)
            .ConfigureAwait(false);

        return MapResult(
            "fs.list_tree",
            result);
    }

    private async Task<LoomResult<JsonElement>> FindPathsAsync(
        WorkId workId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parsed = Deserialize<FindPathsArguments>(
            arguments,
            "fs.find_paths");
        if (!parsed.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(parsed.Error!);
        }

        var value = parsed.Value!;
        if (string.IsNullOrWhiteSpace(value.Path) ||
            value.Queries is null)
        {
            return LoomResult<JsonElement>.Failure(
                LoomErrors.InvalidArgument(
                    "fs.find_paths requires path and queries."));
        }

        if (!TryParseMatchMode(
                value.MatchMode,
                out var matchMode))
        {
            return LoomResult<JsonElement>.Failure(
                LoomErrors.InvalidArgument(
                    "match_mode must be 'substring' or 'suffix'."));
        }

        if (!TryParseEntryType(
                value.Type,
                out var type))
        {
            return LoomResult<JsonElement>.Failure(
                LoomErrors.InvalidArgument(
                    "type must be 'any', 'file', 'directory', or 'symlink'."));
        }

        var result = await _filesystem.FindPathsAsync(
                value.Path,
                value.Queries,
                matchMode,
                type,
                workId,
                value.IncludeGenerated,
                value.ExcludeDirectories,
                value.MaxDepth,
                value.MaxResults,
                value.Cursor,
                cancellationToken)
            .ConfigureAwait(false);

        return MapResult(
            "fs.find_paths",
            result);
    }

    private async Task<LoomResult<JsonElement>> SearchTextAsync(
        WorkId workId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parsed = Deserialize<SearchTextArguments>(
            arguments,
            "fs.search_text");
        if (!parsed.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(parsed.Error!);
        }

        var value = parsed.Value!;
        if (string.IsNullOrWhiteSpace(value.Path) ||
            value.Queries is null)
        {
            return LoomResult<JsonElement>.Failure(
                LoomErrors.InvalidArgument(
                    "fs.search_text requires path and queries."));
        }

        var result = await _filesystem.SearchTextAsync(
                value.Path,
                value.Queries,
                workId,
                value.CaseSensitive,
                value.IncludeGenerated,
                value.ExcludeDirectories,
                value.MaxDepth,
                value.MaxResults,
                value.ContextLines,
                value.Cursor,
                cancellationToken)
            .ConfigureAwait(false);

        return MapResult(
            "fs.search_text",
            result);
    }

    private async Task<LoomResult<JsonElement>> ReadFilesAsync(
        WorkId workId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parsed = Deserialize<ReadFilesArguments>(
            arguments,
            "fs.read_files");
        if (!parsed.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(parsed.Error!);
        }

        var files = parsed.Value!.Files;
        if (files is null)
        {
            return LoomResult<JsonElement>.Failure(
                LoomErrors.InvalidArgument(
                    "fs.read_files requires files."));
        }

        var requests = files
            .Select(file => (
                file.Path ?? string.Empty,
                file.Offset,
                file.Limit))
            .ToArray();

        var result = await _filesystem.ReadFilesAsync(
                requests,
                workId,
                cancellationToken)
            .ConfigureAwait(false);

        return MapResult(
            "fs.read_files",
            result,
            "Use smaller line ranges or split files across calls.");
    }

    private async Task<LoomResult<JsonElement>> ApplyPatchAsync(
        WorkId workId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parsed = Deserialize<ApplyPatchArguments>(
            arguments,
            "fs.apply_patch");
        if (!parsed.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(parsed.Error!);
        }

        var changes = parsed.Value!.Changes;
        if (changes is null)
        {
            return LoomResult<JsonElement>.Failure(
                LoomErrors.InvalidArgument(
                    "fs.apply_patch requires changes."));
        }

        var mapped = new List<FilesystemPatchChange>(
            changes.Length);

        foreach (var change in changes)
        {
            if (!TryParsePatchOperation(
                    change.Op,
                    out var operation))
            {
                return LoomResult<JsonElement>.Failure(
                    LoomErrors.InvalidArgument(
                        "op must be 'write', 'replace', 'delete', or 'move'."));
            }

            mapped.Add(
                new FilesystemPatchChange(
                    operation,
                    change.Path,
                    change.Content,
                    change.Overwrite,
                    change.OldText,
                    change.NewText,
                    change.ExpectedOccurrences,
                    change.FromPath,
                    change.ToPath));
        }

        var result = await _filesystem.ApplyPatchAsync(
                mapped,
                workId,
                cancellationToken)
            .ConfigureAwait(false);

        return MapResult(
            "fs.apply_patch",
            result);
    }

    private async Task<LoomResult<JsonElement>> ManageDirectoryAsync(
        WorkId workId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parsed = Deserialize<ManageDirectoryArguments>(
            arguments,
            "fs.manage_directory");
        if (!parsed.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(parsed.Error!);
        }

        var value = parsed.Value!;
        if (string.IsNullOrWhiteSpace(value.Action) ||
            string.IsNullOrWhiteSpace(value.Path))
        {
            return LoomResult<JsonElement>.Failure(
                LoomErrors.InvalidArgument(
                    "fs.manage_directory requires action and path."));
        }

        LoomResult<FilesystemDirectoryResult> result;
        if (string.Equals(
                value.Action,
                "create",
                StringComparison.OrdinalIgnoreCase))
        {
            result = await _filesystem.CreateDirectoryAsync(
                    value.Path,
                    workId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (string.Equals(
                     value.Action,
                     "delete",
                     StringComparison.OrdinalIgnoreCase))
        {
            result = await _filesystem.DeleteDirectoryAsync(
                    value.Path,
                    workId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            return LoomResult<JsonElement>.Failure(
                LoomErrors.InvalidArgument(
                    "action must be 'create' or 'delete'."));
        }

        return MapResult(
            "fs.manage_directory",
            result);
    }

    private async Task<LoomResult<JsonElement>> ReadPdfAsync(
        WorkId workId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parsed = Deserialize<ReadPdfArguments>(
            arguments,
            "fs.read_pdf");
        if (!parsed.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(parsed.Error!);
        }

        var value = parsed.Value!;
        if (string.IsNullOrWhiteSpace(value.Path))
        {
            return LoomResult<JsonElement>.Failure(
                LoomErrors.InvalidArgument(
                    "fs.read_pdf requires path."));
        }

        var result = await _visualFiles.ReadPdfTextAsync(
                value.Path,
                workId,
                value.StartPage,
                value.MaxPages,
                cancellationToken)
            .ConfigureAwait(false);

        return MapResult(
            "fs.read_pdf",
            result);
    }

    private static LoomResult<T> Deserialize<T>(
        JsonElement arguments,
        string method)
        where T : class
    {
        try
        {
            var value = arguments.Deserialize<T>(
                InputJsonOptions);

            return value is not null
                ? LoomResult<T>.Success(value)
                : LoomResult<T>.Failure(
                    LoomErrors.InvalidArgument(
                        $"{method} arguments are invalid."));
        }
        catch (JsonException ex)
        {
            return LoomResult<T>.Failure(
                LoomErrors.InvalidArgument(
                    $"{method} arguments are invalid: {ex.Message}"));
        }
        catch (NotSupportedException ex)
        {
            return LoomResult<T>.Failure(
                LoomErrors.InvalidArgument(
                    $"{method} arguments are invalid: {ex.Message}"));
        }
    }

    private static LoomResult<JsonElement> MapResult<T>(
        string method,
        LoomResult<T> result,
        string? sizeHint = null)
    {
        if (!result.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                result.Error!);
        }

        JsonElement element;
        try
        {
            element = JsonSerializer.SerializeToElement(
                result.Value,
                ResultJsonOptions);
        }
        catch (Exception ex) when (
            ex is JsonException or
            NotSupportedException)
        {
            return LoomResult<JsonElement>.Failure(
                LoomErrors.Internal(
                    $"Could not serialize Python bridge result: {ex.Message}"));
        }

        var serializedBytes = Encoding.UTF8.GetByteCount(
            element.GetRawText());

        if (serializedBytes >
            PythonBridgeLimits.MaxResultFrameBytes)
        {
            var message =
                $"Python bridge result for '{method}' exceeds " +
                $"{PythonBridgeLimits.MaxResultFrameBytes} bytes.";

            if (!string.IsNullOrWhiteSpace(sizeHint))
            {
                message += $" {sizeHint}";
            }

            return LoomResult<JsonElement>.Failure(
                new LoomError(
                    "unsupported",
                    message,
                    false,
                    new Dictionary<string, object?>
                    {
                        ["reason"] = "bridge_payload_too_large",
                        ["method"] = method,
                        ["serialized_result_bytes"] =
                            serializedBytes,
                        ["max_bridge_result_bytes"] =
                            PythonBridgeLimits.MaxResultFrameBytes
                    }));
        }

        return LoomResult<JsonElement>.Success(element);
    }

    private static bool TryParseMatchMode(
        string value,
        out FilesystemPathMatchMode mode)
    {
        if (string.Equals(
                value,
                "substring",
                StringComparison.OrdinalIgnoreCase))
        {
            mode = FilesystemPathMatchMode.Substring;
            return true;
        }

        if (string.Equals(
                value,
                "suffix",
                StringComparison.OrdinalIgnoreCase))
        {
            mode = FilesystemPathMatchMode.Suffix;
            return true;
        }

        mode = default;
        return false;
    }

    private static bool TryParseEntryType(
        string value,
        out FilesystemEntryType? type)
    {
        if (string.Equals(
                value,
                "any",
                StringComparison.OrdinalIgnoreCase))
        {
            type = null;
            return true;
        }

        if (Enum.TryParse<FilesystemEntryType>(
                value,
                true,
                out var parsed))
        {
            type = parsed;
            return true;
        }

        type = null;
        return false;
    }

    private static bool TryParsePatchOperation(
        string? value,
        out FilesystemPatchOperation operation)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            Enum.TryParse<FilesystemPatchOperation>(
                value,
                true,
                out operation))
        {
            return true;
        }

        operation = default;
        return false;
    }

    private static JsonSerializerOptions CreateResultJsonOptions()
    {
        var options = new JsonSerializerOptions(
            JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.SnakeCaseLower
        };

        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.SnakeCaseLower));

        return options;
    }

    private sealed class ListTreeArguments
    {
        public string Path { get; init; } = ".";
        public bool IncludeGenerated { get; init; }
        public string[]? ExcludeDirectories { get; init; }
        public int MaxDepth { get; init; } = 3;
        public int MaxEntries { get; init; } = 1000;
        public string? Cursor { get; init; }
    }

    private sealed class FindPathsArguments
    {
        public string? Path { get; init; }
        public string[]? Queries { get; init; }
        public string MatchMode { get; init; } = "substring";
        public string Type { get; init; } = "any";
        public bool IncludeGenerated { get; init; }
        public string[]? ExcludeDirectories { get; init; }
        public int MaxDepth { get; init; } = 12;
        public int MaxResults { get; init; } = 100;
        public string? Cursor { get; init; }
    }

    private sealed class SearchTextArguments
    {
        public string? Path { get; init; }
        public string[]? Queries { get; init; }
        public bool CaseSensitive { get; init; }
        public bool IncludeGenerated { get; init; }
        public string[]? ExcludeDirectories { get; init; }
        public int MaxDepth { get; init; } = 12;
        public int MaxResults { get; init; } = 100;
        public int ContextLines { get; init; } = 1;
        public string? Cursor { get; init; }
    }

    private sealed class ReadFilesArguments
    {
        public ReadFileArgument[]? Files { get; init; }
    }

    private sealed class ReadFileArgument
    {
        public string? Path { get; init; }
        public int? Offset { get; init; }
        public int? Limit { get; init; }
    }

    private sealed class ApplyPatchArguments
    {
        public PatchChangeArgument[]? Changes { get; init; }
    }

    private sealed class PatchChangeArgument
    {
        public string? Op { get; init; }
        public string? Path { get; init; }
        public string? Content { get; init; }
        public bool Overwrite { get; init; }
        public string? OldText { get; init; }
        public string? NewText { get; init; }
        public int ExpectedOccurrences { get; init; } = 1;
        public string? FromPath { get; init; }
        public string? ToPath { get; init; }
    }

    private sealed class ManageDirectoryArguments
    {
        public string? Action { get; init; }
        public string? Path { get; init; }
    }

    private sealed class ReadPdfArguments
    {
        public string? Path { get; init; }
        public int StartPage { get; init; } = 1;
        public int MaxPages { get; init; } = 10;
    }
}
