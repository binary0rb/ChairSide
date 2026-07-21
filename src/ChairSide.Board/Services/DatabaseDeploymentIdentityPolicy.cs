using System.Globalization;
using System.Security;

using Microsoft.Data.Sqlite;

namespace ChairSide.Board.Services;

public enum DeployedDatabaseFileState
{
    New,
    Existing
}

public static class DatabaseDeploymentIdentityValues
{
    public const string TableName = "chairside_deployment_identity";
    public const int SchemaVersion = 1;
    public const string FreshDatabase = "FreshDatabase";
}

public sealed record DatabaseDeploymentIdentity(
    string DeploymentRole,
    int IdentitySchemaVersion,
    DateTimeOffset EstablishedAtUtc,
    string EstablishedVia);

public sealed class DatabaseDeploymentIdentityPolicy
{
    public const string CreateTableSql = """
        CREATE TABLE chairside_deployment_identity (
            singleton_id INTEGER NOT NULL
                PRIMARY KEY
                CHECK (singleton_id = 1),
            deployment_role TEXT NOT NULL
                CHECK (deployment_role IN ('Production', 'Training')),
            identity_schema_version INTEGER NOT NULL
                CHECK (identity_schema_version = 1),
            established_at_utc TEXT NOT NULL,
            established_via TEXT NOT NULL
                CHECK (established_via = 'FreshDatabase')
        ) WITHOUT ROWID;
        """;

    private static readonly IdentityColumn[] ExpectedColumns =
    [
        new("singleton_id", "INTEGER", NotNull: true, PrimaryKeyOrdinal: 1),
        new("deployment_role", "TEXT", NotNull: true, PrimaryKeyOrdinal: 0),
        new("identity_schema_version", "INTEGER", NotNull: true, PrimaryKeyOrdinal: 0),
        new("established_at_utc", "TEXT", NotNull: true, PrimaryKeyOrdinal: 0),
        new("established_via", "TEXT", NotNull: true, PrimaryKeyOrdinal: 0)
    ];

    private static readonly string[] ExpectedCheckConstraints =
    [
        ConstrainedSqlExpressionNormalizer.NormalizeCheckExpression("singleton_id = 1"),
        ConstrainedSqlExpressionNormalizer.NormalizeCheckExpression(
            "deployment_role IN ('Production', 'Training')"),
        ConstrainedSqlExpressionNormalizer.NormalizeCheckExpression("identity_schema_version = 1"),
        ConstrainedSqlExpressionNormalizer.NormalizeCheckExpression(
            "established_via = 'FreshDatabase'")
    ];

