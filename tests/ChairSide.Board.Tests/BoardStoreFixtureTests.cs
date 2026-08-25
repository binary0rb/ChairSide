using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Tests;

public sealed partial class BoardStoreTests
{
    // -------------------------------------------------------------------------
    // Dev-only synthetic report data seeding
    // -------------------------------------------------------------------------

    [Fact]
    public void Synthetic_report_data_seeds_clean_included_cycles_within_target_shape()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var result = context.Store.SeedSyntheticReportData();

        Assert.InRange(result.CyclesInserted, 100, 140);
        Assert.Equal(4, result.DoctorsRepresented);
        Assert.True(result.ProcedureFamiliesRepresented >= 7);
        Assert.Equal(result.CyclesInserted, result.ExpectedAllocationCases);
        Assert.Equal(0, result.ExceptionsExpected);

        var reports = context.Store.GetReports();
        // No reporting exceptions: everything is included in standard metrics.
        Assert.True(reports.IncludedCompletedCycleCount > 0);
        Assert.Equal(0, reports.ExcludedCompletedCycleCount);
        Assert.Equal(0, reports.ExceptionCount);
        Assert.Equal(result.CyclesInserted, reports.IncludedCompletedCycleCount);
    }

    [Fact]
    public void Synthetic_report_data_has_allocation_snapshots_and_no_legacy_or_standalone_sedation()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        context.Store.SeedSyntheticReportData();

        var cycles = context.Repository.LoadCompletedCycles();
        Assert.NotEmpty(cycles);
        Assert.All(cycles, cycle =>
        {
            // Expected allocation snapshot present on every seeded cycle.
            Assert.True(cycle.ExpectedAllocationUnits > 0);
            Assert.True(cycle.ExpectedAllocationMinutes > 0);
            Assert.Equal(cycle.ExpectedAllocationUnits * 10, cycle.ExpectedAllocationMinutes);
            // Full timing - nothing missing.
            Assert.NotNull(cycle.DoctorArrivedAt);
            Assert.NotNull(cycle.DoctorCompleteAt);
            Assert.NotNull(cycle.RoomAvailableAt);
            // No standalone legacy "SED" procedure (sedation is only ever a "+SED" modifier).
            Assert.NotEqual("SED", cycle.ProcedureCode.ToUpperInvariant());
            // No calendar-day crossing.
            Assert.Equal(cycle.SeatedAt.UtcDateTime.Date, cycle.DoctorCompleteAt!.Value.UtcDateTime.Date);
        });
    }

    [Fact]
    public void Synthetic_report_data_covers_doctors_families_and_variance_distribution()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        context.Store.SeedSyntheticReportData();
        var reports = context.Store.GetReports();

        // All four doctors and at least seven procedure families are represented.
        Assert.Equal(4, reports.DoctorSummaries.Select(summary => summary.AssignedDoctor).Distinct().Count());
        Assert.True(reports.BaseProcedureSummaries.Count >= 7);

        // Over / under / at expected allocation examples all exist, plus adjusted-from-default cases.
        var allocation = reports.AllocationVariance!;
        Assert.True(allocation.CasesOverExpectedAllocation > 0);
        Assert.True(allocation.CasesUnderExpectedAllocation > 0);
        Assert.True(allocation.CasesAtExpectedAllocation > 0);
        Assert.True(allocation.AdjustedAllocationCycleCount > 0);
    }

    [Fact]
    public void Synthetic_report_data_produces_distinct_doctor_allocation_profiles()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        context.Store.SeedSyntheticReportData();
        var reports = context.Store.GetReports();

        // Net allocation variance per doctor (summed across the returned per-month summaries).
        var netByDoctor = reports.DoctorSummaries
            .GroupBy(summary => summary.AssignedDoctor)
            .Select(group => group.Sum(summary => summary.Allocation.NetAllocationVarianceMinutes))
            .ToList();

        // The four doctors must not all share the same allocation balance.
        Assert.True(netByDoctor.Distinct().Count() >= 2);
        // At least one doctor runs net over expected and at least one runs net under expected,
        // so the UI shows genuinely different doctor profiles (not a symmetrical pattern).
        Assert.True(netByDoctor.Max() > 0);
        Assert.True(netByDoctor.Min() < 0);
    }

    [Fact]
    public void Synthetic_report_data_seeding_is_idempotent()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var first = context.Store.SeedSyntheticReportData();
        var second = context.Store.SeedSyntheticReportData();

        // Re-seeding writes the same deterministic set without duplicating records.
        Assert.Equal(first.CyclesInserted, second.CyclesInserted);
        Assert.Equal(first.CyclesInserted, context.Store.GetReports().IncludedCompletedCycleCount);
    }

    [Fact]
    public void Synthetic_report_data_populates_every_date_range_preset()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        context.Store.SeedSyntheticReportData();

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        int CompletedIn(DateOnly start, DateOnly end) =>
            context.Store.GetReports(ReportDateRange.FromDates(start, end)).CompletedRoomCyclesCount;

        var todayCount = CompletedIn(today, today);
        var last7 = CompletedIn(today.AddDays(-6), today);
        var last30 = CompletedIn(today.AddDays(-29), today);
        var allTime = context.Store.GetReports().CompletedRoomCyclesCount;

        // Today non-empty; each wider preset is strictly larger so the presets are all meaningful.
        Assert.True(todayCount >= 6, $"today={todayCount}");
        Assert.True(last7 > todayCount, $"last7={last7} today={todayCount}");
        Assert.True(last30 > last7, $"last30={last30} last7={last7}");
        Assert.True(allTime > last30, $"all={allTime} last30={last30}");
        Assert.InRange(allTime, 100, 140);
    }

    [Fact]
    public void Synthetic_report_data_is_clean_across_all_date_range_presets()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        context.Store.SeedSyntheticReportData();

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var ranges = new[]
        {
            ReportDateRange.FromDates(today, today),
            ReportDateRange.FromDates(today.AddDays(-6), today),
            ReportDateRange.FromDates(today.AddDays(-29), today),
            ReportDateRange.AllTime
        };

        foreach (var range in ranges)
        {
            var reports = context.Store.GetReports(range);
            Assert.Equal(0, reports.ExcludedCompletedCycleCount);
            Assert.Equal(0, reports.ExceptionCount);
        }
    }

    [Fact]
    public void Synthetic_report_data_summaries_grow_with_wider_date_ranges()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        context.Store.SeedSyntheticReportData();

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var todayReports = context.Store.GetReports(ReportDateRange.FromDates(today, today));
        var allReports = context.Store.GetReports();

        // Procedure family summaries cover more completed cycles over the wider window.
        var todayFamilyCycles = todayReports.BaseProcedureSummaries.Sum(summary => summary.CompletedCycleCount);
        var allFamilyCycles = allReports.BaseProcedureSummaries.Sum(summary => summary.CompletedCycleCount);
        Assert.True(allFamilyCycles > todayFamilyCycles, $"all={allFamilyCycles} today={todayFamilyCycles}");

        // Doctor summaries (via allocation aggregates) likewise reflect more cases over the wider window.
        var todayDoctorCycles = todayReports.DoctorSummaries.Sum(summary => summary.Allocation.AllocationVarianceCycleCount);
        var allDoctorCycles = allReports.DoctorSummaries.Sum(summary => summary.Allocation.AllocationVarianceCycleCount);
        Assert.True(allDoctorCycles > todayDoctorCycles, $"all={allDoctorCycles} today={todayDoctorCycles}");

        // Both windows still represent all four doctors.
        Assert.Equal(4, allReports.DoctorSummaries.Select(summary => summary.AssignedDoctor).Distinct().Count());
        Assert.Equal(4, todayReports.DoctorSummaries.Select(summary => summary.AssignedDoctor).Distinct().Count());
    }

    // -------------------------------------------------------------------------
    // Maintenance reset (training seed / empty beta)
    // -------------------------------------------------------------------------

    [Fact]
    public void Training_reset_clears_prior_completed_cycles_and_seeds_synthetic_data()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Pre-existing "alpha" completed cycle with a distinctive sub-second seat time.
        var alpha = CompleteOneCycle(context, room: 1, doctor: "otte");
        var alphaSeatedAt = alpha.SeatedAt;

        var result = context.Store.ResetAndSeedSyntheticTrainingData();

        Assert.True(result.CompletedCyclesCleared >= 1);
        Assert.InRange(result.CyclesSeeded, 100, 140);
        Assert.Equal(0, result.ExceptionsExpected);

        var cycles = context.Repository.LoadCompletedCycles();
        // The alpha cycle is gone; only the freshly seeded synthetic set remains.
        Assert.DoesNotContain(cycles, cycle => cycle.SeatedAt == alphaSeatedAt);
        Assert.Equal(result.CyclesSeeded, cycles.Count);
    }

    [Fact]
    public void Training_reset_clears_active_room_state()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "EXT", expectedAllocationUnits: 5));

        context.Store.ResetAndSeedSyntheticTrainingData();

        var room = context.Store.GetRoom(1);
        Assert.NotNull(room);
        Assert.Equal(RoomStates.Available, room.State);
        Assert.Null(room.SeatedAt);
        Assert.Equal(0, room.ExpectedAllocationUnits);
        Assert.Equal(0, room.ExpectedAllocationMinutes);
    }

    [Fact]
    public void Training_reset_produces_zero_exceptions_and_calculable_allocation()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var result = context.Store.ResetAndSeedSyntheticTrainingData();
        var reports = context.Store.GetReports();

        Assert.Equal(0, reports.ExcludedCompletedCycleCount);
        Assert.Equal(0, reports.ExceptionCount);
        Assert.Equal(result.CyclesSeeded, reports.IncludedCompletedCycleCount);
        // Every seeded completed cycle contributes a calculable allocation variance.
        Assert.Equal(reports.IncludedCompletedCycleCount, reports.AllocationVariance!.AllocationVarianceCycleCount);
    }

    [Fact]
    public void Training_reset_is_idempotent_and_does_not_duplicate()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var first = context.Store.ResetAndSeedSyntheticTrainingData();
        var second = context.Store.ResetAndSeedSyntheticTrainingData();

        Assert.Equal(first.CyclesSeeded, second.CyclesSeeded);
        Assert.Equal(second.CyclesSeeded, context.Repository.LoadCompletedCycles().Count);
        // The second run cleared exactly what the first run seeded - no accumulation.
        Assert.Equal(first.CyclesSeeded, second.CompletedCyclesCleared);
    }

    [Fact]
    public void Training_reset_persists_across_store_restart()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var result = first.Store.ResetAndSeedSyntheticTrainingData();

        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = second.Store.GetReports();

        Assert.Equal(result.CyclesSeeded, reports.IncludedCompletedCycleCount);
        Assert.Equal(0, reports.ExcludedCompletedCycleCount);
        Assert.Equal(0, reports.ExceptionCount);
    }

    [Fact]
    public void Empty_beta_reset_clears_completed_cycles_and_leaves_no_synthetic_data()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Start from a seeded training fixture, then reset to empty.
        context.Store.ResetAndSeedSyntheticTrainingData();

        var result = context.Store.ResetAllDataForEmptyBeta();

        Assert.True(result.CompletedCyclesCleared >= 40);
        Assert.Equal(0, result.CyclesSeeded);
        Assert.Empty(context.Repository.LoadCompletedCycles());

        var reports = context.Store.GetReports();
        Assert.Equal(0, reports.CompletedRoomCyclesCount);
        Assert.Equal(0, reports.IncludedCompletedCycleCount);
    }

    [Fact]
    public void Empty_beta_reset_clears_active_room_state()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "EXT"));

        context.Store.ResetAllDataForEmptyBeta();

        var room = context.Store.GetRoom(1);
        Assert.NotNull(room);
        Assert.Equal(RoomStates.Available, room.State);
        Assert.Null(room.SeatedAt);
    }

    // -------------------------------------------------------------------------
    // Maintenance CLI argument resolution (refusals never mutate)
    // -------------------------------------------------------------------------

    [Fact]
    public void Maintenance_resolve_returns_not_requested_without_flag()
    {
        Assert.Equal(MaintenanceOutcome.NotRequested, MaintenanceCommands.Resolve([]).Outcome);
        Assert.Equal(MaintenanceOutcome.NotRequested, MaintenanceCommands.Resolve(["--urls", "http://localhost"]).Outcome);
    }

    [Fact]
    public void Maintenance_resolve_authorizes_matching_command_and_token()
    {
        var training = MaintenanceCommands.Resolve(["--maintenance", "reset-training-data", "--confirm", "RESET_TRAINING_DATA"]);
        Assert.Equal(MaintenanceOutcome.Authorized, training.Outcome);
        Assert.Equal(MaintenanceCommands.TrainingSeedCommand, training.Command);

        var empty = MaintenanceCommands.Resolve(["--maintenance", "reset-empty", "--confirm", "RESET_EMPTY_BETA"]);
        Assert.Equal(MaintenanceOutcome.Authorized, empty.Outcome);
        Assert.Equal(MaintenanceCommands.EmptyBetaCommand, empty.Command);
    }

    [Theory]
    [InlineData("reset-training-data", "WRONG_TOKEN")]
    [InlineData("reset-training-data", "RESET_EMPTY_BETA")]
    [InlineData("reset-empty", "RESET_TRAINING_DATA")]
    public void Maintenance_resolve_refuses_wrong_token(string command, string token)
    {
        var resolution = MaintenanceCommands.Resolve(["--maintenance", command, "--confirm", token]);
        Assert.Equal(MaintenanceOutcome.Refused, resolution.Outcome);
        Assert.NotNull(resolution.RefusalReason);
    }

    [Fact]
    public void Maintenance_resolve_refuses_missing_token_and_unknown_command()
    {
        Assert.Equal(MaintenanceOutcome.Refused, MaintenanceCommands.Resolve(["--maintenance", "reset-training-data"]).Outcome);
        Assert.Equal(MaintenanceOutcome.Refused, MaintenanceCommands.Resolve(["--maintenance", "drop-everything", "--confirm", "x"]).Outcome);
    }

    // -------------------------------------------------------------------------
    // Large synthetic reporting dataset (maintenance-only, non-Production)
    // -------------------------------------------------------------------------

    [Fact]
    public void Maintenance_resolve_authorizes_large_synthetic_command_with_default_count()
    {
        var resolution = MaintenanceCommands.Resolve(
            ["--maintenance", "reset-large-synthetic-report-data", "--confirm", "RESET_LARGE_SYNTHETIC_REPORT_DATA"]);

        Assert.Equal(MaintenanceOutcome.Authorized, resolution.Outcome);
        Assert.Equal(MaintenanceCommands.LargeSyntheticSeedCommand, resolution.Command);
        Assert.Equal(MaintenanceCommands.DefaultCompletedCycles, resolution.CompletedCycles);
    }

    [Fact]
    public void Maintenance_resolve_parses_completed_cycles_argument()
    {
        var resolution = MaintenanceCommands.Resolve(
            ["--maintenance", "reset-large-synthetic-report-data", "--confirm", "RESET_LARGE_SYNTHETIC_REPORT_DATA", "--completed-cycles", "500"]);

        Assert.Equal(MaintenanceOutcome.Authorized, resolution.Outcome);
        Assert.Equal(500, resolution.CompletedCycles);
    }

    [Fact]
    public void Maintenance_resolve_refuses_large_synthetic_command_with_wrong_token()
    {
        var resolution = MaintenanceCommands.Resolve(
            ["--maintenance", "reset-large-synthetic-report-data", "--confirm", "RESET_TRAINING_DATA"]);

        Assert.Equal(MaintenanceOutcome.Refused, resolution.Outcome);
        Assert.NotNull(resolution.RefusalReason);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("10001")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void Maintenance_resolve_refuses_out_of_range_or_non_numeric_completed_cycles(string value)
    {
        var resolution = MaintenanceCommands.Resolve(
            ["--maintenance", "reset-large-synthetic-report-data", "--confirm", "RESET_LARGE_SYNTHETIC_REPORT_DATA", "--completed-cycles", value]);

        Assert.Equal(MaintenanceOutcome.Refused, resolution.Outcome);
        Assert.NotNull(resolution.RefusalReason);
    }

    [Theory]
    [InlineData("100", 100)]
    [InlineData("10000", 10000)]
    public void Maintenance_resolve_accepts_boundary_completed_cycles(string value, int expected)
    {
        var resolution = MaintenanceCommands.Resolve(
            ["--maintenance", "reset-large-synthetic-report-data", "--confirm", "RESET_LARGE_SYNTHETIC_REPORT_DATA", "--completed-cycles", value]);

        Assert.Equal(MaintenanceOutcome.Authorized, resolution.Outcome);
        Assert.Equal(expected, resolution.CompletedCycles);
    }

    [Fact]
    public void Maintenance_policy_defaults_to_deny_for_unknown_commands()
    {
        var development = DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Development);
        var training = DeploymentEnvironmentPolicy.Resolve(ChairSideEnvironmentNames.Training);

        Assert.False(MaintenanceExecutionPolicy.IsAllowed(development, "future-command"));
        Assert.False(MaintenanceExecutionPolicy.IsAllowed(training, "future-command"));
        Assert.False(MaintenanceExecutionPolicy.IsAllowed(development, null));
    }

    [Fact]
    public void Large_synthetic_report_data_seeds_exactly_the_requested_completed_cycle_count()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var result = context.Store.ResetAndSeedLargeSyntheticReportData(1000);

        Assert.Equal(1000, result.CyclesSeeded);
        Assert.Equal(0, result.ExceptionsExpected);

        var reports = context.Store.GetReports();
        Assert.Equal(1000, reports.TotalCompletedCycleCount);
        Assert.Equal(1000, reports.CompletedRoomCyclesCount);
        Assert.Equal(1000, reports.IncludedCompletedCycleCount);
        Assert.Equal(0, reports.ExcludedCompletedCycleCount);
        Assert.Equal(0, reports.ExceptionCount);
    }

    [Fact]
    public void Large_synthetic_report_data_populates_sedation_procedure_mix_and_observed_load()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        context.Store.ResetAndSeedLargeSyntheticReportData(1000);
        var reports = context.Store.GetReports();

        // The sedation partition covers exactly the included population, with both sides represented.
        Assert.Equal(1000, reports.SedationCaseCount + reports.NonSedationCaseCount);
        Assert.True(reports.SedationCaseCount > 0);
        Assert.True(reports.NonSedationCaseCount > 0);

        // Procedure mix and observed load both have rows at volume.
        Assert.NotNull(reports.DoctorProcedureMix);
        Assert.NotEmpty(reports.DoctorProcedureMix!);
        Assert.NotNull(reports.ObservedDoctorDays);
        Assert.NotEmpty(reports.ObservedDoctorDays!);
    }

    [Fact]
    public void Large_synthetic_report_data_stays_clean_with_full_timing_and_no_day_crossing()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        context.Store.ResetAndSeedLargeSyntheticReportData(1000);

        var cycles = context.Repository.LoadCompletedCycles();
        Assert.Equal(1000, cycles.Count);
        Assert.All(cycles, cycle =>
        {
            Assert.NotNull(cycle.DoctorArrivedAt);
            Assert.NotNull(cycle.DoctorCompleteAt);
            Assert.NotNull(cycle.RoomAvailableAt);
            Assert.True(cycle.ExpectedAllocationUnits > 0);
            // No overnight cycle: the flat per-day cap keeps seat and completion on the same UTC day.
            Assert.Equal(cycle.SeatedAt.UtcDateTime.Date, cycle.DoctorCompleteAt!.Value.UtcDateTime.Date);
            Assert.Equal(cycle.SeatedAt.UtcDateTime.Date, cycle.RoomAvailableAt!.Value.UtcDateTime.Date);
        });
    }

    [Fact]
    public void Large_synthetic_report_data_converges_without_duplicate_inflation_on_reseed()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var first = context.Store.ResetAndSeedLargeSyntheticReportData(1000);
        var second = context.Store.ResetAndSeedLargeSyntheticReportData(1000);

        Assert.Equal(1000, first.CyclesSeeded);
        Assert.Equal(first.CyclesSeeded, second.CyclesSeeded);
        // The second run cleared exactly what the first seeded - no accumulation across runs.
        Assert.Equal(first.CyclesSeeded, second.CompletedCyclesCleared);
        Assert.Equal(1000, context.Repository.LoadCompletedCycles().Count);
        Assert.Equal(1000, context.Store.GetReports().IncludedCompletedCycleCount);
    }

    [Fact]
    public void Large_synthetic_report_data_clamps_below_range_requests_to_the_minimum()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Defense-in-depth: even a direct out-of-range call (bypassing the CLI validation) is clamped.
        var result = context.Store.ResetAndSeedLargeSyntheticReportData(50);

        Assert.Equal(MaintenanceCommands.MinCompletedCycles, result.CyclesSeeded);
    }

    // -------------------------------------------------------------------------
    // Deterministic stress fixtures (reset-stress-fixture)
    // -------------------------------------------------------------------------

    [Fact]
    public void Maintenance_resolve_authorizes_stress_fixture_command_with_valid_profile()
    {
        var resolution = MaintenanceCommands.Resolve(
            ["--maintenance", "reset-stress-fixture", "--confirm", "RESET_STRESS_FIXTURE", "--profile", "live-board-stress"]);

        Assert.Equal(MaintenanceOutcome.Authorized, resolution.Outcome);
        Assert.Equal(MaintenanceCommands.StressFixtureCommand, resolution.Command);
        Assert.Equal(MaintenanceCommands.ProfileLiveBoardStress, resolution.Profile);
        Assert.Null(resolution.CompletedCycles);
    }

    [Fact]
    public void Maintenance_resolve_refuses_stress_fixture_command_with_wrong_token()
    {
        var resolution = MaintenanceCommands.Resolve(
            ["--maintenance", "reset-stress-fixture", "--confirm", "RESET_LARGE_SYNTHETIC_REPORT_DATA", "--profile", "live-board-stress"]);

        Assert.Equal(MaintenanceOutcome.Refused, resolution.Outcome);
        Assert.NotNull(resolution.RefusalReason);
    }

    [Fact]
    public void Maintenance_resolve_refuses_stress_fixture_command_without_profile()
    {
        var resolution = MaintenanceCommands.Resolve(
            ["--maintenance", "reset-stress-fixture", "--confirm", "RESET_STRESS_FIXTURE"]);

        Assert.Equal(MaintenanceOutcome.Refused, resolution.Outcome);
        Assert.NotNull(resolution.RefusalReason);
    }

    [Fact]
    public void Maintenance_resolve_refuses_stress_fixture_command_with_unknown_profile()
    {
        var resolution = MaintenanceCommands.Resolve(
            ["--maintenance", "reset-stress-fixture", "--confirm", "RESET_STRESS_FIXTURE", "--profile", "not-a-real-profile"]);

        Assert.Equal(MaintenanceOutcome.Refused, resolution.Outcome);
        Assert.NotNull(resolution.RefusalReason);
    }

    [Theory]
    [InlineData("reporting-volume")]
    [InlineData("live-board-stress")]
    [InlineData("doctor-view-stress")]
    [InlineData("doctor-view-overflow-stress")]
    [InlineData("scenario-rich")]
    [InlineData("full-stress")]
    [InlineData("all-scenarios")]
    public void Maintenance_resolve_accepts_all_seven_stress_fixture_profiles(string profile)
    {
        var resolution = MaintenanceCommands.Resolve(
            ["--maintenance", "reset-stress-fixture", "--confirm", "RESET_STRESS_FIXTURE", "--profile", profile]);

        Assert.Equal(MaintenanceOutcome.Authorized, resolution.Outcome);
        Assert.Equal(profile, resolution.Profile);
    }

    [Theory]
    [InlineData("reporting-volume")]
    [InlineData("all-scenarios")]
    public void Maintenance_resolve_accepts_completed_cycles_for_reporting_volume_and_all_scenarios_profiles(string profile)
    {
        var resolution = MaintenanceCommands.Resolve(
            ["--maintenance", "reset-stress-fixture", "--confirm", "RESET_STRESS_FIXTURE",
             "--profile", profile, "--completed-cycles", "500"]);

        Assert.Equal(MaintenanceOutcome.Authorized, resolution.Outcome);
        Assert.Equal(500, resolution.CompletedCycles);
    }

    [Theory]
    [InlineData("reporting-volume")]
    [InlineData("all-scenarios")]
    public void Maintenance_resolve_authorizes_default_completed_cycles_when_omitted(string profile)
    {
        var resolution = MaintenanceCommands.Resolve(
            ["--maintenance", "reset-stress-fixture", "--confirm", "RESET_STRESS_FIXTURE", "--profile", profile]);

        Assert.Equal(MaintenanceOutcome.Authorized, resolution.Outcome);
        Assert.Equal(MaintenanceCommands.DefaultCompletedCycles, resolution.CompletedCycles);
    }

    [Theory]
    [InlineData("live-board-stress")]
    [InlineData("doctor-view-stress")]
    [InlineData("doctor-view-overflow-stress")]
    [InlineData("scenario-rich")]
    [InlineData("full-stress")]
    public void Maintenance_resolve_refuses_completed_cycles_for_non_reporting_volume_non_all_scenarios_profiles(string profile)
    {
        var resolution = MaintenanceCommands.Resolve(
            ["--maintenance", "reset-stress-fixture", "--confirm", "RESET_STRESS_FIXTURE",
             "--profile", profile, "--completed-cycles", "500"]);

        Assert.Equal(MaintenanceOutcome.Refused, resolution.Outcome);
        Assert.NotNull(resolution.RefusalReason);
    }

    [Fact]
    public void Live_board_stress_fills_all_twelve_rooms_with_every_primary_state_and_ready_urgency_present()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, roomCount: 12);

        var result = context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileLiveBoardStress, null);

        Assert.Equal(12, result.ActiveRoomsReset);
        Assert.Equal(12, result.RoomStateCounts.Values.Sum());
        Assert.Equal(1, result.RoomStateCounts.GetValueOrDefault(RoomStates.Available));
        foreach (var state in new[]
                 {
                     RoomStates.Available, RoomStates.Seated, RoomStates.ReadyForDoctor,
                     RoomStates.DoctorInRoom, RoomStates.Turnover
                 })
        {
            Assert.True(result.RoomStateCounts.GetValueOrDefault(state) >= 1, $"Expected at least one room in state '{state}'.");
        }
        Assert.Equal(0, result.RoomStateCounts.GetValueOrDefault(RoomStates.Aging));
        Assert.Equal(0, result.RoomStateCounts.GetValueOrDefault(RoomStates.Stale));

        var rooms = context.Store.GetSnapshot().Rooms;
        Assert.Contains(rooms, room => room.State == RoomStates.Available && room.AssignedDoctor is null);
        Assert.Contains(rooms, room => room.State == RoomStates.ReadyForDoctor && room.ReadyUrgency == ReadyUrgency.Aging);
        Assert.Contains(rooms, room => room.State == RoomStates.ReadyForDoctor && room.ReadyUrgency == ReadyUrgency.Stale);
        Assert.Contains(rooms, room => room.ProcedureCode != null && room.ProcedureCode.EndsWith("+SED", StringComparison.Ordinal));
        Assert.Contains(rooms, room => room.ProcedureCode == "PCOC");
    }

    [Fact]
    public void Live_board_stress_in_progress_rows_do_not_count_as_completed_history()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, roomCount: 12);

        var result = context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileLiveBoardStress, null);

        // live-board-stress seeds two DoctorInRoom rooms and one Turnover room - each gets a paired
        // in-progress completed-cycle row (RoomAvailableAt still null). None of that is seeded
        // *history*: no completed cycles exist yet, so exception/audit counts and the history
        // horizon must all read as empty/not-seeded, never inflated or dated by in-progress rows.
        Assert.Equal(3, result.InProgressCycleRowsSeeded);
        Assert.Empty(result.DerivedExceptionReasonCounts);
        Assert.Equal(0, result.ManualAuditCandidatesSeeded);
        Assert.Null(result.HistoryEarliestSeatedAt);
        Assert.Null(result.HistoryLatestSeatedAt);
    }

    [Fact]
    public void Live_board_stress_in_progress_rows_compute_arrival_wait_state_from_their_own_timestamps()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, roomCount: 12);

        context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileLiveBoardStress, null);

        // FinalWaitState/Aging/StaleThresholdReached are set once, at arrival, by the real lifecycle
        // (ApplyDoctorArrived) and are never revisited by MarkDoctorComplete/MarkRoomAvailable - so a
        // directly-seeded DoctorInRoom/Turnover row must compute these correctly at seed time from its
        // own ReadyForDoctorAt -> DoctorArrivedAt gap, not assume a fixed placeholder. Uses the default
        // 7-minute aging / 12-minute stale thresholds (StoreContext.Create's defaults, not overridden).
        var inProgressCycles = context.Repository.LoadCompletedCycles().Where(cycle => cycle.RoomAvailableAt is null).ToList();
        Assert.Equal(3, inProgressCycles.Count);
        Assert.All(inProgressCycles, cycle =>
        {
            Assert.NotNull(cycle.ReadyForDoctorAt);
            Assert.NotNull(cycle.DoctorArrivedAt);
            var elapsed = cycle.DoctorArrivedAt!.Value - cycle.ReadyForDoctorAt!.Value;
            var expectedState = elapsed >= TimeSpan.FromMinutes(12) ? RoomStates.Stale
                : elapsed >= TimeSpan.FromMinutes(7) ? RoomStates.Aging
                : RoomStates.ReadyForDoctor;
            Assert.Equal(expectedState, cycle.FinalWaitState);
            Assert.Equal(elapsed >= TimeSpan.FromMinutes(7), cycle.AgingThresholdReached);
            Assert.Equal(elapsed >= TimeSpan.FromMinutes(12), cycle.StaleThresholdReached);
        });
    }

    [Fact]
    public void Doctor_view_stress_splits_active_rooms_one_three_four_four_with_pre_arrival_states()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, roomCount: 12);

        var result = context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileDoctorViewStress, null);

        Assert.Equal(1, result.ActiveRoomDoctorCounts.GetValueOrDefault("otte"));
        Assert.Equal(3, result.ActiveRoomDoctorCounts.GetValueOrDefault("pledger"));
        Assert.Equal(4, result.ActiveRoomDoctorCounts.GetValueOrDefault("gibson"));
        Assert.Equal(4, result.ActiveRoomDoctorCounts.GetValueOrDefault("schroeder"));

        // Every counted room stays pre-arrival, so Doctor View's assignment-based (not
        // state-filtered) current-room-frame count can never be accidentally inflated by an
        // assigned IN ROOM/TURNOVER room.
        var preArrivalStates = new[] { RoomStates.Seated, RoomStates.ReadyForDoctor };
        var assignedRooms = context.Store.GetSnapshot().Rooms.Where(room => room.AssignedDoctor is not null);
        Assert.All(assignedRooms, room => Assert.Contains(room.State, preArrivalStates));
    }

    [Fact]
    public void Doctor_view_overflow_stress_gives_one_doctor_five_active_rooms()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, roomCount: 12);

        var result = context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileDoctorViewOverflowStress, null);

        Assert.Equal(5, result.ActiveRoomDoctorCounts.GetValueOrDefault("otte"));
        Assert.Equal(3, result.ActiveRoomDoctorCounts.GetValueOrDefault("pledger"));
        Assert.Equal(2, result.ActiveRoomDoctorCounts.GetValueOrDefault("gibson"));
        Assert.Equal(2, result.ActiveRoomDoctorCounts.GetValueOrDefault("schroeder"));

        var preArrivalStates = new[] { RoomStates.Seated, RoomStates.ReadyForDoctor };
        var assignedRooms = context.Store.GetSnapshot().Rooms.Where(room => room.AssignedDoctor is not null);
        Assert.All(assignedRooms, room => Assert.Contains(room.State, preArrivalStates));
    }

    [Fact]
    public void Scenario_rich_derived_exceptions_surface_in_a_bounded_range_exactly_once_with_no_overlap()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, roomCount: 12, timeProvider: clock);

        context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileScenarioRich, null);

        // Bounded (not All-time) window covering just today.AddDays(-2) through today, where every
        // derived-exception edge case was seeded. Narrow on purpose so the total candidate count
        // stays comfortably under RecentCompletedCycles' 25-row cap regardless of sort order (the
        // MissingTiming cycle has a null DoctorArrivedAt, the field RecentCompletedCycles sorts on).
        var today = DateOnly.FromDateTime(now.UtcDateTime.Date);
        var window = ReportDateRange.FromDates(today.AddDays(-2), today);
        var reports = context.Store.GetReports(window);

        var expectedReasons = new[]
        {
            ReportingExceptionReasons.UnmappedProcedure,
            ReportingExceptionReasons.LegacyProcedure,
            ReportingExceptionReasons.ExtremeDuration,
            ReportingExceptionReasons.OvernightLifecycle,
            ReportingExceptionReasons.MissingTiming
        };

        var flaggedCycles = reports.RecentCompletedCycles.Where(cycle => cycle.HasReportingException).ToList();

        // Exactly the five intended edge-case cycles are flagged in this window - no unexpected
        // extras (the bulk clean history and the four bucket markers never trip a derived reason).
        Assert.Equal(5, flaggedCycles.Count);
        Assert.All(flaggedCycles, cycle => Assert.Single(cycle.ReportingExceptionReasons));

        foreach (var reason in expectedReasons)
        {
            var matching = flaggedCycles.Where(cycle => cycle.ReportingExceptionReasons.Contains(reason)).ToList();
            Assert.Single(matching);
        }
    }

    [Fact]
    public void Scenario_rich_populates_every_report_date_range_bucket_with_strictly_increasing_counts()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, roomCount: 12, timeProvider: clock);

        context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileScenarioRich, null);

        var today = DateOnly.FromDateTime(now.UtcDateTime.Date);
        var todayCount = context.Store.GetReports(ReportDateRange.FromDates(today, today)).CompletedRoomCyclesCount;
        var last7Count = context.Store.GetReports(ReportDateRange.FromDates(today.AddDays(-6), today)).CompletedRoomCyclesCount;
        var last30Count = context.Store.GetReports(ReportDateRange.FromDates(today.AddDays(-29), today)).CompletedRoomCyclesCount;
        var allTimeCount = context.Store.GetReports(ReportDateRange.AllTime).CompletedRoomCyclesCount;

        Assert.True(todayCount > 0, "Today bucket marker did not land in the Today window.");
        Assert.True(last7Count > todayCount, "Last-7 window did not exceed Today.");
        Assert.True(last30Count > last7Count, "Last-30 window did not exceed Last-7.");
        Assert.True(allTimeCount > last30Count, "All-time window did not exceed Last-30.");
    }

    [Fact]
    public void Full_stress_composes_live_board_and_scenario_rich_data_without_bespoke_logic()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, roomCount: 12, timeProvider: clock);

        var result = context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileFullStress, null);

        // Renders all 12 room cards: 11 assigned/active rooms plus 1 intentionally unassigned
        // AVAILABLE room - not "all 12 active".
        Assert.Equal(12, result.RoomStateCounts.Values.Sum());
        Assert.Equal(1, result.RoomStateCounts.GetValueOrDefault(RoomStates.Available));
        Assert.Equal(11, result.ActiveRoomDoctorCounts.Values.Sum());
        Assert.Equal(5, result.ActiveRoomDoctorCounts.GetValueOrDefault("otte"));
        Assert.Equal(2, result.ActiveRoomDoctorCounts.GetValueOrDefault("gibson"));
        Assert.Equal(2, result.ActiveRoomDoctorCounts.GetValueOrDefault("pledger"));
        Assert.Equal(2, result.ActiveRoomDoctorCounts.GetValueOrDefault("schroeder"));

        // Composes live-board-stress's IN ROOM/TURNOVER coverage: the resulting in-progress cycle
        // rows must not be counted or dated as seeded history (same invariant as the isolated
        // live-board-stress test) - the history horizon must reflect the 120-day scenario-rich seed,
        // not the minutes-old in-progress rows.
        Assert.Equal(2, result.InProgressCycleRowsSeeded);
        Assert.NotNull(result.HistoryEarliestSeatedAt);
        Assert.True(
            result.HistoryEarliestSeatedAt <= now.AddDays(-100),
            "History horizon should reflect the 120-day scenario-rich seed, not the minutes-old in-progress rows.");

        // Composes scenario-rich's edge cases: every derived reason present exactly once, plus the
        // manual audit candidate.
        var expectedReasons = new[]
        {
            ReportingExceptionReasons.UnmappedProcedure,
            ReportingExceptionReasons.LegacyProcedure,
            ReportingExceptionReasons.ExtremeDuration,
            ReportingExceptionReasons.OvernightLifecycle,
            ReportingExceptionReasons.MissingTiming
        };
        foreach (var reason in expectedReasons)
        {
            Assert.Equal(1, result.DerivedExceptionReasonCounts.GetValueOrDefault(reason));
        }
        Assert.Equal(1, result.ManualAuditCandidatesSeeded);
    }

    [Fact]
    public void Scenario_rich_includes_deterministic_three_room_doctor_overlap()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            roomCount: 12,
            timeProvider: new ManualTimeProvider(now));

        context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileScenarioRich, null);

        var reportDate = DateOnly.FromDateTime(now.AddDays(-5).UtcDateTime.Date);
        var reports = context.Store.GetReports(ReportDateRange.FromDates(reportDate, reportDate));
        var day = Assert.Single(
            reports.ObservedDoctorFlowDays!,
            item => item.DoctorId == "otte" && item.ReportDate == reportDate.ToString("yyyy-MM-dd"));
        Assert.True(day.PeakConcurrentRooms >= 3);
        Assert.True(day.MinutesWithThreeOrMoreDoctorWorkingRooms > 0);
    }

    [Fact]
    public void All_scenarios_composes_live_board_reporting_volume_and_scenario_rich_with_exact_ground_truth_count()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, roomCount: 12, timeProvider: clock);

        var result = context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileAllScenarios, 500);

        // Live shape: reuses full-stress's fixture table exactly - 12 room cards (11 assigned/active
        // plus 1 unassigned AVAILABLE), Otte at the named Doctor View overflow count of 5.
        Assert.Equal(12, result.RoomStateCounts.Values.Sum());
        Assert.Equal(1, result.RoomStateCounts.GetValueOrDefault(RoomStates.Available));
        Assert.Equal(11, result.ActiveRoomDoctorCounts.Values.Sum());
        Assert.Equal(5, result.ActiveRoomDoctorCounts.GetValueOrDefault("otte"));
        Assert.Equal(2, result.ActiveRoomDoctorCounts.GetValueOrDefault("gibson"));
        Assert.Equal(2, result.ActiveRoomDoctorCounts.GetValueOrDefault("pledger"));
        Assert.Equal(2, result.ActiveRoomDoctorCounts.GetValueOrDefault("schroeder"));
        Assert.Equal(2, result.InProgressCycleRowsSeeded);

        // Exact deterministic total: 500 large-synthetic cycles + 363 scenario-rich bulk-history
        // cycles (121 days x 3/day) + 13 explicit scenario-rich edge cases = 876, with zero rows
        // lost to collision (the day-offset shift keeps the bulk history's calendar days disjoint
        // from the large-synthetic seed's range). CyclesSeeded is ground-truth (ties out exactly to
        // the persisted completed-cycle count), not a sum of sub-seeder self-reports.
        Assert.Equal(876, result.CyclesSeeded);
        var persistedCompletedCount = context.Repository.LoadCompletedCycles().Count(cycle => cycle.RoomAvailableAt is not null);
        Assert.Equal(persistedCompletedCount, result.CyclesSeeded);
        Assert.Equal(4, result.DoctorsRepresented);
        Assert.True(result.ProcedureFamiliesRepresented > 1, "Expected more than one procedure family across the composed history.");

        // History horizon reflects the shifted scenario-rich bulk history (2000+ days back), not the
        // large-synthetic seed's much more recent range and not the minutes-old in-progress rows.
        Assert.NotNull(result.HistoryEarliestSeatedAt);
        Assert.True(
            result.HistoryEarliestSeatedAt <= now.AddDays(-2000),
            "History horizon should reflect the shifted scenario-rich bulk history, not the large-synthetic seed's more recent range.");

        // Every derived reporting-exception reason present exactly once, plus the manual audit
        // candidate - composed unmodified from SeedScenarioRichEdgeCases.
        var expectedReasons = new[]
        {
            ReportingExceptionReasons.UnmappedProcedure,
            ReportingExceptionReasons.LegacyProcedure,
            ReportingExceptionReasons.ExtremeDuration,
            ReportingExceptionReasons.OvernightLifecycle,
            ReportingExceptionReasons.MissingTiming
        };
        foreach (var reason in expectedReasons)
        {
            Assert.Equal(1, result.DerivedExceptionReasonCounts.GetValueOrDefault(reason));
        }
        Assert.Equal(1, result.ManualAuditCandidatesSeeded);

        // Date-range buckets are populated: the Today marker (plus the large-synthetic seed's own
        // recent cycles) land in Today. TotalCompletedCycleCount is the exact all-time analytical
        // count independent of the selected window and uses the same canonical administrative gate.
        // The Needs Review audit candidate is excluded, so both all-time and window counts remain
        // consistent with their effective reporting populations rather than exposing a raw table count.
        var today = DateOnly.FromDateTime(now.UtcDateTime.Date);
        var todayCount = context.Store.GetReports(ReportDateRange.FromDates(today, today)).CompletedRoomCyclesCount;
        var allTimeTotal = context.Store.GetReports(ReportDateRange.AllTime).TotalCompletedCycleCount;
        Assert.True(todayCount > 0, "Expected at least one completed cycle in the Today window.");
        Assert.Equal(875, allTimeTotal);
    }

    [Fact]
    public void All_scenarios_defaults_completed_cycles_when_omitted()
    {
        using var workspace = TestWorkspace.Create();
        // Fixed clock, matching the composition test above - keeps the Today marker's dynamic
        // timestamp away from the large-synthetic seed's fixed per-room hours, so the exact total
        // below is deterministic regardless of when this test actually runs.
        var now = new DateTimeOffset(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, roomCount: 12, timeProvider: clock);

        var result = context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileAllScenarios, null);

        // 1000 (default) + 363 (scenario-rich bulk history) + 13 (edge cases) = 1376.
        Assert.Equal(1376, result.CyclesSeeded);
    }

    // Seats, readies, completes, and frees one room across the given minute offsets from seatedAt.
    // Each call uses a self-contained time window; keep windows non-overlapping to avoid
    // cross-cycle doctor-occupied wait when that is not under test.
}
