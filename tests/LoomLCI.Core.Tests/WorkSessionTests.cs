using LoomLCI.Core.Observability;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;

namespace LoomLCI.Core.Tests;

public sealed class WorkSessionTests
{
    [Fact]
    public async Task ClosingSessionDisposesOwnedResourcesAndLeavesClosedTombstone()
    {
        var events = new LoomEventBus();
        var resources = new ResourceRegistry();
        await using var sessions = new WorkSessionManager(resources, events);

        var created = sessions.Create(Path.GetTempPath(), "test");
        Assert.True(created.IsSuccess);
        var work = created.Value!;

        var disposed = false;
        var resource = new object();
        var handle = resources.Register(
            "fake",
            "fake",
            resource,
            work.Id,
            ResourceOwnership.SessionOwned,
            () =>
            {
                disposed = true;
                return ValueTask.CompletedTask;
            });

        var closed = await sessions.CloseAsync(work.Id);

        Assert.True(closed.IsSuccess);
        Assert.True(disposed);

        var resolved = resources.Resolve<object>(handle, "fake");
        Assert.False(resolved.IsSuccess);
        Assert.Equal("resource_closed", resolved.Error?.Code);
    }

    [Fact]
    public async Task ConcurrentCloseIsIdempotentAndDisposesResourceOnce()
    {
        var events = new LoomEventBus();
        var resources = new ResourceRegistry();
        await using var sessions = new WorkSessionManager(resources, events);

        var created = sessions.Create(Path.GetTempPath());
        Assert.True(created.IsSuccess);

        var disposeCount = 0;
        resources.Register(
            "fake",
            "fake",
            new object(),
            created.Value!.Id,
            ResourceOwnership.SessionOwned,
            () =>
            {
                Interlocked.Increment(ref disposeCount);
                return ValueTask.CompletedTask;
            });

        await Task.WhenAll(
            sessions.CloseAsync(created.Value.Id).AsTask(),
            sessions.CloseAsync(created.Value.Id).AsTask());

        Assert.Equal(1, disposeCount);
    }

    [Fact]
    public async Task FailedCleanupKeepsSessionClosingAndRetryCompletesRemainingResources()
    {
        var events = new LoomEventBus();
        await using var resources = new ResourceRegistry();
        await using var sessions = new WorkSessionManager(resources, events);
        var work = sessions.Create().Value!;
        var failedAttempts = 0;
        var successfulAttempts = 0;

        var failingHandle = resources.Register(
            "fake", "fake", new object(), work.Id,
            ResourceOwnership.SessionOwned,
            () =>
            {
                if (Interlocked.Increment(ref failedAttempts) == 1)
                {
                    throw new InvalidOperationException("Injected failure.");
                }

                return ValueTask.CompletedTask;
            });
        var goodHandle = resources.Register(
            "fake", "fake", new object(), work.Id,
            ResourceOwnership.SessionOwned,
            () =>
            {
                Interlocked.Increment(ref successfulAttempts);
                return ValueTask.CompletedTask;
            });

        var first = await sessions.CloseAsync(work.Id);
        Assert.False(first.IsSuccess);
        Assert.Equal("cleanup_failed", first.Error?.Code);
        Assert.True(first.Error?.Retryable);
        Assert.Equal(WorkSessionState.Closing, work.State);
        Assert.Equal(ResourceState.Active, resources.Inspect(failingHandle, "fake").Value!.State);
        Assert.Equal(ResourceState.Closed, resources.Inspect(goodHandle, "fake").Value!.State);
        Assert.Equal(1, failedAttempts);
        Assert.Equal(1, successfulAttempts);
        Assert.False(sessions.Resolve(work.Id).IsSuccess);

        var retried = await sessions.CloseAsync(work.Id);
        Assert.True(retried.IsSuccess, retried.Error?.Message);
        Assert.Equal(WorkSessionState.Closed, work.State);
        Assert.Equal(ResourceState.Closed, resources.Inspect(failingHandle, "fake").Value!.State);
        Assert.Equal(2, failedAttempts);
        Assert.Equal(1, successfulAttempts);
    }

