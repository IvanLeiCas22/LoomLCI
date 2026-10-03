namespace LoomLCI.Core.Filesystem;

public enum FilesystemEntryType
{
    File,
    Directory,
    Symlink
}

public enum FilesystemPathMatchMode
{
    Substring,
    Suffix
}

public sealed record FilesystemTraversalOptions(
    bool IncludeGenerated = false,
    IReadOnlyList<string>? ExcludeDirectories = null);

public sealed record FilesystemEntry(
    string Path,
    string Name,
    FilesystemEntryType Type,
    long? Size,
    int Depth,
    string? ChildrenExcluded = null);

public sealed record FilesystemListTreeResult(
    string Root,
    int MaxDepth,
    int MaxEntries,
    IReadOnlyList<FilesystemEntry> Entries,
    bool Truncated);

public sealed record FilesystemFindPathsResult(
    string Root,
    IReadOnlyList<string> Queries,
    FilesystemPathMatchMode MatchMode,
    IReadOnlyList<FilesystemEntry> Matches,
    bool Truncated);

public sealed record FilesystemTextMatch(
    string Path,
    string Query,
    int Line,
    int Column,
    string Text,
    IReadOnlyList<string> ContextBefore,
    IReadOnlyList<string> ContextAfter);

public sealed record FilesystemSearchTextResult(
    string Root,
    IReadOnlyList<string> Queries,
    IReadOnlyList<FilesystemTextMatch> Matches,
    int FilesRead,
    long BytesRead,
    bool Truncated);

public sealed record FilesystemReadFileRequest(
    string RequestedPath,
    string FullPath,
    int? Offset = null,
    int? Limit = null);

public sealed record FilesystemReadFileResult(
    string RequestedPath,
    string FullPath,
    int StartLine,
    int EndLine,
    int TotalLines,
    bool Truncated,
    string Text);

public sealed record FilesystemReadFilesResult(
    IReadOnlyList<FilesystemReadFileResult> Files);

public enum FilesystemPatchOperation
{
    Write,
    Replace,
    Delete,
    Move
}

public sealed record FilesystemPatchChange(
    FilesystemPatchOperation Operation,
    string? Path = null,
    string? Content = null,
    bool Overwrite = false,
    string? OldText = null,
    string? NewText = null,
    int ExpectedOccurrences = 1,
    string? FromPath = null,
    string? ToPath = null);

public sealed record FilesystemPatchResult(int AppliedChanges);

public sealed record FilesystemDirectoryResult(string Path, bool Exists);

public interface IFilesystemProvider
{
    Task<LoomResult<FilesystemListTreeResult>> ListTreeAsync(
        string root,
        FilesystemTraversalOptions traversal,
        int maxDepth,
        int maxEntries,
        CancellationToken cancellationToken);

    Task<LoomResult<FilesystemFindPathsResult>> FindPathsAsync(
        string root,
        IReadOnlyList<string> queries,
        FilesystemPathMatchMode matchMode,
        FilesystemEntryType? type,
        FilesystemTraversalOptions traversal,
        int maxDepth,
        int maxResults,
        CancellationToken cancellationToken);

    Task<LoomResult<FilesystemSearchTextResult>> SearchTextAsync(
        string root,
        IReadOnlyList<string> queries,
        bool caseSensitive,
        FilesystemTraversalOptions traversal,
        int maxDepth,
        int maxResults,
        int contextLines,
        CancellationToken cancellationToken);

    Task<LoomResult<FilesystemReadFilesResult>> ReadFilesAsync(
        IReadOnlyList<FilesystemReadFileRequest> files,
        CancellationToken cancellationToken);

    Task<LoomResult<FilesystemPatchResult>> ApplyPatchAsync(
        IReadOnlyList<FilesystemPatchChange> changes,
        CancellationToken cancellationToken);

    Task<LoomResult<FilesystemDirectoryResult>> CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken);

    Task<LoomResult<FilesystemDirectoryResult>> DeleteDirectoryAsync(
        string path,
        CancellationToken cancellationToken);
}
