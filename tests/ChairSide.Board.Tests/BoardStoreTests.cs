using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

using ChairSide.Board.Hubs;
using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.SignalR;

namespace ChairSide.Board.Tests;

public sealed class BoardStoreTests
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

        Assert.Null(context.Store.SeatRoom(1));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "EXT", sedation: true, expectedAllocationUnits: 5));
        var beforeSeat = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

        clock.SetUtcNow(seatAt);
        var seated = context.Store.SeatRoom(1);

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
    public void Development_demo_offset_shifts_prestage_and_seated_timestamps_together()
    {
        using var workspace = TestWorkspace.Create();
        var prestageAt = new DateTimeOffset(2026, 7, 1, 13, 0, 0, TimeSpan.Zero);
        var seatAt = prestageAt.AddMinutes(4);
        var clock = new ManualTimeProvider(prestageAt);
        var context = StoreContext.Create(workspace, environmentName: Environments.Development, timeProvider: clock);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        clock.SetUtcNow(seatAt);

        var seated = context.Store.SeatRoom(1, demoElapsedMinutes: 15);

        Assert.NotNull(seated);
        var stored = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(prestageAt.AddMinutes(-15), stored.PrestageStartedAt);
        Assert.Equal(seatAt.AddMinutes(-15), stored.SeatedAt);
        Assert.Equal(TimeSpan.FromMinutes(4), stored.SeatedAt!.Value - stored.PrestageStartedAt!.Value);
    }

    [Fact]
    public void Development_negative_demo_offset_is_rejected_without_mutating_room()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = Path.Combine(workspace.DataRoot, "negative-demo-offset.db");
        var prestageAt = new DateTimeOffset(2026, 7, 1, 13, 30, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(prestageAt);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: databasePath,
            timeProvider: clock);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var before = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        clock.SetUtcNow(prestageAt.AddMinutes(3));

        Assert.Null(context.Store.SeatRoom(1, demoElapsedMinutes: -1));

        var after = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        AssertSameRoomState(before, after);

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: databasePath,
            timeProvider: clock);
        AssertSameRoomState(before, reloaded.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
    }

    [Fact]
    public void Development_demo_offset_above_maximum_is_rejected_without_mutating_room()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = Path.Combine(workspace.DataRoot, "oversized-demo-offset.db");
        var prestageAt = new DateTimeOffset(2026, 7, 1, 13, 45, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(prestageAt);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: databasePath,
            timeProvider: clock);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var before = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        clock.SetUtcNow(prestageAt.AddMinutes(3));

        Assert.Null(context.Store.SeatRoom(1, demoElapsedMinutes: 241));

        var after = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        AssertSameRoomState(before, after);

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: databasePath,
            timeProvider: clock);
        AssertSameRoomState(before, reloaded.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
    }

    [Fact]
    public void Production_demo_offset_is_rejected_before_state_derivation_or_mutation()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var prestageAt = new DateTimeOffset(2026, 7, 1, 14, 0, 0, TimeSpan.Zero);
        var seatAt = prestageAt.AddMinutes(3);
        var clock = new ManualTimeProvider(prestageAt);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            boardUiOptions: new BoardUiOptions { DemoTimerEnabled = true },
            timeProvider: clock);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var persistedBefore = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

        var roomsField = typeof(DemoBoardStore).GetField(
            "_rooms",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var inMemoryRooms = Assert.IsType<List<RoomState>>(roomsField?.GetValue(context.Store));
        var inMemoryRoom = inMemoryRooms.Single(room => room.RoomId == 1);
        var sentinelAgingAt = prestageAt.AddMinutes(-2);
        var sentinelStaleAt = prestageAt.AddMinutes(-1);
        inMemoryRoom.State = RoomStates.Available;
        inMemoryRoom.AgingStartedAt = sentinelAgingAt;
        inMemoryRoom.StaleStartedAt = sentinelStaleAt;

        clock.SetUtcNow(seatAt);

        Assert.Null(context.Store.SeatRoom(1, demoElapsedMinutes: 15));
        Assert.Equal(RoomStates.Available, inMemoryRoom.State);
        Assert.Equal(sentinelAgingAt, inMemoryRoom.AgingStartedAt);
        Assert.Equal(sentinelStaleAt, inMemoryRoom.StaleStartedAt);
        Assert.Null(inMemoryRoom.SeatedAt);

        var persistedAfter = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        AssertSameRoomState(persistedBefore, persistedAfter);

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);
        AssertSameRoomState(persistedBefore, reloaded.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
    }

    [Fact]
    public void Development_demo_offset_boundaries_zero_and_240_are_applied_without_clamping()
    {
        using var workspace = TestWorkspace.Create();
        var prestageAt = new DateTimeOffset(2026, 7, 1, 14, 30, 0, TimeSpan.Zero);
        var seatAt = prestageAt.AddMinutes(5);
        var zeroClock = new ManualTimeProvider(prestageAt);
        var maximumClock = new ManualTimeProvider(prestageAt);
        var zeroContext = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: Path.Combine(workspace.DataRoot, "zero-demo-offset.db"),
            timeProvider: zeroClock);
        var maximumContext = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: Path.Combine(workspace.DataRoot, "maximum-demo-offset.db"),
            timeProvider: maximumClock);

        Assert.NotNull(zeroContext.Store.BeginPrestage(1, "otte", "CON"));
        Assert.NotNull(maximumContext.Store.BeginPrestage(1, "pledger", "EXT"));
        zeroClock.SetUtcNow(seatAt);
        maximumClock.SetUtcNow(seatAt);

        Assert.NotNull(zeroContext.Store.SeatRoom(1, demoElapsedMinutes: 0));
        Assert.NotNull(maximumContext.Store.SeatRoom(1, demoElapsedMinutes: 240));

        var zeroOffsetRoom = zeroContext.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var maximumOffsetRoom = maximumContext.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(prestageAt, zeroOffsetRoom.PrestageStartedAt);
        Assert.Equal(seatAt, zeroOffsetRoom.SeatedAt);
        Assert.Equal(prestageAt.AddMinutes(-240), maximumOffsetRoom.PrestageStartedAt);
        Assert.Equal(seatAt.AddMinutes(-240), maximumOffsetRoom.SeatedAt);
        Assert.Equal(
            zeroOffsetRoom.SeatedAt!.Value - zeroOffsetRoom.PrestageStartedAt!.Value,
            maximumOffsetRoom.SeatedAt!.Value - maximumOffsetRoom.PrestageStartedAt!.Value);
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
        Assert.NotNull(context.Store.SeatRoom(1));
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
        Assert.NotNull(context.Store.SeatRoom(1));
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
        Assert.NotNull(context.Store.SeatRoom(1));
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
        Assert.NotNull(first.Store.SeatRoom(1));
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

        // Standalone sedation is not seatable, by code or by id, for new seating or updates.
        Assert.Null(SeatViaPrestage(context.Store, 1, "otte", "SED"));
        Assert.Null(SeatViaPrestage(context.Store, 1, "otte", "sedation"));
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "EXT"));
        Assert.Null(context.Store.UpdateAssignment(1, "otte", "SED"));
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
    public void Sedation_modifier_can_be_added_and_removed_via_update_assignment()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "IMP"));

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
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        using var connection = OpenConnection(context.DatabasePath);
        var activeRoomColumns = GetColumnNames(connection, "active_rooms");
        var completedCycleColumns = GetColumnNames(connection, "completed_room_cycles");
        var abortedAssignmentColumns = GetColumnNames(connection, "aborted_room_assignments");

        Assert.Subset(AllowedActiveRoomColumns, activeRoomColumns);
        Assert.Subset(AllowedCompletedCycleColumns, completedCycleColumns);
        Assert.Subset(AllowedAbortedAssignmentColumns, abortedAssignmentColumns);
        Assert.DoesNotContain(
            activeRoomColumns.Concat(completedCycleColumns).Concat(abortedAssignmentColumns),
            ContainsBannedPhiTerm);
    }

    [Fact]
    public void DateTimeOffset_round_trip_preserves_cycle_dedup_key()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var seated = SeatViaPrestage(context.Store, 1, "otte", "CON");
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
    public void Aborted_assignment_round_trips_full_snapshot_and_nullable_phase_timestamps()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var prestageStartedAt = new DateTimeOffset(2026, 3, 2, 14, 5, 0, TimeSpan.Zero);
        var seatedAt = prestageStartedAt.AddMinutes(4);
        var terminatedAt = prestageStartedAt.AddMinutes(9);
        var record = new AbortedRoomAssignment
        {
            EpisodeId = "episode-a",
            RoomId = 1,
            AssignedDoctor = "otte",
            AssignedDoctorDisplayName = "Dr. Snapshot Otte",
            ProcedureCode = "EXT+SED",
            ProcedureCategory = "Snapshot Extraction + Sedation",
            OriginalDefaultExpectedUnits = 3,
            ExpectedAllocationUnits = 4,
            ExpectedAllocationMinutes = 40,
            AllocationAdjustedFromDefault = true,
            PrestageStartedAt = prestageStartedAt,
            SeatedAt = seatedAt,
            ReadyForDoctorAt = null,
            TerminatedAt = terminatedAt,
            TerminatedFromState = RoomStates.Seated,
            TerminationKind = TerminationKinds.StaffCanceled,
            CancellationReason = CancellationReasons.PatientCanceled
        };

        context.Repository.TerminateIncompleteAssignment(record, new RoomState(1), context.Doctors, context.Procedures);

        var reloaded = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.True(reloaded.AbortedAssignmentId > 0);
        Assert.Equal("episode-a", reloaded.EpisodeId);
        Assert.Equal(1, reloaded.RoomId);
        Assert.Equal("otte", reloaded.AssignedDoctor);
        Assert.Equal("Dr. Snapshot Otte", reloaded.AssignedDoctorDisplayName);
        Assert.Equal("EXT+SED", reloaded.ProcedureCode);
        Assert.Equal("Snapshot Extraction + Sedation", reloaded.ProcedureCategory);
        Assert.Equal(3, reloaded.OriginalDefaultExpectedUnits);
        Assert.Equal(4, reloaded.ExpectedAllocationUnits);
        Assert.Equal(40, reloaded.ExpectedAllocationMinutes);
        Assert.True(reloaded.AllocationAdjustedFromDefault);
        Assert.Equal(prestageStartedAt, reloaded.PrestageStartedAt);
        Assert.Equal(seatedAt, reloaded.SeatedAt);
        Assert.Null(reloaded.ReadyForDoctorAt);
        Assert.Equal(terminatedAt, reloaded.TerminatedAt);
        Assert.Equal(RoomStates.Seated, reloaded.TerminatedFromState);
        Assert.Equal(TerminationKinds.StaffCanceled, reloaded.TerminationKind);
        Assert.Equal(CancellationReasons.PatientCanceled, reloaded.CancellationReason);
    }

    [Fact]
    public void Terminating_the_same_episode_twice_is_idempotent()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var at = new DateTimeOffset(2026, 3, 3, 12, 0, 0, TimeSpan.Zero);
        var first = NewAbortedAssignment("episode-dup", roomId: 1, prestageStartedAt: at, terminatedAt: at.AddMinutes(5));
        context.Repository.TerminateIncompleteAssignment(first, new RoomState(1), context.Doctors, context.Procedures);
        var firstId = first.AbortedAssignmentId;

        // Re-terminating the same episode id (e.g. a retried request or a restart-replayed sweep).
        var second = NewAbortedAssignment("episode-dup", roomId: 1, prestageStartedAt: at, terminatedAt: at.AddMinutes(5));
        context.Repository.TerminateIncompleteAssignment(second, new RoomState(1), context.Doctors, context.Procedures);

        Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.True(firstId > 0);
        Assert.Equal(firstId, second.AbortedAssignmentId);
    }

    [Fact]
    public void Distinct_episodes_in_same_room_at_same_timestamp_persist_separately()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Same room, identical prestage/termination timestamps (the fixed-clock case), different
        // episode ids: both genuinely-distinct episodes must survive - neither overwrites the other.
        var at = new DateTimeOffset(2026, 3, 4, 15, 30, 0, TimeSpan.Zero);
        var episodeX = NewAbortedAssignment("episode-x", roomId: 1, prestageStartedAt: at, terminatedAt: at);
        var episodeY = NewAbortedAssignment("episode-y", roomId: 1, prestageStartedAt: at, terminatedAt: at);
        context.Repository.TerminateIncompleteAssignment(episodeX, new RoomState(1), context.Doctors, context.Procedures);
        context.Repository.TerminateIncompleteAssignment(episodeY, new RoomState(1), context.Doctors, context.Procedures);

        var loaded = context.Repository.LoadAbortedAssignments();
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, r => r.EpisodeId == "episode-x");
        Assert.Contains(loaded, r => r.EpisodeId == "episode-y");
    }

    [Fact]
    public void Terminate_incomplete_assignment_persists_record_and_reset_room_together()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Put room 1 into a non-Available state so the reset is observable.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));

        var record = NewAbortedAssignment(
            "episode-d", roomId: 1,
            prestageStartedAt: new DateTimeOffset(2026, 3, 5, 10, 0, 0, TimeSpan.Zero),
            terminatedAt: new DateTimeOffset(2026, 3, 5, 10, 6, 0, TimeSpan.Zero));
        context.Repository.TerminateIncompleteAssignment(record, new RoomState(1), context.Doctors, context.Procedures);

        // Both writes landed from the one call: the durable record exists...
        Assert.Single(context.Repository.LoadAbortedAssignments());

        // ...and the active room was reset to Available with no residual assignment/episode state.
        var room1 = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Available, room1.State);
        Assert.Null(room1.AssignedDoctor);
        Assert.Null(room1.SeatedAt);
        Assert.Null(room1.PrestageStartedAt);
        Assert.Null(room1.EpisodeId);
    }

    [Fact]
    public void Save_completed_cycle_and_room_persists_both_and_propagates_id_on_insert_and_update()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var seatedAt = new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.Zero);
        var cycle = new CompletedRoomCycle
        {
            RoomId = 1,
            EpisodeId = "episode-e",
            AssignedDoctor = "otte",
            ProcedureCode = "CON",
            PrestageStartedAt = seatedAt.AddMinutes(-5),
            SeatedAt = seatedAt,
            SeatedToDoctorSeconds = 600,
            FinalWaitState = RoomStates.ReadyForDoctor
        };

        context.Repository.SaveCompletedCycleAndRoom(cycle, new RoomState(1), context.Doctors, context.Procedures);

        // Insert path: the id assigned inside the transaction is read back and propagated onto the cycle.
        Assert.True(cycle.CompletedCycleId > 0);
        var insertedId = cycle.CompletedCycleId;

        var loadedCycle = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.Equal(insertedId, loadedCycle.CompletedCycleId);
        Assert.Equal("episode-e", loadedCycle.EpisodeId);
        Assert.Equal(seatedAt.AddMinutes(-5), loadedCycle.PrestageStartedAt);

        // The active room was reset to Available in the same operation.
        var room1 = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Available, room1.State);

        // Update path: the same (room_id, seated_at) upsert reads the id back inside the transaction
        // and keeps it stable, so the propagated id is consistent across insert and update.
        cycle.RoomAvailableAt = seatedAt.AddMinutes(30);
        context.Repository.SaveCompletedCycleAndRoom(cycle, new RoomState(1), context.Doctors, context.Procedures);
        Assert.Equal(insertedId, cycle.CompletedCycleId);

        var afterUpdate = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.Equal(insertedId, afterUpdate.CompletedCycleId);
        Assert.Equal(seatedAt.AddMinutes(30), afterUpdate.RoomAvailableAt);
    }

    [Fact]
    public void Legacy_database_without_prestage_columns_initializes_and_preserves_active_seated_row()
    {
        using var workspace = TestWorkspace.Create();
        var legacyDbPath = Path.Combine(workspace.DataRoot, "legacy.db");
        var seatedAt = new DateTimeOffset(2026, 2, 10, 9, 0, 0, TimeSpan.Zero);

        // Pre-create a database with the pre-Prestaging active_rooms schema (no prestage_started_at /
        // episode_id) holding one Seated room, simulating a production database from before this feature.
        using (var seed = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = legacyDbPath }.ToString()))
        {
            seed.Open();
            ExecuteSql(seed, """
                CREATE TABLE active_rooms (
                    room_id INTEGER PRIMARY KEY,
                    assigned_doctor_id TEXT NULL,
                    assigned_doctor_display_name TEXT NULL,
                    procedure_code TEXT NULL,
                    procedure_category TEXT NULL,
                    state TEXT NOT NULL,
                    seated_at TEXT NULL,
                    aging_started_at TEXT NULL,
                    stale_started_at TEXT NULL,
                    ready_for_doctor_at TEXT NULL,
                    doctor_arrived_at TEXT NULL,
                    doctor_complete_at TEXT NULL,
                    room_available_at TEXT NULL,
                    original_default_expected_units INTEGER NOT NULL DEFAULT 0,
                    expected_allocation_units INTEGER NOT NULL DEFAULT 0,
                    expected_allocation_minutes INTEGER NOT NULL DEFAULT 0,
                    allocation_adjusted_from_default INTEGER NOT NULL DEFAULT 0,
                    updated_at TEXT NOT NULL
                );
                """);
            ExecuteSql(seed, $"""
                INSERT INTO active_rooms (room_id, assigned_doctor_id, procedure_code, state, seated_at, updated_at)
                VALUES (1, 'otte', 'CON', 'seated', '{seatedAt:O}', '{seatedAt:O}');
                """);
        }

        // Opening the store runs the additive migrations; the legacy row must survive untouched and
        // is never forced through Prestaging.
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: legacyDbPath,
            roomCount: 3);

        using (var connection = OpenConnection(legacyDbPath))
        {
            var columns = GetColumnNames(connection, "active_rooms");
            Assert.Contains("prestage_started_at", columns);
            Assert.Contains("episode_id", columns);
        }

        var room1 = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Seated, room1.State);
        Assert.Equal("otte", room1.AssignedDoctor);
        Assert.Equal("CON", room1.ProcedureCode);
        Assert.Equal(seatedAt, room1.SeatedAt);
        Assert.Null(room1.PrestageStartedAt);
        Assert.Null(room1.EpisodeId);
    }

    [Fact]
    public void Reset_room_clears_episode_id_and_prestage_started_at_along_with_every_other_field()
    {
        // ResetRoom is private static; invoked via reflection, mirroring InvokeTryAddColumn's pattern
        // for testing a private static helper directly rather than through a specific caller.
        var room = new RoomState(1)
        {
            EpisodeId = "episode-r",
            AssignedDoctor = "otte",
            AssignedDoctorDisplayName = "Dr. Otte",
            ProcedureCode = "CON",
            ProcedureCategory = "Consult",
            State = RoomStates.Seated,
            PrestageStartedAt = new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero),
            SeatedAt = new DateTimeOffset(2026, 5, 1, 9, 5, 0, TimeSpan.Zero),
            AgingStartedAt = new DateTimeOffset(2026, 5, 1, 9, 12, 0, TimeSpan.Zero),
            StaleStartedAt = new DateTimeOffset(2026, 5, 1, 9, 17, 0, TimeSpan.Zero),
            ReadyForDoctorAt = new DateTimeOffset(2026, 5, 1, 9, 6, 0, TimeSpan.Zero),
            DoctorArrivedAt = new DateTimeOffset(2026, 5, 1, 9, 20, 0, TimeSpan.Zero),
            DoctorCompleteAt = new DateTimeOffset(2026, 5, 1, 9, 40, 0, TimeSpan.Zero),
            RoomAvailableAt = new DateTimeOffset(2026, 5, 1, 9, 50, 0, TimeSpan.Zero),
            OriginalDefaultExpectedUnits = 2,
            ExpectedAllocationUnits = 3,
            ExpectedAllocationMinutes = 30,
            AllocationAdjustedFromDefault = true
        };

        InvokeResetRoom(room);

        Assert.Null(room.EpisodeId);
        Assert.Null(room.AssignedDoctor);
        Assert.Null(room.AssignedDoctorDisplayName);
        Assert.Null(room.ProcedureCode);
        Assert.Null(room.ProcedureCategory);
        Assert.Equal(RoomStates.Available, room.State);
        Assert.Null(room.PrestageStartedAt);
        Assert.Null(room.SeatedAt);
        Assert.Null(room.AgingStartedAt);
        Assert.Null(room.StaleStartedAt);
        Assert.Null(room.ReadyForDoctorAt);
        Assert.Null(room.DoctorArrivedAt);
        Assert.Null(room.DoctorCompleteAt);
        Assert.Null(room.RoomAvailableAt);
        Assert.Equal(0, room.OriginalDefaultExpectedUnits);
        Assert.Equal(0, room.ExpectedAllocationUnits);
        Assert.Equal(0, room.ExpectedAllocationMinutes);
        Assert.False(room.AllocationAdjustedFromDefault);
    }

    [Fact]
    public void Development_demo_room_constructions_use_episode_identity_only_for_canonical_ready_seed()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Development, roomCount: 12);

        var rooms = context.Repository.LoadRooms(12);
        Assert.NotEmpty(rooms);
        Assert.All(rooms, room =>
        {
            if (room.RoomId is >= 6 and <= 9)
            {
                Assert.Equal(RoomStates.ReadyForDoctor, room.State);
                Assert.False(string.IsNullOrWhiteSpace(room.EpisodeId));
                Assert.NotNull(room.PrestageStartedAt);
                Assert.False(string.IsNullOrWhiteSpace(room.ActiveReadyHandoffId));
            }
            else
            {
                Assert.Null(room.EpisodeId);
                Assert.Null(room.PrestageStartedAt);
                Assert.Null(room.ActiveReadyHandoffId);
            }
        });
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
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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
    public async Task Begin_prestage_endpoint_captures_assignment_snapshot_and_rejects_invalid_states()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var request = new BeginPrestageRequest(
            DoctorId: "otte",
            ProcedureCode: "EXT",
            Sedation: true,
            ExpectedAllocationUnits: 5);

        var success = await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            1, request, NewRoomMutationHttpContext(1, token: null), CreateBindingValidator(enabled: false),
            context.Store, logger, new NoopBoardHubContext());

        Assert.Equal(200, await ExecuteBindingResult(success));
        var prestaged = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Prestaging, prestaged.State);
        Assert.Equal("otte", prestaged.AssignedDoctor);
        Assert.Equal("EXT+SED", prestaged.ProcedureCode);
        Assert.Equal(3, prestaged.OriginalDefaultExpectedUnits);
        Assert.Equal(5, prestaged.ExpectedAllocationUnits);
        Assert.Equal(50, prestaged.ExpectedAllocationMinutes);

        var duplicate = await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            1, request, NewRoomMutationHttpContext(1, token: null), CreateBindingValidator(enabled: false),
            context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(duplicate));
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(1)?.State);

        var invalidDoctor = await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            2, new BeginPrestageRequest(DoctorId: "missing", ProcedureCode: "CON"),
            NewRoomMutationHttpContext(2, token: null), CreateBindingValidator(enabled: false),
            context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(invalidDoctor));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(2)?.State);

        var invalidProcedure = await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            2, new BeginPrestageRequest(DoctorId: "otte", ProcedureCode: "NOPE"),
            NewRoomMutationHttpContext(2, token: null), CreateBindingValidator(enabled: false),
            context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(invalidProcedure));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(2)?.State);
    }

    [Fact]
    public async Task Seat_room_endpoint_uses_minimal_prestaged_transition_and_preserves_snapshot()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 5, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var databasePath = Path.Combine(workspace.DataRoot, "development-seat-minimal.db");
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: databasePath,
            timeProvider: clock);
        context.Store.ResetAllDataForEmptyBeta();
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var environment = new TestWebHostEnvironment(workspace.ContentRoot, Environments.Development);

        Assert.Equal(200, await ExecuteBindingResult(await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            1,
            new BeginPrestageRequest(DoctorId: "otte", ProcedureCode: "EXT", ExpectedAllocationUnits: 5),
            NewRoomMutationHttpContext(1, token: null), CreateBindingValidator(enabled: false),
            context.Store, logger, new NoopBoardHubContext())));
        var prestaged = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var episodeId = prestaged.EpisodeId;
        var prestageAt = prestaged.PrestageStartedAt;

        // Canonical Seat contract: empty JSON object, demoElapsedMinutes omitted -> means 0.
        var response = await global::RoomLifecycleEndpointHandler.SeatAsync(
            1, NewJsonBodyContext(1, token: null, "{}"),
            CreateBindingValidator(enabled: false), context.Store, environment, logger, new NoopBoardHubContext());

        Assert.Equal(200, await ExecuteBindingResult(response));
        var seated = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Seated, seated.State);
        Assert.Equal(episodeId, seated.EpisodeId);
        Assert.Equal(prestageAt, seated.PrestageStartedAt);
        Assert.Equal("otte", seated.AssignedDoctor);
        Assert.Equal("EXT", seated.ProcedureCode);
        Assert.Equal(5, seated.ExpectedAllocationUnits);

        var bypass = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, "{}"),
            CreateBindingValidator(enabled: false), context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(409, await ExecuteBindingResult(bypass));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(2)?.State);
    }

    [Fact]
    public async Task Seat_room_endpoint_supports_development_simulation_and_rejects_invalid_values()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 5, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var databasePath = Path.Combine(workspace.DataRoot, "development-seat-simulation.db");
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: databasePath,
            timeProvider: clock);
        context.Store.ResetAllDataForEmptyBeta();
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var development = new TestWebHostEnvironment(workspace.ContentRoot, Environments.Development);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var originalPrestageAt = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1).PrestageStartedAt;
        var positive = await global::RoomLifecycleEndpointHandler.SeatAsync(
            1, NewJsonBodyContext(1, token: null, """{"demoElapsedMinutes":240}"""),
            CreateBindingValidator(enabled: false), context.Store, development, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(positive));
        var simulated = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(now.AddMinutes(-240), simulated.SeatedAt);
        Assert.Equal(originalPrestageAt!.Value.AddMinutes(-240), simulated.PrestageStartedAt);

        Assert.NotNull(context.Store.BeginPrestage(2, "pledger", "CON"));
        var negative = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, """{"demoElapsedMinutes":-1}"""),
            CreateBindingValidator(enabled: false), context.Store, development, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(negative));
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(2)?.State);

        Assert.NotNull(context.Store.BeginPrestage(3, "gibson", "CON"));
        var overMaximum = await global::RoomLifecycleEndpointHandler.SeatAsync(
            3, NewJsonBodyContext(3, token: null, """{"demoElapsedMinutes":241}"""),
            CreateBindingValidator(enabled: false), context.Store, development, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(overMaximum));
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(3)?.State);

        using var productionWorkspace = TestWorkspace.Create();
        var production = StoreContext.Create(productionWorkspace, environmentName: Environments.Production, timeProvider: clock);
        var productionLogger = CreateDiagnosticLogger(Path.Combine(productionWorkspace.DataRoot, "logs"), productionWorkspace.ContentRoot);
        Assert.NotNull(production.Store.BeginPrestage(1, "otte", "CON"));
        var productionOffset = await global::RoomLifecycleEndpointHandler.SeatAsync(
            1, NewJsonBodyContext(1, token: null, """{"demoElapsedMinutes":1}"""),
            CreateBindingValidator(enabled: false), production.Store,
            new TestWebHostEnvironment(productionWorkspace.ContentRoot, Environments.Production), productionLogger,
            new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(productionOffset));
        Assert.Equal(RoomStates.Prestaging, production.Store.GetRoom(1)?.State);
    }

    [Fact]
    public async Task Assignment_bearing_seat_compatibility_request_starts_and_seats_without_overwriting_prestaging()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var environment = new TestWebHostEnvironment(workspace.ContentRoot, Environments.Development);

        // Exact checked-in room-panel payload shape (board.js sendSeatRoom): doctorId, procedureCode,
        // procedureId, sedation, expectedAllocationUnits, demoElapsedMinutes all present.
        var compatibilityBody = """
            {"doctorId":"otte","procedureCode":"EXT","procedureId":"EXT","sedation":true,"expectedAllocationUnits":5,"demoElapsedMinutes":0}
            """;
        var compatibility = await global::RoomLifecycleEndpointHandler.SeatAsync(
            1, NewJsonBodyContext(1, token: null, compatibilityBody),
            CreateBindingValidator(enabled: false), context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(compatibility));
        var seated = context.Store.GetRoom(1)!;
        Assert.Equal(RoomStates.Seated, seated.State);
        Assert.Equal("otte", seated.AssignedDoctor);
        Assert.Equal("EXT+SED", seated.ProcedureCode);
        Assert.Equal(5, seated.ExpectedAllocationUnits);

        // A compatibility request while the room is already Prestaging must be rejected without
        // overwriting the existing prestaged snapshot (Begin Prestage's own state guard, not a new
        // atomic store method).
        Assert.NotNull(context.Store.BeginPrestage(2, "pledger", "CON"));
        var before = context.Repository.LoadRooms(3).Single(room => room.RoomId == 2);
        var conflicting = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, """{"doctorId":"otte","procedureCode":"EXT"}"""),
            CreateBindingValidator(enabled: false), context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(conflicting));
        var after = context.Repository.LoadRooms(3).Single(room => room.RoomId == 2);
        Assert.Equal(RoomStates.Prestaging, after.State);
        Assert.Equal(before.EpisodeId, after.EpisodeId);
        Assert.Equal("pledger", after.AssignedDoctor);
        Assert.Equal("CON", after.ProcedureCode);
    }

    [Fact]
    public async Task Procedure_alias_resolution_rejects_conflicts_and_accepts_single_or_matching_aliases()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var environment = new TestWebHostEnvironment(workspace.ContentRoot, Environments.Production);
        var validator = CreateBindingValidator(enabled: false);

        // Begin Prestage: conflicting aliases are rejected before any mutation.
        var conflictingBegin = await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            1, new BeginPrestageRequest(DoctorId: "otte", ProcedureCode: "EXT", ProcedureId: "CON"),
            NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(conflictingBegin));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        // Begin Prestage: procedureId alone is accepted.
        var idAlone = await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            1, new BeginPrestageRequest(DoctorId: "otte", ProcedureId: "EXT"),
            NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(idAlone));
        Assert.Equal("EXT", context.Store.GetRoom(1)?.ProcedureCode);
        Assert.NotNull(context.Store.CancelPrestage(1));

        // Begin Prestage: procedureCode alone is accepted.
        var codeAlone = await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            1, new BeginPrestageRequest(DoctorId: "otte", ProcedureCode: "EXT"),
            NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(codeAlone));
        Assert.Equal("EXT", context.Store.GetRoom(1)?.ProcedureCode);
        Assert.NotNull(context.Store.CancelPrestage(1));

        // Begin Prestage: matching aliases (case-insensitive, after trim) are accepted.
        var matchingBegin = await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            1, new BeginPrestageRequest(DoctorId: "otte", ProcedureCode: " ext ", ProcedureId: "EXT"),
            NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(matchingBegin));
        Assert.Equal("EXT", context.Store.GetRoom(1)?.ProcedureCode);

        // Compatibility Seat: conflicting aliases are rejected before any mutation.
        var conflictingSeat = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, """{"doctorId":"pledger","procedureCode":"EXT","procedureId":"CON"}"""),
            validator, context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(conflictingSeat));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(2)?.State);

        // Compatibility Seat: matching aliases are accepted and seat through Begin Prestage -> Seat Room.
        var matchingSeat = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, """{"doctorId":"pledger","procedureCode":"EXT","procedureId":"ext"}"""),
            validator, context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(matchingSeat));
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(2)?.State);
        Assert.Equal("EXT", context.Store.GetRoom(2)?.ProcedureCode);
    }

    [Fact]
    public async Task Procedure_alias_resolution_rejects_a_supplied_blank_alias_before_mutation()
    {
        // A SUPPLIED blank/whitespace-only alias is invalid input and must be rejected outright - it
        // is not silently treated the same as an omitted (null) alias, even when the other alias is
        // perfectly valid.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var environment = new TestWebHostEnvironment(workspace.ContentRoot, Environments.Production);
        var validator = CreateBindingValidator(enabled: false);

        // Begin Prestage: procedureCode blank, procedureId valid -> rejected, no mutation.
        var codeBlankBegin = await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            1, new BeginPrestageRequest(DoctorId: "otte", ProcedureCode: "", ProcedureId: "EXT"),
            NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(codeBlankBegin));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        // Begin Prestage: procedureCode valid, procedureId blank -> rejected, no mutation.
        var idBlankBegin = await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            1, new BeginPrestageRequest(DoctorId: "otte", ProcedureCode: "EXT", ProcedureId: " "),
            NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(idBlankBegin));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        // Begin Prestage: both blank -> rejected, no mutation.
        var bothBlankBegin = await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            1, new BeginPrestageRequest(DoctorId: "otte", ProcedureCode: "", ProcedureId: "  "),
            NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(bothBlankBegin));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        // Compatibility Seat: procedureCode blank, procedureId valid -> rejected, no mutation.
        var codeBlankSeat = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, """{"doctorId":"pledger","procedureCode":"","procedureId":"EXT"}"""),
            validator, context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(codeBlankSeat));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(2)?.State);

        // Compatibility Seat: procedureCode valid, procedureId blank -> rejected, no mutation.
        var idBlankSeat = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, """{"doctorId":"pledger","procedureCode":"EXT","procedureId":" "}"""),
            validator, context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(idBlankSeat));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(2)?.State);

        // Compatibility Seat: both blank -> rejected, no mutation.
        var bothBlankSeat = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, """{"doctorId":"pledger","procedureCode":"","procedureId":"  "}"""),
            validator, context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(bothBlankSeat));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(2)?.State);
    }

    [Fact]
    public async Task Seat_endpoint_treats_no_body_empty_body_and_empty_object_as_canonical_zero_minutes()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var environment = new TestWebHostEnvironment(workspace.ContentRoot, Environments.Production);
        var validator = CreateBindingValidator(enabled: false);

        // No body at all (matches the current reasonless-style caller expectations for optional bodies).
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var noBody = await global::RoomLifecycleEndpointHandler.SeatAsync(
            1, NewRoomMutationHttpContext(1, token: null), validator, context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(noBody));
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(1)?.State);
        Assert.NotNull(context.Store.CancelSeating(1));

        // Empty body (zero bytes).
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var emptyBody = await global::RoomLifecycleEndpointHandler.SeatAsync(
            1, NewJsonBodyContext(1, token: null, ""), validator, context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(emptyBody));
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(1)?.State);
        Assert.NotNull(context.Store.CancelSeating(1));

        // Empty JSON object.
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var emptyObject = await global::RoomLifecycleEndpointHandler.SeatAsync(
            1, NewJsonBodyContext(1, token: null, "{}"), validator, context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(emptyObject));
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(1)?.State);
    }

    [Fact]
    public async Task Seat_endpoint_parses_canonical_body_and_rejects_unknown_malformed_or_wrong_content_type()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 6, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var databasePath = Path.Combine(workspace.DataRoot, "development-seat-contract.db");
        var context = StoreContext.Create(
            workspace, environmentName: Environments.Development, databasePath: databasePath, timeProvider: clock);
        context.Store.ResetAllDataForEmptyBeta();
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var environment = new TestWebHostEnvironment(workspace.ContentRoot, Environments.Development);
        var validator = CreateBindingValidator(enabled: false);

        // Canonical demoElapsedMinutes body: accepted, bare Seat Room transition.
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var canonical = await global::RoomLifecycleEndpointHandler.SeatAsync(
            1, NewJsonBodyContext(1, token: null, """{"demoElapsedMinutes":15}"""),
            validator, context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(canonical));
        Assert.Equal(now.AddMinutes(-15), context.Store.GetRoom(1)?.SeatedAt);

        // Unknown property: rejected, no mutation.
        Assert.NotNull(context.Store.BeginPrestage(2, "pledger", "CON"));
        var unknownProperty = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, """{"demoElapsedMinutes":0,"unexpectedField":true}"""),
            validator, context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(unknownProperty));
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(2)?.State);

        // Incomplete compatibility payload (sedation present, doctor/procedure absent): rejected via
        // the existing missing-doctor/procedure validation, no mutation.
        var incomplete = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, """{"sedation":true}"""),
            validator, context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(incomplete));
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(2)?.State);

        // Malformed JSON: rejected, no mutation.
        var malformed = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, "{not-json"),
            validator, context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(malformed));
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(2)?.State);

        // Unsupported content type on a non-empty body: rejected, no mutation.
        var wrongContentType = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, """{"demoElapsedMinutes":0}""", contentType: "text/plain"),
            validator, context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(wrongContentType));
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(2)?.State);
    }

    [Fact]
    public async Task Compatibility_seat_rejects_positive_demo_elapsed_minutes_outside_development_before_begin_prestage()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var productionEnvironment = new TestWebHostEnvironment(workspace.ContentRoot, Environments.Production);
        var validator = CreateBindingValidator(enabled: false);

        var body = """{"doctorId":"otte","procedureCode":"CON","demoElapsedMinutes":30}""";
        var response = await global::RoomLifecycleEndpointHandler.SeatAsync(
            1, NewJsonBodyContext(1, token: null, body), validator, context.Store, productionEnvironment, logger, new NoopBoardHubContext());

        Assert.Equal(400, await ExecuteBindingResult(response));
        // No Begin Prestage mutation occurred: room stays Available and no aborted-assignment record exists.
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
    }

    [Fact]
    public async Task Seat_endpoint_classifies_compatibility_by_property_presence_not_value()
    {
        // Each of these bodies supplies one of the five assignment-bearing property NAMES with a
        // null/false value. Presence alone must classify them as compatibility input - never as a
        // canonical bare Seat request - so each is rejected as an incomplete compatibility payload
        // (missing doctor/procedure) and never reaches store.SeatRoom. The room is left Prestaging
        // beforehand specifically so a misclassification-as-canonical would be observable: if these
        // were (wrongly) treated as canonical, the bare Seat transition would succeed against the
        // room's EXISTING prestaged snapshot, silently ignoring what the caller sent.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var environment = new TestWebHostEnvironment(workspace.ContentRoot, Environments.Production);
        var validator = CreateBindingValidator(enabled: false);

        string[] bodies =
        [
            """{"sedation":false}""",
            """{"expectedAllocationUnits":null}""",
            """{"doctorId":null}""",
            """{"procedureCode":null}""",
            """{"procedureId":null}""",
            """{"demoElapsedMinutes":0,"sedation":false}"""
        ];

        foreach (var body in bodies)
        {
            Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
            var before = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

            var response = await global::RoomLifecycleEndpointHandler.SeatAsync(
                1, NewJsonBodyContext(1, token: null, body), validator, context.Store, environment, logger, new NoopBoardHubContext());

            // Rejected as an incomplete compatibility request (missing doctor/procedure), not silently
            // accepted as canonical.
            Assert.Equal(400, await ExecuteBindingResult(response));

            // The existing prestaged snapshot is completely untouched - never silently seated.
            var after = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
            Assert.Equal(RoomStates.Prestaging, after.State);
            Assert.Equal(before.EpisodeId, after.EpisodeId);
            Assert.Equal(before.AssignedDoctor, after.AssignedDoctor);
            Assert.Equal(before.ProcedureCode, after.ProcedureCode);
            Assert.Null(after.SeatedAt);

            Assert.NotNull(context.Store.CancelPrestage(1));
        }
    }

    [Fact]
    public async Task Cancel_prestage_endpoint_parses_body_strictly()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var validator = CreateBindingValidator(enabled: false);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var noBody = await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
            1, NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(noBody));
        Assert.Null(context.Repository.LoadAbortedAssignments().Single(record => record.RoomId == 1).CancellationReason);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var emptyBody = await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
            1, NewJsonBodyContext(1, token: null, ""), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(emptyBody));

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var emptyObject = await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
            1, NewJsonBodyContext(1, token: null, "{}"), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(emptyObject));

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var explicitNull = await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
            1, NewJsonBodyContext(1, token: null, """{"cancellationReason":null}"""), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(explicitNull));

        Assert.NotNull(context.Store.BeginPrestage(2, "pledger", "EXT"));
        var validReasonBody = $"{{\"cancellationReason\":\"{CancellationReasons.MovedRoom}\"}}";
        var validReason = await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
            2, NewJsonBodyContext(2, token: null, validReasonBody), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(validReason));
        Assert.Equal(CancellationReasons.MovedRoom,
            context.Repository.LoadAbortedAssignments().Single(record => record.RoomId == 2).CancellationReason);

        Assert.NotNull(context.Store.BeginPrestage(3, "gibson", "CON"));
        var malformed = await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
            3, NewJsonBodyContext(3, token: null, "{bad"), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(malformed));
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(3)?.State);

        var unknownProperty = await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
            3, NewJsonBodyContext(3, token: null, """{"unexpectedField":true}"""), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(unknownProperty));
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(3)?.State);

        var wrongContentType = await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
            3, NewJsonBodyContext(3, token: null, """{"cancellationReason":"Other"}""", contentType: "text/plain"),
            validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(wrongContentType));
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(3)?.State);

        // Existing lifecycle guard: wrong state remains a 400 even with a strictly-parsed empty body.
        Assert.NotNull(context.Store.SeatRoom(3));
        var wrongState = await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
            3, NewRoomMutationHttpContext(3, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(wrongState));
    }

    [Fact]
    public async Task Cancel_seating_endpoint_parses_body_strictly_and_forwards_optional_reason()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var validator = CreateBindingValidator(enabled: false);

        // No body: reasonless caller compatibility (matches the current checked-in Cancel Seating caller).
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        var noBody = await global::RoomLifecycleEndpointHandler.CancelSeatingAsync(
            1, NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(noBody));
        Assert.Null(context.Repository.LoadAbortedAssignments().Single(record => record.RoomId == 1).CancellationReason);

        // Empty body.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        var emptyBody = await global::RoomLifecycleEndpointHandler.CancelSeatingAsync(
            1, NewJsonBodyContext(1, token: null, ""), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(emptyBody));

        // Empty JSON object.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        var emptyObject = await global::RoomLifecycleEndpointHandler.CancelSeatingAsync(
            1, NewJsonBodyContext(1, token: null, "{}"), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(emptyObject));

        // Explicit cancellationReason: null.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        var explicitNull = await global::RoomLifecycleEndpointHandler.CancelSeatingAsync(
            1, NewJsonBodyContext(1, token: null, """{"cancellationReason":null}"""), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(explicitNull));

        // Valid reason: forwarded unchanged.
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "pledger", "EXT"));
        var validReasonBody = $"{{\"cancellationReason\":\"{CancellationReasons.ProcedureChanged}\"}}";
        var reasoned = await global::RoomLifecycleEndpointHandler.CancelSeatingAsync(
            2, NewJsonBodyContext(2, token: null, validReasonBody), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(reasoned));
        Assert.Equal(CancellationReasons.ProcedureChanged,
            context.Repository.LoadAbortedAssignments().Single(record => record.RoomId == 2).CancellationReason);

        // Malformed JSON: rejected, no mutation.
        Assert.NotNull(SeatViaPrestage(context.Store, 3, "gibson", "CON"));
        var malformed = await global::RoomLifecycleEndpointHandler.CancelSeatingAsync(
            3, NewJsonBodyContext(3, token: null, "{bad"), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(malformed));
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(3)?.State);

        // Unknown property: rejected, no mutation.
        var unknownProperty = await global::RoomLifecycleEndpointHandler.CancelSeatingAsync(
            3, NewJsonBodyContext(3, token: null, """{"unexpectedField":true}"""), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(unknownProperty));
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(3)?.State);

        // Unsupported content type on a non-empty body: rejected, no mutation.
        var wrongContentType = await global::RoomLifecycleEndpointHandler.CancelSeatingAsync(
            3, NewJsonBodyContext(3, token: null, """{"cancellationReason":"Other"}""", contentType: "text/plain"),
            validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(wrongContentType));
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(3)?.State);
    }

    [Fact]
    public async Task Prestaging_route_authorization_matrix_blocks_before_mutation_across_all_four_routes()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var environment = new TestWebHostEnvironment(workspace.ContentRoot, Environments.Production);
        var enabledValidator = CreateBindingValidator(enabled: true);

        async Task<int?> InvokeBegin(int room, DefaultHttpContext httpContext) =>
            await ExecuteBindingResult(await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
                room, new BeginPrestageRequest(DoctorId: "otte", ProcedureCode: "CON"),
                httpContext, enabledValidator, context.Store, logger, new NoopBoardHubContext()));

        async Task<int?> InvokeSeat(int room, DefaultHttpContext httpContext) =>
            await ExecuteBindingResult(await global::RoomLifecycleEndpointHandler.SeatAsync(
                room, httpContext, enabledValidator, context.Store, environment, logger, new NoopBoardHubContext()));

        async Task<int?> InvokeCancelPrestage(int room, DefaultHttpContext httpContext) =>
            await ExecuteBindingResult(await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
                room, httpContext, enabledValidator, context.Store, logger, new NoopBoardHubContext()));

        async Task<int?> InvokeCancelSeating(int room, DefaultHttpContext httpContext) =>
            await ExecuteBindingResult(await global::RoomLifecycleEndpointHandler.CancelSeatingAsync(
                room, httpContext, enabledValidator, context.Store, logger, new NoopBoardHubContext()));

        async Task<int?> InvokeUpdateRoomAssignment(int room, DefaultHttpContext httpContext) =>
            await ExecuteBindingResult(await global::RoomLifecycleEndpointHandler.UpdateRoomAssignmentAsync(
                room, new BeginPrestageRequest(DoctorId: "pledger", ProcedureCode: "EXT"),
                httpContext, enabledValidator, context.Store, logger, new NoopBoardHubContext()));

        // Table-driven: the same missing-token / invalid-token / cross-room-token cases run for all
        // five routes. None of these should ever mutate a room, so rooms 1 and 2 are safely reused
        // across every route in the table (CreateBindingValidator configures room 1 -> "room-1-token",
        // room 2 -> "room-2-token").
        var routes = new (string Name, Func<int, DefaultHttpContext, Task<int?>> Invoke)[]
        {
            ("begin-prestage", InvokeBegin),
            ("seat", InvokeSeat),
            ("cancel-prestage", InvokeCancelPrestage),
            ("cancel-seating", InvokeCancelSeating),
            ("update-room-assignment", InvokeUpdateRoomAssignment)
        };

        foreach (var route in routes)
        {
            // Missing token -> 401, no mutation.
            Assert.Equal(401, await route.Invoke(1, NewRoomMutationHttpContext(1, token: null)));
            Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

            // Invalid token -> 403, no mutation.
            Assert.Equal(403, await route.Invoke(1, NewRoomMutationHttpContext(1, token: "garbage-token")));
            Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

            // Token bound to a different room (room 1's real token used against room 2) -> 403, no mutation.
            Assert.Equal(403, await route.Invoke(2, NewRoomMutationHttpContext(2, token: "room-1-token")));
            Assert.Equal(RoomStates.Available, context.Store.GetRoom(2)?.State);
        }

        // Disabled-binding passthrough is already exercised by every other test in this file via
        // CreateBindingValidator(enabled: false); no additional case is needed here.
    }

    [Fact]
    public async Task Prestaging_mutation_endpoints_return_404_for_unconfigured_room()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var environment = new TestWebHostEnvironment(workspace.ContentRoot, Environments.Production);
        var validator = CreateBindingValidator(enabled: false);
        const int unconfiguredRoom = 999;

        var begin = await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            unconfiguredRoom, new BeginPrestageRequest(DoctorId: "otte", ProcedureCode: "CON"),
            NewRoomMutationHttpContext(unconfiguredRoom, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(404, await ExecuteBindingResult(begin));

        var seat = await global::RoomLifecycleEndpointHandler.SeatAsync(
            unconfiguredRoom, NewRoomMutationHttpContext(unconfiguredRoom, token: null), validator,
            context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(404, await ExecuteBindingResult(seat));

        var cancelPrestage = await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
            unconfiguredRoom, NewRoomMutationHttpContext(unconfiguredRoom, token: null), validator,
            context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(404, await ExecuteBindingResult(cancelPrestage));

        var cancelSeating = await global::RoomLifecycleEndpointHandler.CancelSeatingAsync(
            unconfiguredRoom, NewRoomMutationHttpContext(unconfiguredRoom, token: null), validator,
            context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(404, await ExecuteBindingResult(cancelSeating));

        var updateRoomAssignment = await global::RoomLifecycleEndpointHandler.UpdateRoomAssignmentAsync(
            unconfiguredRoom, new BeginPrestageRequest(DoctorId: "otte", ProcedureCode: "CON"),
            NewRoomMutationHttpContext(unconfiguredRoom, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(404, await ExecuteBindingResult(updateRoomAssignment));
    }

    [Fact]
    public async Task Begin_prestage_and_compatibility_seat_endpoints_return_400_for_invalid_procedure_allocation()
    {
        using var workspace = TestWorkspace.Create();
        var procedureRosterOptions = new ProcedureRosterOptions
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
        };
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, procedureRosterOptions: procedureRosterOptions);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var environment = new TestWebHostEnvironment(workspace.ContentRoot, Environments.Production);
        var validator = CreateBindingValidator(enabled: false);

        var begin = await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            1, new BeginPrestageRequest(DoctorId: "otte", ProcedureCode: "BAD"),
            NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(begin));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        var seat = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, """{"doctorId":"otte","procedureCode":"BAD"}"""),
            validator, context.Store, environment, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(seat));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(2)?.State);
    }

    // -------------------------------------------------------------------------
    // Draft Update Room Assignment (store)
    //
    // Prestaging and Seated assignments remain provisional and correctable. Ready is the immutable
    // handoff boundary, including Aging/Stale urgency projections. Accepted changes preserve the
    // same episode and every existing phase timestamp.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(RoomStates.Prestaging)]
    [InlineData(RoomStates.Seated)]
    [InlineData(RoomStates.ReadyForDoctor)]
    [InlineData(RoomStates.Aging)]
    [InlineData(RoomStates.Stale)]
    public void Update_room_assignment_allows_draft_states_but_rejects_ready_lock_without_mutation(string effectiveState)
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace, environmentName: Environments.Production, agingMinutes: 7, staleMinutes: 12, timeProvider: clock);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON", expectedAllocationUnits: 1));
        if (effectiveState != RoomStates.Prestaging)
        {
            Assert.NotNull(context.Store.SeatRoom(1));
            if (effectiveState != RoomStates.Seated)
            {
                Assert.NotNull(context.Store.MarkReadyForDoctor(1));
                if (effectiveState == RoomStates.Aging)
                {
                    clock.SetUtcNow(now.AddMinutes(8)); // past aging (7), before stale (12)
                }
                else if (effectiveState == RoomStates.Stale)
                {
                    clock.SetUtcNow(now.AddMinutes(13)); // past stale (12)
                }
            }
        }

        // Ready is the primary state; Aging/Stale are derived urgency only.
        Assert.Equal(
            effectiveState is RoomStates.Aging or RoomStates.Stale ? RoomStates.ReadyForDoctor : effectiveState,
            context.Store.GetRoom(1)?.State);
        var before = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

        var updated = context.Store.UpdateRoomAssignment(1, "pledger", "EXT", sedation: true, expectedAllocationUnits: 5);

        var after = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

        // Preserved: episode identity and every existing phase timestamp.
        Assert.Equal(before.EpisodeId, after.EpisodeId);
        Assert.Equal(before.PrestageStartedAt, after.PrestageStartedAt);
        Assert.Equal(before.SeatedAt, after.SeatedAt);
        Assert.Equal(before.ReadyForDoctorAt, after.ReadyForDoctorAt);
        Assert.Null(after.DoctorArrivedAt);
        Assert.Null(after.DoctorCompleteAt);
        Assert.Null(after.RoomAvailableAt);

        if (effectiveState is RoomStates.Prestaging or RoomStates.Seated)
        {
            Assert.NotNull(updated);
            Assert.Equal("pledger", after.AssignedDoctor);
            Assert.Equal("Dr. Pledger", after.AssignedDoctorDisplayName);
            Assert.Equal("EXT+SED", after.ProcedureCode);
            Assert.Equal("Extraction + Sedation", after.ProcedureCategory);
            Assert.Equal(3, after.OriginalDefaultExpectedUnits);
            Assert.Equal(5, after.ExpectedAllocationUnits);
            Assert.Equal(50, after.ExpectedAllocationMinutes);
            Assert.True(after.AllocationAdjustedFromDefault);
        }
        else
        {
            Assert.Null(updated);
            Assert.Equal(before.AssignedDoctor, after.AssignedDoctor);
            Assert.Equal(before.ProcedureCode, after.ProcedureCode);
        }
    }

    [Theory]
    [InlineData(RoomStates.Available)]
    [InlineData(RoomStates.DoctorInRoom)]
    [InlineData(RoomStates.Turnover)]
    public void Update_room_assignment_rejects_disallowed_states_without_mutation(string disallowedState)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        if (disallowedState != RoomStates.Available)
        {
            Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
            Assert.NotNull(context.Store.MarkReadyForDoctor(1));
            Assert.NotNull(context.Store.MarkDoctorArrived(1));
            if (disallowedState == RoomStates.Turnover)
            {
                Assert.NotNull(context.Store.MarkDoctorComplete(1));
            }
        }

        Assert.Equal(disallowedState, context.Store.GetRoom(1)?.State);
        var before = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

        var result = context.Store.UpdateRoomAssignment(1, "pledger", "EXT");

        Assert.Null(result);
        var after = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(before.AssignedDoctor, after.AssignedDoctor);
        Assert.Equal(before.ProcedureCode, after.ProcedureCode);
        Assert.Equal(before.State, after.State);
    }

    [Fact]
    public void Update_room_assignment_rejects_when_doctor_arrived_at_is_set_even_if_state_is_otherwise_allowed()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 8, 2, 9, 0, 0, TimeSpan.Zero);
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // Directly persist a malformed row: State is one of the otherwise-allowed values (Seated),
        // but DoctorArrivedAt is set - a combination the store's own transitions never produce. This
        // proves the independent DoctorArrivedAt guard, not just the State allow-list.
        var malformed = new RoomState(1)
        {
            AssignedDoctor = "otte",
            AssignedDoctorDisplayName = "Dr. Otte",
            ProcedureCode = "CON",
            ProcedureCategory = "Consult",
            State = RoomStates.Seated,
            SeatedAt = now.AddMinutes(-30),
            DoctorArrivedAt = now.AddMinutes(-10),
            OriginalDefaultExpectedUnits = 1,
            ExpectedAllocationUnits = 1,
            ExpectedAllocationMinutes = 10
        };
        seed.Repository.SaveRoom(malformed, seed.Doctors, seed.Procedures);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var before = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Seated, before.State);
        Assert.NotNull(before.DoctorArrivedAt);

        var result = context.Store.UpdateRoomAssignment(1, "pledger", "EXT");

        Assert.Null(result);
        var after = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal("otte", after.AssignedDoctor);
        Assert.Equal("CON", after.ProcedureCode);
    }

    [Fact]
    public void Update_room_assignment_rejects_invalid_input_without_mutation()
    {
        using var workspace = TestWorkspace.Create();
        var procedureRosterOptions = new ProcedureRosterOptions
        {
            Procedures =
            [
                new() { Id = "consult", Code = "CON", Label = "Consult", Icon = "speech", Active = true, DefaultExpectedUnits = 1 },
                new() { Id = "extraction", Code = "EXT", Label = "Extraction", Icon = "forceps", Active = true, SedationEligible = true, AllocationBehavior = AllocationBehaviors.Variable, DefaultExpectedUnits = 3 },
                new() { Id = "bad-allocation", Code = "BAD", Label = "Bad allocation", Icon = "speech", Active = true, DefaultExpectedUnits = 0 }
            ]
        };
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, procedureRosterOptions: procedureRosterOptions);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var before = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

        Assert.Null(context.Store.UpdateRoomAssignment(1, "missing", "EXT"));
        Assert.Null(context.Store.UpdateRoomAssignment(1, "pledger", "NOPE"));
        Assert.Null(context.Store.UpdateRoomAssignment(1, "pledger", "CON", sedation: true)); // CON is not sedation-eligible
        Assert.Null(context.Store.UpdateRoomAssignment(1, "pledger", "BAD")); // procedure's own default allocation is invalid

        var after = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(before.AssignedDoctor, after.AssignedDoctor);
        Assert.Equal(before.ProcedureCode, after.ProcedureCode);
        Assert.Equal(before.EpisodeId, after.EpisodeId);
    }

    [Fact]
    public void Update_room_assignment_reload_preserves_corrected_assignment()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        Assert.NotNull(first.Store.BeginPrestage(1, "otte", "CON"));
        Assert.NotNull(first.Store.UpdateRoomAssignment(1, "pledger", "EXT", sedation: true, expectedAllocationUnits: 5));

        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reloaded = second.Store.GetRoom(1);

        Assert.NotNull(reloaded);
        Assert.Equal("pledger", reloaded.AssignedDoctor);
        Assert.Equal("EXT+SED", reloaded.ProcedureCode);
        Assert.Equal(5, reloaded.ExpectedAllocationUnits);
        Assert.Equal(RoomStates.Prestaging, reloaded.State);
    }

    [Fact]
    public void Update_room_assignment_creates_no_completed_cycle_and_no_aborted_assignment()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        Assert.NotNull(context.Store.UpdateRoomAssignment(1, "pledger", "EXT"));

        Assert.Empty(context.Store.GetReports().RecentCompletedCycles);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
    }

    [Fact]
    public void Second_update_room_assignment_reuses_the_same_episode_without_creating_a_second_record()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var originalEpisodeId = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1).EpisodeId;
        Assert.NotNull(originalEpisodeId);

        Assert.NotNull(context.Store.UpdateRoomAssignment(1, "pledger", "EXT"));
        Assert.NotNull(context.Store.UpdateRoomAssignment(1, "gibson", "BX", sedation: true, expectedAllocationUnits: 4));

        var after = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(originalEpisodeId, after.EpisodeId);
        Assert.Equal("gibson", after.AssignedDoctor);
        Assert.Equal("BX+SED", after.ProcedureCode);
        Assert.Equal(4, after.ExpectedAllocationUnits);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
        Assert.Empty(context.Store.GetReports().RecentCompletedCycles);
    }

    [Fact]
    public void Update_room_assignment_preserves_legacy_null_episode_and_prestage_fields()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        // A legacy row predating the Prestaging feature: null EpisodeId, null PrestageStartedAt, but a
        // real SeatedAt/ReadyForDoctorAt from before the feature shipped.
        var legacyRoom = new RoomState(1)
        {
            AssignedDoctor = "otte",
            AssignedDoctorDisplayName = "Dr. Otte",
            ProcedureCode = "CON",
            ProcedureCategory = "Consult",
            State = RoomStates.ReadyForDoctor,
            SeatedAt = now.AddMinutes(-20),
            ReadyForDoctorAt = now.AddMinutes(-15),
            OriginalDefaultExpectedUnits = 1,
            ExpectedAllocationUnits = 1,
            ExpectedAllocationMinutes = 10
        };
        seed.Repository.SaveRoom(legacyRoom, seed.Doctors, seed.Procedures);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var before = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Null(before.EpisodeId);
        Assert.Null(before.PrestageStartedAt);

        var updated = context.Store.UpdateRoomAssignment(1, "pledger", "EXT", sedation: true, expectedAllocationUnits: 5);

        Assert.Null(updated);
        var after = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Null(after.EpisodeId);
        Assert.Null(after.PrestageStartedAt);
        Assert.Equal(before.SeatedAt, after.SeatedAt);
        Assert.Equal(before.ReadyForDoctorAt, after.ReadyForDoctorAt);
        Assert.Equal(before.AssignedDoctor, after.AssignedDoctor);
        Assert.Equal(before.ProcedureCode, after.ProcedureCode);
    }

    [Fact]
    public void Update_room_assignment_followed_by_cancellation_archives_the_corrected_assignment()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        Assert.NotNull(context.Store.UpdateRoomAssignment(1, "pledger", "EXT", sedation: true, expectedAllocationUnits: 5));

        Assert.NotNull(context.Store.CancelPrestage(1, CancellationReasons.ProcedureChanged));

        var abort = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal("pledger", abort.AssignedDoctor);
        Assert.Equal("EXT+SED", abort.ProcedureCode);
        Assert.Equal(5, abort.ExpectedAllocationUnits);
    }

    [Fact]
    public void Update_room_assignment_followed_by_doctor_arrived_creates_completed_cycle_from_corrected_assignment()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        Assert.NotNull(context.Store.SeatRoom(1));
        Assert.NotNull(context.Store.UpdateRoomAssignment(1, "pledger", "EXT", sedation: true, expectedAllocationUnits: 5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        Assert.Equal("pledger", cycle.AssignedDoctor);
        Assert.Equal("EXT+SED", cycle.ProcedureCode);
        Assert.Equal(5, cycle.ExpectedAllocationUnits);
    }

    // -------------------------------------------------------------------------
    // Pre-arrival Update Room Assignment (API)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Update_room_assignment_endpoint_succeeds_from_an_allowed_state()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var validator = CreateBindingValidator(enabled: false);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var before = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

        var response = await global::RoomLifecycleEndpointHandler.UpdateRoomAssignmentAsync(
            1, new BeginPrestageRequest(DoctorId: "pledger", ProcedureCode: "EXT", Sedation: true, ExpectedAllocationUnits: 5),
            NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());

        Assert.Equal(200, await ExecuteBindingResult(response));
        var after = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(before.EpisodeId, after.EpisodeId);
        Assert.Equal(before.PrestageStartedAt, after.PrestageStartedAt);
        Assert.Equal(RoomStates.Prestaging, after.State);
        Assert.Equal("pledger", after.AssignedDoctor);
        Assert.Equal("EXT+SED", after.ProcedureCode);
        Assert.Equal(5, after.ExpectedAllocationUnits);
    }

    [Theory]
    [InlineData(RoomStates.Available)]
    [InlineData(RoomStates.DoctorInRoom)]
    [InlineData(RoomStates.Turnover)]
    public async Task Update_room_assignment_endpoint_rejects_disallowed_states(string disallowedState)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var validator = CreateBindingValidator(enabled: false);

        if (disallowedState != RoomStates.Available)
        {
            Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
            Assert.NotNull(context.Store.MarkReadyForDoctor(1));
            Assert.NotNull(context.Store.MarkDoctorArrived(1));
            if (disallowedState == RoomStates.Turnover)
            {
                Assert.NotNull(context.Store.MarkDoctorComplete(1));
            }
        }

        Assert.Equal(disallowedState, context.Store.GetRoom(1)?.State);

        var response = await global::RoomLifecycleEndpointHandler.UpdateRoomAssignmentAsync(
            1, new BeginPrestageRequest(DoctorId: "pledger", ProcedureCode: "EXT"),
            NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());

        Assert.Equal(400, await ExecuteBindingResult(response));
        Assert.Equal(disallowedState, context.Store.GetRoom(1)?.State);
    }

    [Fact]
    public async Task Update_room_assignment_endpoint_rejects_blank_and_conflicting_procedure_aliases_before_mutation()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var validator = CreateBindingValidator(enabled: false);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var before = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

        var blank = await global::RoomLifecycleEndpointHandler.UpdateRoomAssignmentAsync(
            1, new BeginPrestageRequest(DoctorId: "pledger", ProcedureCode: "", ProcedureId: "EXT"),
            NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(blank));

        var conflicting = await global::RoomLifecycleEndpointHandler.UpdateRoomAssignmentAsync(
            1, new BeginPrestageRequest(DoctorId: "pledger", ProcedureCode: "EXT", ProcedureId: "CON"),
            NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(conflicting));

        var after = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(before.AssignedDoctor, after.AssignedDoctor);
        Assert.Equal(before.ProcedureCode, after.ProcedureCode);
    }

    [Fact]
    public async Task Update_room_assignment_endpoint_returns_400_for_invalid_doctor_procedure_and_allocation()
    {
        using var workspace = TestWorkspace.Create();
        var procedureRosterOptions = new ProcedureRosterOptions
        {
            Procedures =
            [
                new() { Id = "consult", Code = "CON", Label = "Consult", Icon = "speech", Active = true, DefaultExpectedUnits = 1 },
                new() { Id = "bad-allocation", Code = "BAD", Label = "Bad allocation", Icon = "speech", Active = true, DefaultExpectedUnits = 0 }
            ]
        };
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, procedureRosterOptions: procedureRosterOptions);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var validator = CreateBindingValidator(enabled: false);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));

        var invalidDoctor = await global::RoomLifecycleEndpointHandler.UpdateRoomAssignmentAsync(
            1, new BeginPrestageRequest(DoctorId: "missing", ProcedureCode: "CON"),
            NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(invalidDoctor));

        var invalidProcedure = await global::RoomLifecycleEndpointHandler.UpdateRoomAssignmentAsync(
            1, new BeginPrestageRequest(DoctorId: "pledger", ProcedureCode: "NOPE"),
            NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(invalidProcedure));

        var invalidAllocation = await global::RoomLifecycleEndpointHandler.UpdateRoomAssignmentAsync(
            1, new BeginPrestageRequest(DoctorId: "pledger", ProcedureCode: "BAD"),
            NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(invalidAllocation));

        Assert.Equal("otte", context.Store.GetRoom(1)?.AssignedDoctor);
        Assert.Equal("CON", context.Store.GetRoom(1)?.ProcedureCode);
    }

    [Fact]
    public async Task Cancel_prestage_endpoint_handles_reasons_and_lifecycle_guards()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var validator = CreateBindingValidator(enabled: false);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var nullReason = await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
            1, NewRoomMutationHttpContext(1, token: null), validator,
            context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(nullReason));
        Assert.Null(Assert.Single(context.Repository.LoadAbortedAssignments()).CancellationReason);

        Assert.NotNull(context.Store.BeginPrestage(2, "pledger", "EXT"));
        var validReasonBody = $"{{\"cancellationReason\":\"{CancellationReasons.MovedRoom}\"}}";
        var validReason = await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
            2, NewJsonBodyContext(2, token: null, validReasonBody),
            validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(validReason));
        Assert.Equal(CancellationReasons.MovedRoom,
            context.Repository.LoadAbortedAssignments().Single(record => record.RoomId == 2).CancellationReason);

        Assert.NotNull(context.Store.BeginPrestage(3, "gibson", "CON"));
        var blank = await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
            3, NewJsonBodyContext(3, token: null, """{"cancellationReason":" "}"""),
            validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(blank));
        var unknown = await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
            3, NewJsonBodyContext(3, token: null, """{"cancellationReason":"unknown"}"""),
            validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(unknown));
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(3)?.State);

        Assert.NotNull(context.Store.SeatRoom(3));
        var wrongState = await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
            3, NewRoomMutationHttpContext(3, token: null), validator,
            context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(wrongState));
    }

    [Fact]
    public async Task Cancel_seating_endpoint_forwards_optional_reason_and_keeps_reasonless_callers_compatible()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var validator = CreateBindingValidator(enabled: false);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        var reasonedBody = $"{{\"cancellationReason\":\"{CancellationReasons.ProcedureChanged}\"}}";
        var reasoned = await global::RoomLifecycleEndpointHandler.CancelSeatingAsync(
            1, NewJsonBodyContext(1, token: null, reasonedBody),
            validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(reasoned));
        Assert.Equal(CancellationReasons.ProcedureChanged,
            context.Repository.LoadAbortedAssignments().Single(record => record.RoomId == 1).CancellationReason);

        Assert.NotNull(SeatViaPrestage(context.Store, 2, "pledger", "EXT"));
        var reasonless = await global::RoomLifecycleEndpointHandler.CancelSeatingAsync(
            2, NewRoomMutationHttpContext(2, token: null), validator,
            context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(reasonless));
        Assert.Null(context.Repository.LoadAbortedAssignments().Single(record => record.RoomId == 2).CancellationReason);
    }

    [Fact]
    public void Ready_for_doctor_blocks_doctor_arrived_until_called()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        var seated = SeatViaPrestage(context.Store, 1, "otte", "CON");
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
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        var arrived = context.Store.MarkDoctorArrived(1);
        Assert.NotNull(arrived);
        Assert.Equal(RoomStates.DoctorInRoom, arrived.State);
        Assert.Single(context.Store.GetReports().DoctorSummaries);
    }

    [Fact]
    public void Aging_ready_urgency_allows_doctor_arrived_and_captures_threshold()
    {
        // Aging is a projection of the Ready for Doctor phase, not a primary lifecycle state.
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
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(now.AddMinutes(8)); // past aging (7) but before stale (12)

        var aging = context.Store.GetRoom(1);
        Assert.NotNull(aging);
        Assert.Equal(RoomStates.ReadyForDoctor, aging.State);
        Assert.Equal(ReadyUrgency.Aging, aging.ReadyUrgency);
        Assert.Null(aging.AgingStartedAt);
        Assert.Null(aging.StaleStartedAt);

        var arrived = context.Store.MarkDoctorArrived(1);
        Assert.NotNull(arrived);
        Assert.Equal(RoomStates.DoctorInRoom, arrived.State);

        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(RoomStates.ReadyForDoctor, cycle.FinalWaitState);
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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
            Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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
    public void Board_ui_demo_timer_defaults_to_development_only_and_training_cannot_enable_it()
    {
        using var workspace = TestWorkspace.Create();

        var development = StoreContext.Create(workspace, environmentName: Environments.Development);
        var training = StoreContext.Create(
            workspace,
            environmentName: ChairSideEnvironmentNames.Training,
            databasePath: Path.Combine(workspace.DataRoot, "chairside-training.db"));
        var trainingConfigured = StoreContext.Create(
            workspace,
            environmentName: ChairSideEnvironmentNames.Training,
            databasePath: Path.Combine(workspace.DataRoot, "chairside-training-configured.db"),
            boardUiOptions: new BoardUiOptions { DemoTimerEnabled = true });
        var production = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: workspace.ProductionDatabasePath());
        var productionEnabled = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: Path.Combine(workspace.DataRoot, "chairside-demo-enabled.db"),
            boardUiOptions: new BoardUiOptions { DemoTimerEnabled = true });

        Assert.True(development.Store.GetSnapshot().DemoTimerEnabled);
        Assert.False(training.Store.GetSnapshot().DemoTimerEnabled);
        Assert.False(trainingConfigured.Store.GetSnapshot().DemoTimerEnabled);
        Assert.False(production.Store.GetSnapshot().DemoTimerEnabled);
        Assert.True(productionEnabled.Store.GetSnapshot().DemoTimerEnabled);
    }

    [Fact]
    public void Board_snapshot_identifies_only_the_Training_environment()
    {
        using var workspace = TestWorkspace.Create();

        var development = StoreContext.Create(
            workspace,
            environmentName: ChairSideEnvironmentNames.Development,
            databasePath: Path.Combine(workspace.DataRoot, "snapshot-development.db"));
        var training = StoreContext.Create(
            workspace,
            environmentName: ChairSideEnvironmentNames.Training,
            databasePath: Path.Combine(workspace.DataRoot, "snapshot-training.db"));
        var production = StoreContext.Create(
            workspace,
            environmentName: ChairSideEnvironmentNames.Production,
            databasePath: Path.Combine(workspace.DataRoot, "snapshot-production.db"));

        Assert.False(development.Store.GetSnapshot().IsTraining);
        Assert.True(training.Store.GetSnapshot().IsTraining);
        Assert.False(production.Store.GetSnapshot().IsTraining);
    }

    [Fact]
    public void Client_Training_badge_is_snapshot_driven_shared_and_duplicate_safe()
    {
        var root = FindRepositoryRoot();
        var boardScript = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "board.js"));
        var styles = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "styles.css"));

        Assert.Contains("syncTrainingEnvironmentBadge(snapshot.isTraining === true)", boardScript, StringComparison.Ordinal);
        Assert.Contains("document.getElementById(\"trainingEnvironmentBadge\")", boardScript, StringComparison.Ordinal);
        Assert.Contains("if (!isTraining)", boardScript, StringComparison.Ordinal);
        Assert.Contains("badge?.remove()", boardScript, StringComparison.Ordinal);
        Assert.Contains("if (badge)", boardScript, StringComparison.Ordinal);
        Assert.Contains("document.querySelector(\".brand-lockup\")", boardScript, StringComparison.Ordinal);
        Assert.Contains("badge.textContent = \"TRAINING\"", boardScript, StringComparison.Ordinal);
        Assert.DoesNotContain("hostname", boardScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".training-environment-badge", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Zero_demo_elapsed_uses_current_time_regardless_of_demo_timer_visibility()
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

        var enabledRoom = SeatViaPrestage(enabled.Store, 1, "otte", "CON");
        var disabledRoom = SeatViaPrestage(disabled.Store, 1, "otte", "CON");

        Assert.NotNull(enabledRoom);
        Assert.NotNull(disabledRoom);
        Assert.Equal(now, enabledRoom.SeatedAt);
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
    public async Task Resolve_conflict_endpoint_writes_audit_entries_for_resolving_and_auto_completed_rooms()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var httpContext = NewResolveConflictHttpContext(roomNumber: 2, token: "room-2-token");

        DriveRoomToDoctorInRoom(context, 1, "otte", "CON");
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        var response = await global::DoctorArrivalConflictEndpointHandler.ResolveAsync(
            2,
            new ResolveDoctorArrivalConflictRequest(1),
            httpContext,
            CreateBindingValidator(enabled: true),
            context.Store,
            logger,
            new NoopBoardHubContext());

        Assert.Equal(200, await ExecuteBindingResult(response));

        var oldRoom = context.Store.GetRoom(1)!;
        Assert.Equal(RoomStates.Turnover, oldRoom.State);
        Assert.NotNull(oldRoom.DoctorCompleteAt);
        Assert.Null(oldRoom.RoomAvailableAt);

        var newRoom = context.Store.GetRoom(2)!;
        Assert.Equal(RoomStates.DoctorInRoom, newRoom.State);
        Assert.NotNull(newRoom.DoctorArrivedAt);

        var entries = await ReadRoomAuditEntries(Path.Combine(workspace.DataRoot, "logs"));
        Assert.Contains(entries, entry =>
            entry.Action == "doctor-arrived-resolve"
            && entry.RoomNumber == 2
            && entry.Success);
        var autocomplete = Assert.Single(entries, entry => entry.Action == "doctor-arrived-resolve-autocomplete");
        Assert.Equal(1, autocomplete.RoomNumber);
        Assert.Equal(RoomStates.DoctorInRoom, autocomplete.PreviousState);
        Assert.Equal(RoomStates.Turnover, autocomplete.NewState);
        Assert.Equal("otte", autocomplete.DoctorId);
        Assert.Equal("CON", autocomplete.ProcedureCode);
        Assert.Equal("auto-completed-by-resolving-room-2", autocomplete.Reason);
    }

    [Fact]
    public async Task Resolve_conflict_endpoint_does_not_write_autocomplete_audit_for_stale_conflict()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var httpContext = NewResolveConflictHttpContext(roomNumber: 2, token: "room-2-token");

        DriveRoomToDoctorInRoom(context, 1, "otte", "CON");
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));

        var response = await global::DoctorArrivalConflictEndpointHandler.ResolveAsync(
            2,
            new ResolveDoctorArrivalConflictRequest(1),
            httpContext,
            CreateBindingValidator(enabled: true),
            context.Store,
            logger,
            new NoopBoardHubContext());

        Assert.Equal(409, await ExecuteBindingResult(response));
        Assert.Equal(RoomStates.Turnover, context.Store.GetRoom(1)!.State);
        Assert.Equal(RoomStates.ReadyForDoctor, context.Store.GetRoom(2)!.State);

        var entries = await ReadRoomAuditEntries(Path.Combine(workspace.DataRoot, "logs"));
        Assert.DoesNotContain(entries, entry => entry.Action == "doctor-arrived-resolve-autocomplete");
        Assert.Contains(entries, entry =>
            entry.Action == "doctor-arrived-resolve"
            && entry.RoomNumber == 2
            && !entry.Success
            && entry.Reason == "conflict-stale");
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        // Cycle B: only reached DoctorArrived on room 2 - will be marked exception.
        clock.SetUtcNow(now.AddMinutes(30));
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "pledger", "EXT"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        var arrivedA = context.Store.MarkDoctorArrived(1);
        Assert.NotNull(arrivedA);

        // Cycle B: only reached DoctorArrived - will be marked exception.
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "pledger", "EXT"));
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
        Assert.NotNull(SeatViaPrestage(first.Store, 1, "otte", "CON"));
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
    public void Room_expiration_options_defaults_are_locked()
    {
        var options = new RoomExpirationOptions();

        Assert.True(options.Enabled);
        Assert.Equal(8, options.MaxActiveDurationHours);
        Assert.True(options.AfterHoursSweepEnabled);
        Assert.Equal("19:00", options.AfterHoursSweepTime);
        Assert.Equal("America/Chicago", options.TimeZone);
    }

    [Fact]
    public void Prestaging_before_max_duration_is_not_expired()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero);
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

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "EXT", expectedAllocationUnits: 5));
        clock.SetUtcNow(now.AddHours(7).AddMinutes(59));

        Assert.Empty(context.Store.CheckAndExpireActiveCycles());
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(1)?.State);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
        Assert.Empty(context.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Absent_assignment_prestaging_max_duration_expiration_preserves_truthful_abort_history()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 3, 18, 0, 0, TimeSpan.Zero);
        var seed = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);
        seed.Repository.SaveRooms(
            [
                new RoomState(1)
                {
                    State = RoomStates.Prestaging,
                    PrestageStartedAt = now.AddHours(-9)
                }
            ],
            seed.Doctors,
            seed.Procedures);

        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: new ManualTimeProvider(now),
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.Equal([1], context.Store.CheckAndExpireActiveCycles());
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Null(aborted.AssignedDoctor);
        Assert.Null(aborted.ProcedureCode);
        Assert.Null(aborted.SeatedAt);
        Assert.Empty(context.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Prestaging_over_max_duration_persists_complete_abort_and_reset_across_reload()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var prestageAt = new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero);
        var terminatedAt = prestageAt.AddHours(8).AddSeconds(1);
        var clock = new ManualTimeProvider(prestageAt);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "EXT", sedation: true, expectedAllocationUnits: 5));
        var active = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        clock.SetUtcNow(terminatedAt);

        Assert.Equal([1], context.Store.CheckAndExpireActiveCycles());
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        var abort = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(active.EpisodeId, abort.EpisodeId);
        Assert.Equal(1, abort.RoomId);
        Assert.Equal("otte", abort.AssignedDoctor);
        Assert.Equal("Dr. Otte", abort.AssignedDoctorDisplayName);
        Assert.Equal("EXT+SED", abort.ProcedureCode);
        Assert.Equal("Extraction + Sedation", abort.ProcedureCategory);
        Assert.Equal(3, abort.OriginalDefaultExpectedUnits);
        Assert.Equal(5, abort.ExpectedAllocationUnits);
        Assert.Equal(50, abort.ExpectedAllocationMinutes);
        Assert.True(abort.AllocationAdjustedFromDefault);
        Assert.Equal(prestageAt, abort.PrestageStartedAt);
        Assert.Null(abort.SeatedAt);
        Assert.Null(abort.ReadyForDoctorAt);
        Assert.Equal(terminatedAt, abort.TerminatedAt);
        Assert.Equal(RoomStates.Prestaging, abort.TerminatedFromState);
        Assert.Equal(TerminationKinds.MaxDurationExpired, abort.TerminationKind);
        Assert.Null(abort.CancellationReason);
        Assert.Empty(context.Repository.LoadCompletedCycles());

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);
        var durableRoom = reloaded.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Available, durableRoom.State);
        Assert.Null(durableRoom.EpisodeId);
        Assert.Null(durableRoom.PrestageStartedAt);
        Assert.Null(durableRoom.SeatedAt);
        AssertSameAbortedAssignment(abort, Assert.Single(reloaded.Repository.LoadAbortedAssignments()));
        Assert.Empty(reloaded.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Prestaging_max_duration_expiration_is_idempotent_after_success()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero);
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

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));

        Assert.Equal([1], context.Store.CheckAndExpireActiveCycles());
        Assert.Empty(context.Store.CheckAndExpireActiveCycles());
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Empty(context.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Prestaging_after_hours_sweep_uses_clinic_time_and_runs_once_per_clinic_day()
    {
        using var workspace = TestWorkspace.Create();
        var beforeCutoff = new DateTimeOffset(2026, 7, 3, 23, 30, 0, TimeSpan.Zero); // 18:30 CDT
        var afterCutoff = beforeCutoff.AddHours(1); // 19:30 CDT, same clinic day
        var clock = new ManualTimeProvider(beforeCutoff);
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

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        Assert.Empty(context.Store.TryRunAfterHoursSweep());

        clock.SetUtcNow(afterCutoff);
        Assert.Equal([1], context.Store.TryRunAfterHoursSweep());
        var abort = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(RoomStates.Prestaging, abort.TerminatedFromState);
        Assert.Equal(TerminationKinds.AfterHoursExpired, abort.TerminationKind);
        Assert.Equal(afterCutoff, abort.TerminatedAt);
        Assert.Null(abort.SeatedAt);
        Assert.Null(abort.CancellationReason);
        Assert.Empty(context.Repository.LoadCompletedCycles());

        Assert.NotNull(context.Store.BeginPrestage(2, "pledger", "EXT"));
        clock.SetUtcNow(afterCutoff.AddMinutes(10));
        Assert.Empty(context.Store.TryRunAfterHoursSweep());
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(2)?.State);
        Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Empty(context.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Absent_assignment_prestaging_after_hours_expiration_preserves_truthful_abort_history()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 3, 19, 0, 0, TimeSpan.Zero);
        var seed = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);
        seed.Repository.SaveRooms(
            [
                new RoomState(1)
                {
                    State = RoomStates.Prestaging,
                    PrestageStartedAt = now.AddMinutes(-30)
                }
            ],
            seed.Doctors,
            seed.Procedures);

        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: new ManualTimeProvider(now),
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "19:00",
                TimeZone = "UTC"
            });

        Assert.Equal([1], context.Store.TryRunAfterHoursSweep());
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Null(aborted.AssignedDoctor);
        Assert.Null(aborted.ProcedureCode);
        Assert.Null(aborted.SeatedAt);
        Assert.Empty(context.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Manual_mark_as_exception_moves_cycle_from_normal_to_exceptions()
    {
        // The admin marks a completed cycle as ManualReview - it should disappear from
        // normal metrics and appear in ExceptionCycles with the default reason/action.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));

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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        var active = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        // Advance past 8-hour limit.
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        var expired = context.Store.CheckAndExpireActiveCycles();

        Assert.Equal([1], expired);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(active.EpisodeId, aborted.EpisodeId);
        Assert.Equal(active.PrestageStartedAt, aborted.PrestageStartedAt);
        Assert.Equal(TerminationKinds.MaxDurationExpired, aborted.TerminationKind);
        Assert.Equal(0, context.Store.GetReports().CompletedRoomCyclesCount);
        Assert.Empty(context.Store.GetReports().ExceptionCycles);
        Assert.Empty(context.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Active_room_over_max_duration_with_doctor_arrived_is_expired_with_review_timing_suggestion()
    {
        // Room reached DoctorArrived - post-arrival expiration releases the room and records a
        // review-required exception cycle with SuggestedAction "Review timing". It must not fabricate
        // DoctorCompleteAt and must not create aborted pre-arrival history.
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));

        // Advance past 8-hour limit.
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        var expired = context.Store.CheckAndExpireActiveCycles();

        Assert.Equal([1], expired);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        var reports = context.Store.GetReports();
        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.Equal(1, exception.RoomId);
        Assert.Equal(ExceptionReasons.ExceededMaxActiveDuration, exception.ExceptionReason);
        Assert.Equal("Review timing", exception.SuggestedAction);
        Assert.NotNull(exception.DoctorArrivedAt);
        Assert.Null(exception.DoctorCompleteAt);

        // Post-arrival expiration is not throughput and not aborted pre-arrival history.
        Assert.Equal(0, reports.CompletedRoomCyclesCount);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "pledger", "EXT"));
        var activeRooms = context.Repository.LoadRooms(3)
            .Where(room => room.RoomId is 1 or 2)
            .ToDictionary(room => room.RoomId);

        var expired = context.Store.TryRunAfterHoursSweep();

        Assert.Equal(2, expired.Count);
        Assert.Contains(1, expired);
        Assert.Contains(2, expired);

        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(2)?.State);

        var aborted = context.Repository.LoadAbortedAssignments();
        Assert.Equal(2, aborted.Count);
        Assert.All(aborted, record =>
        {
            Assert.Equal(TerminationKinds.AfterHoursExpired, record.TerminationKind);
            Assert.Equal(activeRooms[record.RoomId].EpisodeId, record.EpisodeId);
            Assert.Equal(activeRooms[record.RoomId].PrestageStartedAt, record.PrestageStartedAt);
        });
        Assert.Empty(context.Repository.LoadCompletedCycles());
        Assert.Empty(context.Store.GetReports().ExceptionCycles);
        Assert.Equal(0, context.Store.GetReports().CompletedRoomCyclesCount);
    }

    [Fact]
    public void Legacy_seated_room_with_null_episode_and_prestage_expires_safely()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 3, 18, 0, 0, TimeSpan.Zero);
        var seatedAt = now.AddHours(-9);
        var seed = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);
        seed.Repository.SaveRooms(
            [
                new RoomState(1)
                {
                    AssignedDoctor = "otte",
                    ProcedureCode = "CON",
                    State = RoomStates.Seated,
                    SeatedAt = seatedAt,
                    OriginalDefaultExpectedUnits = 1,
                    ExpectedAllocationUnits = 1,
                    ExpectedAllocationMinutes = 10
                }
            ],
            seed.Doctors,
            seed.Procedures);

        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.Equal([1], context.Store.CheckAndExpireActiveCycles());
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.NotNull(aborted.EpisodeId);
        Assert.Null(aborted.PrestageStartedAt);
        Assert.Equal(seatedAt, aborted.SeatedAt);
        Assert.Equal(TerminationKinds.MaxDurationExpired, aborted.TerminationKind);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        Assert.Empty(context.Repository.LoadCompletedCycles());
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));

        // First sweep: expires room 1.
        var firstSweep = context.Store.TryRunAfterHoursSweep();
        Assert.Equal([1], firstSweep);

        // Re-seat room 1 (simulate activity resuming).
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));

        // Second sweep on the same clinic day (even 10 minutes later): should not fire.
        clock.SetUtcNow(now.AddMinutes(10));
        var secondSweep = context.Store.TryRunAfterHoursSweep();
        Assert.Empty(secondSweep);

        // Only the one aborted pre-arrival episode from the first sweep should exist.
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(TerminationKinds.AfterHoursExpired, aborted.TerminationKind);
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));

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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));

        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        var expired = context.Store.CheckAndExpireActiveCycles();

        Assert.Equal([1], expired);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(TerminationKinds.MaxDurationExpired, aborted.TerminationKind);
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));

        var expired = context.Store.TryRunAfterHoursSweep();

        Assert.Equal([1], expired);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(TerminationKinds.AfterHoursExpired, aborted.TerminationKind);
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
        // Post-arrival expiration releases the room and records the review-required exception cycle,
        // but must NEVER set DoctorCompleteAt (Doctor Complete was never called).
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        // Note: MarkDoctorComplete is intentionally NOT called.

        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        Assert.Equal([1], context.Store.CheckAndExpireActiveCycles());

        // Room is released, not stranded in DoctorInRoom.
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        var exception = Assert.Single(context.Store.GetReports().ExceptionCycles);
        Assert.Null(exception.DoctorCompleteAt);
    }

    [Fact]
    public void After_hours_sweep_expires_arrived_room_as_review_required_exception_cycle()
    {
        // The after-hours sweep must handle an already-arrived room the same way as the max-duration
        // check: release it and record a review-required exception cycle, without fabricating
        // DoctorCompleteAt or aborted pre-arrival history.
        using var workspace = TestWorkspace.Create();
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));

        var expired = context.Store.TryRunAfterHoursSweep();

        Assert.Equal([1], expired);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        var exception = Assert.Single(context.Store.GetReports().ExceptionCycles);
        Assert.Equal(1, exception.RoomId);
        Assert.Equal(ExceptionReasons.AfterHoursSweep, exception.ExceptionReason);
        Assert.Equal("Review timing", exception.SuggestedAction);
        Assert.NotNull(exception.DoctorArrivedAt);
        Assert.Null(exception.DoctorCompleteAt);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
    }

    [Fact]
    public void Pre_arrival_seated_room_over_max_duration_expires_into_aborted_history_not_throughput()
    {
        // Seated (In Prep) but never marked Ready or Arrived: pre-arrival expiration must record
        // aborted history and create no completed/exception throughput cycle.
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        var active = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(1)?.State);

        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        Assert.Equal([1], context.Store.CheckAndExpireActiveCycles());

        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(active.EpisodeId, aborted.EpisodeId);
        Assert.Equal(TerminationKinds.MaxDurationExpired, aborted.TerminationKind);

        // No throughput: no completed or exception cycles.
        Assert.Empty(context.Repository.LoadCompletedCycles());
        Assert.Equal(0, context.Store.GetReports().CompletedRoomCyclesCount);
        Assert.Empty(context.Store.GetReports().ExceptionCycles);
    }

    [Fact]
    public void Post_arrival_expiration_persists_review_required_exception_cycle_across_reload()
    {
        // Durable-before-live: after post-arrival expiration a fresh store on the same database must
        // observe the released room and the persisted review-required exception cycle, with a truthful
        // DoctorArrivedAt and no fabricated DoctorCompleteAt.
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        Assert.Equal([1], context.Store.CheckAndExpireActiveCycles());

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock);
        Assert.Equal(RoomStates.Available, reloaded.Store.GetRoom(1)?.State);
        var exception = Assert.Single(reloaded.Store.GetReports().ExceptionCycles);
        Assert.Equal(1, exception.RoomId);
        Assert.Equal("Review timing", exception.SuggestedAction);
        Assert.NotNull(exception.DoctorArrivedAt);
        Assert.Null(exception.DoctorCompleteAt);
    }

    [Fact]
    public void Expired_exception_cycles_are_excluded_from_normal_metrics()
    {
        // Normal completed cycle (room 1) + post-arrival force-expired exception cycle (room 2):
        // only the normal cycle contributes to throughput/metrics; the review-required exception is
        // excluded. Post-arrival expiration preserves DoctorArrivedAt and never fabricates
        // DoctorCompleteAt or aborted pre-arrival history.
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
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        // Room 2: reaches Doctor Arrived, then gets force-expired - review-required exception cycle.
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "pledger", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2));
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        Assert.Equal([2], context.Store.CheckAndExpireActiveCycles());

        // Room 2 is released.
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(2)?.State);

        var reports = context.Store.GetReports();
        // Normal throughput/metric population excludes the exception cycle.
        Assert.Equal(1, reports.CompletedRoomCyclesCount);
        Assert.Single(reports.RecentCompletedCycles);
        Assert.Equal(1, reports.RecentCompletedCycles[0].RoomId);
        Assert.DoesNotContain(reports.RecentCompletedCycles, cycle => cycle.RoomId == 2);

        // The exception cycle exists for room 2, preserving DoctorArrivedAt and no DoctorCompleteAt.
        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.Equal(2, exception.RoomId);
        Assert.NotNull(exception.DoctorArrivedAt);
        Assert.Null(exception.DoctorCompleteAt);

        // Post-arrival expiration records no aborted pre-arrival history.
        Assert.Empty(context.Repository.LoadAbortedAssignments());
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

        // Room reaches Doctor Arrived, then is force-expired past the max active duration.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        Assert.Equal([1], context.Store.CheckAndExpireActiveCycles());

        // Room is released.
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        var reports = context.Store.GetReports();
        // Excluded from the normal completed population...
        Assert.Empty(reports.RecentCompletedCycles);
        // ...and present in the pending-review exceptions population.
        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.Equal(1, exception.RoomId);
        Assert.True(exception.RequiresReview);
        Assert.Equal(ReviewStatuses.PendingReview, exception.ReviewStatus);
        Assert.Equal(ExceptionReasons.ExceededMaxActiveDuration, exception.ExceptionReason);
        Assert.Equal("Review timing", exception.SuggestedAction);
        Assert.NotNull(exception.DoctorArrivedAt);
        Assert.Null(exception.DoctorCompleteAt);

        // Post-arrival expiration records no aborted pre-arrival history.
        Assert.Empty(context.Repository.LoadAbortedAssignments());
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

        Assert.NotNull(SeatViaPrestage(first.Store, 1, "otte", "CON"));

        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        var expired = first.Store.CheckAndExpireActiveCycles();
        Assert.Equal([1], expired);

        // Verify in-memory: room available, aborted pre-arrival history recorded.
        Assert.Equal(RoomStates.Available, first.Store.GetRoom(1)?.State);
        Assert.Single(first.Repository.LoadAbortedAssignments());

        // Reload from DB: room must still be Available, aborted history preserved.
        var second = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);

        Assert.Equal(RoomStates.Available, second.Store.GetRoom(1)?.State);
        var aborted = Assert.Single(second.Repository.LoadAbortedAssignments());
        Assert.Equal(1, aborted.RoomId);
        Assert.Equal(TerminationKinds.MaxDurationExpired, aborted.TerminationKind);
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2)); // DoctorArrivedAt = base_+0

        // Room 1 (target): ready at t=5, arrives at t=20.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2)); // DoctorArrivedAt = base_

        // Room 1 (target): seat now, ready at t=5.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2));

        // Room 1 (pledger): seat, ready at t=5, arrive at t=20.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "pledger", "EXT"));
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        clock.SetUtcNow(now.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(now.AddMinutes(15));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(now.AddMinutes(20));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        // Exception cycle: Room 2, same doctor, mark as exception.
        clock.SetUtcNow(now.AddMinutes(25));
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "CON"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "CON"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "pledger", "CON"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 3, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(3));
        Assert.NotNull(context.Store.MarkDoctorArrived(3)); // DoctorArrivedAt = base_+20

        // Room 2 (otte, target): ready at t=25.
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "CON"));
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "EXT"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "EXT"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "pledger", "EXT"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        // Room 2: same doctor, ready.
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "EXT"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "EXT"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "EXT"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "EXT"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, room, doctor, procedure));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2));

        // Room 1 (CON target): ready at t+5, arrives at t+15 => readyToDoctor 600s, overlap 300s.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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
        // cycles (121 days x 3/day) + 10 explicit scenario-rich edge cases = 873, with zero rows
        // lost to collision (the day-offset shift keeps the bulk history's calendar days disjoint
        // from the large-synthetic seed's range). CyclesSeeded is ground-truth (ties out exactly to
        // the persisted completed-cycle count), not a sum of sub-seeder self-reports.
        Assert.Equal(873, result.CyclesSeeded);
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
        // recent cycles) land in Today. TotalCompletedCycleCount (not CompletedRoomCyclesCount) is
        // the ground-truth all-time total independent of the selected window: CompletedRoomCyclesCount
        // deliberately excludes the manual-audit-candidate cycle (IsException = true), so it reads 872
        // here, one less than the raw 873 - that exclusion is correct reporting behavior, not a
        // collision or a miscount.
        var today = DateOnly.FromDateTime(now.UtcDateTime.Date);
        var todayCount = context.Store.GetReports(ReportDateRange.FromDates(today, today)).CompletedRoomCyclesCount;
        var allTimeTotal = context.Store.GetReports(ReportDateRange.AllTime).TotalCompletedCycleCount;
        Assert.True(todayCount > 0, "Expected at least one completed cycle in the Today window.");
        Assert.Equal(873, allTimeTotal);
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

        // 1000 (default) + 363 (scenario-rich bulk history) + 10 (edge cases) = 1373.
        Assert.Equal(1373, result.CyclesSeeded);
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
        Assert.NotNull(SeatViaPrestage(context.Store, room, doctor, procedure, sedation: sedation, expectedAllocationUnits: expectedAllocationUnits));
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

    [Fact]
    public void Reports_observed_doctor_days_report_span_fields_for_included_completed_cycles()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        var seatedAt = Utc(2026, 6, 10, 8);
        SaveObservedCycle(
            seed,
            room: 1,
            doctor: "otte",
            code: "CON",
            seatedAt: seatedAt,
            readyForDoctorAt: seatedAt.AddMinutes(5),
            doctorArrivedAt: seatedAt.AddMinutes(10),
            doctorCompleteAt: seatedAt.AddMinutes(30),
            roomAvailableAt: seatedAt.AddMinutes(40),
            expectedUnits: 3);

        SaveObservedCycle(
            seed,
            room: 2,
            doctor: "otte",
            code: "EXT",
            seatedAt: seatedAt.AddMinutes(60),
            readyForDoctorAt: seatedAt.AddMinutes(65),
            doctorArrivedAt: seatedAt.AddMinutes(80),
            doctorCompleteAt: seatedAt.AddMinutes(105),
            roomAvailableAt: seatedAt.AddMinutes(115),
            expectedUnits: 4);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-10", "2026-06-10"));

        var day = Assert.Single(reports.ObservedDoctorDays!);
        Assert.Equal("otte", day.DoctorId);
        Assert.False(string.IsNullOrWhiteSpace(day.DoctorName));
        Assert.Equal("2026-06-10", day.ReportDate);
        Assert.Equal(2, day.EncounterCount);
        Assert.Equal(seatedAt, day.FirstSeatedAt);
        Assert.Equal(seatedAt.AddMinutes(10), day.FirstDoctorArrivedAt);
        Assert.Equal(seatedAt.AddMinutes(105), day.LastDoctorCompleteAt);
        Assert.Equal(seatedAt.AddMinutes(115), day.LastRoomAvailableAt);
        Assert.Equal(105, day.ObservedClinicalSpanMinutes);
        Assert.Equal(115, day.ObservedTeamSpanMinutes);
    }

    [Fact]
    public void Reports_observed_doctor_days_bucket_active_room_minutes_by_concurrency()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var seed = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);

        var baseTime = Utc(2026, 6, 10, 8);
        SaveObservedCycle(seed, room: 1, doctor: "otte", code: "CON", seatedAt: baseTime, readyForDoctorAt: baseTime.AddMinutes(5), doctorArrivedAt: baseTime.AddMinutes(10), doctorCompleteAt: baseTime.AddMinutes(30), roomAvailableAt: baseTime.AddMinutes(35), expectedUnits: 3);
        SaveObservedCycle(seed, room: 2, doctor: "otte", code: "EXT", seatedAt: baseTime.AddMinutes(10), readyForDoctorAt: baseTime.AddMinutes(15), doctorArrivedAt: baseTime.AddMinutes(20), doctorCompleteAt: baseTime.AddMinutes(40), roomAvailableAt: baseTime.AddMinutes(45), expectedUnits: 3);
        SaveObservedCycle(seed, room: 3, doctor: "otte", code: "IMP", seatedAt: baseTime.AddMinutes(20), readyForDoctorAt: baseTime.AddMinutes(25), doctorArrivedAt: baseTime.AddMinutes(30), doctorCompleteAt: baseTime.AddMinutes(50), roomAvailableAt: baseTime.AddMinutes(55), expectedUnits: 3);

        var context = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = context.Store.GetReports(ReportDateRange.FromDateStrings("2026-06-10", "2026-06-10"));

        var day = Assert.Single(reports.ObservedDoctorDays!);
        Assert.Equal(20, day.MinutesWithOneActiveRoom);
        Assert.Equal(20, day.MinutesWithTwoActiveRooms);
        Assert.Equal(10, day.MinutesWithThreeOrMoreActiveRooms);
        Assert.Equal(3, day.MaxActiveRoomCount);
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static void DriveRoomToCancellationState(
        StoreContext context,
        ManualTimeProvider clock,
        string targetState)
    {
        if (targetState == RoomStates.Available)
        {
            Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
            return;
        }

        var startedAt = clock.GetUtcNow();
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "EXT", expectedAllocationUnits: 4));
        if (targetState == RoomStates.Prestaging)
        {
            return;
        }

        Assert.NotNull(context.Store.SeatRoom(1));
        if (targetState == RoomStates.Seated)
        {
            return;
        }

        clock.SetUtcNow(startedAt.AddMinutes(1));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        if (targetState == RoomStates.ReadyForDoctor)
        {
            return;
        }

        if (targetState is RoomStates.Aging or RoomStates.Stale)
        {
            clock.SetUtcNow(startedAt.AddMinutes(targetState == RoomStates.Aging ? 9 : 14));
            return;
        }

        clock.SetUtcNow(startedAt.AddMinutes(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        if (targetState == RoomStates.DoctorInRoom)
        {
            return;
        }

        Assert.Equal(RoomStates.Turnover, targetState);
        clock.SetUtcNow(startedAt.AddMinutes(3));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
    }

    private static void AssertSameRoomState(RoomState expected, RoomState actual)
    {
        Assert.Equal(expected.RoomId, actual.RoomId);
        Assert.Equal(expected.EpisodeId, actual.EpisodeId);
        Assert.Equal(expected.AssignedDoctor, actual.AssignedDoctor);
        Assert.Equal(expected.AssignedDoctorDisplayName, actual.AssignedDoctorDisplayName);
        Assert.Equal(expected.ProcedureCode, actual.ProcedureCode);
        Assert.Equal(expected.ProcedureCategory, actual.ProcedureCategory);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.PrestageStartedAt, actual.PrestageStartedAt);
        Assert.Equal(expected.SeatedAt, actual.SeatedAt);
        Assert.Equal(expected.AgingStartedAt, actual.AgingStartedAt);
        Assert.Equal(expected.StaleStartedAt, actual.StaleStartedAt);
        Assert.Equal(expected.ReadyForDoctorAt, actual.ReadyForDoctorAt);
        Assert.Equal(expected.DoctorArrivedAt, actual.DoctorArrivedAt);
        Assert.Equal(expected.DoctorCompleteAt, actual.DoctorCompleteAt);
        Assert.Equal(expected.RoomAvailableAt, actual.RoomAvailableAt);
        Assert.Equal(expected.OriginalDefaultExpectedUnits, actual.OriginalDefaultExpectedUnits);
        Assert.Equal(expected.ExpectedAllocationUnits, actual.ExpectedAllocationUnits);
        Assert.Equal(expected.ExpectedAllocationMinutes, actual.ExpectedAllocationMinutes);
        Assert.Equal(expected.AllocationAdjustedFromDefault, actual.AllocationAdjustedFromDefault);
    }

    private static void AssertSameAbortedAssignment(AbortedRoomAssignment expected, AbortedRoomAssignment actual)
    {
        Assert.Equal(expected.AbortedAssignmentId, actual.AbortedAssignmentId);
        Assert.Equal(expected.EpisodeId, actual.EpisodeId);
        Assert.Equal(expected.RoomId, actual.RoomId);
        Assert.Equal(expected.AssignedDoctor, actual.AssignedDoctor);
        Assert.Equal(expected.AssignedDoctorDisplayName, actual.AssignedDoctorDisplayName);
        Assert.Equal(expected.ProcedureCode, actual.ProcedureCode);
        Assert.Equal(expected.ProcedureCategory, actual.ProcedureCategory);
        Assert.Equal(expected.OriginalDefaultExpectedUnits, actual.OriginalDefaultExpectedUnits);
        Assert.Equal(expected.ExpectedAllocationUnits, actual.ExpectedAllocationUnits);
        Assert.Equal(expected.ExpectedAllocationMinutes, actual.ExpectedAllocationMinutes);
        Assert.Equal(expected.AllocationAdjustedFromDefault, actual.AllocationAdjustedFromDefault);
        Assert.Equal(expected.PrestageStartedAt, actual.PrestageStartedAt);
        Assert.Equal(expected.SeatedAt, actual.SeatedAt);
        Assert.Equal(expected.ReadyForDoctorAt, actual.ReadyForDoctorAt);
        Assert.Equal(expected.TerminatedAt, actual.TerminatedAt);
        Assert.Equal(expected.TerminatedFromState, actual.TerminatedFromState);
        Assert.Equal(expected.TerminationKind, actual.TerminationKind);
        Assert.Equal(expected.CancellationReason, actual.CancellationReason);
    }

    private static RoomStatus? SeatViaPrestage(
        DemoBoardStore store,
        int room,
        string doctor,
        string procedure,
        int demoElapsedMinutes = 0,
        bool sedation = false,
        int? expectedAllocationUnits = null)
    {
        var prestaged = store.BeginPrestage(room, doctor, procedure, sedation, expectedAllocationUnits);
        return prestaged is null
            ? null
            : store.SeatRoom(room, demoElapsedMinutes);
    }

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

    private static void SaveObservedCycle(
        StoreContext context,
        int room,
        string doctor,
        string code,
        DateTimeOffset seatedAt,
        DateTimeOffset readyForDoctorAt,
        DateTimeOffset doctorArrivedAt,
        DateTimeOffset doctorCompleteAt,
        DateTimeOffset roomAvailableAt,
        int expectedUnits)
    {
        var cycle = new CompletedRoomCycle
        {
            RoomId = room,
            AssignedDoctor = doctor,
            ProcedureCode = code,
            SeatedAt = seatedAt,
            ReadyForDoctorAt = readyForDoctorAt,
            DoctorArrivedAt = doctorArrivedAt,
            DoctorCompleteAt = doctorCompleteAt,
            RoomAvailableAt = roomAvailableAt,
            SeatedToDoctorSeconds = (int)(doctorArrivedAt - seatedAt).TotalSeconds,
            PrepSeconds = (int)(readyForDoctorAt - seatedAt).TotalSeconds,
            ReadyToDoctorSeconds = (int)(doctorArrivedAt - readyForDoctorAt).TotalSeconds,
            DoctorInRoomSeconds = (int)(doctorCompleteAt - doctorArrivedAt).TotalSeconds,
            TurnoverSeconds = (int)(roomAvailableAt - doctorCompleteAt).TotalSeconds,
            TotalRoomCycleSeconds = (int)(roomAvailableAt - seatedAt).TotalSeconds,
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
        Assert.NotNull(SeatViaPrestage(context.Store, room, doctor, "CON"));
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        clock.SetUtcNow(now.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(now.AddMinutes(15));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(now.AddMinutes(20));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        // Exception cycle on room 2.
        clock.SetUtcNow(now.AddMinutes(25));
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "pledger", "EXT"));
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
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
        Assert.NotNull(SeatViaPrestage(first.Store, 1, "otte", "CON"));
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
        var deploymentEnvironment = DeploymentEnvironmentPolicy.Resolve(Environments.Development);
        var repository = new SqliteBoardRepository(
            Microsoft.Extensions.Options.Options.Create(new BoardPersistenceOptions { DatabasePath = databasePath }),
            environment,
            deploymentEnvironment,
            new DatabaseIsolationPolicy(
                workspace.DatabaseIsolationLayout(),
                new FileSystemReparsePointInspector()),
            new DatabaseDeploymentIdentityPolicy());

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
        "prestage_started_at",
        "episode_id",
        "sedation_state",
        "expected_allocation_state",
        "expected_allocation_suggested_units",
        "expected_allocation_confirmed_units",
        "active_ready_handoff_id",
        "accepted_ready_handoff_id",
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
        "prestage_started_at",
        "episode_id",
        "accepted_ready_handoff_id",
        "created_at",
        "updated_at"
    ];

    private static readonly HashSet<string> AllowedAbortedAssignmentColumns =
    [
        "id",
        "episode_id",
        "room_id",
        "assigned_doctor_id",
        "assigned_doctor_display_name",
        "procedure_code",
        "procedure_category",
        "sedation_state",
        "expected_allocation_state",
        "expected_allocation_suggested_units",
        "expected_allocation_confirmed_units",
        "terminal_ready_handoff_id",
        "original_default_expected_units",
        "expected_allocation_units",
        "expected_allocation_minutes",
        "allocation_adjusted_from_default",
        "prestage_started_at",
        "seated_at",
        "ready_for_doctor_at",
        "terminated_at",
        "terminated_from_state",
        "termination_kind",
        "cancellation_reason",
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

    // Minimal valid aborted-assignment record for repository tests that do not care about the full
    // snapshot (idempotency, distinct-episode, and combined-write coverage). Phase timestamps beyond
    // the prestage start are left null (a prestage-phase cancel).
    private static AbortedRoomAssignment NewAbortedAssignment(
        string episodeId, int roomId, DateTimeOffset prestageStartedAt, DateTimeOffset terminatedAt) =>
        new()
        {
            EpisodeId = episodeId,
            RoomId = roomId,
            AssignedDoctor = "otte",
            ProcedureCode = "CON",
            OriginalDefaultExpectedUnits = 1,
            ExpectedAllocationUnits = 1,
            ExpectedAllocationMinutes = 10,
            AllocationAdjustedFromDefault = false,
            PrestageStartedAt = prestageStartedAt,
            SeatedAt = null,
            ReadyForDoctorAt = null,
            TerminatedAt = terminatedAt,
            TerminatedFromState = RoomStates.Seated,
            TerminationKind = TerminationKinds.StaffCanceled,
            CancellationReason = CancellationReasons.PatientCanceled
        };

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

    private static void InvokeResetRoom(RoomState room)
    {
        var method = typeof(DemoBoardStore).GetMethod(
            "ResetRoom",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        try
        {
            method.Invoke(null, [room]);
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
        new AdminAccessOptionsValidator(DeploymentEnvironmentPolicy.Resolve(environmentName))
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

    private static DefaultHttpContext NewResolveConflictHttpContext(int roomNumber, string? token)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = $"/api/rooms/{roomNumber}/doctor-arrived/resolve-conflict";
        if (token is not null)
        {
            context.Request.Headers[RoomDeviceTokenValidator.HeaderName] = token;
        }

        return context;
    }

    private static DefaultHttpContext NewRoomMutationHttpContext(int roomNumber, string? token)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = $"/api/rooms/{roomNumber}";
        if (token is not null)
        {
            context.Request.Headers[RoomDeviceTokenValidator.HeaderName] = token;
        }

        return context;
    }

    // Builds a room-mutation HttpContext carrying a real request body, so tests exercise the actual
    // strict JSON parsing path (StrictJsonRequestReader / SeatRequestParser / CancelRequestParser)
    // through DefaultHttpContext rather than constructing a bound DTO directly.
    private static DefaultHttpContext NewJsonBodyContext(int roomNumber, string? token, string body, string? contentType = "application/json")
    {
        var context = NewRoomMutationHttpContext(roomNumber, token);
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        if (contentType is not null)
        {
            context.Request.ContentType = contentType;
        }

        return context;
    }

    private static async Task<IReadOnlyList<RoomAuditEntry>> ReadRoomAuditEntries(string logDirectory)
    {
        var logPath = Path.Combine(logDirectory, "room-audit.log");
        if (!File.Exists(logPath))
        {
            return [];
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var entries = new List<RoomAuditEntry>();
        foreach (var line in await File.ReadAllLinesAsync(logPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<RoomAuditEntry>(line, options);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

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
        var resolvedDatabasePath = databasePath
            ?? (string.Equals(environmentName, Environments.Production, StringComparison.Ordinal)
                ? workspace.ProductionDatabasePath()
                : string.Equals(environmentName, ChairSideEnvironmentNames.Training, StringComparison.Ordinal)
                    ? workspace.TrainingDatabasePath()
                    : Path.Combine(workspace.ContentRoot, "data", "chairside-test.db"));
        var environment = new TestWebHostEnvironment(workspace.ContentRoot, environmentName);
        var deploymentEnvironment = DeploymentEnvironmentPolicy.Resolve(environmentName);
        var isolationLayout = workspace.DatabaseIsolationLayout(
            productionDatabasePath: deploymentEnvironment.IsProduction ? resolvedDatabasePath : null,
            trainingDatabasePath: deploymentEnvironment.IsTraining ? resolvedDatabasePath : null);
        var repository = new SqliteBoardRepository(
            Microsoft.Extensions.Options.Options.Create(new BoardPersistenceOptions { DatabasePath = resolvedDatabasePath }),
            environment,
            deploymentEnvironment,
            new DatabaseIsolationPolicy(isolationLayout, new FileSystemReparsePointInspector()),
            new DatabaseDeploymentIdentityPolicy());
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
            deploymentEnvironment,
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
        Path.Combine(Root, "production", "data", "chairside-test.db");

    public string TrainingDatabasePath() =>
        Path.Combine(Root, "training", "data", "chairside-training-test.db");

    public DatabaseIsolationLayout DatabaseIsolationLayout(
        string? productionDatabasePath = null,
        string? trainingDatabasePath = null)
    {
        var resolvedProductionDatabasePath = productionDatabasePath ?? ProductionDatabasePath();
        var resolvedTrainingDatabasePath = trainingDatabasePath ?? TrainingDatabasePath();

        return new DatabaseIsolationLayout(
            ProductionAppRoot: Path.Combine(Root, "production", "app"),
            ProductionDataRoot: Path.GetDirectoryName(resolvedProductionDatabasePath)!,
            ProductionDatabasePath: resolvedProductionDatabasePath,
            TrainingAppRoot: Path.Combine(Root, "training", "app"),
            TrainingDataRoot: Path.GetDirectoryName(resolvedTrainingDatabasePath)!,
            TrainingDatabasePath: resolvedTrainingDatabasePath);
    }

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

internal sealed class NoopBoardHubContext : IHubContext<BoardHub>
{
    public IHubClients Clients { get; } = new NoopHubClients();

    public IGroupManager Groups { get; } = new NoopGroupManager();
}

internal sealed class NoopHubClients : IHubClients
{
    private static readonly IClientProxy Proxy = new NoopClientProxy();

    public IClientProxy All => Proxy;

    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;

    public IClientProxy Client(string connectionId) => Proxy;

    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;

    public IClientProxy Group(string groupName) => Proxy;

    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;

    public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;

    public IClientProxy User(string userId) => Proxy;

    public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
}

internal sealed class NoopGroupManager : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class NoopClientProxy : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
