using System.Text.RegularExpressions;
using ChairSide.Board.Services;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class BoardReadyPresentationTests
{
    [Fact]
    public void Canonical_room_reads_keep_prestaging_and_seated_distinct_without_fabricating_assignment()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);

        var began = context.Store.BeginPrestageCanonical(1);

        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, began.Outcome);
        var prestaging = Assert.IsType<RoomStatus>(began.Room);
        Assert.Equal(RoomStates.Prestaging, prestaging.State);
        Assert.NotNull(prestaging.PrestageStartedAt);
        Assert.Null(prestaging.SeatedAt);
        Assert.False(prestaging.AssignmentLocked);
        Assert.Equal(ReadyUrgency.None, prestaging.ReadyUrgency);
        Assert.NotNull(prestaging.Assignment);
        Assert.Equal(AssignmentCompleteness.Absent, prestaging.Assignment.Completeness);
        Assert.Null(prestaging.Assignment.DoctorId);
        Assert.Null(prestaging.Assignment.ProcedureCode);
        Assert.Equal(SedationState.UnavailableNoProcedure, prestaging.Assignment.Sedation.State);
        Assert.Equal(ExpectedAllocationState.Unknown, prestaging.Assignment.ExpectedAllocation.State);
        Assert.True(prestaging.Capabilities?.CanEditAssignment);
        Assert.True(prestaging.Capabilities?.CanSaveDetails);
        Assert.True(prestaging.Capabilities?.CanSeat);
        Assert.True(prestaging.Capabilities?.CanCancelPrestage);
        Assert.False(prestaging.Capabilities?.CanReady);

        var partial = context.Store.ConvertCanonicalAssignment(
            new CanonicalAssignmentRequest("otte", null, null, null)).Value!;
        var saved = context.Store.SaveAssignmentDetailsCanonical(1, partial);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, saved.Outcome);
        Assert.Equal(AssignmentCompleteness.Partial, saved.Room?.Assignment?.Completeness);
        Assert.Equal("otte", saved.Room?.Assignment?.DoctorId);
        Assert.Null(saved.Room?.Assignment?.ProcedureCode);

        var seatedResult = context.Store.SeatRoomCanonical(1, null);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, seatedResult.Outcome);
        var seated = Assert.IsType<RoomStatus>(seatedResult.Room);
        Assert.Equal(RoomStates.Seated, seated.State);
        Assert.Equal(prestaging.EpisodeId, seated.EpisodeId);
        Assert.Equal(prestaging.PrestageStartedAt, seated.PrestageStartedAt);
        Assert.NotNull(seated.SeatedAt);
        Assert.Equal("otte", seated.Assignment?.DoctorId);
        Assert.Null(seated.Assignment?.ProcedureCode);
        Assert.True(seated.Capabilities?.CanEditAssignment);
        Assert.True(seated.Capabilities?.CanSaveDetails);
        Assert.True(seated.Capabilities?.CanReady);
        Assert.True(seated.Capabilities?.CanCancelSeating);
        Assert.False(seated.Capabilities?.CanSeat);
    }

    [Theory]
    [InlineData(8, ReadyUrgency.Aging)]
    [InlineData(13, ReadyUrgency.Stale)]
    public void Ready_remains_primary_while_withdrawal_and_arrival_clear_urgency_and_keep_assignment(
        int elapsedMinutes,
        ReadyUrgency expectedUrgency)
    {
        using var workspace = TestWorkspace.Create();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 16, 14, 0, 0, TimeSpan.Zero));
        var context = StoreContext.Create(
            workspace,
            Environments.Production,
            agingMinutes: 7,
            staleMinutes: 12,
            timeProvider: clock);
        var assignment = context.Store.ConvertCanonicalAssignment(
            new CanonicalAssignmentRequest("pledger", "EXT", "yes", 4)).Value!;

        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.BeginPrestageCanonical(1).Outcome);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.SeatRoomCanonical(1, assignment).Outcome);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.MarkReadyForDoctorCanonical(1, null).Outcome);

        clock.SetUtcNow(clock.GetUtcNow().AddMinutes(elapsedMinutes));
        var urgent = context.Store.GetRoom(1)!;
        Assert.Equal(RoomStates.ReadyForDoctor, urgent.State);
        Assert.Equal(expectedUrgency, urgent.ReadyUrgency);
        Assert.True(urgent.AssignmentLocked);
        Assert.Equal("pledger", urgent.Assignment?.DoctorId);
        Assert.Equal("EXT", urgent.Assignment?.ProcedureCode);
        Assert.True(urgent.Capabilities?.CanWithdrawReady);
        Assert.True(urgent.Capabilities?.CanDoctorArrive);
        Assert.True(urgent.Capabilities?.CanCancelSeating);

        var withdrawn = Assert.IsType<RoomStatus>(context.Store.WithdrawReadyCanonical(1).Room);
        Assert.Equal(RoomStates.Seated, withdrawn.State);
        Assert.Equal(ReadyUrgency.None, withdrawn.ReadyUrgency);
        Assert.False(withdrawn.AssignmentLocked);
        Assert.Equal("pledger", withdrawn.Assignment?.DoctorId);
        Assert.Equal("EXT", withdrawn.Assignment?.ProcedureCode);
        Assert.True(withdrawn.Capabilities?.CanReady);
        Assert.True(withdrawn.Capabilities?.CanEditAssignment);

        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.MarkReadyForDoctorCanonical(1, null).Outcome);
        clock.SetUtcNow(clock.GetUtcNow().AddMinutes(elapsedMinutes));
        Assert.Equal(expectedUrgency, context.Store.GetRoom(1)?.ReadyUrgency);

        var arrived = Assert.IsType<RoomStatus>(context.Store.MarkDoctorArrivedCanonical(1).Room);
        Assert.Equal(RoomStates.DoctorInRoom, arrived.State);
        Assert.Equal(ReadyUrgency.None, arrived.ReadyUrgency);
        Assert.True(arrived.AssignmentLocked);
        Assert.Equal("pledger", arrived.Assignment?.DoctorId);
        Assert.Equal("EXT", arrived.Assignment?.ProcedureCode);
        Assert.NotNull(arrived.AcceptedReadyHandoffId);
        Assert.True(arrived.Capabilities?.CanDoctorComplete);

        var completed = Assert.IsType<RoomStatus>(context.Store.MarkDoctorComplete(1));
        Assert.Equal(RoomStates.Turnover, completed.State);
        Assert.True(completed.Capabilities?.CanRoomAvailable);
        Assert.False(completed.Capabilities?.CanDoctorComplete);

        var available = Assert.IsType<RoomStatus>(context.Store.MarkRoomAvailable(1));
        Assert.Equal(RoomStates.Available, available.State);
        Assert.True(available.Capabilities?.CanBeginPrestage);
        Assert.False(available.Capabilities?.CanRoomAvailable);
    }

    [Fact]
    public void Master_and_doctor_cards_share_canonical_assignment_and_ready_presentation()
    {
        var root = FindRepositoryRoot();
        var boardScript = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "board.js"));
        var roomCardScript = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "room-card.js"));
        var styles = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "styles.css"));
        var master = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "master.html"));
        var index = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "index.html"));

        Assert.Contains("const presentation = roomPresentationState(room);", roomCardScript, StringComparison.Ordinal);
        Assert.Contains("return { primaryState: \"ready-for-doctor\", readyUrgency };", roomCardScript, StringComparison.Ordinal);
        Assert.Contains("class=\"ready-status-stack\"", roomCardScript, StringComparison.Ordinal);
        Assert.Contains("<span class=\"ready-primary-badge\">READY</span>", roomCardScript, StringComparison.Ordinal);
        Assert.Contains("const assignment = room?.assignment || null;", roomCardScript, StringComparison.Ordinal);
        Assert.Contains("roomAssignedDoctorId(room) === doctor.id", boardScript, StringComparison.Ordinal);
        Assert.DoesNotContain("room.assignedDoctor === doctor.id", boardScript, StringComparison.Ordinal);
        Assert.Contains("Assignment pending", roomCardScript, StringComparison.Ordinal);
        Assert.Contains("Doctor pending", roomCardScript, StringComparison.Ordinal);
        Assert.Contains("Procedure pending", roomCardScript, StringComparison.Ordinal);
        Assert.Contains("Allocation pending", roomCardScript, StringComparison.Ordinal);
        Assert.Contains("Aging: Ready wait &gt;", boardScript, StringComparison.Ordinal);
        Assert.Contains("Stale: Ready wait &gt;", boardScript, StringComparison.Ordinal);
        Assert.Contains(".room-tile.ready-for-doctor.urgency-aging", styles, StringComparison.Ordinal);
        Assert.Contains(".room-tile.ready-for-doctor.urgency-stale", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".room-tile.aging", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".room-tile.stale", styles, StringComparison.Ordinal);
        Assert.Contains("state-dot prestaging", master, StringComparison.Ordinal);
        Assert.Contains("Aging: Ready wait needs attention", master, StringComparison.Ordinal);
        Assert.Contains("Stale: Ready wait needs urgent attention", master, StringComparison.Ordinal);
        Assert.Equal(StateKey(master), StateKey(index));
    }

    [Fact]
    public void Doctor_operational_header_and_room_panel_contracts_remain_in_place()
    {
        var root = FindRepositoryRoot();
        var doctor = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "doctor.html"));
        var genericRoom = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "room.html"));
        var roomOne = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "room-1.html"));
        var requiredRoomControls = new[]
        {
            "beginPrestageButton", "saveDetailsButton", "discardChangesButton", "withdrawReadyButton",
            "sedationToggle", "allocationSection", "allocationConfirm", "seatButton", "readyForDoctorButton"
        };

        var currentRooms = doctor.IndexOf("doctor-current-rooms-frame", StringComparison.Ordinal);
        var snapshot = doctor.IndexOf("doctor-report-snapshot", StringComparison.Ordinal);
        var reportDetails = doctor.IndexOf("doctor-report-details", StringComparison.Ordinal);
        Assert.True(currentRooms >= 0 && snapshot > currentRooms && reportDetails > snapshot);
        Assert.All(requiredRoomControls, id => Assert.Contains($"id=\"{id}\"", genericRoom, StringComparison.Ordinal));
        Assert.All(requiredRoomControls, id => Assert.Contains($"id=\"{id}\"", roomOne, StringComparison.Ordinal));
        Assert.Equal(ElementIds(genericRoom), ElementIds(roomOne));
        Assert.DoesNotContain("Update Assignment", genericRoom, StringComparison.Ordinal);
        Assert.DoesNotContain("Update Assignment", roomOne, StringComparison.Ordinal);
    }

    [Fact]
    public void Primary_workflow_highlights_only_the_enabled_next_action()
    {
        var root = FindRepositoryRoot();
        var boardScript = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "board.js"));
        var roomWorkflowScript = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "room-workflow.js"));
        var styles = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "styles.css"));
        var genericRoom = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "room.html"));
        var roomOne = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "room-1.html"));
        var primaryActionIds = new[]
        {
            "beginPrestageButton", "seatButton", "readyForDoctorButton", "doctorArrivedButton",
            "doctorCompleteButton", "roomAvailableButton"
        };

        Assert.All(primaryActionIds, id =>
        {
            Assert.Contains($"class=\"secondary-button\" id=\"{id}\"", genericRoom, StringComparison.Ordinal);
            Assert.Contains($"class=\"secondary-button\" id=\"{id}\"", roomOne, StringComparison.Ordinal);
        });
        Assert.Contains("/styles.css?v=20260722-primary-workflow-action-colors", genericRoom, StringComparison.Ordinal);
        Assert.Contains("/styles.css?v=20260722-primary-workflow-action-colors", roomOne, StringComparison.Ordinal);
        Assert.Contains("createRoomWorkflow", boardScript, StringComparison.Ordinal);
        Assert.Contains("function roomCapabilities(room)", roomWorkflowScript, StringComparison.Ordinal);
        Assert.Contains("function setNextPrimaryAction(room)", roomWorkflowScript, StringComparison.Ordinal);
        Assert.Contains("room?.capabilities?.canBeginPrestage === true", roomWorkflowScript, StringComparison.Ordinal);
        Assert.Contains("room?.capabilities?.canDoctorComplete === true", roomWorkflowScript, StringComparison.Ordinal);
        Assert.Contains("assignmentDraft.confirmedValue !== null", roomWorkflowScript, StringComparison.Ordinal);
        Assert.Contains("document.getElementById(id)?.classList.toggle(\"is-next-action\", id === nextActionId);", roomWorkflowScript, StringComparison.Ordinal);
        Assert.All(primaryActionIds, id =>
            Assert.Contains($"nextActionId = \"{id}\";", roomWorkflowScript, StringComparison.Ordinal));
        Assert.DoesNotContain("activeSeatedStates", roomWorkflowScript, StringComparison.Ordinal);
        Assert.DoesNotContain("cancelableStates", roomWorkflowScript, StringComparison.Ordinal);
        Assert.DoesNotContain("doctorArrivedStates", roomWorkflowScript, StringComparison.Ordinal);
        Assert.Contains(
            "const action = capabilities.canCancelPrestage ? \"cancel-prestage\" : \"cancel-seating\";",
            roomWorkflowScript,
            StringComparison.Ordinal);
        Assert.Matches(
            "(?s)\\.primary-action-grid \\.is-next-action:not\\(:disabled\\)\\s*\\{[^}]*border-color:\\s*#15803d;[^}]*background:\\s*#15803d;[^}]*color:\\s*#ffffff;",
            styles);
        Assert.Matches(
            "(?s)button:disabled\\s*\\{[^}]*opacity:\\s*0\\.55;",
            styles);
    }

    [Fact]
    public void All_pages_use_the_ordered_native_module_bootstrap()
    {
        var root = FindRepositoryRoot();
        var webRoot = Path.Combine(root, "src", "ChairSide.Board", "wwwroot");
        var bootstrap = File.ReadAllText(Path.Combine(webRoot, "bootstrap.js"));
        var pageNames = new[]
        {
            "doctor.html",
            "index.html",
            "master.html",
            "reports.html",
            "room-1.html",
            "room.html",
            "workshop.html"
        };
        const string moduleEntry =
            "<script type=\"module\" src=\"/bootstrap.js?v=20260728-native-module-bootstrap\"></script>";

        var signalRImport =
            bootstrap.IndexOf("import \"./signalr-lite.js\";", StringComparison.Ordinal);
        var boardImport =
            bootstrap.IndexOf("import \"./board.js?v=20260727-room-capabilities\";", StringComparison.Ordinal);
        Assert.True(signalRImport >= 0);
        Assert.True(boardImport > signalRImport);

        Assert.All(pageNames, pageName =>
        {
            var page = File.ReadAllText(Path.Combine(webRoot, pageName));
            Assert.Equal(1, Regex.Matches(page, "<script\\b", RegexOptions.IgnoreCase).Count);
            Assert.Contains(moduleEntry, page, StringComparison.Ordinal);
            Assert.DoesNotContain("<script src=\"/signalr-lite.js\"></script>", page, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "<script src=\"/board.js?v=20260727-room-capabilities\"></script>",
                page,
                StringComparison.Ordinal);
        });
    }

    private static string StateKey(string html)
    {
        var match = Regex.Match(html, "<div class=\"state-key\">(?<content>[\\s\\S]*?)</div>");
        Assert.True(match.Success);
        return match.Groups["content"].Value;
    }

    private static string[] ElementIds(string html) =>
        Regex.Matches(html, "id=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ChairSide.Board.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the ChairSide repository root.");
    }
}
