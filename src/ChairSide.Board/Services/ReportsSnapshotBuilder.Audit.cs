namespace ChairSide.Board.Services;

internal sealed partial class ReportsSnapshotBuilder
{
    private const int DefaultAuditLimit = 50;
    private const int MaximumAuditLimit = 100;

    internal ReportAuditPage BuildAudit(
        IReadOnlyList<CompletedRoomCycle> completedCycles,
        IReadOnlyList<AbortedRoomAssignment> abortedAssignments,
        ReportAuditRequest request)
    {
        using var spoolScope = ReportSpoolScope.Begin();
        ArgumentNullException.ThrowIfNull(completedCycles);
        ArgumentNullException.ThrowIfNull(abortedAssignments);
        ArgumentNullException.ThrowIfNull(request);

        var query = ReportQuery.FromStrings(
            request.From,
            request.To,
            request.Scope,
            request.DoctorId,
            request.Sedation,
            request.ProcedureGrouping);
        var contributorKind = NormalizeContributorKind(request.ContributorKind);
        var standing = NormalizeStanding(request.AnalyticalStanding);
        var evidenceIds = (request.EvidenceIds ?? [])
            .Where(item => item.CompletedCycleId > 0)
            .DistinctBy(item => item.CompletedCycleId)
            .ToList();
        var offset = Math.Max(0, request.Offset);
        var limit = request.Limit <= 0
            ? DefaultAuditLimit
            : Math.Min(request.Limit, MaximumAuditLimit);
        var isReview = contributorKind is ReportAuditContributorKinds.PendingReview
            or ReportAuditContributorKinds.ReviewedExceptionHistory
            or ReportAuditContributorKinds.AnomalyReview;
        var anomalyStatus = NormalizeAnomalyStatus(request.AnomalyStatus);
        var supportedSorts = isReview ? ReportAuditSorts.Review : ReportAuditSorts.All;
        var sort = supportedSorts.Contains(request.Sort, StringComparer.OrdinalIgnoreCase)
            ? supportedSorts.First(item => string.Equals(item, request.Sort, StringComparison.OrdinalIgnoreCase))
            : ReportAuditSorts.MostRecent;
        var selection = new ReportAuditSelection(
            query.ToContext(),
            contributorKind,
            NormalizeOptional(request.SegmentDoctorId),
            NormalizeOptional(request.ProcedureCode),
            NormalizeOptional(request.BaseProcedureCode),
            standing,
            evidenceIds,
            anomalyStatus);

        if (isReview)
        {
            return BuildReviewAuditPage(
                completedCycles,
                abortedAssignments,
                selection,
                query,
                sort,
                offset,
                limit);
        }

        var selected = BoundedReportCollections.Materialize(CreateAnnotatedCompletedCycleSnapshot(completedCycles)
            .Where(cycle => query.Window.Includes(cycle.DoctorCompleteAt))
            .Where(cycle => cycle.ReportingProjection?.IsAdministrativelyExcluded != true)
            .Where(query.IncludesAnalyticalCycle)
            .Where(cycle => MatchesSegmentDoctor(cycle, selection.SegmentDoctorId)));
        var standard = BoundedReportCollections.Materialize(
            selected.Where(cycle => !cycle.IsExcludedFromStandardMetrics));
        var population = SelectContributorPopulation(selected, standard, selection);
        var mode = contributorKind is ReportAuditContributorKinds.PracticeCompletedCases
            or ReportAuditContributorKinds.IncludedCompletedCases
            ? ReportAuditModes.CompletedCaseAudit
            : ReportAuditModes.MetricEvidence;

        IReadOnlyList<CalibrationEvidenceCase> calibrationEvidence = [];
        if (contributorKind == ReportAuditContributorKinds.CalibrationEvidence)
        {
            calibrationEvidence = ResolveCalibrationEvidence(population, selection);
            population = SelectCalibrationEvidencePopulation(population, calibrationEvidence);
        }

        population = BoundedReportCollections.Materialize(ApplyStanding(population, standing));
        var retainedLimit = offset > int.MaxValue - limit ? int.MaxValue : offset + limit;
        var rows = new List<ReportAuditRow>(Math.Min(retainedLimit, MaximumAuditLimit));
        var totalMatchingCount = 0;
        using var calibrationEnumerator = calibrationEvidence.GetEnumerator();
        var hasCalibrationEvidence = calibrationEnumerator.MoveNext();
        foreach (var cycle in population)
        {
            while (hasCalibrationEvidence
                && calibrationEnumerator.Current.CompletedCycleId < cycle.CompletedCycleId)
            {
                hasCalibrationEvidence = calibrationEnumerator.MoveNext();
            }
            var matchingCalibrationEvidence = hasCalibrationEvidence
                && calibrationEnumerator.Current.CompletedCycleId == cycle.CompletedCycleId
                    ? calibrationEnumerator.Current
                    : null;
            totalMatchingCount++;
            rows.Add(ToAuditRow(
                cycle,
                mode,
                matchingCalibrationEvidence));
            if (rows.Count > retainedLimit)
            {
                rows = OrderAuditRows(rows, sort).Take(retainedLimit).ToList();
            }
        }
        var ordered = OrderAuditRows(rows, sort).ToList();
        var pageRows = ordered.Skip(offset).Take(limit).ToList();

        return new ReportAuditPage(
            selection,
            mode,
            pageRows,
            [],
            pageRows.Count,
            totalMatchingCount,
            offset,
            limit,
            offset + pageRows.Count < totalMatchingCount,
            sort,
            supportedSorts);
    }

