using System.ComponentModel;
using LoomLCI.Core;
using LoomLCI.Core.Work;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LoomLCI.Mcp;

public sealed record WorkSessionDto(
    string WorkId,
    string? BaseDirectory,
    string? Label,
    string State,
    DateTimeOffset CreatedAt,
    long IdleTimeoutSeconds);

[McpServerToolType]
public sealed class WorkTools
{
    private readonly WorkSessionManager _workSessions;

    public WorkTools(WorkSessionManager workSessions)
    {
        _workSessions = workSessions;
    }

    [McpServerTool(
        Name = "work_create",
        Title = "Create work session",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<WorkSessionDto>),
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description("Creates a work session for calls that need a shared base directory or session-owned resources. Use it before relative-path operations or session-owned process_start calls; stateless operations with fully explicit absolute paths do not require one. Inactive work sessions expire automatically after the idle timeout reported in the result; active invocations keep the session alive.")]
    public CallToolResult Create(
        [Description("Optional base directory used to resolve relative paths. This is context, not a security boundary.")] string? baseDirectory = null,
        [Description("Optional human-readable label for the work session.")] string? label = null)
    {
        var result = _workSessions.Create(baseDirectory, label);
        ToolEnvelope<WorkSessionDto> envelope;

        if (!result.IsSuccess)
        {
            envelope = ToolEnvelope<WorkSessionDto>.From(LoomResult<WorkSessionDto>.Failure(result.Error!));
        }
        else
        {
            var session = result.Value!;
            envelope = ToolEnvelope<WorkSessionDto>.From(LoomResult<WorkSessionDto>.Success(
                new WorkSessionDto(
                    session.Id.Value,
                    session.BaseDirectory,
                    session.Label,
                    session.State.ToString().ToLowerInvariant(),
                    session.CreatedAt,
                    checked((long)_workSessions.IdleTimeout.TotalSeconds))));
        }

        return McpToolResults.From(envelope);
    }

    [McpServerTool(
        Name = "work_close",
        Title = "Close work session",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<bool>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Closes a work session and cleans up its session-owned resources. After close, session-owned resource handles are closed and can no longer be inspected with tools such as process_status or process_read, so read any final state or output you need before closing. Call this when the task using that session is complete; independent resources are not owned by the session.")]
    public async Task<CallToolResult> Close(
        [Description("The work session handle returned by work_create.")] string workId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _workSessions.CloseAsync(new WorkId(workId)).ConfigureAwait(false);
        var envelope = result.IsSuccess
            ? ToolEnvelope<bool>.From(LoomResult<bool>.Success(true))
            : ToolEnvelope<bool>.From(LoomResult<bool>.Failure(result.Error!));

        return McpToolResults.From(envelope);
    }
}
