using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using LoomLCI.Core;
using LoomLCI.Core.Processes;

namespace LoomLCI.Windows.Processes;

internal sealed class WindowsTerminalProcessIo : IWindowsProcessIo
{
    private enum TerminalSessionState
    {
        Open,
        Closing,
        Closed
    }

    private const long StreamSpoolMaxBytes = 64L * 1024 * 1024;
    private const long StreamSpoolMaxChars = StreamSpoolMaxBytes / sizeof(char);

    private readonly WindowsPseudoConsole _pseudoConsole;
    private readonly StreamWriter _input;
    private readonly StreamReader _outputReader;
    private readonly ProcessOutputStore _terminal;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Task _outputPump;
    private readonly object _stateGate = new();

    private TerminalSessionState _state = TerminalSessionState.Open;
    private Task? _closeTask;
    private bool _disposed;

    public WindowsTerminalProcessIo(
        WindowsPseudoConsole pseudoConsole,
        AnonymousPipeServerStream input,
        AnonymousPipeServerStream output)
    {
        _pseudoConsole = pseudoConsole;
        _input = new StreamWriter(
            input,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: false);
        _outputReader = new StreamReader(
            output,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: false);

        _terminal = new ProcessOutputStore(StreamSpoolMaxChars);
        _outputPump = PumpAsync(
            _outputReader,
            _terminal,
            _lifetime.Token);
    }

    public ProcessIoMode IoMode => ProcessIoMode.Terminal;
    public string? StdoutSpoolPath => null;
    public string? StderrSpoolPath => null;
    public string? TerminalSpoolPath => _terminal.SpoolPath;

    public ProcessOutputReadResult Read(
        ProcessStatusResult process,
        long stdoutCursor,
        long stderrCursor,
        long terminalCursor,
        int maxChars)
        => new(
            process,
            IoMode,
            null,
            null,
            _terminal.Read(terminalCursor, maxChars));

    public async Task<LoomResult<Unit>> WriteAsync(
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            return LoomResult<Unit>.Success(Unit.Value);
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsOpen())
            {
                return LoomResult<Unit>.Failure(
                    LoomErrors.Conflict(
                        "Cannot write terminal input because the terminal is closing or closed."));
            }

            try
            {
                await _input.WriteAsync(text.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
                return LoomResult<Unit>.Success(Unit.Value);
            }
            catch (IOException ex)
            {
                return LoomResult<Unit>.Failure(
                    LoomErrors.ExecutionFailed(
                        $"Could not write terminal input: {ex.Message}"));
            }
            catch (ObjectDisposedException ex)
            {
                return LoomResult<Unit>.Failure(
                    LoomErrors.ExecutionFailed(
                        $"Could not write terminal input: {ex.Message}"));
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<LoomResult<Unit>> ResizeAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsOpen())
            {
                return LoomResult<Unit>.Failure(
                    LoomErrors.Conflict(
                        "Cannot resize terminal because the terminal is closing or closed."));
            }

            try
            {
                _pseudoConsole.Resize(columns, rows);
                return LoomResult<Unit>.Success(Unit.Value);
            }
            catch (ObjectDisposedException)
            {
                return LoomResult<Unit>.Failure(
                    LoomErrors.Conflict(
                        "Cannot resize a closed terminal."));
            }
            catch (COMException ex)
            {
                return LoomResult<Unit>.Failure(
                    LoomErrors.ExecutionFailed(
                        $"Could not resize terminal: {ex.Message}"));
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task CloseSessionAsync()
    {
        lock (_stateGate)
        {
            if (_closeTask is not null)
            {
                return _closeTask;
            }

            if (_state == TerminalSessionState.Closed)
            {
                return Task.CompletedTask;
            }

            _state = TerminalSessionState.Closing;
            _closeTask = CloseSessionCoreAsync();
            return _closeTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            await CloseSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Cancel();
            _outputReader.Dispose();
            _terminal.Dispose();
            _operationGate.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task CloseSessionCoreAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                _input.Dispose();
            }
            catch (IOException)
            {
            }
        }
        finally
        {
            _operationGate.Release();
        }

        var released = false;
        try
        {
            released = _pseudoConsole.TryRelease();
        }
        catch (ObjectDisposedException)
        {
        }

        if (released)
        {
            await AwaitOutputPumpAsync().ConfigureAwait(false);
            _pseudoConsole.Dispose();
        }
        else
        {
            var closePseudoConsole = Task.Factory.StartNew(
                static state =>
                    ((WindowsPseudoConsole)state!).Dispose(),
                _pseudoConsole,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            await Task.WhenAll(
                    AwaitOutputPumpAsync(),
                    closePseudoConsole)
                .ConfigureAwait(false);
        }

        _outputReader.Dispose();

        lock (_stateGate)
        {
            _state = TerminalSessionState.Closed;
        }
    }

    private async Task AwaitOutputPumpAsync()
    {
        try
        {
            await _outputPump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool IsOpen()
    {
        lock (_stateGate)
        {
            return !_disposed && _state == TerminalSessionState.Open;
        }
    }

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
            catch (IOException)
            {
                break;
            }
            catch (ObjectDisposedException)
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
