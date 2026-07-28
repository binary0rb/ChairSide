using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class SqliteBoardSchemaTests
{
    [Fact]
    public void Repeated_initialization_preserves_schema_identity_indexes_and_operational_rows()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var first = CreateRepository(workspace, Environments.Production, databasePath);
        first.EnsureConfiguredRooms(3);

        var before = ReadDatabaseSnapshot(databasePath);
        Assert.Contains("table|active_rooms|", before, StringComparison.Ordinal);
        Assert.Contains("index|ix_ready_handoffs_one_active_per_episode|", before, StringComparison.Ordinal);
        Assert.Contains("identity|Production|1|", before, StringComparison.Ordinal);
        Assert.Contains("rooms|3|6", before, StringComparison.Ordinal);
        Assert.EndsWith("wal", before, StringComparison.Ordinal);

        _ = CreateRepository(workspace, Environments.Production, databasePath);

        Assert.Equal(before, ReadDatabaseSnapshot(databasePath));
    }

    [Fact]
    public void Legacy_migration_applies_only_lossless_canonical_backfills_and_preserves_values()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = Path.Combine(workspace.DataRoot, "legacy-backfills.db");
        using (var connection = Open(databasePath))
        {
            Execute(connection, """
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
                    doctor_arrived_at TEXT NULL,
                    doctor_complete_at TEXT NULL,
                    room_available_at TEXT NULL,
                    original_default_expected_units INTEGER NOT NULL DEFAULT 0,
                    expected_allocation_units INTEGER NOT NULL DEFAULT 0,
                    expected_allocation_minutes INTEGER NOT NULL DEFAULT 0,
                    allocation_adjusted_from_default INTEGER NOT NULL DEFAULT 0,
                    updated_at TEXT NOT NULL
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

                INSERT INTO active_rooms (
                    room_id, state, original_default_expected_units, expected_allocation_units,
                    expected_allocation_minutes, allocation_adjusted_from_default, updated_at)
                VALUES (1, 'prestaging', 0, 0, 0, 0, '2026-07-01T10:00:00.0000000+00:00');

                INSERT INTO active_rooms (
                    room_id, assigned_doctor_id, assigned_doctor_display_name, procedure_code,
                    procedure_category, state, original_default_expected_units,
                    expected_allocation_units, expected_allocation_minutes,
                    allocation_adjusted_from_default, updated_at)
                VALUES (2, 'otte', 'Dr. Otte', 'EXT+SED', 'Extraction + Sedation', 'seated',
                    3, 4, 40, 1, '2026-07-01T10:01:00.0000000+00:00');

                INSERT INTO active_rooms (
                    room_id, assigned_doctor_id, assigned_doctor_display_name, procedure_code,
                    procedure_category, state, original_default_expected_units,
                    expected_allocation_units, expected_allocation_minutes,
                    allocation_adjusted_from_default, updated_at)
                VALUES (3, 'pledger', 'Dr. Pledger', 'EXT', 'Extraction', 'seated',
                    3, 3, 30, 0, '2026-07-01T10:02:00.0000000+00:00');

                INSERT INTO aborted_room_assignments (
                    episode_id, room_id, assigned_doctor_id, assigned_doctor_display_name,
                    procedure_code, procedure_category, original_default_expected_units,
                    expected_allocation_units, expected_allocation_minutes,
                    allocation_adjusted_from_default, terminated_at, terminated_from_state,
                    termination_kind, cancellation_reason, created_at, updated_at)
                VALUES ('abort-adjusted', 4, 'gibson', 'Dr. Gibson', 'IMP+SED',
                    'Implant + Sedation', 4, 5, 50, 1,
                    '2026-07-01T11:00:00.0000000+00:00', 'seated', 'StaffCanceled',
                    'Room no longer needed', '2026-07-01T11:00:00.0000000+00:00',
                    '2026-07-01T11:00:00.0000000+00:00');
                """);
        }

        _ = CreateRepository(workspace, Environments.Development, databasePath);

        using var verify = Open(databasePath);
        AssertCanonicalBackfill(
            verify,
            "active_rooms",
            "room_id = 1",
            expectedSedation: "UnavailableNoProcedure",
            expectedAllocationState: null,
            expectedSuggested: null,
            expectedConfirmed: null);
        AssertCanonicalBackfill(
            verify,
            "active_rooms",
            "room_id = 2",
            expectedSedation: "EligibleYes",
            expectedAllocationState: "ConfirmedAdjustedValue",
            expectedSuggested: 3,
            expectedConfirmed: 4);
        AssertCanonicalBackfill(
            verify,
            "active_rooms",
            "room_id = 3",
            expectedSedation: null,
            expectedAllocationState: null,
            expectedSuggested: null,
            expectedConfirmed: null);
        AssertCanonicalBackfill(
            verify,
            "aborted_room_assignments",
            "episode_id = 'abort-adjusted'",
            expectedSedation: "EligibleYes",
            expectedAllocationState: "ConfirmedAdjustedValue",
            expectedSuggested: 4,
            expectedConfirmed: 5);

        Assert.Equal(
            "Room no longer needed",
            Scalar<string>(
                verify,
                "SELECT cancellation_reason FROM aborted_room_assignments WHERE episode_id = 'abort-adjusted';"));
        Assert.Equal(
            0L,
            Scalar<long>(
                verify,
                """
                SELECT "notnull"
                FROM pragma_table_info('aborted_room_assignments')
                WHERE name = 'assigned_doctor_id';
                """));
        Assert.Equal(0L, Scalar<long>(verify, "SELECT COUNT(*) FROM ready_handoffs;"));
        Assert.Equal(
            3L,
            Scalar<long>(
                verify,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name LIKE 'ix_ready_handoffs_%';"));
    }

    [Fact]
    public void Nullable_doctor_arrived_at_rebuild_preserves_existing_rows()
    {
        using var connection = OpenInMemory();
        CreateCompletedCycleTableWithRequiredDoctorArrived(connection, omitCreatedAt: false);
        InsertCompletedCycle(connection);

        SqliteBoardSchema.MigrateNullableDoctorArrivedAt(connection);

        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM completed_room_cycles;"));
        Assert.Equal(
            "episode-nullable",
            Scalar<string>(connection, "SELECT episode_id FROM completed_room_cycles WHERE id = 1;"));
        Assert.Equal(
            0L,
            Scalar<long>(
                connection,
                """
                SELECT "notnull"
                FROM pragma_table_info('completed_room_cycles')
                WHERE name = 'doctor_arrived_at';
                """));
        Execute(
            connection,
            """
            INSERT INTO completed_room_cycles (
                room_id, assigned_doctor_id, assigned_doctor_display_name, procedure_code,
                procedure_category, seated_at, doctor_arrived_at, seated_to_doctor_seconds,
                final_wait_state, created_at, updated_at)
            VALUES (2, 'pledger', 'Dr. Pledger', 'EXT', 'Extraction',
                '2026-07-02T10:00:00.0000000+00:00', NULL, 0, 'seated',
                '2026-07-02T10:00:00.0000000+00:00',
                '2026-07-02T10:00:00.0000000+00:00');
            """);
        Assert.Equal(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM completed_room_cycles;"));
    }

    [Fact]
    public void Rebuilding_migration_rolls_back_rename_create_and_copy_on_failure()
    {
        using var connection = OpenInMemory();
        CreateCompletedCycleTableWithRequiredDoctorArrived(connection, omitCreatedAt: true);
        InsertCompletedCycle(connection, includeCreatedAt: false);

        Assert.Throws<SqliteException>(() => SqliteBoardSchema.MigrateNullableDoctorArrivedAt(connection));

        Assert.True(TableExists(connection, "completed_room_cycles"));
        Assert.False(TableExists(connection, "completed_room_cycles_v1"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM completed_room_cycles;"));
        Assert.Equal(
            1L,
            Scalar<long>(
                connection,
                """
                SELECT "notnull"
                FROM pragma_table_info('completed_room_cycles')
                WHERE name = 'doctor_arrived_at';
                """));
    }

    [Fact]
    public void Additive_column_migration_ignores_duplicate_column_errors()
    {
        using var connection = OpenInMemory();
        Execute(connection, "CREATE TABLE migration_test (id INTEGER PRIMARY KEY, existing_col TEXT NULL);");

        SqliteBoardSchema.TryAddColumn(
            connection,
            "ALTER TABLE migration_test ADD COLUMN existing_col TEXT NULL");

        Assert.Equal(
            1L,
            Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM pragma_table_info('migration_test') WHERE name = 'existing_col';"));
    }

    [Fact]
    public void Additive_column_migration_throws_non_duplicate_errors_with_context()
    {
        using var connection = OpenInMemory();
        const string alterTableSql = "ALTER TABLE missing_table ADD COLUMN new_col TEXT NULL";

        var exception = Assert.Throws<InvalidOperationException>(
            () => SqliteBoardSchema.TryAddColumn(connection, alterTableSql));

        Assert.Contains("SQLite migration failed", exception.Message);
        Assert.Contains(alterTableSql, exception.Message);
        Assert.IsType<SqliteException>(exception.InnerException);
    }

    [Theory]
    [InlineData("Development", false)]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    [InlineData("Production", true)]
    public void Initialization_hooks_preserve_environment_specific_sequence(
        string environmentName,
        bool existingDatabase)
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = string.Equals(environmentName, Environments.Production, StringComparison.Ordinal)
            ? workspace.ProductionDatabasePath()
            : Path.Combine(workspace.DataRoot, "development-order.db");
        if (existingDatabase)
        {
            _ = CreateRepository(workspace, environmentName, databasePath);
        }

        var observed = new List<string>();
        _ = CreateRepository(
            workspace,
            environmentName,
            databasePath,
            new DatabaseInitializationTestHooks(
                BeforeDirectoryPreparation: () => observed.Add("directory"),
                BeforeEnableWal: () => observed.Add("wal"),
                AfterFreshSchemaCreatedBeforeCommit: () => observed.Add("fresh-schema")));

        var expected = !existingDatabase && string.Equals(
            environmentName,
            Environments.Production,
            StringComparison.Ordinal)
            ? new[] { "directory", "fresh-schema", "wal" }
            : new[] { "directory", "wal" };
        Assert.Equal(expected, observed);
    }

    private static SqliteBoardRepository CreateRepository(
        TestWorkspace workspace,
        string environmentName,
        string databasePath,
        DatabaseInitializationTestHooks? hooks = null)
    {
        var deploymentEnvironment = DeploymentEnvironmentPolicy.Resolve(environmentName);
        var environment = new TestWebHostEnvironment(workspace.ContentRoot, environmentName);
        var isolationLayout = workspace.DatabaseIsolationLayout(
            productionDatabasePath: deploymentEnvironment.IsProduction ? databasePath : null,
            trainingDatabasePath: deploymentEnvironment.IsTraining ? databasePath : null);
        return new SqliteBoardRepository(
            Microsoft.Extensions.Options.Options.Create(
                new BoardPersistenceOptions { DatabasePath = databasePath }),
            environment,
            deploymentEnvironment,
            new DatabaseIsolationPolicy(
                isolationLayout,
                new FileSystemReparsePointInspector()),
            new DatabaseDeploymentIdentityPolicy(),
            hooks ?? DatabaseInitializationTestHooks.None);
    }

    private static string ReadDatabaseSnapshot(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type || '|' || name || '|' || coalesce(sql, '')
            FROM sqlite_master
            WHERE name NOT LIKE 'sqlite_autoindex_%'
            ORDER BY type, name;

            SELECT 'identity|' || deployment_role || '|' || identity_schema_version || '|'
                || established_at_utc || '|' || established_via
            FROM chairside_deployment_identity;

            SELECT 'rooms|' || count(*) || '|' || coalesce(sum(room_id), 0)
            FROM active_rooms;

            PRAGMA journal_mode;
            """;
        var values = new List<string>();
        using var reader = command.ExecuteReader();
        do
        {
            while (reader.Read())
            {
                values.Add(reader.GetValue(0)?.ToString() ?? "<null>");
            }
        }
        while (reader.NextResult());

        return string.Join("\n", values);
    }

    private static void AssertCanonicalBackfill(
        SqliteConnection connection,
        string tableName,
        string predicate,
        string? expectedSedation,
        string? expectedAllocationState,
        long? expectedSuggested,
        long? expectedConfirmed)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT sedation_state, expected_allocation_state,
                expected_allocation_suggested_units, expected_allocation_confirmed_units
            FROM {tableName}
            WHERE {predicate};
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expectedSedation, ReadNullableString(reader, 0));
        Assert.Equal(expectedAllocationState, ReadNullableString(reader, 1));
        Assert.Equal(expectedSuggested, ReadNullableInt64(reader, 2));
        Assert.Equal(expectedConfirmed, ReadNullableInt64(reader, 3));
        Assert.False(reader.Read());
    }

    private static void CreateCompletedCycleTableWithRequiredDoctorArrived(
        SqliteConnection connection,
        bool omitCreatedAt)
    {
        SqliteBoardSchema.CreateCurrentSchema(connection);
        var createSql = Scalar<string>(
            connection,
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'completed_room_cycles';");
        createSql = createSql.Replace(
            "doctor_arrived_at TEXT NULL",
            "doctor_arrived_at TEXT NOT NULL",
            StringComparison.Ordinal);
        if (omitCreatedAt)
        {
            createSql = createSql.Replace(
                "created_at TEXT NOT NULL,\n",
                "",
                StringComparison.Ordinal);
        }

        Execute(connection, "DROP TABLE completed_room_cycles;");
        Execute(connection, createSql);
    }

    private static void InsertCompletedCycle(
        SqliteConnection connection,
        bool includeCreatedAt = true)
    {
        var createdColumn = includeCreatedAt ? ", created_at" : "";
        var createdValue = includeCreatedAt
            ? ", '2026-07-01T10:20:00.0000000+00:00'"
            : "";
        Execute(
            connection,
            $"""
            INSERT INTO completed_room_cycles (
                id, room_id, assigned_doctor_id, assigned_doctor_display_name,
                procedure_code, procedure_category, seated_at, doctor_arrived_at,
                seated_to_doctor_seconds, final_wait_state, episode_id{createdColumn}, updated_at)
            VALUES (
                1, 1, 'otte', 'Dr. Otte', 'CON', 'Consult',
                '2026-07-01T10:00:00.0000000+00:00',
                '2026-07-01T10:05:00.0000000+00:00',
                300, 'readyForDoctor', 'episode-nullable'{createdValue},
                '2026-07-01T10:20:00.0000000+00:00');
            """);
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        return connection;
    }

    private static SqliteConnection OpenInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
