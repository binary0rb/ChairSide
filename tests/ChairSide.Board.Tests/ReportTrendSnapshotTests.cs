using System.Reflection;

using ChairSide.Board.Services;

namespace ChairSide.Board.Tests;

public sealed class ReportTrendSnapshotTests
{
    [Fact]
    public void Empty_population_returns_empty_weekly_snapshot()
    {
        var snapshot = ReportTrendSnapshotBuilder.BuildWeekly([]);

        Assert.Equal("Week", snapshot.BucketSize);
        Assert.Empty(snapshot.Buckets);
    }

    [Fact]
    public void Cycles_group_into_monday_start_utc_weeks_by_doctor_complete_at()
    {
        var snapshot = ReportTrendSnapshotBuilder.BuildWeekly(
        [
            Cycle(completeAt: Utc(2026, 6, 8, 9), seatedToDoctorSeconds: 300),  // Monday
            Cycle(completeAt: Utc(2026, 6, 14, 23), seatedToDoctorSeconds: 600), // Sunday
            Cycle(completeAt: Utc(2026, 6, 15, 0), seatedToDoctorSeconds: 900)   // Next Monday
        ]);

        Assert.Collection(
            snapshot.Buckets,
            first =>
            {
                Assert.Equal("2026-06-08", first.StartDate);
                Assert.Equal("2026-06-15", first.EndDate);
                Assert.Equal(2, first.CompletedCycleCount);
            },
            second =>
            {
                Assert.Equal("2026-06-15", second.StartDate);
                Assert.Equal("2026-06-22", second.EndDate);
                Assert.Equal(1, second.CompletedCycleCount);
            });
    }

    [Fact]
    public void Median_seated_to_doctor_is_correct_for_odd_counts()
    {
        var bucket = SingleBucket(
            300,
            900,
            600);

        Assert.Equal(600, bucket.MedianSeatedToDoctorSeconds);
    }

    [Fact]
    public void Median_seated_to_doctor_is_correct_for_even_counts()
    {
        var bucket = SingleBucket(
            300,
            900,
            600,
            1200);

        Assert.Equal(750, bucket.MedianSeatedToDoctorSeconds);
    }

    [Fact]
    public void Average_seated_to_doctor_is_correct()
    {
        var bucket = SingleBucket(
            300,
            900,
            600);

        Assert.Equal(600, bucket.AverageSeatedToDoctorSeconds);
    }

    [Fact]
    public void Median_turnover_is_correct()
    {
        var bucket = SingleTurnoverBucket(
            300,
            900,
            600,
            1200);

        Assert.Equal(750, bucket.MedianTurnoverSeconds);
    }

    [Fact]
    public void Average_turnover_is_correct()
    {
        var bucket = SingleTurnoverBucket(
            300,
            900,
            600);

        Assert.Equal(600, bucket.AverageTurnoverSeconds);
    }

    [Fact]
    public void Turnover_count_uses_only_cycles_with_usable_turnover_values()
    {
        var snapshot = ReportTrendSnapshotBuilder.BuildWeekly(
        [
            Cycle(completeAt: Utc(2026, 6, 8, 9), seatedToDoctorSeconds: 300, turnoverSeconds: 120),
            Cycle(completeAt: Utc(2026, 6, 9, 9), seatedToDoctorSeconds: 600, turnoverSeconds: null),
            Cycle(completeAt: Utc(2026, 6, 10, 9), seatedToDoctorSeconds: 900, turnoverSeconds: -1),
            Cycle(completeAt: Utc(2026, 6, 11, 9), seatedToDoctorSeconds: 1200, turnoverSeconds: 240)
        ]);

        var bucket = Assert.Single(snapshot.Buckets);
        Assert.Equal(4, bucket.CompletedCycleCount);
        Assert.Equal(750, bucket.MedianSeatedToDoctorSeconds);
        Assert.Equal(2, bucket.TurnoverCycleCount);
        Assert.Equal(180, bucket.MedianTurnoverSeconds);
        Assert.Equal(180, bucket.AverageTurnoverSeconds);
    }

