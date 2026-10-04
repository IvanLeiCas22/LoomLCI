using System.IO.Pipes;
using System.Text;
using LoomLCI.Core;
using LoomLCI.Core.Processes;

namespace LoomLCI.Windows.Processes;

internal sealed class WindowsPipeProcessIo : IWindowsProcessIo
{
    private const long StreamSpoolMaxBytes = 64L * 1024 * 1024;
    private const long StreamSpoolMaxChars = StreamSpoolMaxBytes / sizeof(char);

    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdoutReader;
    private readonly StreamReader _stderrReader;
    private readonly ProcessOutputStore _stdout;
    private readonly ProcessOutputStore _stderr;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _stdinGate = new(1, 1);
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private bool _disposed;

    public WindowsPipeProcessIo(
        AnonymousPipeServerStream stdin,
        AnonymousPipeServerStream stdout,
        AnonymousPipeServerStream stderr)
    {
        _stdin = new StreamWriter(
            stdin,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: false);
        _stdoutReader = new StreamReader(
            stdout,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        _stderrReader = new StreamReader(
            stderr,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);

        _stdout = new ProcessOutputStore(StreamSpoolMaxChars);
        try
        {
            _stderr = new ProcessOutputStore(StreamSpoolMaxChars);
        }
        catch
        {
            _stdout.Dispose();
            _stdin.Dispose();
            _stdoutReader.Dispose();
            _stderrReader.Dispose();
            throw;
        }

        _stdoutPump = PumpAsync(_stdoutReader, _stdout, _lifetime.Token);
        _stderrPump = PumpAsync(_stderrReader, _stderr, _lifetime.Token);
    }

    public ProcessIoMode IoMode => ProcessIoMode.Pipes;
    public string? StdoutSpoolPath => _stdout.SpoolPath;
    public string? StderrSpoolPath => _stderr.SpoolPath;
    public string? TerminalSpoolPath => null;

    public ProcessOutputReadResult Read(
        ProcessStatusResult process,
        long stdoutCursor,
        long stderrCursor,
        long terminalCursor,
        int maxChars)
    {
        var stdoutBudget = (maxChars + 1) / 2;
        var stderrBudget = maxChars / 2;

        var stdout = _stdout.Read(stdoutCursor, stdoutBudget);
        var stderr = _stderr.Read(stderrCursor, stderrBudget);
        var remaining = maxChars - CountChars(stdout) - CountChars(stderr);

        if (remaining > 0 && stdout.NextCursor < stdout.RetainedUntilCursor)
        {
            var extra = _stdout.Read(stdout.NextCursor, remaining);
            stdout = Merge(stdout, extra);
            remaining = maxChars - CountChars(stdout) - CountChars(stderr);
        }

        if (remaining > 0 && stderr.NextCursor < stderr.RetainedUntilCursor)
        {
            var extra = _stderr.Read(stderr.NextCursor, remaining);
            stderr = Merge(stderr, extra);
        }

        return new ProcessOutputReadResult(
            process,
            IoMode,
            stdout,
            stderr,
            null);
    }

    public async Task<LoomResult<Unit>> WriteAsync(
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            return LoomResult<Unit>.Success(Unit.Value);
        }

        await _stdinGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stdin.WriteAsync(text.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
            return LoomResult<Unit>.Success(Unit.Value);
        }
        catch (IOException ex)
        {
            return LoomResult<Unit>.Failure(
                LoomErrors.ExecutionFailed(
                    $"Could not write process stdin: {ex.Message}"));
        }
        catch (ObjectDisposedException ex)
        {
            return LoomResult<Unit>.Failure(
                LoomErrors.ExecutionFailed(
                    $"Could not write process stdin: {ex.Message}"));
        }
        finally
        {
            _stdinGate.Release();
        }
    }

    public Task<LoomResult<Unit>> ResizeAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            LoomResult<Unit>.Failure(
                LoomErrors.Unsupported(
                    "Pipe-based processes do not support terminal resize.")));
    }

    public Task CloseSessionAsync()
        => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _stdin.Dispose();
        }
        catch (IOException)
        {
        }

        _lifetime.Cancel();

        try
        {
            await Task.WhenAll(_stdoutPump, _stderrPump).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _stdoutReader.Dispose();
            _stderrReader.Dispose();
            _stdout.Dispose();
            _stderr.Dispose();
            _stdinGate.Dispose();
            _lifetime.Dispose();
        }
    }

    private static int CountChars(OutputStreamReadResult result)
        => result.Chunks.Sum(chunk => chunk.Text.Length);

    private static OutputStreamReadResult Merge(
        OutputStreamReadResult first,
        OutputStreamReadResult second)
        => new(
            first.RequestedCursor,
            first.EarliestAvailableCursor,
            second.NextCursor,
            second.RetainedUntilCursor,
            second.ObservedUntilCursor,
            first.Truncated || second.Truncated,
            second.RetentionLimitReached,
            first.Chunks.Concat(second.Chunks).ToArray());

    private static async Task PumpAsync(
        StreamReader reader,
        ProcessOutputStore destination,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(
                        buffer.AsMemory(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            destination.Append(new string(buffer, 0, read));
        }
    }
}
