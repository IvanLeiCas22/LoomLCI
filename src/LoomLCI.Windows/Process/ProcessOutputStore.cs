using LoomLCI.Core.Processes;

namespace LoomLCI.Windows.Processes;

internal sealed class ProcessOutputStore : IDisposable
{
    private const int BytesPerChar = sizeof(char);
    private static readonly string SpoolDirectory = Path.Combine(
        Path.GetTempPath(),
        "LoomLCI",
        "process-output");

    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly long _maxRetainedChars;
    private long _retainedChars;
    private long _observedChars;
    private bool _disposed;

    public ProcessOutputStore(long maxRetainedChars)
    {
        if (maxRetainedChars < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetainedChars));
        }

        _maxRetainedChars = maxRetainedChars;
        Directory.CreateDirectory(SpoolDirectory);
        SpoolPath = Path.Combine(SpoolDirectory, $"{Guid.NewGuid():N}.spool");
        _stream = new FileStream(
            SpoolPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.RandomAccess | FileOptions.DeleteOnClose);
    }

    internal string SpoolPath { get; }

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_gate)
        {
            ThrowIfDisposed();

            if (_observedChars == _retainedChars)
            {
                var available = Math.Max(0, _maxRetainedChars - _retainedChars);
                var retainCount = (int)Math.Min(text.Length, available);

                if (retainCount == 0 &&
                    text.Length > 0 &&
                    char.IsLowSurrogate(text[0]) &&
                    _retainedChars > 0 &&
                    char.IsHighSurrogate(ReadCharAt(_retainedChars - 1)))
                {
                    _retainedChars--;
                }
                else if (retainCount > 0 &&
                         retainCount < text.Length &&
                         char.IsHighSurrogate(text[retainCount - 1]) &&
                         char.IsLowSurrogate(text[retainCount]))
                {
                    retainCount--;
                }

                if (retainCount > 0)
                {
                    var bytes = new byte[retainCount * BytesPerChar];
                    EncodeUtf16CodeUnits(text.AsSpan(0, retainCount), bytes);
                    RandomAccess.Write(
                        _stream.SafeFileHandle,
                        bytes,
                        checked(_retainedChars * BytesPerChar));
                    _retainedChars += retainCount;
                }
            }

            _observedChars += text.Length;
        }
    }

    public OutputStreamReadResult Read(long requestedCursor, int maxChars)
    {
        if (maxChars < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChars));
        }

        lock (_gate)
        {
            ThrowIfDisposed();

            const long earliest = 0;
            var effectiveCursor = Math.Max(requestedCursor, earliest);
            var available = Math.Max(0, _retainedChars - effectiveCursor);
            var count = (int)Math.Min(maxChars, available);
            var chunks = Array.Empty<OutputChunk>();
            var nextCursor = effectiveCursor;

            if (count > 0)
            {
                var text = ReadText(effectiveCursor, count);
                if (char.IsHighSurrogate(text[^1]) && effectiveCursor + count < _retainedChars)
                {
                    var following = ReadCharAt(effectiveCursor + count);
                    if (char.IsLowSurrogate(following))
                    {
                        text += following;
                        count++;
                    }
                }

                chunks = [new OutputChunk(effectiveCursor, text)];
                nextCursor = effectiveCursor + count;
            }

            return new OutputStreamReadResult(
                requestedCursor,
                earliest,
                nextCursor,
                _retainedChars,
                _observedChars,
                requestedCursor < earliest,
                _observedChars > _retainedChars,
                chunks);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stream.Dispose();

            try
            {
                if (File.Exists(SpoolPath))
                {
                    File.Delete(SpoolPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private string ReadText(long cursor, int count)
    {
        var bytes = new byte[count * BytesPerChar];
        ReadExactly(
            _stream.SafeFileHandle,
            bytes,
            checked(cursor * BytesPerChar));
        return DecodeUtf16CodeUnits(bytes);
    }

    private char ReadCharAt(long cursor)
        => ReadText(cursor, 1)[0];

    private static void EncodeUtf16CodeUnits(ReadOnlySpan<char> chars, Span<byte> bytes)
    {
        for (var i = 0; i < chars.Length; i++)
        {
            var value = chars[i];
            bytes[i * 2] = (byte)value;
            bytes[i * 2 + 1] = (byte)(value >> 8);
        }
    }

    private static string DecodeUtf16CodeUnits(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length / BytesPerChar];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
        }
        return new string(chars);
    }

    private static void ReadExactly(Microsoft.Win32.SafeHandles.SafeFileHandle handle, Span<byte> destination, long offset)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(handle, destination[total..], offset + total);
            if (read == 0)
            {
                throw new EndOfStreamException("Process output spool ended unexpectedly.");
            }
            total += read;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
