using System.Text;

using ChairSide.Board.Options;
using ChairSide.Board.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace ChairSide.Board.Tests;

public sealed class EnvironmentMaintenancePreflightTests
{
    public static IEnumerable<object[]> ValidMaintenanceCommands()
    {
        yield return
        [
            MaintenanceCommands.TrainingSeedCommand,
            new[] { "--maintenance", MaintenanceCommands.TrainingSeedCommand, "--confirm", MaintenanceCommands.TrainingSeedToken }
        ];
        yield return
        [
            MaintenanceCommands.EmptyBetaCommand,
            new[] { "--maintenance", MaintenanceCommands.EmptyBetaCommand, "--confirm", MaintenanceCommands.EmptyBetaToken }
        ];
        yield return
        [
            MaintenanceCommands.LargeSyntheticSeedCommand,
            new[] { "--maintenance", MaintenanceCommands.LargeSyntheticSeedCommand, "--confirm", MaintenanceCommands.LargeSyntheticSeedToken }
        ];
        yield return
        [
            MaintenanceCommands.StressFixtureCommand,
            new[]
            {
                "--maintenance", MaintenanceCommands.StressFixtureCommand,
                "--confirm", MaintenanceCommands.StressFixtureToken,
                "--profile", MaintenanceCommands.ProfileLiveBoardStress
            }
        ];
    }

