using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

internal sealed class StoreContext
{
    private StoreContext(
        DemoBoardStore store,
        SqliteBoardRepository repository,
        string databasePath)
    {
        Store = store;
        Repository = repository;
        DatabasePath = databasePath;
    }

    public DemoBoardStore Store { get; }

    public SqliteBoardRepository Repository { get; }

    public string DatabasePath { get; }

    public IReadOnlyList<Doctor> Doctors { get; } =
    [
        new("otte", "Dr. Otte", "Otte", "#2563eb"),
        new("pledger", "Dr. Pledger", "Pledger", "#16a34a"),
        new("gibson", "Dr. Gibson", "Gibson", "#f97316"),
        new("schroeder", "Dr. Schroeder", "Schroeder", "#7c3aed")
    ];

    public IReadOnlyList<ProcedureCategory> Procedures { get; } =
    [
        new("consult", "CON", "Consult", "speech"),
        new("extraction", "EXT", "Extraction", "forceps"),
        new("sedation", "SED", "Sedation", "moon"),
        new("post-op", "POST", "Post-op", "check"),
        new("implant", "IMP", "Implant", "bolt"),
        new("biopsy", "BX", "Biopsy", "vial")
    ];

    public static StoreContext Create(
        TestWorkspace workspace,
        string environmentName,
        string? databasePath = null,
        int agingMinutes = 7,
        int staleMinutes = 12,
        int roomCount = 3,
        DoctorRosterOptions? doctorRosterOptions = null,
        ProcedureRosterOptions? procedureRosterOptions = null,
        BoardUiOptions? boardUiOptions = null,
        TimeProvider? timeProvider = null,
        RoomExpirationOptions? expirationOptions = null)
    {
        var resolvedDatabasePath = databasePath
            ?? (string.Equals(environmentName, Environments.Production, StringComparison.Ordinal)
                ? workspace.ProductionDatabasePath()
                : string.Equals(environmentName, ChairSideEnvironmentNames.Training, StringComparison.Ordinal)
                    ? workspace.TrainingDatabasePath()
                    : Path.Combine(workspace.ContentRoot, "data", "chairside-test.db"));
        var environment = new TestWebHostEnvironment(workspace.ContentRoot, environmentName);
        var deploymentEnvironment = DeploymentEnvironmentPolicy.Resolve(environmentName);
        var isolationLayout = workspace.DatabaseIsolationLayout(
            productionDatabasePath: deploymentEnvironment.IsProduction ? resolvedDatabasePath : null,
            trainingDatabasePath: deploymentEnvironment.IsTraining ? resolvedDatabasePath : null);
        var repository = new SqliteBoardRepository(
            Microsoft.Extensions.Options.Options.Create(new BoardPersistenceOptions { DatabasePath = resolvedDatabasePath }),
            environment,
            deploymentEnvironment,
            new DatabaseIsolationPolicy(isolationLayout, new FileSystemReparsePointInspector()),
            new DatabaseDeploymentIdentityPolicy());
        var store = new DemoBoardStore(
            new TestOptionsMonitor<BoardThresholdOptions>(new BoardThresholdOptions
            {
                AgingMinutes = agingMinutes,
                StaleMinutes = staleMinutes
            }),
            new TestOptionsMonitor<RoomExpirationOptions>(expirationOptions ?? new RoomExpirationOptions { Enabled = false }),
            Microsoft.Extensions.Options.Options.Create(new BoardOptions { RoomCount = roomCount }),
            Microsoft.Extensions.Options.Options.Create(boardUiOptions ?? new BoardUiOptions()),
            Microsoft.Extensions.Options.Options.Create(doctorRosterOptions ?? new DoctorRosterOptions
            {
                Doctors = DoctorRosterOptions.DefaultDoctors()
            }),
            Microsoft.Extensions.Options.Options.Create(procedureRosterOptions ?? new ProcedureRosterOptions
            {
                Procedures = ProcedureRosterOptions.DefaultProcedures()
            }),
            repository,
            deploymentEnvironment,
            timeProvider);

        return new StoreContext(store, repository, resolvedDatabasePath);
    }
}
