using ChairSide.Board.Services;

namespace ChairSide.Board.Tests;

public sealed class CalibrationInsightReportTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 13, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Historical_summary_uses_exact_seconds_without_per_case_rounding()
    {
        CompletedRoomCycle[] cycles =
        [
            Cycle(1, expectedMinutes: 10, observedSeconds: 600.6),
            Cycle(2, expectedMinutes: 10, observedSeconds: 600.6)
        ];

        var summary = ExactScheduleFitCalculator.BuildHistoricalAssignedSummary(cycles);

        Assert.Equal(1200d, summary.TotalExpectedSeconds);
        Assert.Equal(1201.2d, summary.TotalObservedSeconds, precision: 6);
        Assert.Equal(1.2d, summary.NetVarianceSeconds, precision: 6);
        Assert.Equal(1.2d, summary.TotalDebtSeconds, precision: 6);
        Assert.Equal(0d, summary.TotalSlackSeconds);
    }

    [Fact]
    public void Historical_summary_excludes_reversed_interval_and_keeps_truthful_zero()
    {
        var reversed = Cycle(1, 10, 600);
        reversed.DoctorCompleteAt = reversed.SeatedAt.AddSeconds(-1);
        var zero = Cycle(2, 10, 0);

        var summary = ExactScheduleFitCalculator.BuildHistoricalAssignedSummary([reversed, zero]);

        Assert.Equal(2, summary.PopulationCount);
        Assert.Equal(1, summary.PairedCaseCount);
        Assert.Equal(0.5d, summary.PopulationCoverage);
        Assert.Equal(0d, summary.TotalObservedSeconds);
        Assert.Equal(600d, summary.TotalSlackSeconds);
    }

    [Fact]
    public void Historical_summary_keeps_slack_and_debt_when_net_is_zero()
    {
        var summary = ExactScheduleFitCalculator.BuildHistoricalAssignedSummary(
        [
            Cycle(1, expectedMinutes: 10, observedSeconds: 500),
            Cycle(2, expectedMinutes: 10, observedSeconds: 700)
        ]);

        Assert.Equal(0d, summary.NetVarianceSeconds);
        Assert.Equal(100d, summary.TotalSlackSeconds);
        Assert.Equal(100d, summary.TotalDebtSeconds);
    }

    [Fact]
    public void Historical_summary_excludes_missing_nonpositive_expected_and_missing_observed_values()
    {
        var missingObserved = Cycle(1, expectedMinutes: 10, observedSeconds: 600);
        missingObserved.DoctorCompleteAt = null;
        CompletedRoomCycle[] population =
        [
            missingObserved,
            Cycle(2, expectedMinutes: 0, observedSeconds: 600),
            Cycle(3, expectedMinutes: -10, observedSeconds: 600),
            Cycle(4, expectedMinutes: 10, observedSeconds: 600)
        ];

        var summary = ExactScheduleFitCalculator.BuildHistoricalAssignedSummary(population);

        Assert.Equal(4, summary.PopulationCount);
        Assert.Equal(1, summary.PairedCaseCount);
        Assert.Equal(0.25d, summary.PopulationCoverage);
    }

    [Theory]
    [InlineData(-601, ScheduleFitToleranceClassifications.LessTimeThanAllocation)]
    [InlineData(-600, ScheduleFitToleranceClassifications.AtExpected)]
    [InlineData(-599, ScheduleFitToleranceClassifications.AtExpected)]
    [InlineData(0, ScheduleFitToleranceClassifications.AtExpected)]
    [InlineData(599, ScheduleFitToleranceClassifications.AtExpected)]
    [InlineData(600, ScheduleFitToleranceClassifications.AtExpected)]
    [InlineData(601, ScheduleFitToleranceClassifications.MoreTimeThanAllocation)]
    public void Tolerance_boundaries_are_inclusive(double variance, string expected)
    {
        Assert.Equal(expected, ExactScheduleFitCalculator.ToleranceClassification(variance, 600));
    }

    [Theory]
    [InlineData(-1, CalibrationRawDirections.BelowBaseline)]
    [InlineData(0, CalibrationRawDirections.EqualBaseline)]
    [InlineData(1, CalibrationRawDirections.AboveBaseline)]
    public void Raw_direction_is_independent_from_tolerance(double variance, string expected)
    {
        Assert.Equal(expected, ExactScheduleFitCalculator.RawDirection(variance));
    }

    [Fact]
    public void Paired_median_preserves_sign_when_difference_of_medians_reverses_it()
    {
        var expected = new[] { 10d, 100d, 100d };
        var observed = new[] { 20d, 90d, 110d };
        var paired = expected.Select((value, index) => observed[index] - value);

        Assert.Equal(10d, ExactScheduleFitCalculator.Median(paired));
        Assert.Equal(-10d,
            ExactScheduleFitCalculator.Median(observed) - ExactScheduleFitCalculator.Median(expected));
    }

    [Fact]
    public void Paired_median_avoids_difference_of_medians_magnitude_distortion()
    {
        var expected = new[] { 20d, 30d, 100d };
        var observed = new[] { 30d, 90d, 110d };
        var paired = expected.Select((value, index) => observed[index] - value);

        Assert.Equal(10d, ExactScheduleFitCalculator.Median(paired));
        Assert.Equal(60d,
            ExactScheduleFitCalculator.Median(observed) - ExactScheduleFitCalculator.Median(expected));
    }

    [Fact]
    public void Nine_pairs_are_below_minimum_even_when_other_gates_would_pass()
    {
        var evaluation = Evaluate(Enumerable.Repeat(660d, 9));

        Assert.Equal(CalibrationDecisions.BelowMinimumSample, evaluation.Decision);
        Assert.Equal(9, evaluation.TotalPairedCaseCount);
        Assert.Null(evaluation.Insight);
    }

    [Fact]
    public void Seven_of_ten_raw_direction_is_suppressed_and_eight_of_ten_qualifies()
    {
        var seven = Evaluate(Enumerable.Repeat(1200d, 7).Concat(Enumerable.Repeat(-1200d, 3)));
        var eight = Evaluate(Enumerable.Repeat(660d, 8).Concat(Enumerable.Repeat(0d, 2)));

        Assert.Equal(CalibrationDecisions.InsufficientDirectionalConsistency, seven.Decision);
        Assert.Equal(0.7d, seven.DirectionalShare, precision: 10);
        Assert.Equal(CalibrationDecisions.Qualified, eight.Decision);
        Assert.Equal(0.8d, eight.DirectionalShare, precision: 10);
        Assert.Equal(CalibrationInsightDirections.MoreTimeThanCurrentDefault, eight.Insight!.Direction);
    }

    [Fact]
    public void Directional_share_uses_all_pairs_for_non_ten_denominators()
    {
        var below = Evaluate(Enumerable.Repeat(1200d, 8).Concat(Enumerable.Repeat(0d, 3)));
        var above = Evaluate(Enumerable.Repeat(1200d, 9).Concat(Enumerable.Repeat(0d, 2)));

        Assert.Equal(8d / 11d, below.DirectionalShare, precision: 10);
        Assert.Equal(CalibrationDecisions.InsufficientDirectionalConsistency, below.Decision);
        Assert.Equal(9d / 11d, above.DirectionalShare, precision: 10);
        Assert.Equal(CalibrationDecisions.Qualified, above.Decision);
    }

    [Theory]
    [InlineData(600)]
    [InlineData(-600)]
    public void Exact_material_boundary_does_not_qualify(double variance)
    {
        var evaluation = Evaluate(Enumerable.Repeat(variance, 10));

        Assert.Equal(CalibrationDecisions.BelowMaterialDeviation, evaluation.Decision);
        Assert.Null(evaluation.Insight);
    }

    [Theory]
    [InlineData(601, CalibrationInsightDirections.MoreTimeThanCurrentDefault)]
    [InlineData(-601, CalibrationInsightDirections.LessTimeThanCurrentDefault)]
    public void Strictly_beyond_material_boundary_qualifies(double variance, string direction)
    {
        var evaluation = Evaluate(Enumerable.Repeat(variance, 10));

        Assert.Equal(CalibrationDecisions.Qualified, evaluation.Decision);
        Assert.Equal(direction, evaluation.Insight!.Direction);
    }

    [Fact]
    public void Many_small_same_sign_differences_and_one_outlier_fail_material_median()
    {
        var evaluation = Evaluate(Enumerable.Repeat(300d, 9).Append(3000d));

        Assert.Equal(1d, evaluation.DirectionalShare);
        Assert.Equal(300d, evaluation.MedianPairedVarianceSeconds);
        Assert.Equal(CalibrationDecisions.BelowMaterialDeviation, evaluation.Decision);
    }

    [Fact]
    public void Half_small_and_half_large_differences_land_exactly_on_material_boundary()
    {
        var evaluation = Evaluate(Enumerable.Repeat(540d, 5).Concat(Enumerable.Repeat(660d, 5)));

        Assert.Equal(600d, evaluation.MedianPairedVarianceSeconds);
        Assert.Equal(CalibrationDecisions.BelowMaterialDeviation, evaluation.Decision);
    }

    [Fact]
    public void Mixed_and_neutral_combined_populations_follow_raw_direction_then_material_median()
    {
        var halfNeutral = Evaluate(Enumerable.Repeat(0d, 5).Concat(Enumerable.Repeat(1200d, 5)));
        var split = Evaluate(Enumerable.Repeat(-900d, 5).Concat(Enumerable.Repeat(900d, 5)));
        var qualifiedMore = Evaluate(Enumerable.Repeat(1200d, 8).Concat(Enumerable.Repeat(-1200d, 2)));
        var qualifiedLess = Evaluate(Enumerable.Repeat(-660d, 8).Concat(Enumerable.Repeat(0d, 2)));

        Assert.Equal(CalibrationDecisions.InsufficientDirectionalConsistency, halfNeutral.Decision);
        Assert.Equal(CalibrationDecisions.InsufficientDirectionalConsistency, split.Decision);
        Assert.Equal(CalibrationDecisions.Qualified, qualifiedMore.Decision);
        Assert.Equal(CalibrationInsightDirections.MoreTimeThanCurrentDefault,
            qualifiedMore.Insight!.Direction);
        Assert.Equal(CalibrationDecisions.Qualified, qualifiedLess.Decision);
        Assert.Equal(CalibrationInsightDirections.LessTimeThanCurrentDefault,
            qualifiedLess.Insight!.Direction);
    }

    [Fact]
    public void Current_default_is_separate_from_historical_assigned_and_captured_defaults()
    {
        var cycles = Enumerable.Range(1, 10)
            .Select(id => Cycle(id, expectedMinutes: 40, observedSeconds: 40 * 60d, originalDefaultUnits: 3))
            .ToArray();

        var historical = ExactScheduleFitCalculator.BuildHistoricalAssignedSummary(cycles);
        var calibration = ExactScheduleFitCalculator.EvaluateCurrentDefault(cycles, 40);

        Assert.Equal(0d, historical.MedianPairedVarianceSeconds);
        Assert.Equal(0d, calibration.MedianPairedVarianceSeconds);
        Assert.Equal(CalibrationDecisions.InsufficientDirectionalConsistency, calibration.Decision);
        Assert.Null(calibration.Insight);
    }

    [Fact]
    public void Missing_current_default_never_uses_historical_values_as_a_baseline()
    {
        var evaluation = ExactScheduleFitCalculator.EvaluateCurrentDefault(
            [Cycle(1, expectedMinutes: 40, observedSeconds: 50 * 60d, originalDefaultUnits: 3)],
            currentDefaultAllocationMinutes: null);

        Assert.Equal(CalibrationDecisions.CurrentDefaultUnavailable, evaluation.Decision);
        Assert.Equal(0, evaluation.TotalPairedCaseCount);
        Assert.Null(evaluation.Insight);
    }

    [Fact]
    public void Qualified_evidence_reconciles_to_counts_and_median_population()
    {
        var evaluation = Evaluate(Enumerable.Repeat(660d, 8).Concat(Enumerable.Repeat(0d, 2)));
        var insight = Assert.IsType<CalibrationInsight>(evaluation.Insight);

        Assert.Equal(evaluation.TotalPairedCaseCount, insight.Evidence.Count);
        Assert.Equal(evaluation.AboveBaselineCaseCount, insight.DirectionalCaseCount);
        Assert.Equal(evaluation.EqualBaselineCaseCount, insight.EqualCaseCount);
        Assert.Equal(evaluation.AtExpectedCaseCount, insight.AtExpectedCaseCount);
        Assert.Equal(
            evaluation.MedianPairedVarianceSeconds,
            ExactScheduleFitCalculator.Median(insight.Evidence.Select(item => item.PairedVarianceSeconds)));
        Assert.All(insight.Evidence, item =>
        {
            Assert.Equal($"handoff-{item.CompletedCycleId}", item.AcceptedReadyHandoffId);
            Assert.Equal(CalibrationBaselineSources.CurrentRosterDefault, item.BaselineSource);
            Assert.Equal(30, item.BaselineMinutesUsed);
        });
    }

    private static CalibrationEvaluation Evaluate(IEnumerable<double> variances)
    {
        var cycles = variances
            .Select((variance, index) => Cycle(index + 1, 30, 1800d + variance))
            .ToArray();
        return ExactScheduleFitCalculator.EvaluateCurrentDefault(cycles, 30);
    }

    private static CompletedRoomCycle Cycle(
        int id,
        int expectedMinutes,
        double observedSeconds,
        int originalDefaultUnits = 1) =>
        new()
        {
            CompletedCycleId = id,
            AcceptedReadyHandoffId = $"handoff-{id}",
            RoomId = id,
            AssignedDoctor = "otte",
            ProcedureCode = "EXT",
            SeatedAt = Start.AddHours(id),
            DoctorCompleteAt = Start.AddHours(id).AddSeconds(observedSeconds),
            RoomAvailableAt = Start.AddHours(id).AddSeconds(observedSeconds + 300),
            OriginalDefaultExpectedUnits = originalDefaultUnits,
            ExpectedAllocationUnits = expectedMinutes / 10,
            ExpectedAllocationMinutes = expectedMinutes
        };
}
