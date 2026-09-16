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
    internal sealed record ScheduleFitFact(
        long CompletedCycleId,
        string? AcceptedReadyHandoffId,
        string? AssignedDoctor,
        int ExpectedAllocationMinutes,
        double? ObservedCaseFlowSeconds);

    internal static double? TruthfulObservedCaseFlowSeconds(CompletedRoomCycle cycle) =>
        cycle.DoctorCompleteAt is { } completeAt && cycle.SeatedAt <= completeAt
            ? (completeAt - cycle.SeatedAt).TotalSeconds
            : null;

    internal static IReadOnlyList<ScheduleFitFact> BuildFacts(
        IReadOnlyList<CompletedRoomCycle> population)
    {
        ArgumentNullException.ThrowIfNull(population);
        return BoundedReportCollections.Materialize(population.Select(cycle => new ScheduleFitFact(
            cycle.CompletedCycleId,
            cycle.AcceptedReadyHandoffId,
            cycle.AssignedDoctor,
            cycle.ExpectedAllocationMinutes,
            TruthfulObservedCaseFlowSeconds(cycle))));
    }

    internal static ScheduleFitSummary BuildHistoricalAssignedSummary(
        IReadOnlyList<CompletedRoomCycle> population,
        CalibrationRuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(population);
        var facts = BuildFacts(population);
        try
        {
            return BuildHistoricalAssignedSummary(facts, rules);
        }
        finally
        {
            (facts as IDisposable)?.Dispose();
        }
    }

    internal static ScheduleFitSummary BuildHistoricalAssignedSummary(
        IReadOnlyList<ScheduleFitFact> population,
        CalibrationRuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(population);
        var activeRules = rules ?? CalibrationRuleSet.VersionOne;
        var pairedCaseCount = 0;
        var totalExpected = 0d;
        var totalObserved = 0d;
        var totalSlack = 0d;
        var totalDebt = 0d;
        var lessTimeCaseCount = 0;
        var atExpectedCaseCount = 0;
        var moreTimeCaseCount = 0;
        var tolerance = activeRules.AtExpectedToleranceSeconds;
        foreach (var fact in population)
        {
            if (fact.ExpectedAllocationMinutes <= 0 || fact.ObservedCaseFlowSeconds is not { } observed)
            {
                continue;
            }

            var expected = fact.ExpectedAllocationMinutes * 60d;
            var variance = observed - expected;
            pairedCaseCount++;
            totalExpected += expected;
            totalObserved += observed;
            totalSlack += Math.Max(-variance, 0d);
            totalDebt += Math.Max(variance, 0d);
            if (variance < -tolerance) lessTimeCaseCount++;
            else if (variance > tolerance) moreTimeCaseCount++;
            else atExpectedCaseCount++;
        }

        using var expectedOrder = NumericOrderStatistics.Create(HistoricalPairs(population)
            .Select(fact => fact.ExpectedAllocationMinutes * 60d));
        using var observedOrder = NumericOrderStatistics.Create(HistoricalPairs(population)
            .Select(fact => fact.ObservedCaseFlowSeconds!.Value));
        using var varianceOrder = NumericOrderStatistics.Create(HistoricalPairs(population)
            .Select(fact => fact.ObservedCaseFlowSeconds!.Value - (fact.ExpectedAllocationMinutes * 60d)));

        return new ScheduleFitSummary(
            PopulationCount: population.Count,
            PairedCaseCount: pairedCaseCount,
            PopulationCoverage: population.Count == 0 ? 0d : (double)pairedCaseCount / population.Count,
            TotalExpectedSeconds: totalExpected,
            TotalObservedSeconds: totalObserved,
            TotalSlackSeconds: totalSlack,
            TotalDebtSeconds: totalDebt,
            NetVarianceSeconds: totalObserved - totalExpected,
            MedianExpectedSeconds: expectedOrder.Median,
            MedianObservedSeconds: observedOrder.Median,
            MedianPairedVarianceSeconds: varianceOrder.Median,
            LessTimeCaseCount: lessTimeCaseCount,
            AtExpectedCaseCount: atExpectedCaseCount,
            MoreTimeCaseCount: moreTimeCaseCount,
            Sample: ReportSampleContext.Create(population.Count, pairedCaseCount));
    }

    internal static CalibrationEvaluation EvaluateCurrentDefault(
        IReadOnlyList<CompletedRoomCycle> population,
        int? currentDefaultAllocationMinutes,
        CalibrationRuleSet? rules = null)
    {
        ArgumentNullException.ThrowIfNull(population);
        if (currentDefaultAllocationMinutes is not > 0)
        {
            return EvaluateCurrentDefault(
                Array.Empty<ScheduleFitFact>(),
                currentDefaultAllocationMinutes,
                rules);
        }

        var facts = BuildFacts(population);
        try
        {
            return EvaluateCurrentDefault(facts, currentDefaultAllocationMinutes, rules);
        }
        finally
        {
            (facts as IDisposable)?.Dispose();
        }
    }

    internal static CalibrationEvaluation EvaluateCurrentDefault(
        IReadOnlyList<ScheduleFitFact> population,
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
        var pairedCaseCount = 0;
        var aboveCount = 0;
        var belowCount = 0;
        var equalCount = 0;
        var moreThanToleranceCount = 0;
        var lessThanToleranceCount = 0;
        var atExpectedCount = 0;
        foreach (var fact in population)
        {
            if (fact.ObservedCaseFlowSeconds is not { } observed) continue;
            var variance = observed - baselineSeconds;
            pairedCaseCount++;
            if (variance > 0d) aboveCount++;
            else if (variance < 0d) belowCount++;
            else equalCount++;

            if (variance > tolerance) moreThanToleranceCount++;
            else if (variance < -tolerance) lessThanToleranceCount++;
            else atExpectedCount++;
        }

        var aboveShare = pairedCaseCount == 0 ? 0d : (double)aboveCount / pairedCaseCount;
        var belowShare = pairedCaseCount == 0 ? 0d : (double)belowCount / pairedCaseCount;
        var directionalShare = Math.Max(aboveShare, belowShare);
        using var varianceOrder = NumericOrderStatistics.Create(population
            .Where(fact => fact.ObservedCaseFlowSeconds.HasValue)
            .Select(fact => fact.ObservedCaseFlowSeconds!.Value - baselineSeconds));
        var medianVariance = varianceOrder.Median;

        string? candidateDirection = null;
        if (aboveShare >= activeRules.MinimumDirectionalShare)
        {
            candidateDirection = CalibrationInsightDirections.MoreTimeThanCurrentDefault;
        }
        else if (belowShare >= activeRules.MinimumDirectionalShare)
        {
            candidateDirection = CalibrationInsightDirections.LessTimeThanCurrentDefault;
        }

        var decision = pairedCaseCount < activeRules.MinimumPairedCases
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
            var evidence = BoundedReportCollections.OrderBy(population
                .Where(fact => fact.ObservedCaseFlowSeconds.HasValue)
                .Select(fact =>
                {
                    var observed = fact.ObservedCaseFlowSeconds!.Value;
                    var variance = observed - baselineSeconds;
                    return new CalibrationEvidenceCase(
                        fact.CompletedCycleId,
                        fact.AcceptedReadyHandoffId,
                        CalibrationBaselineSources.CurrentRosterDefault,
                        currentDefaultAllocationMinutes.Value,
                        observed,
                        variance,
                        RawDirection(variance),
                        ToleranceClassification(variance, tolerance));
                }),
                item => item.CompletedCycleId.ToString("D20", System.Globalization.CultureInfo.InvariantCulture),
                descending: false);

            insight = new CalibrationInsight(
                candidateDirection,
                medianVariance.Value,
                pairedCaseCount,
                directionalCount,
                oppositeCount,
                equalCount,
                atExpectedCount,
                evidence);
        }

        return new CalibrationEvaluation(
            decision,
            currentDefaultAllocationMinutes,
            pairedCaseCount,
            aboveCount,
            belowCount,
            equalCount,
            moreThanToleranceCount,
            lessThanToleranceCount,
            atExpectedCount,
            directionalShare,
            medianVariance,
            candidateDirection,
            insight);
    }

    private static IEnumerable<ScheduleFitFact> HistoricalPairs(IEnumerable<ScheduleFitFact> population) =>
        population.Where(fact =>
            fact.ExpectedAllocationMinutes > 0 && fact.ObservedCaseFlowSeconds.HasValue);

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
