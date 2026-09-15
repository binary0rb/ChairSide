using System.Globalization;

namespace ChairSide.Board.Services;

/// <summary>
/// One doctor's descriptive weekly flow history over the shared report-level calendar window.
/// EffectiveEndDate is exclusive. Empty/open-ended reports without a dateable observation retain
/// the doctor series with null window metadata and no invented calendar buckets.
/// </summary>
public sealed record DoctorFlowTrendSeries(
    string DoctorId,
    string DoctorName,
    string BucketSize,
    string? EffectiveStartDate,
    string? EffectiveEndDate,
    IReadOnlyList<DoctorFlowTrendBucket> Buckets);

/// <summary>
/// One Monday-start UTC calendar bucket. EndDate and EffectiveEndDate are exclusive. Effective
/// boundaries expose the exact intersection with the selected report/display window so consumers
/// do not have to infer whether the first or last bucket is partial.
/// </summary>
public sealed record DoctorFlowTrendBucket(
    string StartDate,
    string EndDate,
    string EffectiveStartDate,
    string EffectiveEndDate,
    bool IsPartial,
    double? MedianReadyWaitSeconds,
    double? MedianDoctorTimeSeconds,
    int? CompletedCaseCount,
    double? MedianObservedClinicalSpanMinutes,
    ReportDoctorFlowTrendMetricSampleContext Samples);

public sealed record ReportDoctorFlowTrendMetricSampleContext(
    ReportSampleContext ReadyWait,
    ReportSampleContext DoctorTime,
    ReportSampleContext CompletedCases,
    ReportSampleContext ObservedClinicalSpan);

internal sealed record DoctorFlowTrendIdentity(string DoctorId, string DoctorName);

/// <summary>
/// Builds additive Doctor Trends without changing the existing practice ReportTrendSnapshot.
/// Weekly case metrics remain anchored by DoctorCompleteAt; clinical span consumes only the
/// canonical ObservedDoctorFlowDay.ReportDate projection.
/// </summary>
internal static class DoctorFlowTrendSnapshotBuilder
{
    public const string WeeklyBucketSize = "Week";
    public const int MaximumBucketCount = 12;

    public static IReadOnlyList<DoctorFlowTrendSeries> BuildWeekly(
        IReadOnlyList<DoctorFlowTrendIdentity> doctors,
        IReadOnlyList<CompletedRoomCycle> scopedStandardPhaseCycles,
        IReadOnlyList<CompletedRoomCycle> scopedStandardCompletedCycles,
        IReadOnlyList<ObservedDoctorFlowDay> observedDoctorFlowDays,
        ReportDateRange selectedRange)
    {
        ArgumentNullException.ThrowIfNull(doctors);
        ArgumentNullException.ThrowIfNull(scopedStandardPhaseCycles);
        ArgumentNullException.ThrowIfNull(scopedStandardCompletedCycles);
        ArgumentNullException.ThrowIfNull(observedDoctorFlowDays);

        var window = BuildSharedWindow(scopedStandardPhaseCycles, selectedRange);
        if (window is null)
        {
            return doctors.Select(doctor => new DoctorFlowTrendSeries(
                doctor.DoctorId,
                doctor.DoctorName,
                WeeklyBucketSize,
                null,
                null,
                [])).ToList();
        }

        var phaseFacts = BoundedReportCollections.Materialize(
            ProjectPhaseFacts(doctors, scopedStandardPhaseCycles, window));
        IReadOnlyList<DoctorFlowCompletedFact>? completedFacts = null;
        try
        {
            completedFacts = BoundedReportCollections.Materialize(
                ProjectCompletedFacts(doctors, scopedStandardCompletedCycles, window));
            using var phaseGrouping = BoundedGroupingSet<DoctorFlowPhaseFact, DoctorFlowTrendKey>.Create(
                phaseFacts,
                fact => fact.Key);
            using var completedGrouping = BoundedGroupingSet<DoctorFlowCompletedFact, DoctorFlowTrendKey>.Create(
                completedFacts,
                fact => fact.Key);
            var phaseBuckets = phaseGrouping.Groups.ToDictionary(
                group => group.Key,
                BuildPhaseBucket);
            var completedBuckets = completedGrouping.Groups.ToDictionary(
                group => group.Key,
                BuildCompletedBucket);
            var observedBuckets = BuildObservedBuckets(doctors, observedDoctorFlowDays, window);

            return doctors.Select((doctor, index) => BuildSeries(
                doctor,
                index,
                window,
                phaseBuckets,
                completedBuckets,
                observedBuckets)).ToList();
        }
        finally
        {
            (phaseFacts as IDisposable)?.Dispose();
            (completedFacts as IDisposable)?.Dispose();
        }
    }

