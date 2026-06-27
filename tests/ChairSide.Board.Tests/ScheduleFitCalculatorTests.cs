using ChairSide.Board.Services;

namespace ChairSide.Board.Tests;

public sealed class ScheduleFitCalculatorTests
{
    // Builds a minimal cycle carrying only the fields the calculator reads: the confirmed expected
    // allocation (minutes) and the measured case flow (minutes, nullable). Everything else is left at
    // its default - the kernel never looks at lifecycle timestamps or persisted variance.
    private static CompletedRoomCycle Cycle(int expectedMinutes, int? measuredMinutes) =>
        new()
        {
            ExpectedAllocationMinutes = expectedMinutes,
            MeasuredCaseFlowMinutes = measuredMinutes
        };

    [Fact]
    public void Empty_cycle_list_returns_zero_result_and_null_utilization()
    {
        var result = ScheduleFitCalculator.Calculate([]);

        Assert.Equal(0, result.CycleCount);
        Assert.Equal(ScheduleFitCalculator.DefaultBlockMinutes, result.BlockMinutes);
        Assert.Equal(0, result.TotalExpectedMinutes);
        Assert.Equal(0, result.TotalMeasuredMinutes);
        Assert.Equal(0, result.TotalVarianceMinutes);
        Assert.Equal(0, result.TotalSlackMinutes);
        Assert.Equal(0, result.TotalDebtMinutes);
        Assert.Equal(0.0, result.TotalExpectedBlocks);
        Assert.Equal(0.0, result.TotalActualBlocks);
        Assert.Equal(0.0, result.TotalVarianceBlocks);
        Assert.Null(result.UtilizationRatio);
    }

    [Fact]
    public void At_expected_cycle_has_zero_variance_slack_and_debt()
    {
        var result = ScheduleFitCalculator.Calculate([Cycle(expectedMinutes: 30, measuredMinutes: 30)]);

        Assert.Equal(1, result.CycleCount);
        Assert.Equal(30, result.TotalExpectedMinutes);
        Assert.Equal(30, result.TotalMeasuredMinutes);
        Assert.Equal(0, result.TotalVarianceMinutes);
        Assert.Equal(0, result.TotalSlackMinutes);
        Assert.Equal(0, result.TotalDebtMinutes);
        Assert.Equal(1.0, result.UtilizationRatio!.Value, precision: 6);
    }

    [Fact]
    public void Over_expected_cycle_contributes_to_debt_not_slack()
    {
        // measured 40 vs expected 30 -> ran over: variance +10, all of it debt, no slack.
        var result = ScheduleFitCalculator.Calculate([Cycle(expectedMinutes: 30, measuredMinutes: 40)]);

        Assert.Equal(1, result.CycleCount);
        Assert.Equal(10, result.TotalVarianceMinutes);
        Assert.Equal(0, result.TotalSlackMinutes);
        Assert.Equal(10, result.TotalDebtMinutes);
    }

    [Fact]
    public void Under_expected_cycle_contributes_to_slack_not_debt()
    {
        // measured 20 vs expected 30 -> ran under: variance -10, all of it slack, no debt.
        var result = ScheduleFitCalculator.Calculate([Cycle(expectedMinutes: 30, measuredMinutes: 20)]);

        Assert.Equal(1, result.CycleCount);
        Assert.Equal(-10, result.TotalVarianceMinutes);
        Assert.Equal(10, result.TotalSlackMinutes);
        Assert.Equal(0, result.TotalDebtMinutes);
    }

    [Fact]
    public void Mixed_cycles_keep_signed_net_variance_while_tracking_slack_and_debt_separately()
    {
        // One over (+10 debt) and one under (-5 slack). Net variance is the signed sum (+5), but slack
        // and debt are tracked independently so the over/under mix is not hidden by netting.
        var result = ScheduleFitCalculator.Calculate(
        [
            Cycle(expectedMinutes: 30, measuredMinutes: 40),
            Cycle(expectedMinutes: 30, measuredMinutes: 25)
        ]);

        Assert.Equal(2, result.CycleCount);
        Assert.Equal(60, result.TotalExpectedMinutes);
        Assert.Equal(65, result.TotalMeasuredMinutes);
        Assert.Equal(5, result.TotalVarianceMinutes);
        Assert.Equal(5, result.TotalSlackMinutes);
        Assert.Equal(10, result.TotalDebtMinutes);
    }

