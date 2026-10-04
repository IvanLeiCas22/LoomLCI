using System.Collections.Concurrent;
using LoomLCI.Core.Lifetime;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Resources;

namespace LoomLCI.Core.Work;

public enum WorkSessionState
{
    Active,
    Closing,
    Closed,
    Expired
}

public sealed class WorkSession
{
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private WorkSessionState _state = WorkSessionState.Active;
    private WorkSessionState? _closingTarget;
    private DateTimeOffset _lastActivityAt;
    private DateTimeOffset _stateChangedAt;
    private int _activeInvocationCount;

    internal WorkSession(
        WorkId id,
        string? baseDirectory,
        string? label,
        DateTimeOffset now)
    {
        Id = id;
        BaseDirectory = baseDirectory;
        Label = label;
        CreatedAt = now;
        _lastActivityAt = now;
        _stateChangedAt = now;
    }

    internal SemaphoreSlim CloseGate { get; } = new(1, 1);

    public WorkId Id { get; }
    public string? BaseDirectory { get; }
    public string? Label { get; }
    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastActivityAt
    {
        get
        {
            lock (_stateGate)
            {
                return _lastActivityAt;
            }
        }
    }

    public DateTimeOffset StateChangedAt
    {
        get
        {
            lock (_stateGate)
            {
                return _stateChangedAt;
            }
        }
    }

    public WorkSessionState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    internal int ActiveInvocationCount
    {
        get
        {
            lock (_stateGate)
            {
                return _activeInvocationCount;
            }
        }
    }

    internal bool TryTouch(DateTimeOffset now)
    {
        lock (_stateGate)
        {
            if (_state != WorkSessionState.Active)
            {
                return false;
            }

            _lastActivityAt = now;
            return true;
        }
    }

    internal bool TryAcquireInvocation(DateTimeOffset now, out CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            if (_state != WorkSessionState.Active)
            {
                cancellationToken = default;
                return false;
            }

            _activeInvocationCount++;
            _lastActivityAt = now;
            cancellationToken = _lifetime.Token;
            return true;
        }
    }

    internal void ReleaseInvocation(DateTimeOffset now)
    {
        lock (_stateGate)
        {
            if (_activeInvocationCount <= 0)
            {
                throw new InvalidOperationException("Work session invocation lease underflow.");
            }

            _activeInvocationCount--;

            if (_state == WorkSessionState.Active)
            {
                _lastActivityAt = now;
            }
        }
    }

    internal bool IsIdleExpired(DateTimeOffset now, TimeSpan idleTimeout)
    {
        lock (_stateGate)
        {
            return _state == WorkSessionState.Active &&
                   _activeInvocationCount == 0 &&
                   now - _lastActivityAt >= idleTimeout;
        }
    }

    internal bool TryBeginClose(DateTimeOffset now, WorkSessionState terminalState)
    {
        if (terminalState is not (WorkSessionState.Closed or WorkSessionState.Expired))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalState));
        }

        lock (_stateGate)
        {
            if (_state != WorkSessionState.Active)
            {
                return false;
            }

            _state = WorkSessionState.Closing;
            _closingTarget = terminalState;
            _stateChangedAt = now;
            return true;
        }
    }

    internal bool TryBeginExpiry(DateTimeOffset now, TimeSpan idleTimeout)
    {
        lock (_stateGate)
        {
            if (_state != WorkSessionState.Active ||
                _activeInvocationCount != 0 ||
                now - _lastActivityAt < idleTimeout)
            {
                return false;
            }

            _state = WorkSessionState.Closing;
            _closingTarget = WorkSessionState.Expired;
            _stateChangedAt = now;
            return true;
        }
    }

    internal void CancelLifetime() => _lifetime.Cancel();

    internal void CompleteClose(DateTimeOffset now)
    {
        lock (_stateGate)
        {
            _state = _closingTarget ?? WorkSessionState.Closed;
            _closingTarget = null;
            _stateChangedAt = now;
        }
    }

    internal LoomError UnavailableError()
    {
        lock (_stateGate)
        {
            return _state == WorkSessionState.Expired ||
                   (_state == WorkSessionState.Closing && _closingTarget == WorkSessionState.Expired)
                ? LoomErrors.ResourceExpired(Id.Value)
                : LoomErrors.ResourceClosed(Id.Value);
        }
    }

    internal bool TombstoneExpired(DateTimeOffset now, TimeSpan retention)
    {
        lock (_stateGate)
        {
            return _state is WorkSessionState.Closed or WorkSessionState.Expired &&
                   now - _stateChangedAt >= retention;
        }
    }

    internal void DisposeLifetime() => _lifetime.Dispose();
}

public sealed class WorkSessionInvocationLease : IDisposable
{
    private readonly WorkSession _session;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    internal WorkSessionInvocationLease(
        WorkSession session,
        CancellationToken cancellationToken,
        TimeProvider timeProvider)
    {
        _session = session;
        CancellationToken = cancellationToken;
        _timeProvider = timeProvider;
    }

    public WorkSession Session => _session;
    public CancellationToken CancellationToken { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _session.ReleaseInvocation(_timeProvider.GetUtcNow());
        }
    }
}

public sealed record LifetimeSweepResult(
    int ExpiredSessions,
    int PrunedSessions,
    int PrunedResources);