    public DeployedDatabaseFileState ClassifyDeployedDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        try
        {
            if (!File.Exists(databasePath))
            {
                if (File.Exists(databasePath + "-wal") || File.Exists(databasePath + "-shm"))
                {
                    throw new DatabaseIsolationException(
                        $"SQLite database '{databasePath}' is absent while a WAL or SHM companion exists. The deployed database state is ambiguous and startup is refused.");
                }

                return DeployedDatabaseFileState.New;
            }

            if (new FileInfo(databasePath).Length == 0)
            {
                throw new DatabaseIsolationException(
                    $"SQLite database '{databasePath}' is an existing zero-byte file. It is not a new database and startup is refused.");
            }

            return DeployedDatabaseFileState.Existing;
        }
        catch (DatabaseIsolationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException
                                          or SecurityException)
        {
            throw new DatabaseIsolationException(
                $"Unable to classify deployed SQLite database '{databasePath}'. Startup is refused.",
                exception);
        }
    }

    public SqliteConnection OpenExistingReadOnly(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 5
        }.ToString());

        try
        {
            connection.Open();
            SetBusyTimeout(connection);
            return connection;
        }
        catch (Exception exception) when (exception is SqliteException
                                          or InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            connection.Dispose();
            throw new DatabaseIsolationException(
                $"Unable to open SQLite database '{databasePath}' for deployment identity inspection. Startup is refused.",
                exception);
        }
    }

    public SqliteConnection OpenReadWrite(string databasePath)
    {
        return OpenReadWrite(databasePath, SqliteOpenMode.ReadWriteCreate);
    }

    public SqliteConnection OpenExistingReadWrite(string databasePath)
    {
        return OpenReadWrite(databasePath, SqliteOpenMode.ReadWrite);
    }

    private static SqliteConnection OpenReadWrite(string databasePath, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            DefaultTimeout = 5
        }.ToString());

        try
        {
            connection.Open();
            SetBusyTimeout(connection);
            return connection;
        }
        catch (Exception exception) when (exception is SqliteException
                                          or InvalidOperationException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            connection.Dispose();
            throw new DatabaseIsolationException(
                $"Unable to open SQLite database '{databasePath}' for deployment identity validation. Startup is refused.",
                exception);
        }
    }

    public DatabaseDeploymentIdentity? ValidateConnection(
        SqliteConnection connection,
        DeploymentEnvironment deploymentEnvironment,
        SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(deploymentEnvironment);

        try
        {
            if (!IdentityTableExists(connection, transaction))
            {
                if (deploymentEnvironment.IsDevelopment)
                {
                    return null;
                }

                var operationalTableCount = CountOperationalTables(connection, transaction);
                if (operationalTableCount == 0)
                {
                    throw new DatabaseIsolationException(
                        $"Existing {deploymentEnvironment.EnvironmentName} SQLite database has no ChairSide deployment identity or application schema. Startup is refused.");
                }

                throw new DatabaseIsolationException(
                    $"Existing {deploymentEnvironment.EnvironmentName} ChairSide database has no deployment identity. Existing unmarked deployed databases cannot be reused; formal Production must begin with a genuinely new canonical database. Startup is refused.");
            }

            ValidateIdentityTableSchema(connection, transaction);
            var identity = ReadSingleIdentity(connection, transaction);

            if (deploymentEnvironment.IsDevelopment)
            {
                throw new DatabaseIsolationException(
                    $"Development refuses a database carrying the {identity.DeploymentRole} deployment identity.");
            }

            if (!string.Equals(
                    identity.DeploymentRole,
                    deploymentEnvironment.EnvironmentName,
                    StringComparison.Ordinal))
            {
                throw new DatabaseIsolationException(
                    $"{deploymentEnvironment.EnvironmentName} database identity mismatch: the database is marked {identity.DeploymentRole}.");
            }

            return identity;
        }
        catch (DatabaseIsolationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException
                                          or InvalidOperationException
                                          or FormatException
                                          or OverflowException)
        {
            throw new DatabaseIsolationException(
                $"Unable to validate the {deploymentEnvironment.EnvironmentName} SQLite deployment identity. Startup is refused.",
                exception);
        }
    }

    public DatabaseDeploymentIdentity CreateIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DeploymentEnvironment deploymentEnvironment)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(deploymentEnvironment);

        if (!deploymentEnvironment.IsDeployed)
        {
            throw new DatabaseIsolationException("Development databases do not receive a deployment identity marker.");
        }

        var establishedAt = DateTimeOffset.UtcNow;
        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = CreateTableSql;
            create.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO chairside_deployment_identity (
                    singleton_id,
                    deployment_role,
                    identity_schema_version,
                    established_at_utc,
                    established_via)
                VALUES (1, $deploymentRole, $schemaVersion, $establishedAtUtc, $establishedVia);
                """;
            insert.Parameters.AddWithValue("$deploymentRole", deploymentEnvironment.EnvironmentName);
            insert.Parameters.AddWithValue("$schemaVersion", DatabaseDeploymentIdentityValues.SchemaVersion);
            insert.Parameters.AddWithValue(
                "$establishedAtUtc",
                establishedAt.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$establishedVia", DatabaseDeploymentIdentityValues.FreshDatabase);
            insert.ExecuteNonQuery();
        }

        return new DatabaseDeploymentIdentity(
            deploymentEnvironment.EnvironmentName,
            DatabaseDeploymentIdentityValues.SchemaVersion,
            establishedAt,
            DatabaseDeploymentIdentityValues.FreshDatabase);
    }

    public bool IdentityTableExists(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_schema
            WHERE type = 'table' AND name = $tableName;
            """;
        command.Parameters.AddWithValue("$tableName", DatabaseDeploymentIdentityValues.TableName);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static void ValidateIdentityTableSchema(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var columns = new List<IdentityColumn>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_xinfo('{DatabaseDeploymentIdentityValues.TableName}');";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetInt32(6) != 0)
                {
                    throw InvalidSchema("hidden or generated identity columns are not allowed");
                }

                columns.Add(new IdentityColumn(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3) == 1,
                    reader.GetInt32(5)));
            }
        }

        if (columns.Count != ExpectedColumns.Length
            || columns.Where((column, index) =>
                    !string.Equals(column.Name, ExpectedColumns[index].Name, StringComparison.Ordinal)
                    || !string.Equals(column.Type, ExpectedColumns[index].Type, StringComparison.OrdinalIgnoreCase)
                    || column.NotNull != ExpectedColumns[index].NotNull
                    || column.PrimaryKeyOrdinal != ExpectedColumns[index].PrimaryKeyOrdinal)
                .Any())
        {
            throw InvalidSchema("columns, types, nullability, or primary-key shape do not match");
        }

        using (var tableList = connection.CreateCommand())
        {
            tableList.Transaction = transaction;
            tableList.CommandText = $"PRAGMA table_list('{DatabaseDeploymentIdentityValues.TableName}');";
            using var reader = tableList.ExecuteReader();
            if (!reader.Read() || reader.GetInt32(3) != ExpectedColumns.Length || reader.GetInt32(4) != 1 || reader.Read())
            {
                throw InvalidSchema("the table must contain exactly five columns and use WITHOUT ROWID");
            }
        }

        string? primaryKeyIndex = null;
        using (var indexList = connection.CreateCommand())
        {
            indexList.Transaction = transaction;
            indexList.CommandText = $"PRAGMA index_list('{DatabaseDeploymentIdentityValues.TableName}');";
            using var reader = indexList.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetInt32(2) == 1 && string.Equals(reader.GetString(3), "pk", StringComparison.Ordinal))
                {
                    if (primaryKeyIndex is not null)
                    {
                        throw InvalidSchema("multiple primary-key indexes were found");
                    }

                    primaryKeyIndex = reader.GetString(1);
                }
            }
        }

        if (primaryKeyIndex is null)
        {
            throw InvalidSchema("the required singleton primary-key index is missing");
        }

        using (var indexInfo = connection.CreateCommand())
        {
            indexInfo.Transaction = transaction;
            indexInfo.CommandText = $"PRAGMA index_xinfo('{primaryKeyIndex.Replace("'", "''", StringComparison.Ordinal)}');";
            using var reader = indexInfo.ExecuteReader();
            var keyColumns = new List<string>();
            while (reader.Read())
            {
                if (reader.GetInt32(5) == 1)
                {
                    keyColumns.Add(reader.GetString(2));
                }
            }

            if (keyColumns.Count != 1 || !string.Equals(keyColumns[0], "singleton_id", StringComparison.Ordinal))
            {
                throw InvalidSchema("the primary key must contain only singleton_id");
            }
        }

        string createSql;
        using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = """
                SELECT sql
                FROM sqlite_schema
                WHERE type = 'table' AND name = $tableName;
                """;
            schema.Parameters.AddWithValue("$tableName", DatabaseDeploymentIdentityValues.TableName);
            createSql = schema.ExecuteScalar() as string
                ?? throw InvalidSchema("the CREATE TABLE definition is missing");
        }

        var actualConstraints = ConstrainedSqlExpressionNormalizer
            .ExtractCheckExpressions(createSql)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedConstraints = ExpectedCheckConstraints
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actualConstraints.SequenceEqual(expectedConstraints, StringComparer.Ordinal))
        {
            throw InvalidSchema("required singleton, role, version, or provenance constraints are missing or altered");
        }
    }

    private static DatabaseDeploymentIdentity ReadSingleIdentity(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                singleton_id,
                deployment_role,
                identity_schema_version,
                established_at_utc,
                established_via
            FROM chairside_deployment_identity;
            """;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new DatabaseIsolationException("The deployment identity table contains zero rows. Startup is refused.");
        }

        var singletonId = reader.GetInt64(0);
        var deploymentRole = reader.GetString(1);
        var schemaVersion = reader.GetInt32(2);
        var establishedAtText = reader.GetString(3);
        var establishedVia = reader.GetString(4);
        if (reader.Read())
        {
            throw new DatabaseIsolationException("The deployment identity table contains more than one row. Startup is refused.");
        }

        if (singletonId != 1)
        {
            throw new DatabaseIsolationException("The deployment identity singleton_id must equal 1. Startup is refused.");
        }

        if (deploymentRole is not (ChairSideEnvironmentNames.Production or ChairSideEnvironmentNames.Training))
        {
            throw new DatabaseIsolationException(
                $"The deployment identity role '{deploymentRole}' is not recognized. Startup is refused.");
        }

        if (schemaVersion != DatabaseDeploymentIdentityValues.SchemaVersion)
        {
            throw new DatabaseIsolationException(
                $"The deployment identity schema version '{schemaVersion}' is not supported. Startup is refused.");
        }

        if (establishedVia != DatabaseDeploymentIdentityValues.FreshDatabase)
        {
            throw new DatabaseIsolationException(
                $"The deployment identity provenance '{establishedVia}' is not recognized. Startup is refused.");
        }

        if (!DateTimeOffset.TryParseExact(
                establishedAtText,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var establishedAt)
            || establishedAt.Offset != TimeSpan.Zero)
        {
            throw new DatabaseIsolationException(
                "The deployment identity established_at_utc is not a valid UTC round-trip timestamp. Startup is refused.");
        }

        return new DatabaseDeploymentIdentity(
            deploymentRole,
            schemaVersion,
            establishedAt,
            establishedVia);
    }

    private static int CountOperationalTables(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_schema
            WHERE type = 'table'
              AND name IN ('active_rooms', 'completed_room_cycles', 'aborted_room_assignments', 'ready_handoffs');
            """;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void SetBusyTimeout(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
    }

    private static DatabaseIsolationException InvalidSchema(string reason) =>
        new($"The deployment identity table schema is invalid: {reason}. Startup is refused.");

    private sealed record IdentityColumn(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrdinal);
}

