namespace ChairSide.Board.Services;

/// <summary>
/// Read model that bridges a completed-cycle population to a schedule-fit summary for future Reports
/// and Workshop use. It is intentionally thin: it does not own any schedule-fit math (that lives in
/// <see cref="ScheduleFitCalculator"/>) and it does not decide which cycles belong to the report -
/// the caller passes the intended population (eventually the standard completed-cycle set the rest of
/// the report already uses). This keeps the read model honest about its inputs and free of duplicated
/// population or variance logic.
///
/// <see cref="IncludedCycleCount"/> is the size of the supplied population (non-null entries), while
/// <see cref="ScheduleFitCycleCount"/> is the allocation-calculable subset that actually contributed
/// to <see cref="Overall"/>. The gap between them lets a future card state "fit computed over N of M
/// completed cases" without re-deriving either number.
/// </summary>
public sealed record ScheduleFitReport(
    ScheduleFitResult Overall,
    int IncludedCycleCount,
    int ScheduleFitCycleCount);

/// <summary>
/// Builds a <see cref="ScheduleFitReport"/> over a caller-supplied completed-cycle population, reusing
/// <see cref="ScheduleFitCalculator"/> for every metric. No I/O, no DI, no population inference.
/// </summary>
public static class ScheduleFitReportBuilder
{
    /// <summary>
    /// Summarizes schedule fit over <paramref name="cycles"/>. Null entries are ignored and do not
    /// count toward <see cref="ScheduleFitReport.IncludedCycleCount"/>. The schedule-fit totals come
    /// straight from <see cref="ScheduleFitCalculator.Calculate"/>, so eligibility (positive expected
    /// allocation and a measured case flow) and all math stay defined in exactly one place.
    /// </summary>
    /// <param name="cycles">The intended report population (e.g. standard completed cycles).</param>
    /// <param name="blockMinutes">Reporting block size in minutes; forwarded to the calculator.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cycles"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockMinutes"/> is not positive.</exception>
    public static ScheduleFitReport Build(
        IEnumerable<CompletedRoomCycle> cycles,
        int blockMinutes = ScheduleFitCalculator.DefaultBlockMinutes)
    {
        ArgumentNullException.ThrowIfNull(cycles);

        // Materialize once so a lazy source is not enumerated twice (count + calculate). Drop null
        // defensive entries here so IncludedCycleCount reflects real supplied cycles only.
        var population = cycles.Where(cycle => cycle is not null).ToList();

        var overall = ScheduleFitCalculator.Calculate(population, blockMinutes);

        return new ScheduleFitReport(overall, population.Count, overall.CycleCount);
    }
}
