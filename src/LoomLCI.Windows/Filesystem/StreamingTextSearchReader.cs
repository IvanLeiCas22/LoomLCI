using System.Text;
using LoomLCI.Core.Filesystem;

namespace LoomLCI.Windows.Filesystem;

internal sealed record SearchTextExcerpt(
    string Text,
    int StartColumn,
    bool Truncated);

internal sealed record ScannedTextLine(
    int LineNumber,
    long StartByteOffset,
    long NextByteOffset,
    SearchTextExcerpt Excerpt,
    IReadOnlyList<FilesystemTextQueryMatch> QueryMatches);

internal sealed record TextFileFormat(
    Encoding Encoding,
    string EncodingId,
    int PreambleLength,
    bool IsBinary);

internal sealed class StreamingTextSearchReader : IAsyncDisposable
{
    internal const int MaxExcerptChars = 500;
    internal const int BinaryPrefixBytes = 64 * 1024;

    private readonly FileStream _stream;
    private readonly StreamReader _reader;
    private readonly char[] _buffer = new char[8192];
    private readonly IReadOnlyList<string> _queries;
    private readonly StringComparison _comparison;
    private readonly Encoding _encoding;
    private int _bufferOffset;
    private int _bufferCount;
    private bool _endOfStream;
    private bool _pendingCarriageReturn;
    private int _lineNumber;
    private long _lineStartByteOffset;
    private LineAccumulator _line;

    private StreamingTextSearchReader(
        FileStream stream,
        StreamReader reader,
        Encoding encoding,
        IReadOnlyList<string> queries,
        bool caseSensitive,
        int startLine,
        long startByteOffset)
    {
        _stream = stream;
        _reader = reader;
        _encoding = encoding;
        _queries = queries;
        _comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        _lineNumber = startLine;
        _lineStartByteOffset = startByteOffset;
        _line = CreateAccumulator();
    }

    public static async Task<TextFileFormat> InspectAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var probe = new byte[Math.Min(
            checked(BinaryPrefixBytes + 4),
            (int)Math.Min(int.MaxValue, stream.Length))];
        var read = 0;
        while (read < probe.Length)
        {
            var chunk = await stream.ReadAsync(
                probe.AsMemory(read, probe.Length - read),
                cancellationToken).ConfigureAwait(false);
            if (chunk == 0)
            {
                break;
            }
            read += chunk;
        }

        var format = DetectEncoding(probe.AsSpan(0, read));
        var prefixLength = Math.Max(0, read - format.PreambleLength);
        var decodedPrefix = prefixLength == 0
            ? string.Empty
            : format.Encoding.GetString(
                probe,
                format.PreambleLength,
                Math.Min(prefixLength, BinaryPrefixBytes));

