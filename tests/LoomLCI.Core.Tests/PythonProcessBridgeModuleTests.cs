using System.Text.Json;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Python;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;

namespace LoomLCI.Core.Tests;

public sealed class PythonProcessBridgeModuleTests
{
    [Fact]
    public async Task ExposesExpectedMethods()
    {
        await using var fixture = new ProcessBridgeFixture();

        Assert.Equal(
            [
                "process.read",
                "process.release",
                "process.resize",
                "process.run",
                "process.start",
                "process.status",
                "process.terminate",
                "process.write"
            ],
            fixture.Module.Methods);
    }

    [Fact]
    public async Task RunUsesCurrentWorkSessionAndMapsFlatSnakeCaseResult()
    {
        await using var fixture = new ProcessBridgeFixture();
        var work = fixture.CreateWork();

        var result = await fixture.Module.DispatchAsync(
            work.Id,
            Call(
                "process.run",
                new
                {
                    executable = "fake.exe",
                    arguments = new[] { "--probe" },
                    working_directory = ".",
                    timeout_seconds = 5,
                    max_output_chars = 128
                }),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            fixture.BaseDirectory,
            fixture.Provider.LastSpec?.WorkingDirectory);
        Assert.Equal(
            ProcessIoMode.Pipes,
            fixture.Provider.LastSpec?.IoMode);

        var root = result.Value;
        Assert.Equal(
            7,
            root.GetProperty("exit_code").GetInt32());
        Assert.Equal(
            "stdout",
            root.GetProperty("stdout").GetString());
        Assert.Equal(
            "stderr",
            root.GetProperty("stderr").GetString());
        Assert.False(
            root.TryGetProperty("exitCode", out _));
    }

    [Fact]
    public async Task StartForcesSessionOwnedAndRejectsIndependentField()
    {
        await using var fixture = new ProcessBridgeFixture();
        var work = fixture.CreateWork();

        var started = await fixture.Module.DispatchAsync(
            work.Id,
            Call(
                "process.start",
                new
                {
                    executable = "fake.exe",
                    io_mode = "pipes"
                }),
            CancellationToken.None);

        Assert.True(started.IsSuccess, started.Error?.Message);

        var handle = started.Value
            .GetProperty("process_handle")
            .GetString();
        Assert.False(string.IsNullOrWhiteSpace(handle));

        var metadata = fixture.Resources.Inspect(
            new ResourceHandle(handle!),
            ProcessCapability.ResourceKind);
        Assert.True(metadata.IsSuccess, metadata.Error?.Message);
        Assert.Equal(
            ResourceOwnership.SessionOwned,
            metadata.Value!.Ownership);
        Assert.Equal(work.Id, metadata.Value.OwnerWorkId);

        var invalid = await fixture.Module.DispatchAsync(
            work.Id,
            Call(
                "process.start",
                new
                {
                    executable = "fake.exe",
                    independent = true
                }),
            CancellationToken.None);

        Assert.False(invalid.IsSuccess);
        Assert.Equal(
            "invalid_argument",
            invalid.Error?.Code);
    }