    internal ReportDataQualitySummary BuildDataQualitySummary(
        IReadOnlyList<CompletedRoomCycle> normalCompletedCycles,
        IReadOnlyList<CompletedRoomCycle> standardCompletedCycles,
        IReadOnlyList<CompletedRoomCycle> allCompletedCycles,
        IReadOnlyList<AbortedRoomAssignment> abortedAssignments,
        ReportQuery query)
    {
        var reviewRows = BuildReviewRows(allCompletedCycles, abortedAssignments, query);
        var needsReview = reviewRows.Count(row =>
            row.Disposition == HistoricalAdministrativeDispositions.NeedsReview);
        var cleared = reviewRows.Count(row =>
            row.Disposition == HistoricalAdministrativeDispositions.ClearedForReporting);
        var confirmed = reviewRows.Count(row =>
            row.Disposition == HistoricalAdministrativeDispositions.ConfirmedException);
        var corrected = reviewRows.Count(row => row.HasHistoricalCorrection);
        var reviewed = reviewRows.Count(row => row.HasReviewedProvenance);
        return new ReportDataQualitySummary(
            normalCompletedCycles.Count,
            standardCompletedCycles.Count,
            normalCompletedCycles.Count - standardCompletedCycles.Count,
            needsReview,
            reviewed,
            BuildDataQualityReasonCounts(normalCompletedCycles),
            query.Scope == ReportScopeKinds.Doctor
                ? $"Doctor {query.DoctorId ?? "not selected"}; {query.Sedation}"
                : $"Practice; {query.Sedation}",
            query.Window.Label,
            needsReview,
            cleared,
            confirmed,
            corrected,
            reviewed);
    }

    internal static DateTimeOffset? ReviewAnchor(CompletedRoomCycle cycle) =>
        cycle.DoctorCompleteAt
        ?? cycle.DoctorArrivedAt
        ?? (cycle.SeatedAt == default ? cycle.PrestageStartedAt : cycle.SeatedAt);

