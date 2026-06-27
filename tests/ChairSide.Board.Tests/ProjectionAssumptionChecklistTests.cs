using System.Reflection;

using ChairSide.Board.Services;

namespace ChairSide.Board.Tests;

public sealed class ProjectionAssumptionChecklistTests
{
    [Fact]
    public void Output_status_is_always_not_computed()
    {
        var withFit = ProjectionAssumptionChecklistBuilder.Build(
            "balanced-flow",
            "Balanced Flow",
            "Today",
            ScheduleFitReport());

        var withoutFit = ProjectionAssumptionChecklistBuilder.Build(
            "more-throughput",
            "More Throughput",
            "Today",
            scheduleFit: null);

        Assert.Equal(ProjectionAssumptionChecklistBuilder.NotComputedStatus, withFit.ScenarioOutputStatus);
        Assert.Equal("NotComputed", withFit.ScenarioOutputStatus);
        Assert.Equal(ProjectionAssumptionChecklistBuilder.NotComputedStatus, withoutFit.ScenarioOutputStatus);
    }

    [Fact]
    public void Safety_warning_states_observed_slack_is_not_automatically_usable_capacity()
    {
        var checklist = ProjectionAssumptionChecklistBuilder.Build(
            "balanced-flow",
            "Balanced Flow",
            "Last 7 days",
            ScheduleFitReport());

        Assert.Contains("Observed slack is not automatically usable capacity", checklist.SafetyWarning);
        Assert.Contains("explicit assumptions", checklist.SafetyWarning);
        Assert.Contains("which slack can safely be used", checklist.SafetyWarning);
    }

    [Fact]
    public void Schedule_fit_totals_are_copied_into_observed_summary_without_projection_math()
    {
        var report = ScheduleFitReport();

        var checklist = ProjectionAssumptionChecklistBuilder.Build(
            "balanced-flow",
            "Balanced Flow",
            "Today",
            report);

        Assert.True(checklist.ObservedScheduleFit.HasScheduleFitReport);
        Assert.Equal(report.IncludedCycleCount, checklist.ObservedScheduleFit.IncludedCycleCount);
        Assert.Equal(report.ScheduleFitCycleCount, checklist.ObservedScheduleFit.ScheduleFitCycleCount);
        Assert.Equal(report.Overall.BlockMinutes, checklist.ObservedScheduleFit.BlockMinutes);
        Assert.Equal(report.Overall.TotalExpectedMinutes, checklist.ObservedScheduleFit.TotalExpectedMinutes);
        Assert.Equal(report.Overall.TotalMeasuredMinutes, checklist.ObservedScheduleFit.TotalMeasuredMinutes);
        Assert.Equal(report.Overall.TotalSlackMinutes, checklist.ObservedScheduleFit.TotalSlackMinutes);
        Assert.Equal(report.Overall.TotalDebtMinutes, checklist.ObservedScheduleFit.TotalDebtMinutes);
        Assert.Equal(report.Overall.TotalVarianceMinutes, checklist.ObservedScheduleFit.TotalVarianceMinutes);
        Assert.Equal(report.Overall.UtilizationRatio, checklist.ObservedScheduleFit.UtilizationRatio);
    }

    [Fact]
    public void Missing_schedule_fit_produces_empty_observed_summary_and_missing_input()
    {
        var checklist = ProjectionAssumptionChecklistBuilder.Build(
            "balanced-flow",
            "Balanced Flow",
            reportWindowLabel: null,
            scheduleFit: null);

        Assert.False(checklist.ObservedScheduleFit.HasScheduleFitReport);
        Assert.Equal(0, checklist.ObservedScheduleFit.IncludedCycleCount);
        Assert.Equal(0, checklist.ObservedScheduleFit.ScheduleFitCycleCount);
        Assert.Equal(0, checklist.ObservedScheduleFit.BlockMinutes);
        Assert.Equal(0, checklist.ObservedScheduleFit.TotalExpectedMinutes);
        Assert.Equal(0, checklist.ObservedScheduleFit.TotalMeasuredMinutes);
        Assert.Equal(0, checklist.ObservedScheduleFit.TotalSlackMinutes);
        Assert.Equal(0, checklist.ObservedScheduleFit.TotalDebtMinutes);
        Assert.Equal(0, checklist.ObservedScheduleFit.TotalVarianceMinutes);
        Assert.Null(checklist.ObservedScheduleFit.UtilizationRatio);

        var missingInput = Assert.Single(checklist.MissingInputs);
        Assert.Equal("observed-schedule-fit-data", missingInput.Key);
    }

