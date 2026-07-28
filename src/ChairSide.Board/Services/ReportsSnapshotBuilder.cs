using System.Globalization;

namespace ChairSide.Board.Services;

/// <summary>
/// Builds the complete reports read model from detached historical inputs.
/// Population selection and aggregation are intentionally independent of live room mutation,
/// persistence, and lifecycle synchronization.
/// </summary>
internal sealed class ReportsSnapshotBuilder
{
    private static readonly TimeSpan ExtremeCaseFlowThreshold = TimeSpan.FromHours(4);
    private static readonly TimeSpan ExtremeRoomCycleThreshold = TimeSpan.FromHours(6);

    private const string SedationModifierSuffix = "+SED";
    private const string LegacySedationCode = "SED";

    private readonly IReadOnlyList<Doctor> _doctors;
    private readonly IReadOnlyList<ProcedureCategory> _procedures;
    private readonly IReadOnlyList<ProcedureCategory> _activeProcedures;

    public ReportsSnapshotBuilder(
        IEnumerable<Doctor> doctors,
        IEnumerable<ProcedureCategory> procedures,
        IEnumerable<ProcedureCategory> activeProcedures)
    {
        ArgumentNullException.ThrowIfNull(doctors);
        ArgumentNullException.ThrowIfNull(procedures);
        ArgumentNullException.ThrowIfNull(activeProcedures);

        _doctors = doctors.ToArray();
        _procedures = procedures.ToArray();
        _activeProcedures = activeProcedures.ToArray();
    }

    public ReportsSnapshot Build(
        IReadOnlyList<CompletedRoomCycle> completedCycles,
        IReadOnlyList<AbortedRoomAssignment> abortedAssignments,
        ReportDateRange range)
    {
        ArgumentNullException.ThrowIfNull(completedCycles);
        ArgumentNullException.ThrowIfNull(abortedAssignments);

        // All-time completed total (for "X of Y" context), independent of the selected window.
        var totalCompletedAllTime = completedCycles.Count(cycle => cycle.RoomAvailableAt is not null);

        // Apply the completion window before copying or deriving report-time annotations. This
        // preserves the established DoctorCompleteAt boundary and leaves out-of-window history
        // completely uninspected by downstream reporting calculations.
        var selectedCycles = completedCycles
            .Where(cycle => range.Includes(cycle.DoctorCompleteAt))
            .ToList();

        // Report-time annotations are applied only to detached copies. The store's durable
        // historical snapshots remain untouched by report reads.
        var allCycles = CreateAnnotatedCompletedCycleSnapshot(selectedCycles)
            .OrderByDescending(cycle => cycle.DoctorArrivedAt)
            .ToList();
        var detachedAbortedAssignments = abortedAssignments.Select(CopyAbortedAssignment).ToList();

        // Manual review exceptions are excluded from normal operational metrics by default.
        var normalCycles = allCycles.Where(cycle => !cycle.IsException).ToList();
        // Standard population additionally drops reporting-exception cycles (legacy/unmapped/
        // extreme/overnight) so doctor-facing aggregates are not skewed by sample/legacy data.
        var standardCycles = normalCycles.Where(cycle => !cycle.IsExcludedFromStandardMetrics).ToList();

        // Raw/audit completed set keeps reporting-exception cycles visible (with badges);
        // the standard completed set drives aggregates.
        var normalCompletedCycles = normalCycles
            .Where(cycle => cycle.RoomAvailableAt is not null)
            .ToList();
        var standardCompletedCycles = standardCycles
            .Where(cycle => cycle.RoomAvailableAt is not null)
            .ToList();

        // "Exceptions Requiring Review" is a pending-review queue: only exceptions that still
        // require review appear here. A reviewed exception remains IsException (and therefore
        // excluded from normal metrics) but drops out of this queue once its review is confirmed.
        var exceptionCycles = allCycles
            .Where(cycle => cycle.IsException && cycle.RequiresReview)
            .OrderByDescending(cycle => cycle.SeatedAt)
            .ToList();
        var exceptionReviewRecords = BuildExceptionReviewRecords(exceptionCycles, detachedAbortedAssignments);

        // Annotate the raw display set with doctor-occupied / available wait, but use only the
        // standard population as the blocker pool so an extreme/overnight outlier never distorts
        // another cycle's occupied wait.
        AnnotateOccupiedWait(normalCycles, standardCycles);

        return new ReportsSnapshot(
            normalCompletedCycles.Count,
            AverageSeconds(standardCycles.Select(cycle => (int?)cycle.SeatedToDoctorSeconds)),
            MedianSeconds(standardCycles.Select(cycle => (int?)cycle.SeatedToDoctorSeconds)),
            AverageSeconds(standardCycles.Select(cycle => cycle.PrepSeconds)),
            MedianSeconds(standardCycles.Select(cycle => cycle.PrepSeconds)),
            AverageSeconds(standardCycles.Select(cycle => cycle.ReadyToDoctorSeconds)),
            MedianSeconds(standardCycles.Select(cycle => cycle.ReadyToDoctorSeconds)),
            AverageSeconds(standardCycles.Select(cycle => cycle.DoctorInRoomSeconds)),
            MedianSeconds(standardCycles.Select(cycle => cycle.DoctorInRoomSeconds)),
            AverageSeconds(standardCycles.Select(cycle => cycle.TurnoverSeconds)),
            MedianSeconds(standardCycles.Select(cycle => cycle.TurnoverSeconds)),
            standardCycles.Count(cycle => cycle.AgingThresholdReached),
            standardCycles.Count(cycle => cycle.StaleThresholdReached),
            AverageSeconds(standardCycles.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
            MedianSeconds(standardCycles.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
            AverageSeconds(standardCycles.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
            MedianSeconds(standardCycles.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
            BuildDoctorSummaries(standardCycles),
            normalCompletedCycles.Take(25).ToList(),
            exceptionCycles,
            BuildProcedureSummaries(standardCompletedCycles),
            standardCompletedCycles.Count(cycle => IsSedationProcedureCode(cycle.ProcedureCode)),
            standardCompletedCycles.Count(cycle => !IsSedationProcedureCode(cycle.ProcedureCode)),
            BuildBaseProcedureSummaries(standardCompletedCycles),
            standardCompletedCycles.Count,
            normalCompletedCycles.Count - standardCompletedCycles.Count,
            normalCompletedCycles.Count(cycle => cycle.HasReportingException),
            BuildAllocationVarianceSummary(standardCompletedCycles),
            range.StartDateText,
            range.EndDateText,
            range.Label,
            totalCompletedAllTime,
            BuildDoctorDailyAllocationSeries(standardCycles),
            ScheduleFitReportBuilder.Build(standardCompletedCycles),
            ReportTrendSnapshotBuilder.BuildWeekly(standardCompletedCycles),
            BuildObservedDoctorDays(standardCompletedCycles),
            BuildDoctorProcedureMix(standardCompletedCycles),
            exceptionReviewRecords);
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
                BuildAllocationVarianceSummary(group)))
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
                BuildAllocationVarianceSummary(group)))
            .OrderByDescending(summary => summary.CompletedCycleCount)
            .ThenBy(summary => summary.ProcedureLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

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
            return (false, false);
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
