using System.Security.Cryptography;
using System.Text;
using LoomLCI.Core;
using LoomLCI.Core.Filesystem;

namespace LoomLCI.Windows.Filesystem;

public sealed class WindowsFilesystemProvider : IFilesystemProvider
{
    private const long MaxTextFileBytes = 16L * 1024 * 1024;
    private static readonly object PatchGate = new();
    private const long DefaultMaxSearchTotalBytes = 64L * 1024 * 1024;
    private const long MaxReadTotalBytes = 64L * 1024 * 1024;
    private readonly long _maxSearchTotalBytes;
    private readonly Action<string, string>? _patchStageHook;

    public WindowsFilesystemProvider()
        : this(DefaultMaxSearchTotalBytes)
    {
    }

    internal WindowsFilesystemProvider(
        long maxSearchTotalBytes,
        Action<string, string>? patchStageHook = null)
    {
        _maxSearchTotalBytes = maxSearchTotalBytes > 0
            ? maxSearchTotalBytes
            : throw new ArgumentOutOfRangeException(nameof(maxSearchTotalBytes));
        _patchStageHook = patchStageHook;
    }
    private const int MaxSkippedFileSamples = 20;

    private static readonly string[] DefaultGeneratedDirectories =
    [
        ".git",
        ".vs",
        ".venv",
        "__pycache__",
        "bin",
        "node_modules",
        "obj"
    ];

    public Task<LoomResult<FilesystemListTreeResult>> ListTreeAsync(
        string root,
        FilesystemTraversalOptions traversal,
        int maxDepth,
        int maxEntries,
        string? cursor,
        CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    return LoomResult<FilesystemListTreeResult>.Failure(
                        LoomErrors.NotFound($"Directory '{root}' was not found."));
                }

                var fingerprint = FilesystemCursorCodec.CreateListTreeFingerprint(root, traversal, maxDepth);
                var decoded = FilesystemCursorCodec.Decode(cursor, "list_tree", fingerprint);
                if (!decoded.IsSuccess)
                {
                    return LoomResult<FilesystemListTreeResult>.Failure(decoded.Error!);
                }

                var continuation = decoded.Value;
                var entries = new List<FilesystemEntry>(Math.Min(maxEntries, 1024));
                string? nextCursor = null;
                var ordinal = 0L;
                var resumeValidated = continuation is null;

                foreach (var item in Enumerate(root, traversal, maxDepth, cancellationToken))
                {
                    var relative = NormalizeRelative(Path.GetRelativePath(root, item.FullPath));

                    if (!resumeValidated)
                    {
                        if (ordinal < continuation!.Ordinal)
                        {
                            ordinal++;
                            continue;
                        }

                        if (ordinal != continuation.Ordinal ||
                            !string.Equals(relative, continuation.ExpectedPath, StringComparison.OrdinalIgnoreCase) ||
                            item.Type != continuation.ExpectedType)
                        {
                            return LoomResult<FilesystemListTreeResult>.Failure(
                                FilesystemCursorCodec.StaleCursorError());
                        }

                        resumeValidated = true;
                    }

                    if (entries.Count >= maxEntries)
                    {
                        nextCursor = FilesystemCursorCodec.Encode(
                            "list_tree",
                            fingerprint,
                            new FilesystemTraversalContinuation(ordinal, relative, item.Type));
                        break;
                    }

                    entries.Add(ToEntry(
                        root,
                        item.FullPath,
                        item.Depth,
                        item.Type,
                        item.ChildrenExcluded));
                    ordinal++;
                }

                if (!resumeValidated)
                {
                    return LoomResult<FilesystemListTreeResult>.Failure(
                        FilesystemCursorCodec.StaleCursorError());
                }

