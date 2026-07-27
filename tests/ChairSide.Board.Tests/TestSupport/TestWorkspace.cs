using ChairSide.Board.Services;

namespace ChairSide.Board.Tests;

internal sealed class TestWorkspace : IDisposable
{
    private TestWorkspace(string root)
    {
        Root = root;
        ContentRoot = Path.Combine(root, "app");
        DataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(ContentRoot);
        Directory.CreateDirectory(DataRoot);
    }

    public string Root { get; }

    public string ContentRoot { get; }

    public string DataRoot { get; }

    public static TestWorkspace Create() =>
        new(Path.Combine(Path.GetTempPath(), "ChairSide.Board.Tests", Guid.NewGuid().ToString("N")));

    public string ProductionDatabasePath() =>
        Path.Combine(Root, "production", "data", "chairside-test.db");

    public string TrainingDatabasePath() =>
        Path.Combine(Root, "training", "data", "chairside-training-test.db");

    public DatabaseIsolationLayout DatabaseIsolationLayout(
        string? productionDatabasePath = null,
        string? trainingDatabasePath = null)
    {
        var resolvedProductionDatabasePath = productionDatabasePath ?? ProductionDatabasePath();
        var resolvedTrainingDatabasePath = trainingDatabasePath ?? TrainingDatabasePath();

        return new DatabaseIsolationLayout(
            ProductionAppRoot: Path.Combine(Root, "production", "app"),
            ProductionDataRoot: Path.GetDirectoryName(resolvedProductionDatabasePath)!,
            ProductionDatabasePath: resolvedProductionDatabasePath,
            TrainingAppRoot: Path.Combine(Root, "training", "app"),
            TrainingDataRoot: Path.GetDirectoryName(resolvedTrainingDatabasePath)!,
            TrainingDatabasePath: resolvedTrainingDatabasePath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for SQLite handles released just after test completion.
        }
    }
}