    [Fact]
    public async Task OtherWorkSessionHandleIsDenied()
    {
        await using var fixture = new ProcessBridgeFixture();
        var owner = fixture.CreateWork();
        var other = fixture.CreateWork();

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "fake.exe",
                WorkId: owner.Id));
        Assert.True(started.IsSuccess, started.Error?.Message);

        var result = await fixture.Module.DispatchAsync(
            other.Id,
            Call(
                "process.status",
                new
                {
                    process_handle =
                        started.Value!.Handle.Value
                }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "access_denied",
            result.Error?.Code);
        Assert.Equal(
            "process_handle_not_owned_by_work_session",
            result.Error?.Details?["reason"]);
    }

    [Fact]
    public async Task IndependentHandleIsDenied()
    {
        await using var fixture = new ProcessBridgeFixture();
        var work = fixture.CreateWork();

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "fake.exe",
                Ownership: ResourceOwnership.Independent));
        Assert.True(started.IsSuccess, started.Error?.Message);

        var result = await fixture.Module.DispatchAsync(
            work.Id,
            Call(
                "process.status",
                new
                {
                    process_handle =
                        started.Value!.Handle.Value
                }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "access_denied",
            result.Error?.Code);
    }

    [Fact]
    public async Task SameWorkSessionCanOperateProcessCreatedOutsideBridge()
    {
        await using var fixture = new ProcessBridgeFixture();
        var work = fixture.CreateWork();

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "fake.exe",
                WorkId: work.Id));
        Assert.True(started.IsSuccess, started.Error?.Message);

        var result = await fixture.Module.DispatchAsync(
            work.Id,
            Call(
                "process.status",
                new
                {
                    process_handle =
                        started.Value!.Handle.Value
                }),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            started.Value.Handle.Value,
            result.Value
                .GetProperty("process_handle")
                .GetString());
    }

    [Fact]
    public async Task DurableOperationsReturnBooleansAndReleaseIsIdempotent()
    {
        await using var fixture = new ProcessBridgeFixture();
        var work = fixture.CreateWork();

        var started = await fixture.Module.DispatchAsync(
            work.Id,
            Call(
                "process.start",
                new
                {
                    executable = "fake.exe",
                    io_mode = "terminal",
                    terminal_columns = 80,
                    terminal_rows = 24
                }),
            CancellationToken.None);

        Assert.True(started.IsSuccess, started.Error?.Message);
        var handle = started.Value
            .GetProperty("process_handle")
            .GetString()!;

        Assert.True(
            (await fixture.Module.DispatchAsync(
                work.Id,
                Call(
                    "process.write",
                    new
                    {
                        process_handle = handle,
                        text = "hello"
                    }),
                CancellationToken.None))
            .Value.GetBoolean());

        Assert.True(
            (await fixture.Module.DispatchAsync(
                work.Id,
                Call(
                    "process.resize",
                    new
                    {
                        process_handle = handle,
                        columns = 100,
                        rows = 30
                    }),
                CancellationToken.None))
            .Value.GetBoolean());

        Assert.True(
            (await fixture.Module.DispatchAsync(
                work.Id,
                Call(
                    "process.terminate",
                    new
                    {
                        process_handle = handle
                    }),
                CancellationToken.None))
            .Value.GetBoolean());

        var release = await fixture.Module.DispatchAsync(
            work.Id,
            Call(
                "process.release",
                new
                {
                    process_handle = handle
                }),
            CancellationToken.None);
        Assert.True(release.IsSuccess, release.Error?.Message);
        Assert.True(release.Value.GetBoolean());

        var repeated = await fixture.Module.DispatchAsync(
            work.Id,
            Call(
                "process.release",
                new
                {
                    process_handle = handle
                }),
            CancellationToken.None);
        Assert.True(
            repeated.IsSuccess,
            repeated.Error?.Message);
        Assert.True(repeated.Value.GetBoolean());
    }

    [Fact]
    public async Task MissingClosedAndExpiredErrorsArePreservedForActiveOperations()
    {
        await using var fixture = new ProcessBridgeFixture();
        var work = fixture.CreateWork();

        var missing = await fixture.Module.DispatchAsync(
            work.Id,
            Call(
                "process.status",
                new
                {
                    process_handle = "proc_missing"
                }),
            CancellationToken.None);
        Assert.False(missing.IsSuccess);
        Assert.Equal(
            "not_found",
            missing.Error?.Code);

        var started = await fixture.Processes.StartAsync(
            new ProcessStartRequest(
                "fake.exe",
                WorkId: work.Id));
        Assert.True(started.IsSuccess);

        await fixture.Resources.CloseAsync(
            started.Value!.Handle.AsResourceHandle());

        var closed = await fixture.Module.DispatchAsync(
            work.Id,
            Call(
                "process.status",
                new
                {
                    process_handle =
                        started.Value.Handle.Value
                }),
            CancellationToken.None);
        Assert.False(closed.IsSuccess);
        Assert.Equal(
            "resource_closed",
            closed.Error?.Code);
    }

    private static PythonBridgeCall Call(
        string method,
        object arguments)
        => new(
            method,
            JsonSerializer.SerializeToElement(
                arguments));

    private sealed class ProcessBridgeFixture
        : IAsyncDisposable
    {
        public ProcessBridgeFixture()
        {
            BaseDirectory = Path.GetFullPath(
                Environment.CurrentDirectory);

            Events = new LoomEventBus();
            Resources = new ResourceRegistry();
            Sessions = new WorkSessionManager(
                Resources,
                Events);
            Invocations = new InvocationRunner(
                Events,
                Sessions);
            Provider = new FakeProcessProvider();
            Processes = new ProcessCapability(
                Provider,
                Resources,
                Invocations,
                Events);
            Module = new PythonProcessBridgeModule(
                Processes);
        }

        public string BaseDirectory { get; }
        public LoomEventBus Events { get; }
        public ResourceRegistry Resources { get; }
        public WorkSessionManager Sessions { get; }
        public InvocationRunner Invocations { get; }
        public FakeProcessProvider Provider { get; }
        public ProcessCapability Processes { get; }
        public PythonProcessBridgeModule Module { get; }

        public WorkSession CreateWork()
        {
            var created = Sessions.Create(
                BaseDirectory);
            Assert.True(
                created.IsSuccess,
                created.Error?.Message);
            return created.Value!;
        }

        public async ValueTask DisposeAsync()
        {
            await Sessions.DisposeAsync();
            await Resources.DisposeAsync();
        }
    }

    private sealed class FakeProcessProvider
        : IProcessProvider
    {
        private int _nextPid = 5000;

        public ProcessLaunchSpec? LastSpec { get; private set; }

        public Task<LoomResult<IProcessResource>> StartAsync(
            ProcessLaunchSpec spec,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSpec = spec;

            return Task.FromResult(
                LoomResult<IProcessResource>.Success(
                    new FakeProcessResource(
                        Interlocked.Increment(
                            ref _nextPid),
                        spec.IoMode)));
        }
    }

    private sealed class FakeProcessResource(
        int processId,
        ProcessIoMode ioMode)
        : IProcessResource
    {
        private ManagedProcessState _state =
            ManagedProcessState.Running;
        private int? _exitCode;
        private DateTimeOffset? _exitedAt;

        public int ProcessId { get; } = processId;
        public DateTimeOffset StartedAt { get; } =
            DateTimeOffset.UtcNow;
        public ProcessIoMode IoMode { get; } = ioMode;
        public string? LastWrite { get; private set; }
        public (int Columns, int Rows)? LastResize { get; private set; }

        public ProcessStatusResult Snapshot(
            ProcessHandle handle)
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
            var stdout = IoMode == ProcessIoMode.Pipes
                ? Stream(
                    stdoutCursor,
                    "stdout")
                : null;
            var stderr = IoMode == ProcessIoMode.Pipes
                ? Stream(
                    stderrCursor,
                    "stderr")
                : null;
            var terminal = IoMode == ProcessIoMode.Terminal
                ? Stream(
                    terminalCursor,
                    "terminal")
                : null;

            return new ProcessOutputReadResult(
                Snapshot(handle),
                IoMode,
                stdout,
                stderr,
                terminal);
        }

        public Task WaitForExitAndOutputAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state = ManagedProcessState.Exited;
            _exitCode = 7;
            _exitedAt = DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        }

        public Task<LoomResult<Unit>> WriteAsync(
            string text,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastWrite = text;
            return Task.FromResult(
                LoomResult<Unit>.Success(
                    Unit.Value));
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
            return Task.FromResult(
                LoomResult<Unit>.Success(
                    Unit.Value));
        }

        public Task<LoomResult<Unit>> TerminateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state = ManagedProcessState.Terminated;
            _exitCode ??= -1;
            _exitedAt ??= DateTimeOffset.UtcNow;

            return Task.FromResult(
                LoomResult<Unit>.Success(
                    Unit.Value));
        }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;

        private static OutputStreamReadResult Stream(
            long cursor,
            string text)
        {
            var next = cursor + text.Length;
            return new OutputStreamReadResult(
                cursor,
                cursor,
                next,
                next,
                next,
                false,
                false,
                [
                    new OutputChunk(
                        cursor,
                        text)
                ]);
        }
    }
}
