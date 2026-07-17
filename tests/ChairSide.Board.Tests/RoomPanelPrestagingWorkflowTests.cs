using System.Text.RegularExpressions;
using ChairSide.Board.Services;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class RoomPanelPrestagingWorkflowTests
{
    [Fact]
    public void Room_read_contract_restores_canonical_assignment_and_lock_after_reload()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var assignment = context.Store.ConvertCanonicalAssignment(
            new CanonicalAssignmentRequest("otte", "EXT", "yes", 4)).Value!;

        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.BeginPrestageCanonical(1).Outcome);
        Assert.Equal(
            PrestagingLifecycleMutationOutcome.Success,
            context.Store.SaveAssignmentDetailsCanonical(1, assignment).Outcome);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.SeatRoomCanonical(1, null).Outcome);
        Assert.Equal(
            PrestagingLifecycleMutationOutcome.Success,
            context.Store.MarkReadyForDoctorCanonical(1, null).Outcome);

        AssertRoomRead(context.Store.GetRoom(1)!);
        AssertRoomRead(context.Store.GetSnapshot().Rooms.Single(room => room.RoomId == 1));

        var reloaded = StoreContext.Create(workspace, Environments.Production, context.DatabasePath);
        AssertRoomRead(reloaded.Store.GetRoom(1)!);
        AssertRoomRead(reloaded.Store.GetSnapshot().Rooms.Single(room => room.RoomId == 1));
    }

    [Theory]
    [InlineData("save")]
    [InlineData("seat")]
    [InlineData("ready")]
    public void Eligible_unchecked_normalizes_to_durable_no_at_each_room_commit_boundary(string boundary)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var uncheckedAssignment = context.Store.ConvertCanonicalAssignment(
            new CanonicalAssignmentRequest("otte", "EXT", null, 3)).Value!;

        Assert.Equal(SedationState.EligibleNo, uncheckedAssignment.Sedation.State);
        Assert.Equal(AssignmentCompleteness.Complete, uncheckedAssignment.Completeness);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.BeginPrestageCanonical(1).Outcome);

        var result = boundary switch
        {
            "save" => context.Store.SaveAssignmentDetailsCanonical(1, uncheckedAssignment),
            "seat" => context.Store.SeatRoomCanonical(1, uncheckedAssignment),
            "ready" => ReadyWithSuppliedAssignment(context, uncheckedAssignment),
            _ => throw new ArgumentOutOfRangeException(nameof(boundary), boundary, null)
        };

        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, result.Outcome);
        var stored = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(SedationState.EligibleNo, stored.SedationState);
        Assert.Equal("EXT", stored.ProcedureCode);
    }

    [Fact]
    public void Explicit_yes_remains_selected_and_uses_the_internal_sedation_modifier()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var selected = context.Store.ConvertCanonicalAssignment(
            new CanonicalAssignmentRequest("otte", "EXT", "yes", 3)).Value!;

        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.BeginPrestageCanonical(1).Outcome);
        Assert.Equal(
            PrestagingLifecycleMutationOutcome.Success,
            context.Store.SaveAssignmentDetailsCanonical(1, selected).Outcome);

        var stored = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var read = context.Store.GetRoom(1)!;
        Assert.Equal(SedationState.EligibleYes, stored.SedationState);
        Assert.Equal("EXT+SED", stored.ProcedureCode);
        Assert.Equal("EXT", read.Assignment!.ProcedureCode);
        Assert.Equal(SedationState.EligibleYes, read.Assignment.Sedation.State);
    }

    [Fact]
    public void Room_pages_share_the_canonical_workflow_controls_and_remove_legacy_edit_mode()
    {
        var root = FindRepositoryRoot();
        var generic = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "room.html"));
        var roomOne = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "room-1.html"));
        var boardScript = File.ReadAllText(Path.Combine(root, "src", "ChairSide.Board", "wwwroot", "board.js"));
        var requiredIds = new[]
        {
            "beginPrestageButton", "saveDetailsButton", "discardChangesButton", "withdrawReadyButton",
            "sedationToggle", "allocationSection", "allocationConfirm", "seatButton", "readyForDoctorButton"
        };

        Assert.All(requiredIds, id => Assert.Contains($"id=\"{id}\"", generic));
        Assert.All(requiredIds, id => Assert.Contains($"id=\"{id}\"", roomOne));
        Assert.Equal(ElementIds(generic), ElementIds(roomOne));
        Assert.DoesNotContain("Update Assignment", generic, StringComparison.Ordinal);
        Assert.DoesNotContain("Update Assignment", roomOne, StringComparison.Ordinal);
        Assert.DoesNotContain("assignmentEditMode", boardScript, StringComparison.Ordinal);
        Assert.Contains("if (procedureChanged) {", boardScript, StringComparison.Ordinal);
        Assert.Contains("app.sedationOn = false;", boardScript, StringComparison.Ordinal);
        Assert.Contains("app.expectedUnitsConfirmed = false;", boardScript, StringComparison.Ordinal);
        Assert.Contains("discardAssignmentDraft();", boardScript, StringComparison.Ordinal);
        Assert.Contains("isAssignmentDraftDirty", boardScript, StringComparison.Ordinal);
        Assert.Contains("sedationChoice: procedure?.sedationEligible && app.sedationOn ? \"yes\" : null", boardScript, StringComparison.Ordinal);
        Assert.Contains("room?.assignment?.sedation?.state === \"EligibleYes\"", boardScript, StringComparison.Ordinal);
        Assert.Contains("function isLegacyActiveRoom(room)", boardScript, StringComparison.Ordinal);
        Assert.Contains("!activeSeatedStates.has(state) || !room?.episodeId", boardScript, StringComparison.Ordinal);
        Assert.Contains("function focusFirstUnresolvedAssignmentControl()", boardScript, StringComparison.Ordinal);
        Assert.Contains("focusFirstUnresolvedAssignmentControl();", boardScript, StringComparison.Ordinal);
    }

    private static PrestagingLifecycleMutationResult ReadyWithSuppliedAssignment(
        StoreContext context,
        RoomAssignmentContract assignment)
    {
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.SeatRoomCanonical(1, null).Outcome);
        return context.Store.MarkReadyForDoctorCanonical(1, assignment);
    }

    private static void AssertRoomRead(RoomStatus room)
    {
        Assert.False(string.IsNullOrWhiteSpace(room.EpisodeId));
        Assert.NotNull(room.PrestageStartedAt);
        Assert.True(room.AssignmentLocked);
        Assert.False(string.IsNullOrWhiteSpace(room.ActiveReadyHandoffId));
        Assert.Null(room.AcceptedReadyHandoffId);
        Assert.Equal("otte", room.Assignment?.DoctorId);
        Assert.Equal("EXT", room.Assignment?.ProcedureCode);
        Assert.Equal(SedationState.EligibleYes, room.Assignment?.Sedation.State);
        Assert.Equal(ExpectedAllocationState.ConfirmedAdjustedValue, room.Assignment?.ExpectedAllocation.State);
        Assert.Equal(3, room.Assignment?.ExpectedAllocation.SuggestedValue);
        Assert.Equal(4, room.Assignment?.ExpectedAllocation.ConfirmedValue);
        Assert.Equal(AssignmentCompleteness.Complete, room.Assignment?.Completeness);
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
