using System.Collections.Concurrent;

namespace LoomLCI.Core.Resources;

public enum ResourceOwnership
{
    SessionOwned,
    Independent
}

public sealed record ResourceLease<T>(
    ResourceHandle Handle,
    T Resource,
    WorkId? OwnerWorkId,
    ResourceOwnership Ownership)
    where T : class;

public sealed class ResourceRegistry
{
    private sealed class Entry(
        ResourceHandle handle,
        string kind,
        object resource,
        WorkId? ownerWorkId,
        ResourceOwnership ownership,
        Func<ValueTask> disposer)
    {
        public ResourceHandle Handle { get; } = handle;
        public string Kind { get; } = kind;
        public object? Resource { get; set; } = resource;
        public WorkId? OwnerWorkId { get; } = ownerWorkId;
        public ResourceOwnership Ownership { get; } = ownership;
        public Func<ValueTask> Disposer { get; } = disposer;
        public bool Closed { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public int ActiveCount => _entries.Values.Count(entry => !entry.Closed);

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
        var entry = new Entry(handle, kind, resource, ownerWorkId, ownership, disposer);

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
            return LoomResult<ResourceLease<T>>.Failure(LoomErrors.NotFound($"Resource '{handle}' was not found."));
        }

        if (!string.Equals(entry.Kind, expectedKind, StringComparison.Ordinal))
        {
            return LoomResult<ResourceLease<T>>.Failure(LoomErrors.ResourceTypeMismatch(handle.Value, expectedKind));
        }

        if (entry.Closed || entry.Resource is null)
        {
            return LoomResult<ResourceLease<T>>.Failure(LoomErrors.ResourceClosed(handle.Value));
        }

        if (entry.Resource is not T typed)
        {
            return LoomResult<ResourceLease<T>>.Failure(LoomErrors.Internal("Resource registry type invariant was violated."));
        }

        return LoomResult<ResourceLease<T>>.Success(
            new ResourceLease<T>(handle, typed, entry.OwnerWorkId, entry.Ownership));
    }

    public async ValueTask<LoomResult<Unit>> CloseAsync(ResourceHandle handle)
    {
        if (!_entries.TryGetValue(handle.Value, out var entry))
        {
            return LoomResult<Unit>.Failure(LoomErrors.NotFound($"Resource '{handle}' was not found."));
        }

        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (entry.Closed)
            {
                return LoomResult<Unit>.Success(Unit.Value);
            }

            await entry.Disposer().ConfigureAwait(false);
            entry.Resource = null;
            entry.Closed = true;
            return LoomResult<Unit>.Success(Unit.Value);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async ValueTask CloseOwnedAsync(WorkId workId)
    {
        var owned = _entries.Values
            .Where(e => e.OwnerWorkId == workId && e.Ownership == ResourceOwnership.SessionOwned)
            .Select(e => e.Handle)
            .ToArray();

        foreach (var handle in owned)
        {
            await CloseAsync(handle).ConfigureAwait(false);
        }
    }
}
