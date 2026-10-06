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

public sealed record WorkPlanPatchChangeInputDto(
    [property: Description("Patch operation: add, update, or remove.")]
    [property: AllowedValues("add", "update", "remove")]
    string Op,
    [property: Description("Existing opaque step id. Required for update/remove and forbidden for add.")]
    string? Id = null,
    [property: Description("Optional short single-line step text. Required for add; optional for update; forbidden for remove.")]
    [property: MinLength(1)]
    [property: MaxLength(512)]
    [property: RegularExpression(@"^[^\r\n]*$")]
    string? Text = null,
    [property: Description("Optional logical step status. add defaults to pending when omitted.")]
    [property: AllowedValues("pending", "active", "waiting", "completed")]
    string? Status = null);

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
        "Reads the current logical Work Plan when resuming or inspecting an existing non-trivial work session, or before reconciling a conflict from work_plan_update/work_plan_patch. " +
        "A newly created WorkSession always starts with an empty plan at revision 0, so do not call this only to initialize a new plan; work_plan_update can start it directly with expectedRevision=0. " +
        "The plan tracks agent-visible progress only; it does not execute, observe, or synchronize Process, Python, Filesystem, or other real work. " +
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
        Title = "Create or replace work plan",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<WorkPlanSnapshotDto>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Creates or atomically replaces the complete logical Work Plan for non-trivial work. " +
        "Use this for initial creation, reordering, full reconciliation, replacement, or clearing; prefer work_plan_patch for small milestone changes to an existing plan. " +
        "On a newly created WorkSession, start directly with expectedRevision=0; otherwise use the revision from the latest work_plan_get or successful mutation. " +
        "On conflict, reread the plan, reconcile, and retry. For a new step omit id or pass null; for an existing step preserve its opaque id exactly. " +
        "Omitting an existing id removes that step; an empty steps array clears the plan. Multiple active and waiting steps are allowed. " +
        "This tool only changes logical plan state; it does not execute steps or alter Process, Python, Filesystem, or other resources.")]
    public async Task<CallToolResult> Update(
        [Description("Work session handle returned by work_create.")] string workId,
        [Description("Expected Work Plan revision. Use 0 immediately after creating a new WorkSession; otherwise use the latest observed revision.")]
        [Range(0, long.MaxValue)]
        long expectedRevision,
        [Description("Complete replacement list of zero to 32 logical steps. Prefer a concise set of outcome-oriented steps.")]
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

    [McpServerTool(
        Name = "work_plan_patch",
        Title = "Patch work plan",
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope<WorkPlanSnapshotDto>),
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false)]
    [Description(
        "Atomically applies one to 32 small changes to the current Work Plan without resending untouched steps. " +
        "Use add to append a new step, update to change text/status of one existing step while preserving omitted fields and position, and remove to delete one existing step. " +
        "Preserve opaque step ids exactly. The whole patch uses expectedRevision compare-and-swap: on conflict no changes are applied, so reread, reconcile, and retry; Loom does not auto-merge concurrent patches. " +
        "A successful call increments revision once and returns the complete normalized snapshot, including ids assigned to added steps. " +
        "Use work_plan_update instead when you need initial creation, reordering, complete replacement, or clearing.")]
    public async Task<CallToolResult> Patch(
        [Description("Work session handle returned by work_create.")] string workId,
        [Description("Expected Work Plan revision from the latest observed snapshot or successful mutation.")]
        [Range(0, long.MaxValue)]
        long expectedRevision,
        [Description("One to 32 atomic patch changes. add uses text plus optional status; update uses id plus text and/or status; remove uses id only.")]
        [MinLength(1)]
        [MaxLength(32)]
        WorkPlanPatchChangeInputDto[] changes,
        CancellationToken cancellationToken = default)
    {
        if (changes is null)
        {
            return Failure("changes is required.");
        }

        var mapped = new WorkPlanPatchChange[changes.Length];
        for (var index = 0; index < changes.Length; index++)
        {
            var change = changes[index];
            if (change is null)
            {
                return Failure("changes cannot contain null entries.");
            }

            if (!TryParsePatchOperation(change.Op, out var operation))
            {
                return Failure("op must be 'add', 'update', or 'remove'.");
            }

            WorkPlanStepStatus? status = null;
            if (change.Status is not null)
            {
                if (!TryParseStatus(change.Status, out var parsedStatus))
                {
                    return Failure(
                        "status must be 'pending', 'active', 'waiting', or 'completed'.");
                }

                status = parsedStatus;
            }

            mapped[index] = new WorkPlanPatchChange(
                operation,
                change.Id is null ? null : new WorkPlanStepId(change.Id),
                change.Text,
                status);
        }

        var result = await _workPlan
            .PatchAsync(
                new WorkPlanPatchRequest(
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

    private static bool TryParsePatchOperation(
        string? value,
        out WorkPlanPatchOperation operation)
    {
        operation = value switch
        {
            "add" => WorkPlanPatchOperation.Add,
            "update" => WorkPlanPatchOperation.Update,
            "remove" => WorkPlanPatchOperation.Remove,
            _ => default
        };

        return value is "add" or "update" or "remove";
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
