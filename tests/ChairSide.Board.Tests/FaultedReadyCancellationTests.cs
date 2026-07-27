using ChairSide.Board.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

// Faulted Ready cancellation coverage. A Ready room can carry a nonblank ActiveReadyHandoffId that does not resolve to
// the room's own Active handoff (dangling, owned by another room/episode, withdrawn, accepted, or
// terminated). Such a room is faulted: Doctor Arrived and Withdraw Ready stay blocked, but pre-arrival
// cancellation must still succeed. Cancellation must record truthful aborted history and release the
// room without terminating or rewriting the malformed/unrelated/historical handoff row - the strict
// handoff-termination path (used by normal lifecycle transitions) must not fire for a reference the
// room does not genuinely own.
public sealed class FaultedReadyCancellationTests
{
    private static readonly DateTimeOffset SeedNow = new(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
    private const string RoomEpisode = "episode-room-1";

    [Fact]
    public void Cancellation_of_ready_room_with_dangling_handoff_reference_aborts_without_touching_any_handoff()
    {
        using var workspace = TestWorkspace.Create();
        SaveReadyRoom(workspace, 1, RoomEpisode, activeHandoffId: "handoff-does-not-exist");

        var recovered = StoreContext.Create(workspace, environmentName: Environments.Production);
        AssertVisibleFaultAndBlocked(recovered, RoomIntegrityFaultCode.ReadyHandoffMissing);

        Assert.Null(recovered.Repository.LoadReadyHandoff("handoff-does-not-exist"));
        AssertCancelReleasesRoomWithAbortedHistory(recovered, workspace, expectedTerminalHandoffId: null);
        // Still no such handoff row after cancellation - nothing was fabricated.
        Assert.Null(recovered.Repository.LoadReadyHandoff("handoff-does-not-exist"));
    }

    [Fact]
    public void Cancellation_of_ready_room_referencing_another_rooms_handoff_leaves_that_handoff_active()
    {
        using var workspace = TestWorkspace.Create();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Room 2 owns a genuine Active handoff.
        var room2 = NewReadyRoomState(2, "episode-room-2", activeHandoffId: null);
        var handoff2 = seed.Repository.CreateReadyHandoff(room2, CompleteAssignment(), SeedNow, seed.Doctors, seed.Procedures);

        // Room 1 faultily references room 2's handoff.
        SaveReadyRoom(workspace, 1, RoomEpisode, activeHandoffId: handoff2.HandoffId);

        var before = seed.Repository.LoadReadyHandoff(handoff2.HandoffId);
        var room2Before = seed.Repository.LoadRooms(3).Single(room => room.RoomId == 2);

        var recovered = StoreContext.Create(workspace, environmentName: Environments.Production);
        AssertVisibleFaultAndBlocked(recovered, RoomIntegrityFaultCode.ReadyHandoffOwnershipMismatch);
        AssertCancelReleasesRoomWithAbortedHistory(recovered, workspace, expectedTerminalHandoffId: null);

        // Room 2's handoff is untouched and still Active; room 2 itself is unchanged.
        var after = recovered.Repository.LoadReadyHandoff(handoff2.HandoffId);
        AssertHandoffUnchanged(before, after);
        Assert.Equal(ReadyHandoffStatus.Active, after!.ContractStatus);
        var room2After = recovered.Repository.LoadRooms(3).Single(room => room.RoomId == 2);
        Assert.Equal(RoomStates.ReadyForDoctor, room2After.State);
        Assert.Equal(handoff2.HandoffId, room2After.ActiveReadyHandoffId);
        Assert.Equal(room2Before.EpisodeId, room2After.EpisodeId);
    }

    [Fact]
    public void Cancellation_of_ready_room_referencing_another_episodes_handoff_preserves_that_handoff()
    {
        using var workspace = TestWorkspace.Create();
        InsertHandoffRow(workspace, "handoff-other-episode", "episode-foreign", roomId: 1, readyAt: SeedNow,
            withdrawnAt: null, acceptedAt: null, terminatedAt: null, terminationKind: null);
        SaveReadyRoom(workspace, 1, RoomEpisode, activeHandoffId: "handoff-other-episode");

        var recovered = StoreContext.Create(workspace, environmentName: Environments.Production);
        var before = recovered.Repository.LoadReadyHandoff("handoff-other-episode");
        AssertVisibleFaultAndBlocked(recovered, RoomIntegrityFaultCode.ReadyHandoffOwnershipMismatch);
        AssertCancelReleasesRoomWithAbortedHistory(recovered, workspace, expectedTerminalHandoffId: null);

        var after = recovered.Repository.LoadReadyHandoff("handoff-other-episode");
        AssertHandoffUnchanged(before, after);
        Assert.Equal(ReadyHandoffStatus.Active, after!.ContractStatus);
    }

    [Fact]
    public void Cancellation_of_ready_room_referencing_withdrawn_handoff_preserves_that_handoff()
    {
        using var workspace = TestWorkspace.Create();
        InsertHandoffRow(workspace, "handoff-withdrawn", RoomEpisode, roomId: 1, readyAt: SeedNow,
            withdrawnAt: SeedNow.AddMinutes(1), acceptedAt: null, terminatedAt: null, terminationKind: null);
        SaveReadyRoom(workspace, 1, RoomEpisode, activeHandoffId: "handoff-withdrawn");

        var recovered = StoreContext.Create(workspace, environmentName: Environments.Production);
        var before = recovered.Repository.LoadReadyHandoff("handoff-withdrawn");
        AssertVisibleFaultAndBlocked(recovered, RoomIntegrityFaultCode.ReadyHandoffNotActive);
        AssertCancelReleasesRoomWithAbortedHistory(recovered, workspace, expectedTerminalHandoffId: null);

        var after = recovered.Repository.LoadReadyHandoff("handoff-withdrawn");
        AssertHandoffUnchanged(before, after);
        Assert.Equal(ReadyHandoffStatus.Withdrawn, after!.ContractStatus);
    }

    [Fact]
    public void Cancellation_of_ready_room_referencing_accepted_handoff_preserves_that_handoff()
    {
        using var workspace = TestWorkspace.Create();
        InsertHandoffRow(workspace, "handoff-accepted", RoomEpisode, roomId: 1, readyAt: SeedNow,
            withdrawnAt: null, acceptedAt: SeedNow.AddMinutes(1), terminatedAt: null, terminationKind: null);
        SaveReadyRoom(workspace, 1, RoomEpisode, activeHandoffId: "handoff-accepted");

        var recovered = StoreContext.Create(workspace, environmentName: Environments.Production);
        var before = recovered.Repository.LoadReadyHandoff("handoff-accepted");
        AssertVisibleFaultAndBlocked(recovered, RoomIntegrityFaultCode.ReadyHandoffNotActive);
        AssertCancelReleasesRoomWithAbortedHistory(recovered, workspace, expectedTerminalHandoffId: null);

        var after = recovered.Repository.LoadReadyHandoff("handoff-accepted");
        AssertHandoffUnchanged(before, after);
        Assert.Equal(ReadyHandoffStatus.Accepted, after!.ContractStatus);
    }

    [Fact]
    public void Cancellation_of_ready_room_referencing_terminated_handoff_preserves_that_handoff()
    {
        using var workspace = TestWorkspace.Create();
        InsertHandoffRow(workspace, "handoff-terminated", RoomEpisode, roomId: 1, readyAt: SeedNow,
            withdrawnAt: null, acceptedAt: null, terminatedAt: SeedNow.AddMinutes(1), terminationKind: ReadyHandoffTerminationKinds.Expired);
        SaveReadyRoom(workspace, 1, RoomEpisode, activeHandoffId: "handoff-terminated");

        var recovered = StoreContext.Create(workspace, environmentName: Environments.Production);
        var before = recovered.Repository.LoadReadyHandoff("handoff-terminated");
        AssertVisibleFaultAndBlocked(recovered, RoomIntegrityFaultCode.ReadyHandoffNotActive);
        AssertCancelReleasesRoomWithAbortedHistory(recovered, workspace, expectedTerminalHandoffId: null);

        var after = recovered.Repository.LoadReadyHandoff("handoff-terminated");
        AssertHandoffUnchanged(before, after);
        Assert.Null(after!.ContractStatus); // terminated handoffs are audit history, not a live status
        Assert.Equal(ReadyHandoffTerminationKinds.Expired, after.TerminationKind);
    }

    [Fact]
    public void Cancellation_of_clean_owned_active_ready_room_still_terminates_its_handoff()
    {
        using var workspace = TestWorkspace.Create();
        var clock = new ManualTimeProvider(SeedNow);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        Assert.NotNull(context.Store.BeginPrestage(1));
        Assert.NotNull(context.Store.SaveAssignmentDetails(1, CompleteAssignment()));
        clock.SetUtcNow(SeedNow.AddMinutes(2));
        Assert.NotNull(context.Store.SeatRoomCanonical(1, null).Room);
        clock.SetUtcNow(SeedNow.AddMinutes(4));
        var ready = context.Store.MarkReadyForDoctor(1);
        Assert.NotNull(ready);
        var handoffId = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1).ActiveReadyHandoffId;
        Assert.False(string.IsNullOrWhiteSpace(handoffId));

        Assert.Empty(context.Store.GetRoom(1)!.IntegrityFaults!); // clean: no faults

        Assert.NotNull(context.Store.CancelSeating(1));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        // The owned Active handoff is terminated as part of cancellation and linked to the abort record.
        var handoff = context.Repository.LoadReadyHandoff(handoffId!);
        Assert.NotNull(handoff);
        Assert.Null(handoff!.ContractStatus);
        Assert.Equal(ReadyHandoffTerminationKinds.Canceled, handoff.TerminationKind);
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(handoffId, aborted.TerminalReadyHandoffId);
        Assert.Equal(TerminationKinds.StaffCanceled, aborted.TerminationKind);
        Assert.Empty(context.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Cancellation_of_legacy_ready_room_without_handoff_reference_still_cancels()
    {
        using var workspace = TestWorkspace.Create();
        SaveReadyRoom(workspace, 1, RoomEpisode, activeHandoffId: null);

        var recovered = StoreContext.Create(workspace, environmentName: Environments.Production);
        // Legacy Ready with no handoff reference is faulted (missing handoff) but must still cancel.
        AssertVisibleFaultAndBlocked(recovered, RoomIntegrityFaultCode.ReadyHandoffMissing);
        AssertCancelReleasesRoomWithAbortedHistory(recovered, workspace, expectedTerminalHandoffId: null);
    }

    private static void AssertVisibleFaultAndBlocked(StoreContext recovered, RoomIntegrityFaultCode expectedFault)
    {
        var status = recovered.Store.GetRoom(1);
        Assert.NotNull(status);
        Assert.Equal(RoomStates.ReadyForDoctor, status!.State);
        Assert.NotNull(status.IntegrityFaults);
        Assert.Contains(status.IntegrityFaults!, fault => fault.Code == expectedFault);

        // Unsafe progression stays blocked.
        Assert.Null(recovered.Store.MarkDoctorArrived(1));
        Assert.Null(recovered.Store.WithdrawReady(1));
    }

    private static void AssertCancelReleasesRoomWithAbortedHistory(
        StoreContext recovered,
        TestWorkspace workspace,
        string? expectedTerminalHandoffId)
    {
        Assert.NotNull(recovered.Store.CancelSeating(1));
        Assert.Equal(RoomStates.Available, recovered.Store.GetRoom(1)?.State);

        var aborted = Assert.Single(recovered.Repository.LoadAbortedAssignments());
        Assert.Equal(1, aborted.RoomId);
        Assert.Equal(RoomEpisode, aborted.EpisodeId);
        Assert.Equal(TerminationKinds.StaffCanceled, aborted.TerminationKind);
        Assert.Equal(expectedTerminalHandoffId, aborted.TerminalReadyHandoffId);

        // No throughput fabricated.
        Assert.Empty(recovered.Repository.LoadCompletedCycles());
        Assert.Empty(recovered.Store.GetReports().ExceptionCycles);
        Assert.Equal(0, recovered.Store.GetReports().CompletedRoomCyclesCount);

        // Durable release survives reload.
        var reloaded = StoreContext.Create(workspace, environmentName: Environments.Production);
        Assert.Equal(RoomStates.Available, reloaded.Store.GetRoom(1)?.State);
        var durableRoom = reloaded.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Null(durableRoom.EpisodeId);
        Assert.Null(durableRoom.ActiveReadyHandoffId);
        var durableAbort = Assert.Single(reloaded.Repository.LoadAbortedAssignments());
        Assert.Equal(1, durableAbort.RoomId);
        Assert.Equal(expectedTerminalHandoffId, durableAbort.TerminalReadyHandoffId);
    }

    private static void AssertHandoffUnchanged(PersistedReadyHandoff? before, PersistedReadyHandoff? after)
    {
        if (before is null)
        {
            Assert.Null(after);
            return;
        }

        Assert.NotNull(after);
        Assert.Equal(before.HandoffId, after!.HandoffId);
        Assert.Equal(before.EpisodeId, after.EpisodeId);
        Assert.Equal(before.RoomId, after.RoomId);
        Assert.Equal(before.ReadyAt, after.ReadyAt);
        Assert.Equal(before.WithdrawnAt, after.WithdrawnAt);
        Assert.Equal(before.AcceptedAt, after.AcceptedAt);
        Assert.Equal(before.TerminatedAt, after.TerminatedAt);
        Assert.Equal(before.TerminationKind, after.TerminationKind);
        Assert.Equal(before.Assignment.DoctorId, after.Assignment.DoctorId);
        Assert.Equal(before.Assignment.ProcedureCode, after.Assignment.ProcedureCode);
        Assert.Equal(before.Assignment.SedationState, after.Assignment.SedationState);
        Assert.Equal(before.Assignment.ExpectedAllocationState, after.Assignment.ExpectedAllocationState);
        Assert.Equal(before.Assignment.ExpectedAllocationSuggestedUnits, after.Assignment.ExpectedAllocationSuggestedUnits);
        Assert.Equal(before.Assignment.ExpectedAllocationConfirmedUnits, after.Assignment.ExpectedAllocationConfirmedUnits);
    }

    private static RoomAssignmentContract CompleteAssignment() =>
        RoomAssignmentContract.Create(
            "otte",
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(3));

    private static RoomState NewReadyRoomState(int roomId, string episodeId, string? activeHandoffId) =>
        new(roomId)
        {
            EpisodeId = episodeId,
            AssignedDoctor = "otte",
            AssignedDoctorDisplayName = "Dr. Otte",
            ProcedureCode = "CON",
            ProcedureCategory = "Consult",
            State = RoomStates.ReadyForDoctor,
            PrestageStartedAt = SeedNow.AddMinutes(-12),
            SeatedAt = SeedNow.AddMinutes(-8),
            ReadyForDoctorAt = SeedNow.AddMinutes(-2),
            SedationState = SedationState.UnavailableProcedureIneligible,
            ExpectedAllocationState = ExpectedAllocationState.ConfirmedSuggestedValue,
            ExpectedAllocationSuggestedUnits = 3,
            ExpectedAllocationConfirmedUnits = 3,
            ActiveReadyHandoffId = activeHandoffId
        };

    private static void SaveReadyRoom(TestWorkspace workspace, int roomId, string episodeId, string? activeHandoffId)
    {
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production);
        seed.Repository.SaveRoom(NewReadyRoomState(roomId, episodeId, activeHandoffId), seed.Doctors, seed.Procedures);
    }

    private static void InsertHandoffRow(
        TestWorkspace workspace,
        string handoffId,
        string episodeId,
        int roomId,
        DateTimeOffset readyAt,
        DateTimeOffset? withdrawnAt,
        DateTimeOffset? acceptedAt,
        DateTimeOffset? terminatedAt,
        string? terminationKind)
    {
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        using var connection = OpenConnection(context.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ready_handoffs (
                handoff_id, episode_id, room_id, ready_at, withdrawn_at, accepted_at, terminated_at,
                termination_kind, doctor_id, procedure_code, sedation_state, expected_allocation_state,
                expected_allocation_suggested_units, expected_allocation_confirmed_units)
            VALUES (
                $handoffId, $episodeId, $roomId, $readyAt, $withdrawnAt, $acceptedAt, $terminatedAt,
                $terminationKind, 'otte', 'CON', 'UnavailableProcedureIneligible', 'ConfirmedSuggestedValue',
                3, 3);
            """;
        command.Parameters.AddWithValue("$handoffId", handoffId);
        command.Parameters.AddWithValue("$episodeId", episodeId);
        command.Parameters.AddWithValue("$roomId", roomId);
        command.Parameters.AddWithValue("$readyAt", FormatDateTimeOffset(readyAt));
        command.Parameters.AddWithValue("$withdrawnAt", (object?)(withdrawnAt is { } w ? FormatDateTimeOffset(w) : null) ?? DBNull.Value);
        command.Parameters.AddWithValue("$acceptedAt", (object?)(acceptedAt is { } a ? FormatDateTimeOffset(a) : null) ?? DBNull.Value);
        command.Parameters.AddWithValue("$terminatedAt", (object?)(terminatedAt is { } t ? FormatDateTimeOffset(t) : null) ?? DBNull.Value);
        command.Parameters.AddWithValue("$terminationKind", (object?)terminationKind ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenConnection(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string FormatDateTimeOffset(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O");
}
