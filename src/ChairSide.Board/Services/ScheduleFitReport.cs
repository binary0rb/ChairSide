namespace ChairSide.Board.Services;

/// <summary>
/// Read model that preserves the legacy integer-minute <see cref="Overall"/> contract for Workshop
/// while allowing Reports to append exact-second Schedule Fit and Calibration projections. Population
/// selection remains the caller's responsibility.
///
/// <see cref="IncludedCycleCount"/> is the size of the supplied population (non-null entries), while
/// <see cref="ScheduleFitCycleCount"/> is the allocation-calculable subset that actually contributed
/// to <see cref="Overall"/>. The gap between them lets a future card state "fit computed over N of M
/// completed cases" without re-deriving either number.
/// </summary>
public sealed record ScheduleFitReport(
    ScheduleFitResult Overall,
    int IncludedCycleCount,
    int ScheduleFitCycleCount,
    ScheduleFitSummary? Practice = null,
    IReadOnlyList<ScheduleFitSegment>? ProcedureSegments = null,
    IReadOnlyList<DoctorScheduleFitSummary>? DoctorSummaries = null,
    CalibrationRuleSet? Rules = null);

/// <summary>
/// Builds the compatibility portion of a <see cref="ScheduleFitReport"/> over a caller-supplied
/// completed-cycle population. ReportsSnapshotBuilder appends the exact-second Reports projection.
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
