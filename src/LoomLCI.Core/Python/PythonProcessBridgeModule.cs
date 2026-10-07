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
