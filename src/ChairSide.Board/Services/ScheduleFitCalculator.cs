namespace ChairSide.Board.Services;

/// <summary>
/// Pure, dependency-free kernel that summarizes expected-vs-actual case timing across a set of
/// completed room cycles. This is the first backend foundation for Workshop reporting/projection
/// tools: it adds no UI, no endpoint, no persisted state, and no lifecycle behavior.
///
/// "Expected" is each cycle's confirmed expected allocation (<see cref="CompletedRoomCycle.ExpectedAllocationMinutes"/>);
/// "measured/actual" is the case flow Seat -> Doctor Complete
/// (<see cref="CompletedRoomCycle.MeasuredCaseFlowMinutes"/>). Both are already computed elsewhere
/// (expected at seating, measured by AnnotateAllocationVariance at report time); this calculator only
/// aggregates them, so it never duplicates the source-of-truth for any single metric.
///
/// Durations are summed in minutes. Blocks are a reporting lens only: the caller picks a block size
/// (default <see cref="DefaultBlockMinutes"/>, matching the 10-minute AOS scheduling block) and minute
/// totals are divided by it. Block size never changes any stored value or lifecycle state.
///
/// Framing is neutral and additive: slack (ran under expected) and debt (ran over expected) are tracked
/// separately, while variance keeps its sign (measured - expected), so a balanced set does not hide an
/// equal mix of over- and under-runs.
/// </summary>
public static class ScheduleFitCalculator
{
    /// <summary>
    /// Default reporting block size in minutes. AOS schedules in 10-minute blocks, which is also how
    /// expected allocation units currently map to minutes - but this is only a reporting lens here, not
    /// a lifecycle constant, and callers may override it.
    /// </summary>
    public const int DefaultBlockMinutes = 10;

    /// <summary>
    /// Summarizes schedule fit over the supplied cycles. Only allocation-calculable cycles contribute:
    /// a cycle is included when it carries a positive expected allocation
    /// (<see cref="CompletedRoomCycle.ExpectedAllocationMinutes"/> &gt; 0) and a measured case flow
    /// (<see cref="CompletedRoomCycle.MeasuredCaseFlowMinutes"/> has a value). Variance is computed here
    /// as measured - expected, independent of any pre-populated AllocationVarianceMinutes, so the kernel
    /// is correct on raw cycles as well as report-annotated ones.
    /// </summary>
    /// <param name="cycles">Completed cycles to summarize. Null-safe entries are ignored.</param>
    /// <param name="blockMinutes">Reporting block size in minutes. Must be greater than zero.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cycles"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockMinutes"/> is not positive.</exception>
    public static ScheduleFitResult Calculate(IEnumerable<CompletedRoomCycle> cycles, int blockMinutes = DefaultBlockMinutes)
    {
        ArgumentNullException.ThrowIfNull(cycles);
        if (blockMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockMinutes), blockMinutes, "Block size must be greater than zero.");
        }

        var cycleCount = 0;
        var totalExpected = 0;
        var totalMeasured = 0;
        var totalSlack = 0;
        var totalDebt = 0;

        foreach (var cycle in cycles)
        {
            if (cycle is null || cycle.ExpectedAllocationMinutes <= 0 || cycle.MeasuredCaseFlowMinutes is not { } measured)
            {
                continue;
            }

            var expected = cycle.ExpectedAllocationMinutes;
            var variance = measured - expected;

            cycleCount++;
            totalExpected += expected;
            totalMeasured += measured;
            totalSlack += Math.Max(expected - measured, 0);
            totalDebt += Math.Max(variance, 0);
        }

        var totalVariance = totalMeasured - totalExpected;
        double? utilization = totalExpected == 0 ? null : (double)totalMeasured / totalExpected;

        return new ScheduleFitResult(
            cycleCount,
            blockMinutes,
            totalExpected,
            totalMeasured,
            totalVariance,
            totalSlack,
            totalDebt,
            (double)totalExpected / blockMinutes,
            (double)totalMeasured / blockMinutes,
            (double)totalVariance / blockMinutes,
            utilization);
    }
}

/// <summary>
/// Aggregate schedule-fit summary over a set of completed cycles (operational, non-PHI). All minute
/// totals are signed sums except slack/debt, which are non-negative by construction. Block totals are
/// the matching minute totals divided by <see cref="BlockMinutes"/>. <see cref="UtilizationRatio"/> is
/// measured / expected, or null when there are no expected minutes to divide by.
/// </summary>
public sealed record ScheduleFitResult(
    int CycleCount,
    int BlockMinutes,
    int TotalExpectedMinutes,
    int TotalMeasuredMinutes,
    int TotalVarianceMinutes,
    int TotalSlackMinutes,
    int TotalDebtMinutes,
    double TotalExpectedBlocks,
    double TotalActualBlocks,
    double TotalVarianceBlocks,
    double? UtilizationRatio);
