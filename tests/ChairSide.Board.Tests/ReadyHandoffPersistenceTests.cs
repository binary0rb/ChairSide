using ChairSide.Board.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class ReadyHandoffPersistenceTests
{
    [Fact]
    public void Fresh_database_contains_ready_handoff_schema_and_canonical_columns()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        using var connection = OpenConnection(context.DatabasePath);

        Assert.Contains("sedation_state", GetColumnNames(connection, "active_rooms"));
        Assert.Contains("expected_allocation_state", GetColumnNames(connection, "active_rooms"));
        Assert.Contains("active_ready_handoff_id", GetColumnNames(connection, "active_rooms"));
        Assert.Contains("accepted_ready_handoff_id", GetColumnNames(connection, "active_rooms"));
        Assert.Contains("accepted_ready_handoff_id", GetColumnNames(connection, "completed_room_cycles"));
        Assert.Contains("terminal_ready_handoff_id", GetColumnNames(connection, "aborted_room_assignments"));
        Assert.Contains("ready_handoffs", GetTableNames(connection));
        Assert.Contains("ix_ready_handoffs_one_active_per_episode", GetIndexNames(connection));
        Assert.Contains("ix_ready_handoffs_one_accepted_per_episode", GetIndexNames(connection));
    }

    [Fact]
    public void Legacy_schema_migrates_without_fabricating_ready_handoffs_or_accepted_snapshots()
    {
        using var workspace = TestWorkspace.Create();
        var legacyDbPath = Path.Combine(workspace.DataRoot, "legacy.db");
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var seatedAt = readyAt.AddMinutes(-8);

        using (var seed = OpenConnection(legacyDbPath))
        {
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
                    prestage_started_at TEXT NULL,
                    episode_id TEXT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE completed_room_cycles (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    room_id INTEGER NOT NULL,
                    assigned_doctor_id TEXT NOT NULL,
                    assigned_doctor_display_name TEXT NOT NULL,
                    procedure_code TEXT NOT NULL,
                    procedure_category TEXT NOT NULL,
                    seated_at TEXT NOT NULL,
                    ready_for_doctor_at TEXT NULL,
                    doctor_arrived_at TEXT NULL,
                    doctor_complete_at TEXT NULL,
                    room_available_at TEXT NULL,
                    seated_to_doctor_seconds INTEGER NOT NULL,
                    prep_seconds INTEGER NULL,
                    ready_to_doctor_seconds INTEGER NULL,
                    doctor_in_room_seconds INTEGER NULL,
                    turnover_seconds INTEGER NULL,
                    total_room_cycle_seconds INTEGER NULL,
                    original_default_expected_units INTEGER NOT NULL DEFAULT 0,
                    expected_allocation_units INTEGER NOT NULL DEFAULT 0,
                    expected_allocation_minutes INTEGER NOT NULL DEFAULT 0,
                    allocation_adjusted_from_default INTEGER NOT NULL DEFAULT 0,
                    final_wait_state TEXT NOT NULL,
                    aging_threshold_reached INTEGER NOT NULL DEFAULT 0,
                    stale_threshold_reached INTEGER NOT NULL DEFAULT 0,
                    is_exception INTEGER NOT NULL DEFAULT 0,
                    requires_review INTEGER NOT NULL DEFAULT 0,
                    exception_reason TEXT NULL,
                    review_status TEXT NOT NULL DEFAULT 'PendingReview',
                    suggested_action TEXT NULL,
                    reviewed_at TEXT NULL,
                    reviewed_by TEXT NULL,
                    prestage_started_at TEXT NULL,
                    episode_id TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(room_id, seated_at)
                );

                CREATE TABLE aborted_room_assignments (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    episode_id TEXT NOT NULL,
                    room_id INTEGER NOT NULL,
                    assigned_doctor_id TEXT NOT NULL,
                    assigned_doctor_display_name TEXT NOT NULL,
                    procedure_code TEXT NOT NULL,
                    procedure_category TEXT NOT NULL,
                    original_default_expected_units INTEGER NOT NULL DEFAULT 0,
                    expected_allocation_units INTEGER NOT NULL DEFAULT 0,
                    expected_allocation_minutes INTEGER NOT NULL DEFAULT 0,
                    allocation_adjusted_from_default INTEGER NOT NULL DEFAULT 0,
                    prestage_started_at TEXT NULL,
                    seated_at TEXT NULL,
                    ready_for_doctor_at TEXT NULL,
                    terminated_at TEXT NOT NULL,
                    terminated_from_state TEXT NOT NULL,
                    termination_kind TEXT NOT NULL,
                    cancellation_reason TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(episode_id)
                );
                """);
            ExecuteSql(seed, $"""
                INSERT INTO active_rooms (
                    room_id, assigned_doctor_id, procedure_code, state, seated_at,
                    ready_for_doctor_at, episode_id, updated_at)
                VALUES (1, 'otte', 'CON', 'readyForDoctor', '{FormatDateTimeOffset(seatedAt)}',
                    '{FormatDateTimeOffset(readyAt)}', 'legacy-ready', '{FormatDateTimeOffset(readyAt)}');

                INSERT INTO completed_room_cycles (
                    room_id, assigned_doctor_id, assigned_doctor_display_name, procedure_code,
                    procedure_category, seated_at, ready_for_doctor_at, doctor_arrived_at,
                    seated_to_doctor_seconds, final_wait_state, episode_id, created_at, updated_at)
                VALUES (1, 'otte', 'Dr. Otte', 'CON', 'Consult',
                    '{FormatDateTimeOffset(seatedAt)}', NULL, '{FormatDateTimeOffset(readyAt.AddMinutes(4))}',
                    720, 'readyForDoctor', 'legacy-complete', '{FormatDateTimeOffset(readyAt)}',
                    '{FormatDateTimeOffset(readyAt)}');

                INSERT INTO aborted_room_assignments (
                    episode_id, room_id, assigned_doctor_id, assigned_doctor_display_name,
                    procedure_code, procedure_category, prestage_started_at, terminated_at,
                    terminated_from_state, termination_kind, created_at, updated_at)
                VALUES ('legacy-abort', 2, 'pledger', 'Dr. Pledger', 'EXT+SED',
                    'Extraction + Sedation', '{FormatDateTimeOffset(seatedAt)}',
                    '{FormatDateTimeOffset(readyAt)}', 'prestaging', 'StaffCanceled',
                    '{FormatDateTimeOffset(readyAt)}', '{FormatDateTimeOffset(readyAt)}');
                """);
        }

        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: legacyDbPath);

        using var connection = OpenConnection(context.DatabasePath);
        Assert.Equal(0L, ExecuteScalar<long>(connection, "SELECT COUNT(*) FROM ready_handoffs;"));

        var active = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Contains(active.State, [RoomStates.ReadyForDoctor, RoomStates.Aging, RoomStates.Stale]);
        Assert.Null(active.SedationState);
        Assert.Null(active.ExpectedAllocationState);
        Assert.Null(active.ActiveReadyHandoffId);
        Assert.Null(active.AcceptedReadyHandoffId);

        var completed = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.Null(completed.AcceptedReadyHandoffId);

        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal("legacy-abort", aborted.EpisodeId);
        Assert.Equal(SedationState.EligibleYes, aborted.SedationState);
        Assert.Null(aborted.TerminalReadyHandoffId);
    }

    [Theory]
    [MemberData(nameof(CanonicalAssignments))]
    public void Canonical_active_room_assignment_round_trips(PersistedRoomAssignment assignment)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var room = new RoomState(1)
        {
            EpisodeId = "episode-assignment",
            State = RoomStates.Prestaging,
            PrestageStartedAt = new DateTimeOffset(2026, 7, 12, 14, 0, 0, TimeSpan.Zero)
        };

        var expectation = ActiveRoomWriteExpectation.FromRoom(LoadRoom(context));
        context.Repository.SaveCanonicalAssignment(room, assignment, expectation, context.Doctors, context.Procedures);

        var loaded = context.Repository.LoadRooms(3).Single(item => item.RoomId == 1);
        Assert.Equal(assignment.DoctorId, loaded.AssignedDoctor);
        Assert.Equal(assignment.ProcedureCode, loaded.ProcedureCode);
        Assert.Equal(assignment.SedationState, loaded.SedationState);
        Assert.Equal(assignment.ExpectedAllocationState, loaded.ExpectedAllocationState);
        Assert.Equal(assignment.ExpectedAllocationSuggestedUnits, loaded.ExpectedAllocationSuggestedUnits);
        Assert.Equal(assignment.ExpectedAllocationConfirmedUnits, loaded.ExpectedAllocationConfirmedUnits);
    }

    [Theory]
    [InlineData("episode")]
    [InlineData("state")]
    public void Canonical_assignment_compare_and_swap_rejects_wrong_expected_identity(
        string mismatch)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var durableBefore = LoadRoom(context);
        var candidate = new RoomState(1)
        {
            EpisodeId = "episode-candidate",
            State = RoomStates.Prestaging,
            PrestageStartedAt = new DateTimeOffset(2026, 7, 12, 14, 0, 0, TimeSpan.Zero)
        };
        var candidateBefore = RoomSnapshot.From(candidate);
        var expectation = ActiveRoomWriteExpectation.FromRoom(durableBefore);
        expectation = mismatch switch
        {
            "episode" => expectation with { EpisodeId = "wrong-episode" },
            "state" => expectation with { State = RoomStates.Seated },
            _ => throw new InvalidOperationException($"Unexpected mismatch '{mismatch}'.")
        };

        var result = context.Repository.SaveCanonicalAssignment(
            candidate,
            CompletePersistedAssignment(),
            expectation,
            context.Doctors,
            context.Procedures);

        Assert.Null(result);
        Assert.Equal(candidateBefore, RoomSnapshot.From(candidate));
        Assert.Equal(RoomSnapshot.From(durableBefore), RoomSnapshot.From(LoadRoom(context)));
    }

    [Fact]
    public void Canonical_assignment_compare_and_swap_rejects_expected_null_active_handoff()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var handoff = context.Repository.CreateReadyHandoff(
            ReadyRoom(readyAt),
            CompleteAssignment(),
            readyAt,
            context.Doctors,
            context.Procedures);
        var durableBefore = LoadRoom(context);
        var candidate = CopyRoom(durableBefore);
        var candidateBefore = RoomSnapshot.From(candidate);
        var expectation = ActiveRoomWriteExpectation.FromRoom(durableBefore) with
        {
            ActiveReadyHandoffId = null
        };

        var result = context.Repository.SaveCanonicalAssignment(
            candidate,
            CompletePersistedAssignment(),
            expectation,
            context.Doctors,
            context.Procedures);

        Assert.Null(result);
        Assert.Equal(candidateBefore, RoomSnapshot.From(candidate));
        Assert.Equal(RoomSnapshot.From(durableBefore), RoomSnapshot.From(LoadRoom(context)));
        Assert.Equal(ReadyHandoffStatus.Active, context.Repository.LoadReadyHandoff(handoff.HandoffId)?.ContractStatus);
    }

    [Fact]
    public void Canonical_assignment_database_abort_does_not_mutate_supplied_room()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));

        var durableBefore = LoadRoom(context);
        var candidate = CopyRoom(durableBefore);
        var candidateBefore = RoomSnapshot.From(candidate);
        var assignment = PersistedRoomAssignment.FromCanonicalContract(
            RoomAssignmentContract.Create(
                "pledger",
                "EXT",
                SedationContract.EligibleNo(),
                ExpectedAllocationContract.ConfirmedSuggestedValue(3)),
            doctorDisplayName: "Dr. Pledger",
            procedureCategory: "Extraction");

        using (var connection = OpenConnection(databasePath))
        {
            ExecuteSql(connection, """
                CREATE TRIGGER fail_room_1_canonical_assignment
                BEFORE UPDATE OF assigned_doctor_id, procedure_code ON active_rooms
                FOR EACH ROW
                WHEN OLD.room_id = 1
                    AND OLD.episode_id IS NEW.episode_id
                    AND OLD.state = NEW.state
                    AND OLD.active_ready_handoff_id IS NEW.active_ready_handoff_id
                    AND OLD.assigned_doctor_id = 'otte'
                    AND OLD.procedure_code = 'CON'
                    AND NEW.assigned_doctor_id = 'pledger'
                    AND NEW.procedure_code = 'EXT'
                BEGIN
                    SELECT RAISE(ABORT, 'injected canonical assignment failure');
                END;
                """);
        }

        SqliteException exception;
        try
        {
            exception = Assert.Throws<SqliteException>(() => context.Repository.SaveCanonicalAssignment(
                candidate,
                assignment,
                ActiveRoomWriteExpectation.FromRoom(durableBefore),
                context.Doctors,
                context.Procedures));
        }
        finally
        {
            using var connection = OpenConnection(databasePath);
            ExecuteSql(connection, "DROP TRIGGER IF EXISTS fail_room_1_canonical_assignment;");
        }

        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Contains("injected canonical assignment failure", exception.Message, StringComparison.Ordinal);
        Assert.Equal(candidateBefore, RoomSnapshot.From(candidate));
        Assert.Equal(RoomSnapshot.From(durableBefore), RoomSnapshot.From(LoadRoom(context)));

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);
        Assert.Equal(RoomSnapshot.From(durableBefore), RoomSnapshot.From(LoadRoom(reloaded)));
    }

    [Fact]
    public void Ready_handoff_creation_persists_active_reference_and_complete_snapshot()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var room = ReadyRoom(readyAt);
        var originalRoom = RoomSnapshot.From(room);

        var handoff = context.Repository.CreateReadyHandoff(
            room,
            CompleteAssignment(),
            readyAt,
            context.Doctors,
            context.Procedures);

        Assert.Equal(originalRoom, RoomSnapshot.From(room));
        Assert.False(string.IsNullOrWhiteSpace(handoff.HandoffId));
        Assert.Equal(handoff.HandoffId.ToLowerInvariant(), handoff.HandoffId);

        var loadedRoom = context.Repository.LoadRooms(3).Single(item => item.RoomId == 1);
        Assert.Equal(handoff.HandoffId, loadedRoom.ActiveReadyHandoffId);
        Assert.Null(loadedRoom.AcceptedReadyHandoffId);

        var loadedHandoff = Assert.Single(context.Repository.LoadReadyHandoffsByEpisode("episode-ready"));
        Assert.Equal(ReadyHandoffStatus.Active, loadedHandoff.ContractStatus);
        Assert.Equal(AssignmentCompleteness.Complete, loadedHandoff.ToContract().Assignment.Completeness);
        Assert.Equal(SedationState.UnavailableProcedureIneligible, loadedHandoff.Assignment.SedationState);
        Assert.Equal(ExpectedAllocationState.ConfirmedSuggestedValue, loadedHandoff.Assignment.ExpectedAllocationState);
    }

    [Fact]
    public void Ready_handoff_rejects_incomplete_assignment_without_persisting_side_effects()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var room = ReadyRoom(new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero));
        var partial = RoomAssignmentContract.Create(
            "otte",
            "EXT",
            SedationContract.EligibleUnresolved(),
            ExpectedAllocationContract.Suggested(4));

        Assert.Throws<ArgumentException>(() => context.Repository.CreateReadyHandoff(
            room,
            partial,
            room.ReadyForDoctorAt!.Value,
            context.Doctors,
            context.Procedures));

        Assert.Empty(context.Repository.LoadReadyHandoffsByEpisode("episode-ready"));
        Assert.Null(context.Repository.LoadRooms(3).Single(item => item.RoomId == 1).ActiveReadyHandoffId);
    }

    [Fact]
    public void Withdrawn_handoff_allows_reissued_active_handoff_and_preserves_history()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var room = ReadyRoom(readyAt);
        var first = context.Repository.CreateReadyHandoff(room, CompleteAssignment(), readyAt, context.Doctors, context.Procedures);

        var activeRoom = LoadRoom(context);
        activeRoom.State = RoomStates.Seated;
        context.Repository.WithdrawReadyHandoff(activeRoom, first.HandoffId, readyAt.AddMinutes(2), context.Doctors, context.Procedures);

        var seatedRoom = LoadRoom(context);
        seatedRoom.State = RoomStates.ReadyForDoctor;
        var second = context.Repository.CreateReadyHandoff(seatedRoom, CompleteAssignment(), readyAt.AddMinutes(4), context.Doctors, context.Procedures);

        var history = context.Repository.LoadReadyHandoffsByEpisode("episode-ready");
        Assert.Equal(2, history.Count);
        Assert.Contains(history, handoff => handoff.HandoffId == first.HandoffId && handoff.ContractStatus == ReadyHandoffStatus.Withdrawn);
        Assert.Contains(history, handoff => handoff.HandoffId == second.HandoffId && handoff.ContractStatus == ReadyHandoffStatus.Active);
    }

    [Fact]
    public void Accepted_handoff_updates_room_and_completed_cycle_atomically()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var room = ReadyRoom(readyAt);
        var handoff = context.Repository.CreateReadyHandoff(room, CompleteAssignment(), readyAt, context.Doctors, context.Procedures);
        var acceptedAt = readyAt.AddMinutes(5);
        var activeRoom = LoadRoom(context);
        activeRoom.State = RoomStates.DoctorInRoom;
        activeRoom.DoctorArrivedAt = acceptedAt;
        var cycle = CompletedCycle(activeRoom, acceptedAt);

        context.Repository.AcceptReadyHandoffAndSaveCycle(activeRoom, cycle, handoff.HandoffId, acceptedAt, context.Doctors, context.Procedures);

        var loadedHandoff = context.Repository.LoadReadyHandoff(handoff.HandoffId)!;
        Assert.Equal(ReadyHandoffStatus.Accepted, loadedHandoff.ContractStatus);
        Assert.Equal(acceptedAt, loadedHandoff.AcceptedAt);

        var loadedRoom = context.Repository.LoadRooms(3).Single(item => item.RoomId == 1);
        Assert.Null(loadedRoom.ActiveReadyHandoffId);
        Assert.Equal(handoff.HandoffId, loadedRoom.AcceptedReadyHandoffId);

        var loadedCycle = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.Equal(handoff.HandoffId, loadedCycle.AcceptedReadyHandoffId);
    }

    [Fact]
    public void Canceled_ready_handoff_termination_is_distinct_from_withdrawal()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var room = ReadyRoom(readyAt);
        var handoff = context.Repository.CreateReadyHandoff(room, CompleteAssignment(), readyAt, context.Doctors, context.Procedures);
        var activeRoom = LoadRoom(context);
        var aborted = new AbortedRoomAssignment
        {
            EpisodeId = "episode-ready",
            RoomId = 1,
            AssignedDoctor = "otte",
            ProcedureCode = "CON",
            SedationState = SedationState.UnavailableProcedureIneligible,
            ExpectedAllocationState = ExpectedAllocationState.ConfirmedSuggestedValue,
            ExpectedAllocationSuggestedUnits = 3,
            ExpectedAllocationConfirmedUnits = 3,
            PrestageStartedAt = room.PrestageStartedAt,
            SeatedAt = room.SeatedAt,
            ReadyForDoctorAt = readyAt,
            TerminatedAt = readyAt.AddMinutes(3),
            TerminatedFromState = RoomStates.ReadyForDoctor,
            TerminationKind = TerminationKinds.StaffCanceled,
            CancellationReason = CancellationReasons.PatientCanceled
        };

        context.Repository.TerminateReadyHandoffAndIncompleteAssignment(
            aborted,
            activeRoom,
            handoff.HandoffId,
            aborted.TerminatedAt,
            ReadyHandoffTerminationKinds.Canceled,
            context.Doctors,
            context.Procedures);

        var loadedHandoff = context.Repository.LoadReadyHandoff(handoff.HandoffId)!;
        Assert.Null(loadedHandoff.ContractStatus);
        Assert.Null(loadedHandoff.WithdrawnAt);
        Assert.Null(loadedHandoff.AcceptedAt);
        Assert.Equal(ReadyHandoffTerminationKinds.Canceled, loadedHandoff.TerminationKind);
        Assert.Equal(handoff.HandoffId, Assert.Single(context.Repository.LoadAbortedAssignments()).TerminalReadyHandoffId);
    }

    [Fact]
    public void Expired_ready_handoff_termination_is_distinct_from_withdrawal()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var room = ReadyRoom(readyAt);
        var handoff = context.Repository.CreateReadyHandoff(room, CompleteAssignment(), readyAt, context.Doctors, context.Procedures);
        var activeRoom = LoadRoom(context);
        var aborted = AbortedAssignment(activeRoom, readyAt.AddMinutes(3));
        aborted.TerminationKind = TerminationKinds.AfterHoursExpired;
        aborted.CancellationReason = null;

        context.Repository.TerminateReadyHandoffAndIncompleteAssignment(
            aborted,
            activeRoom,
            handoff.HandoffId,
            aborted.TerminatedAt,
            ReadyHandoffTerminationKinds.Expired,
            context.Doctors,
            context.Procedures);

        var loadedHandoff = context.Repository.LoadReadyHandoff(handoff.HandoffId)!;
        Assert.Null(loadedHandoff.ContractStatus);
        Assert.Null(loadedHandoff.WithdrawnAt);
        Assert.Null(loadedHandoff.AcceptedAt);
        Assert.Equal(ReadyHandoffTerminationKinds.Expired, loadedHandoff.TerminationKind);
        Assert.Equal(handoff.HandoffId, Assert.Single(context.Repository.LoadAbortedAssignments()).TerminalReadyHandoffId);
    }

    [Fact]
    public void Failed_acceptance_rolls_back_handoff_outcome()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var room = ReadyRoom(readyAt);
        var handoff = context.Repository.CreateReadyHandoff(room, CompleteAssignment(), readyAt, context.Doctors, context.Procedures);
        var activeRoom = LoadRoom(context);
        activeRoom.State = RoomStates.DoctorInRoom;
        activeRoom.DoctorArrivedAt = readyAt.AddMinutes(5);
        var invalidCycle = CompletedCycle(activeRoom, readyAt.AddMinutes(5));
        invalidCycle.FinalWaitState = null!;

        Assert.ThrowsAny<Exception>(() => context.Repository.AcceptReadyHandoffAndSaveCycle(
            activeRoom,
            invalidCycle,
            handoff.HandoffId,
            readyAt.AddMinutes(5),
            context.Doctors,
            context.Procedures));

        var loadedHandoff = context.Repository.LoadReadyHandoff(handoff.HandoffId)!;
        Assert.Equal(ReadyHandoffStatus.Active, loadedHandoff.ContractStatus);
        Assert.Null(loadedHandoff.AcceptedAt);
        Assert.Empty(context.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Database_constraints_reject_second_active_and_second_accepted_handoff_for_episode()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var room = ReadyRoom(readyAt);
        var handoff = context.Repository.CreateReadyHandoff(room, CompleteAssignment(), readyAt, context.Doctors, context.Procedures);

        Assert.ThrowsAny<Exception>(() => context.Repository.CreateReadyHandoff(
            room,
            CompleteAssignment(),
            readyAt.AddMinutes(1),
            context.Doctors,
            context.Procedures));

        var activeRoom = LoadRoom(context);
        activeRoom.State = RoomStates.DoctorInRoom;
        var acceptedAt = readyAt.AddMinutes(5);
        context.Repository.AcceptReadyHandoffAndSaveCycle(
            activeRoom,
            CompletedCycle(activeRoom, acceptedAt),
            handoff.HandoffId,
            acceptedAt,
            context.Doctors,
            context.Procedures);

        using var connection = OpenConnection(context.DatabasePath);
        Assert.Throws<SqliteException>(() => ExecuteSql(connection, $"""
            INSERT INTO ready_handoffs (
                handoff_id, episode_id, room_id, ready_at, accepted_at, doctor_id,
                procedure_code, sedation_state, expected_allocation_state,
                expected_allocation_suggested_units, expected_allocation_confirmed_units)
            VALUES (
                'manual-accepted', 'episode-ready', 1, '{FormatDateTimeOffset(readyAt.AddMinutes(6))}',
                '{FormatDateTimeOffset(readyAt.AddMinutes(8))}', 'otte', 'CON',
                'UnavailableProcedureIneligible', 'ConfirmedSuggestedValue', 3, 3);
            """));
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("accept")]
    [InlineData("terminate")]
    public void Ready_handoff_operations_reject_wrong_episode(string operation)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var handoff = context.Repository.CreateReadyHandoff(ReadyRoom(readyAt), CompleteAssignment(), readyAt, context.Doctors, context.Procedures);
        var wrongRoom = CopyRoom(LoadRoom(context));
        wrongRoom.EpisodeId = "wrong-episode";

        AssertOperationFailsWithoutChanges(context, operation, wrongRoom, handoff.HandoffId, readyAt.AddMinutes(3));
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("accept")]
    [InlineData("terminate")]
    public void Ready_handoff_operations_reject_wrong_room(string operation)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var handoff = context.Repository.CreateReadyHandoff(ReadyRoom(readyAt), CompleteAssignment(), readyAt, context.Doctors, context.Procedures);
        var wrongRoom = ReadyRoom(readyAt, roomId: 2);
        wrongRoom.ActiveReadyHandoffId = handoff.HandoffId;

        AssertOperationFailsWithoutChanges(context, operation, wrongRoom, handoff.HandoffId, readyAt.AddMinutes(3));
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("accept")]
    [InlineData("terminate")]
    public void Ready_handoff_operations_reject_stale_handoff_after_reissued_ready(string operation)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var first = context.Repository.CreateReadyHandoff(ReadyRoom(readyAt), CompleteAssignment(), readyAt, context.Doctors, context.Procedures);
        var activeRoom = LoadRoom(context);
        activeRoom.State = RoomStates.Seated;
        context.Repository.WithdrawReadyHandoff(activeRoom, first.HandoffId, readyAt.AddMinutes(2), context.Doctors, context.Procedures);
        var seatedRoom = LoadRoom(context);
        var second = context.Repository.CreateReadyHandoff(seatedRoom, CompleteAssignment(), readyAt.AddMinutes(4), context.Doctors, context.Procedures);
        Assert.NotEqual(first.HandoffId, second.HandoffId);

        AssertOperationFailsWithoutChanges(context, operation, LoadRoom(context), first.HandoffId, readyAt.AddMinutes(6));
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("accept")]
    [InlineData("terminate")]
    public void Ready_handoff_operations_reject_missing_active_room_reference(string operation)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var handoff = context.Repository.CreateReadyHandoff(ReadyRoom(readyAt), CompleteAssignment(), readyAt, context.Doctors, context.Procedures);
        var originalActiveRoom = LoadRoom(context);
        using var connection = OpenConnection(context.DatabasePath);
        ExecuteSql(connection, "UPDATE active_rooms SET active_ready_handoff_id = NULL WHERE room_id = 1;");

        AssertOperationFailsWithoutChanges(context, operation, originalActiveRoom, handoff.HandoffId, readyAt.AddMinutes(3));
    }

    [Theory]
    [InlineData("withdraw")]
    [InlineData("accept")]
    [InlineData("terminate")]
    public void Ready_handoff_operations_reject_changed_active_room_reference(string operation)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var handoff = context.Repository.CreateReadyHandoff(ReadyRoom(readyAt), CompleteAssignment(), readyAt, context.Doctors, context.Procedures);
        var originalActiveRoom = LoadRoom(context);
        using var connection = OpenConnection(context.DatabasePath);
        ExecuteSql(connection, "UPDATE active_rooms SET active_ready_handoff_id = 'different-handoff' WHERE room_id = 1;");

        AssertOperationFailsWithoutChanges(context, operation, originalActiveRoom, handoff.HandoffId, readyAt.AddMinutes(3));
    }

    [Fact]
    public void Accepted_handoff_derives_completed_cycle_assignment_from_immutable_snapshot()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var handoff = context.Repository.CreateReadyHandoff(ReadyRoom(readyAt), CompleteAssignment(), readyAt, context.Doctors, context.Procedures);
        var acceptedAt = readyAt.AddMinutes(5);
        var mismatchedRoom = LoadRoom(context);
        mismatchedRoom.State = RoomStates.DoctorInRoom;
        mismatchedRoom.DoctorArrivedAt = acceptedAt;
        mismatchedRoom.AssignedDoctor = "pledger";
        mismatchedRoom.ProcedureCode = "EXT";
        mismatchedRoom.SedationState = SedationState.EligibleYes;
        mismatchedRoom.ExpectedAllocationState = ExpectedAllocationState.ConfirmedAdjustedValue;
        mismatchedRoom.ExpectedAllocationSuggestedUnits = 4;
        mismatchedRoom.ExpectedAllocationConfirmedUnits = 6;
        var mismatchedCycle = CompletedCycle(mismatchedRoom, acceptedAt);
        mismatchedCycle.AssignedDoctor = "pledger";
        mismatchedCycle.ProcedureCode = "EXT";
        mismatchedCycle.OriginalDefaultExpectedUnits = 4;
        mismatchedCycle.ExpectedAllocationUnits = 6;
        mismatchedCycle.ExpectedAllocationMinutes = 60;
        mismatchedCycle.AllocationAdjustedFromDefault = true;

        context.Repository.AcceptReadyHandoffAndSaveCycle(mismatchedRoom, mismatchedCycle, handoff.HandoffId, acceptedAt, context.Doctors, context.Procedures);

        var loadedCycle = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.Equal("otte", loadedCycle.AssignedDoctor);
        Assert.Equal("CON", loadedCycle.ProcedureCode);
        Assert.Equal(3, loadedCycle.OriginalDefaultExpectedUnits);
        Assert.Equal(3, loadedCycle.ExpectedAllocationUnits);
        Assert.Equal(30, loadedCycle.ExpectedAllocationMinutes);
        Assert.False(loadedCycle.AllocationAdjustedFromDefault);
        Assert.Equal(handoff.HandoffId, loadedCycle.AcceptedReadyHandoffId);

        var loadedRoom = LoadRoom(context);
        Assert.Equal("otte", loadedRoom.AssignedDoctor);
        Assert.Equal("CON", loadedRoom.ProcedureCode);
        Assert.Equal(SedationState.UnavailableProcedureIneligible, loadedRoom.SedationState);
        Assert.Equal(ExpectedAllocationState.ConfirmedSuggestedValue, loadedRoom.ExpectedAllocationState);
        Assert.Equal(3, loadedRoom.ExpectedAllocationConfirmedUnits);
    }

    [Theory]
    [MemberData(nameof(AmbiguousCanonicalAssignments))]
    public void New_canonical_assignment_writes_reject_legacy_ambiguous_state(PersistedRoomAssignment assignment)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var room = ReadyRoom(new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero));

        Assert.Throws<InvalidOperationException>(() =>
            context.Repository.SaveCanonicalAssignment(
                room,
                assignment,
                ActiveRoomWriteExpectation.FromRoom(LoadRoom(context)),
                context.Doctors,
                context.Procedures));

        var loaded = LoadRoom(context);
        Assert.Null(loaded.SedationState);
        Assert.Null(loaded.ExpectedAllocationState);
    }

    [Fact]
    public void Canonical_absent_assignment_is_a_strict_new_write()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var room = new RoomState(1)
        {
            EpisodeId = "episode-absent",
            State = RoomStates.Prestaging,
            PrestageStartedAt = new DateTimeOffset(2026, 7, 12, 14, 0, 0, TimeSpan.Zero)
        };
        var absent = PersistedRoomAssignment.FromCanonicalContract(RoomAssignmentContract.Create(
            null,
            null,
            SedationContract.UnavailableNoProcedure(),
            ExpectedAllocationContract.Unknown()));

        context.Repository.SaveCanonicalAssignment(
            room,
            absent,
            ActiveRoomWriteExpectation.FromRoom(LoadRoom(context)),
            context.Doctors,
            context.Procedures);

        var loaded = LoadRoom(context);
        Assert.Null(loaded.AssignedDoctor);
        Assert.Null(loaded.ProcedureCode);
        Assert.Equal(SedationState.UnavailableNoProcedure, loaded.SedationState);
        Assert.Equal(ExpectedAllocationState.Unknown, loaded.ExpectedAllocationState);
    }

    [Fact]
    public void Create_ready_handoff_does_not_mutate_caller_on_duplicate_active_failure()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        context.Repository.CreateReadyHandoff(ReadyRoom(readyAt), CompleteAssignment(), readyAt, context.Doctors, context.Procedures);
        var duplicateRoom = ReadyRoom(readyAt);
        var before = RoomSnapshot.From(duplicateRoom);

        Assert.ThrowsAny<Exception>(() => context.Repository.CreateReadyHandoff(
            duplicateRoom,
            CompleteAssignment(),
            readyAt.AddMinutes(1),
            context.Doctors,
            context.Procedures));

        Assert.Equal(before, RoomSnapshot.From(duplicateRoom));
        Assert.Single(context.Repository.LoadReadyHandoffsByEpisode("episode-ready"));
    }

    [Fact]
    public void Create_ready_handoff_does_not_mutate_caller_when_room_save_fails()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var invalidRoom = ReadyRoom(readyAt, episodeId: "episode-invalid-room");
        invalidRoom.State = null!;
        var before = RoomSnapshot.From(invalidRoom);

        Assert.ThrowsAny<Exception>(() => context.Repository.CreateReadyHandoff(
            invalidRoom,
            CompleteAssignment(),
            readyAt,
            context.Doctors,
            context.Procedures));

        Assert.Equal(before, RoomSnapshot.From(invalidRoom));
        Assert.Empty(context.Repository.LoadReadyHandoffsByEpisode("episode-invalid-room"));
    }

    [Fact]
    public void Failed_withdrawal_rolls_back_room_and_handoff()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var handoff = context.Repository.CreateReadyHandoff(ReadyRoom(readyAt), CompleteAssignment(), readyAt, context.Doctors, context.Procedures);
        var invalidRoom = LoadRoom(context);
        invalidRoom.State = null!;

        AssertOperationFailsWithoutChanges(context, "withdraw", invalidRoom, handoff.HandoffId, readyAt.AddMinutes(3));
    }

    [Theory]
    [InlineData(ReadyHandoffTerminationKinds.Canceled)]
    [InlineData(ReadyHandoffTerminationKinds.Expired)]
    public void Failed_ready_termination_rolls_back_room_handoff_and_aborted_history(string readyHandoffTerminationKind)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var readyAt = new DateTimeOffset(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);
        var handoff = context.Repository.CreateReadyHandoff(ReadyRoom(readyAt), CompleteAssignment(), readyAt, context.Doctors, context.Procedures);
        var activeRoom = LoadRoom(context);
        var aborted = AbortedAssignment(activeRoom, readyAt.AddMinutes(4));
        aborted.TerminationKind = null!;

        var beforeHandoff = HandoffSnapshot.From(context.Repository.LoadReadyHandoff(handoff.HandoffId)!);
        var beforeRoom = RoomSnapshot.From(LoadRoom(context));
        Assert.ThrowsAny<Exception>(() => context.Repository.TerminateReadyHandoffAndIncompleteAssignment(
            aborted,
            activeRoom,
            handoff.HandoffId,
            aborted.TerminatedAt,
            readyHandoffTerminationKind,
            context.Doctors,
            context.Procedures));

        Assert.Equal(beforeHandoff, HandoffSnapshot.From(context.Repository.LoadReadyHandoff(handoff.HandoffId)!));
        Assert.Equal(beforeRoom, RoomSnapshot.From(LoadRoom(context)));
        Assert.Empty(context.Repository.LoadAbortedAssignments());
    }

    public static IEnumerable<object[]> AmbiguousCanonicalAssignments()
    {
        yield return [new PersistedRoomAssignment(null, null, null, null, null, null, null, null)];
        yield return [new PersistedRoomAssignment(null, null, null, null, null, ExpectedAllocationState.Unknown, null, null)];
        yield return [new PersistedRoomAssignment(null, null, null, null, SedationState.UnavailableNoProcedure, null, null, null)];
    }

    public static IEnumerable<object[]> CanonicalAssignments()
    {
        yield return [PersistedRoomAssignment.FromContract(RoomAssignmentContract.Create(
            null,
            null,
            SedationContract.UnavailableNoProcedure(),
            ExpectedAllocationContract.Unknown()))];

        yield return [PersistedRoomAssignment.FromContract(RoomAssignmentContract.Create(
            "otte",
            null,
            SedationContract.UnavailableNoProcedure(),
            ExpectedAllocationContract.Unknown()),
            doctorDisplayName: "Dr. Otte")];

        yield return [PersistedRoomAssignment.FromContract(RoomAssignmentContract.Create(
            null,
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.Suggested(3)),
            procedureCategory: "Consult")];

        yield return [PersistedRoomAssignment.FromContract(RoomAssignmentContract.Create(
            "otte",
            "EXT",
            SedationContract.EligibleUnresolved(),
            ExpectedAllocationContract.Suggested(4)))];

        yield return [PersistedRoomAssignment.FromContract(RoomAssignmentContract.Create(
            "otte",
            "EXT",
            SedationContract.EligibleYes(),
            ExpectedAllocationContract.ConfirmedAdjustedValue(4, 5)))];

        yield return [PersistedRoomAssignment.FromContract(RoomAssignmentContract.Create(
            "otte",
            "EXT",
            SedationContract.EligibleNo(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(4)))];

        yield return [PersistedRoomAssignment.FromContract(RoomAssignmentContract.Create(
            "otte",
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.ConfirmedAdjustedValue(suggestedValue: null, confirmedValue: 3)))];
    }

    private static RoomState ReadyRoom(
        DateTimeOffset readyAt,
        int roomId = 1,
        string episodeId = "episode-ready") =>
        new(roomId)
        {
            EpisodeId = episodeId,
            AssignedDoctor = "otte",
            ProcedureCode = "CON",
            State = RoomStates.ReadyForDoctor,
            PrestageStartedAt = readyAt.AddMinutes(-12),
            SeatedAt = readyAt.AddMinutes(-8),
            ReadyForDoctorAt = readyAt
        };

    private static RoomAssignmentContract CompleteAssignment() =>
        RoomAssignmentContract.Create(
            "otte",
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(3));

    private static PersistedRoomAssignment CompletePersistedAssignment() =>
        PersistedRoomAssignment.FromCanonicalContract(CompleteAssignment());

    private static CompletedRoomCycle CompletedCycle(RoomState room, DateTimeOffset doctorArrivedAt) =>
        new()
        {
            EpisodeId = room.EpisodeId,
            RoomId = room.RoomId,
            AssignedDoctor = room.AssignedDoctor!,
            ProcedureCode = room.ProcedureCode!,
            PrestageStartedAt = room.PrestageStartedAt,
            SeatedAt = room.SeatedAt!.Value,
            ReadyForDoctorAt = room.ReadyForDoctorAt,
            DoctorArrivedAt = doctorArrivedAt,
            SeatedToDoctorSeconds = 480,
            PrepSeconds = 480,
            ReadyToDoctorSeconds = 300,
            FinalWaitState = RoomStates.ReadyForDoctor,
            OriginalDefaultExpectedUnits = 3,
            ExpectedAllocationUnits = 3,
            ExpectedAllocationMinutes = 30
        };

    private static AbortedRoomAssignment AbortedAssignment(RoomState room, DateTimeOffset terminatedAt) =>
        new()
        {
            EpisodeId = room.EpisodeId!,
            RoomId = room.RoomId,
            AssignedDoctor = room.AssignedDoctor,
            ProcedureCode = room.ProcedureCode,
            SedationState = room.SedationState,
            ExpectedAllocationState = room.ExpectedAllocationState,
            ExpectedAllocationSuggestedUnits = room.ExpectedAllocationSuggestedUnits,
            ExpectedAllocationConfirmedUnits = room.ExpectedAllocationConfirmedUnits,
            OriginalDefaultExpectedUnits = room.OriginalDefaultExpectedUnits,
            ExpectedAllocationUnits = room.ExpectedAllocationUnits,
            ExpectedAllocationMinutes = room.ExpectedAllocationMinutes,
            AllocationAdjustedFromDefault = room.AllocationAdjustedFromDefault,
            PrestageStartedAt = room.PrestageStartedAt,
            SeatedAt = room.SeatedAt,
            ReadyForDoctorAt = room.ReadyForDoctorAt,
            TerminatedAt = terminatedAt,
            TerminatedFromState = room.State,
            TerminationKind = TerminationKinds.StaffCanceled,
            CancellationReason = CancellationReasons.PatientCanceled
        };

    private static RoomState LoadRoom(StoreContext context, int roomId = 1) =>
        context.Repository.LoadRooms(3).Single(item => item.RoomId == roomId);

    private static RoomState CopyRoom(RoomState room) =>
        new(room.RoomId)
        {
            EpisodeId = room.EpisodeId,
            AssignedDoctor = room.AssignedDoctor,
            AssignedDoctorDisplayName = room.AssignedDoctorDisplayName,
            ProcedureCode = room.ProcedureCode,
            ProcedureCategory = room.ProcedureCategory,
            SedationState = room.SedationState,
            ExpectedAllocationState = room.ExpectedAllocationState,
            ExpectedAllocationSuggestedUnits = room.ExpectedAllocationSuggestedUnits,
            ExpectedAllocationConfirmedUnits = room.ExpectedAllocationConfirmedUnits,
            ActiveReadyHandoffId = room.ActiveReadyHandoffId,
            AcceptedReadyHandoffId = room.AcceptedReadyHandoffId,
            State = room.State,
            PrestageStartedAt = room.PrestageStartedAt,
            SeatedAt = room.SeatedAt,
            AgingStartedAt = room.AgingStartedAt,
            StaleStartedAt = room.StaleStartedAt,
            ReadyForDoctorAt = room.ReadyForDoctorAt,
            DoctorArrivedAt = room.DoctorArrivedAt,
            DoctorCompleteAt = room.DoctorCompleteAt,
            RoomAvailableAt = room.RoomAvailableAt,
            OriginalDefaultExpectedUnits = room.OriginalDefaultExpectedUnits,
            ExpectedAllocationUnits = room.ExpectedAllocationUnits,
            ExpectedAllocationMinutes = room.ExpectedAllocationMinutes,
            AllocationAdjustedFromDefault = room.AllocationAdjustedFromDefault
        };

    private static void AssertOperationFailsWithoutChanges(
        StoreContext context,
        string operation,
        RoomState operationRoom,
        string handoffId,
        DateTimeOffset operationAt)
    {
        var beforeHandoff = HandoffSnapshot.From(context.Repository.LoadReadyHandoff(handoffId)!);
        var beforeRoom = RoomSnapshot.From(LoadRoom(context, beforeHandoff.RoomId));
        var completedCount = CountRows(context, "completed_room_cycles");
        var abortedCount = CountRows(context, "aborted_room_assignments");

        Assert.ThrowsAny<Exception>(() => InvokeReadyHandoffOperation(
            context,
            operation,
            operationRoom,
            handoffId,
            operationAt));

        Assert.Equal(beforeHandoff, HandoffSnapshot.From(context.Repository.LoadReadyHandoff(handoffId)!));
        Assert.Equal(beforeRoom, RoomSnapshot.From(LoadRoom(context, beforeHandoff.RoomId)));
        Assert.Equal(completedCount, CountRows(context, "completed_room_cycles"));
        Assert.Equal(abortedCount, CountRows(context, "aborted_room_assignments"));
    }

    private static void InvokeReadyHandoffOperation(
        StoreContext context,
        string operation,
        RoomState operationRoom,
        string handoffId,
        DateTimeOffset operationAt)
    {
        switch (operation)
        {
            case "withdraw":
                context.Repository.WithdrawReadyHandoff(
                    operationRoom,
                    handoffId,
                    operationAt,
                    context.Doctors,
                    context.Procedures);
                break;
            case "accept":
                operationRoom.DoctorArrivedAt ??= operationAt;
                context.Repository.AcceptReadyHandoffAndSaveCycle(
                    operationRoom,
                    CompletedCycle(operationRoom, operationAt),
                    handoffId,
                    operationAt,
                    context.Doctors,
                    context.Procedures);
                break;
            case "terminate":
                context.Repository.TerminateReadyHandoffAndIncompleteAssignment(
                    AbortedAssignment(operationRoom, operationAt),
                    operationRoom,
                    handoffId,
                    operationAt,
                    ReadyHandoffTerminationKinds.Canceled,
                    context.Doctors,
                    context.Procedures);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown test operation.");
        }
    }

    private static long CountRows(StoreContext context, string tableName)
    {
        using var connection = OpenConnection(context.DatabasePath);
        return ExecuteScalar<long>(connection, $"SELECT COUNT(*) FROM {tableName};");
    }

    private sealed record RoomSnapshot(
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
        DateTimeOffset? AgingStartedAt,
        DateTimeOffset? StaleStartedAt,
        DateTimeOffset? ReadyForDoctorAt,
        DateTimeOffset? DoctorArrivedAt,
        DateTimeOffset? DoctorCompleteAt,
        DateTimeOffset? RoomAvailableAt,
        int OriginalDefaultExpectedUnits,
        int ExpectedAllocationUnits,
        int ExpectedAllocationMinutes,
        bool AllocationAdjustedFromDefault)
    {
        public static RoomSnapshot From(RoomState room) =>
            new(
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
                room.AgingStartedAt,
                room.StaleStartedAt,
                room.ReadyForDoctorAt,
                room.DoctorArrivedAt,
                room.DoctorCompleteAt,
                room.RoomAvailableAt,
                room.OriginalDefaultExpectedUnits,
                room.ExpectedAllocationUnits,
                room.ExpectedAllocationMinutes,
                room.AllocationAdjustedFromDefault);
    }

    private sealed record HandoffSnapshot(
        string HandoffId,
        string EpisodeId,
        int RoomId,
        DateTimeOffset ReadyAt,
        DateTimeOffset? WithdrawnAt,
        DateTimeOffset? AcceptedAt,
        DateTimeOffset? TerminatedAt,
        string? TerminationKind,
        PersistedRoomAssignment Assignment)
    {
        public static HandoffSnapshot From(PersistedReadyHandoff handoff) =>
            new(
                handoff.HandoffId,
                handoff.EpisodeId,
                handoff.RoomId,
                handoff.ReadyAt,
                handoff.WithdrawnAt,
                handoff.AcceptedAt,
                handoff.TerminatedAt,
                handoff.TerminationKind,
                handoff.Assignment);
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

    private static HashSet<string> GetColumnNames(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static HashSet<string> GetTableNames(SqliteConnection connection) =>
        GetSchemaNames(connection, "table");

    private static HashSet<string> GetIndexNames(SqliteConnection connection) =>
        GetSchemaNames(connection, "index");

    private static HashSet<string> GetSchemaNames(SqliteConnection connection, string type)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type;";
        command.Parameters.AddWithValue("$type", type);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static void ExecuteSql(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T ExecuteScalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static string FormatDateTimeOffset(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O");
}
