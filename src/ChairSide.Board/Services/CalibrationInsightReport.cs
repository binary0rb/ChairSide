namespace ChairSide.Board.Services;

public static class ScheduleFitToleranceClassifications
{
    public const string LessTimeThanAllocation = "LessTimeThanAllocation";
    public const string AtExpected = "AtExpected";
    public const string MoreTimeThanAllocation = "MoreTimeThanAllocation";
}

public static class CalibrationRawDirections
{
    public const string BelowBaseline = "BelowBaseline";
    public const string EqualBaseline = "EqualBaseline";
    public const string AboveBaseline = "AboveBaseline";
}

public static class CalibrationDecisions
{
    public const string CurrentDefaultUnavailable = "CurrentDefaultUnavailable";
    public const string BelowMinimumSample = "BelowMinimumSample";
    public const string InsufficientDirectionalConsistency = "InsufficientDirectionalConsistency";
    public const string BelowMaterialDeviation = "BelowMaterialDeviation";
    public const string Qualified = "Qualified";
}

public static class CalibrationInsightDirections
{
    public const string MoreTimeThanCurrentDefault = "MoreTimeThanCurrentDefault";
    public const string LessTimeThanCurrentDefault = "LessTimeThanCurrentDefault";
}

public static class CalibrationBaselineSources
{
    public const string CurrentRosterDefault = "CurrentRosterDefault";
}

public sealed record CalibrationRuleSet(
    string Version,
    int MinimumPairedCases,
    int AtExpectedToleranceSeconds,
    double MinimumDirectionalShare,
    string DirectionalMethod,
    string DirectionalDenominator,
    int MaterialDeviationSeconds,
    string MaterialComparison,
    string CentralMethod,
    string PersistenceRequirement,
    string Baseline)
{
    public static CalibrationRuleSet VersionOne { get; } = new(
        Version: "1",
        MinimumPairedCases: 10,
        AtExpectedToleranceSeconds: 600,
        MinimumDirectionalShare: 0.80d,
        DirectionalMethod: "RawPairedVarianceSign",
        DirectionalDenominator: "AllPairedCases",
        MaterialDeviationSeconds: 600,
        MaterialComparison: "StrictlyGreaterThan",
        CentralMethod: "MedianPairedVariance",
        PersistenceRequirement: "SelectedPopulationOnly",
        Baseline: CalibrationBaselineSources.CurrentRosterDefault);
}

public sealed record ScheduleFitSummary(
    int PopulationCount,
    int PairedCaseCount,
    double PopulationCoverage,
    double TotalExpectedSeconds,
    double TotalObservedSeconds,
    double TotalSlackSeconds,
    double TotalDebtSeconds,
    double NetVarianceSeconds,
    double? MedianExpectedSeconds,
    double? MedianObservedSeconds,
    double? MedianPairedVarianceSeconds,
    int LessTimeCaseCount,
    int AtExpectedCaseCount,
    int MoreTimeCaseCount,
    ReportSampleContext Sample);

public sealed record CalibrationEvidenceCase(
    long CompletedCycleId,
    string? AcceptedReadyHandoffId,
    string BaselineSource,
    int BaselineMinutesUsed,
    double ObservedCaseFlowSeconds,
    double PairedVarianceSeconds,
    string RawDirection,
    string ToleranceClassification);

public sealed record CalibrationInsight(
    string Direction,
    double MedianDifferenceSeconds,
    int TotalPairedCaseCount,
    int DirectionalCaseCount,
    int OppositeDirectionCaseCount,
    int EqualCaseCount,
    int AtExpectedCaseCount,
    IReadOnlyList<CalibrationEvidenceCase> Evidence);

public sealed record CalibrationEvaluation(
    string Decision,
    int? CurrentDefaultAllocationMinutes,
    int TotalPairedCaseCount,
    int AboveBaselineCaseCount,
    int BelowBaselineCaseCount,
    int EqualBaselineCaseCount,
    int MoreThanToleranceCaseCount,
    int LessThanToleranceCaseCount,
    int AtExpectedCaseCount,
    double DirectionalShare,
    double? MedianPairedVarianceSeconds,
    string? CandidateDirection,
    CalibrationInsight? Insight);

public sealed record DoctorScheduleFitSegment(
    string DoctorId,
    string DoctorName,
    ScheduleFitSummary HistoricalAssignedFit,
    CalibrationEvaluation CurrentDefaultCalibration);

public sealed record ScheduleFitSegment(
    string ProcedureCode,
    string ProcedureLabel,
    string BaseProcedureCode,
    string ProcedureGrouping,
    bool? IsSedationCase,
    int? CurrentDefaultAllocationMinutes,
    ScheduleFitSummary HistoricalAssignedFit,
    CalibrationEvaluation CurrentDefaultCalibration,
    IReadOnlyList<DoctorScheduleFitSegment> DoctorBreakdown);

