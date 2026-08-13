using ChairSide.Board.Services;

namespace ChairSide.Board.Tests;

public sealed class DoctorFlowTrendSnapshotTests
{
    private static readonly DoctorFlowTrendIdentity Otte = new("otte", "Dr. Otte");
    private static readonly DoctorFlowTrendIdentity Pledger = new("pledger", "Dr. Pledger");

    [Fact]
    public void Finite_range_builds_one_common_monday_skeleton_with_partial_boundaries()
    {
        var otteSunday = Cycle("otte", Utc(2026, 6, 14, 23), readyWaitSeconds: 60);
        var pledgerOffsetSunday = Cycle(
            "pledger",
            new DateTimeOffset(2026, 6, 15, 1, 0, 0, TimeSpan.FromHours(2)),
            readyWaitSeconds: 120);
        var nextMonday = Cycle("otte", Utc(2026, 6, 22, 0), readyWaitSeconds: 180);

        var series = Build(
            [Otte, Pledger],
            [otteSunday, pledgerOffsetSunday, nextMonday],
            ReportDateRange.FromDates(new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 23)));

        Assert.Equal(2, series.Count);
        Assert.All(series, item =>
        {
            Assert.Equal("2026-06-10", item.EffectiveStartDate);
            Assert.Equal("2026-06-24", item.EffectiveEndDate);
            Assert.Equal(
                ["2026-06-08", "2026-06-15", "2026-06-22"],
                item.Buckets.Select(bucket => bucket.StartDate));
            Assert.Equal(
                ["2026-06-15", "2026-06-22", "2026-06-29"],
                item.Buckets.Select(bucket => bucket.EndDate));
            Assert.True(item.Buckets[0].IsPartial);
            Assert.False(item.Buckets[1].IsPartial);
            Assert.True(item.Buckets[2].IsPartial);
        });

