using System.Globalization;

namespace ChairSide.Board.Services;

/// <summary>
/// Additive report trend read model for historical operational timing. This first slice is
/// summary-only: weekly seated-to-doctor wait buckets, no charts, no scoring, no projections.
/// </summary>
public sealed record ReportTrendSnapshot(
    string BucketSize,
    IReadOnlyList<ReportTrendBucket> Buckets);

/// <summary>
/// One Monday-start UTC week of completed-cycle wait timing. <see cref="EndDate"/> is exclusive.
/// </summary>
public sealed record ReportTrendBucket(
    string StartDate,
    string EndDate,
    int CompletedCycleCount,
    double MedianSeatedToDoctorSeconds,
    double AverageSeatedToDoctorSeconds);

/// <summary>
/// Builds report trend snapshots over a caller-supplied standard/included completed-cycle population.
/// The builder groups only by observed completion dates and wait durations; it does not infer
/// capacity, rankings, forecasts, or projection output.
/// </summary>
public static class ReportTrendSnapshotBuilder
{
    public const string WeeklyBucketSize = "Week";

    public static ReportTrendSnapshot BuildWeekly(IEnumerable<CompletedRoomCycle> cycles)
    {
        ArgumentNullException.ThrowIfNull(cycles);

        var eligible = new List<CompletedRoomCycle>();
        foreach (var cycle in cycles)
        {
            if (cycle is null || cycle.DoctorCompleteAt is null || cycle.SeatedToDoctorSeconds < 0)
            {
                continue;
            }

            eligible.Add(cycle);
        }

        var buckets = eligible
            .GroupBy(cycle => WeekStart(DateOnly.FromDateTime(cycle.DoctorCompleteAt!.Value.UtcDateTime)))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var values = group.Select(cycle => cycle.SeatedToDoctorSeconds).Order().ToList();
                var average = values.Count == 0 ? 0 : values.Average();
                return new ReportTrendBucket(
                    FormatDate(group.Key),
                    FormatDate(group.Key.AddDays(7)),
                    values.Count,
                    Median(values),
                    average);
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

    private static double Median(IReadOnlyList<int> orderedValues)
    {
        if (orderedValues.Count == 0)
        {
            return 0;
        }

        var middle = orderedValues.Count / 2;
        return orderedValues.Count % 2 == 1
            ? orderedValues[middle]
            : (orderedValues[middle - 1] + orderedValues[middle]) / 2.0;
    }
}
