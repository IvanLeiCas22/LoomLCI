using System.Text;
using LoomLCI.Core;
using LoomLCI.Core.Filesystem;

namespace LoomLCI.Windows.Filesystem;

public sealed class WindowsFilesystemProvider : IFilesystemProvider
{
    private const long MaxTextFileBytes = 16L * 1024 * 1024;
    private const long MaxSearchTotalBytes = 64L * 1024 * 1024;
    private const long MaxReadTotalBytes = 64L * 1024 * 1024;
    private const int MaxSkippedLargeFileSamples = 20;

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

                var entries = new List<FilesystemEntry>(Math.Min(maxEntries, 1024));
                var truncated = false;

                foreach (var item in Enumerate(root, traversal, maxDepth, cancellationToken))
                {
                    if (entries.Count >= maxEntries)
                    {
                        truncated = true;
                        break;
                    }

                    entries.Add(ToEntry(
                        root,
                        item.FullPath,
                        item.Depth,
                        item.Type,
                        item.ChildrenExcluded));
                }

                return LoomResult<FilesystemListTreeResult>.Success(
                    new FilesystemListTreeResult(root, maxDepth, maxEntries, entries, truncated));
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

                var comparison = StringComparison.OrdinalIgnoreCase;
                var matches = new List<FilesystemEntry>(Math.Min(maxResults, 256));
                var truncated = false;

                foreach (var item in Enumerate(root, traversal, maxDepth, cancellationToken))
                {
                    if (type is not null && item.Type != type)
                    {
                        continue;
                    }

                    var relative = NormalizeRelative(Path.GetRelativePath(root, item.FullPath));
                    var matched = queries.Any(query => matchMode switch
                    {
                        FilesystemPathMatchMode.Substring => relative.Contains(query, comparison),
                        FilesystemPathMatchMode.Suffix => relative.EndsWith(query, comparison),
                        _ => false
                    });

                    if (!matched)
                    {
                        continue;
                    }

                    if (matches.Count >= maxResults)
                    {
                        truncated = true;
                        break;
                    }

                    matches.Add(ToEntry(
                        root,
                        item.FullPath,
                        item.Depth,
                        item.Type,
                        item.ChildrenExcluded));
                }