    private static DoctorFlowTrendSeries BuildSeries(
        DoctorFlowTrendIdentity doctor,
        int doctorIndex,
        TrendWindow window,
        IReadOnlyDictionary<DoctorFlowTrendKey, DoctorFlowPhaseBucket> phaseBuckets,
        IReadOnlyDictionary<DoctorFlowTrendKey, DoctorFlowCompletedBucket> completedBuckets,
        IReadOnlyDictionary<DoctorFlowTrendKey, IReadOnlyList<ObservedDoctorFlowDay>> observedBuckets)
    {
        var buckets = new List<DoctorFlowTrendBucket>();
        var bucketIndex = 0;
        for (var start = window.CalendarStart; start <= window.CalendarEnd; start = start.AddDays(7), bucketIndex++)
        {
            var end = start.AddDays(7);
            var effectiveStart = start < window.EffectiveStart ? window.EffectiveStart : start;
            var effectiveEnd = end > window.EffectiveEndExclusive ? window.EffectiveEndExclusive : end;
            var key = new DoctorFlowTrendKey(doctorIndex, bucketIndex);
            phaseBuckets.TryGetValue(key, out var phase);
            completedBuckets.TryGetValue(key, out var completed);
            observedBuckets.TryGetValue(key, out var canonicalDays);
            canonicalDays ??= [];

            buckets.Add(new DoctorFlowTrendBucket(
                FormatDate(start),
                FormatDate(end),
                FormatDate(effectiveStart),
                FormatDate(effectiveEnd),
                effectiveStart != start || effectiveEnd != end,
                phase?.MedianReadyWaitSeconds,
                phase?.MedianDoctorTimeSeconds,
                completed?.PopulationCount,
                ReportsSnapshotBuilder.MedianWholeMinutesOrNull(
                    canonicalDays.Select(day => day.ObservedClinicalSpanMinutes)),
                new ReportDoctorFlowTrendMetricSampleContext(
                    ReadyWait: ReportSampleContext.Create(
                        phase?.PopulationCount ?? 0,
                        phase?.ReadyWaitContributorCount ?? 0),
                    DoctorTime: ReportSampleContext.Create(
                        phase?.PopulationCount ?? 0,
                        phase?.DoctorTimeContributorCount ?? 0),
                    CompletedCases: ReportSampleContext.ForPopulation(completed?.PopulationCount ?? 0),
                    ObservedClinicalSpan: ReportSampleContext.Create(
                        completed?.RepresentedDateCount ?? 0,
                        canonicalDays.Count))));
        }

        return new DoctorFlowTrendSeries(
            doctor.DoctorId,
            doctor.DoctorName,
            WeeklyBucketSize,
            FormatDate(window.EffectiveStart),
            FormatDate(window.EffectiveEndExclusive),
            buckets);
    }

