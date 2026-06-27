using ChairSide.Board.Services;

using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class ScheduleFitReportTests
{
    // Minimal cycle carrying only the two fields the schedule-fit kernel reads: confirmed expected
    // allocation (minutes) and measured case flow (minutes, nullable).
    private static CompletedRoomCycle Cycle(int expectedMinutes, int? measuredMinutes) =>
        new()
        {
            ExpectedAllocationMinutes = expectedMinutes,
            MeasuredCaseFlowMinutes = measuredMinutes
        };

    [Fact]
    public void Empty_population_returns_zero_counts_and_zero_overall_result()
    {
        var report = ScheduleFitReportBuilder.Build([]);

        Assert.Equal(0, report.IncludedCycleCount);
        Assert.Equal(0, report.ScheduleFitCycleCount);
        Assert.Equal(0, report.Overall.CycleCount);
        Assert.Equal(0, report.Overall.TotalExpectedMinutes);
        Assert.Equal(0, report.Overall.TotalMeasuredMinutes);
        Assert.Equal(0, report.Overall.TotalVarianceMinutes);
        Assert.Equal(0, report.Overall.TotalSlackMinutes);
        Assert.Equal(0, report.Overall.TotalDebtMinutes);
        Assert.Null(report.Overall.UtilizationRatio);
    }

    [Fact]
    public void Mixed_over_and_under_cycles_delegate_to_the_calculator()
    {
        // Same inputs the kernel's own mixed-case test uses: one over (+10 debt), one under (-5 slack).
        var cycles = new[]
        {
            Cycle(expectedMinutes: 30, measuredMinutes: 40),
            Cycle(expectedMinutes: 30, measuredMinutes: 25)
        };

        var report = ScheduleFitReportBuilder.Build(cycles);
        var expected = ScheduleFitCalculator.Calculate(cycles);

        // The builder returns the calculator's result verbatim.
        Assert.Equal(expected, report.Overall);
        Assert.Equal(2, report.IncludedCycleCount);
        Assert.Equal(2, report.ScheduleFitCycleCount);
        Assert.Equal(5, report.Overall.TotalVarianceMinutes);
        Assert.Equal(5, report.Overall.TotalSlackMinutes);
        Assert.Equal(10, report.Overall.TotalDebtMinutes);
    }

    [Fact]
    public void Included_count_exceeds_fit_count_when_some_cycles_are_not_allocation_calculable()
    {
        // Three supplied cycles, but only the last is allocation-calculable (positive expected and a
        // measured case flow). Included counts all three; the fit count counts only the contributor.
        var cycles = new[]
        {
            Cycle(expectedMinutes: 0, measuredMinutes: 40),    // no expected allocation
            Cycle(expectedMinutes: 30, measuredMinutes: null), // no measured case flow
            Cycle(expectedMinutes: 30, measuredMinutes: 40)    // calculable
        };

        var report = ScheduleFitReportBuilder.Build(cycles);

        Assert.Equal(3, report.IncludedCycleCount);
        Assert.Equal(1, report.ScheduleFitCycleCount);
        Assert.Equal(1, report.Overall.CycleCount);
        Assert.Equal(30, report.Overall.TotalExpectedMinutes);
        Assert.Equal(40, report.Overall.TotalMeasuredMinutes);
    }

    [Fact]
    public void Null_defensive_entries_do_not_count_as_included_cycles()
    {
        var cycles = new List<CompletedRoomCycle>
        {
            Cycle(expectedMinutes: 30, measuredMinutes: 30),
            null!,
            Cycle(expectedMinutes: 30, measuredMinutes: 36)
        };

        var report = ScheduleFitReportBuilder.Build(cycles);

        // Two real cycles, both calculable; the null is ignored entirely.
        Assert.Equal(2, report.IncludedCycleCount);
        Assert.Equal(2, report.ScheduleFitCycleCount);
        Assert.Equal(60, report.Overall.TotalExpectedMinutes);
        Assert.Equal(66, report.Overall.TotalMeasuredMinutes);
    }

    [Fact]
    public void Custom_block_size_flows_through_to_overall_block_totals()
    {
        // expected 30, measured 45, variance +15; a 15-minute lens yields 2 / 3 / 1 blocks.
        var report = ScheduleFitReportBuilder.Build(
            [Cycle(expectedMinutes: 30, measuredMinutes: 45)],
            blockMinutes: 15);

        Assert.Equal(15, report.Overall.BlockMinutes);
        Assert.Equal(2.0, report.Overall.TotalExpectedBlocks, precision: 6);
        Assert.Equal(3.0, report.Overall.TotalActualBlocks, precision: 6);
        Assert.Equal(1.0, report.Overall.TotalVarianceBlocks, precision: 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Invalid_block_size_propagates_argument_out_of_range(int blockMinutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScheduleFitReportBuilder.Build([Cycle(expectedMinutes: 30, measuredMinutes: 30)], blockMinutes));
    }

    // -------------------------------------------------------------------------
    // Consistency: schedule-fit totals must agree with the existing allocation
    // variance summary over the same standard completed-cycle population. This
    // guards against the two independent computations drifting apart.
    // -------------------------------------------------------------------------

    [Fact]
    public void Schedule_fit_totals_agree_with_allocation_variance_summary_for_standard_population()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // Clean, in-roster, same-day cycles so all are standard completed cycles (no reporting
        // exceptions). Measured case flow is fixed at 30 min by the seed; expected varies by units, so
        // the net variance is non-trivial: +10, +10, -10 => net +10 over expected.
        SeedCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 2);
        SeedCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 11, 14), expectedUnits: 2);
        SeedCleanCycle(seed, room: 3, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 12, 14), expectedUnits: 4);

        // Reload so the store picks up the seeded cycles, then build the standard report.
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports();

        // For clean data the recent completed set equals the standard completed population that
        // BuildAllocationVarianceSummary uses, and each cycle is already variance-annotated.
        var fit = ScheduleFitReportBuilder.Build(reports.RecentCompletedCycles);

        var allocation = reports.AllocationVariance;
        Assert.NotNull(allocation);
        Assert.Equal(allocation.AllocationVarianceCycleCount, fit.Overall.CycleCount);
        Assert.Equal(allocation.TotalExpectedAllocationMinutes, fit.Overall.TotalExpectedMinutes);
        Assert.Equal(allocation.TotalMeasuredCaseFlowMinutes, fit.Overall.TotalMeasuredMinutes);
        Assert.Equal(allocation.NetAllocationVarianceMinutes, fit.Overall.TotalVarianceMinutes);

        // Sanity-anchor the expected values so the consistency assertions cannot all pass on zeros.
        Assert.Equal(3, fit.Overall.CycleCount);
        Assert.Equal(80, fit.Overall.TotalExpectedMinutes);
        Assert.Equal(90, fit.Overall.TotalMeasuredMinutes);
        Assert.Equal(10, fit.Overall.TotalVarianceMinutes);
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    // Local clean-cycle seed mirroring the BoardStoreTests helper (which is private to that class).
    // Writes one in-roster, fully-timed, same-day completed cycle straight to the repository. Measured
    // case flow is fixed at 30 minutes (seated 30 minutes before complete); expected = units * 10.
    private static void SeedCleanCycle(
        StoreContext context, int room, string doctor, string code, DateTimeOffset completeAt, int expectedUnits)
    {
        var seatedAt = completeAt.AddMinutes(-30);
        var cycle = new CompletedRoomCycle
        {
            RoomId = room,
            AssignedDoctor = doctor,
            ProcedureCode = code,
            SeatedAt = seatedAt,
            ReadyForDoctorAt = seatedAt.AddMinutes(5),
            DoctorArrivedAt = seatedAt.AddMinutes(15),
            DoctorCompleteAt = completeAt,
            RoomAvailableAt = completeAt.AddMinutes(5),
            SeatedToDoctorSeconds = 900,
            PrepSeconds = 300,
            ReadyToDoctorSeconds = 600,
            DoctorInRoomSeconds = 900,
            TurnoverSeconds = 300,
            TotalRoomCycleSeconds = 2100,
            FinalWaitState = "ready-for-doctor",
            OriginalDefaultExpectedUnits = expectedUnits,
            ExpectedAllocationUnits = expectedUnits,
            ExpectedAllocationMinutes = expectedUnits * 10
        };
        context.Repository.SaveCompletedCycle(cycle, context.Doctors, context.Procedures);
    }
}
