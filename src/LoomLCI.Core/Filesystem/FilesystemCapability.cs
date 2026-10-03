using LoomLCI.Core.Invocations;
using LoomLCI.Core.Observability;

namespace LoomLCI.Core.Filesystem;

public sealed class FilesystemCapability
{
    private readonly IFilesystemProvider _provider;
    private readonly InvocationRunner _invocations;
    private readonly LoomEventBus _events;

    public FilesystemCapability(
        IFilesystemProvider provider,
        InvocationRunner invocations,
        LoomEventBus events)
    {
        _provider = provider;
        _invocations = invocations;
        _events = events;
    }

    public Task<LoomResult<FilesystemListTreeResult>> ListTreeAsync(
        string path,
        WorkId? workId = null,
        bool includeGenerated = false,
        IReadOnlyList<string>? excludeDirectories = null,
        int maxDepth = 3,
        int maxEntries = 1000,
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => _invocations.RunAsync(
            "filesystem.list_tree",
            workId,
            async (context, token) =>
            {
                if (maxDepth is < 1 or > 32)
                {
                    return LoomResult<FilesystemListTreeResult>.Failure(
                        LoomErrors.InvalidArgument("max_depth must be between 1 and 32."));
                }

                if (maxEntries is < 1 or > 5000)
                {
                    return LoomResult<FilesystemListTreeResult>.Failure(
                        LoomErrors.InvalidArgument("max_entries must be between 1 and 5000."));
                }

                var traversal = CreateTraversalOptions(includeGenerated, excludeDirectories);
                if (!traversal.IsSuccess)
                {
                    return LoomResult<FilesystemListTreeResult>.Failure(traversal.Error!);
                }

                var resolved = ResolvePath(path, context.WorkSession?.BaseDirectory);
                return resolved.IsSuccess
                    ? await _provider.ListTreeAsync(
                        resolved.Value!,
                        traversal.Value!,
                        maxDepth,
                        maxEntries,
                        cursor,
                        token).ConfigureAwait(false)
                    : LoomResult<FilesystemListTreeResult>.Failure(resolved.Error!);
            },
            cancellationToken);

    public Task<LoomResult<FilesystemFindPathsResult>> FindPathsAsync(
        string path,
        IReadOnlyList<string> queries,
        FilesystemPathMatchMode matchMode = FilesystemPathMatchMode.Substring,
        FilesystemEntryType? type = null,
        WorkId? workId = null,
        bool includeGenerated = false,
        IReadOnlyList<string>? excludeDirectories = null,
        int maxDepth = 12,
        int maxResults = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => _invocations.RunAsync(
            "filesystem.find_paths",
            workId,
            async (context, token) =>
            {
                if (queries.Count is < 1 or > 32 || queries.Any(string.IsNullOrWhiteSpace))
                {
                    return LoomResult<FilesystemFindPathsResult>.Failure(
                        LoomErrors.InvalidArgument("queries must contain between 1 and 32 non-empty values."));
                }

                if (maxDepth is < 1 or > 32 || maxResults is < 1 or > 1000)
                {
                    return LoomResult<FilesystemFindPathsResult>.Failure(
                        LoomErrors.InvalidArgument("max_depth must be 1..32 and max_results must be 1..1000."));
                }

                var traversal = CreateTraversalOptions(includeGenerated, excludeDirectories);
                if (!traversal.IsSuccess)
                {
                    return LoomResult<FilesystemFindPathsResult>.Failure(traversal.Error!);
                }

                var resolved = ResolvePath(path, context.WorkSession?.BaseDirectory);
                return resolved.IsSuccess
                    ? await _provider.FindPathsAsync(
                        resolved.Value!,
                        queries,
                        matchMode,
                        type,
                        traversal.Value!,
                        maxDepth,
                        maxResults,
                        cursor,
                        token).ConfigureAwait(false)
                    : LoomResult<FilesystemFindPathsResult>.Failure(resolved.Error!);
            },
            cancellationToken);

    public Task<LoomResult<FilesystemSearchTextResult>> SearchTextAsync(
        string path,
        IReadOnlyList<string> queries,
        WorkId? workId = null,
        bool caseSensitive = false,
        bool includeGenerated = false,
        IReadOnlyList<string>? excludeDirectories = null,
        int maxDepth = 12,
        int maxResults = 100,
        int contextLines = 1,
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => _invocations.RunAsync(
            "filesystem.search_text",
            workId,
            async (context, token) =>
            {
                if (queries.Count is < 1 or > 32 || queries.Any(string.IsNullOrWhiteSpace))
                {
                    return LoomResult<FilesystemSearchTextResult>.Failure(
                        LoomErrors.InvalidArgument(
                            "queries must contain between 1 and 32 non-empty values."));
                }

                if (maxDepth is < 1 or > 32 ||
                    maxResults is < 1 or > 500 ||
                    contextLines is < 0 or > 3)
                {
                    return LoomResult<FilesystemSearchTextResult>.Failure(
                        LoomErrors.InvalidArgument(
                            "max_depth must be 1..32, max_results 1..500, and context_lines 0..3."));
                }

                var traversal = CreateTraversalOptions(includeGenerated, excludeDirectories);
                if (!traversal.IsSuccess)
                {
                    return LoomResult<FilesystemSearchTextResult>.Failure(traversal.Error!);
                }

                var resolved = ResolvePath(path, context.WorkSession?.BaseDirectory);
                return resolved.IsSuccess
                    ? await _provider.SearchTextAsync(
                        resolved.Value!,
                        queries,
                        caseSensitive,
                        traversal.Value!,
                        maxDepth,
                        maxResults,
                        contextLines,
                        cursor,
                        token).ConfigureAwait(false)
                    : LoomResult<FilesystemSearchTextResult>.Failure(resolved.Error!);
            },
            cancellationToken);

    public Task<LoomResult<FilesystemReadFilesResult>> ReadFilesAsync(
        IReadOnlyList<(string Path, int? Offset, int? Limit)> files,
        WorkId? workId = null,
        CancellationToken cancellationToken = default)
        => _invocations.RunAsync(
            "filesystem.read_files",
            workId,
            async (context, token) =>
            {
                if (files.Count is < 1 or > 32)
                {
                    return LoomResult<FilesystemReadFilesResult>.Failure(
                        LoomErrors.InvalidArgument("files must contain between 1 and 32 paths."));
                }

                var requests = new List<FilesystemReadFileRequest>(files.Count);
                foreach (var file in files)
                {
                    if (file.Offset is < 1 || file.Limit is < 1 or > 10000)
                    {
                        return LoomResult<FilesystemReadFilesResult>.Failure(
                            LoomErrors.InvalidArgument("offset must be >= 1 and limit must be 1..10000."));
                    }

                    var resolved = ResolvePath(file.Path, context.WorkSession?.BaseDirectory);
                    if (!resolved.IsSuccess)
                    {
                        return LoomResult<FilesystemReadFilesResult>.Failure(resolved.Error!);
                    }

                    requests.Add(new FilesystemReadFileRequest(
                        file.Path,
                        resolved.Value!,
                        file.Offset,
                        file.Limit));
                }

                return await _provider.ReadFilesAsync(requests, token).ConfigureAwait(false);
            },
            cancellationToken);

    public Task<LoomResult<FilesystemPatchResult>> ApplyPatchAsync(
        IReadOnlyList<FilesystemPatchChange> changes,
        WorkId? workId = null,
        CancellationToken cancellationToken = default)
        => _invocations.RunAsync(
            "filesystem.apply_patch",
            workId,
            async (context, token) =>
            {
                if (changes.Count is < 1 or > 64)
                {
                    return LoomResult<FilesystemPatchResult>.Failure(
                        LoomErrors.InvalidArgument("changes must contain between 1 and 64 operations."));
                }

                var normalized = new List<FilesystemPatchChange>(changes.Count);
                foreach (var change in changes)
                {
                    var mapped = ResolveChange(change, context.WorkSession?.BaseDirectory);
                    if (!mapped.IsSuccess)
                    {
                        return LoomResult<FilesystemPatchResult>.Failure(mapped.Error!);
                    }

                    normalized.Add(mapped.Value!);
                }

                var result = await _provider.ApplyPatchAsync(normalized, token).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    _events.Publish(
                        "FilesystemPatched",
                        "filesystem",
                        context.WorkSession?.Id,
                        context.Id,
                        payload: new Dictionary<string, object?> { ["changes"] = result.Value!.AppliedChanges });
                }

                return result;
            },
            cancellationToken);

    public Task<LoomResult<FilesystemDirectoryResult>> CreateDirectoryAsync(
        string path,
        WorkId? workId = null,
        CancellationToken cancellationToken = default)
        => ManageDirectoryAsync("filesystem.directory.create", path, workId, true, cancellationToken);

    public Task<LoomResult<FilesystemDirectoryResult>> DeleteDirectoryAsync(
        string path,
        WorkId? workId = null,
        CancellationToken cancellationToken = default)
        => ManageDirectoryAsync("filesystem.directory.delete", path, workId, false, cancellationToken);

    private Task<LoomResult<FilesystemDirectoryResult>> ManageDirectoryAsync(
        string operation,
        string path,
        WorkId? workId,
        bool create,
        CancellationToken cancellationToken)
        => _invocations.RunAsync(
            operation,
            workId,
            async (context, token) =>
            {
                var resolved = ResolvePath(path, context.WorkSession?.BaseDirectory);
                if (!resolved.IsSuccess)
                {
                    return LoomResult<FilesystemDirectoryResult>.Failure(resolved.Error!);
                }

                var result = create
                    ? await _provider.CreateDirectoryAsync(resolved.Value!, token).ConfigureAwait(false)
                    : await _provider.DeleteDirectoryAsync(resolved.Value!, token).ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    _events.Publish(
                        create ? "DirectoryCreated" : "DirectoryDeleted",
                        "filesystem",
                        context.WorkSession?.Id,
                        context.Id,
                        payload: new Dictionary<string, object?> { ["path"] = result.Value!.Path });
                }

                return result;
            },
            cancellationToken);

    private static LoomResult<FilesystemPatchChange> ResolveChange(
        FilesystemPatchChange change,
        string? baseDirectory)
    {
        LoomResult<string> ResolveRequired(string? value, string label)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return LoomResult<string>.Failure(LoomErrors.InvalidArgument($"{label} is required."));
            }

            var resolved = ResolvePath(value, baseDirectory);
            return resolved.IsSuccess
                ? LoomResult<string>.Success(resolved.Value!)
                : LoomResult<string>.Failure(resolved.Error!);
        }

