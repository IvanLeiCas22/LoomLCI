#pragma warning disable CA1416

using System.Text;
using LoomLCI.Core;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Python;
using LoomLCI.Windows.Python;

namespace LoomLCI.Windows.Tests;

public sealed class WindowsPythonPackageProviderTests
{
    [Fact]
    public async Task PrepareCreatesImmutableEnvironmentAndReusesIt()
    {
        var root = TemporaryDirectory();

        try
        {
            var runtime = new FakeRuntimeProvisioner(root);
            var manager = new FakePackageManagerProvisioner(root);
            var processes = new FakeUvProcessProvider();
            var provider = new WindowsPythonPackageProvider(
                processes,
                runtime,
                manager,
                root);

            var spec = new PythonPackagesPrepareSpec(
                [
                    new PythonPackageRequirement(
                        "numpy",
                        "2.5.3"),
                    new PythonPackageRequirement(
                        "pandas",
                        "3.0.6")
                ]);

            var first = await provider.PrepareAsync(
                spec,
                CancellationToken.None);
            var second = await provider.PrepareAsync(
                spec,
                CancellationToken.None);

            Assert.True(first.IsSuccess, first.Error?.Message);
            Assert.True(second.IsSuccess, second.Error?.Message);
            Assert.NotNull(first.Value!.Environment);
            Assert.NotNull(second.Value!.Environment);
            Assert.False(first.Value.Reused);
            Assert.True(second.Value.Reused);
            Assert.Equal(
                first.Value.Environment!.EnvironmentId,
                second.Value.Environment!.EnvironmentId);
            Assert.Equal(
                64,
                first.Value.Environment.EnvironmentId.Length);
            Assert.True(
                Directory.Exists(
                    first.Value.Environment.SitePath));
            Assert.True(
                File.Exists(
                    Path.Combine(
                        Path.GetDirectoryName(
                            first.Value.Environment.SitePath)!,
                        "requirements.lock.txt")));
            Assert.True(
                File.Exists(
                    Path.Combine(
                        Path.GetDirectoryName(
                            first.Value.Environment.SitePath)!,
                        ".loom-packages.json")));

            Assert.Equal(
                ["numpy", "pandas"],
                first.Value.Environment.Packages
                    .Select(package => package.Name)
                    .ToArray());

            Assert.Equal(3, processes.StartCount);
            Assert.Equal(2, runtime.EnsureCount);
            Assert.Equal(2, manager.EnsureCount);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task EmptyPackageSetUsesBaseRuntimeWithoutUv()
    {
        var root = TemporaryDirectory();

        try
        {
            var runtime = new FakeRuntimeProvisioner(root);
            var manager = new FakePackageManagerProvisioner(root);
            var processes = new FakeUvProcessProvider();
            var provider = new WindowsPythonPackageProvider(
                processes,
                runtime,
                manager,
                root);

            var result = await provider.PrepareAsync(
                new PythonPackagesPrepareSpec(
                    Array.Empty<PythonPackageRequirement>()),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Null(result.Value!.Environment);
            Assert.Equal("3.14.8", result.Value.PythonVersion);
            Assert.True(result.Value.Reused);
            Assert.Equal(1, runtime.EnsureCount);
            Assert.Equal(0, manager.EnsureCount);
            Assert.Equal(0, processes.StartCount);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task FailedSyncDoesNotPublishEnvironment()
    {
        var root = TemporaryDirectory();

        try
        {
            var runtime = new FakeRuntimeProvisioner(root);
            var manager = new FakePackageManagerProvisioner(root);
            var processes = new FakeUvProcessProvider
            {
                FailSync = true
            };
            var provider = new WindowsPythonPackageProvider(
                processes,
                runtime,
                manager,
                root);

            var result = await provider.PrepareAsync(
                new PythonPackagesPrepareSpec(
                    [new PythonPackageRequirement("numpy")]),
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(
                "execution_failed",
                result.Error?.Code);

            var envRoot = Path.Combine(
                root,
                "packages",
                "python",
                "3.14.8-amd64",
                "envs");
            Assert.True(Directory.Exists(envRoot));
            Assert.Empty(
                Directory.EnumerateDirectories(envRoot));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ReservedLoomNamespaceDoesNotPublishEnvironment()
    {
        var root = TemporaryDirectory();

        try
        {
            var runtime = new FakeRuntimeProvisioner(root);
            var manager = new FakePackageManagerProvisioner(root);
            var processes = new FakeUvProcessProvider
            {
                PublishReservedLoomModule = true
            };
            var provider = new WindowsPythonPackageProvider(
                processes,
                runtime,
                manager,
                root);

            var result = await provider.PrepareAsync(
                new PythonPackagesPrepareSpec(
                    [new PythonPackageRequirement("numpy")]),
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal(
                "unsupported",
                result.Error?.Code);
            Assert.Contains(
                "reserved",
                result.Error?.Message,
                StringComparison.OrdinalIgnoreCase);

            var envRoot = Path.Combine(
                root,
                "packages",
                "python",
                "3.14.8-amd64",
                "envs");
            Assert.True(Directory.Exists(envRoot));
            Assert.Empty(
                Directory.EnumerateDirectories(envRoot));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task PruneKeepsProtectedEnvironmentAndEvictsUnprotectedOne()
    {
        var root = TemporaryDirectory();

        try
        {
            var runtime = new FakeRuntimeProvisioner(root);
            var provider = new WindowsPythonPackageProvider(
                new FakeUvProcessProvider(),
                runtime,
                new FakePackageManagerProvisioner(root),
                root,
                environmentBudgetBytes: 64,
                cacheBudgetBytes: long.MaxValue);

            var envRoot = Path.Combine(
                root,
                "packages",
                "python",
                "3.14.8-amd64",
                "envs");
            var protectedEnvironment = Path.Combine(
                envRoot,
                "protected");
            var evictableEnvironment = Path.Combine(
                envRoot,
                "evictable");

            Directory.CreateDirectory(protectedEnvironment);
            Directory.CreateDirectory(evictableEnvironment);
            await File.WriteAllBytesAsync(
                Path.Combine(protectedEnvironment, "payload.bin"),
                new byte[64]);
            await File.WriteAllBytesAsync(
                Path.Combine(evictableEnvironment, "payload.bin"),
                new byte[64]);

            var result = await provider.PruneAsync(
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "protected"
                },
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.True(Directory.Exists(protectedEnvironment));
            Assert.False(Directory.Exists(evictableEnvironment));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task PruneRecreatesUvCacheWhenItExceedsBudget()
    {
        var root = TemporaryDirectory();

        try
        {
            var runtime = new FakeRuntimeProvisioner(root);
            var provider = new WindowsPythonPackageProvider(
                new FakeUvProcessProvider(),
                runtime,
                new FakePackageManagerProvisioner(root),
                root,
                environmentBudgetBytes: long.MaxValue,
                cacheBudgetBytes: 16);

            var cacheRoot = Path.Combine(
                root,
                "packages",
                "python",
                "3.14.8-amd64",
                "cache");
            Directory.CreateDirectory(cacheRoot);
            var cachePayload = Path.Combine(
                cacheRoot,
                "payload.bin");
            await File.WriteAllBytesAsync(
                cachePayload,
                new byte[64]);

            var result = await provider.PruneAsync(
                new HashSet<string>(StringComparer.Ordinal),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.True(Directory.Exists(cacheRoot));
            Assert.False(File.Exists(cachePayload));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private sealed class FakeRuntimeProvisioner
        : IPythonRuntimeProvisioner
    {
        private readonly PythonRuntimeInstallation _installation;
        private int _ensureCount;

        public FakeRuntimeProvisioner(string root)
        {
            var runtimeDirectory = Path.Combine(
                root,
                "fake-runtime");
            Directory.CreateDirectory(runtimeDirectory);

            var pythonPath = Path.Combine(
                runtimeDirectory,
                "python.exe");
            File.WriteAllText(
                pythonPath,
                "fake",
                Encoding.UTF8);

            var workerPath = Path.Combine(
                runtimeDirectory,
                "worker.py");
            File.WriteAllText(
                workerPath,
                "fake",
                Encoding.UTF8);

            var bridgePath = Path.Combine(
                runtimeDirectory,
                "loom_bridge.py");
            File.WriteAllText(
                bridgePath,
                "fake",
                Encoding.UTF8);

            _installation = new PythonRuntimeInstallation(
                pythonPath,
                workerPath,
                bridgePath,
                new PythonRuntimeManifest(
                    "3.14.8",
                    "amd64",
                    "embed",
                    "https://runtime.invalid/python.zip",
                    new string('a', 64)));
        }

        public int EnsureCount
            => Volatile.Read(ref _ensureCount);

        public Task<LoomResult<PythonRuntimeInstallation>> EnsureAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _ensureCount);
            return Task.FromResult(
                LoomResult<PythonRuntimeInstallation>.Success(
                    _installation));
        }
    }

    private sealed class FakePackageManagerProvisioner
        : IPythonPackageManagerProvisioner
    {
        private readonly PythonPackageManagerInstallation _installation;
        private int _ensureCount;

        public FakePackageManagerProvisioner(string root)
        {
            var path = Path.Combine(
                root,
                "fake-uv.exe");
            File.WriteAllText(
                path,
                "fake",
                Encoding.UTF8);

            _installation =
                new PythonPackageManagerInstallation(
                    path,
                    new PythonPackageManagerManifest(
                        "0.12.23",
                        "x86_64-pc-windows-msvc",
                        "https://packages.invalid/uv.zip",
                        new string('b', 64),
                        "uv.exe"));
        }

        public int EnsureCount
            => Volatile.Read(ref _ensureCount);

        public Task<LoomResult<PythonPackageManagerInstallation>>
            EnsureAsync(
                CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _ensureCount);
            return Task.FromResult(
                LoomResult<PythonPackageManagerInstallation>.Success(
                    _installation));
        }
    }

    private sealed class FakeUvProcessProvider : IProcessProvider
    {
        private int _startCount;

        public int StartCount
            => Volatile.Read(ref _startCount);

        public bool FailSync { get; set; }
        public bool PublishReservedLoomModule { get; set; }

        public Task<LoomResult<IProcessResource>> StartAsync(
            ProcessLaunchSpec spec,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCount);

            var arguments = spec.Arguments.ToArray();
            var isCompile =
                arguments.Length >= 2 &&
                arguments[0] == "pip" &&
                arguments[1] == "compile";
            var isSync =
                arguments.Length >= 2 &&
                arguments[0] == "pip" &&
                arguments[1] == "sync";

            if (isCompile)
            {
                var outputPath = ValueAfter(
                    arguments,
                    "--output-file");
                Directory.CreateDirectory(
                    Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(
                    outputPath,
                    "numpy==2.5.3 \\\n" +
                    "    --hash=sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n" +
                    "pandas==3.0.6 \\\n" +
                    "    --hash=sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n",
                    new UTF8Encoding(false));
            }

            if (isSync && !FailSync)
            {
                var target = ValueAfter(
                    arguments,
                    "--target");
                Directory.CreateDirectory(target);
                File.WriteAllText(
                    Path.Combine(target, "installed.txt"),
                    "ok",
                    Encoding.UTF8);

                if (PublishReservedLoomModule)
                {
                    File.WriteAllText(
                        Path.Combine(target, "loom.py"),
                        "reserved",
                        Encoding.UTF8);
                }
            }

            var exitCode =
                isSync && FailSync
                    ? 1
                    : 0;

            IProcessResource resource =
                new FakeExitedProcessResource(
                    Interlocked.Increment(ref _nextPid),
                    exitCode,
                    exitCode == 0
                        ? string.Empty
                        : "sync failed");

            return Task.FromResult(
                LoomResult<IProcessResource>.Success(
                    resource));
        }

        private int _nextPid = 10_000;

        private static string ValueAfter(
            string[] arguments,
            string option)
        {
            var index = Array.IndexOf(
                arguments,
                option);
            Assert.True(index >= 0);
            Assert.True(index + 1 < arguments.Length);
            return arguments[index + 1];
        }
    }

    private sealed class FakeExitedProcessResource
        : IProcessResource
    {
        private readonly int _exitCode;
        private readonly string _stderr;

        public FakeExitedProcessResource(
            int processId,
            int exitCode,
            string stderr)
        {
            ProcessId = processId;
            _exitCode = exitCode;
            _stderr = stderr;
            StartedAt = DateTimeOffset.UtcNow;
        }

        public int ProcessId { get; }
        public DateTimeOffset StartedAt { get; }
        public ProcessIoMode IoMode
            => ProcessIoMode.Pipes;

        public ProcessStatusResult Snapshot(
            ProcessHandle handle)
            => new(
                handle,
                ProcessId,
                ManagedProcessState.Exited,
                _exitCode,
                StartedAt,
                StartedAt,
                ProcessIoMode.Pipes,
                null);

        public ProcessOutputReadResult Read(
            ProcessHandle handle,
            long stdoutCursor,
            long stderrCursor,
            long terminalCursor,
            int maxChars)
            => new(
                Snapshot(handle),
                ProcessIoMode.Pipes,
                EmptyOutput(stdoutCursor),
                TextOutput(
                    stderrCursor,
                    _stderr),
                null);

        public Task WaitForExitAndOutputAsync(
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<LoomResult<Unit>> WriteAsync(
            string text,
            CancellationToken cancellationToken)
            => Task.FromResult(
                LoomResult<Unit>.Failure(
                    LoomErrors.Unsupported("not supported")));

        public Task<LoomResult<Unit>> ResizeAsync(
            int columns,
            int rows,
            CancellationToken cancellationToken)
            => Task.FromResult(
                LoomResult<Unit>.Failure(
                    LoomErrors.Unsupported("not supported")));

        public Task<LoomResult<Unit>> TerminateAsync(
            CancellationToken cancellationToken)
            => Task.FromResult(
                LoomResult<Unit>.Success(Unit.Value));

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;

        private static OutputStreamReadResult EmptyOutput(
            long cursor)
            => new(
                cursor,
                0,
                cursor,
                0,
                0,
                false,
                false,
                Array.Empty<OutputChunk>());

        private static OutputStreamReadResult TextOutput(
            long cursor,
            string text)
            => string.IsNullOrEmpty(text)
                ? EmptyOutput(cursor)
                : new OutputStreamReadResult(
                    cursor,
                    0,
                    text.Length,
                    text.Length,
                    text.Length,
                    false,
                    false,
                    [new OutputChunk(0, text)]);
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"loom-python-packages-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
