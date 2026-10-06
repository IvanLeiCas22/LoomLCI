using LoomLCI.Core.AgentSupport;
using LoomLCI.Core.Invocations;
using LoomLCI.Core.Lifetime;
using LoomLCI.Core.Observability;
using LoomLCI.Core.Resources;
using LoomLCI.Core.Work;
using Microsoft.Extensions.Time.Testing;

namespace LoomLCI.Core.Tests;

public sealed class WorkPlanCapabilityTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NewSessionStartsWithEmptyRevisionZeroPlan()
    {
        await using var fixture = new Fixture();

        var result = await fixture.Capability.GetAsync(fixture.Work.Id);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(0, result.Value!.Revision);
        Assert.Empty(result.Value.Steps);
    }

    [Fact]
    public async Task FirstUpdateGeneratesIdsNormalizesTextAndIncrementsRevision()
    {
        await using var fixture = new Fixture();

        var result = await fixture.Capability.UpdateAsync(
            Request(
                fixture.Work.Id,
                0,
                NewStep("  Compile project  ", WorkPlanStepStatus.Active)));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, result.Value!.Revision);
        var step = Assert.Single(result.Value.Steps);
        Assert.StartsWith("step_", step.Id.Value, StringComparison.Ordinal);
        Assert.Equal("Compile project", step.Text);
        Assert.Equal(WorkPlanStepStatus.Active, step.Status);
    }

    [Fact]
    public async Task ExistingIdsSurviveUpdatesAndReordering()
    {
        await using var fixture = new Fixture();

        var first = await fixture.Capability.UpdateAsync(
            Request(
                fixture.Work.Id,
                0,
                NewStep("first"),
                NewStep("second")));
        Assert.True(first.IsSuccess, first.Error?.Message);

        var firstId = first.Value!.Steps[0].Id;
        var secondId = first.Value.Steps[1].Id;

        var second = await fixture.Capability.UpdateAsync(
            Request(
                fixture.Work.Id,
                1,
                ExistingStep(secondId, "second updated", WorkPlanStepStatus.Waiting),
                ExistingStep(firstId, "first", WorkPlanStepStatus.Completed)));

        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(2, second.Value!.Revision);
        Assert.Equal(secondId, second.Value.Steps[0].Id);
        Assert.Equal(firstId, second.Value.Steps[1].Id);
        Assert.Equal("second updated", second.Value.Steps[0].Text);
    }

    [Fact]
    public async Task MultipleActiveAndWaitingStepsAreAllowed()
    {
        await using var fixture = new Fixture();

        var result = await fixture.Capability.UpdateAsync(
            Request(
                fixture.Work.Id,
                0,
                NewStep("a", WorkPlanStepStatus.Active),
                NewStep("b", WorkPlanStepStatus.Active),
                NewStep("c", WorkPlanStepStatus.Waiting),
                NewStep("d", WorkPlanStepStatus.Waiting)));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value!.Steps.Count(step => step.Status == WorkPlanStepStatus.Active));
        Assert.Equal(2, result.Value.Steps.Count(step => step.Status == WorkPlanStepStatus.Waiting));
    }

    [Fact]
    public async Task EmptyUpdateClearsPlanAndStillIncrementsRevision()
    {
        await using var fixture = new Fixture();

        var first = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 0, NewStep("temporary")));
        Assert.True(first.IsSuccess, first.Error?.Message);

        var cleared = await fixture.Capability.UpdateAsync(
            new WorkPlanUpdateRequest(
                fixture.Work.Id,
                1,
                Array.Empty<WorkPlanStepInput>()));

        Assert.True(cleared.IsSuccess, cleared.Error?.Message);
        Assert.Equal(2, cleared.Value!.Revision);
        Assert.Empty(cleared.Value.Steps);
    }

    [Fact]
    public async Task StaleRevisionReturnsConflictWithCurrentRevisionBeforeIdValidation()
    {
        await using var fixture = new Fixture();

        var first = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 0, NewStep("current")));
        Assert.True(first.IsSuccess, first.Error?.Message);

        var stale = await fixture.Capability.UpdateAsync(
            Request(
                fixture.Work.Id,
                0,
                ExistingStep(new WorkPlanStepId("step_unknown"), "stale")));

        Assert.False(stale.IsSuccess);
        Assert.Equal("conflict", stale.Error?.Code);
        Assert.NotNull(stale.Error?.Details);
        Assert.Equal(1L, stale.Error!.Details!["currentRevision"]);
    }

    [Fact]
    public async Task ConcurrentUpdatesOnSameRevisionHaveExactlyOneWinner()
    {
        await using var fixture = new Fixture();

        var leftRequest = Request(fixture.Work.Id, 0, NewStep("left"));
        var rightRequest = Request(fixture.Work.Id, 0, NewStep("right"));

        var results = await Task.WhenAll(
            Task.Run(() => fixture.Capability.UpdateAsync(leftRequest)),
            Task.Run(() => fixture.Capability.UpdateAsync(rightRequest)));

        var success = Assert.Single(results, result => result.IsSuccess);
        var conflict = Assert.Single(results, result => !result.IsSuccess);

        Assert.Equal(1, success.Value!.Revision);
        Assert.Equal("conflict", conflict.Error?.Code);

        var current = await fixture.Capability.GetAsync(fixture.Work.Id);
        Assert.True(current.IsSuccess, current.Error?.Message);
        Assert.Equal(1, current.Value!.Revision);
        Assert.Single(current.Value.Steps);
    }

    [Fact]
    public async Task DuplicateExistingIdsAreRejected()
    {
        await using var fixture = new Fixture();

        var first = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 0, NewStep("one")));
        Assert.True(first.IsSuccess, first.Error?.Message);

        var id = first.Value!.Steps[0].Id;
        var duplicate = await fixture.Capability.UpdateAsync(
            Request(
                fixture.Work.Id,
                1,
                ExistingStep(id, "one"),
                ExistingStep(id, "duplicate")));

        Assert.False(duplicate.IsSuccess);
        Assert.Equal("invalid_argument", duplicate.Error?.Code);
    }

    [Fact]
    public async Task UnknownIdAtCurrentRevisionIsRejected()
    {
        await using var fixture = new Fixture();

        var result = await fixture.Capability.UpdateAsync(
            Request(
                fixture.Work.Id,
                0,
                ExistingStep(new WorkPlanStepId("step_unknown"), "unknown")));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_argument", result.Error?.Code);
    }

    [Fact]
    public async Task StepCountLimitAccepts32AndRejects33()
    {
        await using var fixture = new Fixture();

        var accepted = await fixture.Capability.UpdateAsync(
            new WorkPlanUpdateRequest(
                fixture.Work.Id,
                0,
                Enumerable.Range(0, 32)
                    .Select(index => NewStep($"step {index}"))
                    .ToArray()));
        Assert.True(accepted.IsSuccess, accepted.Error?.Message);

        var rejected = await fixture.Capability.UpdateAsync(
            new WorkPlanUpdateRequest(
                fixture.Work.Id,
                1,
                Enumerable.Range(0, 33)
                    .Select(index => NewStep($"step {index}"))
                    .ToArray()));

        Assert.False(rejected.IsSuccess);
        Assert.Equal("invalid_argument", rejected.Error?.Code);
    }

    [Fact]
    public async Task TextLimitCountsUnicodeScalarsInsteadOfUtf16CodeUnits()
    {
        await using var fixture = new Fixture();

        var exactly512 = string.Concat(Enumerable.Repeat("😀", 512));
        var accepted = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 0, NewStep(exactly512)));
        Assert.True(accepted.IsSuccess, accepted.Error?.Message);

        var rejected = await fixture.Capability.UpdateAsync(
            Request(
                fixture.Work.Id,
                1,
                NewStep(exactly512 + "x")));

        Assert.False(rejected.IsSuccess);
        Assert.Equal("invalid_argument", rejected.Error?.Code);
    }

    [Fact]
    public async Task MalformedUnicodeIsRejected()
    {
        await using var fixture = new Fixture();

        var result = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 0, NewStep("\uD800")));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_argument", result.Error?.Code);
        Assert.Contains("Unicode", result.Error?.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line1\nline2")]
    [InlineData("line1\rline2")]
    public async Task EmptyWhitespaceOrMultilineTextIsRejected(string text)
    {
        await using var fixture = new Fixture();

        var result = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 0, NewStep(text)));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_argument", result.Error?.Code);
    }

    [Fact]
    public async Task InvalidEnumValueIsRejected()
    {
        await using var fixture = new Fixture();

        var result = await fixture.Capability.UpdateAsync(
            Request(
                fixture.Work.Id,
                0,
                NewStep("invalid", (WorkPlanStepStatus)999)));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_argument", result.Error?.Code);
    }

    [Fact]
    public async Task NegativeRevisionAndEmptySuppliedIdAreRejected()
    {
        await using var fixture = new Fixture();

        var negative = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, -1, NewStep("negative")));
        Assert.False(negative.IsSuccess);
        Assert.Equal("invalid_argument", negative.Error?.Code);

        var emptyId = await fixture.Capability.UpdateAsync(
            Request(
                fixture.Work.Id,
                0,
                ExistingStep(new WorkPlanStepId(" "), "empty id")));
        Assert.False(emptyId.IsSuccess);
        Assert.Equal("invalid_argument", emptyId.Error?.Code);
    }

    [Fact]
    public async Task PublishedSnapshotCannotBeMutatedThroughItsStepsCollection()
    {
        await using var fixture = new Fixture();

        var result = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 0, NewStep("immutable")));
        Assert.True(result.IsSuccess, result.Error?.Message);

        var list = Assert.IsAssignableFrom<IList<WorkPlanStep>>(result.Value!.Steps);
        Assert.Throws<NotSupportedException>(
            () => list[0] = new WorkPlanStep(
                new WorkPlanStepId("step_replacement"),
                "replacement",
                WorkPlanStepStatus.Pending));

        var current = await fixture.Capability.GetAsync(fixture.Work.Id);
        Assert.Equal("immutable", current.Value!.Steps[0].Text);
    }

    [Fact]
    public async Task CloseMakesPlanUnavailableAndDoesNotCreateRegistryResources()
    {
        await using var fixture = new Fixture();

        var updated = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 0, NewStep("close me")));
        Assert.True(updated.IsSuccess, updated.Error?.Message);
        Assert.Equal(0, fixture.Resources.Count);

        var closed = await fixture.Sessions.CloseAsync(fixture.Work.Id);
        Assert.True(closed.IsSuccess, closed.Error?.Message);

        var get = await fixture.Capability.GetAsync(fixture.Work.Id);
        var update = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 1, NewStep("after close")));

        Assert.False(get.IsSuccess);
        Assert.Equal("resource_closed", get.Error?.Code);
        Assert.False(update.IsSuccess);
        Assert.Equal("resource_closed", update.Error?.Code);
        Assert.Equal(0, fixture.Resources.Count);
    }

    [Fact]
    public async Task ExpiryMakesPlanUnavailable()
    {
        await using var fixture = new Fixture();

        var updated = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 0, NewStep("expire me")));
        Assert.True(updated.IsSuccess, updated.Error?.Message);

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        var sweep = await fixture.Sessions.SweepExpiredAsync();

        Assert.Equal(1, sweep.ExpiredSessions);

        var get = await fixture.Capability.GetAsync(fixture.Work.Id);
        var update = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 1, NewStep("after expiry")));

        Assert.False(get.IsSuccess);
        Assert.Equal("resource_expired", get.Error?.Code);
        Assert.False(update.IsSuccess);
        Assert.Equal("resource_expired", update.Error?.Code);
    }

    [Fact]
    public async Task GetAndUpdateRefreshWorkSessionActivity()
    {
        await using var fixture = new Fixture();

        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        var get = await fixture.Capability.GetAsync(fixture.Work.Id);
        Assert.True(get.IsSuccess, get.Error?.Message);
        Assert.Equal(Start.AddMinutes(4), fixture.Work.LastActivityAt);

        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        var update = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 0, NewStep("touch")));
        Assert.True(update.IsSuccess, update.Error?.Message);
        Assert.Equal(Start.AddMinutes(8), fixture.Work.LastActivityAt);

        fixture.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(0, (await fixture.Sessions.SweepExpiredAsync()).ExpiredSessions);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, (await fixture.Sessions.SweepExpiredAsync()).ExpiredSessions);
    }

    [Fact]
    public async Task UpdatePublishesBoundedEventWithoutPlanTextOrIds()
    {
        await using var fixture = new Fixture();
        DrainEvents(fixture.Events);

        const string secretText = "do not copy this text into events";
        var result = await fixture.Capability.UpdateAsync(
            Request(
                fixture.Work.Id,
                0,
                NewStep(secretText, WorkPlanStepStatus.Active),
                NewStep("waiting", WorkPlanStepStatus.Waiting)));
        Assert.True(result.IsSuccess, result.Error?.Message);

        var events = DrainEvents(fixture.Events);
        var updated = Assert.Single(events, item => item.Kind == "WorkPlanUpdated");

        Assert.Equal("agent_support.work_plan", updated.Source);
        Assert.Equal(fixture.Work.Id, updated.WorkId);
        Assert.NotNull(updated.InvocationId);
        Assert.NotNull(updated.Payload);
        Assert.Equal(1L, updated.Payload!["revision"]);
        Assert.Equal(2, updated.Payload["stepCount"]);
        Assert.Equal(1, updated.Payload["activeCount"]);
        Assert.Equal(1, updated.Payload["waitingCount"]);
        Assert.DoesNotContain(
            updated.Payload.Values,
            value => value is string text &&
                     (text.Contains(secretText, StringComparison.Ordinal) ||
                      text.StartsWith("step_", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task StaticValidationFailureDoesNotTouchSessionOrEmitUpdateEvent()
    {
        await using var fixture = new Fixture();
        DrainEvents(fixture.Events);

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var result = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, -1, NewStep("invalid")));

        Assert.False(result.IsSuccess);
        Assert.Equal(Start, fixture.Work.LastActivityAt);
        Assert.DoesNotContain(
            DrainEvents(fixture.Events),
            item => item.Kind == "WorkPlanUpdated");
    }

    [Fact]
    public async Task PatchUpdatesOnlyRequestedFieldsAndPreservesUntouchedSteps()
    {
        await using var fixture = new Fixture();

        var initial = await fixture.Capability.UpdateAsync(
            Request(
                fixture.Work.Id,
                0,
                NewStep("first", WorkPlanStepStatus.Active),
                NewStep("second", WorkPlanStepStatus.Waiting)));
        Assert.True(initial.IsSuccess, initial.Error?.Message);

        var firstId = initial.Value!.Steps[0].Id;
        var secondId = initial.Value.Steps[1].Id;

        var patched = await fixture.Capability.PatchAsync(
            PatchRequest(
                fixture.Work.Id,
                1,
                UpdateChange(firstId, status: WorkPlanStepStatus.Completed)));

        Assert.True(patched.IsSuccess, patched.Error?.Message);
        Assert.Equal(2, patched.Value!.Revision);
        Assert.Equal(firstId, patched.Value.Steps[0].Id);
        Assert.Equal("first", patched.Value.Steps[0].Text);
        Assert.Equal(WorkPlanStepStatus.Completed, patched.Value.Steps[0].Status);
        Assert.Equal(secondId, patched.Value.Steps[1].Id);
        Assert.Equal("second", patched.Value.Steps[1].Text);
        Assert.Equal(WorkPlanStepStatus.Waiting, patched.Value.Steps[1].Status);
    }

    [Fact]
    public async Task PatchAddAppendsGeneratedIdAndDefaultsStatusToPending()
    {
        await using var fixture = new Fixture();

        var initial = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 0, NewStep("existing")));
        Assert.True(initial.IsSuccess, initial.Error?.Message);

        var existingId = initial.Value!.Steps[0].Id;
        var patched = await fixture.Capability.PatchAsync(
            PatchRequest(
                fixture.Work.Id,
                1,
                AddChange("  appended  ")));

        Assert.True(patched.IsSuccess, patched.Error?.Message);
        Assert.Equal(2, patched.Value!.Revision);
        Assert.Equal(2, patched.Value.Steps.Count);
        Assert.Equal(existingId, patched.Value.Steps[0].Id);

        var added = patched.Value.Steps[1];
        Assert.StartsWith("step_", added.Id.Value, StringComparison.Ordinal);
        Assert.NotEqual(existingId, added.Id);
        Assert.Equal("appended", added.Text);
        Assert.Equal(WorkPlanStepStatus.Pending, added.Status);
    }

    [Fact]
    public async Task PatchRemoveDeletesOnlyTargetedStep()
    {
        await using var fixture = new Fixture();

        var initial = await fixture.Capability.UpdateAsync(
            Request(
                fixture.Work.Id,
                0,
                NewStep("keep"),
                NewStep("remove"),
                NewStep("also keep")));
        Assert.True(initial.IsSuccess, initial.Error?.Message);

        var keepId = initial.Value!.Steps[0].Id;
        var removeId = initial.Value.Steps[1].Id;
        var lastId = initial.Value.Steps[2].Id;

        var patched = await fixture.Capability.PatchAsync(
            PatchRequest(
                fixture.Work.Id,
                1,
                RemoveChange(removeId)));

        Assert.True(patched.IsSuccess, patched.Error?.Message);
        Assert.Equal(new[] { keepId, lastId }, patched.Value!.Steps.Select(step => step.Id));
    }

    [Fact]
    public async Task PatchStaleRevisionWinsBeforeUnknownIdValidation()
    {
        await using var fixture = new Fixture();

        var initial = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 0, NewStep("current")));
        Assert.True(initial.IsSuccess, initial.Error?.Message);

        var stale = await fixture.Capability.PatchAsync(
            PatchRequest(
                fixture.Work.Id,
                0,
                RemoveChange(new WorkPlanStepId("step_unknown"))));

        Assert.False(stale.IsSuccess);
        Assert.Equal("conflict", stale.Error?.Code);
        Assert.Equal(1L, stale.Error!.Details!["currentRevision"]);
    }

    [Fact]
    public async Task ConcurrentPatchAndFullUpdateOnSameRevisionHaveExactlyOneWinner()
    {
        await using var fixture = new Fixture();

        var initial = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 0, NewStep("base")));
        Assert.True(initial.IsSuccess, initial.Error?.Message);
        var id = initial.Value!.Steps[0].Id;

        var patchRequest = PatchRequest(
            fixture.Work.Id,
            1,
            UpdateChange(id, status: WorkPlanStepStatus.Completed));
        var updateRequest = Request(
            fixture.Work.Id,
            1,
            ExistingStep(id, "replacement", WorkPlanStepStatus.Active));

        var results = await Task.WhenAll(
            Task.Run(async () => await fixture.Capability.PatchAsync(patchRequest)),
            Task.Run(async () => await fixture.Capability.UpdateAsync(updateRequest)));

        var success = Assert.Single(results, result => result.IsSuccess);
        var conflict = Assert.Single(results, result => !result.IsSuccess);

        Assert.Equal(2, success.Value!.Revision);
        Assert.Equal("conflict", conflict.Error?.Code);

        var current = await fixture.Capability.GetAsync(fixture.Work.Id);
        Assert.True(current.IsSuccess, current.Error?.Message);
        Assert.Equal(2, current.Value!.Revision);
        Assert.Single(current.Value.Steps);
        Assert.Equal(id, current.Value.Steps[0].Id);
    }

    [Fact]
    public async Task PatchRejectsDuplicateTargetsWithoutPublishingPartialChanges()
    {
        await using var fixture = new Fixture();

        var initial = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 0, NewStep("original")));
        Assert.True(initial.IsSuccess, initial.Error?.Message);
        var id = initial.Value!.Steps[0].Id;

        var rejected = await fixture.Capability.PatchAsync(
            PatchRequest(
                fixture.Work.Id,
                1,
                UpdateChange(id, text: "changed"),
                RemoveChange(id)));

        Assert.False(rejected.IsSuccess);
        Assert.Equal("invalid_argument", rejected.Error?.Code);

        var current = await fixture.Capability.GetAsync(fixture.Work.Id);
        Assert.Equal(1, current.Value!.Revision);
        var step = Assert.Single(current.Value.Steps);
        Assert.Equal(id, step.Id);
        Assert.Equal("original", step.Text);
    }

    [Fact]
    public async Task PatchRejectsFinalPlanAboveStepLimitAtomically()
    {
        await using var fixture = new Fixture();

        var initial = await fixture.Capability.UpdateAsync(
            new WorkPlanUpdateRequest(
                fixture.Work.Id,
                0,
                Enumerable.Range(0, WorkPlanCapability.MaxSteps)
                    .Select(index => NewStep($"step {index}"))
                    .ToArray()));
        Assert.True(initial.IsSuccess, initial.Error?.Message);

        var rejected = await fixture.Capability.PatchAsync(
            PatchRequest(
                fixture.Work.Id,
                1,
                AddChange("too many")));

        Assert.False(rejected.IsSuccess);
        Assert.Equal("invalid_argument", rejected.Error?.Code);

        var current = await fixture.Capability.GetAsync(fixture.Work.Id);
        Assert.Equal(1, current.Value!.Revision);
        Assert.Equal(WorkPlanCapability.MaxSteps, current.Value.Steps.Count);
    }

    [Fact]
    public async Task InvalidPatchShapesAreRejectedWithoutTouchingSession()
    {
        await using var fixture = new Fixture();
        DrainEvents(fixture.Events);

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));

        var empty = await fixture.Capability.PatchAsync(
            new WorkPlanPatchRequest(
                fixture.Work.Id,
                0,
                Array.Empty<WorkPlanPatchChange>()));
        var addWithId = await fixture.Capability.PatchAsync(
            PatchRequest(
                fixture.Work.Id,
                0,
                new WorkPlanPatchChange(
                    WorkPlanPatchOperation.Add,
                    new WorkPlanStepId("step_supplied"),
                    "invalid",
                    null)));
        var updateWithoutFields = await fixture.Capability.PatchAsync(
            PatchRequest(
                fixture.Work.Id,
                0,
                new WorkPlanPatchChange(
                    WorkPlanPatchOperation.Update,
                    new WorkPlanStepId("step_missing"),
                    null,
                    null)));
        var removeWithText = await fixture.Capability.PatchAsync(
            PatchRequest(
                fixture.Work.Id,
                0,
                new WorkPlanPatchChange(
                    WorkPlanPatchOperation.Remove,
                    new WorkPlanStepId("step_missing"),
                    "invalid",
                    null)));

        Assert.All(
            new[] { empty, addWithId, updateWithoutFields, removeWithText },
            result =>
            {
                Assert.False(result.IsSuccess);
                Assert.Equal("invalid_argument", result.Error?.Code);
            });
        Assert.Equal(Start, fixture.Work.LastActivityAt);
        Assert.DoesNotContain(
            DrainEvents(fixture.Events),
            item => item.Kind == "WorkPlanUpdated");
    }

    [Fact]
    public async Task PatchPublishesSameBoundedUpdateEventAndRefreshesActivity()
    {
        await using var fixture = new Fixture();

        var initial = await fixture.Capability.UpdateAsync(
            Request(fixture.Work.Id, 0, NewStep("secret text")));
        Assert.True(initial.IsSuccess, initial.Error?.Message);
        var id = initial.Value!.Steps[0].Id;
        DrainEvents(fixture.Events);

        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        var patched = await fixture.Capability.PatchAsync(
            PatchRequest(
                fixture.Work.Id,
                1,
                UpdateChange(id, status: WorkPlanStepStatus.Completed)));

        Assert.True(patched.IsSuccess, patched.Error?.Message);
        Assert.Equal(Start.AddMinutes(4), fixture.Work.LastActivityAt);

        var updated = Assert.Single(
            DrainEvents(fixture.Events),
            item => item.Kind == "WorkPlanUpdated");
        Assert.Equal(2L, updated.Payload!["revision"]);
        Assert.Equal(1, updated.Payload["stepCount"]);
        Assert.Equal(1, updated.Payload["completedCount"]);
        Assert.DoesNotContain(
            updated.Payload.Values,
            value => value is string text &&
                     (text.Contains("secret text", StringComparison.Ordinal) ||
                      text.StartsWith("step_", StringComparison.Ordinal)));
    }

    private static WorkPlanPatchRequest PatchRequest(
        WorkId workId,
        long revision,
        params WorkPlanPatchChange[] changes)
        => new(workId, revision, changes);

    private static WorkPlanPatchChange AddChange(
        string text,
        WorkPlanStepStatus? status = null)
        => new(WorkPlanPatchOperation.Add, null, text, status);

    private static WorkPlanPatchChange UpdateChange(
        WorkPlanStepId id,
        string? text = null,
        WorkPlanStepStatus? status = null)
        => new(WorkPlanPatchOperation.Update, id, text, status);

    private static WorkPlanPatchChange RemoveChange(WorkPlanStepId id)
        => new(WorkPlanPatchOperation.Remove, id, null, null);

    private static WorkPlanUpdateRequest Request(
        WorkId workId,
        long revision,
        params WorkPlanStepInput[] steps)
        => new(workId, revision, steps);

    private static WorkPlanStepInput NewStep(
        string text,
        WorkPlanStepStatus status = WorkPlanStepStatus.Pending)
        => new(null, text, status);

    private static WorkPlanStepInput ExistingStep(
        WorkPlanStepId id,
        string text,
        WorkPlanStepStatus status = WorkPlanStepStatus.Pending)
        => new(id, text, status);

    private static List<LoomEvent> DrainEvents(LoomEventBus events)
    {
        var result = new List<LoomEvent>();
        while (events.TryRead(out var loomEvent))
        {
            if (loomEvent is not null)
            {
                result.Add(loomEvent);
            }
        }

        return result;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture()
        {
            Clock = new FakeTimeProvider(Start);
            Events = new LoomEventBus(Clock);
            Resources = new ResourceRegistry(Clock);
            Sessions = new WorkSessionManager(
                Resources,
                Events,
                new LifetimeOptions(
                    workSessionIdleTimeout: TimeSpan.FromMinutes(10),
                    tombstoneRetention: TimeSpan.FromMinutes(20),
                    sweepInterval: TimeSpan.FromMinutes(1)),
                Clock);
            Invocations = new InvocationRunner(Events, Sessions, Clock);
            Capability = new WorkPlanCapability(Invocations, Events);

            var created = Sessions.Create(Path.GetTempPath(), "work-plan-test");
            Assert.True(created.IsSuccess, created.Error?.Message);
            Work = created.Value!;
        }

        public FakeTimeProvider Clock { get; }
        public LoomEventBus Events { get; }
        public ResourceRegistry Resources { get; }
        public WorkSessionManager Sessions { get; }
        public InvocationRunner Invocations { get; }
        public WorkPlanCapability Capability { get; }
        public WorkSession Work { get; }

        public async ValueTask DisposeAsync()
        {
            await Sessions.DisposeAsync();
            await Resources.DisposeAsync();
        }
    }
}
