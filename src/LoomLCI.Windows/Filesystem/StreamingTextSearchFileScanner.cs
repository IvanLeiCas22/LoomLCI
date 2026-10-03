using LoomLCI.Core.Filesystem;

namespace LoomLCI.Windows.Filesystem;

internal sealed record SearchResumePoint(
    int NextLine,
    long NextByteOffset,
    int ContextStartLine,
    long ContextStartByteOffset);

internal sealed record SearchFileScanResult(
    IReadOnlyList<FilesystemTextMatch> Matches,
    long BytesRead,
    bool ResultLimitReached,
    bool ScanLimitReached,
    bool EndOfFile,
    SearchResumePoint? ResumePoint);

internal static class StreamingTextSearchFileScanner
{
    public static async Task<SearchFileScanResult> ScanAsync(
        string fullPath,
        string relativePath,
        TextFileFormat format,
        IReadOnlyList<string> queries,
        bool caseSensitive,
        int contextLines,
        int startLine,
        long startByteOffset,
        int emitMatchesFromLine,
        int maxResults,
        long scanBudgetBytes,
        CancellationToken cancellationToken)
    {
        var matches = new List<FilesystemTextMatch>(Math.Min(maxResults, 128));
        var history = new Queue<ScannedTextLine>(contextLines);
        var pending = new List<PendingMatch>();
        var bytesRead = 0L;
        var acceptedMatches = 0;
        var resultLimitReached = false;
        var scanLimitReached = false;
        var stopRequested = false;
        SearchResumePoint? resumePoint = null;
        var endOfFile = false;

        await using var reader = await StreamingTextSearchReader.OpenAsync(
            fullPath,
            format,
            queries,
            caseSensitive,
            startLine,
            startByteOffset,
            cancellationToken).ConfigureAwait(false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                endOfFile = true;
                FinalizePending(relativePath, pending, matches);
                break;
            }

            bytesRead += line.NextByteOffset - line.StartByteOffset;

            AddAfterContext(relativePath, line, contextLines, pending, matches);

            var mayEmit = !stopRequested &&
                line.LineNumber >= emitMatchesFromLine &&
                line.QueryMatches.Count > 0;

            var resultLimitJustReached = false;
            if (mayEmit)
            {
                var before = history
                    .Select(item => ToPublicExcerpt(item.Excerpt))
                    .ToArray();
                pending.Add(new PendingMatch(line, before));
                acceptedMatches++;

                if (contextLines == 0)
                {
                    FinalizeReady(relativePath, pending, matches);
                }

                if (acceptedMatches >= maxResults)
                {
                    resultLimitReached = true;
                    stopRequested = true;
                    resultLimitJustReached = true;
                }
            }

            AddHistory(history, line, contextLines);

            if (resultLimitJustReached)
            {
                resumePoint = CreateResumeAfter(line, history, contextLines);
            }

            if (bytesRead >= scanBudgetBytes)
            {
                scanLimitReached = true;
                if (!stopRequested)
                {
                    stopRequested = true;
                    resumePoint = CreateResumeAfter(line, history, contextLines);
                }
            }

            if (stopRequested && pending.Count == 0)
            {
                break;
            }
        }

        return new SearchFileScanResult(
            matches,
            bytesRead,
            resultLimitReached,
            scanLimitReached,
            endOfFile,
            resumePoint);
    }

    private static void AddAfterContext(
        string relativePath,
        ScannedTextLine line,
        int contextLines,
        List<PendingMatch> pending,
        List<FilesystemTextMatch> completed)
    {
        if (pending.Count == 0)
        {
            return;
        }

        foreach (var item in pending)
        {
            if (item.Line.LineNumber < line.LineNumber &&
                item.After.Count < contextLines)
            {
                item.After.Add(ToPublicExcerpt(line.Excerpt));
            }
        }

        FinalizeReady(relativePath, pending, completed, contextLines);
    }

    private static void FinalizeReady(
        string relativePath,
        List<PendingMatch> pending,
        List<FilesystemTextMatch> completed,
        int requiredAfter = 0)
    {
        for (var index = pending.Count - 1; index >= 0; index--)
        {
            var item = pending[index];
            if (item.After.Count < requiredAfter)
            {
                continue;
            }

            completed.Add(ToMatch(relativePath, item));
            pending.RemoveAt(index);
        }

        completed.Sort((left, right) => left.Line.CompareTo(right.Line));
    }

    private static void FinalizePending(
        string relativePath,
        List<PendingMatch> pending,
        List<FilesystemTextMatch> completed)
    {
        foreach (var item in pending)
        {
            completed.Add(ToMatch(relativePath, item));
        }

        pending.Clear();
        completed.Sort((left, right) => left.Line.CompareTo(right.Line));
    }

    private static FilesystemTextMatch ToMatch(
        string relativePath,
        PendingMatch pending)
        => new(
            relativePath,
            pending.Line.LineNumber,
            pending.Line.Excerpt.Text,
            pending.Line.Excerpt.StartColumn,
            pending.Line.Excerpt.Truncated,
            pending.Line.QueryMatches,
            pending.Before,
            pending.After);

    private static FilesystemTextLineExcerpt ToPublicExcerpt(SearchTextExcerpt excerpt)
        => new(excerpt.Text, excerpt.StartColumn, excerpt.Truncated);

    private static void AddHistory(
        Queue<ScannedTextLine> history,
        ScannedTextLine line,
        int contextLines)
    {
        if (contextLines == 0)
        {
            return;
        }

        history.Enqueue(line);
        while (history.Count > contextLines)
        {
            history.Dequeue();
        }
    }

    private static SearchResumePoint CreateResumeAfter(
        ScannedTextLine line,
        Queue<ScannedTextLine> history,
        int contextLines)
    {
        var nextLine = line.LineNumber + 1;
        var nextOffset = line.NextByteOffset;
        if (contextLines == 0 || history.Count == 0)
        {
            return new SearchResumePoint(
                nextLine,
                nextOffset,
                nextLine,
                nextOffset);
        }

        var first = history.Peek();
        return new SearchResumePoint(
            nextLine,
            nextOffset,
            first.LineNumber,
            first.StartByteOffset);
    }

    private sealed class PendingMatch(
        ScannedTextLine line,
        IReadOnlyList<FilesystemTextLineExcerpt> before)
    {
        public ScannedTextLine Line { get; } = line;
        public IReadOnlyList<FilesystemTextLineExcerpt> Before { get; } = before;
        public List<FilesystemTextLineExcerpt> After { get; } = [];
    }
}
