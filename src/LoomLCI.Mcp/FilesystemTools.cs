using System.ComponentModel;
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
    int Depth);

public sealed record FilesystemListTreeDto(
    string Root,
    int MaxDepth,
    int MaxEntries,
    IReadOnlyList<FilesystemEntryDto> Entries,
    bool Truncated);

public sealed record FilesystemFindPathsDto(
    string Root,
    IReadOnlyList<string> Queries,
    string MatchMode,
    IReadOnlyList<FilesystemEntryDto> Matches,
    bool Truncated);

public sealed record FilesystemTextMatchDto(
    string Path,
    int Line,
    int Column,
    string Text,
    IReadOnlyList<string> ContextBefore,
    IReadOnlyList<string> ContextAfter);

public sealed record FilesystemSearchTextDto(
    string Root,
    string Query,
    IReadOnlyList<FilesystemTextMatchDto> Matches,
    int FilesRead,
    long BytesRead,
    bool Truncated);

public sealed record FilesystemReadFileInput(
    string Path,
    int? Offset = null,
    int? Limit = null);

public sealed record FilesystemReadFileDto(
    string RequestedPath,
    string FullPath,
    int StartLine,
    int EndLine,
    int TotalLines,
    bool Truncated,
    string Text);

public sealed record FilesystemReadFilesDto(
    IReadOnlyList<FilesystemReadFileDto> Files);

public sealed record FilesystemPatchChangeInput(
    string Op,
    string? Path = null,
    string? Content = null,
    bool Overwrite = false,
    string? OldText = null,
    string? NewText = null,
    int ExpectedOccurrences = 1,
    string? FromPath = null,
    string? ToPath = null);

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
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<FilesystemListTreeDto>),
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Lists a bounded recursive directory tree. Relative paths require a work session with base_directory.")]
    public async Task<CallToolResult> ListTree(
        [Description("Directory path. May be absolute or relative to the work session base directory.")] string path = ".",
        [Description("Optional work session handle used to resolve relative paths.")] string? workId = null,
        [Description("Maximum recursion depth, from 1 to 32.")] int maxDepth = 3,
        [Description("Maximum entries returned, from 1 to 5000.")] int maxEntries = 1000,
        CancellationToken cancellationToken = default)
    {
        var result = await _filesystem.ListTreeAsync(
            path,
            ParseWorkId(workId),
            maxDepth,
            maxEntries,
            cancellationToken).ConfigureAwait(false);

        return McpToolResults.From(MapListTree(result));
    }

    [McpServerTool(
        Name = "filesystem_find_paths",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<FilesystemFindPathsDto>),
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Finds files and directories by literal path queries without shell globbing or regex.")]
    public async Task<CallToolResult> FindPaths(
        [Description("Root directory to search.")] string path,
        [Description("One to 32 literal path queries. Multiple queries use OR semantics.")] string[] queries,
        [Description("substring or suffix.")] string matchMode = "substring",
        [Description("Optional entry type: file, directory, symlink, or any.")] string type = "any",
        [Description("Optional work session handle used to resolve relative paths.")] string? workId = null,
        int maxDepth = 12,
        int maxResults = 100,
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
            maxDepth,
            maxResults,
            cancellationToken).ConfigureAwait(false);

        return McpToolResults.From(MapFindPaths(result));
    }

    [McpServerTool(
        Name = "filesystem_search_text",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<FilesystemSearchTextDto>),
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Searches bounded text content and returns path, line, column, and small context.")]
    public async Task<CallToolResult> SearchText(
        [Description("File or directory path to search.")] string path,
        [Description("Literal text query.")] string query,
        [Description("Optional work session handle used to resolve relative paths.")] string? workId = null,
        bool caseSensitive = false,
        int maxDepth = 12,
        int maxResults = 100,
        int contextLines = 1,
        CancellationToken cancellationToken = default)
    {
        var result = await _filesystem.SearchTextAsync(
            path,
            query,
            ParseWorkId(workId),
            caseSensitive,
            maxDepth,
            maxResults,
            contextLines,
            cancellationToken).ConfigureAwait(false);

        return McpToolResults.From(MapSearchText(result));
    }

    [McpServerTool(
        Name = "filesystem_read_files",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<FilesystemReadFilesDto>),
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Reads up to 32 UTF-8-compatible text files, optionally by 1-based line range.")]
    public async Task<CallToolResult> ReadFiles(
        [Description("Files to read. Offset and limit are 1-based line controls.")] FilesystemReadFileInput[] files,
        [Description("Optional work session handle used to resolve relative paths.")] string? workId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _filesystem.ReadFilesAsync(
            files.Select(file => (file.Path, file.Offset, file.Limit)).ToArray(),
            ParseWorkId(workId),
            cancellationToken).ConfigureAwait(false);

        return McpToolResults.From(MapReadFiles(result));
    }

    [McpServerTool(
        Name = "filesystem_apply_patch",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<FilesystemPatchDto>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true)]
    [Description("Applies validated text-file write, replace, delete, and move operations. Dependent edits to the same path must be split into separate calls.")]
    public async Task<CallToolResult> ApplyPatch(
        [Description("One to 64 patch operations. op is write, replace, delete, or move.")] FilesystemPatchChangeInput[] changes,
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
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<FilesystemDirectoryDto>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = true)]
    [Description("Creates a directory including missing parents, or deletes one existing empty directory.")]
    public async Task<CallToolResult> ManageDirectory(
        [Description("create or delete.")] string action,
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
                        result.Value.Truncated)))
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
                        result.Value.Truncated)))
            : ToolEnvelope<FilesystemFindPathsDto>.From(
                LoomResult<FilesystemFindPathsDto>.Failure(result.Error!));

    private static ToolEnvelope<FilesystemSearchTextDto> MapSearchText(
        LoomResult<FilesystemSearchTextResult> result)
        => result.IsSuccess
            ? ToolEnvelope<FilesystemSearchTextDto>.From(
                LoomResult<FilesystemSearchTextDto>.Success(
                    new FilesystemSearchTextDto(
                        result.Value!.Root,
                        result.Value.Query,
                        result.Value.Matches.Select(match => new FilesystemTextMatchDto(
                            match.Path,
                            match.Line,
                            match.Column,
                            match.Text,
                            match.ContextBefore,
                            match.ContextAfter)).ToArray(),
                        result.Value.FilesRead,
                        result.Value.BytesRead,
                        result.Value.Truncated)))
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
                            file.Truncated,
                            file.Text)).ToArray())))
            : ToolEnvelope<FilesystemReadFilesDto>.From(
                LoomResult<FilesystemReadFilesDto>.Failure(result.Error!));

    private static FilesystemEntryDto ToDto(FilesystemEntry entry)
        => new(
            entry.Path,
            entry.Name,
            entry.Type.ToString().ToLowerInvariant(),
            entry.Size,
            entry.Depth);

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