    [Fact]
    public void Ready_wait_median_uses_only_truthful_weekly_contributors()
    {
        var snapshot = ReportTrendSnapshotBuilder.BuildWeekly(
        [
            Cycle(completeAt: Utc(2026, 6, 8, 9), seatedToDoctorSeconds: 300, readyToDoctorSeconds: 300),
            Cycle(completeAt: Utc(2026, 6, 9, 9), seatedToDoctorSeconds: 600, readyToDoctorSeconds: null),
            Cycle(completeAt: Utc(2026, 6, 10, 9), seatedToDoctorSeconds: 900, readyToDoctorSeconds: 0)
        ]);

        var bucket = Assert.Single(snapshot.Buckets);
        Assert.Equal(2, bucket.ReadyWaitCycleCount);
        Assert.Equal(150, bucket.MedianReadyWaitSeconds);
        Assert.Equal(3, bucket.ReadyWaitSample!.PopulationCount);
        Assert.Equal(2, bucket.ReadyWaitSample.ContributingCount);
        Assert.Equal(ReportSampleStates.Limited, bucket.ReadyWaitSample.State);
    }

    [Fact]
    public void Ready_wait_is_unavailable_for_nonempty_week_without_truthful_ready_observations()
    {
        var snapshot = ReportTrendSnapshotBuilder.BuildWeekly(
        [
            Cycle(completeAt: Utc(2026, 6, 8, 9), seatedToDoctorSeconds: 300, readyToDoctorSeconds: null),
            Cycle(completeAt: Utc(2026, 6, 9, 9), seatedToDoctorSeconds: 600, readyToDoctorSeconds: null)
        ]);

        var bucket = Assert.Single(snapshot.Buckets);
        Assert.Equal(0, bucket.ReadyWaitCycleCount);
        Assert.Null(bucket.MedianReadyWaitSeconds);
        Assert.Equal(2, bucket.ReadyWaitSample!.PopulationCount);
        Assert.Equal(0, bucket.ReadyWaitSample.ContributingCount);
        Assert.Equal(ReportSampleStates.Unavailable, bucket.ReadyWaitSample.State);
    }

    [Fact]
    public void Truthful_zero_ready_wait_remains_an_observed_value()
    {
        var bucket = Assert.Single(ReportTrendSnapshotBuilder.BuildWeekly(
        [
            Cycle(completeAt: Utc(2026, 6, 8, 9), seatedToDoctorSeconds: 0, readyToDoctorSeconds: 0)
        ]).Buckets);

        Assert.Equal(0, bucket.MedianReadyWaitSeconds);
        Assert.Equal(1, bucket.ReadyWaitSample!.ContributingCount);
        Assert.Equal(ReportSampleStates.Limited, bucket.ReadyWaitSample.State);
    }

    [Fact]
    public void Weekly_samples_use_one_population_and_metric_specific_contributors()
    {
        var snapshot = ReportTrendSnapshotBuilder.BuildWeekly(
        [
            Cycle(completeAt: Utc(2026, 6, 8, 9), seatedToDoctorSeconds: 300, readyToDoctorSeconds: 60, turnoverSeconds: 120),
            Cycle(completeAt: Utc(2026, 6, 9, 9), seatedToDoctorSeconds: 600, readyToDoctorSeconds: 120, turnoverSeconds: null),
            Cycle(completeAt: Utc(2026, 6, 10, 9), seatedToDoctorSeconds: 900, readyToDoctorSeconds: 180, turnoverSeconds: null),
            Cycle(completeAt: Utc(2026, 6, 11, 9), seatedToDoctorSeconds: 1200, readyToDoctorSeconds: 240, turnoverSeconds: null),
            Cycle(completeAt: Utc(2026, 6, 12, 9), seatedToDoctorSeconds: -1, readyToDoctorSeconds: 300, turnoverSeconds: null)
        ]);

        var bucket = Assert.Single(snapshot.Buckets);
        Assert.Equal(4, bucket.CompletedCycleCount);
        Assert.Equal(5, bucket.SeatedToDoctorSample!.PopulationCount);
        Assert.Equal(4, bucket.SeatedToDoctorSample.ContributingCount);
        Assert.Equal(ReportSampleStates.Limited, bucket.SeatedToDoctorSample.State);
        Assert.Equal(5, bucket.ReadyWaitSample!.ContributingCount);
        Assert.Equal(ReportSampleStates.Sufficient, bucket.ReadyWaitSample.State);
        Assert.Equal(1, bucket.TurnoverSample!.ContributingCount);
        Assert.Equal(ReportSampleStates.Limited, bucket.TurnoverSample.State);
    }

