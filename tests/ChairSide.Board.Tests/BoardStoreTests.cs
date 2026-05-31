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
    public void Stale_elapsed_seated_room_reloads_as_stale_before_doctor_arrived_report()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();

        var first = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            agingMinutes: 1,
            staleMinutes: 2);
        Assert.NotNull(first.Store.SeatRoom(1, "otte", "CON"));

        var staleSeatedAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        using (var connection = OpenConnection(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE active_rooms
                SET state = 'seated',
                    seated_at = $seatedAt,
                    aging_started_at = NULL,
                    stale_started_at = NULL
                WHERE room_id = 1;
                """;
            command.Parameters.AddWithValue("$seatedAt", FormatDateTimeOffset(staleSeatedAt));
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

    [Theory]
    [InlineData(6, 59, RoomStates.Seated, false, false)]
    [InlineData(7, 0, RoomStates.Aging, true, false)]
    [InlineData(11, 59, RoomStates.Aging, true, false)]
    [InlineData(12, 0, RoomStates.Stale, true, true)]
    public void Threshold_boundaries_resolve_deterministically(
        int elapsedMinutes,
        int elapsedSeconds,
        string expectedState,
        bool expectedAgingStarted,
        bool expectedStaleStarted)
    {
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
        Assert.Equal(["CON", "EXT", "SED", "POST", "IMP", "BX"], snapshot.Procedures.Select(procedure => procedure.Label));
        Assert.Equal(["Consult", "Extraction", "Sedation", "Post-op", "Implant", "Biopsy"], snapshot.Procedures.Select(procedure => procedure.Name));
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
                    new() { Id = "active-procedure", Code = "ACT", Label = "Active", Icon = "speech", Active = true },
                    new() { Id = "inactive-procedure", Code = "OFF", Label = "Inactive", Icon = "vial", Active = false }
                ]
            });

        var snapshot = context.Store.GetSnapshot();

        Assert.Equal(["active"], snapshot.Doctors.Select(doctor => doctor.Id));
        Assert.Equal(["ACT"], snapshot.Procedures.Select(procedure => procedure.Label));
        Assert.Null(context.Store.SeatRoom(1, "inactive", "ACT"));
        Assert.Null(context.Store.SeatRoom(1, "active", "OFF"));
        Assert.NotNull(context.Store.SeatRoom(1, "active", "ACT"));
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
        Assert.Contains("${escapeHtml(procedure.label)}", boardJs);
        Assert.Contains("${escapeHtml(procedure.name)}", boardJs);
        Assert.DoesNotContain(">${doctor.name}", boardJs);
        Assert.DoesNotContain("<strong>${procedure.label}</strong>", boardJs);
        Assert.DoesNotContain("<small>${procedure.name}</small>", boardJs);
    }

    [Fact]
    public void Persisted_schema_contains_only_non_phi_operational_fields()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        Assert.NotNull(context.Store.SeatRoom(1, "otte", "CON"));
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
    public void Admin_access_protects_reports_and_keeps_board_room_surfaces_open()
    {
        Assert.True(AdminAccessGuard.IsProtectedPath("/reports.html"));
        Assert.True(AdminAccessGuard.IsProtectedPath("/api/reports"));

        Assert.False(AdminAccessGuard.IsProtectedPath("/"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/master.html"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/doctor.html"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/room.html"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/room-1.html"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/api/board"));
        Assert.False(AdminAccessGuard.IsProtectedPath("/api/rooms/1"));
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
        "doctor_arrived_at",
        "doctor_complete_at",
        "room_available_at",
        "seated_to_doctor_seconds",
        "doctor_in_room_seconds",
        "turnover_seconds",
        "total_room_cycle_seconds",
        "final_wait_state",
        "aging_threshold_reached",
        "stale_threshold_reached",
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
