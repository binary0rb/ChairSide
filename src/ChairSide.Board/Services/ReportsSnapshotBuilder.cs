using System.Globalization;

namespace ChairSide.Board.Services;

/// <summary>
/// Builds the complete reports read model from detached historical inputs.
/// Population selection and aggregation are intentionally independent of live room mutation,
/// persistence, and lifecycle synchronization.
/// </summary>
internal sealed partial class ReportsSnapshotBuilder
{
    private sealed record ScopedProcedurePopulation(
        string ProcedureCode,
        string ProcedureLabel,
        string BaseProcedureCode,
        bool? IsSedationCase,
        IReadOnlyList<CompletedRoomCycle> Cycles);

    private static readonly TimeSpan ExtremeCaseFlowThreshold = TimeSpan.FromHours(4);
    private static readonly TimeSpan ExtremeRoomCycleThreshold = TimeSpan.FromHours(6);

    private const string SedationModifierSuffix = "+SED";
    private const string LegacySedationCode = "SED";

    private readonly IReadOnlyList<Doctor> _doctors;
    private readonly IReadOnlyList<Doctor> _activeDoctors;
    private readonly IReadOnlyList<ProcedureCategory> _procedures;
    private readonly IReadOnlyList<ProcedureCategory> _activeProcedures;

    public ReportsSnapshotBuilder(
        IEnumerable<Doctor> doctors,
        IEnumerable<Doctor> activeDoctors,
        IEnumerable<ProcedureCategory> procedures,
        IEnumerable<ProcedureCategory> activeProcedures)
    {
        ArgumentNullException.ThrowIfNull(doctors);
        ArgumentNullException.ThrowIfNull(activeDoctors);
        ArgumentNullException.ThrowIfNull(procedures);
        ArgumentNullException.ThrowIfNull(activeProcedures);

        _doctors = doctors.ToArray();
        _activeDoctors = activeDoctors.ToArray();
        _procedures = procedures.ToArray();
        _activeProcedures = activeProcedures.ToArray();
    }

    public ReportsSnapshot Build(
        IReadOnlyList<CompletedRoomCycle> completedCycles,
        IReadOnlyList<AbortedRoomAssignment> abortedAssignments,
        ReportDateRange range) =>
        Build(completedCycles, abortedAssignments, ReportQuery.Default with { Window = range });

    public ReportsSnapshot Build(
        IReadOnlyList<CompletedRoomCycle> completedCycles,
        IReadOnlyList<AbortedRoomAssignment> abortedAssignments,
        ReportQuery query) =>
        ReportsSnapshotAdapter.ToSnapshot(Compose(completedCycles, abortedAssignments, query));

    public ReportsSnapshot Build(
        IReadOnlyList<CompletedRoomCycle> reportWindowCompletedCycles,
        IReadOnlyList<CompletedRoomCycle> reviewWindowCompletedCycles,
        IReadOnlyList<AbortedRoomAssignment> reviewWindowAbortedAssignments,
        ReportQuery query,
        int totalCompletedAllTime) =>
        ReportsSnapshotAdapter.ToSnapshot(Compose(
            reportWindowCompletedCycles,
            reviewWindowCompletedCycles,
            reviewWindowAbortedAssignments,
            query,
            totalCompletedAllTime));

    internal ReportsSnapshotComposition Compose(
        IReadOnlyList<CompletedRoomCycle> completedCycles,
        IReadOnlyList<AbortedRoomAssignment> abortedAssignments,
        ReportDateRange range) =>
        Compose(completedCycles, abortedAssignments, ReportQuery.Default with { Window = range });

    internal ReportsSnapshotComposition Compose(
        IReadOnlyList<CompletedRoomCycle> completedCycles,
        IReadOnlyList<AbortedRoomAssignment> abortedAssignments,
        ReportQuery query)
        => Compose(
            completedCycles,
            completedCycles,
            abortedAssignments,
            query,
            null);