public sealed record DoctorScheduleFitSummary(
    string DoctorId,
    string DoctorName,
    ScheduleFitSummary HistoricalAssignedFit);

internal static class ExactScheduleFitCalculator
{
    public sealed record HistoricalPair(
        CompletedRoomCycle Cycle,
        double ExpectedSeconds,
        double ObservedSeconds,
        double VarianceSeconds);

    public sealed record CalibrationPair(
        CompletedRoomCycle Cycle,
        double ObservedSeconds,
        double VarianceSeconds,
        string RawDirection,
        string ToleranceClassification);

    internal static double? TruthfulObservedCaseFlowSeconds(CompletedRoomCycle cycle) =>
        cycle.DoctorCompleteAt is { } completeAt && cycle.SeatedAt <= completeAt
            ? (completeAt - cycle.SeatedAt).TotalSeconds
            : null;

    internal static ScheduleFitSummary BuildHistoricalAssignedSummary(
        IReadOnlyList<CompletedRoomCycle> population,
        CalibrationRuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(population);
        var activeRules = rules ?? CalibrationRuleSet.VersionOne;
        var pairs = BoundedReportCollections.Materialize(population
            .Where(cycle => cycle.ExpectedAllocationMinutes > 0)
            .Select(cycle =>
            {
                var observed = TruthfulObservedCaseFlowSeconds(cycle);
                if (!observed.HasValue)
                {
                    return null;
                }

                var expected = cycle.ExpectedAllocationMinutes * 60d;
                return new HistoricalPair(cycle, expected, observed.Value, observed.Value - expected);
            })
            .Where(pair => pair is not null)
            .Select(pair => pair!));

        var totalExpected = pairs.Sum(pair => pair.ExpectedSeconds);
        var totalObserved = pairs.Sum(pair => pair.ObservedSeconds);
        var tolerance = activeRules.AtExpectedToleranceSeconds;

        return new ScheduleFitSummary(
            PopulationCount: population.Count,
            PairedCaseCount: pairs.Count,
            PopulationCoverage: population.Count == 0 ? 0d : (double)pairs.Count / population.Count,
            TotalExpectedSeconds: totalExpected,
            TotalObservedSeconds: totalObserved,
            TotalSlackSeconds: pairs.Sum(pair => Math.Max(-pair.VarianceSeconds, 0d)),
            TotalDebtSeconds: pairs.Sum(pair => Math.Max(pair.VarianceSeconds, 0d)),
            NetVarianceSeconds: totalObserved - totalExpected,
            MedianExpectedSeconds: Median(pairs.Select(pair => pair.ExpectedSeconds)),
            MedianObservedSeconds: Median(pairs.Select(pair => pair.ObservedSeconds)),
            MedianPairedVarianceSeconds: Median(pairs.Select(pair => pair.VarianceSeconds)),
            LessTimeCaseCount: pairs.Count(pair => pair.VarianceSeconds < -tolerance),
            AtExpectedCaseCount: pairs.Count(pair => pair.VarianceSeconds >= -tolerance && pair.VarianceSeconds <= tolerance),
            MoreTimeCaseCount: pairs.Count(pair => pair.VarianceSeconds > tolerance),
            Sample: ReportSampleContext.Create(population.Count, pairs.Count));
    }