    [Fact]
    public async Task MultipleCleanupErrorsAreReportedWithoutSkippingLaterResources()
    {
        await using var resources = new ResourceRegistry();
        await using var sessions = new WorkSessionManager(resources, new LoomEventBus());
        var work = sessions.Create().Value!;
        var attempts = 0;
        var handles = new List<ResourceHandle>();
        for (var i = 0; i < 3; i++)
        {
            handles.Add(resources.Register(
                "fake", "fake", new object(), work.Id,
                ResourceOwnership.SessionOwned,
                () =>
                {
                    Interlocked.Increment(ref attempts);
                    if (attempts <= 2)
                    {
                        throw new InvalidOperationException("Injected cleanup error.");
                    }

                    return ValueTask.CompletedTask;
                }));
        }

        var first = await sessions.CloseAsync(work.Id);
        Assert.False(first.IsSuccess);
        Assert.Equal(3, attempts);
        Assert.Equal("cleanup_failed", first.Error!.Code);
        var failures = Assert.IsType<List<Dictionary<string, object?>>>(
            first.Error.Details!["failed_resources"]);
        Assert.Equal(2, failures.Count);
        Assert.Equal(WorkSessionState.Closing, work.State);

        var next = await sessions.CloseAsync(work.Id);
        Assert.True(next.IsSuccess);
        Assert.Equal(5, attempts);
        Assert.All(handles, h =>
            Assert.Equal(ResourceState.Closed, resources.Inspect(h, "fake").Value!.State));
    }

    [Fact]
    public async Task ParallelRetriesDoNotDisposeTheSameResourceTwice()
    {
        await using var resources = new ResourceRegistry();
        await using var sessions = new WorkSessionManager(resources, new LoomEventBus());
        var work = sessions.Create().Value!;
        var attempts = 0;
        resources.Register(
            "fake", "fake", new object(), work.Id,
            ResourceOwnership.SessionOwned,
            () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("Injected first attempt failure.");
                }

                return ValueTask.CompletedTask;
            });

        Assert.False((await sessions.CloseAsync(work.Id)).IsSuccess);
        var close1 = sessions.CloseAsync(work.Id).AsTask();
        var close2 = sessions.CloseAsync(work.Id).AsTask();
        await Task.WhenAll(close1, close2);

        Assert.True((await close1).IsSuccess);
        Assert.True((await close2).IsSuccess);
        Assert.Equal(2, attempts);
        Assert.Equal(WorkSessionState.Closed, work.State);
    }

    [Fact]
    public async Task FailedManagerShutdownPreservesSessionsForRetry()
    {
        await using var resources = new ResourceRegistry();
        var sessions = new WorkSessionManager(resources, new LoomEventBus());
        var work = sessions.Create().Value!;
        var fail = true;
        resources.Register(
            "fake", "fake", new object(), work.Id,
            ResourceOwnership.SessionOwned,
            () =>
            {
                if (fail) throw new InvalidOperationException("Injected persistent failure.");
                return ValueTask.CompletedTask;
            });

        Assert.False((await sessions.CloseAsync(work.Id)).IsSuccess);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sessions.DisposeAsync().AsTask());
        Assert.Equal(WorkSessionState.Closing, work.State);
        Assert.Equal(1, sessions.Count);
        fail = false;
        Assert.True((await sessions.CloseAsync(work.Id)).IsSuccess);
        await sessions.DisposeAsync();
        Assert.Equal(0, sessions.Count);
    }

    [Fact]
    public void RelativeBaseDirectoryIsNormalized()
    {
        var events = new LoomEventBus();
        var resources = new ResourceRegistry();
        var sessions = new WorkSessionManager(resources, events);

        var created = sessions.Create(".");

        Assert.True(created.IsSuccess);
        Assert.NotNull(created.Value!.BaseDirectory);
        Assert.True(Path.IsPathFullyQualified(created.Value.BaseDirectory!));
    }
}
