namespace LoomLCI.Windows.Processes;

internal sealed class BoundedChunkBuffer
{
    private sealed record Chunk(long Cursor, string Text);

    private readonly object _gate = new();
    private readonly LinkedList<Chunk> _chunks = new();
    private readonly int _maxChars;
    private int _charCount;
    private long _nextCursor;

    public BoundedChunkBuffer(int maxChars)
    {
        _maxChars = maxChars;
    }

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_gate)
        {
            _chunks.AddLast(new Chunk(_nextCursor++, text));
            _charCount += text.Length;

            while (_charCount > _maxChars && _chunks.First is { } first && _chunks.Count > 1)
            {
                _charCount -= first.Value.Text.Length;
                _chunks.RemoveFirst();
            }
        }
    }

    public LoomLCI.Core.Processes.OutputStreamReadResult Read(long requestedCursor, int maxChars)
    {
        lock (_gate)
        {
            var earliest = _chunks.First?.Value.Cursor ?? _nextCursor;
            var effectiveCursor = Math.Max(requestedCursor, earliest);
            var truncated = requestedCursor < earliest;
            var result = new List<LoomLCI.Core.Processes.OutputChunk>();
            var used = 0;
            var next = effectiveCursor;

            foreach (var chunk in _chunks)
            {
                if (chunk.Cursor < effectiveCursor)
                {
                    continue;
                }

                if (result.Count > 0 && used + chunk.Text.Length > maxChars)
                {
                    break;
                }

                var text = chunk.Text;
                if (result.Count == 0 && text.Length > maxChars)
                {
                    text = text[..maxChars];
                }

                result.Add(new LoomLCI.Core.Processes.OutputChunk(chunk.Cursor, text));
                used += text.Length;
                next = chunk.Cursor + 1;

                if (used >= maxChars)
                {
                    break;
                }
            }

            return new LoomLCI.Core.Processes.OutputStreamReadResult(
                requestedCursor,
                earliest,
                next,
                truncated,
                result);
        }
    }
}