    private ReportAuditPage BuildReviewAuditPage(
        IReadOnlyList<CompletedRoomCycle> completedCycles,
        IReadOnlyList<AbortedRoomAssignment> abortedAssignments,
        ReportAuditSelection selection,
        ReportQuery query,
        string sort,
        int offset,
        int limit)
    {
        var compatibilityPending = selection.ContributorKind == ReportAuditContributorKinds.PendingReview;
        var compatibilityReviewed = selection.ContributorKind == ReportAuditContributorKinds.ReviewedExceptionHistory;
        var rows = BuildReviewRows(completedCycles, abortedAssignments, query)
            .Where(row => compatibilityPending
                ? row.Disposition == HistoricalAdministrativeDispositions.NeedsReview
                : compatibilityReviewed
                    ? row.HasReviewedProvenance
                    : MatchesAnomalyStatus(row.Disposition, selection.AnomalyStatus))
            .Where(row => MatchesReviewProcedure(row, selection))
            .ToList();
        var ordered = OrderReviewRows(rows, sort).ToList();
        var pageRows = ordered.Skip(offset).Take(limit).ToList();
        return new ReportAuditPage(
            selection,
            ReportAuditModes.ExceptionReview,
            [],
            pageRows,
            pageRows.Count,
            ordered.Count,
            offset,
            limit,
            offset + pageRows.Count < ordered.Count,
            sort,
            ReportAuditSorts.Review);
    }

    private IReadOnlyList<ReportReviewAuditRow> BuildReviewRows(
        IReadOnlyList<CompletedRoomCycle> completedCycles,
        IReadOnlyList<AbortedRoomAssignment> abortedAssignments,
        ReportQuery query)
    {
        var completed = completedCycles
            .Select(CreateEffectiveCompletedCycleCopy)
            .Where(cycle => query.Window.Includes(ReviewAnchor(cycle)))
            .Where(cycle => cycle.ReportingProjection is { } projection
                && (projection.IsAnomaly
                    || projection.HasHistoricalCorrectionProvenance
                    || projection.HasReviewedProvenance)
                && query.IncludesReviewEncounter(projection))
            .Select(cycle => new ReportReviewAuditRow(
                ExceptionReviewSources.CompletedCycle,
                cycle.CompletedCycleId,
                cycle.CompletedCycleId,
                0,
                cycle.EpisodeId,
                ReviewAnchor(cycle),
                cycle.RoomId,
                cycle.AssignedDoctor,
                ResolveDoctorName(cycle.AssignedDoctor),
                cycle.ProcedureCode,
                ResolveProcedureLabel(cycle.ProcedureCode),
                cycle.PrestageStartedAt,
                cycle.SeatedAt,
                cycle.ReadyForDoctorAt,
                cycle.DoctorArrivedAt,
                cycle.DoctorCompleteAt,
                cycle.RoomAvailableAt,
                null,
                cycle.FinalWaitState,
                cycle.ExceptionReason,
                cycle.SuggestedAction,
                cycle.ReviewStatus,
                cycle.ReviewedAt,
                cycle.ReviewedBy,
                cycle.RequiresReview,
                cycle.ReportingProjection!.Disposition,
                cycle.ReportingProjection.HasHistoricalCorrectionProvenance,
                cycle.ReportingProjection.HasReviewedProvenance,
                cycle.ReportingProjection.AdministrativeRevision));
        var aborted = abortedAssignments
            .Select(CopyAbortedAssignment)
            .Where(record => query.Window.Includes(record.TerminatedAt))
            .Where(record => record.ReportingProjection is { } projection
                && (projection.IsAnomaly
                    || projection.HasHistoricalCorrectionProvenance
                    || projection.HasReviewedProvenance)
                && query.IncludesReviewEncounter(projection))
            .Select(record => new ReportReviewAuditRow(
                ExceptionReviewSources.AbortedAssignment,
                record.AbortedAssignmentId,
                0,
                record.AbortedAssignmentId,
                record.EpisodeId,
                record.TerminatedAt,
                record.RoomId,
                record.AssignedDoctor,
                ResolveDoctorName(record.AssignedDoctor),
                record.ProcedureCode,
                ResolveProcedureLabel(record.ProcedureCode ?? ""),
                record.PrestageStartedAt,
                record.SeatedAt,
                record.ReadyForDoctorAt,
                null,
                null,
                null,
                record.TerminatedAt,
                record.TerminatedFromState,
                record.ExceptionReason,
                record.SuggestedAction,
                record.ReviewStatus,
                record.ReviewedAt,
                record.ReviewedBy,
                record.RequiresReview,
                record.ReportingProjection!.Disposition,
                record.ReportingProjection.HasHistoricalCorrectionProvenance,
                record.ReportingProjection.HasReviewedProvenance,
                record.ReportingProjection.AdministrativeRevision));
        return BoundedReportCollections.Materialize(completed.Concat(aborted));
    }

