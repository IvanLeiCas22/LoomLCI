using System.ComponentModel;
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
    string State);

public sealed record ProcessStatusDto(
    string ProcessHandle,
    int ProcessId,
    string State,
    int? ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? ExitedAt);

public sealed record OutputChunkDto(long Cursor, string Text);

public sealed record OutputStreamDto(
    long RequestedCursor,
    long EarliestAvailableCursor,
    long NextCursor,
    bool Truncated,
    IReadOnlyList<OutputChunkDto> Chunks);

public sealed record ProcessReadDto(
    ProcessStatusDto Process,
    OutputStreamDto Stdout,
    OutputStreamDto Stderr);

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
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<ProcessStartDto>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true)]
    [Description("Starts a process directly without shell parsing and returns a durable process handle immediately.")]
    public async Task<CallToolResult> Start(
        [Description("Executable path or executable name resolved by Windows.")] string executable,
        [Description("Arguments passed directly as an argument array, without shell quoting.")] string[]? arguments = null,
        [Description("Optional working directory. Relative paths require a work session with a base directory.")] string? workingDirectory = null,
        [Description("Work session handle. Required for session-owned processes.")] string? workId = null,
        [Description("If true, the process is not cleaned up when the work session closes.")] bool independent = false,
        [Description("Environment variable overrides. A null value removes that variable.")] Dictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ProcessStartRequest(
            executable,
            arguments,
            workingDirectory,
            environment,
            string.IsNullOrWhiteSpace(workId) ? null : new WorkId(workId),
            independent ? ResourceOwnership.Independent : ResourceOwnership.SessionOwned);

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
                    value.State.ToString().ToLowerInvariant())));
        }

        return McpToolResults.From(envelope);
    }

    [McpServerTool(
        Name = "process_status",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<ProcessStatusDto>),
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Returns the current state and exit metadata for a Loom process handle.")]
    public async Task<CallToolResult> Status(
        [Description("Process handle returned by process_start.")] string processHandle,
        CancellationToken cancellationToken = default)
    {
        var result = await _processes.StatusAsync(new ProcessHandle(processHandle), cancellationToken).ConfigureAwait(false);
        return McpToolResults.From(MapStatus(result));
    }

    [McpServerTool(
        Name = "process_read",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<ProcessReadDto>),
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Reads buffered stdout/stderr non-destructively using monotonic cursors.")]
    public async Task<CallToolResult> Read(
        [Description("Process handle returned by process_start.")] string processHandle,
        [Description("Next stdout cursor from a previous read, or 0 for the beginning still retained.")] long stdoutCursor = 0,
        [Description("Next stderr cursor from a previous read, or 0 for the beginning still retained.")] long stderrCursor = 0,
        [Description("Maximum total characters returned, from 1 to 1048576.")] int maxChars = 65536,
        CancellationToken cancellationToken = default)
    {
        var result = await _processes.ReadAsync(
            new ProcessHandle(processHandle),
            stdoutCursor,
            stderrCursor,
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
                    ToDto(value.Stdout),
                    ToDto(value.Stderr))));
        }

        return McpToolResults.From(envelope);
    }

    [McpServerTool(
        Name = "process_write",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<bool>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true)]
    [Description("Writes text to the stdin of a running pipe-based process.")]
    public async Task<CallToolResult> Write(
        [Description("Process handle returned by process_start.")] string processHandle,
        [Description("Text to write verbatim to stdin. Include newline characters when required by the target process.")] string text,
        CancellationToken cancellationToken = default)
    {
        var result = await _processes.WriteAsync(new ProcessHandle(processHandle), text, cancellationToken).ConfigureAwait(false);
        var envelope = result.IsSuccess
            ? ToolEnvelope<bool>.From(LoomResult<bool>.Success(true))
            : ToolEnvelope<bool>.From(LoomResult<bool>.Failure(result.Error!));

        return McpToolResults.From(envelope);
    }

    [McpServerTool(
        Name = "process_terminate",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<bool>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = true)]
    [Description("Terminates a Loom-managed process and its descendant process tree.")]
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
            value.ExitedAt);

    private static OutputStreamDto ToDto(OutputStreamReadResult value)
        => new(
            value.RequestedCursor,
            value.EarliestAvailableCursor,
            value.NextCursor,
            value.Truncated,
            value.Chunks.Select(c => new OutputChunkDto(c.Cursor, c.Text)).ToArray());
}