    private static IEnumerable<DoctorFlowPhaseFact> ProjectPhaseFacts(
        IReadOnlyList<DoctorFlowTrendIdentity> doctors,
        IEnumerable<CompletedRoomCycle> cycles,
        TrendWindow window)
    {
        foreach (var cycle in cycles)
        {
            if (!TryGetBucketIndex(cycle.DoctorCompleteAt, window, out var bucketIndex)) continue;
            for (var doctorIndex = 0; doctorIndex < doctors.Count; doctorIndex++)
            {
                if (!IsDoctor(cycle.AssignedDoctor, doctors[doctorIndex].DoctorId)) continue;
                yield return new DoctorFlowPhaseFact(
                    new DoctorFlowTrendKey(doctorIndex, bucketIndex),
                    ReportsSnapshotBuilder.TruthfulReadyWaitSeconds(cycle),
                    ReportsSnapshotBuilder.TruthfulDoctorTimeSeconds(cycle));
            }
        }
    }

    private static IEnumerable<DoctorFlowCompletedFact> ProjectCompletedFacts(
        IReadOnlyList<DoctorFlowTrendIdentity> doctors,
        IEnumerable<CompletedRoomCycle> cycles,
        TrendWindow window)
    {
        foreach (var cycle in cycles)
        {
            if (!TryGetBucketIndex(cycle.DoctorCompleteAt, window, out var bucketIndex)) continue;
            var reportDate = DateOnly.FromDateTime(cycle.DoctorCompleteAt!.Value.UtcDateTime);
            for (var doctorIndex = 0; doctorIndex < doctors.Count; doctorIndex++)
            {
                if (IsDoctor(cycle.AssignedDoctor, doctors[doctorIndex].DoctorId))
                {
                    yield return new DoctorFlowCompletedFact(
                        new DoctorFlowTrendKey(doctorIndex, bucketIndex),
                        reportDate);
                }
            }
        }
    }

    private static DoctorFlowPhaseBucket BuildPhaseBucket(IGrouping<DoctorFlowTrendKey, DoctorFlowPhaseFact> group)
    {
        var population = BoundedReportCollections.Materialize(group);
        try
        {
            using var readyWait = NumericOrderStatistics.Create(
                population.Where(fact => fact.ReadyWaitSeconds.HasValue)
                    .Select(fact => (double)fact.ReadyWaitSeconds!.Value));
            using var doctorTime = NumericOrderStatistics.Create(
                population.Where(fact => fact.DoctorTimeSeconds.HasValue)
                    .Select(fact => (double)fact.DoctorTimeSeconds!.Value));
            return new DoctorFlowPhaseBucket(
                population.Count,
                readyWait.Count,
                doctorTime.Count,
                readyWait.Median,
                doctorTime.Median);
        }
        finally
        {
            (population as IDisposable)?.Dispose();
        }
    }

    private static DoctorFlowCompletedBucket BuildCompletedBucket(
        IGrouping<DoctorFlowTrendKey, DoctorFlowCompletedFact> group)
    {
        var populationCount = 0;
        HashSet<DateOnly> representedDates = [];
        foreach (var fact in group)
        {
            populationCount++;
            representedDates.Add(fact.ReportDate);
        }
        return new DoctorFlowCompletedBucket(populationCount, representedDates.Count);
    }

