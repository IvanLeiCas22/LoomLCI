using System.Text.Json;
using LoomLCI.Core;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Python;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;

namespace LoomLCI.Core.Tests;

public sealed class PythonRunManyBridgeTests
{
    private static PythonBridgeCall Call(object args)
        => new("process.run_many", JsonSerializer.SerializeToElement(args));

    [Fact]
    public async Task RunsConcurrentJobsWithinLimitAndPreservesInputOrder()
    {
        await using var f = new Fixture();
        var session = f.NewWork();
        var result = await f.Module.DispatchAsync(session.Id,
            Call(new
            {
                jobs = Enumerable.Range(0, 4)
                    .Select(i => new { id = $"job-{i}", executable = "ok.exe" }),
                max_concurrent = 2,
                job_timeout_seconds = 5,
                batch_timeout_seconds = 10,
                max_output_chars = 128
            }), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var jobs = result.Value.GetProperty("jobs").EnumerateArray().ToArray();
        Assert.Equal(4, jobs.Length);
        for (var i = 0; i < jobs.Length; i++)
        {
            Assert.Equal($"job-{i}", jobs[i].GetProperty("id").GetString());
            Assert.Equal("success", jobs[i].GetProperty("outcome").GetString());
            Assert.Equal(0, jobs[i].GetProperty("exit_code").GetInt32());
            Assert.Equal("ok", jobs[i].GetProperty("stdout").GetString());
        }
        Assert.Equal(2, f.Provider.PeakActive);
        Assert.Equal(4, f.Provider.Started);
        Assert.Equal(4, f.Provider.Disposed);
        Assert.False(result.Value.GetProperty("timed_out").GetBoolean());
    }

    [Fact]
    public async Task IsolatesJobErrorsAndPreservesNonzeroExitCode()
    {
        await using var f = new Fixture();
        var session = f.NewWork();
        var result = await f.Module.DispatchAsync(session.Id,
            Call(new
            {
                jobs = new[]
                {
                    new { id = "missing", executable = "missing.exe" },
                    new { id = "nonzero", executable = "nonzero.exe" },
                    new { id = "good", executable = "ok.exe" }
                }
            }), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var jobs = result.Value.GetProperty("jobs").EnumerateArray().ToArray();
        Assert.Equal("launch_error", jobs[0].GetProperty("outcome").GetString());
        Assert.Equal("execution_failed", jobs[0].GetProperty("error_code").GetString());
        Assert.Equal("nonzero_exit", jobs[1].GetProperty("outcome").GetString());
        Assert.Equal(7, jobs[1].GetProperty("exit_code").GetInt32());
        Assert.Equal("success", jobs[2].GetProperty("outcome").GetString());
        Assert.Equal(2, f.Provider.Disposed);
    }

    [Theory]
    [InlineData(0, 2, 30, 45, 4096)]
    [InlineData(33, 2, 30, 45, 4096)]
    [InlineData(2, 0, 30, 45, 4096)]
    [InlineData(2, 9, 30, 45, 4096)]
    [InlineData(2, 2, 0, 45, 4096)]
    [InlineData(2, 2, 30, 541, 4096)]
    [InlineData(2, 2, 30, 45, 65537)]
    public async Task InvalidLimitsFailBeforeStartingAnything(
        int count, int concurrency, int jobTimeout, int batchTimeout, int output)
    {
        await using var f = new Fixture();
        var session = f.NewWork();
        var jobs = Enumerable.Range(0, count)
            .Select(i => new { id = $"job-{i}", executable = "ok.exe" });
        var result = await f.Module.DispatchAsync(session.Id,
            Call(new
            {
                jobs,
                max_concurrent = concurrency,
                job_timeout_seconds = jobTimeout,
                batch_timeout_seconds = batchTimeout,
                max_output_chars = output
            }), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_argument", result.Error?.Code);
        Assert.Equal(0, f.Provider.Started);
    }

    [Fact]
    public async Task DuplicateIdsAndUnknownFieldsAreRejected()
    {
        await using var f = new Fixture();
        var session = f.NewWork();
        var dup = await f.Module.DispatchAsync(session.Id,
            Call(new
            {
                jobs = new[]
                {
                    new { id = "same", executable = "ok.exe" },
                    new { id = "same", executable = "ok.exe" }
                }
            }), CancellationToken.None);
        Assert.Equal("invalid_argument", dup.Error?.Code);
        var unexpected = await f.Module.DispatchAsync(session.Id,
            Call(new { jobs = new[] { new { id = "one", executable = "ok.exe", independent = true } } }),
            CancellationToken.None);
        Assert.Equal("invalid_argument", unexpected.Error?.Code);
        Assert.Equal(0, f.Provider.Started);
    }

    [Fact]
    public async Task BatchDeadlineCancelsActiveAndLeavesQueuedUnstarted()
    {
        await using var f = new Fixture(delay: TimeSpan.FromSeconds(5));
        var session = f.NewWork();
        var result = await f.Module.DispatchAsync(session.Id,
            Call(new
            {
                jobs = Enumerable.Range(0, 4)
                    .Select(i => new { id = $"job-{i}", executable = "ok.exe" }),
                max_concurrent = 2,
                job_timeout_seconds = 10,
                batch_timeout_seconds = 1
            }), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value.GetProperty("timed_out").GetBoolean());
        var jobs = result.Value.GetProperty("jobs").EnumerateArray().ToArray();
        Assert.Equal(2, jobs.Count(j => j.GetProperty("outcome").GetString() == "timeout"));
        Assert.Equal(2, jobs.Count(j => j.GetProperty("outcome").GetString() == "not_started"));
        Assert.Equal(2, f.Provider.Started);
        Assert.Equal(2, f.Provider.Disposed);
        Assert.Equal(0, f.Provider.Active);
    }

    [Fact]
    public async Task IndividualTimeoutDoesNotStopOtherJobs()
    {
        await using var f = new Fixture(delay: TimeSpan.FromSeconds(5));
        var session = f.NewWork();
        var result = await f.Module.DispatchAsync(session.Id,
            Call(new
            {
                jobs = new[]
                {
                    new { id = "slow-a", executable = "ok.exe" },
                    new { id = "slow-b", executable = "ok.exe" }
                },
                max_concurrent = 2,
                job_timeout_seconds = 1,
                batch_timeout_seconds = 4
            }), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(result.Value.GetProperty("timed_out").GetBoolean());
        foreach (var job in result.Value.GetProperty("jobs").EnumerateArray())
        {
            Assert.Equal("timeout", job.GetProperty("outcome").GetString());
            Assert.Equal("deadline_exceeded", job.GetProperty("error_code").GetString());
        }
        Assert.Equal(2, f.Provider.Disposed);
        Assert.Equal(0, f.Provider.Active);
    }

    [Fact]
    public async Task CallerCancellationWaitsForAllActiveProcessDisposals()
    {
        await using var f = new Fixture(delay: TimeSpan.FromSeconds(8));
        var session = f.NewWork();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await f.Module.DispatchAsync(session.Id,
                Call(new
                {
                    jobs = Enumerable.Range(0, 4)
                        .Select(i => new { id = $"job-{i}", executable = "ok.exe" }),
                    max_concurrent = 2,
                    batch_timeout_seconds = 20
                }), cancel.Token));

        Assert.Equal(2, f.Provider.Started);
        Assert.Equal(2, f.Provider.Disposed);
        Assert.Equal(0, f.Provider.Active);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly LoomEventBus _events = new();
        private readonly ResourceRegistry _resources = new();
        private readonly WorkSessionManager _sessions;
        public TimedProvider Provider { get; }
        public PythonProcessBridgeModule Module { get; }

        public Fixture(TimeSpan? delay = null)
        {
            _sessions = new WorkSessionManager(_resources, _events);
            var invocations = new InvocationRunner(_events, _sessions);
            Provider = new TimedProvider(delay ?? TimeSpan.FromMilliseconds(100));
            Module = new PythonProcessBridgeModule(
                new ProcessCapability(Provider, _resources, invocations, _events));
        }

        public WorkSession NewWork()
        {
            var created = _sessions.Create(Environment.CurrentDirectory);
            Assert.True(created.IsSuccess);
            return created.Value!;
        }

        public async ValueTask DisposeAsync()
        {
            await _sessions.DisposeAsync();
            await _resources.DisposeAsync();
        }
    }

    private sealed class TimedProvider(TimeSpan delay) : IProcessProvider
    {
        private int _started, _disposed, _active, _peak;
        public int Started => Volatile.Read(ref _started);
        public int Disposed => Volatile.Read(ref _disposed);
        public int Active => Volatile.Read(ref _active);
        public int PeakActive => Volatile.Read(ref _peak);

        public Task<LoomResult<IProcessResource>> StartAsync(
            ProcessLaunchSpec spec, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (spec.Executable == "missing.exe")
                return Task.FromResult(LoomResult<IProcessResource>.Failure(
                    LoomErrors.ExecutionFailed("The test executable is missing.")));
            var pid = Interlocked.Increment(ref _started);
            IProcessResource resource = new TimedResource(this, pid,
                spec.Executable == "nonzero.exe" ? 7 : 0, delay);
            return Task.FromResult(LoomResult<IProcessResource>.Success(resource));
        }

        public void Enter()
        {
            var value = Interlocked.Increment(ref _active);
            int current;
            while ((current = Volatile.Read(ref _peak)) < value &&
                   Interlocked.CompareExchange(ref _peak, value, current) != current) { }
        }
        public void Leave() => Interlocked.Decrement(ref _active);
        public void DisposedOne() => Interlocked.Increment(ref _disposed);
    }

    private sealed class TimedResource(
        TimedProvider provider, int pid, int desiredExitCode, TimeSpan delay)
        : IProcessResource
    {
        private bool _done;
        private bool _disposed;
        public int ProcessId => 5000 + pid;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public ProcessIoMode IoMode => ProcessIoMode.Pipes;
        private DateTimeOffset? _exitedAt;

        public ProcessStatusResult Snapshot(ProcessHandle handle)
            => new(handle, ProcessId,
                _done ? ManagedProcessState.Exited : ManagedProcessState.Running,
                _done ? desiredExitCode : null, StartedAt, _exitedAt,
                ProcessIoMode.Pipes, null);

        public ProcessOutputReadResult Read(ProcessHandle handle, long stdoutCursor,
            long stderrCursor, long terminalCursor, int maxChars)
            => new(Snapshot(handle), IoMode,
                new OutputStreamReadResult(0, 0, 2, 2, 2, false, false,
                    [new OutputChunk(0, "ok")]),
                new OutputStreamReadResult(0, 0, 0, 0, 0, false, false, []), null);

        public async Task WaitForExitAsync(CancellationToken token)
            => await WaitForExitAndOutputAsync(token);
        public async Task WaitForExitAndOutputAsync(CancellationToken token)
        {
            provider.Enter();
            try { await Task.Delay(delay, token); }
            finally { provider.Leave(); }
            _exitedAt = DateTimeOffset.UtcNow;
            _done = true;
        }
        public Task<LoomResult<Unit>> WriteAsync(string text, CancellationToken token)
            => Task.FromResult(LoomResult<Unit>.Success(Unit.Value));
        public Task<LoomResult<Unit>> ResizeAsync(int columns, int rows, CancellationToken token)
            => Task.FromResult(LoomResult<Unit>.Success(Unit.Value));
        public Task<LoomResult<Unit>> TerminateAsync(CancellationToken token)
            => Task.FromResult(LoomResult<Unit>.Success(Unit.Value));
        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                provider.DisposedOne();
            }
            return ValueTask.CompletedTask;
        }
    }
}
