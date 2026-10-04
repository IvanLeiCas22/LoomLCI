using LoomLCI.Core;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Lifetime;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;
using Microsoft.Extensions.Time.Testing;

namespace LoomLCI.Core.Tests;

public sealed class ProcessRetentionTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 10, 4, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunningProcessDoesNotExpireWithoutPolling()
    {
        await using var fixture = new Fixture();
        var started = await fixture.StartIndependentAsync();
        Assert.True(started.IsSuccess, started.Error?.Message);
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            started.Value!.PostExitRetention);

        fixture.Clock.Advance(TimeSpan.FromHours(6));
        var sweep = await fixture.Processes.SweepExpiredAsync();

        Assert.Equal(0, sweep.ExpiredProcesses);
        Assert.False(fixture.Provider.Resource!.Disposed);

        var status = await fixture.Processes.StatusAsync(
            started.Value.Handle);
        Assert.True(status.IsSuccess, status.Error?.Message);
        Assert.Equal(ManagedProcessState.Running, status.Value!.State);
        Assert.Null(status.Value.RetentionExpiresAt);
    }

    [Fact]
    public async Task LongRunningProcessStartsRetentionAtExit()
    {
        await using var fixture = new Fixture();
        var started = await fixture.StartIndependentAsync();
        Assert.True(started.IsSuccess, started.Error?.Message);

        fixture.Clock.Advance(TimeSpan.FromHours(6));
        fixture.Provider.Resource!.Exit(0);

        var immediate = await fixture.Processes.SweepExpiredAsync();
        Assert.Equal(0, immediate.ExpiredProcesses);
        Assert.False(fixture.Provider.Resource.Disposed);

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        var afterRetention = await fixture.Processes.SweepExpiredAsync();

        Assert.Equal(1, afterRetention.ExpiredProcesses);
        Assert.True(fixture.Provider.Resource.Disposed);
    }

    [Fact]
    public async Task ExitedProcessExpiresAfterRetention()
    {
        await using var fixture = new Fixture();
        var started = await fixture.StartIndependentAsync();
        Assert.True(started.IsSuccess, started.Error?.Message);

        fixture.Provider.Resource!.Exit(7);
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));

        var sweep = await fixture.Processes.SweepExpiredAsync();

        Assert.Equal(1, sweep.ExpiredProcesses);
        Assert.True(fixture.Provider.Resource.Disposed);

        var status = await fixture.Processes.StatusAsync(
            started.Value!.Handle);
        Assert.False(status.IsSuccess);
        Assert.Equal("resource_expired", status.Error?.Code);
    }

    [Fact]
    public async Task StatusRefreshesPostExitRetentionDeadline()
    {
        await using var fixture = new Fixture();
        var started = await fixture.StartIndependentAsync();
        Assert.True(started.IsSuccess, started.Error?.Message);

        fixture.Provider.Resource!.Exit(0);
        fixture.Clock.Advance(TimeSpan.FromMinutes(9));

        var status = await fixture.Processes.StatusAsync(
            started.Value!.Handle);
        Assert.True(status.IsSuccess, status.Error?.Message);
        Assert.Equal(
            fixture.Clock.GetUtcNow() + TimeSpan.FromMinutes(10),
            status.Value!.RetentionExpiresAt);

        fixture.Clock.Advance(TimeSpan.FromMinutes(9));
        var before = await fixture.Processes.SweepExpiredAsync();
        Assert.Equal(0, before.ExpiredProcesses);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var atDeadline = await fixture.Processes.SweepExpiredAsync();
        Assert.Equal(1, atDeadline.ExpiredProcesses);
    }

    [Fact]
    public async Task ReadRefreshesPostExitRetentionDeadline()
    {
        await using var fixture = new Fixture();
        var started = await fixture.StartIndependentAsync();
        Assert.True(started.IsSuccess, started.Error?.Message);

        fixture.Provider.Resource!.Exit(0);
        fixture.Clock.Advance(TimeSpan.FromMinutes(9));

        var read = await fixture.Processes.ReadAsync(
            started.Value!.Handle);
        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Equal(
            fixture.Clock.GetUtcNow() + TimeSpan.FromMinutes(10),
            read.Value!.Process.RetentionExpiresAt);

        fixture.Clock.Advance(TimeSpan.FromMinutes(9));
        var before = await fixture.Processes.SweepExpiredAsync();
        Assert.Equal(0, before.ExpiredProcesses);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var atDeadline = await fixture.Processes.SweepExpiredAsync();
        Assert.Equal(1, atDeadline.ExpiredProcesses);
    }

    [Fact]
    public async Task InvalidWriteAfterExitDoesNotRefreshRetention()
    {
        await using var fixture = new Fixture();
        var started = await fixture.StartIndependentAsync();
        Assert.True(started.IsSuccess, started.Error?.Message);

        fixture.Provider.Resource!.Exit(0);
        fixture.Clock.Advance(TimeSpan.FromMinutes(9));

        var write = await fixture.Processes.WriteAsync(
            started.Value!.Handle,
            "ignored");
        Assert.False(write.IsSuccess);
        Assert.Equal("conflict", write.Error?.Code);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var sweep = await fixture.Processes.SweepExpiredAsync();

        Assert.Equal(1, sweep.ExpiredProcesses);
    }

    [Fact]
    public async Task SuccessfulTerminateAfterRootExitRefreshesRetention()
    {
        await using var fixture = new Fixture();
        var started = await fixture.StartIndependentAsync();
        Assert.True(started.IsSuccess, started.Error?.Message);

        fixture.Provider.Resource!.Exit(0);
        fixture.Clock.Advance(TimeSpan.FromMinutes(9));

        var terminate = await fixture.Processes.TerminateAsync(
            started.Value!.Handle);
        Assert.True(terminate.IsSuccess, terminate.Error?.Message);

        fixture.Clock.Advance(TimeSpan.FromMinutes(9));
        var before = await fixture.Processes.SweepExpiredAsync();
        Assert.Equal(0, before.ExpiredProcesses);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var atDeadline = await fixture.Processes.SweepExpiredAsync();
        Assert.Equal(1, atDeadline.ExpiredProcesses);
    }

    [Fact]
    public async Task ActiveReadLeasePreventsConcurrentExpiry()
    {
        await using var fixture = new Fixture();
        var started = await fixture.StartIndependentAsync();
        Assert.True(started.IsSuccess, started.Error?.Message);

        fixture.Provider.Resource!.Exit(0);
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        fixture.Provider.Resource.BlockReads = true;

        var readTask = Task.Run(
            async () => await fixture.Processes.ReadAsync(
                started.Value!.Handle));

        Assert.True(
            fixture.Provider.Resource.ReadEntered.Wait(
                TimeSpan.FromSeconds(5)));

        var duringRead = await fixture.Processes.SweepExpiredAsync();
        Assert.Equal(0, duringRead.ExpiredProcesses);
        Assert.False(fixture.Provider.Resource.Disposed);

        fixture.Provider.Resource.ReleaseRead.Set();
        var read = await readTask;
        Assert.True(read.IsSuccess, read.Error?.Message);

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        var afterRead = await fixture.Processes.SweepExpiredAsync();

        Assert.Equal(1, afterRead.ExpiredProcesses);
        Assert.True(fixture.Provider.Resource.Disposed);
    }

    [Theory]
    [InlineData(ManagedProcessState.Starting)]
    [InlineData(ManagedProcessState.Running)]
    [InlineData(ManagedProcessState.Terminating)]
    public async Task ReleaseRejectsNonTerminalProcess(
        ManagedProcessState state)
    {
        await using var fixture = new Fixture();
        var started = await fixture.StartIndependentAsync();
        Assert.True(started.IsSuccess, started.Error?.Message);

        fixture.Provider.Resource!.SetState(state);

        var released = await fixture.Processes.ReleaseAsync(
            started.Value!.Handle);

        Assert.False(released.IsSuccess);
        Assert.Equal("conflict", released.Error?.Code);
        Assert.Contains(
            "process_terminate",
            released.Error?.Message,
            StringComparison.Ordinal);
        Assert.False(fixture.Provider.Resource.Disposed);
    }

    [Fact]
    public async Task ReleaseExitedProcessClosesRetainedResource()
    {
        await using var fixture = new Fixture();
        var started = await fixture.StartIndependentAsync();
        Assert.True(started.IsSuccess, started.Error?.Message);

        fixture.Provider.Resource!.Exit(0);

        var released = await fixture.Processes.ReleaseAsync(
            started.Value!.Handle);

        Assert.True(released.IsSuccess, released.Error?.Message);
        Assert.True(fixture.Provider.Resource.Disposed);
        Assert.Equal(1, fixture.Provider.Resource.DisposeCount);

        var status = await fixture.Processes.StatusAsync(
            started.Value.Handle);
        Assert.False(status.IsSuccess);
        Assert.Equal("resource_closed", status.Error?.Code);
    }

    [Fact]
    public async Task ReleaseTerminatedProcessClosesRetainedResource()
    {
        await using var fixture = new Fixture();
        var started = await fixture.StartIndependentAsync();
        Assert.True(started.IsSuccess, started.Error?.Message);

        var terminated = await fixture.Processes.TerminateAsync(
            started.Value!.Handle);
        Assert.True(terminated.IsSuccess, terminated.Error?.Message);

        var released = await fixture.Processes.ReleaseAsync(
            started.Value.Handle);

        Assert.True(released.IsSuccess, released.Error?.Message);
        Assert.Equal(1, fixture.Provider.Resource!.DisposeCount);
    }

    [Fact]
    public async Task ReleaseIsIdempotentAndDisposesOnce()
    {
        await using var fixture = new Fixture();
        var started = await fixture.StartIndependentAsync();
        Assert.True(started.IsSuccess, started.Error?.Message);

        fixture.Provider.Resource!.Exit(0);

        var first = await fixture.Processes.ReleaseAsync(
            started.Value!.Handle);
        var second = await fixture.Processes.ReleaseAsync(
            started.Value.Handle);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(1, fixture.Provider.Resource.DisposeCount);
    }

    [Fact]
    public async Task ReleaseAfterExpiryIsIdempotentAndKeepsExpiredTombstone()
    {
        await using var fixture = new Fixture();
        var started = await fixture.StartIndependentAsync();
        Assert.True(started.IsSuccess, started.Error?.Message);

        fixture.Provider.Resource!.Exit(0);
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        var sweep = await fixture.Processes.SweepExpiredAsync();
        Assert.Equal(1, sweep.ExpiredProcesses);

        var released = await fixture.Processes.ReleaseAsync(
            started.Value!.Handle);

        Assert.True(released.IsSuccess, released.Error?.Message);
        Assert.Equal(1, fixture.Provider.Resource.DisposeCount);

        var status = await fixture.Processes.StatusAsync(
            started.Value.Handle);
        Assert.False(status.IsSuccess);
        Assert.Equal("resource_expired", status.Error?.Code);
    }

    [Fact]
    public async Task ActiveReadLeaseDelaysReleaseUntilReadFinishes()
    {
        await using var fixture = new Fixture();
        var started = await fixture.StartIndependentAsync();
        Assert.True(started.IsSuccess, started.Error?.Message);

        var handle = started.Value!.Handle;
        var resource = fixture.Provider.Resource!;
        resource.Exit(0);
        resource.BlockReads = true;

        var readTask = Task.Run(
            async () => await fixture.Processes.ReadAsync(handle));

        Assert.True(
            resource.ReadEntered.Wait(
                TimeSpan.FromSeconds(5)));

        var releaseTask = fixture.Processes.ReleaseAsync(handle);
        Assert.False(releaseTask.IsCompleted);
        Assert.False(resource.Disposed);

        resource.ReleaseRead.Set();

        var read = await readTask;
        var released = await releaseTask;

        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.True(released.IsSuccess, released.Error?.Message);
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentReleaseCallsDisposeOnce()
    {
        await using var fixture = new Fixture();
        var started = await fixture.StartIndependentAsync();
        Assert.True(started.IsSuccess, started.Error?.Message);

        fixture.Provider.Resource!.Exit(0);

        var first = fixture.Processes.ReleaseAsync(
            started.Value!.Handle);
        var second = fixture.Processes.ReleaseAsync(
            started.Value.Handle);

        var results = await Task.WhenAll(first, second);

        Assert.All(
            results,
            result => Assert.True(
                result.IsSuccess,
                result.Error?.Message));
        Assert.Equal(1, fixture.Provider.Resource.DisposeCount);
    }

    [Fact]
    public async Task ReleaseConvergesWithWorkClose()
    {
        await using var fixture = new Fixture();
        var work = fixture.Sessions.Create(Path.GetTempPath());
        Assert.True(work.IsSuccess, work.Error?.Message);

        var started = await fixture.StartSessionOwnedAsync(
            work.Value!.Id);
        Assert.True(started.IsSuccess, started.Error?.Message);

        fixture.Provider.Resource!.Exit(0);

        var releaseTask = fixture.Processes.ReleaseAsync(
            started.Value!.Handle);
        var closeTask = fixture.Sessions.CloseAsync(
            work.Value.Id).AsTask();

        var released = await releaseTask;
        var closed = await closeTask;

        Assert.True(released.IsSuccess, released.Error?.Message);
        Assert.True(closed.IsSuccess, closed.Error?.Message);
        Assert.Equal(1, fixture.Provider.Resource.DisposeCount);

        var status = await fixture.Processes.StatusAsync(
            started.Value.Handle);
        Assert.False(status.IsSuccess);
        Assert.Equal("resource_closed", status.Error?.Code);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture()
        {
            Clock = new FakeTimeProvider(Start);
            Options = new LifetimeOptions(
                workSessionIdleTimeout: TimeSpan.FromHours(1),
                tombstoneRetention: TimeSpan.FromHours(1),
                sweepInterval: TimeSpan.FromMinutes(1),
                processPostExitRetention: TimeSpan.FromMinutes(10));
            Events = new LoomEventBus(Clock);
            Resources = new ResourceRegistry(Clock);
            Sessions = new WorkSessionManager(
                Resources,
                Events,
                Options,
                Clock);
            Invocations = new InvocationRunner(
                Events,
                Sessions,
                Clock);
            Provider = new FakeProcessProvider(Clock);
            Processes = new ProcessCapability(
                Provider,
                Resources,
                Invocations,
                Events,
                Options,
                Clock);
        }

        public FakeTimeProvider Clock { get; }
        public LifetimeOptions Options { get; }
        public LoomEventBus Events { get; }
        public ResourceRegistry Resources { get; }
        public WorkSessionManager Sessions { get; }
        public InvocationRunner Invocations { get; }
        public FakeProcessProvider Provider { get; }
        public ProcessCapability Processes { get; }

        public Task<LoomResult<ProcessStartResult>>
            StartIndependentAsync()
            => Processes.StartAsync(
                new ProcessStartRequest(
                    "fake.exe",
                    Ownership: ResourceOwnership.Independent));

        public Task<LoomResult<ProcessStartResult>>
            StartSessionOwnedAsync(WorkId workId)
            => Processes.StartAsync(
                new ProcessStartRequest(
                    "fake.exe",
                    WorkId: workId,
                    Ownership: ResourceOwnership.SessionOwned));

        public async ValueTask DisposeAsync()
        {
            Provider.Resource?.ReleaseRead.Set();
            await Sessions.DisposeAsync();
            await Resources.DisposeAsync();
        }
    }

    private sealed class FakeProcessProvider(TimeProvider clock)
        : IProcessProvider
    {
        public FakeProcessResource? Resource { get; private set; }

        public Task<LoomResult<IProcessResource>> StartAsync(
            ProcessLaunchSpec spec,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Resource = new FakeProcessResource(spec.IoMode, clock);
            return Task.FromResult(
                LoomResult<IProcessResource>.Success(Resource));
        }
    }

    private sealed class FakeProcessResource(
        ProcessIoMode ioMode,
        TimeProvider clock)
        : IProcessResource
    {
        private ManagedProcessState _state =
            ManagedProcessState.Running;
        private int? _exitCode;
        private DateTimeOffset? _exitedAt;

        public int ProcessId { get; } = 4242;
        public DateTimeOffset StartedAt { get; } =
            clock.GetUtcNow();
        public ProcessIoMode IoMode { get; } = ioMode;
        public int DisposeCount { get; private set; }
        public bool Disposed => DisposeCount != 0;
        public bool BlockReads { get; set; }
        public ManualResetEventSlim ReadEntered { get; } =
            new(initialState: false);
        public ManualResetEventSlim ReleaseRead { get; } =
            new(initialState: false);

        public void Exit(int exitCode)
        {
            _state = ManagedProcessState.Exited;
            _exitCode = exitCode;
            _exitedAt = clock.GetUtcNow();
        }

        public void SetState(ManagedProcessState state)
        {
            _state = state;
        }

        public ProcessStatusResult Snapshot(ProcessHandle handle)
            => new(
                handle,
                ProcessId,
                _state,
                _exitCode,
                StartedAt,
                _exitedAt,
                IoMode,
                null);

        public ProcessOutputReadResult Read(
            ProcessHandle handle,
            long stdoutCursor,
            long stderrCursor,
            long terminalCursor,
            int maxChars)
        {
            if (BlockReads)
            {
                ReadEntered.Set();
                ReleaseRead.Wait(TimeSpan.FromSeconds(5));
            }

            return IoMode == ProcessIoMode.Pipes
                ? new ProcessOutputReadResult(
                    Snapshot(handle),
                    IoMode,
                    Empty(stdoutCursor),
                    Empty(stderrCursor),
                    null)
                : new ProcessOutputReadResult(
                    Snapshot(handle),
                    IoMode,
                    null,
                    null,
                    Empty(terminalCursor));
        }

        public Task<LoomResult<Unit>> WriteAsync(
            string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                _state == ManagedProcessState.Running
                    ? LoomResult<Unit>.Success(Unit.Value)
                    : LoomResult<Unit>.Failure(
                        LoomErrors.Conflict(
                            "Cannot write process input because the process is not running.")));
        }

        public Task<LoomResult<Unit>> ResizeAsync(
            int columns,
            int rows,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                _state == ManagedProcessState.Running
                    ? LoomResult<Unit>.Success(Unit.Value)
                    : LoomResult<Unit>.Failure(
                        LoomErrors.Conflict(
                            "Cannot resize process because the process is not running.")));
        }

        public Task<LoomResult<Unit>> TerminateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_state == ManagedProcessState.Running)
            {
                _state = ManagedProcessState.Terminated;
                _exitCode = 1;
                _exitedAt = clock.GetUtcNow();
            }

            return Task.FromResult(
                LoomResult<Unit>.Success(Unit.Value));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            ReleaseRead.Set();
            return ValueTask.CompletedTask;
        }

        private static OutputStreamReadResult Empty(
            long requestedCursor)
            => new(
                requestedCursor,
                0,
                requestedCursor,
                requestedCursor,
                requestedCursor,
                false,
                false,
                []);
    }
}