        switch (change.Operation)
        {
            case FilesystemPatchOperation.Write:
            {
                var path = ResolveRequired(change.Path, "path");
                if (!path.IsSuccess)
                {
                    return LoomResult<FilesystemPatchChange>.Failure(path.Error!);
                }

                if (change.Content is null)
                {
                    return LoomResult<FilesystemPatchChange>.Failure(
                        LoomErrors.InvalidArgument("content is required for write."));
                }

                return LoomResult<FilesystemPatchChange>.Success(change with { Path = path.Value });
            }

            case FilesystemPatchOperation.Replace:
            {
                var path = ResolveRequired(change.Path, "path");
                if (!path.IsSuccess)
                {
                    return LoomResult<FilesystemPatchChange>.Failure(path.Error!);
                }

                if (string.IsNullOrEmpty(change.OldText) || change.NewText is null || change.ExpectedOccurrences < 1)
                {
                    return LoomResult<FilesystemPatchChange>.Failure(
                        LoomErrors.InvalidArgument(
                            "replace requires non-empty old_text, new_text, and expected_occurrences >= 1."));
                }

                return LoomResult<FilesystemPatchChange>.Success(change with { Path = path.Value });
            }

            case FilesystemPatchOperation.Delete:
            {
                var path = ResolveRequired(change.Path, "path");
                return path.IsSuccess
                    ? LoomResult<FilesystemPatchChange>.Success(change with { Path = path.Value })
                    : LoomResult<FilesystemPatchChange>.Failure(path.Error!);
            }

            case FilesystemPatchOperation.Move:
            {
                var from = ResolveRequired(change.FromPath, "from_path");
                if (!from.IsSuccess)
                {
                    return LoomResult<FilesystemPatchChange>.Failure(from.Error!);
                }

                var to = ResolveRequired(change.ToPath, "to_path");
                return to.IsSuccess
                    ? LoomResult<FilesystemPatchChange>.Success(change with
                    {
                        FromPath = from.Value,
                        ToPath = to.Value
                    })
                    : LoomResult<FilesystemPatchChange>.Failure(to.Error!);
            }

            default:
                return LoomResult<FilesystemPatchChange>.Failure(
                    LoomErrors.InvalidArgument("Unsupported patch operation."));
        }
    }

    private static LoomResult<FilesystemTraversalOptions> CreateTraversalOptions(
        bool includeGenerated,
        IReadOnlyList<string>? excludeDirectories)
    {
        if (excludeDirectories is { Count: > 64 })
        {
            return LoomResult<FilesystemTraversalOptions>.Failure(
                LoomErrors.InvalidArgument("exclude_directories must contain at most 64 directory names."));
        }

        if (excludeDirectories is not null)
        {
            foreach (var directory in excludeDirectories)
            {
                if (string.IsNullOrWhiteSpace(directory) ||
                    directory is "." or ".." ||
                    directory.Contains('/') ||
                    directory.Contains('\\'))
                {
                    return LoomResult<FilesystemTraversalOptions>.Failure(
                        LoomErrors.InvalidArgument(
                            "exclude_directories values must be non-empty directory names, not paths."));
                }
            }
        }

        return LoomResult<FilesystemTraversalOptions>.Success(
            new FilesystemTraversalOptions(includeGenerated, excludeDirectories));
    }

    private static LoomResult<string> ResolvePath(string? requested, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return LoomResult<string>.Failure(LoomErrors.InvalidArgument("path is required."));
        }

        try
        {
            if (Path.IsPathFullyQualified(requested))
            {
                return LoomResult<string>.Success(Path.GetFullPath(requested));
            }

            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                return LoomResult<string>.Failure(
                    LoomErrors.InvalidArgument(
                        "A relative path requires a work session with base_directory."));
            }

            return LoomResult<string>.Success(Path.GetFullPath(Path.Combine(baseDirectory, requested)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return LoomResult<string>.Failure(LoomErrors.InvalidArgument($"Invalid path: {ex.Message}"));
        }
    }
}
