using System.Text.Json;

using ChairSide.Board.Services;

namespace ChairSide.Board.Tests;

public sealed class PrestagingAssignmentContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Sedation_without_procedure_is_unavailable()
    {
        var sedation = SedationContract.FromProcedure(
            hasProcedure: false,
            procedureSedationEligible: false,
            explicitDecision: null);

        Assert.Equal(SedationState.UnavailableNoProcedure, sedation.State);
        Assert.Null(sedation.ExplicitDecision);
        Assert.False(sedation.SatisfiesAssignmentCompleteness);
    }

    [Fact]
    public void Sedation_for_ineligible_procedure_is_unavailable_but_complete_for_sedation()
    {
        var sedation = SedationContract.FromProcedure(
            hasProcedure: true,
            procedureSedationEligible: false,
            explicitDecision: null);

        Assert.Equal(SedationState.UnavailableProcedureIneligible, sedation.State);
        Assert.Null(sedation.ExplicitDecision);
        Assert.True(sedation.SatisfiesAssignmentCompleteness);
    }

    [Fact]
    public void Eligible_sedation_without_explicit_choice_is_unresolved()
    {
        var sedation = SedationContract.FromProcedure(
            hasProcedure: true,
            procedureSedationEligible: true,
            explicitDecision: null);

        Assert.Equal(SedationState.EligibleUnresolved, sedation.State);
        Assert.Null(sedation.ExplicitDecision);
        Assert.False(sedation.SatisfiesAssignmentCompleteness);
    }

    [Fact]
    public void Eligible_sedation_yes_is_explicit_and_complete_for_sedation()
    {
        var sedation = SedationContract.FromProcedure(
            hasProcedure: true,
            procedureSedationEligible: true,
            explicitDecision: true);

        Assert.Equal(SedationState.EligibleYes, sedation.State);
        Assert.True(sedation.ExplicitDecision);
        Assert.True(sedation.SatisfiesAssignmentCompleteness);
    }

    [Fact]
    public void Eligible_sedation_no_is_explicit_and_complete_for_sedation()
    {
        var sedation = SedationContract.FromProcedure(
            hasProcedure: true,
            procedureSedationEligible: true,
            explicitDecision: false);

        Assert.Equal(SedationState.EligibleNo, sedation.State);
        Assert.False(sedation.ExplicitDecision);
        Assert.True(sedation.SatisfiesAssignmentCompleteness);
    }

    [Fact]
    public void Illegal_sedation_combinations_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => SedationContract.FromProcedure(
            hasProcedure: false,
            procedureSedationEligible: false,
            explicitDecision: false));

        Assert.Throws<ArgumentException>(() => SedationContract.FromProcedure(
            hasProcedure: true,
            procedureSedationEligible: false,
            explicitDecision: true));

        Assert.Throws<ArgumentException>(() => SedationContract.Create(SedationState.EligibleYes, false));
        Assert.Throws<ArgumentException>(() => SedationContract.Create(SedationState.EligibleNo, true));
        Assert.Throws<ArgumentException>(() => SedationContract.Create(SedationState.UnavailableNoProcedure, false));
    }

    [Fact]
    public void Unknown_allocation_has_no_values()
    {
        var allocation = ExpectedAllocationContract.Unknown();

        Assert.Equal(ExpectedAllocationState.Unknown, allocation.State);
        Assert.Null(allocation.SuggestedValue);
        Assert.Null(allocation.ConfirmedValue);
        Assert.False(allocation.SatisfiesAssignmentCompleteness);
    }

    [Fact]
    public void Suggested_allocation_has_suggested_value_only()
    {
        var allocation = ExpectedAllocationContract.Suggested(4);

        Assert.Equal(ExpectedAllocationState.Suggested, allocation.State);
        Assert.Equal(4, allocation.SuggestedValue);
        Assert.Null(allocation.ConfirmedValue);
        Assert.False(allocation.SatisfiesAssignmentCompleteness);
    }

    [Fact]
    public void Confirmed_suggestion_has_matching_suggested_and_confirmed_values()
    {
        var allocation = ExpectedAllocationContract.ConfirmedSuggestedValue(4);

        Assert.Equal(ExpectedAllocationState.ConfirmedSuggestedValue, allocation.State);
        Assert.Equal(4, allocation.SuggestedValue);
        Assert.Equal(4, allocation.ConfirmedValue);
        Assert.True(allocation.SatisfiesAssignmentCompleteness);
    }

    [Fact]
    public void Confirmed_adjustment_has_distinct_confirmed_value()
    {
        var allocation = ExpectedAllocationContract.ConfirmedAdjustedValue(suggestedValue: 4, confirmedValue: 5);

        Assert.Equal(ExpectedAllocationState.ConfirmedAdjustedValue, allocation.State);
        Assert.Equal(4, allocation.SuggestedValue);
        Assert.Equal(5, allocation.ConfirmedValue);
        Assert.True(allocation.SatisfiesAssignmentCompleteness);
    }

    [Fact]
    public void Zero_and_negative_allocation_values_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExpectedAllocationContract.Suggested(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExpectedAllocationContract.Suggested(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExpectedAllocationContract.ConfirmedSuggestedValue(0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExpectedAllocationContract.ConfirmedAdjustedValue(suggestedValue: 4, confirmedValue: -1));
    }

    [Fact]
    public void Suggested_allocation_alone_does_not_complete_assignment()
    {
        var assignment = RoomAssignmentContract.Create(
            "otte",
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.Suggested(3));

        Assert.Equal(AssignmentCompleteness.Partial, assignment.Completeness);
    }

    [Fact]
    public void Entirely_absent_assignment_is_absent()
    {
        var assignment = AbsentAssignment();

        Assert.Equal(AssignmentCompleteness.Absent, assignment.Completeness);
    }

    [Fact]
    public void Doctor_only_assignment_is_partial()
    {
        var assignment = RoomAssignmentContract.Create(
            "otte",
            procedureCode: null,
            SedationContract.UnavailableNoProcedure(),
            ExpectedAllocationContract.Unknown());

        Assert.Equal(AssignmentCompleteness.Partial, assignment.Completeness);
    }

    [Fact]
    public void Procedure_only_assignment_is_partial()
    {
        var assignment = RoomAssignmentContract.Create(
            doctorId: null,
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.Unknown());

        Assert.Equal(AssignmentCompleteness.Partial, assignment.Completeness);
    }

    [Fact]
    public void Doctor_and_eligible_procedure_with_unresolved_sedation_is_partial()
    {
        var assignment = RoomAssignmentContract.Create(
            "otte",
            "EXT",
            SedationContract.EligibleUnresolved(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(5));

        Assert.Equal(AssignmentCompleteness.Partial, assignment.Completeness);
    }

    [Fact]
    public void Assignment_missing_allocation_confirmation_is_partial()
    {
        var assignment = RoomAssignmentContract.Create(
            "otte",
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.Suggested(3));

        Assert.Equal(AssignmentCompleteness.Partial, assignment.Completeness);
    }

    [Fact]
    public void Ineligible_procedure_assignment_can_be_complete_without_sedation_decision()
    {
        var assignment = CompleteIneligibleAssignment();

        Assert.Equal(AssignmentCompleteness.Complete, assignment.Completeness);
    }

    [Fact]
    public void Eligible_yes_assignment_can_be_complete()
    {
        var assignment = RoomAssignmentContract.Create(
            "otte",
            "EXT",
            SedationContract.EligibleYes(),
            ExpectedAllocationContract.ConfirmedAdjustedValue(suggestedValue: 4, confirmedValue: 5));

        Assert.Equal(AssignmentCompleteness.Complete, assignment.Completeness);
    }

    [Fact]
    public void Eligible_no_assignment_can_be_complete()
    {
        var assignment = RoomAssignmentContract.Create(
            "otte",
            "EXT",
            SedationContract.EligibleNo(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(4));

        Assert.Equal(AssignmentCompleteness.Complete, assignment.Completeness);
    }

    [Fact]
    public void Active_handoff_contains_complete_assignment_snapshot()
    {
        var readyAt = DateTimeOffset.Parse("2026-07-12T15:00:00Z");
        var handoff = ReadyHandoffContract.Active("handoff-1", readyAt, CompleteIneligibleAssignment());

        Assert.Equal("handoff-1", handoff.HandoffId);
        Assert.Equal(readyAt, handoff.ReadyAt);
        Assert.Equal(ReadyHandoffStatus.Active, handoff.Status);
        Assert.Equal(AssignmentCompleteness.Complete, handoff.Assignment.Completeness);
        Assert.Null(handoff.WithdrawnAt);
        Assert.Null(handoff.AcceptedAt);
    }

    [Fact]
    public void Withdrawn_handoff_requires_withdrawn_timestamp()
    {
        var readyAt = DateTimeOffset.Parse("2026-07-12T15:00:00Z");
        var withdrawnAt = readyAt.AddMinutes(4);
        var handoff = ReadyHandoffContract.Withdrawn(
            "handoff-1",
            readyAt,
            CompleteIneligibleAssignment(),
            withdrawnAt);

        Assert.Equal(ReadyHandoffStatus.Withdrawn, handoff.Status);
        Assert.Equal(withdrawnAt, handoff.WithdrawnAt);
        Assert.Null(handoff.AcceptedAt);
    }

    [Fact]
    public void Accepted_handoff_requires_accepted_timestamp()
    {
        var readyAt = DateTimeOffset.Parse("2026-07-12T15:00:00Z");
        var acceptedAt = readyAt.AddMinutes(6);
        var handoff = ReadyHandoffContract.Accepted(
            "handoff-1",
            readyAt,
            CompleteIneligibleAssignment(),
            acceptedAt);

        Assert.Equal(ReadyHandoffStatus.Accepted, handoff.Status);
        Assert.Equal(acceptedAt, handoff.AcceptedAt);
        Assert.Null(handoff.WithdrawnAt);
    }

    [Fact]
    public void Handoff_rejects_incomplete_assignment()
    {
        Assert.Throws<ArgumentException>(() => ReadyHandoffContract.Active(
            "handoff-1",
            DateTimeOffset.Parse("2026-07-12T15:00:00Z"),
            AbsentAssignment()));
    }

    [Fact]
    public void Handoff_rejects_invalid_timestamp_and_status_combinations()
    {
        var readyAt = DateTimeOffset.Parse("2026-07-12T15:00:00Z");
        var complete = CompleteIneligibleAssignment();

        Assert.Throws<ArgumentException>(() => ReadyHandoffContract.Active("handoff-1", default, complete));
        Assert.Throws<ArgumentException>(() => ReadyHandoffContract.Create(
            "handoff-1",
            readyAt,
            complete,
            ReadyHandoffStatus.Active,
            withdrawnAt: readyAt.AddMinutes(1)));
        Assert.Throws<ArgumentException>(() => ReadyHandoffContract.Create(
            "handoff-1",
            readyAt,
            complete,
            ReadyHandoffStatus.Withdrawn));
        Assert.Throws<ArgumentException>(() => ReadyHandoffContract.Create(
            "handoff-1",
            readyAt,
            complete,
            ReadyHandoffStatus.Withdrawn,
            withdrawnAt: readyAt.AddMinutes(1),
            acceptedAt: readyAt.AddMinutes(2)));
        Assert.Throws<ArgumentException>(() => ReadyHandoffContract.Create(
            "handoff-1",
            readyAt,
            complete,
            ReadyHandoffStatus.Accepted));
        Assert.Throws<ArgumentException>(() => ReadyHandoffContract.Create(
            "handoff-1",
            readyAt,
            complete,
            ReadyHandoffStatus.Accepted,
            withdrawnAt: readyAt.AddMinutes(1),
            acceptedAt: readyAt.AddMinutes(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReadyHandoffContract.Withdrawn("handoff-1", readyAt, complete, readyAt.AddMinutes(-1)));
    }

    [Fact]
    public void Handoff_rejects_empty_identifier()
    {
        Assert.Throws<ArgumentException>(() => ReadyHandoffContract.Active(
            " ",
            DateTimeOffset.Parse("2026-07-12T15:00:00Z"),
            CompleteIneligibleAssignment()));
    }

    [Fact]
    public void None_urgency_is_valid_outside_ready()
    {
        var urgency = ReadyUrgencyEvaluator.Validate(CanonicalRoomLifecycleState.SeatedInPrep, ReadyUrgency.None);

        Assert.Equal(ReadyUrgency.None, urgency);
    }

    [Theory]
    [InlineData(ReadyUrgency.Aging)]
    [InlineData(ReadyUrgency.Stale)]
    public void Aging_and_stale_urgency_are_valid_while_ready(ReadyUrgency urgency)
    {
        var validated = ReadyUrgencyEvaluator.Validate(CanonicalRoomLifecycleState.ReadyForDoctor, urgency);

        Assert.Equal(urgency, validated);
    }

    [Theory]
    [InlineData(CanonicalRoomLifecycleState.Available)]
    [InlineData(CanonicalRoomLifecycleState.Prestaging)]
    [InlineData(CanonicalRoomLifecycleState.SeatedInPrep)]
    [InlineData(CanonicalRoomLifecycleState.DoctorWorking)]
    [InlineData(CanonicalRoomLifecycleState.DoctorComplete)]
    [InlineData(CanonicalRoomLifecycleState.Turnover)]
    public void Aging_and_stale_urgency_are_rejected_outside_ready(CanonicalRoomLifecycleState state)
    {
        Assert.Throws<ArgumentException>(() => ReadyUrgencyEvaluator.Validate(state, ReadyUrgency.Aging));
        Assert.Throws<ArgumentException>(() => ReadyUrgencyEvaluator.Validate(state, ReadyUrgency.Stale));
    }

    [Fact]
    public void Ready_or_later_incomplete_assignment_yields_integrity_fault()
    {
        var assignment = RoomAssignmentContract.Create(
            "otte",
            "EXT",
            SedationContract.EligibleUnresolved(),
            ExpectedAllocationContract.Suggested(4));

        var faults = RoomIntegrityFaultEvaluator.Evaluate(
            CanonicalRoomLifecycleState.ReadyForDoctor,
            assignment);

        var fault = Assert.Single(faults);
        Assert.Equal(RoomIntegrityFaultCode.ReadyAssignmentIncomplete, fault.Code);
        Assert.Same(assignment, fault.Assignment);
    }

    [Fact]
    public void Integrity_fault_is_separate_from_assignment_completeness()
    {
        var assignment = RoomAssignmentContract.Create(
            "otte",
            "EXT",
            SedationContract.EligibleUnresolved(),
            ExpectedAllocationContract.Suggested(4));

        var faults = RoomIntegrityFaultEvaluator.Evaluate(
            CanonicalRoomLifecycleState.DoctorWorking,
            assignment);

        Assert.Equal(AssignmentCompleteness.Partial, assignment.Completeness);
        Assert.Equal(RoomIntegrityFaultCode.ReadyAssignmentIncomplete, Assert.Single(faults).Code);
    }

    [Fact]
    public void Legacy_false_sentinel_is_not_automatically_canonical_eligible_no()
    {
        const bool legacySedationFlag = false;
        const bool sourceProvesExplicitDecision = false;

        var sedation = SedationContract.FromProcedure(
            hasProcedure: true,
            procedureSedationEligible: true,
            explicitDecision: sourceProvesExplicitDecision ? legacySedationFlag : null);

        Assert.Equal(SedationState.EligibleUnresolved, sedation.State);
        Assert.Null(sedation.ExplicitDecision);
    }

    [Fact]
    public void Legacy_zero_sentinel_is_not_canonical_confirmed_allocation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExpectedAllocationContract.Create(
                ExpectedAllocationState.ConfirmedSuggestedValue,
                suggestedValue: 0,
                confirmedValue: 0));
    }

    [Fact]
    public void Procedure_suggestion_is_not_allocation_confirmation()
    {
        var assignment = RoomAssignmentContract.Create(
            "otte",
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.Suggested(3));

        Assert.Equal(ExpectedAllocationState.Suggested, assignment.ExpectedAllocation.State);
        Assert.False(assignment.ExpectedAllocation.SatisfiesAssignmentCompleteness);
        Assert.Equal(AssignmentCompleteness.Partial, assignment.Completeness);
    }

    [Fact]
    public void Capabilities_follow_canonical_lifecycle()
    {
        var available = RoomCapabilitiesEvaluator.Evaluate(
            CanonicalRoomLifecycleState.Available,
            hasCanonicalEpisode: false,
            hasIntegrityFaults: false);
        var prestaging = RoomCapabilitiesEvaluator.Evaluate(
            CanonicalRoomLifecycleState.Prestaging,
            hasCanonicalEpisode: true,
            hasIntegrityFaults: false);
        var seated = RoomCapabilitiesEvaluator.Evaluate(
            CanonicalRoomLifecycleState.SeatedInPrep,
            hasCanonicalEpisode: true,
            hasIntegrityFaults: false);
        var ready = RoomCapabilitiesEvaluator.Evaluate(
            CanonicalRoomLifecycleState.ReadyForDoctor,
            hasCanonicalEpisode: true,
            hasIntegrityFaults: false);
        var working = RoomCapabilitiesEvaluator.Evaluate(
            CanonicalRoomLifecycleState.DoctorWorking,
            hasCanonicalEpisode: true,
            hasIntegrityFaults: false);
        var turnover = RoomCapabilitiesEvaluator.Evaluate(
            CanonicalRoomLifecycleState.Turnover,
            hasCanonicalEpisode: true,
            hasIntegrityFaults: false);

        Assert.True(available.CanBeginPrestage);
        Assert.False(available.CanEditAssignment);
        Assert.True(prestaging.CanEditAssignment);
        Assert.True(prestaging.CanSaveDetails);
        Assert.True(prestaging.CanSeat);
        Assert.True(prestaging.CanCancelPrestage);
        Assert.False(prestaging.CanCancelSeating);
        Assert.False(prestaging.CanReady);
        Assert.True(seated.CanEditAssignment);
        Assert.True(seated.CanReady);
        Assert.True(seated.CanCancelSeating);
        Assert.False(seated.CanSeat);
        Assert.False(ready.CanEditAssignment);
        Assert.False(ready.CanSaveDetails);
        Assert.True(ready.CanCancelSeating);
        Assert.True(ready.CanWithdrawReady);
        Assert.True(ready.CanDoctorArrive);
        Assert.False(working.CanEditAssignment);
        Assert.False(working.CanWithdrawReady);
        Assert.True(working.CanDoctorComplete);
        Assert.True(turnover.CanRoomAvailable);

        var json = JsonSerializer.Serialize(seated, JsonOptions);
        Assert.Contains("\"canReady\":true", json);
        Assert.Contains("\"canCancelSeating\":true", json);
        Assert.DoesNotContain("\"CanReady\":", json);
    }

    [Theory]
    [InlineData(CanonicalRoomLifecycleState.Prestaging)]
    [InlineData(CanonicalRoomLifecycleState.SeatedInPrep)]
    public void Canonical_prearrival_capabilities_require_an_episode(
        CanonicalRoomLifecycleState state)
    {
        var capabilities = RoomCapabilitiesEvaluator.Evaluate(
            state,
            hasCanonicalEpisode: false,
            hasIntegrityFaults: false);

        Assert.False(capabilities.CanEditAssignment);
        Assert.False(capabilities.CanSaveDetails);
        Assert.False(capabilities.CanSeat);
        Assert.False(capabilities.CanReady);
        Assert.True(capabilities.CanCancelPrestage || capabilities.CanCancelSeating);
    }

    [Theory]
    [InlineData(CanonicalRoomLifecycleState.ReadyForDoctor)]
    [InlineData(CanonicalRoomLifecycleState.DoctorWorking)]
    [InlineData(CanonicalRoomLifecycleState.Turnover)]
    public void Integrity_faults_block_progression_but_preserve_ready_cancellation(
        CanonicalRoomLifecycleState state)
    {
        var capabilities = RoomCapabilitiesEvaluator.Evaluate(
            state,
            hasCanonicalEpisode: true,
            hasIntegrityFaults: true);

        Assert.False(capabilities.CanWithdrawReady);
        Assert.False(capabilities.CanDoctorArrive);
        Assert.False(capabilities.CanDoctorComplete);
        Assert.False(capabilities.CanRoomAvailable);
        Assert.Equal(
            state == CanonicalRoomLifecycleState.ReadyForDoctor,
            capabilities.CanCancelSeating);
    }

    [Fact]
    public void New_contract_status_values_serialize_as_strings_with_web_property_names()
    {
        var handoff = ReadyHandoffContract.Active(
            "handoff-1",
            DateTimeOffset.Parse("2026-07-12T15:00:00Z"),
            CompleteIneligibleAssignment());

        var json = JsonSerializer.Serialize(handoff, JsonOptions);

        Assert.Contains("\"handoffId\":\"handoff-1\"", json);
        Assert.Contains("\"status\":\"Active\"", json);
        Assert.Contains("\"completeness\":\"Complete\"", json);
        Assert.Contains("\"state\":\"UnavailableProcedureIneligible\"", json);
        Assert.Contains("\"state\":\"ConfirmedSuggestedValue\"", json);
        Assert.DoesNotContain("\"Status\":", json);
        Assert.DoesNotContain("\"status\":0", json);
        Assert.DoesNotContain("\"completeness\":2", json);
    }

    [Fact]
    public void Nullable_handoff_absence_serializes_as_null()
    {
        var json = JsonSerializer.Serialize(new HandoffEnvelope(null), JsonOptions);

        Assert.Equal("{\"handoff\":null}", json);
    }

    private static RoomAssignmentContract AbsentAssignment() =>
        RoomAssignmentContract.Create(
            doctorId: null,
            procedureCode: null,
            SedationContract.UnavailableNoProcedure(),
            ExpectedAllocationContract.Unknown());

    private static RoomAssignmentContract CompleteIneligibleAssignment() =>
        RoomAssignmentContract.Create(
            "otte",
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(3));

    private sealed record HandoffEnvelope(ReadyHandoffContract? Handoff);
}
