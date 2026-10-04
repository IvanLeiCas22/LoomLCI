using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using LoomLCI.Core;
using LoomLCI.Core.Processes;
using LoomLCI.Core.Resources;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LoomLCI.Mcp;

public sealed record ProcessStartDto(
    string ProcessHandle,
    int ProcessId,
    DateTimeOffset StartedAt,
    string State,
    string IoMode,
    long PostExitRetentionSeconds);

public sealed record ProcessStatusDto(
    string ProcessHandle,
    int ProcessId,
    string State,
    int? ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? ExitedAt,
    string IoMode,
    DateTimeOffset? RetentionExpiresAt);

public sealed record OutputChunkDto(long Cursor, string Text);

public sealed record OutputStreamDto(
    long RequestedCursor,
    long EarliestAvailableCursor,
    long NextCursor,
    long RetainedUntilCursor,
    long ObservedUntilCursor,
    bool Truncated,
    bool RetentionLimitReached,
    IReadOnlyList<OutputChunkDto> Chunks);

public sealed record ProcessReadDto(
    ProcessStatusDto Process,
    string IoMode,
    OutputStreamDto? Stdout,
    OutputStreamDto? Stderr,
    OutputStreamDto? Terminal);

[McpServerToolType]
public sealed class ProcessTools
{
    private readonly ProcessCapability _processes;

    public ProcessTools(ProcessCapability processes)
    {
        _processes = processes;
    }

