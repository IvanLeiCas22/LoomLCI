using System.IO.Pipes;
using LoomLCI.Core;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Python;

namespace LoomLCI.Windows.Python;

public sealed class WindowsPythonRuntimeProvider : IPythonRuntimeProvider
{
    internal static readonly TimeSpan StartupTimeout =
        TimeSpan.FromSeconds(10);

    private readonly IProcessProvider _processProvider;
    private readonly IPythonRuntimeProvisioner _provisioner;

    public WindowsPythonRuntimeProvider(
        IProcessProvider processProvider)
        : this(
            processProvider,
            new PythonRuntimeProvisioner())
    {
    }

    internal WindowsPythonRuntimeProvider(
        IProcessProvider processProvider,
        IPythonRuntimeProvisioner provisioner)
    {
        _processProvider = processProvider;
        _provisioner = provisioner;
    }

    public async Task<LoomResult<IPythonWorkerResource>> StartAsync(
        PythonWorkerStartSpec spec,
        CancellationToken cancellationToken)
    {
        var provisioned = await _provisioner
            .EnsureAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!provisioned.IsSuccess)
        {
            return LoomResult<IPythonWorkerResource>.Failure(
                provisioned.Error!);
        }

        var installation = provisioned.Value!;
        var workingDirectory = string.IsNullOrWhiteSpace(
                spec.WorkingDirectory)
            ? Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile)
            : spec.WorkingDirectory;

        if (string.IsNullOrWhiteSpace(workingDirectory) ||
            !Directory.Exists(workingDirectory))
        {
            return LoomResult<IPythonWorkerResource>.Failure(
                LoomErrors.InvalidArgument(
                    $"Python working directory '{workingDirectory}' does not exist."));
        }

        if (spec.PackageEnvironment is { } packageEnvironment &&
            !Directory.Exists(packageEnvironment.SitePath))
        {
            return LoomResult<IPythonWorkerResource>.Failure(
                LoomErrors.ExecutionFailed(
                    $"Python package environment '{packageEnvironment.EnvironmentId}' is missing its site directory."));
        }

        var pipeName =
            PythonWorkerProtocol.CreatePipeName();
        NamedPipeServerStream? pipe = null;
        IProcessResource? process = null;

        try
        {
            pipe = PythonWorkerProtocol.CreateServer(
                pipeName);

            using var startupDeadline =
                new CancellationTokenSource(
                    StartupTimeout);
            using var startup =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    startupDeadline.Token);

            var arguments = new List<string>
            {
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
            };

            if (spec.PackageEnvironment is { } environment)
            {
                arguments.Add("--package-site");
                arguments.Add(environment.SitePath);
            }

            var started = await _processProvider.StartAsync(
                    new ProcessLaunchSpec(
                        installation.PythonExecutablePath,
                        arguments,
                        workingDirectory,
                        new Dictionary<string, string?>
                        {
                            ["PYTHONHOME"] = null,
                            ["PYTHONPATH"] = null,
                            ["PYTHONSTARTUP"] = null,
                            ["PYTHONUSERBASE"] = null
                        },
                        ProcessIoMode.Pipes,
                        null,
                        null),
                    startup.Token)
                .ConfigureAwait(false);

            if (!started.IsSuccess)
            {
                pipe.Dispose();
                return LoomResult<IPythonWorkerResource>.Failure(
                    started.Error!);
            }

            process = started.Value!;

            var connection = pipe.WaitForConnectionAsync(
                startup.Token);

            await WaitForConnectionOrExitAsync(
                    connection,
                    process,
                    startup.Token)
                .ConfigureAwait(false);

            var hello = await PythonWorkerProtocol
                .ReadHelloAsync(
                    pipe,
                    startup.Token)
                .ConfigureAwait(false);

            if (hello.ProcessId != process.ProcessId)
            {
                throw new PythonWorkerProtocolException(
                    $"Python worker hello PID {hello.ProcessId} does not match launched PID {process.ProcessId}.");
            }

            if (!string.Equals(
                    hello.PythonVersion,
                    installation.Manifest.Version,
                    StringComparison.Ordinal))
            {
                throw new PythonWorkerProtocolException(
                    $"Python worker version '{hello.PythonVersion}' does not match pinned version '{installation.Manifest.Version}'.");
            }

            var resource =
                new WindowsPythonWorkerResource(
                    process,
                    pipe,
                    hello,
                    spec.PackageEnvironment?.EnvironmentId);

            process = null;
            pipe = null;

            return LoomResult<IPythonWorkerResource>.Success(
                resource);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            var diagnostics = ReadDiagnostics(process);
            await CleanupAsync(
                    pipe,
                    process)
                .ConfigureAwait(false);

            var detail = string.IsNullOrWhiteSpace(diagnostics)
                ? string.Empty
                : $" Diagnostics: {diagnostics}";

            return LoomResult<IPythonWorkerResource>.Failure(
                LoomErrors.ExecutionFailed(
                    $"Python worker startup exceeded {StartupTimeout.TotalSeconds:0} seconds.{detail}"));
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync(
                    pipe,
                    process)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (
            ex is PythonWorkerProtocolException or
            IOException or
            InvalidOperationException or
            UnauthorizedAccessException)
        {
            var diagnostics = ReadDiagnostics(process);
            await CleanupAsync(
                    pipe,
                    process)
                .ConfigureAwait(false);

            var detail = string.IsNullOrWhiteSpace(diagnostics)
                ? string.Empty
                : $" Diagnostics: {diagnostics}";

            return LoomResult<IPythonWorkerResource>.Failure(
                LoomErrors.ExecutionFailed(
                    $"Could not start Python worker: {ex.Message}{detail}"));
        }
    }

    private static async Task WaitForConnectionOrExitAsync(
        Task connection,
        IProcessResource process,
        CancellationToken cancellationToken)
    {
        var handle = new ProcessHandle(
            $"proc_python_start_{process.ProcessId}");

        while (!connection.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = process
                .Snapshot(handle)
                .State;
            if (state is ManagedProcessState.Exited or
                ManagedProcessState.Terminated or
                ManagedProcessState.FailedToStart)
            {
                throw new InvalidOperationException(
                    $"Python worker exited before connecting to the control pipe (state={state}).");
            }

            var delay = Task.Delay(
                TimeSpan.FromMilliseconds(25),
                cancellationToken);
            await Task.WhenAny(
                    connection,
                    delay)
                .ConfigureAwait(false);
        }

        await connection.ConfigureAwait(false);
    }

    private static async Task CleanupAsync(
        NamedPipeServerStream? pipe,
        IProcessResource? process)
    {
        try
        {
            pipe?.Dispose();
        }
        catch
        {
        }

        if (process is null)
        {
            return;
        }

        try
        {
            await process.DisposeAsync()
                .ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static string ReadDiagnostics(
        IProcessResource? process)
    {
        if (process is null)
        {
            return string.Empty;
        }

        try
        {
            var output = process.Read(
                new ProcessHandle(
                    $"proc_python_start_{process.ProcessId}"),
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

            return string.Join(
                " | ",
                new[] { stderr, stdout }
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Select(text =>
                        text.Replace(
                            Environment.NewLine,
                            " ",
                            StringComparison.Ordinal)
                            .Trim()));
        }
        catch
        {
            return string.Empty;
        }
    }
}
