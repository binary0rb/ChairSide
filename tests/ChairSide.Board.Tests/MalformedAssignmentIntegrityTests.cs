using ChairSide.Board.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

// Malformed assignment integrity coverage. The active_rooms table stores assignment columns without value CHECK
// constraints, so a persisted room can hold malformed or contradictory assignment data (procedure/
// sedation mismatch, non-positive or inconsistent allocation). Converting such a row throws the
// contract-conversion exception family. Integrity projection and restart recovery must treat these
// rows as faulted history rather than letting the throw escape and hide the room from GetRoom, the
// board snapshot, or recovery: the malformed persisted truth must remain visible and unrewritten,
// unsafe progression must be blocked, and safe pre-arrival cancellation must remain available.
//
// The ready_handoffs table, by contrast, enforces the assignment contract with CHECK constraints, so
// malformed handoff assignment rows cannot be persisted at all. The handoff tests below pin that
// persistence-layer guard rather than a projection escape that cannot occur.
public sealed class MalformedAssignmentIntegrityTests
{
    private static readonly DateTimeOffset SeedNow = new(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Malformed_active_room_with_contradictory_sedation_recovers_visibly_faulted()
    {
        using var workspace = TestWorkspace.Create();
        // Procedure present but sedation resolved as no-procedure: RoomAssignmentContract.Create throws
        // ArgumentException on the procedure/sedation compatibility check.
        SaveMalformedActiveRoom(
            workspace,
            SedationState.UnavailableNoProcedure,
            ExpectedAllocationState.ConfirmedSuggestedValue,
            suggestedUnits: 3,
            confirmedUnits: 3);

        AssertActiveRoomRecoversVisiblyFaulted(
            workspace,
            SedationState.UnavailableNoProcedure,
            ExpectedAllocationState.ConfirmedSuggestedValue,
            suggestedUnits: 3,
            confirmedUnits: 3);
    }

    [Fact]
    public void Malformed_active_room_with_non_positive_allocation_recovers_visibly_faulted()
    {
        using var workspace = TestWorkspace.Create();
        // Confirmed allocation of zero units: ExpectedAllocationContract.Create throws
        // ArgumentOutOfRangeException on the positive-value guard.
        SaveMalformedActiveRoom(
            workspace,
            SedationState.UnavailableProcedureIneligible,
            ExpectedAllocationState.ConfirmedAdjustedValue,
            suggestedUnits: 3,
            confirmedUnits: 0);

        AssertActiveRoomRecoversVisiblyFaulted(
            workspace,
            SedationState.UnavailableProcedureIneligible,
            ExpectedAllocationState.ConfirmedAdjustedValue,
            suggestedUnits: 3,
            confirmedUnits: 0);
    }

    [Fact]
    public void Malformed_active_room_with_inconsistent_allocation_confirmation_recovers_visibly_faulted()
    {
        using var workspace = TestWorkspace.Create();
        // Confirmed-suggested state but confirmed does not equal suggested: ExpectedAllocationContract
        // .Create throws ArgumentException on the state/value consistency check.
        SaveMalformedActiveRoom(
            workspace,
            SedationState.UnavailableProcedureIneligible,
            ExpectedAllocationState.ConfirmedSuggestedValue,
            suggestedUnits: 3,
            confirmedUnits: 5);

        AssertActiveRoomRecoversVisiblyFaulted(
            workspace,
            SedationState.UnavailableProcedureIneligible,
            ExpectedAllocationState.ConfirmedSuggestedValue,
            suggestedUnits: 3,
            confirmedUnits: 5);
    }

    // A malformed Ready-handoff assignment cannot be persisted: unlike active_rooms, the ready_handoffs
    // table enforces the assignment contract with CHECK constraints (sedation limited to the three
    // completeness-satisfying states, allocation limited to the two confirmed states, positive confirmed
    // units, and allocation state/value consistency). Because malformed handoff assignment rows are
    // rejected at insert time, the ToContract escape cannot arise from a persisted handoff. These tests
    // pin that persistence-layer invariant so a future schema change that weakens it is caught.
    [Fact]
    public void Ready_handoff_schema_rejects_contradictory_sedation_state()
    {
        using var workspace = TestWorkspace.Create();
        Assert.Throws<SqliteException>(() => InsertRawReadyHandoff(
            workspace,
            sedationState: "UnavailableNoProcedure",
            allocationState: "ConfirmedSuggestedValue",
            suggestedUnits: 3,
            confirmedUnits: 3));
    }

    [Fact]
    public void Ready_handoff_schema_rejects_non_positive_allocation()
    {
        using var workspace = TestWorkspace.Create();
        Assert.Throws<SqliteException>(() => InsertRawReadyHandoff(
            workspace,
            sedationState: "UnavailableProcedureIneligible",
            allocationState: "ConfirmedAdjustedValue",
            suggestedUnits: 3,
            confirmedUnits: 0));
    }

    [Fact]
    public void Ready_handoff_schema_rejects_inconsistent_allocation_confirmation()
    {
        using var workspace = TestWorkspace.Create();
        Assert.Throws<SqliteException>(() => InsertRawReadyHandoff(
            workspace,
            sedationState: "UnavailableProcedureIneligible",
            allocationState: "ConfirmedSuggestedValue",
            suggestedUnits: 3,
            confirmedUnits: 5));
    }

    private static void SaveMalformedActiveRoom(
        TestWorkspace workspace,
        SedationState sedationState,
        ExpectedAllocationState allocationState,
        int? suggestedUnits,
        int? confirmedUnits)
    {
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production);
        var room = new RoomState(1)
        {
            EpisodeId = "episode-malformed",
            AssignedDoctor = "otte",
            AssignedDoctorDisplayName = "Dr. Otte",
            ProcedureCode = "CON",
            ProcedureCategory = "Consult",
            State = RoomStates.ReadyForDoctor,
            PrestageStartedAt = SeedNow.AddMinutes(-12),
            SeatedAt = SeedNow.AddMinutes(-8),
            ReadyForDoctorAt = SeedNow.AddMinutes(-2),
            SedationState = sedationState,
            ExpectedAllocationState = allocationState,
            ExpectedAllocationSuggestedUnits = suggestedUnits,
            ExpectedAllocationConfirmedUnits = confirmedUnits
        };
        seed.Repository.SaveRoom(room, seed.Doctors, seed.Procedures);
    }