public sealed class WorkSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, WorkSession> _sessions = new(StringComparer.Ordinal);
    private readonly ResourceRegistry _resources;
    private readonly LoomEventBus _events;
    private readonly LifetimeOptions _options;
    private readonly TimeProvider _timeProvider;

    public WorkSessionManager(
        ResourceRegistry resources,
        LoomEventBus events,
        LifetimeOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _resources = resources;
        _events = events;
        _options = options ?? new LifetimeOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan IdleTimeout => _options.WorkSessionIdleTimeout;
    public int Count => _sessions.Count;

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
            return LoomResult<WorkSession>.Failure(
                LoomErrors.InvalidArgument($"Invalid base directory: {ex.Message}"));
        }

        var session = new WorkSession(
            WorkId.Create(),
            baseDirectory,
            label,
            _timeProvider.GetUtcNow());

        if (!_sessions.TryAdd(session.Id.Value, session))
        {
            return LoomResult<WorkSession>.Failure(
                LoomErrors.Internal("Could not register work session."));
        }

        _events.Publish("WorkSessionCreated", "core.work", workId: session.Id);
        return LoomResult<WorkSession>.Success(session);
    }

    public LoomResult<WorkSession> Resolve(WorkId workId)
    {
        if (!_sessions.TryGetValue(workId.Value, out var session))
        {
            return LoomResult<WorkSession>.Failure(
                LoomErrors.NotFound($"Work session '{workId}' was not found."));
        }

        if (!session.TryTouch(_timeProvider.GetUtcNow()))
        {
            return LoomResult<WorkSession>.Failure(session.UnavailableError());
        }

        return LoomResult<WorkSession>.Success(session);
    }

    public LoomResult<WorkSessionInvocationLease> AcquireInvocation(WorkId workId)
    {
        if (!_sessions.TryGetValue(workId.Value, out var session))
        {
            return LoomResult<WorkSessionInvocationLease>.Failure(
                LoomErrors.NotFound($"Work session '{workId}' was not found."));
        }

        if (!session.TryAcquireInvocation(
                _timeProvider.GetUtcNow(),
                out var cancellationToken))
        {
            return LoomResult<WorkSessionInvocationLease>.Failure(session.UnavailableError());
        }

        return LoomResult<WorkSessionInvocationLease>.Success(
            new WorkSessionInvocationLease(session, cancellationToken, _timeProvider));
    }

    public async ValueTask<LoomResult<Unit>> CloseAsync(WorkId workId)
    {
        if (!_sessions.TryGetValue(workId.Value, out var session))
        {
            return LoomResult<Unit>.Failure(
                LoomErrors.NotFound($"Work session '{workId}' was not found."));
        }

        await session.CloseGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session.State is WorkSessionState.Closed or WorkSessionState.Expired)
            {
                return LoomResult<Unit>.Success(Unit.Value);
            }

            var now = _timeProvider.GetUtcNow();
            if (!session.TryBeginClose(now, WorkSessionState.Closed))
            {
                return LoomResult<Unit>.Success(Unit.Value);
            }

            _events.Publish("WorkSessionClosing", "core.work", workId: workId);
            session.CancelLifetime();
            await _resources.CloseOwnedAsync(workId).ConfigureAwait(false);
            session.CompleteClose(_timeProvider.GetUtcNow());
            _events.Publish("WorkSessionClosed", "core.work", workId: workId);
            return LoomResult<Unit>.Success(Unit.Value);
        }
        finally
        {
            session.CloseGate.Release();
        }
    }

    public async Task<LifetimeSweepResult> SweepExpiredAsync()
    {
        var now = _timeProvider.GetUtcNow();
        var expiredSessions = 0;

        foreach (var session in _sessions.Values.ToArray())
        {
            if (!session.IsIdleExpired(now, _options.WorkSessionIdleTimeout))
            {
                continue;
            }

            if (await ExpireIfIdleAsync(session, now).ConfigureAwait(false))
            {
                expiredSessions++;
            }
        }

        var prunedSessions = 0;
        now = _timeProvider.GetUtcNow();

        foreach (var session in _sessions.Values.ToArray())
        {
            if (!session.TombstoneExpired(now, _options.TombstoneRetention))
            {
                continue;
            }

            if (_sessions.TryRemove(session.Id.Value, out var removed))
            {
                removed.DisposeLifetime();
                prunedSessions++;
            }
        }

        var prunedResources = _resources.PruneTombstones(_options.TombstoneRetention);
        return new LifetimeSweepResult(expiredSessions, prunedSessions, prunedResources);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values
                     .Where(session => session.State == WorkSessionState.Active)
                     .ToArray())
        {
            await CloseAsync(session.Id).ConfigureAwait(false);
        }

        foreach (var session in _sessions.Values)
        {
            session.DisposeLifetime();
        }

        _sessions.Clear();
    }

    private async Task<bool> ExpireIfIdleAsync(WorkSession session, DateTimeOffset observedNow)
    {
        await session.CloseGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!session.TryBeginExpiry(observedNow, _options.WorkSessionIdleTimeout))
            {
                return false;
            }

            _events.Publish("WorkSessionExpiring", "core.work", workId: session.Id);
            session.CancelLifetime();
            await _resources.ExpireOwnedAsync(session.Id).ConfigureAwait(false);
            session.CompleteClose(_timeProvider.GetUtcNow());
            _events.Publish("WorkSessionExpired", "core.work", workId: session.Id);
            return true;
        }
        finally
        {
            session.CloseGate.Release();
        }
    }
}
