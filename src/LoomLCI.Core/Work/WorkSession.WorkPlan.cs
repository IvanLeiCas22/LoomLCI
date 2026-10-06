using LoomLCI.Core.AgentSupport;

namespace LoomLCI.Core.Work;

public sealed partial class WorkSession
{
    private static readonly WorkPlanSnapshot EmptyWorkPlan = CreateSnapshot(
        revision: 0,
        Array.Empty<WorkPlanStep>());

    private WorkPlanSnapshot _workPlan = EmptyWorkPlan;

    internal LoomResult<WorkPlanSnapshot> GetWorkPlanSnapshot()
    {
        lock (_stateGate)
        {
            if (_state != WorkSessionState.Active)
            {
                return LoomResult<WorkPlanSnapshot>.Failure(UnavailableError());
            }

            return LoomResult<WorkPlanSnapshot>.Success(_workPlan);
        }
    }

    internal LoomResult<WorkPlanSnapshot> UpdateWorkPlan(
        long expectedRevision,
        IReadOnlyList<WorkPlanStepInput> requestedSteps)
    {
        lock (_stateGate)
        {
            var precondition = ValidateMutationPreconditionUnsafe(expectedRevision);
            if (precondition is not null)
            {
                return LoomResult<WorkPlanSnapshot>.Failure(precondition);
            }

            var currentIds = _workPlan.Steps
                .Select(step => step.Id.Value)
                .ToHashSet(StringComparer.Ordinal);
            var requestIds = new HashSet<string>(StringComparer.Ordinal);
            var nextSteps = new WorkPlanStep[requestedSteps.Count];

            for (var index = 0; index < requestedSteps.Count; index++)
            {
                var requested = requestedSteps[index];
                WorkPlanStepId id;

                if (requested.Id is { } existingId)
                {
                    if (!currentIds.Contains(existingId.Value))
                    {
                        return LoomResult<WorkPlanSnapshot>.Failure(
                            LoomErrors.InvalidArgument(
                                $"Work plan step id '{existingId.Value}' does not exist in the current plan."));
                    }

                    id = existingId;
                }
                else
                {
                    id = CreateUniqueStepId(currentIds, requestIds);
                }

                if (!requestIds.Add(id.Value))
                {
                    return LoomResult<WorkPlanSnapshot>.Failure(
                        LoomErrors.InvalidArgument(
                            $"Work plan step id '{id.Value}' appears more than once in the update."));
                }

                nextSteps[index] = new WorkPlanStep(id, requested.Text, requested.Status);
            }

            _workPlan = CreateSnapshot(_workPlan.Revision + 1, nextSteps);
            return LoomResult<WorkPlanSnapshot>.Success(_workPlan);
        }
    }

    internal LoomResult<WorkPlanSnapshot> PatchWorkPlan(
        long expectedRevision,
        IReadOnlyList<WorkPlanPatchChange> changes)
    {
        lock (_stateGate)
        {
            var precondition = ValidateMutationPreconditionUnsafe(expectedRevision);
            if (precondition is not null)
            {
                return LoomResult<WorkPlanSnapshot>.Failure(precondition);
            }

            var nextSteps = _workPlan.Steps.ToList();
            var currentIds = _workPlan.Steps
                .Select(step => step.Id.Value)
                .ToHashSet(StringComparer.Ordinal);
            var targetedIds = new HashSet<string>(StringComparer.Ordinal);
            var generatedIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var change in changes)
            {
                switch (change.Operation)
                {
                    case WorkPlanPatchOperation.Add:
                    {
                        var id = CreateUniqueStepId(currentIds, generatedIds);
                        generatedIds.Add(id.Value);
                        nextSteps.Add(
                            new WorkPlanStep(
                                id,
                                change.Text!,
                                change.Status ?? WorkPlanStepStatus.Pending));
                        break;
                    }

                    case WorkPlanPatchOperation.Update:
                    case WorkPlanPatchOperation.Remove:
                    {
                        var id = change.Id!.Value;
                        if (!currentIds.Contains(id.Value))
                        {
                            return LoomResult<WorkPlanSnapshot>.Failure(
                                LoomErrors.InvalidArgument(
                                    $"Work plan step id '{id.Value}' does not exist in the current plan."));
                        }

                        if (!targetedIds.Add(id.Value))
                        {
                            return LoomResult<WorkPlanSnapshot>.Failure(
                                LoomErrors.InvalidArgument(
                                    $"Work plan step id '{id.Value}' appears more than once in the patch."));
                        }

                        var stepIndex = nextSteps.FindIndex(
                            step => string.Equals(
                                step.Id.Value,
                                id.Value,
                                StringComparison.Ordinal));

                        if (change.Operation == WorkPlanPatchOperation.Remove)
                        {
                            nextSteps.RemoveAt(stepIndex);
                            break;
                        }

                        var current = nextSteps[stepIndex];
                        nextSteps[stepIndex] = new WorkPlanStep(
                            current.Id,
                            change.Text ?? current.Text,
                            change.Status ?? current.Status);
                        break;
                    }

                    default:
                        throw new InvalidOperationException(
                            $"Unknown Work Plan patch operation '{change.Operation}'.");
                }
            }

            if (nextSteps.Count > WorkPlanCapability.MaxSteps)
            {
                return LoomResult<WorkPlanSnapshot>.Failure(
                    LoomErrors.InvalidArgument(
                        $"patched plan must contain no more than {WorkPlanCapability.MaxSteps} steps."));
            }

            _workPlan = CreateSnapshot(_workPlan.Revision + 1, nextSteps);
            return LoomResult<WorkPlanSnapshot>.Success(_workPlan);
        }
    }

    private LoomError? ValidateMutationPreconditionUnsafe(long expectedRevision)
    {
        if (_state != WorkSessionState.Active)
        {
            return UnavailableError();
        }

        if (expectedRevision != _workPlan.Revision)
        {
            return LoomErrors.Conflict(
                $"Work plan revision {expectedRevision} is stale; current revision is {_workPlan.Revision}.",
                new Dictionary<string, object?>
                {
                    ["currentRevision"] = _workPlan.Revision
                });
        }

        if (_workPlan.Revision == long.MaxValue)
        {
            return LoomErrors.Internal("Work plan revision limit was reached.");
        }

        return null;
    }

    private static WorkPlanStepId CreateUniqueStepId(
        IReadOnlySet<string> currentIds,
        IReadOnlySet<string> additionalIds)
    {
        WorkPlanStepId id;
        do
        {
            id = WorkPlanStepId.Create();
        }
        while (currentIds.Contains(id.Value) || additionalIds.Contains(id.Value));

        return id;
    }

    private void ClearWorkPlanUnsafe()
        => _workPlan = EmptyWorkPlan;

    private static WorkPlanSnapshot CreateSnapshot(
        long revision,
        IReadOnlyCollection<WorkPlanStep> steps)
    {
        var copy = steps.Count == 0
            ? Array.Empty<WorkPlanStep>()
            : steps.ToArray();

        return new WorkPlanSnapshot(revision, Array.AsReadOnly(copy));
    }
}