    private static void AssertActiveRoomRecoversVisiblyFaulted(
        TestWorkspace workspace,
        SedationState sedationState,
        ExpectedAllocationState allocationState,
        int? suggestedUnits,
        int? confirmedUnits)
    {
        var recovered = StoreContext.Create(workspace, environmentName: Environments.Production);

        // GetRoom must not throw; the room stays visible with a deterministic integrity fault.
        var status = recovered.Store.GetRoom(1);
        Assert.NotNull(status);
        Assert.Equal(RoomStates.ReadyForDoctor, status!.State);
        Assert.NotNull(status.IntegrityFaults);
        Assert.Contains(status.IntegrityFaults!, fault => fault.Code == RoomIntegrityFaultCode.ReadyAssignmentIncomplete);

        // Board snapshot projection must not throw either, and the room remains present.
        var snapshot = recovered.Store.GetSnapshot();
        Assert.Contains(snapshot.Rooms, room => room.RoomId == 1);

        // Unsafe progression is blocked.
        Assert.Null(recovered.Store.MarkDoctorArrived(1));

        // The malformed persisted truth is not silently rewritten or normalized.
        var reloaded = recovered.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(sedationState, reloaded.SedationState);
        Assert.Equal(allocationState, reloaded.ExpectedAllocationState);
        Assert.Equal(suggestedUnits, reloaded.ExpectedAllocationSuggestedUnits);
        Assert.Equal(confirmedUnits, reloaded.ExpectedAllocationConfirmedUnits);

        // Safe pre-arrival cancellation remains available (legacy Ready without a handoff reference).
        Assert.NotNull(recovered.Store.CancelSeating(1));
        Assert.Equal(RoomStates.Available, recovered.Store.GetRoom(1)?.State);
    }

    private static void InsertRawReadyHandoff(
        TestWorkspace workspace,
        string sedationState,
        string allocationState,
        int? suggestedUnits,
        int confirmedUnits)
    {
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        using var connection = OpenConnection(context.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ready_handoffs (
                handoff_id, episode_id, room_id, ready_at, doctor_id,
                procedure_code, sedation_state, expected_allocation_state,
                expected_allocation_suggested_units, expected_allocation_confirmed_units)
            VALUES (
                'handoff-malformed', 'episode-handoff-malformed', 1, $readyAt, 'otte',
                'CON', $sedationState, $allocationState,
                $suggested, $confirmed);
            """;
        command.Parameters.AddWithValue("$readyAt", FormatDateTimeOffset(SeedNow.AddMinutes(-2)));
        command.Parameters.AddWithValue("$sedationState", sedationState);
        command.Parameters.AddWithValue("$allocationState", allocationState);
        command.Parameters.AddWithValue("$suggested", (object?)suggestedUnits ?? DBNull.Value);
        command.Parameters.AddWithValue("$confirmed", confirmedUnits);
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
