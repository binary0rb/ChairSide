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
        IWebHostEnvironment environment)
    {
        _databasePath = ResolveDatabasePath(options.Value.DatabasePath, environment.ContentRootPath);
        var directory = Path.GetDirectoryName(_databasePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("SQLite database path must include a directory.");
        }

        ValidateDatabasePath(environment, directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            DefaultTimeout = 5
        }.ToString();

        Initialize();
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
                accepted_ready_handoff_id
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
                AcceptedReadyHandoffId = ReadNullableString(reader, 24)
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
                accepted_ready_handoff_id
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
                AcceptedReadyHandoffId = ReadNullableString(reader, 31)
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
            expectation.ExpectedAllocationConfirmedUnits);
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
                terminal_ready_handoff_id
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
                TerminalReadyHandoffId = ReadNullableString(reader, 22)
            });
        }

        return records;
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
            expected_allocation_confirmed_units
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
                    reader.GetInt32(13))
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
                expected_allocation_confirmed_units
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
                $expectedAllocationConfirmedUnits
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

    private void Initialize()
    {
        using var connection = OpenConnection();

        // Create tables (or no-op if they already exist).
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA busy_timeout = 5000;

                CREATE TABLE IF NOT EXISTS active_rooms (
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
                    sedation_state TEXT NULL,
                    expected_allocation_state TEXT NULL,
                    expected_allocation_suggested_units INTEGER NULL,
                    expected_allocation_confirmed_units INTEGER NULL,
                    active_ready_handoff_id TEXT NULL,
                    accepted_ready_handoff_id TEXT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS completed_room_cycles (
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
                    accepted_ready_handoff_id TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(room_id, seated_at)
                );

                CREATE TABLE IF NOT EXISTS aborted_room_assignments (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    episode_id TEXT NOT NULL,
                    room_id INTEGER NOT NULL,
                    assigned_doctor_id TEXT NULL,
                    assigned_doctor_display_name TEXT NULL,
                    procedure_code TEXT NULL,
                    procedure_category TEXT NULL,
                    sedation_state TEXT NULL,
                    expected_allocation_state TEXT NULL,
                    expected_allocation_suggested_units INTEGER NULL,
                    expected_allocation_confirmed_units INTEGER NULL,
                    terminal_ready_handoff_id TEXT NULL,
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

                CREATE TABLE IF NOT EXISTS ready_handoffs (
                    handoff_id TEXT PRIMARY KEY CHECK (length(trim(handoff_id)) > 0),
                    episode_id TEXT NOT NULL CHECK (length(trim(episode_id)) > 0),
                    room_id INTEGER NOT NULL,
                    ready_at TEXT NOT NULL CHECK (length(trim(ready_at)) > 0),
                    withdrawn_at TEXT NULL,
                    accepted_at TEXT NULL,
                    terminated_at TEXT NULL,
                    termination_kind TEXT NULL CHECK (termination_kind IS NULL OR termination_kind IN ('Canceled', 'Expired')),
                    doctor_id TEXT NOT NULL CHECK (length(trim(doctor_id)) > 0),
                    procedure_code TEXT NOT NULL CHECK (length(trim(procedure_code)) > 0),
                    sedation_state TEXT NOT NULL CHECK (sedation_state IN ('UnavailableProcedureIneligible', 'EligibleYes', 'EligibleNo')),
                    expected_allocation_state TEXT NOT NULL CHECK (expected_allocation_state IN ('ConfirmedSuggestedValue', 'ConfirmedAdjustedValue')),
                    expected_allocation_suggested_units INTEGER NULL CHECK (expected_allocation_suggested_units IS NULL OR expected_allocation_suggested_units > 0),
                    expected_allocation_confirmed_units INTEGER NOT NULL CHECK (expected_allocation_confirmed_units > 0),
                    CHECK (
                        (CASE WHEN withdrawn_at IS NULL THEN 0 ELSE 1 END)
                        + (CASE WHEN accepted_at IS NULL THEN 0 ELSE 1 END)
                        + (CASE WHEN terminated_at IS NULL THEN 0 ELSE 1 END) <= 1
                    ),
                    CHECK ((terminated_at IS NULL AND termination_kind IS NULL) OR (terminated_at IS NOT NULL AND termination_kind IS NOT NULL)),
                    CHECK (withdrawn_at IS NULL OR withdrawn_at >= ready_at),
                    CHECK (accepted_at IS NULL OR accepted_at >= ready_at),
                    CHECK (terminated_at IS NULL OR terminated_at >= ready_at),
                    CHECK (
                        (expected_allocation_state = 'ConfirmedSuggestedValue'
                            AND expected_allocation_suggested_units IS NOT NULL
                            AND expected_allocation_confirmed_units = expected_allocation_suggested_units)
                        OR (expected_allocation_state = 'ConfirmedAdjustedValue'
                            AND (expected_allocation_suggested_units IS NULL
                                OR expected_allocation_confirmed_units <> expected_allocation_suggested_units))
                    )
                );
                """;
            command.ExecuteNonQuery();
        }

        CreateReadyHandoffIndexes(connection);

        // Migration: add new columns to existing databases that predate this schema version.
        // Each ALTER TABLE is attempted independently. Duplicate-column failures are benign
        // idempotency signals; every other SQLite error should stop startup with context.
        TryAddColumn(connection, "ALTER TABLE active_rooms ADD COLUMN ready_for_doctor_at TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN ready_for_doctor_at TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN prep_seconds INTEGER NULL");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN ready_to_doctor_seconds INTEGER NULL");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN is_exception INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN requires_review INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN exception_reason TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN review_status TEXT NOT NULL DEFAULT 'PendingReview'");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN suggested_action TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN reviewed_at TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN reviewed_by TEXT NULL");

        // Expected allocation snapshot (operational, non-PHI). Additive on both tables; existing
        // rows default to 0 (no allocation captured), which is harmless for historical records.
        TryAddColumn(connection, "ALTER TABLE active_rooms ADD COLUMN original_default_expected_units INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(connection, "ALTER TABLE active_rooms ADD COLUMN expected_allocation_units INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(connection, "ALTER TABLE active_rooms ADD COLUMN expected_allocation_minutes INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(connection, "ALTER TABLE active_rooms ADD COLUMN allocation_adjusted_from_default INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN original_default_expected_units INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN expected_allocation_units INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN expected_allocation_minutes INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN allocation_adjusted_from_default INTEGER NOT NULL DEFAULT 0");

        // Prestaging phase (operational, non-PHI). Additive on both tables; existing rows default to
        // null (no prestage phase / episode recorded), so legacy rows reload with their current state
        // and are never forced through Prestaging. Added before the rebuild migrations below so those
        // rebuilds' shared-column copies pick these up on any table they touch.
        TryAddColumn(connection, "ALTER TABLE active_rooms ADD COLUMN prestage_started_at TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE active_rooms ADD COLUMN episode_id TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE active_rooms ADD COLUMN sedation_state TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE active_rooms ADD COLUMN expected_allocation_state TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE active_rooms ADD COLUMN expected_allocation_suggested_units INTEGER NULL");
        TryAddColumn(connection, "ALTER TABLE active_rooms ADD COLUMN expected_allocation_confirmed_units INTEGER NULL");
        TryAddColumn(connection, "ALTER TABLE active_rooms ADD COLUMN active_ready_handoff_id TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE active_rooms ADD COLUMN accepted_ready_handoff_id TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN prestage_started_at TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN episode_id TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN accepted_ready_handoff_id TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN sedation_state TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN expected_allocation_state TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN expected_allocation_suggested_units INTEGER NULL");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN expected_allocation_confirmed_units INTEGER NULL");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN terminal_ready_handoff_id TEXT NULL");
        ApplyLosslessCanonicalBackfills(connection);

        // Migration: ensure completed_room_cycles has an explicit id primary key column.
        // The table has declared "id INTEGER PRIMARY KEY AUTOINCREMENT" since its first version,
        // so this is a defensive no-op for all known databases. It exists to safely backfill a
        // stable identity onto any legacy table that somehow predates the explicit id column.
        // Runs before the nullable migration so that migration's "SELECT id" remains valid.
        EnsureCompletedCycleIdColumn(connection);

        // Migration: make doctor_arrived_at nullable to support force-expired cycles where
        // the doctor never arrived. ALTER TABLE cannot change NOT NULL in SQLite, so we recreate
        // the table if needed. Wrapped in a transaction - safe to retry on restart.
        MigrateNullableDoctorArrivedAt(connection);
        MigrateAbortedAssignmentCanonicalSchema(connection);
        CreateReadyHandoffIndexes(connection);
    }

    // Canonical CREATE for completed_room_cycles (current schema: explicit id primary key,
    // nullable doctor_arrived_at). Used by the id-backfill rebuild. Not "IF NOT EXISTS" because
    // the rebuild always creates a fresh table after renaming the legacy one out of the way.
    private const string CanonicalCompletedCycleCreateSql = """
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
            accepted_ready_handoff_id TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(room_id, seated_at)
        );
        """;

    // Canonical non-id columns in CanonicalCompletedCycleCreateSql order. Used to copy the
    // intersection of old and new columns during the id-backfill rebuild.
    private static readonly string[] CanonicalCompletedCycleColumns =
    [
        "room_id",
        "assigned_doctor_id",
        "assigned_doctor_display_name",
        "procedure_code",
        "procedure_category",
        "seated_at",
        "ready_for_doctor_at",
        "doctor_arrived_at",
        "doctor_complete_at",
        "room_available_at",
        "seated_to_doctor_seconds",
        "prep_seconds",
        "ready_to_doctor_seconds",
        "doctor_in_room_seconds",
        "turnover_seconds",
        "total_room_cycle_seconds",
        "original_default_expected_units",
        "expected_allocation_units",
        "expected_allocation_minutes",
        "allocation_adjusted_from_default",
        "final_wait_state",
        "aging_threshold_reached",
        "stale_threshold_reached",
        "is_exception",
        "requires_review",
        "exception_reason",
        "review_status",
        "suggested_action",
        "reviewed_at",
        "reviewed_by",
        "prestage_started_at",
        "episode_id",
        "accepted_ready_handoff_id",
        "created_at",
        "updated_at"
    ];

    private const string CanonicalAbortedAssignmentCreateSql = """
        CREATE TABLE aborted_room_assignments (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            episode_id TEXT NOT NULL,
            room_id INTEGER NOT NULL,
            assigned_doctor_id TEXT NULL,
            assigned_doctor_display_name TEXT NULL,
            procedure_code TEXT NULL,
            procedure_category TEXT NULL,
            sedation_state TEXT NULL,
            expected_allocation_state TEXT NULL,
            expected_allocation_suggested_units INTEGER NULL,
            expected_allocation_confirmed_units INTEGER NULL,
            terminal_ready_handoff_id TEXT NULL,
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
        """;

    private static readonly string[] CanonicalAbortedAssignmentColumns =
    [
        "episode_id",
        "room_id",
        "assigned_doctor_id",
        "assigned_doctor_display_name",
        "procedure_code",
        "procedure_category",
        "sedation_state",
        "expected_allocation_state",
        "expected_allocation_suggested_units",
        "expected_allocation_confirmed_units",
        "terminal_ready_handoff_id",
        "original_default_expected_units",
        "expected_allocation_units",
        "expected_allocation_minutes",
        "allocation_adjusted_from_default",
        "prestage_started_at",
        "seated_at",
        "ready_for_doctor_at",
        "terminated_at",
        "terminated_from_state",
        "termination_kind",
        "cancellation_reason",
        "created_at",
        "updated_at"
    ];

    /// <summary>
    /// Ensures completed_room_cycles has an explicit id INTEGER PRIMARY KEY AUTOINCREMENT column.
    /// Idempotent. No-op when the table does not exist yet or already has an id column (the case
    /// for every known database). When an id column is somehow missing, the table is recreated
    /// preserving all existing columns and data; SQLite assigns a fresh autoincrement id to every
    /// row, which backfills a unique stable identity. Only columns shared between the old and new
    /// schema are copied, so the rebuild is safe even if the legacy table predates newer columns.
    /// </summary>
    private static void EnsureCompletedCycleIdColumn(SqliteConnection connection)
    {
        if (!TableExists(connection, "completed_room_cycles"))
        {
            return;
        }

        var existingColumns = GetColumnNames(connection, "completed_room_cycles");
        if (existingColumns.Contains("id"))
        {
            return;
        }

        // Columns common to both the legacy table and the canonical schema, in canonical order.
        // The id column is intentionally excluded so SQLite assigns fresh autoincrement values.
        var sharedColumns = CanonicalCompletedCycleColumns
            .Where(column => existingColumns.Contains(column))
            .ToList();
        var columnList = string.Join(",\n                ", sharedColumns);

        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction, "ALTER TABLE completed_room_cycles RENAME TO completed_room_cycles_preid;");
        Execute(connection, transaction, CanonicalCompletedCycleCreateSql);
        Execute(connection, transaction, $"""
            INSERT INTO completed_room_cycles (
                {columnList}
            )
            SELECT
                {columnList}
            FROM completed_room_cycles_preid;
            """);
        Execute(connection, transaction, "DROP TABLE completed_room_cycles_preid;");

        transaction.Commit();
    }

    /// <summary>
    /// Recreates completed_room_cycles with a nullable doctor_arrived_at column if the existing
    /// schema has it as NOT NULL. Idempotent - no-op if the column is already nullable or if
    /// the table does not exist yet.
    /// </summary>
    private static void MigrateNullableDoctorArrivedAt(SqliteConnection connection)
    {
        if (!TableExists(connection, "completed_room_cycles"))
        {
            return;
        }

        // Check whether doctor_arrived_at currently has a NOT NULL constraint.
        var needsMigration = false;
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(completed_room_cycles);";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "doctor_arrived_at", StringComparison.Ordinal) &&
                    reader.GetInt32(3) == 1)
                {
                    needsMigration = true;
                }
            }
        }

        if (!needsMigration)
        {
            return;
        }

        // Recreate the table allowing NULL for doctor_arrived_at.
        // All steps are inside one transaction - SQLite rolls DDL back on failure.
        using var transaction = connection.BeginTransaction();

        Execute(connection, transaction, "ALTER TABLE completed_room_cycles RENAME TO completed_room_cycles_v1;");
        Execute(connection, transaction, """
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
                accepted_ready_handoff_id TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(room_id, seated_at)
            );
            """);
        Execute(connection, transaction, """
            INSERT INTO completed_room_cycles (
                id,
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
                created_at,
                updated_at
            )
            SELECT
                id,
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
                created_at,
                updated_at
            FROM completed_room_cycles_v1;
            """);
        Execute(connection, transaction, "DROP TABLE completed_room_cycles_v1;");

        transaction.Commit();
    }

    private static void MigrateAbortedAssignmentCanonicalSchema(SqliteConnection connection)
    {
        if (!TableExists(connection, "aborted_room_assignments"))
        {
            return;
        }

        var existingColumns = GetColumnNames(connection, "aborted_room_assignments");
        var needsMigration = !existingColumns.IsSupersetOf(CanonicalAbortedAssignmentColumns);
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(aborted_room_assignments);";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                var columnName = reader.GetString(1);
                if (columnName is "assigned_doctor_id" or "assigned_doctor_display_name" or "procedure_code" or "procedure_category"
                    && reader.GetInt32(3) == 1)
                {
                    needsMigration = true;
                }
            }
        }

        if (!needsMigration)
        {
            return;
        }

        var sharedColumns = CanonicalAbortedAssignmentColumns
            .Where(existingColumns.Contains)
            .ToList();
        var columnList = string.Join(",\n                ", sharedColumns);

        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "ALTER TABLE aborted_room_assignments RENAME TO aborted_room_assignments_v1;");
        Execute(connection, transaction, CanonicalAbortedAssignmentCreateSql);
        Execute(connection, transaction, $"""
            INSERT INTO aborted_room_assignments (
                id,
                {columnList}
            )
            SELECT
                id,
                {columnList}
            FROM aborted_room_assignments_v1;
            """);
        Execute(connection, transaction, "DROP TABLE aborted_room_assignments_v1;");
        transaction.Commit();
    }

    private static void CreateReadyHandoffIndexes(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS ix_ready_handoffs_one_active_per_episode
                ON ready_handoffs(episode_id)
                WHERE withdrawn_at IS NULL AND accepted_at IS NULL AND terminated_at IS NULL;

            CREATE UNIQUE INDEX IF NOT EXISTS ix_ready_handoffs_one_accepted_per_episode
                ON ready_handoffs(episode_id)
                WHERE accepted_at IS NOT NULL;

            CREATE INDEX IF NOT EXISTS ix_ready_handoffs_episode_id
                ON ready_handoffs(episode_id);
            """;
        command.ExecuteNonQuery();
    }

    private static void ApplyLosslessCanonicalBackfills(SqliteConnection connection)
    {
        // Lossless legacy backfills only:
        // - no procedure proves the no-procedure sedation state;
        // - the historical "+SED" suffix proves explicit sedation Yes;
        // - an explicit adjusted-allocation flag with positive, distinct stored values proves a
        //   confirmed adjusted allocation. Unadjusted defaults and zeros remain ambiguous/null.
        ApplyLosslessCanonicalBackfills(connection, "active_rooms");
        ApplyLosslessCanonicalBackfills(connection, "aborted_room_assignments");
    }

    private static void ApplyLosslessCanonicalBackfills(SqliteConnection connection, string tableName)
    {
        if (!TableExists(connection, tableName))
        {
            return;
        }

        Execute(connection, $"""
            UPDATE {tableName}
            SET sedation_state = 'UnavailableNoProcedure'
            WHERE sedation_state IS NULL
                AND procedure_code IS NULL;
            """);
        Execute(connection, $"""
            UPDATE {tableName}
            SET sedation_state = 'EligibleYes'
            WHERE sedation_state IS NULL
                AND upper(procedure_code) LIKE '%+SED';
            """);
        Execute(connection, $"""
            UPDATE {tableName}
            SET expected_allocation_state = 'ConfirmedAdjustedValue',
                expected_allocation_suggested_units = original_default_expected_units,
                expected_allocation_confirmed_units = expected_allocation_units
            WHERE expected_allocation_state IS NULL
                AND allocation_adjusted_from_default = 1
                AND original_default_expected_units > 0
                AND expected_allocation_units > 0
                AND original_default_expected_units <> expected_allocation_units;
            """);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);";
        cmd.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(cmd.ExecuteScalar()) == 1;
    }

    private static HashSet<string> GetColumnNames(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void TryAddColumn(SqliteConnection connection, string alterTableSql)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = alterTableSql;
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (IsDuplicateColumnError(ex))
        {
            // Column already exists on fresh databases - no action needed.
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException(
                $"SQLite migration failed while applying additive column migration: {alterTableSql}",
                ex);
        }
    }

    private static bool IsDuplicateColumnError(SqliteException ex) =>
        ex.SqliteErrorCode == 1
        && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase);

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
            CancellationReason = record.CancellationReason
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

    private static void ValidateDatabasePath(IWebHostEnvironment environment, string databaseDirectory)
    {
        if (environment.IsProduction() && IsPathInsideContentRoot(databaseDirectory, environment.ContentRootPath))
        {
            throw new InvalidOperationException(
                "Production SQLite database path must be outside the deployed app content root. Use an operational data directory such as C:\\ChairSide\\Data\\chairside.db.");
        }

        Directory.CreateDirectory(databaseDirectory);
        VerifyDirectoryWritable(databaseDirectory);
    }

    private static bool IsPathInsideContentRoot(string path, string contentRootPath)
    {
        var fullPath = NormalizeDirectoryPath(path);
        var fullContentRoot = NormalizeDirectoryPath(contentRootPath);
        return fullPath.StartsWith(fullContentRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.EndsWith(Path.DirectorySeparatorChar)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }

    private static void VerifyDirectoryWritable(string directory)
    {
        var testPath = Path.Combine(directory, $".chairside-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(testPath, "write-test");
        }
        finally
        {
            if (File.Exists(testPath))
            {
                File.Delete(testPath);
            }
        }
    }

    private static string ResolveDatabasePath(string databasePath, string contentRootPath) =>
        Path.GetFullPath(Path.IsPathRooted(databasePath)
            ? databasePath
            : Path.Combine(contentRootPath, databasePath));

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
