using LoomLCI.Core;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;
using LoomLCI.Windows.Processes;

namespace LoomLCI.Windows.Tests;

public sealed class ProcessCapabilityTests
{
    [Fact]
    public async Task ShortProcessReturnsExitCodeAndNonDestructiveOutput()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(new ProcessStartRequest(
            "cmd.exe",
            ["/d", "/s", "/c", "echo loom-test & exit /b 7"],
            WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);
        var handle = started.Value!.Handle;

        var status = await WaitForExitAsync(fixture.Processes, handle);
        Assert.Equal(ManagedProcessState.Exited, status.State);
        Assert.Equal(7, status.ExitCode);

        var first = await WaitForOutputAsync(fixture.Processes, handle, "loom-test");
        Assert.True(first.IsSuccess, first.Error?.Message);

        var again = await fixture.Processes.ReadAsync(handle, 0, 0);
        Assert.True(again.IsSuccess);
        Assert.Equal(
            string.Concat(first.Value!.Stdout.Chunks.Select(c => c.Text)),
            string.Concat(again.Value!.Stdout.Chunks.Select(c => c.Text)));

        var afterCursor = await fixture.Processes.ReadAsync(
            handle,
            first.Value.Stdout.NextCursor,
            first.Value.Stderr.NextCursor);

        Assert.True(afterCursor.IsSuccess);
        Assert.Empty(afterCursor.Value!.Stdout.Chunks);
    }

    [Fact]
    public async Task SessionCloseTerminatesAndClosesLongRunningProcess()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(new ProcessStartRequest(
            "powershell.exe",
            ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"],
            WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);
        var handle = started.Value!.Handle;

        var closed = await fixture.Sessions.CloseAsync(work.Value.Id);
        Assert.True(closed.IsSuccess);

        var status = await fixture.Processes.StatusAsync(handle);
        Assert.False(status.IsSuccess);
        Assert.Equal("resource_closed", status.Error?.Code);
    }

    [Fact]
    public async Task ExplicitTerminateStopsLongRunningProcess()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(new ProcessStartRequest(
            "powershell.exe",
            ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"],
            WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);

        var terminated = await fixture.Processes.TerminateAsync(started.Value!.Handle);
        Assert.True(terminated.IsSuccess, terminated.Error?.Message);

        var status = await fixture.Processes.StatusAsync(started.Value.Handle);
        Assert.True(status.IsSuccess);
        Assert.Equal(ManagedProcessState.Terminated, status.Value!.State);
    }

    [Fact]
    public async Task CancelledStartDoesNotRegisterPartialResource()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "powershell.exe",
                ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"],
                WorkId: work.Value!.Id),
            cancellation.Token);

        Assert.False(started.IsSuccess);
        Assert.Equal("cancelled", started.Error?.Code);
        Assert.Equal(0, fixture.Resources.ActiveCount);
    }

    [Fact]
    public async Task DistinctProcessesCanRunConcurrently()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var starts = await Task.WhenAll(
            fixture.Processes.StartAsync(new ProcessStartRequest(
                "powershell.exe",
                ["-NoProfile", "-Command", "Start-Sleep -Milliseconds 150; Write-Output one"],
                WorkId: work.Value!.Id)),
            fixture.Processes.StartAsync(new ProcessStartRequest(
                "powershell.exe",
                ["-NoProfile", "-Command", "Start-Sleep -Milliseconds 150; Write-Output two"],
                WorkId: work.Value.Id)));

        Assert.All(starts, result => Assert.True(result.IsSuccess, result.Error?.Message));
        var first = Assert.IsType<ProcessStartResult>(starts[0].Value);
        var second = Assert.IsType<ProcessStartResult>(starts[1].Value);
        Assert.NotEqual(first.Handle, second.Handle);

        var exits = await Task.WhenAll(
            WaitForExitAsync(fixture.Processes, first.Handle),
            WaitForExitAsync(fixture.Processes, second.Handle));

        Assert.All(exits, status => Assert.Equal(0, status.ExitCode));
    }

    [Fact]
    public async Task ResourceCreationIsCorrelatedWithInvocationEvents()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(new ProcessStartRequest(
            "cmd.exe",
            ["/d", "/s", "/c", "exit /b 0"],
            WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);

        var events = new List<LoomEvent>();
        while (fixture.Events.TryRead(out var loomEvent))
        {
            if (loomEvent is not null)
            {
                events.Add(loomEvent);
            }
        }

        var created = Assert.Single(
            events,
            e => e.Kind == "ResourceCreated" &&
                 e.ResourceHandle?.Value == started.Value!.Handle.Value);

        Assert.NotNull(created.InvocationId);
        Assert.Contains(events, e => e.Kind == "InvocationStarted" && e.InvocationId == created.InvocationId);
        Assert.Contains(events, e => e.Kind == "InvocationCompleted" && e.InvocationId == created.InvocationId);
    }

    [Fact]
    public async Task StdinCanDriveInteractivePipeProcess()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(new ProcessStartRequest(
            "cmd.exe",
            ["/d", "/q"],
            WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);
        var handle = started.Value!.Handle;

        var write = await fixture.Processes.WriteAsync(handle, "echo stdin-ok\r\nexit /b 0\r\n");
        Assert.True(write.IsSuccess, write.Error?.Message);

        var status = await WaitForExitAsync(fixture.Processes, handle);
        Assert.Equal(0, status.ExitCode);

        var output = await WaitForOutputAsync(fixture.Processes, handle, "stdin-ok");
        Assert.True(output.IsSuccess);
    }

    private static async Task<ProcessStatusResult> WaitForExitAsync(
        ProcessCapability processes,
        ProcessHandle handle)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await processes.StatusAsync(handle);
            Assert.True(status.IsSuccess, status.Error?.Message);

            if (status.Value!.State is ManagedProcessState.Exited or ManagedProcessState.Terminated)
            {
                return status.Value;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Process did not exit in time.");
    }

    private static async Task<LoomLCI.Core.LoomResult<ProcessOutputReadResult>> WaitForOutputAsync(
        ProcessCapability processes,
        ProcessHandle handle,
        string expected)
    {
        LoomLCI.Core.LoomResult<ProcessOutputReadResult>? latest = null;

        for (var i = 0; i < 100; i++)
        {
            latest = await processes.ReadAsync(handle, 0, 0);
            if (!latest.IsSuccess)
            {
                return latest;
            }

            var text = string.Concat(latest.Value!.Stdout.Chunks.Select(c => c.Text));
            if (text.Contains(expected, StringComparison.Ordinal))
            {
                return latest;
            }

            await Task.Delay(25);
        }

        return latest!;
    }

    private sealed class ProcessFixture : IAsyncDisposable
    {
        public ProcessFixture()
        {
            Events = new LoomEventBus();
            Resources = new ResourceRegistry();
            Sessions = new WorkSessionManager(Resources, Events);
            Invocations = new InvocationRunner(Events, Sessions);
            Processes = new ProcessCapability(
                new WindowsProcessProvider(),
                Resources,
                Invocations,
                Events);
        }

        public LoomEventBus Events { get; }
        public ResourceRegistry Resources { get; }
        public WorkSessionManager Sessions { get; }
        public InvocationRunner Invocations { get; }
        public ProcessCapability Processes { get; }

        public async ValueTask DisposeAsync()
        {
            await Sessions.DisposeAsync();
        }
    }
}