    private static IReadOnlyDictionary<DoctorFlowTrendKey, IReadOnlyList<ObservedDoctorFlowDay>> BuildObservedBuckets(
        IReadOnlyList<DoctorFlowTrendIdentity> doctors,
        IEnumerable<ObservedDoctorFlowDay> days,
        TrendWindow window)
    {
        var buckets = new Dictionary<DoctorFlowTrendKey, List<ObservedDoctorFlowDay>>();
        foreach (var day in days)
        {
            var reportDate = ParseDate(day.ReportDate);
            if (!TryGetBucketIndex(reportDate, window, out var bucketIndex)) continue;
            for (var doctorIndex = 0; doctorIndex < doctors.Count; doctorIndex++)
            {
                if (!IsDoctor(day.DoctorId, doctors[doctorIndex].DoctorId)) continue;
                var key = new DoctorFlowTrendKey(doctorIndex, bucketIndex);
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    buckets.Add(key, bucket);
                }
                bucket.Add(day);
            }
        }
        return buckets.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ObservedDoctorFlowDay>)pair.Value);
    }

    private static bool TryGetBucketIndex(
        DateTimeOffset? timestamp,
        TrendWindow window,
        out int bucketIndex) =>
        TryGetBucketIndex(
            timestamp.HasValue ? DateOnly.FromDateTime(timestamp.Value.UtcDateTime) : null,
            window,
            out bucketIndex);

    private static bool TryGetBucketIndex(
        DateOnly? reportDate,
        TrendWindow window,
        out int bucketIndex)
    {
        if (!reportDate.HasValue
            || reportDate.Value < window.EffectiveStart
            || reportDate.Value >= window.EffectiveEndExclusive)
        {
            bucketIndex = -1;
            return false;
        }

        bucketIndex = (WeekStart(reportDate.Value).DayNumber - window.CalendarStart.DayNumber) / 7;
        return true;
    }

    private static TrendWindow? BuildSharedWindow(
        IReadOnlyList<CompletedRoomCycle> scopedStandardPhaseCycles,
        ReportDateRange selectedRange)
    {
        var hasExplicitEnd = selectedRange.EndDate.HasValue;
        var latestDateableObservation = default(DateOnly);
        var hasDateableObservation = false;
        foreach (var cycle in scopedStandardPhaseCycles.Where(cycle => cycle.DoctorCompleteAt.HasValue))
        {
            var date = DateOnly.FromDateTime(cycle.DoctorCompleteAt!.Value.UtcDateTime);
            if (!hasDateableObservation || date > latestDateableObservation) latestDateableObservation = date;
            hasDateableObservation = true;
        }

        DateOnly endInclusive;
        if (hasExplicitEnd)
        {
            endInclusive = selectedRange.EndDate!.Value;
        }
        else if (hasDateableObservation)
        {
            endInclusive = latestDateableObservation;
        }
        else
        {
            return null;
        }

        var calendarEnd = WeekStart(endInclusive);
        var cappedCalendarStart = calendarEnd.AddDays(-7 * (MaximumBucketCount - 1));
        var selectedCalendarStart = selectedRange.StartDate.HasValue
            ? WeekStart(selectedRange.StartDate.Value)
            : cappedCalendarStart;
        var calendarStart = selectedCalendarStart > cappedCalendarStart
            ? selectedCalendarStart
            : cappedCalendarStart;
        var effectiveStart = selectedRange.StartDate.HasValue
            && selectedRange.StartDate.Value > calendarStart
                ? selectedRange.StartDate.Value
                : calendarStart;
        var effectiveEndExclusive = hasExplicitEnd
            ? selectedRange.EndDate!.Value.AddDays(1)
            : endInclusive.AddDays(1);

        return new TrendWindow(
            calendarStart,
            calendarEnd,
            effectiveStart,
            effectiveEndExclusive);
    }

    private static bool IsDoctor(string? value, string doctorId) =>
        string.Equals(value, doctorId, StringComparison.OrdinalIgnoreCase);

    private static DateOnly WeekStart(DateOnly day)
    {
        var offset = ((int)day.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return day.AddDays(-offset);
    }

    private static string FormatDate(DateOnly day) =>
        day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? ParseDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private sealed record TrendWindow(
        DateOnly CalendarStart,
        DateOnly CalendarEnd,
        DateOnly EffectiveStart,
        DateOnly EffectiveEndExclusive);

    private sealed record DoctorFlowTrendKey(int DoctorIndex, int BucketIndex);

    private sealed record DoctorFlowPhaseFact(
        DoctorFlowTrendKey Key,
        int? ReadyWaitSeconds,
        int? DoctorTimeSeconds);

    private sealed record DoctorFlowCompletedFact(
        DoctorFlowTrendKey Key,
        DateOnly ReportDate);

    private sealed record DoctorFlowPhaseBucket(
        int PopulationCount,
        int ReadyWaitContributorCount,
        int DoctorTimeContributorCount,
        double? MedianReadyWaitSeconds,
        double? MedianDoctorTimeSeconds);

    private sealed record DoctorFlowCompletedBucket(
        int PopulationCount,
        int RepresentedDateCount);
}
