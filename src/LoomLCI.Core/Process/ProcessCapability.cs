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

    internal LoomResult<Unit> ValidateSessionOwnedHandle(
        ProcessHandle handle,
        WorkId workId,
        bool allowTombstone = false)
    {
        WorkId? ownerWorkId;
        ResourceOwnership ownership;

        if (allowTombstone)
        {
            var metadata = _resources.Inspect(
                handle.AsResourceHandle(),
                ResourceKind);
            if (!metadata.IsSuccess)
            {
                return LoomResult<Unit>.Failure(
                    metadata.Error!);
            }

            ownerWorkId = metadata.Value!.OwnerWorkId;
            ownership = metadata.Value.Ownership;
        }
        else
        {
            var resolved = Resolve(handle);
            if (!resolved.IsSuccess)
            {
                return LoomResult<Unit>.Failure(
                    resolved.Error!);
            }

            ownerWorkId = resolved.Value!.OwnerWorkId;
            ownership = resolved.Value.Ownership;
        }

        if (ownership != ResourceOwnership.SessionOwned ||
            ownerWorkId != workId)
        {
            return LoomResult<Unit>.Failure(
                new LoomError(
                    "access_denied",
                    "Process handle is not session-owned by the active WorkSession.",
                    false,
                    new Dictionary<string, object?>
                    {
                        ["reason"] =
                            "process_handle_not_owned_by_work_session",
                        ["process_handle"] = handle.Value,
                        ["work_id"] = workId.Value
                    }));
        }

        return LoomResult<Unit>.Success(Unit.Value);
    }

    public Task<LoomResult<ProcessStartResult>> StartAsync(
        ProcessStartRequest request,
        CancellationToken cancellationToken = default)
        => _invocations.RunAsync(
            "process.start",
            request.WorkId,
            async (context, token) =>
            {
                var session = context.WorkSession;

                if (request.Ownership == ResourceOwnership.SessionOwned && session is null)
                {
                    return LoomResult<ProcessStartResult>.Failure(
                        LoomErrors.InvalidArgument(
                            "Session-owned processes require a work_id."));
                }

                var specResult = BuildLaunchSpec(
                    request.Executable,
                    request.Arguments,
                    request.WorkingDirectory,
                    request.Environment,
                    request.IoMode,
                    request.TerminalColumns,
                    request.TerminalRows,
                    session?.BaseDirectory);
                if (!specResult.IsSuccess)
                {
                    return LoomResult<ProcessStartResult>.Failure(
                        specResult.Error!);
                }

                var started = await _provider.StartAsync(specResult.Value!, token)
                    .ConfigureAwait(false);
                if (!started.IsSuccess)
                {
                    return LoomResult<ProcessStartResult>.Failure(started.Error!);
                }

                var resource = started.Value!;
                if (token.IsCancellationRequested)
                {
                    await resource.DisposeAsync().ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }

                WorkId? owner = request.Ownership == ResourceOwnership.SessionOwned
                    ? session!.Id
                    : null;

                ResourceHandle rawHandle;
                try
                {
                    rawHandle = _resources.Register(
                        "proc",
                        ResourceKind,
                        resource,
                        owner,
                        request.Ownership,
                        resource.DisposeAsync);
                }
                catch
                {
                    await resource.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                if (token.IsCancellationRequested)
                {
                    await _resources.CloseAsync(rawHandle).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }

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

    public Task<LoomResult<ProcessRunResult>> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default)
    {
        var timeout = request.Timeout ?? TimeSpan.FromSeconds(30);
        if (timeout < TimeSpan.FromSeconds(1) ||
            timeout > TimeSpan.FromSeconds(600))
        {
            return Task.FromResult(
                LoomResult<ProcessRunResult>.Failure(
                    LoomErrors.InvalidArgument(
                        "timeout must be between 1 and 600 seconds.")));
        }

        if (request.MaxOutputChars is < 1 or > 1024 * 1024)
        {
            return Task.FromResult(
                LoomResult<ProcessRunResult>.Failure(
                    LoomErrors.InvalidArgument(
                        "max_output_chars must be between 1 and 1048576.")));
        }

        return _invocations.RunAsync(
            "process.run",
            request.WorkId,
            async (context, token) =>
            {
                var specResult = BuildLaunchSpec(
                    request.Executable,
                    request.Arguments,
                    request.WorkingDirectory,
                    request.Environment,
                    ProcessIoMode.Pipes,
                    terminalColumns: null,
                    terminalRows: null,
                    context.WorkSession?.BaseDirectory);
                if (!specResult.IsSuccess)
                {
                    return LoomResult<ProcessRunResult>.Failure(
                        specResult.Error!);
                }

                var started = await _provider
                    .StartAsync(specResult.Value!, token)
                    .ConfigureAwait(false);
                if (!started.IsSuccess)
                {
                    return LoomResult<ProcessRunResult>.Failure(
                        started.Error!);
                }

                await using var resource = started.Value!;
                await resource
                    .WaitForExitAndOutputAsync(token)
                    .ConfigureAwait(false);

                var read = resource.Read(
                    new ProcessHandle("proc_run"),
                    stdoutCursor: 0,
                    stderrCursor: 0,
                    terminalCursor: 0,
                    request.MaxOutputChars);
                var status = read.Process;

                if (!IsTerminal(status.State) ||
                    status.ExitCode is not { } exitCode ||
                    status.ExitedAt is not { } exitedAt)
                {
                    return LoomResult<ProcessRunResult>.Failure(
                        LoomErrors.ExecutionFailed(
                            "One-shot process completed without terminal exit metadata."));
                }

                return LoomResult<ProcessRunResult>.Success(
                    new ProcessRunResult(
                        resource.ProcessId,
                        exitCode,
                        resource.StartedAt,
                        exitedAt,
                        FlattenOutput(read.Stdout),
                        FlattenOutput(read.Stderr),
                        IsRunOutputTruncated(read.Stdout),
                        IsRunOutputTruncated(read.Stderr),
                        read.Stdout?.ObservedUntilCursor ?? 0,
                        read.Stderr?.ObservedUntilCursor ?? 0));
            },
            cancellationToken,
            timeout);
    }

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

    public Task<LoomResult<Unit>> ReleaseAsync(
        ProcessHandle handle,
        CancellationToken cancellationToken = default)
    {
        var initial = Resolve(handle);
        var owner = initial.IsSuccess
            ? initial.Value!.OwnerWorkId
            : null;

        return _invocations.RunAsync(
            "process.release",
            workId: null,
            async (context, token) =>
            {
                token.ThrowIfCancellationRequested();

                var closed = await _resources.CloseIfAsync<IProcessResource>(
                        handle.AsResourceHandle(),
                        ResourceKind,
                        resource =>
                        {
                            var status = resource.Snapshot(handle);
                            if (IsTerminal(status.State))
                            {
                                return LoomResult<Unit>.Success(Unit.Value);
                            }

                            return LoomResult<Unit>.Failure(
                                LoomErrors.Conflict(
                                    $"Cannot release process '{handle}' while its state is '{status.State.ToString().ToLowerInvariant()}'. " +
                                    "process_release only discards retained state after the root process has exited or been terminated; " +
                                    "use process_terminate first if the process should be stopped."));
                        })
                    .ConfigureAwait(false);

                if (!closed.IsSuccess)
                {
                    return LoomResult<Unit>.Failure(closed.Error!);
                }

                if (closed.Value == true)
                {
                    _events.Publish(
                        "ResourceClosed",
                        "process",
                        owner,
                        context.Id,
                        handle.AsResourceHandle(),
                        new Dictionary<string, object?>
                        {
                            ["kind"] = ResourceKind,
                            ["reason"] = "explicit_release"
                        });
                }

                return LoomResult<Unit>.Success(Unit.Value);
            },
            cancellationToken);
    }

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

    private static LoomResult<ProcessLaunchSpec> BuildLaunchSpec(
        string executable,
        IReadOnlyList<string>? arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        ProcessIoMode ioMode,
        int? terminalColumns,
        int? terminalRows,
        string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return LoomResult<ProcessLaunchSpec>.Failure(
                LoomErrors.InvalidArgument("Executable is required."));
        }

        if (ioMode is not (ProcessIoMode.Pipes or ProcessIoMode.Terminal))
        {
            return LoomResult<ProcessLaunchSpec>.Failure(
                LoomErrors.InvalidArgument("Unsupported process I/O mode."));
        }

        int? normalizedColumns = null;
        int? normalizedRows = null;

        if (ioMode == ProcessIoMode.Pipes)
        {
            if (terminalColumns is not null || terminalRows is not null)
            {
                return LoomResult<ProcessLaunchSpec>.Failure(
                    LoomErrors.InvalidArgument(
                        "Terminal dimensions are only valid when io_mode is terminal."));
            }
        }
        else
        {
            normalizedColumns = terminalColumns ?? 80;
            normalizedRows = terminalRows ?? 24;

            if (normalizedColumns is < 1 or > short.MaxValue ||
                normalizedRows is < 1 or > short.MaxValue)
            {
                return LoomResult<ProcessLaunchSpec>.Failure(
                    LoomErrors.InvalidArgument(
                        $"Terminal dimensions must be between 1 and {short.MaxValue}."));
            }
        }

        var workingDirectoryResult = ResolveWorkingDirectory(
            workingDirectory,
            baseDirectory);
        if (!workingDirectoryResult.IsSuccess)
        {
            return LoomResult<ProcessLaunchSpec>.Failure(
                workingDirectoryResult.Error!);
        }

        return LoomResult<ProcessLaunchSpec>.Success(
            new ProcessLaunchSpec(
                executable,
                arguments ?? Array.Empty<string>(),
                workingDirectoryResult.Value,
                environment ?? new Dictionary<string, string?>(),
                ioMode,
                normalizedColumns,
                normalizedRows));
    }

    private static string FlattenOutput(OutputStreamReadResult? stream)
        => stream is null
            ? string.Empty
            : string.Concat(stream.Chunks.Select(chunk => chunk.Text));

    private static bool IsRunOutputTruncated(OutputStreamReadResult? stream)
        => stream is not null &&
           (stream.Truncated ||
            stream.RetentionLimitReached ||
            stream.NextCursor < stream.ObservedUntilCursor);

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
