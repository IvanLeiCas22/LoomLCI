using LoomLCI.Core;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Lifetime;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;
using Microsoft.Extensions.Time.Testing;

namespace LoomLCI.Core.Tests;

public sealed class LifetimeTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IdleSessionExpiresOwnedResourcesAndLeavesExpiredTombstones()
    {
        var clock = new FakeTimeProvider(Start);
        var events = new LoomEventBus(clock);
        await using var resources = new ResourceRegistry(clock);
        await using var sessions = new WorkSessionManager(
            resources,
            events,
            Options(),
            clock);

        var created = sessions.Create(Path.GetTempPath());
        Assert.True(created.IsSuccess);

        var disposed = false;
        var handle = resources.Register(
            "fake",
            "fake",
            new object(),
            created.Value!.Id,
            ResourceOwnership.SessionOwned,
            () =>
            {
                disposed = true;
                return ValueTask.CompletedTask;
            });

        clock.Advance(TimeSpan.FromMinutes(10));
        var sweep = await sessions.SweepExpiredAsync();

        Assert.Equal(1, sweep.ExpiredSessions);
        Assert.True(disposed);
        Assert.Equal(WorkSessionState.Expired, created.Value.State);

        var sessionResolve = sessions.Resolve(created.Value.Id);
        Assert.False(sessionResolve.IsSuccess);
        Assert.Equal("resource_expired", sessionResolve.Error?.Code);

        var resourceResolve = resources.Resolve<object>(handle, "fake");
        Assert.False(resourceResolve.IsSuccess);
        Assert.Equal("resource_expired", resourceResolve.Error?.Code);
    }

    [Fact]
    public async Task ActivityRefreshesSessionIdleTimeout()
    {
        var clock = new FakeTimeProvider(Start);
        var events = new LoomEventBus(clock);
        await using var resources = new ResourceRegistry(clock);
        await using var sessions = new WorkSessionManager(
            resources,
            events,
            Options(),
            clock);

        var created = sessions.Create(Path.GetTempPath());
        Assert.True(created.IsSuccess);

        clock.Advance(TimeSpan.FromMinutes(9));
        var resolved = sessions.Resolve(created.Value!.Id);
        Assert.True(resolved.IsSuccess);

        clock.Advance(TimeSpan.FromMinutes(9));
        var firstSweep = await sessions.SweepExpiredAsync();
        Assert.Equal(0, firstSweep.ExpiredSessions);
        Assert.Equal(WorkSessionState.Active, created.Value.State);

        clock.Advance(TimeSpan.FromMinutes(1));
        var secondSweep = await sessions.SweepExpiredAsync();
        Assert.Equal(1, secondSweep.ExpiredSessions);
        Assert.Equal(WorkSessionState.Expired, created.Value.State);
    }

    [Fact]
    public async Task ActiveInvocationPreventsExpiryAndReleaseRestartsIdleClock()
    {
        var clock = new FakeTimeProvider(Start);
        var events = new LoomEventBus(clock);
        await using var resources = new ResourceRegistry(clock);
        await using var sessions = new WorkSessionManager(
            resources,
            events,
            Options(),
            clock);
        var invocations = new InvocationRunner(events, sessions, clock);

        var created = sessions.Create(Path.GetTempPath());
        Assert.True(created.IsSuccess);

        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var invocation = invocations.RunAsync(
            "test.long",
            created.Value!.Id,
            async (_, _) =>
            {
                entered.SetResult();
                await release.Task.ConfigureAwait(false);
                return LoomResult<bool>.Success(true);
            });

        await entered.Task;

        clock.Advance(TimeSpan.FromMinutes(20));
        var duringInvocation = await sessions.SweepExpiredAsync();

        Assert.Equal(0, duringInvocation.ExpiredSessions);
        Assert.Equal(WorkSessionState.Active, created.Value.State);

        release.SetResult();
        var completed = await invocation;
        Assert.True(completed.IsSuccess);

        clock.Advance(TimeSpan.FromMinutes(9));
        var beforeTimeout = await sessions.SweepExpiredAsync();
        Assert.Equal(0, beforeTimeout.ExpiredSessions);

        clock.Advance(TimeSpan.FromMinutes(1));
        var atTimeout = await sessions.SweepExpiredAsync();
        Assert.Equal(1, atTimeout.ExpiredSessions);
    }

    [Fact]
    public async Task ExplicitCloseCancelsActiveInvocation()
    {
        var clock = new FakeTimeProvider(Start);
        var events = new LoomEventBus(clock);
        await using var resources = new ResourceRegistry(clock);
        await using var sessions = new WorkSessionManager(
            resources,
            events,
            Options(),
            clock);
        var invocations = new InvocationRunner(events, sessions, clock);

        var created = sessions.Create(Path.GetTempPath());
        Assert.True(created.IsSuccess);

        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var invocation = invocations.RunAsync(
            "test.cancel",
            created.Value!.Id,
            async (_, token) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return LoomResult<bool>.Success(true);
            });

        await entered.Task;
        var closed = await sessions.CloseAsync(created.Value.Id);
        var completed = await invocation;

        Assert.True(closed.IsSuccess);
        Assert.False(completed.IsSuccess);
        Assert.Equal("cancelled", completed.Error?.Code);
        Assert.Equal(WorkSessionState.Closed, created.Value.State);
    }

    [Fact]
    public async Task ExplicitCloseProducesClosedTombstones()
    {
        var clock = new FakeTimeProvider(Start);
        var events = new LoomEventBus(clock);
        await using var resources = new ResourceRegistry(clock);
        await using var sessions = new WorkSessionManager(
            resources,
            events,
            Options(),
            clock);

        var created = sessions.Create(Path.GetTempPath());
        Assert.True(created.IsSuccess);

        var handle = resources.Register(
            "fake",
            "fake",
            new object(),
            created.Value!.Id,
            ResourceOwnership.SessionOwned,
            () => ValueTask.CompletedTask);

        var closed = await sessions.CloseAsync(created.Value.Id);
        Assert.True(closed.IsSuccess);
        Assert.Equal(WorkSessionState.Closed, created.Value.State);

        var sessionResolve = sessions.Resolve(created.Value.Id);
        Assert.False(sessionResolve.IsSuccess);
        Assert.Equal("resource_closed", sessionResolve.Error?.Code);

        var resourceResolve = resources.Resolve<object>(handle, "fake");
        Assert.False(resourceResolve.IsSuccess);
        Assert.Equal("resource_closed", resourceResolve.Error?.Code);
    }

    [Fact]
    public async Task TombstonesArePrunedAfterRetention()
    {
        var clock = new FakeTimeProvider(Start);
        var events = new LoomEventBus(clock);
        await using var resources = new ResourceRegistry(clock);
        await using var sessions = new WorkSessionManager(
            resources,
            events,
            Options(),
            clock);

        var created = sessions.Create(Path.GetTempPath());
        Assert.True(created.IsSuccess);

        var handle = resources.Register(
            "fake",
            "fake",
            new object(),
            created.Value!.Id,
            ResourceOwnership.SessionOwned,
            () => ValueTask.CompletedTask);

        clock.Advance(TimeSpan.FromMinutes(10));
        var expired = await sessions.SweepExpiredAsync();
        Assert.Equal(1, expired.ExpiredSessions);
        Assert.Equal(1, sessions.Count);
        Assert.Equal(1, resources.TombstoneCount);

        clock.Advance(TimeSpan.FromMinutes(20));
        var pruned = await sessions.SweepExpiredAsync();

        Assert.Equal(1, pruned.PrunedSessions);
        Assert.Equal(1, pruned.PrunedResources);
        Assert.Equal(0, sessions.Count);
        Assert.Equal(0, resources.Count);

        var sessionResolve = sessions.Resolve(created.Value.Id);
        Assert.False(sessionResolve.IsSuccess);
        Assert.Equal("not_found", sessionResolve.Error?.Code);

        var resourceResolve = resources.Resolve<object>(handle, "fake");
        Assert.False(resourceResolve.IsSuccess);
        Assert.Equal("not_found", resourceResolve.Error?.Code);
    }

    [Fact]
    public async Task ConcurrentExpiryAndExplicitCloseDisposeOwnedResourceOnce()
    {
        var clock = new FakeTimeProvider(Start);
        var events = new LoomEventBus(clock);
        await using var resources = new ResourceRegistry(clock);
        await using var sessions = new WorkSessionManager(
            resources,
            events,
            Options(),
            clock);

        var created = sessions.Create(Path.GetTempPath());
        Assert.True(created.IsSuccess);

        var disposeCount = 0;
        var disposerEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDisposer = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        resources.Register(
            "fake",
            "fake",
            new object(),
            created.Value!.Id,
            ResourceOwnership.SessionOwned,
            async () =>
            {
                Interlocked.Increment(ref disposeCount);
                disposerEntered.SetResult();
                await releaseDisposer.Task.ConfigureAwait(false);
            });

        clock.Advance(TimeSpan.FromMinutes(10));
        var sweepTask = sessions.SweepExpiredAsync();
        await disposerEntered.Task;

        var closeTask = sessions.CloseAsync(created.Value.Id).AsTask();

        releaseDisposer.SetResult();
        await Task.WhenAll(sweepTask, closeTask);

        Assert.Equal(1, disposeCount);
        Assert.Equal(WorkSessionState.Expired, created.Value.State);
    }

    [Fact]
    public async Task ResourceCloseWaitsForActiveOperationLease()
    {
        var clock = new FakeTimeProvider(Start);
        await using var resources = new ResourceRegistry(clock);
        var disposed = false;

        var handle = resources.Register(
            "fake",
            "fake",
            new object(),
            ownerWorkId: null,
            ResourceOwnership.Independent,
            () =>
            {
                disposed = true;
                return ValueTask.CompletedTask;
            });

        var acquired = resources.Acquire<object>(handle, "fake");
        Assert.True(acquired.IsSuccess, acquired.Error?.Message);

        var closeTask = resources.CloseAsync(handle).AsTask();
        await Task.Yield();

        Assert.False(closeTask.IsCompleted);
        Assert.False(disposed);

        acquired.Value!.Dispose();

        var closed = await closeTask;
        Assert.True(closed.IsSuccess, closed.Error?.Message);
        Assert.True(disposed);
    }

    [Fact]
    public async Task ResourceRegistryShutdownClosesIndependentResources()
    {
        var clock = new FakeTimeProvider(Start);
        var resources = new ResourceRegistry(clock);
        var disposed = false;

        resources.Register(
            "fake",
            "fake",
            new object(),
            ownerWorkId: null,
            ResourceOwnership.Independent,
            () =>
            {
                disposed = true;
                return ValueTask.CompletedTask;
            });

        await resources.DisposeAsync();

        Assert.True(disposed);
        Assert.Equal(0, resources.Count);
    }

    [Fact]
    public async Task FailedExpiryStaysClosingUntilRetryAndKeepsExpiredTarget()
    {
        var clock = new FakeTimeProvider(Start);
        await using var resources = new ResourceRegistry(clock);
        await using var sessions = new WorkSessionManager(
            resources, new LoomEventBus(clock), Options(), clock);
        var work = sessions.Create().Value!;
        var attempts = 0;
        var handle = resources.Register(
            "fake", "fake", new object(), work.Id,
            ResourceOwnership.SessionOwned,
            () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("Transient expiry failure.");
                }

                return ValueTask.CompletedTask;
            });

        clock.Advance(TimeSpan.FromMinutes(10));
        var first = await sessions.SweepExpiredAsync();
        Assert.Equal(0, first.ExpiredSessions);
        Assert.Equal(1, first.FailedCleanups);
        Assert.Equal(WorkSessionState.Closing, work.State);
        Assert.Equal(ResourceState.Active, resources.Inspect(handle, "fake").Value!.State);
        Assert.Equal(1, attempts);

        var immediate = await sessions.SweepExpiredAsync();
        Assert.Equal(0, immediate.ExpiredSessions);
        Assert.Equal(0, immediate.FailedCleanups);
        Assert.Equal(1, attempts);

        clock.Advance(TimeSpan.FromMinutes(1));
        var retried = await sessions.SweepExpiredAsync();
        Assert.Equal(1, retried.ExpiredSessions);
        Assert.Equal(0, retried.FailedCleanups);
        Assert.Equal(WorkSessionState.Expired, work.State);
        Assert.Equal(ResourceState.Expired, resources.Inspect(handle, "fake").Value!.State);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExplicitCloseAfterFailedExpiryCompletesAsExpired()
    {
        var clock = new FakeTimeProvider(Start);
        await using var resources = new ResourceRegistry(clock);
        await using var sessions = new WorkSessionManager(
            resources, new LoomEventBus(clock), Options(), clock);
        var work = sessions.Create().Value!;
        var attempts = 0;
        var handle = resources.Register(
            "fake", "fake", new object(), work.Id,
            ResourceOwnership.SessionOwned,
            () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("Transient.");
                }

                return ValueTask.CompletedTask;
            });

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, (await sessions.SweepExpiredAsync()).FailedCleanups);
        var completed = await sessions.CloseAsync(work.Id);
        Assert.True(completed.IsSuccess);
        Assert.Equal(WorkSessionState.Expired, work.State);
        Assert.Equal(ResourceState.Expired, resources.Inspect(handle, "fake").Value!.State);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task FailedExplicitCloseRetriesDuringSweepWithoutExpiring()
    {
        var clock = new FakeTimeProvider(Start);
        await using var resources = new ResourceRegistry(clock);
        await using var sessions = new WorkSessionManager(
            resources, new LoomEventBus(clock), Options(), clock);
        var work = sessions.Create().Value!;
        var attempts = 0;
        resources.Register(
            "fake", "fake", new object(), work.Id,
            ResourceOwnership.SessionOwned,
            () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("Transient.");
                }

                return ValueTask.CompletedTask;
            });

        Assert.False((await sessions.CloseAsync(work.Id)).IsSuccess);
        clock.Advance(TimeSpan.FromMinutes(1));
        var sweep = await sessions.SweepExpiredAsync();
        Assert.Equal(0, sweep.ExpiredSessions);
        Assert.Equal(0, sweep.FailedCleanups);
        Assert.Equal(WorkSessionState.Closed, work.State);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ResourceRegistryShutdownRetainsFailedEntriesAndAllowsRetry()
    {
        var resources = new ResourceRegistry();
        var fail = true;
        var attempts = 0;
        var handle = resources.Register(
            "fake", "fake", new object(), ownerWorkId: null,
            ResourceOwnership.Independent,
            () =>
            {
                Interlocked.Increment(ref attempts);
                if (fail) throw new InvalidOperationException("Injected shutdown error.");
                return ValueTask.CompletedTask;
            });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resources.DisposeAsync().AsTask());
        Assert.Equal(1, resources.Count);
        Assert.Equal(ResourceState.Active, resources.Inspect(handle, "fake").Value!.State);

        fail = false;
        await resources.DisposeAsync();
        Assert.Equal(0, resources.Count);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task PermanentCleanupFailureDoesNotBlockExpiryOfOtherSessions()
    {
        var clock = new FakeTimeProvider(Start);
        await using var resources = new ResourceRegistry(clock);
        var sessions = new WorkSessionManager(
            resources, new LoomEventBus(clock), Options(), clock);
        var blocked = sessions.Create().Value!;
        var normal = sessions.Create().Value!;
        var shouldFail = true;
        var blockedHandle = resources.Register(
            "fake", "fake", new object(), blocked.Id,
            ResourceOwnership.SessionOwned,
            () =>
            {
                if (shouldFail) throw new InvalidOperationException("Permanent failure.");
                return ValueTask.CompletedTask;
            });
        var normalHandle = resources.Register(
            "fake", "fake", new object(), normal.Id,
            ResourceOwnership.SessionOwned,
            () => ValueTask.CompletedTask);

        clock.Advance(TimeSpan.FromMinutes(10));
        var sweep = await sessions.SweepExpiredAsync();
        Assert.Equal(1, sweep.ExpiredSessions);
        Assert.Equal(1, sweep.FailedCleanups);
        Assert.Equal(WorkSessionState.Closing, blocked.State);
        Assert.Equal(WorkSessionState.Expired, normal.State);
        Assert.Equal(ResourceState.Active, resources.Inspect(blockedHandle, "fake").Value!.State);
        Assert.Equal(ResourceState.Expired, resources.Inspect(normalHandle, "fake").Value!.State);

        // A permanently failing disposer remains tracked, not silently cleared.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sessions.DisposeAsync().AsTask());
        Assert.Equal(2, sessions.Count);
        shouldFail = false;
        Assert.True((await sessions.CloseAsync(blocked.Id)).IsSuccess);
        await sessions.DisposeAsync();
    }

    [Fact]
    public async Task IndependentResourceIsNotClosedWithSession()
    {
        await using var resources = new ResourceRegistry();
        await using var sessions = new WorkSessionManager(
            resources, new LoomEventBus());
        var work = sessions.Create().Value!;
        var count = 0;
        var handle = resources.Register(
            "fake", "fake", new object(), null,
            ResourceOwnership.Independent,
            () =>
            {
                count++;
                return ValueTask.CompletedTask;
            });

        Assert.True((await sessions.CloseAsync(work.Id)).IsSuccess);
        Assert.Equal(0, count);
        Assert.Equal(ResourceState.Active, resources.Inspect(handle, "fake").Value!.State);
        Assert.True((await resources.CloseAsync(handle)).IsSuccess);
        Assert.Equal(1, count);
    }

    private static LifetimeOptions Options()
        => new(
            workSessionIdleTimeout: TimeSpan.FromMinutes(10),
            tombstoneRetention: TimeSpan.FromMinutes(20),
            sweepInterval: TimeSpan.FromMinutes(1));
}
