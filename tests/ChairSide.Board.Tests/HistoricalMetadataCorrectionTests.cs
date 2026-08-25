using System.Text.Json;

using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Tests;

public sealed class HistoricalMetadataCorrectionTests
{
    [Fact]
    public void Procedure_and_sedation_cross_eligibility_boundaries_atomically_in_both_directions()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateCompletedSource(context, roomId: 1, procedureCode: "EXT");
        var service = CreateService(context);
        MarkForReview(context, key);

        var original = Assert.IsType<HistoricalEffectiveEncounter>(service.GetEffectiveEncounter(key));
        Assert.Equal(HistoricalMetadataEvidenceAuthority.AcceptedReadyHandoff, original.OriginalEvidenceAuthority);
        Assert.Equal("EXT", original.OriginalMetadata.ProcedureCode);
        Assert.Equal(SedationState.EligibleNo, original.OriginalMetadata.SedationState);
        var originalAllocation = original.OriginalMetadata.ExpectedAllocation;

        var toConsult = AssertSuccess(service.CorrectProcedureAndSedation(
            key,
            1,
            "CON",
            SedationState.UnavailableProcedureIneligible,
            "Explicit paired correction"), 2);

        Assert.Equal("CON", toConsult.Encounter!.EffectiveMetadata.ProcedureCode);
        Assert.Equal(
            SedationState.UnavailableProcedureIneligible,
            toConsult.Encounter.EffectiveMetadata.SedationState);
        Assert.Equal(originalAllocation, toConsult.Encounter.EffectiveMetadata.ExpectedAllocation);
        Assert.Equal(HistoricalAdministrativeDispositions.NeedsReview, toConsult.State!.Disposition);
        Assert.Equal(HistoricalManualReviewReasons.IncorrectProcedure, toConsult.State.CurrentReason);
        Assert.Equal(HistoricalAdministrativeReasonSources.LocalAdmin, toConsult.State.ReasonSource);
        Assert.Equal(HistoricalMetadataCorrectionFields.ProcedureAndSedation, toConsult.LedgerEvent!.StructuredReason);
        Assert.Equal(
            "{\"procedure\":\"EXT\",\"sedation\":\"EligibleNo\"}",
            toConsult.LedgerEvent.PreviousValue);
        Assert.Equal(
            "{\"procedure\":\"CON\",\"sedation\":\"UnavailableProcedureIneligible\"}",
            toConsult.LedgerEvent.NewValue);
        Assert.Equal("Explicit paired correction", toConsult.LedgerEvent.AdminNote);

        var toExtraction = AssertSuccess(service.CorrectProcedureAndSedation(
            key,
            2,
            "EXT",
            SedationState.EligibleNo), 3);
        Assert.Null(toExtraction.State!.OverrideProcedureCode);
        Assert.Null(toExtraction.State.OverrideSedationState);
        Assert.Equal("EXT", toExtraction.Encounter!.EffectiveMetadata.ProcedureCode);
        Assert.Equal(SedationState.EligibleNo, toExtraction.Encounter.EffectiveMetadata.SedationState);

