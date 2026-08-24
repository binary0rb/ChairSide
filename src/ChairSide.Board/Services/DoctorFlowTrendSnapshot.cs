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
        return doctors
            .Select(doctor => BuildSeries(
                doctor,
                scopedStandardPhaseCycles,
                scopedStandardCompletedCycles,
                observedDoctorFlowDays,
                window))
            .ToList();
    }

    private static DoctorFlowTrendSeries BuildSeries(
        DoctorFlowTrendIdentity doctor,
        IReadOnlyList<CompletedRoomCycle> scopedStandardPhaseCycles,
        IReadOnlyList<CompletedRoomCycle> scopedStandardCompletedCycles,
        IReadOnlyList<ObservedDoctorFlowDay> observedDoctorFlowDays,
        TrendWindow? window)
    {
        if (window is null)
        {
            return new DoctorFlowTrendSeries(
                doctor.DoctorId,
                doctor.DoctorName,
                WeeklyBucketSize,
                null,
                null,
                []);
        }

        var doctorDays = observedDoctorFlowDays
            .Where(day => IsDoctor(day.DoctorId, doctor.DoctorId))
            .Select(day => new ParsedObservedDoctorFlowDay(day, ParseDate(day.ReportDate)))
            .Where(item => item.ReportDate.HasValue
                && item.ReportDate.Value >= window.EffectiveStart
                && item.ReportDate.Value < window.EffectiveEndExclusive)
            .ToList();

        var buckets = new List<DoctorFlowTrendBucket>();
        for (var start = window.CalendarStart; start <= window.CalendarEnd; start = start.AddDays(7))
        {
            var end = start.AddDays(7);
            var effectiveStart = start < window.EffectiveStart ? window.EffectiveStart : start;
            var effectiveEnd = end > window.EffectiveEndExclusive ? window.EffectiveEndExclusive : end;
            var phasePopulation = BoundedReportCollections.Materialize(scopedStandardPhaseCycles
                .Where(cycle => IsDoctor(cycle.AssignedDoctor, doctor.DoctorId)
                    && cycle.DoctorCompleteAt.HasValue
                    && IsInBucket(cycle.DoctorCompleteAt.Value, effectiveStart, effectiveEnd)));
            var completedPopulation = BoundedReportCollections.Materialize(scopedStandardCompletedCycles
                .Where(cycle => IsDoctor(cycle.AssignedDoctor, doctor.DoctorId)
                    && cycle.DoctorCompleteAt.HasValue
                    && IsInBucket(cycle.DoctorCompleteAt.Value, effectiveStart, effectiveEnd)));
            var canonicalDays = doctorDays
                .Where(item => item.ReportDate!.Value >= effectiveStart
                    && item.ReportDate.Value < effectiveEnd)
                .Select(item => item.Day)
                .ToList();
            var readyWaitValues = BoundedReportCollections.Materialize(phasePopulation
                .Select(ReportsSnapshotBuilder.TruthfulReadyWaitSeconds)
                .Where(value => value.HasValue));
            var doctorTimeValues = BoundedReportCollections.Materialize(phasePopulation
                .Select(ReportsSnapshotBuilder.TruthfulDoctorTimeSeconds)
                .Where(value => value.HasValue));
            var representedCompletedDates = completedPopulation
                .Select(cycle => DateOnly.FromDateTime(cycle.DoctorCompleteAt!.Value.UtcDateTime))
                .Distinct()
                .Count();

            buckets.Add(new DoctorFlowTrendBucket(
                FormatDate(start),
                FormatDate(end),
                FormatDate(effectiveStart),
                FormatDate(effectiveEnd),
                effectiveStart != start || effectiveEnd != end,
                ReportsSnapshotBuilder.MedianSecondsOrNull(readyWaitValues),
                ReportsSnapshotBuilder.MedianSecondsOrNull(doctorTimeValues),
                completedPopulation.Count == 0 ? null : completedPopulation.Count,
                ReportsSnapshotBuilder.MedianWholeMinutesOrNull(
                    canonicalDays.Select(day => day.ObservedClinicalSpanMinutes)),
                new ReportDoctorFlowTrendMetricSampleContext(
                    ReadyWait: ReportSampleContext.Create(phasePopulation.Count, readyWaitValues.Count),
                    DoctorTime: ReportSampleContext.Create(phasePopulation.Count, doctorTimeValues.Count),
                    CompletedCases: ReportSampleContext.ForPopulation(completedPopulation.Count),
                    ObservedClinicalSpan: ReportSampleContext.Create(
                        representedCompletedDates,
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

    private static bool IsInBucket(DateTimeOffset timestamp, DateOnly start, DateOnly end)
    {
        var date = DateOnly.FromDateTime(timestamp.UtcDateTime);
        return date >= start && date < end;
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

    private sealed record ParsedObservedDoctorFlowDay(
        ObservedDoctorFlowDay Day,
        DateOnly? ReportDate);
}
