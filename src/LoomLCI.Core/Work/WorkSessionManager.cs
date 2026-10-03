using System.Collections.Concurrent;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Resources;

namespace LoomLCI.Core.Work;

public enum WorkSessionState
{
    Active,
    Closing,
    Closed
}

public sealed class WorkSession
{
    internal WorkSession(WorkId id, string? baseDirectory, string? label)
    {
        Id = id;
        BaseDirectory = baseDirectory;
        Label = label;
        CreatedAt = DateTimeOffset.UtcNow;
        LastActivityAt = CreatedAt;
    }

    private readonly CancellationTokenSource _lifetime = new();

    internal SemaphoreSlim CloseGate { get; } = new(1, 1);

    public WorkId Id { get; }
    public string? BaseDirectory { get; }
    public string? Label { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastActivityAt { get; private set; }
    public WorkSessionState State { get; private set; } = WorkSessionState.Active;
    public CancellationToken CancellationToken => _lifetime.Token;

    internal bool TryTouch()
    {
        if (State != WorkSessionState.Active)
        {
            return false;
        }

        LastActivityAt = DateTimeOffset.UtcNow;
        return true;
    }

    internal void BeginClose()
    {
        if (State != WorkSessionState.Active)
        {
            return;
        }

        State = WorkSessionState.Closing;
        _lifetime.Cancel();
    }

    internal void CompleteClose()
    {
        State = WorkSessionState.Closed;
        _lifetime.Dispose();
    }
}

public sealed class WorkSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, WorkSession> _sessions = new(StringComparer.Ordinal);
    private readonly ResourceRegistry _resources;
    private readonly LoomEventBus _events;

    public WorkSessionManager(ResourceRegistry resources, LoomEventBus events)
    {
        _resources = resources;
        _events = events;
    }

    public LoomResult<WorkSession> Create(string? baseDirectory = null, string? label = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(baseDirectory))
            {
                baseDirectory = Path.GetFullPath(baseDirectory);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return LoomResult<WorkSession>.Failure(LoomErrors.InvalidArgument($"Invalid base directory: {ex.Message}"));
        }

        var session = new WorkSession(WorkId.Create(), baseDirectory, label);

        if (!_sessions.TryAdd(session.Id.Value, session))
        {
            return LoomResult<WorkSession>.Failure(LoomErrors.Internal("Could not register work session."));
        }

        _events.Publish("WorkSessionCreated", "core.work", workId: session.Id);
        return LoomResult<WorkSession>.Success(session);
    }

    public LoomResult<WorkSession> Resolve(WorkId workId)
    {
        if (!_sessions.TryGetValue(workId.Value, out var session))
        {
            return LoomResult<WorkSession>.Failure(LoomErrors.NotFound($"Work session '{workId}' was not found."));
        }

        if (session.State != WorkSessionState.Active)
        {
            return LoomResult<WorkSession>.Failure(LoomErrors.ResourceClosed(workId.Value));
        }

        session.TryTouch();
        return LoomResult<WorkSession>.Success(session);
    }

    public async ValueTask<LoomResult<Unit>> CloseAsync(WorkId workId)
    {
        if (!_sessions.TryGetValue(workId.Value, out var session))
        {
            return LoomResult<Unit>.Failure(LoomErrors.NotFound($"Work session '{workId}' was not found."));
        }

        await session.CloseGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session.State == WorkSessionState.Closed)
            {
                return LoomResult<Unit>.Success(Unit.Value);
            }

            _events.Publish("WorkSessionClosing", "core.work", workId: workId);
            session.BeginClose();
            await _resources.CloseOwnedAsync(workId).ConfigureAwait(false);
            session.CompleteClose();
            _events.Publish("WorkSessionClosed", "core.work", workId: workId);
            return LoomResult<Unit>.Success(Unit.Value);
        }
        finally
        {
            session.CloseGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values.Where(s => s.State == WorkSessionState.Active).ToArray())
        {
            await CloseAsync(session.Id).ConfigureAwait(false);
        }
    }
}