        var ledger = context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows;
        Assert.Equal(3, ledger.Count);
        Assert.Equal(2, ledger.Count(row => row.EventType == HistoricalAdministrativeLedgerEventTypes.MetadataCorrected));
        Assert.DoesNotContain(ledger, row => row.EventType == HistoricalAdministrativeLedgerEventTypes.NoteAdded);
    }

    [Fact]
    public void Eligibility_boundary_intermediates_are_rejected_without_mutation()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateCompletedSource(context, roomId: 1, procedureCode: "EXT");
        var service = CreateService(context);
        MarkForReview(context, key);
        var before = context.Repository.LoadHistoricalAdministrativeState(key);
        var ledgerBefore = context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows.ToArray();

        Assert.Equal(
            HistoricalMetadataCorrectionOutcome.InvalidProcedure,
            service.CorrectProcedure(key, 1, "CON").Outcome);
        Assert.Equal(
            HistoricalMetadataCorrectionOutcome.InvalidSedation,
            service.CorrectSedation(key, 1, SedationState.UnavailableProcedureIneligible).Outcome);

        Assert.Equal(before, context.Repository.LoadHistoricalAdministrativeState(key));
        Assert.Equal(ledgerBefore, context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);
    }

    [Fact]
    public void Same_eligibility_procedure_correction_remains_one_field_and_inactive_procedure_is_governed()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var eligibleKey = CreateCompletedSource(context, roomId: 1, procedureCode: "EXT");
        var ineligibleKey = CreateCompletedSource(context, roomId: 2, procedureCode: "CON");
        var service = CreateService(context);
        MarkForReview(context, eligibleKey);
        MarkForReview(context, ineligibleKey);

        var eligible = AssertSuccess(service.CorrectProcedure(eligibleKey, 1, "IMP"), 2);
        Assert.Equal("IMP", eligible.State!.OverrideProcedureCode);
        Assert.Null(eligible.State.OverrideSedationState);
        Assert.Equal(HistoricalMetadataCorrectionFields.Procedure, eligible.LedgerEvent!.StructuredReason);
        Assert.Equal(
            HistoricalMetadataCorrectionOutcome.InvalidProcedure,
            service.CorrectProcedure(eligibleKey, 2, "implant").Outcome);
        Assert.Equal(
            HistoricalMetadataCorrectionOutcome.InvalidProcedure,
            service.CorrectProcedure(eligibleKey, 2, "IMP+SED").Outcome);
        Assert.Equal(
            HistoricalMetadataCorrectionOutcome.PairedCorrectionNotRequired,
            service.CorrectProcedureAndSedation(
                eligibleKey,
                2,
                "EXT",
                SedationState.EligibleNo).Outcome);

        var inactive = AssertSuccess(service.CorrectProcedure(ineligibleKey, 1, "SED"), 2);
        Assert.Equal("SED", inactive.Encounter!.EffectiveMetadata.ProcedureCode);
        Assert.Equal(SedationState.UnavailableProcedureIneligible, inactive.Encounter.EffectiveMetadata.SedationState);
        Assert.Equal(
            HistoricalMetadataCorrectionOutcome.InvalidProcedure,
            service.CorrectProcedure(ineligibleKey, 2, "NOT-GOVERNED").Outcome);
    }

    [Fact]
    public void Doctor_corrections_accept_active_and_inactive_roster_entries_and_normalize_back_to_original()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateCompletedSource(context, roomId: 1, procedureCode: "CON");
        var service = CreateService(context);
        MarkForReview(context, key);

        AssertSuccess(service.CorrectDoctor(key, 1, "pledger"), 2);
        var inactive = AssertSuccess(service.CorrectDoctor(key, 2, "historical-doctor"), 3);
        Assert.Equal("historical-doctor", inactive.State!.OverrideDoctorId);
        Assert.Equal("pledger", inactive.LedgerEvent!.PreviousValue);
        Assert.Equal("historical-doctor", inactive.LedgerEvent.NewValue);

        var restored = AssertSuccess(service.CorrectDoctor(key, 3, "otte"), 4);
        Assert.Null(restored.State!.OverrideDoctorId);
        Assert.Equal("otte", restored.Encounter!.EffectiveMetadata.DoctorId);
        Assert.Equal(
            HistoricalMetadataCorrectionOutcome.InvalidDoctor,
            service.CorrectDoctor(key, 4, "arbitrary-doctor").Outcome);
    }

    [Fact]
    public void Sedation_and_add_on_corrections_are_explicit_independent_overlays()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateCompletedSource(context, roomId: 1, procedureCode: "EXT");
        var service = CreateService(context);
        MarkForReview(context, key);

        var sedation = AssertSuccess(service.CorrectSedation(key, 1, SedationState.EligibleYes), 2);
        Assert.Equal(SedationState.EligibleYes, sedation.State!.OverrideSedationState);
        Assert.Equal(HistoricalMetadataCorrectionFields.Sedation, sedation.LedgerEvent!.StructuredReason);
        Assert.Equal(nameof(SedationState.EligibleNo), sedation.LedgerEvent.PreviousValue);
        Assert.Equal(nameof(SedationState.EligibleYes), sedation.LedgerEvent.NewValue);

        var restoredSedation = AssertSuccess(service.CorrectSedation(key, 2, SedationState.EligibleNo), 3);
        Assert.Null(restoredSedation.State!.OverrideSedationState);
        Assert.Equal(HistoricalMetadataCorrectionFields.Sedation, restoredSedation.LedgerEvent!.StructuredReason);
        Assert.Equal(nameof(SedationState.EligibleYes), restoredSedation.LedgerEvent.PreviousValue);
        Assert.Equal(nameof(SedationState.EligibleNo), restoredSedation.LedgerEvent.NewValue);
        Assert.Equal(
            HistoricalMetadataCorrectionOutcome.InvalidSedation,
            service.CorrectSedation(key, 3, SedationState.EligibleUnresolved).Outcome);
        var stateBeforeAddOn = context.Repository.LoadHistoricalAdministrativeState(key)!;

        var addOn = AssertSuccess(service.CorrectAddOn(key, 3, true), 4);
        Assert.True(addOn.State!.OverrideIsAddOn);
        Assert.Equal(stateBeforeAddOn.OverrideDoctorId, addOn.State.OverrideDoctorId);
        Assert.Equal(stateBeforeAddOn.OverrideProcedureCode, addOn.State.OverrideProcedureCode);
        Assert.Equal(stateBeforeAddOn.OverrideSedationState, addOn.State.OverrideSedationState);
        Assert.Equal(stateBeforeAddOn.OverrideExpectedAllocationState, addOn.State.OverrideExpectedAllocationState);
        Assert.Equal(HistoricalMetadataCorrectionFields.AddOn, addOn.LedgerEvent!.StructuredReason);
        Assert.Equal("false", addOn.LedgerEvent.PreviousValue);
        Assert.Equal("true", addOn.LedgerEvent.NewValue);

        var sedationLedger = context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows
            .Where(row => row.EventType == HistoricalAdministrativeLedgerEventTypes.MetadataCorrected
                && row.StructuredReason == HistoricalMetadataCorrectionFields.Sedation)
            .ToArray();
        Assert.Collection(
            sedationLedger,
            first =>
            {
                Assert.Equal(nameof(SedationState.EligibleNo), first.PreviousValue);
                Assert.Equal(nameof(SedationState.EligibleYes), first.NewValue);
                Assert.Equal(2, first.AdministrativeRevision);
            },
            second =>
            {
                Assert.Equal(nameof(SedationState.EligibleYes), second.PreviousValue);
                Assert.Equal(nameof(SedationState.EligibleNo), second.NewValue);
                Assert.Equal(3, second.AdministrativeRevision);
            });
    }

    [Fact]
    public void Expected_allocation_is_explicit_repeated_and_independent_of_current_roster_default()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateCompletedSource(context, roomId: 1, procedureCode: "EXT");
        var procedures = ProcedureRosterOptions.DefaultProcedures();
        procedures.Single(row => row.Code == "EXT").DefaultExpectedUnits = 24;
        var service = CreateService(context, procedureOptions: new ProcedureRosterOptions { Procedures = procedures });
        MarkForReview(context, key);

        var first = AssertSuccess(service.CorrectExpectedAllocation(
            key,
            1,
            ExpectedAllocationState.ConfirmedAdjustedValue,
            suggestedUnits: 3,
            confirmedUnits: 4), 2);
        Assert.Equal(4, first.Encounter!.EffectiveMetadata.ExpectedAllocation!.ConfirmedValue);
        Assert.Equal(3, first.Encounter.EffectiveMetadata.ExpectedAllocation.SuggestedValue);
        Assert.Equal(
            "{\"state\":\"ConfirmedSuggestedValue\",\"suggestedUnits\":3,\"confirmedUnits\":3}",
            first.LedgerEvent!.PreviousValue);

        var second = AssertSuccess(service.CorrectExpectedAllocation(
            key,
            2,
            ExpectedAllocationState.ConfirmedAdjustedValue,
            suggestedUnits: null,
            confirmedUnits: 5), 3);
        Assert.Equal(
            "{\"state\":\"ConfirmedAdjustedValue\",\"suggestedUnits\":3,\"confirmedUnits\":4}",
            second.LedgerEvent!.PreviousValue);
        Assert.Equal(5, second.State!.OverrideExpectedAllocationConfirmedUnits);

        var restored = AssertSuccess(service.CorrectExpectedAllocation(
            key,
            3,
            ExpectedAllocationState.ConfirmedSuggestedValue,
            suggestedUnits: 3,
            confirmedUnits: 3), 4);
        Assert.Null(restored.State!.OverrideExpectedAllocationState);
        Assert.Null(restored.State.OverrideExpectedAllocationSuggestedUnits);
        Assert.Null(restored.State.OverrideExpectedAllocationConfirmedUnits);
        Assert.Equal(
            HistoricalMetadataCorrectionOutcome.InvalidExpectedAllocation,
            service.CorrectExpectedAllocation(
                key,
                4,
                ExpectedAllocationState.ConfirmedSuggestedValue,
                suggestedUnits: 3,
                confirmedUnits: 4).Outcome);
    }

    [Fact]
    public void Correction_workflow_gate_requires_needs_review_and_preserves_disposition_reason_and_provenance()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateCompletedSource(context, roomId: 1, procedureCode: "CON");
        var correction = CreateService(context);
        var administration = new HistoricalAnomalyAdministrationService(context.Repository);

        Assert.Equal(
            HistoricalMetadataCorrectionOutcome.InvalidSource,
            correction.CorrectDoctor(
                new HistoricalEncounterKey("completed", key.SourceRecordId),
                0,
                "pledger").Outcome);
        Assert.Equal(
            HistoricalMetadataCorrectionOutcome.ReviewNotPending,
            correction.CorrectDoctor(key, 0, "pledger").Outcome);
        var marked = AssertSuccess(administration.MarkForReview(
            key,
            0,
            HistoricalManualReviewReasons.IncorrectDoctor), 1);
        var corrected = AssertSuccess(correction.CorrectDoctor(key, 1, "pledger"), 2);
        Assert.Equal(HistoricalAdministrativeDispositions.NeedsReview, corrected.State!.Disposition);
        Assert.Equal(marked.State!.CurrentReason, corrected.State.CurrentReason);
        Assert.Equal(marked.State.ReasonSource, corrected.State.ReasonSource);
        Assert.Equal(marked.State.KnownReviewedAt, corrected.State.KnownReviewedAt);
        Assert.Equal(marked.State.KnownReviewedActorClass, corrected.State.KnownReviewedActorClass);

        AssertSuccess(administration.ClearForReporting(key, 2), 3);
        Assert.Equal(
            HistoricalMetadataCorrectionOutcome.ReviewNotPending,
            correction.CorrectDoctor(key, 3, "gibson").Outcome);
        AssertSuccess(administration.ReopenReview(key, 3), 4);
        AssertSuccess(correction.CorrectDoctor(key, 4, "gibson"), 5);
        AssertSuccess(administration.ConfirmException(key, 5), 6);
        Assert.Equal(
            HistoricalMetadataCorrectionOutcome.ReviewNotPending,
            correction.CorrectDoctor(key, 6, "schroeder").Outcome);
    }

    [Fact]
    public void Stale_and_failed_paired_corrections_leave_projection_and_ledger_unchanged()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateCompletedSource(context, roomId: 1, procedureCode: "EXT");
        var service = CreateService(context);
        MarkForReview(context, key);
        var before = context.Repository.LoadHistoricalAdministrativeState(key);
        var ledgerBefore = context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows.ToArray();

        var stale = service.CorrectProcedureAndSedation(
            key,
            0,
            "CON",
            SedationState.UnavailableProcedureIneligible);
        Assert.Equal(HistoricalMetadataCorrectionOutcome.StaleWrite, stale.Outcome);
        Assert.Equal(before, context.Repository.LoadHistoricalAdministrativeState(key));
        Assert.Equal(ledgerBefore, context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);

        InstallMetadataLedgerFailureTrigger(context.DatabasePath);
        Assert.Throws<SqliteException>(() => service.CorrectProcedureAndSedation(
            key,
            1,
            "CON",
            SedationState.UnavailableProcedureIneligible));
        Assert.Equal(before, context.Repository.LoadHistoricalAdministrativeState(key));
        Assert.Equal(ledgerBefore, context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);
    }

    [Fact]
    public void Paired_correction_preserves_disposition_reason_provenance_and_every_unrelated_override()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateCompletedSource(context, roomId: 1, procedureCode: "EXT");
        var reviewedAt = DateTimeOffset.Parse("2026-08-24T18:00:00Z");
        var seeded = new HistoricalEncounterAdministrativeState(
            key,
            HistoricalAdministrativeDispositions.NeedsReview,
            HistoricalManualReviewReasons.IncorrectProcedure,
            HistoricalAdministrativeReasonSources.LocalAdmin,
            reviewedAt,
            HistoricalAdministrativeActorClasses.LocalAdmin,
            OverrideDoctorId: "pledger",
            OverrideProcedureCode: null,
            OverrideSedationState: null,
            OverrideIsAddOn: true,
            OverrideExpectedAllocationState: ExpectedAllocationState.ConfirmedAdjustedValue,
            OverrideExpectedAllocationSuggestedUnits: 3,
            OverrideExpectedAllocationConfirmedUnits: 4,
            AdministrativeRevision: 0);
        context.Repository.PersistHistoricalAdministrativeStateAndLedger(
            seeded,
            new HistoricalEncounterAdministrativeLedgerEvent(
                LedgerId: 0,
                Key: key,
                EventType: HistoricalAdministrativeLedgerEventTypes.ManualFlag,
                OccurredAt: reviewedAt,
                ActorClass: HistoricalAdministrativeActorClasses.LocalAdmin,
                ReasonSource: HistoricalAdministrativeReasonSources.LocalAdmin,
                StructuredReason: HistoricalManualReviewReasons.IncorrectProcedure,
                PreviousValue: HistoricalAdministrativeDispositions.NoAnomaly,
                NewValue: HistoricalAdministrativeDispositions.NeedsReview,
                AdminNote: null,
                AdministrativeRevision: 0));
        var service = CreateService(context);

        var result = AssertSuccess(service.CorrectProcedureAndSedation(
            key,
            0,
            "CON",
            SedationState.UnavailableProcedureIneligible), 1);

        Assert.Equal(seeded.Disposition, result.State!.Disposition);
        Assert.Equal(seeded.CurrentReason, result.State.CurrentReason);
        Assert.Equal(seeded.ReasonSource, result.State.ReasonSource);
        Assert.Equal(seeded.KnownReviewedAt, result.State.KnownReviewedAt);
        Assert.Equal(seeded.KnownReviewedActorClass, result.State.KnownReviewedActorClass);
        Assert.Equal(seeded.OverrideDoctorId, result.State.OverrideDoctorId);
        Assert.Equal(seeded.OverrideIsAddOn, result.State.OverrideIsAddOn);
        Assert.Equal(seeded.OverrideExpectedAllocationState, result.State.OverrideExpectedAllocationState);
        Assert.Equal(seeded.OverrideExpectedAllocationSuggestedUnits, result.State.OverrideExpectedAllocationSuggestedUnits);
        Assert.Equal(seeded.OverrideExpectedAllocationConfirmedUnits, result.State.OverrideExpectedAllocationConfirmedUnits);
        Assert.Equal(4, result.Encounter!.EffectiveMetadata.ExpectedAllocation!.ConfirmedValue);
        Assert.Equal(2, context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows.Count);
    }

    [Fact]
    public void Corrections_never_mutate_source_ready_or_lifecycle_evidence()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateCompletedSource(context, roomId: 1, procedureCode: "EXT");
        var service = CreateService(context);
        MarkForReview(context, key);
        var sourceBefore = context.Repository.LoadHistoricalEncounter(key)!;
        var handoffId = sourceBefore.CompletedCycle!.AcceptedReadyHandoffId!;
        var handoffBefore = context.Repository.LoadReadyHandoff(handoffId)!;
        var sourceJson = JsonSerializer.Serialize(sourceBefore.CompletedCycle);
        var handoffJson = JsonSerializer.Serialize(handoffBefore);

        AssertSuccess(service.CorrectDoctor(key, 1, "pledger"), 2);
        AssertSuccess(service.CorrectProcedure(key, 2, "IMP"), 3);
        AssertSuccess(service.CorrectSedation(key, 3, SedationState.EligibleYes), 4);
        AssertSuccess(service.CorrectAddOn(key, 4, true), 5);
        AssertSuccess(service.CorrectExpectedAllocation(
            key,
            5,
            ExpectedAllocationState.ConfirmedAdjustedValue,
            suggestedUnits: 3,
            confirmedUnits: 4), 6);

        var sourceAfter = context.Repository.LoadHistoricalEncounter(key)!;
        var handoffAfter = context.Repository.LoadReadyHandoff(handoffId)!;
        Assert.Equal(sourceJson, JsonSerializer.Serialize(sourceAfter.CompletedCycle));
        Assert.Equal(handoffJson, JsonSerializer.Serialize(handoffAfter));
        Assert.Equal(handoffBefore.ReadyAt, handoffAfter.ReadyAt);
        Assert.Equal(handoffBefore.AcceptedAt, handoffAfter.AcceptedAt);
        Assert.Equal(sourceBefore.CompletedCycle.SeatedAt, sourceAfter.CompletedCycle!.SeatedAt);
        Assert.Equal(sourceBefore.CompletedCycle.DoctorArrivedAt, sourceAfter.CompletedCycle.DoctorArrivedAt);
        Assert.Equal(3, handoffAfter.Assignment.ExpectedAllocationConfirmedUnits);
    }

    [Fact]
    public void Aborted_sources_project_truthful_evidence_and_reject_fields_never_recorded()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        Assert.NotNull(context.Store.BeginPrestageCanonical(1).Room);
        Assert.NotNull(context.Store.CancelPrestage(1));
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        var key = new HistoricalEncounterKey(
            HistoricalEncounterSourceTypes.AbortedAssignment,
            aborted.AbortedAssignmentId);
        var service = CreateService(context);
        MarkForReview(context, key);

        var projection = Assert.IsType<HistoricalEffectiveEncounter>(service.GetEffectiveEncounter(key));
        Assert.Equal(HistoricalMetadataEvidenceAuthority.AbortedAssignment, projection.OriginalEvidenceAuthority);
        Assert.Null(projection.OriginalMetadata.DoctorId);
        Assert.Null(projection.OriginalMetadata.ProcedureCode);
        Assert.False(projection.CorrectionSupport.Doctor);
        Assert.False(projection.CorrectionSupport.Procedure);
        Assert.True(projection.CorrectionSupport.AddOn);
        Assert.False(projection.CorrectionSupport.ExpectedAllocation);
        Assert.Null(projection.Source.AbortedAssignment!.SeatedAt);
        Assert.Null(projection.Source.AbortedAssignment.ReadyForDoctorAt);
        Assert.Equal(
            HistoricalMetadataCorrectionOutcome.UnsupportedCorrection,
            service.CorrectDoctor(key, 1, "pledger").Outcome);
        Assert.Equal(
            HistoricalMetadataCorrectionOutcome.UnsupportedCorrection,
            service.CorrectExpectedAllocation(
                key,
                1,
                ExpectedAllocationState.ConfirmedSuggestedValue,
                1,
                1).Outcome);

        var addOn = AssertSuccess(service.CorrectAddOn(key, 1, true), 2);
        Assert.True(addOn.Encounter!.EffectiveMetadata.IsAddOn);
        Assert.Null(addOn.Encounter.Source.AbortedAssignment!.SeatedAt);
        Assert.Null(addOn.Encounter.Source.AbortedAssignment.ReadyForDoctorAt);
    }

    [Fact]
    public void Terminal_ready_aborts_use_ready_evidence_and_typed_equal_ids_remain_isolated()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var completedKey = CreateCompletedSource(context, roomId: 1, procedureCode: "CON");
        Assert.NotNull(context.Store.BeginPrestage(2, "otte", "EXT"));
        Assert.NotNull(context.Store.SeatRoomCanonical(2, null).Room);
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.CancelSeating(2));
        var aborted = context.Repository.LoadAbortedAssignments().Single(row => row.RoomId == 2);
        var abortedKey = new HistoricalEncounterKey(
            HistoricalEncounterSourceTypes.AbortedAssignment,
            aborted.AbortedAssignmentId);
        Assert.Equal(completedKey.SourceRecordId, abortedKey.SourceRecordId);
        var service = CreateService(context);
        MarkForReview(context, completedKey);
        MarkForReview(context, abortedKey);

        var abortedProjection = service.GetEffectiveEncounter(abortedKey)!;
        Assert.Equal(HistoricalMetadataEvidenceAuthority.TerminalReadyHandoff, abortedProjection.OriginalEvidenceAuthority);
        Assert.Equal(aborted.TerminalReadyHandoffId, abortedProjection.OriginalReadyHandoffId);
        AssertSuccess(service.CorrectProcedureAndSedation(
            abortedKey,
            1,
            "CON",
            SedationState.UnavailableProcedureIneligible), 2);
        AssertSuccess(service.CorrectDoctor(completedKey, 1, "pledger"), 2);
        var abortedState = context.Repository.LoadHistoricalAdministrativeState(abortedKey)!;
        Assert.Null(abortedState.OverrideDoctorId);
        Assert.Equal("CON", abortedState.OverrideProcedureCode);
        Assert.Equal(SedationState.UnavailableProcedureIneligible, abortedState.OverrideSedationState);
    }

    [Fact]
    public void Legacy_completed_projection_exposes_missing_ready_and_sedation_truth_without_fabrication()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var seatedAt = DateTimeOffset.Parse("2026-08-24T14:00:00Z");
        var sourceId = InsertLegacyCompletedSource(context.DatabasePath, seatedAt);
        var key = new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, sourceId);
        var service = CreateService(context);
        MarkForReview(context, key);

        var projection = service.GetEffectiveEncounter(key)!;
        Assert.Equal(HistoricalMetadataEvidenceAuthority.CompletedCycle, projection.OriginalEvidenceAuthority);
        Assert.Null(projection.OriginalReadyHandoffId);
        Assert.Equal("otte", projection.OriginalMetadata.DoctorId);
        Assert.Equal("CON", projection.OriginalMetadata.ProcedureCode);
        Assert.Null(projection.OriginalMetadata.SedationState);
        Assert.Equal(seatedAt, projection.Source.CompletedCycle!.SeatedAt);
        Assert.Null(projection.Source.CompletedCycle.ReadyForDoctorAt);
        Assert.Null(projection.Source.CompletedCycle.DoctorArrivedAt);
        Assert.False(projection.CorrectionSupport.Procedure);
        Assert.False(projection.CorrectionSupport.Sedation);
        Assert.True(projection.CorrectionSupport.Doctor);

        Assert.Equal(
            HistoricalMetadataCorrectionOutcome.UnsupportedCorrection,
            service.CorrectProcedure(key, 1, "POST").Outcome);
        var doctor = AssertSuccess(service.CorrectDoctor(key, 1, "historical-doctor"), 2);
        Assert.Equal("historical-doctor", doctor.Encounter!.EffectiveMetadata.DoctorId);
        Assert.Null(doctor.Encounter.OriginalMetadata.SedationState);
        Assert.Null(doctor.Encounter.Source.CompletedCycle!.ReadyForDoctorAt);
    }

    private static HistoricalMetadataCorrectionService CreateService(
        StoreContext context,
        DoctorRosterOptions? doctorOptions = null,
        ProcedureRosterOptions? procedureOptions = null)
    {
        doctorOptions ??= new DoctorRosterOptions { Doctors = DoctorRosterOptions.DefaultDoctors() };
        if (!doctorOptions.Doctors.Any(row => row.Id == "historical-doctor"))
        {
            doctorOptions.Doctors.Add(new DoctorRosterItem
            {
                Id = "historical-doctor",
                DisplayName = "Dr. Historical",
                ShortName = "Historical",
                Color = "#64748b",
                Active = false
            });
        }
        procedureOptions ??= new ProcedureRosterOptions { Procedures = ProcedureRosterOptions.DefaultProcedures() };
        return new HistoricalMetadataCorrectionService(
            context.Repository,
            Microsoft.Extensions.Options.Options.Create(doctorOptions),
            Microsoft.Extensions.Options.Options.Create(procedureOptions));
    }

    private static HistoricalEncounterKey CreateCompletedSource(
        StoreContext context,
        int roomId,
        string procedureCode)
    {
        Assert.NotNull(context.Store.BeginPrestage(roomId, "otte", procedureCode));
        Assert.NotNull(context.Store.SeatRoomCanonical(roomId, null).Room);
        Assert.NotNull(context.Store.MarkReadyForDoctor(roomId));
        Assert.NotNull(context.Store.MarkDoctorArrived(roomId));
        var cycle = context.Repository.LoadCompletedCycles().Single(row => row.RoomId == roomId);
        return new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, cycle.CompletedCycleId);
    }

    private static void MarkForReview(StoreContext context, HistoricalEncounterKey key)
    {
        var administration = new HistoricalAnomalyAdministrationService(context.Repository);
        AssertSuccess(administration.MarkForReview(
            key,
            0,
            HistoricalManualReviewReasons.IncorrectProcedure), 1);
    }

    private static HistoricalMetadataCorrectionResult AssertSuccess(
        HistoricalMetadataCorrectionResult result,
        int revision)
    {
        Assert.Equal(HistoricalMetadataCorrectionOutcome.Success, result.Outcome);
        Assert.Equal(revision, result.CurrentRevision);
        Assert.Equal(revision, result.State!.AdministrativeRevision);
        Assert.Equal(revision, result.LedgerEvent!.AdministrativeRevision);
        return result;
    }

    private static HistoricalAdministrativeOperationResult AssertSuccess(
        HistoricalAdministrativeOperationResult result,
        int revision)
    {
        Assert.Equal(HistoricalAdministrativeOperationOutcome.Success, result.Outcome);
        Assert.Equal(revision, result.CurrentRevision);
        return result;
    }

    private static void InstallMetadataLedgerFailureTrigger(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER fail_metadata_correction_ledger
            BEFORE INSERT ON historical_encounter_ledger
            WHEN NEW.event_type = 'MetadataCorrected'
            BEGIN
                SELECT RAISE(ABORT, 'injected metadata correction ledger failure');
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static long InsertLegacyCompletedSource(string databasePath, DateTimeOffset seatedAt)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO completed_room_cycles (
                room_id,
                assigned_doctor_id,
                assigned_doctor_display_name,
                procedure_code,
                procedure_category,
                seated_at,
                seated_to_doctor_seconds,
                final_wait_state,
                original_default_expected_units,
                expected_allocation_units,
                expected_allocation_minutes,
                allocation_adjusted_from_default,
                created_at,
                updated_at
            )
            VALUES (
                9,
                'otte',
                'Dr. Otte',
                'CON',
                'Consult',
                $seatedAt,
                0,
                'Available',
                1,
                1,
                10,
                0,
                $seatedAt,
                $seatedAt
            );
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$seatedAt", seatedAt.ToUniversalTime().ToString("O"));
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
