using ChairSide.Board.Services;

namespace ChairSide.Board.Tests;

public sealed class ReportAuditBuilderTests
{
    [Fact]
    public void Completed_and_metric_evidence_populations_remain_distinct_and_exact()
    {
        var completed = Cycle(1, "otte", "EXT", Utc(8, 0), ready: 5, arrived: 15, complete: 45, available: 55);
        var phaseOnly = Cycle(2, "otte", "EXT", Utc(9, 0), ready: 5, arrived: 20, complete: 50, available: null);
        var manualException = Cycle(3, "otte", "EXT", Utc(10, 0), ready: 5, arrived: 10, complete: 35, available: 40);
        manualException.IsException = true;
        manualException.RequiresReview = true;

        var builder = CreateBuilder();
        var completedPage = builder.BuildAudit(
            [completed, phaseOnly, manualException],
            [],
            new ReportAuditRequest(ContributorKind: ReportAuditContributorKinds.PracticeCompletedCases));
        var metricPage = builder.BuildAudit(
            [completed, phaseOnly, manualException],
            [],
            new ReportAuditRequest(ContributorKind: ReportAuditContributorKinds.ReadyWait));

        Assert.Equal(ReportAuditModes.CompletedCaseAudit, completedPage.Mode);
        Assert.Equal([1L], completedPage.Rows.Select(row => row.CompletedCycleId));
        Assert.Equal(ReportAuditModes.MetricEvidence, metricPage.Mode);
        Assert.Equal([1L, 2L], metricPage.Rows.Select(row => row.CompletedCycleId).Order());
        Assert.Equal(600d, metricPage.Rows.Single(row => row.CompletedCycleId == 1).ReadyWaitSeconds);
        Assert.Equal(900d, metricPage.Rows.Single(row => row.CompletedCycleId == 2).ReadyWaitSeconds);
    }

    [Fact]
    public void Procedure_intelligence_timing_uses_completed_contributors_while_generic_phase_kinds_remain_broader()
    {
        var completed = Cycle(1, "otte", "EXT", Utc(8, 0), available: 55);
        var phaseOnly = Cycle(2, "otte", "EXT", Utc(9, 0), available: null);
        var noAllocation = Cycle(3, "otte", "EXT", Utc(10, 0), available: 55, expected: 0);
        var negativeReady = Cycle(4, "otte", "EXT", Utc(11, 0), available: 55);
        negativeReady.ReadyToDoctorSeconds = -1;
        var negativeDoctor = Cycle(5, "otte", "EXT", Utc(12, 0), available: 55);
        negativeDoctor.DoctorInRoomSeconds = -1;
        var cycles = new[] { completed, phaseOnly, noAllocation, negativeReady, negativeDoctor };
        var builder = CreateBuilder();

        var genericReady = builder.BuildAudit(
            cycles, [],
            new ReportAuditRequest(ContributorKind: ReportAuditContributorKinds.ReadyWait));
        var genericDoctor = builder.BuildAudit(
            cycles, [],
            new ReportAuditRequest(ContributorKind: ReportAuditContributorKinds.DoctorTime));
        var procedureReady = builder.BuildAudit(
            cycles, [],
            new ReportAuditRequest(ContributorKind: ReportAuditContributorKinds.ProcedureIntelligenceReadyWait));
        var procedureDoctor = builder.BuildAudit(
            cycles, [],
            new ReportAuditRequest(ContributorKind: ReportAuditContributorKinds.ProcedureIntelligenceDoctorTime));
        var procedureSeatedToComplete = builder.BuildAudit(
            cycles, [],
            new ReportAuditRequest(ContributorKind: ReportAuditContributorKinds.ProcedureIntelligenceSeatedToDoctorComplete));
        var historicalScheduleFit = builder.BuildAudit(
            cycles, [],
            new ReportAuditRequest(ContributorKind: ReportAuditContributorKinds.HistoricalScheduleFit));

        Assert.Equal([1L, 2L, 3L, 4L, 5L], genericReady.Rows.Select(row => row.CompletedCycleId).Order());
        Assert.Equal([1L, 2L, 3L, 4L, 5L], genericDoctor.Rows.Select(row => row.CompletedCycleId).Order());
        Assert.Equal([1L, 3L, 5L], procedureReady.Rows.Select(row => row.CompletedCycleId).Order());
        Assert.Equal([1L, 3L, 4L], procedureDoctor.Rows.Select(row => row.CompletedCycleId).Order());
        Assert.Equal([1L, 3L, 4L, 5L], procedureSeatedToComplete.Rows.Select(row => row.CompletedCycleId).Order());
        Assert.Contains(procedureSeatedToComplete.Rows, row => row.CompletedCycleId == 3 && row.ExpectedAllocationMinutes == 0);
        Assert.DoesNotContain(historicalScheduleFit.Rows, row => row.CompletedCycleId == 3);

        var intelligence = Assert.Single(builder.Build(cycles, [], ReportQuery.Default).ProcedureIntelligenceRows!);
        Assert.Equal(3, intelligence.Metrics.ReadyWaitSample.ContributingCount);
        Assert.Equal(3, intelligence.Metrics.DoctorTimeSample.ContributingCount);
    }

