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
        Assert.Equal(
            ["CON", "EXT", "SED", "POST", "IMP", "BX", "MISC", "POE", "IMPRES", "INTCK", "BXPOST", "IMPRM", "PCOC"],
            snapshot.Procedures.Select(procedure => procedure.Code));
        Assert.Equal(
            ["Consult", "Extraction", "Sedation", "Post-op", "Implant", "Biopsy",
             "Misc", "Periodic Exam", "Impressions", "Integration Check",
             "Biopsy Post-op", "Implant Removal", "Phone → Office Consult"],
            snapshot.Procedures.Select(procedure => procedure.Label));
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
        var requiredIcons = new[] { "speech", "forceps", "moon", "check", "bolt", "vial", "teeth", "interlock", "wrench", "phone" };
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
        // Same SHA-256 hash normalisation as admin tokens — length of the submitted
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
        // Directory does not exist yet — logger must create it.
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
        // Unknown/proxied source IPs must never be blocked — they cannot be rate-limited
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

        // Cycle A: full lifecycle on room 1 — should appear in normal metrics.
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        // Cycle B: only reached DoctorArrived on room 2 — will be marked exception.
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

        // Cycle A: complete lifecycle → normal.
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        var arrivedA = context.Store.MarkDoctorArrived(1);
        Assert.NotNull(arrivedA);

        // Cycle B: only reached DoctorArrived — will be marked exception.
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

        // Reload the store — simulates a server restart.
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
        TimeProvider? timeProvider = null)
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
