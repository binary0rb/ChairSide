using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class AfterHoursSweepRetryTests
{
    private const string RoomTwoResetTrigger = "fail_room_2_after_hours_reset";

    [Fact]
    public void Failure_after_an_earlier_room_commits_retries_remaining_rooms_same_day_then_stops()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 16, 23, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock,
            expirationOptions: SweepOptions());

        ActivateSeated(context.Store, 1, "otte", "CON");
        ActivateReady(context.Store, 2, "pledger", "EXT");
        ActivateSeated(context.Store, 3, "gibson", "CON");

        var roomTwoBefore = context.Repository.LoadRooms(3).Single(room => room.RoomId == 2);
        var roomTwoHandoffId = Assert.IsType<string>(roomTwoBefore.ActiveReadyHandoffId);
        var handoffBefore = context.Repository.LoadReadyHandoff(roomTwoHandoffId);
        Assert.NotNull(handoffBefore);
        var eventCountBefore = context.Store.GetSnapshot().RecentEvents.Count;

        InstallRoomTwoResetFailure(databasePath);
        try
        {
            var exception = Assert.Throws<SqliteException>(() => context.Store.TryRunAfterHoursSweep());
            Assert.Equal(19, exception.SqliteErrorCode);
            Assert.Contains("injected room 2 after-hours reset failure", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DropTrigger(databasePath, RoomTwoResetTrigger);
        }

        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        Assert.Equal(RoomStates.ReadyForDoctor, context.Store.GetRoom(2)?.State);
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(3)?.State);

        var durableAfterFailure = context.Repository.LoadRooms(3).ToDictionary(room => room.RoomId);
        Assert.Equal(RoomStates.Available, durableAfterFailure[1].State);
        Assert.Equal(RoomStates.ReadyForDoctor, durableAfterFailure[2].State);
        Assert.Equal(RoomStates.Seated, durableAfterFailure[3].State);
        Assert.Equal(roomTwoBefore.EpisodeId, durableAfterFailure[2].EpisodeId);
        Assert.Equal(roomTwoBefore.ActiveReadyHandoffId, durableAfterFailure[2].ActiveReadyHandoffId);
        var handoffAfterFailure = Assert.IsType<PersistedReadyHandoff>(context.Repository.LoadReadyHandoff(roomTwoHandoffId));
        Assert.Equal(handoffBefore.HandoffId, handoffAfterFailure.HandoffId);
        Assert.Equal(handoffBefore.EpisodeId, handoffAfterFailure.EpisodeId);
        Assert.Equal(handoffBefore.ReadyAt, handoffAfterFailure.ReadyAt);
        Assert.Equal(handoffBefore.WithdrawnAt, handoffAfterFailure.WithdrawnAt);
        Assert.Equal(handoffBefore.AcceptedAt, handoffAfterFailure.AcceptedAt);
        Assert.Equal(handoffBefore.TerminatedAt, handoffAfterFailure.TerminatedAt);
        Assert.Equal(handoffBefore.TerminationKind, handoffAfterFailure.TerminationKind);
        Assert.Equal(handoffBefore.Assignment.DoctorId, handoffAfterFailure.Assignment.DoctorId);
        Assert.Equal(handoffBefore.Assignment.ProcedureCode, handoffAfterFailure.Assignment.ProcedureCode);
        Assert.Equal(eventCountBefore + 1, context.Store.GetSnapshot().RecentEvents.Count);

        var firstPassHistory = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(1, firstPassHistory.RoomId);
        Assert.Equal(TerminationKinds.AfterHoursExpired, firstPassHistory.TerminationKind);
        Assert.Empty(context.Repository.LoadCompletedCycles());

        var retry = context.Store.TryRunAfterHoursSweep();

        Assert.Equal([2, 3], retry);
        Assert.All(context.Repository.LoadRooms(3), room => Assert.Equal(RoomStates.Available, room.State));
        var retryHistory = context.Repository.LoadAbortedAssignments();
        Assert.Equal(3, retryHistory.Count);
        Assert.Equal(3, retryHistory.Select(record => record.EpisodeId).Distinct().Count());
        Assert.Single(retryHistory, record => record.RoomId == 1);
        Assert.Single(retryHistory, record => record.RoomId == 2);
        Assert.Single(retryHistory, record => record.RoomId == 3);
        var terminatedHandoff = Assert.IsType<PersistedReadyHandoff>(context.Repository.LoadReadyHandoff(roomTwoHandoffId));
        Assert.Equal(ReadyHandoffTerminationKinds.Expired, terminatedHandoff.TerminationKind);
        Assert.NotNull(terminatedHandoff.TerminatedAt);

        ActivateSeated(context.Store, 1, "otte", "CON");
        clock.SetUtcNow(now.AddMinutes(10));

        Assert.Empty(context.Store.TryRunAfterHoursSweep());
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(1)?.State);
        Assert.Equal(3, context.Repository.LoadAbortedAssignments().Count);
    }

    [Fact]
    public void Restart_after_partial_completion_processes_only_rooms_still_active()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 16, 23, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var first = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock,
            expirationOptions: SweepOptions());

        ActivateSeated(first.Store, 1, "otte", "CON");
        ActivateReady(first.Store, 2, "pledger", "EXT");
        ActivateSeated(first.Store, 3, "gibson", "CON");
        var episodes = first.Repository.LoadRooms(3)
            .Where(room => room.RoomId is 1 or 2 or 3)
            .ToDictionary(room => room.RoomId, room => room.EpisodeId);

        InstallRoomTwoResetFailure(databasePath);
        try
        {
            Assert.Throws<SqliteException>(() => first.Store.TryRunAfterHoursSweep());
        }
        finally
        {
            DropTrigger(databasePath, RoomTwoResetTrigger);
        }

        Assert.Equal(1, Assert.Single(first.Repository.LoadAbortedAssignments()).RoomId);

        var restarted = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock,
            expirationOptions: SweepOptions());

        Assert.Equal([2, 3], restarted.Store.TryRunAfterHoursSweep());
        var history = restarted.Repository.LoadAbortedAssignments();
        Assert.Equal(3, history.Count);
        Assert.Equal(episodes[1], Assert.Single(history, record => record.RoomId == 1).EpisodeId);
        Assert.Equal(episodes[2], Assert.Single(history, record => record.RoomId == 2).EpisodeId);
        Assert.Equal(episodes[3], Assert.Single(history, record => record.RoomId == 3).EpisodeId);
        Assert.Equal(3, history.Select(record => record.EpisodeId).Distinct().Count());
        Assert.All(restarted.Repository.LoadRooms(3), room => Assert.Equal(RoomStates.Available, room.State));
        Assert.Empty(restarted.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Every_after_hours_leftover_is_reviewable_without_fabricating_lifecycle_facts()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 16, 23, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            roomCount: 4,
            timeProvider: clock,
            expirationOptions: SweepOptions());

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        ActivateReady(context.Store, 2, "pledger", "EXT");
        ActivateReady(context.Store, 3, "gibson", "CON");
        Assert.NotNull(context.Store.MarkDoctorArrived(3));
        ActivateReady(context.Store, 4, "schroeder", "CON");
        Assert.NotNull(context.Store.MarkDoctorArrived(4));
        Assert.NotNull(context.Store.MarkDoctorComplete(4));

        var truthfulBefore = context.Repository.LoadRooms(4).ToDictionary(room => room.RoomId);

        Assert.Equal([1, 2, 3, 4], context.Store.TryRunAfterHoursSweep());
        Assert.All(context.Store.GetSnapshot().Rooms, room => Assert.Equal(RoomStates.Available, room.State));

        var reports = context.Store.GetReports();
        var reviewRecords = Assert.IsAssignableFrom<IReadOnlyList<ExceptionReviewRecord>>(reports.ExceptionReviewRecords);
        Assert.Equal(4, reviewRecords.Count);
        Assert.Equal(0, reports.CompletedRoomCyclesCount);
        Assert.Empty(reports.RecentCompletedCycles);
        Assert.All(reviewRecords, exception =>
        {
            Assert.Equal(ExceptionReasons.AfterHoursSweep, exception.ExceptionReason);
            Assert.True(exception.IsException);
            Assert.True(exception.RequiresReview);
            Assert.Equal(ReviewStatuses.PendingReview, exception.ReviewStatus);
        });

        var prestaging = reviewRecords.Single(exception => exception.RoomId == 1);
        Assert.Equal(RoomStates.Prestaging, prestaging.FinalWaitState);
        Assert.Equal(truthfulBefore[1].PrestageStartedAt, prestaging.PrestageStartedAt);
        Assert.Null(prestaging.SeatedAt);
        Assert.Null(prestaging.ReadyForDoctorAt);
        Assert.Null(prestaging.DoctorArrivedAt);
        Assert.Null(prestaging.DoctorCompleteAt);

        var ready = reviewRecords.Single(exception => exception.RoomId == 2);
        Assert.Equal(RoomStates.ReadyForDoctor, ready.FinalWaitState);
        Assert.Equal(truthfulBefore[2].SeatedAt, ready.SeatedAt);
        Assert.Equal(truthfulBefore[2].ReadyForDoctorAt, ready.ReadyForDoctorAt);
        Assert.Null(ready.DoctorArrivedAt);
        Assert.Null(ready.DoctorCompleteAt);

        var working = reviewRecords.Single(exception => exception.RoomId == 3);
        Assert.Equal(RoomStates.DoctorInRoom, working.FinalWaitState);
        Assert.Equal(truthfulBefore[3].DoctorArrivedAt, working.DoctorArrivedAt);
        Assert.Null(working.DoctorCompleteAt);

        var turnover = reviewRecords.Single(exception => exception.RoomId == 4);
        Assert.Equal(RoomStates.Turnover, turnover.FinalWaitState);
        Assert.Equal(truthfulBefore[4].DoctorArrivedAt, turnover.DoctorArrivedAt);
        Assert.Equal(truthfulBefore[4].DoctorCompleteAt, turnover.DoctorCompleteAt);

        var aborted = context.Repository.LoadAbortedAssignments();
        Assert.Equal(2, aborted.Count);
        Assert.Equal([1, 2], aborted.Select(record => record.RoomId).Order().ToArray());
        Assert.Equal(2, context.Repository.LoadCompletedCycles().Count);

        Assert.Equal(ExceptionReviewSources.AbortedAssignment, prestaging.SourceType);
        Assert.True(prestaging.AbortedAssignmentId > 0);
        clock.SetUtcNow(now.AddMinutes(5));
        var reviewed = context.Store.ReviewAbortedAssignmentById(prestaging.AbortedAssignmentId);
        Assert.Equal(ReviewExceptionOutcome.Reviewed, reviewed.Outcome);
        Assert.Equal(1, reviewed.RoomId);

        var persistedReview = context.Repository.LoadAbortedAssignments()
            .Single(record => record.AbortedAssignmentId == prestaging.AbortedAssignmentId);
        Assert.True(persistedReview.IsException);
        Assert.True(persistedReview.RequiresReview);
        Assert.Equal(ReviewStatuses.PendingReview, persistedReview.ReviewStatus);
        Assert.Null(persistedReview.ReviewedAt);
        Assert.Null(persistedReview.ReviewedBy);
        var state = Assert.IsType<HistoricalEncounterAdministrativeState>(
            context.Repository.LoadHistoricalAdministrativeState(new HistoricalEncounterKey(
                HistoricalEncounterSourceTypes.AbortedAssignment,
                prestaging.AbortedAssignmentId)));
        Assert.Equal(HistoricalAdministrativeDispositions.ConfirmedException, state.Disposition);
        Assert.DoesNotContain(
            context.Store.GetReports().ExceptionReviewRecords!,
            record => record.AbortedAssignmentId == prestaging.AbortedAssignmentId);
    }

    private static RoomExpirationOptions SweepOptions() =>
        new()
        {
            Enabled = true,
            AfterHoursSweepEnabled = true,
            AfterHoursSweepTime = "23:00",
            TimeZone = "UTC"
        };

    private static void ActivateSeated(DemoBoardStore store, int roomId, string doctorId, string procedureCode)
    {
        Assert.NotNull(store.BeginPrestage(roomId, doctorId, procedureCode));
        Assert.NotNull(store.SeatRoomCanonical(roomId, null).Room);
    }

    private static void ActivateReady(DemoBoardStore store, int roomId, string doctorId, string procedureCode)
    {
        ActivateSeated(store, roomId, doctorId, procedureCode);
        Assert.NotNull(store.MarkReadyForDoctor(roomId));
    }

    private static void InstallRoomTwoResetFailure(string databasePath) =>
        ExecuteSql(databasePath, """
            CREATE TRIGGER fail_room_2_after_hours_reset
            BEFORE UPDATE ON active_rooms
            FOR EACH ROW
            WHEN OLD.room_id = 2
                AND OLD.episode_id IS NOT NULL
                AND NEW.room_id = OLD.room_id
                AND NEW.state = 'available'
                AND NEW.episode_id IS NULL
            BEGIN
                SELECT RAISE(ABORT, 'injected room 2 after-hours reset failure');
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
}
