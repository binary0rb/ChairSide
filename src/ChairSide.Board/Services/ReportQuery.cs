namespace ChairSide.Board.Services;

public static class ReportScopeKinds
{
    public const string Practice = "Practice";
    public const string Doctor = "Doctor";
}

public static class ReportSedationSegments
{
    public const string All = "All";
    public const string Sedation = "Sedation";
    public const string NonSedation = "NonSedation";
}

public static class ReportProcedureGroupings
{
    public const string Family = "Family";
    public const string DetailedVariant = "DetailedVariant";
}

public static class ReportSampleStates
{
    public const string Empty = "Empty";
    public const string Unavailable = "Unavailable";
    public const string Limited = "Limited";
    public const string Sufficient = "Sufficient";
}

/// <summary>
/// Normalized report query. Window and analytical scope narrow the source population, while
/// ProcedureGrouping selects an aggregation lens without changing population membership.
/// </summary>
public readonly record struct ReportQuery(
    ReportDateRange Window,
    string Scope,
    string? DoctorId,
    string Sedation,
    string ProcedureGrouping)
{
    private const string SedationModifierSuffix = "+SED";
    private const string LegacySedationCode = "SED";

    public static ReportQuery Default => new(
        ReportDateRange.AllTime,
        ReportScopeKinds.Practice,
        null,
        ReportSedationSegments.All,
        ReportProcedureGroupings.Family);

    public static ReportQuery FromStrings(
        string? from,
        string? to,
        string? scope,
        string? doctorId,
        string? sedation,
        string? procedureGrouping) =>
        new(
            ReportDateRange.FromDateStrings(from, to),
            NormalizeChoice(scope, ReportScopeKinds.Practice, ReportScopeKinds.Doctor),
            NormalizeDoctorId(doctorId),
            NormalizeChoice(
                sedation,
                ReportSedationSegments.All,
                ReportSedationSegments.Sedation,
                ReportSedationSegments.NonSedation),
            NormalizeChoice(
                procedureGrouping,
                ReportProcedureGroupings.Family,
                ReportProcedureGroupings.DetailedVariant));

    public bool IncludesAnalyticalCycle(CompletedRoomCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        if (Scope == ReportScopeKinds.Doctor
            && (DoctorId is null
                || !string.Equals(cycle.AssignedDoctor, DoctorId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var isSedationCase = IsSedationProcedureCode(cycle.ProcedureCode);
        return Sedation switch
        {
            ReportSedationSegments.Sedation => isSedationCase,
            ReportSedationSegments.NonSedation => !isSedationCase,
            _ => true
        };
    }

    public ReportQueryContext ToContext() => new(
        Scope,
        DoctorId,
        Sedation,
        ProcedureGrouping,
        Window.StartDateText,
        Window.EndDateText,
        Window.Label);

    private static string NormalizeChoice(string? value, string defaultValue, params string[] alternatives)
    {
        if (string.Equals(value, defaultValue, StringComparison.OrdinalIgnoreCase))
        {
            return defaultValue;
        }

        foreach (var alternative in alternatives)
        {
            if (string.Equals(value, alternative, StringComparison.OrdinalIgnoreCase))
            {
                return alternative;
            }
        }

        return defaultValue;
    }

    private static string? NormalizeDoctorId(string? doctorId) =>
        string.IsNullOrWhiteSpace(doctorId) ? null : doctorId.Trim();

    private static bool IsSedationProcedureCode(string? procedureCode) =>
        !string.IsNullOrWhiteSpace(procedureCode)
        && (procedureCode.EndsWith(SedationModifierSuffix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(procedureCode, LegacySedationCode, StringComparison.OrdinalIgnoreCase));
}

public sealed record ReportQueryContext(
    string Scope,
    string? DoctorId,
    string Sedation,
    string ProcedureGrouping,
    string? RangeStartDate,
    string? RangeEndDate,
    string RangeLabel);

public sealed record ReportSampleContext(
    int PopulationCount,
    int ContributingCount,
    string State,
    int LimitedSampleThreshold,
    bool SupportsComparison)
{
    public const int GeneralDescriptiveThreshold = 5;

    public static ReportSampleContext Create(int populationCount, int contributingCount)
    {
        if (populationCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(populationCount));
        }

        if (contributingCount < 0 || contributingCount > populationCount)
        {
            throw new ArgumentOutOfRangeException(nameof(contributingCount));
        }

        var state = populationCount == 0
            ? ReportSampleStates.Empty
            : contributingCount == 0
                ? ReportSampleStates.Unavailable
                : contributingCount < GeneralDescriptiveThreshold
                    ? ReportSampleStates.Limited
                    : ReportSampleStates.Sufficient;

        return new ReportSampleContext(
            populationCount,
            contributingCount,
            state,
            GeneralDescriptiveThreshold,
            state == ReportSampleStates.Sufficient);
    }

    public static ReportSampleContext ForPopulation(int populationCount) =>
        Create(populationCount, populationCount);
}

public sealed record ReportMetricSampleContext(
    ReportSampleContext CompletedCases,
    ReportSampleContext IncludedCompletedCases,
    ReportSampleContext SeatedToDoctor,
    ReportSampleContext Prep,
    ReportSampleContext ReadyWait,
    ReportSampleContext DoctorTime,
    ReportSampleContext Turnover,
    ReportSampleContext DoctorOccupiedWait,
    ReportSampleContext DoctorAvailableWait,
    ReportSampleContext ScheduleFit);

public sealed record ReportProcedureMetricSampleContext(
    ReportSampleContext CompletedCases,
    ReportSampleContext Total,
    ReportSampleContext ReadyWait,
    ReportSampleContext DoctorTime,
    ReportSampleContext DoctorOccupiedWait,
    ReportSampleContext DoctorAvailableWait,
    ReportSampleContext Allocation);

/// <summary>
/// Allocation population/contributor context for one doctor in the current analytical scope.
/// </summary>
public sealed record ReportDoctorAllocationSampleContext(
    string DoctorId,
    ReportSampleContext Sample);

/// <summary>
/// One procedure aggregation row over the current scoped included completed population.
/// Family grouping folds sedation variants without changing the population selected by the query.
/// </summary>
public sealed record ScopedProcedureGroup(
    string ProcedureCode,
    string ProcedureLabel,
    string BaseProcedureCode,
    bool? IsSedationCase,
    int CaseCount,
    int ScopedPopulationCount,
    double ShareOfScopedCases,
    ReportSampleContext Sample);