        Assert.Equal(1, series[0].Buckets[0].CompletedCaseCount);
        Assert.Equal(1, series[1].Buckets[0].CompletedCaseCount);
        Assert.Equal(1, series[0].Buckets[2].CompletedCaseCount);
    }

    [Fact]
    public void Finite_range_never_widens_and_keeps_missing_middle_week_as_empty_gap()
    {
        var outsideStart = Cycle("otte", Utc(2026, 6, 9, 12));
        var first = Cycle("otte", Utc(2026, 6, 10, 12));
        var last = Cycle("otte", Utc(2026, 6, 22, 12));
        var outsideEnd = Cycle("otte", Utc(2026, 6, 24, 12));

        var bucket = Assert.Single(Build(
            [Otte],
            [outsideStart, first, last, outsideEnd],
            ReportDateRange.FromDates(new DateOnly(2026, 6, 10), new DateOnly(2026, 6, 23))));

        Assert.Equal(3, bucket.Buckets.Count);
        Assert.Equal(1, bucket.Buckets[0].CompletedCaseCount);
        Assert.Null(bucket.Buckets[1].CompletedCaseCount);
        Assert.Equal(ReportSampleStates.Empty, bucket.Buckets[1].Samples.CompletedCases.State);
        Assert.Equal(1, bucket.Buckets[2].CompletedCaseCount);
    }

    [Fact]
    public void Selected_range_is_capped_at_trailing_twelve_buckets_and_short_ranges_stay_short()
    {
        var longSeries = Assert.Single(Build(
            [Otte],
            [],
            ReportDateRange.FromDates(new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30))));
        var shortSeries = Assert.Single(Build(
            [Otte],
            [],
            ReportDateRange.FromDates(new DateOnly(2026, 4, 13), new DateOnly(2026, 4, 30))));

        Assert.Equal(DoctorFlowTrendSnapshotBuilder.MaximumBucketCount, longSeries.Buckets.Count);
        Assert.Equal("2026-02-09", longSeries.Buckets[0].StartDate);
        Assert.Equal("2026-05-01", longSeries.EffectiveEndDate);
        Assert.Equal(3, shortSeries.Buckets.Count);
        Assert.Equal("2026-04-13", shortSeries.EffectiveStartDate);
    }

    [Fact]
    public void End_only_range_uses_explicit_end_and_retains_empty_trailing_weeks()
    {
        var series = Assert.Single(Build(
            [Otte],
            [Cycle("otte", Utc(2026, 6, 15, 12))],
            ReportDateRange.FromDates(null, new DateOnly(2026, 8, 1))));

        Assert.Equal("2026-05-11", series.EffectiveStartDate);
        Assert.Equal("2026-08-02", series.EffectiveEndDate);
        Assert.Equal(DoctorFlowTrendSnapshotBuilder.MaximumBucketCount, series.Buckets.Count);
        Assert.Equal("2026-07-27", series.Buckets[^1].StartDate);
        Assert.True(series.Buckets[^1].IsPartial);
        Assert.Equal(1, Assert.Single(
            series.Buckets,
            bucket => bucket.StartDate == "2026-06-15").CompletedCaseCount);
        var trailingGaps = series.Buckets
            .Where(bucket => bucket.StartDate.CompareTo("2026-06-15") > 0)
            .ToList();
        Assert.Equal(6, trailingGaps.Count);
        Assert.All(trailingGaps, bucket =>
        {
            Assert.Null(bucket.MedianReadyWaitSeconds);
            Assert.Null(bucket.MedianDoctorTimeSeconds);
            Assert.Null(bucket.CompletedCaseCount);
            Assert.Null(bucket.MedianObservedClinicalSpanMinutes);
            Assert.Equal(ReportSampleStates.Empty, bucket.Samples.ReadyWait.State);
            Assert.Equal(ReportSampleStates.Empty, bucket.Samples.DoctorTime.State);
            Assert.Equal(ReportSampleStates.Empty, bucket.Samples.CompletedCases.State);
            Assert.Equal(ReportSampleStates.Empty, bucket.Samples.ObservedClinicalSpan.State);
        });
    }

    [Fact]
    public void End_only_range_without_observations_emits_empty_gaps_through_explicit_end()
    {
        var series = Assert.Single(Build(
            [Otte],
            [],
            ReportDateRange.FromDates(null, new DateOnly(2026, 8, 1))));

        Assert.Equal("2026-05-11", series.EffectiveStartDate);
        Assert.Equal("2026-08-02", series.EffectiveEndDate);
        Assert.Equal(DoctorFlowTrendSnapshotBuilder.MaximumBucketCount, series.Buckets.Count);
        Assert.All(series.Buckets, bucket =>
        {
            Assert.Null(bucket.MedianReadyWaitSeconds);
            Assert.Null(bucket.MedianDoctorTimeSeconds);
            Assert.Null(bucket.CompletedCaseCount);
            Assert.Null(bucket.MedianObservedClinicalSpanMinutes);
            Assert.Equal(ReportSampleStates.Empty, bucket.Samples.ReadyWait.State);
            Assert.Equal(ReportSampleStates.Empty, bucket.Samples.DoctorTime.State);
            Assert.Equal(ReportSampleStates.Empty, bucket.Samples.CompletedCases.State);
            Assert.Equal(ReportSampleStates.Empty, bucket.Samples.ObservedClinicalSpan.State);
        });
    }

    [Fact]
    public void Start_only_range_uses_latest_observation_and_invents_no_window_without_one()
    {
        var range = ReportDateRange.FromDates(new DateOnly(2026, 6, 10), null);
        var observed = Assert.Single(Build(
            [Otte],
            [Cycle("otte", Utc(2026, 7, 22, 12))],
            range));

        Assert.Equal("2026-06-10", observed.EffectiveStartDate);
        Assert.Equal("2026-07-23", observed.EffectiveEndDate);
        Assert.Equal(7, observed.Buckets.Count);
        Assert.Equal("2026-06-08", observed.Buckets[0].StartDate);
        Assert.True(observed.Buckets[0].IsPartial);
        Assert.Equal("2026-07-20", observed.Buckets[^1].StartDate);
        Assert.True(observed.Buckets[^1].IsPartial);

        var unobserved = Assert.Single(Build([Otte], [], range));

        Assert.Null(unobserved.EffectiveStartDate);
        Assert.Null(unobserved.EffectiveEndDate);
        Assert.Empty(unobserved.Buckets);
    }

    [Fact]
    public void All_time_uses_report_level_latest_dateable_observation_for_every_doctor()
    {
        var series = Build(
            [Otte, Pledger],
            [
                Cycle("otte", Utc(2026, 6, 10, 12)),
                Cycle("pledger", Utc(2026, 7, 22, 12))
            ],
            ReportDateRange.AllTime);

        Assert.All(series, item =>
        {
            Assert.Equal("2026-05-04", item.EffectiveStartDate);
            Assert.Equal("2026-07-23", item.EffectiveEndDate);
            Assert.Equal(12, item.Buckets.Count);
            Assert.Equal("2026-07-20", item.Buckets[^1].StartDate);
            Assert.True(item.Buckets[^1].IsPartial);
        });
    }

    [Fact]
    public void All_time_without_dateable_observation_keeps_series_but_invents_no_history()
    {
        var undated = Cycle("otte", completeAt: null, readyWaitSeconds: 60, doctorTimeSeconds: 300);

        var series = Assert.Single(Build([Otte], [undated], ReportDateRange.AllTime));

        Assert.Null(series.EffectiveStartDate);
        Assert.Null(series.EffectiveEndDate);
        Assert.Empty(series.Buckets);
    }

    [Fact]
    public void Weekly_metrics_use_underlying_observations_and_metric_specific_samples()
    {
        var phase = new[]
        {
            Cycle("otte", Utc(2026, 6, 8, 10), readyWaitSeconds: 0, doctorTimeSeconds: 300),
            Cycle("otte", Utc(2026, 6, 9, 10), readyWaitSeconds: 60, doctorTimeSeconds: 600),
            Cycle("otte", Utc(2026, 6, 10, 10), readyWaitSeconds: null, doctorTimeSeconds: 900),
            Cycle("otte", Utc(2026, 6, 11, 10), readyWaitSeconds: 180, doctorTimeSeconds: 1200, available: false),
            Cycle("otte", Utc(2026, 6, 12, 10), readyWaitSeconds: 900, doctorTimeSeconds: null, available: false)
        };
        var days = new[]
        {
            ObservedDay("otte", "2026-06-08", 30),
            ObservedDay("otte", "2026-06-09", 90)
        };

        var bucket = Assert.Single(Assert.Single(DoctorFlowTrendSnapshotBuilder.BuildWeekly(
            [Otte],
            phase,
            phase.Where(cycle => cycle.RoomAvailableAt.HasValue).ToList(),
            days,
            ReportDateRange.FromDates(new DateOnly(2026, 6, 8), new DateOnly(2026, 6, 14)))).Buckets);

        Assert.Equal(120, bucket.MedianReadyWaitSeconds);
        Assert.Equal(750, bucket.MedianDoctorTimeSeconds);
        Assert.Equal(3, bucket.CompletedCaseCount);
        Assert.Equal(60, bucket.MedianObservedClinicalSpanMinutes);
        Assert.Equal((5, 4, ReportSampleStates.Limited), Sample(bucket.Samples.ReadyWait));
        Assert.Equal((5, 4, ReportSampleStates.Limited), Sample(bucket.Samples.DoctorTime));
        Assert.Equal((3, 3, ReportSampleStates.Limited), Sample(bucket.Samples.CompletedCases));
        Assert.Equal((3, 2, ReportSampleStates.Limited), Sample(bucket.Samples.ObservedClinicalSpan));
    }

    [Fact]
    public void Weekly_median_is_not_a_median_of_daily_medians()
    {
        var cycles = new[]
        {
            Cycle("otte", Utc(2026, 6, 8, 10), readyWaitSeconds: 0),
            Cycle("otte", Utc(2026, 6, 8, 11), readyWaitSeconds: 1000),
            Cycle("otte", Utc(2026, 6, 9, 10), readyWaitSeconds: 1000)
        };

        var bucket = Assert.Single(Assert.Single(Build(
            [Otte],
            cycles,
            ReportDateRange.FromDates(new DateOnly(2026, 6, 8), new DateOnly(2026, 6, 14)))).Buckets);

        Assert.Equal(1000, bucket.MedianReadyWaitSeconds);
    }

    [Fact]
    public void Weekly_odd_medians_use_underlying_ready_wait_and_doctor_time_observations()
    {
        var cycles = new[]
        {
            Cycle("otte", Utc(2026, 6, 8, 10), readyWaitSeconds: 60, doctorTimeSeconds: 300),
            Cycle("otte", Utc(2026, 6, 9, 10), readyWaitSeconds: 900, doctorTimeSeconds: 1200),
            Cycle("otte", Utc(2026, 6, 10, 10), readyWaitSeconds: 180, doctorTimeSeconds: 600)
        };

        var bucket = Assert.Single(Assert.Single(Build(
            [Otte],
            cycles,
            ReportDateRange.FromDates(new DateOnly(2026, 6, 8), new DateOnly(2026, 6, 14)))).Buckets);

        Assert.Equal(180, bucket.MedianReadyWaitSeconds);
        Assert.Equal(600, bucket.MedianDoctorTimeSeconds);
    }

    [Fact]
    public void Empty_unavailable_sufficient_and_truthful_zero_remain_distinct()
    {
        var unavailable = Cycle("otte", Utc(2026, 6, 8, 10), readyWaitSeconds: null);
        var zeros = Enumerable.Range(0, 5)
            .Select(index => Cycle("otte", Utc(2026, 6, 15, 8 + index), readyWaitSeconds: 0))
            .ToList();
        var cycles = new[] { unavailable }.Concat(zeros).ToList();

        var series = Assert.Single(Build(
            [Otte],
            cycles,
            ReportDateRange.FromDates(new DateOnly(2026, 6, 8), new DateOnly(2026, 6, 28))));

        Assert.Null(series.Buckets[0].MedianReadyWaitSeconds);
        Assert.Equal(ReportSampleStates.Unavailable, series.Buckets[0].Samples.ReadyWait.State);
        Assert.Equal(0, series.Buckets[1].MedianReadyWaitSeconds);
        Assert.Equal(ReportSampleStates.Sufficient, series.Buckets[1].Samples.ReadyWait.State);
        Assert.Null(series.Buckets[2].MedianReadyWaitSeconds);
        Assert.Equal(ReportSampleStates.Empty, series.Buckets[2].Samples.ReadyWait.State);
    }

    [Fact]
    public void Canonical_ready_anchored_span_is_the_only_clinical_span_authority()
    {
        var seated = Utc(2026, 6, 8, 8);
        var ready = Utc(2026, 6, 8, 8, 30);
        var complete = Utc(2026, 6, 8, 9);
        var cycle = Cycle("otte", complete, readyWaitSeconds: 600, doctorTimeSeconds: 1200);
        cycle.SeatedAt = seated;
        cycle.ReadyForDoctorAt = ready;
        cycle.DoctorArrivedAt = Utc(2026, 6, 8, 8, 40);
        cycle.ReadyToDoctorSeconds = 600;
        cycle.DoctorInRoomSeconds = 1200;

        var snapshot = CreateBuilder().Build([cycle], [], ReportDateRange.AllTime);
        var otteSeries = Assert.Single(snapshot.DoctorFlowTrends!, item => item.DoctorId == "otte");
        var bucket = otteSeries.Buckets[^1];

        Assert.Equal(30, bucket.MedianObservedClinicalSpanMinutes);
        Assert.Equal(60, Assert.Single(snapshot.ObservedDoctorDays!).ObservedClinicalSpanMinutes);
    }

    [Fact]
    public void Practice_and_doctor_scopes_preserve_roster_history_and_sedation_isolation()
    {
        var cycles = new[]
        {
            Cycle("otte", Utc(2026, 6, 8, 10), procedureCode: "EXT"),
            Cycle("pledger", Utc(2026, 6, 9, 10), procedureCode: "EXT+SED"),
            Cycle("legacy", Utc(2026, 6, 10, 10), procedureCode: "EXT")
        };
        var builder = CreateBuilder();
        var practice = builder.Build(cycles, [], ReportQuery.Default);
        var detailed = builder.Build(cycles, [], ReportQuery.FromStrings(
            null,
            null,
            ReportScopeKinds.Practice,
            null,
            ReportSedationSegments.All,
            ReportProcedureGroupings.DetailedVariant));
        var sedation = builder.Build(cycles, [], ReportQuery.FromStrings(
            null,
            null,
            ReportScopeKinds.Practice,
            null,
            ReportSedationSegments.Sedation,
            ReportProcedureGroupings.DetailedVariant));
        var historical = builder.Build(cycles, [], ReportQuery.FromStrings(
            null,
            null,
            ReportScopeKinds.Doctor,
            "legacy",
            ReportSedationSegments.All,
            ReportProcedureGroupings.Family));

        var practiceSeries = Assert.IsAssignableFrom<IReadOnlyList<DoctorFlowTrendSeries>>(practice.DoctorFlowTrends);
        var detailedSeries = Assert.IsAssignableFrom<IReadOnlyList<DoctorFlowTrendSeries>>(detailed.DoctorFlowTrends);
        Assert.Equal(["otte", "pledger", "legacy"], practiceSeries.Select(item => item.DoctorId));
        Assert.Equal(
            practiceSeries.SelectMany(item => item.Buckets).Select(item => item.CompletedCaseCount),
            detailedSeries.SelectMany(item => item.Buckets).Select(item => item.CompletedCaseCount));
        var sedationSeries = Assert.IsAssignableFrom<IReadOnlyList<DoctorFlowTrendSeries>>(sedation.DoctorFlowTrends);
        Assert.Equal(["otte", "pledger"], sedationSeries.Select(item => item.DoctorId));
        Assert.Null(sedationSeries[0].Buckets[^1].CompletedCaseCount);
        Assert.Equal(1, sedationSeries[1].Buckets[^1].CompletedCaseCount);
        Assert.Equal("legacy", Assert.Single(historical.DoctorFlowTrends!).DoctorId);
    }

    private static IReadOnlyList<DoctorFlowTrendSeries> Build(
        IReadOnlyList<DoctorFlowTrendIdentity> doctors,
        IReadOnlyList<CompletedRoomCycle> phaseCycles,
        ReportDateRange range) =>
        DoctorFlowTrendSnapshotBuilder.BuildWeekly(
            doctors,
            phaseCycles,
            phaseCycles.Where(cycle => cycle.RoomAvailableAt.HasValue).ToList(),
            [],
            range);

    private static ReportsSnapshotBuilder CreateBuilder()
    {
        Doctor[] doctors =
        [
            new("otte", "Dr. Otte", "LDO", "#dc2626"),
            new("pledger", "Dr. Pledger", "JWP", "#16a34a"),
            new("legacy", "Dr. Legacy", "LEG", "#64748b")
        ];
        ProcedureCategory[] procedures =
        [
            new("extraction", "EXT", "Extraction", "forceps", SedationEligible: true)
        ];
        return new ReportsSnapshotBuilder(doctors, doctors[..2], procedures, procedures);
    }

    private static CompletedRoomCycle Cycle(
        string doctorId,
        DateTimeOffset? completeAt,
        int? readyWaitSeconds = 60,
        int? doctorTimeSeconds = 300,
        bool available = true,
        string procedureCode = "EXT")
    {
        var arrivedAt = completeAt?.AddSeconds(-(doctorTimeSeconds ?? 300)) ?? Utc(2026, 6, 8, 9);
        var readyAt = readyWaitSeconds.HasValue ? arrivedAt.AddSeconds(-readyWaitSeconds.Value) : arrivedAt.AddMinutes(-5);
        return new CompletedRoomCycle
        {
            AssignedDoctor = doctorId,
            ProcedureCode = procedureCode,
            SeatedAt = readyAt.AddMinutes(-5),
            ReadyForDoctorAt = readyWaitSeconds.HasValue ? readyAt : null,
            DoctorArrivedAt = arrivedAt,
            DoctorCompleteAt = completeAt,
            RoomAvailableAt = completeAt.HasValue && available ? completeAt.Value.AddMinutes(5) : null,
            ReadyToDoctorSeconds = readyWaitSeconds,
            DoctorInRoomSeconds = doctorTimeSeconds,
            SeatedToDoctorSeconds = 600,
            PrepSeconds = 300,
            TurnoverSeconds = completeAt.HasValue && available ? 300 : null,
            TotalRoomCycleSeconds = completeAt.HasValue && available ? 1200 : null
        };
    }

    private static ObservedDoctorFlowDay ObservedDay(string doctorId, string date, int spanMinutes) =>
        new(
            doctorId,
            doctorId == "otte" ? "Dr. Otte" : "Dr. Pledger",
            date,
            1,
            Utc(2026, 6, 8, 8),
            Utc(2026, 6, 8, 9),
            spanMinutes,
            spanMinutes,
            0,
            0,
            0,
            1);

    private static (int Population, int Contributors, string State) Sample(ReportSampleContext sample) =>
        (sample.PopulationCount, sample.ContributingCount, sample.State);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