    [Fact]
    public void Completed_cycle_count_per_bucket_is_the_eligible_cycle_count()
    {
        var snapshot = ReportTrendSnapshotBuilder.BuildWeekly(
        [
            Cycle(completeAt: Utc(2026, 6, 8, 9), seatedToDoctorSeconds: 300),
            Cycle(completeAt: Utc(2026, 6, 9, 9), seatedToDoctorSeconds: 600),
            Cycle(completeAt: Utc(2026, 6, 10, 9), seatedToDoctorSeconds: 900),
            Cycle(completeAt: Utc(2026, 6, 11, 9), seatedToDoctorSeconds: -1),
            new CompletedRoomCycle { SeatedToDoctorSeconds = 1200 }
        ]);

        var bucket = Assert.Single(snapshot.Buckets);
        Assert.Equal(3, bucket.CompletedCycleCount);
    }

    [Fact]
    public void Public_model_does_not_expose_projection_capacity_or_ranking_names()
    {
        var forbiddenNameFragments = new[]
        {
            "Performance",
            "PerformanceScore",
            "Score",
            "Best",
            "Worst",
            "Ranking",
            "Capacity",
            "CapacityGained",
            "ExtraAppointments",
            "Recoverable",
            "Projection",
            "Forecast",
            "Prediction",
            "Roi",
            "Blame"
        };

        var publicMemberNames = PublicModelMemberNames();

        foreach (var memberName in publicMemberNames)
        {
            foreach (var forbidden in forbiddenNameFragments)
            {
                Assert.DoesNotContain(forbidden, memberName, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Public_model_does_not_expose_patient_like_field_names()
    {
        var forbiddenNameFragments = new[]
        {
            "Patient",
            "Dob",
            "DateOfBirth",
            "Chart",
            "Mrn",
            "Medical",
            "Diagnosis",
            "Insurance",
            "Billing",
            "Note"
        };

        foreach (var memberName in PublicModelMemberNames())
        {
            foreach (var forbidden in forbiddenNameFragments)
            {
                Assert.DoesNotContain(forbidden, memberName, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static IEnumerable<string> PublicModelMemberNames() =>
        new[]
        {
            typeof(ReportTrendSnapshot),
            typeof(ReportTrendBucket)
        }
        .SelectMany(type => type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static))
        .Select(member => member.Name);

    private static ReportTrendBucket SingleBucket(params int[] seatedToDoctorSeconds)
    {
        var snapshot = ReportTrendSnapshotBuilder.BuildWeekly(
            seatedToDoctorSeconds.Select(value =>
                Cycle(completeAt: Utc(2026, 6, 10, 9), seatedToDoctorSeconds: value)));

        return Assert.Single(snapshot.Buckets);
    }

    private static ReportTrendBucket SingleTurnoverBucket(params int[] turnoverSeconds)
    {
        var snapshot = ReportTrendSnapshotBuilder.BuildWeekly(
            turnoverSeconds.Select(value =>
                Cycle(completeAt: Utc(2026, 6, 10, 9), seatedToDoctorSeconds: 300, turnoverSeconds: value)));

        return Assert.Single(snapshot.Buckets);
    }

    private static CompletedRoomCycle Cycle(
        DateTimeOffset completeAt,
        int seatedToDoctorSeconds,
        int? turnoverSeconds = null,
        int? readyToDoctorSeconds = 180) =>
        new()
        {
            DoctorCompleteAt = completeAt,
            DoctorArrivedAt = completeAt.AddMinutes(-10),
            SeatedToDoctorSeconds = seatedToDoctorSeconds,
            ReadyToDoctorSeconds = readyToDoctorSeconds,
            TurnoverSeconds = turnoverSeconds
        };

    private static DateTimeOffset Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);
}