    [Fact]
    public void Procedure_intelligence_contributors_inherit_scope_segment_sedation_and_grouping_filters()
    {
        var otteBase = Cycle(1, "otte", "EXT", Utc(8, 0));
        var otteSedation = Cycle(2, "otte", "EXT+SED", Utc(9, 0));
        var pledgerSedation = Cycle(3, "pledger", "EXT+SED", Utc(10, 0));
        var cycles = new[] { otteBase, otteSedation, pledgerSedation };
        var builder = CreateBuilder();

        var practiceFamilySegment = builder.BuildAudit(
            cycles, [],
            new ReportAuditRequest(
                Scope: ReportScopeKinds.Practice,
                Sedation: ReportSedationSegments.Sedation,
                ProcedureGrouping: ReportProcedureGroupings.Family,
                ContributorKind: ReportAuditContributorKinds.ProcedureIntelligenceReadyWait,
                SegmentDoctorId: "pledger",
                BaseProcedureCode: "EXT"));
        var doctorDetailed = builder.BuildAudit(
            cycles, [],
            new ReportAuditRequest(
                Scope: ReportScopeKinds.Doctor,
                DoctorId: "otte",
                Sedation: ReportSedationSegments.Sedation,
                ProcedureGrouping: ReportProcedureGroupings.DetailedVariant,
                ContributorKind: ReportAuditContributorKinds.ProcedureIntelligenceDoctorTime,
                ProcedureCode: "EXT+SED"));

        Assert.Equal([3L], practiceFamilySegment.Rows.Select(row => row.CompletedCycleId));
        Assert.Equal([2L], doctorDetailed.Rows.Select(row => row.CompletedCycleId));
    }

    [Fact]
    public void Broad_completed_audit_marks_reporting_exclusions_but_included_audit_omits_them()
    {
        var included = Cycle(1, "otte", "EXT", Utc(8, 0));
        var unmapped = Cycle(2, "otte", "OLD", Utc(9, 0));
        var builder = CreateBuilder();

        var broad = builder.BuildAudit(
            [included, unmapped], [],
            new ReportAuditRequest(ContributorKind: ReportAuditContributorKinds.PracticeCompletedCases));
        var standard = builder.BuildAudit(
            [included, unmapped], [],
            new ReportAuditRequest(ContributorKind: ReportAuditContributorKinds.IncludedCompletedCases));

        Assert.Equal(2, broad.TotalMatchingCount);
        var excluded = broad.Rows.Single(row => row.CompletedCycleId == 2);
        Assert.Equal(ReportAuditStanding.ReportingExcluded, excluded.AnalyticalStanding);
        Assert.NotEmpty(excluded.ReportingExclusionReasons);
        Assert.Equal([1L], standard.Rows.Select(row => row.CompletedCycleId));
    }

    [Fact]
    public void Exact_projection_keeps_truthful_zero_and_reversed_intervals_null()
    {
        var zero = Cycle(1, "otte", "EXT", Utc(8, 0), ready: 10, arrived: 10, complete: 30, available: 30);
        var reversed = Cycle(2, "otte", "EXT", Utc(9, 0), ready: 20, arrived: 10, complete: 40, available: 35);
        var page = CreateBuilder().BuildAudit(
            [zero, reversed], [],
            new ReportAuditRequest(
                ContributorKind: ReportAuditContributorKinds.PracticeCompletedCases,
                Sort: ReportAuditSorts.LongestReadyWait));

        var zeroRow = page.Rows.Single(row => row.CompletedCycleId == 1);
        Assert.Equal(0d, zeroRow.ReadyWaitSeconds);
        Assert.Equal(0d, zeroRow.TurnoverSeconds);
        var reversedRow = page.Rows.Single(row => row.CompletedCycleId == 2);
        Assert.Null(reversedRow.ReadyWaitSeconds);
        Assert.Null(reversedRow.TurnoverSeconds);
        Assert.Equal(2, page.Rows[^1].CompletedCycleId);
    }

