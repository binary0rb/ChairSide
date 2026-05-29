namespace ChairSide.Board.Options;

public sealed class BoardThresholdOptions
{
    public const string SectionName = "BoardThresholdOptions";

    public int AgingMinutes { get; set; } = 7;

    public int StaleMinutes { get; set; } = 12;

    public TimeSpan AgingThreshold => TimeSpan.FromMinutes(AgingMinutes);

    public TimeSpan StaleThreshold => TimeSpan.FromMinutes(StaleMinutes);
}
