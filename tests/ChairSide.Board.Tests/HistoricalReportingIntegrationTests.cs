using System.Text.Json;

using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class HistoricalReportingIntegrationTests
{
    [Fact]
    public void Explicit_sedation_state_normalizes_contradictory_transport_suffixes()
    {
        var nonSedation = Projection(
            "EXT+SED",
            SedationState.EligibleNo,
            hasExplicitSedationEvidence: true,
            preserveLegacySedationTransport: false);
        var nonSedationCycle = new CompletedRoomCycle();
        nonSedation.ApplyTo(nonSedationCycle);
        Assert.Equal("EXT", nonSedationCycle.ProcedureCode);
        Assert.True(nonSedation.MatchesAnalyticalScope(
            ReportQuery.Default with { Sedation = ReportSedationSegments.NonSedation },
            requireExplicitSedation: false));
        Assert.False(nonSedation.MatchesAnalyticalScope(
            ReportQuery.Default with { Sedation = ReportSedationSegments.Sedation },
            requireExplicitSedation: false));

        var sedation = Projection(
            "EXT+SED",
            SedationState.EligibleYes,
            hasExplicitSedationEvidence: true,
            preserveLegacySedationTransport: false);
        var sedationCycle = new CompletedRoomCycle();
        sedation.ApplyTo(sedationCycle);
        Assert.Equal("EXT+SED", sedationCycle.ProcedureCode);
    }

    [Fact]
    public void Legacy_review_columns_without_canonical_projection_do_not_create_runtime_administrative_state()
    {
        var seatedAt = DateTimeOffset.Parse("2026-08-19T14:00:00Z");
        var completed = new CompletedRoomCycle
        {
            CompletedCycleId = 1,
            RoomId = 1,
            AssignedDoctor = "otte",
            ProcedureCode = "EXT+SED",
            IsAddOn = true,
            SeatedAt = seatedAt,
            ReadyForDoctorAt = seatedAt.AddMinutes(2),
            DoctorArrivedAt = seatedAt.AddMinutes(5),
            DoctorCompleteAt = seatedAt.AddMinutes(15),
            RoomAvailableAt = seatedAt.AddMinutes(18),
            OriginalDefaultExpectedUnits = 3,
            ExpectedAllocationUnits = 3,
            ExpectedAllocationMinutes = 30,
            IsException = true,
            RequiresReview = true,
            ExceptionReason = ExceptionReasons.ManualReview,
            ReviewStatus = ReviewStatuses.PendingReview
        };
        var reviewedAt = seatedAt.AddHours(1);
        var aborted = new AbortedRoomAssignment
        {
            AbortedAssignmentId = 1,
            EpisodeId = "legacy-reviewed-abort",
            RoomId = 2,
            AssignedDoctor = "pledger",
            ProcedureCode = "EXT",
            SedationState = SedationState.EligibleYes,
            ExpectedAllocationState = ExpectedAllocationState.ConfirmedAdjustedValue,
            ExpectedAllocationSuggestedUnits = 9,
            ExpectedAllocationConfirmedUnits = 4,
            OriginalDefaultExpectedUnits = 3,
            ExpectedAllocationUnits = 4,
            ExpectedAllocationMinutes = 40,
            TerminatedAt = seatedAt,
            IsException = true,
            RequiresReview = false,
            ExceptionReason = ExceptionReasons.AfterHoursSweep,
            ReviewStatus = ReviewStatuses.Reviewed,
            ReviewedAt = reviewedAt,
            ReviewedBy = ExceptionReviewers.LocalAdmin
        };

        var completedFallback = HistoricalReportingProjection.FromSource(completed);
        AssertNoRuntimeAdministrativeState(completedFallback);
        Assert.Equal("otte", completedFallback.EffectiveDoctorId);
        Assert.Equal("EXT+SED", completedFallback.EffectiveProcedureCode);
        Assert.True(completedFallback.EffectiveIsAddOn);
        Assert.True(completedFallback.IsSedationCaseForNormalReporting);
        Assert.Equal(3, completedFallback.EffectiveExpectedAllocationConfirmedUnits);

        var abortedFallback = HistoricalReportingProjection.FromSource(aborted);
        AssertNoRuntimeAdministrativeState(abortedFallback);
        Assert.Equal("pledger", abortedFallback.EffectiveDoctorId);
        Assert.Equal("EXT", abortedFallback.EffectiveProcedureCode);
        Assert.Equal(SedationState.EligibleYes, abortedFallback.EffectiveSedationState);
        Assert.Equal(9, abortedFallback.EffectiveExpectedAllocationSuggestedUnits);
        Assert.Equal(4, abortedFallback.EffectiveExpectedAllocationConfirmedUnits);

        Doctor[] doctors =
        [
            new("otte", "Dr. Otte", "LDO", "#dc2626"),
            new("pledger", "Dr. Pledger", "JWP", "#16a34a")
        ];
        ProcedureCategory[] procedures =
        [
            new("extraction", "EXT", "Extraction", "forceps", SedationEligible: true,
                AllocationBehavior: "Variable", DefaultExpectedUnits: 3)
        ];
        var report = new ReportsSnapshotBuilder(doctors, doctors, procedures, procedures)
            .Build([completed], [aborted], ReportQuery.Default);
        Assert.Equal(1, report.CompletedRoomCyclesCount);
        Assert.Equal(0, report.DataQuality!.NeedsReviewCount);
        Assert.Equal(0, report.DataQuality.ConfirmedExceptionCount);
        Assert.Empty(report.ExceptionReviewRecords!);
        var reported = Assert.Single(report.RecentCompletedCycles);
        Assert.False(reported.IsException);
        Assert.False(reported.RequiresReview);
        Assert.Equal(ReviewStatuses.PendingReview, reported.ReviewStatus);
        Assert.Null(reported.ExceptionReason);
        Assert.Null(reported.SuggestedAction);
        Assert.Null(reported.ReviewedAt);
        Assert.Null(reported.ReviewedBy);

        abortedFallback.ApplyTo(aborted);
        Assert.False(aborted.IsException);
        Assert.False(aborted.RequiresReview);
        Assert.Equal(ReviewStatuses.PendingReview, aborted.ReviewStatus);
        Assert.Null(aborted.ExceptionReason);
        Assert.Null(aborted.SuggestedAction);
        Assert.Null(aborted.ReviewedAt);
        Assert.Null(aborted.ReviewedBy);
        Assert.Equal(3, aborted.OriginalDefaultExpectedUnits);
        Assert.Equal(4, aborted.ExpectedAllocationUnits);
        Assert.Equal(40, aborted.ExpectedAllocationMinutes);
        Assert.True(aborted.AllocationAdjustedFromDefault);
    }

    [Fact]
    public void Canonical_gate_and_all_effective_fields_update_reports_audit_and_data_quality_immediately()
    {
        using var workspace = TestWorkspace.Create();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-20T14:00:00Z"));
        var procedureOptions = new ProcedureRosterOptions
        {
            Procedures = ProcedureRosterOptions.DefaultProcedures()
        };
        procedureOptions.Procedures.Single(row => row.Code == "IMP").DefaultExpectedUnits = 9;
        var context = StoreContext.Create(
            workspace,
            Environments.Production,
            timeProvider: clock,
            procedureRosterOptions: procedureOptions);
        var key = CompleteCycle(context, clock, "otte", "EXT", sedation: false, expectedUnits: 3);
        var administration = new HistoricalAnomalyAdministrationService(context.Repository, clock);
        var correction = CreateCorrectionService(context, procedureOptions);
        var sourceBefore = context.Repository.LoadHistoricalEncounter(key)!.CompletedCycle!;
        var handoffBefore = context.Repository.LoadReadyHandoff(sourceBefore.AcceptedReadyHandoffId!)!;
        var sourceJson = JsonSerializer.Serialize(sourceBefore);
        var handoffJson = JsonSerializer.Serialize(handoffBefore);

        using (var baseline = context.Store.GetReports())
        {
            Assert.Equal(1, baseline.CompletedRoomCyclesCount);
            Assert.Equal(1, baseline.IncludedCompletedCycleCount);
            Assert.Equal(0, baseline.DataQuality!.NeedsReviewCount);
        }

        AssertSuccess(administration.MarkForReview(
            key,
            0,
            HistoricalManualReviewReasons.IncorrectDoctor), 1);
        using (var pending = context.Store.GetReports())
        {
            Assert.Equal(0, pending.CompletedRoomCyclesCount);
            Assert.Equal(1, pending.DataQuality!.NeedsReviewCount);
        }

        AssertCorrectionSuccess(correction.CorrectDoctor(key, 1, "pledger"), 2);
        AssertCorrectionSuccess(correction.CorrectProcedure(key, 2, "IMP"), 3);
        AssertCorrectionSuccess(correction.CorrectSedation(key, 3, SedationState.EligibleYes), 4);
        AssertCorrectionSuccess(correction.CorrectAddOn(key, 4, true), 5);
        AssertCorrectionSuccess(correction.CorrectExpectedAllocation(
            key,
            5,
            ExpectedAllocationState.ConfirmedAdjustedValue,
            suggestedUnits: 9,
            confirmedUnits: 4), 6);

        var correctedScope = new ReportQuery(
            ReportDateRange.AllTime,
            ReportScopeKinds.Doctor,
            "pledger",
            ReportSedationSegments.Sedation,
            ReportProcedureGroupings.DetailedVariant);
        using (var correctedPending = context.Store.GetReports(correctedScope))
        {
            Assert.Equal(0, correctedPending.CompletedRoomCyclesCount);
            Assert.Equal(1, correctedPending.DataQuality!.NeedsReviewCount);
            Assert.Equal(1, correctedPending.DataQuality.HistoricalCorrectionCount);
        }
        using (var originalScope = context.Store.GetReports(correctedScope with { DoctorId = "otte" }))
        {
            Assert.Equal(0, originalScope.DataQuality!.NeedsReviewCount);
        }

        var pendingAudit = context.Store.QueryReportAudit(new ReportAuditRequest(
            Scope: ReportScopeKinds.Doctor,
            DoctorId: "pledger",
            Sedation: ReportSedationSegments.Sedation,
            ProcedureGrouping: ReportProcedureGroupings.DetailedVariant,
            ContributorKind: ReportAuditContributorKinds.PendingReview,
            ProcedureCode: "IMP+SED"));
        var pendingRow = Assert.Single(pendingAudit.ReviewRows);
        Assert.Equal("pledger", pendingRow.DoctorId);
        Assert.Equal("IMP+SED", pendingRow.ProcedureCode);
        Assert.Equal(HistoricalAdministrativeDispositions.NeedsReview, pendingRow.Disposition);
        Assert.True(pendingRow.HasHistoricalCorrection);

        AssertSuccess(administration.ClearForReporting(key, 6), 7);
        var ledgerReadsBeforeReports = context.Repository.HistoricalLedgerPageReadCount;
        var ledgerCountBeforeReports = context.Repository
            .LoadHistoricalAdministrativeLedger(key, 0, 100)
            .TotalMatchingCount;
        using (var cleared = context.Store.GetReports(correctedScope))
        {
            Assert.Equal(1, cleared.CompletedRoomCyclesCount);
            Assert.Equal(1, cleared.TotalCompletedCycleCount);
            Assert.Equal(1, cleared.SedationCaseCount);
            Assert.Equal(0, cleared.NonSedationCaseCount);
            Assert.Equal(60, cleared.MedianReadyToDoctorSeconds);
            Assert.Equal(1, cleared.DataQuality!.ClearedAnomalyCount);
            Assert.Equal(1, cleared.DataQuality.HistoricalCorrectionCount);

            var effective = Assert.Single(cleared.RecentCompletedCycles);
            Assert.Equal("pledger", effective.AssignedDoctor);
            Assert.Equal("IMP+SED", effective.ProcedureCode);
            Assert.True(effective.IsAddOn);
            Assert.Equal(3, effective.OriginalDefaultExpectedUnits);
            Assert.Equal(4, effective.ExpectedAllocationUnits);
            Assert.Equal(40, effective.ExpectedAllocationMinutes);
            Assert.True(effective.AllocationAdjustedFromDefault);

            var procedure = Assert.Single(cleared.ProcedureSummaries);
            Assert.Equal("IMP+SED", procedure.ProcedureCode);
            Assert.Equal("pledger", Assert.Single(cleared.DoctorSummaries).AssignedDoctor);
            Assert.All(cleared.DoctorFlowSummaries!, row => Assert.Equal("pledger", row.DoctorId));
            Assert.Equal("IMP+SED", Assert.Single(cleared.ProcedureIntelligenceRows!).ProcedureCode);
            Assert.Equal(40, Assert.IsType<AllocationVarianceSummary>(
                cleared.AllocationVariance).TotalExpectedAllocationMinutes);
            var scheduleFit = Assert.IsType<ScheduleFitReport>(cleared.ScheduleFit);
            Assert.Equal(2_400, Assert.IsType<ScheduleFitSummary>(scheduleFit.Practice).TotalExpectedSeconds);
            var segment = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ScheduleFitSegment>>(
                scheduleFit.ProcedureSegments));
            Assert.Equal("IMP+SED", segment.ProcedureCode);
            Assert.Equal(90, segment.CurrentDefaultAllocationMinutes);
            Assert.Equal(2_400, segment.HistoricalAssignedFit.TotalExpectedSeconds);
        }
        using (var originalDoctor = context.Store.GetReports(correctedScope with { DoctorId = "otte" }))
        {
            Assert.Equal(0, originalDoctor.CompletedRoomCyclesCount);
            Assert.Equal(0, originalDoctor.TotalCompletedCycleCount);
        }

        var audit = context.Store.QueryReportAudit(new ReportAuditRequest(
            Scope: ReportScopeKinds.Doctor,
            DoctorId: "pledger",
            Sedation: ReportSedationSegments.Sedation,
            ProcedureGrouping: ReportProcedureGroupings.DetailedVariant,
            ContributorKind: ReportAuditContributorKinds.ProcedureMix,
            ProcedureCode: "IMP+SED"));
        var auditRow = Assert.Single(audit.Rows);
        Assert.Equal("pledger", auditRow.DoctorId);
        Assert.Equal("IMP+SED", auditRow.ProcedureCode);
        Assert.True(auditRow.IsSedationCase);
        Assert.True(auditRow.IsAddOn);
        Assert.Equal(3, auditRow.OriginalDefaultExpectedUnits);
        Assert.Equal(4, auditRow.ExpectedAllocationUnits);
        Assert.Equal(40, auditRow.ExpectedAllocationMinutes);
        Assert.Equal(-1_680, auditRow.ExactScheduleFitVarianceSeconds);

        Assert.Equal(
            ledgerCountBeforeReports,
            context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 100).TotalMatchingCount);
        Assert.Equal(ledgerReadsBeforeReports + 2, context.Repository.HistoricalLedgerPageReadCount);
        var sourceAfter = context.Repository.LoadHistoricalEncounter(key)!.CompletedCycle!;
        Assert.Equal(3, sourceAfter.OriginalDefaultExpectedUnits);
        Assert.Equal(3, sourceAfter.ExpectedAllocationUnits);
        Assert.Equal(30, sourceAfter.ExpectedAllocationMinutes);
        Assert.Equal(sourceJson, JsonSerializer.Serialize(sourceAfter));
        var handoffAfter = context.Repository.LoadReadyHandoff(sourceBefore.AcceptedReadyHandoffId!)!;
        Assert.Equal(ExpectedAllocationState.ConfirmedSuggestedValue, handoffAfter.Assignment.ExpectedAllocationState);
        Assert.Equal(3, handoffAfter.Assignment.ExpectedAllocationSuggestedUnits);
        Assert.Equal(3, handoffAfter.Assignment.ExpectedAllocationConfirmedUnits);
        Assert.Equal(handoffJson, JsonSerializer.Serialize(handoffAfter));

        AssertSuccess(administration.ReopenReview(key, 7), 8);
        using (var reopened = context.Store.GetReports(correctedScope))
        {
            Assert.Equal(0, reopened.CompletedRoomCyclesCount);
            Assert.Equal(1, reopened.DataQuality!.NeedsReviewCount);
        }
        AssertSuccess(administration.ConfirmException(key, 8), 9);
        using (var confirmed = context.Store.GetReports(correctedScope))
        {
            Assert.Equal(0, confirmed.CompletedRoomCyclesCount);
            Assert.Equal(1, confirmed.DataQuality!.ConfirmedExceptionCount);
            Assert.Equal(1, confirmed.DataQuality.ReviewedProvenanceCount);
        }
    }

    [Fact]
    public void Effective_procedure_reannotations_remain_separate_and_correction_provenance_survives_restore()
    {
        using var workspace = TestWorkspace.Create();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-21T14:00:00Z"));
        var context = StoreContext.Create(workspace, Environments.Production, timeProvider: clock);
        var key = CompleteCycle(context, clock, "otte", "CON", sedation: false, expectedUnits: 1);
        var administration = new HistoricalAnomalyAdministrationService(context.Repository, clock);
        var correction = CreateCorrectionService(context);
        AssertSuccess(administration.MarkForReview(
            key,
            0,
            HistoricalManualReviewReasons.IncorrectProcedure), 1);
        AssertCorrectionSuccess(correction.CorrectProcedure(key, 1, "SED"), 2);
        AssertSuccess(administration.ClearForReporting(key, 2), 3);

        using (var legacyEffective = context.Store.GetReports(new ReportQuery(
            ReportDateRange.AllTime,
            ReportScopeKinds.Practice,
            null,
            ReportSedationSegments.NonSedation,
            ReportProcedureGroupings.DetailedVariant)))
        {
            Assert.Equal(1, legacyEffective.CompletedRoomCyclesCount);
            Assert.Equal(0, legacyEffective.IncludedCompletedCycleCount);
            var cycle = Assert.Single(legacyEffective.RecentCompletedCycles);
            Assert.Equal("SED", cycle.ProcedureCode);
            Assert.True(cycle.IsLegacyProcedure);
            Assert.Contains(ReportingExceptionReasons.LegacyProcedure, cycle.ReportingExceptionReasons);
            Assert.Equal(0, legacyEffective.DataQuality!.NeedsReviewCount);
            Assert.Equal(1, legacyEffective.DataQuality.ClearedAnomalyCount);
            Assert.Equal(1, legacyEffective.DataQuality.HistoricalCorrectionCount);
        }

        AssertSuccess(administration.ReopenReview(key, 3), 4);
        AssertCorrectionSuccess(correction.CorrectProcedure(key, 4, "CON"), 5);
        AssertSuccess(administration.ClearForReporting(key, 5), 6);

        using var restored = context.Store.GetReports();
        Assert.Equal(1, restored.IncludedCompletedCycleCount);
        var restoredCycle = Assert.Single(restored.RecentCompletedCycles);
        Assert.Equal("CON", restoredCycle.ProcedureCode);
        Assert.False(restoredCycle.IsLegacyProcedure);
        Assert.DoesNotContain(ReportingExceptionReasons.LegacyProcedure, restoredCycle.ReportingExceptionReasons);
        Assert.Equal(1, restored.DataQuality!.ClearedAnomalyCount);
        Assert.Equal(1, restored.DataQuality.HistoricalCorrectionCount);
        Assert.Equal("CON", context.Repository.LoadHistoricalEncounter(key)!.CompletedCycle!.ProcedureCode);
    }

    [Fact]
    public void Legacy_sedation_transport_is_normal_only_and_unknown_sedation_matches_no_scoped_review_segment()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var cycle = SaveLegacyCycle(context, DateTimeOffset.Parse("2026-08-22T14:00:00Z"), "EXT+SED");
        var key = new HistoricalEncounterKey(
            HistoricalEncounterSourceTypes.CompletedCycle,
            cycle.CompletedCycleId);
        var sedationQuery = new ReportQuery(
            ReportDateRange.AllTime,
            ReportScopeKinds.Practice,
            null,
            ReportSedationSegments.Sedation,
            ReportProcedureGroupings.DetailedVariant);
        var nonSedationQuery = sedationQuery with { Sedation = ReportSedationSegments.NonSedation };

        using (var legacyNormal = context.Store.GetReports(sedationQuery))
        {
            Assert.Equal(1, legacyNormal.CompletedRoomCyclesCount);
            Assert.Equal(1, legacyNormal.SedationCaseCount);
        }
        using (var legacyNonSedation = context.Store.GetReports(nonSedationQuery))
        {
            Assert.Equal(0, legacyNonSedation.CompletedRoomCyclesCount);
        }

        var administration = new HistoricalAnomalyAdministrationService(context.Repository);
        AssertSuccess(administration.MarkForReview(
            key,
            0,
            HistoricalManualReviewReasons.OtherNeedsReview), 1);
        using (var sedationReview = context.Store.GetReports(sedationQuery))
        using (var nonSedationReview = context.Store.GetReports(nonSedationQuery))
        using (var allReview = context.Store.GetReports())
        {
            Assert.Equal(0, sedationReview.DataQuality!.NeedsReviewCount);
            Assert.Equal(0, nonSedationReview.DataQuality!.NeedsReviewCount);
            Assert.Equal(1, allReview.DataQuality!.NeedsReviewCount);
        }
        Assert.Empty(context.Store.QueryReportAudit(new ReportAuditRequest(
            Sedation: ReportSedationSegments.Sedation,
            ContributorKind: ReportAuditContributorKinds.PendingReview)).ReviewRows);
        Assert.Empty(context.Store.QueryReportAudit(new ReportAuditRequest(
            Sedation: ReportSedationSegments.NonSedation,
            ContributorKind: ReportAuditContributorKinds.PendingReview)).ReviewRows);

        AssertSuccess(administration.ClearForReporting(key, 1), 2);
        using var clearedLegacyNormal = context.Store.GetReports(sedationQuery);
        Assert.Equal(1, clearedLegacyNormal.CompletedRoomCyclesCount);
        Assert.Equal(0, clearedLegacyNormal.DataQuality!.ClearedAnomalyCount);
        using var clearedAll = context.Store.GetReports();
        Assert.Equal(1, clearedAll.DataQuality!.ClearedAnomalyCount);
    }

    [Fact]
    public void Cleared_aborted_assignment_stays_out_of_throughput_and_review_rows_respect_effective_scope_and_date()
    {
        using var workspace = TestWorkspace.Create();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-23T14:00:00Z"));
        var context = StoreContext.Create(workspace, Environments.Production, timeProvider: clock);
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "EXT"));
        clock.SetUtcNow(DateTimeOffset.Parse("2026-08-23T14:05:00Z"));
        Assert.NotNull(context.Store.CancelPrestage(1));
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        var key = new HistoricalEncounterKey(
            HistoricalEncounterSourceTypes.AbortedAssignment,
            aborted.AbortedAssignmentId);
        var administration = new HistoricalAnomalyAdministrationService(context.Repository, clock);
        var correction = CreateCorrectionService(context);
        AssertSuccess(administration.MarkForReview(
            key,
            0,
            HistoricalManualReviewReasons.IncorrectDoctor), 1);
        AssertCorrectionSuccess(correction.CorrectDoctor(key, 1, "pledger"), 2);

        var request = new ReportAuditRequest(
            From: "2026-08-23",
            To: "2026-08-23",
            Scope: ReportScopeKinds.Doctor,
            DoctorId: "pledger",
            Sedation: ReportSedationSegments.NonSedation,
            ProcedureGrouping: ReportProcedureGroupings.DetailedVariant,
            ContributorKind: ReportAuditContributorKinds.PendingReview,
            ProcedureCode: "EXT");
        var pending = context.Store.QueryReportAudit(request);
        Assert.Single(pending.ReviewRows);
        Assert.Empty(context.Store.QueryReportAudit(request with { DoctorId = "otte" }).ReviewRows);
        Assert.Empty(context.Store.QueryReportAudit(request with
        {
            From = "2026-08-22",
            To = "2026-08-22"
        }).ReviewRows);

        AssertSuccess(administration.ClearForReporting(key, 2), 3);
        var query = ReportQuery.FromStrings(
            "2026-08-23",
            "2026-08-23",
            ReportScopeKinds.Doctor,
            "pledger",
            ReportSedationSegments.NonSedation,
            ReportProcedureGroupings.DetailedVariant);
        using var reports = context.Store.GetReports(query);
        Assert.Equal(0, reports.CompletedRoomCyclesCount);
        Assert.Equal(0, reports.TotalCompletedCycleCount);
        Assert.Equal(1, reports.DataQuality!.ClearedAnomalyCount);
        Assert.Empty(reports.ExceptionReviewRecords!);

        var reviewed = context.Store.QueryReportAudit(request with
        {
            ContributorKind = ReportAuditContributorKinds.ReviewedExceptionHistory
        });
        var reviewedRow = Assert.Single(reviewed.ReviewRows);
        Assert.Equal("pledger", reviewedRow.DoctorId);
        Assert.Equal(HistoricalAdministrativeDispositions.ClearedForReporting, reviewedRow.Disposition);
        Assert.True(reviewedRow.HasReviewedProvenance);
    }

    private static HistoricalEncounterKey CompleteCycle(
        StoreContext context,
        ManualTimeProvider clock,
        string doctor,
        string procedure,
        bool sedation,
        int expectedUnits)
    {
        var started = clock.GetUtcNow();
        Assert.NotNull(context.Store.BeginPrestage(
            1,
            doctor,
            procedure,
            sedation,
            expectedAllocationUnits: expectedUnits));
        clock.SetUtcNow(started.AddMinutes(1));
        Assert.NotNull(context.Store.SeatRoomCanonical(1, null).Room);
        clock.SetUtcNow(started.AddMinutes(2));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(started.AddMinutes(3));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        clock.SetUtcNow(started.AddMinutes(13));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(started.AddMinutes(15));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));
        var cycle = Assert.Single(context.Repository.LoadCompletedCycles());
        return new HistoricalEncounterKey(
            HistoricalEncounterSourceTypes.CompletedCycle,
            cycle.CompletedCycleId);
    }

    private static CompletedRoomCycle SaveLegacyCycle(
        StoreContext context,
        DateTimeOffset seatedAt,
        string procedureCode)
    {
        var cycle = new CompletedRoomCycle
        {
            RoomId = 1,
            AssignedDoctor = "otte",
            ProcedureCode = procedureCode,
            SeatedAt = seatedAt,
            ReadyForDoctorAt = seatedAt.AddMinutes(2),
            DoctorArrivedAt = seatedAt.AddMinutes(5),
            DoctorCompleteAt = seatedAt.AddMinutes(15),
            RoomAvailableAt = seatedAt.AddMinutes(18),
            PrepSeconds = 120,
            SeatedToDoctorSeconds = 300,
            ReadyToDoctorSeconds = 180,
            DoctorInRoomSeconds = 600,
            TurnoverSeconds = 180,
            TotalRoomCycleSeconds = 1_080,
            OriginalDefaultExpectedUnits = 3,
            ExpectedAllocationUnits = 3,
            ExpectedAllocationMinutes = 30,
            FinalWaitState = RoomStates.ReadyForDoctor
        };
        context.Repository.SaveCompletedCycle(cycle, context.Doctors, context.Procedures);
        return cycle;
    }

    private static HistoricalMetadataCorrectionService CreateCorrectionService(
        StoreContext context,
        ProcedureRosterOptions? procedureOptions = null) =>
        new(
            context.Repository,
            Microsoft.Extensions.Options.Options.Create(new DoctorRosterOptions
            {
                Doctors = DoctorRosterOptions.DefaultDoctors()
            }),
            Microsoft.Extensions.Options.Options.Create(procedureOptions ?? new ProcedureRosterOptions
            {
                Procedures = ProcedureRosterOptions.DefaultProcedures()
            }));

    private static HistoricalReportingProjection Projection(
        string procedureCode,
        SedationState? sedationState,
        bool hasExplicitSedationEvidence,
        bool preserveLegacySedationTransport) =>
        new(
            HistoricalAdministrativeDispositions.NoAnomaly,
            "otte",
            procedureCode,
            sedationState,
            hasExplicitSedationEvidence,
            preserveLegacySedationTransport,
            EffectiveIsAddOn: false,
            EffectiveExpectedAllocationState: null,
            EffectiveExpectedAllocationSuggestedUnits: null,
            EffectiveExpectedAllocationConfirmedUnits: null,
            CurrentReason: null,
            ReasonSource: null,
            KnownReviewedAt: null,
            KnownReviewedActorClass: null,
            AdministrativeRevision: 0,
            HasHistoricalCorrectionProvenance: false,
            HasReviewedProvenance: false);

    private static void AssertSuccess(
        HistoricalAdministrativeOperationResult result,
        int expectedRevision)
    {
        Assert.Equal(HistoricalAdministrativeOperationOutcome.Success, result.Outcome);
        Assert.Equal(expectedRevision, result.CurrentRevision);
    }

    private static void AssertCorrectionSuccess(
        HistoricalMetadataCorrectionResult result,
        int expectedRevision)
    {
        Assert.Equal(HistoricalMetadataCorrectionOutcome.Success, result.Outcome);
        Assert.Equal(expectedRevision, result.CurrentRevision);
    }

    private static void AssertNoRuntimeAdministrativeState(HistoricalReportingProjection projection)
    {
        Assert.Equal(HistoricalAdministrativeDispositions.NoAnomaly, projection.Disposition);
        Assert.Null(projection.CurrentReason);
        Assert.Null(projection.ReasonSource);
        Assert.Null(projection.KnownReviewedAt);
        Assert.Null(projection.KnownReviewedActorClass);
        Assert.Equal(0, projection.AdministrativeRevision);
        Assert.False(projection.HasHistoricalCorrectionProvenance);
        Assert.False(projection.HasReviewedProvenance);
    }
}
