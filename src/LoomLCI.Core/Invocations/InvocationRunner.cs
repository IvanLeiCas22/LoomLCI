using System.Diagnostics;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Work;

namespace LoomLCI.Core.Invocations;

public sealed record InvocationContext(
    InvocationId Id,
    string Operation,
    WorkSession? WorkSession,
    DateTimeOffset StartedAt);

public sealed class InvocationRunner
{
    public static readonly ActivitySource ActivitySource = new("LoomLCI.Core");

    private readonly LoomEventBus _events;
    private readonly WorkSessionManager _workSessions;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _hostLifetime = new();

    public InvocationRunner(
        LoomEventBus events,
        WorkSessionManager workSessions,
        TimeProvider? timeProvider = null)
    {
        _events = events;
        _workSessions = workSessions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LoomResult<T>> RunAsync<T>(
        string operation,
        WorkId? workId,
        Func<InvocationContext, CancellationToken, Task<LoomResult<T>>> action,
        CancellationToken callerCancellation = default,
        TimeSpan? timeout = null)
    {
        WorkSessionInvocationLease? sessionLease = null;
        WorkSession? session = null;

        if (workId is { } id)
        {
            var leaseResult = _workSessions.AcquireInvocation(id);
            if (!leaseResult.IsSuccess)
            {
                return LoomResult<T>.Failure(leaseResult.Error!);
            }

            sessionLease = leaseResult.Value!;
            session = sessionLease.Session;
        }

        using (sessionLease)
        {
            var invocationId = InvocationId.Create();
            var startedAt = _timeProvider.GetUtcNow();
            using var activity = ActivitySource.StartActivity(operation, ActivityKind.Internal);
            activity?.SetTag("loom.invocation.id", invocationId.Value);
            activity?.SetTag("loom.work.id", session?.Id.Value);

            using var linked = sessionLease is null
                ? CancellationTokenSource.CreateLinkedTokenSource(
                    callerCancellation,
                    _hostLifetime.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(
                    callerCancellation,
                    _hostLifetime.Token,
                    sessionLease.CancellationToken);

            using var deadline = timeout is null
                ? null
                : new CancellationTokenSource(timeout.Value, _timeProvider);
            using var all = deadline is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(linked.Token, deadline.Token);

            var token = all?.Token ?? linked.Token;
            var context = new InvocationContext(invocationId, operation, session, startedAt);

            _events.Publish("InvocationStarted", "core.invocation", session?.Id, invocationId);

            try
            {
                var result = await action(context, token).ConfigureAwait(false);
                activity?.SetStatus(
                    result.IsSuccess ? ActivityStatusCode.Ok : ActivityStatusCode.Error,
                    result.Error?.Code);
                _events.Publish(
                    "InvocationCompleted",
                    "core.invocation",
                    session?.Id,
                    invocationId,
                    payload: new Dictionary<string, object?>
                    {
                        ["success"] = result.IsSuccess,
                        ["error"] = result.Error?.Code
                    });
                return result;
            }
            catch (OperationCanceledException) when (deadline?.IsCancellationRequested == true)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "deadline_exceeded");
                var error = LoomErrors.DeadlineExceeded();
                _events.Publish(
                    "InvocationCompleted",
                    "core.invocation",
                    session?.Id,
                    invocationId,
                    payload: new Dictionary<string, object?>
                    {
                        ["success"] = false,
                        ["error"] = error.Code
                    });
                return LoomResult<T>.Failure(error);
            }
            catch (OperationCanceledException)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
                var error = LoomErrors.Cancelled();
                _events.Publish(
                    "InvocationCompleted",
                    "core.invocation",
                    session?.Id,
                    invocationId,
                    payload: new Dictionary<string, object?>
                    {
                        ["success"] = false,
                        ["error"] = error.Code
                    });
                return LoomResult<T>.Failure(error);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "internal");
                var error = LoomErrors.Internal(ex.Message);
                _events.Publish(
                    "InvocationCompleted",
                    "core.invocation",
                    session?.Id,
                    invocationId,
                    payload: new Dictionary<string, object?>
                    {
                        ["success"] = false,
                        ["error"] = error.Code
                    });
                return LoomResult<T>.Failure(error);
            }
        }
    }

    public void RequestShutdown() => _hostLifetime.Cancel();
}
