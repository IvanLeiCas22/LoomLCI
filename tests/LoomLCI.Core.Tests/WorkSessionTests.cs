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