    [Fact]
    public void Query_inherits_base_scope_and_stacks_segment_sedation_and_grouping_filters()
    {
        var otteBase = Cycle(1, "otte", "EXT", Utc(8, 0));
        var otteSedation = Cycle(2, "otte", "EXT+SED", Utc(9, 0));
        var pledgerSedation = Cycle(3, "pledger", "EXT+SED", Utc(10, 0));
        var builder = CreateBuilder();

        var practiceSegment = builder.BuildAudit(
            [otteBase, otteSedation, pledgerSedation], [],
            new ReportAuditRequest(
                Scope: ReportScopeKinds.Practice,
                Sedation: ReportSedationSegments.Sedation,
                ProcedureGrouping: ReportProcedureGroupings.Family,
                ContributorKind: ReportAuditContributorKinds.ProcedureMix,
                SegmentDoctorId: "pledger",
                BaseProcedureCode: "EXT"));
        var doctorDetailed = builder.BuildAudit(
            [otteBase, otteSedation, pledgerSedation], [],
            new ReportAuditRequest(
                Scope: ReportScopeKinds.Doctor,
                DoctorId: "otte",
                ProcedureGrouping: ReportProcedureGroupings.DetailedVariant,
                ContributorKind: ReportAuditContributorKinds.ProcedureMix,
                ProcedureCode: "EXT+SED"));

        Assert.Equal(ReportScopeKinds.Practice, practiceSegment.NormalizedSelection.Query.Scope);
        Assert.Equal("pledger", practiceSegment.NormalizedSelection.SegmentDoctorId);
        Assert.Equal([3L], practiceSegment.Rows.Select(row => row.CompletedCycleId));
        Assert.Equal([2L], doctorDetailed.Rows.Select(row => row.CompletedCycleId));
    }

    [Fact]
    public void Review_queries_use_source_specific_anchors_and_effective_analytical_scope()
    {
        var pendingCompleted = Cycle(1, "pledger", "EXT+SED", Utc(12, 0), ready: 5, arrived: 10, complete: null, available: null);
        pendingCompleted.IsException = true;
        pendingCompleted.RequiresReview = true;
        pendingCompleted.ExceptionReason = "post-arrival-expiration";
        pendingCompleted.ReportingProjection = Projection(
            HistoricalAdministrativeDispositions.NeedsReview,
            doctor: "otte",
            procedure: "EXT",
            sedation: SedationState.EligibleNo);
        var reviewedCompleted = Cycle(2, "otte", "EXT", Utc(13, 0));
        reviewedCompleted.IsException = true;
        reviewedCompleted.RequiresReview = false;
        reviewedCompleted.ReviewStatus = ReviewStatuses.Reviewed;
        reviewedCompleted.ReviewedAt = Utc(15, 0);
        reviewedCompleted.ReportingProjection = Projection(
            HistoricalAdministrativeDispositions.ConfirmedException,
            doctor: "otte",
            procedure: "EXT",
            sedation: SedationState.EligibleNo,
            reviewed: true);
        var pendingAborted = Aborted(10, Utc(14, 0), requiresReview: true);
        var reviewedAborted = Aborted(11, Utc(15, 0), requiresReview: false);
        pendingAborted.ReportingProjection = Projection(
            HistoricalAdministrativeDispositions.NeedsReview,
            doctor: "pledger",
            procedure: "EXT",
            sedation: SedationState.EligibleYes);
        reviewedAborted.ReportingProjection = Projection(
            HistoricalAdministrativeDispositions.ConfirmedException,
            doctor: "pledger",
            procedure: "EXT",
            sedation: SedationState.EligibleYes,
            reviewed: true);

        var request = new ReportAuditRequest(
            From: "2026-08-10",
            To: "2026-08-10",
            Scope: ReportScopeKinds.Doctor,
            DoctorId: "otte",
            Sedation: ReportSedationSegments.NonSedation,
            ContributorKind: ReportAuditContributorKinds.PendingReview);
        var builder = CreateBuilder();
        var pending = builder.BuildAudit([pendingCompleted, reviewedCompleted], [pendingAborted, reviewedAborted], request);
        var reviewed = builder.BuildAudit(
            [pendingCompleted, reviewedCompleted], [pendingAborted, reviewedAborted],
            request with { ContributorKind = ReportAuditContributorKinds.ReviewedExceptionHistory });

        Assert.Single(pending.ReviewRows);
        Assert.Contains(pending.ReviewRows, row => row.CompletedCycleId == 1 && row.ReviewAnchor == pendingCompleted.DoctorArrivedAt);
        Assert.All(pending.ReviewRows, row => Assert.True(row.RequiresReview));
        Assert.Single(reviewed.ReviewRows);
        Assert.All(reviewed.ReviewRows, row => Assert.False(row.RequiresReview));
    }

