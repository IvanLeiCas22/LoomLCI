#pragma warning disable CA1416 // LoomLCI.Windows.Tests exercises the Windows-specific backend.

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LoomLCI.Core;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Python;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;
using LoomLCI.Windows.Processes;
using LoomLCI.Windows.Python;

namespace LoomLCI.Windows.Tests;

public sealed class PythonRuntimeProviderTests
{
    [Fact]
    public void EmbeddedRuntimeAssetsMatchPinnedManifest()
    {
        var manifest = PythonRuntimeAssets.Manifest;

        Assert.Equal("3.14.8", manifest.Version);
        Assert.Equal("amd64", manifest.Architecture);
        Assert.Equal("embed", manifest.Distribution);
        Assert.Equal(64, manifest.Sha256.Length);
        Assert.NotEmpty(PythonRuntimeAssets.WorkerBytes);
        Assert.Equal(64, PythonRuntimeAssets.WorkerSha256.Length);
        Assert.NotEmpty(PythonRuntimeAssets.BridgeBytes);
        Assert.Equal(64, PythonRuntimeAssets.BridgeSha256.Length);
    }

    [Fact]
    public async Task ProvisionerInstallsVerifiedArchiveAndReusesIt()
    {
        var root = TemporaryDirectory();
        var archive = CreateFakeRuntimeArchive();
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(archive));
        var handler = new StaticArchiveHandler(archive);
        using var http = new HttpClient(handler);
        var manifest = FakeManifest(hash);

