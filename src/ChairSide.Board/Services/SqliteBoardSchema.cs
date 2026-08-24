using System.Security;

using ChairSide.Board.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Services;

internal static class SqliteBoardSchema
{
    internal static SqliteBoardInitialization Initialize(
        IOptions<BoardPersistenceOptions> options,
        IWebHostEnvironment environment,
        DeploymentEnvironment deploymentEnvironment,
        DatabaseIsolationPolicy databaseIsolationPolicy,
        DatabaseDeploymentIdentityPolicy databaseDeploymentIdentityPolicy,
        DatabaseInitializationTestHooks initializationHooks)
    {
        ArgumentNullException.ThrowIfNull(initializationHooks);
        var databasePath = databaseIsolationPolicy.ResolveAndValidate(
            options.Value.DatabasePath,
            environment.ContentRootPath,
            deploymentEnvironment);
        var directory = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("SQLite database path must include a directory.");
        }

        try
        {
            initializationHooks.BeforeDirectoryPreparation?.Invoke();
            Directory.CreateDirectory(directory);
            databaseIsolationPolicy.RescanDeployedPath(databasePath, deploymentEnvironment);
            VerifyDirectoryWritable(directory);
        }
        catch (DatabaseIsolationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or SecurityException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            throw new DatabaseIsolationException(
                $"Unable to prepare the SQLite database directory '{directory}'. Startup is refused.",
                exception);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            DefaultTimeout = 5
        }.ToString();

        if (deploymentEnvironment.IsDevelopment)
        {
            InitializeDevelopment(
                databasePath,
                connectionString,
                databaseDeploymentIdentityPolicy,
                deploymentEnvironment,
                initializationHooks);
        }
        else
        {
            InitializeDeployed(
                databasePath,
                databaseDeploymentIdentityPolicy,
                deploymentEnvironment,
                initializationHooks);
        }