    [Fact]
    public void Data_quality_reconciles_analytical_counts_and_separate_review_counts()
    {
        var included = Cycle(1, "otte", "EXT", Utc(8, 0));
        var excluded = Cycle(2, "otte", "OLD", Utc(9, 0));
        var pendingOtherDoctor = Cycle(3, "pledger", "EXT+SED", Utc(10, 0));
        pendingOtherDoctor.IsException = true;
        pendingOtherDoctor.RequiresReview = true;
        var reviewedAborted = Aborted(20, Utc(11, 0), requiresReview: false);
        var query = ReportQuery.FromStrings(
            "2026-08-10", "2026-08-10", ReportScopeKinds.Doctor, "otte",
            ReportSedationSegments.NonSedation, ReportProcedureGroupings.Family);

        var snapshot = CreateBuilder().Build([included, excluded, pendingOtherDoctor], [reviewedAborted], query);

        Assert.NotNull(snapshot.DataQuality);
        Assert.Equal(2, snapshot.DataQuality.CompletedCount);
        Assert.Equal(1, snapshot.DataQuality.IncludedCount);
        Assert.Equal(1, snapshot.DataQuality.ReportingExcludedCount);
        Assert.Equal(0, snapshot.DataQuality.PendingReviewCount);
        Assert.Equal(0, snapshot.DataQuality.ReviewedExceptionCount);
        Assert.NotEmpty(snapshot.DataQuality.ExclusionReasonCounts);
    }

    [Fact]
    public void Paging_clamps_to_one_hundred_and_preserves_stable_server_sorting()
    {
        var cycles = Enumerable.Range(1, 125)
            .Select(index => Cycle(index, index % 2 == 0 ? "otte" : "pledger", "EXT", Utc(0, 0).AddMinutes(index)))
            .ToList();
        cycles[0].ReadyForDoctorAt = cycles[0].SeatedAt;
        cycles[0].DoctorArrivedAt = cycles[0].SeatedAt.AddHours(2);
        cycles[0].ReadyToDoctorSeconds = 7200;
        var builder = CreateBuilder();

        var first = builder.BuildAudit(
            cycles, [],
            new ReportAuditRequest(
                ContributorKind: ReportAuditContributorKinds.ReadyWait,
                Sort: ReportAuditSorts.LongestReadyWait,
                Limit: 500));
        var second = builder.BuildAudit(
            cycles, [],
            new ReportAuditRequest(
                ContributorKind: ReportAuditContributorKinds.ReadyWait,
                Sort: ReportAuditSorts.LongestReadyWait,
                Offset: 100,
                Limit: 50));

        Assert.Equal(100, first.Limit);
        Assert.Equal(125, first.TotalMatchingCount);
        Assert.Equal(100, first.ReturnedCount);
        Assert.True(first.HasMore);
        Assert.Equal(1, first.Rows[0].CompletedCycleId);
        Assert.Equal(25, second.ReturnedCount);
        Assert.False(second.HasMore);
        Assert.Empty(first.Rows.Select(row => row.CompletedCycleId).Intersect(second.Rows.Select(row => row.CompletedCycleId)));
    }