        try
        {
            var provisioner = new PythonRuntimeProvisioner(
                http,
                root,
                manifest,
                Encoding.UTF8.GetBytes("print('worker')\n"));

            var first = await provisioner.EnsureAsync(
                CancellationToken.None);
            var second = await provisioner.EnsureAsync(
                CancellationToken.None);

            Assert.True(first.IsSuccess, first.Error?.Message);
            Assert.True(second.IsSuccess, second.Error?.Message);
            Assert.Equal(1, handler.RequestCount);
            Assert.True(File.Exists(
                first.Value!.PythonExecutablePath));
            Assert.True(File.Exists(
                first.Value.WorkerScriptPath));
            Assert.True(File.Exists(
                first.Value.BridgeScriptPath));
            Assert.True(File.Exists(Path.Combine(
                Path.GetDirectoryName(
                    first.Value.PythonExecutablePath)!,
                ".loom-runtime.json")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ProvisionerConcurrentEnsureDownloadsOnce()
    {
        var root = TemporaryDirectory();
        var archive = CreateFakeRuntimeArchive();
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(archive));
        var handler = new StaticArchiveHandler(
            archive,
            TimeSpan.FromMilliseconds(100));
        using var http = new HttpClient(handler);

        try
        {
            var provisioner = new PythonRuntimeProvisioner(
                http,
                root,
                FakeManifest(hash),
                Encoding.UTF8.GetBytes("worker"));

            var results = await Task.WhenAll(
                provisioner.EnsureAsync(CancellationToken.None),
                provisioner.EnsureAsync(CancellationToken.None));

            Assert.All(
                results,
                result => Assert.True(
                    result.IsSuccess,
                    result.Error?.Message));
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ProvisionerCancellationCleansStaging()
    {
        var root = TemporaryDirectory();
        var handler = new BlockingArchiveHandler();
        using var http = new HttpClient(handler);
        var archive = CreateFakeRuntimeArchive();
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(archive));

        try
        {
            var provisioner = new PythonRuntimeProvisioner(
                http,
                root,
                FakeManifest(hash),
                Encoding.UTF8.GetBytes("worker"));

            using var cancellation =
                new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(150));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => provisioner.EnsureAsync(
                    cancellation.Token));

            var parent = Path.Combine(
                root,
                "runtimes",
                "python");
            if (Directory.Exists(parent))
            {
                Assert.Empty(
                    Directory.EnumerateDirectories(
                        parent,
                        ".staging-*"));
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task ProvisionerRejectsBadHashAndCleansStaging()
    {
        var root = TemporaryDirectory();
        var archive = CreateFakeRuntimeArchive();
        var handler = new StaticArchiveHandler(archive);
        using var http = new HttpClient(handler);

        try
        {
            var provisioner = new PythonRuntimeProvisioner(
                http,
                root,
                FakeManifest(new string('0', 64)),
                Encoding.UTF8.GetBytes("worker"));

            var result = await provisioner.EnsureAsync(
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal("execution_failed", result.Error?.Code);

            var runtimeDirectory = Path.Combine(
                root,
                "runtimes",
                "python",
                "3.14.8-amd64");
            Assert.False(Directory.Exists(runtimeDirectory));

            var parent = Path.GetDirectoryName(runtimeDirectory)!;
            if (Directory.Exists(parent))
            {
                Assert.Empty(
                    Directory.EnumerateDirectories(
                        parent,
                        ".staging-*"));
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task RealProviderPersistsNamespaceAndUsesWorkDirectory()
    {
        var workingDirectory = TemporaryDirectory();

        try
        {
            await using var fixture = new PythonFixture();
            var work = fixture.CreateWork(workingDirectory);

            var first = await fixture.ExecuteAsync(
                work.Id,
                "x = 41");
            var second = await fixture.ExecuteAsync(
                work.Id,
                "import os\nprint(x + 1)\nprint(os.getcwd())");

            Assert.True(first.IsSuccess, first.Error?.Message);
            Assert.True(second.IsSuccess, second.Error?.Message);

            var lines = second.Value!.Stdout
                .Split(
                    '\n',
                    StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("42", lines[0]);
            Assert.Equal(
                Path.GetFullPath(workingDirectory),
                Path.GetFullPath(lines[1]));

            var worker = fixture.ActiveWorker(work.Id);
            Assert.Equal("3.14.8", worker.PythonVersion);
            Assert.True(worker.ProcessId > 0);
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    [Fact]
    public async Task RealProviderExposesPrivateLoomBridge()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork(
            Environment.CurrentDirectory);

        var result = await fixture.ExecuteAsync(
            work.Id,
            "import loom\n" +
            "print(loom.__bridge_version__)\n" +
            "print(loom.capabilities())");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            PythonExecutionStatus.Completed,
            result.Value!.Status);
        Assert.Equal(
            "1\n[]\n",
            result.Value.Stdout);
    }

    [Fact]
    public async Task UnsupportedBridgeCallIsRecoverableAndWorkerStaysAlive()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork(
            Environment.CurrentDirectory);

        var first = await fixture.ExecuteAsync(
            work.Id,
            "import loom\n" +
            "try:\n" +
            "    loom._bridge_call('missing.method', {})\n" +
            "except loom.LoomError as exc:\n" +
            "    print(exc.code)\n" +
            "    print(exc.retryable)");

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Equal(
            "unsupported\nFalse\n",
            first.Value!.Stdout);

        var firstPid = fixture.ActiveWorker(work.Id).ProcessId;

        var second = await fixture.ExecuteAsync(
            work.Id,
            "print('still-alive')");

        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(
            "still-alive\n",
            second.Value!.Stdout);
        Assert.Equal(
            firstPid,
            fixture.ActiveWorker(work.Id).ProcessId);
    }

    [Fact]
    public async Task OversizedBridgeCallIsRecoverableAndWorkerStaysAlive()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork(
            Environment.CurrentDirectory);

        var result = await fixture.ExecuteAsync(
            work.Id,
            "import loom\n" +
            "try:\n" +
            "    loom._bridge_call('bridge.capabilities', {'x': 'x' * (2 * 1024 * 1024)})\n" +
            "except loom.LoomError as exc:\n" +
            "    print(exc.code)\n" +
            "print(len(loom.capabilities()))");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            "unsupported\n0\n",
            result.Value!.Stdout);

        var firstPid = fixture.ActiveWorker(work.Id).ProcessId;
        var next = await fixture.ExecuteAsync(
            work.Id,
            "print('alive-after-oversize')");

        Assert.True(next.IsSuccess, next.Error?.Message);
        Assert.Equal(
            "alive-after-oversize\n",
            next.Value!.Stdout);
        Assert.Equal(
            firstPid,
            fixture.ActiveWorker(work.Id).ProcessId);
    }

    [Fact]
    public async Task ThreadCreatedDuringExecuteCanUseBridge()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork(
            Environment.CurrentDirectory);

        var result = await fixture.ExecuteAsync(
            work.Id,
            "import threading, loom\n" +
            "values = []\n" +
            "def use_bridge():\n" +
            "    values.append(loom.capabilities())\n" +
            "thread = threading.Thread(target=use_bridge)\n" +
            "thread.start()\n" +
            "thread.join()\n" +
            "print(values)");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            "[[]]\n",
            result.Value!.Stdout);
    }

    [Fact]
    public async Task RepeatedThreadedBridgeCallsStayCorrelated()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork(
            Environment.CurrentDirectory);

        var result = await fixture.ExecuteAsync(
            work.Id,
            "import threading, loom\n" +
            "errors = []\n" +
            "def probe():\n" +
            "    try:\n" +
            "        for _ in range(50):\n" +
            "            if loom.capabilities() != []:\n" +
            "                errors.append('bad-result')\n" +
            "    except BaseException as exc:\n" +
            "        errors.append(type(exc).__name__)\n" +
            "threads = [threading.Thread(target=probe) for _ in range(8)]\n" +
            "for thread in threads:\n" +
            "    thread.start()\n" +
            "for thread in threads:\n" +
            "    thread.join()\n" +
            "print(errors)",
            TimeSpan.FromSeconds(30));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            "[]\n",
            result.Value!.Stdout);
    }

    [Fact]
    public async Task StaleThreadCannotUseBridgeInLaterExecute()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork(
            Environment.CurrentDirectory);

        var first = await fixture.ExecuteAsync(
            work.Id,
            "import threading, time, loom\n" +
            "late_bridge = []\n" +
            "def late_call():\n" +
            "    time.sleep(0.2)\n" +
            "    try:\n" +
            "        loom.capabilities()\n" +
            "        late_bridge.append('unexpected-success')\n" +
            "    except loom.LoomError as exc:\n" +
            "        late_bridge.append(exc.code)\n" +
            "threading.Thread(target=late_call, daemon=True).start()");

        Assert.True(first.IsSuccess, first.Error?.Message);

        var second = await fixture.ExecuteAsync(
            work.Id,
            "import time\n" +
            "time.sleep(0.5)\n" +
            "print(late_bridge)");

        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(
            "['bridge_unavailable']\n",
            second.Value!.Stdout);
    }

    [Fact]
    public async Task FinalResultWaitsForActiveBridgeCallback()
    {
        var dispatcher = new BlockingBridgeDispatcher();
        await using var fixture = new PythonFixture(
            bridgeDispatcher: dispatcher);
        var work = fixture.CreateWork(
            Environment.CurrentDirectory);

        var execution = fixture.ExecuteAsync(
            work.Id,
            "import loom\nprint(loom.capabilities())");

        await dispatcher.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.False(execution.IsCompleted);

        dispatcher.Release.TrySetResult();

        var result = await execution;
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            "[]\n",
            result.Value!.Stdout);
    }