                return LoomResult<FilesystemFindPathsResult>.Success(
                    new FilesystemFindPathsResult(root, queries, matchMode, matches, truncated));
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
        CancellationToken cancellationToken)
    {
        try
        {
            IEnumerable<(string FullPath, string RelativePath)> files;
            if (File.Exists(root))
            {
                files = [(root, Path.GetFileName(root))];
            }
            else if (Directory.Exists(root))
            {
                files = Enumerate(root, traversal, maxDepth, cancellationToken)
                    .Where(item => item.Type == FilesystemEntryType.File)
                    .Select(item => (
                        item.FullPath,
                        NormalizeRelative(Path.GetRelativePath(root, item.FullPath))));
            }
            else
            {
                return LoomResult<FilesystemSearchTextResult>.Failure(
                    LoomErrors.NotFound($"Path '{root}' was not found."));
            }

            var matches = new List<FilesystemTextMatch>(Math.Min(maxResults, 128));
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var filesRead = 0;
            long bytesRead = 0;
            var resultLimitReached = false;
            var scanLimitReached = false;
            var skippedLargeFileCount = 0;
            var skippedLargeFiles = new List<string>();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var info = new FileInfo(file.FullPath);
                if (info.Length > MaxTextFileBytes)
                {
                    skippedLargeFileCount++;
                    if (skippedLargeFiles.Count < MaxSkippedLargeFileSamples)
                    {
                        skippedLargeFiles.Add(file.RelativePath);
                    }
                    continue;
                }

                if (bytesRead + info.Length > MaxSearchTotalBytes)
                {
                    scanLimitReached = true;
                    break;
                }

                var text = await File.ReadAllTextAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
                bytesRead += info.Length;
                filesRead++;

                if (text.IndexOf('\0') >= 0)
                {
                    continue;
                }

                var lines = ParseTextLines(text);
                for (var i = 0; i < lines.Count; i++)
                {
                    var lineText = lines[i].Content;
                    List<FilesystemTextQueryMatch>? queryMatches = null;
                    foreach (var query in queries)
                    {
                        var column = lineText.IndexOf(query, comparison);
                        if (column >= 0)
                        {
                            queryMatches ??= new List<FilesystemTextQueryMatch>(queries.Count);
                            queryMatches.Add(new FilesystemTextQueryMatch(query, column + 1));
                        }
                    }

                    if (queryMatches is null)
                    {
                        continue;
                    }

                    if (matches.Count >= maxResults)
                    {
                        resultLimitReached = true;
                        break;
                    }

                    var beforeStart = Math.Max(0, i - contextLines);
                    var afterEnd = Math.Min(lines.Count - 1, i + contextLines);
                    var before = Enumerable.Range(beforeStart, i - beforeStart)
                        .Select(index => lines[index].Content)
                        .ToArray();
                    var after = i + 1 <= afterEnd
                        ? Enumerable.Range(i + 1, afterEnd - i)
                            .Select(index => lines[index].Content)
                            .ToArray()
                        : Array.Empty<string>();

                    matches.Add(new FilesystemTextMatch(
                        file.RelativePath,
                        i + 1,
                        lineText,
                        queryMatches,
                        before,
                        after));
                }

                if (resultLimitReached || scanLimitReached)
                {
                    break;
                }
            }

            var truncated = resultLimitReached || scanLimitReached || skippedLargeFileCount > 0;
            return LoomResult<FilesystemSearchTextResult>.Success(
                new FilesystemSearchTextResult(
                    root,
                    queries,
                    matches,
                    filesRead,
                    bytesRead,
                    truncated,
                    resultLimitReached,
                    scanLimitReached,
                    skippedLargeFileCount,
                    skippedLargeFiles));
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
            try
            {
                ValidatePatch(changes, cancellationToken);

                var undo = new Stack<Action>();
                var cleanup = new Stack<Action>();
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
                                var existed = File.Exists(path);
                                var encoding = existed ? GetExistingTextEncoding(path) : new UTF8Encoding(false);
                                string? backup = null;

                                if (existed)
                                {
                                    backup = CreateSiblingBackupPath(path);
                                    File.Move(path, backup);
                                    var capturedBackup = backup;
                                    undo.Push(() =>
                                    {
                                        if (File.Exists(path))
                                        {
                                            File.Delete(path);
                                        }
                                        if (File.Exists(capturedBackup))
                                        {
                                            File.Move(capturedBackup, path, true);
                                        }
                                    });
                                    cleanup.Push(() => DeleteFileIfExists(capturedBackup));
                                }
                                else
                                {
                                    undo.Push(() => DeleteFileIfExists(path));
                                }

                                File.WriteAllText(path, change.Content!, encoding);
                                break;
                            }

                            case FilesystemPatchOperation.Replace:
                            {
                                var path = change.Path!;
                                var originalBytes = File.ReadAllBytes(path);
                                var original = File.ReadAllText(path);
                                var updated = original.Replace(change.OldText!, change.NewText!, StringComparison.Ordinal);
                                File.WriteAllText(path, updated, GetExistingTextEncoding(path));
                                undo.Push(() => File.WriteAllBytes(path, originalBytes));
                                break;
                            }

                            case FilesystemPatchOperation.Delete:
                            {
                                var path = change.Path!;
                                var backup = CreateSiblingBackupPath(path);
                                File.Move(path, backup);
                                undo.Push(() =>
                                {
                                    if (File.Exists(backup))
                                    {
                                        File.Move(backup, path, true);
                                    }
                                });
                                cleanup.Push(() => DeleteFileIfExists(backup));
                                break;
                            }

                            case FilesystemPatchOperation.Move:
                            {
                                var from = change.FromPath!;
                                var to = change.ToPath!;
                                string? destinationBackup = null;

                                if (File.Exists(to))
                                {
                                    destinationBackup = CreateSiblingBackupPath(to);
                                    File.Move(to, destinationBackup);
                                    var capturedBackup = destinationBackup;
                                    undo.Push(() =>
                                    {
                                        if (File.Exists(capturedBackup))
                                        {
                                            File.Move(capturedBackup, to, true);
                                        }
                                    });
                                    cleanup.Push(() => DeleteFileIfExists(capturedBackup));
                                }

                                File.Move(from, to, overwrite: false);
                                undo.Push(() =>
                                {
                                    if (File.Exists(to))
                                    {
                                        File.Move(to, from, true);
                                    }
                                });
                                break;
                            }
                        }
                    }
                }
                catch
                {
                    while (undo.Count > 0)
                    {
                        try
                        {
                            undo.Pop().Invoke();
                        }
                        catch
                        {
                            // Preserve the original failure. Rollback is best-effort.
                        }
                    }

                    throw;
                }

                while (cleanup.Count > 0)
                {
                    try
                    {
                        cleanup.Pop().Invoke();
                    }
                    catch
                    {
                        // The requested changes are already committed. Cleanup is best-effort.
                    }
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

    private static void ValidatePatch(
        IReadOnlyList<FilesystemPatchChange> changes,
        CancellationToken cancellationToken)
    {
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                    if (File.Exists(path) && !change.Overwrite)
                    {
                        throw Conflict($"File '{path}' already exists. Set overwrite=true to replace it.");
                    }

                    EnsureContentSize(change.Content!, path);
                    break;
                }

                case FilesystemPatchOperation.Replace:
                {
                    var path = change.Path!;
                    EnsureUnique(touched, path);
                    EnsureTextFile(path);

                    var text = File.ReadAllText(path);
                    var occurrences = CountOccurrences(text, change.OldText!);
                    if (occurrences != change.ExpectedOccurrences)
                    {
                        throw Conflict(
                            $"Expected {change.ExpectedOccurrences} occurrence(s) of old_text in '{path}', found {occurrences}.");
                    }

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
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
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

    private static Encoding GetExistingTextEncoding(string path)
    {
        Span<byte> prefix = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        var read = stream.Read(prefix);

        if (read >= 4 &&
            prefix[0] == 0x00 &&
            prefix[1] == 0x00 &&
            prefix[2] == 0xFE &&
            prefix[3] == 0xFF)
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        }

        if (read >= 4 &&
            prefix[0] == 0xFF &&
            prefix[1] == 0xFE &&
            prefix[2] == 0x00 &&
            prefix[3] == 0x00)
        {
            return new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        }

        if (read >= 3 &&
            prefix[0] == 0xEF &&
            prefix[1] == 0xBB &&
            prefix[2] == 0xBF)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        if (read >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF)
        {
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        }

        if (read >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE)
        {
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
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

    private static void EnsureTextFile(string path)
    {
        EnsureFileExists(path);

        var info = new FileInfo(path);
        if (info.Length > MaxTextFileBytes)
        {
            throw new PatchValidationException(
                LoomErrors.Unsupported($"File '{path}' exceeds the 16 MiB text patch limit."));
        }

        var text = File.ReadAllText(path);
        if (text.IndexOf('\0') >= 0)
        {
            throw new PatchValidationException(
                LoomErrors.Unsupported($"File '{path}' does not appear to be a text file."));
        }
    }

    private static void EnsureContentSize(string content, string path)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxTextFileBytes)
        {
            throw new PatchValidationException(
                LoomErrors.Unsupported($"Content for '{path}' exceeds the 16 MiB text patch limit."));
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
