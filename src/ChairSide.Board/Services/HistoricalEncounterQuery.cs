namespace ChairSide.Board.Services;

public static class HistoricalEncounterSourceTypes
{
    public const string CompletedCycle = ExceptionReviewSources.CompletedCycle;
    public const string AbortedAssignment = ExceptionReviewSources.AbortedAssignment;
}

/// <summary>
/// Stable typed identity for one durable historical source row. Later anomaly work can attach
/// administrative state to this shape without introducing a synthetic historical encounter row.
/// </summary>
public readonly record struct HistoricalEncounterKey(string SourceType, long SourceRecordId)
{
    public bool IsValid =>
        SourceRecordId > 0
        && (SourceType == HistoricalEncounterSourceTypes.CompletedCycle
            || SourceType == HistoricalEncounterSourceTypes.AbortedAssignment);
}

public sealed record HistoricalEncounterRecord(
    HistoricalEncounterKey Key,
    CompletedRoomCycle? CompletedCycle,
    AbortedRoomAssignment? AbortedAssignment);

/// <summary>
/// One bounded repository page. TotalMatchingCount is evaluated by SQLite against the same
/// predicates as Rows, so callers never need to load the complete population merely to count it.
/// </summary>
public sealed record HistoricalQueryPage<T>(
    IReadOnlyList<T> Rows,
    int TotalMatchingCount,
    int Offset,
    int Limit)
{
    public bool HasMore => Offset + Rows.Count < TotalMatchingCount;
}