    [Fact]
    public void Required_assumptions_cover_the_non_timing_inputs_needed_before_projection()
    {
        var checklist = ProjectionAssumptionChecklistBuilder.Build(
            "balanced-flow",
            "Balanced Flow",
            "Today",
            ScheduleFitReport());

        var keys = checklist.RequiredAssumptions.Select(assumption => assumption.Key).ToArray();

        Assert.Contains("future-demand", keys);
        Assert.Contains("room-staff-availability", keys);
        Assert.Contains("turnover-sedation-recovery-constraints", keys);
        Assert.Contains("usable-slack-policy", keys);
        Assert.Contains("scheduling-policy", keys);
        Assert.Contains("clinical-team-judgment", keys);
    }

    [Fact]
    public void Preset_identity_and_report_window_are_copied_but_do_not_affect_observed_calculation()
    {
        var report = ScheduleFitReport();

        var first = ProjectionAssumptionChecklistBuilder.Build(
            "balanced-flow",
            "Balanced Flow",
            "Today",
            report);

        var second = ProjectionAssumptionChecklistBuilder.Build(
            "reset-admin-buffer-protected",
            "Reset / Admin Buffer Protected",
            "Last 30 days",
            report);

        Assert.Equal("balanced-flow", first.PresetId);
        Assert.Equal("Balanced Flow", first.PresetName);
        Assert.Equal("Today", first.ReportWindowLabel);

        Assert.Equal("reset-admin-buffer-protected", second.PresetId);
        Assert.Equal("Reset / Admin Buffer Protected", second.PresetName);
        Assert.Equal("Last 30 days", second.ReportWindowLabel);

        Assert.Equal(first.ObservedScheduleFit, second.ObservedScheduleFit);
        Assert.Equal(first.ScenarioOutputStatus, second.ScenarioOutputStatus);
        Assert.Equal(first.SafetyWarning, second.SafetyWarning);
    }

    [Fact]
    public void Public_model_does_not_expose_projection_or_capacity_promise_names()
    {
        var forbiddenNameFragments = new[]
        {
            "ProjectedAppointments",
            "ProjectedCapacity",
            "RecoverableSlots",
            "CapacityGain",
            "ExtraAppointments",
            "Forecast",
            "Prediction"
        };

        var publicMemberNames = new[]
        {
            typeof(ProjectionAssumptionChecklist),
            typeof(ObservedScheduleFitInputSummary),
            typeof(ProjectionAssumptionRequirement)
        }
        .SelectMany(type => type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static))
        .Select(member => member.Name);

        foreach (var memberName in publicMemberNames)
        {
            foreach (var forbidden in forbiddenNameFragments)
            {
                Assert.DoesNotContain(forbidden, memberName, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static ScheduleFitReport ScheduleFitReport() =>
        new(
            new ScheduleFitResult(
                CycleCount: 3,
                BlockMinutes: 10,
                TotalExpectedMinutes: 90,
                TotalMeasuredMinutes: 80,
                TotalVarianceMinutes: -10,
                TotalSlackMinutes: 15,
                TotalDebtMinutes: 5,
                TotalExpectedBlocks: 9.0,
                TotalActualBlocks: 8.0,
                TotalVarianceBlocks: -1.0,
                UtilizationRatio: 80.0 / 90.0),
            IncludedCycleCount: 4,
            ScheduleFitCycleCount: 3);
}