    [McpServerTool(
        Name = "process_start",
        Title = "Start process",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<ProcessStartDto>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description("Starts one executable directly, without shell parsing, and returns a durable process handle immediately. ioMode=pipes is the default for normal automation with separate stdout/stderr; ioMode=terminal requests a real Windows terminal for shells, REPLs, TUIs, or programs that require console semantics. Pass arguments as an explicit array. For cmd.exe syntax such as pipes, redirection, &&, or built-ins, explicitly start cmd.exe with /d /s /c; for PowerShell syntax, explicitly start powershell.exe or pwsh.exe. After the process reaches a terminal state, the handle and retained output remain available for the reported postExitRetentionSeconds; successful process_status/process_read calls refresh that post-exit retention window. A live process does not expire merely because it is not polled. Use process_read when output matters and process_status when only state or exit metadata is needed.")]
    public async Task<CallToolResult> Start(
        [Description("Executable path or executable name resolved by Windows. This tool does not infer or insert a shell.")] string executable,
        [Description("Arguments passed directly to the executable as an argument array. Do not apply shell quoting or combine multiple shell tokens into one argument unless the target executable itself expects that.")] string[]? arguments = null,
        [Description("Optional working directory. Relative paths require a work session with a base directory.")] string? workingDirectory = null,
        [Description("Work session handle. Required for session-owned processes.")] string? workId = null,
        [Description("If true, the live process is not cleaned up when the work session closes. It remains Loom-managed: after the root process reaches a terminal state, post-exit retention still applies, and expiry releases retained output plus any descendants still in the process Job.")] bool independent = false,
        [Description("Environment variable overrides. A null value removes that variable.")] Dictionary<string, string?>? environment = null,
        [Description("Process I/O mode. pipes keeps separate stdout/stderr and is the default; terminal requests ConPTY terminal semantics.")][AllowedValues("pipes", "terminal")] string ioMode = "pipes",
        [Description("Optional initial terminal width in columns. Only valid with ioMode=terminal; defaults to 80 there.")][Range(1, short.MaxValue)] int? terminalColumns = null,
        [Description("Optional initial terminal height in rows. Only valid with ioMode=terminal; defaults to 24 there.")][Range(1, short.MaxValue)] int? terminalRows = null,
        CancellationToken cancellationToken = default)
    {
        var parsedIoMode = ioMode switch
        {
            "pipes" => ProcessIoMode.Pipes,
            "terminal" => ProcessIoMode.Terminal,
            _ => (ProcessIoMode?)null
        };

        if (parsedIoMode is null)
        {
            return McpToolResults.From(
                ToolEnvelope<ProcessStartDto>.From(
                    LoomResult<ProcessStartDto>.Failure(
                        LoomErrors.InvalidArgument("ioMode must be 'pipes' or 'terminal'."))));
        }

        var request = new ProcessStartRequest(
            executable,
            arguments,
            workingDirectory,
            environment,
            string.IsNullOrWhiteSpace(workId) ? null : new WorkId(workId),
            independent ? ResourceOwnership.Independent : ResourceOwnership.SessionOwned,
            parsedIoMode.Value,
            terminalColumns,
            terminalRows);

        var result = await _processes.StartAsync(request, cancellationToken).ConfigureAwait(false);
        ToolEnvelope<ProcessStartDto> envelope;

        if (!result.IsSuccess)
        {
            envelope = ToolEnvelope<ProcessStartDto>.From(LoomResult<ProcessStartDto>.Failure(result.Error!));
        }
        else
        {
            var value = result.Value!;
            envelope = ToolEnvelope<ProcessStartDto>.From(LoomResult<ProcessStartDto>.Success(
                new ProcessStartDto(
                    value.Handle.Value,
                    value.ProcessId,
                    value.StartedAt,
                    value.State.ToString().ToLowerInvariant(),
                    value.IoMode.ToString().ToLowerInvariant(),
                    checked((long)value.PostExitRetention.TotalSeconds))));
        }

        return McpToolResults.From(envelope);
    }

    [McpServerTool(
        Name = "process_status",
        Title = "Get process status",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<ProcessStatusDto>),
        ReadOnly = true,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description("Returns only the current state, I/O mode, exit metadata, and post-exit retention deadline for a Loom process handle. For an exited or terminated process, a successful call refreshes the post-exit retention window and returns the new retentionExpiresAt. Running processes do not expire for lack of polling. Use this when output is irrelevant; use process_read instead when process output is needed because process_read also includes current process state.")]
    public async Task<CallToolResult> Status(
        [Description("Process handle returned by process_start.")] string processHandle,
        CancellationToken cancellationToken = default)
    {
        var result = await _processes.StatusAsync(new ProcessHandle(processHandle), cancellationToken).ConfigureAwait(false);
        return McpToolResults.From(MapStatus(result));
    }

    [McpServerTool(
        Name = "process_read",
        Title = "Read process output",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<ProcessReadDto>),
        ReadOnly = true,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description("Reads retained process output non-destructively and also returns current process state. For an exited or terminated process, a successful read refreshes the post-exit retention window and returns the new retentionExpiresAt inside process. Pipe-based processes expose separate stdout/stderr streams; terminal processes expose one terminal stream containing raw UTF-8-decoded text plus VT/ANSI sequences. Cursors are absolute UTF-16 positions: reuse each returned nextCursor to continue exactly after the last returned text, even when maxChars cuts through a producer read. maxChars limits only this response, not capture; a read may exceed it by one UTF-16 code unit rather than split a surrogate pair. Each active stream is spooled to temporary storage up to a 64 MiB retention quota; if that quota is exceeded, retentionLimitReached is true and retainedUntilCursor/observedUntilCursor make the omitted tail explicit.")]
    public async Task<CallToolResult> Read(
        [Description("Process handle returned by process_start.")] string processHandle,
        [Description("Next stdout cursor for a pipe-based process, or 0. Must remain 0 for terminal processes.")][Range(0, long.MaxValue)] long stdoutCursor = 0,
        [Description("Next stderr cursor for a pipe-based process, or 0. Must remain 0 for terminal processes.")][Range(0, long.MaxValue)] long stderrCursor = 0,
        [Description("Next terminal cursor for a terminal process, or 0. Must remain 0 for pipe-based processes.")][Range(0, long.MaxValue)] long terminalCursor = 0,
        [Description("Maximum total characters returned across the active process output streams.")][Range(1, 1048576)] int maxChars = 65536,
        CancellationToken cancellationToken = default)
    {
        var result = await _processes.ReadAsync(
            new ProcessHandle(processHandle),
            stdoutCursor,
            stderrCursor,
            terminalCursor,
            maxChars,
            cancellationToken).ConfigureAwait(false);

        ToolEnvelope<ProcessReadDto> envelope;
        if (!result.IsSuccess)
        {
            envelope = ToolEnvelope<ProcessReadDto>.From(LoomResult<ProcessReadDto>.Failure(result.Error!));
        }
        else
        {
            var value = result.Value!;
            envelope = ToolEnvelope<ProcessReadDto>.From(LoomResult<ProcessReadDto>.Success(
                new ProcessReadDto(
                    ToDto(value.Process),
                    value.IoMode.ToString().ToLowerInvariant(),
                    ToDto(value.Stdout),
                    ToDto(value.Stderr),
                    ToDto(value.Terminal))));
        }

        return McpToolResults.From(envelope);
    }

    [McpServerTool(
        Name = "process_write",
        Title = "Write process stdin",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<bool>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description("Writes text verbatim to a running process input. For pipe-based processes this writes stdin; for terminal processes this writes the ConPTY input stream, including control characters or VT sequences. To send Ctrl+C to a terminal without terminating the terminal process, write \\u0003 (ETX). No newline is appended automatically; include it in text when the target expects Enter or a line terminator.")]
    public async Task<CallToolResult> Write(
        [Description("Process handle returned by process_start.")] string processHandle,
        [Description("Text to write verbatim to the process input. Include newline or control characters when required by the target process. For terminal Ctrl+C, write \\u0003 (ETX).")] string text,
        CancellationToken cancellationToken = default)
    {
        var result = await _processes.WriteAsync(new ProcessHandle(processHandle), text, cancellationToken).ConfigureAwait(false);
        var envelope = result.IsSuccess
            ? ToolEnvelope<bool>.From(LoomResult<bool>.Success(true))
            : ToolEnvelope<bool>.From(LoomResult<bool>.Failure(result.Error!));

        return McpToolResults.From(envelope);
    }

    [McpServerTool(
        Name = "process_resize",
        Title = "Resize process terminal",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<bool>),
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Resizes a running terminal-mode process. This is only supported for processes started with ioMode=terminal; pipe-based processes do not have terminal dimensions.")]
    public async Task<CallToolResult> Resize(
        [Description("Process handle returned by process_start.")] string processHandle,
        [Description("Terminal width in columns.")][Range(1, short.MaxValue)] int columns,
        [Description("Terminal height in rows.")][Range(1, short.MaxValue)] int rows,
        CancellationToken cancellationToken = default)
    {
        var result = await _processes.ResizeAsync(
            new ProcessHandle(processHandle),
            columns,
            rows,
            cancellationToken).ConfigureAwait(false);
        var envelope = result.IsSuccess
            ? ToolEnvelope<bool>.From(LoomResult<bool>.Success(true))
            : ToolEnvelope<bool>.From(LoomResult<bool>.Failure(result.Error!));

        return McpToolResults.From(envelope);
    }

    [McpServerTool(
        Name = "process_terminate",
        Title = "Terminate process",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<bool>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description("Terminates a Loom-managed process and its descendant process tree. Use this only when the process should be stopped rather than allowed to exit normally. Termination itself is idempotent: calling it again after the process has already exited or been terminated succeeds without changing the final process state. A successful call also refreshes the post-exit retention window, so the overall tool call is not annotated as idempotent.")]
    public async Task<CallToolResult> Terminate(
        [Description("Process handle returned by process_start.")] string processHandle,
        CancellationToken cancellationToken = default)
    {
        var result = await _processes.TerminateAsync(new ProcessHandle(processHandle), cancellationToken).ConfigureAwait(false);
        var envelope = result.IsSuccess
            ? ToolEnvelope<bool>.From(LoomResult<bool>.Success(true))
            : ToolEnvelope<bool>.From(LoomResult<bool>.Failure(result.Error!));

        return McpToolResults.From(envelope);
    }

    [McpServerTool(
        Name = "process_release",
        Title = "Release process handle",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<bool>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Explicitly releases a retained Loom process handle after the root process has exited or been terminated. This permanently discards retained process state and output, closes process/Job/pipe or terminal resources, and may terminate descendants that are still attached to the Loom Job. Read any final output you need before calling this tool. This does not stop a live process: starting, running, or terminating processes return conflict; use process_terminate first when the process should be stopped. Releasing an already closed or expired handle is idempotent while its tombstone is retained.")]
    public async Task<CallToolResult> Release(
        [Description("Process handle returned by process_start. The root process must already be exited or terminated.")] string processHandle,
        CancellationToken cancellationToken = default)
    {
        var result = await _processes.ReleaseAsync(
            new ProcessHandle(processHandle),
            cancellationToken).ConfigureAwait(false);
        var envelope = result.IsSuccess
            ? ToolEnvelope<bool>.From(LoomResult<bool>.Success(true))
            : ToolEnvelope<bool>.From(LoomResult<bool>.Failure(result.Error!));

        return McpToolResults.From(envelope);
    }

    private static ToolEnvelope<ProcessStatusDto> MapStatus(LoomResult<ProcessStatusResult> result)
        => result.IsSuccess
            ? ToolEnvelope<ProcessStatusDto>.From(LoomResult<ProcessStatusDto>.Success(ToDto(result.Value!)))
            : ToolEnvelope<ProcessStatusDto>.From(LoomResult<ProcessStatusDto>.Failure(result.Error!));

    private static ProcessStatusDto ToDto(ProcessStatusResult value)
        => new(
            value.Handle.Value,
            value.ProcessId,
            value.State.ToString().ToLowerInvariant(),
            value.ExitCode,
            value.StartedAt,
            value.ExitedAt,
            value.IoMode.ToString().ToLowerInvariant(),
            value.RetentionExpiresAt);

    private static OutputStreamDto? ToDto(OutputStreamReadResult? value)
        => value is null
            ? null
            : new OutputStreamDto(
                value.RequestedCursor,
                value.EarliestAvailableCursor,
                value.NextCursor,
                value.RetainedUntilCursor,
                value.ObservedUntilCursor,
                value.Truncated,
                value.RetentionLimitReached,
                value.Chunks.Select(c => new OutputChunkDto(c.Cursor, c.Text)).ToArray());
}