    [Fact]
    public async Task TimeoutDuringBridgeCallbackDiscardsWorker()
    {
        var dispatcher = new BlockingBridgeDispatcher();
        await using var fixture = new PythonFixture(
            bridgeDispatcher: dispatcher);
        var work = fixture.CreateWork(
            Environment.CurrentDirectory);

        var execution = fixture.ExecuteAsync(
            work.Id,
            "import loom\nloom.capabilities()",
            TimeSpan.FromSeconds(1));

        await dispatcher.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        var workerPid = fixture.ActiveWorker(work.Id).ProcessId;

        var result = await execution;

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "deadline_exceeded",
            result.Error?.Code);
        Assert.Empty(
            fixture.Resources.GetActiveOwnedHandles(
                PythonCapability.ResourceKind,
                work.Id));
        await AssertProcessGoneAsync(workerPid);
    }

    [Fact]
    public async Task WorkerExitDuringBridgeCallbackCancelsCallbackAndRecoversPromptly()
    {
        var dispatcher = new BlockingBridgeDispatcher();
        await using var fixture = new PythonFixture(
            bridgeDispatcher: dispatcher);
        var work = fixture.CreateWork(
            Environment.CurrentDirectory);

        var execution = fixture.ExecuteAsync(
            work.Id,
            "import os, threading, time, loom\n" +
            "def die():\n" +
            "    time.sleep(0.2)\n" +
            "    os._exit(23)\n" +
            "threading.Thread(target=die, daemon=True).start()\n" +
            "loom.capabilities()",
            TimeSpan.FromSeconds(15));

        await dispatcher.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        var firstPid = fixture.ActiveWorker(work.Id).ProcessId;

        var result = await execution.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "execution_failed",
            result.Error?.Code);
        await dispatcher.Cancelled.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        await AssertProcessGoneAsync(firstPid);

        var recovered = await fixture.ExecuteAsync(
            work.Id,
            "print('recovered-after-callback-crash')");

        Assert.True(
            recovered.IsSuccess,
            recovered.Error?.Message);
        Assert.Equal(
            "recovered-after-callback-crash\n",
            recovered.Value!.Stdout);
        Assert.NotEqual(
            firstPid,
            fixture.ActiveWorker(work.Id).ProcessId);
    }

    [Fact]
    public async Task WorkCloseCancelsActiveBridgeCallbackAndKillsWorker()
    {
        var dispatcher = new BlockingBridgeDispatcher();
        await using var fixture = new PythonFixture(
            bridgeDispatcher: dispatcher);
        var work = fixture.CreateWork(
            Environment.CurrentDirectory);

        var execution = fixture.ExecuteAsync(
            work.Id,
            "import loom\nloom.capabilities()",
            TimeSpan.FromSeconds(30));

        await dispatcher.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        var workerPid = fixture.ActiveWorker(work.Id).ProcessId;

        var closed = await fixture.Sessions.CloseAsync(work.Id);
        Assert.True(
            closed.IsSuccess,
            closed.Error?.Message);

        var result = await execution;
        Assert.False(result.IsSuccess);
        Assert.Equal(
            "cancelled",
            result.Error?.Code);
        await dispatcher.Cancelled.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        await AssertProcessGoneAsync(workerPid);
    }

    [Fact]
    public async Task OsExitInvalidatesWorkerAndNextExecuteRecreatesIt()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork(
            Environment.CurrentDirectory);

        Assert.True(
            (await fixture.ExecuteAsync(work.Id, "x = 1"))
                .IsSuccess);
        var firstPid = fixture.ActiveWorker(work.Id).ProcessId;

        var crashed = await fixture.ExecuteAsync(
            work.Id,
            "import os\nos._exit(17)");

        Assert.False(crashed.IsSuccess);
        Assert.Equal(
            "execution_failed",
            crashed.Error?.Code);

        var recovered = await fixture.ExecuteAsync(
            work.Id,
            "print('recovered')");

        Assert.True(
            recovered.IsSuccess,
            recovered.Error?.Message);
        Assert.Equal(
            "recovered\n",
            recovered.Value!.Stdout);

        var secondPid = fixture.ActiveWorker(work.Id).ProcessId;
        Assert.NotEqual(firstPid, secondPid);
        await AssertProcessGoneAsync(firstPid);
    }

    [Fact]
    public async Task TimeoutKillsWorkerAndDescendantTree()
    {
        var directory = TemporaryDirectory();
        var childPidPath = Path.Combine(
            directory,
            "child.pid");

        try
        {
            await using var fixture = new PythonFixture();
            var work = fixture.CreateWork(directory);

            Assert.True(
                (await fixture.ExecuteAsync(
                    work.Id,
                    "print('ready')"))
                .IsSuccess);
            var workerPid =
                fixture.ActiveWorker(work.Id).ProcessId;

            var pidLiteral =
                JsonSerializer.Serialize(childPidPath);
            var code =
                "import pathlib, subprocess, sys\n" +
                "p = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(300)'])\n" +
                $"pathlib.Path({pidLiteral}).write_text(str(p.pid), encoding='utf-8')\n" +
                "while True:\n    pass";

            var result = await fixture.ExecuteAsync(
                work.Id,
                code,
                TimeSpan.FromSeconds(2));

            Assert.False(result.IsSuccess);
            Assert.Equal(
                "deadline_exceeded",
                result.Error?.Code);

            var childPid = await ReadPidAsync(
                childPidPath);
            await AssertProcessGoneAsync(workerPid);
            await AssertProcessGoneAsync(childPid);

            Assert.Empty(
                fixture.Resources.GetActiveOwnedHandles(
                    PythonCapability.ResourceKind,
                    work.Id));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task WorkCloseKillsWorkerAndDescendantTree()
    {
        var directory = TemporaryDirectory();
        var childPidPath = Path.Combine(
            directory,
            "child.pid");

        try
        {
            await using var fixture = new PythonFixture();
            var work = fixture.CreateWork(directory);

            var pidLiteral =
                JsonSerializer.Serialize(childPidPath);
            var started = await fixture.ExecuteAsync(
                work.Id,
                "import pathlib, subprocess, sys\n" +
                "p = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(300)'])\n" +
                $"pathlib.Path({pidLiteral}).write_text(str(p.pid), encoding='utf-8')");

            Assert.True(
                started.IsSuccess,
                started.Error?.Message);

            var workerPid =
                fixture.ActiveWorker(work.Id).ProcessId;
            var childPid =
                await ReadPidAsync(childPidPath);

            var closed =
                await fixture.Sessions.CloseAsync(work.Id);
            Assert.True(
                closed.IsSuccess,
                closed.Error?.Message);

            await AssertProcessGoneAsync(workerPid);
            await AssertProcessGoneAsync(childPid);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task WorkCloseCancelsActiveExecutionAndKillsTree()
    {
        var directory = TemporaryDirectory();
        var childPidPath = Path.Combine(
            directory,
            "active-child.pid");

        try
        {
            await using var fixture = new PythonFixture();
            var work = fixture.CreateWork(directory);

            var pidLiteral =
                JsonSerializer.Serialize(childPidPath);
            var execution = fixture.ExecuteAsync(
                work.Id,
                "import pathlib, subprocess, sys\n" +
                "p = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(300)'])\n" +
                $"pathlib.Path({pidLiteral}).write_text(str(p.pid), encoding='utf-8')\n" +
                "while True:\n    pass");

            var childPid =
                await ReadPidAsync(childPidPath);
            var workerPid =
                fixture.ActiveWorker(work.Id).ProcessId;

            var closed =
                await fixture.Sessions.CloseAsync(work.Id);
            Assert.True(
                closed.IsSuccess,
                closed.Error?.Message);

            var result = await execution;
            Assert.False(result.IsSuccess);
            Assert.Equal(
                "cancelled",
                result.Error?.Code);

            await AssertProcessGoneAsync(workerPid);
            await AssertProcessGoneAsync(childPid);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ResetChangesPidAndClearsNamespace()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork(
            Environment.CurrentDirectory);

        Assert.True(
            (await fixture.ExecuteAsync(
                work.Id,
                "reset_probe = 99"))
            .IsSuccess);
        var firstPid =
            fixture.ActiveWorker(work.Id).ProcessId;

        var reset =
            await fixture.Python.ResetAsync(work.Id);
        Assert.True(
            reset.IsSuccess,
            reset.Error?.Message);

        var after = await fixture.ExecuteAsync(
            work.Id,
            "print('reset_probe' in globals())");
        Assert.True(
            after.IsSuccess,
            after.Error?.Message);
        Assert.Equal(
            "False\n",
            after.Value!.Stdout);

        var secondPid =
            fixture.ActiveWorker(work.Id).ProcessId;
        Assert.NotEqual(firstPid, secondPid);
        await AssertProcessGoneAsync(firstPid);
    }

    [Fact]
    public async Task RealWorkerReturnsBusyForConcurrentExecution()
    {
        await using var fixture = new PythonFixture();
        var work = fixture.CreateWork(
            Environment.CurrentDirectory);

        Assert.True(
            (await fixture.ExecuteAsync(
                work.Id,
                "print('ready')"))
            .IsSuccess);

        var first = fixture.ExecuteAsync(
            work.Id,
            "import time\ntime.sleep(1.5)");

        await Task.Delay(150);

        var second = await fixture.ExecuteAsync(
            work.Id,
            "print('second')");

        Assert.False(second.IsSuccess);
        Assert.Equal("busy", second.Error?.Code);

        var firstResult = await first;
        Assert.True(
            firstResult.IsSuccess,
            firstResult.Error?.Message);
    }

    private sealed class BlockingBridgeDispatcher
        : IPythonBridgeDispatcher
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LoomResult<JsonElement>> DispatchAsync(
            WorkId workId,
            PythonBridgeCall call,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(
                    call.Method,
                    "bridge.capabilities",
                    StringComparison.Ordinal))
            {
                return LoomResult<JsonElement>.Failure(
                    LoomErrors.Unsupported(
                        $"Python bridge method '{call.Method}' is not supported."));
            }

            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }

            return LoomResult<JsonElement>.Success(
                JsonSerializer.SerializeToElement(
                    Array.Empty<string>()));
        }
    }

    private sealed class PythonFixture : IAsyncDisposable
    {
        public PythonFixture(
            IPythonBridgeDispatcher? bridgeDispatcher = null)
        {
            Events = new LoomEventBus();
            Resources = new ResourceRegistry();
            Sessions = new WorkSessionManager(
                Resources,
                Events);
            Invocations = new InvocationRunner(
                Events,
                Sessions);

            var processProvider =
                new WindowsProcessProvider();
            Provider =
                new WindowsPythonRuntimeProvider(
                    processProvider);
            Python = new PythonCapability(
                Provider,
                bridgeDispatcher ?? new PythonBridgeDispatcher(),
                Resources,
                Invocations,
                Events);
        }

        public LoomEventBus Events { get; }
        public ResourceRegistry Resources { get; }
        public WorkSessionManager Sessions { get; }
        public InvocationRunner Invocations { get; }
        public WindowsPythonRuntimeProvider Provider { get; }
        public PythonCapability Python { get; }

        public WorkSession CreateWork(
            string baseDirectory)
        {
            var result = Sessions.Create(
                baseDirectory);
            Assert.True(
                result.IsSuccess,
                result.Error?.Message);
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
                    timeout ??
                        PythonCapability.DefaultTimeout,
                    PythonCapability.DefaultMaxOutputChars));

        public WindowsPythonWorkerResource ActiveWorker(
            WorkId workId)
        {
            var handle = Assert.Single(
                Resources.GetActiveOwnedHandles(
                    PythonCapability.ResourceKind,
                    workId));
            var resolved =
                Resources.Resolve<IPythonWorkerResource>(
                    handle,
                    PythonCapability.ResourceKind);
            Assert.True(
                resolved.IsSuccess,
                resolved.Error?.Message);
            return Assert.IsType<WindowsPythonWorkerResource>(
                resolved.Value!.Resource);
        }

        public ValueTask DisposeAsync()
            => Sessions.DisposeAsync();
    }

    private sealed class BlockingArchiveHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class StaticArchiveHandler(
        byte[] archive,
        TimeSpan? delay = null) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount
            => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(
                ref _requestCount);

            if (delay is { } actualDelay)
            {
                await Task.Delay(
                    actualDelay,
                    cancellationToken);
            }

            return new HttpResponseMessage(
                HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(
                    archive)
            };
        }
    }

    private static PythonRuntimeManifest FakeManifest(
        string sha256)
        => new(
            "3.14.8",
            "amd64",
            "embed",
            "https://runtime.invalid/python.zip",
            sha256);

    private static byte[] CreateFakeRuntimeArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(
                   stream,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            WriteEntry(
                archive,
                "python.exe",
                "fake executable");
            WriteEntry(
                archive,
                "python314._pth",
                "python314.zip\n.\n");
        }

        return stream.ToArray();
    }

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(
            entry.Open(),
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static async Task<int> ReadPidAsync(
        string path)
    {
        var deadline =
            DateTime.UtcNow +
            TimeSpan.FromSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                var text =
                    await File.ReadAllTextAsync(path);
                if (int.TryParse(
                        text.Trim(),
                        out var pid))
                {
                    return pid;
                }
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"PID file '{path}' was not created.");
    }

    private static async Task AssertProcessGoneAsync(
        int processId)
    {
        var deadline =
            DateTime.UtcNow +
            TimeSpan.FromSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            if (!ProcessExists(processId))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.False(
            ProcessExists(processId),
            $"Process {processId} is still alive.");
    }

    private static bool ProcessExists(
        int processId)
    {
        try
        {
            using var process =
                Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"loom-python-e13-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(
        string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(
                    path,
                    recursive: true);
            }
        }
        catch
        {
        }
    }
}
