using System.Diagnostics;
using System.Text.Json;

using ChairSide.Board.Options;
using ChairSide.Board.Services;

using Xunit.Abstractions;

namespace ChairSide.Board.Tests;

public sealed class DatabaseIsolationPolicyTests(ITestOutputHelper testOutput)
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Null_or_blank_configured_path_is_refused(string? configuredPath)
    {
        using var workspace = TestWorkspace.Create();

        var exception = Assert.Throws<DatabaseIsolationException>(() =>
            Policy(workspace.DatabaseIsolationLayout()).ResolveAndValidate(
                configuredPath,
                workspace.ContentRoot,
                DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Development)));

        Assert.Contains("database path is required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Null_or_blank_ContentRootPath_is_refused(string? contentRootPath)
    {
        using var workspace = TestWorkspace.Create();

        var exception = Assert.Throws<DatabaseIsolationException>(() =>
            Policy(workspace.DatabaseIsolationLayout()).ResolveAndValidate(
                @"relative\chairside-dev.db",
                contentRootPath,
                DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Development)));

        Assert.Contains("ContentRootPath is required", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ChairSideEnvironmentNames.Production)]
    [InlineData(ChairSideEnvironmentNames.Training)]
    public void Deployed_canonical_path_is_accepted(string environmentName)
    {
        using var workspace = TestWorkspace.Create();
        var layout = workspace.DatabaseIsolationLayout();
        var configuredPath = environmentName == ChairSideEnvironmentNames.Production
            ? layout.ProductionDatabasePath
            : layout.TrainingDatabasePath;

        var normalized = Policy(layout).ResolveAndValidate(
            configuredPath,
            workspace.ContentRoot,
            DeploymentEnvironmentPolicy.Resolve(environmentName));

        Assert.Equal(Path.GetFullPath(configuredPath), normalized, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Training_pointed_at_Production_path_is_refused()
    {
        using var workspace = TestWorkspace.Create();
        var layout = workspace.DatabaseIsolationLayout();

        var exception = Assert.Throws<DatabaseIsolationException>(() => Policy(layout).ResolveAndValidate(
            layout.ProductionDatabasePath,
            workspace.ContentRoot,
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Training)));

        Assert.Contains("Production deployment", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_pointed_at_Training_path_is_refused()
    {
        using var workspace = TestWorkspace.Create();
        var layout = workspace.DatabaseIsolationLayout();

        var exception = Assert.Throws<DatabaseIsolationException>(() => Policy(layout).ResolveAndValidate(
            layout.TrainingDatabasePath,
            workspace.ContentRoot,
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Production)));

        Assert.Contains("Training deployment", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ChairSideEnvironmentNames.Production, @"data\chairside.db")]
    [InlineData(ChairSideEnvironmentNames.Training, @".\data\chairside-training.db")]
    [InlineData(ChairSideEnvironmentNames.Production, @"C:chairside.db")]
    [InlineData(ChairSideEnvironmentNames.Training, @"C:data\chairside-training.db")]
    public void Deployed_relative_and_drive_relative_paths_are_refused(
        string environmentName,
        string configuredPath)
    {
        using var workspace = TestWorkspace.Create();

        var exception = Assert.Throws<DatabaseIsolationException>(() =>
            Policy(workspace.DatabaseIsolationLayout()).ResolveAndValidate(
                configuredPath,
                workspace.ContentRoot,
                DeploymentEnvironmentPolicy.Resolve(environmentName)));

        Assert.Contains("fully qualified", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ChairSideEnvironmentNames.Production)]
    [InlineData(ChairSideEnvironmentNames.Training)]
    public void Wrong_filename_under_correct_data_root_is_refused(string environmentName)
    {
        using var workspace = TestWorkspace.Create();
        var layout = workspace.DatabaseIsolationLayout();
        var dataRoot = environmentName == ChairSideEnvironmentNames.Production
            ? layout.ProductionDataRoot
            : layout.TrainingDataRoot;

        var exception = Assert.Throws<DatabaseIsolationException>(() => Policy(layout).ResolveAndValidate(
            Path.Combine(dataRoot, "wrong.db"),
            workspace.ContentRoot,
            DeploymentEnvironmentPolicy.Resolve(environmentName)));

        Assert.Contains("must be exactly", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deployed_path_inside_actual_content_root_is_refused()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = Path.Combine(workspace.ContentRoot, "data", "chairside.db");
        var layout = workspace.DatabaseIsolationLayout(productionDatabasePath: databasePath);

        var exception = Assert.Throws<DatabaseIsolationException>(() => Policy(layout).ResolveAndValidate(
            databasePath,
            workspace.ContentRoot,
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Production)));

        Assert.Contains("outside the deployed app content root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deployed_path_inside_Production_app_root_is_refused()
    {
        using var workspace = TestWorkspace.Create();
        var baseLayout = workspace.DatabaseIsolationLayout();
        var databasePath = Path.Combine(baseLayout.ProductionAppRoot, "data", "chairside.db");
        var layout = baseLayout with
        {
            ProductionDataRoot = Path.GetDirectoryName(databasePath)!,
            ProductionDatabasePath = databasePath
        };

        var exception = Assert.Throws<DatabaseIsolationException>(() => Policy(layout).ResolveAndValidate(
            databasePath,
            workspace.ContentRoot,
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Production)));

        Assert.Contains("Production application root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deployed_path_inside_Training_app_root_is_refused()
    {
        using var workspace = TestWorkspace.Create();
        var baseLayout = workspace.DatabaseIsolationLayout();
        var databasePath = Path.Combine(baseLayout.TrainingAppRoot, "data", "chairside-training.db");
        var layout = baseLayout with
        {
            TrainingDataRoot = Path.GetDirectoryName(databasePath)!,
            TrainingDatabasePath = databasePath
        };

        var exception = Assert.Throws<DatabaseIsolationException>(() => Policy(layout).ResolveAndValidate(
            databasePath,
            workspace.ContentRoot,
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Training)));

        Assert.Contains("Training application root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Development_relative_path_resolves_against_ContentRootPath()
    {
        using var workspace = TestWorkspace.Create();

        var normalized = Policy(workspace.DatabaseIsolationLayout()).ResolveAndValidate(
            @"relative\chairside-dev.db",
            workspace.ContentRoot,
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Development));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(workspace.ContentRoot, @"relative\chairside-dev.db")),
            normalized,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Development_temporary_absolute_path_is_accepted()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = Path.Combine(workspace.DataRoot, "temporary-dev.db");

        var normalized = Policy(workspace.DatabaseIsolationLayout()).ResolveAndValidate(
            databasePath,
            workspace.ContentRoot,
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Development));

        Assert.Equal(Path.GetFullPath(databasePath), normalized, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("production-app")]
    [InlineData("production-data")]
    [InlineData("training-app")]
    [InlineData("training-data")]
    public void Development_paths_under_deployed_protected_roots_are_refused(string rootName)
    {
        using var workspace = TestWorkspace.Create();
        var layout = workspace.DatabaseIsolationLayout();
        var protectedRoot = rootName switch
        {
            "production-app" => layout.ProductionAppRoot,
            "production-data" => layout.ProductionDataRoot,
            "training-app" => layout.TrainingAppRoot,
            _ => layout.TrainingDataRoot
        };

        var exception = Assert.Throws<DatabaseIsolationException>(() => Policy(layout).ResolveAndValidate(
            Path.Combine(protectedRoot, "development.db"),
            workspace.ContentRoot,
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Development)));

        Assert.Contains("protected", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_path_equality_is_case_insensitive()
    {
        using var workspace = TestWorkspace.Create();
        var layout = workspace.DatabaseIsolationLayout();

        var normalized = Policy(layout).ResolveAndValidate(
            layout.ProductionDatabasePath.ToUpperInvariant(),
            workspace.ContentRoot,
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Production));

        Assert.Equal(layout.ProductionDatabasePath, normalized, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Data2_does_not_match_protected_Data_root()
    {
        using var workspace = TestWorkspace.Create();
        var layout = workspace.DatabaseIsolationLayout();
        var adjacentPath = Path.Combine(layout.ProductionDataRoot + "2", "development.db");

        var normalized = Policy(layout).ResolveAndValidate(
            adjacentPath,
            workspace.ContentRoot,
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Development));

        Assert.Equal(Path.GetFullPath(adjacentPath), normalized, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Existing_directory_at_database_filename_is_refused()
    {
        using var workspace = TestWorkspace.Create();
        var layout = workspace.DatabaseIsolationLayout();
        Directory.CreateDirectory(layout.ProductionDatabasePath);

        var exception = Assert.Throws<DatabaseIsolationException>(() =>
            Policy(layout, new FileSystemReparsePointInspector()).ResolveAndValidate(
                layout.ProductionDatabasePath,
                workspace.ContentRoot,
                DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Production)));

        Assert.Contains("existing directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Simulated_reparse_point_component_is_refused()
    {
        using var workspace = TestWorkspace.Create();
        var layout = workspace.DatabaseIsolationLayout();
        var inspector = new DelegateReparsePointInspector(path =>
            string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(layout.ProductionDataRoot)),
                StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : null);

        var exception = Assert.Throws<DatabaseIsolationException>(() => Policy(layout, inspector).ResolveAndValidate(
            layout.ProductionDatabasePath,
            workspace.ContentRoot,
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Production)));

        Assert.Contains("reparse point", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unexpected_metadata_access_error_fails_closed()
    {
        using var workspace = TestWorkspace.Create();
        var layout = workspace.DatabaseIsolationLayout();
        var inspector = new DelegateReparsePointInspector(_ =>
            throw new UnauthorizedAccessException("simulated metadata refusal"));

        var exception = Assert.Throws<DatabaseIsolationException>(() => Policy(layout, inspector).ResolveAndValidate(
            layout.ProductionDatabasePath,
            workspace.ContentRoot,
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Production)));

        Assert.IsType<UnauthorizedAccessException>(exception.InnerException);
        Assert.Contains("Startup is refused", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_canonical_deployed_directory_is_created_after_validation_and_rescanned()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = Path.Combine(workspace.Root, "missing-production", "data", "chairside.db");
        var layout = workspace.DatabaseIsolationLayout(productionDatabasePath: databasePath) with
        {
            ProductionDataRoot = Path.GetDirectoryName(databasePath)!
        };
        var recordingInspector = new RecordingReparsePointInspector(new FileSystemReparsePointInspector());
        var deploymentEnvironment = DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Production);

        var repository = new SqliteBoardRepository(
            Microsoft.Extensions.Options.Options.Create(new BoardPersistenceOptions { DatabasePath = databasePath }),
            new TestWebHostEnvironment(workspace.ContentRoot, ChairSideEnvironmentNames.Production),
            deploymentEnvironment,
            new DatabaseIsolationPolicy(layout, recordingInspector));

        Assert.Equal(Path.GetFullPath(databasePath), repository.DatabasePath, StringComparer.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.GetDirectoryName(databasePath)));
        Assert.True(File.Exists(databasePath));
        Assert.True(recordingInspector.Count(Path.GetDirectoryName(databasePath)!) >= 2);
    }

    [Fact]
    public void Invalid_pure_policy_path_creates_no_database_artifacts()
    {
        using var workspace = TestWorkspace.Create();
        var dataRoot = Path.Combine(workspace.Root, "not-created", "production", "data");
        var canonicalPath = Path.Combine(dataRoot, "chairside.db");
        var invalidPath = Path.Combine(dataRoot, "wrong.db");
        var layout = workspace.DatabaseIsolationLayout(productionDatabasePath: canonicalPath) with
        {
            ProductionDataRoot = dataRoot
        };

        Assert.Throws<DatabaseIsolationException>(() => Policy(layout).ResolveAndValidate(
            invalidPath,
            workspace.ContentRoot,
            DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Production)));

        Assert.False(Directory.Exists(dataRoot));
        Assert.False(File.Exists(invalidPath));
        Assert.False(File.Exists(invalidPath + "-wal"));
        Assert.False(File.Exists(invalidPath + "-shm"));
    }

    [Fact]
    public void Production_store_startup_succeeds_with_injected_canonical_layout()
    {
        using var workspace = TestWorkspace.Create();

        var context = StoreContext.Create(
            workspace,
            environmentName: ChairSideEnvironmentNames.Production);

        Assert.True(File.Exists(context.Repository.DatabasePath));
        Assert.All(context.Store.GetSnapshot().Rooms, room => Assert.Equal(RoomStates.Available, room.State));
    }

    [Fact]
    public void Training_store_startup_succeeds_with_injected_canonical_layout_and_available_rooms()
    {
        using var workspace = TestWorkspace.Create();

        var context = StoreContext.Create(
            workspace,
            environmentName: ChairSideEnvironmentNames.Training,
            roomCount: 12);

        Assert.True(File.Exists(context.Repository.DatabasePath));
        Assert.All(context.Store.GetSnapshot().Rooms, room => Assert.Equal(RoomStates.Available, room.State));
    }

    [Fact]
    public void Development_relative_repository_startup_remains_compatible_and_seeds_demo_rooms()
    {
        using var workspace = TestWorkspace.Create();

        var context = StoreContext.Create(
            workspace,
            environmentName: ChairSideEnvironmentNames.Development,
            databasePath: @"relative\chairside-dev.db",
            roomCount: 12);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(workspace.ContentRoot, @"relative\chairside-dev.db")),
            context.Repository.DatabasePath,
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains(context.Store.GetSnapshot().Rooms, room => room.State != RoomStates.Available);
    }

    [Fact]
    public void Training_configuration_contains_only_the_approved_paths()
    {
        var repositoryRoot = FindRepositoryRoot();
        var configurationPath = Path.Combine(
            repositoryRoot,
            "src",
            "ChairSide.Board",
            "appsettings.Training.json");
        using var document = JsonDocument.Parse(File.ReadAllText(configurationPath));
        var root = document.RootElement;

        Assert.Equal(2, root.EnumerateObject().Count());
        Assert.Equal(
            @"C:\ChairSide\Training\Data\chairside-training.db",
            root.GetProperty("BoardPersistenceOptions").GetProperty("DatabasePath").GetString());
        Assert.Single(root.GetProperty("BoardPersistenceOptions").EnumerateObject());
        Assert.Equal(
            @"C:\ChairSide\Training\Logs",
            root.GetProperty("DiagnosticOptions").GetProperty("LogDirectory").GetString());
        Assert.Single(root.GetProperty("DiagnosticOptions").EnumerateObject());
        Assert.False(root.TryGetProperty("ConnectionStrings", out _));
        Assert.False(root.TryGetProperty("AdminAccessOptions", out _));
        Assert.False(root.TryGetProperty("RoomDeviceBindingOptions", out _));
        Assert.False(root.TryGetProperty("Secrets", out _));
    }

    [Theory]
    [InlineData(ChairSideEnvironmentNames.Production)]
    [InlineData(ChairSideEnvironmentNames.Training)]
    public async Task Process_level_path_refusal_creates_no_database_or_diagnostic_log_artifacts(
        string environmentName)
    {
        await AssertControlledProcessRefusalAsync(
            environmentName,
            $"{environmentName} normal startup");
    }

    [Fact]
    public async Task Authorized_Training_maintenance_path_refusal_is_controlled_and_creates_no_artifacts()
    {
        await AssertControlledProcessRefusalAsync(
            ChairSideEnvironmentNames.Training,
            "Training authorized maintenance",
            [
                MaintenanceCommands.MaintenanceFlag,
                MaintenanceCommands.EmptyBetaCommand,
                MaintenanceCommands.ConfirmFlag,
                MaintenanceCommands.EmptyBetaToken
            ]);
    }

    private async Task AssertControlledProcessRefusalAsync(
        string environmentName,
        string scenario,
        IReadOnlyList<string>? applicationArguments = null)
    {
        using var workspace = TestWorkspace.Create();
        var repositoryRoot = FindRepositoryRoot();
        var projectDirectory = Path.Combine(repositoryRoot, "src", "ChairSide.Board");
        var testOutputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var buildConfiguration = testOutputDirectory.Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the active test build configuration.");
        var applicationAssembly = Path.Combine(
            projectDirectory,
            "bin",
            buildConfiguration,
            "net8.0",
            "ChairSide.Board.dll");
        var dataRoot = Path.Combine(workspace.Root, "process-refusal", "data");
        var databasePath = Path.Combine(dataRoot, "wrong.db");
        var logRoot = Path.Combine(workspace.Root, "process-refusal", "logs");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = projectDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(applicationAssembly);
        foreach (var argument in applicationArguments ?? [])
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = environmentName;
        process.StartInfo.Environment["BoardPersistenceOptions__DatabasePath"] = databasePath;
        process.StartInfo.Environment["DiagnosticOptions__LogDirectory"] = logRoot;

        var stopwatch = Stopwatch.StartNew();
        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var standardOutputText = await standardOutput;
        var standardErrorText = await standardError;
        stopwatch.Stop();
        var combinedOutput = standardOutputText + standardErrorText;

        testOutput.WriteLine(
            $"{scenario}: ExitCode={process.ExitCode}; ElapsedMilliseconds={stopwatch.ElapsedMilliseconds}; ForcedKill=False");
        Assert.Equal(2, process.ExitCode);
        Assert.Contains("[ChairSide Startup] Refused:", combinedOutput, StringComparison.Ordinal);
        Assert.Contains(
            $"{environmentName} SQLite database path must be exactly",
            combinedOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", combinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0xe0434352", combinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(dataRoot));
        Assert.False(File.Exists(databasePath));
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(databasePath + "-shm"));
        Assert.False(Directory.Exists(logRoot));
    }

    private static DatabaseIsolationPolicy Policy(
        DatabaseIsolationLayout layout,
        IReparsePointInspector? inspector = null) =>
        new(layout, inspector ?? new DelegateReparsePointInspector(_ => null));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChairSide.Board.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new InvalidOperationException("Could not locate ChairSide.Board.sln from the test output directory.");
    }

    private sealed class DelegateReparsePointInspector(Func<string, FileAttributes?> inspect)
        : IReparsePointInspector
    {
        public FileAttributes? GetAttributesIfExists(string path) => inspect(path);
    }

    private sealed class RecordingReparsePointInspector(IReparsePointInspector inner)
        : IReparsePointInspector
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

        public FileAttributes? GetAttributesIfExists(string path)
        {
            var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            _counts[normalizedPath] = _counts.GetValueOrDefault(normalizedPath) + 1;
            return inner.GetAttributesIfExists(path);
        }

        public int Count(string path) =>
            _counts.GetValueOrDefault(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
    }
}
