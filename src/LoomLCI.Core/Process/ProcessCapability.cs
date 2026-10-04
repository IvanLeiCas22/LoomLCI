using LoomLCI.Core.Invocations;
using LoomLCI.Core.Lifetime;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;

namespace LoomLCI.Core.Processes;

public sealed record ProcessLifetimeSweepResult(int ExpiredProcesses);

public sealed class ProcessCapability
{
    public const string ResourceKind = "process";

    private readonly IProcessProvider _provider;
    private readonly ResourceRegistry _resources;
    private readonly InvocationRunner _invocations;
    private readonly LoomEventBus _events;
    private readonly LifetimeOptions _lifetimeOptions;
    private readonly TimeProvider _timeProvider;

    public ProcessCapability(
        IProcessProvider provider,
        ResourceRegistry resources,
        InvocationRunner invocations,
        LoomEventBus events,
        LifetimeOptions? lifetimeOptions = null,
        TimeProvider? timeProvider = null)
    {
        _provider = provider;
        _resources = resources;
        _invocations = invocations;
        _events = events;
        _lifetimeOptions = lifetimeOptions ?? new LifetimeOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan PostExitRetention => _lifetimeOptions.ProcessPostExitRetention;

    public Task<LoomResult<ProcessStartResult>> StartAsync(
        ProcessStartRequest request,
        CancellationToken cancellationToken = default)
        => _invocations.RunAsync(
            "process.start",
            request.WorkId,
            async (context, token) =>
            {
                if (string.IsNullOrWhiteSpace(request.Executable))
                {
                    return LoomResult<ProcessStartResult>.Failure(
                        LoomErrors.InvalidArgument("Executable is required."));
                }

                var session = context.WorkSession;

                if (request.Ownership == ResourceOwnership.SessionOwned && session is null)
                {
                    return LoomResult<ProcessStartResult>.Failure(
                        LoomErrors.InvalidArgument(
                            "Session-owned processes require a work_id."));
                }

                if (request.IoMode is not (ProcessIoMode.Pipes or ProcessIoMode.Terminal))
                {
                    return LoomResult<ProcessStartResult>.Failure(
                        LoomErrors.InvalidArgument("Unsupported process I/O mode."));
                }

                int? terminalColumns = null;
                int? terminalRows = null;

                if (request.IoMode == ProcessIoMode.Pipes)
                {
                    if (request.TerminalColumns is not null ||
                        request.TerminalRows is not null)
                    {
                        return LoomResult<ProcessStartResult>.Failure(
                            LoomErrors.InvalidArgument(
                                "Terminal dimensions are only valid when io_mode is terminal."));
                    }
                }
                else
                {
                    terminalColumns = request.TerminalColumns ?? 80;
                    terminalRows = request.TerminalRows ?? 24;

                    if (terminalColumns is < 1 or > short.MaxValue ||
                        terminalRows is < 1 or > short.MaxValue)
                    {
                        return LoomResult<ProcessStartResult>.Failure(
                            LoomErrors.InvalidArgument(
                                $"Terminal dimensions must be between 1 and {short.MaxValue}."));
                    }
                }

                var workingDirectoryResult = ResolveWorkingDirectory(
                    request.WorkingDirectory,
                    session?.BaseDirectory);
                if (!workingDirectoryResult.IsSuccess)
                {
                    return LoomResult<ProcessStartResult>.Failure(
                        workingDirectoryResult.Error!);
                }

                var spec = new ProcessLaunchSpec(
                    request.Executable,
                    request.Arguments ?? Array.Empty<string>(),
                    workingDirectoryResult.Value,
                    request.Environment ?? new Dictionary<string, string?>(),
                    request.IoMode,
                    terminalColumns,
                    terminalRows);

                var started = await _provider.StartAsync(spec, token)
                    .ConfigureAwait(false);
                if (!started.IsSuccess)
                {
                    return LoomResult<ProcessStartResult>.Failure(started.Error!);
                }

                var resource = started.Value!;
                WorkId? owner = request.Ownership == ResourceOwnership.SessionOwned
                    ? session!.Id
                    : null;
                var rawHandle = _resources.Register(
                    "proc",
                    ResourceKind,
                    resource,
                    owner,
                    request.Ownership,
                    resource.DisposeAsync);
                var handle = new ProcessHandle(rawHandle.Value);
                var result = resource.Snapshot(handle);

                _events.Publish(
                    "ResourceCreated",
                    "process",
                    owner,
                    context.Id,
                    rawHandle,
                    new Dictionary<string, object?>
                    {
                        ["kind"] = ResourceKind,
                        ["pid"] = resource.ProcessId
                    });

                return LoomResult<ProcessStartResult>.Success(
                    new ProcessStartResult(
                        handle,
                        resource.ProcessId,
                        resource.StartedAt,
                        result.State,
                        result.IoMode,
                        _lifetimeOptions.ProcessPostExitRetention));
            },
            cancellationToken);

    public Task<LoomResult<ProcessStatusResult>> StatusAsync(
        ProcessHandle handle,
        CancellationToken cancellationToken = default)
        => RunWithProcessAsync(
            "process.status",
            handle,
            (resolved, _, _) =>
            {
                var status = resolved.Resource.Snapshot(handle);
                var touchedAt = TouchOrPrevious(resolved);
                return Task.FromResult(
                    LoomResult<ProcessStatusResult>.Success(
                        AddRetentionDeadline(status, touchedAt)));
            },
            cancellationToken);

    public Task<LoomResult<ProcessOutputReadResult>> ReadAsync(
        ProcessHandle handle,
        long stdoutCursor = 0,
        long stderrCursor = 0,
        long terminalCursor = 0,
        int maxChars = 64 * 1024,
        CancellationToken cancellationToken = default)
    {
        if (stdoutCursor < 0 || stderrCursor < 0 || terminalCursor < 0)
        {
            return Task.FromResult(
                LoomResult<ProcessOutputReadResult>.Failure(
                    LoomErrors.InvalidArgument(
                        "Output cursors must be non-negative.")));
        }

        if (maxChars is < 1 or > 1024 * 1024)
        {
            return Task.FromResult(
                LoomResult<ProcessOutputReadResult>.Failure(
                    LoomErrors.InvalidArgument(
                        "max_chars must be between 1 and 1048576.")));
        }

        return RunWithProcessAsync(
            "process.read",
            handle,
            (resolved, _, _) =>
            {
                if (resolved.Resource.IoMode == ProcessIoMode.Pipes &&
                    terminalCursor != 0)
                {
                    return Task.FromResult(
                        LoomResult<ProcessOutputReadResult>.Failure(
                            LoomErrors.InvalidArgument(
                                "terminal_cursor must be 0 for pipe-based processes.")));
                }

                if (resolved.Resource.IoMode == ProcessIoMode.Terminal &&
                    (stdoutCursor != 0 || stderrCursor != 0))
                {
                    return Task.FromResult(
                        LoomResult<ProcessOutputReadResult>.Failure(
                            LoomErrors.InvalidArgument(
                                "stdout_cursor and stderr_cursor must be 0 for terminal processes.")));
                }

                var read = resolved.Resource.Read(
                    handle,
                    stdoutCursor,
                    stderrCursor,
                    terminalCursor,
                    maxChars);
                var touchedAt = TouchOrPrevious(resolved);

                return Task.FromResult(
                    LoomResult<ProcessOutputReadResult>.Success(
                        read with
                        {
                            Process = AddRetentionDeadline(
                                read.Process,
                                touchedAt)
                        }));
            },
            cancellationToken);
    }

    public Task<LoomResult<Unit>> WriteAsync(
        ProcessHandle handle,
        string text,
        CancellationToken cancellationToken = default)
        => RunWithProcessAsync(
            "process.write",
            handle,
            (resolved, _, token) =>
                resolved.Resource.WriteAsync(text, token),
            cancellationToken);

    public Task<LoomResult<Unit>> ResizeAsync(
        ProcessHandle handle,
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        if (columns is < 1 or > short.MaxValue ||
            rows is < 1 or > short.MaxValue)
        {
            return Task.FromResult(
                LoomResult<Unit>.Failure(
                    LoomErrors.InvalidArgument(
                        $"Terminal dimensions must be between 1 and {short.MaxValue}.")));
        }

        return RunWithProcessAsync(
            "process.resize",
            handle,
            (resolved, _, token) =>
                resolved.Resource.ResizeAsync(columns, rows, token),
            cancellationToken);
    }

    public Task<LoomResult<Unit>> TerminateAsync(
        ProcessHandle handle,
        CancellationToken cancellationToken = default)
        => RunWithProcessAsync(
            "process.terminate",
            handle,
            async (resolved, context, token) =>
            {
                var result = await resolved.Resource.TerminateAsync(token)
                    .ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    _events.Publish(
                        "ProcessTerminated",
                        "process",
                        resolved.OwnerWorkId,
                        context.Id,
                        handle.AsResourceHandle());
                }

                return result;
            },
            cancellationToken,
            touchOnSuccess: true);

