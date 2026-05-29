namespace ChairSide.Board.Options;

public sealed class BoardPersistenceOptions
{
    public const string SectionName = "BoardPersistenceOptions";

    public string DatabasePath { get; set; } = "./data/chairside-dev.db";
}
