namespace LoomLCI.Core.AgentSupport;

public enum WorkPlanStepStatus
{
    Pending,
    Active,
    Waiting,
    Completed
}

public enum WorkPlanPatchOperation
{
    Add,
    Update,
    Remove
}

public sealed record WorkPlanStep(
    WorkPlanStepId Id,
    string Text,
    WorkPlanStepStatus Status);

public sealed record WorkPlanSnapshot(
    long Revision,
    IReadOnlyList<WorkPlanStep> Steps);

public sealed record WorkPlanStepInput(
    WorkPlanStepId? Id,
    string Text,
    WorkPlanStepStatus Status);

public sealed record WorkPlanUpdateRequest(
    WorkId WorkId,
    long ExpectedRevision,
    IReadOnlyList<WorkPlanStepInput> Steps);

public sealed record WorkPlanPatchChange(
    WorkPlanPatchOperation Operation,
    WorkPlanStepId? Id,
    string? Text,
    WorkPlanStepStatus? Status);

public sealed record WorkPlanPatchRequest(
    WorkId WorkId,
    long ExpectedRevision,
    IReadOnlyList<WorkPlanPatchChange> Changes);
