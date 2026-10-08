using System.Text.Json;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Resources;

namespace LoomLCI.Core.Python;

public sealed class PythonProcessBridgeModule : IPythonBridgeModule
{
    private static readonly string[] SupportedMethods =
    [
        "process.read",
        "process.release",
        "process.resize",
        "process.run",
        "process.run_many",
        "process.start",
        "process.status",
        "process.terminate",
        "process.write"
    ];

    private readonly ProcessCapability _processes;

    public PythonProcessBridgeModule(
        ProcessCapability processes)
    {
        _processes = processes;
    }

    public IReadOnlyList<string> Methods => SupportedMethods;

    public Task<LoomResult<JsonElement>> DispatchAsync(
        WorkId workId,
        PythonBridgeCall call,
        CancellationToken cancellationToken)
        => call.Method switch
        {
            "process.run" => RunAsync(
                workId,
                call.Arguments,
                cancellationToken),
            "process.run_many" => RunManyAsync(
                workId,
                call.Arguments,
                cancellationToken),
            "process.start" => StartAsync(
                workId,
                call.Arguments,
                cancellationToken),
            "process.status" => StatusAsync(
                workId,
                call.Arguments,
                cancellationToken),
            "process.read" => ReadAsync(
                workId,
                call.Arguments,
                cancellationToken),
            "process.write" => WriteAsync(
                workId,
                call.Arguments,
                cancellationToken),
            "process.resize" => ResizeAsync(
                workId,
                call.Arguments,
                cancellationToken),
            "process.terminate" => TerminateAsync(
                workId,
                call.Arguments,
                cancellationToken),
            "process.release" => ReleaseAsync(
                workId,
                call.Arguments,
                cancellationToken),
            _ => Task.FromResult(
                LoomResult<JsonElement>.Failure(
                    LoomErrors.Unsupported(
                        $"Python process bridge method '{call.Method}' is not supported.")))
        };

    private async Task<LoomResult<JsonElement>> RunAsync(
        WorkId workId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parsed = PythonBridgeJson.Deserialize<RunArguments>(
            arguments,
            "process.run");
        if (!parsed.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                parsed.Error!);
        }

        var value = parsed.Value!;
        if (string.IsNullOrWhiteSpace(value.Executable))
        {
            return LoomResult<JsonElement>.Failure(
                LoomErrors.InvalidArgument(
                    "process.run requires executable."));
        }

