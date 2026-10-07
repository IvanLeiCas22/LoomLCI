using System.Text;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;

namespace LoomLCI.Core.Python;

public sealed class PythonCapability
{
    public const string ResourceKind = "python_worker";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(10);
    public const int DefaultMaxOutputChars = 65_536;
    public const int MaxOutputChars = 1_048_576;
    public const int MaxCodeUtf8Bytes = 256 * 1024;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IPythonRuntimeProvider _provider;
    private readonly ResourceRegistry _resources;
    private readonly InvocationRunner _invocations;
    private readonly LoomEventBus _events;
    private readonly SemaphoreSlim _workerGate = new(1, 1);

    public PythonCapability(
        IPythonRuntimeProvider provider,
        ResourceRegistry resources,
        InvocationRunner invocations,
        LoomEventBus events)
    {
        _provider = provider;
        _resources = resources;
        _invocations = invocations;
        _events = events;
    }

    public Task<LoomResult<PythonExecutionResult>> ExecuteAsync(
        PythonExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.WorkId.Value))
        {
            return Task.FromResult(
                LoomResult<PythonExecutionResult>.Failure(
                    LoomErrors.InvalidArgument("work_id is required.")));
        }

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return Task.FromResult(
                LoomResult<PythonExecutionResult>.Failure(
                    LoomErrors.InvalidArgument("Python code is required.")));
        }

        int codeBytes;
        try
        {
            codeBytes = StrictUtf8.GetByteCount(request.Code);
        }
        catch (EncoderFallbackException)
        {
            return Task.FromResult(
                LoomResult<PythonExecutionResult>.Failure(
                    LoomErrors.InvalidArgument(
                        "Python code must contain valid Unicode.")));
        }

        if (codeBytes > MaxCodeUtf8Bytes)
        {
            return Task.FromResult(
                LoomResult<PythonExecutionResult>.Failure(
                    LoomErrors.InvalidArgument(
                        $"Python code must be no more than {MaxCodeUtf8Bytes} UTF-8 bytes.")));
        }

        if (request.Timeout <= TimeSpan.Zero || request.Timeout > MaxTimeout)
        {
            return Task.FromResult(
                LoomResult<PythonExecutionResult>.Failure(
                    LoomErrors.InvalidArgument(
                        $"Python timeout must be greater than zero and no more than {MaxTimeout.TotalSeconds:0} seconds.")));
        }

        if (request.MaxOutputChars is < 1 or > MaxOutputChars)
        {
            return Task.FromResult(
                LoomResult<PythonExecutionResult>.Failure(
                    LoomErrors.InvalidArgument(
                        $"max_output_chars must be between 1 and {MaxOutputChars}.")));
        }

        return _invocations.RunAsync(
            "python.execute",
            request.WorkId,
            async (context, token) =>
            {
                var workerHandle = await GetOrCreateWorkerAsync(
                        context.WorkSession!,
                        token)
                    .ConfigureAwait(false);
                if (!workerHandle.IsSuccess)
                {
                    return LoomResult<PythonExecutionResult>.Failure(
                        workerHandle.Error!);
                }

                var acquired = _resources.Acquire<IPythonWorkerResource>(
                    workerHandle.Value,
                    ResourceKind);
                if (!acquired.IsSuccess)
                {
                    return LoomResult<PythonExecutionResult>.Failure(
                        acquired.Error!);
                }

                var lease = acquired.Value!;
                var discard = false;

                try
                {
                    if (!lease.Resource.IsHealthy)
                    {
                        discard = true;
                        return LoomResult<PythonExecutionResult>.Failure(
                            LoomErrors.ExecutionFailed(
                                "Python worker is not healthy."));
                    }

                    var desiredEnvironment =
                        context.WorkSession!.GetPythonPackageEnvironment();
                    if (!desiredEnvironment.IsSuccess)
                    {
                        return LoomResult<PythonExecutionResult>.Failure(
                            desiredEnvironment.Error!);
                    }

                    if (!WorkerMatchesEnvironment(
                            lease.Resource,
                            desiredEnvironment.Value))
                    {
                        return LoomResult<PythonExecutionResult>.Failure(
                            PackageEnvironmentConflict());
                    }

                    var result = await lease.Resource.ExecuteAsync(
                            new PythonWorkerExecuteSpec(
                                request.Code,
                                request.MaxOutputChars),
                            token)
                        .ConfigureAwait(false);

                    discard =
                        token.IsCancellationRequested ||
                        !lease.Resource.IsHealthy;

                    return result;
                }
                catch
                {
                    discard = true;
                    throw;
                }
                finally
                {
                    discard =
                        discard ||
                        token.IsCancellationRequested ||
                        !lease.Resource.IsHealthy;

                    lease.Dispose();

                    if (discard)
                    {
                        await DiscardWorkerAsync(
                                workerHandle.Value,
                                context,
                                "unhealthy_or_cancelled")
                            .ConfigureAwait(false);
                    }
                }
            },
            cancellationToken,
            request.Timeout);
    }

    public Task<LoomResult<Unit>> ResetAsync(
        WorkId workId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workId.Value))
        {
            return Task.FromResult(
                LoomResult<Unit>.Failure(
                    LoomErrors.InvalidArgument("work_id is required.")));
        }

        return _invocations.RunAsync(
            "python.reset",
            workId,
            async (context, token) =>
            {
                await _workerGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var handles = _resources.GetActiveOwnedHandles(
                        ResourceKind,
                        workId);

                    if (handles.Count > 1)
                    {
                        return LoomResult<Unit>.Failure(
                            DuplicateWorkerError(workId, handles.Count));
                    }

                    if (handles.Count == 0)
                    {
                        return LoomResult<Unit>.Success(Unit.Value);
                    }

                    var closed = await _resources.CloseAsync(handles[0])
                        .ConfigureAwait(false);
                    if (!closed.IsSuccess)
                    {
                        return LoomResult<Unit>.Failure(closed.Error!);
                    }

                    _events.Publish(
                        "ResourceClosed",
                        "python",
                        workId,
                        context.Id,
                        handles[0],
                        new Dictionary<string, object?>
                        {
                            ["kind"] = ResourceKind,
                            ["reason"] = "explicit_reset"
                        });

                    return LoomResult<Unit>.Success(Unit.Value);
                }
                finally
                {
                    _workerGate.Release();
                }
            },
            cancellationToken);
    }

    private async Task<LoomResult<ResourceHandle>> GetOrCreateWorkerAsync(
        WorkSession session,
        CancellationToken cancellationToken)
    {
        var desired = session.GetPythonPackageEnvironment();
        if (!desired.IsSuccess)
        {
            return LoomResult<ResourceHandle>.Failure(
                desired.Error!);
        }

        var desiredEnvironment = desired.Value;

        var existing = ResolveSingleActiveWorker(session.Id);
        if (!existing.IsSuccess)
        {
            return LoomResult<ResourceHandle>.Failure(existing.Error!);
        }

        if (existing.Value is { } existingHandle)
        {
            var resolved = _resources.Resolve<IPythonWorkerResource>(
                existingHandle,
                ResourceKind);

            if (resolved.IsSuccess && resolved.Value!.Resource.IsHealthy)
            {
                return WorkerMatchesEnvironment(
                        resolved.Value.Resource,
                        desiredEnvironment)
                    ? LoomResult<ResourceHandle>.Success(existingHandle)
                    : LoomResult<ResourceHandle>.Failure(
                        PackageEnvironmentConflict());
            }
        }

        await _workerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            desired = session.GetPythonPackageEnvironment();
            if (!desired.IsSuccess)
            {
                return LoomResult<ResourceHandle>.Failure(
                    desired.Error!);
            }

            desiredEnvironment = desired.Value;

            existing = ResolveSingleActiveWorker(session.Id);
            if (!existing.IsSuccess)
            {
                return LoomResult<ResourceHandle>.Failure(existing.Error!);
            }

            if (existing.Value is { } currentHandle)
            {
                var resolved = _resources.Resolve<IPythonWorkerResource>(
                    currentHandle,
                    ResourceKind);

                if (resolved.IsSuccess && resolved.Value!.Resource.IsHealthy)
                {
                    return WorkerMatchesEnvironment(
                            resolved.Value.Resource,
                            desiredEnvironment)
                        ? LoomResult<ResourceHandle>.Success(currentHandle)
                        : LoomResult<ResourceHandle>.Failure(
                            PackageEnvironmentConflict());
                }

                var closed = await _resources.CloseAsync(currentHandle)
                    .ConfigureAwait(false);
                if (!closed.IsSuccess)
                {
                    return LoomResult<ResourceHandle>.Failure(closed.Error!);
                }
            }

            var started = await _provider.StartAsync(
                    new PythonWorkerStartSpec(
                        session.Id,
                        session.BaseDirectory,
                        desiredEnvironment),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!started.IsSuccess)
            {
                return LoomResult<ResourceHandle>.Failure(started.Error!);
            }

            var worker = started.Value!;
            if (cancellationToken.IsCancellationRequested)
            {
                await worker.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!worker.IsHealthy)
            {
                await worker.DisposeAsync().ConfigureAwait(false);
                return LoomResult<ResourceHandle>.Failure(
                    LoomErrors.ExecutionFailed(
                        "Python provider returned an unhealthy worker."));
            }

            if (!WorkerMatchesEnvironment(
                    worker,
                    desiredEnvironment))
            {
                await worker.DisposeAsync().ConfigureAwait(false);
                return LoomResult<ResourceHandle>.Failure(
                    LoomErrors.ExecutionFailed(
                        "Python provider returned a worker with the wrong package environment."));
            }

            ResourceHandle handle;
            try
            {
                handle = _resources.Register(
                    "pyw",
                    ResourceKind,
                    worker,
                    session.Id,
                    ResourceOwnership.SessionOwned,
                    worker.DisposeAsync);
            }
            catch
            {
                await worker.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await _resources.CloseAsync(handle).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            _events.Publish(
                "ResourceCreated",
                "python",
                session.Id,
                resourceHandle: handle,
                payload: new Dictionary<string, object?>
                {
                    ["kind"] = ResourceKind,
                    ["packageEnvironmentId"] =
                        desiredEnvironment?.EnvironmentId
                });

            return LoomResult<ResourceHandle>.Success(handle);
        }
        finally
        {
            _workerGate.Release();
        }
    }

    private LoomResult<ResourceHandle?> ResolveSingleActiveWorker(
        WorkId workId)
    {
        var handles = _resources.GetActiveOwnedHandles(
            ResourceKind,
            workId);

        if (handles.Count > 1)
        {
            return LoomResult<ResourceHandle?>.Failure(
                DuplicateWorkerError(workId, handles.Count));
        }

        return LoomResult<ResourceHandle?>.Success(
            handles.Count == 0 ? null : handles[0]);
    }

    private async Task DiscardWorkerAsync(
        ResourceHandle handle,
        InvocationContext context,
        string reason)
    {
        var closed = await _resources.CloseAsync(handle)
            .ConfigureAwait(false);
        if (!closed.IsSuccess)
        {
            return;
        }

        _events.Publish(
            "ResourceClosed",
            "python",
            context.WorkSession?.Id,
            context.Id,
            handle,
            new Dictionary<string, object?>
            {
                ["kind"] = ResourceKind,
                ["reason"] = reason
            });
    }

    private static bool WorkerMatchesEnvironment(
        IPythonWorkerResource worker,
        PythonPackageEnvironment? desiredEnvironment)
        => string.Equals(
            worker.PackageEnvironmentId,
            desiredEnvironment?.EnvironmentId,
            StringComparison.Ordinal);

    private static LoomError PackageEnvironmentConflict()
        => LoomErrors.Conflict(
            "Python package environment changed while the current worker is alive. Call python_reset before python_execute.");

    private static LoomError DuplicateWorkerError(
        WorkId workId,
        int count)
        => LoomErrors.Internal(
            $"Work session '{workId}' has {count} active Python workers; expected at most one.");
}
