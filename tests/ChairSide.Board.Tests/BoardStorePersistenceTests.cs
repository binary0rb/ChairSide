using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Tests;

public sealed partial class BoardStoreTests
{
    [Fact]
    public void Persisted_schema_contains_only_non_phi_operational_fields()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        using var connection = OpenConnection(context.DatabasePath);
        var activeRoomColumns = GetColumnNames(connection, "active_rooms");
        var completedCycleColumns = GetColumnNames(connection, "completed_room_cycles");
        var abortedAssignmentColumns = GetColumnNames(connection, "aborted_room_assignments");

        Assert.Subset(AllowedActiveRoomColumns, activeRoomColumns);
        Assert.Subset(AllowedCompletedCycleColumns, completedCycleColumns);
        Assert.Subset(AllowedAbortedAssignmentColumns, abortedAssignmentColumns);
        Assert.DoesNotContain(
            activeRoomColumns.Concat(completedCycleColumns).Concat(abortedAssignmentColumns),
            ContainsBannedPhiTerm);
    }

    [Fact]
    public void DateTimeOffset_round_trip_preserves_cycle_dedup_key()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var seated = SeatViaPrestage(context.Store, 1, "otte", "CON");
        Assert.NotNull(seated);
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));

        var loadedCycle = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.Equal(seated.SeatedAt, loadedCycle.SeatedAt);

        context.Repository.SaveCompletedCycle(loadedCycle, context.Doctors, context.Procedures);

        using var connection = OpenConnection(context.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM completed_room_cycles WHERE room_id = 1;";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void Aborted_assignment_round_trips_full_snapshot_and_nullable_phase_timestamps()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var prestageStartedAt = new DateTimeOffset(2026, 3, 2, 14, 5, 0, TimeSpan.Zero);
        var seatedAt = prestageStartedAt.AddMinutes(4);
        var terminatedAt = prestageStartedAt.AddMinutes(9);
        var record = new AbortedRoomAssignment
        {
            EpisodeId = "episode-a",
            RoomId = 1,
            AssignedDoctor = "otte",
            AssignedDoctorDisplayName = "Dr. Snapshot Otte",
            ProcedureCode = "EXT+SED",
            ProcedureCategory = "Snapshot Extraction + Sedation",
            OriginalDefaultExpectedUnits = 3,
            ExpectedAllocationUnits = 4,
            ExpectedAllocationMinutes = 40,
            AllocationAdjustedFromDefault = true,
            PrestageStartedAt = prestageStartedAt,
            SeatedAt = seatedAt,
            ReadyForDoctorAt = null,
            TerminatedAt = terminatedAt,
            TerminatedFromState = RoomStates.Seated,
            TerminationKind = TerminationKinds.StaffCanceled,
            CancellationReason = CancellationReasons.PatientCanceled
        };

        context.Repository.TerminateIncompleteAssignment(record, new RoomState(1), context.Doctors, context.Procedures);

        var reloaded = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.True(reloaded.AbortedAssignmentId > 0);
        Assert.Equal("episode-a", reloaded.EpisodeId);
        Assert.Equal(1, reloaded.RoomId);
        Assert.Equal("otte", reloaded.AssignedDoctor);
        Assert.Equal("Dr. Snapshot Otte", reloaded.AssignedDoctorDisplayName);
        Assert.Equal("EXT+SED", reloaded.ProcedureCode);
        Assert.Equal("Snapshot Extraction + Sedation", reloaded.ProcedureCategory);
        Assert.Equal(3, reloaded.OriginalDefaultExpectedUnits);
        Assert.Equal(4, reloaded.ExpectedAllocationUnits);
        Assert.Equal(40, reloaded.ExpectedAllocationMinutes);
        Assert.True(reloaded.AllocationAdjustedFromDefault);
        Assert.Equal(prestageStartedAt, reloaded.PrestageStartedAt);
        Assert.Equal(seatedAt, reloaded.SeatedAt);
        Assert.Null(reloaded.ReadyForDoctorAt);
        Assert.Equal(terminatedAt, reloaded.TerminatedAt);
        Assert.Equal(RoomStates.Seated, reloaded.TerminatedFromState);
        Assert.Equal(TerminationKinds.StaffCanceled, reloaded.TerminationKind);
        Assert.Equal(CancellationReasons.PatientCanceled, reloaded.CancellationReason);
    }

    [Fact]
    public void Terminating_the_same_episode_twice_is_idempotent()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var at = new DateTimeOffset(2026, 3, 3, 12, 0, 0, TimeSpan.Zero);
        var first = NewAbortedAssignment("episode-dup", roomId: 1, prestageStartedAt: at, terminatedAt: at.AddMinutes(5));
        context.Repository.TerminateIncompleteAssignment(first, new RoomState(1), context.Doctors, context.Procedures);
        var firstId = first.AbortedAssignmentId;

        // Re-terminating the same episode id (e.g. a retried request or a restart-replayed sweep).
        var second = NewAbortedAssignment("episode-dup", roomId: 1, prestageStartedAt: at, terminatedAt: at.AddMinutes(5));
        context.Repository.TerminateIncompleteAssignment(second, new RoomState(1), context.Doctors, context.Procedures);

        Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.True(firstId > 0);
        Assert.Equal(firstId, second.AbortedAssignmentId);
    }

    [Fact]
    public void Distinct_episodes_in_same_room_at_same_timestamp_persist_separately()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Same room, identical prestage/termination timestamps (the fixed-clock case), different
        // episode ids: both genuinely-distinct episodes must survive - neither overwrites the other.
        var at = new DateTimeOffset(2026, 3, 4, 15, 30, 0, TimeSpan.Zero);
        var episodeX = NewAbortedAssignment("episode-x", roomId: 1, prestageStartedAt: at, terminatedAt: at);
        var episodeY = NewAbortedAssignment("episode-y", roomId: 1, prestageStartedAt: at, terminatedAt: at);
        context.Repository.TerminateIncompleteAssignment(episodeX, new RoomState(1), context.Doctors, context.Procedures);
        context.Repository.TerminateIncompleteAssignment(episodeY, new RoomState(1), context.Doctors, context.Procedures);

        var loaded = context.Repository.LoadAbortedAssignments();
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, r => r.EpisodeId == "episode-x");
        Assert.Contains(loaded, r => r.EpisodeId == "episode-y");
    }

    [Fact]
    public void Terminate_incomplete_assignment_persists_record_and_reset_room_together()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Put room 1 into a non-Available state so the reset is observable.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));

        var record = NewAbortedAssignment(
            "episode-d", roomId: 1,
            prestageStartedAt: new DateTimeOffset(2026, 3, 5, 10, 0, 0, TimeSpan.Zero),
            terminatedAt: new DateTimeOffset(2026, 3, 5, 10, 6, 0, TimeSpan.Zero));
        context.Repository.TerminateIncompleteAssignment(record, new RoomState(1), context.Doctors, context.Procedures);

        // Both writes landed from the one call: the durable record exists...
        Assert.Single(context.Repository.LoadAbortedAssignments());

        // ...and the active room was reset to Available with no residual assignment/episode state.
        var room1 = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Available, room1.State);
        Assert.Null(room1.AssignedDoctor);
        Assert.Null(room1.SeatedAt);
        Assert.Null(room1.PrestageStartedAt);
        Assert.Null(room1.EpisodeId);
    }

    [Fact]
    public void Save_completed_cycle_and_room_persists_both_and_propagates_id_on_insert_and_update()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var seatedAt = new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.Zero);
        var cycle = new CompletedRoomCycle
        {
            RoomId = 1,
            EpisodeId = "episode-e",
            AssignedDoctor = "otte",
            ProcedureCode = "CON",
            PrestageStartedAt = seatedAt.AddMinutes(-5),
            SeatedAt = seatedAt,
            SeatedToDoctorSeconds = 600,
            FinalWaitState = RoomStates.ReadyForDoctor
        };

        context.Repository.SaveCompletedCycleAndRoom(cycle, new RoomState(1), context.Doctors, context.Procedures);

        // Insert path: the id assigned inside the transaction is read back and propagated onto the cycle.
        Assert.True(cycle.CompletedCycleId > 0);
        var insertedId = cycle.CompletedCycleId;

        var loadedCycle = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.Equal(insertedId, loadedCycle.CompletedCycleId);
        Assert.Equal("episode-e", loadedCycle.EpisodeId);
        Assert.Equal(seatedAt.AddMinutes(-5), loadedCycle.PrestageStartedAt);

        // The active room was reset to Available in the same operation.
        var room1 = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Available, room1.State);

        // Update path: the same (room_id, seated_at) upsert reads the id back inside the transaction
        // and keeps it stable, so the propagated id is consistent across insert and update.
        cycle.RoomAvailableAt = seatedAt.AddMinutes(30);
        context.Repository.SaveCompletedCycleAndRoom(cycle, new RoomState(1), context.Doctors, context.Procedures);
        Assert.Equal(insertedId, cycle.CompletedCycleId);

        var afterUpdate = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.Equal(insertedId, afterUpdate.CompletedCycleId);
        Assert.Equal(seatedAt.AddMinutes(30), afterUpdate.RoomAvailableAt);
    }

    [Fact]
    public void Legacy_database_without_prestage_columns_initializes_and_preserves_active_seated_row()
    {
        using var workspace = TestWorkspace.Create();
        var legacyDbPath = Path.Combine(workspace.DataRoot, "legacy.db");
        var seatedAt = new DateTimeOffset(2026, 2, 10, 9, 0, 0, TimeSpan.Zero);

        // Pre-create a database with the pre-Prestaging active_rooms schema (no prestage_started_at /
        // episode_id) holding one Seated room, simulating a production database from before this feature.
        using (var seed = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = legacyDbPath }.ToString()))
        {
            seed.Open();
            ExecuteSql(seed, """
                CREATE TABLE active_rooms (
                    room_id INTEGER PRIMARY KEY,
                    assigned_doctor_id TEXT NULL,
                    assigned_doctor_display_name TEXT NULL,
                    procedure_code TEXT NULL,
                    procedure_category TEXT NULL,
                    state TEXT NOT NULL,
                    seated_at TEXT NULL,
                    aging_started_at TEXT NULL,
                    stale_started_at TEXT NULL,
                    ready_for_doctor_at TEXT NULL,
                    doctor_arrived_at TEXT NULL,
                    doctor_complete_at TEXT NULL,
                    room_available_at TEXT NULL,
                    original_default_expected_units INTEGER NOT NULL DEFAULT 0,
                    expected_allocation_units INTEGER NOT NULL DEFAULT 0,
                    expected_allocation_minutes INTEGER NOT NULL DEFAULT 0,
                    allocation_adjusted_from_default INTEGER NOT NULL DEFAULT 0,
                    updated_at TEXT NOT NULL
                );
                """);
            ExecuteSql(seed, $"""
                INSERT INTO active_rooms (room_id, assigned_doctor_id, procedure_code, state, seated_at, updated_at)
                VALUES (1, 'otte', 'CON', 'seated', '{seatedAt:O}', '{seatedAt:O}');
                """);
        }

        // Opening the store runs the additive migrations; the legacy row must survive untouched and
        // is never forced through Prestaging.
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: legacyDbPath,
            roomCount: 3);

        using (var connection = OpenConnection(legacyDbPath))
        {
            var columns = GetColumnNames(connection, "active_rooms");
            Assert.Contains("prestage_started_at", columns);
            Assert.Contains("episode_id", columns);
        }

        var room1 = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Seated, room1.State);
        Assert.Equal("otte", room1.AssignedDoctor);
        Assert.Equal("CON", room1.ProcedureCode);
        Assert.Equal(seatedAt, room1.SeatedAt);
        Assert.Null(room1.PrestageStartedAt);
        Assert.Null(room1.EpisodeId);
    }

    [Fact]
    public void Reset_room_clears_episode_id_and_prestage_started_at_along_with_every_other_field()
    {
        // ResetRoom is private static; invoked via reflection, mirroring InvokeTryAddColumn's pattern
        // for testing a private static helper directly rather than through a specific caller.
        var room = new RoomState(1)
        {
            EpisodeId = "episode-r",
            AssignedDoctor = "otte",
            AssignedDoctorDisplayName = "Dr. Otte",
            ProcedureCode = "CON",
            ProcedureCategory = "Consult",
            State = RoomStates.Seated,
            PrestageStartedAt = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero),
            SeatedAt = new DateTimeOffset(2026, 5, 1, 9, 5, 0, TimeSpan.Zero),
            AgingStartedAt = new DateTimeOffset(2026, 5, 1, 9, 12, 0, TimeSpan.Zero),
            StaleStartedAt = new DateTimeOffset(2026, 5, 1, 9, 17, 0, TimeSpan.Zero),
            ReadyForDoctorAt = new DateTimeOffset(2026, 5, 1, 9, 6, 0, TimeSpan.Zero),
            DoctorArrivedAt = new DateTimeOffset(2026, 5, 1, 9, 20, 0, TimeSpan.Zero),
            DoctorCompleteAt = new DateTimeOffset(2026, 5, 1, 9, 40, 0, TimeSpan.Zero),
            RoomAvailableAt = new DateTimeOffset(2026, 5, 1, 9, 50, 0, TimeSpan.Zero),
            OriginalDefaultExpectedUnits = 2,
            ExpectedAllocationUnits = 3,
            ExpectedAllocationMinutes = 30,
            AllocationAdjustedFromDefault = true
        };

        InvokeResetRoom(room);

        Assert.Null(room.EpisodeId);
        Assert.Null(room.AssignedDoctor);
        Assert.Null(room.AssignedDoctorDisplayName);
        Assert.Null(room.ProcedureCode);
        Assert.Null(room.ProcedureCategory);
        Assert.Equal(RoomStates.Available, room.State);
        Assert.Null(room.PrestageStartedAt);
        Assert.Null(room.SeatedAt);
        Assert.Null(room.AgingStartedAt);
        Assert.Null(room.StaleStartedAt);
        Assert.Null(room.ReadyForDoctorAt);
        Assert.Null(room.DoctorArrivedAt);
        Assert.Null(room.DoctorCompleteAt);
        Assert.Null(room.RoomAvailableAt);
        Assert.Equal(0, room.OriginalDefaultExpectedUnits);
        Assert.Equal(0, room.ExpectedAllocationUnits);
        Assert.Equal(0, room.ExpectedAllocationMinutes);
        Assert.False(room.AllocationAdjustedFromDefault);
    }

    [Fact]
    public void Development_demo_room_constructions_use_episode_identity_only_for_canonical_ready_seed()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Development, roomCount: 12);

        var rooms = context.Repository.LoadRooms(12);
        Assert.NotEmpty(rooms);
        Assert.All(rooms, room =>
        {
            if (room.RoomId is >= 6 and <= 9)
            {
                Assert.Equal(RoomStates.ReadyForDoctor, room.State);
                Assert.False(string.IsNullOrWhiteSpace(room.EpisodeId));
                Assert.NotNull(room.PrestageStartedAt);
                Assert.False(string.IsNullOrWhiteSpace(room.ActiveReadyHandoffId));
            }
            else
            {
                Assert.Null(room.EpisodeId);
                Assert.Null(room.PrestageStartedAt);
                Assert.Null(room.ActiveReadyHandoffId);
            }
        });
    }

    [Fact]
    public void SQLite_database_uses_wal_journal_mode()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        using var connection = OpenConnection(context.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        Assert.Equal("wal", (string)command.ExecuteScalar()!);
    }

    // -------------------------------------------------------------------------
    // CompletedCycleId stable identity
    // -------------------------------------------------------------------------

    [Fact]
    public void Completed_cycle_receives_nonzero_completed_cycle_id()
    {
        // A newly persisted cycle must carry a positive CompletedCycleId assigned by SQLite.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        Assert.True(cycle.CompletedCycleId > 0, "CompletedCycleId must be a positive value.");
    }

    [Fact]
    public void Reports_expose_completed_cycle_id_for_normal_and_exception_cycles()
    {
        // Both RecentCompletedCycles and ExceptionCycles must expose a positive CompletedCycleId.
        // Also confirms available-wait math is untouched: an unblocked cycle keeps
        // doctorAvailableWaitSeconds == readyToDoctorSeconds and doctorOccupiedWaitSeconds == 0.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Normal cycle on room 1.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        clock.SetUtcNow(now.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(now.AddMinutes(15));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(now.AddMinutes(20));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        // Exception cycle on room 2.
        clock.SetUtcNow(now.AddMinutes(25));
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "pledger", "EXT"));
        clock.SetUtcNow(now.AddMinutes(30));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        clock.SetUtcNow(now.AddMinutes(35));
        var arrived2 = context.Store.MarkDoctorArrived(2);
        Assert.NotNull(arrived2);
        context.Store.MarkCycleAsException(2, arrived2.SeatedAt!.Value, "Test", "Exclude");

        var reports = context.Store.GetReports();

        var normal = Assert.Single(reports.RecentCompletedCycles);
        Assert.True(normal.CompletedCycleId > 0);
        // Available-wait math unchanged: no same-doctor blocker for this cycle.
        Assert.Equal(10 * 60, normal.ReadyToDoctorSeconds);
        Assert.Equal(0, normal.DoctorOccupiedWaitSeconds);
        Assert.Equal(10 * 60, normal.DoctorAvailableWaitSeconds);

        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.True(exception.CompletedCycleId > 0);
        Assert.NotEqual(normal.CompletedCycleId, exception.CompletedCycleId);
    }

    [Fact]
    public void Mark_exception_by_completed_cycle_id_succeeds()
    {
        // The preferred targeting path: mark a cycle as an exception by its stable id.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycleId = Assert.Single(context.Store.GetReports().RecentCompletedCycles).CompletedCycleId;
        Assert.True(cycleId > 0);

        var marked = context.Store.MarkCycleAsExceptionById(cycleId, ExceptionReasons.ManualReview, "Exclude from normal metrics");
        Assert.True(marked);

        var reports = context.Store.GetReports();
        Assert.Empty(reports.RecentCompletedCycles);
        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.Equal(cycleId, exception.CompletedCycleId);
        Assert.True(exception.IsException);
        Assert.Equal(ExceptionReasons.ManualReview, exception.ExceptionReason);

        // Targeting a non-existent id returns false.
        Assert.False(context.Store.MarkCycleAsExceptionById(999999, ExceptionReasons.ManualReview, "noop"));
        // A non-positive id is rejected.
        Assert.False(context.Store.MarkCycleAsExceptionById(0, ExceptionReasons.ManualReview, "noop"));
    }

    [Fact]
    public void Mark_exception_by_legacy_room_and_seated_at_still_works()
    {
        // Backward compatibility: the legacy (roomId, seatedAt) targeting path must still work.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        var marked = context.Store.MarkCycleAsException(cycle.RoomId, cycle.SeatedAt, ExceptionReasons.ManualReview, "Exclude from normal metrics");
        Assert.True(marked);

        var reports = context.Store.GetReports();
        Assert.Empty(reports.RecentCompletedCycles);
        Assert.Equal(cycle.CompletedCycleId, Assert.Single(reports.ExceptionCycles).CompletedCycleId);
    }

    [Fact]
    public void Completed_cycle_id_is_stable_across_store_reload()
    {
        // The id assigned on first persist must survive a store restart unchanged.
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        Assert.NotNull(SeatViaPrestage(first.Store, 1, "otte", "CON"));
        Assert.NotNull(first.Store.MarkReadyForDoctor(1));
        Assert.NotNull(first.Store.MarkDoctorArrived(1));
        Assert.NotNull(first.Store.MarkDoctorComplete(1));
        Assert.NotNull(first.Store.MarkRoomAvailable(1));

        var originalId = Assert.Single(first.Store.GetReports().RecentCompletedCycles).CompletedCycleId;
        Assert.True(originalId > 0);

        // Reload from the same database file.
        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reloaded = Assert.Single(second.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(originalId, reloaded.CompletedCycleId);
    }

    [Fact]
    public void Legacy_completed_cycles_table_without_id_is_migrated_and_backfilled()
    {
        // Defensive migration: a legacy table that predates the explicit id column must be
        // rebuilt with id INTEGER PRIMARY KEY AUTOINCREMENT, preserving all existing data and
        // assigning a unique positive id to every row (backfill).
        using var workspace = TestWorkspace.Create();
        var databasePath = Path.Combine(workspace.DataRoot, "legacy-no-id.db");

        using (var seed = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
        {
            seed.Open();
            using (var create = seed.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE completed_room_cycles (
                        room_id INTEGER NOT NULL,
                        assigned_doctor_id TEXT NOT NULL,
                        assigned_doctor_display_name TEXT NOT NULL,
                        procedure_code TEXT NOT NULL,
                        procedure_category TEXT NOT NULL,
                        seated_at TEXT NOT NULL,
                        doctor_arrived_at TEXT NULL,
                        doctor_complete_at TEXT NULL,
                        room_available_at TEXT NULL,
                        seated_to_doctor_seconds INTEGER NOT NULL,
                        doctor_in_room_seconds INTEGER NULL,
                        turnover_seconds INTEGER NULL,
                        total_room_cycle_seconds INTEGER NULL,
                        final_wait_state TEXT NOT NULL,
                        aging_threshold_reached INTEGER NOT NULL DEFAULT 0,
                        stale_threshold_reached INTEGER NOT NULL DEFAULT 0,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL,
                        UNIQUE(room_id, seated_at)
                    );
                    """;
                create.ExecuteNonQuery();
            }

            InsertLegacyCompletedCycleRow(seed, 1, "2026-06-01T10:00:00.0000000+00:00");
            InsertLegacyCompletedCycleRow(seed, 2, "2026-06-01T11:00:00.0000000+00:00");
        }

        // Constructing the repository runs Initialize() including the id-backfill migration.
        var environment = new TestWebHostEnvironment(workspace.ContentRoot, Environments.Development);
        var deploymentEnvironment = DeploymentEnvironmentPolicy.Resolve(Environments.Development);
        var repository = new SqliteBoardRepository(
            Microsoft.Extensions.Options.Options.Create(new BoardPersistenceOptions { DatabasePath = databasePath }),
            environment,
            deploymentEnvironment,
            new DatabaseIsolationPolicy(
                workspace.DatabaseIsolationLayout(),
                new FileSystemReparsePointInspector()),
            new DatabaseDeploymentIdentityPolicy());

        var cycles = repository.LoadCompletedCycles();

        // All data preserved.
        Assert.Equal(2, cycles.Count);
        Assert.Contains(cycles, c => c.RoomId == 1);
        Assert.Contains(cycles, c => c.RoomId == 2);

        // Every row backfilled with a unique positive id.
        Assert.All(cycles, c => Assert.True(c.CompletedCycleId > 0));
        Assert.Equal(2, cycles.Select(c => c.CompletedCycleId).Distinct().Count());

        // The id column now exists in the schema.
        using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        verify.Open();
        Assert.Contains("id", GetColumnNames(verify, "completed_room_cycles"));
    }

}
