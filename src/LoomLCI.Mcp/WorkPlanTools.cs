using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using LoomLCI.Core;
using LoomLCI.Core.AgentSupport;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LoomLCI.Mcp;

public sealed record WorkPlanStepDto(
    string Id,
    string Text,
    string Status);

public sealed record WorkPlanSnapshotDto(
    long Revision,
    IReadOnlyList<WorkPlanStepDto> Steps);

public sealed record WorkPlanStepInputDto(
    [property: Description("Short single-line step text. Maximum 512 Unicode code points.")]
    [property: MinLength(1)]
    [property: MaxLength(512)]
    [property: RegularExpression(@"^[^\r\n]*$")]
    string Text,
    [property: Description("Logical step status.")]
    [property: AllowedValues("pending", "active", "waiting", "completed")]
    string Status,
    [property: Description("Existing opaque step id returned by Loom. Omit or pass null to create a new step.")]
    string? Id = null);

[McpServerToolType]
public sealed class WorkPlanTools
{
    private readonly WorkPlanCapability _workPlan;

    public WorkPlanTools(WorkPlanCapability workPlan)
    {
        _workPlan = workPlan;
    }

    [McpServerTool(
        Name = "work_plan_get",
        Title = "Get work plan",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<WorkPlanSnapshotDto>),
        ReadOnly = true,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Returns the current logical Work Plan snapshot for a work session. " +
        "The plan tracks agent-visible progress only; it does not execute, observe, or synchronize Process, Python, Filesystem, or other real work. " +
        "Use this when resuming non-trivial work or before reconciling a conflict from work_plan_update. " +
        "A successful read refreshes the work session idle timeout.")]
    public async Task<CallToolResult> Get(
        [Description("Work session handle returned by work_create.")] string workId,
        CancellationToken cancellationToken = default)
    {
        var result = await _workPlan
            .GetAsync(new WorkId(workId), cancellationToken)
            .ConfigureAwait(false);

        return McpToolResults.From(MapSnapshot(result));
    }

    [McpServerTool(
        Name = "work_plan_update",
        Title = "Update work plan",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<WorkPlanSnapshotDto>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Atomically replaces the complete logical Work Plan snapshot for a work session. " +
        "Use expectedRevision from the latest work_plan_get or successful update; on conflict, reread the plan, reconcile, and retry. " +
        "For a new step omit id or pass null; for an existing step preserve its opaque id exactly. Omitting an existing id removes that step. " +
        "An empty steps array clears the plan. Multiple active and waiting steps are allowed. " +
        "This tool only changes logical plan state; it does not execute steps or alter Process, Python, Filesystem, or other resources.")]
    public async Task<CallToolResult> Update(
        [Description("Work session handle returned by work_create.")] string workId,
        [Description("Revision from the latest observed Work Plan snapshot.")]
        [Range(0, long.MaxValue)]
        long expectedRevision,
        [Description("Complete replacement list of zero to 32 logical steps.")]
        [MaxLength(32)]
        WorkPlanStepInputDto[] steps,
        CancellationToken cancellationToken = default)
    {
        if (steps is null)
        {
            return Failure("steps is required.");
        }

        var mapped = new WorkPlanStepInput[steps.Length];
        for (var index = 0; index < steps.Length; index++)
        {
            var step = steps[index];
            if (step is null)
            {
                return Failure("steps cannot contain null entries.");
            }

            if (!TryParseStatus(step.Status, out var status))
            {
                return Failure(
                    "step status must be 'pending', 'active', 'waiting', or 'completed'.");
            }

            WorkPlanStepId? id = step.Id is null
                ? null
                : new WorkPlanStepId(step.Id);

            mapped[index] = new WorkPlanStepInput(
                id,
                step.Text,
                status);
        }

        var result = await _workPlan
            .UpdateAsync(
                new WorkPlanUpdateRequest(
                    new WorkId(workId),
                    expectedRevision,
                    mapped),
                cancellationToken)
            .ConfigureAwait(false);

        return McpToolResults.From(MapSnapshot(result));
    }

    private static CallToolResult Failure(string message)
        => McpToolResults.From(
            ToolEnvelope<WorkPlanSnapshotDto>.From(
                LoomResult<WorkPlanSnapshotDto>.Failure(
                    LoomErrors.InvalidArgument(message))));

    private static ToolEnvelope<WorkPlanSnapshotDto> MapSnapshot(
        LoomResult<WorkPlanSnapshot> result)
    {
        if (!result.IsSuccess)
        {
            return ToolEnvelope<WorkPlanSnapshotDto>.From(
                LoomResult<WorkPlanSnapshotDto>.Failure(result.Error!));
        }

        var value = result.Value!;
        var dto = new WorkPlanSnapshotDto(
            value.Revision,
            value.Steps
                .Select(step => new WorkPlanStepDto(
                    step.Id.Value,
                    step.Text,
                    FormatStatus(step.Status)))
                .ToArray());

        return ToolEnvelope<WorkPlanSnapshotDto>.From(
            LoomResult<WorkPlanSnapshotDto>.Success(dto));
    }

    private static bool TryParseStatus(
        string? value,
        out WorkPlanStepStatus status)
    {
        status = value switch
        {
            "pending" => WorkPlanStepStatus.Pending,
            "active" => WorkPlanStepStatus.Active,
            "waiting" => WorkPlanStepStatus.Waiting,
            "completed" => WorkPlanStepStatus.Completed,
            _ => default
        };

        return value is "pending" or "active" or "waiting" or "completed";
    }

    private static string FormatStatus(WorkPlanStepStatus status)
        => status switch
        {
            WorkPlanStepStatus.Pending => "pending",
            WorkPlanStepStatus.Active => "active",
            WorkPlanStepStatus.Waiting => "waiting",
            WorkPlanStepStatus.Completed => "completed",
            _ => throw new InvalidOperationException(
                $"Unknown Work Plan step status '{status}'.")
        };
}