    private static IReadOnlyList<ReportDataQualityReasonCount> BuildDataQualityReasonCounts(
        IReadOnlyList<CompletedRoomCycle> cycles)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var reason in cycles.SelectMany(cycle => cycle.ReportingExceptionReasons))
        {
            counts[reason] = counts.GetValueOrDefault(reason) + 1;
        }
        return counts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ReportDataQualityReasonCount(pair.Key, pair.Value))
            .ToList();
    }

    private IReadOnlyList<CompletedRoomCycle> SelectContributorPopulation(
        IReadOnlyList<CompletedRoomCycle> selected,
        IReadOnlyList<CompletedRoomCycle> standard,
        ReportAuditSelection selection)
    {
        IEnumerable<CompletedRoomCycle> population = selection.ContributorKind switch
        {
            ReportAuditContributorKinds.PracticeCompletedCases =>
                selected.Where(cycle => cycle.RoomAvailableAt.HasValue),
            ReportAuditContributorKinds.IncludedCompletedCases =>
                standard.Where(cycle => cycle.RoomAvailableAt.HasValue),
            ReportAuditContributorKinds.ReadyWait => standard.Where(HasReadyWait),
            ReportAuditContributorKinds.SeatedToDoctor => standard.Where(HasSeatedToDoctor),
            ReportAuditContributorKinds.DoctorTime => standard.Where(HasDoctorTime),
            ReportAuditContributorKinds.Turnover => standard.Where(HasTurnover),
            ReportAuditContributorKinds.ProcedureMix =>
                standard.Where(cycle => cycle.RoomAvailableAt.HasValue),
            ReportAuditContributorKinds.ProcedureIntelligenceReadyWait =>
                standard.Where(cycle => cycle.RoomAvailableAt.HasValue)
                    .Where(cycle => TruthfulReadyWaitSeconds(cycle).HasValue),
            ReportAuditContributorKinds.ProcedureIntelligenceDoctorTime =>
                standard.Where(cycle => cycle.RoomAvailableAt.HasValue)
                    .Where(cycle => TruthfulDoctorTimeSeconds(cycle).HasValue),
            ReportAuditContributorKinds.ProcedureIntelligenceSeatedToDoctorComplete =>
                standard.Where(cycle => cycle.RoomAvailableAt.HasValue)
                    .Where(cycle => TruthfulSeatedToDoctorCompleteSeconds(cycle).HasValue),
            ReportAuditContributorKinds.HistoricalScheduleFit => standard.Where(HasHistoricalScheduleFit),
            ReportAuditContributorKinds.CalibrationEvidence => standard.Where(HasObservedScheduleFit),
            _ => throw new ReportAuditQueryException($"Unsupported contributorKind '{selection.ContributorKind}'.")
        };

        return BoundedReportCollections.Materialize(
            population.Where(cycle => MatchesProcedure(cycle, selection)));
    }

    private IReadOnlyList<CalibrationEvidenceCase> ResolveCalibrationEvidence(
        IReadOnlyList<CompletedRoomCycle> population,
        ReportAuditSelection selection)
    {
        if (selection.EvidenceIds.Count == 0)
        {
            throw new ReportAuditQueryException("CalibrationEvidence requires evidenceIds.");
        }

        var distinctBaseCodes = population
            .Select(cycle => ResolveBaseProcedureCode(cycle.ProcedureCode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        var baseCode = selection.BaseProcedureCode
            ?? (distinctBaseCodes.Count == 1
                ? distinctBaseCodes[0]
                : throw new ReportAuditQueryException("CalibrationEvidence requires one explicit baseProcedureCode."));
        var rosterProcedure = baseCode is null ? null : FindActiveProcedure(baseCode);
        var defaultMinutes = rosterProcedure?.DefaultExpectedUnits is > 0
            ? rosterProcedure.DefaultExpectedUnits * 10
            : (int?)null;
        var evaluation = ExactScheduleFitCalculator.EvaluateCurrentDefault(
            population,
            defaultMinutes,
            CalibrationRuleSet.VersionOne);
        var evidence = evaluation.Insight?.Evidence
            ?? throw new ReportAuditQueryException("The selected population no longer has a qualified Calibration insight.");
        var expected = selection.EvidenceIds.OrderBy(item => item.CompletedCycleId);
        using var expectedEnumerator = expected.GetEnumerator();
        using var actualEnumerator = evidence.GetEnumerator();
        var mismatch = false;
        while (true)
        {
            var hasExpected = expectedEnumerator.MoveNext();
            var hasActual = actualEnumerator.MoveNext();
            if (hasExpected != hasActual)
            {
                mismatch = true;
                break;
            }
            if (!hasExpected) break;
            if (expectedEnumerator.Current.CompletedCycleId != actualEnumerator.Current.CompletedCycleId
                || (!string.IsNullOrWhiteSpace(expectedEnumerator.Current.AcceptedReadyHandoffId)
                    && !string.Equals(
                        expectedEnumerator.Current.AcceptedReadyHandoffId,
                        actualEnumerator.Current.AcceptedReadyHandoffId,
                        StringComparison.Ordinal)))
            {
                mismatch = true;
                break;
            }
        }
        if (mismatch)
        {
            throw new ReportAuditQueryException("Calibration evidence no longer reconciles to the selected report population. Refresh Reports and try again.");
        }

        return evidence;
    }

    private static IReadOnlyList<CompletedRoomCycle> SelectCalibrationEvidencePopulation(
        IReadOnlyList<CompletedRoomCycle> population,
        IReadOnlyList<CalibrationEvidenceCase> evidence)
    {
        var orderedPopulation = BoundedReportCollections.OrderBy(
            population,
            cycle => cycle.CompletedCycleId.ToString("D20", System.Globalization.CultureInfo.InvariantCulture),
            descending: false);
        return BoundedReportCollections.Materialize(MatchEvidence(orderedPopulation, evidence));
    }

    private static IEnumerable<CompletedRoomCycle> MatchEvidence(
        IReadOnlyList<CompletedRoomCycle> orderedPopulation,
        IReadOnlyList<CalibrationEvidenceCase> orderedEvidence)
    {
        using var evidenceEnumerator = orderedEvidence.GetEnumerator();
        var hasEvidence = evidenceEnumerator.MoveNext();
        foreach (var cycle in orderedPopulation)
        {
            while (hasEvidence && evidenceEnumerator.Current.CompletedCycleId < cycle.CompletedCycleId)
            {
                hasEvidence = evidenceEnumerator.MoveNext();
            }
            if (!hasEvidence) yield break;
            if (evidenceEnumerator.Current.CompletedCycleId == cycle.CompletedCycleId) yield return cycle;
        }
    }

    private ReportAuditRow ToAuditRow(
        CompletedRoomCycle cycle,
        string mode,
        CalibrationEvidenceCase? calibrationEvidence)
    {
        var observed = OrderedSeconds(cycle.SeatedAt, cycle.DoctorCompleteAt);
        double? variance = observed.HasValue && cycle.ExpectedAllocationMinutes > 0
            ? observed.Value - cycle.ExpectedAllocationMinutes * 60d
            : null;
        return new ReportAuditRow(
            cycle.CompletedCycleId,
            cycle.AcceptedReadyHandoffId,
            mode,
            cycle.IsExcludedFromStandardMetrics
                ? ReportAuditStanding.ReportingExcluded
                : ReportAuditStanding.Included,
            cycle.DoctorCompleteAt,
            cycle.RoomId,
            cycle.AssignedDoctor,
            ResolveDoctorName(cycle.AssignedDoctor),
            cycle.ProcedureCode,
            ResolveProcedureLabel(cycle.ProcedureCode),
            ResolveBaseProcedureCode(cycle.ProcedureCode),
            IsSedationProcedureCode(cycle.ProcedureCode),
            cycle.IsAddOn,
            cycle.PrestageStartedAt,
            cycle.SeatedAt,
            cycle.ReadyForDoctorAt,
            cycle.DoctorArrivedAt,
            cycle.DoctorCompleteAt,
            cycle.RoomAvailableAt,
            OrderedSeconds(cycle.SeatedAt, cycle.ReadyForDoctorAt),
            OrderedSeconds(cycle.SeatedAt, cycle.DoctorArrivedAt),
            OrderedSeconds(cycle.ReadyForDoctorAt, cycle.DoctorArrivedAt),
            OrderedSeconds(cycle.DoctorArrivedAt, cycle.DoctorCompleteAt),
            observed,
            OrderedSeconds(cycle.DoctorCompleteAt, cycle.RoomAvailableAt),
            OrderedSeconds(cycle.SeatedAt, cycle.RoomAvailableAt),
            cycle.OriginalDefaultExpectedUnits,
            cycle.ExpectedAllocationUnits,
            cycle.ExpectedAllocationMinutes,
            cycle.AllocationAdjustedFromDefault,
            observed,
            variance,
            cycle.AgingThresholdReached,
            cycle.StaleThresholdReached,
            cycle.ReportingExceptionReasons.ToArray(),
            calibrationEvidence,
            cycle.CompletedCycleId > 0,
            cycle.CompletedCycleId > 0);
    }

    private string ResolveDoctorName(string? doctorId) =>
        _doctors.FirstOrDefault(doctor => string.Equals(doctor.Id, doctorId, StringComparison.OrdinalIgnoreCase))?.Name
        ?? (string.IsNullOrWhiteSpace(doctorId) ? "Unknown doctor" : doctorId);

    private static IEnumerable<CompletedRoomCycle> ApplyStanding(
        IEnumerable<CompletedRoomCycle> cycles,
        string standing) => standing switch
        {
            ReportAuditStanding.Included => cycles.Where(cycle => !cycle.IsExcludedFromStandardMetrics),
            ReportAuditStanding.ReportingExcluded => cycles.Where(cycle => cycle.IsExcludedFromStandardMetrics),
            _ => cycles
        };

    private bool MatchesProcedure(CompletedRoomCycle cycle, ReportAuditSelection selection)
    {
        if (selection.Query.ProcedureGrouping == ReportProcedureGroupings.DetailedVariant)
        {
            return selection.ProcedureCode is null
                || string.Equals(cycle.ProcedureCode, selection.ProcedureCode, StringComparison.OrdinalIgnoreCase);
        }

        var requestedBase = selection.BaseProcedureCode ?? selection.ProcedureCode;
        return requestedBase is null
            || string.Equals(
                ResolveBaseProcedureCode(cycle.ProcedureCode),
                requestedBase,
                StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesReviewProcedure(ReportReviewAuditRow row, ReportAuditSelection selection)
    {
        if (selection.Query.ProcedureGrouping == ReportProcedureGroupings.DetailedVariant)
        {
            return selection.ProcedureCode is null
                || string.Equals(row.ProcedureCode, selection.ProcedureCode, StringComparison.OrdinalIgnoreCase);
        }

        var requestedBase = selection.BaseProcedureCode ?? selection.ProcedureCode;
        return requestedBase is null
            || string.Equals(
                ResolveBaseProcedureCode(row.ProcedureCode),
                requestedBase,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSegmentDoctor(CompletedRoomCycle cycle, string? segmentDoctorId) =>
        segmentDoctorId is null
        || string.Equals(cycle.AssignedDoctor, segmentDoctorId, StringComparison.OrdinalIgnoreCase);

    private static bool HasReadyWait(CompletedRoomCycle cycle) =>
        cycle.ReadyToDoctorSeconds.HasValue
        && OrderedSeconds(cycle.ReadyForDoctorAt, cycle.DoctorArrivedAt).HasValue;

    private static bool HasSeatedToDoctor(CompletedRoomCycle cycle) =>
        OrderedSeconds(cycle.SeatedAt, cycle.DoctorArrivedAt).HasValue;

    private static bool HasDoctorTime(CompletedRoomCycle cycle) =>
        cycle.DoctorInRoomSeconds.HasValue
        && OrderedSeconds(cycle.DoctorArrivedAt, cycle.DoctorCompleteAt).HasValue;

    private static bool HasTurnover(CompletedRoomCycle cycle) =>
        cycle.TurnoverSeconds.HasValue
        && OrderedSeconds(cycle.DoctorCompleteAt, cycle.RoomAvailableAt).HasValue;

    private static bool HasObservedScheduleFit(CompletedRoomCycle cycle) =>
        OrderedSeconds(cycle.SeatedAt, cycle.DoctorCompleteAt).HasValue;

    private static bool HasHistoricalScheduleFit(CompletedRoomCycle cycle) =>
        cycle.ExpectedAllocationMinutes > 0 && HasObservedScheduleFit(cycle);

    private static double? OrderedSeconds(DateTimeOffset? start, DateTimeOffset? end) =>
        start.HasValue && end.HasValue && start.Value <= end.Value
            ? (end.Value - start.Value).TotalSeconds
            : null;

    private static IEnumerable<ReportAuditRow> OrderAuditRows(
        IReadOnlyList<ReportAuditRow> rows,
        string sort)
    {
        IOrderedEnumerable<ReportAuditRow> ordered = sort switch
        {
            ReportAuditSorts.LongestReadyWait => rows
                .OrderBy(row => row.ReadyWaitSeconds.HasValue ? 0 : 1)
                .ThenByDescending(row => row.ReadyWaitSeconds),
            ReportAuditSorts.LongestDoctorTime => rows
                .OrderBy(row => row.DoctorTimeSeconds.HasValue ? 0 : 1)
                .ThenByDescending(row => row.DoctorTimeSeconds),
            ReportAuditSorts.LargestPositiveScheduleFitVariance => rows
                .OrderBy(row => row.ExactScheduleFitVarianceSeconds is > 0 ? 0 : row.ExactScheduleFitVarianceSeconds.HasValue ? 1 : 2)
                .ThenByDescending(row => row.ExactScheduleFitVarianceSeconds),
            ReportAuditSorts.LargestNegativeScheduleFitVariance => rows
                .OrderBy(row => row.ExactScheduleFitVarianceSeconds is < 0 ? 0 : row.ExactScheduleFitVarianceSeconds.HasValue ? 1 : 2)
                .ThenBy(row => row.ExactScheduleFitVarianceSeconds),
            ReportAuditSorts.Doctor => rows
                .OrderBy(row => row.DoctorName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.DoctorId, StringComparer.OrdinalIgnoreCase),
            ReportAuditSorts.Procedure => rows
                .OrderBy(row => row.ProcedureLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.ProcedureCode, StringComparer.OrdinalIgnoreCase),
            _ => rows
                .OrderBy(row => row.DoctorCompleteAt.HasValue ? 0 : 1)
                .ThenByDescending(row => row.DoctorCompleteAt)
        };

        return ordered
            .ThenBy(row => row.DoctorCompleteAt.HasValue ? 0 : 1)
            .ThenByDescending(row => row.DoctorCompleteAt)
            .ThenByDescending(row => row.CompletedCycleId);
    }

    internal static IReadOnlyList<ReportAuditRow> OrderProjectedAuditRows(
        IReadOnlyList<ReportAuditRow> rows,
        string sort) =>
        OrderAuditRows(rows, sort).ToList();

    private static IEnumerable<ReportReviewAuditRow> OrderReviewRows(
        IReadOnlyList<ReportReviewAuditRow> rows,
        string sort)
    {
        IOrderedEnumerable<ReportReviewAuditRow> ordered = sort switch
        {
            ReportAuditSorts.Doctor => rows
                .OrderBy(row => row.DoctorName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.DoctorId, StringComparer.OrdinalIgnoreCase),
            ReportAuditSorts.Procedure => rows
                .OrderBy(row => row.ProcedureLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.ProcedureCode, StringComparer.OrdinalIgnoreCase),
            _ => rows
                .OrderBy(row => row.ReviewAnchor.HasValue ? 0 : 1)
                .ThenByDescending(row => row.ReviewAnchor)
        };
        return ordered
            .ThenBy(row => row.ReviewAnchor.HasValue ? 0 : 1)
            .ThenByDescending(row => row.ReviewAnchor)
            .ThenByDescending(row => row.ReviewRecordId);
    }

    internal static IReadOnlyList<ReportReviewAuditRow> OrderProjectedReviewRows(
        IReadOnlyList<ReportReviewAuditRow> rows,
        string sort) =>
        OrderReviewRows(rows, sort).ToList();

    private static string NormalizeContributorKind(string? value)
    {
        var supported = new[]
        {
            ReportAuditContributorKinds.PracticeCompletedCases,
            ReportAuditContributorKinds.IncludedCompletedCases,
            ReportAuditContributorKinds.ReadyWait,
            ReportAuditContributorKinds.SeatedToDoctor,
            ReportAuditContributorKinds.DoctorTime,
            ReportAuditContributorKinds.Turnover,
            ReportAuditContributorKinds.ProcedureMix,
            ReportAuditContributorKinds.ProcedureIntelligenceReadyWait,
            ReportAuditContributorKinds.ProcedureIntelligenceDoctorTime,
            ReportAuditContributorKinds.ProcedureIntelligenceSeatedToDoctorComplete,
            ReportAuditContributorKinds.HistoricalScheduleFit,
            ReportAuditContributorKinds.CalibrationEvidence,
            ReportAuditContributorKinds.AnomalyReview,
            ReportAuditContributorKinds.PendingReview,
            ReportAuditContributorKinds.ReviewedExceptionHistory
        };
        if (string.IsNullOrWhiteSpace(value))
        {
            return ReportAuditContributorKinds.PracticeCompletedCases;
        }

        return supported.FirstOrDefault(item => string.Equals(item, value.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ReportAuditQueryException($"Unsupported contributorKind '{value}'.");
    }

    internal static string NormalizeAnomalyStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ReportAnomalyStatuses.NeedsReview;
        }

        return ReportAnomalyStatuses.All.FirstOrDefault(item =>
                string.Equals(item, value.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ReportAuditQueryException($"Unsupported anomalyStatus '{value}'.");
    }

    internal static bool MatchesAnomalyStatus(string disposition, string anomalyStatus) =>
        anomalyStatus == ReportAnomalyStatuses.AllAnomalies
            ? disposition is HistoricalAdministrativeDispositions.NeedsReview
                or HistoricalAdministrativeDispositions.ConfirmedException
                or HistoricalAdministrativeDispositions.ClearedForReporting
            : disposition == anomalyStatus;

    private static string NormalizeStanding(string? value) =>
        value?.Trim() switch
        {
            var item when string.Equals(item, ReportAuditStanding.Included, StringComparison.OrdinalIgnoreCase) => ReportAuditStanding.Included,
            var item when string.Equals(item, ReportAuditStanding.ReportingExcluded, StringComparison.OrdinalIgnoreCase) => ReportAuditStanding.ReportingExcluded,
            _ => ReportAuditStanding.All
        };

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
