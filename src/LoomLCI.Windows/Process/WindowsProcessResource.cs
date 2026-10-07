#pragma warning disable CA1416 // LoomLCI.Windows is the Windows-specific platform backend.

using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using LoomLCI.Core;
using LoomLCI.Core.Processes;
using Windows.Win32;

namespace LoomLCI.Windows.Processes;

internal sealed class WindowsProcessResource : IProcessResource
{
    private const uint ForcedTerminationExitCode = 1;
    private const uint StillActiveExitCode = 259;

    private readonly SafeFileHandle _processHandle;
    private readonly WindowsJobObject _job;
    private readonly IWindowsProcessIo _io;
    private readonly ProcessWaitHandle _processWaitHandle;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _exitObserver;
    private readonly object _stateGate = new();
    private readonly object _jobGate = new();
    private readonly TimeProvider _timeProvider;

    private ManagedProcessState _state = ManagedProcessState.Running;
    private int? _exitCode;
    private DateTimeOffset? _exitedAt;
    private bool _disposed;
    private bool _jobTerminated;
    private bool _jobDisposed;

    public WindowsProcessResource(
        int processId,
        SafeFileHandle processHandle,
        WindowsJobObject job,
        IWindowsProcessIo io,
        TimeProvider? timeProvider = null)
    {
        ProcessId = processId;
        _timeProvider = timeProvider ?? TimeProvider.System;
        StartedAt = _timeProvider.GetUtcNow();

        _processHandle = processHandle;
        _job = job;
        _io = io;
        _processWaitHandle = new ProcessWaitHandle(processHandle);
        _exitObserver = ObserveExitAsync();
    }

    public int ProcessId { get; }
    public DateTimeOffset StartedAt { get; }
    public ProcessIoMode IoMode => _io.IoMode;

    internal string StdoutSpoolPath
        => _io.StdoutSpoolPath ??
           throw new InvalidOperationException(
               "This process does not have a stdout spool.");

    internal string StderrSpoolPath
        => _io.StderrSpoolPath ??
           throw new InvalidOperationException(
               "This process does not have a stderr spool.");

