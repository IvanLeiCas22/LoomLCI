using LoomLCI.Core;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Python;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;

namespace LoomLCI.Core.Tests;

public sealed class PythonPackageCapabilityTests
{
    [Fact]
    public async Task PrepareBindsEnvironmentAndNextWorkerUsesIt()
    {
        await using var fixture = new Fixture();
        var work = fixture.CreateWork();

        var prepared = await fixture.Packages.PrepareAsync(
            new PythonPackagesPrepareRequest(
                work.Id,
                [new PythonPackageRequirement("NumPy", "2.5.3")],
                TimeSpan.FromMinutes(1)));

        Assert.True(prepared.IsSuccess, prepared.Error?.Message);
        Assert.Equal("env-numpy-2.5.3", prepared.Value!.EnvironmentId);
        Assert.False(prepared.Value.WorkerRestartRequired);
        Assert.Equal("numpy", prepared.Value.Packages[0].Name);

        var executed = await fixture.Python.ExecuteAsync(
            new PythonExecuteRequest(
                work.Id,
                "print('ok')",
                TimeSpan.FromSeconds(10)));

        Assert.True(executed.IsSuccess, executed.Error?.Message);
        Assert.Equal(
            "env-numpy-2.5.3",
            fixture.Runtime.LastSpec?.PackageEnvironment?.EnvironmentId);
        Assert.Equal(
            "env-numpy-2.5.3",
            Assert.Single(fixture.Runtime.Workers).PackageEnvironmentId);
    }

    [Fact]
    public async Task ChangingEnvironmentRequiresExplicitReset()
    {
        await using var fixture = new Fixture();
        var work = fixture.CreateWork();

        Assert.True(
            (await fixture.Packages.PrepareAsync(
                new PythonPackagesPrepareRequest(
                    work.Id,
                    [new PythonPackageRequirement("numpy", "2.5.3")],
                    TimeSpan.FromMinutes(1))))
            .IsSuccess);

        Assert.True(
            (await fixture.Python.ExecuteAsync(
                new PythonExecuteRequest(
                    work.Id,
                    "first",
                    TimeSpan.FromSeconds(10))))
            .IsSuccess);

        var changed = await fixture.Packages.PrepareAsync(
            new PythonPackagesPrepareRequest(
                work.Id,
                [new PythonPackageRequirement("pandas", "3.0.6")],
                TimeSpan.FromMinutes(1)));

        Assert.True(changed.IsSuccess, changed.Error?.Message);
        Assert.True(changed.Value!.WorkerRestartRequired);
        Assert.Equal("env-pandas-3.0.6", changed.Value.EnvironmentId);
        Assert.Contains(
            "env-numpy-2.5.3",
            fixture.PackageProvider.LastProtectedEnvironmentIds);
        Assert.Contains(
            "env-pandas-3.0.6",
            fixture.PackageProvider.LastProtectedEnvironmentIds);

        var blocked = await fixture.Python.ExecuteAsync(
            new PythonExecuteRequest(
                work.Id,
                "blocked",
                TimeSpan.FromSeconds(10)));

        Assert.False(blocked.IsSuccess);
        Assert.Equal("conflict", blocked.Error?.Code);
        Assert.Contains(
            "python_reset",
            blocked.Error?.Message,
            StringComparison.Ordinal);

        var reset = await fixture.Python.ResetAsync(work.Id);
        Assert.True(reset.IsSuccess, reset.Error?.Message);

        var afterReset = await fixture.Python.ExecuteAsync(
            new PythonExecuteRequest(
                work.Id,
                "after",
                TimeSpan.FromSeconds(10)));

        Assert.True(afterReset.IsSuccess, afterReset.Error?.Message);
        Assert.Equal(2, fixture.Runtime.StartCount);
        Assert.Equal(
            "env-pandas-3.0.6",
            fixture.Runtime.LastSpec?.PackageEnvironment?.EnvironmentId);
    }

    [Fact]
    public async Task EmptyPackageSetReturnsToBasePython()
    {
        await using var fixture = new Fixture();
        var work = fixture.CreateWork();

        Assert.True(
            (await fixture.Packages.PrepareAsync(
                new PythonPackagesPrepareRequest(
                    work.Id,
                    [new PythonPackageRequirement("numpy")],
                    TimeSpan.FromMinutes(1))))
            .IsSuccess);

        Assert.True(
            (await fixture.Python.ExecuteAsync(
                new PythonExecuteRequest(
                    work.Id,
                    "first",
                    TimeSpan.FromSeconds(10))))
            .IsSuccess);

        var cleared = await fixture.Packages.PrepareAsync(
            new PythonPackagesPrepareRequest(
                work.Id,
                Array.Empty<PythonPackageRequirement>(),
                TimeSpan.FromMinutes(1)));

        Assert.True(cleared.IsSuccess, cleared.Error?.Message);
        Assert.Null(cleared.Value!.EnvironmentId);
        Assert.True(cleared.Value.WorkerRestartRequired);

        Assert.True((await fixture.Python.ResetAsync(work.Id)).IsSuccess);
        Assert.True(
            (await fixture.Python.ExecuteAsync(
                new PythonExecuteRequest(
                    work.Id,
                    "base",
                    TimeSpan.FromSeconds(10))))
            .IsSuccess);

        Assert.Null(fixture.Runtime.LastSpec?.PackageEnvironment);
    }

