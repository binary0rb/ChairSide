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
    [Fact]
    public void Production_database_path_inside_content_root_fails_fast()
    {
        using var workspace = TestWorkspace.Create();
        var insideContentRoot = Path.Combine(workspace.ContentRoot, "data", "prod.db");

        var exception = Assert.Throws<DatabaseIsolationException>(() =>
            StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: insideContentRoot));

        Assert.Contains("outside the deployed app content root", exception.Message);
    }

    [Fact]
    public void Production_fresh_database_starts_rooms_available_without_demo_activity()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var snapshot = context.Store.GetSnapshot();

        Assert.Equal(3, snapshot.RoomCount);
        Assert.All(snapshot.Rooms, room =>
        {
            Assert.Equal(RoomStates.Available, room.State);
            Assert.Null(room.SeatedAt);
        });
        Assert.Equal(0, context.Store.GetReports().CompletedRoomCyclesCount);
    }

    [Fact]
    public void Development_fresh_database_seeds_demo_active_rooms()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Development, roomCount: 12);

        var snapshot = context.Store.GetSnapshot();
        var activeRooms = snapshot.Rooms.Where(room => room.State != RoomStates.Available || room.SeatedAt is not null).ToList();

        Assert.Equal(12, snapshot.RoomCount);
        Assert.NotEmpty(activeRooms);
        Assert.Contains(activeRooms, room => room.AssignedDoctor == "otte" && room.ProcedureCode == "CON");
    }

    [Fact]
    public void Default_rosters_load_current_doctors_and_procedures()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var snapshot = context.Store.GetSnapshot();

        Assert.Equal(["otte", "pledger", "gibson", "schroeder"], snapshot.Doctors.Select(doctor => doctor.Id));
        Assert.Equal(["Dr. Otte", "Dr. Pledger", "Dr. Gibson", "Dr. Schroeder"], snapshot.Doctors.Select(doctor => doctor.Name));
        Assert.Equal(["Otte", "Pledger", "Gibson", "Schroeder"], snapshot.Doctors.Select(doctor => doctor.ShortName));
        // Sedation is no longer an active, standalone selectable procedure; it is applied
        // as a modifier on eligible primary procedures, so "SED" is absent from the roster.
        Assert.Equal(
            ["CON", "EXT", "POST", "IMP", "BX", "MISC", "POE", "IMPRES", "INTCK", "BXPOST", "IMPRM", "PCOC", "UNCOV", "EXBOND", "AO4"],
            snapshot.Procedures.Select(procedure => procedure.Code));
        Assert.Equal(
            ["Consult", "Extraction", "Post-op", "Implant", "Biopsy",
             "Misc", "Periodic Exam", "Impressions", "Integration Check",
             "Biopsy Post-op", "Implant Removal", "Phone -> Office Consult",
             "Uncover", "Expose and Bond", "All on Four"],
            snapshot.Procedures.Select(procedure => procedure.Label));
        // Only the approved sedation-eligible procedures expose the sedation modifier.
        Assert.Equal(
            ["EXT", "IMP", "BX", "MISC", "IMPRM", "UNCOV", "EXBOND", "AO4"],
            snapshot.Procedures.Where(procedure => procedure.SedationEligible).Select(procedure => procedure.Code));
    }

    // -------------------------------------------------------------------------
    // Expected allocation snapshot (operational, non-PHI; 1 unit = 10 minutes)
    // -------------------------------------------------------------------------

    [Fact]
    public void Procedure_roster_exposes_allocation_behavior_and_default_expected_units()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var snapshot = context.Store.GetSnapshot();

        var extraction = snapshot.Procedures.Single(procedure => procedure.Code == "EXT");
        Assert.Equal(AllocationBehaviors.Variable, extraction.AllocationBehavior);
        Assert.Equal(3, extraction.DefaultExpectedUnits);

        var consult = snapshot.Procedures.Single(procedure => procedure.Code == "CON");
        Assert.Equal(AllocationBehaviors.Known, consult.AllocationBehavior);
        Assert.Equal(1, consult.DefaultExpectedUnits);
    }

    [Fact]
    public void Procedure_allocation_behavior_classification_matches_known_and_variable_lists()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var behaviorByCode = context.Store.GetSnapshot().Procedures
            .ToDictionary(procedure => procedure.Code, procedure => procedure.AllocationBehavior);

        string[] expectedVariable = ["EXT", "IMP", "IMPRM", "EXBOND", "AO4", "UNCOV", "BX", "MISC"];
        string[] expectedKnown = ["CON", "POST", "POE", "IMPRES", "INTCK", "BXPOST", "PCOC"];

        foreach (var code in expectedVariable)
        {
            Assert.Equal(AllocationBehaviors.Variable, behaviorByCode[code]);
        }

        foreach (var code in expectedKnown)
        {
            Assert.Equal(AllocationBehaviors.Known, behaviorByCode[code]);
        }

        // Every active procedure is classified by exactly one of the two lists.
        Assert.Equal(expectedVariable.Length + expectedKnown.Length, behaviorByCode.Count);
    }

    [Fact]
    public void Seating_without_units_applies_procedure_default_allocation()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // EXT default is 3 units (30 minutes).
        var seated = SeatViaPrestage(context.Store, 1, "otte", "EXT");
        Assert.NotNull(seated);
        Assert.Equal(3, seated.OriginalDefaultExpectedUnits);
        Assert.Equal(3, seated.ExpectedAllocationUnits);
        Assert.Equal(30, seated.ExpectedAllocationMinutes);
        Assert.False(seated.AllocationAdjustedFromDefault);
    }

    [Fact]
    public void Seating_with_explicit_units_stores_final_allocation()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var seated = SeatViaPrestage(context.Store, 1, "otte", "EXT", expectedAllocationUnits: 5);
        Assert.NotNull(seated);
        Assert.Equal(3, seated.OriginalDefaultExpectedUnits);
        Assert.Equal(5, seated.ExpectedAllocationUnits);
        Assert.Equal(50, seated.ExpectedAllocationMinutes);
        Assert.True(seated.AllocationAdjustedFromDefault);
    }

    [Fact]
    public void Allocation_adjusted_flag_is_false_when_units_equal_default()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Explicitly supplying the default value is not an adjustment.
        var seated = SeatViaPrestage(context.Store, 1, "otte", "EXT", expectedAllocationUnits: 3);
        Assert.NotNull(seated);
        Assert.Equal(3, seated.ExpectedAllocationUnits);
        Assert.Equal(30, seated.ExpectedAllocationMinutes);
        Assert.False(seated.AllocationAdjustedFromDefault);
    }

    [Fact]
    public void Allocation_adjusted_flag_is_true_when_units_differ_from_default()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var seated = SeatViaPrestage(context.Store, 1, "otte", "IMP", expectedAllocationUnits: 4);
        Assert.NotNull(seated);
        Assert.Equal(6, seated.OriginalDefaultExpectedUnits);
        Assert.Equal(4, seated.ExpectedAllocationUnits);
        Assert.Equal(40, seated.ExpectedAllocationMinutes);
        Assert.True(seated.AllocationAdjustedFromDefault);
    }

    [Fact]
    public void Seating_with_zero_units_clamps_to_minimum_and_never_yields_zero_minutes()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Explicit 0 must not produce a 0-minute expected allocation.
        var seated = SeatViaPrestage(context.Store, 1, "otte", "EXT", expectedAllocationUnits: 0);
        Assert.NotNull(seated);
        Assert.Equal(1, seated.ExpectedAllocationUnits);
        Assert.Equal(10, seated.ExpectedAllocationMinutes);
        // EXT default is 3, so a clamped-to-1 value is an adjustment from default.
        Assert.True(seated.AllocationAdjustedFromDefault);
    }

    [Fact]
    public void Seating_with_negative_units_clamps_to_minimum()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var seated = SeatViaPrestage(context.Store, 1, "otte", "EXT", expectedAllocationUnits: -5);
        Assert.NotNull(seated);
        Assert.Equal(1, seated.ExpectedAllocationUnits);
        Assert.Equal(10, seated.ExpectedAllocationMinutes);
    }

    [Fact]
    public void Seating_with_units_above_maximum_clamps_to_maximum()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var seated = SeatViaPrestage(context.Store, 1, "otte", "EXT", expectedAllocationUnits: 100);
        Assert.NotNull(seated);
        Assert.Equal(24, seated.ExpectedAllocationUnits);
        Assert.Equal(240, seated.ExpectedAllocationMinutes);
        Assert.True(seated.AllocationAdjustedFromDefault);
    }

    [Fact]
    public void Allocation_snapshot_carries_from_active_room_into_completed_cycle()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "IMP", expectedAllocationUnits: 7));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(6, cycle.OriginalDefaultExpectedUnits);
        Assert.Equal(7, cycle.ExpectedAllocationUnits);
        Assert.Equal(70, cycle.ExpectedAllocationMinutes);
        Assert.True(cycle.AllocationAdjustedFromDefault);
    }

    [Fact]
    public void Allocation_snapshot_survives_restart_for_active_rooms()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        Assert.NotNull(SeatViaPrestage(first.Store, 1, "otte", "EXT", expectedAllocationUnits: 5));

        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reloaded = second.Store.GetRoom(1);

        Assert.NotNull(reloaded);
        Assert.Equal(3, reloaded.OriginalDefaultExpectedUnits);
        Assert.Equal(5, reloaded.ExpectedAllocationUnits);
        Assert.Equal(50, reloaded.ExpectedAllocationMinutes);
        Assert.True(reloaded.AllocationAdjustedFromDefault);
    }

    [Fact]
    public void Allocation_snapshot_survives_restart_for_completed_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        Assert.NotNull(SeatViaPrestage(first.Store, 1, "otte", "IMP", expectedAllocationUnits: 9));
        Assert.NotNull(first.Store.MarkReadyForDoctor(1));
        Assert.NotNull(first.Store.MarkDoctorArrived(1));
        Assert.NotNull(first.Store.MarkDoctorComplete(1));
        Assert.NotNull(first.Store.MarkRoomAvailable(1));

        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var cycle = Assert.Single(second.Store.GetReports().RecentCompletedCycles);

        Assert.Equal(6, cycle.OriginalDefaultExpectedUnits);
        Assert.Equal(9, cycle.ExpectedAllocationUnits);
        Assert.Equal(90, cycle.ExpectedAllocationMinutes);
        Assert.True(cycle.AllocationAdjustedFromDefault);
    }

    [Fact]
    public void Sedation_variant_inherits_base_procedure_default_allocation()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // The sedation modifier does not change which roster default applies.
        var seated = SeatViaPrestage(context.Store, 1, "otte", "EXT", sedation: true);
        Assert.NotNull(seated);
        Assert.Equal("EXT+SED", seated.ProcedureCode);
        Assert.Equal(3, seated.OriginalDefaultExpectedUnits);
        Assert.Equal(3, seated.ExpectedAllocationUnits);
        Assert.Equal(30, seated.ExpectedAllocationMinutes);
        Assert.False(seated.AllocationAdjustedFromDefault);
    }

    [Fact]
    public void Inactive_doctors_and_procedures_are_not_selectable()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            doctorRosterOptions: new DoctorRosterOptions
            {
                Doctors =
                [
                    new() { Id = "active", DisplayName = "Dr. Active", ShortName = "Active", Color = "#2563eb", Active = true },
                    new() { Id = "inactive", DisplayName = "Dr. Inactive", ShortName = "Inactive", Color = "#16a34a", Active = false }
                ]
            },
            procedureRosterOptions: new ProcedureRosterOptions
            {
                Procedures =
                [
                    new() { Id = "active-procedure", Code = "ACT", Label = "Active procedure", Icon = "speech", Active = true },
                    new() { Id = "inactive-procedure", Code = "OFF", Label = "Inactive", Icon = "vial", Active = false }
                ]
            });

        var snapshot = context.Store.GetSnapshot();

        Assert.Equal(["active"], snapshot.Doctors.Select(doctor => doctor.Id));
        Assert.Equal(["ACT"], snapshot.Procedures.Select(procedure => procedure.Code));
        Assert.Null(SeatViaPrestage(context.Store, 1, "inactive", "ACT"));
        Assert.Null(SeatViaPrestage(context.Store, 1, "active", "OFF"));
        var seated = SeatViaPrestage(context.Store, 1, "active", "ACT");
        Assert.NotNull(seated);
        Assert.Equal("ACT", seated.ProcedureCode);
        Assert.Equal("ACT", seated.Procedure?.Code);
        Assert.Equal("Active procedure", seated.Procedure?.Label);
    }

    [Fact]
    public void Sedation_is_not_a_standalone_selectable_procedure()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var snapshot = context.Store.GetSnapshot();
        Assert.DoesNotContain("SED", snapshot.Procedures.Select(procedure => procedure.Code));

        // Standalone sedation is not seatable, by code or by id.
        Assert.Null(SeatViaPrestage(context.Store, 1, "otte", "SED"));
        Assert.Null(SeatViaPrestage(context.Store, 1, "otte", "sedation"));
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "EXT"));
    }

    [Fact]
    public void Eligible_procedure_seated_without_sedation_stays_a_base_procedure()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Sedation defaults Off: an eligible procedure seated without the flag is the base case.
        var seated = SeatViaPrestage(context.Store, 1, "otte", "EXT");
        Assert.NotNull(seated);
        Assert.Equal("EXT", seated.ProcedureCode);
        Assert.Equal("EXT", seated.Procedure?.Code);
        Assert.Equal("Extraction", seated.Procedure?.Label);
    }

    [Fact]
    public void Eligible_procedure_with_sedation_is_stored_and_displayed_as_a_variant()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var seated = SeatViaPrestage(context.Store, 1, "otte", "EXT", sedation: true);
        Assert.NotNull(seated);
        // Base procedure, sedation flag, and combined case type are all derivable from the code.
        Assert.Equal("EXT+SED", seated.ProcedureCode);
        Assert.Equal("EXT+SED", seated.Procedure?.Code);
        Assert.Equal("Extraction + Sedation", seated.Procedure?.Label);
        Assert.True(seated.Procedure?.SedationEligible);
    }

    [Fact]
    public void New_all_on_four_procedure_supports_sedation_modifier_and_resolves_label()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // All on Four is a newly added, sedation-eligible procedure.
        var seated = SeatViaPrestage(context.Store, 1, "otte", "AO4", sedation: true);
        Assert.NotNull(seated);
        Assert.Equal("AO4+SED", seated.ProcedureCode);
        Assert.Equal("AO4+SED", seated.Procedure?.Code);
        Assert.Equal("All on Four + Sedation", seated.Procedure?.Label);
        Assert.True(seated.Procedure?.SedationEligible);
    }

    [Fact]
    public void New_all_on_four_sedation_variant_rolls_up_under_base_in_reports()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        RunProcedureCycle(context, clock, baseTime, 1, "otte", "AO4", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 2, "otte", "AO4", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5, sedation: true);

        var reports = context.Store.GetReports();

        // Full-variant summaries stay distinct.
        var plain = Assert.Single(reports.ProcedureSummaries, s => s.ProcedureCode == "AO4");
        Assert.Equal("AO4", plain.BaseProcedureCode);
        Assert.False(plain.IsSedationCase);

        var sedationVariant = Assert.Single(reports.ProcedureSummaries, s => s.ProcedureCode == "AO4+SED");
        Assert.Equal("AO4", sedationVariant.BaseProcedureCode);
        Assert.True(sedationVariant.IsSedationCase);

        // Base roll-up aggregates both cycles under "AO4" / "All on Four".
        var baseAllOnFour = Assert.Single(reports.BaseProcedureSummaries, s => s.ProcedureCode == "AO4");
        Assert.Equal("All on Four", baseAllOnFour.ProcedureLabel);
        Assert.Equal(2, baseAllOnFour.CompletedCycleCount);
    }

    [Fact]
    public void Sedation_modifier_is_rejected_for_non_eligible_procedures()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Consult is not sedation-eligible, so it can never be marked as a sedation case.
        Assert.Null(SeatViaPrestage(context.Store, 1, "otte", "CON", sedation: true));

        // The same procedure remains seatable without sedation.
        var seated = SeatViaPrestage(context.Store, 1, "otte", "CON");
        Assert.NotNull(seated);
        Assert.Equal("CON", seated.ProcedureCode);
    }

    [Fact]
    public void Sedation_modifier_can_be_added_and_removed_via_save_details()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "IMP"));

        var withSedation = context.Store.SaveAssignmentDetails(
            1,
            RoomAssignmentContract.Create(
                "otte",
                "IMP+SED",
                SedationContract.EligibleYes(),
                ExpectedAllocationContract.ConfirmedSuggestedValue(6)));
        Assert.NotNull(withSedation);
        Assert.Equal("IMP+SED", withSedation.ProcedureCode);
        Assert.Equal("Implant + Sedation", withSedation.Procedure?.Label);

        var withoutSedation = context.Store.SaveAssignmentDetails(
            1,
            RoomAssignmentContract.Create(
                "otte",
                "IMP",
                SedationContract.EligibleNo(),
                ExpectedAllocationContract.ConfirmedSuggestedValue(6)));
        Assert.NotNull(withoutSedation);
        Assert.Equal("IMP", withoutSedation.ProcedureCode);

        // Switching to a non-eligible procedure cannot carry sedation forward.
        Assert.Null(context.Store.SaveAssignmentDetails(
            1,
            RoomAssignmentContract.Create(
                "otte",
                "POST",
                SedationContract.EligibleYes(),
                ExpectedAllocationContract.ConfirmedSuggestedValue(1))));
    }

    [Fact]
    public void Sedation_cases_report_as_distinct_variants_from_base_procedure()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Two plain extractions and one extraction-with-sedation in separate hour blocks.
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(2), 2, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5, sedation: true);

        var summaries = context.Store.GetReports().ProcedureSummaries;

        var extraction = Assert.Single(summaries, summary => summary.ProcedureCode == "EXT");
        Assert.Equal("Extraction", extraction.ProcedureLabel);
        Assert.Equal(2, extraction.CompletedCycleCount);

        var sedationVariant = Assert.Single(summaries, summary => summary.ProcedureCode == "EXT+SED");
        Assert.Equal("Extraction + Sedation", sedationVariant.ProcedureLabel);
        Assert.Equal(1, sedationVariant.CompletedCycleCount);
    }

    [Fact]
    public void Variant_summaries_carry_base_code_and_sedation_flag()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        RunProcedureCycle(context, clock, baseTime, 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 2, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5, sedation: true);

        var summaries = context.Store.GetReports().ProcedureSummaries;

        var plain = Assert.Single(summaries, summary => summary.ProcedureCode == "EXT");
        Assert.Equal("EXT", plain.BaseProcedureCode);
        Assert.False(plain.IsSedationCase);

        var sedationVariant = Assert.Single(summaries, summary => summary.ProcedureCode == "EXT+SED");
        Assert.Equal("EXT", sedationVariant.BaseProcedureCode);
        Assert.True(sedationVariant.IsSedationCase);
    }

    [Fact]
    public void Doctor_procedure_mix_groups_by_doctor_and_variant_with_shares()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Otte: two plain extractions and one extraction-with-sedation, so the denominator is 3 and
        // the sedation variant stays a separate row from the plain extraction.
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(2), 2, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5, sedation: true);

        var otteRows = context.Store.GetReports().DoctorProcedureMix!
            .Where(row => row.DoctorId == "otte")
            .ToList();
        Assert.Equal(2, otteRows.Count);

        var plain = Assert.Single(otteRows, row => row.ProcedureCode == "EXT");
        Assert.Equal("Extraction", plain.ProcedureLabel);
        Assert.Equal("EXT", plain.BaseProcedureCode);
        Assert.False(plain.IsSedationCase);
        Assert.Equal(2, plain.CaseCount);
        Assert.Equal(3, plain.DoctorCompletedCaseCount);
        Assert.Equal(2d / 3d, plain.ShareOfDoctorCases, 3);

        var sedationVariant = Assert.Single(otteRows, row => row.ProcedureCode == "EXT+SED");
        Assert.Equal("EXT", sedationVariant.BaseProcedureCode);
        Assert.True(sedationVariant.IsSedationCase);
        Assert.Equal(1, sedationVariant.CaseCount);
        Assert.Equal(3, sedationVariant.DoctorCompletedCaseCount);
        Assert.Equal(1d / 3d, sedationVariant.ShareOfDoctorCases, 3);

        // Ordered by case count descending within the doctor.
        Assert.Equal("EXT", otteRows[0].ProcedureCode);
    }

    [Fact]
    public void Doctor_procedure_mix_isolates_rows_and_denominators_per_doctor()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Otte: two extractions. Pledger: one consult. Each doctor's denominator is its own.
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(2), 2, "pledger", "CON", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);

        var mix = context.Store.GetReports().DoctorProcedureMix!;

        var otte = Assert.Single(mix, row => row.DoctorId == "otte");
        Assert.Equal("EXT", otte.ProcedureCode);
        Assert.Equal(2, otte.CaseCount);
        Assert.Equal(2, otte.DoctorCompletedCaseCount);
        Assert.Equal(1d, otte.ShareOfDoctorCases, 3);

        var pledger = Assert.Single(mix, row => row.DoctorId == "pledger");
        Assert.Equal("CON", pledger.ProcedureCode);
        Assert.Equal(1, pledger.CaseCount);
        Assert.Equal(1, pledger.DoctorCompletedCaseCount);
        Assert.Equal(1d, pledger.ShareOfDoctorCases, 3);
    }

    [Fact]
    public void Doctor_procedure_mix_excludes_incomplete_and_reporting_exception_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // One valid, completed extraction for Otte - the only case that should count.
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);

        // A legacy standalone-sedation completed cycle is a reporting exception (excluded from
        // standard metrics); it must not appear in the mix nor inflate the doctor's denominator.
        var legacyCycle = new CompletedRoomCycle
        {
            RoomId = 2,
            AssignedDoctor = "otte",
            ProcedureCode = "SED",
            SeatedAt = baseTime.AddHours(3),
            ReadyForDoctorAt = baseTime.AddHours(3).AddMinutes(5),
            DoctorArrivedAt = baseTime.AddHours(3).AddMinutes(15),
            DoctorCompleteAt = baseTime.AddHours(3).AddMinutes(25),
            RoomAvailableAt = baseTime.AddHours(3).AddMinutes(30),
            SeatedToDoctorSeconds = 900,
            PrepSeconds = 300,
            ReadyToDoctorSeconds = 600,
            DoctorInRoomSeconds = 600,
            TurnoverSeconds = 300,
            TotalRoomCycleSeconds = 1800,
            FinalWaitState = "ready-for-doctor"
        };
        context.Repository.SaveCompletedCycle(legacyCycle, context.Doctors, context.Procedures);

        // An incomplete cycle (never reaches Room Available) must not count either.
        clock.SetUtcNow(baseTime.AddHours(5));
        Assert.NotNull(SeatViaPrestage(context.Store, 3, "otte", "IMP"));
        clock.SetUtcNow(baseTime.AddHours(5).AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(3));

        var mix = context.Store.GetReports().DoctorProcedureMix!;
        var otteRows = mix.Where(row => row.DoctorId == "otte").ToList();

        var only = Assert.Single(otteRows);
        Assert.Equal("EXT", only.ProcedureCode);
        Assert.Equal(1, only.CaseCount);
        Assert.Equal(1, only.DoctorCompletedCaseCount);
        Assert.Equal(1d, only.ShareOfDoctorCases, 3);
        Assert.DoesNotContain(mix, row => row.ProcedureCode == "SED");
        Assert.DoesNotContain(mix, row => row.ProcedureCode == "IMP");
    }

    [Fact]
    public void Doctor_procedure_mix_skips_cycles_with_blank_doctor()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // A completed cycle with no assigned doctor cannot be attributed a per-doctor share, so it is
        // dropped from the mix entirely rather than forming a blank-doctor row.
        var orphanCycle = new CompletedRoomCycle
        {
            RoomId = 1,
            AssignedDoctor = "",
            ProcedureCode = "EXT",
            SeatedAt = baseTime,
            ReadyForDoctorAt = baseTime.AddMinutes(5),
            DoctorArrivedAt = baseTime.AddMinutes(15),
            DoctorCompleteAt = baseTime.AddMinutes(25),
            RoomAvailableAt = baseTime.AddMinutes(30),
            SeatedToDoctorSeconds = 900,
            PrepSeconds = 300,
            ReadyToDoctorSeconds = 600,
            DoctorInRoomSeconds = 600,
            TurnoverSeconds = 300,
            TotalRoomCycleSeconds = 1800,
            FinalWaitState = "ready-for-doctor"
        };
        first.Repository.SaveCompletedCycle(orphanCycle, first.Doctors, first.Procedures);

        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var mix = second.Store.GetReports().DoctorProcedureMix!;

        Assert.DoesNotContain(mix, row => string.IsNullOrWhiteSpace(row.DoctorId));
    }

    [Fact]
    public void Base_procedure_summaries_roll_up_variants_over_raw_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Three extractions with distinct durations: two plain, one with sedation. Distinct totals
        // make the base-group median (over all three raw cycles) differ from anything that could be
        // recombined from the per-variant EXT summary, which only covers two of them.
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 2, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 20, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(2), 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 30, turnoverMin: 5, sedation: true);

        var reports = context.Store.GetReports();

        // The full-variant summaries stay distinct.
        Assert.Equal(2, Assert.Single(reports.ProcedureSummaries, s => s.ProcedureCode == "EXT").CompletedCycleCount);
        Assert.Equal(1, Assert.Single(reports.ProcedureSummaries, s => s.ProcedureCode == "EXT+SED").CompletedCycleCount);

        // The base roll-up aggregates all three cycles under "EXT" / "Extraction".
        var baseExtraction = Assert.Single(reports.BaseProcedureSummaries, s => s.ProcedureCode == "EXT");
        Assert.Equal("Extraction", baseExtraction.ProcedureLabel);
        Assert.Equal("EXT", baseExtraction.BaseProcedureCode);
        Assert.False(baseExtraction.IsSedationCase);
        Assert.Equal(3, baseExtraction.CompletedCycleCount);

        // Median is computed from the raw cycles, not recombined from variant medians.
        var orderedTotals = reports.RecentCompletedCycles
            .Select(cycle => cycle.TotalRoomCycleSeconds!.Value)
            .OrderBy(value => value)
            .ToList();
        Assert.Equal(3, orderedTotals.Count);
        double expectedBaseMedianTotal = orderedTotals[1];
        Assert.Equal(expectedBaseMedianTotal, baseExtraction.MedianTotalSeconds);
        // Sanity: the variant EXT median (two cycles) is the mean of the two and differs.
        Assert.NotEqual(expectedBaseMedianTotal, Assert.Single(reports.ProcedureSummaries, s => s.ProcedureCode == "EXT").MedianTotalSeconds);
    }

    [Fact]
    public void Sedation_and_non_sedation_counts_partition_completed_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Two non-sedation cycles, one sedation cycle.
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 2, "otte", "IMP", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(2), 1, "otte", "IMP", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5, sedation: true);

        var reports = context.Store.GetReports();

        Assert.Equal(1, reports.SedationCaseCount);
        Assert.Equal(2, reports.NonSedationCaseCount);
        Assert.Equal(reports.CompletedRoomCyclesCount, reports.SedationCaseCount + reports.NonSedationCaseCount);
    }

    // -------------------------------------------------------------------------
    // Reporting population semantics (characterization) - locks the two intended
    // population axes so future work does not accidentally normalize them into a
    // regression. These assert CURRENT behavior; they are not aspirational.
    //
    //   Completion axis: finalized phase timings (seated-to-doctor / ready-to-doctor /
    //   prep / doctor-in-room) are reported as soon as that phase finalizes - before the
    //   room is fully completed (Room Available). Throughput/allocation/schedule-fit/trend
    //   metrics intentionally use only fully-completed (Room Available) cycles.
    //
    //   Hygiene axis: a fully-completed cycle can still be a reporting exception, counted in
    //   CompletedRoomCyclesCount but excluded from the standard/included partition.
    // -------------------------------------------------------------------------

    [Fact]
    public void Doctor_arrived_only_cycle_reports_finalized_waits_but_is_not_a_completed_cycle()
    {
        // Drive only to Doctor Arrived (no Doctor Complete / Room Available). The seated-to-doctor,
        // prep, and ready-to-doctor phases are finalized at arrival and must be reported now, even
        // though the room cycle is not complete. This locks the intended completion-axis split.
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "EXT", expectedAllocationUnits: 3));
        clock.SetUtcNow(baseTime.AddMinutes(5));   // Ready for Doctor: prep = 5 min
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(baseTime.AddMinutes(15));  // Doctor Arrived: ready-to-doctor = 10 min, seated-to-doctor = 15 min
        Assert.NotNull(context.Store.MarkDoctorArrived(1));

        var reports = context.Store.GetReports();

        // Completion axis: not a completed cycle yet (no Room Available).
        Assert.Equal(0, reports.CompletedRoomCyclesCount);
        Assert.Equal(0, reports.IncludedCompletedCycleCount);

        // Finalized phase timings ARE reported for this in-progress case.
        Assert.Equal(15 * 60, reports.AverageSeatedToDoctorSeconds);
        Assert.Equal(15 * 60, reports.MedianSeatedToDoctorSeconds);
        Assert.Equal(5 * 60, reports.AveragePrepSeconds);
        Assert.Equal(10 * 60, reports.AverageReadyToDoctorSeconds);

        // Phases that finalize only at/after Doctor Complete contribute nothing yet (null-skipped).
        Assert.Equal(0, reports.AverageDoctorInRoomSeconds);
        Assert.Equal(0, reports.AverageTurnoverSeconds);

        // Fully-completed-cycle populations exclude the in-progress case.
        Assert.Equal(0, reports.AllocationVariance!.AllocationVarianceCycleCount);
        Assert.Equal(0, reports.ScheduleFit!.ScheduleFitCycleCount);
        Assert.Empty(reports.Trends!.Buckets);
    }

    [Fact]
    public void Turnover_not_available_cycle_reports_doctor_in_room_but_not_completed_populations()
    {
        // Drive through Doctor Complete but NOT Room Available. Doctor-in-room finalizes at Complete
        // and is reported; turnover only finalizes at Room Available, so it contributes nothing yet;
        // and the cycle is still excluded from fully-completed populations.
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "EXT", expectedAllocationUnits: 3));
        clock.SetUtcNow(baseTime.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(baseTime.AddMinutes(15));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        clock.SetUtcNow(baseTime.AddMinutes(25));  // Doctor Complete: doctor-in-room = 10 min
        Assert.NotNull(context.Store.MarkDoctorComplete(1));

        var reports = context.Store.GetReports();

        // Completion axis: still not a fully-completed (Room Available) cycle.
        Assert.Equal(0, reports.CompletedRoomCyclesCount);
        Assert.Equal(0, reports.IncludedCompletedCycleCount);

        // Finalized phases contribute: seated-to-doctor (15 min) and doctor-in-room (10 min).
        Assert.Equal(15 * 60, reports.AverageSeatedToDoctorSeconds);
        Assert.Equal(10 * 60, reports.AverageDoctorInRoomSeconds);
        // Turnover finalizes only at Room Available, so it contributes nothing yet.
        Assert.Equal(0, reports.AverageTurnoverSeconds);

        // Fully-completed-cycle populations all exclude it.
        Assert.Equal(0, reports.AllocationVariance!.AllocationVarianceCycleCount);
        Assert.Equal(0, reports.ScheduleFit!.ScheduleFitCycleCount);
        Assert.Empty(reports.Trends!.Buckets);
    }

    [Fact]
    public void Reporting_exception_completed_cycle_is_counted_but_excluded_from_included_partition()
    {
        // Two clean fully-completed cycles plus one fully-completed reporting-exception cycle (a
        // legacy standalone "SED" record). All three are completed (Room Available), so all three are
        // counted in CompletedRoomCyclesCount - but the legacy one is excluded from the standard /
        // included partition. This locks the TRUE invariant: the sedation/non-sedation split
        // partitions IncludedCompletedCycleCount, not the raw completed count.
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);

        var first = StoreContext.Create(
            workspace, environmentName: Environments.Production, databasePath: databasePath, timeProvider: clock);

        RunProcedureCycle(first, clock, baseTime, 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(first, clock, baseTime.AddHours(1), 2, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5, sedation: true);

        // Legacy standalone "SED" is a reporting exception (no longer an active procedure family) but
        // is still a fully-completed cycle with Room Available set.
        var legacyCycle = new CompletedRoomCycle
        {
            RoomId = 3,
            AssignedDoctor = "otte",
            ProcedureCode = "SED",
            SeatedAt = baseTime.AddHours(2),
            ReadyForDoctorAt = baseTime.AddHours(2).AddMinutes(5),
            DoctorArrivedAt = baseTime.AddHours(2).AddMinutes(15),
            DoctorCompleteAt = baseTime.AddHours(2).AddMinutes(25),
            RoomAvailableAt = baseTime.AddHours(2).AddMinutes(30),
            SeatedToDoctorSeconds = 900,
            PrepSeconds = 300,
            ReadyToDoctorSeconds = 600,
            DoctorInRoomSeconds = 600,
            TurnoverSeconds = 300,
            TotalRoomCycleSeconds = 1800,
            FinalWaitState = "ready-for-doctor"
        };
        first.Repository.SaveCompletedCycle(legacyCycle, first.Doctors, first.Procedures);

        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = second.Store.GetReports();

        // Hygiene axis: all three are completed (counted); the legacy one is excluded from standard.
        Assert.Equal(3, reports.CompletedRoomCyclesCount);
        Assert.Equal(2, reports.IncludedCompletedCycleCount);
        Assert.Equal(1, reports.ExcludedCompletedCycleCount);

        // True invariant: sedation + non-sedation partitions the INCLUDED population.
        Assert.Equal(1, reports.SedationCaseCount);
        Assert.Equal(1, reports.NonSedationCaseCount);
        Assert.Equal(reports.IncludedCompletedCycleCount, reports.SedationCaseCount + reports.NonSedationCaseCount);

        // And that sum is strictly less than the raw completed count when a reporting-exception
        // completed cycle is present (the older "equals CompletedRoomCyclesCount" framing only holds
        // for fully-clean data).
        Assert.True(
            reports.SedationCaseCount + reports.NonSedationCaseCount < reports.CompletedRoomCyclesCount,
            "A reporting-exception completed cycle must make the sedation partition smaller than the raw completed count.");
    }

    [Fact]
    public void Legacy_standalone_sedation_completed_cycle_is_flagged_excluded_but_readable()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // Simulate a completed cycle persisted before sedation became a modifier: the stored
        // procedure code is the standalone legacy "SED".
        var legacyCycle = new CompletedRoomCycle
        {
            RoomId = 1,
            AssignedDoctor = "otte",
            ProcedureCode = "SED",
            SeatedAt = baseTime,
            ReadyForDoctorAt = baseTime.AddMinutes(5),
            DoctorArrivedAt = baseTime.AddMinutes(15),
            DoctorCompleteAt = baseTime.AddMinutes(25),
            RoomAvailableAt = baseTime.AddMinutes(30),
            SeatedToDoctorSeconds = 900,
            PrepSeconds = 300,
            ReadyToDoctorSeconds = 600,
            DoctorInRoomSeconds = 600,
            TurnoverSeconds = 300,
            TotalRoomCycleSeconds = 1800,
            FinalWaitState = "ready-for-doctor"
        };
        first.Repository.SaveCompletedCycle(legacyCycle, first.Doctors, first.Procedures);

        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = second.Store.GetReports();

        // Standalone Sedation is legacy data now that sedation is a modifier: it must not appear as a
        // current procedure family and must not contribute to standard sedation counts.
        Assert.DoesNotContain(reports.ProcedureSummaries, summary => summary.ProcedureCode == "SED");
        Assert.DoesNotContain(reports.BaseProcedureSummaries, summary => summary.ProcedureCode == "SED");
        Assert.Equal(0, reports.SedationCaseCount);
        Assert.Equal(0, reports.NonSedationCaseCount);
        Assert.Equal(0, reports.IncludedCompletedCycleCount);
        Assert.Equal(1, reports.ExcludedCompletedCycleCount);
        Assert.Equal(1, reports.ExceptionCount);

        // The record is retained and remains visible in raw/audit output, flagged and relabeled.
        var legacy = Assert.Single(reports.RecentCompletedCycles, cycle => cycle.ProcedureCode == "SED");
        Assert.True(legacy.HasReportingException);
        Assert.True(legacy.IsLegacyProcedure);
        Assert.False(legacy.IsUnmappedProcedure);
        Assert.True(legacy.IsExcludedFromStandardMetrics);
        Assert.Contains(ReportingExceptionReasons.LegacyProcedure, legacy.ReportingExceptionReasons);
        Assert.Equal("Sedation (Legacy)", legacy.DisplayProcedureLabel);
    }

    [Fact]
    public void Unknown_procedure_completed_cycle_is_flagged_as_unmapped_and_excluded()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        var unmappedCycle = new CompletedRoomCycle
        {
            RoomId = 1,
            AssignedDoctor = "otte",
            ProcedureCode = "ZZZ",
            SeatedAt = baseTime,
            ReadyForDoctorAt = baseTime.AddMinutes(5),
            DoctorArrivedAt = baseTime.AddMinutes(15),
            DoctorCompleteAt = baseTime.AddMinutes(25),
            RoomAvailableAt = baseTime.AddMinutes(30),
            SeatedToDoctorSeconds = 900,
            PrepSeconds = 300,
            ReadyToDoctorSeconds = 600,
            DoctorInRoomSeconds = 600,
            TurnoverSeconds = 300,
            TotalRoomCycleSeconds = 1800,
            FinalWaitState = "ready-for-doctor"
        };
        first.Repository.SaveCompletedCycle(unmappedCycle, first.Doctors, first.Procedures);

        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = second.Store.GetReports();

        Assert.DoesNotContain(reports.ProcedureSummaries, summary => summary.ProcedureCode == "ZZZ");
        Assert.Equal(0, reports.IncludedCompletedCycleCount);
        Assert.Equal(1, reports.ExcludedCompletedCycleCount);

        var unmapped = Assert.Single(reports.RecentCompletedCycles, cycle => cycle.ProcedureCode == "ZZZ");
        Assert.True(unmapped.HasReportingException);
        Assert.True(unmapped.IsUnmappedProcedure);
        Assert.False(unmapped.IsLegacyProcedure);
        Assert.True(unmapped.IsExcludedFromStandardMetrics);
        Assert.Contains(ReportingExceptionReasons.UnmappedProcedure, unmapped.ReportingExceptionReasons);
        Assert.Equal("ZZZ (Unmapped)", unmapped.DisplayProcedureLabel);
    }

    [Fact]
    public void Extreme_duration_completed_cycle_is_flagged_and_excluded()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        // ~18 hour case flow, modelling an accidentally-open overnight record.
        var seatedAt = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        var extremeCycle = new CompletedRoomCycle
        {
            RoomId = 1,
            AssignedDoctor = "otte",
            ProcedureCode = "EXT",
            SeatedAt = seatedAt,
            ReadyForDoctorAt = seatedAt.AddMinutes(5),
            DoctorArrivedAt = seatedAt.AddMinutes(15),
            DoctorCompleteAt = seatedAt.AddHours(18),
            RoomAvailableAt = seatedAt.AddHours(18).AddMinutes(5),
            SeatedToDoctorSeconds = 900,
            PrepSeconds = 300,
            ReadyToDoctorSeconds = 600,
            DoctorInRoomSeconds = 63900,
            TurnoverSeconds = 300,
            TotalRoomCycleSeconds = 65100,
            FinalWaitState = "ready-for-doctor"
        };
        first.Repository.SaveCompletedCycle(extremeCycle, first.Doctors, first.Procedures);

        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = second.Store.GetReports();

        Assert.DoesNotContain(reports.ProcedureSummaries, summary => summary.ProcedureCode == "EXT");
        Assert.Equal(0, reports.IncludedCompletedCycleCount);
        Assert.Equal(1, reports.ExcludedCompletedCycleCount);

        var extreme = Assert.Single(reports.RecentCompletedCycles, cycle => cycle.RoomId == 1);
        Assert.True(extreme.HasReportingException);
        Assert.True(extreme.IsExcludedFromStandardMetrics);
        Assert.Contains(ReportingExceptionReasons.ExtremeDuration, extreme.ReportingExceptionReasons);
    }

    [Fact]
    public void Overnight_lifecycle_completed_cycle_is_flagged_independent_of_duration()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        // A short (~1 hour) case that nonetheless crosses midnight.
        var seatedAt = new DateTimeOffset(2026, 6, 1, 23, 30, 0, TimeSpan.Zero);

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        var overnightCycle = new CompletedRoomCycle
        {
            RoomId = 1,
            AssignedDoctor = "otte",
            ProcedureCode = "EXT",
            SeatedAt = seatedAt,
            ReadyForDoctorAt = seatedAt.AddMinutes(5),
            DoctorArrivedAt = seatedAt.AddMinutes(15),
            DoctorCompleteAt = seatedAt.AddMinutes(45),
            RoomAvailableAt = seatedAt.AddMinutes(50),
            SeatedToDoctorSeconds = 900,
            PrepSeconds = 300,
            ReadyToDoctorSeconds = 600,
            DoctorInRoomSeconds = 1800,
            TurnoverSeconds = 300,
            TotalRoomCycleSeconds = 3000,
            FinalWaitState = "ready-for-doctor"
        };
        first.Repository.SaveCompletedCycle(overnightCycle, first.Doctors, first.Procedures);

        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = second.Store.GetReports();

        var overnight = Assert.Single(reports.RecentCompletedCycles, cycle => cycle.RoomId == 1);
        Assert.True(overnight.HasReportingException);
        Assert.True(overnight.IsExcludedFromStandardMetrics);
        Assert.Contains(ReportingExceptionReasons.OvernightLifecycle, overnight.ReportingExceptionReasons);
        // A 45-minute case is overnight but not extreme.
        Assert.DoesNotContain(ReportingExceptionReasons.ExtremeDuration, overnight.ReportingExceptionReasons);
        Assert.Equal(1, reports.ExcludedCompletedCycleCount);
    }

    // -------------------------------------------------------------------------
    // Allocation variance (expected allocation vs measured case flow)
    // -------------------------------------------------------------------------

    [Fact]
    public void Positive_allocation_variance_when_case_flow_runs_over_expected()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // EXT default is 3 units (30 min expected). Seat -> Doctor Complete here is 5+10+25 = 40 min.
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 25, turnoverMin: 5);

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(30, cycle.ExpectedAllocationMinutes);
        Assert.Equal(40, cycle.MeasuredCaseFlowMinutes);
        Assert.Equal(10, cycle.AllocationVarianceMinutes);
        Assert.True(cycle.HasAllocationVariance);
        Assert.True(cycle.IsOverExpectedAllocation);
        Assert.False(cycle.IsUnderExpectedAllocation);
        Assert.False(cycle.IsAtExpectedAllocation);
    }

    [Fact]
    public void Negative_allocation_variance_when_case_flow_runs_under_expected()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // IMP default is 6 units (60 min expected). Seat -> Doctor Complete here is 5+10+20 = 35 min.
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "IMP", prepMin: 5, readyMin: 10, doctorMin: 20, turnoverMin: 5);

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(60, cycle.ExpectedAllocationMinutes);
        Assert.Equal(35, cycle.MeasuredCaseFlowMinutes);
        Assert.Equal(-25, cycle.AllocationVarianceMinutes);
        Assert.True(cycle.HasAllocationVariance);
        Assert.True(cycle.IsUnderExpectedAllocation);
        Assert.False(cycle.IsOverExpectedAllocation);
        Assert.False(cycle.IsAtExpectedAllocation);
    }

    [Fact]
    public void Zero_allocation_variance_when_case_flow_matches_expected()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // EXT default is 3 units (30 min expected). Seat -> Doctor Complete here is 5+10+15 = 30 min.
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 15, turnoverMin: 5);

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(30, cycle.ExpectedAllocationMinutes);
        Assert.Equal(30, cycle.MeasuredCaseFlowMinutes);
        Assert.Equal(0, cycle.AllocationVarianceMinutes);
        Assert.False(cycle.HasAllocationVariance);
        Assert.True(cycle.IsAtExpectedAllocation);
        Assert.False(cycle.IsOverExpectedAllocation);
        Assert.False(cycle.IsUnderExpectedAllocation);
    }

    [Fact]
    public void Missing_doctor_complete_does_not_calculate_allocation_variance()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Drive only as far as Doctor Arrived: a cycle exists but DoctorCompleteAt is still null.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "EXT"));
        clock.SetUtcNow(baseTime.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(baseTime.AddMinutes(15));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));

        var reports = context.Store.GetReports();
        // The in-progress cycle has no RoomAvailableAt, so it is not in the completed audit list,
        // and it contributes nothing to the standard variance aggregate.
        Assert.DoesNotContain(reports.RecentCompletedCycles, c => c.RoomId == 1);
        Assert.Equal(0, reports.AllocationVariance!.AllocationVarianceCycleCount);

        var persisted = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.Null(persisted.DoctorCompleteAt);
    }

    [Fact]
    public void Missing_expected_allocation_does_not_calculate_allocation_variance()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // A completed cycle with no expected allocation captured (0 minutes), but a mapped, active
        // procedure so it is not a reporting exception.
        var cycle = new CompletedRoomCycle
        {
            RoomId = 1,
            AssignedDoctor = "otte",
            ProcedureCode = "EXT",
            SeatedAt = baseTime,
            ReadyForDoctorAt = baseTime.AddMinutes(5),
            DoctorArrivedAt = baseTime.AddMinutes(15),
            DoctorCompleteAt = baseTime.AddMinutes(40),
            RoomAvailableAt = baseTime.AddMinutes(45),
            SeatedToDoctorSeconds = 900,
            PrepSeconds = 300,
            ReadyToDoctorSeconds = 600,
            DoctorInRoomSeconds = 1500,
            TurnoverSeconds = 300,
            TotalRoomCycleSeconds = 2700,
            FinalWaitState = "ready-for-doctor",
            ExpectedAllocationMinutes = 0,
            ExpectedAllocationUnits = 0
        };
        first.Repository.SaveCompletedCycle(cycle, first.Doctors, first.Procedures);

        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = second.Store.GetReports();

        var loaded = Assert.Single(reports.RecentCompletedCycles, c => c.RoomId == 1);
        // Measured case flow is still exposed (DoctorCompleteAt present), but variance is not computed.
        Assert.Equal(40, loaded.MeasuredCaseFlowMinutes);
        Assert.Null(loaded.AllocationVarianceMinutes);
        Assert.False(loaded.HasAllocationVariance);
        Assert.False(loaded.IsAtExpectedAllocation);
        Assert.Equal(0, reports.AllocationVariance!.AllocationVarianceCycleCount);
    }

    [Fact]
    public void Reporting_exception_cycle_does_not_contribute_to_standard_variance_aggregates()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // An extreme-duration cycle (~18h) that nonetheless carries an expected allocation snapshot.
        var extreme = new CompletedRoomCycle
        {
            RoomId = 1,
            AssignedDoctor = "otte",
            ProcedureCode = "EXT",
            SeatedAt = baseTime,
            ReadyForDoctorAt = baseTime.AddMinutes(5),
            DoctorArrivedAt = baseTime.AddMinutes(15),
            DoctorCompleteAt = baseTime.AddHours(18),
            RoomAvailableAt = baseTime.AddHours(18).AddMinutes(5),
            SeatedToDoctorSeconds = 900,
            PrepSeconds = 300,
            ReadyToDoctorSeconds = 600,
            DoctorInRoomSeconds = 63900,
            TurnoverSeconds = 300,
            TotalRoomCycleSeconds = 65100,
            FinalWaitState = "ready-for-doctor",
            ExpectedAllocationUnits = 3,
            ExpectedAllocationMinutes = 30,
            OriginalDefaultExpectedUnits = 3
        };
        first.Repository.SaveCompletedCycle(extreme, first.Doctors, first.Procedures);

        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = second.Store.GetReports();

        // Excluded from standard aggregates, so the global variance summary is empty...
        Assert.Equal(0, reports.AllocationVariance!.AllocationVarianceCycleCount);
        Assert.Equal(0, reports.AllocationVariance.NetAllocationVarianceMinutes);

        // ...but its own allocation fields remain exposed for raw/audit.
        var raw = Assert.Single(reports.RecentCompletedCycles, c => c.RoomId == 1);
        Assert.True(raw.IsExcludedFromStandardMetrics);
        Assert.Equal(30, raw.ExpectedAllocationMinutes);
        Assert.True(raw.MeasuredCaseFlowMinutes > 1000);
        Assert.True(raw.IsOverExpectedAllocation);
    }

    [Fact]
    public void Global_allocation_variance_aggregate_sums_over_included_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // EXT (30 expected): over by +10 (40 measured).
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 25, turnoverMin: 5);
        // IMP (60 expected): under by -25 (35 measured).
        RunProcedureCycle(context, clock, baseTime.AddHours(2), 2, "otte", "IMP", prepMin: 5, readyMin: 10, doctorMin: 20, turnoverMin: 5);

        var allocation = context.Store.GetReports().AllocationVariance!;
        Assert.Equal(2, allocation.AllocationVarianceCycleCount);
        Assert.Equal(90, allocation.TotalExpectedAllocationMinutes);
        Assert.Equal(75, allocation.TotalMeasuredCaseFlowMinutes);
        Assert.Equal(-15, allocation.NetAllocationVarianceMinutes);
        Assert.Equal(-7.5, allocation.AverageAllocationVarianceMinutes);
        Assert.Equal(1, allocation.CasesOverExpectedAllocation);
        Assert.Equal(1, allocation.CasesUnderExpectedAllocation);
        Assert.Equal(0, allocation.CasesAtExpectedAllocation);
    }

    [Fact]
    public void Doctor_and_procedure_summaries_carry_allocation_variance_aggregates()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Two EXT cycles for the same doctor: +10 and +10 over the 30-min expected allocation.
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 25, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(2), 2, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 25, turnoverMin: 5);

        var reports = context.Store.GetReports();

        var doctor = Assert.Single(reports.DoctorSummaries);
        Assert.Equal(2, doctor.Allocation.AllocationVarianceCycleCount);
        Assert.Equal(20, doctor.Allocation.NetAllocationVarianceMinutes);
        Assert.Equal(2, doctor.Allocation.CasesOverExpectedAllocation);

        var procedure = Assert.Single(reports.ProcedureSummaries, summary => summary.ProcedureCode == "EXT");
        Assert.Equal(2, procedure.Allocation.AllocationVarianceCycleCount);
        Assert.Equal(60, procedure.Allocation.TotalExpectedAllocationMinutes);
        Assert.Equal(80, procedure.Allocation.TotalMeasuredCaseFlowMinutes);
        Assert.Equal(20, procedure.Allocation.NetAllocationVarianceMinutes);

        var baseProcedure = Assert.Single(reports.BaseProcedureSummaries, summary => summary.ProcedureCode == "EXT");
        Assert.Equal(2, baseProcedure.Allocation.AllocationVarianceCycleCount);
        Assert.Equal(20, baseProcedure.Allocation.NetAllocationVarianceMinutes);
    }

    [Fact]
    public void Adjusted_allocation_cycle_count_reflects_units_changed_from_default()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // One EXT seated at its default allocation, one EXT seated with adjusted units.
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 25, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(2), 2, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 25, turnoverMin: 5, expectedAllocationUnits: 6);

        var allocation = context.Store.GetReports().AllocationVariance!;
        Assert.Equal(2, allocation.AllocationVarianceCycleCount);
        Assert.Equal(1, allocation.AdjustedAllocationCycleCount);
    }

    [Fact]
    public void Normal_completed_cycle_is_not_flagged_and_is_included()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        RunProcedureCycle(context, clock, baseTime, 1, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);

        var reports = context.Store.GetReports();

        var cycle = Assert.Single(reports.RecentCompletedCycles);
        Assert.False(cycle.HasReportingException);
        Assert.False(cycle.IsExcludedFromStandardMetrics);
        Assert.Empty(cycle.ReportingExceptionReasons);
        Assert.Equal("Extraction", cycle.DisplayProcedureLabel);
        Assert.Equal(1, reports.IncludedCompletedCycleCount);
        Assert.Equal(0, reports.ExcludedCompletedCycleCount);
        Assert.Equal(0, reports.ExceptionCount);
        // The included cycle still drives standard procedure baselines.
        Assert.Contains(reports.ProcedureSummaries, summary => summary.ProcedureCode == "EXT");
    }

    [Fact]
    public void Empty_report_snapshot_exposes_empty_additive_fields()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var reports = context.Store.GetReports();

        Assert.Equal(0, reports.CompletedRoomCyclesCount);
        Assert.Equal(0, reports.SedationCaseCount);
        Assert.Equal(0, reports.NonSedationCaseCount);
        Assert.Empty(reports.ProcedureSummaries);
        Assert.Empty(reports.BaseProcedureSummaries);
    }

    [Fact]
    public void Historical_standalone_sedation_records_remain_readable()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // Simulate a legacy record persisted before sedation became a modifier: a room whose
        // stored procedure code is the standalone "SED".
        var legacyRoom = new RoomState(1)
        {
            AssignedDoctor = "otte",
            ProcedureCode = "SED",
            State = RoomStates.Seated,
            SeatedAt = first.Store.GetSnapshot().ServerTime
        };
        first.Repository.SaveRooms([legacyRoom], first.Doctors, first.Procedures);

        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reloaded = second.Store.GetRoom(1);

        Assert.NotNull(reloaded);
        Assert.Equal("SED", reloaded.ProcedureCode);
        Assert.Equal("SED", reloaded.Procedure?.Code);
        Assert.Equal("Sedation", reloaded.Procedure?.Label);
    }

    [Fact]
    public void Doctor_roster_validation_rejects_duplicates_and_blank_required_fields()
    {
        var duplicateResult = ValidateDoctorRoster(new DoctorRosterOptions
        {
            Doctors =
            [
                new() { Id = "otte", DisplayName = "Dr. Otte", ShortName = "Otte", Color = "#2563eb", Active = true },
                new() { Id = "OTTE", DisplayName = "Dr. Other", ShortName = "Other", Color = "#16a34a", Active = true }
            ]
        });
        var blankResult = ValidateDoctorRoster(new DoctorRosterOptions
        {
            Doctors =
            [
                new() { Id = "", DisplayName = "", ShortName = "", Color = "blue", Active = true }
            ]
        });

        Assert.True(duplicateResult.Failed);
        Assert.Contains("unique Id", string.Join(" ", duplicateResult.Failures));
        Assert.True(blankResult.Failed);
        Assert.Contains("Id is required", string.Join(" ", blankResult.Failures));
        Assert.Contains("DisplayName is required", string.Join(" ", blankResult.Failures));
        Assert.Contains("ShortName is required", string.Join(" ", blankResult.Failures));
        Assert.Contains("Color must be a valid hex color", string.Join(" ", blankResult.Failures));
    }

    [Fact]
    public void Procedure_roster_validation_rejects_duplicates_and_blank_required_fields()
    {
        var duplicateResult = ValidateProcedureRoster(new ProcedureRosterOptions
        {
            Procedures =
            [
                new() { Code = "CON", Label = "Consult", Icon = "speech", Active = true },
                new() { Code = "con", Label = "Duplicate", Icon = "vial", Active = true }
            ]
        });
        var blankResult = ValidateProcedureRoster(new ProcedureRosterOptions
        {
            Procedures =
            [
                new() { Code = "", Label = "", Icon = "", Active = true }
            ]
        });

        Assert.True(duplicateResult.Failed);
        Assert.Contains("unique Code", string.Join(" ", duplicateResult.Failures));
        Assert.True(blankResult.Failed);
        Assert.Contains("Code is required", string.Join(" ", blankResult.Failures));
        Assert.Contains("Label is required", string.Join(" ", blankResult.Failures));
        Assert.Contains("Icon is required", string.Join(" ", blankResult.Failures));
    }

    [Fact]
    public void Roster_validation_requires_at_least_one_active_entry()
    {
        var doctorResult = ValidateDoctorRoster(new DoctorRosterOptions
        {
            Doctors =
            [
                new() { Id = "inactive", DisplayName = "Dr. Inactive", ShortName = "Inactive", Color = "#2563eb", Active = false }
            ]
        });
        var procedureResult = ValidateProcedureRoster(new ProcedureRosterOptions
        {
            Procedures =
            [
                new() { Code = "OFF", Label = "Inactive", Icon = "vial", Active = false }
            ]
        });

        Assert.True(doctorResult.Failed);
        Assert.Contains("at least one active doctor", string.Join(" ", doctorResult.Failures));
        Assert.True(procedureResult.Failed);
        Assert.Contains("at least one active procedure", string.Join(" ", procedureResult.Failures));
    }

    [Fact]
    public void Configured_roster_text_is_escaped_before_inner_html_rendering()
    {
        var boardJs = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ChairSide.Board",
            "wwwroot",
            "board.js"));
        var roomWorkflowJs = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ChairSide.Board",
            "wwwroot",
            "room-workflow.js"));
        var domUtilities = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ChairSide.Board",
            "wwwroot",
            "dom-utils.js"));

        Assert.Contains(
            "import { escapeAttribute, escapeHtml } from \"./dom-utils.js\";",
            boardJs);
        Assert.Contains(
            "import { escapeAttribute, escapeHtml, setDisabled, setHidden } from \"./dom-utils.js\";",
            roomWorkflowJs);
        Assert.Contains("export function escapeHtml", domUtilities);
        Assert.Contains("export function escapeAttribute", domUtilities);
        Assert.Contains("export function setDisabled", domUtilities);
        Assert.Contains("export function setHidden", domUtilities);
        Assert.DoesNotContain("function escapeHtml", boardJs);
        Assert.Contains("${escapeHtml(doctor.name)}", boardJs);
        Assert.Contains("${escapeHtml(procedure.code)}", boardJs);
        Assert.Contains("${escapeHtml(procedure.label)}", boardJs);
        Assert.DoesNotContain(">${doctor.name}", boardJs);
        Assert.DoesNotContain("<strong>${procedure.code}</strong>", boardJs);
        Assert.DoesNotContain("<small>${procedure.label}</small>", boardJs);
    }

    [Fact]
    public void Procedure_icon_renderer_supports_all_icons_used_by_default_roster()
    {
        var boardJs = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ChairSide.Board",
            "wwwroot",
            "board.js"));

        // Every icon name referenced by DefaultProcedures() must have an entry
        // in the renderProcedureIcon icons map so tiles never fall back to the
        // empty placeholder icon. INTCK uses the inline SVG alias for "interlock";
        // sync remains in the map for backward compat but is no longer a default-roster icon.
        var requiredIcons = new[] { "speech", "forceps", "moon", "check", "bolt", "vial", "teeth", "interlock", "wrench", "phone", "uncover", "bond", "archfour" };
        foreach (var icon in requiredIcons)
        {
            Assert.Contains($"{icon}:", boardJs);
        }
    }

}
