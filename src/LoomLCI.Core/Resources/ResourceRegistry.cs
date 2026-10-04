using System.Collections.Concurrent;

namespace LoomLCI.Core.Resources;

public enum ResourceOwnership
{
    SessionOwned,
    Independent
}

public enum ResourceState
{
    Active,
    Closing,
    Closed,
    Expired
}

public sealed record ResourceLease<T>(
    ResourceHandle Handle,
    T Resource,
    WorkId? OwnerWorkId,
    ResourceOwnership Ownership)
    where T : class;

public sealed class ResourceOperationLease<T> : IDisposable
    where T : class
{
    private Action? _release;

    internal ResourceOperationLease(
        ResourceHandle handle,
        T resource,
        WorkId? ownerWorkId,
        ResourceOwnership ownership,
        DateTimeOffset lastAccessedAt,
        Action release)
    {
        Handle = handle;
        Resource = resource;
        OwnerWorkId = ownerWorkId;
        Ownership = ownership;
        LastAccessedAt = lastAccessedAt;
        _release = release;
    }

    public ResourceHandle Handle { get; }
    public T Resource { get; }
    public WorkId? OwnerWorkId { get; }
    public ResourceOwnership Ownership { get; }
    public DateTimeOffset LastAccessedAt { get; }

    public void Dispose()
        => Interlocked.Exchange(ref _release, null)?.Invoke();
}