        var result = await _processes.RunAsync(
                new ProcessRunRequest(
                    value.Executable,
                    value.Arguments,
                    value.WorkingDirectory,
                    value.Environment,
                    workId,
                    TimeSpan.FromSeconds(
                        value.TimeoutSeconds),
                    value.MaxOutputChars),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                result.Error!);
        }

        var run = result.Value!;
        return PythonBridgeJson.MapValue(
            "process.run",
            new ProcessRunBridgeResult(
                run.ProcessId,
                run.ExitCode,
                run.StartedAt,
                run.ExitedAt,
                run.Stdout,
                run.Stderr,
                run.StdoutTruncated,
                run.StderrTruncated,
                run.StdoutObservedChars,
                run.StderrObservedChars));
    }

    // One-shot processes are supervised by ProcessCapability, which awaits
    // exit, output pumps and resource disposal before completing.
    private async Task<LoomResult<JsonElement>> RunManyAsync(
        WorkId workId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parsed = PythonBridgeJson.Deserialize<RunManyArguments>(
            arguments, "process.run_many");
        if (!parsed.IsSuccess)
            return LoomResult<JsonElement>.Failure(parsed.Error!);

        var value = parsed.Value!;
        if (value.Jobs is null or { Length: < 1 or > 32 } ||
            value.MaxConcurrent is < 1 or > 8 ||
            value.JobTimeoutSeconds is < 1 or > 600 ||
            value.BatchTimeoutSeconds is < 1 or > 540 ||
            value.MaxOutputChars is < 1 or > 65_536)
        {
            return LoomResult<JsonElement>.Failure(
                LoomErrors.InvalidArgument(
                    "process.run_many requires 1-32 jobs, max_concurrent 1-8, " +
                    "job_timeout_seconds 1-600, batch_timeout_seconds 1-540, " +
                    "and max_output_chars 1-65536."));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var job in value.Jobs)
        {
            if (job is null ||
                string.IsNullOrWhiteSpace(job.Id) ||
                job.Id.Length > 64 ||
                !ids.Add(job.Id) ||
                string.IsNullOrWhiteSpace(job.Executable))
                return LoomResult<JsonElement>.Failure(
                    LoomErrors.InvalidArgument(
                        "Each job requires a unique nonempty id (up to 64 chars) and executable."));
        }

        using var deadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(value.BatchTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token);
        using var limiter = new SemaphoreSlim(value.MaxConcurrent);
        var results = new RunManyJobResult[value.Jobs.Length];

        async Task RunJobAsync(int index)
        {
            var job = value.Jobs[index];
            var entered = false;
            try
            {
                await limiter.WaitAsync(linked.Token).ConfigureAwait(false);
                entered = true;
                linked.Token.ThrowIfCancellationRequested();
                var run = await _processes.RunAsync(
                    new ProcessRunRequest(
                        job.Executable!, job.Arguments, job.WorkingDirectory,
                        job.Environment, workId,
                        TimeSpan.FromSeconds(value.JobTimeoutSeconds),
                        value.MaxOutputChars),
                    linked.Token).ConfigureAwait(false);

                if (run.IsSuccess)
                {
                    var done = run.Value!;
                    results[index] = new RunManyJobResult(
                        job.Id!, done.ExitCode == 0 ? "success" : "nonzero_exit",
                        done.ExitCode, done.ProcessId, done.StartedAt, done.ExitedAt,
                        done.Stdout, done.Stderr,
                        done.StdoutTruncated, done.StderrTruncated,
                        done.StdoutObservedChars, done.StderrObservedChars,
                        null, null);
                }
                else
                {
                    var error = run.Error!;
                    var timeout = error.Code == "deadline_exceeded" ||
                        deadline.IsCancellationRequested;
                    results[index] = FailureJob(
                        job.Id!, timeout ? "timeout" : "launch_error",
                        timeout ? "deadline_exceeded" : error.Code, error.Message);
                }
            }
            catch (OperationCanceledException)
            {
                results[index] = FailureJob(job.Id!,
                    entered ? "timeout" : "not_started",
                    deadline.IsCancellationRequested ? "deadline_exceeded" : "cancelled",
                    "Batch was cancelled before the job completed.");
            }
            catch (Exception ex)
            {
                results[index] = FailureJob(job.Id!, "launch_error",
                    "execution_failed", ex.Message);
            }
            finally
            {
                if (entered) limiter.Release();
            }
        }

        // When externally cancelled, await every active ProcessCapability.RunAsync
        // before propagating cancellation; its disposal must not be skipped.
        await Task.WhenAll(Enumerable.Range(0, value.Jobs.Length)
            .Select(RunJobAsync)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return PythonBridgeJson.MapValue("process.run_many",
            new RunManyBridgeResult(results, deadline.IsCancellationRequested));
    }

    private static RunManyJobResult FailureJob(
        string id, string outcome, string errorCode, string errorMessage)
        => new(id, outcome, null, null, null, null, "", "", false, false,
            0, 0, errorCode, errorMessage);

    private async Task<LoomResult<JsonElement>> StartAsync(
        WorkId workId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parsed = PythonBridgeJson.Deserialize<StartArguments>(
            arguments,
            "process.start");
        if (!parsed.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                parsed.Error!);
        }

        var value = parsed.Value!;
        if (string.IsNullOrWhiteSpace(value.Executable))
        {
            return LoomResult<JsonElement>.Failure(
                LoomErrors.InvalidArgument(
                    "process.start requires executable."));
        }

        if (!TryParseIoMode(
                value.IoMode,
                out var ioMode))
        {
            return LoomResult<JsonElement>.Failure(
                LoomErrors.InvalidArgument(
                    "io_mode must be 'pipes' or 'terminal'."));
        }

        var result = await _processes.StartAsync(
                new ProcessStartRequest(
                    value.Executable,
                    value.Arguments,
                    value.WorkingDirectory,
                    value.Environment,
                    workId,
                    ResourceOwnership.SessionOwned,
                    ioMode,
                    value.TerminalColumns,
                    value.TerminalRows),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                result.Error!);
        }

        var started = result.Value!;
        return PythonBridgeJson.MapValue(
            "process.start",
            new ProcessStartBridgeResult(
                started.Handle.Value,
                started.ProcessId,
                started.StartedAt,
                StateText(started.State),
                IoModeText(started.IoMode),
                checked((long)
                    started.PostExitRetention.TotalSeconds)));
    }

    private async Task<LoomResult<JsonElement>> StatusAsync(
        WorkId workId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parsed = PythonBridgeJson.Deserialize<HandleArguments>(
            arguments,
            "process.status");
        if (!parsed.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                parsed.Error!);
        }

        var handle = ParseHandle(
            parsed.Value!.ProcessHandle,
            "process.status");
        if (!handle.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                handle.Error!);
        }

        var ownership = _processes.ValidateSessionOwnedHandle(
            handle.Value,
            workId);
        if (!ownership.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                ownership.Error!);
        }

        var result = await _processes.StatusAsync(
                handle.Value,
                cancellationToken)
            .ConfigureAwait(false);

        return MapStatusResult(
            "process.status",
            result);
    }

    private async Task<LoomResult<JsonElement>> ReadAsync(
        WorkId workId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parsed = PythonBridgeJson.Deserialize<ReadArguments>(
            arguments,
            "process.read");
        if (!parsed.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                parsed.Error!);
        }

        var value = parsed.Value!;
        var handle = ParseHandle(
            value.ProcessHandle,
            "process.read");
        if (!handle.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                handle.Error!);
        }

        var ownership = _processes.ValidateSessionOwnedHandle(
            handle.Value,
            workId);
        if (!ownership.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                ownership.Error!);
        }

        var result = await _processes.ReadAsync(
                handle.Value,
                value.StdoutCursor,
                value.StderrCursor,
                value.TerminalCursor,
                value.MaxChars,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                result.Error!);
        }

        var read = result.Value!;
        return PythonBridgeJson.MapValue(
            "process.read",
            new ProcessReadBridgeResult(
                ToStatus(read.Process),
                IoModeText(read.IoMode),
                ToOutputStream(read.Stdout),
                ToOutputStream(read.Stderr),
                ToOutputStream(read.Terminal)));
    }

    private async Task<LoomResult<JsonElement>> WriteAsync(
        WorkId workId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parsed = PythonBridgeJson.Deserialize<WriteArguments>(
            arguments,
            "process.write");
        if (!parsed.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                parsed.Error!);
        }

        var value = parsed.Value!;
        if (value.Text is null)
        {
            return LoomResult<JsonElement>.Failure(
                LoomErrors.InvalidArgument(
                    "process.write requires text."));
        }

        var handle = ParseHandle(
            value.ProcessHandle,
            "process.write");
        if (!handle.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                handle.Error!);
        }

        var ownership = _processes.ValidateSessionOwnedHandle(
            handle.Value,
            workId);
        if (!ownership.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                ownership.Error!);
        }

        var result = await _processes.WriteAsync(
                handle.Value,
                value.Text,
                cancellationToken)
            .ConfigureAwait(false);

        return MapUnitResult(
            "process.write",
            result);
    }

    private async Task<LoomResult<JsonElement>> ResizeAsync(
        WorkId workId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parsed = PythonBridgeJson.Deserialize<ResizeArguments>(
            arguments,
            "process.resize");
        if (!parsed.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                parsed.Error!);
        }

        var value = parsed.Value!;
        var handle = ParseHandle(
            value.ProcessHandle,
            "process.resize");
        if (!handle.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                handle.Error!);
        }

        var ownership = _processes.ValidateSessionOwnedHandle(
            handle.Value,
            workId);
        if (!ownership.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                ownership.Error!);
        }

        var result = await _processes.ResizeAsync(
                handle.Value,
                value.Columns,
                value.Rows,
                cancellationToken)
            .ConfigureAwait(false);

        return MapUnitResult(
            "process.resize",
            result);
    }

    private async Task<LoomResult<JsonElement>> TerminateAsync(
        WorkId workId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parsed = PythonBridgeJson.Deserialize<HandleArguments>(
            arguments,
            "process.terminate");
        if (!parsed.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                parsed.Error!);
        }

        var handle = ParseHandle(
            parsed.Value!.ProcessHandle,
            "process.terminate");
        if (!handle.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                handle.Error!);
        }

        var ownership = _processes.ValidateSessionOwnedHandle(
            handle.Value,
            workId);
        if (!ownership.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                ownership.Error!);
        }

        var result = await _processes.TerminateAsync(
                handle.Value,
                cancellationToken)
            .ConfigureAwait(false);

        return MapUnitResult(
            "process.terminate",
            result);
    }

    private async Task<LoomResult<JsonElement>> ReleaseAsync(
        WorkId workId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var parsed = PythonBridgeJson.Deserialize<HandleArguments>(
            arguments,
            "process.release");
        if (!parsed.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                parsed.Error!);
        }

        var handle = ParseHandle(
            parsed.Value!.ProcessHandle,
            "process.release");
        if (!handle.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                handle.Error!);
        }

        var ownership = _processes.ValidateSessionOwnedHandle(
            handle.Value,
            workId,
            allowTombstone: true);
        if (!ownership.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                ownership.Error!);
        }

        var result = await _processes.ReleaseAsync(
                handle.Value,
                cancellationToken)
            .ConfigureAwait(false);

        return MapUnitResult(
            "process.release",
            result);
    }

    private static LoomResult<ProcessHandle> ParseHandle(
        string? value,
        string method)
        => string.IsNullOrWhiteSpace(value)
            ? LoomResult<ProcessHandle>.Failure(
                LoomErrors.InvalidArgument(
                    $"{method} requires process_handle."))
            : LoomResult<ProcessHandle>.Success(
                new ProcessHandle(value));

    private static LoomResult<JsonElement> MapStatusResult(
        string method,
        LoomResult<ProcessStatusResult> result)
    {
        if (!result.IsSuccess)
        {
            return LoomResult<JsonElement>.Failure(
                result.Error!);
        }

        return PythonBridgeJson.MapValue(
            method,
            ToStatus(result.Value!));
    }

    private static LoomResult<JsonElement> MapUnitResult(
        string method,
        LoomResult<Unit> result)
        => result.IsSuccess
            ? PythonBridgeJson.MapValue(
                method,
                true)
            : LoomResult<JsonElement>.Failure(
                result.Error!);

    private static ProcessStatusBridgeResult ToStatus(
        ProcessStatusResult value)
        => new(
            value.Handle.Value,
            value.ProcessId,
            StateText(value.State),
            value.ExitCode,
            value.StartedAt,
            value.ExitedAt,
            IoModeText(value.IoMode),
            value.RetentionExpiresAt);

    private static OutputStreamBridgeResult? ToOutputStream(
        OutputStreamReadResult? value)
        => value is null
            ? null
            : new OutputStreamBridgeResult(
                value.RequestedCursor,
                value.EarliestAvailableCursor,
                value.NextCursor,
                value.RetainedUntilCursor,
                value.ObservedUntilCursor,
                value.Truncated,
                value.RetentionLimitReached,
                value.Chunks
                    .Select(chunk =>
                        new OutputChunkBridgeResult(
                            chunk.Cursor,
                            chunk.Text))
                    .ToArray());

    private static string StateText(
        ManagedProcessState state)
        => state.ToString().ToLowerInvariant();

    private static string IoModeText(
        ProcessIoMode mode)
        => mode.ToString().ToLowerInvariant();

    private static bool TryParseIoMode(
        string value,
        out ProcessIoMode mode)
    {
        if (string.Equals(
                value,
                "pipes",
                StringComparison.OrdinalIgnoreCase))
        {
            mode = ProcessIoMode.Pipes;
            return true;
        }

        if (string.Equals(
                value,
                "terminal",
                StringComparison.OrdinalIgnoreCase))
        {
            mode = ProcessIoMode.Terminal;
            return true;
        }

        mode = default;
        return false;
    }

    private sealed class RunArguments
    {
        public string? Executable { get; init; }
        public string[]? Arguments { get; init; }
        public string? WorkingDirectory { get; init; }
        public Dictionary<string, string?>? Environment { get; init; }
        public int TimeoutSeconds { get; init; } = 30;
        public int MaxOutputChars { get; init; } = 65_536;
    }

    private sealed class RunManyArguments
    {
        public RunManyJobArguments[]? Jobs { get; init; }
        public int MaxConcurrent { get; init; } = 4;
        public int JobTimeoutSeconds { get; init; } = 30;
        public int BatchTimeoutSeconds { get; init; } = 45;
        public int MaxOutputChars { get; init; } = 4096;
    }

    private sealed class RunManyJobArguments
    {
        public string? Id { get; init; }
        public string? Executable { get; init; }
        public string[]? Arguments { get; init; }
        public string? WorkingDirectory { get; init; }
        public Dictionary<string, string?>? Environment { get; init; }
    }

    private sealed record RunManyJobResult(
        string Id, string Outcome, int? ExitCode, int? ProcessId,
        DateTimeOffset? StartedAt, DateTimeOffset? ExitedAt,
        string Stdout, string Stderr, bool StdoutTruncated, bool StderrTruncated,
        long StdoutObservedChars, long StderrObservedChars,
        string? ErrorCode, string? ErrorMessage);

    private sealed record RunManyBridgeResult(
        IReadOnlyList<RunManyJobResult> Jobs, bool TimedOut);

    private sealed class StartArguments
    {
        public string? Executable { get; init; }
        public string[]? Arguments { get; init; }
        public string? WorkingDirectory { get; init; }
        public Dictionary<string, string?>? Environment { get; init; }
        public string IoMode { get; init; } = "pipes";
        public int? TerminalColumns { get; init; }
        public int? TerminalRows { get; init; }
    }

    private sealed class HandleArguments
    {
        public string? ProcessHandle { get; init; }
    }

    private sealed class ReadArguments
    {
        public string? ProcessHandle { get; init; }
        public long StdoutCursor { get; init; }
        public long StderrCursor { get; init; }
        public long TerminalCursor { get; init; }
        public int MaxChars { get; init; } = 65_536;
    }

    private sealed class WriteArguments
    {
        public string? ProcessHandle { get; init; }
        public string? Text { get; init; }
    }

    private sealed class ResizeArguments
    {
        public string? ProcessHandle { get; init; }
        public int Columns { get; init; }
        public int Rows { get; init; }
    }

    private sealed record ProcessStartBridgeResult(
        string ProcessHandle,
        int ProcessId,
        DateTimeOffset StartedAt,
        string State,
        string IoMode,
        long PostExitRetentionSeconds);

    private sealed record ProcessRunBridgeResult(
        int ProcessId,
        int ExitCode,
        DateTimeOffset StartedAt,
        DateTimeOffset ExitedAt,
        string Stdout,
        string Stderr,
        bool StdoutTruncated,
        bool StderrTruncated,
        long StdoutObservedChars,
        long StderrObservedChars);

    private sealed record ProcessStatusBridgeResult(
        string ProcessHandle,
        int ProcessId,
        string State,
        int? ExitCode,
        DateTimeOffset StartedAt,
        DateTimeOffset? ExitedAt,
        string IoMode,
        DateTimeOffset? RetentionExpiresAt);

    private sealed record OutputChunkBridgeResult(
        long Cursor,
        string Text);

    private sealed record OutputStreamBridgeResult(
        long RequestedCursor,
        long EarliestAvailableCursor,
        long NextCursor,
        long RetainedUntilCursor,
        long ObservedUntilCursor,
        bool Truncated,
        bool RetentionLimitReached,
        IReadOnlyList<OutputChunkBridgeResult> Chunks);

    private sealed record ProcessReadBridgeResult(
        ProcessStatusBridgeResult Process,
        string IoMode,
        OutputStreamBridgeResult? Stdout,
        OutputStreamBridgeResult? Stderr,
        OutputStreamBridgeResult? Terminal);
}
