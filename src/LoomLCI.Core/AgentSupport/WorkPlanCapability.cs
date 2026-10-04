using LoomLCI.Core.Invocations;
using LoomLCI.Core.Observability;

namespace LoomLCI.Core.AgentSupport;

public sealed class WorkPlanCapability
{
    public const int MaxSteps = 32;
    public const int MaxStepTextScalars = 512;

    private readonly InvocationRunner _invocations;
    private readonly LoomEventBus _events;

    public WorkPlanCapability(
        InvocationRunner invocations,
        LoomEventBus events)
    {
        _invocations = invocations;
        _events = events;
    }

    public Task<LoomResult<WorkPlanSnapshot>> GetAsync(
        WorkId workId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workId.Value))
        {
            return Task.FromResult(
                LoomResult<WorkPlanSnapshot>.Failure(
                    LoomErrors.InvalidArgument("work_id is required.")));
        }

        return _invocations.RunAsync(
            "agent_support.work_plan.get",
            workId,
            (context, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(context.WorkSession!.GetWorkPlanSnapshot());
            },
            cancellationToken);
    }

    public Task<LoomResult<WorkPlanSnapshot>> UpdateAsync(
        WorkPlanUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var validated = ValidateUpdate(request);
        if (!validated.IsSuccess)
        {
            return Task.FromResult(
                LoomResult<WorkPlanSnapshot>.Failure(validated.Error!));
        }

        var normalizedSteps = validated.Value!;

        return _invocations.RunAsync(
            "agent_support.work_plan.update",
            request.WorkId,
            (context, token) =>
            {
                token.ThrowIfCancellationRequested();

                var updated = context.WorkSession!.UpdateWorkPlan(
                    request.ExpectedRevision,
                    normalizedSteps);
                if (!updated.IsSuccess)
                {
                    return Task.FromResult(updated);
                }

                var snapshot = updated.Value!;
                _events.Publish(
                    "WorkPlanUpdated",
                    "agent_support.work_plan",
                    context.WorkSession.Id,
                    context.Id,
                    payload: new Dictionary<string, object?>
                    {
                        ["revision"] = snapshot.Revision,
                        ["stepCount"] = snapshot.Steps.Count,
                        ["pendingCount"] = snapshot.Steps.Count(step => step.Status == WorkPlanStepStatus.Pending),
                        ["activeCount"] = snapshot.Steps.Count(step => step.Status == WorkPlanStepStatus.Active),
                        ["waitingCount"] = snapshot.Steps.Count(step => step.Status == WorkPlanStepStatus.Waiting),
                        ["completedCount"] = snapshot.Steps.Count(step => step.Status == WorkPlanStepStatus.Completed)
                    });

                return Task.FromResult(updated);
            },
            cancellationToken);
    }

    private static LoomResult<IReadOnlyList<WorkPlanStepInput>> ValidateUpdate(
        WorkPlanUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkId.Value))
        {
            return LoomResult<IReadOnlyList<WorkPlanStepInput>>.Failure(
                LoomErrors.InvalidArgument("work_id is required."));
        }

        if (request.ExpectedRevision < 0)
        {
            return LoomResult<IReadOnlyList<WorkPlanStepInput>>.Failure(
                LoomErrors.InvalidArgument("expected_revision must be non-negative."));
        }

        if (request.Steps is null || request.Steps.Count > MaxSteps)
        {
            return LoomResult<IReadOnlyList<WorkPlanStepInput>>.Failure(
                LoomErrors.InvalidArgument(
                    $"steps must contain between 0 and {MaxSteps} entries."));
        }

        var normalized = new WorkPlanStepInput[request.Steps.Count];

        for (var index = 0; index < request.Steps.Count; index++)
        {
            var step = request.Steps[index];
            if (step is null)
            {
                return LoomResult<IReadOnlyList<WorkPlanStepInput>>.Failure(
                    LoomErrors.InvalidArgument("steps cannot contain null entries."));
            }

            if (step.Id is { } id && string.IsNullOrWhiteSpace(id.Value))
            {
                return LoomResult<IReadOnlyList<WorkPlanStepInput>>.Failure(
                    LoomErrors.InvalidArgument("step id cannot be empty when supplied."));
            }

            if (!Enum.IsDefined(step.Status))
            {
                return LoomResult<IReadOnlyList<WorkPlanStepInput>>.Failure(
                    LoomErrors.InvalidArgument("step status is invalid."));
            }

            var text = ValidateAndNormalizeText(step.Text);
            if (!text.IsSuccess)
            {
                return LoomResult<IReadOnlyList<WorkPlanStepInput>>.Failure(text.Error!);
            }

            normalized[index] = new WorkPlanStepInput(step.Id, text.Value!, step.Status);
        }

        return LoomResult<IReadOnlyList<WorkPlanStepInput>>.Success(
            Array.AsReadOnly(normalized));
    }

    private static LoomResult<string> ValidateAndNormalizeText(string? text)
    {
        if (text is null)
        {
            return LoomResult<string>.Failure(
                LoomErrors.InvalidArgument("step text is required."));
        }

        if (text.Contains('\r') || text.Contains('\n'))
        {
            return LoomResult<string>.Failure(
                LoomErrors.InvalidArgument("step text must be a single line."));
        }

        var scalarCount = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (char.IsHighSurrogate(current))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                {
                    return LoomResult<string>.Failure(
                        LoomErrors.InvalidArgument("step text must contain valid Unicode."));
                }

                index++;
                scalarCount++;
                continue;
            }

            if (char.IsLowSurrogate(current))
            {
                return LoomResult<string>.Failure(
                    LoomErrors.InvalidArgument("step text must contain valid Unicode."));
            }

            scalarCount++;
        }

        var normalized = text.Trim();
        if (normalized.Length == 0)
        {
            return LoomResult<string>.Failure(
                LoomErrors.InvalidArgument("step text cannot be empty or whitespace."));
        }

        var trimmedScalarCount = scalarCount;
        if (!string.Equals(normalized, text, StringComparison.Ordinal))
        {
            trimmedScalarCount = CountUnicodeScalars(normalized);
        }

        if (trimmedScalarCount > MaxStepTextScalars)
        {
            return LoomResult<string>.Failure(
                LoomErrors.InvalidArgument(
                    $"step text must be no more than {MaxStepTextScalars} Unicode scalar values."));
        }

        return LoomResult<string>.Success(normalized);
    }

    private static int CountUnicodeScalars(string text)
    {
        var count = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                index++;
            }

            count++;
        }

        return count;
    }
}