    public async Task<ProcessLifetimeSweepResult> SweepExpiredAsync()
    {
        var now = _timeProvider.GetUtcNow();
        var cutoff = now - _lifetimeOptions.ProcessPostExitRetention;
        var expiredProcesses = 0;

        foreach (var rawHandle in _resources.GetActiveHandles(ResourceKind))
        {
            var acquired = _resources.Acquire<IProcessResource>(
                rawHandle,
                ResourceKind);
            if (!acquired.IsSuccess)
            {
                continue;
            }

            var eligible = false;
            WorkId? owner = null;

            using (var lease = acquired.Value!)
            {
                var status = lease.Resource.Snapshot(
                    new ProcessHandle(rawHandle.Value));
                owner = lease.OwnerWorkId;

                eligible =
                    IsTerminal(status.State) &&
                    status.ExitedAt is { } exitedAt &&
                    exitedAt <= cutoff &&
                    lease.LastAccessedAt <= cutoff;
            }

            if (!eligible)
            {
                continue;
            }

            var expired = await _resources.ExpireIfUnaccessedSinceAsync(
                    rawHandle,
                    ResourceKind,
                    cutoff)
                .ConfigureAwait(false);

            if (!expired.IsSuccess || expired.Value != true)
            {
                continue;
            }

            expiredProcesses++;
            _events.Publish(
                "ResourceExpired",
                "process",
                owner,
                resourceHandle: rawHandle,
                payload: new Dictionary<string, object?>
                {
                    ["kind"] = ResourceKind
                });
        }

        return new ProcessLifetimeSweepResult(expiredProcesses);
    }