    [Fact]
    public async Task NormalizedDuplicatePackageNamesAreRejected()
    {
        await using var fixture = new Fixture();
        var work = fixture.CreateWork();

        var result = await fixture.Packages.PrepareAsync(
            new PythonPackagesPrepareRequest(
                work.Id,
                [
                    new PythonPackageRequirement("my_pkg"),
                    new PythonPackageRequirement("my-pkg")
                ],
                TimeSpan.FromMinutes(1)));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_argument", result.Error?.Code);
        Assert.Equal(0, fixture.PackageProvider.PrepareCount);
    }

    [Fact]
    public async Task ExactVersionInputRejectsRequirementOperators()
    {
        await using var fixture = new Fixture();
        var work = fixture.CreateWork();

        var result = await fixture.Packages.PrepareAsync(
            new PythonPackagesPrepareRequest(
                work.Id,
                [new PythonPackageRequirement("numpy", ">=2.0")],
                TimeSpan.FromMinutes(1)));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_argument", result.Error?.Code);
        Assert.Equal(0, fixture.PackageProvider.PrepareCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture()
        {
            Events = new LoomEventBus();
            Resources = new ResourceRegistry();
            Sessions = new WorkSessionManager(
                Resources,
                Events);
            Invocations = new InvocationRunner(
                Events,
                Sessions);
            PackageProvider = new FakePackageProvider();
            Runtime = new FakeRuntimeProvider();

            Packages = new PythonPackageCapability(
                PackageProvider,
                Resources,
                Sessions,
                Invocations,
                Events);
            Python = new PythonCapability(
                Runtime,
                Resources,
                Invocations,
                Events);
        }

        public LoomEventBus Events { get; }
        public ResourceRegistry Resources { get; }
        public WorkSessionManager Sessions { get; }
        public InvocationRunner Invocations { get; }
        public FakePackageProvider PackageProvider { get; }
        public FakeRuntimeProvider Runtime { get; }
        public PythonPackageCapability Packages { get; }
        public PythonCapability Python { get; }

        public WorkSession CreateWork()
        {
            var result = Sessions.Create(
                Environment.CurrentDirectory,
                "python-packages-test");
            Assert.True(result.IsSuccess, result.Error?.Message);
            return result.Value!;
        }

        public ValueTask DisposeAsync()
            => Sessions.DisposeAsync();
    }

    private sealed class FakePackageProvider : IPythonPackageProvider
    {
        private int _prepareCount;

        public int PrepareCount => Volatile.Read(ref _prepareCount);
        public IReadOnlySet<string> LastProtectedEnvironmentIds { get; private set; }
            = new HashSet<string>(StringComparer.Ordinal);

        public Task<LoomResult<PythonPackagesProviderResult>> PrepareAsync(
            PythonPackagesPrepareSpec spec,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _prepareCount);

            if (spec.Packages.Count == 0)
            {
                return Task.FromResult(
                    LoomResult<PythonPackagesProviderResult>.Success(
                        new PythonPackagesProviderResult(
                            null,
                            "3.14.8",
                            true)));
            }

            var resolved = spec.Packages
                .Select(package =>
                    new PythonResolvedPackage(
                        package.Name,
                        package.Version ?? "latest"))
                .ToArray();

            var environmentId = "env-" + string.Join(
                "-",
                resolved.Select(package =>
                    $"{package.Name}-{package.Version}"));

            var environment = new PythonPackageEnvironment(
                environmentId,
                "3.14.8",
                Path.Combine(
                    Environment.CurrentDirectory,
                    "fake-site",
                    environmentId),
                resolved);

            return Task.FromResult(
                LoomResult<PythonPackagesProviderResult>.Success(
                    new PythonPackagesProviderResult(
                        environment,
                        "3.14.8",
                        false)));
        }

        public Task<LoomResult<Unit>> PruneAsync(
            IReadOnlySet<string> protectedEnvironmentIds,
            CancellationToken cancellationToken)
        {
            LastProtectedEnvironmentIds =
                new HashSet<string>(
                    protectedEnvironmentIds,
                    StringComparer.Ordinal);

            return Task.FromResult(
                LoomResult<Unit>.Success(Unit.Value));
        }
    }

    private sealed class FakeRuntimeProvider : IPythonRuntimeProvider
    {
        private int _startCount;

        public int StartCount => Volatile.Read(ref _startCount);
        public PythonWorkerStartSpec? LastSpec { get; private set; }
        public List<FakeWorker> Workers { get; } = [];

        public Task<LoomResult<IPythonWorkerResource>> StartAsync(
            PythonWorkerStartSpec spec,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCount);
            LastSpec = spec;

            var worker = new FakeWorker(
                spec.PackageEnvironment?.EnvironmentId);
            Workers.Add(worker);

            return Task.FromResult(
                LoomResult<IPythonWorkerResource>.Success(
                    worker));
        }
    }

    private sealed class FakeWorker : IPythonWorkerResource
    {
        private bool _healthy = true;

        public FakeWorker(string? packageEnvironmentId)
        {
            PackageEnvironmentId = packageEnvironmentId;
        }

        public bool IsHealthy => _healthy;
        public string? PackageEnvironmentId { get; }

        public Task<LoomResult<PythonExecutionResult>> ExecuteAsync(
            PythonWorkerExecuteSpec request,
            CancellationToken cancellationToken)
            => Task.FromResult(
                LoomResult<PythonExecutionResult>.Success(
                    new PythonExecutionResult(
                        PythonExecutionStatus.Completed,
                        request.Code,
                        string.Empty,
                        false,
                        false,
                        null)));

        public ValueTask DisposeAsync()
        {
            _healthy = false;
            return ValueTask.CompletedTask;
        }
    }
}
