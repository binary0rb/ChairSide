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
    // Exception review workflow (confirm exclusion)
    // -------------------------------------------------------------------------

    [Fact]
    public void Marked_exception_starts_pending_review()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var cycle = CompleteOneCycle(context, room: 1, doctor: "otte");
        Assert.True(context.Store.MarkCycleAsExceptionById(cycle.CompletedCycleId, ExceptionReasons.ManualReview, "Exclude from normal metrics"));

        var exception = Assert.Single(context.Store.GetReports().ExceptionCycles);
        Assert.True(exception.IsException);
        Assert.True(exception.RequiresReview);
        Assert.Equal(ReviewStatuses.PendingReview, exception.ReviewStatus);
        Assert.Null(exception.ReviewedAt);
        Assert.Null(exception.ReviewedBy);
    }

    [Fact]
    public void Confirm_exclusion_marks_reviewed_and_keeps_cycle_excluded()
    {
        using var workspace = TestWorkspace.Create();
        var reviewedAt = new DateTimeOffset(2026, 6, 11, 14, 30, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(reviewedAt);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        var cycle = CompleteOneCycle(context, room: 1, doctor: "otte");
        Assert.True(context.Store.MarkCycleAsExceptionById(cycle.CompletedCycleId, ExceptionReasons.ManualReview, "Exclude from normal metrics"));

        var result = context.Store.ReviewExceptionCycleById(cycle.CompletedCycleId);
        Assert.Equal(ReviewExceptionOutcome.Reviewed, result.Outcome);
        Assert.Equal(1, result.RoomId);

        var reports = context.Store.GetReports();

        // After review the cycle is no longer pending and never returns to normal completed cycles.
        Assert.Empty(reports.RecentCompletedCycles);
        Assert.Empty(reports.ExceptionCycles);
        Assert.Equal(0, reports.CompletedRoomCyclesCount);
    }

    [Fact]
    public void Confirm_exclusion_sets_reviewed_metadata()
    {
        using var workspace = TestWorkspace.Create();
        var reviewedAt = new DateTimeOffset(2026, 6, 11, 14, 30, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(reviewedAt);
        var databasePath = workspace.ProductionDatabasePath();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock, databasePath: databasePath);

        var cycle = CompleteOneCycle(context, room: 1, doctor: "otte");
        Assert.True(context.Store.MarkCycleAsExceptionById(cycle.CompletedCycleId, ExceptionReasons.ManualReview, "Exclude from normal metrics"));
        Assert.Equal(ReviewExceptionOutcome.Reviewed, context.Store.ReviewExceptionCycleById(cycle.CompletedCycleId).Outcome);

        // Reload from the same database to confirm the reviewed metadata persisted.
        var reloaded = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var persisted = reloaded.Repository.LoadCompletedCycles()
            .Single(item => item.CompletedCycleId == cycle.CompletedCycleId);

        Assert.True(persisted.IsException);
        Assert.False(persisted.RequiresReview);
        Assert.Equal(ReviewStatuses.Reviewed, persisted.ReviewStatus);
        Assert.Equal("Exclude from normal metrics", persisted.SuggestedAction);
        Assert.Equal(reviewedAt, persisted.ReviewedAt);
        Assert.Equal(ExceptionReviewers.LocalAdmin, persisted.ReviewedBy);
    }

    [Fact]
    public void Reviewed_exception_stays_excluded_from_normal_metrics()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // One normal cycle and one cycle that becomes a reviewed exception.
        var normal = CompleteOneCycle(context, room: 1, doctor: "otte");
        var flagged = CompleteOneCycle(context, room: 2, doctor: "pledger");
        Assert.True(context.Store.MarkCycleAsExceptionById(flagged.CompletedCycleId, ExceptionReasons.ManualReview, "Exclude from normal metrics"));
        Assert.Equal(ReviewExceptionOutcome.Reviewed, context.Store.ReviewExceptionCycleById(flagged.CompletedCycleId).Outcome);

        var reports = context.Store.GetReports();

        // Only the normal cycle counts toward metrics; the reviewed exception is excluded and is
        // not in the pending-review queue either.
        Assert.Equal(1, reports.CompletedRoomCyclesCount);
        Assert.Equal(normal.CompletedCycleId, Assert.Single(reports.RecentCompletedCycles).CompletedCycleId);
        Assert.Empty(reports.ExceptionCycles);

        // Available-wait math unchanged for the surviving normal cycle.
        var normalReported = reports.RecentCompletedCycles.Single();
        Assert.Equal(normalReported.ReadyToDoctorSeconds, normalReported.DoctorAvailableWaitSeconds);
        Assert.Equal(0, normalReported.DoctorOccupiedWaitSeconds);
    }

    [Fact]
    public void Confirm_exclusion_missing_id_returns_not_found()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.Equal(ReviewExceptionOutcome.NotFound, context.Store.ReviewExceptionCycleById(999999).Outcome);
        Assert.Equal(ReviewExceptionOutcome.NotFound, context.Store.ReviewExceptionCycleById(0).Outcome);
        Assert.Equal(ReviewExceptionOutcome.NotFound, context.Store.ReviewExceptionCycleById(-5).Outcome);
    }

    [Fact]
    public void Confirm_exclusion_on_non_exception_returns_not_an_exception()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var cycle = CompleteOneCycle(context, room: 1, doctor: "otte");
        var result = context.Store.ReviewExceptionCycleById(cycle.CompletedCycleId);

        Assert.Equal(ReviewExceptionOutcome.NotAnException, result.Outcome);
        Assert.Equal(1, result.RoomId);

        // The cycle remains a normal completed cycle, untouched.
        var reports = context.Store.GetReports();
        var normal = Assert.Single(reports.RecentCompletedCycles);
        Assert.False(normal.IsException);
        Assert.Equal(ReviewStatuses.PendingReview, normal.ReviewStatus);
        Assert.Null(normal.ReviewedAt);
    }

    [Fact]
    public void Confirm_exclusion_is_idempotent()
    {
        using var workspace = TestWorkspace.Create();
        var firstReview = new DateTimeOffset(2026, 6, 11, 14, 30, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(firstReview);
        var databasePath = workspace.ProductionDatabasePath();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock, databasePath: databasePath);

        var cycle = CompleteOneCycle(context, room: 1, doctor: "otte");
        Assert.True(context.Store.MarkCycleAsExceptionById(cycle.CompletedCycleId, ExceptionReasons.ManualReview, "Exclude from normal metrics"));

        Assert.Equal(ReviewExceptionOutcome.Reviewed, context.Store.ReviewExceptionCycleById(cycle.CompletedCycleId).Outcome);

        // Confirming again succeeds (idempotent) and keeps the reviewed state stable.
        clock.SetUtcNow(firstReview.AddHours(1));
        Assert.Equal(ReviewExceptionOutcome.Reviewed, context.Store.ReviewExceptionCycleById(cycle.CompletedCycleId).Outcome);

        var reloaded = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var persisted = reloaded.Repository.LoadCompletedCycles()
            .Single(item => item.CompletedCycleId == cycle.CompletedCycleId);
        Assert.False(persisted.RequiresReview);
        Assert.Equal(ReviewStatuses.Reviewed, persisted.ReviewStatus);
        Assert.True(persisted.IsException);
    }

    [Fact]
    public void Legacy_mark_exception_still_appears_in_pending_review_queue()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var cycle = CompleteOneCycle(context, room: 1, doctor: "otte");
        // Legacy targeting by (roomId, seatedAt) must still flag the cycle as a pending exception.
        Assert.True(context.Store.MarkCycleAsException(cycle.RoomId, cycle.SeatedAt, ExceptionReasons.ManualReview, "Exclude from normal metrics"));

        var exception = Assert.Single(context.Store.GetReports().ExceptionCycles);
        Assert.True(exception.RequiresReview);
        Assert.Equal(ReviewStatuses.PendingReview, exception.ReviewStatus);
    }

    // -------------------------------------------------------------------------
    // Report date range filtering (bounds the completed-cycle population by DoctorCompleteAt)
    // -------------------------------------------------------------------------

    [Fact]
    public void Reports_without_date_range_include_all_completed_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 3);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 20, 14), expectedUnits: 3);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports();

        Assert.Equal(2, reports.CompletedRoomCyclesCount);
        Assert.Equal("All time", reports.RangeLabel);
        Assert.Null(reports.RangeStartDate);
        Assert.Equal(2, reports.TotalCompletedCycleCount);
    }

    [Fact]
    public void Date_range_includes_in_range_and_excludes_out_of_range_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 3);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 20, 14), expectedUnits: 3);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-08", "2026-06-12"));

        // Only the in-range cycle survives the source filter.
        Assert.Equal(1, reports.CompletedRoomCyclesCount);
        var cycle = Assert.Single(reports.RecentCompletedCycles);
        Assert.Equal(1, cycle.RoomId);
        // Window metadata reflects the selection; the all-time total is still reported for context.
        Assert.Equal("2026-06-08", reports.RangeStartDate);
        Assert.Equal("2026-06-12", reports.RangeEndDate);
        Assert.Equal(2, reports.TotalCompletedCycleCount);
    }

    [Fact]
    public void Date_range_end_day_is_inclusive_through_end_of_day()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // A cycle completing late on the end day must be included (end day inclusive through 23:59).
        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 12, 23, 30), expectedUnits: 3);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-10", "2026-06-12"));

        Assert.Equal(1, reports.CompletedRoomCyclesCount);
    }

    [Fact]
    public void Date_filter_applies_before_allocation_and_hygiene_counts()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 3);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 20, 14), expectedUnits: 3);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-08", "2026-06-12"));

        // Hygiene and allocation aggregates are computed over the date-filtered population only.
        Assert.Equal(1, reports.IncludedCompletedCycleCount);
        Assert.Equal(0, reports.ExcludedCompletedCycleCount);
        Assert.Equal(1, reports.AllocationVariance!.AllocationVarianceCycleCount);
    }

    [Fact]
    public void Date_filter_affects_doctor_and_procedure_family_summaries()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // In range: otte / EXT. Out of range: pledger / IMP.
        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 3);
        SaveCleanCycle(seed, room: 2, doctor: "pledger", code: "IMP", completeAt: Utc(2026, 6, 20, 14), expectedUnits: 6);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-08", "2026-06-12"));

        Assert.Equal("otte", Assert.Single(reports.DoctorSummaries).AssignedDoctor);
        Assert.Equal("EXT", Assert.Single(reports.BaseProcedureSummaries).ProcedureCode);
    }

    [Fact]
    public void Reversed_date_range_is_normalized_and_invalid_dates_degrade_to_all_time()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 15, 14), expectedUnits: 3);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 7, 15, 14), expectedUnits: 3);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // Reversed pair is normalized to Jun 10 - Jun 20 and includes the Jun 15 cycle.
        var reversed = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-20", "2026-06-10"));
        Assert.Equal(1, reversed.CompletedRoomCyclesCount);
        Assert.Equal("2026-06-10", reversed.RangeStartDate);
        Assert.Equal("2026-06-20", reversed.RangeEndDate);

        // Unparseable dates do not crash and behave as all-time.
        var invalid = context.Store.GetReports(ReportDateRange.FromDateStrings("not-a-date", "also-bad"));
        Assert.Equal(2, invalid.CompletedRoomCyclesCount);
        Assert.Equal("All time", invalid.RangeLabel);
    }

    // -------------------------------------------------------------------------
    // Doctor daily allocation balance (sparkline data)
    //
    // SaveCleanCycle fixes measured case flow at 30 min (seated 30 min before complete), so each
    // cycle's net allocation variance is 30 - expectedUnits * 10: expectedUnits 2 => +10 (over),
    // 1 => +20 (over), 4 => -10 (under), 3 => 0 (at expected).
    // -------------------------------------------------------------------------

    [Fact]
    public void Doctor_daily_allocation_series_reflects_filtered_date_range()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // otte: one cycle in range (Jun 10, +10 variance), one outside the window (Jun 20).
        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 2);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 20, 14), expectedUnits: 2);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-08", "2026-06-12"));

        var series = Assert.Single(reports.DoctorDailyAllocationSeries!);
        Assert.Equal("otte", series.DoctorId);
        var point = Assert.Single(series.Points);
        Assert.Equal("2026-06-10", point.Date);
        Assert.Equal(1, point.CaseCount);
        Assert.Equal(10, point.NetVarianceMinutes);
    }

    [Fact]
    public void Doctor_daily_allocation_point_net_variance_can_be_negative()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // expectedUnits 4 => 40 min expected vs 30 min measured => -10 (under expected).
        SaveCleanCycle(seed, room: 1, doctor: "pledger", code: "IMP", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 4);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-08", "2026-06-12"));

        var point = Assert.Single(Assert.Single(reports.DoctorDailyAllocationSeries!).Points);
        Assert.Equal(1, point.CaseCount);
        Assert.Equal(-10, point.NetVarianceMinutes);
    }

    [Fact]
    public void Doctor_daily_allocation_series_today_contains_only_todays_points()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        context.Store.SeedSyntheticReportData();

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var reports = context.Store.GetReports(ReportDateRange.FromDates(today, today));

        var todayStr = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var series in reports.DoctorDailyAllocationSeries!)
        {
            foreach (var point in series.Points)
            {
                Assert.Equal(todayStr, point.Date);
            }
        }
    }

    [Fact]
    public void Doctor_daily_allocation_series_grows_with_wider_date_ranges()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        context.Store.SeedSyntheticReportData();

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        static int TotalPoints(IReadOnlyList<DoctorDailyAllocation>? series) =>
            (series ?? []).Sum(s => s.Points.Count);

        var todayPoints = TotalPoints(context.Store.GetReports(ReportDateRange.FromDates(today, today)).DoctorDailyAllocationSeries);
        var last7Points = TotalPoints(context.Store.GetReports(ReportDateRange.FromDates(today.AddDays(-6), today)).DoctorDailyAllocationSeries);
        var last30Points = TotalPoints(context.Store.GetReports(ReportDateRange.FromDates(today.AddDays(-29), today)).DoctorDailyAllocationSeries);
        var allPoints = TotalPoints(context.Store.GetReports().DoctorDailyAllocationSeries);

        Assert.True(last7Points > todayPoints, $"last7={last7Points} today={todayPoints}");
        Assert.True(last30Points > last7Points, $"last30={last30Points} last7={last7Points}");
        Assert.True(allPoints > last30Points, $"all={allPoints} last30={last30Points}");
    }

    [Fact]
    public void Doctor_daily_allocation_series_covers_all_four_seeded_doctors()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        context.Store.SeedSyntheticReportData();

        var doctorIds = context.Store.GetReports().DoctorDailyAllocationSeries!
            .Select(s => s.DoctorId)
            .ToHashSet();

        Assert.Contains("otte", doctorIds);
        Assert.Contains("pledger", doctorIds);
        Assert.Contains("gibson", doctorIds);
        Assert.Contains("schroeder", doctorIds);
    }

    [Fact]
    public void Doctor_daily_allocation_point_aggregates_case_count_and_signed_net_variance()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // Three cycles for otte on the same UTC day, mixing over and under so the signed sum (not an
        // absolute) is exercised: +20, +10, -10 => net +20 across 3 cases.
        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "CON", completeAt: Utc(2026, 6, 10, 9), expectedUnits: 1);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 11), expectedUnits: 2);
        SaveCleanCycle(seed, room: 3, doctor: "otte", code: "IMP", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 4);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-10", "2026-06-10"));

        var series = Assert.Single(reports.DoctorDailyAllocationSeries!);
        Assert.Equal("otte", series.DoctorId);
        var point = Assert.Single(series.Points);
        Assert.Equal("2026-06-10", point.Date);
        Assert.Equal(3, point.CaseCount);
        Assert.Equal(20, point.NetVarianceMinutes);
    }

    [Fact]
    public void Doctor_daily_allocation_point_net_variance_rolls_up_to_report_total()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // Two cycles for otte across two different days, both in range.
        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 9), expectedUnits: 2);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "IMP", completeAt: Utc(2026, 6, 11, 9), expectedUnits: 4);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-08", "2026-06-12"));

        // The doctor's daily points sum to the same net the report's allocation aggregate reports.
        var series = Assert.Single(reports.DoctorDailyAllocationSeries!);
        var dailySum = series.Points.Sum(p => p.NetVarianceMinutes);
        Assert.Equal(reports.AllocationVariance!.NetAllocationVarianceMinutes, dailySum);
    }

    // -------------------------------------------------------------------------
    // Weekly wait trend read model exposed on the report snapshot
    // -------------------------------------------------------------------------

    [Fact]
    public void Reports_expose_weekly_wait_trends_over_standard_completed_population()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 8, 9), expectedUnits: 2);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 14, 9), expectedUnits: 2);
        SaveCleanCycle(seed, room: 3, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 15, 9), expectedUnits: 2);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var trends = context.Store.GetReports().Trends;

        Assert.NotNull(trends);
        Assert.Equal(ReportTrendSnapshotBuilder.WeeklyBucketSize, trends.BucketSize);
        Assert.Collection(
            trends.Buckets,
            first =>
            {
                Assert.Equal("2026-06-08", first.StartDate);
                Assert.Equal("2026-06-15", first.EndDate);
                Assert.Equal(2, first.CompletedCycleCount);
                Assert.Equal(900, first.MedianSeatedToDoctorSeconds);
                Assert.Equal(900, first.AverageSeatedToDoctorSeconds);
                Assert.Equal(2, first.TurnoverCycleCount);
                Assert.Equal(300, first.MedianTurnoverSeconds);
                Assert.Equal(300, first.AverageTurnoverSeconds);
            },
            second =>
            {
                Assert.Equal("2026-06-15", second.StartDate);
                Assert.Equal("2026-06-22", second.EndDate);
                Assert.Equal(1, second.CompletedCycleCount);
                Assert.Equal(1, second.TurnoverCycleCount);
                Assert.Equal(300, second.MedianTurnoverSeconds);
                Assert.Equal(300, second.AverageTurnoverSeconds);
            });
    }

    [Fact]
    public void Reports_wait_trends_respect_date_range_filter()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 2);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 20, 14), expectedUnits: 2);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-08", "2026-06-12"));

        var trends = reports.Trends;
        Assert.NotNull(trends);
        var bucket = Assert.Single(trends.Buckets);
        Assert.Equal("2026-06-08", bucket.StartDate);
        Assert.Equal("2026-06-15", bucket.EndDate);
        Assert.Equal(1, bucket.CompletedCycleCount);
        Assert.Equal(1, bucket.TurnoverCycleCount);
        Assert.Equal(300, bucket.MedianTurnoverSeconds);
        Assert.Equal(300, bucket.AverageTurnoverSeconds);
    }

    [Fact]
    public void Reports_wait_trends_exclude_reporting_exception_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 9), expectedUnits: 2);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "UNKNOWN", completeAt: Utc(2026, 6, 11, 9), expectedUnits: 2);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports();

        Assert.Equal(2, reports.CompletedRoomCyclesCount);
        Assert.Equal(1, reports.IncludedCompletedCycleCount);
        Assert.Equal(1, reports.ExcludedCompletedCycleCount);

        var bucket = Assert.Single(reports.Trends!.Buckets);
        Assert.Equal(1, bucket.CompletedCycleCount);
        Assert.Equal(1, bucket.TurnoverCycleCount);
    }

    [Fact]
    public void Reports_ready_wait_trend_uses_reissued_accepted_ready_not_withdrawn_interval()
    {
        using var workspace = TestWorkspace.Create();
        var start = Utc(2026, 6, 10, 8);
        var clock = new ManualTimeProvider(start);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock);
        var assignment = RoomAssignmentContract.Create(
            "otte",
            "EXT+SED",
            SedationContract.EligibleYes(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(3),
            isAddOn: true);

        Assert.NotNull(context.Store.BeginPrestage(1));
        Assert.NotNull(context.Store.SaveAssignmentDetails(1, assignment));
        clock.SetUtcNow(start.AddMinutes(1));
        Assert.NotNull(context.Store.SeatRoomCanonical(1, null).Room);
        clock.SetUtcNow(start.AddMinutes(2));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(start.AddMinutes(12));
        Assert.NotNull(context.Store.WithdrawReady(1));
        clock.SetUtcNow(start.AddMinutes(20));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(start.AddMinutes(23));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        clock.SetUtcNow(start.AddMinutes(40));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(start.AddMinutes(45));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var reports = context.Store.GetReports();
        Assert.Equal(180, reports.MedianReadyToDoctorSeconds);
        Assert.Equal(1, reports.IncludedCompletedCycleCount);
        var procedure = Assert.Single(reports.ScopedProcedureGroups!);
        Assert.Equal("EXT", procedure.ProcedureCode);
        Assert.Equal(1, procedure.CaseCount);
        Assert.Equal(1, procedure.ScopedPopulationCount);
        Assert.Equal(1d, procedure.ShareOfScopedCases);
        var completedCycle = Assert.Single(reports.RecentCompletedCycles);
        Assert.Equal("EXT+SED", completedCycle.ProcedureCode);
        Assert.True(completedCycle.IsAddOn);
        var bucket = Assert.Single(reports.Trends!.Buckets);
        Assert.Equal(180, bucket.MedianReadyWaitSeconds);
        Assert.Equal(1, bucket.ReadyWaitSample!.ContributingCount);
        Assert.Equal(ReportSampleStates.Limited, bucket.ReadyWaitSample.State);
    }

    // -------------------------------------------------------------------------
    // Schedule-fit read model exposed on the report snapshot
    //
    // SaveCleanCycle fixes measured case flow at 30 min, so each cycle's variance is
    // 30 - expectedUnits * 10. ScheduleFit is computed over the standard completed-cycle
    // population, the same set that feeds AllocationVariance, so shared totals must agree.
    // -------------------------------------------------------------------------

    [Fact]
    public void Reports_expose_schedule_fit_over_standard_completed_population()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // Three clean cycles; measured 30 each. Expected 20, 20, 40 => variance +10, +10, -10 => net +10.
        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 9), expectedUnits: 2);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 11, 9), expectedUnits: 2);
        SaveCleanCycle(seed, room: 3, doctor: "otte", code: "IMP", completeAt: Utc(2026, 6, 12, 9), expectedUnits: 4);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports();

        var scheduleFit = reports.ScheduleFit;
        Assert.NotNull(scheduleFit);

        // All three are standard completed cycles and allocation-calculable.
        Assert.Equal(3, scheduleFit.IncludedCycleCount);
        Assert.Equal(3, scheduleFit.ScheduleFitCycleCount);
        Assert.Equal(3, scheduleFit.Overall.CycleCount);

        // expected 20+20+40 = 80; measured 30*3 = 90; variance = 90 - 80 = +10.
        Assert.Equal(80, scheduleFit.Overall.TotalExpectedMinutes);
        Assert.Equal(90, scheduleFit.Overall.TotalMeasuredMinutes);
        Assert.Equal(10, scheduleFit.Overall.TotalVarianceMinutes);

        // Shared totals must agree with the allocation variance summary over the same population.
        var allocation = reports.AllocationVariance;
        Assert.NotNull(allocation);
        Assert.Equal(allocation.AllocationVarianceCycleCount, scheduleFit.Overall.CycleCount);
        Assert.Equal(allocation.TotalExpectedAllocationMinutes, scheduleFit.Overall.TotalExpectedMinutes);
        Assert.Equal(allocation.TotalMeasuredCaseFlowMinutes, scheduleFit.Overall.TotalMeasuredMinutes);
        Assert.Equal(allocation.NetAllocationVarianceMinutes, scheduleFit.Overall.TotalVarianceMinutes);
    }

    [Fact]
    public void Reports_schedule_fit_present_and_zero_when_no_completed_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var scheduleFit = context.Store.GetReports().ScheduleFit;

        // Always populated, even with no data: the builder returns a zero report, not null.
        Assert.NotNull(scheduleFit);
        Assert.Equal(0, scheduleFit.IncludedCycleCount);
        Assert.Equal(0, scheduleFit.ScheduleFitCycleCount);
        Assert.Equal(0, scheduleFit.Overall.CycleCount);
        Assert.Equal(0, scheduleFit.Overall.TotalExpectedMinutes);
        Assert.Equal(0, scheduleFit.Overall.TotalMeasuredMinutes);
        Assert.Equal(0, scheduleFit.Overall.TotalVarianceMinutes);
        Assert.Equal(0, scheduleFit.Overall.TotalSlackMinutes);
        Assert.Equal(0, scheduleFit.Overall.TotalDebtMinutes);
        Assert.Null(scheduleFit.Overall.UtilizationRatio);
    }

    [Fact]
    public void Reports_schedule_fit_respects_date_range_filter()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // One cycle in range (Jun 10, expected 20, +10 variance), one outside the window (Jun 20).
        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 2);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 20, 14), expectedUnits: 2);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-08", "2026-06-12"));

        // Only the in-range cycle feeds schedule fit; the out-of-range cycle is filtered upstream.
        var scheduleFit = reports.ScheduleFit;
        Assert.NotNull(scheduleFit);
        Assert.Equal(1, scheduleFit.IncludedCycleCount);
        Assert.Equal(1, scheduleFit.ScheduleFitCycleCount);
        Assert.Equal(20, scheduleFit.Overall.TotalExpectedMinutes);
        Assert.Equal(30, scheduleFit.Overall.TotalMeasuredMinutes);
        Assert.Equal(10, scheduleFit.Overall.TotalVarianceMinutes);
    }

    [Fact]
    public void Reports_observed_doctor_days_report_span_fields_for_included_completed_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        var seatedAt = Utc(2026, 6, 10, 8);
        SaveObservedCycle(
            seed,
            room: 1,
            doctor: "otte",
            code: "CON",
            seatedAt: seatedAt,
            readyForDoctorAt: seatedAt.AddMinutes(5),
            doctorArrivedAt: seatedAt.AddMinutes(10),
            doctorCompleteAt: seatedAt.AddMinutes(30),
            roomAvailableAt: seatedAt.AddMinutes(40),
            expectedUnits: 3);

        SaveObservedCycle(
            seed,
            room: 2,
            doctor: "otte",
            code: "EXT",
            seatedAt: seatedAt.AddMinutes(60),
            readyForDoctorAt: seatedAt.AddMinutes(65),
            doctorArrivedAt: seatedAt.AddMinutes(80),
            doctorCompleteAt: seatedAt.AddMinutes(105),
            roomAvailableAt: seatedAt.AddMinutes(115),
            expectedUnits: 4);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-10", "2026-06-10"));

        var day = Assert.Single(reports.ObservedDoctorDays!);
        Assert.Equal("otte", day.DoctorId);
        Assert.False(string.IsNullOrWhiteSpace(day.DoctorName));
        Assert.Equal("2026-06-10", day.ReportDate);
        Assert.Equal(2, day.EncounterCount);
        Assert.Equal(seatedAt, day.FirstSeatedAt);
        Assert.Equal(seatedAt.AddMinutes(10), day.FirstDoctorArrivedAt);
        Assert.Equal(seatedAt.AddMinutes(105), day.LastDoctorCompleteAt);
        Assert.Equal(seatedAt.AddMinutes(115), day.LastRoomAvailableAt);
        Assert.Equal(105, day.ObservedClinicalSpanMinutes);
        Assert.Equal(115, day.ObservedTeamSpanMinutes);
    }

    [Fact]
    public void Reports_observed_doctor_days_bucket_active_room_minutes_by_concurrency()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        var baseTime = Utc(2026, 6, 10, 8);
        SaveObservedCycle(seed, room: 1, doctor: "otte", code: "CON", seatedAt: baseTime, readyForDoctorAt: baseTime.AddMinutes(5), doctorArrivedAt: baseTime.AddMinutes(10), doctorCompleteAt: baseTime.AddMinutes(30), roomAvailableAt: baseTime.AddMinutes(35), expectedUnits: 3);
        SaveObservedCycle(seed, room: 2, doctor: "otte", code: "EXT", seatedAt: baseTime.AddMinutes(10), readyForDoctorAt: baseTime.AddMinutes(15), doctorArrivedAt: baseTime.AddMinutes(20), doctorCompleteAt: baseTime.AddMinutes(40), roomAvailableAt: baseTime.AddMinutes(45), expectedUnits: 3);
        SaveObservedCycle(seed, room: 3, doctor: "otte", code: "IMP", seatedAt: baseTime.AddMinutes(20), readyForDoctorAt: baseTime.AddMinutes(25), doctorArrivedAt: baseTime.AddMinutes(30), doctorCompleteAt: baseTime.AddMinutes(50), roomAvailableAt: baseTime.AddMinutes(55), expectedUnits: 3);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-10", "2026-06-10"));

        var day = Assert.Single(reports.ObservedDoctorDays!);
        Assert.Equal(20, day.MinutesWithOneActiveRoom);
        Assert.Equal(20, day.MinutesWithTwoActiveRooms);
        Assert.Equal(10, day.MinutesWithThreeOrMoreActiveRooms);
        Assert.Equal(3, day.MaxActiveRoomCount);
    }

}
