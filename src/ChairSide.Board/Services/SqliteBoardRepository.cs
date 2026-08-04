using ChairSide.Board.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Services;

public sealed class SqliteBoardRepository
{
    private readonly string _connectionString;
    private readonly string _databasePath;

    public SqliteBoardRepository(
        IOptions<BoardPersistenceOptions> options,
        IWebHostEnvironment environment,
        DeploymentEnvironment deploymentEnvironment,
        DatabaseIsolationPolicy databaseIsolationPolicy,
        DatabaseDeploymentIdentityPolicy databaseDeploymentIdentityPolicy)
        : this(
            options,
            environment,
            deploymentEnvironment,
            databaseIsolationPolicy,
            databaseDeploymentIdentityPolicy,
            DatabaseInitializationTestHooks.None)
    {
    }

    internal SqliteBoardRepository(
        IOptions<BoardPersistenceOptions> options,
        IWebHostEnvironment environment,
        DeploymentEnvironment deploymentEnvironment,
        DatabaseIsolationPolicy databaseIsolationPolicy,
        DatabaseDeploymentIdentityPolicy databaseDeploymentIdentityPolicy,
        DatabaseInitializationTestHooks initializationHooks)
    {
        var initialization = SqliteBoardSchema.Initialize(
            options,
            environment,
            deploymentEnvironment,
            databaseIsolationPolicy,
            databaseDeploymentIdentityPolicy,
            initializationHooks);
        _databasePath = initialization.DatabasePath;
        _connectionString = initialization.ConnectionString;
    }

    public string DatabasePath => _databasePath;

    public bool HasOperationalData()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                EXISTS(SELECT 1 FROM active_rooms LIMIT 1)
                OR EXISTS(SELECT 1 FROM completed_room_cycles LIMIT 1)
                OR EXISTS(SELECT 1 FROM ready_handoffs LIMIT 1)
                OR EXISTS(SELECT 1 FROM aborted_room_assignments LIMIT 1);
            """;
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public IReadOnlyList<RoomState> LoadRooms(int roomCount)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                room_id,
                assigned_doctor_id,
                assigned_doctor_display_name,
                procedure_code,
                procedure_category,
                state,
                seated_at,
                aging_started_at,
                stale_started_at,
                ready_for_doctor_at,
                doctor_arrived_at,
                doctor_complete_at,
                room_available_at,
                original_default_expected_units,
                expected_allocation_units,
                expected_allocation_minutes,
                allocation_adjusted_from_default,
                prestage_started_at,
                episode_id,
                sedation_state,
                expected_allocation_state,
                expected_allocation_suggested_units,
                expected_allocation_confirmed_units,
                active_ready_handoff_id,
                accepted_ready_handoff_id,
                is_add_on
            FROM active_rooms
            WHERE room_id BETWEEN 1 AND $roomCount
            ORDER BY room_id;
            """;
        command.Parameters.AddWithValue("$roomCount", roomCount);

