namespace ChairSide.Board.Services;

public static class ProcedureIntelligenceRangeMethods
{
    public const string Type7Iqr = "Type7Iqr";
}

public sealed record ProcedureAllocationValueCount(
    int Minutes,
    int CaseCount);

/// <summary>
/// Shared nonrecursive metrics for one scoped procedure population or one represented doctor
/// segment. Typical Doctor Time Range deliberately reuses DoctorTimeSample rather than carrying
/// an independently drifting sample context.
/// </summary>
public sealed record ProcedureIntelligenceMetrics(
    int CompletedCaseCount,
    ReportSampleContext CompletedSample,
    double? MedianDoctorTimeSeconds,
    double? AverageDoctorTimeSeconds,
    double? TypicalDoctorTimeLowerSeconds,
    double? TypicalDoctorTimeUpperSeconds,
    string TypicalDoctorTimeMethod,
    ReportSampleContext DoctorTimeSample,
    double? MedianReadyWaitSeconds,
    double? AverageReadyWaitSeconds,
    ReportSampleContext ReadyWaitSample,
    double? MedianSeatedToDoctorCompleteSeconds,
    double? AverageSeatedToDoctorCompleteSeconds,
    ReportSampleContext SeatedToDoctorCompleteSample,
    double? MedianHistoricalAssignedAllocationMinutes,
    ReportSampleContext HistoricalAssignedAllocationSample,
    IReadOnlyList<ProcedureAllocationValueCount> HistoricalAssignedAllocationValues,
    IReadOnlyList<ProcedureAllocationValueCount> HistoricalCapturedDefaultValues);

public sealed record DoctorProcedureIntelligenceSegment(
    string DoctorId,
    string DoctorName,
    ProcedureIntelligenceMetrics Metrics);

/// <summary>
/// Additive Procedure Intelligence projection. Each row corresponds exactly to one
/// ScopedProcedureGroup population built from the same underlying scoped completed cases.
/// </summary>
public sealed record ProcedureIntelligenceRow(
    string ProcedureCode,
    string ProcedureLabel,
    string BaseProcedureCode,
    string ProcedureGrouping,
    bool? IsSedationCase,
    int? CurrentDefaultAllocationMinutes,
    string? AllocationBehavior,
    ProcedureIntelligenceMetrics Metrics,
    IReadOnlyList<DoctorProcedureIntelligenceSegment> DoctorBreakdown);

internal static class ProcedureIntelligenceStatistics
{
    internal static (double? LowerSeconds, double? UpperSeconds) TypicalDoctorTimeRange(
        IReadOnlyList<double> orderedOrUnorderedValues,
        ReportSampleContext doctorTimeSample)
    {
        ArgumentNullException.ThrowIfNull(orderedOrUnorderedValues);
        ArgumentNullException.ThrowIfNull(doctorTimeSample);

        if (!string.Equals(
                doctorTimeSample.State,
                ReportSampleStates.Sufficient,
                StringComparison.Ordinal))
        {
            return (null, null);
        }

        if (orderedOrUnorderedValues.Count != doctorTimeSample.ContributingCount)
        {
            throw new ArgumentException(
                "Doctor Time values must match the shared contributing sample count.",
                nameof(orderedOrUnorderedValues));
        }

        var ordered = orderedOrUnorderedValues.Order().ToArray();
        return (Type7Quantile(ordered, 0.25d), Type7Quantile(ordered, 0.75d));
    }

    internal static double Type7Quantile(IReadOnlyList<double> orderedValues, double probability)
    {
        ArgumentNullException.ThrowIfNull(orderedValues);
        if (orderedValues.Count == 0)
        {
            throw new ArgumentException("At least one observation is required.", nameof(orderedValues));
        }

        if (probability is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(probability));
        }

        var h = (orderedValues.Count - 1) * probability;
        var lowerIndex = (int)Math.Floor(h);
        var fraction = h - lowerIndex;
        if (fraction == 0d || lowerIndex == orderedValues.Count - 1)
        {
            return orderedValues[lowerIndex];
        }

        return orderedValues[lowerIndex]
            + fraction * (orderedValues[lowerIndex + 1] - orderedValues[lowerIndex]);
    }

    internal static double? Median(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return null;
        }

        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2d;
    }

    internal static double? Average(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Count == 0 ? null : values.Average();
    }
}