    private Task<LoomResult<T>> RunWithProcessAsync<T>(
        string operation,
        ProcessHandle handle,
        Func<
            ResourceOperationLease<IProcessResource>,
            InvocationContext,
            CancellationToken,
            Task<LoomResult<T>>> action,
        CancellationToken cancellationToken,
        bool touchOnSuccess = false)
    {
        var initial = Resolve(handle);
        var owner = initial.IsSuccess ? initial.Value!.OwnerWorkId : null;

        return _invocations.RunAsync(
            operation,
            owner,
            async (context, token) =>
            {
                var acquired = _resources.Acquire<IProcessResource>(
                    handle.AsResourceHandle(),
                    ResourceKind);
                if (!acquired.IsSuccess)
                {
                    return LoomResult<T>.Failure(acquired.Error!);
                }

                using var lease = acquired.Value!;
                var result = await action(lease, context, token)
                    .ConfigureAwait(false);

                if (touchOnSuccess && result.IsSuccess)
                {
                    _resources.Touch(
                        handle.AsResourceHandle(),
                        ResourceKind);
                }

                return result;
            },
            cancellationToken);
    }

    private LoomResult<ResourceLease<IProcessResource>> Resolve(
        ProcessHandle handle)
        => _resources.Resolve<IProcessResource>(
            handle.AsResourceHandle(),
            ResourceKind);

    private DateTimeOffset TouchOrPrevious(
        ResourceOperationLease<IProcessResource> resolved)
        => _resources.Touch(resolved.Handle, ResourceKind)
           ?? resolved.LastAccessedAt;

    private ProcessStatusResult AddRetentionDeadline(
        ProcessStatusResult status,
        DateTimeOffset lastAccessedAt)
    {
        if (!IsTerminal(status.State) || status.ExitedAt is not { } exitedAt)
        {
            return status with { RetentionExpiresAt = null };
        }

        var anchor = exitedAt > lastAccessedAt
            ? exitedAt
            : lastAccessedAt;

        return status with
        {
            RetentionExpiresAt =
                anchor + _lifetimeOptions.ProcessPostExitRetention
        };
    }

    private static bool IsTerminal(ManagedProcessState state)
        => state is ManagedProcessState.Exited or ManagedProcessState.Terminated;

    private static LoomResult<string?> ResolveWorkingDirectory(
        string? requested,
        string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return LoomResult<string?>.Success(baseDirectory);
        }

        try
        {
            if (Path.IsPathFullyQualified(requested))
            {
                return LoomResult<string?>.Success(
                    Path.GetFullPath(requested));
            }

            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                return LoomResult<string?>.Failure(
                    LoomErrors.InvalidArgument(
                        "A relative working directory requires a work session with base_directory."));
            }

            return LoomResult<string?>.Success(
                Path.GetFullPath(
                    Path.Combine(baseDirectory, requested)));
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return LoomResult<string?>.Failure(
                LoomErrors.InvalidArgument(
                    $"Invalid working directory: {ex.Message}"));
        }
    }
}
