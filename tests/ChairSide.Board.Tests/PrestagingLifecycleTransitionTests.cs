using ChairSide.Board.Services;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class PrestagingLifecycleTransitionTests
{
    [Fact]
    public void Bare_begin_persists_a_truthful_absent_assignment()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: new ManualTimeProvider(now));

        var status = context.Store.BeginPrestage(1);

        Assert.NotNull(status);
        Assert.Equal(RoomStates.Prestaging, status.State);
        Assert.Null(status.AssignedDoctor);
        Assert.Null(status.ProcedureCode);
        Assert.Null(status.SeatedAt);

        var stored = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.False(string.IsNullOrWhiteSpace(stored.EpisodeId));
        Assert.Equal(now, stored.PrestageStartedAt);
        Assert.Equal(SedationState.UnavailableNoProcedure, stored.SedationState);
        Assert.Equal(ExpectedAllocationState.Unknown, stored.ExpectedAllocationState);
        Assert.Null(stored.ActiveReadyHandoffId);
    }

    [Fact]
    public void Ready_withdrawal_returns_to_editable_seated_and_preserves_episode()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);
        var assignment = RoomAssignmentContract.Create(
            "otte",
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(3));

        Assert.NotNull(context.Store.BeginPrestage(1));
        Assert.NotNull(context.Store.SaveAssignmentDetails(1, assignment));
        clock.SetUtcNow(now.AddMinutes(4));
        Assert.NotNull(context.Store.SeatRoom(1));
        clock.SetUtcNow(now.AddMinutes(6));
        var ready = context.Store.MarkReadyForDoctor(1);
        Assert.NotNull(ready);

        var before = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var withdrawn = context.Store.WithdrawReady(1);

        Assert.NotNull(withdrawn);
        Assert.Equal(RoomStates.Seated, withdrawn.State);
        var stored = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(before.EpisodeId, stored.EpisodeId);
        Assert.Equal(before.PrestageStartedAt, stored.PrestageStartedAt);
        Assert.Equal(before.SeatedAt, stored.SeatedAt);
        Assert.Equal("otte", stored.AssignedDoctor);
        Assert.Null(stored.ActiveReadyHandoffId);
        Assert.Equal(ReadyHandoffStatus.Withdrawn, context.Repository.LoadReadyHandoffsByEpisode(stored.EpisodeId!).Single().ContractStatus);
    }

    [Fact]
    public void Doctor_arrived_accepts_the_immutable_ready_snapshot()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);
        var assignment = RoomAssignmentContract.Create(
            "otte",
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(3));

        Assert.NotNull(context.Store.BeginPrestage(1));
        Assert.NotNull(context.Store.SaveAssignmentDetails(1, assignment));
        clock.SetUtcNow(now.AddMinutes(1));
        Assert.NotNull(context.Store.SeatRoom(1));
        clock.SetUtcNow(now.AddMinutes(2));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(now.AddMinutes(4));

        var arrived = context.Store.MarkDoctorArrived(1);

        Assert.NotNull(arrived);
        Assert.Equal(RoomStates.DoctorInRoom, arrived.State);
        var active = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Null(active.ActiveReadyHandoffId);
        Assert.False(string.IsNullOrWhiteSpace(active.AcceptedReadyHandoffId));
        var cycle = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.Equal(active.AcceptedReadyHandoffId, cycle.AcceptedReadyHandoffId);
        Assert.Equal("otte", cycle.AssignedDoctor);
        Assert.Equal("CON", cycle.ProcedureCode);
    }

    [Fact]
    public void Seat_with_draft_persists_the_assignment_and_seating_in_one_transition()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: new ManualTimeProvider(now));
        var draft = RoomAssignmentContract.Create(
            "otte",
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(3));

        Assert.NotNull(context.Store.BeginPrestage(1));
        var seated = context.Store.SeatRoom(1, draft);

        Assert.NotNull(seated);
        Assert.Equal(RoomStates.Seated, seated.State);
        var stored = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal("otte", stored.AssignedDoctor);
        Assert.Equal("CON", stored.ProcedureCode);
        Assert.Equal(now, stored.SeatedAt);
    }

    // Provenance note: the current flat contract cannot observe whether dependent values in a changed-
    // procedure draft were freshly reconfirmed or copied stale from the prior procedure - those cases are
    // observationally identical. These tests therefore assert only what the model can truthfully enforce:
    // domain compatibility of the submitted contract against the selected procedure. True stale-versus-
    // reconfirmed provenance remains deferred (it needs a client-supplied procedure-binding token).
    [Fact]
    public void Changed_procedure_with_incompatible_dependent_state_is_rejected()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var sedationCase = RoomAssignmentContract.Create(
            "otte", "EXT+SED", SedationContract.EligibleYes(), ExpectedAllocationContract.ConfirmedAdjustedValue(3, 5));

        Assert.NotNull(context.Store.BeginPrestage(1));
        Assert.NotNull(context.Store.SaveAssignmentDetails(1, sedationCase));

        // Change to the sedation-ineligible CON while carrying an EligibleYes decision: incompatible.
        var incompatible = RoomAssignmentContract.Create(
            "otte", "CON", SedationContract.EligibleYes(), ExpectedAllocationContract.ConfirmedSuggestedValue(1));
        Assert.Null(context.Store.SaveAssignmentDetails(1, incompatible));

        // The prior valid assignment is preserved untouched.
        var stored = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal("EXT+SED", stored.ProcedureCode);
        Assert.Equal(SedationState.EligibleYes, stored.SedationState);
        Assert.Equal(ExpectedAllocationState.ConfirmedAdjustedValue, stored.ExpectedAllocationState);
    }

    [Fact]
    public void Changed_procedure_with_valid_replacement_values_is_accepted()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var sedationCase = RoomAssignmentContract.Create(
            "otte", "EXT+SED", SedationContract.EligibleYes(), ExpectedAllocationContract.ConfirmedAdjustedValue(3, 5));

        Assert.NotNull(context.Store.BeginPrestage(1));
        Assert.NotNull(context.Store.SaveAssignmentDetails(1, sedationCase));

        // Replacement whose dependent values are valid for the new procedure is accepted in one commit.
        var consult = RoomAssignmentContract.Create(
            "otte", "CON", SedationContract.UnavailableProcedureIneligible(), ExpectedAllocationContract.ConfirmedSuggestedValue(1));
        Assert.NotNull(context.Store.SaveAssignmentDetails(1, consult));

        var stored = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal("CON", stored.ProcedureCode);
        Assert.Equal(SedationState.UnavailableProcedureIneligible, stored.SedationState);
        Assert.Equal(ExpectedAllocationState.ConfirmedSuggestedValue, stored.ExpectedAllocationState);
        Assert.Equal(1, stored.ExpectedAllocationConfirmedUnits);
    }

    [Fact]
    public void Partial_replacement_remains_partial_and_cannot_reach_ready()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var consult = RoomAssignmentContract.Create(
            "otte", "CON", SedationContract.UnavailableProcedureIneligible(), ExpectedAllocationContract.ConfirmedSuggestedValue(1));

        Assert.NotNull(context.Store.BeginPrestage(1));
        Assert.NotNull(context.Store.SaveAssignmentDetails(1, consult));
        Assert.NotNull(context.Store.SeatRoom(1));

        // Change to sedation-eligible EXT but leave sedation unresolved: valid domain, still Partial.
        var partial = RoomAssignmentContract.Create(
            "otte", "EXT", SedationContract.EligibleUnresolved(), ExpectedAllocationContract.Suggested(3));
        Assert.NotNull(context.Store.SaveAssignmentDetails(1, partial));

        var stored = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal("EXT", stored.ProcedureCode);
        Assert.Equal(SedationState.EligibleUnresolved, stored.SedationState);
        // A partial assignment cannot reach Ready.
        Assert.Null(context.Store.MarkReadyForDoctor(1));
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(1)?.State);
    }

    [Fact]
    public void Same_procedure_legitimate_edits_remain_accepted()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var sedationCase = RoomAssignmentContract.Create(
            "otte", "EXT+SED", SedationContract.EligibleYes(), ExpectedAllocationContract.ConfirmedSuggestedValue(3));

        Assert.NotNull(context.Store.BeginPrestage(1));
        Assert.NotNull(context.Store.SaveAssignmentDetails(1, sedationCase));

        // Same procedure, adjusted allocation confirmation: accepted.
        var edited = RoomAssignmentContract.Create(
            "otte", "EXT+SED", SedationContract.EligibleYes(), ExpectedAllocationContract.ConfirmedAdjustedValue(3, 4));
        Assert.NotNull(context.Store.SaveAssignmentDetails(1, edited));

        var stored = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal("EXT+SED", stored.ProcedureCode);
        Assert.Equal(SedationState.EligibleYes, stored.SedationState);
        Assert.Equal(ExpectedAllocationState.ConfirmedAdjustedValue, stored.ExpectedAllocationState);
        Assert.Equal(4, stored.ExpectedAllocationConfirmedUnits);
    }

    [Fact]
    public void Faulted_legacy_ready_blocks_arrival_but_still_allows_prearrival_cancellation()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var room = new RoomState(1)
        {
            AssignedDoctor = "otte",
            ProcedureCode = "CON",
            State = RoomStates.ReadyForDoctor,
            PrestageStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            SeatedAt = DateTimeOffset.UtcNow.AddMinutes(-8),
            ReadyForDoctorAt = DateTimeOffset.UtcNow.AddMinutes(-2)
        };
        context.Repository.SaveRoom(room, context.Doctors, context.Procedures);
        var recovered = StoreContext.Create(workspace, environmentName: Environments.Production);

        var status = recovered.Store.GetRoom(1);
        Assert.Contains(status!.IntegrityFaults!, fault => fault.Code == RoomIntegrityFaultCode.ReadyHandoffMissing);
        Assert.Null(recovered.Store.MarkDoctorArrived(1));
        Assert.NotNull(recovered.Store.CancelSeating(1));
    }
}
