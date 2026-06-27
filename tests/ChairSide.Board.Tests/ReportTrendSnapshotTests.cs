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
            "PerformanceScore",
            "Best",
            "Worst",
            "Ranking",
            "CapacityGained",
            "ExtraAppointments",
            "Recoverable",
            "Forecast",
            "Prediction",
            "Roi"
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

    private static CompletedRoomCycle Cycle(DateTimeOffset completeAt, int seatedToDoctorSeconds) =>
        new()
        {
            DoctorCompleteAt = completeAt,
            SeatedToDoctorSeconds = seatedToDoctorSeconds
        };

    private static DateTimeOffset Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);
}
