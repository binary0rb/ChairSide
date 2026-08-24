using System.Reflection;
using System.Text.Json;

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

        using var all = context.Store.GetReports();
        using var doctor = context.Store.GetReports(ReportQuery.FromStrings(
            null,
            null,
            ReportScopeKinds.Doctor,
            "otte",
            ReportSedationSegments.All,
            ReportProcedureGroupings.Family));
        using var sedation = context.Store.GetReports(ReportQuery.FromStrings(
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
    public void All_time_pages_every_source_and_preserves_exact_report_composition()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var start = new DateTimeOffset(2025, 1, 1, 8, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 240; index++)
        {
            SaveCycle(
                context,
                index + 1,
                start.AddDays(index / 4).AddMinutes(index % 4 * 20),
                doctor: index % 2 == 0 ? "otte" : "pledger",
                procedure: index % 3 == 0 ? "EXT+SED" : "CON",
                doctorMinutes: 8 + (index % 17));
        }

        using var actual = context.Store.GetReports();

        Assert.Equal(0, context.Repository.UnboundedHistoricalLoadCount);
        Assert.InRange(context.Repository.LargestHistoricalPageSize, 1, 100);

        var completePopulation = context.Repository.LoadCompletedCycles();
        var builderField = typeof(DemoBoardStore).GetField(
            "_reportsSnapshotBuilder",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var builder = Assert.IsType<ReportsSnapshotBuilder>(builderField!.GetValue(context.Store));
        using var reference = builder.Build(completePopulation, [], ReportQuery.Default);

        Assert.Equal(reference.CompletedRoomCyclesCount, actual.CompletedRoomCyclesCount);
        Assert.Equal(reference.MedianSeatedToDoctorSeconds, actual.MedianSeatedToDoctorSeconds);
        Assert.Equal(reference.MedianDoctorInRoomSeconds, actual.MedianDoctorInRoomSeconds);
        Assert.Equal(reference.MedianDoctorOccupiedWaitSeconds, actual.MedianDoctorOccupiedWaitSeconds);
        Assert.Equal(JsonSerializer.Serialize(reference.Trends), JsonSerializer.Serialize(actual.Trends));
        Assert.Equal(JsonSerializer.Serialize(reference.ObservedDoctorDays), JsonSerializer.Serialize(actual.ObservedDoctorDays));
        Assert.Equal(JsonSerializer.Serialize(reference.ObservedDoctorFlowDays), JsonSerializer.Serialize(actual.ObservedDoctorFlowDays));
        Assert.Equal(JsonSerializer.Serialize(reference.ScheduleFit), JsonSerializer.Serialize(actual.ScheduleFit));
        Assert.Equal(
            JsonSerializer.Serialize(reference.ProcedureIntelligenceRows),
            JsonSerializer.Serialize(actual.ProcedureIntelligenceRows));
    }

    [Fact]
    public void Calibration_evidence_reconciles_exactly_from_bounded_candidate_pages()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var start = new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 125; index++)
        {
            SaveCycle(
                context,
                index + 1,
                start.AddDays(index / 10).AddMinutes((index % 10) * 45),
                procedure: "CON",
                doctorMinutes: 30);
        }

        using var reports = context.Store.GetReports();
        var segment = Assert.Single(reports.ScheduleFit!.ProcedureSegments!);
        var insight = segment.CurrentDefaultCalibration.Insight;
        Assert.NotNull(insight);
        Assert.Equal(125, insight.Evidence.Count);
        Assert.IsType<DiskBackedReadOnlyList<CalibrationEvidenceCase>>(insight.Evidence);

        var page = context.Store.QueryReportAudit(new ReportAuditRequest(
            ContributorKind: ReportAuditContributorKinds.CalibrationEvidence,
            BaseProcedureCode: "CON",
            EvidenceIds: insight.Evidence.Select(item => new ReportAuditEvidenceIdentity(
                item.CompletedCycleId,
                item.AcceptedReadyHandoffId)).ToList(),
            Offset: 0,
            Limit: 100));

        Assert.Equal(125, page.TotalMatchingCount);
        Assert.Equal(100, page.ReturnedCount);
        Assert.True(page.HasMore);
        Assert.All(page.Rows, row => Assert.NotNull(row.CalibrationEvidence));
        Assert.Equal(0, context.Repository.UnboundedHistoricalLoadCount);
        Assert.InRange(context.Repository.LargestHistoricalPageSize, 1, 100);
    }

    [Fact]
    public void All_time_review_completed_and_aborted_sources_are_paged_and_disk_backed()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var start = new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 110; index++)
        {
            var cycle = SaveCycle(context, index + 1, start.AddMinutes(index));
            cycle.IsException = true;
            cycle.RequiresReview = true;
            cycle.ExceptionReason = ExceptionReasons.ManualReview;
            context.Repository.SaveCompletedCycle(cycle, context.Doctors, context.Procedures);

            var terminatedAt = start.AddDays(1).AddMinutes(index);
            var aborted = new AbortedRoomAssignment
            {
                EpisodeId = $"bounded-abort-{index}",
                RoomId = 500 + index,
                PrestageStartedAt = terminatedAt.AddMinutes(-5),
                TerminatedAt = terminatedAt,
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

        using var reports = context.Store.GetReports();

        Assert.Equal(110, reports.ExceptionCycles.Count);
        Assert.Equal(220, reports.ExceptionReviewRecords!.Count);
        Assert.IsType<DiskBackedReadOnlyList<CompletedRoomCycle>>(reports.ExceptionCycles);
        Assert.IsType<DiskBackedReadOnlyList<ExceptionReviewRecord>>(reports.ExceptionReviewRecords);
        Assert.Equal(0, context.Repository.UnboundedHistoricalLoadCount);
        Assert.InRange(context.Repository.LargestHistoricalPageSize, 1, 100);
    }

    [Theory]
    [InlineData(ReportAuditSorts.Doctor, "assigned_doctor_display_name")]
    [InlineData(ReportAuditSorts.Procedure, "procedure_category")]
    public void Review_doctor_and_procedure_sorts_use_projected_labels_globally_across_pages(
        string sort,
        string persistedColumn)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var start = new DateTimeOffset(2026, 2, 2, 8, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 130; index++)
        {
            var cycle = SaveCycle(
                context,
                index + 1,
                start.AddMinutes(index),
                doctor: index % 2 == 0 ? "otte" : "pledger",
                procedure: index % 2 == 0 ? "CON" : "SED");
            cycle.IsException = true;
            cycle.RequiresReview = true;
            cycle.ExceptionReason = ExceptionReasons.ManualReview;
            context.Repository.SaveCompletedCycle(cycle, context.Doctors, context.Procedures);
        }

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = context.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = persistedColumn == "assigned_doctor_display_name"
                ? "UPDATE completed_room_cycles SET assigned_doctor_display_name = CASE assigned_doctor_id WHEN 'otte' THEN 'Zulu' ELSE 'Alpha' END;"
                : "UPDATE completed_room_cycles SET procedure_category = CASE procedure_code WHEN 'CON' THEN 'Zulu' ELSE 'Alpha' END;";
            command.ExecuteNonQuery();
        }

        var page = context.Store.QueryReportAudit(new ReportAuditRequest(
            ContributorKind: ReportAuditContributorKinds.PendingReview,
            Sort: sort,
            Offset: 55,
            Limit: 30));

        Assert.Equal(130, page.TotalMatchingCount);
        Assert.Equal(30, page.ReturnedCount);
        var projectedLabels = sort == ReportAuditSorts.Doctor
            ? page.ReviewRows.Select(row => row.DoctorName).ToList()
            : page.ReviewRows.Select(row => row.ProcedureLabel).ToList();
        var firstLabel = sort == ReportAuditSorts.Doctor ? "Dr. Otte" : "Consult";
        var secondLabel = sort == ReportAuditSorts.Doctor ? "Dr. Pledger" : "Sedation";
        Assert.Equal(10, projectedLabels.Count(label => label == firstLabel));
        Assert.Equal(20, projectedLabels.Count(label => label == secondLabel));
        Assert.Equal(Enumerable.Repeat(firstLabel, 10).Concat(Enumerable.Repeat(secondLabel, 20)), projectedLabels);
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
        string procedure = "CON",
        int doctorMinutes = 10)
    {
        var ready = seatedAt.AddMinutes(2);
        var arrived = ready.AddMinutes(3);
        var complete = arrived.AddMinutes(doctorMinutes);
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
            DoctorInRoomSeconds = doctorMinutes * 60,
            TurnoverSeconds = 240,
            TotalRoomCycleSeconds = (int)(available - seatedAt).TotalSeconds,
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
