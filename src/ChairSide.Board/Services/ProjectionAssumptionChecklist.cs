namespace ChairSide.Board.Services;

/// <summary>
/// Pure read model that names the explicit assumptions a future Workshop projection would require.
/// It intentionally carries no projection output and does not interpret observed schedule-fit values
/// as recoverable capacity.
/// </summary>
public sealed record ProjectionAssumptionChecklist(
    string PresetId,
    string PresetName,
    string? ReportWindowLabel,
    ObservedScheduleFitInputSummary ObservedScheduleFit,
    IReadOnlyList<ProjectionAssumptionRequirement> RequiredAssumptions,
    IReadOnlyList<ProjectionAssumptionRequirement> MissingInputs,
    string ScenarioOutputStatus,
    string SafetyWarning);

/// <summary>
/// Observed schedule-fit inputs copied from <see cref="ScheduleFitReport"/>. These values describe
/// what happened in the selected report population; they are not converted into capacity, openings,
/// appointments, or any other projection output.
/// </summary>
public sealed record ObservedScheduleFitInputSummary(
    bool HasScheduleFitReport,
    int IncludedCycleCount,
    int ScheduleFitCycleCount,
    int BlockMinutes,
    int TotalExpectedMinutes,
    int TotalMeasuredMinutes,
    int TotalSlackMinutes,
    int TotalDebtMinutes,
    int TotalVarianceMinutes,
    double? UtilizationRatio);

/// <summary>
/// A required or missing input that must be resolved before ChairSide can honestly compute a future
/// scenario.
/// </summary>
public sealed record ProjectionAssumptionRequirement(
    string Key,
    string Label,
    string Description);

/// <summary>
/// Builds assumption checklists without I/O, API wiring, persistence, or projection math.
/// </summary>
public static class ProjectionAssumptionChecklistBuilder
{
    public const string NotComputedStatus = "NotComputed";

    public const string SafetyWarningText =
        "Observed slack is not automatically usable capacity. A future projection requires explicit assumptions about demand, staffing, rooms, scheduling policy, and which slack can safely be used.";

    public static ProjectionAssumptionChecklist Build(
        string presetId,
        string presetName,
        string? reportWindowLabel,
        ScheduleFitReport? scheduleFit)
    {
        ArgumentNullException.ThrowIfNull(presetId);
        ArgumentNullException.ThrowIfNull(presetName);

        return new ProjectionAssumptionChecklist(
            presetId,
            presetName,
            reportWindowLabel,
            BuildObservedSummary(scheduleFit),
            BuildRequiredAssumptions(),
            BuildMissingInputs(scheduleFit),
            NotComputedStatus,
            SafetyWarningText);
    }

    private static ObservedScheduleFitInputSummary BuildObservedSummary(ScheduleFitReport? scheduleFit)
    {
        if (scheduleFit is null)
        {
            return new ObservedScheduleFitInputSummary(
                HasScheduleFitReport: false,
                IncludedCycleCount: 0,
                ScheduleFitCycleCount: 0,
                BlockMinutes: 0,
                TotalExpectedMinutes: 0,
                TotalMeasuredMinutes: 0,
                TotalSlackMinutes: 0,
                TotalDebtMinutes: 0,
                TotalVarianceMinutes: 0,
                UtilizationRatio: null);
        }

        var overall = scheduleFit.Overall;

        return new ObservedScheduleFitInputSummary(
            HasScheduleFitReport: true,
            scheduleFit.IncludedCycleCount,
            scheduleFit.ScheduleFitCycleCount,
            overall.BlockMinutes,
            overall.TotalExpectedMinutes,
            overall.TotalMeasuredMinutes,
            overall.TotalSlackMinutes,
            overall.TotalDebtMinutes,
            overall.TotalVarianceMinutes,
            overall.UtilizationRatio);
    }

    private static IReadOnlyList<ProjectionAssumptionRequirement> BuildRequiredAssumptions() =>
    [
        new(
            "future-demand",
            "Future demand",
            "Expected request volume and case mix for the future period being considered."),
        new(
            "room-staff-availability",
            "Room and staff availability",
            "Which rooms, doctors, assistants, and support staff are actually available during the scenario window."),
        new(
            "turnover-sedation-recovery-constraints",
            "Turnover and sedation-recovery constraints",
            "Operational limits for turnover, sedation recovery, clinical monitoring, and room readiness."),
        new(
            "usable-slack-policy",
            "Recoverable/usable slack policy",
            "A team-approved rule for which observed slack, if any, can safely be treated as usable scheduling room."),
        new(
            "scheduling-policy",
            "Scheduling policy",
            "How schedule blocks, appointment lengths, buffers, and tradeoffs should be applied."),
        new(
            "clinical-team-judgment",
            "Clinical appropriateness and team judgment",
            "Clinical suitability, patient experience, and team judgment that cannot be inferred from timing data alone.")
    ];

    private static IReadOnlyList<ProjectionAssumptionRequirement> BuildMissingInputs(ScheduleFitReport? scheduleFit)
    {
        if (scheduleFit is not null)
        {
            return [];
        }

        return
        [
            new(
                "observed-schedule-fit-data",
                "Observed schedule-fit data",
                "A schedule-fit report is required before the checklist can summarize observed expected-vs-measured timing inputs.")
        ];
    }
}