    private ReportsSnapshotComposition Compose(
        IReadOnlyList<CompletedRoomCycle> completedCycles,
        IReadOnlyList<CompletedRoomCycle> reviewCompletedCycles,
        IReadOnlyList<AbortedRoomAssignment> abortedAssignments,
        ReportQuery query,
        int? totalCompletedAllTimeOverride)
    {
        ArgumentNullException.ThrowIfNull(completedCycles);
        ArgumentNullException.ThrowIfNull(reviewCompletedCycles);
        ArgumentNullException.ThrowIfNull(abortedAssignments);

        // All-time completed total (for "X of Y" context), independent of the selected window.
        var totalCompletedAllTime = totalCompletedAllTimeOverride ?? completedCycles.Count(cycle =>
            cycle.RoomAvailableAt is not null && query.IncludesAnalyticalCycle(cycle));

        // Apply the completion window before copying or deriving report-time annotations. This
        // preserves the established DoctorCompleteAt boundary and leaves out-of-window history
        // completely uninspected by downstream reporting calculations.
        var selectedCycles = completedCycles
            .Where(cycle => query.Window.Includes(cycle.DoctorCompleteAt))
            .ToList();

        // Report-time annotations are applied only to detached copies. The store's durable
        // historical snapshots remain untouched by report reads.
        var allCycles = CreateAnnotatedCompletedCycleSnapshot(selectedCycles)
            .OrderByDescending(cycle => cycle.DoctorArrivedAt)
            .ToList();
        var detachedAbortedAssignments = abortedAssignments
            .Where(record => query.Window.Includes(record.TerminatedAt))
            .Select(CopyAbortedAssignment)
            .ToList();

        // Manual review exceptions are excluded from normal operational metrics by default.
        var normalCycles = allCycles.Where(cycle => !cycle.IsException).ToList();
        // Standard population additionally drops reporting-exception cycles (legacy/unmapped/
        // extreme/overnight) so doctor-facing aggregates are not skewed by sample/legacy data.
        var standardCycles = normalCycles.Where(cycle => !cycle.IsExcludedFromStandardMetrics).ToList();

        // Doctor and sedation are analytical population filters. Apply them once after report-time
        // annotation so derived operational context (for example occupied wait) can still observe the
        // full window. Procedure grouping is intentionally absent here because it is aggregation only.
        var scopedNormalCycles = normalCycles.Where(query.IncludesAnalyticalCycle).ToList();
        var scopedStandardCycles = standardCycles.Where(query.IncludesAnalyticalCycle).ToList();

        // Raw/audit completed set keeps reporting-exception cycles visible (with badges);
        // the standard completed set drives aggregates.
        var normalCompletedCycles = scopedNormalCycles
            .Where(cycle => cycle.RoomAvailableAt is not null)
            .ToList();
        var standardCompletedCycles = scopedStandardCycles
            .Where(cycle => cycle.RoomAvailableAt is not null)
            .ToList();

        // Review populations use their own truthful source anchors. In particular, post-arrival
        // exceptions without DoctorCompleteAt remain discoverable by their latest observed lifecycle
        // timestamp instead of being lost by the completed-case window.
        var selectedReviewCompletedCycles = reviewCompletedCycles
            .Where(cycle => cycle.IsException && query.Window.Includes(ReviewAnchor(cycle)))
            .Select(CopyCompletedCycle)
            .ToList();
        var exceptionCycles = selectedReviewCompletedCycles
            .Where(cycle => cycle.RequiresReview)
            .OrderByDescending(cycle => cycle.SeatedAt)
            .ToList();
        var exceptionReviewRecords = BuildExceptionReviewRecords(exceptionCycles, detachedAbortedAssignments);

        // Annotate the raw display set with doctor-occupied / available wait, but use only the
        // standard population as the blocker pool so an extreme/overnight outlier never distorts
        // another cycle's occupied wait.
        AnnotateOccupiedWait(normalCycles, standardCycles);

        var compatibilityScheduleFit = ScheduleFitReportBuilder.Build(standardCompletedCycles);
        var scopedProcedurePopulations = BuildScopedProcedurePopulations(
            standardCompletedCycles,
            query.ProcedureGrouping);
        var calibrationRules = CalibrationRuleSet.VersionOne;
        var scheduleFit = compatibilityScheduleFit with
        {
            Practice = ExactScheduleFitCalculator.BuildHistoricalAssignedSummary(
                standardCompletedCycles,
                calibrationRules),
            ProcedureSegments = BuildScheduleFitProcedureSegments(
                scopedProcedurePopulations,
                query,
                calibrationRules),
            DoctorSummaries = BuildDoctorScheduleFitSummaries(
                standardCompletedCycles,
                query,
                calibrationRules),
            Rules = calibrationRules
        };
        var scopedProcedureGroups = BuildScopedProcedureGroups(
            scopedProcedurePopulations,
            standardCompletedCycles.Count);
        var procedureIntelligenceRows = BuildProcedureIntelligenceRows(
            scopedProcedurePopulations,
            query);
        var observedDoctorFlowDays = BuildObservedDoctorFlowDays(standardCompletedCycles);
        var doctorFlowIdentities = BuildDoctorFlowIdentities(scopedStandardCycles, query);
        var doctorFlowSummaries = BuildDoctorFlowSummaries(
            scopedStandardCycles,
            standardCompletedCycles,
            observedDoctorFlowDays,
            doctorFlowIdentities);
        var doctorFlowTrends = DoctorFlowTrendSnapshotBuilder.BuildWeekly(
            doctorFlowIdentities,
            scopedStandardCycles,
            standardCompletedCycles,
            observedDoctorFlowDays,
            query.Window);
        var phasePopulationCount = scopedStandardCycles.Count;
        var samples = new ReportMetricSampleContext(
            CompletedCases: ReportSampleContext.ForPopulation(normalCompletedCycles.Count),
            IncludedCompletedCases: ReportSampleContext.ForPopulation(standardCompletedCycles.Count),
            SeatedToDoctor: ReportSampleContext.Create(
                phasePopulationCount,
                scopedStandardCycles.Count(cycle => cycle.DoctorArrivedAt.HasValue)),
            Prep: ReportSampleContext.Create(
                phasePopulationCount,
                scopedStandardCycles.Count(cycle => cycle.PrepSeconds.HasValue)),
            ReadyWait: ReportSampleContext.Create(
                phasePopulationCount,
                scopedStandardCycles.Count(cycle => cycle.ReadyToDoctorSeconds.HasValue)),
            DoctorTime: ReportSampleContext.Create(
                phasePopulationCount,
                scopedStandardCycles.Count(cycle => cycle.DoctorInRoomSeconds.HasValue)),
            Turnover: ReportSampleContext.Create(
                phasePopulationCount,
                scopedStandardCycles.Count(cycle => cycle.TurnoverSeconds.HasValue)),
            DoctorOccupiedWait: ReportSampleContext.Create(
                phasePopulationCount,
                scopedStandardCycles.Count(cycle => cycle.DoctorOccupiedWaitSeconds.HasValue)),
            DoctorAvailableWait: ReportSampleContext.Create(
                phasePopulationCount,
                scopedStandardCycles.Count(cycle => cycle.DoctorAvailableWaitSeconds.HasValue)),
            ScheduleFit: ReportSampleContext.Create(
                scheduleFit.IncludedCycleCount,
                scheduleFit.ScheduleFitCycleCount));

        return new ReportsSnapshotComposition
        {
            Query = query.ToContext(),
            Samples = samples,
            Population = new ReportPopulationSection
            {
                CompletedRoomCyclesCount = normalCompletedCycles.Count,
                RecentCompletedCycles = normalCompletedCycles.Take(25).ToList(),
                IncludedCompletedCycleCount = standardCompletedCycles.Count,
                ExcludedCompletedCycleCount = normalCompletedCycles.Count - standardCompletedCycles.Count,
                ExceptionCount = normalCompletedCycles.Count(cycle => cycle.HasReportingException),
                DataQuality = BuildDataQualitySummary(
                    normalCompletedCycles,
                    standardCompletedCycles,
                    reviewCompletedCycles,
                    abortedAssignments,
                    query)
            },
            Window = new ReportWindowSection
            {
                RangeStartDate = query.Window.StartDateText,
                RangeEndDate = query.Window.EndDateText,
                RangeLabel = query.Window.Label,
                TotalCompletedCycleCount = totalCompletedAllTime
            },
            Timing = new ReportTimingSection
            {
                AverageSeatedToDoctorSeconds =
                    AverageSeconds(scopedStandardCycles.Select(cycle =>
                        cycle.DoctorArrivedAt.HasValue ? (int?)cycle.SeatedToDoctorSeconds : null)),
                MedianSeatedToDoctorSeconds =
                    MedianSeconds(scopedStandardCycles.Select(cycle =>
                        cycle.DoctorArrivedAt.HasValue ? (int?)cycle.SeatedToDoctorSeconds : null)),
                AveragePrepSeconds = AverageSeconds(scopedStandardCycles.Select(cycle => cycle.PrepSeconds)),
                MedianPrepSeconds = MedianSeconds(scopedStandardCycles.Select(cycle => cycle.PrepSeconds)),
                AverageReadyToDoctorSeconds =
                    AverageSeconds(scopedStandardCycles.Select(cycle => cycle.ReadyToDoctorSeconds)),
                MedianReadyToDoctorSeconds =
                    MedianSeconds(scopedStandardCycles.Select(cycle => cycle.ReadyToDoctorSeconds)),
                AverageDoctorInRoomSeconds =
                    AverageSeconds(scopedStandardCycles.Select(cycle => cycle.DoctorInRoomSeconds)),
                MedianDoctorInRoomSeconds =
                    MedianSeconds(scopedStandardCycles.Select(cycle => cycle.DoctorInRoomSeconds)),
                AverageTurnoverSeconds =
                    AverageSeconds(scopedStandardCycles.Select(cycle => cycle.TurnoverSeconds)),
                MedianTurnoverSeconds =
                    MedianSeconds(scopedStandardCycles.Select(cycle => cycle.TurnoverSeconds)),
                AgingEventCount = scopedStandardCycles.Count(cycle => cycle.AgingThresholdReached),
                StaleEventCount = scopedStandardCycles.Count(cycle => cycle.StaleThresholdReached),
                AverageDoctorOccupiedWaitSeconds =
                    AverageSeconds(scopedStandardCycles.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
                MedianDoctorOccupiedWaitSeconds =
                    MedianSeconds(scopedStandardCycles.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
                AverageDoctorAvailableWaitSeconds =
                    AverageSeconds(scopedStandardCycles.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
                MedianDoctorAvailableWaitSeconds =
                    MedianSeconds(scopedStandardCycles.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
                Trends = ReportTrendSnapshotBuilder.BuildWeekly(standardCompletedCycles)
            },
            Procedures = new ReportProcedureSection
            {
                ProcedureSummaries = BuildProcedureSummaries(standardCompletedCycles),
                SedationCaseCount =
                    standardCompletedCycles.Count(cycle => IsSedationProcedureCode(cycle.ProcedureCode)),
                NonSedationCaseCount =
                    standardCompletedCycles.Count(cycle => !IsSedationProcedureCode(cycle.ProcedureCode)),
                BaseProcedureSummaries = BuildBaseProcedureSummaries(standardCompletedCycles),
                ScopedProcedureGroups = scopedProcedureGroups,
                ProcedureIntelligenceRows = procedureIntelligenceRows
            },
            Allocation = new ReportAllocationSection
            {
                AllocationVariance = BuildAllocationVarianceSummary(standardCompletedCycles),
                DoctorDailyAllocationSeries = BuildDoctorDailyAllocationSeries(scopedStandardCycles),
                ScheduleFit = scheduleFit
            },
            DoctorDetail = new ReportDoctorDetailSection
            {
                DoctorSummaries = BuildDoctorSummaries(scopedStandardCycles),
                DoctorAllocationSamples = BuildDoctorAllocationSamples(scopedStandardCycles, query),
                DoctorFlowSummaries = doctorFlowSummaries,
                DoctorFlowTrends = doctorFlowTrends,
                ObservedDoctorDays = BuildObservedDoctorDays(standardCompletedCycles),
                ObservedDoctorFlowDays = observedDoctorFlowDays,
                DoctorProcedureMix = BuildDoctorProcedureMix(standardCompletedCycles)
            },
            ReviewQueue = new ReportReviewQueueSection
            {
                ExceptionCycles = exceptionCycles,
                ExceptionReviewRecords = exceptionReviewRecords
            }
        };
    }

    internal IReadOnlyList<CompletedRoomCycle> CreateAnnotatedCompletedCycleSnapshot(
        IReadOnlyList<CompletedRoomCycle> completedCycles)
    {
        ArgumentNullException.ThrowIfNull(completedCycles);

        var detached = completedCycles.Select(CopyCompletedCycle).ToList();
        AnnotateReportingExceptions(detached);
        AnnotateAllocationVariance(detached);
        return detached;
    }

    private static IReadOnlyList<ExceptionReviewRecord> BuildExceptionReviewRecords(
        IReadOnlyList<CompletedRoomCycle> completedExceptions,
        IReadOnlyList<AbortedRoomAssignment> abortedAssignments)
    {
        var completed = completedExceptions.Select(cycle => new ExceptionReviewRecord(
            ExceptionReviewSources.CompletedCycle,
            cycle.CompletedCycleId,
            cycle.CompletedCycleId,
            0,
            cycle.EpisodeId,
            cycle.RoomId,
            cycle.AssignedDoctor,
            cycle.ProcedureCode,
            cycle.PrestageStartedAt,
            cycle.SeatedAt,
            cycle.ReadyForDoctorAt,
            cycle.DoctorArrivedAt,
            cycle.DoctorCompleteAt,
            cycle.RoomAvailableAt,
            cycle.FinalWaitState,
            cycle.IsException,
            cycle.RequiresReview,
            cycle.ExceptionReason,
            cycle.ReviewStatus,
            cycle.SuggestedAction,
            cycle.ReviewedAt,
            cycle.ReviewedBy));

        var aborted = abortedAssignments
            .Where(record => record.IsException && record.RequiresReview)
            .Select(record => new ExceptionReviewRecord(
                ExceptionReviewSources.AbortedAssignment,
                record.AbortedAssignmentId,
                0,
                record.AbortedAssignmentId,
                record.EpisodeId,
                record.RoomId,
                record.AssignedDoctor,
                record.ProcedureCode,
                record.PrestageStartedAt,
                record.SeatedAt,
                record.ReadyForDoctorAt,
                null,
                null,
                null,
                record.TerminatedFromState,
                record.IsException,
                record.RequiresReview,
                record.ExceptionReason,
                record.ReviewStatus,
                record.SuggestedAction,
                record.ReviewedAt,
                record.ReviewedBy));

        return completed
            .Concat(aborted)
            .OrderByDescending(record => record.SeatedAt ?? record.PrestageStartedAt)
            .ThenBy(record => record.RoomId)
            .ToList();
    }

    private static double AverageSeconds(IEnumerable<int?> values)
    {
        var completed = values.Where(value => value.HasValue).Select(value => value!.Value).ToList();
        return completed.Count == 0 ? 0 : completed.Average();
    }

    private static double MedianSeconds(IEnumerable<int?> values)
    {
        var ordered = values
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Order()
            .ToList();
        if (ordered.Count == 0)
        {
            return 0;
        }

        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    internal static double? MedianSecondsOrNull(IEnumerable<int?> values)
    {
        var ordered = values
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Order()
            .ToList();
        if (ordered.Count == 0)
        {
            return null;
        }

        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    internal static double? MedianWholeMinutesOrNull(IEnumerable<int> values)
    {
        var ordered = values.Order().ToList();
        if (ordered.Count == 0)
        {
            return null;
        }

        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    private static IReadOnlyList<DoctorCycleSummary> BuildDoctorSummaries(IReadOnlyList<CompletedRoomCycle> cycles) =>
        cycles
            .Where(cycle => cycle.DoctorArrivedAt.HasValue)
            .GroupBy(cycle => new
            {
                cycle.AssignedDoctor,
                Month = new DateOnly(cycle.DoctorArrivedAt!.Value.Year, cycle.DoctorArrivedAt.Value.Month, 1)
            })
            .Select(group => new DoctorCycleSummary(
                group.Key.AssignedDoctor,
                group.Key.Month,
                group.Count(),
                AverageSeconds(group.Select(cycle => (int?)cycle.SeatedToDoctorSeconds)),
                MedianSeconds(group.Select(cycle => (int?)cycle.SeatedToDoctorSeconds)),
                AverageSeconds(group.Select(cycle => cycle.PrepSeconds)),
                MedianSeconds(group.Select(cycle => cycle.PrepSeconds)),
                AverageSeconds(group.Select(cycle => cycle.ReadyToDoctorSeconds)),
                MedianSeconds(group.Select(cycle => cycle.ReadyToDoctorSeconds)),
                AverageSeconds(group.Select(cycle => cycle.DoctorInRoomSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorInRoomSeconds)),
                AverageSeconds(group.Select(cycle => cycle.TurnoverSeconds)),
                MedianSeconds(group.Select(cycle => cycle.TurnoverSeconds)),
                group.Count(cycle => cycle.AgingThresholdReached),
                group.Count(cycle => cycle.StaleThresholdReached),
                AverageSeconds(group.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
                AverageSeconds(group.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
                BuildAllocationVarianceSummary(group)))
            .OrderByDescending(summary => summary.Month)
            .ThenBy(summary => summary.AssignedDoctor)
            .ToList();

    private IReadOnlyList<ReportDoctorAllocationSampleContext> BuildDoctorAllocationSamples(
        IReadOnlyList<CompletedRoomCycle> cycles,
        ReportQuery query)
    {
        var representedCycles = cycles
            .Where(cycle => cycle.DoctorArrivedAt.HasValue && !string.IsNullOrWhiteSpace(cycle.AssignedDoctor))
            .ToList();
        var doctorIds = query.Scope == ReportScopeKinds.Doctor
            ? new[] { query.DoctorId }.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!)
            : _doctors.Select(doctor => doctor.Id)
                .Concat(representedCycles.Select(cycle => cycle.AssignedDoctor!));

        return doctorIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(doctorId =>
            {
                var population = representedCycles
                    .Where(cycle => string.Equals(cycle.AssignedDoctor, doctorId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return new ReportDoctorAllocationSampleContext(
                    doctorId,
                    ReportSampleContext.Create(
                        population.Count,
                        population.Count(cycle => cycle.AllocationVarianceMinutes.HasValue)));
            })
            .ToList();
    }

    private static IReadOnlyList<DoctorDailyAllocation> BuildDoctorDailyAllocationSeries(
        IReadOnlyList<CompletedRoomCycle> cycles) =>
        cycles
            .Where(cycle => cycle.AllocationVarianceMinutes.HasValue && cycle.DoctorCompleteAt.HasValue)
            .GroupBy(cycle => cycle.AssignedDoctor)
            .Select(doctorGroup => new DoctorDailyAllocation(
                doctorGroup.Key,
                doctorGroup
                    .GroupBy(cycle => DateOnly.FromDateTime(cycle.DoctorCompleteAt!.Value.UtcDateTime))
                    .OrderBy(dateGroup => dateGroup.Key)
                    .Select(dateGroup => new DoctorDailyAllocationPoint(
                        dateGroup.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        dateGroup.Count(),
                        dateGroup.Sum(cycle => cycle.AllocationVarianceMinutes!.Value)))
                    .ToList()))
            .OrderBy(item => item.DoctorId)
            .ToList();

    private IReadOnlyList<DoctorFlowTrendIdentity> BuildDoctorFlowIdentities(
        IReadOnlyList<CompletedRoomCycle> scopedStandardPhaseCycles,
        ReportQuery query)
    {
        var representedDoctorIds = scopedStandardPhaseCycles
            .Where(cycle => !string.IsNullOrWhiteSpace(cycle.AssignedDoctor))
            .Select(cycle => cycle.AssignedDoctor)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        IReadOnlyList<string> doctorIds;
        if (query.Scope == ReportScopeKinds.Doctor)
        {
            doctorIds = string.IsNullOrWhiteSpace(query.DoctorId) ? [] : [query.DoctorId];
        }
        else
        {
            var activeIds = _activeDoctors.Select(doctor => doctor.Id).ToList();
            var activeSet = activeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var historicalIds = representedDoctorIds
                .Where(doctorId => !activeSet.Contains(doctorId))
                .OrderBy(doctorId => doctorId, StringComparer.OrdinalIgnoreCase);
            doctorIds = activeIds.Concat(historicalIds).ToList();
        }

        return doctorIds
            .Select(doctorId => new DoctorFlowTrendIdentity(
                doctorId,
                ResolveDoctorDisplayName(doctorId) ?? doctorId))
            .ToList();
    }

    private static IReadOnlyList<DoctorFlowSummary> BuildDoctorFlowSummaries(
        IReadOnlyList<CompletedRoomCycle> scopedStandardPhaseCycles,
        IReadOnlyList<CompletedRoomCycle> scopedStandardCompletedCycles,
        IReadOnlyList<ObservedDoctorFlowDay> observedDoctorFlowDays,
        IReadOnlyList<DoctorFlowTrendIdentity> doctorIdentities)
    {
        return doctorIdentities
            .Select(identity =>
            {
                var doctorId = identity.DoctorId;
                var phasePopulation = scopedStandardPhaseCycles
                    .Where(cycle => string.Equals(
                        cycle.AssignedDoctor,
                        doctorId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var completedPopulation = scopedStandardCompletedCycles
                    .Where(cycle => string.Equals(
                        cycle.AssignedDoctor,
                        doctorId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var days = observedDoctorFlowDays
                    .Where(day => string.Equals(day.DoctorId, doctorId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var readyWaitValues = phasePopulation
                    .Select(TruthfulReadyWaitSeconds)
                    .Where(value => value.HasValue)
                    .ToList();
                var doctorTimeValues = phasePopulation
                    .Select(TruthfulDoctorTimeSeconds)
                    .Where(value => value.HasValue)
                    .ToList();
                var representedCompletedDates = completedPopulation
                    .Where(cycle => cycle.DoctorCompleteAt.HasValue)
                    .Select(cycle => DateOnly.FromDateTime(cycle.DoctorCompleteAt!.Value.UtcDateTime))
                    .Distinct()
                    .Count();
                var samples = new ReportDoctorFlowMetricSampleContext(
                    CompletedCases: ReportSampleContext.ForPopulation(completedPopulation.Count),
                    ReadyWait: ReportSampleContext.Create(phasePopulation.Count, readyWaitValues.Count),
                    DoctorTime: ReportSampleContext.Create(phasePopulation.Count, doctorTimeValues.Count),
                    ObservedDays: ReportSampleContext.Create(representedCompletedDates, days.Count));

                return new DoctorFlowSummary(
                    doctorId,
                    identity.DoctorName,
                    completedPopulation.Count,
                    MedianSecondsOrNull(readyWaitValues),
                    MedianSecondsOrNull(doctorTimeValues),
                    MedianWholeMinutesOrNull(days.Select(day => day.ObservedClinicalSpanMinutes)),
                    days.Count == 0 ? null : days.Max(day => day.PeakConcurrentRooms),
                    days.Count,
                    samples);
            })
            .ToList();
    }

    internal static int? TruthfulReadyWaitSeconds(CompletedRoomCycle cycle) =>
        cycle.ReadyForDoctorAt is { } readyAt
        && cycle.DoctorArrivedAt is { } arrivedAt
        && readyAt <= arrivedAt
        && cycle.ReadyToDoctorSeconds is >= 0
            ? cycle.ReadyToDoctorSeconds
            : null;

    internal static int? TruthfulDoctorTimeSeconds(CompletedRoomCycle cycle) =>
        cycle.DoctorArrivedAt is { } arrivedAt
        && cycle.DoctorCompleteAt is { } completeAt
        && arrivedAt <= completeAt
        && cycle.DoctorInRoomSeconds is >= 0
            ? cycle.DoctorInRoomSeconds
            : null;

    internal static double? TruthfulSeatedToDoctorCompleteSeconds(CompletedRoomCycle cycle) =>
        ExactScheduleFitCalculator.TruthfulObservedCaseFlowSeconds(cycle);

    private IReadOnlyList<ObservedDoctorFlowDay> BuildObservedDoctorFlowDays(
        IReadOnlyList<CompletedRoomCycle> scopedStandardCompletedCycles) =>
        scopedStandardCompletedCycles
            .Where(IsQualifyingObservedDoctorFlowCase)
            .GroupBy(cycle => new
            {
                DoctorId = cycle.AssignedDoctor ?? "",
                ReportDate = DateOnly.FromDateTime(cycle.DoctorCompleteAt!.Value.UtcDateTime)
            })
            .Select(group =>
            {
                var qualifyingCycles = group.ToList();
                var firstAcceptedReadyAt = qualifyingCycles.Min(cycle => cycle.ReadyForDoctorAt!.Value);
                var lastDoctorCompleteAt = qualifyingCycles.Max(cycle => cycle.DoctorCompleteAt!.Value);
                var concurrency = BuildObservedDoctorWorkingConcurrency(
                    firstAcceptedReadyAt,
                    lastDoctorCompleteAt,
                    qualifyingCycles);

                return new ObservedDoctorFlowDay(
                    group.Key.DoctorId,
                    ResolveDoctorDisplayName(group.Key.DoctorId) ?? group.Key.DoctorId,
                    group.Key.ReportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    qualifyingCycles.Count,
                    firstAcceptedReadyAt,
                    lastDoctorCompleteAt,
                    concurrency.ObservedClinicalSpanMinutes,
                    concurrency.MinutesWithOneDoctorWorkingRoom,
                    concurrency.MinutesWithTwoDoctorWorkingRooms,
                    concurrency.MinutesWithThreeOrMoreDoctorWorkingRooms,
                    concurrency.UnstructuredTimeMinutes,
                    concurrency.PeakConcurrentRooms);
            })
            .OrderBy(day => day.ReportDate, StringComparer.Ordinal)
            .ThenBy(day => day.DoctorId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsQualifyingObservedDoctorFlowCase(CompletedRoomCycle cycle)
    {
        if (cycle.ReadyForDoctorAt is not { } readyAt
            || cycle.DoctorArrivedAt is not { } arrivedAt
            || cycle.DoctorCompleteAt is not { } completeAt
            || readyAt > arrivedAt
            || arrivedAt > completeAt)
        {
            return false;
        }

        return readyAt.UtcDateTime.Date == completeAt.UtcDateTime.Date;
    }

    private static ObservedDoctorWorkingConcurrency BuildObservedDoctorWorkingConcurrency(
        DateTimeOffset spanStart,
        DateTimeOffset spanEnd,
        IReadOnlyList<CompletedRoomCycle> qualifyingCycles)
    {
        var events = new List<(DateTimeOffset At, int Delta)>();
        foreach (var cycle in qualifyingCycles)
        {
            var intervalStart = cycle.DoctorArrivedAt!.Value < spanStart
                ? spanStart
                : cycle.DoctorArrivedAt.Value;
            var intervalEnd = cycle.DoctorCompleteAt!.Value > spanEnd
                ? spanEnd
                : cycle.DoctorCompleteAt.Value;
            if (intervalEnd <= intervalStart)
            {
                continue;
            }

            events.Add((intervalStart, 1));
            events.Add((intervalEnd, -1));
        }

        var exactTicks = new long[4];
        var activeCount = 0;
        var peakConcurrentRooms = 0;
        var previousAt = spanStart;
        foreach (var point in events
            .GroupBy(item => item.At)
            .OrderBy(group => group.Key)
            .Select(group => new { At = group.Key, Delta = group.Sum(item => item.Delta) }))
        {
            if (point.At > previousAt)
            {
                exactTicks[DoctorWorkingBucketIndex(activeCount)] += (point.At - previousAt).Ticks;
            }

            activeCount += point.Delta;
            peakConcurrentRooms = Math.Max(peakConcurrentRooms, activeCount);
            previousAt = point.At;
        }

        if (spanEnd > previousAt)
        {
            exactTicks[DoctorWorkingBucketIndex(activeCount)] += (spanEnd - previousAt).Ticks;
        }

        var wholeMinutes = ApportionObservedFlowWholeMinutes(exactTicks);
        return new ObservedDoctorWorkingConcurrency(
            wholeMinutes.Sum(),
            wholeMinutes[1],
            wholeMinutes[2],
            wholeMinutes[3],
            wholeMinutes[0],
            peakConcurrentRooms);
    }

    private static int DoctorWorkingBucketIndex(int activeCount) => activeCount switch
    {
        <= 0 => 0,
        1 => 1,
        2 => 2,
        _ => 3
    };

    private static int[] ApportionObservedFlowWholeMinutes(IReadOnlyList<long> exactTicks)
    {
        var ticksPerMinute = TimeSpan.TicksPerMinute;
        var wholeMinutes = exactTicks.Select(ticks => (int)(ticks / ticksPerMinute)).ToArray();
        var targetMinutes = (int)(exactTicks.Sum() / ticksPerMinute);
        var remainingMinutes = targetMinutes - wholeMinutes.Sum();

        // Stable tie-break order is canonical bucket order: Unstructured, 1 room, 2 rooms, 3+.
        foreach (var bucket in exactTicks
            .Select((ticks, index) => new { Index = index, Remainder = ticks % ticksPerMinute })
            .OrderByDescending(item => item.Remainder)
            .ThenBy(item => item.Index)
            .Take(remainingMinutes))
        {
            wholeMinutes[bucket.Index]++;
        }

        return wholeMinutes;
    }

    // Compatibility projection: preserve the established Seated-based span and concurrency fields.
    // Canonical #216 Ready-anchored flow is built separately by BuildObservedDoctorFlowDays.
    private IReadOnlyList<ObservedDoctorDay> BuildObservedDoctorDays(IReadOnlyList<CompletedRoomCycle> cycles) =>
        cycles
            .Where(cycle => cycle.DoctorCompleteAt.HasValue && cycle.RoomAvailableAt.HasValue)
            .GroupBy(cycle => new
            {
                DoctorId = cycle.AssignedDoctor ?? "",
                ReportDate = DateOnly.FromDateTime(cycle.DoctorCompleteAt!.Value.UtcDateTime)
            })
            .Select(group =>
            {
                var dayCycles = group.ToList();
                var firstSeatedAt = dayCycles.Min(cycle => cycle.SeatedAt);
                var firstDoctorArrivedAt = dayCycles
                    .Where(cycle => cycle.DoctorArrivedAt.HasValue)
                    .Select(cycle => (DateTimeOffset?)cycle.DoctorArrivedAt!.Value)
                    .OrderBy(value => value)
                    .FirstOrDefault();
                var lastDoctorCompleteAt = dayCycles.Max(cycle => cycle.DoctorCompleteAt!.Value);
                var lastRoomAvailableAt = dayCycles.Max(cycle => cycle.RoomAvailableAt!.Value);
                var concurrency = BuildObservedRoomConcurrency(dayCycles);

                return new ObservedDoctorDay(
                    group.Key.DoctorId,
                    ResolveDoctorDisplayName(group.Key.DoctorId) ?? group.Key.DoctorId,
                    group.Key.ReportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    dayCycles.Count,
                    firstSeatedAt,
                    firstDoctorArrivedAt,
                    lastDoctorCompleteAt,
                    lastRoomAvailableAt,
                    ObservedLoadWholeMinutesBetween(firstSeatedAt, lastDoctorCompleteAt),
                    ObservedLoadWholeMinutesBetween(firstSeatedAt, lastRoomAvailableAt),
                    concurrency.MinutesWithOneActiveRoom,
                    concurrency.MinutesWithTwoActiveRooms,
                    concurrency.MinutesWithThreeOrMoreActiveRooms,
                    concurrency.MaxActiveRoomCount);
            })
            .OrderBy(day => day.ReportDate, StringComparer.Ordinal)
            .ThenBy(day => day.DoctorId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static ObservedRoomConcurrency BuildObservedRoomConcurrency(IReadOnlyList<CompletedRoomCycle> cycles)
    {
        var events = new List<(DateTimeOffset At, int Delta)>();
        foreach (var cycle in cycles)
        {
            if (!cycle.DoctorCompleteAt.HasValue || cycle.DoctorCompleteAt.Value <= cycle.SeatedAt)
            {
                continue;
            }

            events.Add((cycle.SeatedAt, 1));
            events.Add((cycle.DoctorCompleteAt.Value, -1));
        }

        var activeRoomCount = 0;
        var maxActiveRoomCount = 0;
        var minutesWithOneActiveRoom = 0;
        var minutesWithTwoActiveRooms = 0;
        var minutesWithThreeOrMoreActiveRooms = 0;
        DateTimeOffset? previousAt = null;

        foreach (var point in events
            .GroupBy(item => item.At)
            .OrderBy(group => group.Key)
            .Select(group => new { At = group.Key, Delta = group.Sum(item => item.Delta) }))
        {
            if (previousAt.HasValue && point.At > previousAt.Value && activeRoomCount > 0)
            {
                var minutes = ObservedLoadWholeMinutesBetween(previousAt.Value, point.At);
                if (activeRoomCount == 1)
                {
                    minutesWithOneActiveRoom += minutes;
                }
                else if (activeRoomCount == 2)
                {
                    minutesWithTwoActiveRooms += minutes;
                }
                else
                {
                    minutesWithThreeOrMoreActiveRooms += minutes;
                }
            }

            activeRoomCount = Math.Max(0, activeRoomCount + point.Delta);
            maxActiveRoomCount = Math.Max(maxActiveRoomCount, activeRoomCount);
            previousAt = point.At;
        }

        return new ObservedRoomConcurrency(
            minutesWithOneActiveRoom,
            minutesWithTwoActiveRooms,
            minutesWithThreeOrMoreActiveRooms,
            maxActiveRoomCount);
    }

    private static int ObservedLoadWholeMinutesBetween(DateTimeOffset start, DateTimeOffset end) =>
        end <= start ? 0 : Math.Max(0, (int)(end - start).TotalMinutes);

    private IReadOnlyList<ProcedureCycleSummary> BuildProcedureSummaries(IReadOnlyList<CompletedRoomCycle> cycles) =>
        cycles
            .GroupBy(cycle => cycle.ProcedureCode ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProcedureCycleSummary(
                group.Key,
                ResolveProcedureLabel(group.Key),
                ResolveBaseProcedureCode(group.Key),
                IsSedationProcedureCode(group.Key),
                group.Count(),
                AverageSeconds(group.Select(cycle => cycle.TotalRoomCycleSeconds)),
                MedianSeconds(group.Select(cycle => cycle.TotalRoomCycleSeconds)),
                AverageSeconds(group.Select(cycle => cycle.ReadyToDoctorSeconds)),
                MedianSeconds(group.Select(cycle => cycle.ReadyToDoctorSeconds)),
                AverageSeconds(group.Select(cycle => cycle.DoctorInRoomSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorInRoomSeconds)),
                AverageSeconds(group.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
                AverageSeconds(group.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
                BuildAllocationVarianceSummary(group),
                BuildProcedureSamples(group)))
            .OrderByDescending(summary => summary.CompletedCycleCount)
            .ThenBy(summary => summary.ProcedureLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private IReadOnlyList<ProcedureCycleSummary> BuildBaseProcedureSummaries(
        IReadOnlyList<CompletedRoomCycle> cycles) =>
        cycles
            .GroupBy(cycle => ResolveBaseProcedureCode(cycle.ProcedureCode), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProcedureCycleSummary(
                group.Key,
                ResolveProcedureLabel(group.Key),
                group.Key,
                false,
                group.Count(),
                AverageSeconds(group.Select(cycle => cycle.TotalRoomCycleSeconds)),
                MedianSeconds(group.Select(cycle => cycle.TotalRoomCycleSeconds)),
                AverageSeconds(group.Select(cycle => cycle.ReadyToDoctorSeconds)),
                MedianSeconds(group.Select(cycle => cycle.ReadyToDoctorSeconds)),
                AverageSeconds(group.Select(cycle => cycle.DoctorInRoomSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorInRoomSeconds)),
                AverageSeconds(group.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
                AverageSeconds(group.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
                BuildAllocationVarianceSummary(group),
                BuildProcedureSamples(group)))
            .OrderByDescending(summary => summary.CompletedCycleCount)
            .ThenBy(summary => summary.ProcedureLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private IReadOnlyList<ScopedProcedurePopulation> BuildScopedProcedurePopulations(
        IReadOnlyList<CompletedRoomCycle> cycles,
        string grouping)
    {
        if (cycles.Count == 0)
        {
            return [];
        }

        if (string.Equals(
                grouping,
                ReportProcedureGroupings.DetailedVariant,
                StringComparison.OrdinalIgnoreCase))
        {
            return cycles
                .GroupBy(cycle => cycle.ProcedureCode ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(group => new ScopedProcedurePopulation(
                    group.Key,
                    ResolveProcedureLabel(group.Key),
                    ResolveBaseProcedureCode(group.Key),
                    IsSedationProcedureCode(group.Key),
                    group.ToList()))
                .OrderByDescending(population => population.Cycles.Count)
                .ThenBy(population => population.ProcedureLabel, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return cycles
            .GroupBy(cycle => ResolveBaseProcedureCode(cycle.ProcedureCode), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ScopedProcedurePopulation(
                group.Key,
                ResolveProcedureLabel(group.Key),
                group.Key,
                null,
                group.ToList()))
            .OrderByDescending(population => population.Cycles.Count)
            .ThenBy(population => population.ProcedureLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<ScopedProcedureGroup> BuildScopedProcedureGroups(
        IReadOnlyList<ScopedProcedurePopulation> populations,
        int scopedPopulationCount) =>
        populations
            .Select(population => new ScopedProcedureGroup(
                population.ProcedureCode,
                population.ProcedureLabel,
                population.BaseProcedureCode,
                population.IsSedationCase,
                population.Cycles.Count,
                scopedPopulationCount,
                scopedPopulationCount == 0
                    ? 0d
                    : (double)population.Cycles.Count / scopedPopulationCount,
                ReportSampleContext.ForPopulation(population.Cycles.Count)))
            .ToList();

    private IReadOnlyList<ProcedureIntelligenceRow> BuildProcedureIntelligenceRows(
        IReadOnlyList<ScopedProcedurePopulation> populations,
        ReportQuery query) =>
        populations
            .Select(population =>
            {
                var rosterProcedure = FindActiveProcedure(population.BaseProcedureCode);
                return new ProcedureIntelligenceRow(
                    population.ProcedureCode,
                    population.ProcedureLabel,
                    population.BaseProcedureCode,
                    query.ProcedureGrouping,
                    population.IsSedationCase,
                    rosterProcedure is null ? null : rosterProcedure.DefaultExpectedUnits * 10,
                    rosterProcedure?.AllocationBehavior,
                    BuildProcedureIntelligenceMetrics(population.Cycles),
                    query.Scope == ReportScopeKinds.Doctor
                        ? []
                        : BuildDoctorProcedureIntelligence(population.Cycles));
            })
            .ToList();

    private IReadOnlyList<ScheduleFitSegment> BuildScheduleFitProcedureSegments(
        IReadOnlyList<ScopedProcedurePopulation> populations,
        ReportQuery query,
        CalibrationRuleSet rules) =>
        populations
            .Select(population =>
            {
                var rosterProcedure = FindActiveProcedure(population.BaseProcedureCode);
                var currentDefaultMinutes = rosterProcedure?.DefaultExpectedUnits is > 0
                    ? rosterProcedure.DefaultExpectedUnits * 10
                    : (int?)null;
                return new ScheduleFitSegment(
                    population.ProcedureCode,
                    population.ProcedureLabel,
                    population.BaseProcedureCode,
                    query.ProcedureGrouping,
                    population.IsSedationCase,
                    currentDefaultMinutes,
                    ExactScheduleFitCalculator.BuildHistoricalAssignedSummary(population.Cycles, rules),
                    ExactScheduleFitCalculator.EvaluateCurrentDefault(
                        population.Cycles,
                        currentDefaultMinutes,
                        rules),
                    query.Scope == ReportScopeKinds.Doctor
                        ? []
                        : BuildDoctorScheduleFitSegments(
                            population.Cycles,
                            currentDefaultMinutes,
                            rules));
            })
            .ToList();

    private IReadOnlyList<DoctorScheduleFitSegment> BuildDoctorScheduleFitSegments(
        IReadOnlyList<CompletedRoomCycle> cycles,
        int? currentDefaultMinutes,
        CalibrationRuleSet rules)
    {
        var represented = cycles
            .Where(cycle => !string.IsNullOrWhiteSpace(cycle.AssignedDoctor))
            .GroupBy(cycle => cycle.AssignedDoctor, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        return OrderedRepresentedDoctorIds(represented.Keys)
            .Select(doctorId => new DoctorScheduleFitSegment(
                doctorId,
                ResolveDoctorDisplayName(doctorId) ?? doctorId,
                ExactScheduleFitCalculator.BuildHistoricalAssignedSummary(represented[doctorId], rules),
                ExactScheduleFitCalculator.EvaluateCurrentDefault(
                    represented[doctorId],
                    currentDefaultMinutes,
                    rules)))
            .ToList();
    }

    private IReadOnlyList<DoctorScheduleFitSummary> BuildDoctorScheduleFitSummaries(
        IReadOnlyList<CompletedRoomCycle> cycles,
        ReportQuery query,
        CalibrationRuleSet rules)
    {
        var represented = cycles
            .Where(cycle => !string.IsNullOrWhiteSpace(cycle.AssignedDoctor))
            .GroupBy(cycle => cycle.AssignedDoctor, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> doctorIds = query.Scope == ReportScopeKinds.Doctor
            ? new[] { query.DoctorId }.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!)
            : _activeDoctors.Select(doctor => doctor.Id).Concat(
                OrderedRepresentedDoctorIds(represented.Keys)
                    .Where(doctorId => !_activeDoctors.Any(active =>
                        string.Equals(active.Id, doctorId, StringComparison.OrdinalIgnoreCase))));

        return doctorIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(doctorId =>
            {
                var population = represented.GetValueOrDefault(doctorId) ?? [];
                return new DoctorScheduleFitSummary(
                    doctorId,
                    ResolveDoctorDisplayName(doctorId) ?? doctorId,
                    ExactScheduleFitCalculator.BuildHistoricalAssignedSummary(population, rules));
            })
            .ToList();
    }

    private IEnumerable<string> OrderedRepresentedDoctorIds(IEnumerable<string> representedDoctorIds)
    {
        var represented = representedDoctorIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeIds = _activeDoctors.Select(doctor => doctor.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _activeDoctors
            .Select(doctor => doctor.Id)
            .Where(represented.Contains)
            .Concat(represented
                .Where(doctorId => !activeIds.Contains(doctorId))
                .OrderBy(doctorId => ResolveDoctorDisplayName(doctorId) ?? doctorId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(doctorId => doctorId, StringComparer.OrdinalIgnoreCase));
    }

    private IReadOnlyList<DoctorProcedureIntelligenceSegment> BuildDoctorProcedureIntelligence(
        IReadOnlyList<CompletedRoomCycle> cycles)
    {
        var represented = cycles
            .Where(cycle => !string.IsNullOrWhiteSpace(cycle.AssignedDoctor))
            .GroupBy(cycle => cycle.AssignedDoctor, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        return OrderedRepresentedDoctorIds(represented.Keys)
            .Select(doctorId => new DoctorProcedureIntelligenceSegment(
                doctorId,
                ResolveDoctorDisplayName(doctorId) ?? doctorId,
                BuildProcedureIntelligenceMetrics(represented[doctorId])))
            .ToList();
    }

    private static ProcedureIntelligenceMetrics BuildProcedureIntelligenceMetrics(
        IReadOnlyList<CompletedRoomCycle> cycles)
    {
        var populationCount = cycles.Count;
        var doctorTimeValues = cycles
            .Select(TruthfulDoctorTimeSeconds)
            .Where(value => value.HasValue)
            .Select(value => (double)value!.Value)
            .ToList();
        var readyWaitValues = cycles
            .Select(TruthfulReadyWaitSeconds)
            .Where(value => value.HasValue)
            .Select(value => (double)value!.Value)
            .ToList();
        var seatedToDoctorCompleteValues = cycles
            .Select(TruthfulSeatedToDoctorCompleteSeconds)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
        var historicalAssignedValues = cycles
            .Where(cycle => cycle.ExpectedAllocationMinutes > 0)
            .Select(cycle => cycle.ExpectedAllocationMinutes)
            .ToList();
        var historicalCapturedDefaultValues = cycles
            .Where(cycle => cycle.OriginalDefaultExpectedUnits > 0)
            .Select(cycle => cycle.OriginalDefaultExpectedUnits * 10)
            .ToList();

        var doctorTimeSample = ReportSampleContext.Create(populationCount, doctorTimeValues.Count);
        var typicalRange = ProcedureIntelligenceStatistics.TypicalDoctorTimeRange(
            doctorTimeValues,
            doctorTimeSample);

        return new ProcedureIntelligenceMetrics(
            populationCount,
            ReportSampleContext.ForPopulation(populationCount),
            ProcedureIntelligenceStatistics.Median(doctorTimeValues),
            ProcedureIntelligenceStatistics.Average(doctorTimeValues),
            typicalRange.LowerSeconds,
            typicalRange.UpperSeconds,
            ProcedureIntelligenceRangeMethods.Type7Iqr,
            doctorTimeSample,
            ProcedureIntelligenceStatistics.Median(readyWaitValues),
            ProcedureIntelligenceStatistics.Average(readyWaitValues),
            ReportSampleContext.Create(populationCount, readyWaitValues.Count),
            ProcedureIntelligenceStatistics.Median(seatedToDoctorCompleteValues),
            ProcedureIntelligenceStatistics.Average(seatedToDoctorCompleteValues),
            ReportSampleContext.Create(populationCount, seatedToDoctorCompleteValues.Count),
            ProcedureIntelligenceStatistics.Median(historicalAssignedValues.Select(value => (double)value).ToList()),
            ReportSampleContext.Create(populationCount, historicalAssignedValues.Count),
            BuildAllocationValueCounts(historicalAssignedValues),
            BuildAllocationValueCounts(historicalCapturedDefaultValues));
    }

    private static IReadOnlyList<ProcedureAllocationValueCount> BuildAllocationValueCounts(
        IEnumerable<int> values) =>
        values
            .GroupBy(value => value)
            .OrderBy(group => group.Key)
            .Select(group => new ProcedureAllocationValueCount(group.Key, group.Count()))
            .ToList();

    private static ReportProcedureMetricSampleContext BuildProcedureSamples(
        IEnumerable<CompletedRoomCycle> cycles)
    {
        var population = cycles.ToList();
        var populationCount = population.Count;
        return new ReportProcedureMetricSampleContext(
            ReportSampleContext.ForPopulation(populationCount),
            ReportSampleContext.Create(
                populationCount,
                population.Count(cycle => cycle.TotalRoomCycleSeconds.HasValue)),
            ReportSampleContext.Create(
                populationCount,
                population.Count(cycle => cycle.ReadyToDoctorSeconds.HasValue)),
            ReportSampleContext.Create(
                populationCount,
                population.Count(cycle => cycle.DoctorInRoomSeconds.HasValue)),
            ReportSampleContext.Create(
                populationCount,
                population.Count(cycle => cycle.DoctorOccupiedWaitSeconds.HasValue)),
            ReportSampleContext.Create(
                populationCount,
                population.Count(cycle => cycle.DoctorAvailableWaitSeconds.HasValue)),
            ReportSampleContext.Create(
                populationCount,
                population.Count(cycle => cycle.AllocationVarianceMinutes.HasValue)));
    }

    private IReadOnlyList<DoctorProcedureMixRow> BuildDoctorProcedureMix(IReadOnlyList<CompletedRoomCycle> cycles) =>
        cycles
            .Where(cycle => !string.IsNullOrWhiteSpace(cycle.AssignedDoctor))
            .GroupBy(cycle => cycle.AssignedDoctor!, StringComparer.OrdinalIgnoreCase)
            .SelectMany(doctorGroup =>
            {
                var doctorCompletedCaseCount = doctorGroup.Count();
                return doctorGroup
                    .GroupBy(cycle => cycle.ProcedureCode ?? "", StringComparer.OrdinalIgnoreCase)
                    .Select(procedureGroup => new DoctorProcedureMixRow(
                        doctorGroup.Key,
                        procedureGroup.Key,
                        ResolveProcedureLabel(procedureGroup.Key),
                        ResolveBaseProcedureCode(procedureGroup.Key),
                        IsSedationProcedureCode(procedureGroup.Key),
                        procedureGroup.Count(),
                        doctorCompletedCaseCount,
                        doctorCompletedCaseCount == 0
                            ? 0d
                            : (double)procedureGroup.Count() / doctorCompletedCaseCount));
            })
            .OrderBy(row => row.DoctorId, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(row => row.CaseCount)
            .ThenBy(row => row.ProcedureLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private string ResolveProcedureLabel(string procedureCode)
    {
        var label = ResolveProcedure(procedureCode)?.Label;
        if (!string.IsNullOrWhiteSpace(label))
        {
            return label;
        }

        return string.IsNullOrWhiteSpace(procedureCode) ? "Unknown" : procedureCode;
    }

    private void AnnotateReportingExceptions(IReadOnlyList<CompletedRoomCycle> cycles)
    {
        foreach (var cycle in cycles)
        {
            var reasons = new List<string>();

            var (isLegacy, isUnmapped) = ClassifyProcedureMapping(cycle.ProcedureCode);
            if (isUnmapped)
            {
                reasons.Add(ReportingExceptionReasons.UnmappedProcedure);
            }
            else if (isLegacy)
            {
                reasons.Add(ReportingExceptionReasons.LegacyProcedure);
            }

            if (HasExtremeDuration(cycle))
            {
                reasons.Add(ReportingExceptionReasons.ExtremeDuration);
            }

            if (CrossesCalendarDay(cycle))
            {
                reasons.Add(ReportingExceptionReasons.OvernightLifecycle);
            }

            if (cycle.DoctorArrivedAt is null)
            {
                reasons.Add(ReportingExceptionReasons.MissingTiming);
            }

            cycle.IsLegacyProcedure = isLegacy;
            cycle.IsUnmappedProcedure = isUnmapped;
            cycle.ReportingExceptionReasons = reasons;
            cycle.HasReportingException = reasons.Count > 0;
            cycle.IsExcludedFromStandardMetrics = reasons.Count > 0;
            cycle.DisplayProcedureLabel = BuildDisplayProcedureLabel(cycle.ProcedureCode, isLegacy, isUnmapped);
        }
    }

    private (bool IsLegacy, bool IsUnmapped) ClassifyProcedureMapping(string? procedureCode)
    {
        if (string.IsNullOrWhiteSpace(procedureCode))
        {
            return (false, true);
        }

        var baseCode = ResolveBaseProcedureCode(procedureCode);
        if (FindActiveProcedure(baseCode) is not null)
        {
            return (false, false);
        }

        return FindProcedure(baseCode) is not null ? (true, false) : (false, true);
    }

    private static bool HasExtremeDuration(CompletedRoomCycle cycle)
    {
        if (cycle.DoctorCompleteAt is { } completeAt && completeAt - cycle.SeatedAt > ExtremeCaseFlowThreshold)
        {
            return true;
        }

        return cycle.RoomAvailableAt is { } availableAt && availableAt - cycle.SeatedAt > ExtremeRoomCycleThreshold;
    }

    private static bool CrossesCalendarDay(CompletedRoomCycle cycle)
    {
        var seatedDate = cycle.SeatedAt.UtcDateTime.Date;
        if (cycle.DoctorCompleteAt is { } completeAt && completeAt.UtcDateTime.Date != seatedDate)
        {
            return true;
        }

        return cycle.RoomAvailableAt is { } availableAt && availableAt.UtcDateTime.Date != seatedDate;
    }

    private string BuildDisplayProcedureLabel(string? procedureCode, bool isLegacy, bool isUnmapped)
    {
        var label = ResolveProcedureLabel(procedureCode ?? "");
        if (isUnmapped)
        {
            return $"{label} (Unmapped)";
        }

        return isLegacy ? $"{label} (Legacy)" : label;
    }

    private static void AnnotateAllocationVariance(IReadOnlyList<CompletedRoomCycle> cycles)
    {
        foreach (var cycle in cycles)
        {
            cycle.MeasuredCaseFlowMinutes = null;
            cycle.AllocationVarianceMinutes = null;
            cycle.HasAllocationVariance = false;
            cycle.IsOverExpectedAllocation = false;
            cycle.IsUnderExpectedAllocation = false;
            cycle.IsAtExpectedAllocation = false;

            if (cycle.DoctorCompleteAt is not { } completeAt)
            {
                continue;
            }

            var measuredMinutes = Math.Max(0, (int)Math.Round((completeAt - cycle.SeatedAt).TotalMinutes));
            cycle.MeasuredCaseFlowMinutes = measuredMinutes;

            if (cycle.ExpectedAllocationMinutes <= 0)
            {
                continue;
            }

            var variance = measuredMinutes - cycle.ExpectedAllocationMinutes;
            cycle.AllocationVarianceMinutes = variance;
            cycle.IsOverExpectedAllocation = variance > 0;
            cycle.IsUnderExpectedAllocation = variance < 0;
            cycle.IsAtExpectedAllocation = variance == 0;
            cycle.HasAllocationVariance = variance != 0;
        }
    }

    private static AllocationVarianceSummary BuildAllocationVarianceSummary(
        IEnumerable<CompletedRoomCycle> cycles)
    {
        var population = cycles.ToList();
        var contributing = population.Where(cycle => cycle.AllocationVarianceMinutes.HasValue).ToList();

        var count = contributing.Count;
        var totalExpected = contributing.Sum(cycle => cycle.ExpectedAllocationMinutes);
        var totalMeasured = contributing.Sum(cycle => cycle.MeasuredCaseFlowMinutes ?? 0);
        var net = totalMeasured - totalExpected;

        return new AllocationVarianceSummary(
            count,
            totalExpected,
            totalMeasured,
            net,
            count == 0 ? 0 : (double)net / count,
            contributing.Count(cycle => cycle.IsOverExpectedAllocation),
            contributing.Count(cycle => cycle.IsUnderExpectedAllocation),
            contributing.Count(cycle => cycle.IsAtExpectedAllocation),
            population.Count(cycle => cycle.AllocationAdjustedFromDefault));
    }

    private static void AnnotateOccupiedWait(
        IReadOnlyList<CompletedRoomCycle> cyclesToAnnotate,
        IReadOnlyList<CompletedRoomCycle> blockerPool)
    {
        var eligibleBlockers = blockerPool
            .Where(cycle =>
                !cycle.IsException &&
                cycle.DoctorArrivedAt.HasValue &&
                cycle.DoctorCompleteAt.HasValue &&
                cycle.DoctorCompleteAt.Value > cycle.DoctorArrivedAt.Value)
            .ToList();

        foreach (var cycle in cyclesToAnnotate)
        {
            if (!cycle.ReadyForDoctorAt.HasValue ||
                !cycle.DoctorArrivedAt.HasValue ||
                cycle.ReadyToDoctorSeconds is null)
            {
                cycle.DoctorOccupiedWaitSeconds = null;
                cycle.DoctorAvailableWaitSeconds = null;
                continue;
            }

            var sameDocOtherIntervals = eligibleBlockers
                .Where(other =>
                    other.AssignedDoctor == cycle.AssignedDoctor &&
                    !(other.RoomId == cycle.RoomId && other.SeatedAt == cycle.SeatedAt))
                .Select(other => (Start: other.DoctorArrivedAt!.Value, End: other.DoctorCompleteAt!.Value))
                .ToList();

            var occupied = ComputeOverlapSeconds(
                cycle.ReadyForDoctorAt.Value,
                cycle.DoctorArrivedAt.Value,
                sameDocOtherIntervals);

            var readyToDoctor = cycle.ReadyToDoctorSeconds.Value;
            var clamped = Math.Min(occupied, readyToDoctor);
            cycle.DoctorOccupiedWaitSeconds = clamped;
            cycle.DoctorAvailableWaitSeconds = readyToDoctor - clamped;
        }
    }

    private static int ComputeOverlapSeconds(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        List<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        if (intervals.Count == 0 || windowEnd <= windowStart)
        {
            return 0;
        }

        long totalTicks = 0;
        foreach (var (start, end) in MergeIntervals(intervals))
        {
            var overlapStart = start > windowStart ? start : windowStart;
            var overlapEnd = end < windowEnd ? end : windowEnd;
            if (overlapEnd > overlapStart)
            {
                totalTicks += (overlapEnd - overlapStart).Ticks;
            }
        }

        return (int)Math.Round(totalTicks / (double)TimeSpan.TicksPerSecond);
    }

    private static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> MergeIntervals(
        IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        var sorted = intervals.OrderBy(interval => interval.Start).ToList();
        var result = new List<(DateTimeOffset Start, DateTimeOffset End)>();

        foreach (var (start, end) in sorted)
        {
            if (result.Count == 0 || start >= result[^1].End)
            {
                result.Add((start, end));
            }
            else if (end > result[^1].End)
            {
                result[^1] = (result[^1].Start, end);
            }
        }

        return result;
    }

    private string? ResolveDoctorDisplayName(string? doctorId) =>
        doctorId is null ? null : _doctors.FirstOrDefault(item => item.Id == doctorId)?.Name;

    private ProcedureCategory? FindProcedure(string procedureCode) =>
        _procedures.FirstOrDefault(item =>
            string.Equals(item.Id, procedureCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Code, procedureCode, StringComparison.OrdinalIgnoreCase));

    private ProcedureCategory? FindActiveProcedure(string procedureCode) =>
        _activeProcedures.FirstOrDefault(item =>
            string.Equals(item.Id, procedureCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Code, procedureCode, StringComparison.OrdinalIgnoreCase));

    private static bool HasSedationModifier(string? procedureCode) =>
        procedureCode is not null &&
        procedureCode.EndsWith(SedationModifierSuffix, StringComparison.OrdinalIgnoreCase);

    private static string StripSedationModifier(string procedureCode) =>
        HasSedationModifier(procedureCode)
            ? procedureCode[..^SedationModifierSuffix.Length]
            : procedureCode;

    private static string ComposeProcedureCode(string baseCode, bool sedation) =>
        sedation ? $"{baseCode}{SedationModifierSuffix}" : baseCode;

    private static bool IsSedationProcedureCode(string? procedureCode) =>
        HasSedationModifier(procedureCode) ||
        string.Equals(procedureCode, LegacySedationCode, StringComparison.OrdinalIgnoreCase);

    private static string ResolveBaseProcedureCode(string? procedureCode) =>
        string.IsNullOrWhiteSpace(procedureCode)
            ? ""
            : HasSedationModifier(procedureCode)
                ? StripSedationModifier(procedureCode)
                : procedureCode;

    private ProcedureCategory? ResolveProcedure(string? procedureCode)
    {
        if (string.IsNullOrWhiteSpace(procedureCode))
        {
            return null;
        }

        if (!HasSedationModifier(procedureCode))
        {
            return FindProcedure(procedureCode);
        }

        var baseProcedure = FindProcedure(StripSedationModifier(procedureCode));
        if (baseProcedure is null)
        {
            return FindProcedure(procedureCode);
        }

        return baseProcedure with
        {
            Id = $"{baseProcedure.Id}+sed",
            Code = ComposeProcedureCode(baseProcedure.Code, true),
            Label = $"{baseProcedure.Label} + Sedation"
        };
    }

    private static CompletedRoomCycle CopyCompletedCycle(CompletedRoomCycle cycle) =>
        new()
        {
            CompletedCycleId = cycle.CompletedCycleId,
            EpisodeId = cycle.EpisodeId,
            RoomId = cycle.RoomId,
            AcceptedReadyHandoffId = cycle.AcceptedReadyHandoffId,
            AssignedDoctor = cycle.AssignedDoctor,
            ProcedureCode = cycle.ProcedureCode,
            PrestageStartedAt = cycle.PrestageStartedAt,
            SeatedAt = cycle.SeatedAt,
            ReadyForDoctorAt = cycle.ReadyForDoctorAt,
            DoctorArrivedAt = cycle.DoctorArrivedAt,
            DoctorCompleteAt = cycle.DoctorCompleteAt,
            RoomAvailableAt = cycle.RoomAvailableAt,
            SeatedToDoctorSeconds = cycle.SeatedToDoctorSeconds,
            PrepSeconds = cycle.PrepSeconds,
            ReadyToDoctorSeconds = cycle.ReadyToDoctorSeconds,
            DoctorInRoomSeconds = cycle.DoctorInRoomSeconds,
            TurnoverSeconds = cycle.TurnoverSeconds,
            TotalRoomCycleSeconds = cycle.TotalRoomCycleSeconds,
            OriginalDefaultExpectedUnits = cycle.OriginalDefaultExpectedUnits,
            ExpectedAllocationUnits = cycle.ExpectedAllocationUnits,
            ExpectedAllocationMinutes = cycle.ExpectedAllocationMinutes,
            AllocationAdjustedFromDefault = cycle.AllocationAdjustedFromDefault,
            IsAddOn = cycle.IsAddOn,
            DoctorOccupiedWaitSeconds = cycle.DoctorOccupiedWaitSeconds,
            DoctorAvailableWaitSeconds = cycle.DoctorAvailableWaitSeconds,
            HasReportingException = cycle.HasReportingException,
            ReportingExceptionReasons = cycle.ReportingExceptionReasons.ToArray(),
            IsExcludedFromStandardMetrics = cycle.IsExcludedFromStandardMetrics,
            DisplayProcedureLabel = cycle.DisplayProcedureLabel,
            IsLegacyProcedure = cycle.IsLegacyProcedure,
            IsUnmappedProcedure = cycle.IsUnmappedProcedure,
            MeasuredCaseFlowMinutes = cycle.MeasuredCaseFlowMinutes,
            AllocationVarianceMinutes = cycle.AllocationVarianceMinutes,
            HasAllocationVariance = cycle.HasAllocationVariance,
            IsOverExpectedAllocation = cycle.IsOverExpectedAllocation,
            IsUnderExpectedAllocation = cycle.IsUnderExpectedAllocation,
            IsAtExpectedAllocation = cycle.IsAtExpectedAllocation,
            FinalWaitState = cycle.FinalWaitState,
            AgingThresholdReached = cycle.AgingThresholdReached,
            StaleThresholdReached = cycle.StaleThresholdReached,
            IsException = cycle.IsException,
            RequiresReview = cycle.RequiresReview,
            ExceptionReason = cycle.ExceptionReason,
            ReviewStatus = cycle.ReviewStatus,
            SuggestedAction = cycle.SuggestedAction,
            ReviewedAt = cycle.ReviewedAt,
            ReviewedBy = cycle.ReviewedBy
        };

    private static AbortedRoomAssignment CopyAbortedAssignment(AbortedRoomAssignment record) =>
        new()
        {
            AbortedAssignmentId = record.AbortedAssignmentId,
            EpisodeId = record.EpisodeId,
            RoomId = record.RoomId,
            AssignedDoctor = record.AssignedDoctor,
            AssignedDoctorDisplayName = record.AssignedDoctorDisplayName,
            ProcedureCode = record.ProcedureCode,
            ProcedureCategory = record.ProcedureCategory,
            SedationState = record.SedationState,
            ExpectedAllocationState = record.ExpectedAllocationState,
            ExpectedAllocationSuggestedUnits = record.ExpectedAllocationSuggestedUnits,
            ExpectedAllocationConfirmedUnits = record.ExpectedAllocationConfirmedUnits,
            TerminalReadyHandoffId = record.TerminalReadyHandoffId,
            IsAddOn = record.IsAddOn,
            OriginalDefaultExpectedUnits = record.OriginalDefaultExpectedUnits,
            ExpectedAllocationUnits = record.ExpectedAllocationUnits,
            ExpectedAllocationMinutes = record.ExpectedAllocationMinutes,
            AllocationAdjustedFromDefault = record.AllocationAdjustedFromDefault,
            PrestageStartedAt = record.PrestageStartedAt,
            SeatedAt = record.SeatedAt,
            ReadyForDoctorAt = record.ReadyForDoctorAt,
            TerminatedAt = record.TerminatedAt,
            TerminatedFromState = record.TerminatedFromState,
            TerminationKind = record.TerminationKind,
            CancellationReason = record.CancellationReason,
            IsException = record.IsException,
            RequiresReview = record.RequiresReview,
            ExceptionReason = record.ExceptionReason,
            ReviewStatus = record.ReviewStatus,
            SuggestedAction = record.SuggestedAction,
            ReviewedAt = record.ReviewedAt,
            ReviewedBy = record.ReviewedBy
        };
}
