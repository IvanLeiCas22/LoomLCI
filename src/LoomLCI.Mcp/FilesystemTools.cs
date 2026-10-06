using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json.Serialization;
using LoomLCI.Core;
using LoomLCI.Core.Filesystem;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LoomLCI.Mcp;

public sealed record FilesystemEntryDto(
    string Path,
    string Name,
    string Type,
    long? Size,
    int Depth,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ChildrenExcluded);

public sealed record FilesystemListTreeDto(
    string Root,
    int MaxDepth,
    int MaxEntries,
    IReadOnlyList<FilesystemEntryDto> Entries,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextCursor);

public sealed record FilesystemFindPathsDto(
    string Root,
    IReadOnlyList<string> Queries,
    string MatchMode,
    IReadOnlyList<FilesystemEntryDto> Matches,
    bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextCursor);

public sealed record FilesystemTextQueryMatchDto(
    string Query,
    int Column);

public sealed record FilesystemTextLineExcerptDto(
    string Text,
    int StartColumn,
    bool Truncated);

public sealed record FilesystemTextMatchDto(
    string Path,
    int Line,
    string Text,
    int TextStartColumn,
    bool TextTruncated,
    IReadOnlyList<FilesystemTextQueryMatchDto> QueryMatches,
    IReadOnlyList<FilesystemTextLineExcerptDto> ContextBefore,
    IReadOnlyList<FilesystemTextLineExcerptDto> ContextAfter);

public sealed record FilesystemSearchTextDto(
    string Root,
    IReadOnlyList<string> Queries,
    IReadOnlyList<FilesystemTextMatchDto> Matches,
    int FilesRead,
    long BytesRead,
    bool Truncated,
    bool ResultLimitReached,
    bool ScanLimitReached,
    int SkippedBinaryFileCount,
    IReadOnlyList<string> SkippedBinaryFiles,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextCursor);

public sealed record FilesystemReadFileInput(
    [property: Description("File path to read. May be absolute or relative to the work session base directory.")] string Path,
    [property: Description("Optional 1-based first line to return.")][property: Range(1, int.MaxValue)] int? Offset = null,
    [property: Description("Optional maximum number of lines to return.")][property: Range(1, 10000)] int? Limit = null);

public sealed record FilesystemReadFileDto(
    string RequestedPath,
    string FullPath,
    int StartLine,
    int EndLine,
    int TotalLines,
    [property: Description("True when the file has lines before startLine that were not returned.")] bool HasMoreBefore,
    [property: Description("True when the file has lines after endLine that were not returned.")] bool HasMoreAfter,
    string Text);

public sealed record FilesystemReadFilesDto(
    IReadOnlyList<FilesystemReadFileDto> Files);

public sealed record FilesystemPatchChangeInput(
    [property: Description("Operation: write, replace, delete, or move.")][property: AllowedValues("write", "replace", "delete", "move")] string Op,
    [property: Description("Target path for write, replace, or delete. Not used by move.")] string? Path = null,
    [property: Description("Complete text content for write. Required when op=write.")] string? Content = null,
    [property: Description("For write or move, allow replacing an existing destination when true.")] bool Overwrite = false,
    [property: Description("Exact non-empty text to replace. Required when op=replace.")] string? OldText = null,
    [property: Description("Replacement text. Required when op=replace and may be empty.")] string? NewText = null,
    [property: Description("Expected number of exact oldText matches for replace; the operation fails if the actual count differs.")][property: Range(1, 1000)] int ExpectedOccurrences = 1,
    [property: Description("Source path for move. Required when op=move.")] string? FromPath = null,
    [property: Description("Destination path for move. Required when op=move.")] string? ToPath = null);

public sealed record FilesystemPatchDto(int AppliedChanges);

public sealed record FilesystemDirectoryDto(string Path, bool Exists);

[McpServerToolType]
public sealed class FilesystemTools
{
    private readonly FilesystemCapability _filesystem;

    public FilesystemTools(FilesystemCapability filesystem)
    {
        _filesystem = filesystem;
    }

