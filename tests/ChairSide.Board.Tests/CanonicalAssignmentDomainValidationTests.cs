using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

// Canonical assignment domain-validation coverage. The contract-bearing store APIs (SaveAssignmentDetails,
// assignment-bearing SeatRoom, MarkReadyForDoctor) must protect their own domain invariants against the
// current active roster - the RoomAssignmentContract only validates its own shape. Validation is
// presence-conditional so partial drafts stay legal; a present doctor must be active, a present
// procedure must resolve to an active procedure, and the sedation state plus the "+SED" code
// representation must be consistent with that procedure's eligibility. Rejection follows the existing
// null-with-no-mutation convention.
public sealed class CanonicalAssignmentDomainValidationTests
{
    private static readonly DateTimeOffset SeedNow = new(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Save_details_rejects_unknown_doctor()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        Assert.NotNull(context.Store.BeginPrestage(1));

        var invalid = RoomAssignmentContract.Create(
            "ghost", "CON", SedationContract.UnavailableProcedureIneligible(), ExpectedAllocationContract.ConfirmedSuggestedValue(1));

        AssertSaveDetailsRejectedWithoutMutation(context, workspace, invalid);
    }

    [Theory]
    [InlineData(ExpectedAllocationState.Suggested)]
    [InlineData(ExpectedAllocationState.ConfirmedSuggestedValue)]
    [InlineData(ExpectedAllocationState.ConfirmedAdjustedValue)]
    public void Save_details_rejects_absent_procedure_with_retained_allocation(
        ExpectedAllocationState allocationState)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        Assert.NotNull(context.Store.BeginPrestage(1));

        var allocation = allocationState switch
        {
            ExpectedAllocationState.Suggested => ExpectedAllocationContract.Suggested(3),
            ExpectedAllocationState.ConfirmedSuggestedValue => ExpectedAllocationContract.ConfirmedSuggestedValue(3),
            ExpectedAllocationState.ConfirmedAdjustedValue => ExpectedAllocationContract.ConfirmedAdjustedValue(3, 4),
            _ => throw new InvalidOperationException($"Unexpected allocation state '{allocationState}'.")
        };
        var invalid = RoomAssignmentContract.Create(
            "otte", null, SedationContract.UnavailableNoProcedure(), allocation);

