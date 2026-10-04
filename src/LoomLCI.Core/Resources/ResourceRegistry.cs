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
        if (!_entries.TryGetValue(handle.Value, out var entry))
        {
            return LoomResult<ResourceLease<T>>.Failure(
                LoomErrors.NotFound($"Resource '{handle}' was not found."));
        }

        lock (entry.Sync)
        {
            if (!string.Equals(entry.Kind, expectedKind, StringComparison.Ordinal))
            {
                return LoomResult<ResourceLease<T>>.Failure(
                    LoomErrors.ResourceTypeMismatch(handle.Value, expectedKind));
            }

            if (entry.State == ResourceState.Expired ||
                (entry.State == ResourceState.Closing && entry.ClosingTarget == ResourceState.Expired))
            {
                return LoomResult<ResourceLease<T>>.Failure(LoomErrors.ResourceExpired(handle.Value));
            }

            if (entry.State != ResourceState.Active || entry.Resource is null)
            {
                return LoomResult<ResourceLease<T>>.Failure(LoomErrors.ResourceClosed(handle.Value));
            }

            if (entry.Resource is not T typed)
            {
                return LoomResult<ResourceLease<T>>.Failure(
                    LoomErrors.Internal("Resource registry type invariant was violated."));
            }

            entry.LastAccessedAt = _timeProvider.GetUtcNow();

            return LoomResult<ResourceLease<T>>.Success(
                new ResourceLease<T>(handle, typed, entry.OwnerWorkId, entry.Ownership));
        }
    }

    public ValueTask<LoomResult<Unit>> CloseAsync(ResourceHandle handle)
        => TransitionAsync(handle, ResourceState.Closed);

    internal ValueTask<LoomResult<Unit>> ExpireAsync(ResourceHandle handle)
        => TransitionAsync(handle, ResourceState.Expired);

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
            lock (entry.Sync)
            {
                if (entry.State is ResourceState.Closed or ResourceState.Expired)
                {
                    return LoomResult<Unit>.Success(Unit.Value);
                }

                entry.State = ResourceState.Closing;
                entry.ClosingTarget = terminalState;
                entry.StateChangedAt = _timeProvider.GetUtcNow();
                disposer = entry.Disposer;
            }

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
        finally
        {
            entry.Gate.Release();
        }
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