    [McpServerTool(
        Name = "filesystem_list_tree",
        Title = "List directory tree",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<FilesystemListTreeDto>),
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Lists a bounded recursive directory tree. Use this to discover project structure when exact paths are not yet known. If nextCursor is returned, pass it back as cursor with the same traversal inputs to continue without repeating entries; maxEntries may change between pages. The cursor is opaque and becomes invalid if traversal parameters change or the filesystem changes at the resume point. Recursive traversal prunes common generated/infrastructure directories by default while still showing the directory itself; explicitly targeting one of those directories as path still works. A returned directory may include childrenExcluded='generated' when default pruning skipped its children or childrenExcluded='excluded' when excludeDirectories skipped them; the field is omitted when children were traversed normally. It does not read file contents; prefer filesystem_find_paths for name/path lookup, filesystem_search_text for content search, and filesystem_read_files once exact files are known.")]
    public async Task<CallToolResult> ListTree(
        [Description("Directory path. May be absolute or relative to the work session base directory.")] string path = ".",
        [Description("Optional work session handle used to resolve relative paths.")] string? workId = null,
        [Description("When false, recursive traversal prunes .git, .vs, .venv, __pycache__, bin, node_modules, obj, and .obsidian/plugins. Explicitly targeting one of those directories as path still traverses it.")] bool includeGenerated = false,
        [Description("Optional exact directory names to prune in addition to the defaults. Names are case-insensitive on Windows and are not glob patterns or paths.")][MaxLength(64)] string[]? excludeDirectories = null,
        [Description("Maximum recursion depth.")][Range(1, 32)] int maxDepth = 3,
        [Description("Maximum entries returned in this page.")][Range(1, 5000)] int maxEntries = 1000,
        [Description("Opaque nextCursor from a previous filesystem_list_tree call with the same traversal inputs. Omit for the first page; maxEntries may differ between pages.")][MaxLength(4096)] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _filesystem.ListTreeAsync(
            path,
            ParseWorkId(workId),
            includeGenerated,
            excludeDirectories,
            maxDepth,
            maxEntries,
            cursor,
            cancellationToken).ConfigureAwait(false);

        return McpToolResults.From(MapListTree(result));
    }

    [McpServerTool(
        Name = "filesystem_find_paths",
        Title = "Find paths",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<FilesystemFindPathsDto>),
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Finds files or directories by literal path/name fragments. If nextCursor is returned, pass it back as cursor with the same search/traversal inputs to continue without repeating matches; maxResults may change between pages. The cursor is opaque and becomes invalid if those inputs change or the filesystem changes at the resume point. Recursive traversal prunes common generated/infrastructure directories by default; explicitly targeting one of those directories as path still works. Directory matches may include childrenExcluded='generated' when default pruning skipped their children or childrenExcluded='excluded' when excludeDirectories skipped them; the field is omitted when children were traversed normally. Use this when searching for paths, filenames, or extensions; it does not inspect file contents. Use filesystem_search_text instead for text inside files. matchMode=suffix is useful for exact filename endings or extensions.")]
    public async Task<CallToolResult> FindPaths(
        [Description("Root directory to search. May be absolute or relative to the work session base directory.")] string path,
        [Description("One to 32 non-empty literal path queries. Multiple queries use OR semantics.")][MinLength(1)][MaxLength(32)] string[] queries,
        [Description("Path matching strategy: substring matches anywhere in the relative path; suffix matches only path endings.")][AllowedValues("substring", "suffix")] string matchMode = "substring",
        [Description("Optional entry type filter.")][AllowedValues("any", "file", "directory", "symlink")] string type = "any",
        [Description("Optional work session handle used to resolve relative paths.")] string? workId = null,
        [Description("When false, recursive traversal prunes .git, .vs, .venv, __pycache__, bin, node_modules, obj, and .obsidian/plugins. Explicitly targeting one of those directories as path still traverses it.")] bool includeGenerated = false,
        [Description("Optional exact directory names to prune in addition to the defaults. Names are case-insensitive on Windows and are not glob patterns or paths.")][MaxLength(64)] string[]? excludeDirectories = null,
        [Description("Maximum recursion depth.")][Range(1, 32)] int maxDepth = 12,
        [Description("Maximum matching entries returned in this page.")][Range(1, 1000)] int maxResults = 100,
        [Description("Opaque nextCursor from a previous filesystem_find_paths call with the same search/traversal inputs. Omit for the first page; maxResults may differ between pages.")][MaxLength(4096)] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseMatchMode(matchMode, out var parsedMode))
        {
            return McpToolResults.From(ToolEnvelope<FilesystemFindPathsDto>.From(
                LoomResult<FilesystemFindPathsDto>.Failure(
                    LoomErrors.InvalidArgument("match_mode must be 'substring' or 'suffix'."))));
        }

        if (!TryParseType(type, out var parsedType))
        {
            return McpToolResults.From(ToolEnvelope<FilesystemFindPathsDto>.From(
                LoomResult<FilesystemFindPathsDto>.Failure(
                    LoomErrors.InvalidArgument("type must be 'any', 'file', 'directory', or 'symlink'."))));
        }

        var result = await _filesystem.FindPathsAsync(
            path,
            queries,
            parsedMode,
            parsedType,
            ParseWorkId(workId),
            includeGenerated,
            excludeDirectories,
            maxDepth,
            maxResults,
            cursor,
            cancellationToken).ConfigureAwait(false);

        return McpToolResults.From(MapFindPaths(result));
    }

    [McpServerTool(
        Name = "filesystem_search_text",
        Title = "Search text",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<FilesystemSearchTextDto>),
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Searches one to 32 literal text queries in a single filesystem traversal with OR semantics. Results are grouped by matching physical line: each line appears once with queryMatches identifying every query that matched and its first 1-based UTF-16 column. Matching lines and context are bounded excerpts; textStartColumn/textTruncated and context excerpt metadata indicate when text was shortened. maxResults limits matching lines per page. Search streams files of any size and scans up to about 64 MiB per page; if nextCursor is returned, pass it back as cursor with the same search/traversal inputs to continue. maxResults may change between pages. resultLimitReached and scanLimitReached explain why the current page stopped. Files classified as binary by an initial decoded-prefix NUL heuristic are skipped and reported by skippedBinaryFileCount/skippedBinaryFiles. Recursive traversal pruning matches the other filesystem discovery tools. Use filesystem_read_files after locating files that need fuller exact context.")]
    public async Task<CallToolResult> SearchText(
        [Description("File or directory path to search. May be absolute or relative to the work session base directory.")] string path,
        [Description("One to 32 non-empty literal text queries. Multiple queries use OR semantics and are searched in one traversal; this is not regex.")][MinLength(1)][MaxLength(32)] string[] queries,
        [Description("Optional work session handle used to resolve relative paths.")] string? workId = null,
        [Description("Whether matching is case-sensitive.")] bool caseSensitive = false,
        [Description("When false, recursive traversal prunes .git, .vs, .venv, __pycache__, bin, node_modules, obj, and .obsidian/plugins. Explicitly targeting one of those directories as path still traverses it.")] bool includeGenerated = false,
        [Description("Optional exact directory names to prune in addition to the defaults. Names are case-insensitive on Windows and are not glob patterns or paths.")][MaxLength(64)] string[]? excludeDirectories = null,
        [Description("Maximum recursion depth.")][Range(1, 32)] int maxDepth = 12,
        [Description("Maximum matching lines returned in this page across all queries.")][Range(1, 500)] int maxResults = 100,
        [Description("Context lines returned before and after each match.")][Range(0, 3)] int contextLines = 1,
        [Description("Opaque nextCursor from a previous filesystem_search_text call with the same search/traversal inputs. Omit for the first page; maxResults may differ between pages.")][MaxLength(4096)] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _filesystem.SearchTextAsync(
            path,
            queries,
            ParseWorkId(workId),
            caseSensitive,
            includeGenerated,
            excludeDirectories,
            maxDepth,
            maxResults,
            contextLines,
            cursor,
            cancellationToken).ConfigureAwait(false);

        return McpToolResults.From(MapSearchText(result));
    }

    [McpServerTool(
        Name = "filesystem_read_files",
        Title = "Read text files",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<FilesystemReadFilesDto>),
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Reads known text files, optionally by 1-based line range. Large files can be read in bounded ranges by supplying limit; unbounded whole-file reads over 16 MiB are rejected. MCP responses are capped at a safe 9 MiB serialized budget, so large results must be split into smaller line ranges or separate calls. Each file result reports hasMoreBefore and hasMoreAfter so partial ranges are explicit without implying transport truncation. Use this when exact file paths are already known; prefer filesystem_search_text when you first need to locate content. Multiple files can be read in one call.")]
    public async Task<CallToolResult> ReadFiles(
        [Description("One to 32 files to read. Offset is the optional 1-based starting line and limit is the optional maximum line count.")][MinLength(1)][MaxLength(32)] FilesystemReadFileInput[] files,
        [Description("Optional work session handle used to resolve relative paths.")] string? workId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _filesystem.ReadFilesAsync(
            files.Select(file => (file.Path, file.Offset, file.Limit)).ToArray(),
            ParseWorkId(workId),
            cancellationToken).ConfigureAwait(false);

        var envelope = MapReadFiles(result);
        if (!result.IsSuccess)
        {
            return McpToolResults.From(envelope);
        }

        long returnedTextUtf8Bytes = 0;
        foreach (var file in result.Value!.Files)
        {
            returnedTextUtf8Bytes = checked(
                returnedTextUtf8Bytes + Encoding.UTF8.GetByteCount(file.Text));
        }

        if (returnedTextUtf8Bytes > McpPayloadLimits.MaxCallToolResultBytes)
        {
            return ReadFilesPayloadTooLarge(
                returnedTextUtf8Bytes,
                serializedCallToolResultBytes: null);
        }

        var toolResult = McpToolResults.From(envelope);
        var serializedBytes =
            McpPayloadLimits.MeasureSerializedCallToolResultBytes(toolResult);

        return serializedBytes <= McpPayloadLimits.MaxCallToolResultBytes
            ? toolResult
            : ReadFilesPayloadTooLarge(
                returnedTextUtf8Bytes,
                serializedBytes);
    }

    [McpServerTool(
        Name = "filesystem_apply_patch",
        Title = "Apply file changes",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<FilesystemPatchDto>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description("Applies structured file changes. write and replace are text operations with a 16 MiB text-content limit; delete and move operate on files without reading their contents, so they also work for large or binary files. Prefer this over shell commands for equivalent filesystem changes. Each call accepts up to 64 validated changes; dependent edits to the same path must be split into separate calls.")]
    public async Task<CallToolResult> ApplyPatch(
        [Description("One to 64 patch operations. write uses path+content; replace uses path+oldText+newText; delete uses path; move uses fromPath+toPath.")][MinLength(1)][MaxLength(64)] FilesystemPatchChangeInput[] changes,
        [Description("Optional work session handle used to resolve relative paths.")] string? workId = null,
        CancellationToken cancellationToken = default)
    {
        var mapped = new List<FilesystemPatchChange>(changes.Length);
        foreach (var change in changes)
        {
            if (!TryParsePatchOperation(change.Op, out var operation))
            {
                return McpToolResults.From(ToolEnvelope<FilesystemPatchDto>.From(
                    LoomResult<FilesystemPatchDto>.Failure(
                        LoomErrors.InvalidArgument("op must be 'write', 'replace', 'delete', or 'move'."))));
            }

            mapped.Add(new FilesystemPatchChange(
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
            ParseWorkId(workId),
            cancellationToken).ConfigureAwait(false);

        return McpToolResults.From(
            result.IsSuccess
                ? ToolEnvelope<FilesystemPatchDto>.From(
                    LoomResult<FilesystemPatchDto>.Success(
                        new FilesystemPatchDto(result.Value!.AppliedChanges)))
                : ToolEnvelope<FilesystemPatchDto>.From(
                    LoomResult<FilesystemPatchDto>.Failure(result.Error!)));
    }

    [McpServerTool(
        Name = "filesystem_manage_directory",
        Title = "Manage directory",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<FilesystemDirectoryDto>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Creates a directory including missing parents, or deletes one existing empty directory. This is not recursive deletion and does not edit file contents.")]
    public async Task<CallToolResult> ManageDirectory(
        [Description("Directory operation.")][AllowedValues("create", "delete")] string action,
        [Description("Directory path.")] string path,
        [Description("Optional work session handle used to resolve relative paths.")] string? workId = null,
        CancellationToken cancellationToken = default)
    {
        LoomResult<FilesystemDirectoryResult> result;
        if (string.Equals(action, "create", StringComparison.OrdinalIgnoreCase))
        {
            result = await _filesystem.CreateDirectoryAsync(
                path,
                ParseWorkId(workId),
                cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(action, "delete", StringComparison.OrdinalIgnoreCase))
        {
            result = await _filesystem.DeleteDirectoryAsync(
                path,
                ParseWorkId(workId),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return McpToolResults.From(ToolEnvelope<FilesystemDirectoryDto>.From(
                LoomResult<FilesystemDirectoryDto>.Failure(
                    LoomErrors.InvalidArgument("action must be 'create' or 'delete'."))));
        }

        return McpToolResults.From(
            result.IsSuccess
                ? ToolEnvelope<FilesystemDirectoryDto>.From(
                    LoomResult<FilesystemDirectoryDto>.Success(
                        new FilesystemDirectoryDto(result.Value!.Path, result.Value.Exists)))
                : ToolEnvelope<FilesystemDirectoryDto>.From(
                    LoomResult<FilesystemDirectoryDto>.Failure(result.Error!)));
    }

    private static WorkId? ParseWorkId(string? workId)
        => string.IsNullOrWhiteSpace(workId) ? null : new WorkId(workId);

    private static ToolEnvelope<FilesystemListTreeDto> MapListTree(
        LoomResult<FilesystemListTreeResult> result)
        => result.IsSuccess
            ? ToolEnvelope<FilesystemListTreeDto>.From(
                LoomResult<FilesystemListTreeDto>.Success(
                    new FilesystemListTreeDto(
                        result.Value!.Root,
                        result.Value.MaxDepth,
                        result.Value.MaxEntries,
                        result.Value.Entries.Select(ToDto).ToArray(),
                        result.Value.Truncated,
                        result.Value.NextCursor)))
            : ToolEnvelope<FilesystemListTreeDto>.From(
                LoomResult<FilesystemListTreeDto>.Failure(result.Error!));

    private static ToolEnvelope<FilesystemFindPathsDto> MapFindPaths(
        LoomResult<FilesystemFindPathsResult> result)
        => result.IsSuccess
            ? ToolEnvelope<FilesystemFindPathsDto>.From(
                LoomResult<FilesystemFindPathsDto>.Success(
                    new FilesystemFindPathsDto(
                        result.Value!.Root,
                        result.Value.Queries,
                        result.Value.MatchMode.ToString().ToLowerInvariant(),
                        result.Value.Matches.Select(ToDto).ToArray(),
                        result.Value.Truncated,
                        result.Value.NextCursor)))
            : ToolEnvelope<FilesystemFindPathsDto>.From(
                LoomResult<FilesystemFindPathsDto>.Failure(result.Error!));

    private static ToolEnvelope<FilesystemSearchTextDto> MapSearchText(
        LoomResult<FilesystemSearchTextResult> result)
        => result.IsSuccess
            ? ToolEnvelope<FilesystemSearchTextDto>.From(
                LoomResult<FilesystemSearchTextDto>.Success(
                    new FilesystemSearchTextDto(
                        result.Value!.Root,
                        result.Value.Queries,
                        result.Value.Matches.Select(match => new FilesystemTextMatchDto(
                            match.Path,
                            match.Line,
                            match.Text,
                            match.TextStartColumn,
                            match.TextTruncated,
                            match.QueryMatches.Select(queryMatch => new FilesystemTextQueryMatchDto(
                                queryMatch.Query,
                                queryMatch.Column)).ToArray(),
                            match.ContextBefore.Select(ToDto).ToArray(),
                            match.ContextAfter.Select(ToDto).ToArray())).ToArray(),
                        result.Value.FilesRead,
                        result.Value.BytesRead,
                        result.Value.Truncated,
                        result.Value.ResultLimitReached,
                        result.Value.ScanLimitReached,
                        result.Value.SkippedBinaryFileCount,
                        result.Value.SkippedBinaryFiles,
                        result.Value.NextCursor)))
            : ToolEnvelope<FilesystemSearchTextDto>.From(
                LoomResult<FilesystemSearchTextDto>.Failure(result.Error!));

    private static ToolEnvelope<FilesystemReadFilesDto> MapReadFiles(
        LoomResult<FilesystemReadFilesResult> result)
        => result.IsSuccess
            ? ToolEnvelope<FilesystemReadFilesDto>.From(
                LoomResult<FilesystemReadFilesDto>.Success(
                    new FilesystemReadFilesDto(
                        result.Value!.Files.Select(file => new FilesystemReadFileDto(
                            file.RequestedPath,
                            file.FullPath,
                            file.StartLine,
                            file.EndLine,
                            file.TotalLines,
                            file.HasMoreBefore,
                            file.HasMoreAfter,
                            file.Text)).ToArray())))
            : ToolEnvelope<FilesystemReadFilesDto>.From(
                LoomResult<FilesystemReadFilesDto>.Failure(result.Error!));

    private static CallToolResult ReadFilesPayloadTooLarge(
        long returnedTextUtf8Bytes,
        long? serializedCallToolResultBytes)
    {
        var details = new Dictionary<string, object?>
        {
            ["reason"] = "mcp_payload_too_large",
            ["returnedTextUtf8Bytes"] = returnedTextUtf8Bytes,
            ["maxCallToolResultBytes"] = McpPayloadLimits.MaxCallToolResultBytes
        };

        if (serializedCallToolResultBytes is { } serializedBytes)
        {
            details["serializedCallToolResultBytes"] = serializedBytes;
        }

        return McpToolResults.From(
            ToolEnvelope<FilesystemReadFilesDto>.From(
                LoomResult<FilesystemReadFilesDto>.Failure(
                    new LoomError(
                        "unsupported",
                        "The filesystem_read_files result would exceed the safe MCP response budget. Use smaller line ranges or split files across calls.",
                        false,
                        details))));
    }

    private static FilesystemTextLineExcerptDto ToDto(FilesystemTextLineExcerpt value)
        => new(value.Text, value.StartColumn, value.Truncated);

    private static FilesystemEntryDto ToDto(FilesystemEntry entry)
        => new(
            entry.Path,
            entry.Name,
            entry.Type.ToString().ToLowerInvariant(),
            entry.Size,
            entry.Depth,
            entry.ChildrenExcluded);

    private static bool TryParseMatchMode(string value, out FilesystemPathMatchMode mode)
    {
        if (string.Equals(value, "substring", StringComparison.OrdinalIgnoreCase))
        {
            mode = FilesystemPathMatchMode.Substring;
            return true;
        }

        if (string.Equals(value, "suffix", StringComparison.OrdinalIgnoreCase))
        {
            mode = FilesystemPathMatchMode.Suffix;
            return true;
        }

        mode = default;
        return false;
    }

    private static bool TryParseType(string value, out FilesystemEntryType? type)
    {
        if (string.Equals(value, "any", StringComparison.OrdinalIgnoreCase))
        {
            type = null;
            return true;
        }

        if (Enum.TryParse<FilesystemEntryType>(value, true, out var parsed))
        {
            type = parsed;
            return true;
        }

        type = null;
        return false;
    }

    private static bool TryParsePatchOperation(string value, out FilesystemPatchOperation operation)
    {
        if (Enum.TryParse<FilesystemPatchOperation>(value, true, out operation))
        {
            return true;
        }

        operation = default;
        return false;
    }
}
