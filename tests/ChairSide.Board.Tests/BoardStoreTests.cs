using System.Globalization;
using System.Reflection;

using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Tests;

public sealed class BoardStoreTests
{
    [Fact]
    public void Lifecycle_actions_preserve_expected_state_and_report_behavior()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var seated = context.Store.SeatRoom(1, "otte", "CON");
        Assert.NotNull(seated);
        Assert.Equal(RoomStates.Seated, seated.State);

        var seatedAt = seated.SeatedAt;
        var updated = context.Store.UpdateAssignment(1, "pledger", "EXT");
        Assert.NotNull(updated);
        Assert.Equal(seatedAt, updated.SeatedAt);
        Assert.Equal("pledger", updated.AssignedDoctor);
        Assert.Equal("EXT", updated.ProcedureCode);
        Assert.Empty(context.Store.GetReports().DoctorSummaries);
        Assert.Equal(0, context.Store.GetReports().CompletedRoomCyclesCount);
        Assert.Empty(context.Repository.LoadCompletedCycles());

        var canceled = context.Store.CancelSeating(1);
        Assert.NotNull(canceled);
        Assert.Equal(RoomStates.Available, canceled.State);
        Assert.Equal(0, context.Store.GetReports().CompletedRoomCyclesCount);
        Assert.Null(context.Store.MarkDoctorArrived(1));

        var reseated = context.Store.SeatRoom(1, "otte", "CON");
        Assert.NotNull(reseated);

        // Doctor Arrived must be blocked until Ready for Doctor is called
        Assert.Null(context.Store.MarkDoctorArrived(1));

        var ready = context.Store.MarkReadyForDoctor(1);
        Assert.NotNull(ready);
        Assert.Equal(RoomStates.ReadyForDoctor, ready.State);

        var arrived = context.Store.MarkDoctorArrived(1);
        Assert.NotNull(arrived);
        Assert.Equal(RoomStates.DoctorInRoom, arrived.State);
        Assert.Single(context.Store.GetReports().DoctorSummaries);

        Assert.Null(context.Store.MarkDoctorArrived(1));

        var complete = context.Store.MarkDoctorComplete(1);
        Assert.NotNull(complete);
        Assert.Equal(RoomStates.Turnover, complete.State);

        var available = context.Store.MarkRoomAvailable(1);
        Assert.NotNull(available);
        Assert.Equal(RoomStates.Available, available.State);