        AssertSaveDetailsRejectedWithoutMutation(context, workspace, invalid);
    }

    [Fact]
    public void Save_details_rejects_inactive_or_unknown_procedure()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        Assert.NotNull(context.Store.BeginPrestage(1));

        // "SED" is present in the roster but Active = false; validation must still reject it.
        var invalid = RoomAssignmentContract.Create(
            "otte", "SED", SedationContract.UnavailableProcedureIneligible(), ExpectedAllocationContract.ConfirmedSuggestedValue(3));

        AssertSaveDetailsRejectedWithoutMutation(context, workspace, invalid);
    }

    [Fact]
    public void Save_details_rejects_sedation_yes_for_ineligible_procedure()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        Assert.NotNull(context.Store.BeginPrestage(1));

        // CON is sedation-ineligible; a Yes decision is not compatible with it.
        var invalid = RoomAssignmentContract.Create(
            "otte", "CON", SedationContract.EligibleYes(), ExpectedAllocationContract.ConfirmedSuggestedValue(1));

        AssertSaveDetailsRejectedWithoutMutation(context, workspace, invalid);
    }

    [Fact]
    public void Save_details_rejects_procedure_code_sedation_mismatch()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        Assert.NotNull(context.Store.BeginPrestage(1));

        // EXT is sedation-eligible and the decision is Yes, but the code omits the "+SED" modifier.
        var invalid = RoomAssignmentContract.Create(
            "otte", "EXT", SedationContract.EligibleYes(), ExpectedAllocationContract.ConfirmedSuggestedValue(3));

        AssertSaveDetailsRejectedWithoutMutation(context, workspace, invalid);
    }

    [Fact]
    public void Seat_room_rejects_unknown_doctor_without_mutation()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        Assert.NotNull(context.Store.BeginPrestage(1));

        var invalid = RoomAssignmentContract.Create(
            "ghost", "CON", SedationContract.UnavailableProcedureIneligible(), ExpectedAllocationContract.ConfirmedSuggestedValue(1));

        AssertSeatRoomRejectedWithoutMutation(context, workspace, invalid);
    }

    [Fact]
    public void Seat_room_rejects_sedation_yes_for_ineligible_procedure_without_mutation()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        Assert.NotNull(context.Store.BeginPrestage(1));

        var invalid = RoomAssignmentContract.Create(
            "otte", "CON", SedationContract.EligibleYes(), ExpectedAllocationContract.ConfirmedSuggestedValue(1));

        AssertSeatRoomRejectedWithoutMutation(context, workspace, invalid);
    }

    [Theory]
    [InlineData(ExpectedAllocationState.Suggested)]
    [InlineData(ExpectedAllocationState.ConfirmedSuggestedValue)]
    [InlineData(ExpectedAllocationState.ConfirmedAdjustedValue)]
    public void Seat_room_rejects_absent_procedure_with_retained_allocation_without_mutation(
        ExpectedAllocationState allocationState)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        Assert.NotNull(context.Store.BeginPrestage(1));

        var allocation = allocationState switch
        {
            ExpectedAllocationState.Suggested => ExpectedAllocationContract.Suggested(3),
            ExpectedAllocationState.ConfirmedSuggestedValue => ExpectedAllocationContract.ConfirmedSuggestedValue(3),
            ExpectedAllocationState.ConfirmedAdjustedValue => ExpectedAllocationContract.ConfirmedAdjustedValue(3, 4),
            _ => throw new InvalidOperationException($"Unexpected allocation state '{allocationState}'.")
        };
        var invalid = RoomAssignmentContract.Create(
            "otte", null, SedationContract.UnavailableNoProcedure(), allocation);

        AssertSeatRoomRejectedWithoutMutation(context, workspace, invalid);
    }

    [Fact]
    public void Stale_save_details_cannot_regress_ready_room_or_orphan_handoff()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = SeedNow;
        var clock = new ManualTimeProvider(now);
        var seed = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);
        Assert.NotNull(seed.Store.BeginPrestage(1, "otte", "CON"));
        Assert.NotNull(seed.Store.SeatRoomCanonical(1, null).Room);

        var contextA = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);
        var contextB = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);
        var staleLiveBefore = CaptureLiveRoom(contextB.Store, 1);
        Assert.Equal(RoomStates.Seated, staleLiveBefore.State);

        clock.SetUtcNow(now.AddMinutes(2));
        Assert.NotNull(contextA.Store.MarkReadyForDoctor(1));
        var durableReadyBefore = contextA.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var readyLiveBefore = CaptureLiveRoom(contextA.Store, 1);
        var activeHandoffBefore = Assert.Single(
            contextA.Repository.LoadReadyHandoffsByEpisode(durableReadyBefore.EpisodeId!));
        Assert.Equal(ReadyHandoffStatus.Active, activeHandoffBefore.ContractStatus);

        var staleAssignment = RoomAssignmentContract.Create(
            "pledger",
            "EXT+SED",
            SedationContract.EligibleYes(),
            ExpectedAllocationContract.ConfirmedAdjustedValue(3, 5));
        var result = contextB.Store.SaveAssignmentDetails(1, staleAssignment);

        Assert.Null(result);
        AssertReadyTruthUnchanged(contextA, durableReadyBefore, activeHandoffBefore);
        Assert.Equal(readyLiveBefore, CaptureLiveRoom(contextA.Store, 1));
        Assert.Equal(staleLiveBefore, CaptureLiveRoom(contextB.Store, 1));
        Assert.Empty(contextB.Store.GetSnapshot().RecentEvents);
        Assert.Empty(contextB.Repository.LoadAbortedAssignments());
        Assert.Empty(contextB.Repository.LoadCompletedCycles());
        Assert.Empty(contextB.Store.GetReports().ExceptionCycles);
    }

    [Fact]
    public void Stale_assignment_bearing_seat_cannot_overwrite_ready_room()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = SeedNow;
        var clock = new ManualTimeProvider(now);
        var seed = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);
        Assert.NotNull(seed.Store.BeginPrestage(1, "otte", "CON"));

        var contextA = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);
        var contextB = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);
        var staleLiveBefore = CaptureLiveRoom(contextB.Store, 1);
        Assert.Equal(RoomStates.Prestaging, staleLiveBefore.State);

        clock.SetUtcNow(now.AddMinutes(2));
        Assert.NotNull(contextA.Store.SeatRoomCanonical(1, null).Room);
        clock.SetUtcNow(now.AddMinutes(4));
        Assert.NotNull(contextA.Store.MarkReadyForDoctor(1));
        var durableReadyBefore = contextA.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var readyLiveBefore = CaptureLiveRoom(contextA.Store, 1);
        var activeHandoffBefore = Assert.Single(
            contextA.Repository.LoadReadyHandoffsByEpisode(durableReadyBefore.EpisodeId!));
        Assert.Equal(ReadyHandoffStatus.Active, activeHandoffBefore.ContractStatus);

        var staleAssignment = RoomAssignmentContract.Create(
            "pledger",
            "EXT+SED",
            SedationContract.EligibleYes(),
            ExpectedAllocationContract.ConfirmedAdjustedValue(3, 5));
        var result = contextB.Store.SeatRoomCanonical(1, staleAssignment).Room;

        Assert.Null(result);
        AssertReadyTruthUnchanged(contextA, durableReadyBefore, activeHandoffBefore);
        Assert.Equal(readyLiveBefore, CaptureLiveRoom(contextA.Store, 1));
        Assert.Equal(staleLiveBefore, CaptureLiveRoom(contextB.Store, 1));
        Assert.Empty(contextB.Store.GetSnapshot().RecentEvents);
        Assert.Empty(contextB.Repository.LoadAbortedAssignments());
        Assert.Empty(contextB.Repository.LoadCompletedCycles());
        Assert.Empty(contextB.Store.GetReports().ExceptionCycles);
    }

    [Fact]
    public void Mark_ready_rejects_assignment_whose_doctor_is_no_longer_active()
    {
        using var workspace = TestWorkspace.Create();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production);
        SaveSeatedRoom(seed, "CON", SedationState.UnavailableProcedureIneligible, ExpectedAllocationState.ConfirmedSuggestedValue, 1, 1);

        var roster = new DoctorRosterOptions { Doctors = DoctorRosterOptions.DefaultDoctors() };
        roster.Doctors.Single(doctor => doctor.Id == "otte").Active = false;
        var recovered = StoreContext.Create(workspace, environmentName: Environments.Production, doctorRosterOptions: roster);

        Assert.Null(recovered.Store.MarkReadyForDoctor(1));
        AssertRoomStillSeatedWithoutHandoff(recovered);
    }

    [Fact]
    public void Mark_ready_rejects_assignment_whose_procedure_is_no_longer_active()
    {
        using var workspace = TestWorkspace.Create();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production);
        SaveSeatedRoom(seed, "EXT+SED", SedationState.EligibleYes, ExpectedAllocationState.ConfirmedSuggestedValue, 3, 3);

        var roster = new ProcedureRosterOptions { Procedures = ProcedureRosterOptions.DefaultProcedures() };
        roster.Procedures.Single(procedure => procedure.Code == "EXT").Active = false;
        var recovered = StoreContext.Create(workspace, environmentName: Environments.Production, procedureRosterOptions: roster);

        Assert.Null(recovered.Store.MarkReadyForDoctor(1));
        AssertRoomStillSeatedWithoutHandoff(recovered);
    }

    [Fact]
    public void Mark_ready_rejects_impossible_stored_procedure_sedation_combination()
    {
        using var workspace = TestWorkspace.Create();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production);
        // CON is sedation-ineligible, yet the stored sedation is EligibleYes: a domain-impossible pairing
        // that is nonetheless a "Complete" contract shape.
        SaveSeatedRoom(seed, "CON", SedationState.EligibleYes, ExpectedAllocationState.ConfirmedSuggestedValue, 1, 1);

        var recovered = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.Null(recovered.Store.MarkReadyForDoctor(1));
        AssertRoomStillSeatedWithoutHandoff(recovered);
    }

    [Fact]
    public void Doctor_only_assignment_with_unknown_allocation_is_valid_partial_draft()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        Assert.NotNull(context.Store.BeginPrestage(1));

        var partial = RoomAssignmentContract.Create(
            "otte", null, SedationContract.UnavailableNoProcedure(), ExpectedAllocationContract.Unknown());

        Assert.Equal(AssignmentCompleteness.Partial, partial.Completeness);
        Assert.NotNull(context.Store.SaveAssignmentDetails(1, partial));
        var stored = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal("otte", stored.AssignedDoctor);
        Assert.Null(stored.ProcedureCode);
        Assert.Equal(ExpectedAllocationState.Unknown, stored.ExpectedAllocationState);
    }

    [Fact]
    public void Fully_absent_assignment_is_valid()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        Assert.NotNull(context.Store.BeginPrestage(1));

        var absent = RoomAssignmentContract.Create(
            null, null, SedationContract.UnavailableNoProcedure(), ExpectedAllocationContract.Unknown());

        Assert.Equal(AssignmentCompleteness.Absent, absent.Completeness);
        Assert.NotNull(context.Store.SaveAssignmentDetails(1, absent));
        var stored = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Null(stored.AssignedDoctor);
        Assert.Null(stored.ProcedureCode);
        Assert.Equal(ExpectedAllocationState.Unknown, stored.ExpectedAllocationState);
    }

    [Fact]
    public void Current_save_details_succeeds()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        Assert.NotNull(context.Store.BeginPrestage(1));
        var assignment = RoomAssignmentContract.Create(
            "otte",
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(1));

        var result = context.Store.SaveAssignmentDetails(1, assignment);

        Assert.NotNull(result);
        Assert.Equal(RoomStates.Prestaging, result!.State);
        Assert.Equal("otte", result.AssignedDoctor);
        Assert.Equal("CON", result.ProcedureCode);
    }

    [Fact]
    public void Current_assignment_bearing_seat_room_succeeds()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        Assert.NotNull(context.Store.BeginPrestage(1));
        var assignment = RoomAssignmentContract.Create(
            "otte",
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(1));

        var result = context.Store.SeatRoomCanonical(1, assignment).Room;

        Assert.NotNull(result);
        Assert.Equal(RoomStates.Seated, result!.State);
        Assert.Equal("otte", result.AssignedDoctor);
        Assert.Equal("CON", result.ProcedureCode);
    }

    [Fact]
    public void Begin_prestage_still_succeeds_from_existing_available_row()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var available = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Available, available.State);

        var result = context.Store.BeginPrestage(1);

        Assert.NotNull(result);
        Assert.Equal(RoomStates.Prestaging, result!.State);
        var stored = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Prestaging, stored.State);
        Assert.False(string.IsNullOrWhiteSpace(stored.EpisodeId));
    }

    [Fact]
    public void Valid_canonical_contract_saves_seats_and_reaches_ready()
    {
        using var workspace = TestWorkspace.Create();
        var clock = new ManualTimeProvider(SeedNow);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        var assignment = RoomAssignmentContract.Create(
            "otte", "EXT+SED", SedationContract.EligibleYes(), ExpectedAllocationContract.ConfirmedSuggestedValue(3));

        Assert.NotNull(context.Store.BeginPrestage(1));
        Assert.NotNull(context.Store.SaveAssignmentDetails(1, assignment));
        clock.SetUtcNow(SeedNow.AddMinutes(2));
        Assert.NotNull(context.Store.SeatRoomCanonical(1, null).Room);
        clock.SetUtcNow(SeedNow.AddMinutes(4));
        var ready = context.Store.MarkReadyForDoctor(1);

        Assert.NotNull(ready);
        Assert.Equal(RoomStates.ReadyForDoctor, ready!.State);
        var stored = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal("EXT+SED", stored.ProcedureCode);
        Assert.False(string.IsNullOrWhiteSpace(stored.ActiveReadyHandoffId));
    }

    private static void AssertSaveDetailsRejectedWithoutMutation(StoreContext context, TestWorkspace workspace, RoomAssignmentContract invalid)
    {
        var before = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

        Assert.Null(context.Store.SaveAssignmentDetails(1, invalid));

        // Live state unchanged.
        var live = context.Store.GetRoom(1);
        Assert.Equal(RoomStates.Prestaging, live!.State);
        Assert.Null(live.AssignedDoctor);
        Assert.Null(live.ProcedureCode);
        // Persisted state unchanged (field-for-field on the assignment snapshot).
        AssertPersistedAssignmentUnchanged(before, workspace);
    }

    private static void AssertSeatRoomRejectedWithoutMutation(StoreContext context, TestWorkspace workspace, RoomAssignmentContract invalid)
    {
        var before = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

        Assert.Null(context.Store.SeatRoomCanonical(1, invalid).Room);

        var live = context.Store.GetRoom(1);
        Assert.Equal(RoomStates.Prestaging, live!.State);
        Assert.Null(live.SeatedAt);
        AssertPersistedAssignmentUnchanged(before, workspace);
    }

    private static void AssertPersistedAssignmentUnchanged(RoomState before, TestWorkspace workspace)
    {
        var reloaded = StoreContext.Create(workspace, environmentName: Environments.Production);
        var after = reloaded.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.EpisodeId, after.EpisodeId);
        Assert.Equal(before.AssignedDoctor, after.AssignedDoctor);
        Assert.Equal(before.ProcedureCode, after.ProcedureCode);
        Assert.Equal(before.SedationState, after.SedationState);
        Assert.Equal(before.ExpectedAllocationState, after.ExpectedAllocationState);
        Assert.Equal(before.SeatedAt, after.SeatedAt);
        Assert.Equal(before.ActiveReadyHandoffId, after.ActiveReadyHandoffId);
    }

    private static void AssertRoomStillSeatedWithoutHandoff(StoreContext recovered)
    {
        var room = recovered.Store.GetRoom(1);
        Assert.Equal(RoomStates.Seated, room!.State);
        Assert.Null(room.ReadyForDoctorAt);
        var stored = recovered.Repository.LoadRooms(3).Single(item => item.RoomId == 1);
        Assert.Null(stored.ActiveReadyHandoffId);
        Assert.Empty(recovered.Repository.LoadReadyHandoffsByEpisode(stored.EpisodeId!));
    }

    private static void AssertReadyTruthUnchanged(
        StoreContext context,
        RoomState expectedRoom,
        PersistedReadyHandoff expectedHandoff)
    {
        var actualRoom = context.Repository.LoadRooms(3).Single(room => room.RoomId == expectedRoom.RoomId);
        Assert.Equal(RoomStates.ReadyForDoctor, actualRoom.State);
        Assert.Equal(expectedRoom.EpisodeId, actualRoom.EpisodeId);
        Assert.Equal(expectedRoom.ReadyForDoctorAt, actualRoom.ReadyForDoctorAt);
        Assert.Equal(expectedRoom.ActiveReadyHandoffId, actualRoom.ActiveReadyHandoffId);
        Assert.Equal(expectedRoom.AssignedDoctor, actualRoom.AssignedDoctor);
        Assert.Equal(expectedRoom.AssignedDoctorDisplayName, actualRoom.AssignedDoctorDisplayName);
        Assert.Equal(expectedRoom.ProcedureCode, actualRoom.ProcedureCode);
        Assert.Equal(expectedRoom.ProcedureCategory, actualRoom.ProcedureCategory);
        Assert.Equal(expectedRoom.SedationState, actualRoom.SedationState);
        Assert.Equal(expectedRoom.ExpectedAllocationState, actualRoom.ExpectedAllocationState);
        Assert.Equal(expectedRoom.ExpectedAllocationSuggestedUnits, actualRoom.ExpectedAllocationSuggestedUnits);
        Assert.Equal(expectedRoom.ExpectedAllocationConfirmedUnits, actualRoom.ExpectedAllocationConfirmedUnits);

        var actualHandoff = Assert.Single(context.Repository.LoadReadyHandoffsByEpisode(expectedRoom.EpisodeId!));
        Assert.Equal(expectedHandoff.HandoffId, actualHandoff.HandoffId);
        Assert.Equal(ReadyHandoffStatus.Active, actualHandoff.ContractStatus);
        Assert.Equal(actualHandoff.HandoffId, actualRoom.ActiveReadyHandoffId);
    }

    private static LiveRoomSnapshot CaptureLiveRoom(DemoBoardStore store, int roomId)
    {
        var roomsField = typeof(DemoBoardStore).GetField(
            "_rooms",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var rooms = Assert.IsType<List<RoomState>>(roomsField?.GetValue(store));
        var room = rooms.Single(item => item.RoomId == roomId);
        return new LiveRoomSnapshot(
            room.EpisodeId,
            room.AssignedDoctor,
            room.AssignedDoctorDisplayName,
            room.ProcedureCode,
            room.ProcedureCategory,
            room.SedationState,
            room.ExpectedAllocationState,
            room.ExpectedAllocationSuggestedUnits,
            room.ExpectedAllocationConfirmedUnits,
            room.ActiveReadyHandoffId,
            room.AcceptedReadyHandoffId,
            room.State,
            room.PrestageStartedAt,
            room.SeatedAt,
            room.ReadyForDoctorAt);
    }

    private static void SaveSeatedRoom(
        StoreContext context,
        string procedureCode,
        SedationState sedationState,
        ExpectedAllocationState allocationState,
        int suggestedUnits,
        int confirmedUnits)
    {
        var room = new RoomState(1)
        {
            EpisodeId = "episode-seated",
            AssignedDoctor = "otte",
            AssignedDoctorDisplayName = "Dr. Otte",
            ProcedureCode = procedureCode,
            ProcedureCategory = "Procedure",
            State = RoomStates.Seated,
            PrestageStartedAt = SeedNow.AddMinutes(-8),
            SeatedAt = SeedNow.AddMinutes(-4),
            SedationState = sedationState,
            ExpectedAllocationState = allocationState,
            ExpectedAllocationSuggestedUnits = suggestedUnits,
            ExpectedAllocationConfirmedUnits = confirmedUnits
        };
        context.Repository.SaveRoom(room, context.Doctors, context.Procedures);
    }

    private sealed record LiveRoomSnapshot(
        string? EpisodeId,
        string? AssignedDoctor,
        string? AssignedDoctorDisplayName,
        string? ProcedureCode,
        string? ProcedureCategory,
        SedationState? SedationState,
        ExpectedAllocationState? ExpectedAllocationState,
        int? ExpectedAllocationSuggestedUnits,
        int? ExpectedAllocationConfirmedUnits,
        string? ActiveReadyHandoffId,
        string? AcceptedReadyHandoffId,
        string State,
        DateTimeOffset? PrestageStartedAt,
        DateTimeOffset? SeatedAt,
        DateTimeOffset? ReadyForDoctorAt);
}
