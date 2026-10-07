using System.IO.Pipes;
using LoomLCI.Core;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Python;

namespace LoomLCI.Windows.Python;

internal sealed class WindowsPythonWorkerResource : IPythonWorkerResource
{
    private readonly IProcessResource _process;
    private readonly NamedPipeServerStream _pipe;
    private readonly ProcessHandle _diagnosticHandle;
    private int _executing;
    private int _healthy = 1;
    private int _disposed;

    public WindowsPythonWorkerResource(
        IProcessResource process,
        NamedPipeServerStream pipe,
        PythonWorkerHello hello,
        string? packageEnvironmentId = null)
    {
        _process = process;
        _pipe = pipe;
        ProcessId = hello.ProcessId;
        PythonVersion = hello.PythonVersion;
        PackageEnvironmentId = packageEnvironmentId;
        _diagnosticHandle = new ProcessHandle(
            $"proc_python_{ProcessId}");
    }

    public int ProcessId { get; }
    public string PythonVersion { get; }
    public string? PackageEnvironmentId { get; }

    public bool IsHealthy
    {
        get
        {
            if (Volatile.Read(ref _healthy) == 0 ||
                Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            try
            {
                var state = _process
                    .Snapshot(_diagnosticHandle)
                    .State;

                if (state is not (
                    ManagedProcessState.Running or
                    ManagedProcessState.Starting))
                {
                    Interlocked.Exchange(ref _healthy, 0);
                    return false;
                }

                if (!_pipe.IsConnected)
                {
                    Interlocked.Exchange(ref _healthy, 0);
                    return false;
                }

                return true;
            }
            catch
            {
                Interlocked.Exchange(ref _healthy, 0);
                return false;
            }
        }
    }

    public async Task<LoomResult<PythonExecutionResult>> ExecuteAsync(
        PythonWorkerExecuteSpec request,
        PythonBridgeHandler bridgeHandler,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(
                ref _executing,
                1,
                0) != 0)
        {
            return LoomResult<PythonExecutionResult>.Failure(
                LoomErrors.Busy(
                    "Python worker already has an active execution."));
        }

        try
        {
            if (!IsHealthy)
            {
                return LoomResult<PythonExecutionResult>.Failure(
                    LoomErrors.ExecutionFailed(
                        "Python worker is not healthy."));
            }

            var requestId =
                $"req_{Guid.NewGuid():N}";

            try
            {
                await PythonWorkerProtocol.WriteExecuteRequestAsync(
                        _pipe,
                        requestId,
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);

                while (true)
                {
                    var message =
                        await PythonWorkerProtocol.ReadExecutionMessageAsync(
                                _pipe,
                                requestId,
                                request.MaxOutputChars,
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (message is PythonWorkerResultMessage resultMessage)
                    {
                        if (!IsHealthy)
                        {
                            await InvalidateAsync().ConfigureAwait(false);
                            return LoomResult<PythonExecutionResult>.Failure(
                                LoomErrors.ExecutionFailed(
                                    "Python worker exited after producing a result."));
                        }

                        return LoomResult<PythonExecutionResult>.Success(
                            resultMessage.Result);
                    }

                    if (message is not PythonWorkerBridgeCallMessage bridgeCall)
                    {
                        throw new PythonWorkerProtocolException(
                            "Python worker returned an unknown execution message.");
                    }

                    LoomResult<System.Text.Json.JsonElement> bridgeResult;
                    try
                    {
                        bridgeResult = await bridgeHandler(
                                bridgeCall.Call,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        bridgeResult =
                            LoomResult<System.Text.Json.JsonElement>.Failure(
                                LoomErrors.Internal(
                                    "Python bridge dispatcher failed."));
                    }

                    await PythonWorkerProtocol.WriteBridgeResultAsync(
                            _pipe,
                            requestId,
                            bridgeCall.CallId,
                            bridgeResult,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                await InvalidateAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (
                ex is PythonWorkerProtocolException or
                IOException or
                ObjectDisposedException or
                InvalidOperationException)
            {
                var diagnostics = ReadDiagnostics();
                await InvalidateAsync().ConfigureAwait(false);

                var detail = string.IsNullOrWhiteSpace(diagnostics)
                    ? string.Empty
                    : $" Diagnostics: {diagnostics}";

                return LoomResult<PythonExecutionResult>.Failure(
                    LoomErrors.ExecutionFailed(
                        $"Python worker communication failed: {ex.Message}{detail}"));
            }
        }
        finally
        {
            Volatile.Write(ref _executing, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(
                ref _disposed,
                1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _healthy, 0);

        try
        {
            _pipe.Dispose();
        }
        catch
        {
        }

        try
        {
            await _process.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task InvalidateAsync()
    {
        Interlocked.Exchange(ref _healthy, 0);
        await DisposeAsync().ConfigureAwait(false);
    }

    private string ReadDiagnostics()
    {
        try
        {
            var output = _process.Read(
                _diagnosticHandle,
                stdoutCursor: 0,
                stderrCursor: 0,
                terminalCursor: 0,
                maxChars: 8_192);

            var stderr = output.Stderr is null
                ? string.Empty
                : string.Concat(
                    output.Stderr.Chunks.Select(
                        chunk => chunk.Text));
            var stdout = output.Stdout is null
                ? string.Empty
                : string.Concat(
                    output.Stdout.Chunks.Select(
                        chunk => chunk.Text));

            var combined = string.Join(
                " | ",
                new[] { stderr, stdout }
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Select(text =>
                        text.Replace(
                            Environment.NewLine,
                            " ",
                            StringComparison.Ordinal)
                            .Trim()));

            return combined.Length <= 8_192
                ? combined
                : combined[..8_192];
        }
        catch
        {
            return string.Empty;
        }
    }
}
