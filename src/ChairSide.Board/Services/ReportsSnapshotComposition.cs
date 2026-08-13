namespace ChairSide.Board.Services;

/// <summary>
/// Internal report construction model. Each section owns one cohesive part of the flattened
/// <see cref="ReportsSnapshot"/> wire contract so report calculations can evolve without relying
/// on the order of one large positional constructor.
/// </summary>
internal sealed record ReportsSnapshotComposition
{
    public required ReportQueryContext Query { get; init; }
    public required ReportMetricSampleContext Samples { get; init; }
    public required ReportPopulationSection Population { get; init; }
    public required ReportWindowSection Window { get; init; }
    public required ReportTimingSection Timing { get; init; }
    public required ReportProcedureSection Procedures { get; init; }
    public required ReportAllocationSection Allocation { get; init; }
    public required ReportDoctorDetailSection DoctorDetail { get; init; }
    public required ReportReviewQueueSection ReviewQueue { get; init; }
}

/// <summary>
/// Selected-window population counts and the bounded recent-cycle display population.
/// ExceptionCount is the reporting-exception count within normal completed cycles; pending
/// manual-review records belong to <see cref="ReportReviewQueueSection"/>.
/// </summary>
internal sealed record ReportPopulationSection
{
    public required int CompletedRoomCyclesCount { get; init; }
    public required IReadOnlyList<CompletedRoomCycle> RecentCompletedCycles { get; init; }
    public required int IncludedCompletedCycleCount { get; init; }
    public required int ExcludedCompletedCycleCount { get; init; }
    public required int ExceptionCount { get; init; }
}

/// <summary>
/// Active report-window metadata plus the all-time completed total used for range context.
/// </summary>
internal sealed record ReportWindowSection
{
    public required string? RangeStartDate { get; init; }
    public required string? RangeEndDate { get; init; }
    public required string RangeLabel { get; init; }
    public required int TotalCompletedCycleCount { get; init; }
}

/// <summary>
/// Global operational timing aggregates, Ready urgency counts, and weekly timing trends.
/// </summary>
internal sealed record ReportTimingSection
{
    public required double AverageSeatedToDoctorSeconds { get; init; }
    public required double MedianSeatedToDoctorSeconds { get; init; }
    public required double AveragePrepSeconds { get; init; }
    public required double MedianPrepSeconds { get; init; }
    public required double AverageReadyToDoctorSeconds { get; init; }
    public required double MedianReadyToDoctorSeconds { get; init; }
    public required double AverageDoctorInRoomSeconds { get; init; }
    public required double MedianDoctorInRoomSeconds { get; init; }
    public required double AverageTurnoverSeconds { get; init; }
    public required double MedianTurnoverSeconds { get; init; }
    public required int AgingEventCount { get; init; }
    public required int StaleEventCount { get; init; }
    public required double AverageDoctorOccupiedWaitSeconds { get; init; }
    public required double MedianDoctorOccupiedWaitSeconds { get; init; }
    public required double AverageDoctorAvailableWaitSeconds { get; init; }
    public required double MedianDoctorAvailableWaitSeconds { get; init; }
    public required ReportTrendSnapshot? Trends { get; init; }
}

/// <summary>
/// Procedure-variant and base-procedure summaries plus the sedation-modifier partition.
/// </summary>
internal sealed record ReportProcedureSection
{
    public required IReadOnlyList<ProcedureCycleSummary> ProcedureSummaries { get; init; }
    public required int SedationCaseCount { get; init; }
    public required int NonSedationCaseCount { get; init; }
    public required IReadOnlyList<ProcedureCycleSummary> BaseProcedureSummaries { get; init; }
    public required IReadOnlyList<ScopedProcedureGroup> ScopedProcedureGroups { get; init; }
    public required IReadOnlyList<ProcedureIntelligenceRow> ProcedureIntelligenceRows { get; init; }
}

/// <summary>
/// Expected-versus-measured allocation read models over the standard completed population.
/// </summary>
internal sealed record ReportAllocationSection
{
    public required AllocationVarianceSummary? AllocationVariance { get; init; }
    public required IReadOnlyList<DoctorDailyAllocation>? DoctorDailyAllocationSeries { get; init; }
    public required ScheduleFitReport? ScheduleFit { get; init; }
}

/// <summary>
/// Doctor-oriented summaries and descriptive detail projections.
/// </summary>
internal sealed record ReportDoctorDetailSection
{
    public required IReadOnlyList<DoctorCycleSummary> DoctorSummaries { get; init; }
    public required IReadOnlyList<ReportDoctorAllocationSampleContext> DoctorAllocationSamples { get; init; }
    public required IReadOnlyList<DoctorFlowSummary> DoctorFlowSummaries { get; init; }
    public required IReadOnlyList<DoctorFlowTrendSeries> DoctorFlowTrends { get; init; }
    public required IReadOnlyList<ObservedDoctorDay>? ObservedDoctorDays { get; init; }
    public required IReadOnlyList<ObservedDoctorFlowDay>? ObservedDoctorFlowDays { get; init; }
    public required IReadOnlyList<DoctorProcedureMixRow>? DoctorProcedureMix { get; init; }
}

