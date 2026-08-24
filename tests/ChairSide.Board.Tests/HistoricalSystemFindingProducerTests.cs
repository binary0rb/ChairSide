using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class HistoricalSystemFindingProducerTests
{
    [Fact]
    public void After_hours_sweep_atomically_attaches_approved_findings_to_both_source_types()
    {
        using var workspace = TestWorkspace.Create();
        var now = DateTimeOffset.Parse("2026-08-21T23:00:00Z");
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "23:00",
                TimeZone = "UTC"
            });
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        ActivateArrived(context.Store, roomId: 2);

        Assert.Equal([1, 2], context.Store.TryRunAfterHoursSweep());

        var aborted = context.Repository.LoadAbortedAssignments().Single(row => row.RoomId == 1);
        var completed = context.Repository.LoadCompletedCycles().Single(row => row.RoomId == 2);
        Assert.True(aborted.IsException);
        Assert.True(aborted.RequiresReview);
        Assert.True(completed.IsException);
        Assert.True(completed.RequiresReview);
        AssertFinding(
            context.Repository,
            new HistoricalEncounterKey(HistoricalEncounterSourceTypes.AbortedAssignment, aborted.AbortedAssignmentId),
            HistoricalSystemFindingReasons.AfterHoursSweep,
            now);
        AssertFinding(
            context.Repository,
            new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, completed.CompletedCycleId),
            HistoricalSystemFindingReasons.AfterHoursSweep,
            now);
    }

    [Fact]
    public void Max_duration_expiration_attaches_findings_without_changing_legacy_pre_arrival_reporting_flags()
    {
        using var workspace = TestWorkspace.Create();
        var startedAt = DateTimeOffset.Parse("2026-08-22T08:00:00Z");
        var clock = new ManualTimeProvider(startedAt);
        var context = StoreContext.Create(
            workspace,
            Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8,
                AfterHoursSweepEnabled = false
            });
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        ActivateArrived(context.Store, roomId: 2);
        clock.SetUtcNow(startedAt.AddHours(8).AddSeconds(1));

        Assert.Equal([1, 2], context.Store.CheckAndExpireActiveCycles());

        var aborted = context.Repository.LoadAbortedAssignments().Single(row => row.RoomId == 1);
        var completed = context.Repository.LoadCompletedCycles().Single(row => row.RoomId == 2);
        Assert.False(aborted.IsException);
        Assert.False(aborted.RequiresReview);
        Assert.True(completed.IsException);
        Assert.True(completed.RequiresReview);
        AssertFinding(
            context.Repository,
            new HistoricalEncounterKey(HistoricalEncounterSourceTypes.AbortedAssignment, aborted.AbortedAssignmentId),
            HistoricalSystemFindingReasons.ExceededMaxActiveDuration,
            clock.GetUtcNow());
        AssertFinding(
            context.Repository,
            new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, completed.CompletedCycleId),
            HistoricalSystemFindingReasons.ExceededMaxActiveDuration,
            clock.GetUtcNow());
    }

    [Fact]
    public void Producer_ledger_failure_rolls_back_handoff_archive_admin_state_and_room_reset()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var startedAt = DateTimeOffset.Parse("2026-08-23T08:00:00Z");
        var clock = new ManualTimeProvider(startedAt);
        var context = StoreContext.Create(
            workspace,
            Environments.Production,
            databasePath: databasePath,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8,
                AfterHoursSweepEnabled = false
            });
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        Assert.NotNull(context.Store.SeatRoomCanonical(1, null).Room);
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        var before = context.Store.GetRoom(1)!;
        var handoffId = Assert.IsType<string>(before.ActiveReadyHandoffId);
        clock.SetUtcNow(startedAt.AddHours(8).AddSeconds(1));
        InstallLedgerFailureTrigger(databasePath);

        try
        {
            var exception = Assert.Throws<SqliteException>(() => context.Store.CheckAndExpireActiveCycles());
            Assert.Contains("injected historical ledger failure", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DropLedgerFailureTrigger(databasePath);
        }

        var after = context.Store.GetRoom(1)!;
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.EpisodeId, after.EpisodeId);
        Assert.Equal(handoffId, after.ActiveReadyHandoffId);
        Assert.Equal(ReadyHandoffStatus.Active, context.Repository.LoadReadyHandoff(handoffId)!.ContractStatus);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
        Assert.Equal(0, Scalar(databasePath, "SELECT COUNT(*) FROM historical_encounter_admin_state;"));
        Assert.Equal(0, Scalar(databasePath, "SELECT COUNT(*) FROM historical_encounter_ledger;"));
        Assert.NotEqual(RoomStates.Available, context.Repository.LoadRooms(1).Single().State);
    }

    [Fact]
    public void Reporting_only_exception_classification_never_creates_administrative_state()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        ActivateArrived(context.Store, roomId: 1);
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));
        var cycle = Assert.Single(context.Repository.LoadCompletedCycles());
        using (var connection = Open(context.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE completed_room_cycles SET procedure_code = 'UNMAPPED' WHERE id = $id;";
            command.Parameters.AddWithValue("$id", cycle.CompletedCycleId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var key = new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, cycle.CompletedCycleId);

        using var report = context.Store.GetReports();
        var classified = Assert.Single(report.RecentCompletedCycles);
        Assert.True(classified.HasReportingException);
        Assert.Contains(ReportingExceptionReasons.UnmappedProcedure, classified.ReportingExceptionReasons);
        Assert.Null(context.Repository.LoadHistoricalAdministrativeState(key));
        Assert.Empty(context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);
    }

    private static void ActivateArrived(DemoBoardStore store, int roomId)
    {
        Assert.NotNull(store.BeginPrestage(roomId, "otte", "CON"));
        Assert.NotNull(store.SeatRoomCanonical(roomId, null).Room);
        Assert.NotNull(store.MarkReadyForDoctor(roomId));
        Assert.NotNull(store.MarkDoctorArrived(roomId));
    }

    private static void AssertFinding(
        SqliteBoardRepository repository,
        HistoricalEncounterKey key,
        string reason,
        DateTimeOffset occurredAt)
    {
        var state = Assert.IsType<HistoricalEncounterAdministrativeState>(
            repository.LoadHistoricalAdministrativeState(key));
        Assert.Equal(HistoricalAdministrativeDispositions.NeedsReview, state.Disposition);
        Assert.Equal(reason, state.CurrentReason);
        Assert.Equal(HistoricalAdministrativeReasonSources.System, state.ReasonSource);
        Assert.Equal(1, state.AdministrativeRevision);
        var ledger = Assert.Single(repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);
        Assert.Equal(HistoricalAdministrativeLedgerEventTypes.SystemFinding, ledger.EventType);
        Assert.Equal(HistoricalAdministrativeActorClasses.System, ledger.ActorClass);
        Assert.Equal(HistoricalAdministrativeReasonSources.System, ledger.ReasonSource);
        Assert.Equal(reason, ledger.StructuredReason);
        Assert.Equal(HistoricalAdministrativeDispositions.NoAnomaly, ledger.PreviousValue);
        Assert.Equal(HistoricalAdministrativeDispositions.NeedsReview, ledger.NewValue);
        Assert.Equal(occurredAt, ledger.OccurredAt);
        Assert.Equal(1, ledger.AdministrativeRevision);
    }

    private static void InstallLedgerFailureTrigger(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER fail_historical_ledger
            BEFORE INSERT ON historical_encounter_ledger
            BEGIN
                SELECT RAISE(ABORT, 'injected historical ledger failure');
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static void DropLedgerFailureTrigger(string databasePath)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER IF EXISTS fail_historical_ledger;";
        command.ExecuteNonQuery();
    }

    private static long Scalar(string databasePath, string sql)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        return connection;
    }
}
