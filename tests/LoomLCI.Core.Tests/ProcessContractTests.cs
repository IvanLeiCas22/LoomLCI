using LoomLCI.Core;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;

namespace LoomLCI.Core.Tests;

public sealed class ProcessContractTests
{
    [Fact]
    public async Task PipeModeIsDefaultAndPropagates()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest("fake.exe", WorkId: work.Value!.Id));

        Assert.True(started.IsSuccess, started.Error?.Message);
        Assert.Equal(ProcessIoMode.Pipes, started.Value!.IoMode);
        Assert.NotNull(fixture.Provider.LastSpec);
        Assert.Equal(ProcessIoMode.Pipes, fixture.Provider.LastSpec!.IoMode);
        Assert.Null(fixture.Provider.LastSpec.TerminalColumns);
        Assert.Null(fixture.Provider.LastSpec.TerminalRows);

        var status = await fixture.Processes.StatusAsync(started.Value.Handle);
        Assert.True(status.IsSuccess);
        Assert.Equal(ProcessIoMode.Pipes, status.Value!.IoMode);

        var read = await fixture.Processes.ReadAsync(started.Value.Handle);
        Assert.True(read.IsSuccess);
        Assert.Equal(ProcessIoMode.Pipes, read.Value!.IoMode);
        Assert.NotNull(read.Value.Stdout);
        Assert.NotNull(read.Value.Stderr);
        Assert.Null(read.Value.Terminal);
    }

    [Fact]
    public async Task PipeModeRejectsExplicitTerminalDimensions()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "fake.exe",
                WorkId: work.Value!.Id,
                TerminalColumns: 80));

        Assert.False(started.IsSuccess);
        Assert.Equal("invalid_argument", started.Error?.Code);
        Assert.Equal(0, fixture.Provider.StartCount);
    }

    [Fact]
    public async Task TerminalModeDefaultsToEightyByTwentyFourAndPropagates()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "fake.exe",
                WorkId: work.Value!.Id,
                IoMode: ProcessIoMode.Terminal));

        Assert.True(started.IsSuccess, started.Error?.Message);
        Assert.Equal(ProcessIoMode.Terminal, started.Value!.IoMode);
        Assert.NotNull(fixture.Provider.LastSpec);
        Assert.Equal(80, fixture.Provider.LastSpec!.TerminalColumns);
        Assert.Equal(24, fixture.Provider.LastSpec.TerminalRows);

        var read = await fixture.Processes.ReadAsync(started.Value.Handle);
        Assert.True(read.IsSuccess);
        Assert.Equal(ProcessIoMode.Terminal, read.Value!.IoMode);
        Assert.Null(read.Value.Stdout);
        Assert.Null(read.Value.Stderr);
        Assert.NotNull(read.Value.Terminal);
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(32768, 24)]
    [InlineData(80, 32768)]
    public async Task TerminalDimensionsMustFitCoord(int columns, int rows)
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "fake.exe",
                WorkId: work.Value!.Id,
                IoMode: ProcessIoMode.Terminal,
                TerminalColumns: columns,
                TerminalRows: rows));

        Assert.False(started.IsSuccess);
        Assert.Equal("invalid_argument", started.Error?.Code);
        Assert.Equal(0, fixture.Provider.StartCount);
    }

    [Fact]
    public async Task ReadRejectsCursorForInactiveStream()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var pipe = await fixture.Processes.StartAsync(
            new ProcessStartRequest("fake.exe", WorkId: work.Value!.Id));
        Assert.True(pipe.IsSuccess);

        var pipeRead = await fixture.Processes.ReadAsync(
            pipe.Value!.Handle,
            terminalCursor: 1);
        Assert.False(pipeRead.IsSuccess);
        Assert.Equal("invalid_argument", pipeRead.Error?.Code);

        var terminal = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "fake.exe",
                WorkId: work.Value.Id,
                IoMode: ProcessIoMode.Terminal));
        Assert.True(terminal.IsSuccess);

        var terminalRead = await fixture.Processes.ReadAsync(
            terminal.Value!.Handle,
            stdoutCursor: 1);
        Assert.False(terminalRead.IsSuccess);
        Assert.Equal("invalid_argument", terminalRead.Error?.Code);
    }

    [Fact]
    public async Task ResizeIsUnsupportedForPipesAndDelegatesForTerminal()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var pipe = await fixture.Processes.StartAsync(
            new ProcessStartRequest("fake.exe", WorkId: work.Value!.Id));
        Assert.True(pipe.IsSuccess);

        var pipeResize = await fixture.Processes.ResizeAsync(
            pipe.Value!.Handle,
            100,
            30);
        Assert.False(pipeResize.IsSuccess);
        Assert.Equal("unsupported", pipeResize.Error?.Code);

        var terminal = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "fake.exe",
                WorkId: work.Value.Id,
                IoMode: ProcessIoMode.Terminal));
        Assert.True(terminal.IsSuccess);

        var terminalResize = await fixture.Processes.ResizeAsync(
            terminal.Value!.Handle,
            120,
            40);
        Assert.True(terminalResize.IsSuccess, terminalResize.Error?.Message);

        var resource = Assert.IsType<FakeProcessResource>(
            fixture.Provider.Resources.Last());
        Assert.Equal((120, 40), resource.LastResize);

        var invalidResize = await fixture.Processes.ResizeAsync(
            terminal.Value.Handle,
            0,
            40);
        Assert.False(invalidResize.IsSuccess);
        Assert.Equal("invalid_argument", invalidResize.Error?.Code);
    }

    [Fact]
    public async Task RunAsyncUsesPipeModeAndDoesNotRegisterDurableResource()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        var result = await fixture.Processes.RunAsync(
            new ProcessRunRequest(
                "fake.exe",
                ["--version"],
                WorkId: work.Value!.Id,
                Timeout: TimeSpan.FromSeconds(5),
                MaxOutputChars: 128));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(4242, result.Value!.ProcessId);
        Assert.Equal(0, result.Value.ExitCode);
        Assert.Equal(ProcessIoMode.Pipes, fixture.Provider.LastSpec!.IoMode);
        Assert.Null(fixture.Provider.LastSpec.TerminalColumns);
        Assert.Null(fixture.Provider.LastSpec.TerminalRows);
        Assert.Equal(0, fixture.Resources.ActiveCount);

        var resource = Assert.IsType<FakeProcessResource>(
            Assert.Single(fixture.Provider.Resources));
        Assert.Equal(1, resource.DisposeCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public async Task RunAsyncRejectsInvalidTimeoutSeconds(int seconds)
    {
        await using var fixture = new ProcessFixture();

        var result = await fixture.Processes.RunAsync(
            new ProcessRunRequest(
                "fake.exe",
                Timeout: TimeSpan.FromSeconds(seconds)));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_argument", result.Error?.Code);
        Assert.Equal(0, fixture.Provider.StartCount);
    }

    [Fact]
    public async Task WorkCloseDuringLateProviderStartDoesNotLeakProcess()
    {
        await using var fixture = new ProcessFixture();
        var work = fixture.Sessions.Create(
            Environment.CurrentDirectory);
        Assert.True(work.IsSuccess);

        fixture.Provider.BlockStart();
        fixture.Provider.IgnoreStartCancellation = true;

        var start = fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "fake.exe",
                WorkId: work.Value!.Id));

        await fixture.Provider.StartEntered.Task;

        var closed = await fixture.Sessions.CloseAsync(
            work.Value.Id);
        Assert.True(closed.IsSuccess, closed.Error?.Message);

        fixture.Provider.ReleaseStart();

        var result = await start;

        Assert.False(result.IsSuccess);
        Assert.Equal("cancelled", result.Error?.Code);

        var resource = Assert.Single(
            fixture.Provider.Resources);
        var fake = Assert.IsType<FakeProcessResource>(resource);
        Assert.Equal(1, fake.DisposeCount);

        Assert.Empty(
            fixture.Resources.GetActiveOwnedHandles(
                ProcessCapability.ResourceKind,
                work.Value.Id));
    }

    private sealed class ProcessFixture : IAsyncDisposable
    {
        public ProcessFixture()
        {
            Events = new LoomEventBus();
            Resources = new ResourceRegistry();
            Sessions = new WorkSessionManager(Resources, Events);
            Invocations = new InvocationRunner(Events, Sessions);
            Provider = new FakeProcessProvider();
            Processes = new ProcessCapability(
                Provider,
                Resources,
                Invocations,
                Events);
        }

        public LoomEventBus Events { get; }
        public ResourceRegistry Resources { get; }
        public WorkSessionManager Sessions { get; }
        public InvocationRunner Invocations { get; }
        public FakeProcessProvider Provider { get; }
        public ProcessCapability Processes { get; }

        public ValueTask DisposeAsync() => Sessions.DisposeAsync();
    }

    private sealed class FakeProcessProvider : IProcessProvider
    {
        private TaskCompletionSource? _startEntered;
        private TaskCompletionSource? _startRelease;

        public ProcessLaunchSpec? LastSpec { get; private set; }
        public int StartCount { get; private set; }
        public bool IgnoreStartCancellation { get; set; }
        public List<IProcessResource> Resources { get; } = [];

        public TaskCompletionSource StartEntered
            => _startEntered ??= NewSignal();

        public void BlockStart()
        {
            _startEntered = NewSignal();
            _startRelease = NewSignal();
        }

        public void ReleaseStart()
            => _startRelease?.TrySetResult();

        public async Task<LoomResult<IProcessResource>> StartAsync(
            ProcessLaunchSpec spec,
            CancellationToken cancellationToken)
        {
            if (!IgnoreStartCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            LastSpec = spec;
            StartCount++;
            _startEntered?.TrySetResult();

            if (_startRelease is not null)
            {
                if (IgnoreStartCancellation)
                {
                    await _startRelease.Task.ConfigureAwait(false);
                }
                else
                {
                    await _startRelease.Task.WaitAsync(
                        cancellationToken);
                }
            }

            IProcessResource resource =
                new FakeProcessResource(spec.IoMode);
            Resources.Add(resource);

            return LoomResult<IProcessResource>.Success(resource);
        }

        private static TaskCompletionSource NewSignal()
            => new(
                TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeProcessResource(ProcessIoMode ioMode) : IProcessResource
    {
        private ManagedProcessState _state = ManagedProcessState.Running;
        private int? _exitCode;
        private DateTimeOffset? _exitedAt;

        public int ProcessId { get; } = 4242;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public ProcessIoMode IoMode { get; } = ioMode;
        public (int Columns, int Rows)? LastResize { get; private set; }
        public int DisposeCount { get; private set; }

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
            => IoMode == ProcessIoMode.Pipes
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

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state = ManagedProcessState.Exited;
            _exitCode = 0;
            _exitedAt = DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        }

        public Task WaitForExitAndOutputAsync(CancellationToken cancellationToken)
            => WaitForExitAsync(cancellationToken);

        public Task<LoomResult<Unit>> WriteAsync(
            string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(LoomResult<Unit>.Success(Unit.Value));
        }

        public Task<LoomResult<Unit>> ResizeAsync(
            int columns,
            int rows,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IoMode != ProcessIoMode.Terminal)
            {
                return Task.FromResult(
                    LoomResult<Unit>.Failure(
                        LoomErrors.Unsupported(
                            "Pipe-based processes do not support terminal resize.")));
            }

            LastResize = (columns, rows);
            return Task.FromResult(LoomResult<Unit>.Success(Unit.Value));
        }

        public Task<LoomResult<Unit>> TerminateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(LoomResult<Unit>.Success(Unit.Value));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        private static OutputStreamReadResult Empty(long requestedCursor)
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
