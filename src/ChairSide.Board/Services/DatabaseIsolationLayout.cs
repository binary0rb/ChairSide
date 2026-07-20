namespace ChairSide.Board.Services;

public sealed record DatabaseIsolationLayout(
    string ProductionAppRoot,
    string ProductionDataRoot,
    string ProductionDatabasePath,
    string TrainingAppRoot,
    string TrainingDataRoot,
    string TrainingDatabasePath)
{
    public static DatabaseIsolationLayout Approved { get; } = new(
        ProductionAppRoot: @"C:\ChairSide\App",
        ProductionDataRoot: @"C:\ChairSide\Data",
        ProductionDatabasePath: @"C:\ChairSide\Data\chairside.db",
        TrainingAppRoot: @"C:\ChairSide\Training\App",
        TrainingDataRoot: @"C:\ChairSide\Training\Data",
        TrainingDatabasePath: @"C:\ChairSide\Training\Data\chairside-training.db");
}
