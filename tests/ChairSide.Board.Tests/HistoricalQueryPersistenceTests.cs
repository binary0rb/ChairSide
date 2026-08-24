using System.Reflection;

using ChairSide.Board.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class HistoricalQueryPersistenceTests
{
    [Fact]
    public void Completed_window_and_page_are_enforced_by_sqlite()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var start = new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero);
        for (var day = 0; day < 6; day++)
        {
            SaveCycle(context, day + 1, start.AddDays(day));
        }

        var window = ReportDateRange.FromDates(new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 5));
        var page = context.Repository.LoadCompletedCyclesPage(window, offset: 1, limit: 2);

        Assert.Equal(4, page.TotalMatchingCount);
        Assert.Equal(2, page.Rows.Count);
        Assert.All(page.Rows, cycle => Assert.True(window.Includes(cycle.DoctorCompleteAt)));
        Assert.Equal([4, 3], page.Rows.Select(cycle => cycle.RoomId));
        Assert.True(page.HasMore);
    }

    [Fact]
    public void Aborted_window_and_page_are_enforced_by_sqlite()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var start = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        for (var day = 0; day < 5; day++)
        {
            var at = start.AddDays(day);
            var record = new AbortedRoomAssignment
            {
                EpisodeId = $"abort-{day}",
                RoomId = day + 1,
                PrestageStartedAt = at,
                TerminatedAt = at.AddMinutes(5),
                TerminatedFromState = RoomStates.Prestaging,
                TerminationKind = TerminationKinds.StaffCanceled
            };
            context.Repository.TerminateIncompleteAssignment(
                record,
                new RoomState(day + 1),
                context.Doctors,
                context.Procedures);
        }

        var window = ReportDateRange.FromDates(new DateOnly(2026, 7, 2), new DateOnly(2026, 7, 4));
        var page = context.Repository.LoadAbortedAssignmentsPage(window, offset: 1, limit: 1);

        Assert.Equal(3, page.TotalMatchingCount);
        var row = Assert.Single(page.Rows);
        Assert.Equal("abort-2", row.EpisodeId);
        Assert.True(page.HasMore);
    }

    [Fact]
    public void Typed_durable_identity_loads_exactly_one_source_record()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var cycle = SaveCycle(context, 1, new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero));
        var abort = new AbortedRoomAssignment
        {
            EpisodeId = "typed-abort",
            RoomId = 2,
            TerminatedAt = new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero),
            TerminatedFromState = RoomStates.Prestaging,
            TerminationKind = TerminationKinds.StaffCanceled
        };
        context.Repository.TerminateIncompleteAssignment(abort, new RoomState(2), context.Doctors, context.Procedures);

        var completed = context.Repository.LoadHistoricalEncounter(new(
            HistoricalEncounterSourceTypes.CompletedCycle,
            cycle.CompletedCycleId));
        var aborted = context.Repository.LoadHistoricalEncounter(new(
            HistoricalEncounterSourceTypes.AbortedAssignment,
            abort.AbortedAssignmentId));

        Assert.NotNull(completed?.CompletedCycle);
        Assert.Null(completed?.AbortedAssignment);
        Assert.NotNull(aborted?.AbortedAssignment);
        Assert.Null(aborted?.CompletedCycle);
        Assert.Null(context.Repository.LoadHistoricalEncounter(new("Unknown", cycle.CompletedCycleId)));
    }

    [Fact]
    public void Review_anchor_falls_back_to_prestage_started_at()
    {
        var prestage = new DateTimeOffset(2026, 8, 11, 7, 30, 0, TimeSpan.Zero);
        var cycle = new CompletedRoomCycle
        {
            PrestageStartedAt = prestage,
            SeatedAt = default
        };

        Assert.Equal(prestage, ReportsSnapshotBuilder.ReviewAnchor(cycle));
    }

    [Fact]
    public void Review_page_combines_sources_and_applies_limit_offset_before_materialization()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var start = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 125; index++)
        {
            var cycle = SaveCycle(context, index + 1, start.AddMinutes(index));
            cycle.IsException = true;
            cycle.RequiresReview = true;
            cycle.ExceptionReason = ExceptionReasons.ManualReview;
            context.Repository.SaveCompletedCycle(cycle, context.Doctors, context.Procedures);
        }
        for (var index = 0; index < 5; index++)
        {
            var at = start.AddDays(1).AddMinutes(index);
            var aborted = new AbortedRoomAssignment
            {
                EpisodeId = $"review-abort-{index}",
                RoomId = 200 + index,
                PrestageStartedAt = at,
                TerminatedAt = at.AddMinutes(5),
                TerminatedFromState = RoomStates.Prestaging,
                TerminationKind = TerminationKinds.AfterHoursExpired,
                IsException = true,
                RequiresReview = true,
                ExceptionReason = ExceptionReasons.AfterHoursSweep
            };
            context.Repository.TerminateIncompleteAssignment(
                aborted,
                new RoomState(aborted.RoomId),
                context.Doctors,
                context.Procedures);
        }

        var page = context.Repository.LoadReviewEncounterKeysPage(
            ReportDateRange.AllTime,
            requiresReview: true,
            ReportAuditSorts.MostRecent,
            offset: 0,
            limit: 10);

        Assert.Equal(130, page.TotalMatchingCount);
        Assert.Equal(10, page.Rows.Count);
        Assert.True(page.HasMore);
        Assert.Equal(5, page.Rows.Count(key => key.SourceType == HistoricalEncounterSourceTypes.AbortedAssignment));
        Assert.Equal(5, page.Rows.Count(key => key.SourceType == HistoricalEncounterSourceTypes.CompletedCycle));

        var storePage = context.Store.QueryReportAudit(new ReportAuditRequest(
            ContributorKind: ReportAuditContributorKinds.PendingReview,
            Offset: 0,
            Limit: 10));
        Assert.Equal(130, storePage.TotalMatchingCount);
        Assert.Equal(10, storePage.ReturnedCount);
        Assert.Equal(5, storePage.ReviewRows.Count(row => row.SourceType == ExceptionReviewSources.AbortedAssignment));
    }

    [Fact]
    public void Store_audit_returns_exact_paged_result_across_multiple_persistence_batches()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var start = new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 225; index++)
        {
            SaveCycle(context, index + 1, start.AddMinutes(index));
        }

        var page = context.Store.QueryReportAudit(new ReportAuditRequest(
            ContributorKind: ReportAuditContributorKinds.PracticeCompletedCases,
            Offset: 200,
            Limit: 25));

        Assert.Equal(225, page.TotalMatchingCount);
        Assert.Equal(25, page.ReturnedCount);
        Assert.Equal(200, page.Offset);
        Assert.False(page.HasMore);
        Assert.Equal(
            Enumerable.Range(1, 25).Reverse(),
            page.Rows.Select(row => row.RoomId));
    }

    [Fact]
    public void Store_startup_has_no_lifetime_completed_cycle_collection()
    {
        Assert.DoesNotContain(
            typeof(DemoBoardStore).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => string.Equals(field.Name, "_completedCycles", StringComparison.Ordinal));
    }

    [Fact]
    public void All_time_and_scoped_counts_remain_exact()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var start = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 240; index++)
        {
            SaveCycle(
                context,
                index + 1,
                start.AddMinutes(index),
                doctor: index % 2 == 0 ? "otte" : "pledger",
                procedure: index % 3 == 0 ? "EXT+SED" : "CON");
        }

        var all = context.Store.GetReports();
        var doctor = context.Store.GetReports(ReportQuery.FromStrings(
            null,
            null,
            ReportScopeKinds.Doctor,
            "otte",
            ReportSedationSegments.All,
            ReportProcedureGroupings.Family));
        var sedation = context.Store.GetReports(ReportQuery.FromStrings(
            null,
            null,
            ReportScopeKinds.Practice,
            null,
            ReportSedationSegments.Sedation,
            ReportProcedureGroupings.Family));

        Assert.Equal(240, all.TotalCompletedCycleCount);
        Assert.Equal(240, all.CompletedRoomCyclesCount);
        Assert.Equal(120, doctor.TotalCompletedCycleCount);
        Assert.Equal(80, sedation.TotalCompletedCycleCount);
    }

    [Fact]
    public void Historical_query_indexes_are_idempotently_present()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = context.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";
        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));

        Assert.Contains("ix_completed_cycles_report_window", names);
        Assert.Contains("ix_completed_cycles_review_window", names);
        Assert.Contains("ix_aborted_assignments_review_window", names);

        AssertQueryPlanUses(connection, """
            SELECT id
            FROM completed_room_cycles
            WHERE doctor_complete_at >= '2026-01-01T00:00:00.0000000+00:00'
            ORDER BY doctor_complete_at DESC, id DESC
            LIMIT 25;
            """, "ix_completed_cycles_report_window");
        AssertQueryPlanUses(connection, """
            SELECT id
            FROM completed_room_cycles
            WHERE is_exception = 1
              AND COALESCE(doctor_complete_at, doctor_arrived_at, seated_at, prestage_started_at)
                  >= '2026-01-01T00:00:00.0000000+00:00'
            ORDER BY COALESCE(doctor_complete_at, doctor_arrived_at, seated_at, prestage_started_at) DESC,
                     id DESC;
            """, "ix_completed_cycles_review_window");
    }

    private static CompletedRoomCycle SaveCycle(
        StoreContext context,
        int roomId,
        DateTimeOffset seatedAt,
        string doctor = "otte",
        string procedure = "CON")
    {
        var ready = seatedAt.AddMinutes(2);
        var arrived = ready.AddMinutes(3);
        var complete = arrived.AddMinutes(10);
        var available = complete.AddMinutes(4);
        var cycle = new CompletedRoomCycle
        {
            RoomId = roomId,
            AssignedDoctor = doctor,
            ProcedureCode = procedure,
            SeatedAt = seatedAt,
            ReadyForDoctorAt = ready,
            DoctorArrivedAt = arrived,
            DoctorCompleteAt = complete,
            RoomAvailableAt = available,
            SeatedToDoctorSeconds = 300,
            PrepSeconds = 120,
            ReadyToDoctorSeconds = 180,
            DoctorInRoomSeconds = 600,
            TurnoverSeconds = 240,
            TotalRoomCycleSeconds = 1140,
            ExpectedAllocationUnits = 2,
            ExpectedAllocationMinutes = 20,
            OriginalDefaultExpectedUnits = 2,
            FinalWaitState = RoomStates.ReadyForDoctor
        };
        context.Repository.SaveCompletedCycle(cycle, context.Doctors, context.Procedures);
        return cycle;
    }

    private static void AssertQueryPlanUses(
        SqliteConnection connection,
        string sql,
        string expectedIndex)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {sql}";
        var details = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) details.Add(reader.GetString(3));
        Assert.Contains(details, detail => detail.Contains(expectedIndex, StringComparison.Ordinal));
    }
}
