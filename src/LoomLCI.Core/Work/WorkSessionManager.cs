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

public sealed partial class WorkSession
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
            ClearWorkPlanUnsafe();
            ClearPythonPackageEnvironmentUnsafe();
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
            ClearWorkPlanUnsafe();
            ClearPythonPackageEnvironmentUnsafe();
            return true;
        }
    }

    internal WorkSessionState? ClosingTarget
    {
        get
        {
            lock (_stateGate)
            {
                return _state == WorkSessionState.Closing ? _closingTarget : null;
            }
        }
    }

    internal bool ShouldRetryCleanup(DateTimeOffset now, TimeSpan interval)
    {
        lock (_stateGate)
        {
            return _state == WorkSessionState.Closing &&
                   now - _stateChangedAt >= interval;
        }
    }

    internal void RecordCleanupFailure(DateTimeOffset now)
    {
        lock (_stateGate)
        {
            if (_state == WorkSessionState.Closing)
            {
                _stateChangedAt = now;
            }
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
    int PrunedResources,
    int FailedCleanups = 0);

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

    internal IReadOnlySet<string> GetActivePythonPackageEnvironmentIds()
    {
        var environmentIds = new HashSet<string>(
            StringComparer.Ordinal);

        foreach (var session in _sessions.Values)
        {
            if (session.State != WorkSessionState.Active)
            {
                continue;
            }

            var environment = session.GetPythonPackageEnvironment();
            if (environment.IsSuccess &&
                environment.Value is { } value)
            {
                environmentIds.Add(value.EnvironmentId);
            }
        }

        return environmentIds;
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

            if (session.State == WorkSessionState.Active)
            {
                if (!session.TryBeginClose(_timeProvider.GetUtcNow(), WorkSessionState.Closed))
                {
                    return LoomResult<Unit>.Failure(
                        LoomErrors.Internal("Could not begin work session close."));
                }

                _events.Publish("WorkSessionClosing", "core.work", workId: workId);
                session.CancelLifetime();
            }

            // A retry must preserve an earlier expiry target, rather than
            // silently transforming Expired into Closed.
            var target = session.ClosingTarget;
            if (target is not (WorkSessionState.Closed or WorkSessionState.Expired))
            {
                return LoomResult<Unit>.Failure(
                    LoomErrors.Internal("Missing work session closing target."));
            }

            var cleanup = target == WorkSessionState.Expired
                ? await _resources.ExpireOwnedAsync(workId).ConfigureAwait(false)
                : await _resources.CloseOwnedAsync(workId).ConfigureAwait(false);
            if (!cleanup.IsSuccess)
            {
                session.RecordCleanupFailure(_timeProvider.GetUtcNow());
                _events.Publish(
                    "WorkSessionCleanupFailed",
                    "core.work",
                    workId: workId,
                    payload: new Dictionary<string, object?>
                    {
                        ["code"] = cleanup.Error!.Code,
                        ["target"] = target.Value.ToString().ToLowerInvariant()
                    });
                return LoomResult<Unit>.Failure(cleanup.Error!);
            }

            session.CompleteClose(_timeProvider.GetUtcNow());
            _events.Publish(
                target == WorkSessionState.Expired ? "WorkSessionExpired" : "WorkSessionClosed",
                "core.work",
                workId: workId);
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
        var failedCleanups = 0;

        foreach (var session in _sessions.Values.ToArray())
        {
            try
            {
                // Retry partial explicit closes as well as expirations. A failed
                // cleanup cannot put the session back into Active.
                if (session.ShouldRetryCleanup(now, _options.SweepInterval))
                {
                    var result = await CloseAsync(session.Id).ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        failedCleanups++;
                    }
                    else if (session.State == WorkSessionState.Expired)
                    {
                        expiredSessions++;
                    }

                    continue;
                }

                if (!session.IsIdleExpired(now, _options.WorkSessionIdleTimeout))
                {
                    continue;
                }

                if (await ExpireIfIdleAsync(session, now).ConfigureAwait(false))
                {
                    expiredSessions++;
                }
                else if (session.State == WorkSessionState.Closing)
                {
                    failedCleanups++;
                }
            }
            catch (Exception ex)
            {
                // One unexpected cleanup failure must not starve unrelated
                // sessions. The Closing session remains eligible for retry.
                session.RecordCleanupFailure(_timeProvider.GetUtcNow());
                failedCleanups++;
                _events.Publish(
                    "WorkSessionCleanupFailed", "core.work", workId: session.Id,
                    payload: new Dictionary<string, object?>
                    {
                        ["code"] = "internal",
                        ["exception_type"] = ex.GetType().Name
                    });
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
        return new LifetimeSweepResult(expiredSessions, prunedSessions, prunedResources, failedCleanups);
    }

    public async ValueTask DisposeAsync()
    {
        var failed = new List<string>();
        foreach (var session in _sessions.Values
                     .Where(session => session.State is WorkSessionState.Active or WorkSessionState.Closing)
                     .ToArray())
        {
            var result = await CloseAsync(session.Id).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                failed.Add(session.Id.Value);
            }
        }

        if (failed.Count > 0)
        {
            // Retain ownership information for any outstanding cleanup.
            throw new InvalidOperationException(
                $"Work session shutdown failed to clean {failed.Count} session(s): " +
                string.Join(", ", failed));
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
            var cleanup = await _resources.ExpireOwnedAsync(session.Id).ConfigureAwait(false);
            if (!cleanup.IsSuccess)
            {
                session.RecordCleanupFailure(_timeProvider.GetUtcNow());
                _events.Publish(
                    "WorkSessionCleanupFailed",
                    "core.work",
                    workId: session.Id,
                    payload: new Dictionary<string, object?>
                    {
                        ["code"] = cleanup.Error!.Code,
                        ["target"] = "expired"
                    });
                return false;
            }

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
