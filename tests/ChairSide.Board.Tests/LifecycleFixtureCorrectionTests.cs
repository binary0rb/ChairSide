using ChairSide.Board.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class LifecycleFixtureCorrectionTests
{
    [Fact]
    public void Development_first_run_demo_seed_persists_across_restart_without_reseeding_existing_state()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = Path.Combine(workspace.ContentRoot, "data", "chairside-test.db");
        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);

        var first = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: databasePath,
            roomCount: 12,
            timeProvider: clock);
        var firstSnapshot = first.Store.GetSnapshot().Rooms.ToDictionary(room => room.RoomId);
        var firstPersistedRooms = first.Repository.LoadRooms(12).ToDictionary(room => room.RoomId);
        Assert.Equal(12, firstSnapshot.Count);
        Assert.Contains(firstSnapshot.Values, room => room.State != RoomStates.Available);
        foreach (var (roomId, live) in firstSnapshot)
        {
            var persistedRoom = firstPersistedRooms[roomId];
            Assert.Equal(live.State, persistedRoom.State);
            Assert.Equal(live.AssignedDoctor, persistedRoom.AssignedDoctor);
            Assert.Equal(live.ProcedureCode, persistedRoom.ProcedureCode);
            Assert.Equal(live.SeatedAt, persistedRoom.SeatedAt);
            Assert.Equal(live.ReadyForDoctorAt, persistedRoom.ReadyForDoctorAt);
        }

        var firstLive = first.Store.GetRoom(2);
        var firstPersisted = LoadRoom(first, 2, roomCount: 12);

        Assert.NotNull(firstLive);
        Assert.Equal(RoomStates.Seated, firstLive.State);
        Assert.Equal(firstLive.State, firstPersisted.State);
        Assert.Equal(firstLive.AssignedDoctor, firstPersisted.AssignedDoctor);
        Assert.Equal(firstLive.ProcedureCode, firstPersisted.ProcedureCode);
        Assert.Equal(firstLive.SeatedAt, firstPersisted.SeatedAt);

        var reopened = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: databasePath,
            roomCount: 12,
            timeProvider: clock);
        var restored = reopened.Store.GetRoom(2);
        var restoredSnapshot = reopened.Store.GetSnapshot().Rooms.ToDictionary(room => room.RoomId);

        Assert.NotNull(restored);
        Assert.Equal(firstSnapshot.Count, restoredSnapshot.Count);
        foreach (var (roomId, original) in firstSnapshot)
        {
            var reloaded = restoredSnapshot[roomId];
            Assert.Equal(original.State, reloaded.State);
            Assert.Equal(original.AssignedDoctor, reloaded.AssignedDoctor);
            Assert.Equal(original.ProcedureCode, reloaded.ProcedureCode);
            Assert.Equal(original.SeatedAt, reloaded.SeatedAt);
            Assert.Equal(original.ReadyForDoctorAt, reloaded.ReadyForDoctorAt);
        }

        Assert.Equal(firstLive.State, restored.State);
        Assert.Equal(firstLive.AssignedDoctor, restored.AssignedDoctor);
        Assert.Equal(firstLive.ProcedureCode, restored.ProcedureCode);
        Assert.Equal(firstLive.SeatedAt, restored.SeatedAt);

        Assert.NotNull(reopened.Store.CancelSeating(2));
        Assert.Equal(RoomStates.Available, LoadRoom(reopened, 2, roomCount: 12).State);

        var reopenedAgain = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: databasePath,
            roomCount: 12,
            timeProvider: clock);

        Assert.Equal(RoomStates.Available, reopenedAgain.Store.GetRoom(2)?.State);
        Assert.Null(reopenedAgain.Store.GetRoom(2)?.AssignedDoctor);
        Assert.Null(reopenedAgain.Store.GetRoom(2)?.SeatedAt);
    }

    [Theory]
    [InlineData(6, ReadyUrgency.Stale)]
    [InlineData(7, ReadyUrgency.Stale)]
    [InlineData(8, ReadyUrgency.Aging)]
    [InlineData(9, ReadyUrgency.Stale)]
    public void Development_ready_demo_seed_is_canonical_immediately_and_after_restart(
        int roomId,
        ReadyUrgency expectedUrgency)
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = Path.Combine(workspace.ContentRoot, "data", "chairside-test.db");
        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var first = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: databasePath,
            roomCount: 12,
            timeProvider: clock);

        AssertCanonicalDemoReadyRoom(first, roomId, expectedUrgency);
        Assert.DoesNotContain(
            first.Repository.LoadRooms(12),
            room => room.State is RoomStates.Aging or RoomStates.Stale);

        var reopened = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: databasePath,
            roomCount: 12,
            timeProvider: clock);

        AssertCanonicalDemoReadyRoom(reopened, roomId, expectedUrgency);
        Assert.DoesNotContain(
            reopened.Repository.LoadRooms(12),
            room => room.State is RoomStates.Aging or RoomStates.Stale);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("handoff")]
    [InlineData("aborted")]
    public void Development_database_with_history_and_no_active_rooms_is_not_demo_seeded(string historyKind)
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = Path.Combine(workspace.DataRoot, "development-history.db");
        var seed = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: databasePath,
            roomCount: 12);
        seed.Store.ResetAllDataForEmptyBeta();

        switch (historyKind)
        {
            case "completed":
                SeedReadyRoom(seed);
                Assert.NotNull(seed.Store.MarkDoctorArrived(1));
                Assert.NotNull(seed.Store.MarkDoctorComplete(1));
                Assert.NotNull(seed.Store.MarkRoomAvailable(1));
                Assert.True(CountRows(seed, "completed_room_cycles") > 0);
                break;
            case "handoff":
                SeedReadyRoom(seed);
                Assert.NotNull(seed.Store.WithdrawReady(1));
                Assert.True(CountRows(seed, "ready_handoffs") > 0);
                break;
            case "aborted":
                Assert.NotNull(seed.Store.BeginPrestage(1));
                Assert.NotNull(seed.Store.CancelPrestage(1));
                Assert.True(CountRows(seed, "aborted_room_assignments") > 0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(historyKind), historyKind, "Unknown history kind.");
        }

        ExecuteNonQuery(databasePath, "DELETE FROM active_rooms;");
        Assert.Equal(0, CountRows(seed, "active_rooms"));

        var development = StoreContext.Create(
            workspace,
            environmentName: Environments.Development,
            databasePath: databasePath,
            roomCount: 12);

        Assert.All(development.Store.GetSnapshot().Rooms, room =>
        {
            Assert.Equal(RoomStates.Available, room.State);
            Assert.Null(room.AssignedDoctor);
            Assert.Null(room.ProcedureCode);
        });
    }

    [Fact]
    public void Repeated_live_fixture_replaces_active_handoffs_without_orphans_and_converges()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 13, 15, 0, 0, TimeSpan.Zero);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            roomCount: 12,
            timeProvider: new ManualTimeProvider(now));

        context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileLiveBoardStress, null);
        var firstRooms = ProjectedRoomFixture(context);
        var firstActiveIds = LoadActiveHandoffIds(context);
        Assert.Equal(6, firstActiveIds.Count);
        AssertActiveHandoffIntegrity(context, expectedCount: 6);

        context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileLiveBoardStress, null);
        var secondRooms = ProjectedRoomFixture(context);
        var secondActiveIds = LoadActiveHandoffIds(context);

        Assert.Equal(firstRooms, secondRooms);
        Assert.Equal(6, secondActiveIds.Count);
        AssertActiveHandoffIntegrity(context, expectedCount: 6);
        Assert.All(firstActiveIds, handoffId => Assert.Null(context.Repository.LoadReadyHandoff(handoffId)));
    }

    [Fact]
    public void Switching_live_fixture_profiles_replaces_active_handoffs_with_second_profile_count()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, roomCount: 12);

        context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileDoctorViewStress, null);
        AssertActiveHandoffIntegrity(context, expectedCount: 8);

        context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileDoctorViewOverflowStress, null);

        AssertActiveHandoffIntegrity(context, expectedCount: 7);
    }

    [Fact]
    public void Switching_from_live_fixture_to_history_only_profile_leaves_no_active_handoffs()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, roomCount: 12);

        context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileLiveBoardStress, null);
        AssertActiveHandoffIntegrity(context, expectedCount: 6);

        context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileScenarioRich, null);

        AssertActiveHandoffIntegrity(context, expectedCount: 0);
    }

    [Fact]
    public void Maintenance_reset_removes_preexisting_orphaned_active_handoff()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, roomCount: 12);
        SeedReadyRoom(context);
        var handoffId = LoadRoom(context, 1, roomCount: 12).ActiveReadyHandoffId;
        Assert.False(string.IsNullOrWhiteSpace(handoffId));
        ExecuteNonQuery(context.DatabasePath, "DELETE FROM active_rooms WHERE room_id = 1;");
        Assert.Equal(1, CountOrphanedActiveHandoffs(context));

        context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileScenarioRich, null);

        Assert.Null(context.Repository.LoadReadyHandoff(handoffId!));
        AssertActiveHandoffIntegrity(context, expectedCount: 0);
    }

    [Fact]
    public void Maintenance_reset_preserves_resolved_handoffs_and_aborted_history()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            roomCount: 12,
            timeProvider: clock);

        SeedReadyRoom(context, 1);
        var withdrawnId = LoadRoom(context, 1, roomCount: 12).ActiveReadyHandoffId!;
        clock.SetUtcNow(now.AddMinutes(1));
        Assert.NotNull(context.Store.WithdrawReady(1));

        SeedReadyRoom(context, 2);
        var acceptedId = LoadRoom(context, 2, roomCount: 12).ActiveReadyHandoffId!;
        clock.SetUtcNow(now.AddMinutes(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2));

        SeedReadyRoom(context, 3);
        var terminatedId = LoadRoom(context, 3, roomCount: 12).ActiveReadyHandoffId!;
        clock.SetUtcNow(now.AddMinutes(3));
        Assert.NotNull(context.Store.CancelSeating(3));

        var resolvedBefore = new[] { withdrawnId, acceptedId, terminatedId }
            .Select(id => HandoffSnapshot.From(context.Repository.LoadReadyHandoff(id)!))
            .ToArray();
        var abortedBefore = context.Repository.LoadAbortedAssignments().Select(AbortedSnapshot.From).ToArray();
        Assert.Single(abortedBefore);

        context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileLiveBoardStress, null);

        var resolvedAfter = new[] { withdrawnId, acceptedId, terminatedId }
            .Select(id => HandoffSnapshot.From(context.Repository.LoadReadyHandoff(id)!))
            .ToArray();
        Assert.Equal(resolvedBefore, resolvedAfter);
        Assert.Equal(abortedBefore, context.Repository.LoadAbortedAssignments().Select(AbortedSnapshot.From).ToArray());
        AssertActiveHandoffIntegrity(context, expectedCount: 6);
    }

    [Fact]
    public void Maintenance_reset_database_abort_rolls_back_durable_and_live_state()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 13, 15, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            roomCount: 12,
            timeProvider: clock);
        context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileLiveBoardStress, null);

        var liveBefore = RoomStatusSnapshot.From(context.Store.GetRoom(3)!);
        var roomsBefore = LoadPersistedRoomSnapshots(context, roomCount: 12);
        var activeBefore = LoadActiveHandoffIds(context);
        var completedBefore = CountRows(context, "completed_room_cycles");

        ExecuteNonQuery(databasePath, """
            CREATE TRIGGER fail_second_available_room_insert
            BEFORE INSERT ON active_rooms
            FOR EACH ROW
            WHEN NEW.room_id = 2 AND NEW.state = 'available'
            BEGIN
                SELECT RAISE(ABORT, 'injected maintenance reset failure');
            END;
            """);
        try
        {
            Assert.Throws<SqliteException>(() =>
                context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileScenarioRich, null));

            Assert.Equal(liveBefore, RoomStatusSnapshot.From(context.Store.GetRoom(3)!));
            Assert.Equal(roomsBefore, LoadPersistedRoomSnapshots(context, roomCount: 12));
            Assert.Equal(activeBefore, LoadActiveHandoffIds(context));
            Assert.Equal(completedBefore, CountRows(context, "completed_room_cycles"));
        }
        finally
        {
            ExecuteNonQuery(databasePath, "DROP TRIGGER IF EXISTS fail_second_available_room_insert;");
        }

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            roomCount: 12,
            timeProvider: clock);
        Assert.Equal(roomsBefore, LoadPersistedRoomSnapshots(reloaded, roomCount: 12));
        Assert.Equal(activeBefore, LoadActiveHandoffIds(reloaded));
        Assert.Equal(completedBefore, CountRows(reloaded, "completed_room_cycles"));
        Assert.Equal(liveBefore, RoomStatusSnapshot.From(reloaded.Store.GetRoom(3)!));
    }

    [Theory]
    [InlineData(4, ReadyUrgency.Aging)]
    [InlineData(5, ReadyUrgency.Stale)]
    public void Maintenance_urgency_fixture_persists_primary_ready_with_owned_active_handoff(
        int roomId,
        ReadyUrgency expectedUrgency)
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 13, 15, 0, 0, TimeSpan.Zero);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            roomCount: 12,
            timeProvider: new ManualTimeProvider(now));

        var result = context.Store.ResetAndSeedStressFixture(MaintenanceCommands.ProfileLiveBoardStress, null);

        var status = context.Store.GetRoom(roomId);
        var persisted = LoadRoom(context, roomId, roomCount: 12);
        Assert.NotNull(status);
        Assert.Equal(RoomStates.ReadyForDoctor, status.State);
        Assert.Equal(expectedUrgency, status.ReadyUrgency);
        Assert.Empty(status!.IntegrityFaults!);
        Assert.Equal(RoomStates.ReadyForDoctor, persisted.State);
        Assert.False(string.IsNullOrWhiteSpace(persisted.EpisodeId));
        Assert.False(string.IsNullOrWhiteSpace(persisted.ActiveReadyHandoffId));

        var handoff = context.Repository.LoadReadyHandoff(persisted.ActiveReadyHandoffId!);
        Assert.NotNull(handoff);
        Assert.Equal(ReadyHandoffStatus.Active, handoff.ContractStatus);
        Assert.Equal(persisted.EpisodeId, handoff.EpisodeId);
        Assert.Equal(persisted.RoomId, handoff.RoomId);
        Assert.Equal(persisted.ReadyForDoctorAt, handoff.ReadyAt);
        Assert.Equal(handoff.HandoffId, persisted.ActiveReadyHandoffId);

        var persistedRooms = context.Repository.LoadRooms(12);
        Assert.DoesNotContain(persistedRooms, room => room.State is RoomStates.Aging or RoomStates.Stale);
        Assert.Equal(0, result.RoomStateCounts.GetValueOrDefault(RoomStates.Aging));
        Assert.Equal(0, result.RoomStateCounts.GetValueOrDefault(RoomStates.Stale));
    }

    [Theory]
    [InlineData(8, ReadyUrgency.Aging)]
    [InlineData(13, ReadyUrgency.Stale)]
    public void Reissued_ready_uses_new_handoff_interval_and_clears_urgency_on_withdrawal_and_arrival(
        int elapsedMinutes,
        ReadyUrgency expectedUrgency)
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            agingMinutes: 7,
            staleMinutes: 12,
            timeProvider: clock);

        Assert.NotNull(context.Store.BeginPrestage(1));
        Assert.NotNull(context.Store.SaveAssignmentDetails(1, InitialAssignment()));
        clock.SetUtcNow(now.AddMinutes(1));
        Assert.NotNull(context.Store.SeatRoomCanonical(1, null).Room);
        clock.SetUtcNow(now.AddMinutes(2));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        var firstReadyRoom = LoadRoom(context, 1);
        var originalEpisodeId = firstReadyRoom.EpisodeId;
        var originalPrestageStartedAt = firstReadyRoom.PrestageStartedAt;
        var originalSeatedAt = firstReadyRoom.SeatedAt;
        var firstHandoffId = firstReadyRoom.ActiveReadyHandoffId;
        var firstHandoff = context.Repository.LoadReadyHandoff(firstHandoffId!);
        Assert.NotNull(firstHandoff);

        clock.SetUtcNow(firstHandoff.ReadyAt.AddMinutes(elapsedMinutes));
        Assert.Equal(expectedUrgency, context.Store.GetRoom(1)?.ReadyUrgency);

        var withdrawn = context.Store.WithdrawReady(1);

        Assert.NotNull(withdrawn);
        Assert.Equal(RoomStates.Seated, withdrawn.State);
        Assert.Equal(ReadyUrgency.None, withdrawn.ReadyUrgency);
        var withdrawnRoom = LoadRoom(context, 1);
        Assert.Equal(originalEpisodeId, withdrawnRoom.EpisodeId);
        Assert.Equal(originalPrestageStartedAt, withdrawnRoom.PrestageStartedAt);
        Assert.Equal(originalSeatedAt, withdrawnRoom.SeatedAt);
        Assert.Null(withdrawnRoom.ActiveReadyHandoffId);
        var withdrawnHandoff = context.Repository.LoadReadyHandoff(firstHandoffId!);
        Assert.NotNull(withdrawnHandoff);
        Assert.Equal(ReadyHandoffStatus.Withdrawn, withdrawnHandoff.ContractStatus);

        var correctedAssignment = CorrectedAssignment();
        var saved = context.Store.SaveAssignmentDetails(1, correctedAssignment);
        Assert.NotNull(saved);
        Assert.Equal("pledger", saved.AssignedDoctor);
        Assert.Equal("EXT", saved.ProcedureCode);

        var secondReadyAt = clock.GetUtcNow().AddMinutes(2);
        clock.SetUtcNow(secondReadyAt);
        var reissued = context.Store.MarkReadyForDoctor(1);
        Assert.NotNull(reissued);
        Assert.Equal(ReadyUrgency.None, reissued.ReadyUrgency);

        var secondReadyRoom = LoadRoom(context, 1);
        var secondHandoffId = secondReadyRoom.ActiveReadyHandoffId;
        var secondHandoff = context.Repository.LoadReadyHandoff(secondHandoffId!);
        Assert.NotNull(secondHandoff);
        Assert.NotEqual(firstHandoffId, secondHandoffId);
        Assert.True(secondHandoff.ReadyAt > firstHandoff.ReadyAt);
        Assert.Equal(secondReadyAt, secondHandoff.ReadyAt);
        Assert.Equal("pledger", secondHandoff.Assignment.DoctorId);
        Assert.Equal("EXT", secondHandoff.Assignment.ProcedureCode);

        clock.SetUtcNow(secondHandoff.ReadyAt.AddMinutes(6));
        Assert.Equal(ReadyUrgency.None, context.Store.GetRoom(1)?.ReadyUrgency);
        clock.SetUtcNow(secondHandoff.ReadyAt.AddMinutes(elapsedMinutes));
        Assert.Equal(expectedUrgency, context.Store.GetRoom(1)?.ReadyUrgency);

        var arrived = context.Store.MarkDoctorArrived(1);
        Assert.NotNull(arrived);
        Assert.Equal(RoomStates.DoctorInRoom, arrived.State);
        Assert.Equal(ReadyUrgency.None, arrived.ReadyUrgency);

        var history = context.Repository.LoadReadyHandoffsByEpisode(originalEpisodeId!);
        Assert.Equal(2, history.Count);
        Assert.Contains(history, handoff => handoff.HandoffId == firstHandoffId && handoff.ContractStatus == ReadyHandoffStatus.Withdrawn);
        Assert.Contains(history, handoff => handoff.HandoffId == secondHandoffId && handoff.ContractStatus == ReadyHandoffStatus.Accepted);
    }

    [Theory]
    [InlineData(RoomStates.Aging)]
    [InlineData(RoomStates.Stale)]
    public void Legacy_ready_peer_state_with_owned_active_handoff_can_withdraw(string legacyState)
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var seed = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: new ManualTimeProvider(now));
        SeedReadyRoom(seed);
        var before = LoadRoom(seed, 1);
        var handoffId = before.ActiveReadyHandoffId;
        SetLegacyReadyState(databasePath, legacyState);

        var recovered = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: new ManualTimeProvider(now));
        Assert.Equal(legacyState, recovered.Store.GetRoom(1)?.State);
        Assert.Empty(recovered.Store.GetRoom(1)!.IntegrityFaults!);

        var withdrawn = recovered.Store.WithdrawReady(1);

        Assert.NotNull(withdrawn);
        Assert.Equal(RoomStates.Seated, withdrawn.State);
        Assert.Equal(ReadyUrgency.None, withdrawn.ReadyUrgency);
        var after = LoadRoom(recovered, 1);
        Assert.Equal(before.EpisodeId, after.EpisodeId);
        Assert.Equal(before.PrestageStartedAt, after.PrestageStartedAt);
        Assert.Equal(before.SeatedAt, after.SeatedAt);
        Assert.Null(after.ActiveReadyHandoffId);
        Assert.Equal(ReadyHandoffStatus.Withdrawn, recovered.Repository.LoadReadyHandoff(handoffId!)?.ContractStatus);
    }

    [Theory]
    [InlineData(RoomStates.Aging)]
    [InlineData(RoomStates.Stale)]
    public void Legacy_ready_peer_state_with_missing_handoff_remains_blocked_and_cancelable(string legacyState)
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var seed = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: new ManualTimeProvider(now));
        SeedReadyRoom(seed);
        var ownedHandoffId = LoadRoom(seed, 1).ActiveReadyHandoffId;
        SetLegacyReadyState(databasePath, legacyState, activeHandoffId: "missing-handoff");

        var recovered = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: new ManualTimeProvider(now));
        var faulted = recovered.Store.GetRoom(1);
        Assert.NotNull(faulted);
        Assert.Equal(legacyState, faulted.State);
        Assert.Contains(faulted!.IntegrityFaults!, fault => fault.Code == RoomIntegrityFaultCode.ReadyHandoffMissing);

        Assert.Null(recovered.Store.WithdrawReady(1));
        var unchanged = LoadRoom(recovered, 1);
        Assert.Equal(legacyState, unchanged.State);
        Assert.Equal("missing-handoff", unchanged.ActiveReadyHandoffId);

        var canceled = recovered.Store.CancelSeating(1);
        Assert.NotNull(canceled);
        Assert.Equal(RoomStates.Available, canceled.State);
        Assert.Single(recovered.Repository.LoadAbortedAssignments());
        Assert.Equal(ReadyHandoffStatus.Active, recovered.Repository.LoadReadyHandoff(ownedHandoffId!)?.ContractStatus);
    }

    private static RoomAssignmentContract InitialAssignment() =>
        RoomAssignmentContract.Create(
            "otte",
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(3));

    private static RoomAssignmentContract CorrectedAssignment() =>
        RoomAssignmentContract.Create(
            "pledger",
            "EXT",
            SedationContract.EligibleNo(),
            ExpectedAllocationContract.ConfirmedAdjustedValue(3, 5));

    private static void SeedReadyRoom(StoreContext context, int roomId = 1)
    {
        Assert.NotNull(context.Store.BeginPrestage(roomId));
        Assert.NotNull(context.Store.SaveAssignmentDetails(roomId, InitialAssignment()));
        Assert.NotNull(context.Store.SeatRoomCanonical(roomId, null).Room);
        Assert.NotNull(context.Store.MarkReadyForDoctor(roomId));
    }

    private static RoomState LoadRoom(StoreContext context, int roomId, int roomCount = 3) =>
        context.Repository.LoadRooms(roomCount).Single(room => room.RoomId == roomId);

    private static void SetLegacyReadyState(
        string databasePath,
        string state,
        string? activeHandoffId = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = activeHandoffId is null
            ? "UPDATE active_rooms SET state = $state WHERE room_id = 1;"
            : "UPDATE active_rooms SET state = $state, active_ready_handoff_id = $handoffId WHERE room_id = 1;";
        command.Parameters.AddWithValue("$state", state);
        if (activeHandoffId is not null)
        {
            command.Parameters.AddWithValue("$handoffId", activeHandoffId);
        }
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static void AssertCanonicalDemoReadyRoom(
        StoreContext context,
        int roomId,
        ReadyUrgency expectedUrgency)
    {
        var status = context.Store.GetRoom(roomId);
        var room = LoadRoom(context, roomId, roomCount: 12);
        Assert.NotNull(status);
        Assert.Equal(RoomStates.ReadyForDoctor, status.State);
        Assert.Equal(expectedUrgency, status.ReadyUrgency);
        Assert.Empty(status.IntegrityFaults!);
        Assert.Equal(RoomStates.ReadyForDoctor, room.State);
        Assert.False(string.IsNullOrWhiteSpace(room.EpisodeId));
        Assert.False(string.IsNullOrWhiteSpace(room.ActiveReadyHandoffId));
        Assert.True(new PersistedRoomAssignment(
            room.AssignedDoctor,
            room.AssignedDoctorDisplayName,
            room.ProcedureCode,
            room.ProcedureCategory,
            room.SedationState,
            room.ExpectedAllocationState,
            room.ExpectedAllocationSuggestedUnits,
            room.ExpectedAllocationConfirmedUnits).TryToContract(out var assignment));
        Assert.Equal(AssignmentCompleteness.Complete, assignment.Completeness);

        var handoff = context.Repository.LoadReadyHandoff(room.ActiveReadyHandoffId!);
        Assert.NotNull(handoff);
        Assert.Equal(ReadyHandoffStatus.Active, handoff.ContractStatus);
        Assert.Equal(room.RoomId, handoff.RoomId);
        Assert.Equal(room.EpisodeId, handoff.EpisodeId);
        Assert.Equal(room.ReadyForDoctorAt, handoff.ReadyAt);
    }

    private static IReadOnlyList<string> ProjectedRoomFixture(StoreContext context) =>
        context.Store.GetSnapshot().Rooms
            .OrderBy(room => room.RoomId)
            .Select(room => $"{room.RoomId}|{room.State}|{room.ReadyUrgency}|{room.AssignedDoctor}|{room.ProcedureCode}")
            .ToArray();

    private static IReadOnlyList<string> LoadActiveHandoffIds(StoreContext context)
    {
        using var connection = OpenConnection(context.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT handoff_id
            FROM ready_handoffs
            WHERE withdrawn_at IS NULL
                AND accepted_at IS NULL
                AND terminated_at IS NULL
            ORDER BY handoff_id;
            """;
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }
        return ids;
    }

    private static void AssertActiveHandoffIntegrity(StoreContext context, int expectedCount)
    {
        Assert.Equal(expectedCount, LoadActiveHandoffIds(context).Count);
        Assert.Equal(0, CountOrphanedActiveHandoffs(context));
    }

    private static long CountOrphanedActiveHandoffs(StoreContext context)
    {
        using var connection = OpenConnection(context.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM ready_handoffs AS handoff
            WHERE handoff.withdrawn_at IS NULL
                AND handoff.accepted_at IS NULL
                AND handoff.terminated_at IS NULL
                AND (
                    SELECT COUNT(*)
                    FROM active_rooms AS room
                    WHERE room.active_ready_handoff_id = handoff.handoff_id
                        AND room.room_id = handoff.room_id
                        AND room.episode_id = handoff.episode_id
                ) <> 1;
            """;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static IReadOnlyList<PersistedRoomSnapshot> LoadPersistedRoomSnapshots(StoreContext context, int roomCount) =>
        context.Repository.LoadRooms(roomCount).Select(PersistedRoomSnapshot.From).ToArray();

    private static long CountRows(StoreContext context, string tableName)
    {
        using var connection = OpenConnection(context.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static SqliteConnection OpenConnection(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        return connection;
    }

    private static void ExecuteNonQuery(string databasePath, string sql)
    {
        using var connection = OpenConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed record RoomStatusSnapshot(
        int RoomId,
        string? AssignedDoctor,
        string? ProcedureCode,
        string State,
        DateTimeOffset? SeatedAt,
        DateTimeOffset? ReadyForDoctorAt,
        ReadyUrgency ReadyUrgency)
    {
        public static RoomStatusSnapshot From(RoomStatus room) =>
            new(
                room.RoomId,
                room.AssignedDoctor,
                room.ProcedureCode,
                room.State,
                room.SeatedAt,
                room.ReadyForDoctorAt,
                room.ReadyUrgency);
    }

    private sealed record PersistedRoomSnapshot(
        int RoomId,
        string? EpisodeId,
        string? AssignedDoctor,
        string? ProcedureCode,
        string State,
        DateTimeOffset? PrestageStartedAt,
        DateTimeOffset? SeatedAt,
        DateTimeOffset? ReadyForDoctorAt,
        string? ActiveReadyHandoffId,
        string? AcceptedReadyHandoffId)
    {
        public static PersistedRoomSnapshot From(RoomState room) =>
            new(
                room.RoomId,
                room.EpisodeId,
                room.AssignedDoctor,
                room.ProcedureCode,
                room.State,
                room.PrestageStartedAt,
                room.SeatedAt,
                room.ReadyForDoctorAt,
                room.ActiveReadyHandoffId,
                room.AcceptedReadyHandoffId);
    }

    private sealed record HandoffSnapshot(
        string HandoffId,
        string EpisodeId,
        int RoomId,
        DateTimeOffset ReadyAt,
        DateTimeOffset? WithdrawnAt,
        DateTimeOffset? AcceptedAt,
        DateTimeOffset? TerminatedAt,
        string? TerminationKind)
    {
        public static HandoffSnapshot From(PersistedReadyHandoff handoff) =>
            new(
                handoff.HandoffId,
                handoff.EpisodeId,
                handoff.RoomId,
                handoff.ReadyAt,
                handoff.WithdrawnAt,
                handoff.AcceptedAt,
                handoff.TerminatedAt,
                handoff.TerminationKind);
    }

    private sealed record AbortedSnapshot(
        long Id,
        string EpisodeId,
        int RoomId,
        string TerminationKind,
        string? TerminalReadyHandoffId)
    {
        public static AbortedSnapshot From(AbortedRoomAssignment aborted) =>
            new(
                aborted.AbortedAssignmentId,
                aborted.EpisodeId,
                aborted.RoomId,
                aborted.TerminationKind,
                aborted.TerminalReadyHandoffId);
    }
}