    internal string TerminalSpoolPath
        => _io.TerminalSpoolPath ??
           throw new InvalidOperationException(
               "This process does not have a terminal spool.");

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
                _exitedAt,
                IoMode,
                null);
        }
    }

    public ProcessOutputReadResult Read(
        ProcessHandle handle,
        long stdoutCursor,
        long stderrCursor,
        long terminalCursor,
        int maxChars)
        => _io.Read(
            Snapshot(handle),
            stdoutCursor,
            stderrCursor,
            terminalCursor,
            maxChars);

    public Task<LoomResult<Unit>> WriteAsync(
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(
                LoomResult<Unit>.Success(Unit.Value));
        }

        if (!IsRootRunning())
        {
            return Task.FromResult(
                LoomResult<Unit>.Failure(
                    LoomErrors.Conflict(
                        "Cannot write process input because the process is not running.")));
        }

        return _io.WriteAsync(text, cancellationToken);
    }

    public Task<LoomResult<Unit>> ResizeAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken)
    {
        if (IoMode == ProcessIoMode.Terminal && !IsRootRunning())
        {
            return Task.FromResult(
                LoomResult<Unit>.Failure(
                    LoomErrors.Conflict(
                        "Cannot resize terminal because the process is not running.")));
        }

        return _io.ResizeAsync(columns, rows, cancellationToken);
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken)
        => _exitObserver.WaitAsync(cancellationToken);

    public async Task WaitForExitAndOutputAsync(CancellationToken cancellationToken)
    {
        await WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (_io is WindowsPipeProcessIo pipes)
        {
            await pipes.WaitForOutputCompletionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<LoomResult<Unit>> TerminateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ManagedProcessState previousState;

        lock (_stateGate)
        {
            previousState = _state;

            if (_state == ManagedProcessState.Terminated)
            {
                return LoomResult<Unit>.Success(Unit.Value);
            }

            if (_state != ManagedProcessState.Exited)
            {
                _state = ManagedProcessState.Terminating;
            }
        }

        try
        {
            TerminateJob(suppressErrors: false);
            await _exitObserver.ConfigureAwait(false);

            if (previousState != ManagedProcessState.Exited)
            {
                lock (_stateGate)
                {
                    _state = ManagedProcessState.Terminated;
                    _exitCode ??= SafeExitCode();
                    _exitedAt ??= _timeProvider.GetUtcNow();
                }
            }

            return LoomResult<Unit>.Success(Unit.Value);
        }
        catch (Win32Exception ex)
        {
            lock (_stateGate)
            {
                if (_state == ManagedProcessState.Terminating)
                {
                    _state = previousState;
                }
            }

            return LoomResult<Unit>.Failure(
                LoomErrors.ExecutionFailed(
                    $"Could not terminate process tree: {ex.Message}"));
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
            TerminateJob(suppressErrors: true);

            try
            {
                await _exitObserver.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await _io.CloseSessionAsync().ConfigureAwait(false);
            await _io.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            DisposeJob();
            _lifetime.Cancel();
            _processWaitHandle.Dispose();
            _processHandle.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            await WaitAsync(_processWaitHandle, _lifetime.Token)
                .ConfigureAwait(false);

            bool wasTerminating;

            lock (_stateGate)
            {
                wasTerminating =
                    _state == ManagedProcessState.Terminating;

                if (!wasTerminating)
                {
                    _state = ManagedProcessState.Exited;
                }

                _exitCode = SafeExitCode();
                _exitedAt = _timeProvider.GetUtcNow();
            }

            if (IoMode == ProcessIoMode.Terminal)
            {
                if (!wasTerminating)
                {
                    TerminateJob(suppressErrors: true);
                }

                await _io.CloseSessionAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool IsRootRunning()
    {
        lock (_stateGate)
        {
            return !_disposed &&
                   _state is
                       ManagedProcessState.Running or
                       ManagedProcessState.Starting;
        }
    }

    private void TerminateJob(bool suppressErrors)
    {
        lock (_jobGate)
        {
            if (_jobDisposed || _jobTerminated)
            {
                return;
            }

            try
            {
                _job.Terminate(ForcedTerminationExitCode);
                _jobTerminated = true;
            }
            catch (Win32Exception) when (suppressErrors)
            {
            }
        }
    }

    private void DisposeJob()
    {
        lock (_jobGate)
        {
            if (_jobDisposed)
            {
                return;
            }

            _jobDisposed = true;
            _job.Dispose();
        }
    }

    private int? SafeExitCode()
    {
        if (!PInvoke.GetExitCodeProcess(_processHandle, out var exitCode) ||
            exitCode == StillActiveExitCode)
        {
            return null;
        }

        return unchecked((int)exitCode);
    }

    private static async Task WaitAsync(
        WaitHandle waitHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        RegisteredWaitHandle? registeredWait = null;
        CancellationTokenRegistration cancellationRegistration = default;

        try
        {
            registeredWait = ThreadPool.RegisterWaitForSingleObject(
                waitHandle,
                static (state, _) =>
                    ((TaskCompletionSource)state!).TrySetResult(),
                completion,
                Timeout.Infinite,
                executeOnlyOnce: true);

            if (cancellationToken.CanBeCanceled)
            {
                cancellationRegistration = cancellationToken.Register(
                    static state =>
                    {
                        var tuple =
                            ((TaskCompletionSource Completion,
                              CancellationToken Token))state!;
                        tuple.Completion.TrySetCanceled(tuple.Token);
                    },
                    (completion, cancellationToken));
            }

            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            cancellationRegistration.Dispose();
            registeredWait?.Unregister(null);
        }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeFileHandle processHandle)
        {
            SafeWaitHandle = new SafeWaitHandle(
                processHandle.DangerousGetHandle(),
                ownsHandle: false);
        }
    }
}