    [Fact]
    public void Block_counts_use_the_supplied_block_size()
    {
        // expected 30, measured 45, variance +15; with a 15-minute lens that is 2 / 3 / 1 blocks.
        var result = ScheduleFitCalculator.Calculate(
            [Cycle(expectedMinutes: 30, measuredMinutes: 45)],
            blockMinutes: 15);

        Assert.Equal(15, result.BlockMinutes);
        Assert.Equal(2.0, result.TotalExpectedBlocks, precision: 6);
        Assert.Equal(3.0, result.TotalActualBlocks, precision: 6);
        Assert.Equal(1.0, result.TotalVarianceBlocks, precision: 6);
    }

    [Fact]
    public void Default_block_size_divides_minute_totals_by_ten()
    {
        var result = ScheduleFitCalculator.Calculate([Cycle(expectedMinutes: 30, measuredMinutes: 50)]);

        Assert.Equal(10, result.BlockMinutes);
        Assert.Equal(3.0, result.TotalExpectedBlocks, precision: 6);
        Assert.Equal(5.0, result.TotalActualBlocks, precision: 6);
        Assert.Equal(2.0, result.TotalVarianceBlocks, precision: 6);
    }

    [Fact]
    public void Utilization_ratio_is_measured_over_expected()
    {
        // expected 60, measured 45 -> 0.75.
        var result = ScheduleFitCalculator.Calculate([Cycle(expectedMinutes: 60, measuredMinutes: 45)]);

        Assert.NotNull(result.UtilizationRatio);
        Assert.Equal(0.75, result.UtilizationRatio!.Value, precision: 6);
    }

    [Fact]
    public void Cycles_with_nonpositive_expected_minutes_are_excluded()
    {
        // Only the third cycle is allocation-calculable; the zero/negative-expected cycles drop out.
        var result = ScheduleFitCalculator.Calculate(
        [
            Cycle(expectedMinutes: 0, measuredMinutes: 40),
            Cycle(expectedMinutes: -10, measuredMinutes: 40),
            Cycle(expectedMinutes: 30, measuredMinutes: 40)
        ]);

        Assert.Equal(1, result.CycleCount);
        Assert.Equal(30, result.TotalExpectedMinutes);
        Assert.Equal(40, result.TotalMeasuredMinutes);
        Assert.Equal(10, result.TotalVarianceMinutes);
    }

    [Fact]
    public void Cycles_with_null_measured_case_flow_are_excluded()
    {
        var result = ScheduleFitCalculator.Calculate(
        [
            Cycle(expectedMinutes: 30, measuredMinutes: null),
            Cycle(expectedMinutes: 30, measuredMinutes: 36)
        ]);

        Assert.Equal(1, result.CycleCount);
        Assert.Equal(30, result.TotalExpectedMinutes);
        Assert.Equal(36, result.TotalMeasuredMinutes);
        Assert.Equal(6, result.TotalVarianceMinutes);
        Assert.Equal(6, result.TotalDebtMinutes);
    }

    [Fact]
    public void All_ineligible_cycles_yield_zero_result_with_null_utilization()
    {
        // No expected minutes survive eligibility, so utilization has nothing to divide by.
        var result = ScheduleFitCalculator.Calculate(
        [
            Cycle(expectedMinutes: 0, measuredMinutes: 40),
            Cycle(expectedMinutes: 30, measuredMinutes: null)
        ]);

        Assert.Equal(0, result.CycleCount);
        Assert.Equal(0, result.TotalExpectedMinutes);
        Assert.Null(result.UtilizationRatio);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Invalid_block_size_throws_argument_out_of_range(int blockMinutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScheduleFitCalculator.Calculate([Cycle(expectedMinutes: 30, measuredMinutes: 30)], blockMinutes));
    }
}