    [Theory]
    [InlineData(ChairSideEnvironmentNames.Development, DeploymentRole.Development)]
    [InlineData(ChairSideEnvironmentNames.Training, DeploymentRole.Training)]
    [InlineData(ChairSideEnvironmentNames.Production, DeploymentRole.Production)]
    [InlineData("development", DeploymentRole.Development)]
    [InlineData("TRAINING", DeploymentRole.Training)]
    [InlineData("production", DeploymentRole.Production)]
    public void Recognized_environment_names_resolve_to_canonical_roles(string name, DeploymentRole expectedRole)
    {
        var environment = DeploymentEnvironmentPolicy.Resolve(name);

        Assert.Equal(expectedRole, environment.Role);
        Assert.Equal(expectedRole.ToString(), environment.EnvironmentName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" Training")]
    [InlineData("Training ")]
    [InlineData("\tProduction")]
    public void Null_blank_and_whitespace_padded_environment_names_fail(string? name)
    {
        Assert.Throws<DeploymentEnvironmentException>(() => DeploymentEnvironmentPolicy.Resolve(name));
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Test")]
    [InlineData("Beta")]
    [InlineData("Local")]
    [InlineData("ChairSide")]
    public void Unknown_environment_names_fail_closed(string name)
    {
        Assert.Throws<DeploymentEnvironmentException>(() => DeploymentEnvironmentPolicy.Resolve(name));
    }

    [Fact]
    public void Unknown_environment_fails_before_service_callback()
    {
        var callbackInvoked = false;

        Assert.Throws<DeploymentEnvironmentException>(() =>
        {
            _ = DeploymentEnvironmentPolicy.Resolve("Staging");
            callbackInvoked = true;
        });

        Assert.False(callbackInvoked);
    }

    [Theory]
    [MemberData(nameof(ValidMaintenanceCommands))]
    public void Known_maintenance_commands_are_allowed_in_development_and_training(
        string command,
        string[] args)
    {
        var resolution = MaintenanceCommands.Resolve(args);
        Assert.Equal(command, resolution.Command);
        Assert.Equal(MaintenanceOutcome.Authorized, resolution.Outcome);

        foreach (var environmentName in new[]
                 {
                     ChairSideEnvironmentNames.Development,
                     ChairSideEnvironmentNames.Training
                 })
        {
            var callbackInvoked = false;
            var exitCode = MaintenanceExecutionPolicy.Execute(
                DeploymentEnvironmentPolicy.Resolve(environmentName),
                resolution,
                () =>
                {
                    callbackInvoked = true;
                    return 17;
                },
                TextWriter.Null);

            Assert.Equal(17, exitCode);
            Assert.True(callbackInvoked);
        }
    }

    [Theory]
    [MemberData(nameof(ValidMaintenanceCommands))]
    public void Every_destructive_maintenance_command_is_refused_in_production_before_callback(
        string command,
        string[] args)
    {
        var resolution = MaintenanceCommands.Resolve(args);
        var callbackInvoked = false;
        using var error = new StringWriter();

        var exitCode = MaintenanceExecutionPolicy.Execute(
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Production),
            resolution,
            () =>
            {
                callbackInvoked = true;
                return 0;
            },
            error);

        Assert.Equal(2, exitCode);
        Assert.False(callbackInvoked);
        Assert.Contains($"'{command}' cannot run in Production", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("No data was changed", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_and_wrong_confirmation_tokens_still_refuse_before_callback()
    {
        var environment = DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Development);
        var missing = MaintenanceCommands.Resolve(
            ["--maintenance", MaintenanceCommands.TrainingSeedCommand]);
        var wrong = MaintenanceCommands.Resolve(
            [
                "--maintenance", MaintenanceCommands.TrainingSeedCommand,
                "--confirm", MaintenanceCommands.EmptyBetaToken
            ]);
        var callbackInvoked = false;

        Assert.Equal(MaintenanceOutcome.Refused, missing.Outcome);
        Assert.Equal(MaintenanceOutcome.Refused, wrong.Outcome);
        Assert.Equal(2, MaintenanceExecutionPolicy.Execute(environment, missing, Callback, TextWriter.Null));
        Assert.Equal(2, MaintenanceExecutionPolicy.Execute(environment, wrong, Callback, TextWriter.Null));
        Assert.False(callbackInvoked);

        int Callback()
        {
            callbackInvoked = true;
            return 0;
        }
    }

    [Fact]
    public void Not_requested_maintenance_is_refused_before_callback()
    {
        var resolution = MaintenanceCommands.Resolve([]);
        var callbackInvoked = false;
        using var error = new StringWriter();

        var exitCode = MaintenanceExecutionPolicy.Execute(
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Development),
            resolution,
            () =>
            {
                callbackInvoked = true;
                return 0;
            },
            error);

        Assert.Equal(MaintenanceOutcome.NotRequested, resolution.Outcome);
        Assert.Equal(2, exitCode);
        Assert.False(callbackInvoked);
        Assert.Contains("No maintenance command was requested", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("No data was changed", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Authorized_Training_maintenance_validates_identity_before_mutation()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = Path.Combine(workspace.Root, "training-maintenance", "data", "chairside-training.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            _ = new DatabaseDeploymentIdentityPolicy().CreateIdentity(
                connection,
                transaction,
                DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Production));
            transaction.Commit();
        }

        var resolution = MaintenanceCommands.Resolve(
            [
                MaintenanceCommands.MaintenanceFlag,
                MaintenanceCommands.EmptyBetaCommand,
                MaintenanceCommands.ConfirmFlag,
                MaintenanceCommands.EmptyBetaToken
            ]);
        var mutationAttempted = false;
        var trainingEnvironment = DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Training);
        var layout = workspace.DatabaseIsolationLayout(trainingDatabasePath: databasePath);

        Assert.Throws<DatabaseIsolationException>(() =>
            MaintenanceExecutionPolicy.Execute(
                trainingEnvironment,
                resolution,
                () =>
                {
                    _ = new SqliteBoardRepository(
                        Microsoft.Extensions.Options.Options.Create(
                            new BoardPersistenceOptions { DatabasePath = databasePath }),
                        new TestWebHostEnvironment(workspace.ContentRoot, ChairSideEnvironmentNames.Training),
                        trainingEnvironment,
                        new DatabaseIsolationPolicy(layout, new FileSystemReparsePointInspector()),
                        new DatabaseDeploymentIdentityPolicy());
                    mutationAttempted = true;
                    return 0;
                },
                TextWriter.Null));

        Assert.False(mutationAttempted);
        Assert.Equal(ChairSideEnvironmentNames.Production, ReadIdentityRole(databasePath));
    }

    [Fact]
    public void Production_refusal_leaves_database_data_directory_and_diagnostic_log_absent()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChairSide.Board.Tests", Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(root, "data");
        var databasePath = Path.Combine(dataDirectory, "refused.db");
        var logDirectory = Path.Combine(root, "logs");
        var diagnosticLogPath = Path.Combine(logDirectory, "room-audit.log");
        var resolution = MaintenanceCommands.Resolve(
            [
                "--maintenance", MaintenanceCommands.TrainingSeedCommand,
                "--confirm", MaintenanceCommands.TrainingSeedToken
            ]);