    internal static CalibrationEvaluation EvaluateCurrentDefault(
        IReadOnlyList<CompletedRoomCycle> population,
        int? currentDefaultAllocationMinutes,
        CalibrationRuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(population);
        var activeRules = rules ?? CalibrationRuleSet.VersionOne;
        if (currentDefaultAllocationMinutes is not > 0)
        {
            return new CalibrationEvaluation(
                CalibrationDecisions.CurrentDefaultUnavailable,
                currentDefaultAllocationMinutes,
                0, 0, 0, 0, 0, 0, 0, 0d, null, null, null);
        }

        var baselineSeconds = currentDefaultAllocationMinutes.Value * 60d;
        var tolerance = activeRules.AtExpectedToleranceSeconds;
        var pairs = BoundedReportCollections.Materialize(population
            .Select(cycle =>
            {
                var observed = TruthfulObservedCaseFlowSeconds(cycle);
                if (!observed.HasValue)
                {
                    return null;
                }

                var variance = observed.Value - baselineSeconds;
                return new CalibrationPair(
                    cycle,
                    observed.Value,
                    variance,
                    RawDirection(variance),
                    ToleranceClassification(variance, tolerance));
            })
            .Where(pair => pair is not null)
            .Select(pair => pair!));

        var aboveCount = pairs.Count(pair => pair.VarianceSeconds > 0d);
        var belowCount = pairs.Count(pair => pair.VarianceSeconds < 0d);
        var equalCount = pairs.Count - aboveCount - belowCount;
        var aboveShare = pairs.Count == 0 ? 0d : (double)aboveCount / pairs.Count;
        var belowShare = pairs.Count == 0 ? 0d : (double)belowCount / pairs.Count;
        var directionalShare = Math.Max(aboveShare, belowShare);
        var medianVariance = Median(pairs.Select(pair => pair.VarianceSeconds));

        string? candidateDirection = null;
        if (aboveShare >= activeRules.MinimumDirectionalShare)
        {
            candidateDirection = CalibrationInsightDirections.MoreTimeThanCurrentDefault;
        }
        else if (belowShare >= activeRules.MinimumDirectionalShare)
        {
            candidateDirection = CalibrationInsightDirections.LessTimeThanCurrentDefault;
        }

        var decision = pairs.Count < activeRules.MinimumPairedCases
            ? CalibrationDecisions.BelowMinimumSample
            : candidateDirection is null
                ? CalibrationDecisions.InsufficientDirectionalConsistency
                : !IsMaterialInCandidateDirection(candidateDirection, medianVariance, activeRules.MaterialDeviationSeconds)
                    ? CalibrationDecisions.BelowMaterialDeviation
                    : CalibrationDecisions.Qualified;

        CalibrationInsight? insight = null;
        if (decision == CalibrationDecisions.Qualified && candidateDirection is not null && medianVariance.HasValue)
        {
            var directionalCount = candidateDirection == CalibrationInsightDirections.MoreTimeThanCurrentDefault
                ? aboveCount
                : belowCount;
            var oppositeCount = candidateDirection == CalibrationInsightDirections.MoreTimeThanCurrentDefault
                ? belowCount
                : aboveCount;
            var evidence = BoundedReportCollections.OrderBy(pairs
                .Select(pair => new CalibrationEvidenceCase(
                    pair.Cycle.CompletedCycleId,
                    pair.Cycle.AcceptedReadyHandoffId,
                    CalibrationBaselineSources.CurrentRosterDefault,
                    currentDefaultAllocationMinutes.Value,
                    pair.ObservedSeconds,
                    pair.VarianceSeconds,
                    pair.RawDirection,
                    pair.ToleranceClassification)),
                item => item.CompletedCycleId.ToString("D20", System.Globalization.CultureInfo.InvariantCulture),
                descending: false);

            insight = new CalibrationInsight(
                candidateDirection,
                medianVariance.Value,
                pairs.Count,
                directionalCount,
                oppositeCount,
                equalCount,
                pairs.Count(pair => pair.ToleranceClassification == ScheduleFitToleranceClassifications.AtExpected),
                evidence);
        }

        return new CalibrationEvaluation(
            decision,
            currentDefaultAllocationMinutes,
            pairs.Count,
            aboveCount,
            belowCount,
            equalCount,
            pairs.Count(pair => pair.ToleranceClassification == ScheduleFitToleranceClassifications.MoreTimeThanAllocation),
            pairs.Count(pair => pair.ToleranceClassification == ScheduleFitToleranceClassifications.LessTimeThanAllocation),
            pairs.Count(pair => pair.ToleranceClassification == ScheduleFitToleranceClassifications.AtExpected),
            directionalShare,
            medianVariance,
            candidateDirection,
            insight);
    }

    internal static string RawDirection(double varianceSeconds) => varianceSeconds switch
    {
        > 0d => CalibrationRawDirections.AboveBaseline,
        < 0d => CalibrationRawDirections.BelowBaseline,
        _ => CalibrationRawDirections.EqualBaseline
    };

    internal static string ToleranceClassification(double varianceSeconds, int toleranceSeconds) =>
        varianceSeconds < -toleranceSeconds
            ? ScheduleFitToleranceClassifications.LessTimeThanAllocation
            : varianceSeconds > toleranceSeconds
                ? ScheduleFitToleranceClassifications.MoreTimeThanAllocation
                : ScheduleFitToleranceClassifications.AtExpected;

    internal static double? Median(IEnumerable<double> values)
    {
        return BoundedReportCollections.Median(values);
    }

    private static bool IsMaterialInCandidateDirection(
        string candidateDirection,
        double? medianVarianceSeconds,
        int materialDeviationSeconds) =>
        candidateDirection == CalibrationInsightDirections.MoreTimeThanCurrentDefault
            ? medianVarianceSeconds > materialDeviationSeconds
            : medianVarianceSeconds < -materialDeviationSeconds;
}
