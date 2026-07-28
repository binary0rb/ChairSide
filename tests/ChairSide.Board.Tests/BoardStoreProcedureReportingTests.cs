using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Tests;

public sealed partial class BoardStoreTests
{
    // -------------------------------------------------------------------------
    // Procedure baseline reporting
    // -------------------------------------------------------------------------

    [Fact]
    public void Procedure_summaries_group_normal_cycles_by_code_with_counts_and_labels()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Three CON cycles and one EXT cycle, each in its own non-overlapping hour block.
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "CON", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 1, "otte", "CON", prepMin: 5, readyMin: 20, doctorMin: 30, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(2), 2, "otte", "CON", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(3), 3, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);

        var summaries = context.Store.GetReports().ProcedureSummaries;

        Assert.Equal(2, summaries.Count);
        // Sorted by count descending: CON (3) before EXT (1).
        Assert.Equal("CON", summaries[0].ProcedureCode);
        Assert.Equal("Consult", summaries[0].ProcedureLabel);
        Assert.Equal(3, summaries[0].CompletedCycleCount);
        Assert.Equal("EXT", summaries[1].ProcedureCode);
        Assert.Equal("Extraction", summaries[1].ProcedureLabel);
        Assert.Equal(1, summaries[1].CompletedCycleCount);
    }

    [Fact]
    public void Procedure_summaries_compute_total_ready_and_doctor_time_metrics()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // CON totals: 1800, 3600, 1800 seconds. ready: 600, 1200, 600. doctorTime: 600, 1800, 600.
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "CON", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 1, "otte", "CON", prepMin: 5, readyMin: 20, doctorMin: 30, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(2), 2, "otte", "CON", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);

        var con = context.Store.GetReports().ProcedureSummaries.Single(summary => summary.ProcedureCode == "CON");

        // Total: avg (1800+3600+1800)/3 = 2400, median of [1800,1800,3600] = 1800.
        Assert.Equal(2400, con.AverageTotalSeconds);
        Assert.Equal(1800, con.MedianTotalSeconds);
        // Ready-to-doctor: avg (600+1200+600)/3 = 800, median of [600,600,1200] = 600.
        Assert.Equal(800, con.AverageReadyToDoctorSeconds);
        Assert.Equal(600, con.MedianReadyToDoctorSeconds);
        // Doctor time (in room): avg (600+1800+600)/3 = 1000, median of [600,600,1800] = 600.
        Assert.Equal(1000, con.AverageDoctorTimeSeconds);
        Assert.Equal(600, con.MedianDoctorTimeSeconds);
    }

    [Fact]
    public void Procedure_summaries_use_existing_occupied_and_available_wait_values()
    {
        // Reuses the existing partial-overlap scenario. The CON cycle's occupied/available wait is
        // produced by AnnotateOccupiedWait; the procedure summary must surface those exact values.
        using var workspace = TestWorkspace.Create();
        var base_ = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(base_);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Room 2 (same doctor, different procedure) is the blocker: in-room from t+0 to t+10.
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2));

        // Room 1 (CON target): ready at t+5, arrives at t+15 => readyToDoctor 600s, overlap 300s.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        clock.SetUtcNow(base_.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        clock.SetUtcNow(base_.AddMinutes(10));
        Assert.NotNull(context.Store.MarkDoctorComplete(2));
        Assert.NotNull(context.Store.MarkRoomAvailable(2));

        clock.SetUtcNow(base_.AddMinutes(15));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(base_.AddMinutes(20));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var reports = context.Store.GetReports();
        var targetCycle = reports.RecentCompletedCycles.Single(cycle => cycle.RoomId == 1);
        var con = reports.ProcedureSummaries.Single(summary => summary.ProcedureCode == "CON");

        // The summary values match the cycle's annotated occupied/available wait exactly.
        Assert.Equal(5 * 60, targetCycle.DoctorOccupiedWaitSeconds);
        Assert.Equal(5 * 60, targetCycle.DoctorAvailableWaitSeconds);
        Assert.Equal(300, con.AverageDoctorOccupiedWaitSeconds);
        Assert.Equal(300, con.MedianDoctorOccupiedWaitSeconds);
        Assert.Equal(300, con.AverageDoctorAvailableWaitSeconds);
        Assert.Equal(300, con.MedianDoctorAvailableWaitSeconds);
    }

    [Fact]
    public void Procedure_summaries_exclude_exception_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        RunProcedureCycle(context, clock, baseTime, 1, "otte", "CON", 5, 10, 10, 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 2, "otte", "CON", 5, 10, 10, 5);

        // Flag one CON cycle as a (pending) exception.
        var flagged = context.Store.GetReports().RecentCompletedCycles.First(cycle => cycle.ProcedureCode == "CON");
        Assert.True(context.Store.MarkCycleAsExceptionById(flagged.CompletedCycleId, ExceptionReasons.ManualReview, "Exclude from normal metrics"));

        var con = context.Store.GetReports().ProcedureSummaries.Single(summary => summary.ProcedureCode == "CON");
        Assert.Equal(1, con.CompletedCycleCount);
    }

    [Fact]
    public void Procedure_summaries_exclude_reviewed_exception_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        RunProcedureCycle(context, clock, baseTime, 1, "otte", "CON", 5, 10, 10, 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 2, "otte", "CON", 5, 10, 10, 5);

        var flagged = context.Store.GetReports().RecentCompletedCycles.First(cycle => cycle.ProcedureCode == "CON");
        Assert.True(context.Store.MarkCycleAsExceptionById(flagged.CompletedCycleId, ExceptionReasons.ManualReview, "Exclude from normal metrics"));
        Assert.Equal(ReviewExceptionOutcome.Reviewed, context.Store.ReviewExceptionCycleById(flagged.CompletedCycleId).Outcome);

        var con = context.Store.GetReports().ProcedureSummaries.Single(summary => summary.ProcedureCode == "CON");
        // The reviewed exception stays excluded; only the one normal CON cycle counts.
        Assert.Equal(1, con.CompletedCycleCount);
    }

    [Fact]
    public void Procedure_summaries_fall_back_to_code_when_label_is_blank()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        // Custom roster with a procedure whose label is blank.
        var roster = new ProcedureRosterOptions
        {
            Procedures =
            [
                new ProcedureRosterItem { Id = "blank", Code = "BLANK", Label = "   ", Icon = "misc", Active = true }
            ]
        };
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            procedureRosterOptions: roster);

        RunProcedureCycle(context, clock, baseTime, 1, "otte", "BLANK", 5, 10, 10, 5);

        var summary = Assert.Single(context.Store.GetReports().ProcedureSummaries);
        // Blank label falls back to the raw code; reports do not crash.
        Assert.Equal("BLANK", summary.ProcedureCode);
        Assert.Equal("BLANK", summary.ProcedureLabel);
        Assert.Equal(1, summary.CompletedCycleCount);
    }

    [Fact]
    public void Procedure_summaries_are_additive_and_global_metrics_stay_combined()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Two CON (ready 600, 1200) and one EXT (ready 600).
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "CON", 5, 10, 10, 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 2, "otte", "CON", 5, 20, 10, 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(2), 3, "otte", "EXT", 5, 10, 10, 5);

        var reports = context.Store.GetReports();

        // Global metrics still combine ALL procedures: count 3, ready avg (600+1200+600)/3 = 800.
        Assert.Equal(3, reports.CompletedRoomCyclesCount);
        Assert.Equal(800, reports.AverageReadyToDoctorSeconds);

        // The CON-only baseline differs from the global figure, proving the breakdown is additive
        // and did not alter the global combined math.
        var con = reports.ProcedureSummaries.Single(summary => summary.ProcedureCode == "CON");
        Assert.Equal(900, con.AverageReadyToDoctorSeconds);
    }

}
