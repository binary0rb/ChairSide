using System.Text;
using System.Text.Json;
using System.Reflection;
using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class PrestagingLifecycleEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(null, 1)]
    [InlineData("{}", 2)]
    public async Task Canonical_begin_is_assignment_free_and_reports_typed_failures(string? body, int room)
    {
        using var h = new Harness();
        var response = await h.Begin(room, body);
        var envelope = Action(response);
        Assert.Equal(RoomStates.Prestaging, envelope.Room.State);
        Assert.Equal(AssignmentCompleteness.Absent, envelope.Lifecycle.Assignment.Completeness);
        Assert.Null(envelope.Lifecycle.Assignment.DoctorId);
        Assert.Null(envelope.Lifecycle.Assignment.ProcedureCode);
        Assert.Equal(ExpectedAllocationState.Unknown, envelope.Lifecycle.Assignment.ExpectedAllocation.State);
        AssertError(await h.Begin(999, null), 404, PrestagingLifecycleErrorCodes.RoomNotFound);
        AssertError(await h.Begin(room, null), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);
    }

    [Fact]
    public async Task Compatibility_begin_and_flat_seat_retain_their_room_response_shape()
    {
        using var h = new Harness();
        var begin = await h.Begin(1, """{"doctorId":"otte","procedureCode":"EXT","sedation":true,"expectedAllocationUnits":5}""");
        Assert.Equal("EXT+SED", Assert.IsType<RoomStatus>(begin.Value).ProcedureCode);

        var seat = await h.Seat(2, """{"doctorId":"otte","procedureCode":"EXT","procedureId":"EXT","sedation":true,"expectedAllocationUnits":5,"demoElapsedMinutes":0}""");
        Assert.Equal(200, seat.StatusCode);
        Assert.IsType<RoomStatus>(seat.Value);
    }

    [Fact]
    public async Task Assignment_details_requires_body_and_round_trips_absent_partial_and_complete()
    {
        using var h = new Harness();
        await h.Begin(1, null);
        await h.Begin(2, null);
        await h.Begin(3, null);
        AssertError(await h.Save(1, null), 400, PrestagingLifecycleErrorCodes.MalformedRequest);
        Assert.Equal(AssignmentCompleteness.Absent, Action(await h.Save(1, "{}")).Lifecycle.Assignment.Completeness);
        Assert.Equal(AssignmentCompleteness.Partial, Action(await h.Save(2, """{"doctorId":"otte","procedureCode":"EXT"}""")).Lifecycle.Assignment.Completeness);
        var complete = await h.Save(3, """{"doctorId":"pledger","procedureCode":"EXT","sedationChoice":"yes","confirmedExpectedAllocationUnits":3}""");
        Assert.Equal(AssignmentCompleteness.Complete, Action(complete).Lifecycle.Assignment.Completeness);
        Assert.DoesNotContain("+SED", complete.Json, StringComparison.OrdinalIgnoreCase);
        AssertError(await h.Save(2, """{"procedureCode":"EXT+SED"}"""), 400, PrestagingLifecycleErrorCodes.InvalidAssignment);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData(null)]
    public async Task Assignment_details_requires_json_content_type(string? contentType)
    {
        using var h = new Harness();
        await h.Begin(1, null);
        var before = h.Context.Store.GetRoom(1);

        var response = await h.Save(1, """{"doctorId":"otte"}""", contentType);

        AssertError(response, 400, PrestagingLifecycleErrorCodes.MalformedRequest);
        Assert.Equal(before, h.Context.Store.GetRoom(1));
    }

    [Fact]
    public async Task Assignment_details_reports_room_not_found()
    {
        using var h = new Harness();

        AssertError(await h.Save(999, "{}"), 404, PrestagingLifecycleErrorCodes.RoomNotFound);
    }

    [Fact]
    public async Task Assignment_details_is_locked_at_ready()
    {
        using var h = new Harness();
        Assert.NotNull(h.Context.Store.BeginPrestage(1, "otte", "CON"));
        Assert.NotNull(h.Context.Store.SeatRoom(1));
        Assert.NotNull(h.Context.Store.MarkReadyForDoctor(1));
        AssertError(await h.Save(1, """{"doctorId":"pledger","procedureCode":"EXT","sedationChoice":"no","confirmedExpectedAllocationUnits":3}"""), 409, PrestagingLifecycleErrorCodes.AssignmentLocked);
    }

    [Fact]
    public async Task Canonical_seat_preserves_or_atomically_saves_drafts_and_rejects_failures_without_mutation()
    {
        using var h = new Harness();
        await h.Begin(1, null);
        await h.Save(1, """{"doctorId":"otte","procedureCode":"EXT"}""");
        await h.Begin(2, null);
        Assert.Equal(AssignmentCompleteness.Partial, Action(await h.Seat(1, null)).Lifecycle.Assignment.Completeness);
        var bearing = Action(await h.Seat(2, """{"assignment":{"doctorId":"pledger","procedureCode":"EXT","sedationChoice":"yes","confirmedExpectedAllocationUnits":4}}"""));
        Assert.Equal(RoomStates.Seated, bearing.Room.State);
        Assert.Equal(AssignmentCompleteness.Complete, bearing.Lifecycle.Assignment.Completeness);
        Assert.NotNull(bearing.Room.SeatedAt);
        AssertError(await h.Seat(3, "{}"), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);

        using var h2 = new Harness();
        await h2.Begin(1, null);
        await h2.Save(1, """{"doctorId":"otte","procedureCode":"EXT"}""");
        var before = h2.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        AssertError(await h2.Seat(1, """{"assignment":{"doctorId":"missing","procedureCode":"EXT","sedationChoice":"no","confirmedExpectedAllocationUnits":3}}"""), 400, PrestagingLifecycleErrorCodes.InvalidAssignment);
        var after = h2.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var live = h2.Context.Store.GetRoom(1)!;
        Assert.Equal((before.State, before.AssignedDoctor, before.ProcedureCode, before.SeatedAt), (after.State, after.AssignedDoctor, after.ProcedureCode, after.SeatedAt));
        Assert.Equal((before.State, before.AssignedDoctor, before.ProcedureCode, before.SeatedAt), (live.State, live.AssignedDoctor, live.ProcedureCode, live.SeatedAt));
        AssertError(await h2.Seat(1, """{"assignment":null,"doctorId":"otte"}"""), 400, PrestagingLifecycleErrorCodes.MalformedRequest);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"assignment\":null}")]
    public async Task Canonical_empty_or_null_assignment_seat_preserves_partial_draft(string body)
    {
        using var h = new Harness();
        await h.Begin(1, null);
        var saved = Action(await h.Save(1, """{"doctorId":"otte","procedureCode":"EXT"}"""));

        var seated = Action(await h.Seat(1, body));

        Assert.Equal(RoomStates.Seated, seated.Room.State);
        Assert.NotNull(seated.Room.SeatedAt);
        Assert.Equal(AssignmentCompleteness.Partial, seated.Lifecycle.Assignment.Completeness);
        Assert.Equal(saved.Lifecycle.Assignment, seated.Lifecycle.Assignment);
    }

    [Theory]
    [InlineData(PrestagingLifecycleMutationOutcome.IntegrityFault, 409, PrestagingLifecycleErrorCodes.IntegrityFault)]
    [InlineData(PrestagingLifecycleMutationOutcome.StaleWrite, 409, PrestagingLifecycleErrorCodes.StaleWrite)]
    [InlineData(PrestagingLifecycleMutationOutcome.PersistenceFailure, 500, PrestagingLifecycleErrorCodes.PersistenceFailure)]
    public void Canonical_mutation_failures_have_stable_http_mappings(
        PrestagingLifecycleMutationOutcome outcome,
        int expectedStatus,
        string expectedCode)
    {
        var fault = new RoomIntegrityFault(
            RoomIntegrityFaultCode.ReadyHandoffMissing,
            RoomAssignmentContract.Create(
                null,
                null,
                SedationContract.UnavailableNoProcedure(),
                ExpectedAllocationContract.Unknown()));
        var mutation = new PrestagingLifecycleMutationResult(
            outcome,
            IntegrityFaults: outcome == PrestagingLifecycleMutationOutcome.IntegrityFault ? [fault] : []);

        var mapped = global::RoomLifecycleEndpointHandler.MapCanonicalFailure(mutation);

        Assert.Equal(expectedStatus, mapped.StatusCode);
        Assert.Equal(expectedCode, mapped.Error.Code);
        Assert.Equal(
            outcome == PrestagingLifecycleMutationOutcome.IntegrityFault ? [fault] : [],
            mapped.Error.IntegrityFaults);
    }

    [Fact]
    public async Task Canonical_mutations_audit_previous_state_and_typed_failures()
    {
        using var h = new Harness();

        await h.Begin(1, null);
        await h.Save(1, """{"doctorId":"otte","procedureCode":"EXT"}""");
        await h.Seat(1, "{}");
        AssertError(await h.Begin(1, null), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);

        var entries = await h.ReadAuditEntriesAsync();
        Assert.Collection(
            entries,
            entry => AssertAudit(entry, "begin-prestage", RoomStates.Available, RoomStates.Prestaging, true, null),
            entry => AssertAudit(entry, "save-assignment-details", RoomStates.Prestaging, RoomStates.Prestaging, true, null),
            entry => AssertAudit(entry, "seat", RoomStates.Prestaging, RoomStates.Seated, true, null),
            entry => AssertAudit(entry, "begin-prestage", RoomStates.Seated, null, false, PrestagingLifecycleErrorCodes.LifecycleConflict));
    }

    [Fact]
    public async Task Canonical_success_and_error_envelopes_have_exact_shapes()
    {
        using var h = new Harness();
        var success = await h.Begin(1, null);
        var error = await h.Begin(999, null);
        Assert.Equal(["room", "lifecycle", "handoff"], Names(success.Json));
        Assert.Equal(["code", "message", "unresolvedFields", "integrityFaults"], Names(error.Json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Bodyless_ready_preserves_the_legacy_room_response_and_uses_guarded_ready(string? body)
    {
        using var h = new Harness();
        await h.Begin(1, null);
        await h.Save(1, """{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}""");
        await h.Seat(1, "{}");

        var response = await h.Ready(1, body);
        var ready = Assert.IsType<RoomStatus>(response.Value);
        var stored = h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var handoff = Assert.Single(h.Context.Repository.LoadReadyHandoffsByEpisode(stored.EpisodeId!));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(RoomStates.ReadyForDoctor, ready.State);
        Assert.Equal(stored.ReadyForDoctorAt, handoff.ReadyAt);
        Assert.Equal(stored.ActiveReadyHandoffId, handoff.HandoffId);
        Assert.Equal(ReadyHandoffStatus.Active, handoff.ContractStatus);
        Assert.Equal(JsonSerializer.Serialize(ready, JsonOptions), response.Json);
    }

    [Fact]
    public async Task Explicit_empty_ready_uses_the_saved_complete_assignment_and_returns_canonical_envelope()
    {
        using var h = new Harness();
        await h.Begin(1, null);
        await h.Save(1, """{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}""");
        await h.Seat(1, "{}");

        var response = await h.Ready(1, "{}");
        var ready = Action(response);

        Assert.Equal(RoomStates.ReadyForDoctor, ready.Room.State);
        Assert.True(ready.Lifecycle.AssignmentLocked);
        Assert.Equal(ReadyUrgency.None, ready.Lifecycle.ReadyUrgency);
        Assert.Equal(AssignmentCompleteness.Complete, ready.Lifecycle.Assignment.Completeness);
        Assert.NotNull(ready.Handoff);
        Assert.Equal(ReadyHandoffStatus.Active, ready.Handoff.Status);
        Assert.Equal(ready.Room.ReadyForDoctorAt, ready.Handoff.ReadyAt);
        Assert.Equal(ready.Lifecycle.Assignment, ready.Handoff.Assignment);
        Assert.Equal(["room", "lifecycle", "handoff"], Names(response.Json));
    }

    [Fact]
    public async Task Assignment_bearing_ready_saves_the_draft_and_handoff_atomically()
    {
        using var h = new Harness();
        await h.Begin(1, null);
        await h.Save(1, """{"doctorId":"otte","procedureCode":"EXT"}""");
        await h.Seat(1, "{}");

        var response = await h.Ready(1, """{"assignment":{"doctorId":"pledger","procedureCode":"EXT","sedationChoice":"yes","confirmedExpectedAllocationUnits":4}}""");
        var ready = Action(response);
        var stored = h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var handoff = Assert.Single(h.Context.Repository.LoadReadyHandoffsByEpisode(stored.EpisodeId!));

        Assert.Equal(RoomStates.ReadyForDoctor, stored.State);
        Assert.Equal("pledger", stored.AssignedDoctor);
        Assert.Equal("EXT+SED", stored.ProcedureCode);
        Assert.Equal(SedationState.EligibleYes, stored.SedationState);
        Assert.Equal(ExpectedAllocationState.ConfirmedAdjustedValue, stored.ExpectedAllocationState);
        Assert.Equal(4, stored.ExpectedAllocationConfirmedUnits);
        Assert.Equal(stored.ActiveReadyHandoffId, handoff.HandoffId);
        Assert.Equal(ReadyHandoffStatus.Active, handoff.ContractStatus);
        Assert.Equal("EXT", ready.Lifecycle.Assignment.ProcedureCode);
        Assert.Equal("EXT", ready.Handoff!.Assignment.ProcedureCode);
        Assert.DoesNotContain("+SED", response.Json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canonical_ready_reports_exact_unresolved_fields_without_mutation(bool suppliedDraft)
    {
        using var h = new Harness();
        await h.Begin(1, null);
        await h.Save(1, """{"doctorId":"otte","procedureCode":"EXT"}""");
        await h.Seat(1, "{}");
        var before = ReadyRoomSnapshot.From(h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
        var eventsBefore = h.Context.Store.GetSnapshot().RecentEvents.Count;

        var response = await h.Ready(
            1,
            suppliedDraft ? """{"assignment":{"doctorId":"otte","procedureCode":"EXT"}}""" : "{}");

        var error = AssertError(response, 409, PrestagingLifecycleErrorCodes.AssignmentIncomplete);
        Assert.Equal(["sedationChoice", "confirmedExpectedAllocationUnits"], error.UnresolvedFields);
        Assert.Equal(["code", "message", "unresolvedFields", "integrityFaults"], Names(response.Json));
        Assert.Equal(before, ReadyRoomSnapshot.From(h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Equal(eventsBefore, h.Context.Store.GetSnapshot().RecentEvents.Count);
        Assert.Empty(h.Context.Repository.LoadReadyHandoffsByEpisode(before.EpisodeId!));
    }

    [Fact]
    public async Task Canonical_ready_rejects_invalid_and_conflicting_requests_without_side_effects()
    {
        using var h = new Harness();
        AssertError(await h.Ready(999, "{}"), 404, PrestagingLifecycleErrorCodes.RoomNotFound);
        AssertError(await h.Ready(1, "{}"), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);
        await h.Begin(1, null);
        AssertError(await h.Ready(1, "{}"), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);
        await h.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
        var before = ReadyRoomSnapshot.From(h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1));

        AssertError(await h.Ready(1, """{"assignment":{"doctorId":"missing","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}"""), 400, PrestagingLifecycleErrorCodes.InvalidAssignment);
        AssertError(await h.Ready(1, """{"assignment":{"doctorId":"otte","procedureCode":"missing","confirmedExpectedAllocationUnits":1}}"""), 400, PrestagingLifecycleErrorCodes.InvalidAssignment);
        AssertError(await h.Ready(1, """{"assignment":{"doctorId":"otte","procedureCode":"EXT+SED","sedationChoice":"yes","confirmedExpectedAllocationUnits":3}}"""), 400, PrestagingLifecycleErrorCodes.InvalidAssignment);
        Assert.Equal(before, ReadyRoomSnapshot.From(h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Empty(h.Context.Repository.LoadReadyHandoffsByEpisode(before.EpisodeId!));
    }

    [Fact]
    public async Task Repeated_ready_creates_no_second_handoff_and_keeps_assignment_locked()
    {
        using var h = new Harness();
        await h.Begin(1, null);
        await h.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
        var first = Action(await h.Ready(1, "{}"));

        AssertError(await h.Ready(1, "{}"), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);
        AssertError(await h.Save(1, """{"doctorId":"pledger","procedureCode":"EXT","sedationChoice":"no","confirmedExpectedAllocationUnits":3}"""), 409, PrestagingLifecycleErrorCodes.AssignmentLocked);
        var episodeId = h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1).EpisodeId!;
        var handoff = Assert.Single(h.Context.Repository.LoadReadyHandoffsByEpisode(episodeId));
        Assert.Equal(first.Handoff!.HandoffId, handoff.HandoffId);
    }

    [Fact]
    public async Task Stale_ready_returns_conflict_without_retry_or_orphaned_handoff()
    {
        using var workspace = TestWorkspace.Create();
        var seed = StoreContext.Create(workspace, Environments.Production);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, seed.Store.BeginPrestageCanonical(1).Outcome);
        Assert.Equal(
            PrestagingLifecycleMutationOutcome.Success,
            seed.Store.SeatRoomCanonical(1, CompleteConsult()).Outcome);
        var contextA = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);
        var contextB = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);
        var readyA = contextA.Store.MarkReadyForDoctorCanonical(1, null);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, readyA.Outcome);
        var durableBefore = ReadyRoomSnapshot.From(contextA.Repository.LoadRooms(3).Single(room => room.RoomId == 1));

        using var h = new Harness(workspace, contextB);
        var response = await h.Ready(1, "{}");
        var error = AssertError(response, 409, PrestagingLifecycleErrorCodes.StaleWrite);

        Assert.Empty(error.UnresolvedFields);
        Assert.Equal(durableBefore, ReadyRoomSnapshot.From(contextB.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        var handoffs = contextB.Repository.LoadReadyHandoffsByEpisode(durableBefore.EpisodeId!);
        var handoff = Assert.Single(handoffs);
        Assert.Equal(durableBefore.ActiveReadyHandoffId, handoff.HandoffId);
        Assert.Equal(ReadyHandoffStatus.Active, handoff.ContractStatus);
        Assert.Equal(RoomStates.Seated, contextB.Store.GetRoom(1)!.State);
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "ready-for-doctor", RoomStates.Seated, null, false, PrestagingLifecycleErrorCodes.StaleWrite);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"assignment\":{\"doctorId\":\"pledger\",\"procedureCode\":\"EXT\",\"sedationChoice\":\"no\",\"confirmedExpectedAllocationUnits\":4}}")]
    public async Task Same_state_assignment_change_makes_ready_stale_without_overwriting_the_durable_draft(string? readyBody)
    {
        using var workspace = TestWorkspace.Create();
        var seed = StoreContext.Create(workspace, Environments.Production);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, seed.Store.BeginPrestageCanonical(1).Outcome);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, seed.Store.SeatRoomCanonical(1, CompleteConsult()).Outcome);
        var contextA = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);
        var contextB = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);
        Assert.Equal(
            PrestagingLifecycleMutationOutcome.Success,
            contextB.Store.SaveAssignmentDetailsCanonical(1, CompleteExtraction()).Outcome);
        var durableAfterCorrection = ReadyRoomSnapshot.From(contextB.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
        var staleLiveBefore = ReadyRoomSnapshot.From(GetLiveRoom(contextA.Store));
        var staleEventCount = contextA.Store.GetSnapshot().RecentEvents.Count;

        using var h = new Harness(workspace, contextA);
        var response = await h.Ready(1, readyBody);

        AssertError(response, 409, PrestagingLifecycleErrorCodes.StaleWrite);
        Assert.Equal(durableAfterCorrection, ReadyRoomSnapshot.From(contextA.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Null(durableAfterCorrection.ReadyForDoctorAt);
        Assert.Null(durableAfterCorrection.ActiveReadyHandoffId);
        Assert.Empty(contextA.Repository.LoadReadyHandoffsByEpisode(durableAfterCorrection.EpisodeId!));
        Assert.Equal(staleLiveBefore, ReadyRoomSnapshot.From(GetLiveRoom(contextA.Store)));
        Assert.Equal(staleEventCount, contextA.Store.GetSnapshot().RecentEvents.Count);
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "ready-for-doctor", RoomStates.Seated, null, false, PrestagingLifecycleErrorCodes.StaleWrite);
    }

    [Fact]
    public async Task Seated_room_with_conflicting_handoff_reference_returns_integrity_fault()
    {
        using var workspace = TestWorkspace.Create();
        var seed = StoreContext.Create(workspace, Environments.Production);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, seed.Store.BeginPrestageCanonical(1).Outcome);
        Assert.Equal(
            PrestagingLifecycleMutationOutcome.Success,
            seed.Store.SeatRoomCanonical(1, CompleteConsult()).Outcome);
        var corrupted = seed.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        corrupted.ActiveReadyHandoffId = "missing-handoff";
        seed.Repository.SaveRoom(corrupted, seed.Doctors, seed.Procedures);
        var recovered = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);

        using var h = new Harness(workspace, recovered);
        var response = await h.Ready(1, "{}");

        var error = AssertError(response, 409, PrestagingLifecycleErrorCodes.IntegrityFault);
        Assert.Contains(error.IntegrityFaults, fault => fault.Code == RoomIntegrityFaultCode.ContradictoryHandoffReferences);
        Assert.Equal("missing-handoff", recovered.Repository.LoadRooms(3).Single(room => room.RoomId == 1).ActiveReadyHandoffId);
        Assert.Empty(recovered.Repository.LoadReadyHandoffsByEpisode(corrupted.EpisodeId!));
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "ready-for-doctor", RoomStates.Seated, null, false, PrestagingLifecycleErrorCodes.IntegrityFault);
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Accepted")]
    public async Task Unreferenced_active_or_accepted_handoff_blocks_ready_as_integrity_fault(string handoffStatus)
    {
        using var workspace = TestWorkspace.Create();
        var seed = StoreContext.Create(workspace, Environments.Production);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, seed.Store.BeginPrestageCanonical(1).Outcome);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, seed.Store.SeatRoomCanonical(1, CompleteConsult()).Outcome);
        var seated = seed.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var candidate = CopyRoom(seated);
        candidate.State = RoomStates.ReadyForDoctor;
        var handoff = seed.Repository.CreateReadyHandoff(
            candidate,
            CompleteConsult(),
            new DateTimeOffset(2026, 7, 14, 14, 0, 0, TimeSpan.Zero),
            seed.Doctors,
            seed.Procedures);
        if (handoffStatus == "Accepted")
        {
            ExecuteSql(seed.DatabasePath, $"UPDATE ready_handoffs SET accepted_at = '{handoff.ReadyAt.AddMinutes(1):O}' WHERE handoff_id = '{handoff.HandoffId}';");
        }
        var corrupted = seed.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        corrupted.State = RoomStates.Seated;
        corrupted.ReadyForDoctorAt = null;
        corrupted.ActiveReadyHandoffId = null;
        corrupted.AcceptedReadyHandoffId = null;
        seed.Repository.SaveRoom(corrupted, seed.Doctors, seed.Procedures);
        var recovered = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);
        var before = ReadyRoomSnapshot.From(recovered.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
        var liveBefore = ReadyRoomSnapshot.From(GetLiveRoom(recovered.Store));
        var eventCountBefore = recovered.Store.GetSnapshot().RecentEvents.Count;

        using var h = new Harness(workspace, recovered);
        var response = await h.Ready(1, "{}");

        var error = AssertError(response, 409, PrestagingLifecycleErrorCodes.IntegrityFault);
        Assert.Contains(error.IntegrityFaults, fault => fault.Code == RoomIntegrityFaultCode.ContradictoryHandoffReferences);
        Assert.Equal(before, ReadyRoomSnapshot.From(recovered.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Equal(liveBefore, ReadyRoomSnapshot.From(GetLiveRoom(recovered.Store)));
        Assert.Equal(eventCountBefore, recovered.Store.GetSnapshot().RecentEvents.Count);
        var preserved = Assert.Single(recovered.Repository.LoadReadyHandoffsByEpisode(before.EpisodeId!));
        Assert.Equal(handoff.HandoffId, preserved.HandoffId);
        Assert.Equal(handoffStatus, preserved.ContractStatus.ToString());
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "ready-for-doctor", RoomStates.Seated, null, false, PrestagingLifecycleErrorCodes.IntegrityFault);
    }

    [Fact]
    public async Task Withdrawn_history_allows_reissued_canonical_ready()
    {
        using var h = new Harness();
        await h.Begin(1, null);
        await h.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
        var first = Action(await h.Ready(1, "{}"));
        var withdrawn = Action(await h.Withdraw(1, "{}"));

        var second = Action(await h.Ready(1, "{}"));
        var episodeId = h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1).EpisodeId!;
        var history = h.Context.Repository.LoadReadyHandoffsByEpisode(episodeId).OrderBy(item => item.ReadyAt).ToArray();

        Assert.Equal(2, history.Length);
        Assert.Equal(ReadyHandoffStatus.Withdrawn, history[0].ContractStatus);
        Assert.Equal(ReadyHandoffStatus.Active, history[1].ContractStatus);
        Assert.NotEqual(first.Handoff!.HandoffId, second.Handoff!.HandoffId);
        Assert.Equal(first.Handoff.ReadyAt, withdrawn.Handoff!.ReadyAt);
        Assert.Equal(first.Handoff.HandoffId, history[0].HandoffId);
        Assert.Equal(first.Handoff.ReadyAt, history[0].ReadyAt);
        Assert.NotNull(history[0].WithdrawnAt);
        Assert.NotEqual(first.Handoff.ReadyAt, second.Handoff.ReadyAt);
    }

    [Fact]
    public async Task Ready_endpoint_database_abort_returns_persistence_failure_without_any_mutation()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.BeginPrestageCanonical(1).Outcome);
        Assert.Equal(PrestagingLifecycleMutationOutcome.Success, context.Store.SeatRoomCanonical(1, CompleteConsult()).Outcome);
        var durableBefore = ReadyRoomSnapshot.From(context.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
        var liveBefore = ReadyRoomSnapshot.From(GetLiveRoom(context.Store));
        var eventCountBefore = context.Store.GetSnapshot().RecentEvents.Count;
        InstallReadyInsertFailureTrigger(context.DatabasePath);

        Response response;
        using (var h = new Harness(workspace, context))
        {
            try
            {
                response = await h.Ready(1, """{"assignment":{"doctorId":"pledger","procedureCode":"EXT","sedationChoice":"yes","confirmedExpectedAllocationUnits":4}}""");
            }
            finally
            {
                DropReadyInsertFailureTrigger(context.DatabasePath);
            }

            AssertError(response, 500, PrestagingLifecycleErrorCodes.PersistenceFailure);
            AssertLastAudit(await h.ReadAuditEntriesAsync(), "ready-for-doctor", RoomStates.Seated, null, false, PrestagingLifecycleErrorCodes.PersistenceFailure);
        }

        Assert.Equal(durableBefore, ReadyRoomSnapshot.From(context.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Equal(liveBefore, ReadyRoomSnapshot.From(GetLiveRoom(context.Store)));
        Assert.Equal(eventCountBefore, context.Store.GetSnapshot().RecentEvents.Count);
        Assert.Empty(context.Repository.LoadReadyHandoffsByEpisode(durableBefore.EpisodeId!));
        var reloaded = StoreContext.Create(workspace, Environments.Production, context.DatabasePath);
        Assert.Equal(durableBefore, ReadyRoomSnapshot.From(reloaded.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
    }

    [Theory]
    [InlineData("text/plain", "{}")]
    [InlineData("application/json", "[]")]
    [InlineData("application/json", " ")]
    [InlineData("application/json", "{\"unknown\":true}")]
    [InlineData("application/json", "{\"assignment\":null,\"Assignment\":null}")]
    public async Task Canonical_ready_rejects_invalid_wire_shapes(string contentType, string body)
    {
        using var h = new Harness();

        AssertError(await h.Ready(1, body, contentType), 400, PrestagingLifecycleErrorCodes.MalformedRequest);
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "ready-for-doctor", RoomStates.Available, null, false, PrestagingLifecycleErrorCodes.MalformedRequest);
    }

    [Fact]
    public async Task Ready_audits_success_incomplete_and_invalid_assignment_with_stable_reasons()
    {
        using var h = new Harness();
        await h.Begin(1, null);
        await h.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
        await h.Begin(2, null);
        await h.Seat(2, """{"assignment":{"doctorId":"otte","procedureCode":"EXT"}}""");
        await h.Begin(3, null);
        await h.Seat(3, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");

        Action(await h.Ready(1, "{}"));
        AssertError(await h.Ready(2, "{}"), 409, PrestagingLifecycleErrorCodes.AssignmentIncomplete);
        AssertError(await h.Ready(3, """{"assignment":{"doctorId":"missing","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}"""), 400, PrestagingLifecycleErrorCodes.InvalidAssignment);

        var readyEntries = (await h.ReadAuditEntriesAsync()).Where(entry => entry.Action == "ready-for-doctor").ToArray();
        Assert.Collection(
            readyEntries,
            entry => AssertAudit(entry, "ready-for-doctor", RoomStates.Seated, RoomStates.ReadyForDoctor, true, null),
            entry => AssertAudit(entry, "ready-for-doctor", RoomStates.Seated, null, false, PrestagingLifecycleErrorCodes.AssignmentIncomplete),
            entry => AssertAudit(entry, "ready-for-doctor", RoomStates.Seated, null, false, PrestagingLifecycleErrorCodes.InvalidAssignment));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    public async Task Canonical_withdraw_preserves_episode_assignment_and_seating_truth(string? body)
    {
        using var h = new Harness();
        await h.Begin(1, null);
        await h.Seat(1, """{"assignment":{"doctorId":"pledger","procedureCode":"EXT","sedationChoice":"yes","confirmedExpectedAllocationUnits":4}}""");
        var ready = Action(await h.Ready(1, "{}"));
        var before = h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

        var response = await h.Withdraw(1, body);
        var withdrawn = Action(response);
        var after = h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var history = Assert.Single(h.Context.Repository.LoadReadyHandoffsByEpisode(after.EpisodeId!));

        Assert.Equal(["room", "lifecycle", "handoff"], Names(response.Json));
        Assert.Equal(RoomStates.Seated, withdrawn.Room.State);
        Assert.False(withdrawn.Lifecycle.AssignmentLocked);
        Assert.Equal(ReadyUrgency.None, withdrawn.Lifecycle.ReadyUrgency);
        Assert.NotNull(withdrawn.Handoff);
        Assert.Equal(ReadyHandoffStatus.Withdrawn, withdrawn.Handoff.Status);
        Assert.Equal(ready.Handoff!.HandoffId, withdrawn.Handoff.HandoffId);
        Assert.Equal("EXT", withdrawn.Lifecycle.Assignment.ProcedureCode);
        Assert.DoesNotContain("+SED", response.Json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before.EpisodeId, after.EpisodeId);
        Assert.Equal(before.PrestageStartedAt, after.PrestageStartedAt);
        Assert.Equal(before.SeatedAt, after.SeatedAt);
        Assert.Equal(before.AssignedDoctor, after.AssignedDoctor);
        Assert.Equal(before.ProcedureCode, after.ProcedureCode);
        Assert.Equal(before.SedationState, after.SedationState);
        Assert.Equal(before.ExpectedAllocationState, after.ExpectedAllocationState);
        Assert.Equal(before.ExpectedAllocationSuggestedUnits, after.ExpectedAllocationSuggestedUnits);
        Assert.Equal(before.ExpectedAllocationConfirmedUnits, after.ExpectedAllocationConfirmedUnits);
        Assert.Null(after.ReadyForDoctorAt);
        Assert.Null(after.ActiveReadyHandoffId);
        Assert.Null(after.AcceptedReadyHandoffId);
        Assert.Equal(ReadyHandoffStatus.Withdrawn, history.ContractStatus);
        Assert.Equal(ready.Handoff.HandoffId, history.HandoffId);
        Assert.NotNull(history.WithdrawnAt);
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "withdraw-ready", RoomStates.ReadyForDoctor, RoomStates.Seated, true, null);
    }

    [Theory]
    [InlineData(8, ReadyUrgency.Aging)]
    [InlineData(13, ReadyUrgency.Stale)]
    public async Task Canonical_withdraw_clears_ready_only_urgency(int elapsedMinutes, ReadyUrgency expectedBefore)
    {
        using var workspace = TestWorkspace.Create();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 14, 14, 0, 0, TimeSpan.Zero));
        var context = StoreContext.Create(workspace, Environments.Production, timeProvider: clock);
        using var h = new Harness(workspace, context);
        await h.Begin(1, null);
        await h.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
        Action(await h.Ready(1, "{}"));
        clock.SetUtcNow(clock.GetUtcNow().AddMinutes(elapsedMinutes));
        Assert.Equal(expectedBefore, context.Store.GetRoom(1)!.ReadyUrgency);

        var withdrawn = Action(await h.Withdraw(1, "{}"));

        Assert.Equal(RoomStates.Seated, withdrawn.Room.State);
        Assert.Equal(ReadyUrgency.None, withdrawn.Lifecycle.ReadyUrgency);
        Assert.Equal(ReadyUrgency.None, context.Store.GetRoom(1)!.ReadyUrgency);
    }

    [Fact]
    public async Task Canonical_withdraw_unlocks_assignment_details_endpoint()
    {
        using var h = new Harness();
        await h.Begin(1, null);
        await h.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
        Action(await h.Ready(1, "{}"));

        var withdrawn = Action(await h.Withdraw(1, "{}"));
        var updated = Action(await h.Save(
            1,
            """{"doctorId":"pledger","procedureCode":"EXT","sedationChoice":"no","confirmedExpectedAllocationUnits":4}"""));
        var durable = h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var history = Assert.Single(h.Context.Repository.LoadReadyHandoffsByEpisode(durable.EpisodeId!));

        Assert.Equal(RoomStates.Seated, withdrawn.Room.State);
        Assert.False(withdrawn.Lifecycle.AssignmentLocked);
        Assert.Equal(RoomStates.Seated, updated.Room.State);
        Assert.False(updated.Lifecycle.AssignmentLocked);
        Assert.Equal("pledger", updated.Lifecycle.Assignment.DoctorId);
        Assert.Equal("EXT", updated.Lifecycle.Assignment.ProcedureCode);
        Assert.Equal("pledger", durable.AssignedDoctor);
        Assert.Equal("EXT", durable.ProcedureCode);
        Assert.Null(durable.ActiveReadyHandoffId);
        Assert.Equal(ReadyHandoffStatus.Withdrawn, history.ContractStatus);
    }

    [Fact]
    public async Task Canonical_withdraw_rejects_missing_non_ready_and_repeated_requests_without_mutation()
    {
        using var h = new Harness();
        AssertError(await h.Withdraw(999, "{}"), 404, PrestagingLifecycleErrorCodes.RoomNotFound);
        AssertError(await h.Withdraw(1, "{}"), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);
        await h.Begin(1, null);
        AssertError(await h.Withdraw(1, "{}"), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);
        await h.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
        AssertError(await h.Withdraw(1, "{}"), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);
        Action(await h.Ready(1, "{}"));
        Action(await h.Withdraw(1, "{}"));
        var episodeId = h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1).EpisodeId!;
        var historyBefore = Assert.Single(h.Context.Repository.LoadReadyHandoffsByEpisode(episodeId));

        AssertError(await h.Withdraw(1, "{}"), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);

        var historyAfter = Assert.Single(h.Context.Repository.LoadReadyHandoffsByEpisode(episodeId));
        Assert.Equal(historyBefore.HandoffId, historyAfter.HandoffId);
        Assert.Equal(historyBefore.WithdrawnAt, historyAfter.WithdrawnAt);
        Assert.Equal(ReadyHandoffStatus.Withdrawn, historyAfter.ContractStatus);

        await h.Begin(2, null);
        await h.Seat(2, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
        Action(await h.Ready(2, "{}"));
        Assert.NotNull(h.Context.Store.MarkDoctorArrived(2));
        var doctorWorkingBefore = h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 2);
        AssertError(await h.Withdraw(2, "{}"), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);
        var doctorWorkingAfter = h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 2);
        Assert.Equal(RoomStates.DoctorInRoom, doctorWorkingAfter.State);
        Assert.Equal(doctorWorkingBefore.AcceptedReadyHandoffId, doctorWorkingAfter.AcceptedReadyHandoffId);
        var audits = await h.ReadAuditEntriesAsync();
        Assert.Contains(audits, entry => entry.Action == "withdraw-ready" && entry.RoomNumber == 999 && entry.Reason == PrestagingLifecycleErrorCodes.RoomNotFound);
        Assert.Contains(audits, entry => entry.Action == "withdraw-ready" && entry.PreviousState == RoomStates.Available && entry.Reason == PrestagingLifecycleErrorCodes.LifecycleConflict);
        Assert.Contains(audits, entry => entry.Action == "withdraw-ready" && entry.PreviousState == RoomStates.DoctorInRoom && entry.Reason == PrestagingLifecycleErrorCodes.LifecycleConflict);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-episode")]
    [InlineData("wrong-room")]
    [InlineData("withdrawn")]
    [InlineData("accepted")]
    [InlineData("terminated")]
    [InlineData("conflicting-accepted")]
    [InlineData("foreign-withdrawn-history")]
    [InlineData("assignment-mismatch")]
    public async Task Canonical_withdraw_rejects_untruthful_handoff_ownership_as_integrity_fault(string corruption)
    {
        using var workspace = TestWorkspace.Create();
        var seed = StoreContext.Create(workspace, Environments.Production);
        using (var setup = new Harness(workspace, seed))
        {
            await setup.Begin(1, null);
            await setup.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
            Action(await setup.Ready(1, "{}"));
        }
        var ready = seed.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var handoff = Assert.Single(seed.Repository.LoadReadyHandoffsByEpisode(ready.EpisodeId!));
        CorruptReadyHandoff(seed.DatabasePath, handoff, corruption);
        var recovered = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);
        var durableBefore = ReadyRoomSnapshot.From(recovered.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
        var liveBefore = ReadyRoomSnapshot.From(GetLiveRoom(recovered.Store));
        var eventCountBefore = recovered.Store.GetSnapshot().RecentEvents.Count;
        var handoffBefore = JsonSerializer.Serialize(recovered.Repository.LoadReadyHandoff(handoff.HandoffId), JsonOptions);
        var historyBefore = JsonSerializer.Serialize(recovered.Repository.LoadReadyHandoffsByEpisode(ready.EpisodeId!), JsonOptions);
        var reportsBefore = JsonSerializer.Serialize(recovered.Store.GetReports(), JsonOptions);

        using var h = new Harness(workspace, recovered);
        var response = await h.Withdraw(1, "{}");

        var error = AssertError(response, 409, PrestagingLifecycleErrorCodes.IntegrityFault);
        if (corruption == "assignment-mismatch")
        {
            Assert.Contains(
                error.IntegrityFaults,
                fault => fault.Code == RoomIntegrityFaultCode.ReadyHandoffAssignmentMismatch);
        }
        Assert.Equal(durableBefore, ReadyRoomSnapshot.From(recovered.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Equal(liveBefore, ReadyRoomSnapshot.From(GetLiveRoom(recovered.Store)));
        Assert.Equal(eventCountBefore, recovered.Store.GetSnapshot().RecentEvents.Count);
        Assert.Equal(handoffBefore, JsonSerializer.Serialize(recovered.Repository.LoadReadyHandoff(handoff.HandoffId), JsonOptions));
        Assert.Equal(historyBefore, JsonSerializer.Serialize(recovered.Repository.LoadReadyHandoffsByEpisode(ready.EpisodeId!), JsonOptions));
        Assert.Equal(reportsBefore, JsonSerializer.Serialize(recovered.Store.GetReports(), JsonOptions));
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "withdraw-ready", RoomStates.ReadyForDoctor, null, false, PrestagingLifecycleErrorCodes.IntegrityFault);
    }

    [Theory]
    [InlineData("assignment")]
    [InlineData("ownership")]
    public async Task Concurrent_durable_change_makes_withdraw_stale_without_retry(string change)
    {
        using var workspace = TestWorkspace.Create();
        var seed = StoreContext.Create(workspace, Environments.Production);
        using (var setup = new Harness(workspace, seed))
        {
            await setup.Begin(1, null);
            await setup.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
            Action(await setup.Ready(1, "{}"));
        }
        var contextA = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);
        var contextB = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);
        var changed = contextB.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        if (change == "assignment")
        {
            changed.AssignedDoctorDisplayName = "Concurrent durable correction";
        }
        else
        {
            changed.ActiveReadyHandoffId = "concurrent-owner";
        }
        contextB.Repository.SaveRoom(changed, contextB.Doctors, contextB.Procedures);
        var durableChanged = ReadyRoomSnapshot.From(contextB.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
        var liveBefore = ReadyRoomSnapshot.From(GetLiveRoom(contextA.Store));
        var eventsBefore = contextA.Store.GetSnapshot().RecentEvents.Count;
        var reportsBefore = JsonSerializer.Serialize(contextA.Store.GetReports(), JsonOptions);

        using var h = new Harness(workspace, contextA);
        var response = await h.Withdraw(1, "{}");

        AssertError(response, 409, PrestagingLifecycleErrorCodes.StaleWrite);
        Assert.Equal(durableChanged, ReadyRoomSnapshot.From(contextA.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Equal(liveBefore, ReadyRoomSnapshot.From(GetLiveRoom(contextA.Store)));
        Assert.Equal(eventsBefore, contextA.Store.GetSnapshot().RecentEvents.Count);
        Assert.Equal(reportsBefore, JsonSerializer.Serialize(contextA.Store.GetReports(), JsonOptions));
        Assert.Equal(ReadyHandoffStatus.Active, Assert.Single(contextA.Repository.LoadReadyHandoffsByEpisode(changed.EpisodeId!)).ContractStatus);
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "withdraw-ready", RoomStates.ReadyForDoctor, null, false, PrestagingLifecycleErrorCodes.StaleWrite);
    }

    [Fact]
    public async Task Withdraw_endpoint_database_abort_rolls_back_room_handoff_live_state_and_event()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        using var h = new Harness(workspace, context);
        await h.Begin(1, null);
        await h.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
        Action(await h.Ready(1, "{}"));
        var durableBefore = ReadyRoomSnapshot.From(context.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
        var liveBefore = ReadyRoomSnapshot.From(GetLiveRoom(context.Store));
        var handoffBefore = Assert.Single(context.Repository.LoadReadyHandoffsByEpisode(durableBefore.EpisodeId!));
        var eventsBefore = context.Store.GetSnapshot().RecentEvents.Count;
        var reportsBefore = JsonSerializer.Serialize(context.Store.GetReports(), JsonOptions);
        InstallWithdrawFailureTrigger(context.DatabasePath);

        try
        {
            AssertError(await h.Withdraw(1, "{}"), 500, PrestagingLifecycleErrorCodes.PersistenceFailure);
        }
        finally
        {
            DropWithdrawFailureTrigger(context.DatabasePath);
        }

        Assert.Equal(durableBefore, ReadyRoomSnapshot.From(context.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Equal(liveBefore, ReadyRoomSnapshot.From(GetLiveRoom(context.Store)));
        Assert.Equal(eventsBefore, context.Store.GetSnapshot().RecentEvents.Count);
        Assert.Equal(reportsBefore, JsonSerializer.Serialize(context.Store.GetReports(), JsonOptions));
        var handoffAfter = Assert.Single(context.Repository.LoadReadyHandoffsByEpisode(durableBefore.EpisodeId!));
        Assert.Equal(handoffBefore.HandoffId, handoffAfter.HandoffId);
        Assert.Equal(ReadyHandoffStatus.Active, handoffAfter.ContractStatus);
        Assert.Null(handoffAfter.WithdrawnAt);
        var reloaded = StoreContext.Create(workspace, Environments.Production, context.DatabasePath);
        Assert.Equal(durableBefore, ReadyRoomSnapshot.From(reloaded.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Equal(reportsBefore, JsonSerializer.Serialize(reloaded.Store.GetReports(), JsonOptions));
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "withdraw-ready", RoomStates.ReadyForDoctor, null, false, PrestagingLifecycleErrorCodes.PersistenceFailure);
    }

    [Theory]
    [InlineData("text/plain", "{}")]
    [InlineData("application/json", " ")]
    [InlineData("application/json", "[]")]
    [InlineData("application/json", "{not-json}")]
    [InlineData("application/json", "{\"unknown\":true}")]
    [InlineData("application/json", "{\"unknown\":true,\"Unknown\":false}")]
    public async Task Canonical_withdraw_rejects_invalid_wire_shapes(string contentType, string body)
    {
        using var h = new Harness();

        AssertError(await h.Withdraw(1, body, contentType), 400, PrestagingLifecycleErrorCodes.MalformedRequest);
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "withdraw-ready", RoomStates.Available, null, false, PrestagingLifecycleErrorCodes.MalformedRequest);
    }

    private static string[] Names(string json) => JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToArray();
    private static RoomAssignmentContract CompleteConsult() =>
        RoomAssignmentContract.Create(
            "otte",
            "CON",
            SedationContract.UnavailableProcedureIneligible(),
            ExpectedAllocationContract.ConfirmedSuggestedValue(1));
    private static RoomAssignmentContract CompleteExtraction() =>
        RoomAssignmentContract.Create(
            "pledger",
            "EXT",
            SedationContract.EligibleNo(),
            ExpectedAllocationContract.ConfirmedAdjustedValue(3, 4));
    private static PrestagingLifecycleActionResponse Action(Response response)
    {
        Assert.Equal(200, response.StatusCode);
        return Assert.IsType<PrestagingLifecycleActionResponse>(response.Value);
    }
    private static PrestagingLifecycleErrorResponse AssertError(Response response, int status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var error = Assert.IsType<PrestagingLifecycleErrorResponse>(response.Value);
        Assert.Equal(code, error.Code);
        return error;
    }
    private static void AssertAudit(
        RoomAuditEntry entry,
        string action,
        string? previousState,
        string? newState,
        bool success,
        string? reason)
    {
        Assert.Equal(action, entry.Action);
        Assert.Equal(previousState, entry.PreviousState);
        Assert.Equal(newState, entry.NewState);
        Assert.Equal(success, entry.Success);
        Assert.Equal(reason, entry.Reason);
    }
    private static void AssertLastAudit(
        IReadOnlyList<RoomAuditEntry> entries,
        string action,
        string? previousState,
        string? newState,
        bool success,
        string? reason) =>
        AssertAudit(Assert.Single(entries.Where(entry => entry.Action == action)), action, previousState, newState, success, reason);

    private static RoomState GetLiveRoom(DemoBoardStore store)
    {
        var field = typeof(DemoBoardStore).GetField("_rooms", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var rooms = Assert.IsType<List<RoomState>>(field.GetValue(store));
        return rooms.Single(room => room.RoomId == 1);
    }

    private static RoomState CopyRoom(RoomState room) =>
        new(room.RoomId)
        {
            EpisodeId = room.EpisodeId,
            AssignedDoctor = room.AssignedDoctor,
            AssignedDoctorDisplayName = room.AssignedDoctorDisplayName,
            ProcedureCode = room.ProcedureCode,
            ProcedureCategory = room.ProcedureCategory,
            SedationState = room.SedationState,
            ExpectedAllocationState = room.ExpectedAllocationState,
            ExpectedAllocationSuggestedUnits = room.ExpectedAllocationSuggestedUnits,
            ExpectedAllocationConfirmedUnits = room.ExpectedAllocationConfirmedUnits,
            State = room.State,
            PrestageStartedAt = room.PrestageStartedAt,
            SeatedAt = room.SeatedAt,
            ReadyForDoctorAt = room.ReadyForDoctorAt,
            ActiveReadyHandoffId = room.ActiveReadyHandoffId,
            AcceptedReadyHandoffId = room.AcceptedReadyHandoffId,
            OriginalDefaultExpectedUnits = room.OriginalDefaultExpectedUnits,
            ExpectedAllocationUnits = room.ExpectedAllocationUnits,
            ExpectedAllocationMinutes = room.ExpectedAllocationMinutes,
            AllocationAdjustedFromDefault = room.AllocationAdjustedFromDefault
        };

    private static void InstallReadyInsertFailureTrigger(string databasePath) =>
        ExecuteSql(databasePath, """
            CREATE TRIGGER fail_ready_endpoint_insert
            BEFORE INSERT ON ready_handoffs
            FOR EACH ROW
            WHEN NEW.room_id = 1
            BEGIN
                SELECT RAISE(ABORT, 'injected ready endpoint failure');
            END;
            """);

    private static void DropReadyInsertFailureTrigger(string databasePath) =>
        ExecuteSql(databasePath, "DROP TRIGGER IF EXISTS fail_ready_endpoint_insert;");

    private static void InstallWithdrawFailureTrigger(string databasePath) =>
        ExecuteSql(databasePath, """
            CREATE TRIGGER fail_withdraw_endpoint_update
            BEFORE UPDATE OF withdrawn_at ON ready_handoffs
            FOR EACH ROW
            WHEN NEW.room_id = 1
            BEGIN
                SELECT RAISE(ABORT, 'injected withdraw endpoint failure');
            END;
            """);

    private static void DropWithdrawFailureTrigger(string databasePath) =>
        ExecuteSql(databasePath, "DROP TRIGGER IF EXISTS fail_withdraw_endpoint_update;");

    private static void CorruptReadyHandoff(string databasePath, PersistedReadyHandoff handoff, string corruption)
    {
        var id = handoff.HandoffId.Replace("'", "''", StringComparison.Ordinal);
        var outcomeAt = handoff.ReadyAt.AddMinutes(1).ToString("O");
        var sql = corruption switch
        {
            "missing" => $"DELETE FROM ready_handoffs WHERE handoff_id = '{id}';",
            "wrong-episode" => $"UPDATE ready_handoffs SET episode_id = 'wrong-episode' WHERE handoff_id = '{id}';",
            "wrong-room" => $"UPDATE ready_handoffs SET room_id = 2 WHERE handoff_id = '{id}';",
            "withdrawn" => $"UPDATE ready_handoffs SET withdrawn_at = '{outcomeAt}' WHERE handoff_id = '{id}';",
            "accepted" => $"UPDATE ready_handoffs SET accepted_at = '{outcomeAt}' WHERE handoff_id = '{id}';",
            "terminated" => $"UPDATE ready_handoffs SET terminated_at = '{outcomeAt}', termination_kind = 'Expired' WHERE handoff_id = '{id}';",
            "assignment-mismatch" => "UPDATE active_rooms SET assigned_doctor_id = 'pledger' WHERE room_id = 1;",
            "conflicting-accepted" => $"""
                INSERT INTO ready_handoffs (
                    handoff_id, episode_id, room_id, ready_at, withdrawn_at, accepted_at,
                    terminated_at, termination_kind, doctor_id, procedure_code, sedation_state,
                    expected_allocation_state, expected_allocation_suggested_units,
                    expected_allocation_confirmed_units)
                SELECT
                    'conflicting-accepted', episode_id, room_id, ready_at, NULL, '{outcomeAt}',
                    NULL, NULL, doctor_id, procedure_code, sedation_state,
                    expected_allocation_state, expected_allocation_suggested_units,
                    expected_allocation_confirmed_units
                FROM ready_handoffs
                WHERE handoff_id = '{id}';
                """,
            "foreign-withdrawn-history" => $"""
                INSERT INTO ready_handoffs (
                    handoff_id, episode_id, room_id, ready_at, withdrawn_at, accepted_at,
                    terminated_at, termination_kind, doctor_id, procedure_code, sedation_state,
                    expected_allocation_state, expected_allocation_suggested_units,
                    expected_allocation_confirmed_units)
                SELECT
                    'foreign-withdrawn-history', episode_id, 2, ready_at, '{outcomeAt}', NULL,
                    NULL, NULL, doctor_id, procedure_code, sedation_state,
                    expected_allocation_state, expected_allocation_suggested_units,
                    expected_allocation_confirmed_units
                FROM ready_handoffs
                WHERE handoff_id = '{id}';
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, "Unknown handoff corruption.")
        };
        ExecuteSql(databasePath, sql);
    }

    private static void ExecuteSql(string databasePath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class Harness : IDisposable
    {
        private readonly TestWorkspace _workspace;
        private readonly bool _ownsWorkspace;
        private readonly RoomDeviceTokenValidator _validator;
        private readonly DiagnosticLogger _logger;
        private readonly IWebHostEnvironment _environment;
        public Harness()
        {
            _workspace = TestWorkspace.Create();
            _ownsWorkspace = true;
            Context = StoreContext.Create(_workspace, Environments.Production);
            _validator = new(new TestOptionsMonitor<RoomDeviceBindingOptions>(new RoomDeviceBindingOptions { Enabled = false }));
            _environment = new TestWebHostEnvironment(_workspace.ContentRoot, Environments.Production);
            _logger = new(Microsoft.Extensions.Options.Options.Create(new DiagnosticOptions { LogDirectory = Path.Combine(_workspace.DataRoot, "logs") }), _environment);
        }
        public Harness(TestWorkspace workspace, StoreContext context)
        {
            _workspace = workspace;
            Context = context;
            _validator = new(new TestOptionsMonitor<RoomDeviceBindingOptions>(new RoomDeviceBindingOptions { Enabled = false }));
            _environment = new TestWebHostEnvironment(_workspace.ContentRoot, Environments.Production);
            _logger = new(Microsoft.Extensions.Options.Options.Create(new DiagnosticOptions { LogDirectory = Path.Combine(_workspace.DataRoot, "logs") }), _environment);
        }
        public StoreContext Context { get; }
        public async Task<Response> Begin(int room, string? body) => Capture(await global::RoomLifecycleEndpointHandler.BeginPrestageRouteAsync(room, Request(room, body), _validator, Context.Store, _logger, new NoopBoardHubContext()));
        public async Task<Response> Save(int room, string? body, string? contentType = "application/json") => Capture(await global::RoomLifecycleEndpointHandler.SaveAssignmentDetailsAsync(room, Request(room, body, contentType), _validator, Context.Store, _logger, new NoopBoardHubContext()));
        public async Task<Response> Seat(int room, string? body) => Capture(await global::RoomLifecycleEndpointHandler.SeatAsync(room, Request(room, body), _validator, Context.Store, _environment, _logger, new NoopBoardHubContext()));
        public async Task<Response> Ready(int room, string? body, string? contentType = "application/json") => Capture(await global::RoomLifecycleEndpointHandler.ReadyForDoctorAsync(room, Request(room, body, contentType), _validator, Context.Store, _logger, new NoopBoardHubContext()));
        public async Task<Response> Withdraw(int room, string? body, string? contentType = "application/json") => Capture(await global::RoomLifecycleEndpointHandler.WithdrawReadyAsync(room, Request(room, body, contentType), _validator, Context.Store, _logger, new NoopBoardHubContext()));
        public async Task<IReadOnlyList<RoomAuditEntry>> ReadAuditEntriesAsync()
        {
            var path = Path.Combine(_workspace.DataRoot, "logs", "room-audit.log");
            if (!File.Exists(path)) return [];
            var entries = new List<RoomAuditEntry>();
            foreach (var line in await File.ReadAllLinesAsync(path))
            {
                if (JsonSerializer.Deserialize<RoomAuditEntry>(line, JsonOptions) is { } entry) entries.Add(entry);
            }
            return entries;
        }
        public void Dispose()
        {
            if (_ownsWorkspace) _workspace.Dispose();
        }
        private static DefaultHttpContext Request(int room, string? body, string? contentType = "application/json")
        {
            var context = new DefaultHttpContext();
            context.Request.Path = $"/api/rooms/{room}";
            if (body is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Request.Body = new MemoryStream(bytes);
                context.Request.ContentLength = bytes.Length;
                if (contentType is not null) context.Request.ContentType = contentType;
            }
            return context;
        }
        private static Response Capture(IResult result)
        {
            var status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
            var value = (result as IValueHttpResult)?.Value;
            return new(status, value, JsonSerializer.Serialize(value, JsonOptions));
        }
    }
    private sealed record Response(int StatusCode, object? Value, string Json);

    private sealed record ReadyRoomSnapshot(
        string? EpisodeId,
        string? AssignedDoctor,
        string? AssignedDoctorDisplayName,
        string? ProcedureCode,
        string? ProcedureCategory,
        SedationState? SedationState,
        ExpectedAllocationState? ExpectedAllocationState,
        int? ExpectedAllocationSuggestedUnits,
        int? ExpectedAllocationConfirmedUnits,
        string State,
        DateTimeOffset? PrestageStartedAt,
        DateTimeOffset? SeatedAt,
        DateTimeOffset? AgingStartedAt,
        DateTimeOffset? StaleStartedAt,
        DateTimeOffset? ReadyForDoctorAt,
        DateTimeOffset? DoctorArrivedAt,
        DateTimeOffset? DoctorCompleteAt,
        DateTimeOffset? RoomAvailableAt,
        string? ActiveReadyHandoffId,
        string? AcceptedReadyHandoffId,
        int OriginalDefaultExpectedUnits,
        int ExpectedAllocationUnits,
        int ExpectedAllocationMinutes,
        bool AllocationAdjustedFromDefault)
    {
        public static ReadyRoomSnapshot From(RoomState room) =>
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
                room.State,
                room.PrestageStartedAt,
                room.SeatedAt,
                room.AgingStartedAt,
                room.StaleStartedAt,
                room.ReadyForDoctorAt,
                room.DoctorArrivedAt,
                room.DoctorCompleteAt,
                room.RoomAvailableAt,
                room.ActiveReadyHandoffId,
                room.AcceptedReadyHandoffId,
                room.OriginalDefaultExpectedUnits,
                room.ExpectedAllocationUnits,
                room.ExpectedAllocationMinutes,
                room.AllocationAdjustedFromDefault);
    }
}
