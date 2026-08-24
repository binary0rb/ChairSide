using System.Globalization;

namespace ChairSide.Board.Services;

/// <summary>
/// Additive report trend read model for historical operational timing. This first slice is
/// summary-only: weekly timing buckets, no charts, no scoring, no projections.
/// </summary>
public sealed record ReportTrendSnapshot(
    string BucketSize,
    IReadOnlyList<ReportTrendBucket> Buckets);

/// <summary>
/// One Monday-start UTC week of completed-cycle timing. <see cref="EndDate"/> is exclusive.
/// </summary>
public sealed record ReportTrendBucket(
    string StartDate,
    string EndDate,
    int CompletedCycleCount,
    double MedianSeatedToDoctorSeconds,
    double AverageSeatedToDoctorSeconds,
    int TurnoverCycleCount,
    double MedianTurnoverSeconds,
    double AverageTurnoverSeconds,
    ReportSampleContext? CompletedSample = null,
    ReportSampleContext? TurnoverSample = null,
    int ReadyWaitCycleCount = 0,
    double? MedianReadyWaitSeconds = null,
    ReportSampleContext? ReadyWaitSample = null,
    ReportSampleContext? SeatedToDoctorSample = null);

/// <summary>
/// Builds report trend snapshots over a caller-supplied standard/included completed-cycle population.
/// The builder groups only by observed completion dates and wait durations; it does not infer
/// capacity, rankings, forecasts, or projection output.
/// </summary>
public static class ReportTrendSnapshotBuilder
{
    public const string WeeklyBucketSize = "Week";

    // Weekly trend buckets are anchored by DoctorCompleteAt for consistency with the report date
    // filter. Turnover values summarize eligible cycles inside that same report bucket. A future
    // "room became available this week" report would need a separately named model if grouped by
    // RoomAvailableAt.
    public static ReportTrendSnapshot BuildWeekly(IEnumerable<CompletedRoomCycle> cycles)
    {
        ArgumentNullException.ThrowIfNull(cycles);

        var eligible = BoundedReportCollections.Materialize(
            cycles.Where(cycle => cycle is not null && cycle.DoctorCompleteAt is not null));
        using var grouping = BoundedGroupingSet<CompletedRoomCycle, DateOnly>.Create(
            eligible,
            cycle => WeekStart(DateOnly.FromDateTime(cycle.DoctorCompleteAt!.Value.UtcDateTime)));

        var buckets = grouping.Groups.OrderBy(group => group.Key)
            .Select(group =>
            {
                var population = BoundedReportCollections.Materialize(group);
                var seatedToDoctorValues = BoundedReportCollections.Materialize(population
                    .Where(cycle => cycle.DoctorArrivedAt.HasValue && cycle.SeatedToDoctorSeconds >= 0)
                    .Select(cycle => cycle.SeatedToDoctorSeconds));
                var readyWaitValues = BoundedReportCollections.Materialize(population
                    .Select(cycle => cycle.ReadyToDoctorSeconds)
                    .Where(value => value is >= 0)
                    .Select(value => value!.Value));
                var turnoverValues = BoundedReportCollections.Materialize(population
                    .Select(cycle => cycle.TurnoverSeconds)
                    .Where(value => value is >= 0)
                    .Select(value => value!.Value));
                return new ReportTrendBucket(
                    FormatDate(group.Key),
                    FormatDate(group.Key.AddDays(7)),
                    seatedToDoctorValues.Count,
                    Median(seatedToDoctorValues),
                    Average(seatedToDoctorValues),
                    turnoverValues.Count,
                    Median(turnoverValues),
                    Average(turnoverValues),
                    ReportSampleContext.ForPopulation(seatedToDoctorValues.Count),
                    ReportSampleContext.Create(population.Count, turnoverValues.Count),
                    readyWaitValues.Count,
                    MedianOrNull(readyWaitValues),
                    ReportSampleContext.Create(population.Count, readyWaitValues.Count),
                    ReportSampleContext.Create(population.Count, seatedToDoctorValues.Count));
            })
            .ToList();

        return new ReportTrendSnapshot(WeeklyBucketSize, buckets);
    }

    private static DateOnly WeekStart(DateOnly day)
    {
        var offset = ((int)day.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return day.AddDays(-offset);
    }

    private static string FormatDate(DateOnly day) =>
        day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static double Average(IReadOnlyList<int> orderedValues) =>
        orderedValues.Count == 0 ? 0 : orderedValues.Average();

    private static double Median(IReadOnlyList<int> values) =>
        BoundedReportCollections.Median(values) ?? 0d;

    private static double? MedianOrNull(IReadOnlyList<int> values) =>
        BoundedReportCollections.Median(values);
}