        return format with { IsBinary = decodedPrefix.IndexOf('\0') >= 0 };
    }

    public static async Task<StreamingTextSearchReader> OpenAsync(
        string path,
        TextFileFormat format,
        IReadOnlyList<string> queries,
        bool caseSensitive,
        int startLine,
        long startByteOffset,
        CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        try
        {
            if (startByteOffset < format.PreambleLength || startByteOffset > stream.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(startByteOffset),
                    "Search resume offset is outside the file.");
            }

            stream.Seek(startByteOffset, SeekOrigin.Begin);
            var reader = new StreamReader(
                stream,
                format.Encoding,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 8192,
                leaveOpen: true);

            await Task.CompletedTask.ConfigureAwait(false);
            return new StreamingTextSearchReader(
                stream,
                reader,
                format.Encoding,
                queries,
                caseSensitive,
                startLine,
                startByteOffset);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ScannedTextLine?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_pendingCarriageReturn)
            {
                if (!await EnsureBufferAsync(cancellationToken).ConfigureAwait(false))
                {
                    _pendingCarriageReturn = false;
                    return CompleteLine("\r");
                }

                if (_buffer[_bufferOffset] == '\n')
                {
                    _bufferOffset++;
                    _pendingCarriageReturn = false;
                    return CompleteLine("\r\n");
                }

                _pendingCarriageReturn = false;
                return CompleteLine("\r");
            }

            if (!await EnsureBufferAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_line.HasContent)
                {
                    return CompleteLine(string.Empty);
                }

                return null;
            }

            var span = _buffer.AsSpan(_bufferOffset, _bufferCount - _bufferOffset);
            var separatorIndex = span.IndexOfAny('\r', '\n');
            if (separatorIndex < 0)
            {
                _line.Feed(span);
                _bufferOffset = _bufferCount;
                continue;
            }

            if (separatorIndex > 0)
            {
                _line.Feed(span[..separatorIndex]);
                _bufferOffset += separatorIndex;
            }

            var separator = _buffer[_bufferOffset++];
            if (separator == '\n')
            {
                return CompleteLine("\n");
            }

            _pendingCarriageReturn = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<bool> EnsureBufferAsync(CancellationToken cancellationToken)
    {
        if (_bufferOffset < _bufferCount)
        {
            return true;
        }

        if (_endOfStream)
        {
            return false;
        }

        _bufferCount = await _reader.ReadAsync(
            _buffer.AsMemory(),
            cancellationToken).ConfigureAwait(false);
        _bufferOffset = 0;
        _endOfStream = _bufferCount == 0;
        return !_endOfStream;
    }

    private ScannedTextLine CompleteLine(string separator)
    {
        var byteCount = _line.CompleteByteCount(separator);
        var result = new ScannedTextLine(
            _lineNumber,
            _lineStartByteOffset,
            checked(_lineStartByteOffset + byteCount),
            _line.CreateExcerpt(),
            _line.CreateQueryMatches());

        _lineNumber++;
        _lineStartByteOffset = result.NextByteOffset;
        _line = CreateAccumulator();
        return result;
    }

    private LineAccumulator CreateAccumulator()
        => new(_queries, _comparison, _encoding);

    private static TextFileFormat DetectEncoding(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length >= 4 &&
            prefix[0] == 0x00 &&
            prefix[1] == 0x00 &&
            prefix[2] == 0xFE &&
            prefix[3] == 0xFF)
        {
            return new TextFileFormat(
                new UTF32Encoding(bigEndian: true, byteOrderMark: true),
                "utf-32be",
                4,
                false);
        }

        if (prefix.Length >= 4 &&
            prefix[0] == 0xFF &&
            prefix[1] == 0xFE &&
            prefix[2] == 0x00 &&
            prefix[3] == 0x00)
        {
            return new TextFileFormat(
                new UTF32Encoding(bigEndian: false, byteOrderMark: true),
                "utf-32le",
                4,
                false);
        }

        if (prefix.Length >= 3 &&
            prefix[0] == 0xEF &&
            prefix[1] == 0xBB &&
            prefix[2] == 0xBF)
        {
            return new TextFileFormat(
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                "utf-8",
                3,
                false);
        }

        if (prefix.Length >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF)
        {
            return new TextFileFormat(
                new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
                "utf-16be",
                2,
                false);
        }

        if (prefix.Length >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE)
        {
            return new TextFileFormat(
                new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
                "utf-16le",
                2,
                false);
        }

        return new TextFileFormat(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            "utf-8",
            0,
            false);
    }

    private sealed class LineAccumulator
    {
        private const int ExcerptBeforeMatchChars = 120;

        private readonly IReadOnlyList<string> _queries;
        private readonly StringComparison _comparison;
        private readonly Encoder _byteCounter;
        private readonly int _maxQueryLength;
        private readonly int?[] _firstColumns;
        private readonly StringBuilder _prefix = new(MaxExcerptChars);
        private string _overlap = string.Empty;
        private string _tail = string.Empty;
        private StringBuilder? _matchExcerpt;
        private long _matchExcerptStart;
        private long _excerptCapturedUntil;
        private long _processedChars;
        private long _encodedBytes;

        public LineAccumulator(
            IReadOnlyList<string> queries,
            StringComparison comparison,
            Encoding encoding)
        {
            _queries = queries;
            _comparison = comparison;
            _byteCounter = encoding.GetEncoder();
            _maxQueryLength = queries.Max(query => query.Length);
            _firstColumns = new int?[queries.Count];
        }

        public bool HasContent => _processedChars > 0;

        public void Feed(ReadOnlySpan<char> chars)
        {
            if (chars.IsEmpty)
            {
                return;
            }

            _encodedBytes += _byteCounter.GetByteCount(chars, flush: false);

            var chunk = chars.ToString();
            var chunkStart = _processedChars;
            AppendPrefix(chunk);
            FindMatches(chunk, chunkStart);
            AppendMatchExcerpt(chunk, chunkStart);
            UpdateTail(chunk);
            UpdateOverlap(chunk);
            _processedChars += chars.Length;
        }

        public long CompleteByteCount(string separator)
        {
            _encodedBytes += _byteCounter.GetByteCount(
                separator.AsSpan(),
                flush: true);
            return _encodedBytes;
        }

        public SearchTextExcerpt CreateExcerpt()
        {
            if (_matchExcerpt is not null)
            {
                return new SearchTextExcerpt(
                    _matchExcerpt.ToString(),
                    checked((int)_matchExcerptStart + 1),
                    _matchExcerptStart > 0 ||
                    _matchExcerpt.Length < _processedChars - _matchExcerptStart);
            }

            return new SearchTextExcerpt(
                _prefix.ToString(),
                1,
                _prefix.Length < _processedChars);
        }

        public IReadOnlyList<FilesystemTextQueryMatch> CreateQueryMatches()
        {
            var matches = new List<FilesystemTextQueryMatch>();
            for (var i = 0; i < _queries.Count; i++)
            {
                if (_firstColumns[i] is int column)
                {
                    matches.Add(new FilesystemTextQueryMatch(_queries[i], column));
                }
            }
            return matches;
        }

        private void FindMatches(string chunk, long chunkStart)
        {
            var combined = _overlap + chunk;
            var combinedStart = chunkStart - _overlap.Length;
            long? earliestNewMatch = null;

            for (var queryIndex = 0; queryIndex < _queries.Count; queryIndex++)
            {
                if (_firstColumns[queryIndex] is not null)
                {
                    continue;
                }

                var query = _queries[queryIndex];
                var searchFrom = 0;
                while (searchFrom <= combined.Length - query.Length)
                {
                    var found = combined.IndexOf(
                        query,
                        searchFrom,
                        _comparison);
                    if (found < 0)
                    {
                        break;
                    }

                    var absoluteStart = combinedStart + found;
                    var absoluteEnd = absoluteStart + query.Length;
                    if (absoluteEnd > chunkStart)
                    {
                        _firstColumns[queryIndex] = checked((int)absoluteStart + 1);
                        earliestNewMatch = earliestNewMatch is null
                            ? absoluteStart
                            : Math.Min(earliestNewMatch.Value, absoluteStart);
                        break;
                    }

                    searchFrom = found + 1;
                }
            }

            if (_matchExcerpt is null && earliestNewMatch is long matchStart)
            {
                StartMatchExcerpt(chunk, chunkStart, matchStart);
            }
        }

        private void StartMatchExcerpt(
            string chunk,
            long chunkStart,
            long matchStart)
        {
            var source = _tail + chunk;
            var sourceStart = chunkStart - _tail.Length;
            _matchExcerptStart = Math.Max(
                sourceStart,
                Math.Max(0, matchStart - ExcerptBeforeMatchChars));

            var startIndex = checked((int)(_matchExcerptStart - sourceStart));
            var available = source.Length - startIndex;
            var take = Math.Min(MaxExcerptChars, available);
            _matchExcerpt = new StringBuilder(MaxExcerptChars);
            if (take > 0)
            {
                _matchExcerpt.Append(source, startIndex, take);
            }
            _excerptCapturedUntil = _matchExcerptStart + take;
        }

        private void AppendMatchExcerpt(string chunk, long chunkStart)
        {
            if (_matchExcerpt is null ||
                _matchExcerpt.Length >= MaxExcerptChars)
            {
                return;
            }

            var chunkEnd = chunkStart + chunk.Length;
            if (_excerptCapturedUntil >= chunkEnd)
            {
                return;
            }

            var start = Math.Max(chunkStart, _excerptCapturedUntil);
            var startIndex = checked((int)(start - chunkStart));
            var available = chunk.Length - startIndex;
            var take = Math.Min(
                MaxExcerptChars - _matchExcerpt.Length,
                available);
            if (take > 0)
            {
                _matchExcerpt.Append(chunk, startIndex, take);
                _excerptCapturedUntil = start + take;
            }
        }

        private void AppendPrefix(string chunk)
        {
            if (_prefix.Length >= MaxExcerptChars)
            {
                return;
            }

            _prefix.Append(
                chunk,
                0,
                Math.Min(MaxExcerptChars - _prefix.Length, chunk.Length));
        }

        private void UpdateTail(string chunk)
        {
            var combined = _tail + chunk;
            _tail = combined.Length <= ExcerptBeforeMatchChars
                ? combined
                : combined[^ExcerptBeforeMatchChars..];
        }

        private void UpdateOverlap(string chunk)
        {
            var keep = Math.Max(0, _maxQueryLength - 1);
            if (keep == 0)
            {
                _overlap = string.Empty;
                return;
            }

            var combined = _overlap + chunk;
            _overlap = combined.Length <= keep
                ? combined
                : combined[^keep..];
        }
    }
}
