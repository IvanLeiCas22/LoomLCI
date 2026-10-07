#pragma warning disable CA1416 // LoomLCI.Windows.Tests exercises the Windows-specific backend.

using System.Buffers.Binary;
using LoomLCI.Core;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using LoomLCI.Core.Python;
using LoomLCI.Core.Processes;
using LoomLCI.Windows.Processes;
using LoomLCI.Windows.Python;

namespace LoomLCI.Windows.Tests;

public sealed class PythonWorkerProtocolTests
{
    [Fact]
    public void PipeAclAllowsCurrentUserAndExplicitlyDeniesNetwork()
    {
        var pipeName = PythonWorkerProtocol.CreatePipeName();
        using var pipe = PythonWorkerProtocol.CreateServer(pipeName);

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentUser = identity.User;
        Assert.NotNull(currentUser);

        var security = pipe.GetAccessControl();
        var rules = security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                targetType: typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();

        Assert.Contains(
            rules,
            rule =>
                Equals(rule.IdentityReference, currentUser) &&
                rule.AccessControlType == AccessControlType.Allow &&
                rule.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));

        var network = new SecurityIdentifier(
            WellKnownSidType.NetworkSid,
            domainSid: null);

        Assert.Contains(
            rules,
            rule =>
                Equals(rule.IdentityReference, network) &&
                rule.AccessControlType == AccessControlType.Deny);
    }

    [Fact]
    public async Task FramingHandlesFragmentedReads()
    {
        await using var encoded = new MemoryStream();
        await PythonWorkerProtocol.WriteRawFrameAsync(
            encoded,
            new { type = "probe", value = "áéíóú" },
            PythonWorkerProtocol.MaxRequestFrameBytes);

        await using var fragmented = new FragmentedReadStream(
            encoded.ToArray(),
            maxChunkBytes: 1);

        using var document = await PythonWorkerProtocol.ReadRawFrameAsync(
            fragmented,
            PythonWorkerProtocol.MaxRequestFrameBytes);

        Assert.Equal("probe", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("áéíóú", document.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task FramingRejectsOversizedLengthBeforeAllocatingPayload()
    {
        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header,
            PythonWorkerProtocol.MaxRequestFrameBytes + 1u);

        await using var stream = new MemoryStream(header);

        var error = await Assert.ThrowsAsync<PythonWorkerProtocolException>(
            () => PythonWorkerProtocol.ReadRawFrameAsync(
                stream,
                PythonWorkerProtocol.MaxRequestFrameBytes));

        Assert.Contains("outside the allowed range", error.Message);
    }

    [Fact]
    public async Task FramingRejectsInvalidUtf8()
    {
        var payload = new byte[] { 0xC3, 0x28 };
        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header,
            checked((uint)payload.Length));

        await using var stream = new MemoryStream();
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
        stream.Position = 0;

        var error = await Assert.ThrowsAsync<PythonWorkerProtocolException>(
            () => PythonWorkerProtocol.ReadRawFrameAsync(
                stream,
                PythonWorkerProtocol.MaxRequestFrameBytes));

        Assert.Contains("strict UTF-8 JSON", error.Message);
    }

    [Fact]
    public async Task ResultRejectsMismatchedRequestId()
    {
        await using var stream = new MemoryStream();
        await PythonWorkerProtocol.WriteRawFrameAsync(
            stream,
            new
            {
                type = "result",
                requestId = "different",
                status = "completed",
                stdout = "",
                stderr = "",
                stdoutTruncated = false,
                stderrTruncated = false,
                exception = (object?)null
            },
            PythonWorkerProtocol.MaxResponseFrameBytes);

        stream.Position = 0;

        var error = await Assert.ThrowsAsync<PythonWorkerProtocolException>(
            () => PythonWorkerProtocol.ReadExecutionResultAsync(
                stream,
                "expected",
                PythonCapability.DefaultMaxOutputChars,
                CancellationToken.None));

        Assert.Contains("requestId", error.Message);
    }

    [Fact]
    public async Task BridgeCallParsesCorrelatedRequestAndArguments()
    {
        await using var stream = new MemoryStream();
        await PythonWorkerProtocol.WriteRawFrameAsync(
            stream,
            new
            {
                type = "bridge_call",
                requestId = "req_1",
                callId = "call_1",
                method = "bridge.capabilities",
                arguments = new
                {
                    sample = 42
                }
            },
            PythonWorkerProtocol.MaxBridgeCallFrameBytes);

        stream.Position = 0;

        var message = await PythonWorkerProtocol.ReadExecutionMessageAsync(
            stream,
            "req_1",
            PythonCapability.DefaultMaxOutputChars,
            CancellationToken.None);

        var bridge = Assert.IsType<PythonWorkerBridgeCallMessage>(
            message);
        Assert.Equal("call_1", bridge.CallId);
        Assert.Equal(
            "bridge.capabilities",
            bridge.Call.Method);
        Assert.Equal(
            42,
            bridge.Call.Arguments
                .GetProperty("sample")
                .GetInt32());
    }

    [Fact]
    public async Task BridgeCallRejectsMismatchedRequestId()
    {
        await using var stream = new MemoryStream();
        await PythonWorkerProtocol.WriteRawFrameAsync(
            stream,
            new
            {
                type = "bridge_call",
                requestId = "different",
                callId = "call_1",
                method = "bridge.capabilities",
                arguments = new { }
            },
            PythonWorkerProtocol.MaxBridgeCallFrameBytes);

        stream.Position = 0;

        var error = await Assert.ThrowsAsync<PythonWorkerProtocolException>(
            () => PythonWorkerProtocol.ReadExecutionMessageAsync(
                stream,
                "expected",
                PythonCapability.DefaultMaxOutputChars,
                CancellationToken.None));

        Assert.Contains("requestId", error.Message);
    }

    [Fact]
    public async Task BridgeResultSerializesStructuredLoomError()
    {
        await using var stream = new MemoryStream();

        await PythonWorkerProtocol.WriteBridgeResultAsync(
            stream,
            "req_1",
            "call_1",
            LoomResult<System.Text.Json.JsonElement>.Failure(
                new LoomError(
                    "unsupported",
                    "not available",
                    false,
                    new Dictionary<string, object?>
                    {
                        ["feature"] = "probe"
                    })),
            CancellationToken.None);

        stream.Position = 0;
        using var document = await PythonWorkerProtocol.ReadRawFrameAsync(
            stream,
            PythonWorkerProtocol.MaxBridgeResultFrameBytes);

        var root = document.RootElement;
        Assert.Equal(
            "bridge_result",
            root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("ok").GetBoolean());

        var error = root.GetProperty("error");
        Assert.Equal(
            "unsupported",
            error.GetProperty("code").GetString());
        Assert.Equal(
            "probe",
            error.GetProperty("details")
                .GetProperty("feature")
                .GetString());
    }

    [Fact]
    public async Task RealWorkerHandshakeAndNamespacePersistenceWork()
    {
        await using var worker = await WorkerHarness.StartAsync();

        Assert.Equal(PythonWorkerProtocol.ProtocolVersion, worker.Hello.ProtocolVersion);
        Assert.Equal(worker.Process.ProcessId, worker.Hello.ProcessId);
        Assert.Equal("3.14.8", worker.Hello.PythonVersion);

        var first = await worker.ExecuteAsync("x = 41");
        var second = await worker.ExecuteAsync("print(x + 1)");

        Assert.Equal(PythonExecutionStatus.Completed, first.Status);
        Assert.Equal(PythonExecutionStatus.Completed, second.Status);
        Assert.Equal("42\n", second.Stdout);
        Assert.Equal("", second.Stderr);
    }

    [Fact]
    public async Task RealWorkerSeparatesUnicodeOutputAndTruncatesWithoutDying()
    {
        await using var worker = await WorkerHarness.StartAsync();

        var output = await worker.ExecuteAsync(
            "import sys\nprint('héllo')\nprint('err-ñ', file=sys.stderr)");

        Assert.Equal("héllo\n", output.Stdout);
        Assert.Equal("err-ñ\n", output.Stderr);
        Assert.False(output.StdoutTruncated);
        Assert.False(output.StderrTruncated);

        var truncated = await worker.ExecuteAsync(
            "import sys\nprint('abcdefgh')\nprint('uvwxyz', file=sys.stderr)",
            maxOutputChars: 5);

        Assert.Equal("abcde", truncated.Stdout);
        Assert.Equal("uvwxy", truncated.Stderr);
        Assert.True(truncated.StdoutTruncated);
        Assert.True(truncated.StderrTruncated);

        var next = await worker.ExecuteAsync("print('alive')");
        Assert.Equal("alive\n", next.Stdout);
    }

    [Fact]
    public async Task RealWorkerCountsUnicodeCodePointsInsteadOfUtf16Units()
    {
        await using var worker = await WorkerHarness.StartAsync();

        var result = await worker.ExecuteAsync(
            "print('😀😀😀')",
            maxOutputChars: 3);

        Assert.Equal("😀😀😀", result.Stdout);
        Assert.True(result.StdoutTruncated);
    }

    [Fact]
    public async Task RealWorkerReturnsStructuredExceptionAndPreservesPartialState()
    {
        await using var worker = await WorkerHarness.StartAsync();

        var failed = await worker.ExecuteAsync(
            "partial = 7\nprint('before')\nraise ValueError('boom')");

        Assert.Equal(PythonExecutionStatus.Exception, failed.Status);
        Assert.Equal("before\n", failed.Stdout);
        Assert.NotNull(failed.Exception);
        Assert.Equal("ValueError", failed.Exception.Type);
        Assert.Equal("boom", failed.Exception.Message);
        Assert.Contains("ValueError: boom", failed.Exception.Traceback);

        var next = await worker.ExecuteAsync("print(partial)");
        Assert.Equal(PythonExecutionStatus.Completed, next.Status);
        Assert.Equal("7\n", next.Stdout);
    }

    [Fact]
    public async Task RealWorkerTreatsSystemExitAndInputAsRecoverableExceptions()
    {
        await using var worker = await WorkerHarness.StartAsync();

        var systemExit = await worker.ExecuteAsync(
            "import sys\nsys.exit(3)");

        Assert.Equal(PythonExecutionStatus.Exception, systemExit.Status);
        Assert.Equal("SystemExit", systemExit.Exception?.Type);
        Assert.Equal("3", systemExit.Exception?.Message);

        var input = await worker.ExecuteAsync("input()");
        Assert.Equal(PythonExecutionStatus.Exception, input.Status);
        Assert.Equal("EOFError", input.Exception?.Type);

        var next = await worker.ExecuteAsync("print('still-running')");
        Assert.Equal("still-running\n", next.Stdout);
    }

    [Fact]
    public async Task RealWorkerCapturesInheritedThreadContext()
    {
        await using var worker = await WorkerHarness.StartAsync();

        var result = await worker.ExecuteAsync(
            "import threading\n" +
            "t = threading.Thread(target=lambda: print('thread-output'))\n" +
            "t.start()\n" +
            "t.join()");

        Assert.Equal(PythonExecutionStatus.Completed, result.Status);
        Assert.Equal("thread-output\n", result.Stdout);
    }

    [Fact]
    public async Task RealWorkerRestoresCaptureStreamsBetweenExecutions()
    {
        await using var worker = await WorkerHarness.StartAsync();

        var reassigned = await worker.ExecuteAsync(
            "import io, sys\nsys.stdout = io.StringIO()\nprint('not-captured')");

        Assert.Equal("", reassigned.Stdout);

        var next = await worker.ExecuteAsync("print('captured-again')");
        Assert.Equal("captured-again\n", next.Stdout);
    }

    [Fact]
    public async Task RealWorkerExitsOnUnsupportedProtocolMessage()
    {
        await using var worker = await WorkerHarness.StartAsync();

        await worker.WriteRawAsync(new { type = "unsupported" });

        var error = await Assert.ThrowsAsync<PythonWorkerProtocolException>(
            () => worker.ReadRawAsync());

        Assert.Contains("Unexpected EOF", error.Message);

        var status = await worker.WaitForExitAsync();
        Assert.Equal(ManagedProcessState.Exited, status.State);
        Assert.Equal(2, status.ExitCode);
    }

    [Fact]
    public async Task RealWorkerCanImportModulesFromWorkingDirectory()
    {
        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"loom-python-worker-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workingDirectory, "loom_probe.py"),
                "VALUE = 42\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            await using var worker = await WorkerHarness.StartAsync(workingDirectory);
            var result = await worker.ExecuteAsync(
                "import loom_probe\nprint(loom_probe.VALUE)");

            Assert.Equal(PythonExecutionStatus.Completed, result.Status);
            Assert.Equal("42\n", result.Stdout);

            Assert.False(Directory.Exists(
                Path.Combine(workingDirectory, "__pycache__")));
        }
        finally
        {
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class WorkerHarness : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;

        private WorkerHarness(
            NamedPipeServerStream pipe,
            IProcessResource process,
            PythonWorkerHello hello)
        {
            _pipe = pipe;
            Process = process;
            Hello = hello;
        }

        public IProcessResource Process { get; }
        public PythonWorkerHello Hello { get; }

        public static async Task<WorkerHarness> StartAsync(
            string? workingDirectory = null)
        {
            var pipeName = PythonWorkerProtocol.CreatePipeName();
            var pipe = PythonWorkerProtocol.CreateServer(pipeName);
            IProcessResource? process = null;

            try
            {
                using var startup = new CancellationTokenSource(
                    TimeSpan.FromSeconds(10));

                var connection = pipe.WaitForConnectionAsync(startup.Token);

                var provisioned =
                    await new PythonRuntimeProvisioner()
                        .EnsureAsync(startup.Token);
                Assert.True(
                    provisioned.IsSuccess,
                    provisioned.Error?.Message);
                var installation = provisioned.Value!;

                var provider = new WindowsProcessProvider();
                var started = await provider.StartAsync(
                    new ProcessLaunchSpec(
                        installation.PythonExecutablePath,
                        [
                            "-I",
                            "-B",
                            "-u",
                            "-X",
                            "utf8",
                            "-X",
                            "faulthandler",
                            "-X",
                            "thread_inherit_context=1",
                            installation.WorkerScriptPath,
                            "--pipe-name",
                            pipeName,
                            "--bridge-script",
                            installation.BridgeScriptPath
                        ],
                        workingDirectory ?? Environment.CurrentDirectory,
                        new Dictionary<string, string?>(),
                        ProcessIoMode.Pipes,
                        null,
                        null),
                    startup.Token);

                Assert.True(started.IsSuccess, started.Error?.Message);
                process = started.Value!;

                await connection;
                var hello = await PythonWorkerProtocol.ReadHelloAsync(
                    pipe,
                    startup.Token);

                return new WorkerHarness(pipe, process, hello);
            }
            catch
            {
                pipe.Dispose();
                if (process is not null)
                {
                    await process.DisposeAsync();
                }

                throw;
            }
        }

        public async Task<PythonExecutionResult> ExecuteAsync(
            string code,
            int maxOutputChars = PythonCapability.DefaultMaxOutputChars)
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(10));

            var requestId = $"req_{Guid.NewGuid():N}";
            await PythonWorkerProtocol.WriteExecuteRequestAsync(
                _pipe,
                requestId,
                new PythonWorkerExecuteSpec(code, maxOutputChars),
                timeout.Token);

            return await PythonWorkerProtocol.ReadExecutionResultAsync(
                _pipe,
                requestId,
                maxOutputChars,
                timeout.Token);
        }

        public Task WriteRawAsync(object value)
            => PythonWorkerProtocol.WriteRawFrameAsync(
                _pipe,
                value,
                PythonWorkerProtocol.MaxRequestFrameBytes);

        public async Task<System.Text.Json.JsonDocument> ReadRawAsync()
            => await PythonWorkerProtocol.ReadRawFrameAsync(
                _pipe,
                PythonWorkerProtocol.MaxResponseFrameBytes);

        public async Task<ProcessStatusResult> WaitForExitAsync()
        {
            var handle = new ProcessHandle("proc_python_worker_test");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

            while (DateTime.UtcNow < deadline)
            {
                var status = Process.Snapshot(handle);
                if (status.State is ManagedProcessState.Exited or
                    ManagedProcessState.Terminated or
                    ManagedProcessState.FailedToStart)
                {
                    return status;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException("Python worker did not exit after protocol failure.");
        }

        public async ValueTask DisposeAsync()
        {
            _pipe.Dispose();
            await Process.DisposeAsync();
        }

    }

    private sealed class FragmentedReadStream(
        byte[] data,
        int maxChunkBytes) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(
                buffer,
                offset,
                Math.Min(count, maxChunkBytes));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, maxChunkBytes);
            return _inner.ReadAsync(buffer[..count], cancellationToken);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();
        public override void SetLength(long value)
            => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
