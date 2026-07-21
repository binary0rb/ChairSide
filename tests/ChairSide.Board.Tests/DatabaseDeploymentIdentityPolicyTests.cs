using ChairSide.Board.Options;
using ChairSide.Board.Services;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Tests;

public sealed class DatabaseDeploymentIdentityPolicyTests
{
    [Theory]
    [InlineData(ChairSideEnvironmentNames.Production)]
    [InlineData(ChairSideEnvironmentNames.Training)]
    public void Fresh_deployed_database_receives_matching_fresh_identity(string environmentName)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName);

        var identity = ReadIdentity(context.DatabasePath);

        Assert.Equal(environmentName, identity.DeploymentRole);
        Assert.Equal(DatabaseDeploymentIdentityValues.SchemaVersion, identity.IdentitySchemaVersion);
        Assert.Equal(DatabaseDeploymentIdentityValues.FreshDatabase, identity.EstablishedVia);
        Assert.Equal(TimeSpan.Zero, identity.EstablishedAtUtc.Offset);
    }

    [Theory]
    [InlineData(ChairSideEnvironmentNames.Production)]
    [InlineData(ChairSideEnvironmentNames.Training)]
    public void Matching_deployed_restart_preserves_identity_exactly(string environmentName)
    {
        using var workspace = TestWorkspace.Create();
        var first = StoreContext.Create(workspace, environmentName);
        var before = ReadIdentity(first.DatabasePath);

        _ = StoreContext.Create(workspace, environmentName, first.DatabasePath);

        Assert.Equal(before, ReadIdentity(first.DatabasePath));
    }

    [Theory]
    [InlineData(ChairSideEnvironmentNames.Production, ChairSideEnvironmentNames.Training)]
    [InlineData(ChairSideEnvironmentNames.Training, ChairSideEnvironmentNames.Production)]
    public void Opposite_deployed_identity_is_refused(string databaseRole, string startupEnvironment)
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = Path.Combine(workspace.Root, "identity-crossing", "data", "chairside.db");
        CreateRawIdentityDatabase(databasePath, role: databaseRole);

        var exception = Assert.Throws<DatabaseIsolationException>(() =>
            CreateRepository(workspace, startupEnvironment, databasePath));

        Assert.Contains("identity mismatch", exception.Message, StringComparison.Ordinal);
        Assert.Equal(databaseRole, ReadIdentity(databasePath).DeploymentRole);
    }

    [Theory]
    [InlineData("production")]
    [InlineData("Unknown")]
    public void Unknown_and_case_variant_identity_roles_are_refused(string role)
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        CreateRawIdentityDatabase(databasePath, role: role, ignoreCheckConstraints: true);

        var exception = Assert.Throws<DatabaseIsolationException>(() =>
            CreateRepository(workspace, ChairSideEnvironmentNames.Production, databasePath));

        Assert.Contains("role", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wrong_identity_version_is_refused()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        CreateRawIdentityDatabase(databasePath, schemaVersion: 2, ignoreCheckConstraints: true);

        var exception = Assert.Throws<DatabaseIsolationException>(() =>
            CreateRepository(workspace, ChairSideEnvironmentNames.Production, databasePath));

        Assert.Contains("version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("timestamp")]
    [InlineData("provenance")]
    public void Malformed_timestamp_and_unknown_provenance_are_refused(string corruption)
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.TrainingDatabasePath();
        CreateRawIdentityDatabase(databasePath, role: ChairSideEnvironmentNames.Training);
        using (var connection = Open(databasePath))
        {
            if (corruption == "timestamp")
            {
                Execute(connection, "UPDATE chairside_deployment_identity SET established_at_utc = 'not-utc';");
            }
            else
            {
                Execute(connection, "PRAGMA ignore_check_constraints = ON;");
                Execute(connection, "UPDATE chairside_deployment_identity SET established_via = 'Unknown';");
            }
        }

        Assert.Throws<DatabaseIsolationException>(() =>
            CreateRepository(workspace, ChairSideEnvironmentNames.Training, databasePath));
    }

    [Fact]
    public void Zero_row_identity_table_is_refused()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.TrainingDatabasePath();
        CreateRawIdentityDatabase(databasePath, insertRow: false);

        var exception = Assert.Throws<DatabaseIsolationException>(() =>
            CreateRepository(workspace, ChairSideEnvironmentNames.Training, databasePath));

        Assert.Contains("zero rows", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TamperedIdentitySchemas))]
    public void Tampered_identity_schema_is_refused(string createSql)
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.TrainingDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = Open(databasePath))
        {
            Execute(connection, createSql);
        }

        var exception = Assert.Throws<DatabaseIsolationException>(() =>
            CreateRepository(workspace, ChairSideEnvironmentNames.Training, databasePath));

        Assert.Contains("schema is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Equivalent_identity_checks_allow_case_comments_and_redundant_outer_parentheses()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.TrainingDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = Open(databasePath))
        {
            Execute(connection, """
                create table chairside_deployment_identity (
                    singleton_id integer not null primary key CHECK (((singleton_id = 1))),
                    deployment_role text not null check
                        ((deployment_role IN ('Production', 'Training'))),
                    identity_schema_version integer not null /* harmless */ CHECK ((identity_schema_version = 1)),
                    established_at_utc text not null,
                    established_via text not null CHECK (((established_via = 'FreshDatabase'))))
                without rowid;
                INSERT INTO chairside_deployment_identity VALUES
                    (1, 'Training', 1, '2026-07-20T12:00:00.0000000+00:00', 'FreshDatabase');
                """);
        }

        _ = CreateRepository(workspace, ChairSideEnvironmentNames.Training, databasePath);

        Assert.Equal(ChairSideEnvironmentNames.Training, ReadIdentity(databasePath).DeploymentRole);
    }

    [Fact]
    public void Duplicate_capable_identity_schema_is_refused()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.TrainingDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = Open(databasePath))
        {
            Execute(connection, """
                CREATE TABLE chairside_deployment_identity (
                    singleton_id INTEGER NOT NULL,
                    deployment_role TEXT NOT NULL,
                    identity_schema_version INTEGER NOT NULL,
                    established_at_utc TEXT NOT NULL,
                    established_via TEXT NOT NULL);
                INSERT INTO chairside_deployment_identity VALUES
                    (1, 'Training', 1, '2026-07-20T12:00:00.0000000+00:00', 'FreshDatabase'),
                    (1, 'Production', 1, '2026-07-20T12:00:00.0000000+00:00', 'FreshDatabase');
                """);
        }

        var exception = Assert.Throws<DatabaseIsolationException>(() =>
            CreateRepository(workspace, ChairSideEnvironmentNames.Training, databasePath));

        Assert.Contains("schema is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_unmarked_ChairSide_database_is_refused_in_deployed_environment()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = Path.Combine(workspace.DataRoot, "legacy-unmarked.db");
        _ = StoreContext.Create(
            workspace,
            ChairSideEnvironmentNames.Development,
            databasePath: databasePath);

        var exception = Assert.Throws<DatabaseIsolationException>(() =>
            CreateRepository(workspace, ChairSideEnvironmentNames.Production, databasePath));

        Assert.Contains("cannot be reused", exception.Message, StringComparison.Ordinal);
        Assert.Contains("genuinely new canonical database", exception.Message, StringComparison.Ordinal);
        Assert.False(TableExists(databasePath, DatabaseDeploymentIdentityValues.TableName));
    }

    [Fact]
    public void Missing_file_is_new_but_zero_byte_and_valid_empty_Sqlite_are_not()
    {
        using var workspace = TestWorkspace.Create();
        var policy = new DatabaseDeploymentIdentityPolicy();
        var missing = Path.Combine(workspace.DataRoot, "missing.db");
        var zeroByte = Path.Combine(workspace.DataRoot, "zero.db");
        var validEmpty = Path.Combine(workspace.DataRoot, "valid-empty.db");

        Assert.Equal(DeployedDatabaseFileState.New, policy.ClassifyDeployedDatabase(missing));

        File.WriteAllBytes(zeroByte, []);
        Assert.Throws<DatabaseIsolationException>(() => policy.ClassifyDeployedDatabase(zeroByte));

        using (var connection = Open(validEmpty))
        {
            Execute(connection, "CREATE TABLE unrelated (id INTEGER PRIMARY KEY);");
        }

        Assert.Equal(DeployedDatabaseFileState.Existing, policy.ClassifyDeployedDatabase(validEmpty));
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public void Missing_main_with_sidecar_is_refused_without_creating_main(string suffix)
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.TrainingDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllText(databasePath + suffix, "orphan-sidecar");

        var exception = Assert.Throws<DatabaseIsolationException>(() =>
            CreateRepository(workspace, ChairSideEnvironmentNames.Training, databasePath));

        Assert.Contains("ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    public void Malformed_Sqlite_is_refused_controllably()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllText(databasePath, "not a sqlite database");

        var exception = Assert.Throws<DatabaseIsolationException>(() =>
            CreateRepository(workspace, ChairSideEnvironmentNames.Production, databasePath));

        Assert.Contains("identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<SqliteException>(exception.InnerException);
    }

    [Fact]
    public void Unreadable_locked_database_is_refused_controllably()
    {
        using var workspace = TestWorkspace.Create();
        var seeded = StoreContext.Create(workspace, ChairSideEnvironmentNames.Production);
        SqliteConnection.ClearAllPools();
        using var lockStream = new FileStream(
            seeded.DatabasePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var exception = Assert.Throws<DatabaseIsolationException>(() =>
            CreateRepository(workspace, ChairSideEnvironmentNames.Production, seeded.DatabasePath));

        Assert.Contains("identity inspection", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Marker_refusal_occurs_before_migration_or_room_creation()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.TrainingDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = Open(databasePath))
        {
            Execute(connection, """
                CREATE TABLE completed_room_cycles (id INTEGER PRIMARY KEY);
                CREATE TABLE chairside_deployment_identity (
                    singleton_id INTEGER,
                    deployment_role TEXT,
                    identity_schema_version INTEGER,
                    established_at_utc TEXT,
                    established_via TEXT);
                INSERT INTO chairside_deployment_identity VALUES
                    (1, 'Production', 1, '2026-07-20T12:00:00.0000000+00:00', 'FreshDatabase');
                """);
        }

        Assert.Throws<DatabaseIsolationException>(() =>
            CreateRepository(workspace, ChairSideEnvironmentNames.Training, databasePath));

        Assert.False(TableExists(databasePath, "active_rooms"));
        using var verify = Open(databasePath);
        Assert.Equal(["id"], ReadColumnNames(verify, "completed_room_cycles"));
    }

    [Theory]
    [InlineData("training")]
    [InlineData("empty")]
    [InlineData("large")]
    [InlineData("stress")]
    public void Every_current_reset_entry_point_preserves_identity_exactly(string resetKind)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, ChairSideEnvironmentNames.Training, roomCount: 12);
        var before = ReadIdentity(context.DatabasePath);

        switch (resetKind)
        {
            case "training":
                _ = context.Store.ResetAndSeedSyntheticTrainingData();
                break;
            case "empty":
                _ = context.Store.ResetAllDataForEmptyBeta();
                break;
            case "large":
                _ = context.Store.ResetAndSeedLargeSyntheticReportData(MaintenanceCommands.MinCompletedCycles);
                break;
            case "stress":
                _ = context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileLiveBoardStress, null);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(resetKind));
        }

        Assert.Equal(before, ReadIdentity(context.DatabasePath));
    }

    [Fact]
    public void Failed_repository_fresh_initialization_rolls_back_identity_and_schema_and_is_not_retried_as_new()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.TrainingDatabasePath();

        var exception = Assert.Throws<DatabaseIsolationException>(() => CreateRepository(
            workspace,
            ChairSideEnvironmentNames.Training,
            databasePath,
            new DatabaseInitializationTestHooks(
                AfterFreshSchemaCreatedBeforeCommit: () =>
                    throw new InvalidOperationException("injected fresh initialization failure"))));

        Assert.Contains("atomically initialize", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.True(File.Exists(databasePath));
        Assert.Equal(0, new FileInfo(databasePath).Length);
        var residualException = Assert.Throws<DatabaseIsolationException>(() =>
            new DatabaseDeploymentIdentityPolicy().ClassifyDeployedDatabase(databasePath));
        Assert.Contains("zero-byte", residualException.Message, StringComparison.Ordinal);

        using var verify = Open(databasePath, SqliteOpenMode.ReadOnly);
        Assert.False(TableExists(verify, DatabaseDeploymentIdentityValues.TableName));
        Assert.False(TableExists(verify, "active_rooms"));

        var restartException = Assert.Throws<DatabaseIsolationException>(() =>
            CreateRepository(workspace, ChairSideEnvironmentNames.Training, databasePath));
        Assert.Contains("zero-byte", restartException.Message, StringComparison.Ordinal);
        Assert.False(TableExists(databasePath, DatabaseDeploymentIdentityValues.TableName));
        Assert.False(TableExists(databasePath, "active_rooms"));
    }

    [Fact]
    public void Directory_preparation_access_failure_is_wrapped_as_database_isolation_refusal()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.TrainingDatabasePath();

        var exception = Assert.Throws<DatabaseIsolationException>(() => CreateRepository(
            workspace,
            ChairSideEnvironmentNames.Training,
            databasePath,
            new DatabaseInitializationTestHooks(
                BeforeDirectoryPreparation: () => throw new UnauthorizedAccessException("injected"))));

        Assert.Contains("prepare", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<UnauthorizedAccessException>(exception.InnerException);
        Assert.False(File.Exists(databasePath));
    }

    [Theory]
    [InlineData("development")]
    [InlineData("fresh-training")]
    [InlineData("existing-training")]
    public void Wal_configuration_failure_is_wrapped_as_database_isolation_refusal(string scenario)
    {
        using var workspace = TestWorkspace.Create();
        var environmentName = scenario == "development"
            ? ChairSideEnvironmentNames.Development
            : ChairSideEnvironmentNames.Training;
        var databasePath = environmentName == ChairSideEnvironmentNames.Training
            ? workspace.TrainingDatabasePath()
            : Path.Combine(workspace.ContentRoot, "data", "development.db");
        if (scenario == "existing-training")
        {
            _ = CreateRepository(workspace, environmentName, databasePath);
        }

        var exception = Assert.Throws<DatabaseIsolationException>(() => CreateRepository(
            workspace,
            environmentName,
            databasePath,
            new DatabaseInitializationTestHooks(
                BeforeEnableWal: () => throw new InvalidOperationException("injected"))));

        Assert.Contains("WAL mode", exception.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public void Development_unmarked_database_remains_compatible_and_deployed_marker_is_refused()
    {
        using var workspace = TestWorkspace.Create();
        var developmentPath = Path.Combine(workspace.DataRoot, "development.db");
        var development = StoreContext.Create(
            workspace,
            ChairSideEnvironmentNames.Development,
            developmentPath);
        Assert.False(TableExists(development.DatabasePath, DatabaseDeploymentIdentityValues.TableName));

        var deployedCopyPath = Path.Combine(workspace.DataRoot, "production-copy.db");
        CreateRawIdentityDatabase(deployedCopyPath);
        var exception = Assert.Throws<DatabaseIsolationException>(() =>
            CreateRepository(
                workspace,
                ChairSideEnvironmentNames.Development,
                deployedCopyPath));
        Assert.Contains("Development refuses", exception.Message, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> TamperedIdentitySchemas()
    {
        yield return
        [
            """
            CREATE TABLE chairside_deployment_identity (
                singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
                deployment_role TEXT NOT NULL CHECK (deployment_role IN ('Production', 'Training')),
                identity_schema_version INTEGER NOT NULL CHECK (identity_schema_version = 1),
                established_at_utc TEXT NOT NULL)
            WITHOUT ROWID;
            """
        ];
        yield return
        [
            DatabaseDeploymentIdentityPolicy.CreateTableSql.Replace(
                "established_via TEXT NOT NULL",
                "extra_column TEXT NULL, established_via TEXT NOT NULL",
                StringComparison.Ordinal)
        ];
        yield return
        [
            """
            CREATE TABLE chairside_deployment_identity (
                singleton_id INTEGER NOT NULL CHECK (singleton_id = 1),
                deployment_role TEXT NOT NULL PRIMARY KEY CHECK (deployment_role IN ('Production', 'Training')),
                identity_schema_version INTEGER NOT NULL CHECK (identity_schema_version = 1),
                established_at_utc TEXT NOT NULL,
                established_via TEXT NOT NULL CHECK (established_via = 'FreshDatabase'))
            WITHOUT ROWID;
            """
        ];
        yield return
        [
            DatabaseDeploymentIdentityPolicy.CreateTableSql.Replace(
                ") WITHOUT ROWID;",
                ");",
                StringComparison.Ordinal)
        ];
        yield return
        [
            DatabaseDeploymentIdentityPolicy.CreateTableSql.Replace(
                "CHECK (singleton_id = 1)",
                "CHECK (singleton_id > 0)",
                StringComparison.Ordinal)
        ];
        yield return
        [
            DatabaseDeploymentIdentityPolicy.CreateTableSql.Replace(
                "'Production', 'Training'",
                "'production', 'Training'",
                StringComparison.Ordinal)
        ];
        yield return
        [
            DatabaseDeploymentIdentityPolicy.CreateTableSql.Replace(
                "CHECK (identity_schema_version = 1)",
                "CHECK (identity_schema_version >= 1)",
                StringComparison.Ordinal)
        ];
        yield return
        [
            DatabaseDeploymentIdentityPolicy.CreateTableSql.Replace(
                "CHECK (established_via = 'FreshDatabase')",
                "CHECK (established_via IN ('FreshDatabase', 'ManualEdit'))",
                StringComparison.Ordinal)
        ];
        yield return
        [
            DatabaseDeploymentIdentityPolicy.CreateTableSql.Replace(
                "CHECK (singleton_id = 1)",
                "CHECK (singleton_id > 0) /* CHECK (singleton_id = 1) */",
                StringComparison.Ordinal)
        ];
        yield return
        [
            DatabaseDeploymentIdentityPolicy.CreateTableSql.Replace(
                "CHECK (identity_schema_version = 1)",
                "CHECK (identity_schema_version > 0) -- CHECK (identity_schema_version = 1)\n",
                StringComparison.Ordinal)
        ];
        yield return
        [
            DatabaseDeploymentIdentityPolicy.CreateTableSql.Replace(
                "CHECK (singleton_id = 1)",
                "CHECK ('singleton_id = 1' IS NOT NULL)",
                StringComparison.Ordinal)
        ];
        yield return
        [
            DatabaseDeploymentIdentityPolicy.CreateTableSql.Replace(
                "'Production', 'Training'",
                "'Production', 'Training', 'Unknown'",
                StringComparison.Ordinal)
        ];
    }

    private static SqliteBoardRepository CreateRepository(
        TestWorkspace workspace,
        string environmentName,
        string databasePath,
        DatabaseInitializationTestHooks? initializationHooks = null)
    {
        var deploymentEnvironment = DeploymentEnvironmentPolicy.Resolve(environmentName);
        var layout = workspace.DatabaseIsolationLayout(
            productionDatabasePath: deploymentEnvironment.IsProduction ? databasePath : null,
            trainingDatabasePath: deploymentEnvironment.IsTraining ? databasePath : null);
        return initializationHooks is null
            ? new SqliteBoardRepository(
                Microsoft.Extensions.Options.Options.Create(new BoardPersistenceOptions { DatabasePath = databasePath }),
                new TestWebHostEnvironment(workspace.ContentRoot, environmentName),
                deploymentEnvironment,
                new DatabaseIsolationPolicy(layout, new FileSystemReparsePointInspector()),
                new DatabaseDeploymentIdentityPolicy())
            : new SqliteBoardRepository(
            Microsoft.Extensions.Options.Options.Create(new BoardPersistenceOptions { DatabasePath = databasePath }),
            new TestWebHostEnvironment(workspace.ContentRoot, environmentName),
            deploymentEnvironment,
            new DatabaseIsolationPolicy(layout, new FileSystemReparsePointInspector()),
            new DatabaseDeploymentIdentityPolicy(),
            initializationHooks);
    }

    private static DatabaseDeploymentIdentity ReadIdentity(string databasePath)
    {
        using var connection = Open(databasePath, SqliteOpenMode.ReadOnly);
        return new DatabaseDeploymentIdentityPolicy().ValidateConnection(
                   connection,
                   DeploymentEnvironmentPolicy.Resolve(
                       ExecuteScalarString(connection, "SELECT deployment_role FROM chairside_deployment_identity;")))
               ?? throw new InvalidOperationException("Expected a deployment identity.");
    }

    private static void CreateRawIdentityDatabase(
        string databasePath,
        string role = ChairSideEnvironmentNames.Production,
        int schemaVersion = DatabaseDeploymentIdentityValues.SchemaVersion,
        bool insertRow = true,
        bool ignoreCheckConstraints = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using var connection = Open(databasePath);
        Execute(connection, DatabaseDeploymentIdentityPolicy.CreateTableSql);
        if (!insertRow)
        {
            return;
        }

        if (ignoreCheckConstraints)
        {
            Execute(connection, "PRAGMA ignore_check_constraints = ON;");
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO chairside_deployment_identity
                (singleton_id, deployment_role, identity_schema_version, established_at_utc, established_via)
            VALUES (1, $role, $version, '2026-07-20T12:00:00.0000000+00:00', 'FreshDatabase');
            """;
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$version", schemaVersion);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(
        string databasePath,
        SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            DefaultTimeout = 1
        }.ToString());
        connection.Open();
        return connection;
    }

    private static bool TableExists(string databasePath, string tableName)
    {
        using var connection = Open(databasePath, SqliteOpenMode.ReadOnly);
        return TableExists(connection, tableName);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static IReadOnlyList<string> ReadColumnNames(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_xinfo('{tableName}');";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static string ExecuteScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