/// <summary>
/// Pending manual-review projections. Completed exceptions retain their completed-cycle shape,
/// while the unified queue also represents eligible aborted assignments without fabricating data.
/// </summary>
internal sealed record ReportReviewQueueSection
{
    public required IReadOnlyList<CompletedRoomCycle> ExceptionCycles { get; init; }
    public required IReadOnlyList<ExceptionReviewRecord>? ExceptionReviewRecords { get; init; }
}

/// <summary>
/// Flattens the internal named composition into the existing public JSON response DTO.
/// Every mapping is named explicitly so constructor order cannot change report meaning.
/// </summary>
internal static class ReportsSnapshotAdapter
{
    public static ReportsSnapshot ToSnapshot(ReportsSnapshotComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);

        return new ReportsSnapshot(
            CompletedRoomCyclesCount: composition.Population.CompletedRoomCyclesCount,
            AverageSeatedToDoctorSeconds: composition.Timing.AverageSeatedToDoctorSeconds,
            MedianSeatedToDoctorSeconds: composition.Timing.MedianSeatedToDoctorSeconds,
            AveragePrepSeconds: composition.Timing.AveragePrepSeconds,
            MedianPrepSeconds: composition.Timing.MedianPrepSeconds,
            AverageReadyToDoctorSeconds: composition.Timing.AverageReadyToDoctorSeconds,
            MedianReadyToDoctorSeconds: composition.Timing.MedianReadyToDoctorSeconds,
            AverageDoctorInRoomSeconds: composition.Timing.AverageDoctorInRoomSeconds,
            MedianDoctorInRoomSeconds: composition.Timing.MedianDoctorInRoomSeconds,
            AverageTurnoverSeconds: composition.Timing.AverageTurnoverSeconds,
            MedianTurnoverSeconds: composition.Timing.MedianTurnoverSeconds,
            AgingEventCount: composition.Timing.AgingEventCount,
            StaleEventCount: composition.Timing.StaleEventCount,
            AverageDoctorOccupiedWaitSeconds: composition.Timing.AverageDoctorOccupiedWaitSeconds,
            MedianDoctorOccupiedWaitSeconds: composition.Timing.MedianDoctorOccupiedWaitSeconds,
            AverageDoctorAvailableWaitSeconds: composition.Timing.AverageDoctorAvailableWaitSeconds,
            MedianDoctorAvailableWaitSeconds: composition.Timing.MedianDoctorAvailableWaitSeconds,
            DoctorSummaries: composition.DoctorDetail.DoctorSummaries,
            RecentCompletedCycles: composition.Population.RecentCompletedCycles,
            ExceptionCycles: composition.ReviewQueue.ExceptionCycles,
            ProcedureSummaries: composition.Procedures.ProcedureSummaries,
            SedationCaseCount: composition.Procedures.SedationCaseCount,
            NonSedationCaseCount: composition.Procedures.NonSedationCaseCount,
            BaseProcedureSummaries: composition.Procedures.BaseProcedureSummaries,
            IncludedCompletedCycleCount: composition.Population.IncludedCompletedCycleCount,
            ExcludedCompletedCycleCount: composition.Population.ExcludedCompletedCycleCount,
            ExceptionCount: composition.Population.ExceptionCount,
            AllocationVariance: composition.Allocation.AllocationVariance,
            RangeStartDate: composition.Window.RangeStartDate,
            RangeEndDate: composition.Window.RangeEndDate,
            RangeLabel: composition.Window.RangeLabel,
            TotalCompletedCycleCount: composition.Window.TotalCompletedCycleCount,
            DoctorDailyAllocationSeries: composition.Allocation.DoctorDailyAllocationSeries,
            ScheduleFit: composition.Allocation.ScheduleFit,
            Trends: composition.Timing.Trends,
            ObservedDoctorDays: composition.DoctorDetail.ObservedDoctorDays,
            DoctorProcedureMix: composition.DoctorDetail.DoctorProcedureMix,
            ExceptionReviewRecords: composition.ReviewQueue.ExceptionReviewRecords,
            Query: composition.Query,
            Samples: composition.Samples,
            ScopedProcedureGroups: composition.Procedures.ScopedProcedureGroups,
            DoctorAllocationSamples: composition.DoctorDetail.DoctorAllocationSamples,
            ObservedDoctorFlowDays: composition.DoctorDetail.ObservedDoctorFlowDays,
            DoctorFlowSummaries: composition.DoctorDetail.DoctorFlowSummaries,
            DoctorFlowTrends: composition.DoctorDetail.DoctorFlowTrends,
            ProcedureIntelligenceRows: composition.Procedures.ProcedureIntelligenceRows);
    }
}
