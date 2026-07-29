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
        var requestUtilities = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "request-utils.js"));

        Assert.Contains("chairside-room-token-${roomNumber}", boardScript);
	 Assert.Contains("function roomTokenStorageKey(roomNumber = pageContext.roomNumber)", boardScript);
        Assert.Contains("sessionStorage.setItem(roomTokenStorageKey(), token)", boardScript);
        Assert.Contains("sessionStorage.removeItem(roomTokenStorageKey())", boardScript);
        Assert.Contains("import {", boardScript);
        Assert.Contains("mutationHeaders,", boardScript);
        Assert.Contains("} from \"./request-utils.js\";", boardScript);
        Assert.Contains("export function mutationHeaders(baseHeaders = {})", requestUtilities);
        Assert.Contains("headers[roomTokenHeaderName] = app.roomToken", requestUtilities);
        Assert.Contains("const roomTokenHeaderName = \"X-ChairSide-Room-Token\"", requestUtilities);
        Assert.Contains("Room access token required", boardScript);
        Assert.DoesNotContain("roomToken=", boardScript);
    }

}
