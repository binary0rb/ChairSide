using ChairSide.Board.Services;

namespace ChairSide.Board.Tests;

public sealed class ProcedureIntelligenceReportTests
{
    public static TheoryData<double[], double, double> Type7Fixtures => new()
    {
        { [10, 11, 12, 13, 14], 11, 13 },
        { [8, 9, 10, 11, 12, 13], 9.25, 11.75 },
        { [10, 11, 12, 13, 14, 15, 60], 11.5, 14.5 },
        { [8, 9, 10, 11, 12, 13, 14, 15, 16, 17], 10.25, 14.75 },
        { Enumerable.Range(10, 20).Select(value => (double)value).ToArray(), 14.75, 24.25 },
        { [10, 10, 10, 10, 10], 10, 10 },
        { [10, 10, 11, 12, 45], 10, 12 },
        { [1, 10, 11, 12, 13], 10, 12 },
        { [8, 9, 10, 11, 12, 14, 18, 25, 40, 70], 10.25, 23.25 }
    };

    [Theory]
    [MemberData(nameof(Type7Fixtures))]
    public void Type7_iqr_matches_approved_sparse_even_odd_outlier_and_skew_fixtures(
        double[] values,
        double expectedLower,
        double expectedUpper)
    {
        var sample = ReportSampleContext.ForPopulation(values.Length);

        var range = ProcedureIntelligenceStatistics.TypicalDoctorTimeRange(values, sample);

        Assert.Equal(expectedLower, range.LowerSeconds!.Value, precision: 10);
        Assert.Equal(expectedUpper, range.UpperSeconds!.Value, precision: 10);
    }

    [Fact]
    public void Type7_iqr_is_input_order_independent()
    {
        double[] ordered = [8, 9, 10, 11, 12, 13];
        double[] permuted = [13, 9, 11, 8, 12, 10];
        var sample = ReportSampleContext.ForPopulation(ordered.Length);

        var expected = ProcedureIntelligenceStatistics.TypicalDoctorTimeRange(ordered, sample);
        var actual = ProcedureIntelligenceStatistics.TypicalDoctorTimeRange(permuted, sample);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Limited_doctor_time_keeps_limited_sample_and_suppresses_range_endpoints(int count)
    {
        var values = Enumerable.Range(1, count).Select(value => (double)value).ToArray();
        var sample = ReportSampleContext.ForPopulation(count);

        var range = ProcedureIntelligenceStatistics.TypicalDoctorTimeRange(values, sample);

        Assert.Equal(ReportSampleStates.Limited, sample.State);
        Assert.Null(range.LowerSeconds);
        Assert.Null(range.UpperSeconds);
    }

    [Fact]
    public void Empty_and_unavailable_samples_suppress_range_without_changing_sample_state()
    {
        var empty = ReportSampleContext.Create(0, 0);
        var unavailable = ReportSampleContext.Create(3, 0);

        var emptyRange = ProcedureIntelligenceStatistics.TypicalDoctorTimeRange([], empty);
        var unavailableRange = ProcedureIntelligenceStatistics.TypicalDoctorTimeRange([], unavailable);

        Assert.Equal(ReportSampleStates.Empty, empty.State);
        Assert.Equal(ReportSampleStates.Unavailable, unavailable.State);
        Assert.Null(emptyRange.LowerSeconds);
        Assert.Null(emptyRange.UpperSeconds);
        Assert.Null(unavailableRange.LowerSeconds);
        Assert.Null(unavailableRange.UpperSeconds);
    }

    [Fact]
    public void Sufficient_truthful_zero_sample_publishes_zero_width_range()
    {
        double[] values = [0, 0, 0, 0, 0];
        var sample = ReportSampleContext.ForPopulation(values.Length);

        var range = ProcedureIntelligenceStatistics.TypicalDoctorTimeRange(values, sample);

        Assert.Equal(ReportSampleStates.Sufficient, sample.State);
        Assert.Equal(0, range.LowerSeconds);
        Assert.Equal(0, range.UpperSeconds);
    }
}
