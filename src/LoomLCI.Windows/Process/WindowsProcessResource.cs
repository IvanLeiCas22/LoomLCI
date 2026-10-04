#pragma warning disable CA1416 // LoomLCI.Windows is the Windows-specific platform backend.

using System.ComponentModel;
using System.IO.Pipes;
using System.Text;
using Microsoft.Win32.SafeHandles;
using LoomLCI.Core;
using LoomLCI.Core.Processes;
using Windows.Win32;

namespace LoomLCI.Windows.Processes;

internal sealed class WindowsProcessResource : IProcessResource
{
    private const long StreamSpoolMaxBytes = 64L * 1024 * 1024;
    private const long StreamSpoolMaxChars = StreamSpoolMaxBytes / sizeof(char);
    private const uint ForcedTerminationExitCode = 1;
    private const uint StillActiveExitCode = 259;

    private readonly SafeFileHandle _processHandle;
    private readonly WindowsJobObject _job;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdoutReader;
    private readonly StreamReader _stderrReader;
    private readonly ProcessWaitHandle _processWaitHandle;
    private readonly ProcessOutputStore _stdout;
    private readonly ProcessOutputStore _stderr;
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

    public WindowsProcessResource(
        int processId,
        SafeFileHandle processHandle,
        WindowsJobObject job,
        AnonymousPipeServerStream stdin,
        AnonymousPipeServerStream stdout,
        AnonymousPipeServerStream stderr)
    {
        ProcessId = processId;
        StartedAt = DateTimeOffset.UtcNow;

        _processHandle = processHandle;
        _job = job;

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
        _processWaitHandle = new ProcessWaitHandle(processHandle);

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
            _processWaitHandle.Dispose();
            throw;
        }

        _stdoutPump = PumpAsync(_stdoutReader, _stdout, _lifetime.Token);
        _stderrPump = PumpAsync(_stderrReader, _stderr, _lifetime.Token);
        _exitObserver = ObserveExitAsync();
    }

    public int ProcessId { get; }
    public DateTimeOffset StartedAt { get; }
    internal string StdoutSpoolPath => _stdout.SpoolPath;
    internal string StderrSpoolPath => _stderr.SpoolPath;

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

    public ProcessOutputReadResult Read(
        ProcessHandle handle,
        long stdoutCursor,
        long stderrCursor,
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
            Snapshot(handle),
            stdout,
            stderr);
    }

    public async Task<LoomResult<Unit>> WriteAsync(
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text))
        {
            return LoomResult<Unit>.Success(Unit.Value);
        }

        if (!IsRootRunning())
        {
            return LoomResult<Unit>.Failure(
                LoomErrors.Conflict(
                    "Cannot write stdin because the process is not running."));
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
            _job.Terminate(ForcedTerminationExitCode);

            if (previousState != ManagedProcessState.Exited)
            {
                await _exitObserver.ConfigureAwait(false);

                lock (_stateGate)
                {
                    _state = ManagedProcessState.Terminated;
                    _exitCode ??= SafeExitCode();
                    _exitedAt ??= DateTimeOffset.UtcNow;
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
            _stdin.Dispose();
        }
        catch (IOException)
        {
        }

        try
        {
            _job.Terminate(ForcedTerminationExitCode);
        }
        catch (Win32Exception)
        {
        }
        finally
        {
            _job.Dispose();
        }

        try
        {
            await _exitObserver.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Cancel();

        try
        {
            await Task.WhenAll(_stdoutPump, _stderrPump)
                .ConfigureAwait(false);
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
            _processWaitHandle.Dispose();
            _processHandle.Dispose();
            _stdout.Dispose();
            _stderr.Dispose();
            _stdinGate.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            await WaitAsync(_processWaitHandle, _lifetime.Token)
                .ConfigureAwait(false);

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

    private bool IsRootRunning()
    {
        lock (_stateGate)
        {
            return _state is
                ManagedProcessState.Running or
                ManagedProcessState.Starting;
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