        var exitCode = MaintenanceExecutionPolicy.Execute(
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Production),
            resolution,
            () =>
            {
                Directory.CreateDirectory(dataDirectory);
                File.WriteAllText(databasePath, "repository-created");
                Directory.CreateDirectory(logDirectory);
                File.WriteAllText(diagnosticLogPath, "logger-created");
                return 0;
            },
            TextWriter.Null);

        Assert.Equal(2, exitCode);
        Assert.False(Directory.Exists(root));
        Assert.False(Directory.Exists(dataDirectory));
        Assert.False(File.Exists(databasePath));
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(databasePath + "-shm"));
        Assert.False(File.Exists(diagnosticLogPath));
    }

    [Fact]
    public async Task Training_fresh_store_starts_available_and_rejects_demo_elapsed_minutes()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(
            workspace,
            environmentName: ChairSideEnvironmentNames.Training,
            roomCount: 12);

        var snapshot = context.Store.GetSnapshot();

        Assert.False(snapshot.DemoTimerEnabled);
        Assert.All(snapshot.Rooms, room =>
        {
            Assert.Equal(RoomStates.Available, room.State);
            Assert.Null(room.AssignedDoctor);
            Assert.Null(room.SeatedAt);
        });
        Assert.False(DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Training).IsDevelopment);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var requestContext = new DefaultHttpContext();
        var body = Encoding.UTF8.GetBytes(
            """
            {"doctorId":"otte","procedureCode":"CON","procedureId":"CON","sedation":false,"expectedAllocationUnits":1,"demoElapsedMinutes":1}
            """);
        requestContext.Request.Path = "/api/rooms/1/seat";
        requestContext.Request.Body = new MemoryStream(body);
        requestContext.Request.ContentLength = body.Length;
        requestContext.Request.ContentType = "application/json";
        var trainingEnvironment = new TestWebHostEnvironment(
            workspace.ContentRoot,
            ChairSideEnvironmentNames.Training);
        var diagnosticLogger = new DiagnosticLogger(
            Microsoft.Extensions.Options.Options.Create(new DiagnosticOptions
            {
                LogDirectory = Path.Combine(workspace.DataRoot, "logs")
            }),
            trainingEnvironment);
        var rejection = await global::RoomLifecycleEndpointHandler.SeatAsync(
            1,
            requestContext,
            new RoomDeviceTokenValidator(
                new TestOptionsMonitor<RoomDeviceBindingOptions>(
                    new RoomDeviceBindingOptions { Enabled = false })),
            context.Store,
            trainingEnvironment,
            diagnosticLogger,
            new NoopBoardHubContext());

        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(rejection).StatusCode);
        Assert.Equal(
            "demoElapsedMinutes is only available in Development.",
            Assert.IsType<string>(Assert.IsAssignableFrom<IValueHttpResult>(rejection).Value));
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(1)!.State);
    }

    [Fact]
    public void Training_environment_does_not_satisfy_the_development_role_check()
    {
        var training = DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Training);

        Assert.False(training.IsDevelopment);
    }

    [Fact]
    public void Existing_development_and_production_fresh_startup_behavior_remains_valid()
    {
        using var workspace = TestWorkspace.Create();
        var development = StoreContext.Create(
            workspace,
            environmentName: ChairSideEnvironmentNames.Development,
            databasePath: Path.Combine(workspace.DataRoot, "development-startup.db"));
        var production = StoreContext.Create(
            workspace,
            environmentName: ChairSideEnvironmentNames.Production,
            databasePath: Path.Combine(workspace.DataRoot, "production-startup.db"));

        Assert.Contains(development.Store.GetSnapshot().Rooms, room => room.State != RoomStates.Available);
        Assert.All(production.Store.GetSnapshot().Rooms, room => Assert.Equal(RoomStates.Available, room.State));
    }

    [Fact]
    public void Training_rejects_development_sample_admin_token_when_protection_is_enabled()
    {
        var validator = new AdminAccessOptionsValidator(
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Training));

        var result = validator.Validate(
            null,
            new AdminAccessOptions { Enabled = true, SharedToken = "dev-admin-token" });

        Assert.True(result.Failed);
        Assert.Contains("in Training", string.Join(" ", result.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void After_hours_defaults_remain_seven_pm_central()
    {
        var options = new RoomExpirationOptions();

        Assert.True(options.AfterHoursSweepEnabled);
        Assert.Equal("19:00", options.AfterHoursSweepTime);
        Assert.Equal("America/Chicago", options.TimeZone);
    }

    private static string ReadIdentityRole(string databasePath)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT deployment_role FROM chairside_deployment_identity;";
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }
}