        var rooms = new List<RoomState>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rooms.Add(new RoomState(reader.GetInt32(0))
            {
                AssignedDoctor = ReadNullableString(reader, 1),
                AssignedDoctorDisplayName = ReadNullableString(reader, 2),
                ProcedureCode = ReadNullableString(reader, 3),
                ProcedureCategory = ReadNullableString(reader, 4),
                State = reader.GetString(5),
                SeatedAt = ReadNullableDateTimeOffset(reader, 6),
                AgingStartedAt = ReadNullableDateTimeOffset(reader, 7),
                StaleStartedAt = ReadNullableDateTimeOffset(reader, 8),
                ReadyForDoctorAt = ReadNullableDateTimeOffset(reader, 9),
                DoctorArrivedAt = ReadNullableDateTimeOffset(reader, 10),
                DoctorCompleteAt = ReadNullableDateTimeOffset(reader, 11),
                RoomAvailableAt = ReadNullableDateTimeOffset(reader, 12),
                OriginalDefaultExpectedUnits = reader.GetInt32(13),
                ExpectedAllocationUnits = reader.GetInt32(14),
                ExpectedAllocationMinutes = reader.GetInt32(15),
                AllocationAdjustedFromDefault = reader.GetInt32(16) == 1,
                PrestageStartedAt = ReadNullableDateTimeOffset(reader, 17),
                EpisodeId = ReadNullableString(reader, 18),
                SedationState = ReadNullableEnum<SedationState>(reader, 19),
                ExpectedAllocationState = ReadNullableEnum<ExpectedAllocationState>(reader, 20),
                ExpectedAllocationSuggestedUnits = ReadNullableInt32(reader, 21),
                ExpectedAllocationConfirmedUnits = ReadNullableInt32(reader, 22),
                ActiveReadyHandoffId = ReadNullableString(reader, 23),
                AcceptedReadyHandoffId = ReadNullableString(reader, 24),
                IsAddOn = reader.GetInt32(25) == 1
            });
        }

        return rooms;
    }

    public void EnsureConfiguredRooms(int roomCount)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        for (var roomId = 1; roomId <= roomCount; roomId++)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO active_rooms (
                    room_id,
                    state,
                    updated_at
                )
                VALUES (
                    $roomId,
                    $state,
                    $updatedAt
                );
                """;
            command.Parameters.AddWithValue("$roomId", roomId);
            command.Parameters.AddWithValue("$state", RoomStates.Available);
            command.Parameters.AddWithValue("$updatedAt", FormatDateTimeOffset(DateTimeOffset.UtcNow));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public int ResetMaintenanceState(int roomCount)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        int completedCyclesCleared;
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = "SELECT COUNT(*) FROM completed_room_cycles;";
            completedCyclesCleared = Convert.ToInt32(countCommand.ExecuteScalar());
        }

        using (var resetCommand = connection.CreateCommand())
        {
            resetCommand.Transaction = transaction;
            resetCommand.CommandText = """
                DELETE FROM completed_room_cycles;

                DELETE FROM ready_handoffs
                WHERE withdrawn_at IS NULL
                    AND accepted_at IS NULL
                    AND terminated_at IS NULL;

                DELETE FROM active_rooms;
                """;
            resetCommand.ExecuteNonQuery();
        }

        var updatedAt = FormatDateTimeOffset(DateTimeOffset.UtcNow);
        for (var roomId = 1; roomId <= roomCount; roomId++)
        {
            using var roomCommand = connection.CreateCommand();
            roomCommand.Transaction = transaction;
            roomCommand.CommandText = """
                INSERT INTO active_rooms (
                    room_id,
                    state,
                    updated_at
                )
                VALUES (
                    $roomId,
                    $state,
                    $updatedAt
                );
                """;
            roomCommand.Parameters.AddWithValue("$roomId", roomId);
            roomCommand.Parameters.AddWithValue("$state", RoomStates.Available);
            roomCommand.Parameters.AddWithValue("$updatedAt", updatedAt);
            roomCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return completedCyclesCleared;
    }

    public void SaveRooms(IEnumerable<RoomState> rooms, IReadOnlyList<Doctor> doctors, IReadOnlyList<ProcedureCategory> procedures)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var room in rooms)
        {
            SaveRoom(connection, transaction, room, doctors, procedures);
        }

        transaction.Commit();
    }

    public void SaveRoom(RoomState room, IReadOnlyList<Doctor> doctors, IReadOnlyList<ProcedureCategory> procedures)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        SaveRoom(connection, transaction, room, doctors, procedures);
        transaction.Commit();
    }

    public IReadOnlyList<CompletedRoomCycle> LoadCompletedCycles()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                room_id,
                assigned_doctor_id,
                procedure_code,
                seated_at,
                doctor_arrived_at,
                doctor_complete_at,
                room_available_at,
                seated_to_doctor_seconds,
                doctor_in_room_seconds,
                turnover_seconds,
                total_room_cycle_seconds,
                final_wait_state,
                aging_threshold_reached,
                stale_threshold_reached,
                ready_for_doctor_at,
                prep_seconds,
                ready_to_doctor_seconds,
                is_exception,
                requires_review,
                exception_reason,
                review_status,
                suggested_action,
                reviewed_at,
                reviewed_by,
                original_default_expected_units,
                expected_allocation_units,
                expected_allocation_minutes,
                allocation_adjusted_from_default,
                prestage_started_at,
                episode_id,
                accepted_ready_handoff_id,
                is_add_on
            FROM completed_room_cycles
            ORDER BY doctor_arrived_at DESC;
            """;

        var cycles = new List<CompletedRoomCycle>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cycles.Add(new CompletedRoomCycle
            {
                CompletedCycleId = reader.GetInt64(0),
                RoomId = reader.GetInt32(1),
                AssignedDoctor = reader.GetString(2),
                ProcedureCode = reader.GetString(3),
                SeatedAt = ReadRequiredDateTimeOffset(reader, 4),
                DoctorArrivedAt = ReadNullableDateTimeOffset(reader, 5),
                DoctorCompleteAt = ReadNullableDateTimeOffset(reader, 6),
                RoomAvailableAt = ReadNullableDateTimeOffset(reader, 7),
                SeatedToDoctorSeconds = reader.GetInt32(8),
                DoctorInRoomSeconds = ReadNullableInt32(reader, 9),
                TurnoverSeconds = ReadNullableInt32(reader, 10),
                TotalRoomCycleSeconds = ReadNullableInt32(reader, 11),
                FinalWaitState = reader.GetString(12),
                AgingThresholdReached = reader.GetInt32(13) == 1,
                StaleThresholdReached = reader.GetInt32(14) == 1,
                ReadyForDoctorAt = ReadNullableDateTimeOffset(reader, 15),
                PrepSeconds = ReadNullableInt32(reader, 16),
                ReadyToDoctorSeconds = ReadNullableInt32(reader, 17),
                IsException = reader.GetInt32(18) == 1,
                RequiresReview = reader.GetInt32(19) == 1,
                ExceptionReason = ReadNullableString(reader, 20),
                ReviewStatus = ReadNullableString(reader, 21) ?? ReviewStatuses.PendingReview,
                SuggestedAction = ReadNullableString(reader, 22),
                ReviewedAt = ReadNullableDateTimeOffset(reader, 23),
                ReviewedBy = ReadNullableString(reader, 24),
                OriginalDefaultExpectedUnits = reader.GetInt32(25),
                ExpectedAllocationUnits = reader.GetInt32(26),
                ExpectedAllocationMinutes = reader.GetInt32(27),
                AllocationAdjustedFromDefault = reader.GetInt32(28) == 1,
                PrestageStartedAt = ReadNullableDateTimeOffset(reader, 29),
                EpisodeId = ReadNullableString(reader, 30),
                AcceptedReadyHandoffId = ReadNullableString(reader, 31),
                IsAddOn = reader.GetInt32(32) == 1
            });
        }

        return cycles;
    }

    public void SaveCompletedCycle(CompletedRoomCycle cycle, IReadOnlyList<Doctor> doctors, IReadOnlyList<ProcedureCategory> procedures)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        SaveCompletedCycle(connection, transaction, cycle, doctors, procedures);
        transaction.Commit();
    }

    // Transactional core of SaveCompletedCycle. Runs the completed-cycle upsert and the id-readback
    // on the caller's connection/transaction so it can be composed atomically with an active-room
    // save (see SaveCompletedCycleAndRoom). Does not open, commit, or roll back - the caller owns
    // the transaction.
    private void SaveCompletedCycle(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CompletedRoomCycle cycle,
        IReadOnlyList<Doctor> doctors,
        IReadOnlyList<ProcedureCategory> procedures)
    {
        var doctor = doctors.FirstOrDefault(item => item.Id == cycle.AssignedDoctor);
        var procedure = procedures.FirstOrDefault(item => item.Code == cycle.ProcedureCode || item.Id == cycle.ProcedureCode);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO completed_room_cycles (
                room_id,
                assigned_doctor_id,
                assigned_doctor_display_name,
                procedure_code,
                procedure_category,
                seated_at,
                ready_for_doctor_at,
                doctor_arrived_at,
                doctor_complete_at,
                room_available_at,
                seated_to_doctor_seconds,
                prep_seconds,
                ready_to_doctor_seconds,
                doctor_in_room_seconds,
                turnover_seconds,
                total_room_cycle_seconds,
                final_wait_state,
                aging_threshold_reached,
                stale_threshold_reached,
                is_exception,
                requires_review,
                exception_reason,
                review_status,
                suggested_action,
                reviewed_at,
                reviewed_by,
                original_default_expected_units,
                expected_allocation_units,
                expected_allocation_minutes,
                allocation_adjusted_from_default,
                prestage_started_at,
                episode_id,
                accepted_ready_handoff_id,
                is_add_on,
                created_at,
                updated_at
            )
            VALUES (
                $roomId,
                $assignedDoctorId,
                $assignedDoctorDisplayName,
                $procedureCode,
                $procedureCategory,
                $seatedAt,
                $readyForDoctorAt,
                $doctorArrivedAt,
                $doctorCompleteAt,
                $roomAvailableAt,
                $seatedToDoctorSeconds,
                $prepSeconds,
                $readyToDoctorSeconds,
                $doctorInRoomSeconds,
                $turnoverSeconds,
                $totalRoomCycleSeconds,
                $finalWaitState,
                $agingThresholdReached,
                $staleThresholdReached,
                $isException,
                $requiresReview,
                $exceptionReason,
                $reviewStatus,
                $suggestedAction,
                $reviewedAt,
                $reviewedBy,
                $originalDefaultExpectedUnits,
                $expectedAllocationUnits,
                $expectedAllocationMinutes,
                $allocationAdjustedFromDefault,
                $prestageStartedAt,
                $episodeId,
                $acceptedReadyHandoffId,
                $isAddOn,
                $now,
                $now
            )
            ON CONFLICT(room_id, seated_at) DO UPDATE SET
                assigned_doctor_id = excluded.assigned_doctor_id,
                assigned_doctor_display_name = excluded.assigned_doctor_display_name,
                procedure_code = excluded.procedure_code,
                procedure_category = excluded.procedure_category,
                ready_for_doctor_at = excluded.ready_for_doctor_at,
                doctor_arrived_at = excluded.doctor_arrived_at,
                doctor_complete_at = excluded.doctor_complete_at,
                room_available_at = excluded.room_available_at,
                seated_to_doctor_seconds = excluded.seated_to_doctor_seconds,
                prep_seconds = excluded.prep_seconds,
                ready_to_doctor_seconds = excluded.ready_to_doctor_seconds,
                doctor_in_room_seconds = excluded.doctor_in_room_seconds,
                turnover_seconds = excluded.turnover_seconds,
                total_room_cycle_seconds = excluded.total_room_cycle_seconds,
                final_wait_state = excluded.final_wait_state,
                aging_threshold_reached = excluded.aging_threshold_reached,
                stale_threshold_reached = excluded.stale_threshold_reached,
                is_exception = excluded.is_exception,
                requires_review = excluded.requires_review,
                exception_reason = excluded.exception_reason,
                review_status = excluded.review_status,
                suggested_action = excluded.suggested_action,
                reviewed_at = excluded.reviewed_at,
                reviewed_by = excluded.reviewed_by,
                original_default_expected_units = excluded.original_default_expected_units,
                expected_allocation_units = excluded.expected_allocation_units,
                expected_allocation_minutes = excluded.expected_allocation_minutes,
                allocation_adjusted_from_default = excluded.allocation_adjusted_from_default,
                prestage_started_at = excluded.prestage_started_at,
                episode_id = excluded.episode_id,
                accepted_ready_handoff_id = excluded.accepted_ready_handoff_id,
                is_add_on = excluded.is_add_on,
                updated_at = excluded.updated_at;
            """;

        var now = FormatDateTimeOffset(DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("$roomId", cycle.RoomId);
        command.Parameters.AddWithValue("$assignedDoctorId", cycle.AssignedDoctor);
        command.Parameters.AddWithValue("$assignedDoctorDisplayName", doctor?.Name ?? cycle.AssignedDoctor);
        command.Parameters.AddWithValue("$procedureCode", cycle.ProcedureCode);
        command.Parameters.AddWithValue("$procedureCategory", procedure?.Label ?? cycle.ProcedureCode);
        command.Parameters.AddWithValue("$seatedAt", FormatDateTimeOffset(cycle.SeatedAt));
        command.Parameters.AddWithValue("$readyForDoctorAt", ToDbValue(cycle.ReadyForDoctorAt));
        command.Parameters.AddWithValue("$doctorArrivedAt", ToDbValue(cycle.DoctorArrivedAt));
        command.Parameters.AddWithValue("$doctorCompleteAt", ToDbValue(cycle.DoctorCompleteAt));
        command.Parameters.AddWithValue("$roomAvailableAt", ToDbValue(cycle.RoomAvailableAt));
        command.Parameters.AddWithValue("$seatedToDoctorSeconds", cycle.SeatedToDoctorSeconds);
        command.Parameters.AddWithValue("$prepSeconds", ToDbValue(cycle.PrepSeconds));
        command.Parameters.AddWithValue("$readyToDoctorSeconds", ToDbValue(cycle.ReadyToDoctorSeconds));
        command.Parameters.AddWithValue("$doctorInRoomSeconds", ToDbValue(cycle.DoctorInRoomSeconds));
        command.Parameters.AddWithValue("$turnoverSeconds", ToDbValue(cycle.TurnoverSeconds));
        command.Parameters.AddWithValue("$totalRoomCycleSeconds", ToDbValue(cycle.TotalRoomCycleSeconds));
        command.Parameters.AddWithValue("$finalWaitState", cycle.FinalWaitState);
        command.Parameters.AddWithValue("$agingThresholdReached", cycle.AgingThresholdReached ? 1 : 0);
        command.Parameters.AddWithValue("$staleThresholdReached", cycle.StaleThresholdReached ? 1 : 0);
        command.Parameters.AddWithValue("$isException", cycle.IsException ? 1 : 0);
        command.Parameters.AddWithValue("$requiresReview", cycle.RequiresReview ? 1 : 0);
        command.Parameters.AddWithValue("$exceptionReason", ToDbValue(cycle.ExceptionReason));
        command.Parameters.AddWithValue("$reviewStatus", cycle.ReviewStatus);
        command.Parameters.AddWithValue("$suggestedAction", ToDbValue(cycle.SuggestedAction));
        command.Parameters.AddWithValue("$reviewedAt", ToDbValue(cycle.ReviewedAt));
        command.Parameters.AddWithValue("$reviewedBy", ToDbValue(cycle.ReviewedBy));
        command.Parameters.AddWithValue("$originalDefaultExpectedUnits", cycle.OriginalDefaultExpectedUnits);
        command.Parameters.AddWithValue("$expectedAllocationUnits", cycle.ExpectedAllocationUnits);
        command.Parameters.AddWithValue("$expectedAllocationMinutes", cycle.ExpectedAllocationMinutes);
        command.Parameters.AddWithValue("$allocationAdjustedFromDefault", cycle.AllocationAdjustedFromDefault ? 1 : 0);
        command.Parameters.AddWithValue("$prestageStartedAt", ToDbValue(cycle.PrestageStartedAt));
        command.Parameters.AddWithValue("$episodeId", ToDbValue(cycle.EpisodeId));
        command.Parameters.AddWithValue("$acceptedReadyHandoffId", ToDbValue(cycle.AcceptedReadyHandoffId));
        command.Parameters.AddWithValue("$isAddOn", cycle.IsAddOn ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();

        // Capture the stable identity assigned by SQLite. The upsert may either insert a new
        // row or update an existing one, so we read the id back by the natural (room_id, seated_at)
        // key rather than relying on last_insert_rowid(), which is not meaningful on the update path.
        using var idCommand = connection.CreateCommand();
        idCommand.Transaction = transaction;
        idCommand.CommandText = """
            SELECT id FROM completed_room_cycles
            WHERE room_id = $roomId AND seated_at = $seatedAt;
            """;
        idCommand.Parameters.AddWithValue("$roomId", cycle.RoomId);
        idCommand.Parameters.AddWithValue("$seatedAt", FormatDateTimeOffset(cycle.SeatedAt));
        var idResult = idCommand.ExecuteScalar();
        if (idResult is not null and not DBNull)
        {
            cycle.CompletedCycleId = Convert.ToInt64(idResult);
        }
    }

    /// <summary>
    /// Atomically upserts a completed cycle and saves the (already reset-in-memory) active room in a
    /// single transaction, so the "record the finished/expired cycle" and "return the room to
    /// Available" writes succeed or fail together. Used by the ordinary Room Available completion and
    /// the seated-room force-expire archive, both of which were previously two separate commits.
    /// </summary>
    public CommittedRoomResult SaveCompletedCycleAndRoom(
        CompletedRoomCycle cycle,
        RoomState room,
        IReadOnlyList<Doctor> doctors,
        IReadOnlyList<ProcedureCategory> procedures)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        SaveCompletedCycle(connection, transaction, cycle, doctors, procedures);
        SaveRoom(connection, transaction, room, doctors, procedures);
        transaction.Commit();
        return new CommittedRoomResult(CopyRoomForPersistence(room), CopyCompletedCycleForPersistence(cycle));
    }

    /// <summary>
    /// Atomically records an aborted (incomplete) assignment and saves the (already reset-in-memory)
    /// active room in a single transaction, so the durable incomplete-assignment record and the
    /// return to Available succeed or fail together. Used by Cancel Prestage, Cancel Seating, and the
    /// sweep expiration of a prestaging room. The insert is idempotent on episode_id: re-terminating
    /// the same episode is a no-op and never overwrites an earlier distinct episode.
    /// </summary>
    public void TerminateIncompleteAssignment(
        AbortedRoomAssignment record,
        RoomState room,
        IReadOnlyList<Doctor> doctors,
        IReadOnlyList<ProcedureCategory> procedures)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        InsertAbortedAssignment(connection, transaction, record, doctors, procedures);
        SaveRoom(connection, transaction, room, doctors, procedures);
        transaction.Commit();
    }

    internal CommittedRoomResult? SaveCanonicalAssignment(
        RoomState room,
        PersistedRoomAssignment assignment,
        ActiveRoomWriteExpectation expectation,
        IReadOnlyList<Doctor> doctors,
        IReadOnlyList<ProcedureCategory> procedures)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(expectation);
        if (room.RoomId != expectation.RoomId)
        {
            throw new InvalidOperationException("Canonical assignment write expectation must identify the candidate room.");
        }
        assignment.ValidateCanonicalWrite();

        var persistedRoom = CopyRoomForPersistence(room);
        ApplyAssignment(persistedRoom, assignment);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var rows = UpdateCanonicalRoom(connection, transaction, persistedRoom, expectation);
        if (rows == 0)
        {
            transaction.Rollback();
            return null;
        }
        if (rows != 1)
        {
            throw new InvalidOperationException("Canonical assignment compare-and-swap must affect exactly one active room.");
        }

        transaction.Commit();
        return new CommittedRoomResult(persistedRoom);
    }

    public PersistedReadyHandoff CreateReadyHandoff(
        RoomState room,
        RoomAssignmentContract assignment,
        DateTimeOffset readyAt,
        IReadOnlyList<Doctor> doctors,
        IReadOnlyList<ProcedureCategory> procedures)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(assignment);
        if (assignment.Completeness != AssignmentCompleteness.Complete)
        {
            throw new ArgumentException("Ready handoff persistence requires a complete assignment.", nameof(assignment));
        }

        if (string.IsNullOrWhiteSpace(room.EpisodeId))
        {
            throw new InvalidOperationException("Ready handoff persistence requires an active episode id.");
        }

        var handoffId = Guid.NewGuid().ToString("N");
        var persistedAssignment = PersistedRoomAssignment.FromCanonicalContract(
            assignment,
            ResolveDoctorDisplayName(doctors, assignment.DoctorId),
            ResolveProcedureCategory(procedures, assignment.ProcedureCode));
        var handoff = new PersistedReadyHandoff
        {
            HandoffId = handoffId,
            EpisodeId = room.EpisodeId,
            RoomId = room.RoomId,
            ReadyAt = readyAt,
            Assignment = persistedAssignment
        };

        _ = ReadyHandoffContract.Active(handoffId, readyAt, assignment);
        var persistedRoom = CopyRoomForPersistence(room);
        ApplyAssignment(persistedRoom, persistedAssignment);
        persistedRoom.ReadyForDoctorAt = readyAt;
        persistedRoom.ActiveReadyHandoffId = handoffId;
        persistedRoom.AcceptedReadyHandoffId = null;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        InsertReadyHandoff(connection, transaction, handoff);
        SaveRoom(connection, transaction, persistedRoom, doctors, procedures);
        transaction.Commit();
        return handoff;
    }

    internal GuardedReadyHandoffPersistenceResult CreateReadyHandoffGuarded(
        RoomState room,
        RoomAssignmentContract assignment,
        DateTimeOffset readyAt,
        ActiveRoomWriteExpectation expectation,
        IReadOnlyList<Doctor> doctors,
        IReadOnlyList<ProcedureCategory> procedures)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(expectation);
        if (room.RoomId != expectation.RoomId)
        {
            throw new InvalidOperationException("Ready handoff write expectation must identify the candidate room.");
        }
        if (assignment.Completeness != AssignmentCompleteness.Complete)
        {
            throw new ArgumentException("Ready handoff persistence requires a complete assignment.", nameof(assignment));
        }
        if (string.IsNullOrWhiteSpace(room.EpisodeId))
        {
            throw new InvalidOperationException("Ready handoff persistence requires an active episode id.");
        }

        var handoffId = Guid.NewGuid().ToString("N");
        var persistedAssignment = PersistedRoomAssignment.FromCanonicalContract(
            assignment,
            ResolveDoctorDisplayName(doctors, assignment.DoctorId),
            ResolveProcedureCategory(procedures, assignment.ProcedureCode));
        var handoff = new PersistedReadyHandoff
        {
            HandoffId = handoffId,
            EpisodeId = room.EpisodeId,
            RoomId = room.RoomId,
            ReadyAt = readyAt,
            Assignment = persistedAssignment
        };
        _ = ReadyHandoffContract.Active(handoffId, readyAt, assignment);
        var persistedRoom = CopyRoomForPersistence(room);
        ApplyAssignment(persistedRoom, persistedAssignment);
        persistedRoom.ReadyForDoctorAt = readyAt;
        persistedRoom.ActiveReadyHandoffId = handoffId;
        persistedRoom.AcceptedReadyHandoffId = null;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var existingHandoffs = LoadReadyHandoffsByEpisode(connection, transaction, room.EpisodeId);
        var rows = UpdateCanonicalRoom(connection, transaction, persistedRoom, expectation);
        if (rows == 0)
        {
            transaction.Rollback();
            return new GuardedReadyHandoffPersistenceResult(GuardedReadyHandoffPersistenceOutcome.StaleWrite);
        }
        if (rows != 1)
        {
            throw new InvalidOperationException("Ready handoff compare-and-swap must affect exactly one active room.");
        }
        if (existingHandoffs.Any(existing =>
                existing.RoomId != room.RoomId
                || existing.ContractStatus != ReadyHandoffStatus.Withdrawn))
        {
            transaction.Rollback();
            return new GuardedReadyHandoffPersistenceResult(GuardedReadyHandoffPersistenceOutcome.IntegrityFault);
        }
        InsertReadyHandoff(connection, transaction, handoff);
        transaction.Commit();
        return new GuardedReadyHandoffPersistenceResult(
            GuardedReadyHandoffPersistenceOutcome.Success,
            new CommittedReadyHandoffResult(handoff, persistedRoom));
    }

    public CommittedReadyHandoffResult WithdrawReadyHandoff(
        RoomState room,
        string handoffId,
        DateTimeOffset withdrawnAt,
        IReadOnlyList<Doctor> doctors,
        IReadOnlyList<ProcedureCategory> procedures)
    {
        ArgumentNullException.ThrowIfNull(room);
        ValidateRequiredId(handoffId, nameof(handoffId));

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var handoff = LoadOwnedActiveReadyHandoff(connection, transaction, room, handoffId);
        UpdateReadyHandoffOutcome(connection, transaction, handoff, "withdrawn_at", withdrawnAt);
        var persistedRoom = CopyRoomForPersistence(room);
        ApplyAssignment(persistedRoom, handoff.Assignment);
        persistedRoom.ActiveReadyHandoffId = null;
        SaveRoom(connection, transaction, persistedRoom, doctors, procedures);
        transaction.Commit();
        return new CommittedReadyHandoffResult(
            CopyReadyHandoff(handoff, withdrawnAt: withdrawnAt),
            persistedRoom);
    }

    internal GuardedWithdrawReadyPersistenceResult WithdrawReadyHandoffGuarded(
        RoomState room,
        string handoffId,
        DateTimeOffset withdrawnAt,
        ActiveRoomWriteExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(expectation);
        ValidateRequiredId(handoffId, nameof(handoffId));
        if (room.RoomId != expectation.RoomId)
        {
            throw new ArgumentException("The Ready withdrawal candidate must match the persistence expectation.", nameof(room));
        }

        var persistedRoom = CopyRoomForPersistence(room);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var rows = UpdateCanonicalRoom(connection, transaction, persistedRoom, expectation);
        if (rows == 0)
        {
            transaction.Rollback();
            return new GuardedWithdrawReadyPersistenceResult(GuardedWithdrawReadyPersistenceOutcome.StaleWrite);
        }
        if (rows != 1)
        {
            throw new InvalidOperationException("Guarded Ready withdrawal must update exactly one room.");
        }

        var history = LoadReadyHandoffsByEpisode(connection, transaction, expectation.EpisodeId!);
        var referenced = history.SingleOrDefault(existing => existing.HandoffId == handoffId);
        var roomAssignment = new PersistedRoomAssignment(
            expectation.AssignedDoctorId,
            expectation.AssignedDoctorDisplayName,
            expectation.ProcedureCode,
            expectation.ProcedureCategory,
            expectation.SedationState,
            expectation.ExpectedAllocationState,
            expectation.ExpectedAllocationSuggestedUnits,
            expectation.ExpectedAllocationConfirmedUnits,
            expectation.IsAddOn);
        var hasIntegrityFault =
            string.IsNullOrWhiteSpace(expectation.EpisodeId)
            || expectation.ActiveReadyHandoffId != handoffId
            || expectation.AcceptedReadyHandoffId is not null
            || referenced is null
            || referenced.RoomId != expectation.RoomId
            || referenced.EpisodeId != expectation.EpisodeId
            || referenced.ContractStatus != ReadyHandoffStatus.Active
            || !roomAssignment.MatchesHandoffSnapshot(referenced.Assignment)
            || history.Any(existing =>
                existing.RoomId != expectation.RoomId
                || !string.Equals(existing.EpisodeId, expectation.EpisodeId, StringComparison.Ordinal)
                || (existing.HandoffId != handoffId
                    && existing.ContractStatus != ReadyHandoffStatus.Withdrawn));
        if (hasIntegrityFault)
        {
            transaction.Rollback();
            return new GuardedWithdrawReadyPersistenceResult(GuardedWithdrawReadyPersistenceOutcome.IntegrityFault);
        }

        UpdateReadyHandoffOutcomeGuarded(connection, transaction, referenced!, "withdrawn_at", withdrawnAt);
        transaction.Commit();
        return new GuardedWithdrawReadyPersistenceResult(
            GuardedWithdrawReadyPersistenceOutcome.Success,
            new CommittedReadyHandoffResult(
                CopyReadyHandoff(referenced!, withdrawnAt: withdrawnAt),
                persistedRoom));
    }

    public CommittedReadyHandoffResult AcceptReadyHandoffAndSaveCycle(
        RoomState room,
        CompletedRoomCycle cycle,
        string handoffId,
        DateTimeOffset acceptedAt,
        IReadOnlyList<Doctor> doctors,
        IReadOnlyList<ProcedureCategory> procedures)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(cycle);
        ValidateRequiredId(handoffId, nameof(handoffId));
        ValidateCompletedCycleMatchesRoom(cycle, room);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var handoff = LoadOwnedActiveReadyHandoff(connection, transaction, room, handoffId);
        UpdateReadyHandoffOutcome(connection, transaction, handoff, "accepted_at", acceptedAt);
        var persistedRoom = CopyRoomForPersistence(room);
        ApplyAssignment(persistedRoom, handoff.Assignment);
        persistedRoom.ActiveReadyHandoffId = null;
        persistedRoom.AcceptedReadyHandoffId = handoffId;
        var persistedCycle = CopyCompletedCycleForPersistence(cycle);
        ApplyAcceptedSnapshotToCompletedCycle(persistedCycle, handoff);
        SaveCompletedCycle(connection, transaction, persistedCycle, doctors, procedures);
        SaveRoom(connection, transaction, persistedRoom, doctors, procedures);
        transaction.Commit();
        return new CommittedReadyHandoffResult(
            CopyReadyHandoff(handoff, acceptedAt: acceptedAt),
            persistedRoom,
            persistedCycle);
    }

    internal GuardedDoctorArrivedPersistenceResult AcceptReadyHandoffAndSaveCycleGuarded(
        RoomState room,
        CompletedRoomCycle cycle,
        string handoffId,
        DateTimeOffset acceptedAt,
        ActiveRoomWriteExpectation expectation,
        IReadOnlyList<Doctor> doctors,
        IReadOnlyList<ProcedureCategory> procedures)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(expectation);
        ValidateRequiredId(handoffId, nameof(handoffId));
        if (room.RoomId != expectation.RoomId)
        {
            throw new ArgumentException("The Doctor Arrived candidate must match the persistence expectation.", nameof(room));
        }
        ValidateCompletedCycleMatchesRoom(cycle, room);

        var persistedRoom = CopyRoomForPersistence(room);
        using var connection = OpenConnection();
        // Acquire the SQLite writer reservation before reading other rooms. Doctor ownership is
        // a cross-room invariant, so a deferred transaction could allow two independent contexts
        // to both observe "no conflict" before either one writes.
        using var transaction = connection.BeginTransaction(deferred: false);
        var history = string.IsNullOrWhiteSpace(expectation.EpisodeId)
            ? []
            : LoadReadyHandoffsByEpisode(connection, transaction, expectation.EpisodeId);
        var referenced = history.SingleOrDefault(existing => existing.HandoffId == handoffId);
        var roomAssignment = new PersistedRoomAssignment(
            expectation.AssignedDoctorId,
            expectation.AssignedDoctorDisplayName,
            expectation.ProcedureCode,
            expectation.ProcedureCategory,
            expectation.SedationState,
            expectation.ExpectedAllocationState,
            expectation.ExpectedAllocationSuggestedUnits,
            expectation.ExpectedAllocationConfirmedUnits,
            expectation.IsAddOn);
        var hasIntegrityFault =
            string.IsNullOrWhiteSpace(expectation.EpisodeId)
            || expectation.ActiveReadyHandoffId != handoffId
            || expectation.AcceptedReadyHandoffId is not null
            || expectation.DoctorArrivedAt is not null
            || referenced is null
            || referenced.RoomId != expectation.RoomId
            || !string.Equals(referenced.EpisodeId, expectation.EpisodeId, StringComparison.Ordinal)
            || referenced.ContractStatus != ReadyHandoffStatus.Active
            || !roomAssignment.MatchesHandoffSnapshot(referenced.Assignment)
            || history.Any(existing =>
                existing.RoomId != expectation.RoomId
                || !string.Equals(existing.EpisodeId, expectation.EpisodeId, StringComparison.Ordinal)
                || (existing.HandoffId != handoffId
                    && existing.ContractStatus != ReadyHandoffStatus.Withdrawn));
        if (hasIntegrityFault)
        {
            transaction.Rollback();
            return new GuardedDoctorArrivedPersistenceResult(GuardedDoctorArrivedPersistenceOutcome.IntegrityFault);
        }

        var workingRoomLookup = FindDoctorWorkingRoom(
            connection,
            transaction,
            referenced!.Assignment.DoctorId,
            expectation.RoomId);
        if (workingRoomLookup.HasIntegrityFault)
        {
            transaction.Rollback();
            return new GuardedDoctorArrivedPersistenceResult(GuardedDoctorArrivedPersistenceOutcome.IntegrityFault);
        }
        if (workingRoomLookup.ConflictingRoomId is not null)
        {
            transaction.Rollback();
            return new GuardedDoctorArrivedPersistenceResult(
                GuardedDoctorArrivedPersistenceOutcome.DoctorConflict,
                ConflictingRoomId: workingRoomLookup.ConflictingRoomId);
        }

        if (CompletedCycleExists(connection, transaction, expectation.RoomId, cycle.SeatedAt))
        {
            transaction.Rollback();
            return new GuardedDoctorArrivedPersistenceResult(GuardedDoctorArrivedPersistenceOutcome.IntegrityFault);
        }

        var rows = UpdateCanonicalRoom(connection, transaction, persistedRoom, expectation);
        if (rows == 0)
        {
            transaction.Rollback();
            return new GuardedDoctorArrivedPersistenceResult(GuardedDoctorArrivedPersistenceOutcome.StaleWrite);
        }
        if (rows != 1)
        {
            throw new InvalidOperationException("Guarded Doctor Arrived must update exactly one room.");
        }

        var acceptedAssignment = new PersistedRoomAssignment(
            referenced!.Assignment.DoctorId,
            expectation.AssignedDoctorDisplayName,
            referenced.Assignment.ProcedureCode,
            expectation.ProcedureCategory,
            referenced.Assignment.SedationState,
            referenced.Assignment.ExpectedAllocationState,
            referenced.Assignment.ExpectedAllocationSuggestedUnits,
            referenced.Assignment.ExpectedAllocationConfirmedUnits,
            referenced.Assignment.IsAddOn);
        ApplyAssignment(persistedRoom, acceptedAssignment);
        persistedRoom.ActiveReadyHandoffId = null;
        persistedRoom.AcceptedReadyHandoffId = handoffId;
        var persistedCycle = CopyCompletedCycleForPersistence(cycle);
        ApplyAcceptedSnapshotToCompletedCycle(persistedCycle, referenced);
        UpdateReadyHandoffOutcomeGuarded(connection, transaction, referenced, "accepted_at", acceptedAt);
        SaveCompletedCycle(connection, transaction, persistedCycle, doctors, procedures);
        transaction.Commit();
        return new GuardedDoctorArrivedPersistenceResult(
            GuardedDoctorArrivedPersistenceOutcome.Success,
            new CommittedReadyHandoffResult(
                CopyReadyHandoff(referenced, acceptedAt: acceptedAt),
                persistedRoom,
                persistedCycle));
    }

    private sealed record DoctorWorkingRoomRecord(
        int RoomId,
        string? EpisodeId,
        string? ActiveReadyHandoffId,
        string? AcceptedReadyHandoffId,
        PersistedRoomAssignment Assignment);

    private sealed record DoctorWorkingRoomLookupResult(
        int? ConflictingRoomId = null,
        bool HasIntegrityFault = false);

    private static DoctorWorkingRoomLookupResult FindDoctorWorkingRoom(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? doctorId,
        int excludedRoomId)
    {
        if (string.IsNullOrWhiteSpace(doctorId))
        {
            return new DoctorWorkingRoomLookupResult();
        }

        var workingRooms = new List<DoctorWorkingRoomRecord>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                room_id,
                episode_id,
                active_ready_handoff_id,
                accepted_ready_handoff_id,
                assigned_doctor_id,
                assigned_doctor_display_name,
                procedure_code,
                procedure_category,
                sedation_state,
                expected_allocation_state,
                expected_allocation_suggested_units,
                expected_allocation_confirmed_units,
                is_add_on
            FROM active_rooms
            WHERE room_id <> $excludedRoomId
              AND state = $doctorWorkingState
            ORDER BY room_id
            ;
            """;
        command.Parameters.AddWithValue("$excludedRoomId", excludedRoomId);
        command.Parameters.AddWithValue("$doctorWorkingState", RoomStates.DoctorInRoom);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                workingRooms.Add(new DoctorWorkingRoomRecord(
                    reader.GetInt32(0),
                    ReadNullableString(reader, 1),
                    ReadNullableString(reader, 2),
                    ReadNullableString(reader, 3),
                    new PersistedRoomAssignment(
                        ReadNullableString(reader, 4),
                        ReadNullableString(reader, 5),
                        ReadNullableString(reader, 6),
                        ReadNullableString(reader, 7),
                        ReadNullableEnum<SedationState>(reader, 8),
                        ReadNullableEnum<ExpectedAllocationState>(reader, 9),
                        ReadNullableInt32(reader, 10),
                        ReadNullableInt32(reader, 11),
                        reader.GetInt32(12) == 1)));
            }
        }

        foreach (var workingRoom in workingRooms)
        {
            if (!string.IsNullOrWhiteSpace(workingRoom.AcceptedReadyHandoffId))
            {
                var acceptedHandoff = LoadReadyHandoff(
                    connection,
                    transaction,
                    workingRoom.AcceptedReadyHandoffId);
                var history = string.IsNullOrWhiteSpace(workingRoom.EpisodeId)
                    ? []
                    : LoadReadyHandoffsByEpisode(connection, transaction, workingRoom.EpisodeId);
                if (!string.IsNullOrWhiteSpace(workingRoom.ActiveReadyHandoffId)
                    || string.IsNullOrWhiteSpace(workingRoom.EpisodeId)
                    || acceptedHandoff is null
                    || acceptedHandoff.RoomId != workingRoom.RoomId
                    || !string.Equals(acceptedHandoff.EpisodeId, workingRoom.EpisodeId, StringComparison.Ordinal)
                    || acceptedHandoff.ContractStatus != ReadyHandoffStatus.Accepted
                    || !workingRoom.Assignment.MatchesHandoffSnapshot(acceptedHandoff.Assignment)
                    || history.Any(existing =>
                        existing.RoomId != workingRoom.RoomId
                        || !string.Equals(existing.EpisodeId, workingRoom.EpisodeId, StringComparison.Ordinal)
                        || (existing.HandoffId != acceptedHandoff.HandoffId
                            && existing.ContractStatus != ReadyHandoffStatus.Withdrawn)))
                {
                    return new DoctorWorkingRoomLookupResult(HasIntegrityFault: true);
                }

                if (string.Equals(acceptedHandoff.Assignment.DoctorId, doctorId, StringComparison.Ordinal))
                {
                    return new DoctorWorkingRoomLookupResult(ConflictingRoomId: workingRoom.RoomId);
                }

                continue;
            }

            if (!string.IsNullOrWhiteSpace(workingRoom.ActiveReadyHandoffId))
            {
                return new DoctorWorkingRoomLookupResult(HasIntegrityFault: true);
            }

            if (!string.IsNullOrWhiteSpace(workingRoom.EpisodeId))
            {
                var history = LoadReadyHandoffsByEpisode(connection, transaction, workingRoom.EpisodeId);
                if (history.Count != 0)
                {
                    return new DoctorWorkingRoomLookupResult(HasIntegrityFault: true);
                }
            }

            // Legacy arrived rooms may predate accepted-handoff metadata. For that narrow case,
            // the persisted room assignment remains the only durable doctor-ownership truth.
            if (string.Equals(workingRoom.Assignment.DoctorId, doctorId, StringComparison.Ordinal))
            {
                return new DoctorWorkingRoomLookupResult(ConflictingRoomId: workingRoom.RoomId);
            }
        }

        return new DoctorWorkingRoomLookupResult();
    }

    private static PersistedReadyHandoff? LoadReadyHandoff(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string handoffId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ReadyHandoffSelectSql + "\n" + """
            WHERE handoff_id = $handoffId;
            """;
        command.Parameters.AddWithValue("$handoffId", handoffId);
        return ReadReadyHandoffs(command).SingleOrDefault();
    }

    private static bool CompletedCycleExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int roomId,
        DateTimeOffset seatedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM completed_room_cycles
            WHERE room_id = $roomId
              AND seated_at = $seatedAt
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$roomId", roomId);
        command.Parameters.AddWithValue("$seatedAt", FormatDateTimeOffset(seatedAt));
        return command.ExecuteScalar() is not null;
    }

    public CommittedReadyHandoffResult TerminateReadyHandoffAndIncompleteAssignment(
        AbortedRoomAssignment record,
        RoomState room,
        string handoffId,
        DateTimeOffset terminatedAt,
        string readyHandoffTerminationKind,
        IReadOnlyList<Doctor> doctors,
        IReadOnlyList<ProcedureCategory> procedures)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(room);
        ValidateRequiredId(handoffId, nameof(handoffId));
        if (!ReadyHandoffTerminationKinds.IsValid(readyHandoffTerminationKind))
        {
            throw new ArgumentException("Ready handoff termination kind must be Canceled or Expired.", nameof(readyHandoffTerminationKind));
        }
        ValidateAbortedAssignmentMatchesRoom(record, room);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var handoff = LoadOwnedActiveReadyHandoff(connection, transaction, room, handoffId);
        TerminateReadyHandoff(connection, transaction, handoff, terminatedAt, readyHandoffTerminationKind);
        var persistedRecord = CopyAbortedAssignmentForPersistence(record);
        persistedRecord.TerminalReadyHandoffId = handoffId;
        var persistedRoom = new RoomState(room.RoomId);
        InsertAbortedAssignment(connection, transaction, persistedRecord, doctors, procedures);
        SaveRoom(connection, transaction, persistedRoom, doctors, procedures);
        transaction.Commit();
        return new CommittedReadyHandoffResult(
            CopyReadyHandoff(handoff, terminatedAt: terminatedAt, terminationKind: readyHandoffTerminationKind),
            persistedRoom,
            AbortedAssignment: persistedRecord);
    }

    /// <summary>
    /// Atomically cancels a Ready room. If the room genuinely owns the referenced Active handoff, that
    /// handoff is terminated (Canceled) as part of the cancellation and linked to the abort record.
    /// Otherwise the referenced handoff row - dangling, foreign, or already resolved - is left untouched
    /// and the abort record carries no terminal handoff link. Either way, the aborted-assignment history
    /// is inserted and the persisted active room is reset in the same transaction, which commits before
    /// the caller mutates live in-memory state. Never terminates a handoff the room does not own.
    /// </summary>
    public void CancelReadyRoom(
        AbortedRoomAssignment record,
        RoomState room,
        string handoffId,
        DateTimeOffset canceledAt,
        IReadOnlyList<Doctor> doctors,
        IReadOnlyList<ProcedureCategory> procedures)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(room);
        ValidateRequiredId(handoffId, nameof(handoffId));
        ValidateAbortedAssignmentMatchesRoom(record, room);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var ownedActiveHandoff = TryLoadOwnedActiveReadyHandoff(connection, transaction, room, handoffId);
        var persistedRecord = CopyAbortedAssignmentForPersistence(record);
        if (ownedActiveHandoff is not null)
        {
            TerminateReadyHandoff(connection, transaction, ownedActiveHandoff, canceledAt, ReadyHandoffTerminationKinds.Canceled);
            persistedRecord.TerminalReadyHandoffId = handoffId;
        }

        InsertAbortedAssignment(connection, transaction, persistedRecord, doctors, procedures);
        SaveRoom(connection, transaction, new RoomState(room.RoomId), doctors, procedures);
        transaction.Commit();
    }

    public IReadOnlyList<AbortedRoomAssignment> LoadAbortedAssignments()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                episode_id,
                room_id,
                assigned_doctor_id,
                assigned_doctor_display_name,
                procedure_code,
                procedure_category,
                original_default_expected_units,
                expected_allocation_units,
                expected_allocation_minutes,
                allocation_adjusted_from_default,
                prestage_started_at,
                seated_at,
                ready_for_doctor_at,
                terminated_at,
                terminated_from_state,
                termination_kind,
                cancellation_reason,
                sedation_state,
                expected_allocation_state,
                expected_allocation_suggested_units,
                expected_allocation_confirmed_units,
                terminal_ready_handoff_id,
                is_exception,
                requires_review,
                exception_reason,
                review_status,
                suggested_action,
                reviewed_at,
                reviewed_by,
                is_add_on
            FROM aborted_room_assignments
            ORDER BY terminated_at DESC;
            """;

        var records = new List<AbortedRoomAssignment>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new AbortedRoomAssignment
            {
                AbortedAssignmentId = reader.GetInt64(0),
                EpisodeId = reader.GetString(1),
                RoomId = reader.GetInt32(2),
                AssignedDoctor = ReadNullableString(reader, 3),
                AssignedDoctorDisplayName = ReadNullableString(reader, 4),
                ProcedureCode = ReadNullableString(reader, 5),
                ProcedureCategory = ReadNullableString(reader, 6),
                OriginalDefaultExpectedUnits = reader.GetInt32(7),
                ExpectedAllocationUnits = reader.GetInt32(8),
                ExpectedAllocationMinutes = reader.GetInt32(9),
                AllocationAdjustedFromDefault = reader.GetInt32(10) == 1,
                PrestageStartedAt = ReadNullableDateTimeOffset(reader, 11),
                SeatedAt = ReadNullableDateTimeOffset(reader, 12),
                ReadyForDoctorAt = ReadNullableDateTimeOffset(reader, 13),
                TerminatedAt = ReadRequiredDateTimeOffset(reader, 14),
                TerminatedFromState = reader.GetString(15),
                TerminationKind = reader.GetString(16),
                CancellationReason = ReadNullableString(reader, 17),
                SedationState = ReadNullableEnum<SedationState>(reader, 18),
                ExpectedAllocationState = ReadNullableEnum<ExpectedAllocationState>(reader, 19),
                ExpectedAllocationSuggestedUnits = ReadNullableInt32(reader, 20),
                ExpectedAllocationConfirmedUnits = ReadNullableInt32(reader, 21),
                TerminalReadyHandoffId = ReadNullableString(reader, 22),
                IsException = reader.GetInt32(23) == 1,
                RequiresReview = reader.GetInt32(24) == 1,
                ExceptionReason = ReadNullableString(reader, 25),
                ReviewStatus = ReadNullableString(reader, 26) ?? ReviewStatuses.PendingReview,
                SuggestedAction = ReadNullableString(reader, 27),
                ReviewedAt = ReadNullableDateTimeOffset(reader, 28),
                ReviewedBy = ReadNullableString(reader, 29),
                IsAddOn = reader.GetInt32(30) == 1
            });
        }

        return records;
    }

    public void ReviewAbortedAssignment(long abortedAssignmentId, DateTimeOffset reviewedAt, string reviewedBy)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE aborted_room_assignments
            SET requires_review = 0,
                review_status = $reviewStatus,
                reviewed_at = $reviewedAt,
                reviewed_by = $reviewedBy,
                updated_at = $reviewedAt
            WHERE id = $id
              AND is_exception = 1;
            """;
        command.Parameters.AddWithValue("$id", abortedAssignmentId);
        command.Parameters.AddWithValue("$reviewStatus", ReviewStatuses.Reviewed);
        command.Parameters.AddWithValue("$reviewedAt", FormatDateTimeOffset(reviewedAt));
        command.Parameters.AddWithValue("$reviewedBy", reviewedBy);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Reviewing an aborted assignment exception must update exactly one record.");
        }
    }

    public IReadOnlyList<PersistedReadyHandoff> LoadReadyHandoffsByEpisode(string episodeId)
    {
        ValidateRequiredId(episodeId, nameof(episodeId));

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = ReadyHandoffSelectSql + "\n" + """
            WHERE episode_id = $episodeId
            ORDER BY ready_at ASC;
            """;
        command.Parameters.AddWithValue("$episodeId", episodeId);

        return ReadReadyHandoffs(command);
    }

    private static IReadOnlyList<PersistedReadyHandoff> LoadReadyHandoffsByEpisode(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string episodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ReadyHandoffSelectSql + "\n" + """
            WHERE episode_id = $episodeId
            ORDER BY ready_at ASC;
            """;
        command.Parameters.AddWithValue("$episodeId", episodeId);
        return ReadReadyHandoffs(command);
    }

    public PersistedReadyHandoff? LoadActiveReadyHandoff(string episodeId)
    {
        ValidateRequiredId(episodeId, nameof(episodeId));

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = ReadyHandoffSelectSql + "\n" + """
            WHERE episode_id = $episodeId
                AND withdrawn_at IS NULL
                AND accepted_at IS NULL
                AND terminated_at IS NULL;
            """;
        command.Parameters.AddWithValue("$episodeId", episodeId);

        return ReadReadyHandoffs(command).SingleOrDefault();
    }

    public PersistedReadyHandoff? LoadReadyHandoff(string handoffId)
    {
        ValidateRequiredId(handoffId, nameof(handoffId));

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = ReadyHandoffSelectSql + "\n" + """
            WHERE handoff_id = $handoffId;
            """;
        command.Parameters.AddWithValue("$handoffId", handoffId);

        return ReadReadyHandoffs(command).SingleOrDefault();
    }

    private const string ReadyHandoffSelectSql = """
        SELECT
            handoff_id,
            episode_id,
            room_id,
            ready_at,
            withdrawn_at,
            accepted_at,
            terminated_at,
            termination_kind,
            doctor_id,
            procedure_code,
            sedation_state,
            expected_allocation_state,
            expected_allocation_suggested_units,
            expected_allocation_confirmed_units,
            is_add_on
        FROM ready_handoffs
        """;

    private static IReadOnlyList<PersistedReadyHandoff> ReadReadyHandoffs(SqliteCommand command)
    {
        var handoffs = new List<PersistedReadyHandoff>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            handoffs.Add(new PersistedReadyHandoff
            {
                HandoffId = reader.GetString(0),
                EpisodeId = reader.GetString(1),
                RoomId = reader.GetInt32(2),
                ReadyAt = ReadRequiredDateTimeOffset(reader, 3),
                WithdrawnAt = ReadNullableDateTimeOffset(reader, 4),
                AcceptedAt = ReadNullableDateTimeOffset(reader, 5),
                TerminatedAt = ReadNullableDateTimeOffset(reader, 6),
                TerminationKind = ReadNullableString(reader, 7),
                Assignment = new PersistedRoomAssignment(
                    reader.GetString(8),
                    null,
                    reader.GetString(9),
                    null,
                    ReadNullableEnum<SedationState>(reader, 10),
                    ReadNullableEnum<ExpectedAllocationState>(reader, 11),
                    ReadNullableInt32(reader, 12),
                    reader.GetInt32(13),
                    reader.GetInt32(14) == 1)
            });
        }

        return handoffs;
    }

    private static PersistedReadyHandoff LoadOwnedActiveReadyHandoff(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RoomState room,
        string handoffId)
    {
        ValidateRoomHandoffReference(room, handoffId);

        return TryLoadOwnedActiveReadyHandoff(connection, transaction, room, handoffId)
            ?? throw new InvalidOperationException("Ready handoff operation requires the supplied room to own the active handoff.");
    }

    // Non-throwing sibling of LoadOwnedActiveReadyHandoff: returns the handoff only when it is genuinely
    // the room's own Active handoff (matching id/episode/room, not withdrawn/accepted/terminated, and
    // referenced by the persisted active room). Returns null for any reference the room does not own -
    // dangling, foreign room/episode, or already resolved - without throwing. Used by cancellation to
    // decide whether to terminate the owned handoff or preserve an unrelated/historical one untouched.
    private static PersistedReadyHandoff? TryLoadOwnedActiveReadyHandoff(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RoomState room,
        string handoffId)
    {
        if (string.IsNullOrWhiteSpace(room.EpisodeId)
            || !string.Equals(room.ActiveReadyHandoffId, handoffId, StringComparison.Ordinal))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ReadyHandoffSelectSql + "\n" + """
            WHERE handoff_id = $handoffId
                AND episode_id = $episodeId
                AND room_id = $roomId
                AND withdrawn_at IS NULL
                AND accepted_at IS NULL
                AND terminated_at IS NULL
                AND EXISTS (
                    SELECT 1
                    FROM active_rooms
                    WHERE active_rooms.room_id = ready_handoffs.room_id
                        AND active_rooms.episode_id = ready_handoffs.episode_id
                        AND active_rooms.active_ready_handoff_id = ready_handoffs.handoff_id
                );
            """;
        command.Parameters.AddWithValue("$handoffId", handoffId);
        command.Parameters.AddWithValue("$episodeId", room.EpisodeId);
        command.Parameters.AddWithValue("$roomId", room.RoomId);

        return ReadReadyHandoffs(command).SingleOrDefault();
    }

    private static void ValidateRoomHandoffReference(RoomState room, string handoffId)
    {
        if (string.IsNullOrWhiteSpace(room.EpisodeId))
        {
            throw new InvalidOperationException("Ready handoff operation requires an active episode id.");
        }

        if (!string.Equals(room.ActiveReadyHandoffId, handoffId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Ready handoff operation requires the supplied room to reference the active handoff.");
        }
    }

    private static void ValidateCompletedCycleMatchesRoom(CompletedRoomCycle cycle, RoomState room)
    {
        ValidateRoomEpisode(room);
        if (cycle.RoomId != room.RoomId
            || !string.Equals(cycle.EpisodeId, room.EpisodeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Completed cycle must belong to the same room episode as the accepted handoff.");
        }
    }

    private static void ValidateAbortedAssignmentMatchesRoom(AbortedRoomAssignment record, RoomState room)
    {
        ValidateRoomEpisode(room);
        if (record.RoomId != room.RoomId
            || !string.Equals(record.EpisodeId, room.EpisodeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Aborted assignment must belong to the same room episode as the terminated handoff.");
        }
    }

    private static void ValidateRoomEpisode(RoomState room)
    {
        if (string.IsNullOrWhiteSpace(room.EpisodeId))
        {
            throw new InvalidOperationException("Ready handoff operation requires an active episode id.");
        }
    }

    private static void InsertReadyHandoff(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PersistedReadyHandoff handoff)
    {
        var assignment = handoff.Assignment.ToContract();
        _ = ReadyHandoffContract.Active(handoff.HandoffId, handoff.ReadyAt, assignment);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ready_handoffs (
                handoff_id,
                episode_id,
                room_id,
                ready_at,
                doctor_id,
                procedure_code,
                sedation_state,
                expected_allocation_state,
                expected_allocation_suggested_units,
                expected_allocation_confirmed_units,
                is_add_on
            )
            VALUES (
                $handoffId,
                $episodeId,
                $roomId,
                $readyAt,
                $doctorId,
                $procedureCode,
                $sedationState,
                $expectedAllocationState,
                $expectedAllocationSuggestedUnits,
                $expectedAllocationConfirmedUnits,
                $isAddOn
            );
            """;
        command.Parameters.AddWithValue("$handoffId", handoff.HandoffId);
        command.Parameters.AddWithValue("$episodeId", handoff.EpisodeId);
        command.Parameters.AddWithValue("$roomId", handoff.RoomId);
        command.Parameters.AddWithValue("$readyAt", FormatDateTimeOffset(handoff.ReadyAt));
        command.Parameters.AddWithValue("$doctorId", assignment.DoctorId!);
        command.Parameters.AddWithValue("$procedureCode", assignment.ProcedureCode!);
        command.Parameters.AddWithValue("$sedationState", assignment.Sedation.State.ToString());
        command.Parameters.AddWithValue("$expectedAllocationState", assignment.ExpectedAllocation.State.ToString());
        command.Parameters.AddWithValue("$expectedAllocationSuggestedUnits", ToDbValue(assignment.ExpectedAllocation.SuggestedValue));
        command.Parameters.AddWithValue("$expectedAllocationConfirmedUnits", assignment.ExpectedAllocation.ConfirmedValue!.Value);
        command.Parameters.AddWithValue("$isAddOn", assignment.IsAddOn ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static void UpdateReadyHandoffOutcome(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PersistedReadyHandoff handoff,
        string outcomeColumn,
        DateTimeOffset outcomeAt)
    {
        if (outcomeColumn is not ("withdrawn_at" or "accepted_at"))
        {
            throw new ArgumentException("Unsupported Ready handoff outcome column.", nameof(outcomeColumn));
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE ready_handoffs
            SET {outcomeColumn} = $outcomeAt
            WHERE handoff_id = $handoffId
                AND episode_id = $episodeId
                AND room_id = $roomId
                AND withdrawn_at IS NULL
                AND accepted_at IS NULL
                AND terminated_at IS NULL
                AND ready_at <= $outcomeAt
                AND EXISTS (
                    SELECT 1
                    FROM active_rooms
                    WHERE active_rooms.room_id = ready_handoffs.room_id
                        AND active_rooms.episode_id = ready_handoffs.episode_id
                        AND active_rooms.active_ready_handoff_id = ready_handoffs.handoff_id
                );
            """;
        command.Parameters.AddWithValue("$handoffId", handoff.HandoffId);
        command.Parameters.AddWithValue("$episodeId", handoff.EpisodeId);
        command.Parameters.AddWithValue("$roomId", handoff.RoomId);
        command.Parameters.AddWithValue("$outcomeAt", FormatDateTimeOffset(outcomeAt));
        var rows = command.ExecuteNonQuery();
        if (rows != 1)
        {
            throw new InvalidOperationException("Ready handoff outcome update requires exactly one active handoff.");
        }
    }

    private static void UpdateReadyHandoffOutcomeGuarded(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PersistedReadyHandoff handoff,
        string outcomeColumn,
        DateTimeOffset outcomeAt)
    {
        if (outcomeColumn is not ("withdrawn_at" or "accepted_at"))
        {
            throw new ArgumentException("Unsupported Ready handoff outcome column.", nameof(outcomeColumn));
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE ready_handoffs
            SET {outcomeColumn} = $outcomeAt
            WHERE handoff_id = $handoffId
                AND episode_id = $episodeId
                AND room_id = $roomId
                AND withdrawn_at IS NULL
                AND accepted_at IS NULL
                AND terminated_at IS NULL
                AND ready_at <= $outcomeAt;
            """;
        command.Parameters.AddWithValue("$handoffId", handoff.HandoffId);
        command.Parameters.AddWithValue("$episodeId", handoff.EpisodeId);
        command.Parameters.AddWithValue("$roomId", handoff.RoomId);
        command.Parameters.AddWithValue("$outcomeAt", FormatDateTimeOffset(outcomeAt));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Guarded Ready handoff outcome update requires exactly one active handoff.");
        }
    }

    private static void TerminateReadyHandoff(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PersistedReadyHandoff handoff,
        DateTimeOffset terminatedAt,
        string terminationKind)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ready_handoffs
            SET terminated_at = $terminatedAt,
                termination_kind = $terminationKind
            WHERE handoff_id = $handoffId
                AND episode_id = $episodeId
                AND room_id = $roomId
                AND withdrawn_at IS NULL
                AND accepted_at IS NULL
                AND terminated_at IS NULL
                AND ready_at <= $terminatedAt
                AND EXISTS (
                    SELECT 1
                    FROM active_rooms
                    WHERE active_rooms.room_id = ready_handoffs.room_id
                        AND active_rooms.episode_id = ready_handoffs.episode_id
                        AND active_rooms.active_ready_handoff_id = ready_handoffs.handoff_id
                );
            """;
        command.Parameters.AddWithValue("$handoffId", handoff.HandoffId);
        command.Parameters.AddWithValue("$episodeId", handoff.EpisodeId);
        command.Parameters.AddWithValue("$roomId", handoff.RoomId);
        command.Parameters.AddWithValue("$terminatedAt", FormatDateTimeOffset(terminatedAt));
        command.Parameters.AddWithValue("$terminationKind", terminationKind);
        var rows = command.ExecuteNonQuery();
        if (rows != 1)
        {
            throw new InvalidOperationException("Ready handoff termination requires exactly one active handoff.");
        }
    }

    // Inserts one aborted-assignment record on the caller's connection/transaction. Idempotent on
    // episode_id (ON CONFLICT DO NOTHING) so a retried or restart-replayed termination cannot create
    // a duplicate, and a later distinct episode (different episode_id) can never overwrite it.
    // Persists the captured doctor/procedure display snapshots. Legacy null snapshots use the
    // current roster as a best-effort fallback. Populates AbortedAssignmentId from the persisted row.
    private void InsertAbortedAssignment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AbortedRoomAssignment record,
        IReadOnlyList<Doctor> doctors,
        IReadOnlyList<ProcedureCategory> procedures)
    {
        var doctorDisplayName = record.AssignedDoctorDisplayName
            ?? (record.AssignedDoctor is null
                ? null
                : doctors.FirstOrDefault(item => item.Id == record.AssignedDoctor)?.Name ?? record.AssignedDoctor);
        var procedureCategory = record.ProcedureCategory
            ?? ResolveProcedureCategory(procedures, record.ProcedureCode)
            ?? record.ProcedureCode;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO aborted_room_assignments (
                episode_id,
                room_id,
                assigned_doctor_id,
                assigned_doctor_display_name,
                procedure_code,
                procedure_category,
                original_default_expected_units,
                expected_allocation_units,
                expected_allocation_minutes,
                allocation_adjusted_from_default,
                prestage_started_at,
                seated_at,
                ready_for_doctor_at,
                terminated_at,
                terminated_from_state,
                termination_kind,
                cancellation_reason,
                sedation_state,
                expected_allocation_state,
                expected_allocation_suggested_units,
                expected_allocation_confirmed_units,
                terminal_ready_handoff_id,
                is_add_on,
                is_exception,
                requires_review,
                exception_reason,
                review_status,
                suggested_action,
                reviewed_at,
                reviewed_by,
                created_at,
                updated_at
            )
            VALUES (
                $episodeId,
                $roomId,
                $assignedDoctorId,
                $assignedDoctorDisplayName,
                $procedureCode,
                $procedureCategory,
                $originalDefaultExpectedUnits,
                $expectedAllocationUnits,
                $expectedAllocationMinutes,
                $allocationAdjustedFromDefault,
                $prestageStartedAt,
                $seatedAt,
                $readyForDoctorAt,
                $terminatedAt,
                $terminatedFromState,
                $terminationKind,
                $cancellationReason,
                $sedationState,
                $expectedAllocationState,
                $expectedAllocationSuggestedUnits,
                $expectedAllocationConfirmedUnits,
                $terminalReadyHandoffId,
                $isAddOn,
                $isException,
                $requiresReview,
                $exceptionReason,
                $reviewStatus,
                $suggestedAction,
                $reviewedAt,
                $reviewedBy,
                $now,
                $now
            )
            ON CONFLICT(episode_id) DO NOTHING;
            """;

        var now = FormatDateTimeOffset(DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("$episodeId", record.EpisodeId);
        command.Parameters.AddWithValue("$roomId", record.RoomId);
        command.Parameters.AddWithValue("$assignedDoctorId", ToDbValue(record.AssignedDoctor));
        command.Parameters.AddWithValue("$assignedDoctorDisplayName", ToDbValue(doctorDisplayName));
        command.Parameters.AddWithValue("$procedureCode", ToDbValue(record.ProcedureCode));
        command.Parameters.AddWithValue("$procedureCategory", ToDbValue(procedureCategory));
        command.Parameters.AddWithValue("$originalDefaultExpectedUnits", record.OriginalDefaultExpectedUnits);
        command.Parameters.AddWithValue("$expectedAllocationUnits", record.ExpectedAllocationUnits);
        command.Parameters.AddWithValue("$expectedAllocationMinutes", record.ExpectedAllocationMinutes);
        command.Parameters.AddWithValue("$allocationAdjustedFromDefault", record.AllocationAdjustedFromDefault ? 1 : 0);
        command.Parameters.AddWithValue("$prestageStartedAt", ToDbValue(record.PrestageStartedAt));
        command.Parameters.AddWithValue("$seatedAt", ToDbValue(record.SeatedAt));
        command.Parameters.AddWithValue("$readyForDoctorAt", ToDbValue(record.ReadyForDoctorAt));
        command.Parameters.AddWithValue("$terminatedAt", FormatDateTimeOffset(record.TerminatedAt));
        command.Parameters.AddWithValue("$terminatedFromState", record.TerminatedFromState);
        command.Parameters.AddWithValue("$terminationKind", record.TerminationKind);
        command.Parameters.AddWithValue("$cancellationReason", ToDbValue(record.CancellationReason));
        command.Parameters.AddWithValue("$sedationState", ToDbValue(record.SedationState));
        command.Parameters.AddWithValue("$expectedAllocationState", ToDbValue(record.ExpectedAllocationState));
        command.Parameters.AddWithValue("$expectedAllocationSuggestedUnits", ToDbValue(record.ExpectedAllocationSuggestedUnits));
        command.Parameters.AddWithValue("$expectedAllocationConfirmedUnits", ToDbValue(record.ExpectedAllocationConfirmedUnits));
        command.Parameters.AddWithValue("$terminalReadyHandoffId", ToDbValue(record.TerminalReadyHandoffId));
        command.Parameters.AddWithValue("$isAddOn", record.IsAddOn ? 1 : 0);
        command.Parameters.AddWithValue("$isException", record.IsException ? 1 : 0);
        command.Parameters.AddWithValue("$requiresReview", record.RequiresReview ? 1 : 0);
        command.Parameters.AddWithValue("$exceptionReason", ToDbValue(record.ExceptionReason));
        command.Parameters.AddWithValue("$reviewStatus", record.ReviewStatus);
        command.Parameters.AddWithValue("$suggestedAction", ToDbValue(record.SuggestedAction));
        command.Parameters.AddWithValue("$reviewedAt", ToDbValue(record.ReviewedAt));
        command.Parameters.AddWithValue("$reviewedBy", ToDbValue(record.ReviewedBy));
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();

        // Read the id back by the idempotency key so the returned record carries the stable id of the
        // row that is actually persisted (whether this call inserted it or a prior call already did).
        using var idCommand = connection.CreateCommand();
        idCommand.Transaction = transaction;
        idCommand.CommandText = "SELECT id FROM aborted_room_assignments WHERE episode_id = $episodeId;";
        idCommand.Parameters.AddWithValue("$episodeId", record.EpisodeId);
        var idResult = idCommand.ExecuteScalar();
        if (idResult is not null and not DBNull)
        {
            record.AbortedAssignmentId = Convert.ToInt64(idResult);
        }
    }
    private static int UpdateCanonicalRoom(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RoomState room,
        ActiveRoomWriteExpectation expectation)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE active_rooms
            SET assigned_doctor_id = $assignedDoctorId,
                assigned_doctor_display_name = $assignedDoctorDisplayName,
                procedure_code = $procedureCode,
                procedure_category = $procedureCategory,
                state = $state,
                seated_at = $seatedAt,
                aging_started_at = $agingStartedAt,
                stale_started_at = $staleStartedAt,
                ready_for_doctor_at = $readyForDoctorAt,
                doctor_arrived_at = $doctorArrivedAt,
                doctor_complete_at = $doctorCompleteAt,
                room_available_at = $roomAvailableAt,
                original_default_expected_units = $originalDefaultExpectedUnits,
                expected_allocation_units = $expectedAllocationUnits,
                expected_allocation_minutes = $expectedAllocationMinutes,
                allocation_adjusted_from_default = $allocationAdjustedFromDefault,
                prestage_started_at = $prestageStartedAt,
                episode_id = $episodeId,
                sedation_state = $sedationState,
                expected_allocation_state = $expectedAllocationState,
                expected_allocation_suggested_units = $expectedAllocationSuggestedUnits,
                expected_allocation_confirmed_units = $expectedAllocationConfirmedUnits,
                is_add_on = $isAddOn,
                active_ready_handoff_id = $activeReadyHandoffId,
                accepted_ready_handoff_id = $acceptedReadyHandoffId,
                updated_at = $updatedAt
            WHERE room_id = $expectedRoomId
                AND episode_id IS $expectedEpisodeId
                AND state = $expectedState
                AND assigned_doctor_id IS $expectedAssignedDoctorId
                AND assigned_doctor_display_name IS $expectedAssignedDoctorDisplayName
                AND procedure_code IS $expectedProcedureCode
                AND procedure_category IS $expectedProcedureCategory
                AND sedation_state IS $expectedSedationState
                AND expected_allocation_state IS $expectedExpectedAllocationState
                AND expected_allocation_suggested_units IS $expectedExpectedAllocationSuggestedUnits
                AND expected_allocation_confirmed_units IS $expectedExpectedAllocationConfirmedUnits
                AND is_add_on IS $expectedIsAddOn
                AND active_ready_handoff_id IS $expectedActiveReadyHandoffId
                AND accepted_ready_handoff_id IS $expectedAcceptedReadyHandoffId
                AND prestage_started_at IS $expectedPrestageStartedAt
                AND seated_at IS $expectedSeatedAt
                AND aging_started_at IS $expectedAgingStartedAt
                AND stale_started_at IS $expectedStaleStartedAt
                AND ready_for_doctor_at IS $expectedReadyForDoctorAt
                AND doctor_arrived_at IS $expectedDoctorArrivedAt
                AND doctor_complete_at IS $expectedDoctorCompleteAt
                AND room_available_at IS $expectedRoomAvailableAt
                AND original_default_expected_units IS $expectedOriginalDefaultExpectedUnits
                AND expected_allocation_units IS $expectedExpectedAllocationUnits
                AND expected_allocation_minutes IS $expectedExpectedAllocationMinutes
                AND allocation_adjusted_from_default IS $expectedAllocationAdjustedFromDefault;
            """;

        command.Parameters.AddWithValue("$assignedDoctorId", ToDbValue(room.AssignedDoctor));
        command.Parameters.AddWithValue("$assignedDoctorDisplayName", ToDbValue(room.AssignedDoctorDisplayName));
        command.Parameters.AddWithValue("$procedureCode", ToDbValue(room.ProcedureCode));
        command.Parameters.AddWithValue("$procedureCategory", ToDbValue(room.ProcedureCategory));
        command.Parameters.AddWithValue("$state", room.State);
        command.Parameters.AddWithValue("$seatedAt", ToDbValue(room.SeatedAt));
        command.Parameters.AddWithValue("$agingStartedAt", ToDbValue(room.AgingStartedAt));
        command.Parameters.AddWithValue("$staleStartedAt", ToDbValue(room.StaleStartedAt));
        command.Parameters.AddWithValue("$readyForDoctorAt", ToDbValue(room.ReadyForDoctorAt));
        command.Parameters.AddWithValue("$doctorArrivedAt", ToDbValue(room.DoctorArrivedAt));
        command.Parameters.AddWithValue("$doctorCompleteAt", ToDbValue(room.DoctorCompleteAt));
        command.Parameters.AddWithValue("$roomAvailableAt", ToDbValue(room.RoomAvailableAt));
        command.Parameters.AddWithValue("$originalDefaultExpectedUnits", room.OriginalDefaultExpectedUnits);
        command.Parameters.AddWithValue("$expectedAllocationUnits", room.ExpectedAllocationUnits);
        command.Parameters.AddWithValue("$expectedAllocationMinutes", room.ExpectedAllocationMinutes);
        command.Parameters.AddWithValue("$allocationAdjustedFromDefault", room.AllocationAdjustedFromDefault ? 1 : 0);
        command.Parameters.AddWithValue("$prestageStartedAt", ToDbValue(room.PrestageStartedAt));
        command.Parameters.AddWithValue("$episodeId", ToDbValue(room.EpisodeId));
        command.Parameters.AddWithValue("$sedationState", ToDbValue(room.SedationState));
        command.Parameters.AddWithValue("$expectedAllocationState", ToDbValue(room.ExpectedAllocationState));
        command.Parameters.AddWithValue("$expectedAllocationSuggestedUnits", ToDbValue(room.ExpectedAllocationSuggestedUnits));
        command.Parameters.AddWithValue("$expectedAllocationConfirmedUnits", ToDbValue(room.ExpectedAllocationConfirmedUnits));
        command.Parameters.AddWithValue("$isAddOn", room.IsAddOn ? 1 : 0);
        command.Parameters.AddWithValue("$activeReadyHandoffId", ToDbValue(room.ActiveReadyHandoffId));
        command.Parameters.AddWithValue("$acceptedReadyHandoffId", ToDbValue(room.AcceptedReadyHandoffId));
        command.Parameters.AddWithValue("$updatedAt", FormatDateTimeOffset(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$expectedRoomId", expectation.RoomId);
        command.Parameters.AddWithValue("$expectedEpisodeId", ToDbValue(expectation.EpisodeId));
        command.Parameters.AddWithValue("$expectedState", expectation.State);
        command.Parameters.AddWithValue("$expectedAssignedDoctorId", ToDbValue(expectation.AssignedDoctorId));
        command.Parameters.AddWithValue("$expectedAssignedDoctorDisplayName", ToDbValue(expectation.AssignedDoctorDisplayName));
        command.Parameters.AddWithValue("$expectedProcedureCode", ToDbValue(expectation.ProcedureCode));
        command.Parameters.AddWithValue("$expectedProcedureCategory", ToDbValue(expectation.ProcedureCategory));
        command.Parameters.AddWithValue("$expectedSedationState", ToDbValue(expectation.SedationState));
        command.Parameters.AddWithValue("$expectedExpectedAllocationState", ToDbValue(expectation.ExpectedAllocationState));
        command.Parameters.AddWithValue("$expectedExpectedAllocationSuggestedUnits", ToDbValue(expectation.ExpectedAllocationSuggestedUnits));
        command.Parameters.AddWithValue("$expectedExpectedAllocationConfirmedUnits", ToDbValue(expectation.ExpectedAllocationConfirmedUnits));
        command.Parameters.AddWithValue("$expectedIsAddOn", expectation.IsAddOn ? 1 : 0);
        command.Parameters.AddWithValue("$expectedActiveReadyHandoffId", ToDbValue(expectation.ActiveReadyHandoffId));
        command.Parameters.AddWithValue("$expectedAcceptedReadyHandoffId", ToDbValue(expectation.AcceptedReadyHandoffId));
        command.Parameters.AddWithValue("$expectedPrestageStartedAt", ToDbValue(expectation.PrestageStartedAt));
        command.Parameters.AddWithValue("$expectedSeatedAt", ToDbValue(expectation.SeatedAt));
        command.Parameters.AddWithValue("$expectedAgingStartedAt", ToDbValue(expectation.AgingStartedAt));
        command.Parameters.AddWithValue("$expectedStaleStartedAt", ToDbValue(expectation.StaleStartedAt));
        command.Parameters.AddWithValue("$expectedReadyForDoctorAt", ToDbValue(expectation.ReadyForDoctorAt));
        command.Parameters.AddWithValue("$expectedDoctorArrivedAt", ToDbValue(expectation.DoctorArrivedAt));
        command.Parameters.AddWithValue("$expectedDoctorCompleteAt", ToDbValue(expectation.DoctorCompleteAt));
        command.Parameters.AddWithValue("$expectedRoomAvailableAt", ToDbValue(expectation.RoomAvailableAt));
        command.Parameters.AddWithValue("$expectedOriginalDefaultExpectedUnits", expectation.OriginalDefaultExpectedUnits);
        command.Parameters.AddWithValue("$expectedExpectedAllocationUnits", expectation.ExpectedAllocationUnits);
        command.Parameters.AddWithValue("$expectedExpectedAllocationMinutes", expectation.ExpectedAllocationMinutes);
        command.Parameters.AddWithValue("$expectedAllocationAdjustedFromDefault", expectation.AllocationAdjustedFromDefault ? 1 : 0);
        return command.ExecuteNonQuery();
    }

    private void SaveRoom(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RoomState room,
        IReadOnlyList<Doctor> doctors,
        IReadOnlyList<ProcedureCategory> procedures)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO active_rooms (
                room_id,
                assigned_doctor_id,
                assigned_doctor_display_name,
                procedure_code,
                procedure_category,
                state,
                seated_at,
                aging_started_at,
                stale_started_at,
                ready_for_doctor_at,
                doctor_arrived_at,
                doctor_complete_at,
                room_available_at,
                original_default_expected_units,
                expected_allocation_units,
                expected_allocation_minutes,
                allocation_adjusted_from_default,
                prestage_started_at,
                episode_id,
                sedation_state,
                expected_allocation_state,
                expected_allocation_suggested_units,
                expected_allocation_confirmed_units,
                is_add_on,
                active_ready_handoff_id,
                accepted_ready_handoff_id,
                updated_at
            )
            VALUES (
                $roomId,
                $assignedDoctorId,
                $assignedDoctorDisplayName,
                $procedureCode,
                $procedureCategory,
                $state,
                $seatedAt,
                $agingStartedAt,
                $staleStartedAt,
                $readyForDoctorAt,
                $doctorArrivedAt,
                $doctorCompleteAt,
                $roomAvailableAt,
                $originalDefaultExpectedUnits,
                $expectedAllocationUnits,
                $expectedAllocationMinutes,
                $allocationAdjustedFromDefault,
                $prestageStartedAt,
                $episodeId,
                $sedationState,
                $expectedAllocationState,
                $expectedAllocationSuggestedUnits,
                $expectedAllocationConfirmedUnits,
                $isAddOn,
                $activeReadyHandoffId,
                $acceptedReadyHandoffId,
                $updatedAt
            )
            ON CONFLICT(room_id) DO UPDATE SET
                assigned_doctor_id = excluded.assigned_doctor_id,
                assigned_doctor_display_name = excluded.assigned_doctor_display_name,
                procedure_code = excluded.procedure_code,
                procedure_category = excluded.procedure_category,
                state = excluded.state,
                seated_at = excluded.seated_at,
                aging_started_at = excluded.aging_started_at,
                stale_started_at = excluded.stale_started_at,
                ready_for_doctor_at = excluded.ready_for_doctor_at,
                doctor_arrived_at = excluded.doctor_arrived_at,
                doctor_complete_at = excluded.doctor_complete_at,
                room_available_at = excluded.room_available_at,
                original_default_expected_units = excluded.original_default_expected_units,
                expected_allocation_units = excluded.expected_allocation_units,
                expected_allocation_minutes = excluded.expected_allocation_minutes,
                allocation_adjusted_from_default = excluded.allocation_adjusted_from_default,
                prestage_started_at = excluded.prestage_started_at,
                episode_id = excluded.episode_id,
                sedation_state = excluded.sedation_state,
                expected_allocation_state = excluded.expected_allocation_state,
                expected_allocation_suggested_units = excluded.expected_allocation_suggested_units,
                expected_allocation_confirmed_units = excluded.expected_allocation_confirmed_units,
                is_add_on = excluded.is_add_on,
                active_ready_handoff_id = excluded.active_ready_handoff_id,
                accepted_ready_handoff_id = excluded.accepted_ready_handoff_id,
                updated_at = excluded.updated_at;
            """;

        command.Parameters.AddWithValue("$roomId", room.RoomId);
        command.Parameters.AddWithValue("$assignedDoctorId", ToDbValue(room.AssignedDoctor));
        command.Parameters.AddWithValue("$assignedDoctorDisplayName", ToDbValue(room.AssignedDoctorDisplayName));
        command.Parameters.AddWithValue("$procedureCode", ToDbValue(room.ProcedureCode));
        command.Parameters.AddWithValue("$procedureCategory", ToDbValue(room.ProcedureCategory));
        command.Parameters.AddWithValue("$state", room.State);
        command.Parameters.AddWithValue("$seatedAt", ToDbValue(room.SeatedAt));
        command.Parameters.AddWithValue("$agingStartedAt", ToDbValue(room.AgingStartedAt));
        command.Parameters.AddWithValue("$staleStartedAt", ToDbValue(room.StaleStartedAt));
        command.Parameters.AddWithValue("$readyForDoctorAt", ToDbValue(room.ReadyForDoctorAt));
        command.Parameters.AddWithValue("$doctorArrivedAt", ToDbValue(room.DoctorArrivedAt));
        command.Parameters.AddWithValue("$doctorCompleteAt", ToDbValue(room.DoctorCompleteAt));
        command.Parameters.AddWithValue("$roomAvailableAt", ToDbValue(room.RoomAvailableAt));
        command.Parameters.AddWithValue("$originalDefaultExpectedUnits", room.OriginalDefaultExpectedUnits);
        command.Parameters.AddWithValue("$expectedAllocationUnits", room.ExpectedAllocationUnits);
        command.Parameters.AddWithValue("$expectedAllocationMinutes", room.ExpectedAllocationMinutes);
        command.Parameters.AddWithValue("$allocationAdjustedFromDefault", room.AllocationAdjustedFromDefault ? 1 : 0);
        command.Parameters.AddWithValue("$prestageStartedAt", ToDbValue(room.PrestageStartedAt));
        command.Parameters.AddWithValue("$episodeId", ToDbValue(room.EpisodeId));
        command.Parameters.AddWithValue("$sedationState", ToDbValue(room.SedationState));
        command.Parameters.AddWithValue("$expectedAllocationState", ToDbValue(room.ExpectedAllocationState));
        command.Parameters.AddWithValue("$expectedAllocationSuggestedUnits", ToDbValue(room.ExpectedAllocationSuggestedUnits));
        command.Parameters.AddWithValue("$expectedAllocationConfirmedUnits", ToDbValue(room.ExpectedAllocationConfirmedUnits));
        command.Parameters.AddWithValue("$isAddOn", room.IsAddOn ? 1 : 0);
        command.Parameters.AddWithValue("$activeReadyHandoffId", ToDbValue(room.ActiveReadyHandoffId));
        command.Parameters.AddWithValue("$acceptedReadyHandoffId", ToDbValue(room.AcceptedReadyHandoffId));
        command.Parameters.AddWithValue("$updatedAt", FormatDateTimeOffset(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    private static RoomState CopyRoomForPersistence(RoomState room) =>
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
            IsAddOn = room.IsAddOn,
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

    private static PersistedReadyHandoff CopyReadyHandoff(
        PersistedReadyHandoff handoff,
        DateTimeOffset? withdrawnAt = null,
        DateTimeOffset? acceptedAt = null,
        DateTimeOffset? terminatedAt = null,
        string? terminationKind = null) =>
        new()
        {
            HandoffId = handoff.HandoffId,
            EpisodeId = handoff.EpisodeId,
            RoomId = handoff.RoomId,
            ReadyAt = handoff.ReadyAt,
            WithdrawnAt = withdrawnAt ?? handoff.WithdrawnAt,
            AcceptedAt = acceptedAt ?? handoff.AcceptedAt,
            TerminatedAt = terminatedAt ?? handoff.TerminatedAt,
            TerminationKind = terminationKind ?? handoff.TerminationKind,
            Assignment = handoff.Assignment
        };

    private static CompletedRoomCycle CopyCompletedCycleForPersistence(CompletedRoomCycle cycle) =>
        new()
        {
            CompletedCycleId = cycle.CompletedCycleId,
            EpisodeId = cycle.EpisodeId,
            AcceptedReadyHandoffId = cycle.AcceptedReadyHandoffId,
            RoomId = cycle.RoomId,
            AssignedDoctor = cycle.AssignedDoctor,
            ProcedureCode = cycle.ProcedureCode,
            PrestageStartedAt = cycle.PrestageStartedAt,
            SeatedAt = cycle.SeatedAt,
            ReadyForDoctorAt = cycle.ReadyForDoctorAt,
            DoctorArrivedAt = cycle.DoctorArrivedAt,
            DoctorCompleteAt = cycle.DoctorCompleteAt,
            RoomAvailableAt = cycle.RoomAvailableAt,
            SeatedToDoctorSeconds = cycle.SeatedToDoctorSeconds,
            PrepSeconds = cycle.PrepSeconds,
            ReadyToDoctorSeconds = cycle.ReadyToDoctorSeconds,
            DoctorInRoomSeconds = cycle.DoctorInRoomSeconds,
            TurnoverSeconds = cycle.TurnoverSeconds,
            TotalRoomCycleSeconds = cycle.TotalRoomCycleSeconds,
            OriginalDefaultExpectedUnits = cycle.OriginalDefaultExpectedUnits,
            ExpectedAllocationUnits = cycle.ExpectedAllocationUnits,
            ExpectedAllocationMinutes = cycle.ExpectedAllocationMinutes,
            AllocationAdjustedFromDefault = cycle.AllocationAdjustedFromDefault,
            IsAddOn = cycle.IsAddOn,
            FinalWaitState = cycle.FinalWaitState,
            AgingThresholdReached = cycle.AgingThresholdReached,
            StaleThresholdReached = cycle.StaleThresholdReached,
            IsException = cycle.IsException,
            RequiresReview = cycle.RequiresReview,
            ExceptionReason = cycle.ExceptionReason,
            ReviewStatus = cycle.ReviewStatus,
            SuggestedAction = cycle.SuggestedAction,
            ReviewedAt = cycle.ReviewedAt,
            ReviewedBy = cycle.ReviewedBy
        };

    private static AbortedRoomAssignment CopyAbortedAssignmentForPersistence(AbortedRoomAssignment record) =>
        new()
        {
            AbortedAssignmentId = record.AbortedAssignmentId,
            EpisodeId = record.EpisodeId,
            RoomId = record.RoomId,
            AssignedDoctor = record.AssignedDoctor,
            AssignedDoctorDisplayName = record.AssignedDoctorDisplayName,
            ProcedureCode = record.ProcedureCode,
            ProcedureCategory = record.ProcedureCategory,
            SedationState = record.SedationState,
            ExpectedAllocationState = record.ExpectedAllocationState,
            ExpectedAllocationSuggestedUnits = record.ExpectedAllocationSuggestedUnits,
            ExpectedAllocationConfirmedUnits = record.ExpectedAllocationConfirmedUnits,
            TerminalReadyHandoffId = record.TerminalReadyHandoffId,
            IsAddOn = record.IsAddOn,
            OriginalDefaultExpectedUnits = record.OriginalDefaultExpectedUnits,
            ExpectedAllocationUnits = record.ExpectedAllocationUnits,
            ExpectedAllocationMinutes = record.ExpectedAllocationMinutes,
            AllocationAdjustedFromDefault = record.AllocationAdjustedFromDefault,
            PrestageStartedAt = record.PrestageStartedAt,
            SeatedAt = record.SeatedAt,
            ReadyForDoctorAt = record.ReadyForDoctorAt,
            TerminatedAt = record.TerminatedAt,
            TerminatedFromState = record.TerminatedFromState,
            TerminationKind = record.TerminationKind,
            CancellationReason = record.CancellationReason,
            IsException = record.IsException,
            RequiresReview = record.RequiresReview,
            ExceptionReason = record.ExceptionReason,
            ReviewStatus = record.ReviewStatus,
            SuggestedAction = record.SuggestedAction,
            ReviewedAt = record.ReviewedAt,
            ReviewedBy = record.ReviewedBy
        };

    private static void ApplyAcceptedSnapshotToCompletedCycle(
        CompletedRoomCycle cycle,
        PersistedReadyHandoff handoff)
    {
        var assignment = handoff.Assignment.ToContract();
        if (assignment.Completeness != AssignmentCompleteness.Complete)
        {
            throw new InvalidOperationException("Accepted Ready handoff snapshot must contain a complete assignment.");
        }

        var confirmedUnits = assignment.ExpectedAllocation.ConfirmedValue
            ?? throw new InvalidOperationException("Accepted Ready handoff snapshot must contain confirmed allocation.");
        var suggestedUnits = assignment.ExpectedAllocation.SuggestedValue;

        cycle.AcceptedReadyHandoffId = handoff.HandoffId;
        cycle.AssignedDoctor = assignment.DoctorId!;
        cycle.ProcedureCode = assignment.ProcedureCode!;
        cycle.OriginalDefaultExpectedUnits = suggestedUnits ?? confirmedUnits;
        cycle.ExpectedAllocationUnits = confirmedUnits;
        cycle.ExpectedAllocationMinutes = confirmedUnits * 10;
        cycle.AllocationAdjustedFromDefault = suggestedUnits.HasValue && suggestedUnits.Value != confirmedUnits;
        cycle.IsAddOn = assignment.IsAddOn;
    }

    private static void ApplyAssignment(RoomState room, PersistedRoomAssignment assignment)
    {
        room.AssignedDoctor = assignment.DoctorId;
        room.AssignedDoctorDisplayName = assignment.DoctorDisplayName;
        room.ProcedureCode = assignment.ProcedureCode;
        room.ProcedureCategory = assignment.ProcedureCategory;
        room.SedationState = assignment.SedationState;
        room.ExpectedAllocationState = assignment.ExpectedAllocationState;
        room.ExpectedAllocationSuggestedUnits = assignment.ExpectedAllocationSuggestedUnits;
        room.ExpectedAllocationConfirmedUnits = assignment.ExpectedAllocationConfirmedUnits;
        room.IsAddOn = assignment.IsAddOn;

        if (assignment.ExpectedAllocationConfirmedUnits is { } confirmedUnits)
        {
            room.OriginalDefaultExpectedUnits = assignment.ExpectedAllocationSuggestedUnits ?? confirmedUnits;
            room.ExpectedAllocationUnits = confirmedUnits;
            room.ExpectedAllocationMinutes = confirmedUnits * 10;
            room.AllocationAdjustedFromDefault =
                assignment.ExpectedAllocationSuggestedUnits.HasValue
                && assignment.ExpectedAllocationSuggestedUnits.Value != confirmedUnits;
        }
        else
        {
            room.OriginalDefaultExpectedUnits = assignment.ExpectedAllocationSuggestedUnits ?? 0;
            room.ExpectedAllocationUnits = 0;
            room.ExpectedAllocationMinutes = 0;
            room.AllocationAdjustedFromDefault = false;
        }
    }

    private static string? ResolveProcedureCategory(
        IReadOnlyList<ProcedureCategory> procedures,
        string? procedureCode)
    {
        if (string.IsNullOrWhiteSpace(procedureCode))
        {
            return null;
        }

        var exact = procedures.FirstOrDefault(item =>
            string.Equals(item.Code, procedureCode, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Id, procedureCode, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact.Label;
        }

        const string sedationSuffix = "+SED";
        if (procedureCode.EndsWith(sedationSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var baseCode = procedureCode[..^sedationSuffix.Length];
            var baseProcedure = procedures.FirstOrDefault(item =>
                string.Equals(item.Code, baseCode, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Id, baseCode, StringComparison.OrdinalIgnoreCase));
            if (baseProcedure is not null)
            {
                return $"{baseProcedure.Label} + Sedation";
            }
        }

        return procedureCode;
    }

    private static string? ResolveDoctorDisplayName(IReadOnlyList<Doctor> doctors, string? doctorId) =>
        string.IsNullOrWhiteSpace(doctorId)
            ? null
            : doctors.FirstOrDefault(item => item.Id == doctorId)?.Name ?? doctorId;

    private static void ValidateRequiredId(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty identifier is required.", parameterName);
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static string FormatDateTimeOffset(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O");

    private static object ToDbValue(DateTimeOffset? value) =>
        value.HasValue ? FormatDateTimeOffset(value.Value) : DBNull.Value;

    private static object ToDbValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static object ToDbValue<TEnum>(TEnum? value)
        where TEnum : struct, Enum =>
        value.HasValue ? value.Value.ToString() : DBNull.Value;

    private static object ToDbValue(int? value) =>
        value.HasValue ? value.Value : DBNull.Value;

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? ReadNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static TEnum? ReadNullableEnum<TEnum>(SqliteDataReader reader, int ordinal)
        where TEnum : struct, Enum =>
        reader.IsDBNull(ordinal)
            ? null
            : Enum.Parse<TEnum>(reader.GetString(ordinal));

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));

    private static DateTimeOffset ReadRequiredDateTimeOffset(SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.Parse(reader.GetString(ordinal));
}