public sealed class ResourceRegistry : IAsyncDisposable
{
    private sealed class Entry(
        ResourceHandle handle,
        string kind,
        object resource,
        WorkId? ownerWorkId,
        ResourceOwnership ownership,
        Func<ValueTask> disposer,
        DateTimeOffset now)
    {
        public object Sync { get; } = new();
        public ResourceHandle Handle { get; } = handle;
        public string Kind { get; } = kind;
        public object? Resource { get; set; } = resource;
        public WorkId? OwnerWorkId { get; } = ownerWorkId;
        public ResourceOwnership Ownership { get; } = ownership;
        public Func<ValueTask>? Disposer { get; set; } = disposer;
        public ResourceState State { get; set; } = ResourceState.Active;
        public ResourceState? ClosingTarget { get; set; }
        public DateTimeOffset CreatedAt { get; } = now;
        public DateTimeOffset LastAccessedAt { get; set; } = now;
        public DateTimeOffset StateChangedAt { get; set; } = now;
        public int ActiveOperations { get; set; }
        public TaskCompletionSource? OperationsDrained { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public ResourceRegistry(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int ActiveCount => _entries.Values.Count(entry => entry.State == ResourceState.Active);
    public int TombstoneCount => _entries.Values.Count(
        entry => entry.State is ResourceState.Closed or ResourceState.Expired);
    public int Count => _entries.Count;

    public ResourceHandle Register<T>(
        string prefix,
        string kind,
        T resource,
        WorkId? ownerWorkId,
        ResourceOwnership ownership,
        Func<ValueTask> disposer)
        where T : class
    {
        var handle = ResourceHandle.Create(prefix);
        var now = _timeProvider.GetUtcNow();
        var entry = new Entry(handle, kind, resource, ownerWorkId, ownership, disposer, now);

        if (!_entries.TryAdd(handle.Value, entry))
        {
            throw new InvalidOperationException("Handle collision.");
        }

        return handle;
    }

    public LoomResult<ResourceLease<T>> Resolve<T>(ResourceHandle handle, string expectedKind)
        where T : class
    {
        var resolved = TryResolveEntry<T>(handle, expectedKind);
        if (!resolved.IsSuccess)
        {
            return LoomResult<ResourceLease<T>>.Failure(resolved.Error!);
        }

        var value = resolved.Value!;
        return LoomResult<ResourceLease<T>>.Success(
            new ResourceLease<T>(
                handle,
                value.Resource,
                value.Entry.OwnerWorkId,
                value.Entry.Ownership));
    }

    public LoomResult<ResourceOperationLease<T>> Acquire<T>(
        ResourceHandle handle,
        string expectedKind)
        where T : class
    {
        if (!_entries.TryGetValue(handle.Value, out var entry))
        {
            return LoomResult<ResourceOperationLease<T>>.Failure(
                LoomErrors.NotFound($"Resource '{handle}' was not found."));
        }

        lock (entry.Sync)
        {
            var validation = ValidateEntry<T>(entry, handle, expectedKind);
            if (!validation.IsSuccess)
            {
                return LoomResult<ResourceOperationLease<T>>.Failure(validation.Error!);
            }

            entry.ActiveOperations++;
            var resource = validation.Value!;

            return LoomResult<ResourceOperationLease<T>>.Success(
                new ResourceOperationLease<T>(
                    handle,
                    resource,
                    entry.OwnerWorkId,
                    entry.Ownership,
                    entry.LastAccessedAt,
                    () => ReleaseOperation(entry)));
        }
    }

    public DateTimeOffset? Touch(ResourceHandle handle, string expectedKind)
    {
        if (!_entries.TryGetValue(handle.Value, out var entry))
        {
            return null;
        }

        lock (entry.Sync)
        {
            if (entry.State != ResourceState.Active ||
                !string.Equals(entry.Kind, expectedKind, StringComparison.Ordinal))
            {
                return null;
            }

            var now = _timeProvider.GetUtcNow();
            entry.LastAccessedAt = now;
            return now;
        }
    }

    public IReadOnlyList<ResourceHandle> GetActiveHandles(string kind)
    {
        var handles = new List<ResourceHandle>();

        foreach (var entry in _entries.Values)
        {
            lock (entry.Sync)
            {
                if (entry.State == ResourceState.Active &&
                    string.Equals(entry.Kind, kind, StringComparison.Ordinal))
                {
                    handles.Add(entry.Handle);
                }
            }
        }

        return handles;
    }

    public IReadOnlyList<ResourceHandle> GetActiveOwnedHandles(
        string kind,
        WorkId ownerWorkId)
    {
        var handles = new List<ResourceHandle>();

        foreach (var entry in _entries.Values)
        {
            lock (entry.Sync)
            {
                if (entry.State == ResourceState.Active &&
                    entry.OwnerWorkId == ownerWorkId &&
                    string.Equals(entry.Kind, kind, StringComparison.Ordinal))
                {
                    handles.Add(entry.Handle);
                }
            }
        }

        return handles;
    }

    public ValueTask<LoomResult<Unit>> CloseAsync(ResourceHandle handle)
        => TransitionAsync(handle, ResourceState.Closed);

    public async ValueTask<LoomResult<bool>> CloseIfAsync<T>(
        ResourceHandle handle,
        string expectedKind,
        Func<T, LoomResult<Unit>> canClose)
        where T : class
    {
        if (!_entries.TryGetValue(handle.Value, out var entry))
        {
            return LoomResult<bool>.Failure(
                LoomErrors.NotFound($"Resource '{handle}' was not found."));
        }

        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            T resource;

            lock (entry.Sync)
            {
                if (!string.Equals(entry.Kind, expectedKind, StringComparison.Ordinal))
                {
                    return LoomResult<bool>.Failure(
                        LoomErrors.ResourceTypeMismatch(handle.Value, expectedKind));
                }

                if (entry.State is ResourceState.Closed or ResourceState.Expired)
                {
                    return LoomResult<bool>.Success(false);
                }

                if (entry.State != ResourceState.Active ||
                    entry.Resource is not T typed)
                {
                    return LoomResult<bool>.Failure(
                        LoomErrors.Internal(
                            "Resource registry type invariant was violated."));
                }

                resource = typed;
            }

            var allowed = canClose(resource);
            if (!allowed.IsSuccess)
            {
                return LoomResult<bool>.Failure(allowed.Error!);
            }

            Func<ValueTask>? disposer;
            Task operationsDrained;

            lock (entry.Sync)
            {
                if (entry.State is ResourceState.Closed or ResourceState.Expired)
                {
                    return LoomResult<bool>.Success(false);
                }

                if (entry.State != ResourceState.Active ||
                    !ReferenceEquals(entry.Resource, resource))
                {
                    return LoomResult<bool>.Failure(
                        LoomErrors.Internal(
                            "Resource registry state changed unexpectedly during conditional close."));
                }

                BeginClosingLocked(entry, ResourceState.Closed);
                disposer = entry.Disposer;
                operationsDrained = GetOperationsDrainedTaskLocked(entry);
            }

            await operationsDrained.ConfigureAwait(false);
            var disposed = await DisposeEntryAsync(
                    entry,
                    ResourceState.Closed,
                    disposer)
                .ConfigureAwait(false);

            return disposed.IsSuccess
                ? LoomResult<bool>.Success(true)
                : LoomResult<bool>.Failure(disposed.Error!);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    internal ValueTask<LoomResult<Unit>> ExpireAsync(ResourceHandle handle)
        => TransitionAsync(handle, ResourceState.Expired);

    public async ValueTask<LoomResult<bool>> ExpireIfUnaccessedSinceAsync(
        ResourceHandle handle,
        string expectedKind,
        DateTimeOffset cutoff)
    {
        if (!_entries.TryGetValue(handle.Value, out var entry))
        {
            return LoomResult<bool>.Failure(
                LoomErrors.NotFound($"Resource '{handle}' was not found."));
        }

        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Func<ValueTask>? disposer;

            lock (entry.Sync)
            {
                if (!string.Equals(entry.Kind, expectedKind, StringComparison.Ordinal))
                {
                    return LoomResult<bool>.Failure(
                        LoomErrors.ResourceTypeMismatch(handle.Value, expectedKind));
                }

                if (entry.State != ResourceState.Active ||
                    entry.ActiveOperations != 0 ||
                    entry.LastAccessedAt > cutoff)
                {
                    return LoomResult<bool>.Success(false);
                }

                BeginClosingLocked(entry, ResourceState.Expired);
                disposer = entry.Disposer;
            }

            var disposed = await DisposeEntryAsync(
                    entry,
                    ResourceState.Expired,
                    disposer)
                .ConfigureAwait(false);

            return disposed.IsSuccess
                ? LoomResult<bool>.Success(true)
                : LoomResult<bool>.Failure(disposed.Error!);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public Task CloseOwnedAsync(WorkId workId)
        => TransitionOwnedAsync(workId, ResourceState.Closed);

    public Task ExpireOwnedAsync(WorkId workId)
        => TransitionOwnedAsync(workId, ResourceState.Expired);

    public int PruneTombstones(TimeSpan retention)
    {
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), retention, "Retention must be positive.");
        }

        var now = _timeProvider.GetUtcNow();
        var pruned = 0;

        foreach (var entry in _entries.Values)
        {
            var shouldRemove = false;
            lock (entry.Sync)
            {
                shouldRemove =
                    entry.State is ResourceState.Closed or ResourceState.Expired &&
                    now - entry.StateChangedAt >= retention;
            }

            if (shouldRemove && _entries.TryRemove(entry.Handle.Value, out _))
            {
                pruned++;
            }
        }

        return pruned;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var handle in _entries.Values
                     .Where(entry => entry.State is ResourceState.Active or ResourceState.Closing)
                     .Select(entry => entry.Handle)
                     .ToArray())
        {
            await CloseAsync(handle).ConfigureAwait(false);
        }

        _entries.Clear();
    }

    private LoomResult<(Entry Entry, T Resource)> TryResolveEntry<T>(
        ResourceHandle handle,
        string expectedKind)
        where T : class
    {
        if (!_entries.TryGetValue(handle.Value, out var entry))
        {
            return LoomResult<(Entry, T)>.Failure(
                LoomErrors.NotFound($"Resource '{handle}' was not found."));
        }

        lock (entry.Sync)
        {
            var validation = ValidateEntry<T>(entry, handle, expectedKind);
            return validation.IsSuccess
                ? LoomResult<(Entry, T)>.Success((entry, validation.Value!))
                : LoomResult<(Entry, T)>.Failure(validation.Error!);
        }
    }

    private static LoomResult<T> ValidateEntry<T>(
        Entry entry,
        ResourceHandle handle,
        string expectedKind)
        where T : class
    {
        if (!string.Equals(entry.Kind, expectedKind, StringComparison.Ordinal))
        {
            return LoomResult<T>.Failure(
                LoomErrors.ResourceTypeMismatch(handle.Value, expectedKind));
        }

        if (entry.State == ResourceState.Expired ||
            (entry.State == ResourceState.Closing && entry.ClosingTarget == ResourceState.Expired))
        {
            return LoomResult<T>.Failure(LoomErrors.ResourceExpired(handle.Value));
        }

        if (entry.State != ResourceState.Active || entry.Resource is null)
        {
            return LoomResult<T>.Failure(LoomErrors.ResourceClosed(handle.Value));
        }

        if (entry.Resource is not T typed)
        {
            return LoomResult<T>.Failure(
                LoomErrors.Internal("Resource registry type invariant was violated."));
        }

        return LoomResult<T>.Success(typed);
    }

    private async ValueTask<LoomResult<Unit>> TransitionAsync(
        ResourceHandle handle,
        ResourceState terminalState)
    {
        if (terminalState is not (ResourceState.Closed or ResourceState.Expired))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalState));
        }

        if (!_entries.TryGetValue(handle.Value, out var entry))
        {
            return LoomResult<Unit>.Failure(
                LoomErrors.NotFound($"Resource '{handle}' was not found."));
        }

        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Func<ValueTask>? disposer;
            Task operationsDrained;

            lock (entry.Sync)
            {
                if (entry.State is ResourceState.Closed or ResourceState.Expired)
                {
                    return LoomResult<Unit>.Success(Unit.Value);
                }

                BeginClosingLocked(entry, terminalState);
                disposer = entry.Disposer;
                operationsDrained = GetOperationsDrainedTaskLocked(entry);
            }

            await operationsDrained.ConfigureAwait(false);
            return await DisposeEntryAsync(entry, terminalState, disposer)
                .ConfigureAwait(false);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private static void BeginClosingLocked(Entry entry, ResourceState terminalState)
    {
        entry.State = ResourceState.Closing;
        entry.ClosingTarget = terminalState;
    }

    private static Task GetOperationsDrainedTaskLocked(Entry entry)
    {
        if (entry.ActiveOperations == 0)
        {
            return Task.CompletedTask;
        }

        entry.OperationsDrained ??= new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return entry.OperationsDrained.Task;
    }

    private async ValueTask<LoomResult<Unit>> DisposeEntryAsync(
        Entry entry,
        ResourceState terminalState,
        Func<ValueTask>? disposer)
    {
        try
        {
            if (disposer is not null)
            {
                await disposer().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            lock (entry.Sync)
            {
                entry.State = ResourceState.Active;
                entry.ClosingTarget = null;
                entry.StateChangedAt = _timeProvider.GetUtcNow();
            }

            return LoomResult<Unit>.Failure(
                LoomErrors.Internal($"Resource cleanup failed: {ex.Message}"));
        }

        lock (entry.Sync)
        {
            entry.Resource = null;
            entry.Disposer = null;
            entry.State = terminalState;
            entry.ClosingTarget = null;
            entry.StateChangedAt = _timeProvider.GetUtcNow();
        }

        return LoomResult<Unit>.Success(Unit.Value);
    }

    private static void ReleaseOperation(Entry entry)
    {
        TaskCompletionSource? drained = null;

        lock (entry.Sync)
        {
            if (entry.ActiveOperations <= 0)
            {
                throw new InvalidOperationException("Resource operation lease underflow.");
            }

            entry.ActiveOperations--;
            if (entry.ActiveOperations == 0 && entry.OperationsDrained is not null)
            {
                drained = entry.OperationsDrained;
                entry.OperationsDrained = null;
            }
        }

        drained?.TrySetResult();
    }

    private async Task TransitionOwnedAsync(WorkId workId, ResourceState terminalState)
    {
        var owned = _entries.Values
            .Where(entry =>
                entry.OwnerWorkId == workId &&
                entry.Ownership == ResourceOwnership.SessionOwned)
            .Select(entry => entry.Handle)
            .ToArray();

        foreach (var handle in owned)
        {
            await TransitionAsync(handle, terminalState).ConfigureAwait(false);
        }
    }
}
