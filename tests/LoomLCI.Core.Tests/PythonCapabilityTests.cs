using LoomLCI.Core;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Lifetime;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Python;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;
using Microsoft.Extensions.Time.Testing;

namespace LoomLCI.Core.Tests;

public sealed class PythonCapabilityTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 10, 4, 12, 0, 0, TimeSpan.FromHours(-3));

    [Fact]
    public async Task FirstExecuteCreatesWorkerAndSecondReusesIt()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork();

        var first = await fixture.ExecuteAsync(work.Id, "x = 41");
        var second = await fixture.ExecuteAsync(work.Id, "print(x + 1)");

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(1, fixture.Provider.StartCount);

        var worker = Assert.Single(fixture.Provider.Workers);
        Assert.Equal(2, worker.ExecuteCount);
        Assert.Equal(work.Id, fixture.Provider.LastSpec?.WorkId);
        Assert.Equal(work.BaseDirectory, fixture.Provider.LastSpec?.WorkingDirectory);
    }

    [Fact]
    public async Task DifferentSessionsReceiveDifferentWorkers()
    {
        await using var fixture = new PythonFixture();
        var firstWork = fixture.CreateWork();
        var secondWork = fixture.CreateWork();

        Assert.True((await fixture.ExecuteAsync(firstWork.Id, "x = 1")).IsSuccess);
        Assert.True((await fixture.ExecuteAsync(secondWork.Id, "x = 2")).IsSuccess);

        Assert.Equal(2, fixture.Provider.StartCount);
        Assert.Equal(2, fixture.Provider.Workers.Count);
        Assert.NotSame(
            fixture.Provider.Workers[0],
            fixture.Provider.Workers[1]);
    }

    [Fact]
    public async Task ConcurrentFirstExecuteCreatesExactlyOneWorker()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork();

        fixture.Provider.BlockStart();

        var first = fixture.ExecuteAsync(work.Id, "first");
        await fixture.Provider.StartEntered.Task;

        var second = fixture.ExecuteAsync(work.Id, "second");

        await Task.Yield();
        Assert.Equal(1, fixture.Provider.StartCount);

        fixture.Provider.ReleaseStart();

        var results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Message));
        Assert.Equal(1, fixture.Provider.StartCount);
        Assert.Single(fixture.Provider.Workers);
    }

    [Fact]
    public async Task ConcurrentExecuteOnSameWorkerReturnsBusy()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork();

        var worker = fixture.Provider.NextWorker();
        worker.BlockExecution();

        var first = fixture.ExecuteAsync(work.Id, "first");
        await worker.ExecutionEntered.Task;

        var second = await fixture.ExecuteAsync(work.Id, "second");

        Assert.False(second.IsSuccess);
        Assert.Equal("busy", second.Error?.Code);

        worker.ReleaseExecution();

        var firstResult = await first;
        Assert.True(firstResult.IsSuccess, firstResult.Error?.Message);
        Assert.Equal(1, fixture.Provider.StartCount);
    }

    [Fact]
    public async Task PythonExceptionIsSuccessfulExecutionAndWorkerRemainsReusable()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork();

        var worker = fixture.Provider.NextWorker();
        worker.Behavior = (_, _) => Task.FromResult(
            LoomResult<PythonExecutionResult>.Success(
                new PythonExecutionResult(
                    PythonExecutionStatus.Exception,
                    "",
                    "",
                    false,
                    false,
                    new PythonExceptionInfo(
                        "ValueError",
                        "boom",
                        "Traceback..."))));

        var failedCode = await fixture.ExecuteAsync(work.Id, "raise ValueError('boom')");

        Assert.True(failedCode.IsSuccess, failedCode.Error?.Message);
        Assert.Equal(PythonExecutionStatus.Exception, failedCode.Value!.Status);
        Assert.Equal("ValueError", failedCode.Value.Exception?.Type);
        Assert.True(worker.IsHealthy);

        worker.Behavior = null;
        var next = await fixture.ExecuteAsync(work.Id, "print('still alive')");

        Assert.True(next.IsSuccess, next.Error?.Message);
        Assert.Equal(1, fixture.Provider.StartCount);
        Assert.Equal(2, worker.ExecuteCount);
    }

    [Fact]
    public async Task UnhealthyWorkerIsDiscardedAndNextExecuteRecreatesIt()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork();

        var firstWorker = fixture.Provider.NextWorker();
        firstWorker.Behavior = (_, _) =>
        {
            firstWorker.MarkUnhealthy();
            return Task.FromResult(
                LoomResult<PythonExecutionResult>.Failure(
                    LoomErrors.ExecutionFailed("worker failed")));
        };

        var first = await fixture.ExecuteAsync(work.Id, "boom");

        Assert.False(first.IsSuccess);
        Assert.Equal("execution_failed", first.Error?.Code);
        Assert.Equal(1, firstWorker.DisposeCount);

        var secondWorker = fixture.Provider.NextWorker();
        var second = await fixture.ExecuteAsync(work.Id, "recovered");

        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(2, fixture.Provider.StartCount);
        Assert.Same(secondWorker, fixture.Provider.Workers[1]);
    }

    [Fact]
    public async Task ResetWithoutWorkerIsIdempotent()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork();

        var first = await fixture.Python.ResetAsync(work.Id);
        var second = await fixture.Python.ResetAsync(work.Id);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(0, fixture.Provider.StartCount);
    }

    [Fact]
    public async Task ResetDiscardsNamespaceAndNextExecuteCreatesNewWorker()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork();

        Assert.True((await fixture.ExecuteAsync(work.Id, "x = 1")).IsSuccess);
        var firstWorker = Assert.Single(fixture.Provider.Workers);

        var reset = await fixture.Python.ResetAsync(work.Id);
        Assert.True(reset.IsSuccess, reset.Error?.Message);
        Assert.Equal(1, firstWorker.DisposeCount);

        Assert.True((await fixture.ExecuteAsync(work.Id, "x = 2")).IsSuccess);

        Assert.Equal(2, fixture.Provider.StartCount);
        Assert.Equal(2, fixture.Provider.Workers.Count);
    }

    [Fact]
    public async Task ResetWaitsForActiveExecutionLeaseAndDisposesOnce()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork();

        var worker = fixture.Provider.NextWorker();
        worker.BlockExecution();

        var execution = fixture.ExecuteAsync(work.Id, "long");
        await worker.ExecutionEntered.Task;

        var reset = fixture.Python.ResetAsync(work.Id);
        await Task.Yield();

        Assert.False(reset.IsCompleted);
        Assert.Equal(0, worker.DisposeCount);

        worker.ReleaseExecution();

        Assert.True((await execution).IsSuccess);
        Assert.True((await reset).IsSuccess);
        Assert.Equal(1, worker.DisposeCount);
    }

    [Fact]
    public async Task WorkCloseDuringLateProviderStartDoesNotLeakWorker()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork();

        fixture.Provider.BlockStart();
        fixture.Provider.IgnoreStartCancellation = true;

        var execution = fixture.ExecuteAsync(work.Id, "x = 1");
        await fixture.Provider.StartEntered.Task;

        var closed = await fixture.Sessions.CloseAsync(work.Id);
        Assert.True(closed.IsSuccess, closed.Error?.Message);

        fixture.Provider.ReleaseStart();

        var result = await execution;

        Assert.False(result.IsSuccess);
        Assert.Equal("cancelled", result.Error?.Code);
        var worker = Assert.Single(fixture.Provider.Workers);
        Assert.Equal(1, worker.DisposeCount);
        Assert.Empty(
            fixture.Resources.GetActiveOwnedHandles(
                PythonCapability.ResourceKind,
                work.Id));
    }

    [Fact]
    public async Task WorkCloseDisposesSessionOwnedWorkerOnce()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork();

        Assert.True((await fixture.ExecuteAsync(work.Id, "x = 1")).IsSuccess);
        var worker = Assert.Single(fixture.Provider.Workers);

        var closed = await fixture.Sessions.CloseAsync(work.Id);

        Assert.True(closed.IsSuccess, closed.Error?.Message);
        Assert.Equal(1, worker.DisposeCount);
        Assert.Empty(
            fixture.Resources.GetActiveOwnedHandles(
                PythonCapability.ResourceKind,
                work.Id));
    }

    [Fact]
    public async Task WorkExpiryDisposesWorker()
    {
        var clock = new FakeTimeProvider(Start);
        await using var fixture = new PythonFixture(
            clock,
            new LifetimeOptions(
                workSessionIdleTimeout: TimeSpan.FromMinutes(1),
                tombstoneRetention: TimeSpan.FromMinutes(10),
                sweepInterval: TimeSpan.FromSeconds(10)));

        var work = fixture.CreateWork();
        Assert.True((await fixture.ExecuteAsync(work.Id, "x = 1")).IsSuccess);
        var worker = Assert.Single(fixture.Provider.Workers);

        clock.Advance(TimeSpan.FromMinutes(2));
        var sweep = await fixture.Sessions.SweepExpiredAsync();

        Assert.Equal(1, sweep.ExpiredSessions);
        Assert.Equal(1, worker.DisposeCount);
        Assert.Equal(WorkSessionState.Expired, work.State);
    }

    [Fact]
    public async Task TimeoutReturnsDeadlineExceededAndDiscardsWorker()
    {
        var clock = new FakeTimeProvider(Start);
        await using var fixture = new PythonFixture(clock);
        var work = fixture.CreateWork();

        var worker = fixture.Provider.NextWorker();
        worker.Behavior = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        };

        var execution = fixture.ExecuteAsync(
            work.Id,
            "while True: pass",
            TimeSpan.FromSeconds(30));

        await worker.ExecutionEntered.Task;
        clock.Advance(TimeSpan.FromSeconds(31));

        var result = await execution;

        Assert.False(result.IsSuccess);
        Assert.Equal("deadline_exceeded", result.Error?.Code);
        Assert.Equal(1, worker.DisposeCount);
        Assert.Empty(
            fixture.Resources.GetActiveOwnedHandles(
                PythonCapability.ResourceKind,
                work.Id));
    }

    [Fact]
    public async Task CallerCancellationReturnsCancelledAndDiscardsWorker()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork();

        var worker = fixture.Provider.NextWorker();
        worker.Behavior = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        };

        using var cancellation = new CancellationTokenSource();
        var execution = fixture.Python.ExecuteAsync(
            new PythonExecuteRequest(
                work.Id,
                "long",
                PythonCapability.DefaultTimeout,
                PythonCapability.DefaultMaxOutputChars),
            cancellation.Token);

        await worker.ExecutionEntered.Task;
        cancellation.Cancel();

        var result = await execution;

        Assert.False(result.IsSuccess);
        Assert.Equal("cancelled", result.Error?.Code);
        Assert.Equal(1, worker.DisposeCount);
    }

    [Fact]
    public async Task ActiveOwnedLookupFiltersKindOwnerAndTerminalResources()
    {
        await using var resources = new ResourceRegistry();
        var owner = WorkId.Create();
        var otherOwner = WorkId.Create();

        var active = resources.Register(
            "a",
            "python_worker",
            new object(),
            owner,
            ResourceOwnership.SessionOwned,
            () => ValueTask.CompletedTask);
        resources.Register(
            "b",
            "other",
            new object(),
            owner,
            ResourceOwnership.SessionOwned,
            () => ValueTask.CompletedTask);
        resources.Register(
            "c",
            "python_worker",
            new object(),
            otherOwner,
            ResourceOwnership.SessionOwned,
            () => ValueTask.CompletedTask);

        Assert.Equal(
            [active],
            resources.GetActiveOwnedHandles("python_worker", owner));

        Assert.True((await resources.CloseAsync(active)).IsSuccess);
        Assert.Empty(resources.GetActiveOwnedHandles("python_worker", owner));

        resources.Register(
            "d",
            "python_worker",
            new object(),
            owner,
            ResourceOwnership.SessionOwned,
            () => ValueTask.CompletedTask);

        await resources.ExpireOwnedAsync(owner);

        Assert.Empty(resources.GetActiveOwnedHandles("python_worker", owner));
    }

    [Fact]
    public async Task DuplicateActiveWorkersReturnInternalErrorInsteadOfChoosingOne()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork();

        fixture.Resources.Register(
            "pyw",
            PythonCapability.ResourceKind,
            new FakePythonWorker(),
            work.Id,
            ResourceOwnership.SessionOwned,
            () => ValueTask.CompletedTask);
        fixture.Resources.Register(
            "pyw",
            PythonCapability.ResourceKind,
            new FakePythonWorker(),
            work.Id,
            ResourceOwnership.SessionOwned,
            () => ValueTask.CompletedTask);

        var result = await fixture.ExecuteAsync(work.Id, "x = 1");

        Assert.False(result.IsSuccess);
        Assert.Equal("internal", result.Error?.Code);
        Assert.Contains("expected at most one", result.Error?.Message);
        Assert.Equal(0, fixture.Provider.StartCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteRejectsEmptyCode(string code)
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork();

        var result = await fixture.ExecuteAsync(work.Id, code);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_argument", result.Error?.Code);
        Assert.Equal(0, fixture.Provider.StartCount);
    }

    [Fact]
    public async Task ExecuteValidatesTimeoutAndOutputLimitBeforeStartingWorker()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork();

        var invalidTimeout = await fixture.Python.ExecuteAsync(
            new PythonExecuteRequest(
                work.Id,
                "x = 1",
                TimeSpan.Zero,
                PythonCapability.DefaultMaxOutputChars));
        var invalidOutput = await fixture.Python.ExecuteAsync(
            new PythonExecuteRequest(
                work.Id,
                "x = 1",
                PythonCapability.DefaultTimeout,
                PythonCapability.MaxOutputChars + 1));

        Assert.False(invalidTimeout.IsSuccess);
        Assert.Equal("invalid_argument", invalidTimeout.Error?.Code);
        Assert.False(invalidOutput.IsSuccess);
        Assert.Equal("invalid_argument", invalidOutput.Error?.Code);
        Assert.Equal(0, fixture.Provider.StartCount);
    }

    private sealed class PythonFixture : IAsyncDisposable
    {
        public PythonFixture(
            TimeProvider? timeProvider = null,
            LifetimeOptions? lifetimeOptions = null)
        {
            Clock = timeProvider ?? TimeProvider.System;
            Events = new LoomEventBus(Clock);
            Resources = new ResourceRegistry(Clock);
            Sessions = new WorkSessionManager(
                Resources,
                Events,
                lifetimeOptions,
                Clock);
            Invocations = new InvocationRunner(
                Events,
                Sessions,
                Clock);
            Provider = new FakePythonRuntimeProvider();
            Python = new PythonCapability(
                Provider,
                Resources,
                Invocations,
                Events);
        }

        public TimeProvider Clock { get; }
        public LoomEventBus Events { get; }
        public ResourceRegistry Resources { get; }
        public WorkSessionManager Sessions { get; }
        public InvocationRunner Invocations { get; }
        public FakePythonRuntimeProvider Provider { get; }
        public PythonCapability Python { get; }

        public WorkSession CreateWork()
        {
            var result = Sessions.Create(Environment.CurrentDirectory);
            Assert.True(result.IsSuccess, result.Error?.Message);
            return result.Value!;
        }

        public Task<LoomResult<PythonExecutionResult>> ExecuteAsync(
            WorkId workId,
            string code,
            TimeSpan? timeout = null)
            => Python.ExecuteAsync(
                new PythonExecuteRequest(
                    workId,
                    code,
                    timeout ?? PythonCapability.DefaultTimeout,
                    PythonCapability.DefaultMaxOutputChars));

        public ValueTask DisposeAsync() => Sessions.DisposeAsync();
    }

    private sealed class FakePythonRuntimeProvider : IPythonRuntimeProvider
    {
        private FakePythonWorker? _nextWorker;
        private TaskCompletionSource? _startEntered;
        private TaskCompletionSource? _startRelease;
        private int _startCount;

        public int StartCount => Volatile.Read(ref _startCount);
        public List<FakePythonWorker> Workers { get; } = [];
        public PythonWorkerStartSpec? LastSpec { get; private set; }
        public bool IgnoreStartCancellation { get; set; }
        public TaskCompletionSource StartEntered
            => _startEntered ??= NewSignal();

        public FakePythonWorker NextWorker()
        {
            var worker = new FakePythonWorker();
            _nextWorker = worker;
            return worker;
        }

        public void BlockStart()
        {
            _startEntered = NewSignal();
            _startRelease = NewSignal();
        }

        public void ReleaseStart() => _startRelease?.TrySetResult();

        public async Task<LoomResult<IPythonWorkerResource>> StartAsync(
            PythonWorkerStartSpec spec,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCount);
            LastSpec = spec;
            _startEntered?.TrySetResult();

            if (_startRelease is not null)
            {
                if (IgnoreStartCancellation)
                {
                    await _startRelease.Task;
                }
                else
                {
                    await _startRelease.Task.WaitAsync(cancellationToken);
                }
            }

            var worker = _nextWorker ?? new FakePythonWorker();
            _nextWorker = null;
            Workers.Add(worker);

            return LoomResult<IPythonWorkerResource>.Success(worker);
        }
    }

    private sealed class FakePythonWorker : IPythonWorkerResource
    {
        private int _executing;
        private int _executeCount;
        private int _disposeCount;
        private bool _healthy = true;
        private TaskCompletionSource _executionEntered = NewSignal();
        private TaskCompletionSource? _executionRelease;

        public bool IsHealthy => Volatile.Read(ref _healthy);
        public int ExecuteCount => Volatile.Read(ref _executeCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public TaskCompletionSource ExecutionEntered => _executionEntered;

        public Func<
            PythonWorkerExecuteSpec,
            CancellationToken,
            Task<LoomResult<PythonExecutionResult>>>? Behavior { get; set; }

        public void BlockExecution()
        {
            _executionEntered = NewSignal();
            _executionRelease = NewSignal();
        }

        public void ReleaseExecution() => _executionRelease?.TrySetResult();

        public void MarkUnhealthy() => Volatile.Write(ref _healthy, false);

        public async Task<LoomResult<PythonExecutionResult>> ExecuteAsync(
            PythonWorkerExecuteSpec request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
            {
                return LoomResult<PythonExecutionResult>.Failure(
                    LoomErrors.Busy(
                        "Python worker already has an active execution."));
            }

            Interlocked.Increment(ref _executeCount);
            _executionEntered?.TrySetResult();

            try
            {
                if (_executionRelease is not null)
                {
                    await _executionRelease.Task.WaitAsync(cancellationToken);
                }

                if (Behavior is not null)
                {
                    return await Behavior(request, cancellationToken);
                }

                return LoomResult<PythonExecutionResult>.Success(
                    new PythonExecutionResult(
                        PythonExecutionStatus.Completed,
                        request.Code,
                        "",
                        false,
                        false,
                        null));
            }
            finally
            {
                Volatile.Write(ref _executing, 0);
            }
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            Volatile.Write(ref _healthy, false);
            return ValueTask.CompletedTask;
        }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