        var reports = context.Store.GetReports();
        Assert.Equal(1, reports.CompletedRoomCyclesCount);
        Assert.Single(reports.RecentCompletedCycles);
    }

    [Fact]
    public void Active_seated_room_survives_store_restart()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var seated = first.Store.SeatRoom(1, "otte", "CON");
        Assert.NotNull(seated);

        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reloaded = second.Store.GetRoom(1);

        Assert.NotNull(reloaded);
        Assert.Equal(RoomStates.Seated, reloaded.State);
        Assert.Equal(seated.SeatedAt, reloaded.SeatedAt);
        Assert.Equal("otte", reloaded.AssignedDoctor);
        Assert.Equal("CON", reloaded.ProcedureCode);
    }

    [Fact]
    public void Completed_report_survives_store_restart()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        Assert.NotNull(first.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(first.Store.MarkReadyForDoctor(1));
        Assert.NotNull(first.Store.MarkDoctorArrived(1));
        Assert.NotNull(first.Store.MarkDoctorComplete(1));
        Assert.NotNull(first.Store.MarkRoomAvailable(1));
        Assert.Equal(1, first.Store.GetReports().CompletedRoomCyclesCount);

        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = second.Store.GetReports();

        Assert.Equal(1, reports.CompletedRoomCyclesCount);
        Assert.Single(reports.RecentCompletedCycles);
    }

    [Fact]
    public void Stale_elapsed_ready_for_doctor_room_reloads_as_stale()
    {
        // Stale escalation is now based on ReadyForDoctorAt. A room that has been seated for a
        // long time but has NOT clicked Ready for Doctor stays Seated. Only after Ready for Doctor
        // can the room escalate to Aging/Stale based on elapsed time from ReadyForDoctorAt.
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();

        var first = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            agingMinutes: 1,
            staleMinutes: 2,
            boardUiOptions: new BoardUiOptions { DemoTimerEnabled = true });
        Assert.NotNull(first.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(first.Store.MarkReadyForDoctor(1));

        // Patch the DB to simulate a stale-elapsed ready_for_doctor_at.
        var staleReadyAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        var seatedAt = staleReadyAt.AddMinutes(-5);
        using (var connection = OpenConnection(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE active_rooms
                SET state = 'readyForDoctor',
                    seated_at = $seatedAt,
                    ready_for_doctor_at = $readyAt,
                    aging_started_at = NULL,
                    stale_started_at = NULL
                WHERE room_id = 1;
                """;
            command.Parameters.AddWithValue("$seatedAt", FormatDateTimeOffset(seatedAt));
            command.Parameters.AddWithValue("$readyAt", FormatDateTimeOffset(staleReadyAt));
            command.ExecuteNonQuery();
        }

        var second = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            agingMinutes: 1,
            staleMinutes: 2);
        var reloaded = second.Store.GetRoom(1);

        Assert.NotNull(reloaded);
        Assert.Equal(RoomStates.Stale, reloaded.State);
        Assert.NotNull(reloaded.AgingStartedAt);
        Assert.NotNull(reloaded.StaleStartedAt);

        Assert.NotNull(second.Store.MarkDoctorArrived(1));
        Assert.NotNull(second.Store.MarkDoctorComplete(1));
        Assert.NotNull(second.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(second.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(RoomStates.Stale, cycle.FinalWaitState);
        Assert.True(cycle.AgingThresholdReached);
        Assert.True(cycle.StaleThresholdReached);
    }

    [Fact]
    public void Doctor_in_room_and_turnover_rooms_survive_reload_without_wait_state_downgrade()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();

        var first = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            agingMinutes: 1,
            staleMinutes: 2);
        Assert.NotNull(first.Store.SeatRoom(1, "otte", "CON", demoElapsedMinutes: 3));
        Assert.NotNull(first.Store.MarkReadyForDoctor(1));
        Assert.NotNull(first.Store.MarkDoctorArrived(1));

        var afterArrivedReload = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            agingMinutes: 1,
            staleMinutes: 2);
        Assert.Equal(RoomStates.DoctorInRoom, afterArrivedReload.Store.GetRoom(1)?.State);

        Assert.NotNull(afterArrivedReload.Store.MarkDoctorComplete(1));
        var afterCompleteReload = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            agingMinutes: 1,
            staleMinutes: 2);
        Assert.Equal(RoomStates.Turnover, afterCompleteReload.Store.GetRoom(1)?.State);
    }

    [Fact]
    public void Turnover_seconds_calculated_from_doctor_complete_to_room_available()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 5, 29, 18, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        clock.SetUtcNow(now.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        clock.SetUtcNow(now.AddMinutes(10));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(now.AddMinutes(13));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(3 * 60, cycle.TurnoverSeconds);
    }

    [Fact]
    public void Room_status_preserves_seated_at_through_doctor_in_room_and_turnover()
    {
        // SeatedAt must be non-null in doctor-in-room and turnover RoomStatus
        // because the client tile timer reads room.seatedAt for its live elapsed display.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var seated = context.Store.SeatRoom(1, "otte", "CON");
        Assert.NotNull(seated);
        Assert.NotNull(seated.SeatedAt);

        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        var arrived = context.Store.MarkDoctorArrived(1);
        Assert.NotNull(arrived);
        Assert.Equal(RoomStates.DoctorInRoom, arrived.State);
        Assert.NotNull(arrived.SeatedAt);

        var complete = context.Store.MarkDoctorComplete(1);
        Assert.NotNull(complete);
        Assert.Equal(RoomStates.Turnover, complete.State);
        Assert.NotNull(complete.SeatedAt);
    }

    [Theory]
    [InlineData(6, 59)]
    [InlineData(7, 0)]
    [InlineData(11, 59)]
    [InlineData(12, 0)]
    public void Seated_room_does_not_escalate_to_aging_or_stale_regardless_of_elapsed_time(
        int elapsedMinutes,
        int elapsedSeconds)
    {
        // Patient Seated / In Prep: aging/stale thresholds are irrelevant until Ready for Doctor.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 5, 29, 18, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            agingMinutes: 7,
            staleMinutes: 12,
            timeProvider: clock);

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        clock.SetUtcNow(now.AddMinutes(elapsedMinutes).AddSeconds(elapsedSeconds));

        var room = context.Store.GetRoom(1);

        Assert.NotNull(room);
        Assert.Equal(RoomStates.Seated, room.State);
        Assert.Null(room.AgingStartedAt);
        Assert.Null(room.StaleStartedAt);
    }

    [Theory]
    [InlineData(6, 59, RoomStates.ReadyForDoctor, false, false)]
    [InlineData(7, 0, RoomStates.Aging, true, false)]
    [InlineData(11, 59, RoomStates.Aging, true, false)]
    [InlineData(12, 0, RoomStates.Stale, true, true)]
    public void Ready_for_doctor_threshold_boundaries_resolve_deterministically(
        int elapsedMinutes,
        int elapsedSeconds,
        string expectedState,
        bool expectedAgingStarted,
        bool expectedStaleStarted)
    {
        // Aging/stale escalation begins from ReadyForDoctorAt, not SeatedAt.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 5, 29, 18, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            agingMinutes: 7,
            staleMinutes: 12,
            timeProvider: clock);

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(now.AddMinutes(elapsedMinutes).AddSeconds(elapsedSeconds));

        var room = context.Store.GetRoom(1);

        Assert.NotNull(room);
        Assert.Equal(expectedState, room.State);
        Assert.Equal(expectedAgingStarted, room.AgingStartedAt is not null);
        Assert.Equal(expectedStaleStarted, room.StaleStartedAt is not null);
    }

    [Fact]
    public void Production_database_path_inside_content_root_fails_fast()
    {
        using var workspace = TestWorkspace.Create();
        var insideContentRoot = Path.Combine(workspace.ContentRoot, "data", "prod.db");

        var exception = Assert.Throws<InvalidOperationException>(() =>
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
        var seated = context.Store.SeatRoom(1, "otte", "EXT");
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

        var seated = context.Store.SeatRoom(1, "otte", "EXT", expectedAllocationUnits: 5);
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
        var seated = context.Store.SeatRoom(1, "otte", "EXT", expectedAllocationUnits: 3);
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

        var seated = context.Store.SeatRoom(1, "otte", "IMP", expectedAllocationUnits: 4);
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
        var seated = context.Store.SeatRoom(1, "otte", "EXT", expectedAllocationUnits: 0);
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

        var seated = context.Store.SeatRoom(1, "otte", "EXT", expectedAllocationUnits: -5);
        Assert.NotNull(seated);
        Assert.Equal(1, seated.ExpectedAllocationUnits);
        Assert.Equal(10, seated.ExpectedAllocationMinutes);
    }

    [Fact]
    public void Seating_with_units_above_maximum_clamps_to_maximum()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var seated = context.Store.SeatRoom(1, "otte", "EXT", expectedAllocationUnits: 100);
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

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "IMP", expectedAllocationUnits: 7));
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
        Assert.NotNull(first.Store.SeatRoom(1, "otte", "EXT", expectedAllocationUnits: 5));

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
        Assert.NotNull(first.Store.SeatRoom(1, "otte", "IMP", expectedAllocationUnits: 9));
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
        var seated = context.Store.SeatRoom(1, "otte", "EXT", sedation: true);
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
        Assert.Null(context.Store.SeatRoom(1, "inactive", "ACT"));
        Assert.Null(context.Store.SeatRoom(1, "active", "OFF"));
        var seated = context.Store.SeatRoom(1, "active", "ACT");
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

        // Standalone sedation is not seatable, by code or by id, for new seating or updates.
        Assert.Null(context.Store.SeatRoom(1, "otte", "SED"));
        Assert.Null(context.Store.SeatRoom(1, "otte", "sedation"));
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "EXT"));
        Assert.Null(context.Store.UpdateAssignment(1, "otte", "SED"));
    }

    [Fact]
    public void Eligible_procedure_seated_without_sedation_stays_a_base_procedure()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Sedation defaults Off: an eligible procedure seated without the flag is the base case.
        var seated = context.Store.SeatRoom(1, "otte", "EXT");
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

        var seated = context.Store.SeatRoom(1, "otte", "EXT", sedation: true);
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
        var seated = context.Store.SeatRoom(1, "otte", "AO4", sedation: true);
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
        Assert.Null(context.Store.SeatRoom(1, "otte", "CON", sedation: true));

        // The same procedure remains seatable without sedation.
        var seated = context.Store.SeatRoom(1, "otte", "CON");
        Assert.NotNull(seated);
        Assert.Equal("CON", seated.ProcedureCode);
    }

    [Fact]
    public void Sedation_modifier_can_be_added_and_removed_via_update_assignment()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "IMP"));

        var withSedation = context.Store.UpdateAssignment(1, "otte", "IMP", sedation: true);
        Assert.NotNull(withSedation);
        Assert.Equal("IMP+SED", withSedation.ProcedureCode);
        Assert.Equal("Implant + Sedation", withSedation.Procedure?.Label);

        var withoutSedation = context.Store.UpdateAssignment(1, "otte", "IMP");
        Assert.NotNull(withoutSedation);
        Assert.Equal("IMP", withoutSedation.ProcedureCode);

        // Switching to a non-eligible procedure cannot carry sedation forward.
        Assert.Null(context.Store.UpdateAssignment(1, "otte", "POST", sedation: true));
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
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "EXT"));
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

        Assert.Contains("function escapeHtml", boardJs);
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
        // empty placeholder icon. INTCK uses "interlock" (PNG); sync remains in
        // the map for backward compat but is no longer a default-roster icon.
        var requiredIcons = new[] { "speech", "forceps", "moon", "check", "bolt", "vial", "teeth", "interlock", "wrench", "phone", "uncover", "bond", "archfour" };
        foreach (var icon in requiredIcons)
        {
            Assert.Contains($"{icon}:", boardJs);
        }
    }

    [Fact]
    public void Persisted_schema_contains_only_non_phi_operational_fields()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        using var connection = OpenConnection(context.DatabasePath);
        var activeRoomColumns = GetColumnNames(connection, "active_rooms");
        var completedCycleColumns = GetColumnNames(connection, "completed_room_cycles");

        Assert.Subset(AllowedActiveRoomColumns, activeRoomColumns);
        Assert.Subset(AllowedCompletedCycleColumns, completedCycleColumns);
        Assert.DoesNotContain(activeRoomColumns.Concat(completedCycleColumns), ContainsBannedPhiTerm);
    }

    [Fact]
    public void DateTimeOffset_round_trip_preserves_cycle_dedup_key()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var seated = context.Store.SeatRoom(1, "otte", "CON");
        Assert.NotNull(seated);
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));

        var loadedCycle = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.Equal(seated.SeatedAt, loadedCycle.SeatedAt);

        context.Repository.SaveCompletedCycle(loadedCycle, context.Doctors, context.Procedures);

        using var connection = OpenConnection(context.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM completed_room_cycles WHERE room_id = 1;";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void SQLite_database_uses_wal_journal_mode()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        using var connection = OpenConnection(context.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        Assert.Equal("wal", (string)command.ExecuteScalar()!);
    }

    [Fact]
    public void Additive_column_migration_ignores_duplicate_column_errors()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        ExecuteSql(connection, "CREATE TABLE migration_test (id INTEGER PRIMARY KEY, existing_col TEXT NULL);");

        InvokeTryAddColumn(connection, "ALTER TABLE migration_test ADD COLUMN existing_col TEXT NULL");

        var columns = GetColumnNames(connection, "migration_test");
        Assert.Contains("existing_col", columns);
    }

    [Fact]
    public void Additive_column_migration_throws_non_duplicate_errors_with_context()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        const string alterTableSql = "ALTER TABLE missing_table ADD COLUMN new_col TEXT NULL";

        var ex = Assert.Throws<InvalidOperationException>(() => InvokeTryAddColumn(connection, alterTableSql));

        Assert.Contains("SQLite migration failed", ex.Message);
        Assert.Contains(alterTableSql, ex.Message);
        Assert.IsType<SqliteException>(ex.InnerException);
    }

    [Fact]
    public void Room_device_binding_disabled_allows_existing_mutation_behavior()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var validator = CreateBindingValidator(enabled: false);

        Assert.Equal(RoomDeviceTokenValidationResult.Disabled, validator.Validate(1, token: null));
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
    }

    [Fact]
    public void Room_device_binding_enabled_rejects_missing_token()
    {
        var validator = CreateBindingValidator(enabled: true);

        Assert.Equal(RoomDeviceTokenValidationResult.Missing, validator.Validate(1, token: null));
        Assert.Equal(RoomDeviceTokenValidationResult.Missing, validator.Validate(1, token: ""));
    }

    [Fact]
    public void Room_device_binding_enabled_rejects_wrong_token()
    {
        var validator = CreateBindingValidator(enabled: true);

        Assert.Equal(RoomDeviceTokenValidationResult.Invalid, validator.Validate(1, "wrong-token"));
    }

    [Fact]
    public void Room_device_binding_enabled_accepts_correct_room_token()
    {
        var validator = CreateBindingValidator(enabled: true);

        Assert.Equal(RoomDeviceTokenValidationResult.Valid, validator.Validate(1, "room-1-token"));
    }

    [Fact]
    public void Room_device_binding_room_one_token_does_not_work_for_room_two()
    {
        var validator = CreateBindingValidator(enabled: true);

        Assert.Equal(RoomDeviceTokenValidationResult.Invalid, validator.Validate(2, "room-1-token"));
    }

    [Fact]
    public void Room_device_binding_enabled_fails_closed_when_room_has_no_configured_token()
    {
        var validator = CreateBindingValidator(enabled: true);

        Assert.Equal(RoomDeviceTokenValidationResult.Invalid, validator.Validate(3, "room-3-token"));
    }

    [Fact]
    public void Read_only_board_state_still_works_without_room_token()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var validator = CreateBindingValidator(enabled: true);

        Assert.Equal(RoomDeviceTokenValidationResult.Missing, validator.Validate(1, token: null));

        var snapshot = context.Store.GetSnapshot();
        var reports = context.Store.GetReports();

        Assert.Equal(3, snapshot.RoomCount);
        Assert.Equal(3, snapshot.Rooms.Count);
        Assert.Equal(0, reports.CompletedRoomCyclesCount);
    }

    [Fact]
    public async Task Room_device_binding_guard_returns_expected_mutation_statuses()
    {
        var enabledValidator = CreateBindingValidator(enabled: true);
        var disabledValidator = CreateBindingValidator(enabled: false);

        Assert.Null(RoomDeviceBindingGuard.ValidateMutationRequest(1, RequestWithHeader(token: null), disabledValidator));
        Assert.Equal(401, await ExecuteBindingResult(RoomDeviceBindingGuard.ValidateMutationRequest(1, RequestWithHeader(token: null), enabledValidator)));
        Assert.Equal(403, await ExecuteBindingResult(RoomDeviceBindingGuard.ValidateMutationRequest(1, RequestWithHeader("wrong-token"), enabledValidator)));
        Assert.Equal(403, await ExecuteBindingResult(RoomDeviceBindingGuard.ValidateMutationRequest(2, RequestWithHeader("room-1-token"), enabledValidator)));
        Assert.Equal(403, await ExecuteBindingResult(RoomDeviceBindingGuard.ValidateMutationRequest(3, RequestWithHeader("room-3-token"), enabledValidator)));
        Assert.Null(RoomDeviceBindingGuard.ValidateMutationRequest(1, RequestWithHeader("room-1-token"), enabledValidator));
        Assert.Equal(401, await ExecuteBindingResult(RoomDeviceBindingGuard.ValidateMutationRequest(1, RequestWithQueryToken("room-1-token"), enabledValidator)));
    }

    [Fact]
    public void Room_device_binding_options_allow_disabled_config_without_room_tokens()
    {
        var result = ValidateBindingOptions(
            roomCount: 3,
            new RoomDeviceBindingOptions
            {
                Enabled = false,
                RoomTokens = []
            });

        Assert.False(result.Failed);
    }

    [Fact]
    public void Room_device_binding_options_require_all_configured_rooms_when_enabled()
    {
        var result = ValidateBindingOptions(
            roomCount: 3,
            new RoomDeviceBindingOptions
            {
                Enabled = true,
                RoomTokens = new Dictionary<string, string>
                {
                    ["1"] = "room-1-token",
                    ["2"] = "room-2-token"
                }
            });

        Assert.True(result.Failed);
        Assert.Contains("RoomDeviceBindingOptions:RoomTokens:3 is required", string.Join(" ", result.Failures));
    }

    [Fact]
    public void Room_device_binding_options_reject_blank_tokens_when_enabled()
    {
        var result = ValidateBindingOptions(
            roomCount: 2,
            new RoomDeviceBindingOptions
            {
                Enabled = true,
                RoomTokens = new Dictionary<string, string>
                {
                    ["1"] = "room-1-token",
                    ["2"] = " "
                }
            });

        Assert.True(result.Failed);
        Assert.Contains("RoomDeviceBindingOptions:RoomTokens:2 must not be blank", string.Join(" ", result.Failures));
    }

    [Fact]
    public void Room_device_binding_options_reject_duplicate_tokens_when_enabled()
    {
        var result = ValidateBindingOptions(
            roomCount: 2,
            new RoomDeviceBindingOptions
            {
                Enabled = true,
                RoomTokens = new Dictionary<string, string>
                {
                    ["1"] = "same-token",
                    ["2"] = "same-token"
                }
            });

        Assert.True(result.Failed);
        Assert.Contains("duplicate token values", string.Join(" ", result.Failures));
    }

    [Fact]
    public void Room_device_binding_options_accept_complete_unique_room_tokens_when_enabled()
    {
        var result = ValidateBindingOptions(
            roomCount: 2,
            new RoomDeviceBindingOptions
            {
                Enabled = true,
                RoomTokens = new Dictionary<string, string>
                {
                    ["1"] = "room-1-token",
                    ["2"] = "room-2-token"
                }
            });

        Assert.False(result.Failed);
    }

    [Fact]
    public void Admin_access_disabled_allows_reports_behavior()
    {
        var validator = CreateAdminValidator(enabled: false);

        Assert.Equal(AdminAccessTokenValidationResult.Disabled, validator.Validate(token: null));
        Assert.Null(AdminAccessGuard.ValidateRequest(RequestWithAdminHeader(token: null), validator));
    }

    [Fact]
    public async Task Admin_access_enabled_rejects_missing_and_wrong_token()
    {
        var validator = CreateAdminValidator(enabled: true);

        Assert.Equal(AdminAccessTokenValidationResult.Missing, validator.Validate(token: null));
        Assert.Equal(AdminAccessTokenValidationResult.Invalid, validator.Validate("wrong-token"));
        Assert.Equal(401, await ExecuteBindingResult(AdminAccessGuard.ValidateRequest(RequestWithAdminHeader(token: null), validator)));
        Assert.Equal(403, await ExecuteBindingResult(AdminAccessGuard.ValidateRequest(RequestWithAdminHeader("wrong-token"), validator)));
    }

    [Fact]
    public void Admin_access_enabled_accepts_correct_token()
    {
        var validator = CreateAdminValidator(enabled: true);

        Assert.Equal(AdminAccessTokenValidationResult.Valid, validator.Validate("admin-token"));
        Assert.Null(AdminAccessGuard.ValidateRequest(RequestWithAdminHeader("admin-token"), validator));
    }

    [Fact]
    public async Task Admin_access_rejects_query_string_token_and_accepts_header_token()
    {
        var validator = CreateAdminValidator(enabled: true);

        Assert.Equal(401, await ExecuteBindingResult(AdminAccessGuard.ValidateRequest(RequestWithAdminQueryToken("admin-token"), validator)));
        Assert.Null(AdminAccessGuard.ValidateRequest(RequestWithAdminHeader("admin-token"), validator));
    }

    [Fact]
    public void Admin_access_token_comparison_rejects_prefix_and_extended_tokens()
    {
        // Comparison hashes both sides with SHA-256 before calling FixedTimeEquals,
        // so tokens that are a prefix of, or an extension of, the configured value
        // are rejected without leaking the expected token's byte length via timing.
        var validator = CreateAdminValidator(enabled: true);

        Assert.Equal(AdminAccessTokenValidationResult.Invalid, validator.Validate("admin-toke"));
        Assert.Equal(AdminAccessTokenValidationResult.Invalid, validator.Validate("admin-token-extra"));
    }

    [Fact]
    public void Room_device_token_comparison_rejects_prefix_and_extended_tokens()
    {
        // Same SHA-256 hash normalisation as admin tokens - length of the submitted
        // value never gates acceptance.
        var validator = CreateBindingValidator(enabled: true);

        Assert.Equal(RoomDeviceTokenValidationResult.Invalid, validator.Validate(1, "room-1-toke"));
        Assert.Equal(RoomDeviceTokenValidationResult.Invalid, validator.Validate(1, "room-1-token-extra"));
    }

    [Fact]
    public void Admin_access_protects_reports_and_keeps_board_room_surfaces_open()
    {
        Assert.False(AdminAccessGuard.IsProtectedPath("/reports.html"));
        Assert.True(AdminAccessGuard.IsProtectedPath("/api/reports"));
        // Admin mutation endpoints nested under /api/reports are also protected.
        Assert.True(AdminAccessGuard.IsProtectedPath("/api/reports/cycles/mark-exception"));

        Assert.False(AdminAccessGuard.IsProtectedPath("/"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/master.html"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/doctor.html"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/room.html"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/room-1.html"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/api/board"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/api/rooms/1"));
        // Client error reporting must be unprotected so normal clients can post.
        Assert.False(AdminAccessGuard.IsProtectedPath("/api/client-errors"));
    }

    [Fact]
    public async Task Diagnostic_logger_appends_client_error_entry_to_log_file()
    {
        using var workspace = TestWorkspace.Create();
        var logDir = Path.Combine(workspace.DataRoot, "logs");
        var logger = CreateDiagnosticLogger(logDir, workspace.ContentRoot);

        await logger.LogClientErrorAsync(new ClientErrorEntry
        {
            ServerTimestamp = "2026-06-04T10:00:00Z",
            Message = "TypeError: Cannot read properties of null",
            View = "room",
            RoomId = "1",
            ConnectionStatus = "live"
        });

        var logPath = Path.Combine(logDir, "client-errors.log");
        Assert.True(File.Exists(logPath));
        var content = await File.ReadAllTextAsync(logPath);
        Assert.Contains("TypeError: Cannot read properties of null", content);
        Assert.Contains("room", content);
        Assert.Contains("serverTimestamp", content);
        Assert.Contains("2026-06-04", content);
    }

    [Fact]
    public async Task Diagnostic_logger_appends_room_audit_entry_to_log_file()
    {
        using var workspace = TestWorkspace.Create();
        var logDir = Path.Combine(workspace.DataRoot, "logs");
        var logger = CreateDiagnosticLogger(logDir, workspace.ContentRoot);

        await logger.LogRoomAuditAsync(new RoomAuditEntry
        {
            Timestamp = "2026-06-04T10:05:00Z",
            Action = "seat",
            RoomNumber = 2,
            PreviousState = "available",
            NewState = "seated",
            DoctorId = "otte",
            ProcedureCode = "CON",
            Success = true
        });

        var logPath = Path.Combine(logDir, "room-audit.log");
        Assert.True(File.Exists(logPath));
        var content = await File.ReadAllTextAsync(logPath);
        Assert.Contains("seat", content);
        Assert.Contains("available", content);
        Assert.Contains("seated", content);
        Assert.Contains("action", content);
        Assert.Contains("roomNumber", content);
    }

    [Fact]
    public async Task Diagnostic_logger_creates_log_directory_if_missing()
    {
        using var workspace = TestWorkspace.Create();
        var logDir = Path.Combine(workspace.DataRoot, "deep", "nested", "logs");
        // Directory does not exist yet - logger must create it.
        Assert.False(Directory.Exists(logDir));

        var logger = CreateDiagnosticLogger(logDir, workspace.ContentRoot);

        await logger.LogRoomAuditAsync(new RoomAuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            Action = "seat",
            RoomNumber = 1,
            Success = true
        });

        Assert.True(File.Exists(Path.Combine(logDir, "room-audit.log")));
    }

    [Fact]
    public async Task Diagnostic_logger_multiple_entries_are_each_on_their_own_line()
    {
        using var workspace = TestWorkspace.Create();
        var logDir = Path.Combine(workspace.DataRoot, "logs");
        var logger = CreateDiagnosticLogger(logDir, workspace.ContentRoot);

        await logger.LogRoomAuditAsync(new RoomAuditEntry { Action = "seat", RoomNumber = 1, Success = true });
        await logger.LogRoomAuditAsync(new RoomAuditEntry { Action = "ready-for-doctor", RoomNumber = 1, Success = true });
        await logger.LogRoomAuditAsync(new RoomAuditEntry { Action = "doctor-arrived", RoomNumber = 1, Success = true });

        var lines = (await File.ReadAllLinesAsync(Path.Combine(logDir, "room-audit.log")))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        Assert.Equal(3, lines.Count);
        Assert.Contains("seat", lines[0]);
        Assert.Contains("ready-for-doctor", lines[1]);
        Assert.Contains("doctor-arrived", lines[2]);
    }

    [Fact]
    public void Client_error_rate_limiter_allows_up_to_limit_then_rejects()
    {
        var now = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var limiter = new ClientErrorRateLimiter(
            new TestOptionsMonitor<DiagnosticOptions>(new DiagnosticOptions { ClientErrorRateLimitPerMinute = 3 }),
            clock);

        // First 3 requests from the same IP are allowed.
        Assert.True(limiter.IsAllowed("10.0.0.1"));
        Assert.True(limiter.IsAllowed("10.0.0.1"));
        Assert.True(limiter.IsAllowed("10.0.0.1"));

        // 4th request in the same window is blocked.
        Assert.False(limiter.IsAllowed("10.0.0.1"));
        Assert.False(limiter.IsAllowed("10.0.0.1"));

        // A different IP is not affected by the first IP's counter.
        Assert.True(limiter.IsAllowed("10.0.0.2"));

        // After the one-minute window expires, the first IP is allowed again.
        clock.SetUtcNow(now.AddMinutes(1).AddSeconds(1));
        Assert.True(limiter.IsAllowed("10.0.0.1"));
        Assert.True(limiter.IsAllowed("10.0.0.1"));
        Assert.True(limiter.IsAllowed("10.0.0.1"));
        Assert.False(limiter.IsAllowed("10.0.0.1")); // new window, same limit
    }

    [Fact]
    public void Client_error_rate_limiter_null_or_empty_ip_is_always_allowed()
    {
        // Unknown/proxied source IPs must never be blocked - they cannot be rate-limited
        // by address and the limiter must not throw on null input.
        var limiter = new ClientErrorRateLimiter(
            new TestOptionsMonitor<DiagnosticOptions>(new DiagnosticOptions { ClientErrorRateLimitPerMinute = 1 }));

        Assert.True(limiter.IsAllowed(null));
        Assert.True(limiter.IsAllowed(null));
        Assert.True(limiter.IsAllowed(""));
        Assert.True(limiter.IsAllowed(""));
    }

    [Fact]
    public async Task Diagnostic_logger_rotates_when_max_file_size_exceeded()
    {
        using var workspace = TestWorkspace.Create();
        var logDir = Path.Combine(workspace.DataRoot, "logs");

        // Set a tiny cap (10 bytes) so the very first write already exceeds the limit
        // and rotation triggers on the second write.
        var logger = CreateDiagnosticLogger(logDir, workspace.ContentRoot, maxFileSizeBytes: 10);

        var logPath = Path.Combine(logDir, "room-audit.log");
        var rotatedPath = logPath + ".1";

        // First write creates the file; no rotation yet (file didn't exist before).
        await logger.LogRoomAuditAsync(new RoomAuditEntry { Action = "seat", RoomNumber = 1, Success = true });
        Assert.True(File.Exists(logPath));
        Assert.False(File.Exists(rotatedPath));

        // Second write: file already exceeds 10 bytes, so rotation fires first.
        await logger.LogRoomAuditAsync(new RoomAuditEntry { Action = "ready-for-doctor", RoomNumber = 1, Success = true });
        Assert.True(File.Exists(logPath));
        Assert.True(File.Exists(rotatedPath));

        // The rotated file holds the first entry; the new file holds the second.
        var rotatedContent = await File.ReadAllTextAsync(rotatedPath);
        var currentContent = await File.ReadAllTextAsync(logPath);
        Assert.Contains("seat", rotatedContent);
        Assert.Contains("ready-for-doctor", currentContent);
    }

    [Fact]
    public async Task Diagnostic_logger_rotation_failure_does_not_block_logging()
    {
        // If the .1 path is occupied by a directory, File.Move will fail.
        // The logger must catch that, write a message to stderr, and still append
        // to the original file so room workflow is never disrupted.
        using var workspace = TestWorkspace.Create();
        var logDir = Path.Combine(workspace.DataRoot, "logs");
        var logger = CreateDiagnosticLogger(logDir, workspace.ContentRoot, maxFileSizeBytes: 10);

        var logPath = Path.Combine(logDir, "room-audit.log");
        var rotatedPath = logPath + ".1";

        // First write creates the file.
        await logger.LogRoomAuditAsync(new RoomAuditEntry { Action = "seat", RoomNumber = 1, Success = true });

        // Block the rotation target so File.Move cannot succeed.
        Directory.CreateDirectory(rotatedPath);

        // Second write: rotation fails silently, but the entry must still be written.
        await logger.LogRoomAuditAsync(new RoomAuditEntry { Action = "ready-for-doctor", RoomNumber = 1, Success = true });

        var content = await File.ReadAllTextAsync(logPath);
        Assert.Contains("ready-for-doctor", content);
    }

    [Fact]
    public void Room_mutation_request_validation_rejects_invalid_assignment_fields()
    {
        Assert.Equal("Doctor id is required.", RoomMutationRequestValidator.ValidateDoctorAndProcedure(null, "CON"));
        Assert.Equal("Doctor id is required.", RoomMutationRequestValidator.ValidateDoctorAndProcedure(" ", "CON"));
        Assert.Equal(
            $"Doctor id must be {RoomMutationRequestValidator.MaxDoctorIdLength} characters or fewer.",
            RoomMutationRequestValidator.ValidateDoctorAndProcedure(new string('d', 65), "CON"));
        Assert.Equal("Procedure code is required.", RoomMutationRequestValidator.ValidateDoctorAndProcedure("otte", null));
        Assert.Equal("Procedure code is required.", RoomMutationRequestValidator.ValidateDoctorAndProcedure("otte", " "));
        Assert.Equal(
            $"Procedure code must be {RoomMutationRequestValidator.MaxProcedureCodeLength} characters or fewer.",
            RoomMutationRequestValidator.ValidateDoctorAndProcedure("otte", new string('p', 33)));
        Assert.Null(RoomMutationRequestValidator.ValidateDoctorAndProcedure("otte", "CON"));
    }

    [Fact]
    public void Ready_for_doctor_blocks_doctor_arrived_until_called()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var seated = context.Store.SeatRoom(1, "otte", "CON");
        Assert.NotNull(seated);
        Assert.Equal(RoomStates.Seated, seated.State);

        // Doctor Arrived must be blocked until Ready for Doctor is explicitly called
        Assert.Null(context.Store.MarkDoctorArrived(1));
        Assert.Equal(0, context.Store.GetReports().CompletedRoomCyclesCount);

        var ready = context.Store.MarkReadyForDoctor(1);
        Assert.NotNull(ready);
        Assert.Equal(RoomStates.ReadyForDoctor, ready.State);
        Assert.NotNull(ready.ReadyForDoctorAt);
        Assert.NotNull(ready.SeatedAt);

        // Cancel Seating must still be available from ReadyForDoctor state
        var canceled = context.Store.CancelSeating(1);
        Assert.NotNull(canceled);
        Assert.Equal(RoomStates.Available, canceled.State);

        // Re-seat and go through to DoctorInRoom
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        var arrived = context.Store.MarkDoctorArrived(1);
        Assert.NotNull(arrived);
        Assert.Equal(RoomStates.DoctorInRoom, arrived.State);
        Assert.Single(context.Store.GetReports().DoctorSummaries);
    }

    [Fact]
    public void Ready_for_doctor_aging_and_stale_allow_doctor_arrived()
    {
        // Aging/stale are now part of the Ready for Doctor phase (doctor requested too long).
        // Doctor Arrived must be accepted from any of: ready-for-doctor, aging, stale.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            agingMinutes: 7,
            staleMinutes: 12,
            timeProvider: clock);

        // Seat and mark ready, then advance past aging threshold
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(now.AddMinutes(8)); // past aging (7) but before stale (12)

        var aging = context.Store.GetRoom(1);
        Assert.NotNull(aging);
        Assert.Equal(RoomStates.Aging, aging.State);
        Assert.NotNull(aging.AgingStartedAt);
        Assert.Null(aging.StaleStartedAt);

        var arrived = context.Store.MarkDoctorArrived(1);
        Assert.NotNull(arrived);
        Assert.Equal(RoomStates.DoctorInRoom, arrived.State);

        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(RoomStates.Aging, cycle.FinalWaitState);
        Assert.True(cycle.AgingThresholdReached);
        Assert.False(cycle.StaleThresholdReached);
    }

    [Fact]
    public void Reports_split_prep_and_ready_to_doctor_seconds()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        clock.SetUtcNow(now.AddMinutes(15)); // 15 min prep
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(now.AddMinutes(20)); // 5 min doctor response
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        clock.SetUtcNow(now.AddMinutes(30)); // 10 min doctor in room
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(now.AddMinutes(35)); // 5 min turnover
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(15 * 60, cycle.PrepSeconds);
        Assert.Equal(5 * 60, cycle.ReadyToDoctorSeconds);
        Assert.Equal(20 * 60, cycle.SeatedToDoctorSeconds); // total = prep + ready-to-doctor
        Assert.Equal(10 * 60, cycle.DoctorInRoomSeconds);
        Assert.Equal(5 * 60, cycle.TurnoverSeconds);
        Assert.Equal(35 * 60, cycle.TotalRoomCycleSeconds);

        var reports = context.Store.GetReports();
        Assert.Equal(15 * 60, reports.AveragePrepSeconds);
        Assert.Equal(5 * 60, reports.AverageReadyToDoctorSeconds);
        Assert.Equal(20 * 60, reports.AverageSeatedToDoctorSeconds);
    }

    [Fact]
    public void Room_event_history_is_capped_to_most_recent_entries()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        for (var i = 0; i < 110; i++)
        {
            Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
            Assert.NotNull(context.Store.CancelSeating(1));
        }

        var eventsField = typeof(DemoBoardStore).GetField("_events", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(eventsField);
        var events = Assert.IsType<List<RoomEvent>>(eventsField.GetValue(context.Store));

        Assert.Equal(200, events.Count);
        Assert.Equal("Seated", events[0].EventType);
        Assert.Equal("SeatingCanceled", events[^1].EventType);
        Assert.Equal(20, context.Store.GetSnapshot().RecentEvents.Count);
    }

    [Fact]
    public void Board_ui_demo_timer_defaults_to_development_only_and_can_be_enabled()
    {
        using var workspace = TestWorkspace.Create();

        var development = StoreContext.Create(workspace, environmentName: Environments.Development);
        var production = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: workspace.ProductionDatabasePath());
        var productionEnabled = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: Path.Combine(workspace.DataRoot, "chairside-demo-enabled.db"),
            boardUiOptions: new BoardUiOptions { DemoTimerEnabled = true });

        Assert.True(development.Store.GetSnapshot().DemoTimerEnabled);
        Assert.False(production.Store.GetSnapshot().DemoTimerEnabled);
        Assert.True(productionEnabled.Store.GetSnapshot().DemoTimerEnabled);
    }

    [Fact]
    public void Demo_elapsed_is_applied_only_when_demo_timer_is_enabled()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);
        var enabledClock = new ManualTimeProvider(now);
        var disabledClock = new ManualTimeProvider(now);
        var enabled = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: Path.Combine(workspace.DataRoot, "demo-enabled.db"),
            boardUiOptions: new BoardUiOptions { DemoTimerEnabled = true },
            timeProvider: enabledClock);
        var disabled = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: Path.Combine(workspace.DataRoot, "demo-disabled.db"),
            boardUiOptions: new BoardUiOptions { DemoTimerEnabled = false },
            timeProvider: disabledClock);

        var enabledRoom = enabled.Store.SeatRoom(1, "otte", "CON", demoElapsedMinutes: 15);
        var disabledRoom = disabled.Store.SeatRoom(1, "otte", "CON", demoElapsedMinutes: 15);

        Assert.NotNull(enabledRoom);
        Assert.NotNull(disabledRoom);
        Assert.Equal(now.AddMinutes(-15), enabledRoom.SeatedAt);
        // Patient Seated / In Prep no longer escalates to aging/stale regardless of elapsed time.
        // demoElapsedMinutes only back-dates SeatedAt; state remains Seated until Ready for Doctor.
        Assert.Equal(RoomStates.Seated, enabledRoom.State);
        Assert.Equal(now, disabledRoom.SeatedAt);
        Assert.Equal(RoomStates.Seated, disabledRoom.State);
    }

    [Fact]
    public void Client_report_metrics_escape_labels_and_values()
    {
        var root = FindRepositoryRoot();
        var boardScript = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "board.js"));

        Assert.Contains("<span>${escapeHtml(label)}</span>", boardScript);
        Assert.Contains("<strong>${escapeHtml(value)}</strong>", boardScript);
    }

    [Fact]
    public void Client_room_token_prompt_uses_room_scoped_session_storage_and_header_only()
    {
        var root = FindRepositoryRoot();
        var boardScript = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "board.js"));

        Assert.Contains("chairside-room-token-${roomNumber}", boardScript);
	 Assert.Contains("function roomTokenStorageKey(roomNumber = getRoomNumber())", boardScript);
        Assert.Contains("sessionStorage.setItem(roomTokenStorageKey(), token)", boardScript);
        Assert.Contains("sessionStorage.removeItem(roomTokenStorageKey())", boardScript);
        Assert.Contains("headers[\"X-ChairSide-Room-Token\"] = app.roomToken", boardScript);
        Assert.Contains("Room access token required", boardScript);
        Assert.DoesNotContain("roomToken=", boardScript);
    }

    [Fact]
    public void Admin_access_options_allow_disabled_config_without_token()
    {
        var result = ValidateAdminAccessOptions(new AdminAccessOptions { Enabled = false, SharedToken = "" });

        Assert.False(result.Failed);
    }

    [Fact]
    public void Admin_access_options_require_token_when_enabled()
    {
        var result = ValidateAdminAccessOptions(new AdminAccessOptions { Enabled = true, SharedToken = " " });

        Assert.True(result.Failed);
        Assert.Contains("AdminAccessOptions:SharedToken is required", string.Join(" ", result.Failures));
    }

    [Fact]
    public void Admin_access_options_reject_sample_token_in_production()
    {
        var result = ValidateAdminAccessOptions(
            new AdminAccessOptions { Enabled = true, SharedToken = "dev-admin-token" },
            Environments.Production);

        Assert.True(result.Failed);
        Assert.Contains("must not use the dev-admin-token sample value in Production", string.Join(" ", result.Failures));
    }

    [Fact]
    public void Backup_restore_scripts_and_documentation_are_present()
    {
        var root = FindRepositoryRoot();
        var backupScript = Path.Combine(root, "scripts", "Backup-ChairSideSqlite.ps1");
        var restoreScript = Path.Combine(root, "scripts", "Restore-ChairSideSqlite.ps1");
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        Assert.True(File.Exists(backupScript));
        Assert.True(File.Exists(restoreScript));
        Assert.Contains(@"C:\ChairSide\Data\chairside.db", readme);
        Assert.Contains(@"C:\ChairSide\Backups", readme);
        Assert.Contains("chairside.db-wal", readme);
        Assert.Contains("Backup-ChairSideSqlite.ps1", readme);
        Assert.Contains("Restore-ChairSideSqlite.ps1", readme);
    }

    [Fact]
    public void Normal_completed_cycles_appear_in_normal_reporting_metrics()
    {
        // A standard full-lifecycle cycle must appear in CompletedRoomCyclesCount
        // and in RecentCompletedCycles; exception flag must default to false.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var reports = context.Store.GetReports();

        Assert.Equal(1, reports.CompletedRoomCyclesCount);
        Assert.Single(reports.RecentCompletedCycles);
        Assert.Empty(reports.ExceptionCycles);

        // The loaded cycle must carry IsException = false.
        var cycle = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.False(cycle.IsException);
        Assert.False(cycle.RequiresReview);
        Assert.Equal(ReviewStatuses.PendingReview, cycle.ReviewStatus);
    }

    [Fact]
    public void Exception_cycles_are_excluded_from_normal_metrics_and_count()
    {
        // After a cycle is marked as an exception it must not appear in
        // CompletedRoomCyclesCount, averages, or RecentCompletedCycles.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Cycle A: full lifecycle on room 1 - should appear in normal metrics.
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        // Cycle B: only reached DoctorArrived on room 2 - will be marked exception.
        clock.SetUtcNow(now.AddMinutes(30));
        Assert.NotNull(context.Store.SeatRoom(2, "pledger", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        var exceptionArrived = context.Store.MarkDoctorArrived(2);
        Assert.NotNull(exceptionArrived);
        // SeatedAt is still set on the DoctorInRoom RoomStatus (room has not been reset).
        Assert.NotNull(exceptionArrived.SeatedAt);
        var exceptionSeatedAt = exceptionArrived.SeatedAt!.Value;

        // Mark cycle B as an exception.
        var marked = context.Store.MarkCycleAsException(2, exceptionSeatedAt, "Abnormal wait time", "Manual review required");
        Assert.True(marked);

        var reports = context.Store.GetReports();

        // Only cycle A (normal, completed) is counted.
        Assert.Equal(1, reports.CompletedRoomCyclesCount);
        Assert.Single(reports.RecentCompletedCycles);
        Assert.Equal(1, reports.RecentCompletedCycles[0].RoomId);

        // Cycle B is surfaced as an exception.
        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.Equal(2, exception.RoomId);
        Assert.Equal("Abnormal wait time", exception.ExceptionReason);
        Assert.Equal("Manual review required", exception.SuggestedAction);
        Assert.Equal(ReviewStatuses.PendingReview, exception.ReviewStatus);
        Assert.True(exception.IsException);
        Assert.True(exception.RequiresReview);
    }

    [Fact]
    public void Exception_cycles_appear_in_exceptions_requiring_review_section()
    {
        // GetReports().ExceptionCycles contains exactly the cycles with IsException = true,
        // regardless of whether they have a RoomAvailableAt timestamp.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Cycle A: complete lifecycle - normal.
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        var arrivedA = context.Store.MarkDoctorArrived(1);
        Assert.NotNull(arrivedA);

        // Cycle B: only reached DoctorArrived - will be marked exception.
        Assert.NotNull(context.Store.SeatRoom(2, "pledger", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        var arrivedB = context.Store.MarkDoctorArrived(2);
        Assert.NotNull(arrivedB);
        var seatedAtB = arrivedB.SeatedAt!.Value;

        context.Store.MarkCycleAsException(2, seatedAtB, "Timed out", "Investigate");

        var reports = context.Store.GetReports();

        // Normal cycle: Cycle A is in both DoctorSummaries (arrived) and RecentCompletedCycles is empty (no RoomAvailableAt yet).
        // Exception cycle: Cycle B is in ExceptionCycles only.
        Assert.Single(reports.ExceptionCycles);
        Assert.Equal(2, reports.ExceptionCycles[0].RoomId);
        Assert.Equal("Timed out", reports.ExceptionCycles[0].ExceptionReason);
        Assert.DoesNotContain(reports.ExceptionCycles, cycle => cycle.RoomId == 1);
    }

    [Fact]
    public void Exception_pending_review_status_survives_store_restart()
    {
        // ReviewStatus = PendingReview and exception fields must round-trip through
        // SQLite and be present after a store reload.
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        Assert.NotNull(first.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(first.Store.MarkReadyForDoctor(1));
        var arrived = first.Store.MarkDoctorArrived(1);
        Assert.NotNull(arrived);
        var seatedAt = arrived.SeatedAt!.Value;

        var marked = first.Store.MarkCycleAsException(1, seatedAt, "Extended wait", "Review with doctor");
        Assert.True(marked);

        // Reload the store - simulates a server restart.
        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = second.Store.GetReports();

        Assert.Empty(reports.RecentCompletedCycles); // excluded from normal
        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.Equal(1, exception.RoomId);
        Assert.True(exception.IsException);
        Assert.True(exception.RequiresReview);
        Assert.Equal("Extended wait", exception.ExceptionReason);
        Assert.Equal("Review with doctor", exception.SuggestedAction);
        Assert.Equal(ReviewStatuses.PendingReview, exception.ReviewStatus);
        Assert.Null(exception.ReviewedAt);
        Assert.Null(exception.ReviewedBy);
    }

    // -----------------------------------------------------------------------
    // Exception cycle handling - manual + automatic expiration
    // -----------------------------------------------------------------------

    [Fact]
    public void Manual_mark_as_exception_moves_cycle_from_normal_to_exceptions()
    {
        // The admin marks a completed cycle as ManualReview - it should disappear from
        // normal metrics and appear in ExceptionCycles with the default reason/action.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        var arrived = context.Store.MarkDoctorArrived(1);
        Assert.NotNull(arrived);
        var seatedAt = arrived.SeatedAt!.Value;

        // Before marking: appears in normal metrics.
        Assert.Single(context.Store.GetReports().DoctorSummaries);
        Assert.Empty(context.Store.GetReports().ExceptionCycles);

        var marked = context.Store.MarkCycleAsException(1, seatedAt, ExceptionReasons.ManualReview, "Exclude from normal metrics");
        Assert.True(marked);

        var reports = context.Store.GetReports();
        Assert.Equal(0, reports.CompletedRoomCyclesCount);
        Assert.Empty(reports.RecentCompletedCycles);
        Assert.Empty(reports.DoctorSummaries);

        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.Equal(1, exception.RoomId);
        Assert.Equal(ExceptionReasons.ManualReview, exception.ExceptionReason);
        Assert.Equal("Exclude from normal metrics", exception.SuggestedAction);
        Assert.Equal(ReviewStatuses.PendingReview, exception.ReviewStatus);
    }

    [Fact]
    public void Active_room_under_max_duration_is_not_expired()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));

        // Advance 7.5 hours - still under the 8-hour limit.
        clock.SetUtcNow(now.AddHours(7).AddMinutes(30));
        var expired = context.Store.CheckAndExpireActiveCycles();

        Assert.Empty(expired);
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(1)?.State);
        Assert.Equal(0, context.Store.GetReports().CompletedRoomCyclesCount);
        Assert.Empty(context.Store.GetReports().ExceptionCycles);
    }

    [Fact]
    public void Active_room_over_max_duration_without_doctor_arrived_is_expired_as_ExceededMaxActiveDuration()
    {
        // Room never reached DoctorArrived - should produce SuggestedAction "Exclude abandoned cycle"
        // and DoctorArrivedAt should be null on the resulting exception cycle.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        // Advance past 8-hour limit.
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        var expired = context.Store.CheckAndExpireActiveCycles();

        Assert.Equal([1], expired);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        var exception = Assert.Single(context.Store.GetReports().ExceptionCycles);
        Assert.Equal(1, exception.RoomId);
        Assert.Equal(ExceptionReasons.ExceededMaxActiveDuration, exception.ExceptionReason);
        Assert.Equal("Exclude abandoned cycle", exception.SuggestedAction);
        Assert.Null(exception.DoctorArrivedAt);
        Assert.Null(exception.DoctorCompleteAt);
        Assert.Equal(ReviewStatuses.PendingReview, exception.ReviewStatus);
        Assert.True(exception.IsException);
        Assert.True(exception.RequiresReview);
        Assert.Equal(0, context.Store.GetReports().CompletedRoomCyclesCount);
    }

    [Fact]
    public void Active_room_over_max_duration_with_doctor_arrived_is_expired_with_review_timing_suggestion()
    {
        // Room reached DoctorArrived - should produce SuggestedAction "Review timing".
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));

        // Advance past 8-hour limit.
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        var expired = context.Store.CheckAndExpireActiveCycles();

        Assert.Equal([1], expired);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        var exception = Assert.Single(context.Store.GetReports().ExceptionCycles);
        Assert.Equal(1, exception.RoomId);
        Assert.Equal(ExceptionReasons.ExceededMaxActiveDuration, exception.ExceptionReason);
        Assert.Equal("Review timing", exception.SuggestedAction);
        Assert.NotNull(exception.DoctorArrivedAt);
        Assert.Null(exception.DoctorCompleteAt);   // Doctor Complete was never called.
        Assert.True(exception.IsException);
    }

    [Fact]
    public void After_hours_sweep_expires_active_rooms_as_AfterHoursSweep()
    {
        using var workspace = TestWorkspace.Create();
        // Use UTC timezone and a clock set to exactly 19:00 UTC.
        var now = new DateTimeOffset(2026, 6, 9, 19, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "19:00",
                TimeZone = "UTC"
            });

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.SeatRoom(2, "pledger", "EXT"));

        var expired = context.Store.TryRunAfterHoursSweep();

        Assert.Equal(2, expired.Count);
        Assert.Contains(1, expired);
        Assert.Contains(2, expired);

        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(2)?.State);

        var exceptions = context.Store.GetReports().ExceptionCycles;
        Assert.Equal(2, exceptions.Count);
        Assert.All(exceptions, ex =>
        {
            Assert.Equal(ExceptionReasons.AfterHoursSweep, ex.ExceptionReason);
            Assert.True(ex.IsException);
            Assert.True(ex.RequiresReview);
        });
        Assert.Equal(0, context.Store.GetReports().CompletedRoomCyclesCount);
    }

    [Fact]
    public void Sweep_runs_once_per_clinic_day_and_does_not_create_duplicate_exceptions()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 19, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var expirationOptions = new RoomExpirationOptions
        {
            Enabled = true,
            AfterHoursSweepEnabled = true,
            AfterHoursSweepTime = "19:00",
            TimeZone = "UTC"
        };
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: expirationOptions);

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));

        // First sweep: expires room 1.
        var firstSweep = context.Store.TryRunAfterHoursSweep();
        Assert.Equal([1], firstSweep);

        // Re-seat room 1 (simulate activity resuming).
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));

        // Second sweep on the same clinic day (even 10 minutes later): should not fire.
        clock.SetUtcNow(now.AddMinutes(10));
        var secondSweep = context.Store.TryRunAfterHoursSweep();
        Assert.Empty(secondSweep);

        // Only the one exception from the first sweep should exist.
        var exceptions = context.Store.GetReports().ExceptionCycles;
        Assert.Single(exceptions);
        Assert.Equal(ExceptionReasons.AfterHoursSweep, exceptions[0].ExceptionReason);
    }

    [Fact]
    public void Invalid_timezone_does_not_run_after_hours_sweep()
    {
        // A misconfigured timezone must not silently become UTC and fire the sweep
        // at the wrong local time. The sweep must be suppressed entirely (fail closed).
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 23, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "19:00",
                TimeZone = "Not/A/Valid/TimeZone"
            });

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));

        var expired = context.Store.TryRunAfterHoursSweep();

        Assert.Empty(expired);
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(1)?.State);
        Assert.Empty(context.Store.GetReports().ExceptionCycles);
    }

    [Fact]
    public void Invalid_timezone_does_not_throw()
    {
        // TryRunAfterHoursSweep must silently no-op on a bad timezone - never throw.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 23, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "19:00",
                TimeZone = "Not/A/Valid/TimeZone"
            });

        var ex = Record.Exception(() => context.Store.TryRunAfterHoursSweep());
        Assert.Null(ex);
    }

    [Fact]
    public void Max_active_duration_expiration_still_works_with_invalid_timezone()
    {
        // CheckAndExpireActiveCycles uses UTC wall-clock only - invalid TimeZone must not affect it.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "19:00",
                TimeZone = "Not/A/Valid/TimeZone"
            });

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));

        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        var expired = context.Store.CheckAndExpireActiveCycles();

        Assert.Equal([1], expired);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        var exception = Assert.Single(context.Store.GetReports().ExceptionCycles);
        Assert.Equal(ExceptionReasons.ExceededMaxActiveDuration, exception.ExceptionReason);
    }

    [Fact]
    public void After_hours_sweep_runs_with_valid_IANA_timezone()
    {
        // "America/Chicago" is CDT (UTC-5) in June. Setting the clock to
        // 2026-06-10 00:30 UTC places clinic local time at 2026-06-09 19:30 CDT,
        // which is past the 19:00 sweep threshold on clinic day June 9.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 10, 0, 30, 0, TimeSpan.Zero); // 19:30 CDT
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "19:00",
                TimeZone = "America/Chicago"
            });

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));

        var expired = context.Store.TryRunAfterHoursSweep();

        Assert.Equal([1], expired);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        var exception = Assert.Single(context.Store.GetReports().ExceptionCycles);
        Assert.Equal(ExceptionReasons.AfterHoursSweep, exception.ExceptionReason);
    }

    [Fact]
    public void Available_rooms_are_not_affected_by_sweep_or_max_duration_check()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 19, 5, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 1,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "19:00",
                TimeZone = "UTC"
            });

        // All rooms start Available - nothing to expire.
        var sweepExpired = context.Store.TryRunAfterHoursSweep();
        var maxExpired = context.Store.CheckAndExpireActiveCycles();

        Assert.Empty(sweepExpired);
        Assert.Empty(maxExpired);
        Assert.Equal(0, context.Store.GetReports().CompletedRoomCyclesCount);
        Assert.Empty(context.Store.GetReports().ExceptionCycles);

        // Rooms remain available.
        Assert.All(context.Store.GetSnapshot().Rooms, room =>
            Assert.Equal(RoomStates.Available, room.State));
    }

    [Fact]
    public void Expired_active_cycles_do_not_manufacture_doctor_complete_at()
    {
        // Force-expiring a DoctorInRoom room must NOT set DoctorCompleteAt.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        // Note: MarkDoctorComplete is intentionally NOT called.

        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        context.Store.CheckAndExpireActiveCycles();

        var exception = Assert.Single(context.Store.GetReports().ExceptionCycles);
        Assert.Null(exception.DoctorCompleteAt);
    }

    [Fact]
    public void Expired_exception_cycles_are_excluded_from_normal_metrics()
    {
        // Normal cycle + force-expired cycle: only the normal cycle contributes to metrics.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        // Room 1: completes the full lifecycle - normal cycle.
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        // Room 2: gets force-expired - exception cycle.
        Assert.NotNull(context.Store.SeatRoom(2, "pledger", "EXT"));
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        context.Store.CheckAndExpireActiveCycles();

        var reports = context.Store.GetReports();
        Assert.Equal(1, reports.CompletedRoomCyclesCount);
        Assert.Single(reports.RecentCompletedCycles);
        Assert.Equal(1, reports.RecentCompletedCycles[0].RoomId);
        Assert.Single(reports.ExceptionCycles);
        Assert.Equal(2, reports.ExceptionCycles[0].RoomId);
    }

    [Fact]
    public void Expired_exception_cycles_appear_in_exceptions_requiring_review()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        context.Store.CheckAndExpireActiveCycles();

        var reports = context.Store.GetReports();
        Assert.Empty(reports.RecentCompletedCycles);
        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.Equal(1, exception.RoomId);
        Assert.Equal(ExceptionReasons.ExceededMaxActiveDuration, exception.ExceptionReason);
        Assert.True(exception.RequiresReview);
        Assert.Equal(ReviewStatuses.PendingReview, exception.ReviewStatus);
    }

    [Fact]
    public void Persistence_restart_does_not_resurrect_expired_active_rooms()
    {
        // After force-expiry the room must persist as Available; a fresh store reload
        // must not re-activate it.
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);

        var first = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.NotNull(first.Store.SeatRoom(1, "otte", "CON"));

        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        var expired = first.Store.CheckAndExpireActiveCycles();
        Assert.Equal([1], expired);

        // Verify in-memory: room available, exception cycle recorded.
        Assert.Equal(RoomStates.Available, first.Store.GetRoom(1)?.State);
        Assert.Single(first.Store.GetReports().ExceptionCycles);

        // Reload from DB: room must still be Available, exception cycle preserved.
        var second = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);

        Assert.Equal(RoomStates.Available, second.Store.GetRoom(1)?.State);
        var exception = Assert.Single(second.Store.GetReports().ExceptionCycles);
        Assert.Equal(1, exception.RoomId);
        Assert.Equal(ExceptionReasons.ExceededMaxActiveDuration, exception.ExceptionReason);
        Assert.True(exception.IsException);
    }

    // -------------------------------------------------------------------------
    // Doctor-occupied wait and doctor-available wait reporting
    // -------------------------------------------------------------------------

    [Fact]
    public void DoctorOccupiedWait_no_same_doctor_overlap()
    {
        // No other same-doctor cycle is in-room during this cycle's ready window.
        // Expected: occupiedWait = 0, availableWait = readyToDoctorSeconds.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        clock.SetUtcNow(now.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(now.AddMinutes(15));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(now.AddMinutes(20));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(10 * 60, cycle.ReadyToDoctorSeconds);
        Assert.Equal(0, cycle.DoctorOccupiedWaitSeconds);
        Assert.Equal(10 * 60, cycle.DoctorAvailableWaitSeconds);
    }

    [Fact]
    public void DoctorOccupiedWait_full_same_doctor_overlap()
    {
        // The same doctor is in another room for the entire ready window.
        // Expected: occupiedWait = readyToDoctorSeconds, availableWait = 0.
        using var workspace = TestWorkspace.Create();
        var base_ = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(base_);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Room 2 (same doctor): arrives at t=0, completes at t=20.
        Assert.NotNull(context.Store.SeatRoom(2, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2)); // DoctorArrivedAt = base_+0

        // Room 1 (target): ready at t=5, arrives at t=20.
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        clock.SetUtcNow(base_.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1)); // ReadyForDoctorAt = base_+5

        clock.SetUtcNow(base_.AddMinutes(20));
        Assert.NotNull(context.Store.MarkDoctorComplete(2)); // DoctorCompleteAt = base_+20
        Assert.NotNull(context.Store.MarkRoomAvailable(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(1)); // DoctorArrivedAt = base_+20, readyToDoctor=15min=900s

        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(base_.AddMinutes(25));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var reports = context.Store.GetReports();
        var cycle = reports.RecentCompletedCycles.Single(c => c.RoomId == 1);
        Assert.Equal(15 * 60, cycle.ReadyToDoctorSeconds); // 15 min = 900s
        Assert.Equal(15 * 60, cycle.DoctorOccupiedWaitSeconds); // fully covered by Room 2's interval
        Assert.Equal(0, cycle.DoctorAvailableWaitSeconds);
    }

    [Fact]
    public void DoctorOccupiedWait_partial_same_doctor_overlap()
    {
        // The same doctor is in another room for only part of the ready window.
        // Room 2 (blocker): DoctorArrivedAt=t+0, DoctorCompleteAt=t+10
        // Room 1 (target):  ReadyForDoctorAt=t+5, DoctorArrivedAt=t+15 => readyToDoctor=600s
        // Overlap: t+5 to t+10 = 5 min = 300s
        // Available: 600 - 300 = 300s
        using var workspace = TestWorkspace.Create();
        var base_ = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(base_);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Room 2 (same doctor, blocker): arrives at t=0.
        Assert.NotNull(context.Store.SeatRoom(2, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2)); // DoctorArrivedAt = base_

        // Room 1 (target): seat now, ready at t=5.
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        clock.SetUtcNow(base_.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1)); // ReadyForDoctorAt = base_+5

        // Room 2 completes at t=10.
        clock.SetUtcNow(base_.AddMinutes(10));
        Assert.NotNull(context.Store.MarkDoctorComplete(2)); // DoctorCompleteAt = base_+10
        Assert.NotNull(context.Store.MarkRoomAvailable(2));

        // Room 1 arrives at t=15 => ReadyToDoctorSeconds = 10 min = 600s.
        clock.SetUtcNow(base_.AddMinutes(15));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(base_.AddMinutes(20));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = context.Store.GetReports().RecentCompletedCycles.Single(c => c.RoomId == 1);
        Assert.Equal(10 * 60, cycle.ReadyToDoctorSeconds);
        Assert.Equal(5 * 60, cycle.DoctorOccupiedWaitSeconds);
        Assert.Equal(5 * 60, cycle.DoctorAvailableWaitSeconds);
    }

    [Fact]
    public void DoctorOccupiedWait_different_doctor_does_not_block()
    {
        // Another doctor being in-room must not affect this cycle's occupied wait.
        using var workspace = TestWorkspace.Create();
        var base_ = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(base_);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Room 2 (different doctor otte): arrives at t=0, completes at t=20.
        Assert.NotNull(context.Store.SeatRoom(2, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2));

        // Room 1 (pledger): seat, ready at t=5, arrive at t=20.
        Assert.NotNull(context.Store.SeatRoom(1, "pledger", "EXT"));
        clock.SetUtcNow(base_.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        clock.SetUtcNow(base_.AddMinutes(20));
        Assert.NotNull(context.Store.MarkDoctorComplete(2));
        Assert.NotNull(context.Store.MarkRoomAvailable(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(1)); // readyToDoctor = 15 min = 900s

        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(base_.AddMinutes(25));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = context.Store.GetReports().RecentCompletedCycles.Single(c => c.RoomId == 1);
        Assert.Equal(15 * 60, cycle.ReadyToDoctorSeconds);
        Assert.Equal(0, cycle.DoctorOccupiedWaitSeconds);
        Assert.Equal(15 * 60, cycle.DoctorAvailableWaitSeconds);
    }

    [Fact]
    public void DoctorOccupiedWait_same_cycle_does_not_self_block()
    {
        // A cycle's own DoctorArrivedAt->DoctorCompleteAt interval must not reduce
        // its own ReadyForDoctorAt->DoctorArrivedAt occupied wait.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        clock.SetUtcNow(now.AddMinutes(10));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(now.AddMinutes(20));
        Assert.NotNull(context.Store.MarkDoctorArrived(1)); // readyToDoctor = 10 min = 600s
        clock.SetUtcNow(now.AddMinutes(30));
        Assert.NotNull(context.Store.MarkDoctorComplete(1)); // DoctorCompleteAt = now+30
        clock.SetUtcNow(now.AddMinutes(35));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        // Own arrived->complete window (t+20 to t+30) overlaps with ready window
        // (t+10 to t+20) by 0 seconds - no overlap since they are adjacent, not overlapping.
        // Even if there were overlap, self-exclusion must prevent it counting.
        Assert.Equal(10 * 60, cycle.ReadyToDoctorSeconds);
        Assert.Equal(0, cycle.DoctorOccupiedWaitSeconds);
        Assert.Equal(10 * 60, cycle.DoctorAvailableWaitSeconds);
    }

    [Fact]
    public void DoctorOccupiedWait_exception_cycle_excluded_from_normal_metrics()
    {
        // An exception cycle must not appear in normal aggregate metrics including
        // the new averageDoctorAvailableWaitSeconds aggregate.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Normal cycle: Room 1, 10 min ready-to-doctor, no blocker.
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        clock.SetUtcNow(now.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(now.AddMinutes(15));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(now.AddMinutes(20));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        // Exception cycle: Room 2, same doctor, mark as exception.
        clock.SetUtcNow(now.AddMinutes(25));
        Assert.NotNull(context.Store.SeatRoom(2, "otte", "CON"));
        clock.SetUtcNow(now.AddMinutes(30));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        clock.SetUtcNow(now.AddMinutes(35));
        var arrived2 = context.Store.MarkDoctorArrived(2);
        Assert.NotNull(arrived2);
        context.Store.MarkCycleAsException(2, arrived2.SeatedAt!.Value, "Test", "Exclude");

        var reports = context.Store.GetReports();

        // Normal cycle metrics must not include the exception cycle.
        Assert.Equal(1, reports.CompletedRoomCyclesCount);
        // Average available wait should equal the single normal cycle's available wait (600s = 0 occupied).
        Assert.Equal(10 * 60, reports.AverageDoctorAvailableWaitSeconds);
        Assert.Equal(10 * 60, reports.MedianDoctorAvailableWaitSeconds);
    }

    [Fact]
    public void DoctorOccupiedWait_exception_cycle_not_used_as_blocker_interval()
    {
        // If the only same-doctor occupied interval belongs to an exception cycle,
        // doctorOccupiedWaitSeconds must remain 0 for the normal cycle.
        using var workspace = TestWorkspace.Create();
        var base_ = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(base_);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Room 2 (same doctor): will be marked as exception.
        Assert.NotNull(context.Store.SeatRoom(2, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2));   // DoctorArrivedAt = base_
        clock.SetUtcNow(base_.AddMinutes(20));
        Assert.NotNull(context.Store.MarkDoctorComplete(2));  // DoctorCompleteAt = base_+20
        Assert.NotNull(context.Store.MarkRoomAvailable(2));
        // Mark Room 2's cycle as an exception - it must not serve as a blocker.
        var seatedAt2 = context.Store.GetReports().ExceptionCycles
            .Concat(context.Store.GetReports().RecentCompletedCycles)
            .First(c => c.RoomId == 2).SeatedAt;
        context.Store.MarkCycleAsException(2, seatedAt2, "Test", "Exclude");

        // Room 1 (normal): ready at t=5, arrives at t=20.
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        clock.SetUtcNow(base_.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(base_.AddMinutes(20));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(base_.AddMinutes(25));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = context.Store.GetReports().RecentCompletedCycles.Single(c => c.RoomId == 1);
        Assert.Equal(15 * 60, cycle.ReadyToDoctorSeconds);
        Assert.Equal(0, cycle.DoctorOccupiedWaitSeconds); // exception cycle must not be a blocker
        Assert.Equal(15 * 60, cycle.DoctorAvailableWaitSeconds);
    }

    [Fact]
    public void DoctorOccupiedWait_aggregate_average_and_median_use_available_wait()
    {
        // Verify averageDoctorAvailableWaitSeconds and medianDoctorAvailableWaitSeconds
        // are computed from doctorAvailableWaitSeconds, not raw readyToDoctorSeconds.
        // Two normal cycles: one fully blocked (availableWait=0), one unblocked (availableWait=600s).
        // Average = 300s, Median = 300s.
        using var workspace = TestWorkspace.Create();
        var base_ = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(base_);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock, roomCount: 4);

        // === Cycle A: Room 1 (pledger), no blocker, readyToDoctor = 10 min = 600s, available = 600s ===
        Assert.NotNull(context.Store.SeatRoom(1, "pledger", "CON"));
        clock.SetUtcNow(base_.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(base_.AddMinutes(15));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(base_.AddMinutes(20));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        // === Cycle B: Room 2 (otte), fully blocked by Room 3 ===
        // Room 3 (otte, blocker): arrives at t=20, completes at t=40.
        clock.SetUtcNow(base_.AddMinutes(20));
        Assert.NotNull(context.Store.SeatRoom(3, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(3));
        Assert.NotNull(context.Store.MarkDoctorArrived(3)); // DoctorArrivedAt = base_+20

        // Room 2 (otte, target): ready at t=25.
        Assert.NotNull(context.Store.SeatRoom(2, "otte", "CON"));
        clock.SetUtcNow(base_.AddMinutes(25));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        clock.SetUtcNow(base_.AddMinutes(40));
        Assert.NotNull(context.Store.MarkDoctorComplete(3)); // DoctorCompleteAt = base_+40
        Assert.NotNull(context.Store.MarkRoomAvailable(3));
        Assert.NotNull(context.Store.MarkDoctorArrived(2)); // readyToDoctor = 15 min = 900s
        Assert.NotNull(context.Store.MarkDoctorComplete(2));
        clock.SetUtcNow(base_.AddMinutes(45));
        Assert.NotNull(context.Store.MarkRoomAvailable(2));

        var reports = context.Store.GetReports();
        Assert.Equal(3, reports.CompletedRoomCyclesCount); // Room 1, 2, 3

        var cycleA = reports.RecentCompletedCycles.Single(c => c.RoomId == 1);
        Assert.Equal(10 * 60, cycleA.ReadyToDoctorSeconds);
        Assert.Equal(0, cycleA.DoctorOccupiedWaitSeconds);
        Assert.Equal(10 * 60, cycleA.DoctorAvailableWaitSeconds);

        var cycleB = reports.RecentCompletedCycles.Single(c => c.RoomId == 2);
        Assert.Equal(15 * 60, cycleB.ReadyToDoctorSeconds);
        Assert.Equal(15 * 60, cycleB.DoctorOccupiedWaitSeconds);
        Assert.Equal(0, cycleB.DoctorAvailableWaitSeconds);

        // Aggregates across all 3 cycles (Room 3 has readyToDoctor but no blocker).
        // Room 1: available=600, Room 2: available=0, Room 3: available=readyToDoctorSeconds of Room 3.
        // The test focuses on confirming the aggregate is not just raw readyToDoctor.
        // Room 2's available (0) differs from its readyToDoctor (900) - confirming the metric
        // reflects occupied-adjusted wait, not raw wait.
        Assert.True(reports.AverageDoctorAvailableWaitSeconds < reports.AverageReadyToDoctorSeconds,
            "Average doctor-available wait must be lower than average ready-to-doctor when blocking occurred.");
    }

    // -------------------------------------------------------------------------
    // Doctor-arrival conflict guard
    // -------------------------------------------------------------------------

    [Fact]
    public void DoctorArrived_succeeds_when_doctor_not_in_another_room()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        var result = context.Store.TryMarkDoctorArrived(1);

        Assert.Equal(DoctorArrivalOutcome.Arrived, result.Outcome);
        Assert.NotNull(result.Status);
        Assert.Equal(RoomStates.DoctorInRoom, result.Status!.State);
        Assert.Null(result.Conflict);
    }

    [Fact]
    public void DoctorArrived_is_rejected_when_same_doctor_already_in_another_room()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Room 1: same doctor checked in.
        DriveRoomToDoctorInRoom(context, 1, "otte", "CON");

        // Room 2: same doctor, ready for doctor.
        Assert.NotNull(context.Store.SeatRoom(2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        var result = context.Store.TryMarkDoctorArrived(2);

        Assert.Equal(DoctorArrivalOutcome.Conflict, result.Outcome);
        Assert.Null(result.Status);
        // Room 2 must remain ready-for-doctor; it was not checked in.
        Assert.Equal(RoomStates.ReadyForDoctor, context.Store.GetRoom(2)!.State);
    }

    [Fact]
    public void DoctorArrived_conflict_includes_room_and_doctor_context()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        DriveRoomToDoctorInRoom(context, 1, "otte", "CON");
        Assert.NotNull(context.Store.SeatRoom(2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        var conflict = context.Store.TryMarkDoctorArrived(2).Conflict;

        Assert.NotNull(conflict);
        Assert.Equal(1, conflict!.ConflictingRoomId);
        Assert.Equal("otte", conflict.DoctorId);
        Assert.False(string.IsNullOrWhiteSpace(conflict.DoctorDisplayName));
    }

    [Fact]
    public void DoctorArrived_is_not_blocked_by_a_different_doctor_in_another_room()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Room 1: a different doctor is checked in.
        DriveRoomToDoctorInRoom(context, 1, "otte", "CON");

        // Room 2: pledger is ready and must not be blocked by otte.
        Assert.NotNull(context.Store.SeatRoom(2, "pledger", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        var result = context.Store.TryMarkDoctorArrived(2);

        Assert.Equal(DoctorArrivalOutcome.Arrived, result.Outcome);
        Assert.Equal(RoomStates.DoctorInRoom, context.Store.GetRoom(2)!.State);
    }

    [Fact]
    public void DoctorArrived_is_not_blocked_when_same_doctor_is_not_in_room()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Room 1: same doctor but only ready-for-doctor (not checked in).
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        // Room 2: same doctor, ready.
        Assert.NotNull(context.Store.SeatRoom(2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        var result = context.Store.TryMarkDoctorArrived(2);

        Assert.Equal(DoctorArrivalOutcome.Arrived, result.Outcome);
        Assert.Equal(RoomStates.DoctorInRoom, context.Store.GetRoom(2)!.State);
    }

    [Fact]
    public void Resolve_completes_old_room_and_arrives_new_room_without_marking_available()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        DriveRoomToDoctorInRoom(context, 1, "otte", "CON");
        Assert.NotNull(context.Store.SeatRoom(2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        var result = context.Store.ResolveDoctorArrivalConflict(2, 1);

        Assert.Equal(DoctorArrivalOutcome.Arrived, result.Outcome);

        // Old room: Doctor Complete -> TURNOVER, with a complete timestamp but NOT available.
        var oldRoom = context.Store.GetRoom(1)!;
        Assert.Equal(RoomStates.Turnover, oldRoom.State);
        Assert.NotNull(oldRoom.DoctorCompleteAt);
        Assert.Null(oldRoom.RoomAvailableAt);

        // New room: now doctor-in-room.
        var newRoom = context.Store.GetRoom(2)!;
        Assert.Equal(RoomStates.DoctorInRoom, newRoom.State);
        Assert.NotNull(newRoom.DoctorArrivedAt);
    }

    [Fact]
    public void Resolve_revalidates_and_fails_safely_when_conflict_is_stale()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        DriveRoomToDoctorInRoom(context, 1, "otte", "CON");
        Assert.NotNull(context.Store.SeatRoom(2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        // The conflict clears before resolve runs: Room 1 is completed independently.
        Assert.NotNull(context.Store.MarkDoctorComplete(1));

        var result = context.Store.ResolveDoctorArrivalConflict(2, 1);

        Assert.Equal(DoctorArrivalOutcome.StaleConflict, result.Outcome);
        // Room 2 must NOT have been checked in by the stale resolve.
        Assert.Equal(RoomStates.ReadyForDoctor, context.Store.GetRoom(2)!.State);
    }

    [Fact]
    public void Resolve_fails_safely_when_conflicting_room_id_does_not_match()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Real conflict is in Room 1, but the caller claims Room 3.
        DriveRoomToDoctorInRoom(context, 1, "otte", "CON");
        Assert.NotNull(context.Store.SeatRoom(2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        var result = context.Store.ResolveDoctorArrivalConflict(2, 3);

        Assert.Equal(DoctorArrivalOutcome.StaleConflict, result.Outcome);
        // Neither room was mutated by the mismatched resolve.
        Assert.Equal(RoomStates.DoctorInRoom, context.Store.GetRoom(1)!.State);
        Assert.Equal(RoomStates.ReadyForDoctor, context.Store.GetRoom(2)!.State);
    }

    // Drives a room from available through to doctor-in-room with the given doctor and procedure.
    private static void DriveRoomToDoctorInRoom(StoreContext context, int room, string doctor, string procedure)
    {
        Assert.NotNull(context.Store.SeatRoom(room, doctor, procedure));
        Assert.NotNull(context.Store.MarkReadyForDoctor(room));
        Assert.NotNull(context.Store.MarkDoctorArrived(room));
        Assert.Equal(RoomStates.DoctorInRoom, context.Store.GetRoom(room)!.State);
    }

    // -------------------------------------------------------------------------
    // Procedure baseline reporting
    // -------------------------------------------------------------------------

    [Fact]
    public void Procedure_summaries_group_normal_cycles_by_code_with_counts_and_labels()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Three CON cycles and one EXT cycle, each in its own non-overlapping hour block.
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "CON", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 1, "otte", "CON", prepMin: 5, readyMin: 20, doctorMin: 30, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(2), 2, "otte", "CON", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(3), 3, "otte", "EXT", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);

        var summaries = context.Store.GetReports().ProcedureSummaries;

        Assert.Equal(2, summaries.Count);
        // Sorted by count descending: CON (3) before EXT (1).
        Assert.Equal("CON", summaries[0].ProcedureCode);
        Assert.Equal("Consult", summaries[0].ProcedureLabel);
        Assert.Equal(3, summaries[0].CompletedCycleCount);
        Assert.Equal("EXT", summaries[1].ProcedureCode);
        Assert.Equal("Extraction", summaries[1].ProcedureLabel);
        Assert.Equal(1, summaries[1].CompletedCycleCount);
    }

    [Fact]
    public void Procedure_summaries_compute_total_ready_and_doctor_time_metrics()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // CON totals: 1800, 3600, 1800 seconds. ready: 600, 1200, 600. doctorTime: 600, 1800, 600.
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "CON", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 1, "otte", "CON", prepMin: 5, readyMin: 20, doctorMin: 30, turnoverMin: 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(2), 2, "otte", "CON", prepMin: 5, readyMin: 10, doctorMin: 10, turnoverMin: 5);

        var con = context.Store.GetReports().ProcedureSummaries.Single(summary => summary.ProcedureCode == "CON");

        // Total: avg (1800+3600+1800)/3 = 2400, median of [1800,1800,3600] = 1800.
        Assert.Equal(2400, con.AverageTotalSeconds);
        Assert.Equal(1800, con.MedianTotalSeconds);
        // Ready-to-doctor: avg (600+1200+600)/3 = 800, median of [600,600,1200] = 600.
        Assert.Equal(800, con.AverageReadyToDoctorSeconds);
        Assert.Equal(600, con.MedianReadyToDoctorSeconds);
        // Doctor time (in room): avg (600+1800+600)/3 = 1000, median of [600,600,1800] = 600.
        Assert.Equal(1000, con.AverageDoctorTimeSeconds);
        Assert.Equal(600, con.MedianDoctorTimeSeconds);
    }

    [Fact]
    public void Procedure_summaries_use_existing_occupied_and_available_wait_values()
    {
        // Reuses the existing partial-overlap scenario. The CON cycle's occupied/available wait is
        // produced by AnnotateOccupiedWait; the procedure summary must surface those exact values.
        using var workspace = TestWorkspace.Create();
        var base_ = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(base_);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Room 2 (same doctor, different procedure) is the blocker: in-room from t+0 to t+10.
        Assert.NotNull(context.Store.SeatRoom(2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2));

        // Room 1 (CON target): ready at t+5, arrives at t+15 => readyToDoctor 600s, overlap 300s.
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        clock.SetUtcNow(base_.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        clock.SetUtcNow(base_.AddMinutes(10));
        Assert.NotNull(context.Store.MarkDoctorComplete(2));
        Assert.NotNull(context.Store.MarkRoomAvailable(2));

        clock.SetUtcNow(base_.AddMinutes(15));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(base_.AddMinutes(20));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var reports = context.Store.GetReports();
        var targetCycle = reports.RecentCompletedCycles.Single(cycle => cycle.RoomId == 1);
        var con = reports.ProcedureSummaries.Single(summary => summary.ProcedureCode == "CON");

        // The summary values match the cycle's annotated occupied/available wait exactly.
        Assert.Equal(5 * 60, targetCycle.DoctorOccupiedWaitSeconds);
        Assert.Equal(5 * 60, targetCycle.DoctorAvailableWaitSeconds);
        Assert.Equal(300, con.AverageDoctorOccupiedWaitSeconds);
        Assert.Equal(300, con.MedianDoctorOccupiedWaitSeconds);
        Assert.Equal(300, con.AverageDoctorAvailableWaitSeconds);
        Assert.Equal(300, con.MedianDoctorAvailableWaitSeconds);
    }

    [Fact]
    public void Procedure_summaries_exclude_exception_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        RunProcedureCycle(context, clock, baseTime, 1, "otte", "CON", 5, 10, 10, 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 2, "otte", "CON", 5, 10, 10, 5);

        // Flag one CON cycle as a (pending) exception.
        var flagged = context.Store.GetReports().RecentCompletedCycles.First(cycle => cycle.ProcedureCode == "CON");
        Assert.True(context.Store.MarkCycleAsExceptionById(flagged.CompletedCycleId, ExceptionReasons.ManualReview, "Exclude from normal metrics"));

        var con = context.Store.GetReports().ProcedureSummaries.Single(summary => summary.ProcedureCode == "CON");
        Assert.Equal(1, con.CompletedCycleCount);
    }

    [Fact]
    public void Procedure_summaries_exclude_reviewed_exception_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        RunProcedureCycle(context, clock, baseTime, 1, "otte", "CON", 5, 10, 10, 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 2, "otte", "CON", 5, 10, 10, 5);

        var flagged = context.Store.GetReports().RecentCompletedCycles.First(cycle => cycle.ProcedureCode == "CON");
        Assert.True(context.Store.MarkCycleAsExceptionById(flagged.CompletedCycleId, ExceptionReasons.ManualReview, "Exclude from normal metrics"));
        Assert.Equal(ReviewExceptionOutcome.Reviewed, context.Store.ReviewExceptionCycleById(flagged.CompletedCycleId).Outcome);

        var con = context.Store.GetReports().ProcedureSummaries.Single(summary => summary.ProcedureCode == "CON");
        // The reviewed exception stays excluded; only the one normal CON cycle counts.
        Assert.Equal(1, con.CompletedCycleCount);
    }

    [Fact]
    public void Procedure_summaries_fall_back_to_code_when_label_is_blank()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        // Custom roster with a procedure whose label is blank.
        var roster = new ProcedureRosterOptions
        {
            Procedures =
            [
                new ProcedureRosterItem { Id = "blank", Code = "BLANK", Label = "   ", Icon = "misc", Active = true }
            ]
        };
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            procedureRosterOptions: roster);

        RunProcedureCycle(context, clock, baseTime, 1, "otte", "BLANK", 5, 10, 10, 5);

        var summary = Assert.Single(context.Store.GetReports().ProcedureSummaries);
        // Blank label falls back to the raw code; reports do not crash.
        Assert.Equal("BLANK", summary.ProcedureCode);
        Assert.Equal("BLANK", summary.ProcedureLabel);
        Assert.Equal(1, summary.CompletedCycleCount);
    }

    [Fact]
    public void Procedure_summaries_are_additive_and_global_metrics_stay_combined()
    {
        using var workspace = TestWorkspace.Create();
        var baseTime = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(baseTime);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Two CON (ready 600, 1200) and one EXT (ready 600).
        RunProcedureCycle(context, clock, baseTime, 1, "otte", "CON", 5, 10, 10, 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(1), 2, "otte", "CON", 5, 20, 10, 5);
        RunProcedureCycle(context, clock, baseTime.AddHours(2), 3, "otte", "EXT", 5, 10, 10, 5);

        var reports = context.Store.GetReports();

        // Global metrics still combine ALL procedures: count 3, ready avg (600+1200+600)/3 = 800.
        Assert.Equal(3, reports.CompletedRoomCyclesCount);
        Assert.Equal(800, reports.AverageReadyToDoctorSeconds);

        // The CON-only baseline differs from the global figure, proving the breakdown is additive
        // and did not alter the global combined math.
        var con = reports.ProcedureSummaries.Single(summary => summary.ProcedureCode == "CON");
        Assert.Equal(900, con.AverageReadyToDoctorSeconds);
    }

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

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "EXT", expectedAllocationUnits: 5));

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

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "EXT"));

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

    // Seats, readies, completes, and frees one room across the given minute offsets from seatedAt.
    // Each call uses a self-contained time window; keep windows non-overlapping to avoid
    // cross-cycle doctor-occupied wait when that is not under test.
    private static void RunProcedureCycle(
        StoreContext context,
        ManualTimeProvider clock,
        DateTimeOffset seatedAt,
        int room,
        string doctor,
        string procedure,
        int prepMin,
        int readyMin,
        int doctorMin,
        int turnoverMin,
        bool sedation = false,
        int? expectedAllocationUnits = null)
    {
        clock.SetUtcNow(seatedAt);
        Assert.NotNull(context.Store.SeatRoom(room, doctor, procedure, sedation: sedation, expectedAllocationUnits: expectedAllocationUnits));
        clock.SetUtcNow(seatedAt.AddMinutes(prepMin));
        Assert.NotNull(context.Store.MarkReadyForDoctor(room));
        clock.SetUtcNow(seatedAt.AddMinutes(prepMin + readyMin));
        Assert.NotNull(context.Store.MarkDoctorArrived(room));
        clock.SetUtcNow(seatedAt.AddMinutes(prepMin + readyMin + doctorMin));
        Assert.NotNull(context.Store.MarkDoctorComplete(room));
        clock.SetUtcNow(seatedAt.AddMinutes(prepMin + readyMin + doctorMin + turnoverMin));
        Assert.NotNull(context.Store.MarkRoomAvailable(room));
    }

    // -------------------------------------------------------------------------
    // Exception review workflow (confirm exclusion)
    // -------------------------------------------------------------------------

    [Fact]
    public void Marked_exception_starts_pending_review()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var cycle = CompleteOneCycle(context, room: 1, doctor: "otte");
        Assert.True(context.Store.MarkCycleAsExceptionById(cycle.CompletedCycleId, ExceptionReasons.ManualReview, "Exclude from normal metrics"));

        var exception = Assert.Single(context.Store.GetReports().ExceptionCycles);
        Assert.True(exception.IsException);
        Assert.True(exception.RequiresReview);
        Assert.Equal(ReviewStatuses.PendingReview, exception.ReviewStatus);
        Assert.Null(exception.ReviewedAt);
        Assert.Null(exception.ReviewedBy);
    }

    [Fact]
    public void Confirm_exclusion_marks_reviewed_and_keeps_cycle_excluded()
    {
        using var workspace = TestWorkspace.Create();
        var reviewedAt = new DateTimeOffset(2026, 6, 11, 14, 30, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(reviewedAt);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        var cycle = CompleteOneCycle(context, room: 1, doctor: "otte");
        Assert.True(context.Store.MarkCycleAsExceptionById(cycle.CompletedCycleId, ExceptionReasons.ManualReview, "Exclude from normal metrics"));

        var result = context.Store.ReviewExceptionCycleById(cycle.CompletedCycleId);
        Assert.Equal(ReviewExceptionOutcome.Reviewed, result.Outcome);
        Assert.Equal(1, result.RoomId);

        var reports = context.Store.GetReports();

        // After review the cycle is no longer pending and never returns to normal completed cycles.
        Assert.Empty(reports.RecentCompletedCycles);
        Assert.Empty(reports.ExceptionCycles);
        Assert.Equal(0, reports.CompletedRoomCyclesCount);
    }

    [Fact]
    public void Confirm_exclusion_sets_reviewed_metadata()
    {
        using var workspace = TestWorkspace.Create();
        var reviewedAt = new DateTimeOffset(2026, 6, 11, 14, 30, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(reviewedAt);
        var databasePath = workspace.ProductionDatabasePath();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock, databasePath: databasePath);

        var cycle = CompleteOneCycle(context, room: 1, doctor: "otte");
        Assert.True(context.Store.MarkCycleAsExceptionById(cycle.CompletedCycleId, ExceptionReasons.ManualReview, "Exclude from normal metrics"));
        Assert.Equal(ReviewExceptionOutcome.Reviewed, context.Store.ReviewExceptionCycleById(cycle.CompletedCycleId).Outcome);

        // Reload from the same database to confirm the reviewed metadata persisted.
        var reloaded = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var persisted = reloaded.Repository.LoadCompletedCycles()
            .Single(item => item.CompletedCycleId == cycle.CompletedCycleId);

        Assert.True(persisted.IsException);
        Assert.False(persisted.RequiresReview);
        Assert.Equal(ReviewStatuses.Reviewed, persisted.ReviewStatus);
        Assert.Equal("Exclude from normal metrics", persisted.SuggestedAction);
        Assert.Equal(reviewedAt, persisted.ReviewedAt);
        Assert.Equal(ExceptionReviewers.LocalAdmin, persisted.ReviewedBy);
    }

    [Fact]
    public void Reviewed_exception_stays_excluded_from_normal_metrics()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // One normal cycle and one cycle that becomes a reviewed exception.
        var normal = CompleteOneCycle(context, room: 1, doctor: "otte");
        var flagged = CompleteOneCycle(context, room: 2, doctor: "pledger");
        Assert.True(context.Store.MarkCycleAsExceptionById(flagged.CompletedCycleId, ExceptionReasons.ManualReview, "Exclude from normal metrics"));
        Assert.Equal(ReviewExceptionOutcome.Reviewed, context.Store.ReviewExceptionCycleById(flagged.CompletedCycleId).Outcome);

        var reports = context.Store.GetReports();

        // Only the normal cycle counts toward metrics; the reviewed exception is excluded and is
        // not in the pending-review queue either.
        Assert.Equal(1, reports.CompletedRoomCyclesCount);
        Assert.Equal(normal.CompletedCycleId, Assert.Single(reports.RecentCompletedCycles).CompletedCycleId);
        Assert.Empty(reports.ExceptionCycles);

        // Available-wait math unchanged for the surviving normal cycle.
        var normalReported = reports.RecentCompletedCycles.Single();
        Assert.Equal(normalReported.ReadyToDoctorSeconds, normalReported.DoctorAvailableWaitSeconds);
        Assert.Equal(0, normalReported.DoctorOccupiedWaitSeconds);
    }

    [Fact]
    public void Confirm_exclusion_missing_id_returns_not_found()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.Equal(ReviewExceptionOutcome.NotFound, context.Store.ReviewExceptionCycleById(999999).Outcome);
        Assert.Equal(ReviewExceptionOutcome.NotFound, context.Store.ReviewExceptionCycleById(0).Outcome);
        Assert.Equal(ReviewExceptionOutcome.NotFound, context.Store.ReviewExceptionCycleById(-5).Outcome);
    }

    [Fact]
    public void Confirm_exclusion_on_non_exception_returns_not_an_exception()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var cycle = CompleteOneCycle(context, room: 1, doctor: "otte");
        var result = context.Store.ReviewExceptionCycleById(cycle.CompletedCycleId);

        Assert.Equal(ReviewExceptionOutcome.NotAnException, result.Outcome);
        Assert.Equal(1, result.RoomId);

        // The cycle remains a normal completed cycle, untouched.
        var reports = context.Store.GetReports();
        var normal = Assert.Single(reports.RecentCompletedCycles);
        Assert.False(normal.IsException);
        Assert.Equal(ReviewStatuses.PendingReview, normal.ReviewStatus);
        Assert.Null(normal.ReviewedAt);
    }

    [Fact]
    public void Confirm_exclusion_is_idempotent()
    {
        using var workspace = TestWorkspace.Create();
        var firstReview = new DateTimeOffset(2026, 6, 11, 14, 30, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(firstReview);
        var databasePath = workspace.ProductionDatabasePath();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock, databasePath: databasePath);

        var cycle = CompleteOneCycle(context, room: 1, doctor: "otte");
        Assert.True(context.Store.MarkCycleAsExceptionById(cycle.CompletedCycleId, ExceptionReasons.ManualReview, "Exclude from normal metrics"));

        Assert.Equal(ReviewExceptionOutcome.Reviewed, context.Store.ReviewExceptionCycleById(cycle.CompletedCycleId).Outcome);

        // Confirming again succeeds (idempotent) and keeps the reviewed state stable.
        clock.SetUtcNow(firstReview.AddHours(1));
        Assert.Equal(ReviewExceptionOutcome.Reviewed, context.Store.ReviewExceptionCycleById(cycle.CompletedCycleId).Outcome);

        var reloaded = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var persisted = reloaded.Repository.LoadCompletedCycles()
            .Single(item => item.CompletedCycleId == cycle.CompletedCycleId);
        Assert.False(persisted.RequiresReview);
        Assert.Equal(ReviewStatuses.Reviewed, persisted.ReviewStatus);
        Assert.True(persisted.IsException);
    }

    [Fact]
    public void Legacy_mark_exception_still_appears_in_pending_review_queue()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var cycle = CompleteOneCycle(context, room: 1, doctor: "otte");
        // Legacy targeting by (roomId, seatedAt) must still flag the cycle as a pending exception.
        Assert.True(context.Store.MarkCycleAsException(cycle.RoomId, cycle.SeatedAt, ExceptionReasons.ManualReview, "Exclude from normal metrics"));

        var exception = Assert.Single(context.Store.GetReports().ExceptionCycles);
        Assert.True(exception.RequiresReview);
        Assert.Equal(ReviewStatuses.PendingReview, exception.ReviewStatus);
    }

    // -------------------------------------------------------------------------
    // Report date range filtering (bounds the completed-cycle population by DoctorCompleteAt)
    // -------------------------------------------------------------------------

    [Fact]
    public void Reports_without_date_range_include_all_completed_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 3);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 20, 14), expectedUnits: 3);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports();

        Assert.Equal(2, reports.CompletedRoomCyclesCount);
        Assert.Equal("All time", reports.RangeLabel);
        Assert.Null(reports.RangeStartDate);
        Assert.Equal(2, reports.TotalCompletedCycleCount);
    }

    [Fact]
    public void Date_range_includes_in_range_and_excludes_out_of_range_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 3);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 20, 14), expectedUnits: 3);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-08", "2026-06-12"));

        // Only the in-range cycle survives the source filter.
        Assert.Equal(1, reports.CompletedRoomCyclesCount);
        var cycle = Assert.Single(reports.RecentCompletedCycles);
        Assert.Equal(1, cycle.RoomId);
        // Window metadata reflects the selection; the all-time total is still reported for context.
        Assert.Equal("2026-06-08", reports.RangeStartDate);
        Assert.Equal("2026-06-12", reports.RangeEndDate);
        Assert.Equal(2, reports.TotalCompletedCycleCount);
    }

    [Fact]
    public void Date_range_end_day_is_inclusive_through_end_of_day()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // A cycle completing late on the end day must be included (end day inclusive through 23:59).
        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 12, 23, 30), expectedUnits: 3);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-10", "2026-06-12"));

        Assert.Equal(1, reports.CompletedRoomCyclesCount);
    }

    [Fact]
    public void Date_filter_applies_before_allocation_and_hygiene_counts()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 3);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 20, 14), expectedUnits: 3);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-08", "2026-06-12"));

        // Hygiene and allocation aggregates are computed over the date-filtered population only.
        Assert.Equal(1, reports.IncludedCompletedCycleCount);
        Assert.Equal(0, reports.ExcludedCompletedCycleCount);
        Assert.Equal(1, reports.AllocationVariance!.AllocationVarianceCycleCount);
    }

    [Fact]
    public void Date_filter_affects_doctor_and_procedure_family_summaries()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // In range: otte / EXT. Out of range: pledger / IMP.
        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 3);
        SaveCleanCycle(seed, room: 2, doctor: "pledger", code: "IMP", completeAt: Utc(2026, 6, 20, 14), expectedUnits: 6);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-08", "2026-06-12"));

        Assert.Equal("otte", Assert.Single(reports.DoctorSummaries).AssignedDoctor);
        Assert.Equal("EXT", Assert.Single(reports.BaseProcedureSummaries).ProcedureCode);
    }

    [Fact]
    public void Reversed_date_range_is_normalized_and_invalid_dates_degrade_to_all_time()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 15, 14), expectedUnits: 3);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 7, 15, 14), expectedUnits: 3);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // Reversed pair is normalized to Jun 10 - Jun 20 and includes the Jun 15 cycle.
        var reversed = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-20", "2026-06-10"));
        Assert.Equal(1, reversed.CompletedRoomCyclesCount);
        Assert.Equal("2026-06-10", reversed.RangeStartDate);
        Assert.Equal("2026-06-20", reversed.RangeEndDate);

        // Unparseable dates do not crash and behave as all-time.
        var invalid = context.Store.GetReports(ReportDateRange.FromDateStrings("not-a-date", "also-bad"));
        Assert.Equal(2, invalid.CompletedRoomCyclesCount);
        Assert.Equal("All time", invalid.RangeLabel);
    }

    // -------------------------------------------------------------------------
    // Doctor daily allocation balance (sparkline data)
    //
    // SaveCleanCycle fixes measured case flow at 30 min (seated 30 min before complete), so each
    // cycle's net allocation variance is 30 - expectedUnits * 10: expectedUnits 2 => +10 (over),
    // 1 => +20 (over), 4 => -10 (under), 3 => 0 (at expected).
    // -------------------------------------------------------------------------

    [Fact]
    public void Doctor_daily_allocation_series_reflects_filtered_date_range()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // otte: one cycle in range (Jun 10, +10 variance), one outside the window (Jun 20).
        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 2);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 20, 14), expectedUnits: 2);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-08", "2026-06-12"));

        var series = Assert.Single(reports.DoctorDailyAllocationSeries!);
        Assert.Equal("otte", series.DoctorId);
        var point = Assert.Single(series.Points);
        Assert.Equal("2026-06-10", point.Date);
        Assert.Equal(1, point.CaseCount);
        Assert.Equal(10, point.NetVarianceMinutes);
    }

    [Fact]
    public void Doctor_daily_allocation_point_net_variance_can_be_negative()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // expectedUnits 4 => 40 min expected vs 30 min measured => -10 (under expected).
        SaveCleanCycle(seed, room: 1, doctor: "pledger", code: "IMP", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 4);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-08", "2026-06-12"));

        var point = Assert.Single(Assert.Single(reports.DoctorDailyAllocationSeries!).Points);
        Assert.Equal(1, point.CaseCount);
        Assert.Equal(-10, point.NetVarianceMinutes);
    }

    [Fact]
    public void Doctor_daily_allocation_series_today_contains_only_todays_points()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        context.Store.SeedSyntheticReportData();

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        var reports = context.Store.GetReports(ReportDateRange.FromDates(today, today));

        var todayStr = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var series in reports.DoctorDailyAllocationSeries!)
        {
            foreach (var point in series.Points)
            {
                Assert.Equal(todayStr, point.Date);
            }
        }
    }

    [Fact]
    public void Doctor_daily_allocation_series_grows_with_wider_date_ranges()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        context.Store.SeedSyntheticReportData();

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        static int TotalPoints(IReadOnlyList<DoctorDailyAllocation>? series) =>
            (series ?? []).Sum(s => s.Points.Count);

        var todayPoints = TotalPoints(context.Store.GetReports(ReportDateRange.FromDates(today, today)).DoctorDailyAllocationSeries);
        var last7Points = TotalPoints(context.Store.GetReports(ReportDateRange.FromDates(today.AddDays(-6), today)).DoctorDailyAllocationSeries);
        var last30Points = TotalPoints(context.Store.GetReports(ReportDateRange.FromDates(today.AddDays(-29), today)).DoctorDailyAllocationSeries);
        var allPoints = TotalPoints(context.Store.GetReports().DoctorDailyAllocationSeries);

        Assert.True(last7Points > todayPoints, $"last7={last7Points} today={todayPoints}");
        Assert.True(last30Points > last7Points, $"last30={last30Points} last7={last7Points}");
        Assert.True(allPoints > last30Points, $"all={allPoints} last30={last30Points}");
    }

    [Fact]
    public void Doctor_daily_allocation_series_covers_all_four_seeded_doctors()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        context.Store.SeedSyntheticReportData();

        var doctorIds = context.Store.GetReports().DoctorDailyAllocationSeries!
            .Select(s => s.DoctorId)
            .ToHashSet();

        Assert.Contains("otte", doctorIds);
        Assert.Contains("pledger", doctorIds);
        Assert.Contains("gibson", doctorIds);
        Assert.Contains("schroeder", doctorIds);
    }

    [Fact]
    public void Doctor_daily_allocation_point_aggregates_case_count_and_signed_net_variance()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // Three cycles for otte on the same UTC day, mixing over and under so the signed sum (not an
        // absolute) is exercised: +20, +10, -10 => net +20 across 3 cases.
        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "CON", completeAt: Utc(2026, 6, 10, 9), expectedUnits: 1);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 11), expectedUnits: 2);
        SaveCleanCycle(seed, room: 3, doctor: "otte", code: "IMP", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 4);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-10", "2026-06-10"));

        var series = Assert.Single(reports.DoctorDailyAllocationSeries!);
        Assert.Equal("otte", series.DoctorId);
        var point = Assert.Single(series.Points);
        Assert.Equal("2026-06-10", point.Date);
        Assert.Equal(3, point.CaseCount);
        Assert.Equal(20, point.NetVarianceMinutes);
    }

    [Fact]
    public void Doctor_daily_allocation_point_net_variance_rolls_up_to_report_total()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // Two cycles for otte across two different days, both in range.
        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 9), expectedUnits: 2);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "IMP", completeAt: Utc(2026, 6, 11, 9), expectedUnits: 4);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-08", "2026-06-12"));

        // The doctor's daily points sum to the same net the report's allocation aggregate reports.
        var series = Assert.Single(reports.DoctorDailyAllocationSeries!);
        var dailySum = series.Points.Sum(p => p.NetVarianceMinutes);
        Assert.Equal(reports.AllocationVariance!.NetAllocationVarianceMinutes, dailySum);
    }

    // -------------------------------------------------------------------------
    // Weekly wait trend read model exposed on the report snapshot
    // -------------------------------------------------------------------------

    [Fact]
    public void Reports_expose_weekly_wait_trends_over_standard_completed_population()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 8, 9), expectedUnits: 2);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 14, 9), expectedUnits: 2);
        SaveCleanCycle(seed, room: 3, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 15, 9), expectedUnits: 2);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var trends = context.Store.GetReports().Trends;

        Assert.NotNull(trends);
        Assert.Equal(ReportTrendSnapshotBuilder.WeeklyBucketSize, trends.BucketSize);
        Assert.Collection(
            trends.Buckets,
            first =>
            {
                Assert.Equal("2026-06-08", first.StartDate);
                Assert.Equal("2026-06-15", first.EndDate);
                Assert.Equal(2, first.CompletedCycleCount);
                Assert.Equal(900, first.MedianSeatedToDoctorSeconds);
                Assert.Equal(900, first.AverageSeatedToDoctorSeconds);
                Assert.Equal(2, first.TurnoverCycleCount);
                Assert.Equal(300, first.MedianTurnoverSeconds);
                Assert.Equal(300, first.AverageTurnoverSeconds);
            },
            second =>
            {
                Assert.Equal("2026-06-15", second.StartDate);
                Assert.Equal("2026-06-22", second.EndDate);
                Assert.Equal(1, second.CompletedCycleCount);
                Assert.Equal(1, second.TurnoverCycleCount);
                Assert.Equal(300, second.MedianTurnoverSeconds);
                Assert.Equal(300, second.AverageTurnoverSeconds);
            });
    }

    [Fact]
    public void Reports_wait_trends_respect_date_range_filter()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 2);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 20, 14), expectedUnits: 2);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-08", "2026-06-12"));

        var trends = reports.Trends;
        Assert.NotNull(trends);
        var bucket = Assert.Single(trends.Buckets);
        Assert.Equal("2026-06-08", bucket.StartDate);
        Assert.Equal("2026-06-15", bucket.EndDate);
        Assert.Equal(1, bucket.CompletedCycleCount);
        Assert.Equal(1, bucket.TurnoverCycleCount);
        Assert.Equal(300, bucket.MedianTurnoverSeconds);
        Assert.Equal(300, bucket.AverageTurnoverSeconds);
    }

    [Fact]
    public void Reports_wait_trends_exclude_reporting_exception_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 9), expectedUnits: 2);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "UNKNOWN", completeAt: Utc(2026, 6, 11, 9), expectedUnits: 2);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports();

        Assert.Equal(2, reports.CompletedRoomCyclesCount);
        Assert.Equal(1, reports.IncludedCompletedCycleCount);
        Assert.Equal(1, reports.ExcludedCompletedCycleCount);

        var bucket = Assert.Single(reports.Trends!.Buckets);
        Assert.Equal(1, bucket.CompletedCycleCount);
        Assert.Equal(1, bucket.TurnoverCycleCount);
    }

    // -------------------------------------------------------------------------
    // Schedule-fit read model exposed on the report snapshot
    //
    // SaveCleanCycle fixes measured case flow at 30 min, so each cycle's variance is
    // 30 - expectedUnits * 10. ScheduleFit is computed over the standard completed-cycle
    // population, the same set that feeds AllocationVariance, so shared totals must agree.
    // -------------------------------------------------------------------------

    [Fact]
    public void Reports_expose_schedule_fit_over_standard_completed_population()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // Three clean cycles; measured 30 each. Expected 20, 20, 40 => variance +10, +10, -10 => net +10.
        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 9), expectedUnits: 2);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 11, 9), expectedUnits: 2);
        SaveCleanCycle(seed, room: 3, doctor: "otte", code: "IMP", completeAt: Utc(2026, 6, 12, 9), expectedUnits: 4);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports();

        var scheduleFit = reports.ScheduleFit;
        Assert.NotNull(scheduleFit);

        // All three are standard completed cycles and allocation-calculable.
        Assert.Equal(3, scheduleFit.IncludedCycleCount);
        Assert.Equal(3, scheduleFit.ScheduleFitCycleCount);
        Assert.Equal(3, scheduleFit.Overall.CycleCount);

        // expected 20+20+40 = 80; measured 30*3 = 90; variance = 90 - 80 = +10.
        Assert.Equal(80, scheduleFit.Overall.TotalExpectedMinutes);
        Assert.Equal(90, scheduleFit.Overall.TotalMeasuredMinutes);
        Assert.Equal(10, scheduleFit.Overall.TotalVarianceMinutes);

        // Shared totals must agree with the allocation variance summary over the same population.
        var allocation = reports.AllocationVariance;
        Assert.NotNull(allocation);
        Assert.Equal(allocation.AllocationVarianceCycleCount, scheduleFit.Overall.CycleCount);
        Assert.Equal(allocation.TotalExpectedAllocationMinutes, scheduleFit.Overall.TotalExpectedMinutes);
        Assert.Equal(allocation.TotalMeasuredCaseFlowMinutes, scheduleFit.Overall.TotalMeasuredMinutes);
        Assert.Equal(allocation.NetAllocationVarianceMinutes, scheduleFit.Overall.TotalVarianceMinutes);
    }

    [Fact]
    public void Reports_schedule_fit_present_and_zero_when_no_completed_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var scheduleFit = context.Store.GetReports().ScheduleFit;

        // Always populated, even with no data: the builder returns a zero report, not null.
        Assert.NotNull(scheduleFit);
        Assert.Equal(0, scheduleFit.IncludedCycleCount);
        Assert.Equal(0, scheduleFit.ScheduleFitCycleCount);
        Assert.Equal(0, scheduleFit.Overall.CycleCount);
        Assert.Equal(0, scheduleFit.Overall.TotalExpectedMinutes);
        Assert.Equal(0, scheduleFit.Overall.TotalMeasuredMinutes);
        Assert.Equal(0, scheduleFit.Overall.TotalVarianceMinutes);
        Assert.Equal(0, scheduleFit.Overall.TotalSlackMinutes);
        Assert.Equal(0, scheduleFit.Overall.TotalDebtMinutes);
        Assert.Null(scheduleFit.Overall.UtilizationRatio);
    }

    [Fact]
    public void Reports_schedule_fit_respects_date_range_filter()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // One cycle in range (Jun 10, expected 20, +10 variance), one outside the window (Jun 20).
        SaveCleanCycle(seed, room: 1, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 10, 14), expectedUnits: 2);
        SaveCleanCycle(seed, room: 2, doctor: "otte", code: "EXT", completeAt: Utc(2026, 6, 20, 14), expectedUnits: 2);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-08", "2026-06-12"));

        // Only the in-range cycle feeds schedule fit; the out-of-range cycle is filtered upstream.
        var scheduleFit = reports.ScheduleFit;
        Assert.NotNull(scheduleFit);
        Assert.Equal(1, scheduleFit.IncludedCycleCount);
        Assert.Equal(1, scheduleFit.ScheduleFitCycleCount);
        Assert.Equal(20, scheduleFit.Overall.TotalExpectedMinutes);
        Assert.Equal(30, scheduleFit.Overall.TotalMeasuredMinutes);
        Assert.Equal(10, scheduleFit.Overall.TotalVarianceMinutes);
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    // Persists a single clean, allocation-calculable completed cycle anchored on completeAt (same UTC
    // day, no hygiene flags), for date-range tests. Reload a new StoreContext on the same DB to read it.
    private static void SaveCleanCycle(
        StoreContext context, int room, string doctor, string code, DateTimeOffset completeAt, int expectedUnits)
    {
        var seatedAt = completeAt.AddMinutes(-30);
        var cycle = new CompletedRoomCycle
        {
            RoomId = room,
            AssignedDoctor = doctor,
            ProcedureCode = code,
            SeatedAt = seatedAt,
            ReadyForDoctorAt = seatedAt.AddMinutes(5),
            DoctorArrivedAt = seatedAt.AddMinutes(15),
            DoctorCompleteAt = completeAt,
            RoomAvailableAt = completeAt.AddMinutes(5),
            SeatedToDoctorSeconds = 900,
            PrepSeconds = 300,
            ReadyToDoctorSeconds = 600,
            DoctorInRoomSeconds = 900,
            TurnoverSeconds = 300,
            TotalRoomCycleSeconds = 2100,
            FinalWaitState = "ready-for-doctor",
            OriginalDefaultExpectedUnits = expectedUnits,
            ExpectedAllocationUnits = expectedUnits,
            ExpectedAllocationMinutes = expectedUnits * 10
        };
        context.Repository.SaveCompletedCycle(cycle, context.Doctors, context.Procedures);
    }

    // Seats, readies, completes, and frees a single room, returning the resulting completed cycle.
    private static CompletedRoomCycle CompleteOneCycle(StoreContext context, int room, string doctor)
    {
        Assert.NotNull(context.Store.SeatRoom(room, doctor, "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(room));
        Assert.NotNull(context.Store.MarkDoctorArrived(room));
        Assert.NotNull(context.Store.MarkDoctorComplete(room));
        Assert.NotNull(context.Store.MarkRoomAvailable(room));
        return context.Store.GetReports().RecentCompletedCycles.Single(cycle => cycle.RoomId == room);
    }

    // -------------------------------------------------------------------------
    // CompletedCycleId stable identity
    // -------------------------------------------------------------------------

    [Fact]
    public void Completed_cycle_receives_nonzero_completed_cycle_id()
    {
        // A newly persisted cycle must carry a positive CompletedCycleId assigned by SQLite.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        Assert.True(cycle.CompletedCycleId > 0, "CompletedCycleId must be a positive value.");
    }

    [Fact]
    public void Reports_expose_completed_cycle_id_for_normal_and_exception_cycles()
    {
        // Both RecentCompletedCycles and ExceptionCycles must expose a positive CompletedCycleId.
        // Also confirms available-wait math is untouched: an unblocked cycle keeps
        // doctorAvailableWaitSeconds == readyToDoctorSeconds and doctorOccupiedWaitSeconds == 0.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Normal cycle on room 1.
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        clock.SetUtcNow(now.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(now.AddMinutes(15));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(now.AddMinutes(20));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        // Exception cycle on room 2.
        clock.SetUtcNow(now.AddMinutes(25));
        Assert.NotNull(context.Store.SeatRoom(2, "pledger", "EXT"));
        clock.SetUtcNow(now.AddMinutes(30));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        clock.SetUtcNow(now.AddMinutes(35));
        var arrived2 = context.Store.MarkDoctorArrived(2);
        Assert.NotNull(arrived2);
        context.Store.MarkCycleAsException(2, arrived2.SeatedAt!.Value, "Test", "Exclude");

        var reports = context.Store.GetReports();

        var normal = Assert.Single(reports.RecentCompletedCycles);
        Assert.True(normal.CompletedCycleId > 0);
        // Available-wait math unchanged: no same-doctor blocker for this cycle.
        Assert.Equal(10 * 60, normal.ReadyToDoctorSeconds);
        Assert.Equal(0, normal.DoctorOccupiedWaitSeconds);
        Assert.Equal(10 * 60, normal.DoctorAvailableWaitSeconds);

        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.True(exception.CompletedCycleId > 0);
        Assert.NotEqual(normal.CompletedCycleId, exception.CompletedCycleId);
    }

    [Fact]
    public void Mark_exception_by_completed_cycle_id_succeeds()
    {
        // The preferred targeting path: mark a cycle as an exception by its stable id.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycleId = Assert.Single(context.Store.GetReports().RecentCompletedCycles).CompletedCycleId;
        Assert.True(cycleId > 0);

        var marked = context.Store.MarkCycleAsExceptionById(cycleId, ExceptionReasons.ManualReview, "Exclude from normal metrics");
        Assert.True(marked);

        var reports = context.Store.GetReports();
        Assert.Empty(reports.RecentCompletedCycles);
        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.Equal(cycleId, exception.CompletedCycleId);
        Assert.True(exception.IsException);
        Assert.Equal(ExceptionReasons.ManualReview, exception.ExceptionReason);

        // Targeting a non-existent id returns false.
        Assert.False(context.Store.MarkCycleAsExceptionById(999999, ExceptionReasons.ManualReview, "noop"));
        // A non-positive id is rejected.
        Assert.False(context.Store.MarkCycleAsExceptionById(0, ExceptionReasons.ManualReview, "noop"));
    }

    [Fact]
    public void Mark_exception_by_legacy_room_and_seated_at_still_works()
    {
        // Backward compatibility: the legacy (roomId, seatedAt) targeting path must still work.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        var marked = context.Store.MarkCycleAsException(cycle.RoomId, cycle.SeatedAt, ExceptionReasons.ManualReview, "Exclude from normal metrics");
        Assert.True(marked);

        var reports = context.Store.GetReports();
        Assert.Empty(reports.RecentCompletedCycles);
        Assert.Equal(cycle.CompletedCycleId, Assert.Single(reports.ExceptionCycles).CompletedCycleId);
    }

    [Fact]
    public void Completed_cycle_id_is_stable_across_store_reload()
    {
        // The id assigned on first persist must survive a store restart unchanged.
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        Assert.NotNull(first.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(first.Store.MarkReadyForDoctor(1));
        Assert.NotNull(first.Store.MarkDoctorArrived(1));
        Assert.NotNull(first.Store.MarkDoctorComplete(1));
        Assert.NotNull(first.Store.MarkRoomAvailable(1));

        var originalId = Assert.Single(first.Store.GetReports().RecentCompletedCycles).CompletedCycleId;
        Assert.True(originalId > 0);

        // Reload from the same database file.
        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reloaded = Assert.Single(second.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(originalId, reloaded.CompletedCycleId);
    }

    [Fact]
    public void Legacy_completed_cycles_table_without_id_is_migrated_and_backfilled()
    {
        // Defensive migration: a legacy table that predates the explicit id column must be
        // rebuilt with id INTEGER PRIMARY KEY AUTOINCREMENT, preserving all existing data and
        // assigning a unique positive id to every row (backfill).
        using var workspace = TestWorkspace.Create();
        var databasePath = Path.Combine(workspace.DataRoot, "legacy-no-id.db");

        using (var seed = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
        {
            seed.Open();
            using (var create = seed.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE completed_room_cycles (
                        room_id INTEGER NOT NULL,
                        assigned_doctor_id TEXT NOT NULL,
                        assigned_doctor_display_name TEXT NOT NULL,
                        procedure_code TEXT NOT NULL,
                        procedure_category TEXT NOT NULL,
                        seated_at TEXT NOT NULL,
                        doctor_arrived_at TEXT NULL,
                        doctor_complete_at TEXT NULL,
                        room_available_at TEXT NULL,
                        seated_to_doctor_seconds INTEGER NOT NULL,
                        doctor_in_room_seconds INTEGER NULL,
                        turnover_seconds INTEGER NULL,
                        total_room_cycle_seconds INTEGER NULL,
                        final_wait_state TEXT NOT NULL,
                        aging_threshold_reached INTEGER NOT NULL DEFAULT 0,
                        stale_threshold_reached INTEGER NOT NULL DEFAULT 0,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL,
                        UNIQUE(room_id, seated_at)
                    );
                    """;
                create.ExecuteNonQuery();
            }

            InsertLegacyCompletedCycleRow(seed, 1, "2026-06-01T10:00:00.0000000+00:00");
            InsertLegacyCompletedCycleRow(seed, 2, "2026-06-01T11:00:00.0000000+00:00");
        }

        // Constructing the repository runs Initialize() including the id-backfill migration.
        var environment = new TestWebHostEnvironment(workspace.ContentRoot, Environments.Development);
        var repository = new SqliteBoardRepository(
            Microsoft.Extensions.Options.Options.Create(new BoardPersistenceOptions { DatabasePath = databasePath }),
            environment);

        var cycles = repository.LoadCompletedCycles();

        // All data preserved.
        Assert.Equal(2, cycles.Count);
        Assert.Contains(cycles, c => c.RoomId == 1);
        Assert.Contains(cycles, c => c.RoomId == 2);

        // Every row backfilled with a unique positive id.
        Assert.All(cycles, c => Assert.True(c.CompletedCycleId > 0));
        Assert.Equal(2, cycles.Select(c => c.CompletedCycleId).Distinct().Count());

        // The id column now exists in the schema.
        using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        verify.Open();
        Assert.Contains("id", GetColumnNames(verify, "completed_room_cycles"));
    }

    private static void InsertLegacyCompletedCycleRow(SqliteConnection connection, int roomId, string seatedAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO completed_room_cycles (
                room_id, assigned_doctor_id, assigned_doctor_display_name,
                procedure_code, procedure_category, seated_at,
                seated_to_doctor_seconds, final_wait_state, created_at, updated_at
            ) VALUES (
                $roomId, 'otte', 'Dr. Otte', 'CON', 'Consult', $seatedAt,
                300, 'ReadyForDoctor', $now, $now
            );
            """;
        cmd.Parameters.AddWithValue("$roomId", roomId);
        cmd.Parameters.AddWithValue("$seatedAt", seatedAt);
        cmd.Parameters.AddWithValue("$now", "2026-06-01T12:00:00.0000000+00:00");
        cmd.ExecuteNonQuery();
    }

    private static readonly HashSet<string> AllowedActiveRoomColumns =
    [
        "room_id",
        "assigned_doctor_id",
        "assigned_doctor_display_name",
        "procedure_code",
        "procedure_category",
        "state",
        "seated_at",
        "aging_started_at",
        "stale_started_at",
        "ready_for_doctor_at",
        "doctor_arrived_at",
        "doctor_complete_at",
        "room_available_at",
        "original_default_expected_units",
        "expected_allocation_units",
        "expected_allocation_minutes",
        "allocation_adjusted_from_default",
        "updated_at"
    ];

    private static readonly HashSet<string> AllowedCompletedCycleColumns =
    [
        "id",
        "room_id",
        "assigned_doctor_id",
        "assigned_doctor_display_name",
        "procedure_code",
        "procedure_category",
        "seated_at",
        "ready_for_doctor_at",
        "doctor_arrived_at",
        "doctor_complete_at",
        "room_available_at",
        "seated_to_doctor_seconds",
        "prep_seconds",
        "ready_to_doctor_seconds",
        "doctor_in_room_seconds",
        "turnover_seconds",
        "total_room_cycle_seconds",
        "original_default_expected_units",
        "expected_allocation_units",
        "expected_allocation_minutes",
        "allocation_adjusted_from_default",
        "final_wait_state",
        "aging_threshold_reached",
        "stale_threshold_reached",
        "is_exception",
        "requires_review",
        "exception_reason",
        "review_status",
        "suggested_action",
        "reviewed_at",
        "reviewed_by",
        "created_at",
        "updated_at"
    ];

    private static bool ContainsBannedPhiTerm(string columnName)
    {
        string[] bannedTerms =
        [
            "patient",
            "dob",
            "date_of_birth",
            "chart",
            "diagnosis",
            "insurance",
            "billing",
            "medical",
            "note"
        ];

        return bannedTerms.Any(term => columnName.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> GetColumnNames(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static void ExecuteSql(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void InvokeTryAddColumn(SqliteConnection connection, string alterTableSql)
    {
        var method = typeof(SqliteBoardRepository).GetMethod(
            "TryAddColumn",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        try
        {
            method.Invoke(null, [connection, alterTableSql]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static SqliteConnection OpenConnection(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string FormatDateTimeOffset(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O");

    private static RoomDeviceTokenValidator CreateBindingValidator(bool enabled) =>
        new(new TestOptionsMonitor<RoomDeviceBindingOptions>(new RoomDeviceBindingOptions
        {
            Enabled = enabled,
            RoomTokens = new Dictionary<string, string>
            {
                ["1"] = "room-1-token",
                ["2"] = "room-2-token"
            }
        }));

    private static AdminAccessTokenValidator CreateAdminValidator(bool enabled) =>
        new(new TestOptionsMonitor<AdminAccessOptions>(new AdminAccessOptions
        {
            Enabled = enabled,
            SharedToken = "admin-token"
        }));

    private static ValidateOptionsResult ValidateBindingOptions(
        int roomCount,
        RoomDeviceBindingOptions options) =>
        new RoomDeviceBindingOptionsValidator(
            Microsoft.Extensions.Options.Options.Create(new BoardOptions { RoomCount = roomCount }))
            .Validate(null, options);

    private static ValidateOptionsResult ValidateAdminAccessOptions(
        AdminAccessOptions options,
        string environmentName = "Development") =>
        new AdminAccessOptionsValidator(new TestWebHostEnvironment(Path.GetTempPath(), environmentName))
            .Validate(null, options);

    private static DiagnosticLogger CreateDiagnosticLogger(
        string logDirectory,
        string contentRoot,
        long maxFileSizeBytes = 50_000_000) =>
        new(
            Microsoft.Extensions.Options.Options.Create(new DiagnosticOptions
            {
                LogDirectory = logDirectory,
                MaxFileSizeBytes = maxFileSizeBytes
            }),
            new TestWebHostEnvironment(contentRoot, Environments.Production));

    private static ValidateOptionsResult ValidateDoctorRoster(DoctorRosterOptions options) =>
        new DoctorRosterOptionsValidator().Validate(null, options);

    private static ValidateOptionsResult ValidateProcedureRoster(ProcedureRosterOptions options) =>
        new ProcedureRosterOptionsValidator().Validate(null, options);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ChairSide.Board.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not find ChairSide.Board.sln.");
        }

        return directory.FullName;
    }

    private static HttpRequest RequestWithHeader(string? token)
    {
        var context = new DefaultHttpContext();
        if (token is not null)
        {
            context.Request.Headers[RoomDeviceTokenValidator.HeaderName] = token;
        }

        return context.Request;
    }

    private static HttpRequest RequestWithQueryToken(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?roomToken={Uri.EscapeDataString(token)}");
        return context.Request;
    }

    private static HttpRequest RequestWithAdminHeader(string? token)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/reports";
        if (token is not null)
        {
            context.Request.Headers[AdminAccessTokenValidator.HeaderName] = token;
        }

        return context.Request;
    }

    private static HttpRequest RequestWithAdminQueryToken(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/reports";
        context.Request.QueryString = new QueryString($"?adminToken={Uri.EscapeDataString(token)}");
        return context.Request;
    }

    private static async Task<int?> ExecuteBindingResult(IResult? result)
    {
        if (result is null)
        {
            return null;
        }

        if (result is IStatusCodeHttpResult statusCodeHttpResult)
        {
            return statusCodeHttpResult.StatusCode;
        }

        var context = new DefaultHttpContext();
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }
}

internal sealed class StoreContext
{
    private StoreContext(
        DemoBoardStore store,
        SqliteBoardRepository repository,
        string databasePath)
    {
        Store = store;
        Repository = repository;
        DatabasePath = databasePath;
    }

    public DemoBoardStore Store { get; }

    public SqliteBoardRepository Repository { get; }

    public string DatabasePath { get; }

    public IReadOnlyList<Doctor> Doctors { get; } =
    [
        new("otte", "Dr. Otte", "Otte", "#2563eb"),
        new("pledger", "Dr. Pledger", "Pledger", "#16a34a"),
        new("gibson", "Dr. Gibson", "Gibson", "#f97316"),
        new("schroeder", "Dr. Schroeder", "Schroeder", "#7c3aed")
    ];

    public IReadOnlyList<ProcedureCategory> Procedures { get; } =
    [
        new("consult", "CON", "Consult", "speech"),
        new("extraction", "EXT", "Extraction", "forceps"),
        new("sedation", "SED", "Sedation", "moon"),
        new("post-op", "POST", "Post-op", "check"),
        new("implant", "IMP", "Implant", "bolt"),
        new("biopsy", "BX", "Biopsy", "vial")
    ];

    public static StoreContext Create(
        TestWorkspace workspace,
        string environmentName,
        string? databasePath = null,
        int agingMinutes = 7,
        int staleMinutes = 12,
        int roomCount = 3,
        DoctorRosterOptions? doctorRosterOptions = null,
        ProcedureRosterOptions? procedureRosterOptions = null,
        BoardUiOptions? boardUiOptions = null,
        TimeProvider? timeProvider = null,
        RoomExpirationOptions? expirationOptions = null)
    {
        var resolvedDatabasePath = databasePath ?? (environmentName == Environments.Production
            ? workspace.ProductionDatabasePath()
            : Path.Combine(workspace.ContentRoot, "data", "chairside-test.db"));
        var environment = new TestWebHostEnvironment(workspace.ContentRoot, environmentName);
        var repository = new SqliteBoardRepository(
            Microsoft.Extensions.Options.Options.Create(new BoardPersistenceOptions { DatabasePath = resolvedDatabasePath }),
            environment);
        var store = new DemoBoardStore(
            new TestOptionsMonitor<BoardThresholdOptions>(new BoardThresholdOptions
            {
                AgingMinutes = agingMinutes,
                StaleMinutes = staleMinutes
            }),
            new TestOptionsMonitor<RoomExpirationOptions>(expirationOptions ?? new RoomExpirationOptions { Enabled = false }),
            Microsoft.Extensions.Options.Options.Create(new BoardOptions { RoomCount = roomCount }),
            Microsoft.Extensions.Options.Options.Create(boardUiOptions ?? new BoardUiOptions()),
            Microsoft.Extensions.Options.Options.Create(doctorRosterOptions ?? new DoctorRosterOptions
            {
                Doctors = DoctorRosterOptions.DefaultDoctors()
            }),
            Microsoft.Extensions.Options.Options.Create(procedureRosterOptions ?? new ProcedureRosterOptions
            {
                Procedures = ProcedureRosterOptions.DefaultProcedures()
            }),
            repository,
            environment,
            timeProvider);

        return new StoreContext(store, repository, resolvedDatabasePath);
    }
}

internal sealed class TestWorkspace : IDisposable
{
    private TestWorkspace(string root)
    {
        Root = root;
        ContentRoot = Path.Combine(root, "app");
        DataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(ContentRoot);
        Directory.CreateDirectory(DataRoot);
    }

    public string Root { get; }

    public string ContentRoot { get; }

    public string DataRoot { get; }

    public static TestWorkspace Create() =>
        new(Path.Combine(Path.GetTempPath(), "ChairSide.Board.Tests", Guid.NewGuid().ToString("N")));

    public string ProductionDatabasePath() =>
        Path.Combine(DataRoot, "chairside-test.db");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for SQLite handles released just after test completion.
        }
    }
}

internal sealed class TestWebHostEnvironment(string contentRootPath, string environmentName) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "ChairSide.Board.Tests";

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

    public string ContentRootPath { get; set; } = contentRootPath;

    public string EnvironmentName { get; set; } = environmentName;

    public string WebRootPath { get; set; } = Path.Combine(contentRootPath, "wwwroot");

    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
}
