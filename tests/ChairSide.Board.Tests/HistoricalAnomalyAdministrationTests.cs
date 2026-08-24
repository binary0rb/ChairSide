using System.Text;
using System.Text.Json;

using ChairSide.Board.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class HistoricalAnomalyAdministrationTests
{
    [Fact]
    public void Local_admin_flow_writes_one_event_per_revision_and_preserves_projection_evidence()
    {
        using var workspace = TestWorkspace.Create();
        var now = DateTimeOffset.Parse("2026-08-20T14:00:00Z");
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, Environments.Production, timeProvider: clock);
        var key = CreateAbortedSource(context, roomId: 1);
        var seeded = new HistoricalEncounterAdministrativeState(
            key,
            HistoricalAdministrativeDispositions.NoAnomaly,
            CurrentReason: null,
            ReasonSource: null,
            KnownReviewedAt: now.AddDays(-1),
            KnownReviewedActorClass: HistoricalAdministrativeActorClasses.LocalAdmin,
            OverrideDoctorId: "gibson-inactive",
            OverrideProcedureCode: "IMP",
            OverrideSedationState: SedationState.EligibleYes,
            OverrideIsAddOn: true,
            OverrideExpectedAllocationState: ExpectedAllocationState.ConfirmedAdjustedValue,
            OverrideExpectedAllocationSuggestedUnits: 2,
            OverrideExpectedAllocationConfirmedUnits: 3,
            AdministrativeRevision: 0);
        context.Repository.PersistHistoricalAdministrativeStateAndLedger(
            seeded,
            Event(key, HistoricalAdministrativeLedgerEventTypes.MetadataCorrected, now.AddMinutes(-1), 0));
        var service = new HistoricalAnomalyAdministrationService(context.Repository, clock);

        AssertSuccess(service.MarkForReview(
            key,
            0,
            HistoricalManualReviewReasons.IncorrectDoctor,
            new string('n', 500)), 1);
        clock.SetUtcNow(now.AddMinutes(1));
        AssertSuccess(service.RefineReason(
            key,
            1,
            HistoricalManualReviewReasons.UnexpectedLifecycle), 2);
        clock.SetUtcNow(now.AddMinutes(2));
        AssertSuccess(service.AddNote(key, 2, "Operational review note"), 3);
        clock.SetUtcNow(now.AddMinutes(3));
        AssertSuccess(service.ClearForReporting(key, 3), 4);
        clock.SetUtcNow(now.AddMinutes(4));
        AssertSuccess(service.ReopenReview(key, 4), 5);
        clock.SetUtcNow(now.AddMinutes(5));
        var confirmed = AssertSuccess(service.ConfirmException(key, 5), 6);

        Assert.Equal(HistoricalAdministrativeDispositions.ConfirmedException, confirmed.State!.Disposition);
        Assert.Equal(HistoricalManualReviewReasons.UnexpectedLifecycle, confirmed.State.CurrentReason);
        Assert.Equal(HistoricalAdministrativeReasonSources.LocalAdmin, confirmed.State.ReasonSource);
        Assert.Equal(seeded.KnownReviewedAt, confirmed.State.KnownReviewedAt);
        Assert.Equal(seeded.KnownReviewedActorClass, confirmed.State.KnownReviewedActorClass);
        Assert.Equal(seeded.OverrideDoctorId, confirmed.State.OverrideDoctorId);
        Assert.Equal(seeded.OverrideProcedureCode, confirmed.State.OverrideProcedureCode);
        Assert.Equal(seeded.OverrideSedationState, confirmed.State.OverrideSedationState);
        Assert.Equal(seeded.OverrideIsAddOn, confirmed.State.OverrideIsAddOn);
        Assert.Equal(seeded.OverrideExpectedAllocationState, confirmed.State.OverrideExpectedAllocationState);
        Assert.Equal(seeded.OverrideExpectedAllocationSuggestedUnits, confirmed.State.OverrideExpectedAllocationSuggestedUnits);
        Assert.Equal(seeded.OverrideExpectedAllocationConfirmedUnits, confirmed.State.OverrideExpectedAllocationConfirmedUnits);

        var ledger = context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 20).Rows;
        Assert.Equal(
            [
                HistoricalAdministrativeLedgerEventTypes.MetadataCorrected,
                HistoricalAdministrativeLedgerEventTypes.ManualFlag,
                HistoricalAdministrativeLedgerEventTypes.ReasonRefined,
                HistoricalAdministrativeLedgerEventTypes.NoteAdded,
                HistoricalAdministrativeLedgerEventTypes.ClearedForReporting,
                HistoricalAdministrativeLedgerEventTypes.ReviewReopened,
                HistoricalAdministrativeLedgerEventTypes.ConfirmedException
            ],
            ledger.Select(row => row.EventType));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], ledger.Select(row => row.AdministrativeRevision));
        Assert.Equal(HistoricalManualReviewReasons.IncorrectDoctor, ledger[1].StructuredReason);
        Assert.Equal(HistoricalManualReviewReasons.IncorrectDoctor, ledger[2].PreviousValue);
        Assert.Equal(HistoricalManualReviewReasons.UnexpectedLifecycle, ledger[2].NewValue);
        Assert.Equal("Operational review note", ledger[3].AdminNote);
        Assert.All(ledger.Skip(1), row => Assert.Equal(HistoricalAdministrativeActorClasses.LocalAdmin, row.ActorClass));
    }

    [Fact]
    public void Invalid_inputs_and_transitions_do_not_mutate_state_or_ledger()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateCompletedSource(context, roomId: 1);
        var service = new HistoricalAnomalyAdministrationService(context.Repository);

        Assert.Equal(HistoricalAdministrativeOperationOutcome.InvalidTransition, service.ClearForReporting(key, 0).Outcome);
        Assert.Equal(HistoricalAdministrativeOperationOutcome.InvalidTransition, service.ConfirmException(key, 0).Outcome);
        Assert.Equal(HistoricalAdministrativeOperationOutcome.InvalidTransition, service.ReopenReview(key, 0).Outcome);
        Assert.Equal(HistoricalAdministrativeOperationOutcome.InvalidTransition, service.AddNote(key, 0, "note").Outcome);
        Assert.Equal(HistoricalAdministrativeOperationOutcome.InvalidReason, service.MarkForReview(key, 0, "AfterHoursSweep").Outcome);
        Assert.Equal(HistoricalAdministrativeOperationOutcome.InvalidNote, service.MarkForReview(
            key, 0, HistoricalManualReviewReasons.OtherNeedsReview, new string('x', 501)).Outcome);
        Assert.Equal(HistoricalAdministrativeOperationOutcome.InvalidSource, service.MarkForReview(
            new HistoricalEncounterKey("completed", key.SourceRecordId),
            0,
            HistoricalManualReviewReasons.OtherNeedsReview).Outcome);
        Assert.Equal(HistoricalAdministrativeOperationOutcome.NotFound, service.MarkForReview(
            new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, 999_999),
            0,
            HistoricalManualReviewReasons.OtherNeedsReview).Outcome);

        Assert.Null(context.Repository.LoadHistoricalAdministrativeState(key));
        Assert.Empty(context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);

        AssertSuccess(service.MarkForReview(key, 0, HistoricalManualReviewReasons.OtherNeedsReview), 1);
        AssertSuccess(service.ClearForReporting(key, 1), 2);
        Assert.Equal(HistoricalAdministrativeOperationOutcome.InvalidTransition, service.ClearForReporting(key, 2).Outcome);
        AssertSuccess(service.MarkForReview(key, 2, HistoricalManualReviewReasons.IncorrectCaseDetails), 3);
        AssertSuccess(service.ConfirmException(key, 3), 4);
        Assert.Equal(HistoricalAdministrativeOperationOutcome.InvalidTransition, service.ConfirmException(key, 4).Outcome);
        Assert.Equal(4, context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 20).TotalMatchingCount);
    }

    [Fact]
    public async Task Concurrent_absent_projection_writers_with_expected_zero_yield_one_success_and_one_stale_write()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateAbortedSource(context, roomId: 1);
        var first = new HistoricalAnomalyAdministrationService(context.Repository);
        var second = new HistoricalAnomalyAdministrationService(context.Repository);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<HistoricalAdministrativeOperationResult> Start(HistoricalAnomalyAdministrationService service) => Task.Run(async () =>
        {
            await gate.Task;
            return service.MarkForReview(key, 0, HistoricalManualReviewReasons.UnexpectedLifecycle);
        });

        var writes = new[] { Start(first), Start(second) };
        gate.SetResult();
        var results = await Task.WhenAll(writes);

        Assert.Single(results, result => result.Outcome == HistoricalAdministrativeOperationOutcome.Success);
        Assert.Single(results, result => result.Outcome == HistoricalAdministrativeOperationOutcome.StaleWrite);
        Assert.Equal(1, context.Repository.LoadHistoricalAdministrativeState(key)!.AdministrativeRevision);
        Assert.Single(context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);
    }

    [Fact]
    public void Stale_and_failed_guarded_writes_leave_projection_and_ledger_unchanged()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateCompletedSource(context, roomId: 1);
        var service = new HistoricalAnomalyAdministrationService(context.Repository);
        AssertSuccess(service.MarkForReview(key, 0, HistoricalManualReviewReasons.IncorrectProcedure), 1);
        var before = context.Repository.LoadHistoricalAdministrativeState(key);
        var ledgerBefore = context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows;

        var stale = service.RefineReason(key, 0, HistoricalManualReviewReasons.UnexpectedLifecycle);
        Assert.Equal(HistoricalAdministrativeOperationOutcome.StaleWrite, stale.Outcome);
        Assert.Equal(1, stale.CurrentRevision);
        Assert.Equal(before, context.Repository.LoadHistoricalAdministrativeState(key));
        Assert.Equal(ledgerBefore, context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);

        var candidate = before! with
        {
            Disposition = HistoricalAdministrativeDispositions.ClearedForReporting,
            AdministrativeRevision = 2
        };
        var ledgerEvent = Event(
            key,
            HistoricalAdministrativeLedgerEventTypes.ClearedForReporting,
            DateTimeOffset.UtcNow,
            2) with
        {
            PreviousValue = HistoricalAdministrativeDispositions.NeedsReview,
            NewValue = HistoricalAdministrativeDispositions.ClearedForReporting
        };
        Assert.Throws<InvalidOperationException>(() =>
            context.Repository.PersistHistoricalAdministrativeStateAndLedgerGuarded(
                1,
                candidate,
                ledgerEvent,
                () => throw new InvalidOperationException("injected ledger boundary failure")));
        Assert.Equal(before, context.Repository.LoadHistoricalAdministrativeState(key));
        Assert.Equal(ledgerBefore, context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);
    }

    [Fact]
    public void Approved_system_finding_reopens_resolved_state_and_repeated_findings_append()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateAbortedSource(context, roomId: 1);
        var service = new HistoricalAnomalyAdministrationService(context.Repository);
        AssertSuccess(service.RecordSystemFinding(
            key, 0, HistoricalSystemFindingKind.AfterHoursSweep), 1);
        var originalFinding = Assert.Single(context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);
        AssertSuccess(service.RefineReason(
            key, 1, HistoricalManualReviewReasons.UnexpectedLifecycle), 2);
        Assert.Equal(originalFinding, context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows[0]);
        AssertSuccess(service.ClearForReporting(key, 2), 3);

        var reopened = AssertSuccess(service.RecordSystemFinding(
            key, 3, HistoricalSystemFindingKind.AfterHoursSweep), 4);
        Assert.Equal(HistoricalAdministrativeDispositions.NeedsReview, reopened.State!.Disposition);
        Assert.Equal(HistoricalSystemFindingReasons.AfterHoursSweep, reopened.State.CurrentReason);
        Assert.Equal(HistoricalAdministrativeReasonSources.System, reopened.State.ReasonSource);
        AssertSuccess(service.RecordSystemFinding(
            key, 4, HistoricalSystemFindingKind.ExceededMaxActiveDuration), 5);
        AssertSuccess(service.ConfirmException(key, 5), 6);
        AssertSuccess(service.RecordSystemFinding(
            key, 6, HistoricalSystemFindingKind.AfterHoursSweep), 7);

        var ledger = context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows;
        Assert.Equal(7, ledger.Count);
        Assert.Equal(HistoricalAdministrativeLedgerEventTypes.SystemFinding, ledger[3].EventType);
        Assert.Equal(HistoricalAdministrativeActorClasses.System, ledger[3].ActorClass);
        Assert.Equal(HistoricalAdministrativeReasonSources.System, ledger[3].ReasonSource);
        Assert.Equal(HistoricalAdministrativeDispositions.ClearedForReporting, ledger[3].PreviousValue);
        Assert.Equal(HistoricalAdministrativeDispositions.NeedsReview, ledger[3].NewValue);
        Assert.Equal(HistoricalSystemFindingReasons.ExceededMaxActiveDuration, ledger[4].StructuredReason);
        Assert.Equal(HistoricalAdministrativeDispositions.ConfirmedException, ledger[6].PreviousValue);

        Assert.Throws<ArgumentOutOfRangeException>(() => service.RecordSystemFinding(
            key, 7, (HistoricalSystemFindingKind)999));
        Assert.Equal(7, context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).TotalMatchingCount);
    }

    [Theory]
    [InlineData(HistoricalManualReviewReasons.IncorrectDoctor)]
    [InlineData(HistoricalManualReviewReasons.IncorrectProcedure)]
    [InlineData(HistoricalManualReviewReasons.IncorrectCaseDetails)]
    [InlineData(HistoricalManualReviewReasons.UnexpectedLifecycle)]
    [InlineData(HistoricalManualReviewReasons.OtherNeedsReview)]
    public void Every_manual_reason_is_accepted_and_pending_mark_can_be_reapplied(string reason)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateAbortedSource(context, roomId: 1);
        var service = new HistoricalAnomalyAdministrationService(context.Repository);

        AssertSuccess(service.MarkForReview(key, 0, reason), 1);
        var repeated = AssertSuccess(service.MarkForReview(key, 1, reason), 2);
        Assert.Equal(HistoricalAdministrativeDispositions.NeedsReview, repeated.State!.Disposition);
        Assert.Equal(2, context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).TotalMatchingCount);
    }

    private static HistoricalAdministrativeOperationResult AssertSuccess(
        HistoricalAdministrativeOperationResult result,
        int revision)
    {
        Assert.Equal(HistoricalAdministrativeOperationOutcome.Success, result.Outcome);
        Assert.Equal(revision, result.CurrentRevision);
        Assert.Equal(revision, result.State!.AdministrativeRevision);
        Assert.Equal(revision, result.LedgerEvent!.AdministrativeRevision);
        return result;
    }

    private static HistoricalEncounterKey CreateAbortedSource(StoreContext context, int roomId)
    {
        Assert.NotNull(context.Store.BeginPrestage(roomId, "otte", "CON"));
        Assert.NotNull(context.Store.CancelPrestage(roomId));
        var record = context.Repository.LoadAbortedAssignments().Single(row => row.RoomId == roomId);
        return new HistoricalEncounterKey(HistoricalEncounterSourceTypes.AbortedAssignment, record.AbortedAssignmentId);
    }

    private static HistoricalEncounterKey CreateCompletedSource(StoreContext context, int roomId)
    {
        Assert.NotNull(context.Store.BeginPrestage(roomId, "otte", "CON"));
        Assert.NotNull(context.Store.SeatRoomCanonical(roomId, null).Room);
        Assert.NotNull(context.Store.MarkReadyForDoctor(roomId));
        Assert.NotNull(context.Store.MarkDoctorArrived(roomId));
        var cycle = context.Repository.LoadCompletedCycles().Single(row => row.RoomId == roomId);
        return new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, cycle.CompletedCycleId);
    }

    private static HistoricalEncounterAdministrativeLedgerEvent Event(
        HistoricalEncounterKey key,
        string eventType,
        DateTimeOffset occurredAt,
        int revision) => new(
            LedgerId: 0,
            Key: key,
            EventType: eventType,
            OccurredAt: occurredAt,
            ActorClass: HistoricalAdministrativeActorClasses.LocalAdmin,
            ReasonSource: HistoricalAdministrativeReasonSources.LocalAdmin,
            StructuredReason: null,
            PreviousValue: null,
            NewValue: null,
            AdminNote: null,
            AdministrativeRevision: revision);
}