        return new SqliteBoardInitialization(databasePath, connectionString);
    }

    private static void InitializeDevelopment(
        string databasePath,
        string connectionString,
        DatabaseDeploymentIdentityPolicy identityPolicy,
        DeploymentEnvironment deploymentEnvironment,
        DatabaseInitializationTestHooks initializationHooks)
    {
        var inspectExistingIdentity = File.Exists(databasePath) && new FileInfo(databasePath).Length > 0;
        if (inspectExistingIdentity)
        {
            using var readOnlyConnection = identityPolicy.OpenExistingReadOnly(databasePath);
            identityPolicy.ValidateConnection(readOnlyConnection, deploymentEnvironment);
        }

        using var connection = inspectExistingIdentity
            ? identityPolicy.OpenExistingReadWrite(databasePath)
            : OpenConnection(connectionString);
        if (inspectExistingIdentity)
        {
            using var validationTransaction = connection.BeginTransaction(deferred: false);
            identityPolicy.ValidateConnection(connection, deploymentEnvironment, validationTransaction);
            validationTransaction.Commit();
        }

        EnableWal(connection, deploymentEnvironment, initializationHooks);
        InitializeSchemaAndMigrations(connection);
    }

    private static void InitializeDeployed(
        string databasePath,
        DatabaseDeploymentIdentityPolicy identityPolicy,
        DeploymentEnvironment deploymentEnvironment,
        DatabaseInitializationTestHooks initializationHooks)
    {
        var fileState = identityPolicy.ClassifyDeployedDatabase(databasePath);
        if (fileState == DeployedDatabaseFileState.New)
        {
            using var connection = identityPolicy.OpenReadWrite(databasePath);
            try
            {
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    identityPolicy.CreateIdentity(
                        connection,
                        transaction,
                        deploymentEnvironment);
                    CreateCurrentSchema(connection, transaction);
                    initializationHooks.AfterFreshSchemaCreatedBeforeCommit?.Invoke();
                    transaction.Commit();
                }
            }
            catch (DatabaseIsolationException)
            {
                throw;
            }
            catch (Exception exception) when (exception is SqliteException
                                              or InvalidOperationException
                                              or IOException
                                              or UnauthorizedAccessException)
            {
                throw new DatabaseIsolationException(
                    $"Unable to atomically initialize the new {deploymentEnvironment.EnvironmentName} database. Startup is refused.",
                    exception);
            }

            EnableWal(connection, deploymentEnvironment, initializationHooks);
            return;
        }

        using (var readOnlyConnection = identityPolicy.OpenExistingReadOnly(databasePath))
        {
            identityPolicy.ValidateConnection(readOnlyConnection, deploymentEnvironment);
        }

        using var writeConnection = identityPolicy.OpenExistingReadWrite(databasePath);
        try
        {
            using var validationTransaction = writeConnection.BeginTransaction(deferred: false);
            identityPolicy.ValidateConnection(writeConnection, deploymentEnvironment, validationTransaction);
            validationTransaction.Commit();
        }
        catch (DatabaseIsolationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            throw new DatabaseIsolationException(
                $"Unable to obtain the SQLite lock required to revalidate the {deploymentEnvironment.EnvironmentName} deployment identity. Startup is refused.",
                exception);
        }

        EnableWal(writeConnection, deploymentEnvironment, initializationHooks);
        InitializeSchemaAndMigrations(writeConnection);
    }

    internal static void CreateCurrentSchema(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {

        // Create tables (or no-op if they already exist).
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
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
                    is_add_on INTEGER NOT NULL DEFAULT 0 CHECK (is_add_on IN (0, 1)),
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
                    is_add_on INTEGER NOT NULL DEFAULT 0 CHECK (is_add_on IN (0, 1)),
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
                    is_add_on INTEGER NOT NULL DEFAULT 0 CHECK (is_add_on IN (0, 1)),
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
                    is_exception INTEGER NOT NULL DEFAULT 0,
                    requires_review INTEGER NOT NULL DEFAULT 0,
                    exception_reason TEXT NULL,
                    review_status TEXT NOT NULL DEFAULT 'PendingReview',
                    suggested_action TEXT NULL,
                    reviewed_at TEXT NULL,
                    reviewed_by TEXT NULL,
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
                    is_add_on INTEGER NOT NULL DEFAULT 0 CHECK (is_add_on IN (0, 1)),
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

        CreateReadyHandoffIndexes(connection, transaction);
        CreateHistoricalQueryIndexes(connection, transaction);
    }

    private static void InitializeSchemaAndMigrations(SqliteConnection connection)
    {
        CreateCurrentSchema(connection);

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
        TryAddColumn(connection, "ALTER TABLE active_rooms ADD COLUMN is_add_on INTEGER NOT NULL DEFAULT 0 CHECK (is_add_on IN (0, 1))");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN prestage_started_at TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN episode_id TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN accepted_ready_handoff_id TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE completed_room_cycles ADD COLUMN is_add_on INTEGER NOT NULL DEFAULT 0 CHECK (is_add_on IN (0, 1))");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN sedation_state TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN expected_allocation_state TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN expected_allocation_suggested_units INTEGER NULL");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN expected_allocation_confirmed_units INTEGER NULL");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN terminal_ready_handoff_id TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN is_add_on INTEGER NOT NULL DEFAULT 0 CHECK (is_add_on IN (0, 1))");
        TryAddColumn(connection, "ALTER TABLE ready_handoffs ADD COLUMN is_add_on INTEGER NOT NULL DEFAULT 0 CHECK (is_add_on IN (0, 1))");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN is_exception INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN requires_review INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN exception_reason TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN review_status TEXT NOT NULL DEFAULT 'PendingReview'");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN suggested_action TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN reviewed_at TEXT NULL");
        TryAddColumn(connection, "ALTER TABLE aborted_room_assignments ADD COLUMN reviewed_by TEXT NULL");
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
        CreateHistoricalQueryIndexes(connection);
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
            is_add_on INTEGER NOT NULL DEFAULT 0 CHECK (is_add_on IN (0, 1)),
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
        "is_add_on",
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
            is_add_on INTEGER NOT NULL DEFAULT 0 CHECK (is_add_on IN (0, 1)),
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
            is_exception INTEGER NOT NULL DEFAULT 0,
            requires_review INTEGER NOT NULL DEFAULT 0,
            exception_reason TEXT NULL,
            review_status TEXT NOT NULL DEFAULT 'PendingReview',
            suggested_action TEXT NULL,
            reviewed_at TEXT NULL,
            reviewed_by TEXT NULL,
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
        "is_add_on",
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
    internal static void MigrateNullableDoctorArrivedAt(SqliteConnection connection)
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

    private static void CreateReadyHandoffIndexes(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private static void CreateHistoricalQueryIndexes(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        var completedColumns = GetColumnNames(connection, "completed_room_cycles");
        if (completedColumns.Contains("id"))
        {
            ExecuteOptional(connection, transaction, """
                CREATE INDEX IF NOT EXISTS ix_completed_cycles_report_window
                    ON completed_room_cycles(doctor_complete_at DESC, id DESC);
                """);
        }

        if (completedColumns.Contains("id")
            && completedColumns.Contains("is_exception")
            && completedColumns.Contains("requires_review")
            && completedColumns.Contains("prestage_started_at"))
        {
            ExecuteOptional(connection, transaction, """
                CREATE INDEX IF NOT EXISTS ix_completed_cycles_review_window
                    ON completed_room_cycles(
                        COALESCE(doctor_complete_at, doctor_arrived_at, seated_at, prestage_started_at) DESC,
                        requires_review,
                        id DESC)
                    WHERE is_exception = 1;
                """);
        }

        var abortedColumns = GetColumnNames(connection, "aborted_room_assignments");
        if (abortedColumns.Contains("id")
            && abortedColumns.Contains("is_exception")
            && abortedColumns.Contains("requires_review"))
        {
            ExecuteOptional(connection, transaction, """
                CREATE INDEX IF NOT EXISTS ix_aborted_assignments_review_window
                    ON aborted_room_assignments(terminated_at DESC, requires_review, id DESC)
                    WHERE is_exception = 1;
                """);
        }
    }

    private static void EnableWal(
        SqliteConnection connection,
        DeploymentEnvironment deploymentEnvironment,
        DatabaseInitializationTestHooks initializationHooks)
    {
        try
        {
            initializationHooks.BeforeEnableWal?.Invoke();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
            command.ExecuteNonQuery();
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            throw new DatabaseIsolationException(
                $"Unable to configure SQLite WAL mode for {deploymentEnvironment.EnvironmentName}. Startup is refused.",
                exception);
        }
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

    private static void ExecuteOptional(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        if (transaction is null)
        {
            Execute(connection, sql);
        }
        else
        {
            Execute(connection, transaction, sql);
        }
    }

    internal static void TryAddColumn(SqliteConnection connection, string alterTableSql)
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


    private static SqliteConnection OpenConnection(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
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

}

internal sealed record SqliteBoardInitialization(string DatabasePath, string ConnectionString);

internal sealed record DatabaseInitializationTestHooks(
    Action? BeforeDirectoryPreparation = null,
    Action? BeforeEnableWal = null,
    Action? AfterFreshSchemaCreatedBeforeCommit = null)
{
    public static DatabaseInitializationTestHooks None { get; } = new();
}