    [Fact]
    public void Calibration_uses_and_reconciles_exact_server_qualified_evidence_identities()
    {
        var cycles = Enumerable.Range(1, 10)
            .Select(index => Cycle(index, "otte", "EXT", Utc(0, 0).AddHours(index), complete: 50, expected: 20))
            .ToList();
        var evidenceIds = cycles
            .Select(cycle => new ReportAuditEvidenceIdentity(cycle.CompletedCycleId, cycle.AcceptedReadyHandoffId))
            .ToList();
        var request = new ReportAuditRequest(
            ContributorKind: ReportAuditContributorKinds.CalibrationEvidence,
            BaseProcedureCode: "EXT",
            EvidenceIds: evidenceIds);
        var builder = CreateBuilder();

        var page = builder.BuildAudit(cycles, [], request);

        Assert.Equal(10, page.TotalMatchingCount);
        Assert.All(page.Rows, row =>
        {
            Assert.NotNull(row.CalibrationEvidence);
            Assert.Equal("CurrentRosterDefault", row.CalibrationEvidence.BaselineSource);
            Assert.Equal(30, row.CalibrationEvidence.BaselineMinutesUsed);
            Assert.Equal(3000d, row.CalibrationEvidence.ObservedCaseFlowSeconds);
            Assert.Equal(1200d, row.CalibrationEvidence.PairedVarianceSeconds);
        });
        var mismatch = request with
        {
            EvidenceIds = evidenceIds.Select((item, index) =>
                index == 0 ? item with { AcceptedReadyHandoffId = "wrong" } : item).ToList()
        };
        Assert.Throws<ReportAuditQueryException>(() => builder.BuildAudit(cycles, [], mismatch));
    }

    [Theory]
    [InlineData(ReportAuditSorts.MostRecent)]
    [InlineData(ReportAuditSorts.LongestReadyWait)]
    [InlineData(ReportAuditSorts.LongestDoctorTime)]
    [InlineData(ReportAuditSorts.LargestPositiveScheduleFitVariance)]
    [InlineData(ReportAuditSorts.LargestNegativeScheduleFitVariance)]
    [InlineData(ReportAuditSorts.Doctor)]
    [InlineData(ReportAuditSorts.Procedure)]
    public void Every_supported_sort_is_server_owned_and_membership_preserving(string sort)
    {
        var cycles = new[]
        {
            Cycle(1, "otte", "EXT", Utc(8, 0), ready: 5, arrived: 20, complete: 80, available: 90, expected: 30),
            Cycle(2, "pledger", "CON", Utc(9, 0), ready: 10, arrived: 10, complete: 20, available: 25, expected: 60),
            Cycle(3, "otte", "EXT", Utc(10, 0), ready: 5, arrived: 15, complete: 45, available: 55, expected: 30)
        };
        var page = CreateBuilder().BuildAudit(
            cycles, [],
            new ReportAuditRequest(
                ContributorKind: ReportAuditContributorKinds.PracticeCompletedCases,
                Sort: sort));

        Assert.Equal(sort, page.ActiveSort);
        Assert.Equal([1L, 2L, 3L], page.Rows.Select(row => row.CompletedCycleId).Order());
    }

    [Fact]
    public void Most_recent_and_metric_ties_use_descending_completed_cycle_identity()
    {
        var same = Utc(8, 0);
        var cycles = new[]
        {
            Cycle(1, "otte", "EXT", same),
            Cycle(2, "otte", "EXT", same),
            Cycle(3, "otte", "EXT", same)
        };
        var builder = CreateBuilder();

        var recent = builder.BuildAudit(
            cycles, [],
            new ReportAuditRequest(ContributorKind: ReportAuditContributorKinds.PracticeCompletedCases));
        var ready = builder.BuildAudit(
            cycles, [],
            new ReportAuditRequest(
                ContributorKind: ReportAuditContributorKinds.ReadyWait,
                Sort: ReportAuditSorts.LongestReadyWait));

        Assert.Equal([3L, 2L, 1L], recent.Rows.Select(row => row.CompletedCycleId));
        Assert.Equal([3L, 2L, 1L], ready.Rows.Select(row => row.CompletedCycleId));
    }

    private static ReportsSnapshotBuilder CreateBuilder()
    {
        Doctor[] doctors =
        [
            new("otte", "Dr. Otte", "LDO", "#dc2626"),
            new("pledger", "Dr. Pledger", "JWP", "#16a34a")
        ];
        ProcedureCategory[] procedures =
        [
            new("extraction", "EXT", "Extraction", "forceps", SedationEligible: true, AllocationBehavior: "Variable", DefaultExpectedUnits: 3),
            new("consult", "CON", "Consult", "message-circle", AllocationBehavior: "Known", DefaultExpectedUnits: 1)
        ];
        return new ReportsSnapshotBuilder(doctors, doctors, procedures, procedures);
    }

