using System.Diagnostics;
using LoomLCI.Core;
using LoomLCI.Core.Processes;

namespace LoomLCI.Windows.Processes;

internal sealed class WindowsProcessResource : IProcessResource
{
    private const int StreamBufferChars = 1024 * 1024;

    private readonly global::System.Diagnostics.Process _process;
    private readonly BoundedChunkBuffer _stdout = new(StreamBufferChars);
    private readonly BoundedChunkBuffer _stderr = new(StreamBufferChars);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _stdinGate = new(1, 1);
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private readonly Task _exitObserver;
    private readonly object _stateGate = new();

    private ManagedProcessState _state = ManagedProcessState.Running;
    private int? _exitCode;
    private DateTimeOffset? _exitedAt;
    private bool _disposed;

    public WindowsProcessResource(global::System.Diagnostics.Process process)
    {
        _process = process;
        ProcessId = process.Id;
        StartedAt = DateTimeOffset.UtcNow;

        _stdoutPump = PumpAsync(process.StandardOutput, _stdout, _lifetime.Token);
        _stderrPump = PumpAsync(process.StandardError, _stderr, _lifetime.Token);
        _exitObserver = ObserveExitAsync();
    }

    public int ProcessId { get; }
    public DateTimeOffset StartedAt { get; }

    public ProcessStatusResult Snapshot(ProcessHandle handle)
    {
        lock (_stateGate)
        {
            return new ProcessStatusResult(
                handle,
                ProcessId,
                _state,
                _exitCode,
                StartedAt,
                _exitedAt);
        }
    }

    public ProcessOutputReadResult Read(ProcessHandle handle, long stdoutCursor, long stderrCursor, int maxChars)
    {
        var half = Math.Max(1, maxChars / 2);
        return new ProcessOutputReadResult(
            Snapshot(handle),
            _stdout.Read(stdoutCursor, half),
            _stderr.Read(stderrCursor, maxChars - half));
    }

    public async Task<LoomResult<Unit>> WriteAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            return LoomResult<Unit>.Success(Unit.Value);
        }

        if (!IsRunning())
        {
            return LoomResult<Unit>.Failure(LoomErrors.Conflict("Cannot write stdin because the process is not running."));
        }

        await _stdinGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            return LoomResult<Unit>.Success(Unit.Value);
        }
        catch (IOException ex)
        {
            return LoomResult<Unit>.Failure(LoomErrors.ExecutionFailed($"Could not write process stdin: {ex.Message}"));
        }
        catch (InvalidOperationException ex)
        {
            return LoomResult<Unit>.Failure(LoomErrors.ExecutionFailed($"Could not write process stdin: {ex.Message}"));
        }
        finally
        {
            _stdinGate.Release();
        }
    }

    public async Task<LoomResult<Unit>> TerminateAsync(CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            if (_state is ManagedProcessState.Exited or ManagedProcessState.Terminated)
            {
                return LoomResult<Unit>.Success(Unit.Value);
            }

            _state = ManagedProcessState.Terminating;
        }

        try
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            lock (_stateGate)
            {
                _state = ManagedProcessState.Terminated;
                _exitCode = SafeExitCode();
                _exitedAt ??= DateTimeOffset.UtcNow;
            }

            return LoomResult<Unit>.Success(Unit.Value);
        }
        catch (InvalidOperationException)
        {
            lock (_stateGate)
            {
                _state = ManagedProcessState.Exited;
                _exitCode = SafeExitCode();
                _exitedAt ??= DateTimeOffset.UtcNow;
            }

            return LoomResult<Unit>.Success(Unit.Value);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return LoomResult<Unit>.Failure(LoomErrors.ExecutionFailed($"Could not terminate process tree: {ex.Message}"));
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

        if (IsRunning())
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        _lifetime.Cancel();

        try
        {
            await Task.WhenAll(_stdoutPump, _stderrPump, _exitObserver).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _process.Dispose();
        _stdinGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false);
            lock (_stateGate)
            {
                if (_state != ManagedProcessState.Terminating)
                {
                    _state = ManagedProcessState.Exited;
                }
                _exitCode = SafeExitCode();
                _exitedAt = DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool IsRunning()
    {
        lock (_stateGate)
        {
            return _state is ManagedProcessState.Running or ManagedProcessState.Starting;
        }
    }

    private int? SafeExitCode()
    {
        try
        {
            return _process.HasExited ? _process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        BoundedChunkBuffer destination,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];

        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
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