internal static class ConstrainedSqlExpressionNormalizer
{
    public static IReadOnlyList<string> ExtractCheckExpressions(string sql)
    {
        var tokens = Tokenize(sql);
        var expressions = new List<string>();
        for (var index = 0; index < tokens.Count; index++)
        {
            if (!tokens[index].IsIdentifier("CHECK"))
            {
                continue;
            }

            if (index + 1 >= tokens.Count || !tokens[index + 1].IsSymbol("("))
            {
                throw new FormatException("CHECK must be followed by a parenthesized expression.");
            }

            var close = FindMatchingClose(tokens, index + 1);
            expressions.Add(NormalizeTokens(tokens.GetRange(index + 2, close - index - 2)));
            index = close;
        }

        return expressions;
    }

    public static string NormalizeCheckExpression(string expression) =>
        NormalizeTokens(Tokenize(expression));

    private static string NormalizeTokens(List<SqlToken> tokens)
    {
        tokens = StripRedundantOuterParentheses(tokens);
        return string.Join(" ", tokens.Select(token => token.Render()));
    }

    private static List<SqlToken> StripRedundantOuterParentheses(List<SqlToken> tokens)
    {
        while (tokens.Count >= 2
               && tokens[0].IsSymbol("(")
               && FindMatchingClose(tokens, 0) == tokens.Count - 1)
        {
            tokens = tokens.GetRange(1, tokens.Count - 2);
        }

        return tokens;
    }