    private static CompletedRoomCycle Cycle(
        long id,
        string doctor,
        string procedure,
        DateTimeOffset seated,
        int ready = 5,
        int arrived = 15,
        int? complete = 45,
        int? available = 55,
        int expected = 30)
    {
        var readyAt = seated.AddMinutes(ready);
        var arrivedAt = seated.AddMinutes(arrived);
        DateTimeOffset? completeAt = complete.HasValue ? seated.AddMinutes(complete.Value) : null;
        DateTimeOffset? availableAt = available.HasValue ? seated.AddMinutes(available.Value) : null;
        return new CompletedRoomCycle
        {
            CompletedCycleId = id,
            EpisodeId = $"episode-{id}",
            AcceptedReadyHandoffId = $"handoff-{id}",
            RoomId = (int)((id - 1) % 12) + 1,
            AssignedDoctor = doctor,
            ProcedureCode = procedure,
            SeatedAt = seated,
            ReadyForDoctorAt = readyAt,
            DoctorArrivedAt = arrivedAt,
            DoctorCompleteAt = completeAt,
            RoomAvailableAt = availableAt,
            PrepSeconds = Seconds(seated, readyAt),
            SeatedToDoctorSeconds = Seconds(seated, arrivedAt),
            ReadyToDoctorSeconds = Seconds(readyAt, arrivedAt),
            DoctorInRoomSeconds = completeAt.HasValue ? Seconds(arrivedAt, completeAt.Value) : null,
            TurnoverSeconds = completeAt.HasValue && availableAt.HasValue ? Seconds(completeAt.Value, availableAt.Value) : null,
            TotalRoomCycleSeconds = availableAt.HasValue ? Seconds(seated, availableAt.Value) : null,
            OriginalDefaultExpectedUnits = expected / 10,
            ExpectedAllocationUnits = expected / 10,
            ExpectedAllocationMinutes = expected,
            FinalWaitState = RoomStates.ReadyForDoctor
        };
    }

    private static AbortedRoomAssignment Aborted(long id, DateTimeOffset terminatedAt, bool requiresReview) =>
        new()
        {
            AbortedAssignmentId = id,
            EpisodeId = $"aborted-{id}",
            RoomId = (int)((id - 1) % 12) + 1,
            AssignedDoctor = "pledger",
            ProcedureCode = "EXT+SED",
            SeatedAt = terminatedAt.AddMinutes(-20),
            ReadyForDoctorAt = terminatedAt.AddMinutes(-10),
            TerminatedAt = terminatedAt,
            TerminatedFromState = RoomStates.ReadyForDoctor,
            TerminationKind = ExceptionReasons.AfterHoursSweep,
            IsException = true,
            RequiresReview = requiresReview,
            ReviewStatus = requiresReview ? ReviewStatuses.PendingReview : ReviewStatuses.Reviewed,
            ReviewedAt = requiresReview ? null : terminatedAt.AddMinutes(5),
            ReviewedBy = requiresReview ? null : ExceptionReviewers.LocalAdmin,
            ExceptionReason = ExceptionReasons.AfterHoursSweep,
            SuggestedAction = "Review"
        };

    private static HistoricalReportingProjection Projection(
        string disposition,
        string doctor,
        string procedure,
        SedationState sedation,
        bool reviewed = false) =>
        new(
            disposition,
            doctor,
            procedure,
            sedation,
            HasExplicitSedationEvidence: true,
            PreserveLegacySedationTransport: false,
            EffectiveIsAddOn: false,
            ExpectedAllocationState.ConfirmedSuggestedValue,
            EffectiveExpectedAllocationSuggestedUnits: 3,
            EffectiveExpectedAllocationConfirmedUnits: 3,
            CurrentReason: HistoricalManualReviewReasons.OtherNeedsReview,
            ReasonSource: HistoricalAdministrativeReasonSources.LocalAdmin,
            KnownReviewedAt: reviewed ? Utc(16, 0) : null,
            KnownReviewedActorClass: reviewed ? HistoricalAdministrativeActorClasses.LocalAdmin : null,
            AdministrativeRevision: reviewed ? 2 : 1,
            HasHistoricalCorrectionProvenance: false,
            HasReviewedProvenance: reviewed);

    private static int Seconds(DateTimeOffset start, DateTimeOffset end) =>
        (int)Math.Round((end - start).TotalSeconds);

    private static DateTimeOffset Utc(int hour, int minute) =>
        new(2026, 8, 10, hour, minute, 0, TimeSpan.Zero);
}
