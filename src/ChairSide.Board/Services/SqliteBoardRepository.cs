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

    public bool HasAnyRoomRows()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM active_rooms LIMIT 1);";
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
                procedure_code,
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
                allocation_adjusted_from_default
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
                ProcedureCode = ReadNullableString(reader, 2),
                State = reader.GetString(3),
                SeatedAt = ReadNullableDateTimeOffset(reader, 4),
                AgingStartedAt = ReadNullableDateTimeOffset(reader, 5),
                StaleStartedAt = ReadNullableDateTimeOffset(reader, 6),
                ReadyForDoctorAt = ReadNullableDateTimeOffset(reader, 7),
                DoctorArrivedAt = ReadNullableDateTimeOffset(reader, 8),
                DoctorCompleteAt = ReadNullableDateTimeOffset(reader, 9),
                RoomAvailableAt = ReadNullableDateTimeOffset(reader, 10),
                OriginalDefaultExpectedUnits = reader.GetInt32(11),
                ExpectedAllocationUnits = reader.GetInt32(12),
                ExpectedAllocationMinutes = reader.GetInt32(13),
                AllocationAdjustedFromDefault = reader.GetInt32(14) == 1
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
                allocation_adjusted_from_default
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
                AllocationAdjustedFromDefault = reader.GetInt32(28) == 1
            });
        }

        return cycles;
    }

    public void SaveCompletedCycle(CompletedRoomCycle cycle, IReadOnlyList<Doctor> doctors, IReadOnlyList<ProcedureCategory> procedures)
    {
        var doctor = doctors.FirstOrDefault(item => item.Id == cycle.AssignedDoctor);
        var procedure = procedures.FirstOrDefault(item => item.Code == cycle.ProcedureCode || item.Id == cycle.ProcedureCode);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
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
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();

        // Capture the stable identity assigned by SQLite. The upsert may either insert a new
        // row or update an existing one, so we read the id back by the natural (room_id, seated_at)
        // key rather than relying on last_insert_rowid(), which is not meaningful on the update path.
        using var idCommand = connection.CreateCommand();
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
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(room_id, seated_at)
                );
                """;
            command.ExecuteNonQuery();
        }

        // Migration: add new columns to existing databases that predate this schema version.
        // Each ALTER TABLE is attempted independently; failure means the column already exists.
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
                created_at,
                updated_at
            FROM completed_room_cycles_v1;
            """);
        Execute(connection, transaction, "DROP TABLE completed_room_cycles_v1;");

        transaction.Commit();
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

    private static void TryAddColumn(SqliteConnection connection, string alterTableSql)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = alterTableSql;
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Column already exists on fresh databases - no action needed.
        }
    }

    private void SaveRoom(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RoomState room,
        IReadOnlyList<Doctor> doctors,
        IReadOnlyList<ProcedureCategory> procedures)
    {
        var doctor = room.AssignedDoctor is null
            ? null
            : doctors.FirstOrDefault(item => item.Id == room.AssignedDoctor);
        var procedure = room.ProcedureCode is null
            ? null
            : procedures.FirstOrDefault(item => item.Code == room.ProcedureCode || item.Id == room.ProcedureCode);

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
                updated_at = excluded.updated_at;
            """;

        command.Parameters.AddWithValue("$roomId", room.RoomId);
        command.Parameters.AddWithValue("$assignedDoctorId", ToDbValue(room.AssignedDoctor));
        command.Parameters.AddWithValue("$assignedDoctorDisplayName", ToDbValue(doctor?.Name));
        command.Parameters.AddWithValue("$procedureCode", ToDbValue(room.ProcedureCode));
        command.Parameters.AddWithValue("$procedureCategory", ToDbValue(procedure?.Label));
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
        command.Parameters.AddWithValue("$updatedAt", FormatDateTimeOffset(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
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

    private static object ToDbValue(int? value) =>
        value.HasValue ? value.Value : DBNull.Value;

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? ReadNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));

    private static DateTimeOffset ReadRequiredDateTimeOffset(SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.Parse(reader.GetString(ordinal));
}