                return LoomResult<FilesystemListTreeResult>.Success(
                    new FilesystemListTreeResult(
                        root,
                        maxDepth,
                        maxEntries,
                        entries,
                        nextCursor is not null,
                        nextCursor));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return LoomResult<FilesystemListTreeResult>.Failure(MapException(ex));
            }
        }, cancellationToken);

    public Task<LoomResult<FilesystemFindPathsResult>> FindPathsAsync(
        string root,
        IReadOnlyList<string> queries,
        FilesystemPathMatchMode matchMode,
        FilesystemEntryType? type,
        FilesystemTraversalOptions traversal,
        int maxDepth,
        int maxResults,
        string? cursor,
        CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    return LoomResult<FilesystemFindPathsResult>.Failure(
                        LoomErrors.NotFound($"Directory '{root}' was not found."));
                }

                var fingerprint = FilesystemCursorCodec.CreateFindPathsFingerprint(
                    root,
                    queries,
                    matchMode,
                    type,
                    traversal,
                    maxDepth);
                var decoded = FilesystemCursorCodec.Decode(cursor, "find_paths", fingerprint);
                if (!decoded.IsSuccess)
                {
                    return LoomResult<FilesystemFindPathsResult>.Failure(decoded.Error!);
                }

                var continuation = decoded.Value;
                var comparison = StringComparison.OrdinalIgnoreCase;
                var matches = new List<FilesystemEntry>(Math.Min(maxResults, 256));
                string? nextCursor = null;
                var ordinal = 0L;
                var resumeValidated = continuation is null;

                foreach (var item in Enumerate(root, traversal, maxDepth, cancellationToken))
                {
                    var relative = NormalizeRelative(Path.GetRelativePath(root, item.FullPath));

                    if (!resumeValidated)
                    {
                        if (ordinal < continuation!.Ordinal)
                        {
                            ordinal++;
                            continue;
                        }

                        if (ordinal != continuation.Ordinal ||
                            !string.Equals(relative, continuation.ExpectedPath, StringComparison.OrdinalIgnoreCase) ||
                            item.Type != continuation.ExpectedType)
                        {
                            return LoomResult<FilesystemFindPathsResult>.Failure(
                                FilesystemCursorCodec.StaleCursorError());
                        }

                        resumeValidated = true;
                    }

                    var matched =
                        (type is null || item.Type == type) &&
                        queries.Any(query => matchMode switch
                        {
                            FilesystemPathMatchMode.Substring => relative.Contains(query, comparison),
                            FilesystemPathMatchMode.Suffix => relative.EndsWith(query, comparison),
                            _ => false
                        });

                    if (matched)
                    {
                        if (matches.Count >= maxResults)
                        {
                            nextCursor = FilesystemCursorCodec.Encode(
                                "find_paths",
                                fingerprint,
                                new FilesystemTraversalContinuation(ordinal, relative, item.Type));
                            break;
                        }

                        matches.Add(ToEntry(
                            root,
                            item.FullPath,
                            item.Depth,
                            item.Type,
                            item.ChildrenExcluded));
                    }

                    ordinal++;
                }

                if (!resumeValidated)
                {
                    return LoomResult<FilesystemFindPathsResult>.Failure(
                        FilesystemCursorCodec.StaleCursorError());
                }

                return LoomResult<FilesystemFindPathsResult>.Success(
                    new FilesystemFindPathsResult(
                        root,
                        queries,
                        matchMode,
                        matches,
                        nextCursor is not null,
                        nextCursor));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return LoomResult<FilesystemFindPathsResult>.Failure(MapException(ex));
            }
        }, cancellationToken);

    public async Task<LoomResult<FilesystemSearchTextResult>> SearchTextAsync(
        string root,
        IReadOnlyList<string> queries,
        bool caseSensitive,
        FilesystemTraversalOptions traversal,
        int maxDepth,
        int maxResults,
        int contextLines,
        string? cursor,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(root) && !Directory.Exists(root))
            {
                return LoomResult<FilesystemSearchTextResult>.Failure(
                    LoomErrors.NotFound($"Path '{root}' was not found."));
            }

            var fingerprint = FilesystemCursorCodec.CreateSearchTextFingerprint(
                root,
                queries,
                caseSensitive,
                traversal,
                maxDepth,
                contextLines);
            var decoded = FilesystemCursorCodec.DecodeSearch(cursor, fingerprint);
            if (!decoded.IsSuccess)
            {
                return LoomResult<FilesystemSearchTextResult>.Failure(decoded.Error!);
            }

            var continuation = decoded.Value;
            var matches = new List<FilesystemTextMatch>(Math.Min(maxResults, 128));
            var filesRead = 0;
            long bytesRead = 0;
            var resultLimitReached = false;
            var scanLimitReached = false;
            var skippedBinaryFileCount = 0;
            var skippedBinaryFiles = new List<string>();
            string? nextCursor = null;
            var resumeValidated = continuation is null;

            foreach (var file in EnumerateSearchFiles(
                         root,
                         traversal,
                         maxDepth,
                         cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var resumesThisFile = false;
                if (!resumeValidated)
                {
                    if (file.Ordinal < continuation!.Ordinal)
                    {
                        continue;
                    }

                    if (file.Ordinal != continuation.Ordinal ||
                        !string.Equals(
                            file.RelativePath,
                            continuation.ExpectedPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return LoomResult<FilesystemSearchTextResult>.Failure(
                            FilesystemCursorCodec.StaleCursorError());
                    }

                    resumeValidated = true;
                    resumesThisFile = true;
                }

                var before = new FileInfo(file.FullPath);
                var beforeLength = before.Length;
                var beforeWriteTicks = before.LastWriteTimeUtc.Ticks;
                var format = await StreamingTextSearchReader.InspectAsync(
                    file.FullPath,
                    cancellationToken).ConfigureAwait(false);

                if (resumesThisFile &&
                    (beforeLength != continuation!.FileLength ||
                     beforeWriteTicks != continuation.LastWriteTimeUtcTicks ||
                     !string.Equals(
                         format.EncodingId,
                         continuation.EncodingId,
                         StringComparison.Ordinal)))
                {
                    return LoomResult<FilesystemSearchTextResult>.Failure(
                        FilesystemCursorCodec.StaleCursorError());
                }

                if (format.IsBinary)
                {
                    if (resumesThisFile)
                    {
                        return LoomResult<FilesystemSearchTextResult>.Failure(
                            FilesystemCursorCodec.StaleCursorError());
                    }

                    skippedBinaryFileCount++;
                    if (skippedBinaryFiles.Count < MaxSkippedFileSamples)
                    {
                        skippedBinaryFiles.Add(file.RelativePath);
                    }
                    continue;
                }

                var startLine = resumesThisFile
                    ? continuation!.ContextStartLine
                    : 1;
                var startOffset = resumesThisFile
                    ? continuation!.ContextStartByteOffset
                    : format.PreambleLength;
                var emitMatchesFromLine = resumesThisFile
                    ? continuation!.NextLine
                    : 1;

                var remainingBudget = _maxSearchTotalBytes - bytesRead;
                if (remainingBudget <= 0)
                {
                    scanLimitReached = true;
                    nextCursor = FilesystemCursorCodec.EncodeSearch(
                        fingerprint,
                        new FilesystemSearchContinuation(
                            file.Ordinal,
                            file.RelativePath,
                            emitMatchesFromLine,
                            resumesThisFile
                                ? continuation!.NextByteOffset
                                : format.PreambleLength,
                            startLine,
                            startOffset,
                            beforeLength,
                            beforeWriteTicks,
                            format.EncodingId));
                    break;
                }

                filesRead++;
                var scanned = await StreamingTextSearchFileScanner.ScanAsync(
                    file.FullPath,
                    file.RelativePath,
                    format,
                    queries,
                    caseSensitive,
                    contextLines,
                    startLine,
                    startOffset,
                    emitMatchesFromLine,
                    maxResults - matches.Count,
                    remainingBudget,
                    cancellationToken).ConfigureAwait(false);

                var after = new FileInfo(file.FullPath);
                if (after.Length != beforeLength ||
                    after.LastWriteTimeUtc.Ticks != beforeWriteTicks)
                {
                    return LoomResult<FilesystemSearchTextResult>.Failure(
                        new LoomError(
                            "conflict",
                            $"File '{file.RelativePath}' changed while it was being searched. Restart the search.",
                            false,
                            new Dictionary<string, object?>
                            {
                                ["reason"] = "file_changed_during_search"
                            }));
                }

                matches.AddRange(scanned.Matches);
                bytesRead += scanned.BytesRead;
                resultLimitReached |= scanned.ResultLimitReached;
                scanLimitReached |= scanned.ScanLimitReached;

                if (scanned.ResumePoint is { } point)
                {
                    nextCursor = FilesystemCursorCodec.EncodeSearch(
                        fingerprint,
                        new FilesystemSearchContinuation(
                            file.Ordinal,
                            file.RelativePath,
                            point.NextLine,
                            point.NextByteOffset,
                            point.ContextStartLine,
                            point.ContextStartByteOffset,
                            beforeLength,
                            beforeWriteTicks,
                            format.EncodingId));
                    break;
                }

                continuation = null;
            }

            if (!resumeValidated)
            {
                return LoomResult<FilesystemSearchTextResult>.Failure(
                    FilesystemCursorCodec.StaleCursorError());
            }

            return LoomResult<FilesystemSearchTextResult>.Success(
                new FilesystemSearchTextResult(
                    root,
                    queries,
                    matches,
                    filesRead,
                    bytesRead,
                    nextCursor is not null,
                    resultLimitReached,
                    scanLimitReached,
                    skippedBinaryFileCount,
                    skippedBinaryFiles,
                    nextCursor));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LoomResult<FilesystemSearchTextResult>.Failure(MapException(ex));
        }
    }

    public async Task<LoomResult<FilesystemReadFilesResult>> ReadFilesAsync(
        IReadOnlyList<FilesystemReadFileRequest> files,
        CancellationToken cancellationToken)
    {
        try
        {
            long totalBytes = 0;
            var results = new List<FilesystemReadFileResult>(files.Count);

            foreach (var request in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(request.FullPath))
                {
                    return LoomResult<FilesystemReadFilesResult>.Failure(
                        LoomErrors.NotFound($"File '{request.FullPath}' was not found."));
                }

                var info = new FileInfo(request.FullPath);
                if (request.Limit is null && info.Length > MaxTextFileBytes)
                {
                    return LoomResult<FilesystemReadFilesResult>.Failure(
                        LoomErrors.Unsupported(
                            $"File '{request.FullPath}' exceeds the 16 MiB unbounded text read limit. " +
                            "Specify a line limit to read large files in bounded ranges."));
                }

                var ranged = await ReadTextRangeAsync(
                    request.FullPath,
                    request.Offset,
                    request.Limit,
                    cancellationToken).ConfigureAwait(false);
                if (!ranged.IsSuccess)
                {
                    return LoomResult<FilesystemReadFilesResult>.Failure(ranged.Error!);
                }

                var value = ranged.Value!;
                totalBytes += Encoding.UTF8.GetByteCount(value.Text);
                if (totalBytes > MaxReadTotalBytes)
                {
                    return LoomResult<FilesystemReadFilesResult>.Failure(
                        LoomErrors.Unsupported(
                            "The returned text would exceed the 64 MiB aggregate read limit. " +
                            "Use smaller line ranges or split the files across calls."));
                }

                results.Add(new FilesystemReadFileResult(
                    request.RequestedPath,
                    request.FullPath,
                    value.StartLine,
                    value.EndLine,
                    value.TotalLines,
                    value.HasMoreBefore,
                    value.HasMoreAfter,
                    value.Text));
            }

            return LoomResult<FilesystemReadFilesResult>.Success(new FilesystemReadFilesResult(results));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LoomResult<FilesystemReadFilesResult>.Failure(MapException(ex));
        }
    }

    public Task<LoomResult<FilesystemPatchResult>> ApplyPatchAsync(
        IReadOnlyList<FilesystemPatchChange> changes,
        CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            // A batch is serialized with every other patch batch in this Host.
            // External processes may still edit files: each text target is rechecked
            // immediately before publishing.
            lock (PatchGate)
            {
                try
                {
                    var snapshots = ValidatePatch(changes, cancellationToken);
                    var undo = new Stack<(string Path, Action Restore)>();
                    var cleanup = new Stack<(string Path, Action Delete)>();

                    try
                    {
                        foreach (var change in changes)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            switch (change.Operation)
                            {
                                case FilesystemPatchOperation.Write:
                                {
                                    var path = change.Path!;
                                    var existed = snapshots.TryGetValue(path, out var originalHash);
                                    var encoding = existed
                                        ? ReadTextSnapshot(path).Encoding
                                        : new UTF8Encoding(false, true);
                                    var bytes = EncodeText(change.Content!, encoding, path);
                                    PublishTextChange(
                                        path, bytes, existed, originalHash, undo, cleanup, cancellationToken, _patchStageHook);
                                    break;
                                }

                                case FilesystemPatchOperation.Replace:
                                {
                                    var path = change.Path!;
                                    var snapshot = ReadTextSnapshot(path);
                                    var occurrences = CountOccurrences(snapshot.Text, change.OldText!);
                                    if (occurrences != change.ExpectedOccurrences)
                                    {
                                        throw Conflict(
                                            $"Expected {change.ExpectedOccurrences} occurrence(s) of old_text in '{path}', found {occurrences}.");
                                    }

                                    ValidateReplacementSize(
                                        snapshot, change.OldText!, change.NewText!, occurrences, path);
                                    var updated = snapshot.Text.Replace(
                                        change.OldText!, change.NewText!, StringComparison.Ordinal);
                                    var bytes = EncodeText(updated, snapshot.Encoding, path);
                                    PublishTextChange(
                                        path, bytes, true, snapshots[path], undo, cleanup, cancellationToken, _patchStageHook);
                                    break;
                                }

                                case FilesystemPatchOperation.Delete:
                                {
                                    var path = change.Path!;
                                    var backup = CreateSiblingBackupPath(path);
                                    File.Move(path, backup);
                                    undo.Push((path, () =>
                                    {
                                        if (File.Exists(backup))
                                        {
                                            File.Move(backup, path, true);
                                        }
                                    }));
                                    cleanup.Push((backup, () => DeleteFileIfExists(backup)));
                                    break;
                                }

                                case FilesystemPatchOperation.Move:
                                {
                                    var from = change.FromPath!;
                                    var to = change.ToPath!;
                                    if (File.Exists(to))
                                    {
                                        var destinationBackup = CreateSiblingBackupPath(to);
                                        File.Move(to, destinationBackup);
                                        undo.Push((to, () =>
                                        {
                                            if (File.Exists(destinationBackup))
                                            {
                                                File.Move(destinationBackup, to, true);
                                            }
                                        }));
                                        cleanup.Push((
                                            destinationBackup,
                                            () => DeleteFileIfExists(destinationBackup)));
                                    }

                                    File.Move(from, to, overwrite: false);
                                    undo.Push((from, () =>
                                    {
                                        if (File.Exists(to))
                                        {
                                            File.Move(to, from, true);
                                        }
                                    }));
                                    break;
                                }
                            }

                            _patchStageHook?.Invoke("after_change", change.Path ?? change.ToPath!);
                        }
                    }
                    catch (Exception originalFailure)
                    {
                        var failedPaths = new List<string>();
                        var restoreErrors = new List<string>();
                        while (undo.Count > 0)
                        {
                            var entry = undo.Pop();
                            try
                            {
                                entry.Restore();
                            }
                            catch (Exception rollbackFailure)
                            {
                                failedPaths.Add(entry.Path);
                                restoreErrors.Add(rollbackFailure.Message);
                            }
                        }

                        if (failedPaths.Count > 0)
                        {
                            throw new PatchValidationException(new LoomError(
                                "rollback_failed",
                                "Patch failed and at least one file could not be restored. " +
                                "Recovery backups have been retained where available.",
                                false,
                                new Dictionary<string, object?>
                                {
                                    ["originalError"] = originalFailure.Message,
                                    ["failedPaths"] = failedPaths,
                                    ["restoreErrors"] = restoreErrors
                                }));
                        }

                        throw;
                    }

                    var cleanupFailures = new List<string>();
                    while (cleanup.Count > 0)
                    {
                        var entry = cleanup.Pop();
                        try
                        {
                            entry.Delete();
                        }
                        catch (Exception)
                        {
                            cleanupFailures.Add(entry.Path);
                        }
                    }

                    if (cleanupFailures.Count > 0)
                    {
                        return LoomResult<FilesystemPatchResult>.Failure(new LoomError(
                            "cleanup_failed",
                            "Patch changes were applied, but backup cleanup was incomplete.",
                            false,
                            new Dictionary<string, object?>
                            {
                                ["appliedChanges"] = changes.Count,
                                ["remainingBackups"] = cleanupFailures
                            }));
                    }

                    return LoomResult<FilesystemPatchResult>.Success(
                        new FilesystemPatchResult(changes.Count));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (PatchValidationException ex)
                {
                    return LoomResult<FilesystemPatchResult>.Failure(ex.Error);
                }
                catch (Exception ex)
                {
                    return LoomResult<FilesystemPatchResult>.Failure(MapException(ex));
                }
            }
        }, cancellationToken);

    public Task<LoomResult<FilesystemDirectoryResult>> CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(path);
                return LoomResult<FilesystemDirectoryResult>.Success(
                    new FilesystemDirectoryResult(path, true));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return LoomResult<FilesystemDirectoryResult>.Failure(MapException(ex));
            }
        }, cancellationToken);

    public Task<LoomResult<FilesystemDirectoryResult>> DeleteDirectoryAsync(
        string path,
        CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Directory.Exists(path))
                {
                    return LoomResult<FilesystemDirectoryResult>.Failure(
                        LoomErrors.NotFound($"Directory '{path}' was not found."));
                }

                Directory.Delete(path, recursive: false);
                return LoomResult<FilesystemDirectoryResult>.Success(
                    new FilesystemDirectoryResult(path, false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return LoomResult<FilesystemDirectoryResult>.Failure(MapException(ex));
            }
        }, cancellationToken);

    private static Dictionary<string, byte[]> ValidatePatch(
        IReadOnlyList<FilesystemPatchChange> changes,
        CancellationToken cancellationToken)
    {
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshots = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (change.Operation)
            {
                case FilesystemPatchOperation.Write:
                {
                    var path = change.Path!;
                    EnsureUnique(touched, path);
                    EnsureParentExists(path);

                    if (Directory.Exists(path))
                    {
                        throw Conflict($"Path '{path}' is a directory, not a file.");
                    }

                    var existed = File.Exists(path);
                    if (existed && !change.Overwrite)
                    {
                        throw Conflict($"File '{path}' already exists. Set overwrite=true to replace it.");
                    }

                    var snapshot = existed ? ReadTextSnapshot(path) : null;
                    var encoding = snapshot?.Encoding ?? new UTF8Encoding(false, true);
                    _ = EncodeText(change.Content!, encoding, path);
                    if (snapshot is not null)
                    {
                        snapshots.Add(path, snapshot.Hash);
                    }

                    break;
                }

                case FilesystemPatchOperation.Replace:
                {
                    var path = change.Path!;
                    EnsureUnique(touched, path);
                    var snapshot = ReadTextSnapshot(path);
                    var occurrences = CountOccurrences(snapshot.Text, change.OldText!);
                    if (occurrences != change.ExpectedOccurrences)
                    {
                        throw Conflict(
                            $"Expected {change.ExpectedOccurrences} occurrence(s) of old_text in '{path}', found {occurrences}.");
                    }

                    ValidateReplacementSize(
                        snapshot, change.OldText!, change.NewText!, occurrences, path);
                    snapshots.Add(path, snapshot.Hash);
                    break;
                }

                case FilesystemPatchOperation.Delete:
                {
                    var path = change.Path!;
                    EnsureUnique(touched, path);
                    EnsureFileExists(path);
                    break;
                }

                case FilesystemPatchOperation.Move:
                {
                    var from = change.FromPath!;
                    var to = change.ToPath!;
                    EnsureUnique(touched, from);
                    EnsureUnique(touched, to);
                    EnsureFileExists(from);
                    EnsureParentExists(to);

                    if (File.Exists(to) && !change.Overwrite)
                    {
                        throw Conflict($"Destination file '{to}' already exists.");
                    }

                    if (Directory.Exists(to))
                    {
                        throw Conflict($"Destination '{to}' is a directory.");
                    }

                    break;
                }
            }
        }

        return snapshots;
    }

    private static IEnumerable<(
        long Ordinal,
        string FullPath,
        string RelativePath)> EnumerateSearchFiles(
        string root,
        FilesystemTraversalOptions traversal,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        if (File.Exists(root))
        {
            yield return (0, root, Path.GetFileName(root));
            yield break;
        }

        var ordinal = 0L;
        foreach (var item in Enumerate(root, traversal, maxDepth, cancellationToken))
        {
            if (item.Type == FilesystemEntryType.File)
            {
                yield return (
                    ordinal,
                    item.FullPath,
                    NormalizeRelative(Path.GetRelativePath(root, item.FullPath)));
            }

            ordinal++;
        }
    }

    private static IEnumerable<(
        string FullPath,
        int Depth,
        FilesystemEntryType Type,
        string? ChildrenExcluded)> Enumerate(
        string root,
        FilesystemTraversalOptions traversal,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        var excludedDirectories = traversal.ExcludeDirectories is null
            ? null
            : new HashSet<string>(traversal.ExcludeDirectories, StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((root, 0));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, parentDepth) = pending.Dequeue();

            foreach (var path in Directory.EnumerateFileSystemEntries(directory)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(p => p, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var depth = parentDepth + 1;
                var attributes = File.GetAttributes(path);
                var isReparse = attributes.HasFlag(FileAttributes.ReparsePoint);
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                var type = isReparse
                    ? FilesystemEntryType.Symlink
                    : isDirectory
                        ? FilesystemEntryType.Directory
                        : FilesystemEntryType.File;
                var childrenExcluded = isDirectory && !isReparse
                    ? GetChildrenExcludedReason(path, traversal, excludedDirectories)
                    : null;

                yield return (path, depth, type, childrenExcluded);

                if (isDirectory && !isReparse && childrenExcluded is null && depth < maxDepth)
                {
                    pending.Enqueue((path, depth));
                }
            }
        }
    }

    private static string? GetChildrenExcludedReason(
        string directoryPath,
        FilesystemTraversalOptions traversal,
        HashSet<string>? excludedDirectories)
    {
        var directoryName = Path.GetFileName(directoryPath);
        if (!traversal.IncludeGenerated && IsDefaultGeneratedDirectory(directoryPath, directoryName))
        {
            return "generated";
        }

        return excludedDirectories?.Contains(directoryName) == true ? "excluded" : null;
    }

    private static bool IsDefaultGeneratedDirectory(string directoryPath, string directoryName)
        => DefaultGeneratedDirectories.Contains(directoryName, StringComparer.OrdinalIgnoreCase) ||
           (string.Equals(directoryName, "plugins", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                Path.GetFileName(Path.GetDirectoryName(directoryPath)),
                ".obsidian",
                StringComparison.OrdinalIgnoreCase));

    private static FilesystemEntry ToEntry(
        string root,
        string fullPath,
        int depth,
        FilesystemEntryType type,
        string? childrenExcluded)
    {
        long? size = type == FilesystemEntryType.File ? new FileInfo(fullPath).Length : null;
        return new FilesystemEntry(
            NormalizeRelative(Path.GetRelativePath(root, fullPath)),
            Path.GetFileName(fullPath),
            type,
            size,
            depth,
            childrenExcluded);
    }

    private static string NormalizeRelative(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/');

    private static List<TextLine> ParseTextLines(string text)
    {
        var lines = new List<TextLine>();
        if (text.Length == 0)
        {
            return lines;
        }

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n'))
            {
                continue;
            }

            var separatorLength = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n'
                ? 2
                : 1;

            lines.Add(new TextLine(
                text[start..i],
                text.Substring(i, separatorLength)));

            i += separatorLength - 1;
            start = i + 1;
        }

        if (start < text.Length)
        {
            lines.Add(new TextLine(text[start..], string.Empty));
        }

        return lines;
    }

    private static string BuildTextSlice(
        IReadOnlyList<TextLine> lines,
        int startIndex,
        int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var offset = 0; offset < count; offset++)
        {
            var index = startIndex + offset;
            var line = lines[index];
            builder.Append(line.Content);

            var isLastSelected = offset == count - 1;
            var isLastFileLine = index == lines.Count - 1;
            if (!isLastSelected || isLastFileLine)
            {
                builder.Append(line.Separator);
            }
        }

        return builder.ToString();
    }

    private static async Task<LoomResult<StreamingTextRange>> ReadTextRangeAsync(
        string path,
        int? offset,
        int? limit,
        CancellationToken cancellationToken)
    {
        var requestedStart = offset ?? 1;
        var selected = new List<TextLine>(Math.Min(limit ?? 128, 10_000));
        var current = new StringBuilder();
        var buffer = new char[8192];
        var pendingCarriageReturn = false;
        var totalLines = 0;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 8192,
            leaveOpen: false);

        void CompleteLine(string separator)
        {
            totalLines++;
            if (totalLines >= requestedStart &&
                (limit is null || selected.Count < limit.Value))
            {
                selected.Add(new TextLine(current.ToString(), separator));
            }

            current.Clear();
        }

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];
                if (ch == '\0')
                {
                    return LoomResult<StreamingTextRange>.Failure(
                        LoomErrors.Unsupported($"File '{path}' does not appear to be a text file."));
                }

                if (pendingCarriageReturn)
                {
                    if (ch == '\n')
                    {
                        CompleteLine("\r\n");
                        pendingCarriageReturn = false;
                        continue;
                    }

                    CompleteLine("\r");
                    pendingCarriageReturn = false;
                }

                if (ch == '\r')
                {
                    pendingCarriageReturn = true;
                }
                else if (ch == '\n')
                {
                    CompleteLine("\n");
                }
                else
                {
                    current.Append(ch);
                }
            }
        }

        if (pendingCarriageReturn)
        {
            CompleteLine("\r");
        }
        else if (current.Length > 0)
        {
            CompleteLine(string.Empty);
        }

        var start = Math.Min(requestedStart, totalLines + 1);
        var end = selected.Count == 0 ? start - 1 : start + selected.Count - 1;
        var text = BuildStreamingTextSlice(selected, start, totalLines);

        return LoomResult<StreamingTextRange>.Success(new StreamingTextRange(
            start,
            end,
            totalLines,
            start > 1,
            end < totalLines,
            text));
    }

    private static string BuildStreamingTextSlice(
        IReadOnlyList<TextLine> selected,
        int startLine,
        int totalLines)
    {
        if (selected.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var index = 0; index < selected.Count; index++)
        {
            var line = selected[index];
            builder.Append(line.Content);

            var isLastSelected = index == selected.Count - 1;
            var isLastFileLine = startLine + index == totalLines;
            if (!isLastSelected || isLastFileLine)
            {
                builder.Append(line.Separator);
            }
        }

        return builder.ToString();
    }

    private static TextFileSnapshot ReadTextSnapshot(string path)
    {
        EnsureFileExists(path);
        if (new FileInfo(path).Length > MaxTextFileBytes)
        {
            throw new PatchValidationException(
                LoomErrors.Unsupported($"File '{path}' exceeds the 16 MiB text patch limit."));
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength > MaxTextFileBytes)
        {
            throw new PatchValidationException(
                LoomErrors.Unsupported($"File '{path}' exceeds the 16 MiB text patch limit."));
        }

        var encoding = GetStrictTextEncoding(bytes);
        var preambleSize = encoding.GetPreamble().Length;
        string text;
        try
        {
            text = encoding.GetString(bytes, preambleSize, bytes.Length - preambleSize);
        }
        catch (DecoderFallbackException)
        {
            throw new PatchValidationException(
                LoomErrors.Unsupported($"File '{path}' has invalid text encoding."));
        }

        if (text.IndexOf('\0') >= 0)
        {
            throw new PatchValidationException(
                LoomErrors.Unsupported($"File '{path}' does not appear to be a text file."));
        }

        return new TextFileSnapshot(text, encoding, SHA256.HashData(bytes));
    }

    private static Encoding GetStrictTextEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        {
            return new UTF32Encoding(true, true, true);
        }

        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
        {
            return new UTF32Encoding(false, true, true);
        }

        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return new UTF8Encoding(true, true);
        }

        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return new UnicodeEncoding(true, true, true);
        }

        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return new UnicodeEncoding(false, true, true);
        }

        return new UTF8Encoding(false, true);
    }

    private static byte[] EncodeText(string text, Encoding encoding, string path)
    {
        if (text.IndexOf('\0') >= 0)
        {
            throw new PatchValidationException(
                LoomErrors.Unsupported($"Content for '{path}' contains a NUL character."));
        }

        try
        {
            var preamble = encoding.GetPreamble();
            var bodySize = encoding.GetByteCount(text);
            if ((long)preamble.Length + bodySize > MaxTextFileBytes)
            {
                throw new PatchValidationException(
                    LoomErrors.Unsupported($"Content for '{path}' exceeds the 16 MiB text patch limit."));
            }

            var result = new byte[preamble.Length + bodySize];
            preamble.CopyTo(result, 0);
            _ = encoding.GetBytes(text, 0, text.Length, result, preamble.Length);
            return result;
        }
        catch (EncoderFallbackException)
        {
            throw new PatchValidationException(
                LoomErrors.Unsupported($"Content for '{path}' has invalid Unicode characters."));
        }
    }

    private static void ValidateReplacementSize(
        TextFileSnapshot snapshot,
        string oldText,
        string newText,
        int occurrences,
        string path)
    {
        try
        {
            var encoding = snapshot.Encoding;
            var originalSize = (long)encoding.GetPreamble().Length + encoding.GetByteCount(snapshot.Text);
            var replacementDelta = (long)encoding.GetByteCount(newText) - encoding.GetByteCount(oldText);
            var predictedSize = originalSize + replacementDelta * occurrences;
            if (predictedSize > MaxTextFileBytes)
            {
                throw new PatchValidationException(
                    LoomErrors.Unsupported($"Content for '{path}' exceeds the 16 MiB text patch limit."));
            }
        }
        catch (EncoderFallbackException)
        {
            throw new PatchValidationException(
                LoomErrors.Unsupported($"Replacement for '{path}' contains invalid Unicode characters."));
        }
    }

    private static void VerifyUnchanged(string path, byte[] expectedHash)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxTextFileBytes)
        {
            throw Conflict($"File '{path}' grew beyond the 16 MiB text patch limit.");
        }

        var actualHash = SHA256.HashData(stream);
        if (!actualHash.AsSpan().SequenceEqual(expectedHash))
        {
            throw Conflict($"File '{path}' changed while the patch was being prepared.");
        }
    }

    private static void RestoreTextBackup(string backup, string path)
    {
        if (!File.Exists(backup))
        {
            return;
        }

        if (File.Exists(path))
        {
            File.Replace(backup, path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(backup, path);
        }
    }

    private static void PublishTextChange(
        string path,
        byte[] content,
        bool existed,
        byte[]? originalHash,
        Stack<(string Path, Action Restore)> undo,
        Stack<(string Path, Action Delete)> cleanup,
        CancellationToken cancellationToken,
        Action<string, string>? patchStageHook)
    {
        var staging = CreateSiblingTemporaryPath(path);
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            patchStageHook?.Invoke("before_publish", path);
            cancellationToken.ThrowIfCancellationRequested();

            if (!existed)
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    throw Conflict($"File '{path}' was created by another operation.");
                }

                File.Move(staging, path);
                undo.Push((path, () => DeleteFileIfExists(path)));
                return;
            }

            var expectedHash = originalHash
                ?? throw new InvalidOperationException("Missing original text fingerprint.");
            var backup = CreateSiblingBackupPath(path);
            try
            {
                VerifyUnchanged(path, expectedHash);
                File.Copy(path, backup);
                VerifyUnchanged(backup, expectedHash);
                VerifyUnchanged(path, expectedHash);
            }
            catch
            {
                DeleteFileIfExists(backup);
                throw;
            }

            // The recovery action is registered before publishing, including
            // the case where File.Replace reports a post-mutation failure.
            undo.Push((path, () => RestoreTextBackup(backup, path)));
            cleanup.Push((backup, () => DeleteFileIfExists(backup)));
            cancellationToken.ThrowIfCancellationRequested();
            File.Replace(staging, path, null, ignoreMetadataErrors: true);
        }
        finally
        {
            DeleteFileIfExists(staging);
        }
    }

    private static string CreateSiblingTemporaryPath(string path)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new IOException($"Could not determine a parent directory for '{path}'.");
        while (true)
        {
            var candidate = Path.Combine(
                directory,
                $".{Path.GetFileName(path)}.loomlci-{Guid.NewGuid():N}.tmp");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void EnsureUnique(HashSet<string> touched, string path)
    {
        var canonical = Path.GetFullPath(path);
        if (!touched.Add(canonical))
        {
            throw Conflict($"Patch touches '{canonical}' more than once. Split dependent edits into separate calls.");
        }
    }

    private static void EnsureParentExists(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            throw new PatchValidationException(
                LoomErrors.NotFound($"Parent directory for '{path}' was not found."));
        }
    }

    private static void EnsureFileExists(string path)
    {
        if (Directory.Exists(path))
        {
            throw Conflict($"Path '{path}' is a directory, not a file.");
        }

        if (!File.Exists(path))
        {
            throw new PatchValidationException(LoomErrors.NotFound($"File '{path}' was not found."));
        }
    }

    private static string CreateSiblingBackupPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException($"Could not determine a parent directory for '{path}'.");
        }

        var name = Path.GetFileName(path);
        while (true)
        {
            var candidate = Path.Combine(
                directory,
                $".{name}.loomlci-{Guid.NewGuid():N}.bak");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static PatchValidationException Conflict(string message)
        => new(LoomErrors.Conflict(message));

    private static LoomError MapException(Exception ex)
        => ex switch
        {
            UnauthorizedAccessException => LoomErrors.AccessDenied(ex.Message),
            FileNotFoundException or DirectoryNotFoundException => LoomErrors.NotFound(ex.Message),
            IOException => LoomErrors.ExecutionFailed(ex.Message),
            ArgumentException or NotSupportedException or PathTooLongException
                => LoomErrors.InvalidArgument(ex.Message),
            _ => LoomErrors.Internal(ex.Message)
        };

    private sealed record TextFileSnapshot(string Text, Encoding Encoding, byte[] Hash);

    private sealed record TextLine(string Content, string Separator);

    private sealed record StreamingTextRange(
        int StartLine,
        int EndLine,
        int TotalLines,
        bool HasMoreBefore,
        bool HasMoreAfter,
        string Text);

    private sealed class PatchValidationException(LoomError error) : Exception(error.Message)
    {
        public LoomError Error { get; } = error;
    }
}
