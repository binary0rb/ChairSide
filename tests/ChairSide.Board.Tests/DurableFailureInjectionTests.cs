using System.Reflection;

using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class DurableFailureInjectionTests
{
    private const string ResetTriggerName = "fail_room_1_reset";
    private const string CanonicalTriggerName = "fail_room_1_canonical_assignment";
    private const string CanonicalSeatTriggerName = "fail_room_1_canonical_seat";
    private const string CanonicalReadyTriggerName = "fail_room_1_canonical_ready";
    private const string ReadyAddOnTriggerName = "fail_room_1_ready_add_on";

    [Fact]
    public void Cancel_seating_database_abort_rolls_back_aborted_history_and_preserves_live_room()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        clock.SetUtcNow(now.AddMinutes(5));
        Assert.NotNull(context.Store.SeatRoomCanonical(1, null).Room);

        var liveBefore = RoomSnapshot.From(GetLiveRoom(context.Store));
        var durableBefore = RoomSnapshot.From(LoadRoom(context));
        var eventCountBefore = context.Store.GetSnapshot().RecentEvents.Count;

        InstallResetFailureTrigger(databasePath);
        SqliteException exception;
        try
        {
            exception = Assert.Throws<SqliteException>(() => context.Store.CancelSeating(1));
        }
        finally
        {
            DropTrigger(databasePath, ResetTriggerName);
        }

        AssertInjectedAbort(exception, "injected active_rooms reset failure");
        Assert.Equal(liveBefore, RoomSnapshot.From(GetLiveRoom(context.Store)));
        Assert.Equal(durableBefore, RoomSnapshot.From(LoadRoom(context)));
        Assert.Equal(eventCountBefore, context.Store.GetSnapshot().RecentEvents.Count);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
        Assert.Empty(context.Repository.LoadCompletedCycles());

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);
        Assert.Equal(durableBefore, RoomSnapshot.From(LoadRoom(reloaded)));
        Assert.Empty(reloaded.Repository.LoadAbortedAssignments());
        Assert.Empty(reloaded.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Cancel_ready_room_database_abort_rolls_back_handoff_and_aborted_history()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        Assert.NotNull(context.Store.SeatRoomCanonical(1, null).Room);
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        var liveBefore = RoomSnapshot.From(GetLiveRoom(context.Store));
        var durableBefore = RoomSnapshot.From(LoadRoom(context));
        var handoffId = Assert.IsType<string>(durableBefore.ActiveReadyHandoffId);
        var handoffBefore = HandoffSnapshot.From(context.Repository.LoadReadyHandoff(handoffId)!);
        var eventCountBefore = context.Store.GetSnapshot().RecentEvents.Count;

        InstallResetFailureTrigger(databasePath);
        SqliteException exception;
        try
        {
            exception = Assert.Throws<SqliteException>(() => context.Store.CancelSeating(1));
        }
        finally
        {
            DropTrigger(databasePath, ResetTriggerName);
        }

        AssertInjectedAbort(exception, "injected active_rooms reset failure");
        Assert.Equal(liveBefore, RoomSnapshot.From(GetLiveRoom(context.Store)));
        Assert.Equal(durableBefore, RoomSnapshot.From(LoadRoom(context)));
        Assert.Equal(handoffBefore, HandoffSnapshot.From(context.Repository.LoadReadyHandoff(handoffId)!));
        Assert.Equal(eventCountBefore, context.Store.GetSnapshot().RecentEvents.Count);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
        Assert.Empty(context.Repository.LoadCompletedCycles());

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);
        Assert.Equal(durableBefore, RoomSnapshot.From(LoadRoom(reloaded)));
        Assert.Equal(handoffBefore, HandoffSnapshot.From(reloaded.Repository.LoadReadyHandoff(handoffId)!));
        Assert.Empty(reloaded.Repository.LoadAbortedAssignments());
        Assert.Empty(reloaded.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Ready_expiration_database_abort_rolls_back_handoff_and_aborted_history()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 13, 11, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock,
            expirationOptions: EnabledExpiration());

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        Assert.NotNull(context.Store.SeatRoomCanonical(1, null).Room);
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        var liveBefore = RoomSnapshot.From(GetLiveRoom(context.Store));
        var durableBefore = RoomSnapshot.From(LoadRoom(context));
        var handoffId = Assert.IsType<string>(durableBefore.ActiveReadyHandoffId);
        var handoffBefore = HandoffSnapshot.From(context.Repository.LoadReadyHandoff(handoffId)!);
        var eventCountBefore = context.Store.GetSnapshot().RecentEvents.Count;
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));

        InstallResetFailureTrigger(databasePath);
        SqliteException exception;
        try
        {
            exception = Assert.Throws<SqliteException>(() => context.Store.CheckAndExpireActiveCycles());
        }
        finally
        {
            DropTrigger(databasePath, ResetTriggerName);
        }

        AssertInjectedAbort(exception, "injected active_rooms reset failure");
        Assert.Equal(liveBefore, RoomSnapshot.From(GetLiveRoom(context.Store)));
        Assert.Equal(durableBefore, RoomSnapshot.From(LoadRoom(context)));
        Assert.Equal(handoffBefore, HandoffSnapshot.From(context.Repository.LoadReadyHandoff(handoffId)!));
        Assert.Equal(eventCountBefore, context.Store.GetSnapshot().RecentEvents.Count);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
        Assert.Empty(context.Repository.LoadCompletedCycles());

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock,
            expirationOptions: EnabledExpiration());
        Assert.Equal(durableBefore, RoomSnapshot.From(LoadRoom(reloaded)));
        Assert.Equal(handoffBefore, HandoffSnapshot.From(reloaded.Repository.LoadReadyHandoff(handoffId)!));
        Assert.Empty(reloaded.Repository.LoadAbortedAssignments());
        Assert.Empty(reloaded.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Arrived_expiration_database_abort_rolls_back_exception_cycle()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock,
            expirationOptions: EnabledExpiration());

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        Assert.NotNull(context.Store.SeatRoomCanonical(1, null).Room);
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));

        var liveBefore = RoomSnapshot.From(GetLiveRoom(context.Store));
        var durableBefore = RoomSnapshot.From(LoadRoom(context));
        var durableCycleBefore = CycleSnapshot.From(Assert.Single(context.Repository.LoadCompletedCycles()));
        var eventCountBefore = context.Store.GetSnapshot().RecentEvents.Count;
        Assert.Equal(RoomStates.DoctorInRoom, durableBefore.State);
        Assert.Null(durableBefore.DoctorCompleteAt);
        Assert.Null(durableBefore.RoomAvailableAt);
        Assert.False(durableCycleBefore.IsException);
        Assert.False(durableCycleBefore.RequiresReview);
        Assert.Null(durableCycleBefore.DoctorCompleteAt);
        Assert.Null(durableCycleBefore.RoomAvailableAt);
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));

        InstallResetFailureTrigger(databasePath);
        SqliteException exception;
        try
        {
            exception = Assert.Throws<SqliteException>(() => context.Store.CheckAndExpireActiveCycles());
        }
        finally
        {
            DropTrigger(databasePath, ResetTriggerName);
        }

        AssertInjectedAbort(exception, "injected active_rooms reset failure");
        Assert.Equal(liveBefore, RoomSnapshot.From(GetLiveRoom(context.Store)));
        Assert.Equal(durableBefore, RoomSnapshot.From(LoadRoom(context)));
        Assert.Equal(durableCycleBefore, CycleSnapshot.From(Assert.Single(context.Repository.LoadCompletedCycles())));
        Assert.Equal(eventCountBefore, context.Store.GetSnapshot().RecentEvents.Count);
        Assert.Empty(context.Repository.LoadAbortedAssignments());

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock,
            expirationOptions: EnabledExpiration());
        Assert.Equal(durableBefore, RoomSnapshot.From(LoadRoom(reloaded)));
        Assert.Equal(durableCycleBefore, CycleSnapshot.From(Assert.Single(reloaded.Repository.LoadCompletedCycles())));
        Assert.Empty(reloaded.Repository.LoadAbortedAssignments());
    }

    [Fact]
    public void Save_assignment_details_database_abort_throws_and_preserves_live_room()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var liveBefore = RoomSnapshot.From(GetLiveRoom(context.Store));
        var durableBefore = RoomSnapshot.From(LoadRoom(context));
        var eventCountBefore = context.Store.GetSnapshot().RecentEvents.Count;

        InstallCanonicalFailureTrigger(databasePath);
        SqliteException exception;
        try
        {
            exception = Assert.Throws<SqliteException>(() =>
                context.Store.SaveAssignmentDetails(
                    1,
                    RoomAssignmentContract.Create(
                        "pledger",
                        "EXT",
                        SedationContract.EligibleNo(),
                        ExpectedAllocationContract.ConfirmedSuggestedValue(3))));
        }
        finally
        {
            DropTrigger(databasePath, CanonicalTriggerName);
        }

        AssertInjectedAbort(exception, "injected canonical assignment failure");
        Assert.Equal(liveBefore, RoomSnapshot.From(GetLiveRoom(context.Store)));
        Assert.Equal(durableBefore, RoomSnapshot.From(LoadRoom(context)));
        Assert.Equal(eventCountBefore, context.Store.GetSnapshot().RecentEvents.Count);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
        Assert.Empty(context.Repository.LoadCompletedCycles());

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);
        Assert.Equal(durableBefore, RoomSnapshot.From(LoadRoom(reloaded)));
        Assert.Empty(reloaded.Repository.LoadAbortedAssignments());
        Assert.Empty(reloaded.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Canonical_assignment_bearing_seat_database_abort_returns_persistence_failure_and_preserves_state()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);

        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.BeginPrestageCanonical(1).Outcome);
        var partial = RoomAssignmentContract.Create(
            "otte",
            "EXT",
            SedationContract.EligibleUnresolved(),
            ExpectedAllocationContract.Suggested(3));
        Assert.Equal(
            PrestagingLifecycleMutationOutcome.Success,
            context.Store.SaveAssignmentDetailsCanonical(1, partial).Outcome);

        var liveBefore = RoomSnapshot.From(GetLiveRoom(context.Store));
        var durableBefore = RoomSnapshot.From(LoadRoom(context));
        var eventCountBefore = context.Store.GetSnapshot().RecentEvents.Count;
        var complete = RoomAssignmentContract.Create(
            "pledger",
            "EXT",
            SedationContract.EligibleNo(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(3));

        InstallCanonicalSeatFailureTrigger(databasePath);
        PrestagingLifecycleMutationResult result;
        try
        {
            result = context.Store.SeatRoomCanonical(1, complete);
        }
        finally
        {
            DropTrigger(databasePath, CanonicalSeatTriggerName);
        }

        Assert.Equal(PrestagingLifecycleMutationOutcome.PersistenceFailure, result.Outcome);
        AssertInjectedAbort(
            Assert.IsType<SqliteException>(result.PersistenceException),
            "injected canonical seat failure");
        Assert.Equal(liveBefore, RoomSnapshot.From(GetLiveRoom(context.Store)));
        Assert.Equal(durableBefore, RoomSnapshot.From(LoadRoom(context)));
        Assert.Equal(eventCountBefore, context.Store.GetSnapshot().RecentEvents.Count);

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);
        Assert.Equal(durableBefore, RoomSnapshot.From(LoadRoom(reloaded)));
    }

    [Fact]
    public void Canonical_assignment_bearing_ready_database_abort_rolls_back_assignment_and_handoff()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.BeginPrestageCanonical(1).Outcome);
        var partial = RoomAssignmentContract.Create(
            "otte",
            "EXT",
            SedationContract.EligibleUnresolved(),
            ExpectedAllocationContract.Suggested(3));
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.SeatRoomCanonical(1, partial).Outcome);
        var liveBefore = RoomSnapshot.From(GetLiveRoom(context.Store));
        var durableBefore = RoomSnapshot.From(LoadRoom(context));
        var eventCountBefore = context.Store.GetSnapshot().RecentEvents.Count;
        var handoffsBefore = context.Repository.LoadReadyHandoffsByEpisode(durableBefore.EpisodeId!).Select(HandoffSnapshot.From).ToArray();
        var complete = RoomAssignmentContract.Create(
            "pledger",
            "EXT+SED",
            SedationContract.EligibleYes(),
            ExpectedAllocationContract.ConfirmedAdjustedValue(3, 4));

        InstallCanonicalReadyFailureTrigger(databasePath);
        PrestagingLifecycleMutationResult result;
        try
        {
            result = context.Store.MarkReadyForDoctorCanonical(1, complete);
        }
        finally
        {
            DropTrigger(databasePath, CanonicalReadyTriggerName);
        }

        Assert.Equal(PrestagingLifecycleMutationOutcome.PersistenceFailure, result.Outcome);
        AssertInjectedAbort(
            Assert.IsType<SqliteException>(result.PersistenceException),
            "injected canonical ready failure");
        Assert.Equal(liveBefore, RoomSnapshot.From(GetLiveRoom(context.Store)));
        Assert.Equal(durableBefore, RoomSnapshot.From(LoadRoom(context)));
        Assert.Equal(eventCountBefore, context.Store.GetSnapshot().RecentEvents.Count);
        Assert.Equal(handoffsBefore, context.Repository.LoadReadyHandoffsByEpisode(durableBefore.EpisodeId!).Select(HandoffSnapshot.From).ToArray());
        Assert.Empty(context.Repository.LoadAbortedAssignments());
        Assert.Empty(context.Repository.LoadCompletedCycles());

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);
        Assert.Equal(durableBefore, RoomSnapshot.From(LoadRoom(reloaded)));
        Assert.Equal(handoffsBefore, reloaded.Repository.LoadReadyHandoffsByEpisode(durableBefore.EpisodeId!).Select(HandoffSnapshot.From).ToArray());
        Assert.Empty(reloaded.Repository.LoadAbortedAssignments());
        Assert.Empty(reloaded.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Ready_add_on_database_abort_rolls_back_room_handoff_and_live_state()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);
        var scheduled = RoomAssignmentContract.Create(
            "otte",
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(1),
            isAddOn: false);
        var addOn = RoomAssignmentContract.Create(
            "otte",
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(1),
            isAddOn: true);

        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.BeginPrestageCanonical(1).Outcome);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.SeatRoomCanonical(1, scheduled).Outcome);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.MarkReadyForDoctorCanonical(1, null).Outcome);
        var liveBefore = RoomSnapshot.From(GetLiveRoom(context.Store));
        var durableBefore = RoomSnapshot.From(LoadRoom(context));
        var handoffId = Assert.IsType<string>(durableBefore.ActiveReadyHandoffId);
        var handoffBefore = HandoffSnapshot.From(context.Repository.LoadReadyHandoff(handoffId)!);
        var eventCountBefore = context.Store.GetSnapshot().RecentEvents.Count;

        InstallReadyAddOnFailureTrigger(databasePath);
        PrestagingLifecycleMutationResult result;
        try
        {
            result = context.Store.SaveAssignmentDetailsCanonical(1, addOn);
        }
        finally
        {
            DropTrigger(databasePath, ReadyAddOnTriggerName);
        }

        Assert.Equal(PrestagingLifecycleMutationOutcome.PersistenceFailure, result.Outcome);
        AssertInjectedAbort(
            Assert.IsType<SqliteException>(result.PersistenceException),
            "injected Ready Add-on failure");
        Assert.Equal(liveBefore, RoomSnapshot.From(GetLiveRoom(context.Store)));
        Assert.Equal(durableBefore, RoomSnapshot.From(LoadRoom(context)));
        Assert.Equal(handoffBefore, HandoffSnapshot.From(context.Repository.LoadReadyHandoff(handoffId)!));
        Assert.Equal(eventCountBefore, context.Store.GetSnapshot().RecentEvents.Count);

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);
        Assert.Equal(durableBefore, RoomSnapshot.From(LoadRoom(reloaded)));
        Assert.Equal(handoffBefore, HandoffSnapshot.From(reloaded.Repository.LoadReadyHandoff(handoffId)!));
    }

    private static RoomExpirationOptions EnabledExpiration() =>
        new()
        {
            Enabled = true,
            MaxActiveDurationHours = 8
        };

    private static RoomState LoadRoom(StoreContext context) =>
        context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

    private static RoomState GetLiveRoom(DemoBoardStore store)
    {
        var field = typeof(DemoBoardStore).GetField("_rooms", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var rooms = Assert.IsType<List<RoomState>>(field.GetValue(store));
        return rooms.Single(room => room.RoomId == 1);
    }

    private static void InstallResetFailureTrigger(string databasePath) =>
        ExecuteSql(databasePath, """
            CREATE TRIGGER fail_room_1_reset
            BEFORE UPDATE ON active_rooms
            FOR EACH ROW
            WHEN OLD.room_id = 1
                AND OLD.episode_id IS NOT NULL
                AND NEW.room_id = OLD.room_id
                AND NEW.state = 'available'
                AND NEW.episode_id IS NULL
            BEGIN
                SELECT RAISE(ABORT, 'injected active_rooms reset failure');
            END;
            """);

    private static void InstallCanonicalFailureTrigger(string databasePath) =>
        ExecuteSql(databasePath, """
            CREATE TRIGGER fail_room_1_canonical_assignment
            BEFORE UPDATE OF assigned_doctor_id, procedure_code ON active_rooms
            FOR EACH ROW
            WHEN OLD.room_id = 1
                AND OLD.episode_id IS NEW.episode_id
                AND OLD.state = NEW.state
                AND OLD.active_ready_handoff_id IS NEW.active_ready_handoff_id
                AND OLD.assigned_doctor_id = 'otte'
                AND OLD.procedure_code = 'CON'
                AND NEW.assigned_doctor_id = 'pledger'
                AND NEW.procedure_code = 'EXT'
            BEGIN
                SELECT RAISE(ABORT, 'injected canonical assignment failure');
            END;
            """);

    private static void InstallCanonicalSeatFailureTrigger(string databasePath) =>
        ExecuteSql(databasePath, """
            CREATE TRIGGER fail_room_1_canonical_seat
            BEFORE UPDATE ON active_rooms
            FOR EACH ROW
            WHEN OLD.room_id = 1
                AND OLD.state = 'prestaging'
                AND NEW.state = 'seated'
                AND OLD.episode_id IS NEW.episode_id
            BEGIN
                SELECT RAISE(ABORT, 'injected canonical seat failure');
            END;
            """);

    private static void InstallCanonicalReadyFailureTrigger(string databasePath) =>
        ExecuteSql(databasePath, """
            CREATE TRIGGER fail_room_1_canonical_ready
            BEFORE INSERT ON ready_handoffs
            FOR EACH ROW
            WHEN NEW.room_id = 1
            BEGIN
                SELECT RAISE(ABORT, 'injected canonical ready failure');
            END;
            """);

    private static void InstallReadyAddOnFailureTrigger(string databasePath) =>
        ExecuteSql(databasePath, """
            CREATE TRIGGER fail_room_1_ready_add_on
            BEFORE UPDATE OF is_add_on ON ready_handoffs
            FOR EACH ROW
            WHEN OLD.room_id = 1
                AND OLD.is_add_on = 0
                AND NEW.is_add_on = 1
            BEGIN
                SELECT RAISE(ABORT, 'injected Ready Add-on failure');
            END;
            """);

    private static void DropTrigger(string databasePath, string triggerName) =>
        ExecuteSql(databasePath, $"DROP TRIGGER IF EXISTS {triggerName};");

    private static void ExecuteSql(string databasePath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AssertInjectedAbort(SqliteException exception, string message)
    {
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    private sealed record HandoffSnapshot(
        string HandoffId,
        string EpisodeId,
        int RoomId,
        DateTimeOffset ReadyAt,
        DateTimeOffset? WithdrawnAt,
        DateTimeOffset? AcceptedAt,
        DateTimeOffset? TerminatedAt,
        string? TerminationKind,
        PersistedRoomAssignment Assignment)
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
                handoff.TerminationKind,
                handoff.Assignment);
    }

    private sealed record CycleSnapshot(
        long CompletedCycleId,
        string? EpisodeId,
        string? AcceptedReadyHandoffId,
        int RoomId,
        string AssignedDoctor,
        string ProcedureCode,
        DateTimeOffset? PrestageStartedAt,
        DateTimeOffset SeatedAt,
        DateTimeOffset? ReadyForDoctorAt,
        DateTimeOffset? DoctorArrivedAt,
        DateTimeOffset? DoctorCompleteAt,
        DateTimeOffset? RoomAvailableAt,
        int SeatedToDoctorSeconds,
        int? PrepSeconds,
        int? ReadyToDoctorSeconds,
        int? DoctorInRoomSeconds,
        int? TurnoverSeconds,
        int? TotalRoomCycleSeconds,
        string FinalWaitState,
        bool AgingThresholdReached,
        bool StaleThresholdReached,
        bool IsException,
        bool RequiresReview,
        string? ExceptionReason,
        string ReviewStatus,
        string? SuggestedAction)
    {
        public static CycleSnapshot From(CompletedRoomCycle cycle) =>
            new(
                cycle.CompletedCycleId,
                cycle.EpisodeId,
                cycle.AcceptedReadyHandoffId,
                cycle.RoomId,
                cycle.AssignedDoctor,
                cycle.ProcedureCode,
                cycle.PrestageStartedAt,
                cycle.SeatedAt,
                cycle.ReadyForDoctorAt,
                cycle.DoctorArrivedAt,
                cycle.DoctorCompleteAt,
                cycle.RoomAvailableAt,
                cycle.SeatedToDoctorSeconds,
                cycle.PrepSeconds,
                cycle.ReadyToDoctorSeconds,
                cycle.DoctorInRoomSeconds,
                cycle.TurnoverSeconds,
                cycle.TotalRoomCycleSeconds,
                cycle.FinalWaitState,
                cycle.AgingThresholdReached,
                cycle.StaleThresholdReached,
                cycle.IsException,
                cycle.RequiresReview,
                cycle.ExceptionReason,
                cycle.ReviewStatus,
                cycle.SuggestedAction);
    }

    private sealed record RoomSnapshot(
        string? EpisodeId,
        string? AssignedDoctor,
        string? AssignedDoctorDisplayName,
        string? ProcedureCode,
        string? ProcedureCategory,
        SedationState? SedationState,
        ExpectedAllocationState? ExpectedAllocationState,
        int? ExpectedAllocationSuggestedUnits,
        int? ExpectedAllocationConfirmedUnits,
        string? ActiveReadyHandoffId,
        string? AcceptedReadyHandoffId,
        string State,
        DateTimeOffset? PrestageStartedAt,
        DateTimeOffset? SeatedAt,
        DateTimeOffset? AgingStartedAt,
        DateTimeOffset? StaleStartedAt,
        DateTimeOffset? ReadyForDoctorAt,
        DateTimeOffset? DoctorArrivedAt,
        DateTimeOffset? DoctorCompleteAt,
        DateTimeOffset? RoomAvailableAt,
        int OriginalDefaultExpectedUnits,
        int ExpectedAllocationUnits,
        int ExpectedAllocationMinutes,
        bool AllocationAdjustedFromDefault,
        bool IsAddOn)
    {
        public static RoomSnapshot From(RoomState room) =>
            new(
                room.EpisodeId,
                room.AssignedDoctor,
                room.AssignedDoctorDisplayName,
                room.ProcedureCode,
                room.ProcedureCategory,
                room.SedationState,
                room.ExpectedAllocationState,
                room.ExpectedAllocationSuggestedUnits,
                room.ExpectedAllocationConfirmedUnits,
                room.ActiveReadyHandoffId,
                room.AcceptedReadyHandoffId,
                room.State,
                room.PrestageStartedAt,
                room.SeatedAt,
                room.AgingStartedAt,
                room.StaleStartedAt,
                room.ReadyForDoctorAt,
                room.DoctorArrivedAt,
                room.DoctorCompleteAt,
                room.RoomAvailableAt,
                room.OriginalDefaultExpectedUnits,
                room.ExpectedAllocationUnits,
                room.ExpectedAllocationMinutes,
                room.AllocationAdjustedFromDefault,
                room.IsAddOn);
    }
}
