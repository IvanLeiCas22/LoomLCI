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
            if (_state != WorkSessionState.Active)
            {
                return LoomResult<WorkPlanSnapshot>.Failure(UnavailableError());
            }

            if (expectedRevision != _workPlan.Revision)
            {
                return LoomResult<WorkPlanSnapshot>.Failure(
                    LoomErrors.Conflict(
                        $"Work plan revision {expectedRevision} is stale; current revision is {_workPlan.Revision}.",
                        new Dictionary<string, object?>
                        {
                            ["currentRevision"] = _workPlan.Revision
                        }));
            }

            if (_workPlan.Revision == long.MaxValue)
            {
                return LoomResult<WorkPlanSnapshot>.Failure(
                    LoomErrors.Internal("Work plan revision limit was reached."));
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
                    do
                    {
                        id = WorkPlanStepId.Create();
                    }
                    while (currentIds.Contains(id.Value) || requestIds.Contains(id.Value));
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