    private static int FindMatchingClose(IReadOnlyList<SqlToken> tokens, int openIndex)
    {
        var depth = 0;
        for (var index = openIndex; index < tokens.Count; index++)
        {
            if (tokens[index].IsSymbol("("))
            {
                depth++;
            }
            else if (tokens[index].IsSymbol(")"))
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        throw new FormatException("SQL parentheses are unbalanced.");
    }

    private static List<SqlToken> Tokenize(string sql)
    {
        var tokens = new List<SqlToken>();
        for (var index = 0; index < sql.Length;)
        {
            var character = sql[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            if (character == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not ('\r' or '\n'))
                {
                    index++;
                }
                continue;
            }

            if (character == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                var close = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    throw new FormatException("SQL block comment is unterminated.");
                }
                index = close + 2;
                continue;
            }

            if (character == '\'')
            {
                var value = ReadQuoted(sql, ref index, '\'', doubledEscape: true);
                tokens.Add(new SqlToken(SqlTokenKind.StringLiteral, value));
                continue;
            }

            if (character is '"' or '`')
            {
                var value = ReadQuoted(sql, ref index, character, doubledEscape: true);
                tokens.Add(new SqlToken(SqlTokenKind.Identifier, value.ToUpperInvariant()));
                continue;
            }

            if (character == '[')
            {
                var close = sql.IndexOf(']', index + 1);
                if (close < 0)
                {
                    throw new FormatException("SQL bracketed identifier is unterminated.");
                }
                tokens.Add(new SqlToken(
                    SqlTokenKind.Identifier,
                    sql[(index + 1)..close].ToUpperInvariant()));
                index = close + 1;
                continue;
            }

            if (char.IsLetter(character) || character == '_')
            {
                var start = index++;
                while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_'))
                {
                    index++;
                }
                tokens.Add(new SqlToken(
                    SqlTokenKind.Identifier,
                    sql[start..index].ToUpperInvariant()));
                continue;
            }

            if (char.IsDigit(character))
            {
                var start = index++;
                while (index < sql.Length && char.IsDigit(sql[index]))
                {
                    index++;
                }
                tokens.Add(new SqlToken(SqlTokenKind.Number, sql[start..index]));
                continue;
            }

            if (character is '(' or ')' or ',' or '=' or ';')
            {
                tokens.Add(new SqlToken(SqlTokenKind.Symbol, character.ToString()));
                index++;
                continue;
            }

            tokens.Add(new SqlToken(SqlTokenKind.Symbol, character.ToString()));
            index++;
        }

        return tokens;
    }

    private static string ReadQuoted(string sql, ref int index, char quote, bool doubledEscape)
    {
        index++;
        var value = new System.Text.StringBuilder();
        while (index < sql.Length)
        {
            if (sql[index] == quote)
            {
                if (doubledEscape && index + 1 < sql.Length && sql[index + 1] == quote)
                {
                    value.Append(quote);
                    index += 2;
                    continue;
                }

                index++;
                return value.ToString();
            }

            value.Append(sql[index++]);
        }

        throw new FormatException("SQL quoted token is unterminated.");
    }

    private enum SqlTokenKind
    {
        Identifier,
        StringLiteral,
        Number,
        Symbol
    }

    private sealed record SqlToken(SqlTokenKind Kind, string Value)
    {
        public bool IsIdentifier(string value) =>
            Kind == SqlTokenKind.Identifier
            && string.Equals(Value, value, StringComparison.OrdinalIgnoreCase);

        public bool IsSymbol(string value) =>
            Kind == SqlTokenKind.Symbol
            && string.Equals(Value, value, StringComparison.Ordinal);

        public string Render() => Kind == SqlTokenKind.StringLiteral
            ? $"'{Value.Replace("'", "''", StringComparison.Ordinal)}'"
            : Value;
    }
}
