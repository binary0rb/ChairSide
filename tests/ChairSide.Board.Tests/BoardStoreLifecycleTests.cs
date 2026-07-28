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
    public void Lifecycle_actions_preserve_expected_state_and_report_behavior()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var seated = SeatViaPrestage(context.Store, 1, "otte", "CON");
        Assert.NotNull(seated);
        Assert.Equal(RoomStates.Seated, seated.State);

        var seatedAt = seated.SeatedAt;
        var correctedAssignment = RoomAssignmentContract.Create(
            "pledger",
            "EXT",
            SedationContract.EligibleNo(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(3));
        var updated = context.Store.SaveAssignmentDetails(1, correctedAssignment);
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

        var reseated = SeatViaPrestage(context.Store, 1, "otte", "CON");
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
    public void Begin_prestage_succeeds_only_from_available_and_captures_snapshot()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        var prestaged = context.Store.BeginPrestage(1, "otte", "EXT", sedation: true, expectedAllocationUnits: 5);

        Assert.NotNull(prestaged);
        Assert.Equal(RoomStates.Prestaging, prestaged.State);
        Assert.Equal("otte", prestaged.AssignedDoctor);
        Assert.Equal("EXT+SED", prestaged.ProcedureCode);
        Assert.Null(prestaged.SeatedAt);
        Assert.Equal(3, prestaged.OriginalDefaultExpectedUnits);
        Assert.Equal(5, prestaged.ExpectedAllocationUnits);
        Assert.Equal(50, prestaged.ExpectedAllocationMinutes);
        Assert.True(prestaged.AllocationAdjustedFromDefault);

        var stored = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Prestaging, stored.State);
        Assert.False(string.IsNullOrWhiteSpace(stored.EpisodeId));
        Assert.Equal(now, stored.PrestageStartedAt);
        Assert.Equal("otte", stored.AssignedDoctor);
        Assert.Equal("EXT+SED", stored.ProcedureCode);
        Assert.Equal(3, stored.OriginalDefaultExpectedUnits);
        Assert.Equal(5, stored.ExpectedAllocationUnits);
        Assert.Equal(50, stored.ExpectedAllocationMinutes);
        Assert.True(stored.AllocationAdjustedFromDefault);

        Assert.Null(context.Store.BeginPrestage(1, "pledger", "CON"));
        var afterRejectedSecondBegin = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(stored.EpisodeId, afterRejectedSecondBegin.EpisodeId);
        Assert.Equal("otte", afterRejectedSecondBegin.AssignedDoctor);
        Assert.Equal("EXT+SED", afterRejectedSecondBegin.ProcedureCode);
    }

    [Fact]
    public void Begin_prestage_invalid_inputs_leave_room_unchanged()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.Null(context.Store.BeginPrestage(1, "missing", "CON"));
        Assert.Null(context.Store.BeginPrestage(1, "otte", "NOPE"));
        Assert.Null(context.Store.BeginPrestage(1, "otte", "CON", sedation: true));

        var unchanged = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Available, unchanged.State);
        Assert.Null(unchanged.EpisodeId);
        Assert.Null(unchanged.PrestageStartedAt);
        Assert.Null(unchanged.AssignedDoctor);
        Assert.Null(unchanged.ProcedureCode);

        var invalidAllocationContext = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: Path.Combine(workspace.DataRoot, "invalid-allocation.db"),
            procedureRosterOptions: new ProcedureRosterOptions
            {
                Procedures =
                [
                    new()
                    {
                        Id = "bad-allocation",
                        Code = "BAD",
                        Label = "Bad allocation",
                        Icon = "speech",
                        Active = true,
                        DefaultExpectedUnits = 0
                    }
                ]
            });

        Assert.Null(invalidAllocationContext.Store.BeginPrestage(1, "otte", "BAD"));
        var afterInvalidAllocation = invalidAllocationContext.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Available, afterInvalidAllocation.State);
        Assert.Null(afterInvalidAllocation.EpisodeId);
        Assert.Null(afterInvalidAllocation.PrestageStartedAt);
        Assert.Null(afterInvalidAllocation.AssignedDoctor);
        Assert.Null(afterInvalidAllocation.ProcedureCode);
    }

    [Fact]
    public void Prestaging_survives_store_restart_reconstruction()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath, timeProvider: clock);
        Assert.NotNull(first.Store.BeginPrestage(1, "otte", "CON"));
        var persisted = first.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath, timeProvider: clock);
        var reloadedStatus = second.Store.GetRoom(1);
        var reloadedRoom = second.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

        Assert.NotNull(reloadedStatus);
        Assert.Equal(RoomStates.Prestaging, reloadedStatus.State);
        Assert.Equal(RoomStates.Prestaging, reloadedRoom.State);
        Assert.Equal(persisted.EpisodeId, reloadedRoom.EpisodeId);
        Assert.Equal(now, reloadedRoom.PrestageStartedAt);
        Assert.Null(reloadedRoom.SeatedAt);
    }

    [Fact]
    public void Seat_room_rejects_available_and_accepts_prestaging_without_replacing_snapshot()
    {
        using var workspace = TestWorkspace.Create();
        var prestageAt = new DateTimeOffset(2026, 7, 1, 11, 0, 0, TimeSpan.Zero);
        var seatAt = prestageAt.AddMinutes(6);
        var clock = new ManualTimeProvider(prestageAt);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        Assert.Null(context.Store.SeatRoomCanonical(1, null).Room);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "EXT", sedation: true, expectedAllocationUnits: 5));
        var beforeSeat = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

        clock.SetUtcNow(seatAt);
        var seated = context.Store.SeatRoomCanonical(1, null).Room;

        Assert.NotNull(seated);
        Assert.Equal(RoomStates.Seated, seated.State);
        Assert.Equal(seatAt, seated.SeatedAt);

        var afterSeat = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(beforeSeat.EpisodeId, afterSeat.EpisodeId);
        Assert.Equal(prestageAt, afterSeat.PrestageStartedAt);
        Assert.Equal(seatAt, afterSeat.SeatedAt);
        Assert.Equal("otte", afterSeat.AssignedDoctor);
        Assert.Equal("EXT+SED", afterSeat.ProcedureCode);
        Assert.Equal(3, afterSeat.OriginalDefaultExpectedUnits);
        Assert.Equal(5, afterSeat.ExpectedAllocationUnits);
        Assert.Equal(50, afterSeat.ExpectedAllocationMinutes);
        Assert.True(afterSeat.AllocationAdjustedFromDefault);
    }

    [Fact]
    public void Fixed_clock_new_episodes_receive_distinct_episode_ids()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        Assert.NotNull(context.Store.BeginPrestage(2, "pledger", "EXT"));

        var rooms = context.Repository.LoadRooms(3);
        var room1Episode = rooms.Single(room => room.RoomId == 1).EpisodeId;
        var room2Episode = rooms.Single(room => room.RoomId == 2).EpisodeId;

        Assert.False(string.IsNullOrWhiteSpace(room1Episode));
        Assert.False(string.IsNullOrWhiteSpace(room2Episode));
        Assert.NotEqual(room1Episode, room2Episode);
    }

    [Fact]
    public void Legacy_active_rows_without_episode_or_prestage_continue_normally()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 1, 15, 0, 0, TimeSpan.Zero);
        var seatedAt = now.AddMinutes(-10);
        var readyAt = now.AddMinutes(-2);

        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        seed.Repository.SaveRooms(
            [
                new RoomState(1)
                {
                    AssignedDoctor = "otte",
                    ProcedureCode = "CON",
                    State = RoomStates.Seated,
                    SeatedAt = seatedAt
                },
                new RoomState(2)
                {
                    AssignedDoctor = "pledger",
                    ProcedureCode = "EXT",
                    State = RoomStates.ReadyForDoctor,
                    SeatedAt = seatedAt,
                    ReadyForDoctorAt = readyAt
                }
            ],
            seed.Doctors,
            seed.Procedures);

        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath, timeProvider: clock);

        var seated = context.Store.GetRoom(1);
        var ready = context.Store.GetRoom(2);
        Assert.NotNull(seated);
        Assert.NotNull(ready);
        Assert.Equal(RoomStates.Seated, seated.State);
        Assert.Equal(RoomStates.ReadyForDoctor, ready.State);

        var storedRooms = context.Repository.LoadRooms(3);
        Assert.Null(storedRooms.Single(room => room.RoomId == 1).EpisodeId);
        Assert.Null(storedRooms.Single(room => room.RoomId == 1).PrestageStartedAt);
        Assert.Null(storedRooms.Single(room => room.RoomId == 2).EpisodeId);
        Assert.Null(storedRooms.Single(room => room.RoomId == 2).PrestageStartedAt);

        Assert.Null(context.Store.MarkReadyForDoctor(1));
        Assert.Contains(ready.IntegrityFaults!, fault => fault.Code == RoomIntegrityFaultCode.ReadyHandoffMissing);
        Assert.Null(context.Store.MarkDoctorArrived(2));
        Assert.NotNull(context.Store.CancelSeating(2));
    }

    [Fact]
    public void Normally_completed_new_episode_persists_episode_id_and_prestage_started_at()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var prestageAt = new DateTimeOffset(2026, 7, 1, 16, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(prestageAt);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath, timeProvider: clock);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var activeEpisode = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1).EpisodeId;
        clock.SetUtcNow(prestageAt.AddMinutes(5));
        Assert.NotNull(context.Store.SeatRoomCanonical(1, null).Room);
        clock.SetUtcNow(prestageAt.AddMinutes(8));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(prestageAt.AddMinutes(12));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.Equal(activeEpisode, cycle.EpisodeId);
        Assert.Equal(prestageAt, cycle.PrestageStartedAt);

        var reloaded = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reloadedCycle = Assert.Single(reloaded.Repository.LoadCompletedCycles());
        Assert.Equal(activeEpisode, reloadedCycle.EpisodeId);
        Assert.Equal(prestageAt, reloadedCycle.PrestageStartedAt);
    }

    [Fact]
    public void Cancel_prestage_persists_full_abort_snapshot_and_resets_room()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var prestageAt = new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero);
        var terminatedAt = prestageAt.AddMinutes(4);
        var clock = new ManualTimeProvider(prestageAt);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);

        var prestaged = context.Store.BeginPrestage(1, "otte", "EXT", expectedAllocationUnits: 5);
        Assert.NotNull(prestaged);
        var activeSnapshot = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var episodeId = activeSnapshot.EpisodeId;
        Assert.Equal("Dr. Otte", activeSnapshot.AssignedDoctorDisplayName);
        Assert.Equal("Extraction", activeSnapshot.ProcedureCategory);
        clock.SetUtcNow(terminatedAt);

        var canceled = context.Store.CancelPrestage(1, CancellationReasons.MovedRoom);

        Assert.NotNull(canceled);
        Assert.Equal(RoomStates.Available, canceled.State);
        var abort = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(episodeId, abort.EpisodeId);
        Assert.Equal(1, abort.RoomId);
        Assert.Equal("otte", abort.AssignedDoctor);
        Assert.Equal("Dr. Otte", abort.AssignedDoctorDisplayName);
        Assert.Equal("EXT", abort.ProcedureCode);
        Assert.Equal("Extraction", abort.ProcedureCategory);
        Assert.Equal(3, abort.OriginalDefaultExpectedUnits);
        Assert.Equal(5, abort.ExpectedAllocationUnits);
        Assert.Equal(50, abort.ExpectedAllocationMinutes);
        Assert.True(abort.AllocationAdjustedFromDefault);
        Assert.Equal(prestageAt, abort.PrestageStartedAt);
        Assert.Null(abort.SeatedAt);
        Assert.Null(abort.ReadyForDoctorAt);
        Assert.Equal(terminatedAt, abort.TerminatedAt);
        Assert.Equal(RoomStates.Prestaging, abort.TerminatedFromState);
        Assert.Equal(TerminationKinds.StaffCanceled, abort.TerminationKind);
        Assert.Equal(CancellationReasons.MovedRoom, abort.CancellationReason);
        Assert.Empty(context.Repository.LoadCompletedCycles());
        Assert.Contains(context.Store.GetSnapshot().RecentEvents, item => item.EventType == "PrestageCanceled");

        using (var connection = OpenConnection(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT assigned_doctor_display_name, procedure_category FROM aborted_room_assignments;";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("Dr. Otte", reader.GetString(0));
            Assert.Equal("Extraction", reader.GetString(1));
            Assert.False(reader.Read());
        }

        Assert.Null(context.Store.CancelPrestage(1));
        Assert.Single(context.Repository.LoadAbortedAssignments());

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);
        var reloadedRoom = reloaded.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Available, reloadedRoom.State);
        Assert.Null(reloadedRoom.EpisodeId);
        Assert.Null(reloadedRoom.AssignedDoctor);
        Assert.Null(reloadedRoom.ProcedureCode);
        Assert.Null(reloadedRoom.PrestageStartedAt);
        Assert.Null(reloadedRoom.SeatedAt);
        Assert.Equal(episodeId, Assert.Single(reloaded.Repository.LoadAbortedAssignments()).EpisodeId);
        Assert.Empty(reloaded.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Composite_sedation_cancellation_persists_canonical_procedure_category()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "EXT", sedation: true));
        var active = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal("EXT+SED", active.ProcedureCode);
        Assert.Equal("Extraction + Sedation", active.ProcedureCategory);

        Assert.NotNull(context.Store.CancelPrestage(1));

        var abort = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal("EXT+SED", abort.ProcedureCode);
        Assert.Equal("Extraction + Sedation", abort.ProcedureCategory);
        using var connection = OpenConnection(context.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT procedure_category FROM aborted_room_assignments;";
        Assert.Equal("Extraction + Sedation", (string)command.ExecuteScalar()!);
    }

    [Fact]
    public void Cancellation_retains_begin_prestage_display_snapshots_after_roster_change()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var originalDoctors = new DoctorRosterOptions
        {
            Doctors =
            [
                new()
                {
                    Id = "otte",
                    DisplayName = "Dr. Original Otte",
                    ShortName = "Otte",
                    Color = "#dc2626",
                    Active = true
                }
            ]
        };
        var originalProcedures = new ProcedureRosterOptions
        {
            Procedures =
            [
                new()
                {
                    Id = "consult",
                    Code = "CON",
                    Label = "Original Consult",
                    Icon = "speech",
                    Active = true,
                    DefaultExpectedUnits = 1
                }
            ]
        };
        var first = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            doctorRosterOptions: originalDoctors,
            procedureRosterOptions: originalProcedures);
        Assert.NotNull(first.Store.BeginPrestage(1, "otte", "CON"));

        var changedDoctors = new DoctorRosterOptions
        {
            Doctors =
            [
                new()
                {
                    Id = "otte",
                    DisplayName = "Dr. Renamed Otte",
                    ShortName = "Renamed",
                    Color = "#dc2626",
                    Active = true
                }
            ]
        };
        var changedProcedures = new ProcedureRosterOptions
        {
            Procedures =
            [
                new()
                {
                    Id = "consult",
                    Code = "CON",
                    Label = "Renamed Consult",
                    Icon = "speech",
                    Active = true,
                    DefaultExpectedUnits = 1
                }
            ]
        };
        var second = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            doctorRosterOptions: changedDoctors,
            procedureRosterOptions: changedProcedures);
        var loaded = second.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal("Dr. Original Otte", loaded.AssignedDoctorDisplayName);
        Assert.Equal("Original Consult", loaded.ProcedureCategory);

        Assert.NotNull(second.Store.CancelPrestage(1));

        var abort = Assert.Single(second.Repository.LoadAbortedAssignments());
        Assert.Equal("Dr. Original Otte", abort.AssignedDoctorDisplayName);
        Assert.Equal("Original Consult", abort.ProcedureCategory);
    }

    [Fact]
    public void Cancel_seating_persists_complete_new_episode_snapshot_and_reset_across_reload()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var prestageAt = new DateTimeOffset(2026, 7, 2, 9, 30, 0, TimeSpan.Zero);
        var seatedAt = prestageAt.AddMinutes(3);
        var readyAt = prestageAt.AddMinutes(5);
        var terminatedAt = prestageAt.AddMinutes(8);
        var clock = new ManualTimeProvider(prestageAt);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "EXT", expectedAllocationUnits: 5));
        var episodeId = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1).EpisodeId;
        clock.SetUtcNow(seatedAt);
        Assert.NotNull(context.Store.SeatRoomCanonical(1, null).Room);
        clock.SetUtcNow(readyAt);
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(terminatedAt);

        Assert.NotNull(context.Store.CancelSeating(1, CancellationReasons.ProcedureChanged));

        var abort = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(episodeId, abort.EpisodeId);
        Assert.Equal(1, abort.RoomId);
        Assert.Equal("otte", abort.AssignedDoctor);
        Assert.Equal("Dr. Otte", abort.AssignedDoctorDisplayName);
        Assert.Equal("EXT", abort.ProcedureCode);
        Assert.Equal("Extraction", abort.ProcedureCategory);
        Assert.Equal(3, abort.OriginalDefaultExpectedUnits);
        Assert.Equal(5, abort.ExpectedAllocationUnits);
        Assert.Equal(50, abort.ExpectedAllocationMinutes);
        Assert.True(abort.AllocationAdjustedFromDefault);
        Assert.Equal(prestageAt, abort.PrestageStartedAt);
        Assert.Equal(seatedAt, abort.SeatedAt);
        Assert.Equal(readyAt, abort.ReadyForDoctorAt);
        Assert.Equal(terminatedAt, abort.TerminatedAt);
        Assert.Equal(RoomStates.ReadyForDoctor, abort.TerminatedFromState);
        Assert.Equal(TerminationKinds.StaffCanceled, abort.TerminationKind);
        Assert.Equal(CancellationReasons.ProcedureChanged, abort.CancellationReason);
        Assert.Empty(context.Repository.LoadCompletedCycles());

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);
        var room = reloaded.Repository.LoadRooms(3).Single(item => item.RoomId == 1);
        Assert.Equal(RoomStates.Available, room.State);
        Assert.Null(room.EpisodeId);
        Assert.Null(room.AssignedDoctor);
        Assert.Null(room.AssignedDoctorDisplayName);
        Assert.Null(room.ProcedureCode);
        Assert.Null(room.ProcedureCategory);
        Assert.Null(room.PrestageStartedAt);
        Assert.Null(room.SeatedAt);
        var durableAbort = Assert.Single(reloaded.Repository.LoadAbortedAssignments());
        AssertSameAbortedAssignment(abort, durableAbort);
        Assert.Empty(reloaded.Repository.LoadCompletedCycles());
    }

    [Theory]
    [InlineData(RoomStates.Available)]
    [InlineData(RoomStates.Seated)]
    [InlineData(RoomStates.ReadyForDoctor)]
    [InlineData(RoomStates.Aging)]
    [InlineData(RoomStates.Stale)]
    [InlineData(RoomStates.DoctorInRoom)]
    [InlineData(RoomStates.Turnover)]
    public void Cancel_prestage_rejects_available_and_post_prestage_states(string state)
    {
        using var workspace = TestWorkspace.Create();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero));
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);
        DriveRoomToCancellationState(context, clock, state);

        Assert.Null(context.Store.CancelPrestage(1));
        Assert.Equal(
            state is RoomStates.Aging or RoomStates.Stale ? RoomStates.ReadyForDoctor : state,
            context.Store.GetRoom(1)?.State);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
    }

    [Theory]
    [InlineData(RoomStates.Seated)]
    [InlineData(RoomStates.ReadyForDoctor)]
    [InlineData(RoomStates.Aging)]
    [InlineData(RoomStates.Stale)]
    public void Cancel_seating_persists_exact_allowed_pre_arrival_state(string state)
    {
        using var workspace = TestWorkspace.Create();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 2, 11, 0, 0, TimeSpan.Zero));
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);
        DriveRoomToCancellationState(context, clock, state);
        var episodeId = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1).EpisodeId;

        var canceled = context.Store.CancelSeating(1);

        Assert.NotNull(canceled);
        Assert.Equal(RoomStates.Available, canceled.State);
        var abort = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(episodeId, abort.EpisodeId);
        Assert.Equal(
            state is RoomStates.Aging or RoomStates.Stale ? RoomStates.ReadyForDoctor : state,
            abort.TerminatedFromState);
        Assert.Equal(TerminationKinds.StaffCanceled, abort.TerminationKind);
        Assert.Null(abort.CancellationReason);
        Assert.NotNull(abort.PrestageStartedAt);
        Assert.NotNull(abort.SeatedAt);
        Assert.Equal(state == RoomStates.Seated, abort.ReadyForDoctorAt is null);
        Assert.Empty(context.Repository.LoadCompletedCycles());
    }

    [Theory]
    [InlineData(RoomStates.Available)]
    [InlineData(RoomStates.DoctorInRoom)]
    [InlineData(RoomStates.Turnover)]
    public void Cancel_seating_rejects_available_and_at_or_after_doctor_arrived(string state)
    {
        using var workspace = TestWorkspace.Create();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 2, 12, 0, 0, TimeSpan.Zero));
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);
        DriveRoomToCancellationState(context, clock, state);

        Assert.Null(context.Store.CancelSeating(1));
        Assert.Equal(state, context.Store.GetRoom(1)?.State);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(true, " ")]
    [InlineData(true, "UnknownReason")]
    [InlineData(false, "")]
    [InlineData(false, " ")]
    [InlineData(false, "UnknownReason")]
    public void Cancellation_rejects_blank_or_unknown_reason_without_mutation(bool prestage, string reason)
    {
        using var workspace = TestWorkspace.Create();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 2, 13, 0, 0, TimeSpan.Zero));
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);
        DriveRoomToCancellationState(context, clock, prestage ? RoomStates.Prestaging : RoomStates.Seated);
        var before = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

        var result = prestage
            ? context.Store.CancelPrestage(1, reason)
            : context.Store.CancelSeating(1, reason);

        Assert.Null(result);
        AssertSameRoomState(before, context.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
        Assert.Equal(before.State, context.Store.GetRoom(1)?.State);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
    }

    [Fact]
    public void Legacy_ready_room_cancellation_mints_episode_id_without_prestage_timestamp()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seatedAt = new DateTimeOffset(2026, 7, 2, 14, 0, 0, TimeSpan.Zero);
        var readyAt = seatedAt.AddMinutes(5);
        var now = readyAt.AddMinutes(1);
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        seed.Repository.SaveRooms(
            [
                new RoomState(1)
                {
                    AssignedDoctor = "otte",
                    ProcedureCode = "EXT",
                    State = RoomStates.ReadyForDoctor,
                    SeatedAt = seatedAt,
                    ReadyForDoctorAt = readyAt,
                    OriginalDefaultExpectedUnits = 3,
                    ExpectedAllocationUnits = 4,
                    ExpectedAllocationMinutes = 40,
                    AllocationAdjustedFromDefault = true
                }
            ],
            seed.Doctors,
            seed.Procedures);
        using (var connection = OpenConnection(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE active_rooms SET assigned_doctor_display_name = NULL, procedure_category = NULL WHERE room_id = 1;";
            command.ExecuteNonQuery();
        }

        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);
        var roomsField = typeof(DemoBoardStore).GetField(
            "_rooms",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var liveRooms = Assert.IsType<List<RoomState>>(roomsField?.GetValue(context.Store));
        var legacyRoom = liveRooms.Single(room => room.RoomId == 1);
        Assert.Null(legacyRoom.AssignedDoctorDisplayName);
        Assert.Null(legacyRoom.ProcedureCategory);
        var persistedLegacyRoom = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Null(persistedLegacyRoom.AssignedDoctorDisplayName);
        Assert.Null(persistedLegacyRoom.ProcedureCategory);

        Assert.NotNull(context.Store.CancelSeating(1, CancellationReasons.NoShow));

        var abort = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.False(string.IsNullOrWhiteSpace(abort.EpisodeId));
        Assert.Equal("Dr. Otte", abort.AssignedDoctorDisplayName);
        Assert.Equal("Extraction", abort.ProcedureCategory);
        Assert.Null(abort.PrestageStartedAt);
        Assert.Equal(seatedAt, abort.SeatedAt);
        Assert.Equal(readyAt, abort.ReadyForDoctorAt);
        Assert.Equal(RoomStates.ReadyForDoctor, abort.TerminatedFromState);
        Assert.Equal(CancellationReasons.NoShow, abort.CancellationReason);
        Assert.Equal(3, abort.OriginalDefaultExpectedUnits);
        Assert.Equal(4, abort.ExpectedAllocationUnits);
        Assert.Equal(40, abort.ExpectedAllocationMinutes);
        Assert.True(abort.AllocationAdjustedFromDefault);
        Assert.Empty(context.Repository.LoadCompletedCycles());

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);
        Assert.Equal(RoomStates.Available, reloaded.Store.GetRoom(1)?.State);
        Assert.Equal(abort.EpisodeId, Assert.Single(reloaded.Repository.LoadAbortedAssignments()).EpisodeId);
    }

    [Fact]
    public void Active_seated_room_survives_store_restart()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var seated = SeatViaPrestage(first.Store, 1, "otte", "CON");
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
        Assert.NotNull(SeatViaPrestage(first.Store, 1, "otte", "CON"));
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
    public void Room_available_atomically_finalizes_cycle_and_available_room_across_reload()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var prestageAt = new DateTimeOffset(2026, 7, 4, 9, 0, 0, TimeSpan.Zero);
        var seatedAt = prestageAt.AddMinutes(2);
        var readyAt = prestageAt.AddMinutes(5);
        var arrivedAt = prestageAt.AddMinutes(10);
        var completedAt = prestageAt.AddMinutes(20);
        var availableAt = prestageAt.AddMinutes(25);
        var clock = new ManualTimeProvider(prestageAt);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "EXT", expectedAllocationUnits: 5));
        clock.SetUtcNow(seatedAt);
        Assert.NotNull(context.Store.SeatRoomCanonical(1, null).Room);
        clock.SetUtcNow(readyAt);
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(arrivedAt);
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        clock.SetUtcNow(completedAt);
        Assert.NotNull(context.Store.MarkDoctorComplete(1));

        var beforeAvailable = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.True(beforeAvailable.CompletedCycleId > 0);
        Assert.Null(beforeAvailable.RoomAvailableAt);
        var completedCycleId = beforeAvailable.CompletedCycleId;
        var episodeId = beforeAvailable.EpisodeId;
        var storedPrestageAt = beforeAvailable.PrestageStartedAt;

        clock.SetUtcNow(availableAt);
        var available = context.Store.MarkRoomAvailable(1);

        Assert.NotNull(available);
        Assert.Equal(RoomStates.Available, available.State);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        var tracked = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(completedCycleId, tracked.CompletedCycleId);
        Assert.Equal(episodeId, tracked.EpisodeId);
        Assert.Equal(storedPrestageAt, tracked.PrestageStartedAt);
        Assert.Equal(availableAt, tracked.RoomAvailableAt);
        Assert.Equal(5 * 60, tracked.TurnoverSeconds);
        Assert.Equal((int)(availableAt - seatedAt).TotalSeconds, tracked.TotalRoomCycleSeconds);

        var persisted = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.Equal(completedCycleId, persisted.CompletedCycleId);
        Assert.Equal(episodeId, persisted.EpisodeId);
        Assert.Equal(storedPrestageAt, persisted.PrestageStartedAt);
        Assert.Equal(availableAt, persisted.RoomAvailableAt);

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);
        Assert.Equal(RoomStates.Available, reloaded.Store.GetRoom(1)?.State);
        var durableCycle = Assert.Single(reloaded.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(completedCycleId, durableCycle.CompletedCycleId);
        Assert.Equal(episodeId, durableCycle.EpisodeId);
        Assert.Equal(storedPrestageAt, durableCycle.PrestageStartedAt);
        Assert.Equal(availableAt, durableCycle.RoomAvailableAt);
    }

    [Fact]
    public void Legacy_cycle_with_null_episode_and_prestage_completes_normally()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 4, 10, 0, 0, TimeSpan.Zero);
        var availableAt = now.AddMinutes(5);
        var clock = new ManualTimeProvider(now);
        var first = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);

        Assert.NotNull(first.Store.BeginPrestage(1, "otte", "CON"));
        Assert.NotNull(first.Store.SeatRoomCanonical(1, null).Room);
        Assert.NotNull(first.Store.MarkReadyForDoctor(1));
        Assert.NotNull(first.Store.MarkDoctorArrived(1));
        Assert.NotNull(first.Store.MarkDoctorComplete(1));
        var cycleId = Assert.Single(first.Repository.LoadCompletedCycles()).CompletedCycleId;

        using (var connection = OpenConnection(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE active_rooms
                SET episode_id = NULL,
                    prestage_started_at = NULL,
                    sedation_state = NULL,
                    expected_allocation_state = NULL,
                    expected_allocation_suggested_units = NULL,
                    expected_allocation_confirmed_units = NULL,
                    active_ready_handoff_id = NULL,
                    accepted_ready_handoff_id = NULL
                WHERE room_id = 1;

                UPDATE completed_room_cycles
                SET episode_id = NULL, prestage_started_at = NULL, accepted_ready_handoff_id = NULL
                WHERE id = $cycleId;
                """;
            command.Parameters.AddWithValue("$cycleId", cycleId);
            command.ExecuteNonQuery();
        }

        clock.SetUtcNow(availableAt);
        var legacy = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);

        Assert.NotNull(legacy.Store.MarkRoomAvailable(1));
        Assert.Equal(RoomStates.Available, legacy.Store.GetRoom(1)?.State);
        var cycle = Assert.Single(legacy.Repository.LoadCompletedCycles());
        Assert.Equal(cycleId, cycle.CompletedCycleId);
        Assert.Null(cycle.EpisodeId);
        Assert.Null(cycle.PrestageStartedAt);
        Assert.Equal(availableAt, cycle.RoomAvailableAt);
    }

    [Fact]
    public void Room_available_rejects_repeat_after_reset_without_duplicate_completed_cycle()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));
        var completedCycleId = Assert.Single(context.Repository.LoadCompletedCycles()).CompletedCycleId;

        Assert.Null(context.Store.MarkRoomAvailable(1));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        var persisted = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.Equal(completedCycleId, persisted.CompletedCycleId);
    }

    [Fact]
    public void Stale_elapsed_ready_room_reloads_as_ready_with_stale_urgency()
    {
        // Stale urgency is based on ReadyForDoctorAt. A room seated for a long time without Ready
        // stays Seated; only the Ready phase projects Aging/Stale urgency.
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();

        var first = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            agingMinutes: 1,
            staleMinutes: 2,
            boardUiOptions: new BoardUiOptions { DemoTimerEnabled = true });
        Assert.NotNull(SeatViaPrestage(first.Store, 1, "otte", "CON"));
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

                UPDATE ready_handoffs
                SET ready_at = $readyAt
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
        Assert.Equal(RoomStates.ReadyForDoctor, reloaded.State);
        Assert.Equal(ReadyUrgency.Stale, reloaded.ReadyUrgency);
        Assert.Null(reloaded.AgingStartedAt);
        Assert.Null(reloaded.StaleStartedAt);

        Assert.NotNull(second.Store.MarkDoctorArrived(1));
        Assert.NotNull(second.Store.MarkDoctorComplete(1));
        Assert.NotNull(second.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(second.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(RoomStates.ReadyForDoctor, cycle.FinalWaitState);
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
        Assert.NotNull(SeatViaPrestage(first.Store, 1, "otte", "CON"));
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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

        var seated = SeatViaPrestage(context.Store, 1, "otte", "CON");
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(now.AddMinutes(elapsedMinutes).AddSeconds(elapsedSeconds));

        var room = context.Store.GetRoom(1);

        Assert.NotNull(room);
        Assert.Equal(RoomStates.ReadyForDoctor, room.State);
        Assert.Equal(
            expectedState == RoomStates.Stale
                ? ReadyUrgency.Stale
                : expectedState == RoomStates.Aging
                    ? ReadyUrgency.Aging
                    : ReadyUrgency.None,
            room.ReadyUrgency);
        Assert.Equal(expectedAgingStarted, room.ReadyUrgency is ReadyUrgency.Aging or ReadyUrgency.Stale);
        Assert.Equal(expectedStaleStarted, room.ReadyUrgency == ReadyUrgency.Stale);
        Assert.False(room.AgingStartedAt.HasValue);
        Assert.False(room.StaleStartedAt.HasValue);
    }

}
