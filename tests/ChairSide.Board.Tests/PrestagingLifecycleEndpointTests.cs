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

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("{}", false)]
    public async Task Canonical_doctor_arrived_accepts_the_owned_handoff_and_preserves_bodyless_compatibility(
        string? body,
        bool legacyResponse)
    {
        using var h = new Harness();
        await h.Begin(1, null);
        await h.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
        var ready = Action(await h.Ready(1, "{}"));
        var before = h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

        var response = await h.DoctorArrived(1, body);

        var arrivedRoom = legacyResponse
            ? Assert.IsType<RoomStatus>(response.Value)
            : Action(response).Room;
        if (legacyResponse)
        {
            Assert.Contains("state", Names(response.Json));
            Assert.DoesNotContain("room", Names(response.Json));
        }
        else
        {
            var envelope = Action(response);
            Assert.Equal(["room", "lifecycle", "handoff"], Names(response.Json));
            Assert.Equal(CanonicalRoomLifecycleState.DoctorWorking, envelope.Lifecycle.State);
            Assert.True(envelope.Lifecycle.AssignmentLocked);
            Assert.Equal(ReadyUrgency.None, envelope.Lifecycle.ReadyUrgency);
            Assert.Equal(ReadyHandoffStatus.Accepted, envelope.Handoff!.Status);
            Assert.Equal("CON", envelope.Handoff.Assignment.ProcedureCode);
        }

        var after = h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var handoff = Assert.Single(h.Context.Repository.LoadReadyHandoffsByEpisode(after.EpisodeId!));
        Assert.Equal(RoomStates.DoctorInRoom, arrivedRoom.State);
        Assert.Equal(RoomStates.DoctorInRoom, after.State);
        Assert.Equal(before.EpisodeId, after.EpisodeId);
        Assert.Equal(before.PrestageStartedAt, after.PrestageStartedAt);
        Assert.Equal(before.SeatedAt, after.SeatedAt);
        Assert.Equal(before.ReadyForDoctorAt, after.ReadyForDoctorAt);
        Assert.Equal(before.AssignedDoctor, after.AssignedDoctor);
        Assert.Equal(before.ProcedureCode, after.ProcedureCode);
        Assert.Equal(before.SedationState, after.SedationState);
        Assert.Equal(before.ExpectedAllocationState, after.ExpectedAllocationState);
        Assert.Equal(before.ExpectedAllocationSuggestedUnits, after.ExpectedAllocationSuggestedUnits);
        Assert.Equal(before.ExpectedAllocationConfirmedUnits, after.ExpectedAllocationConfirmedUnits);
        Assert.NotNull(after.DoctorArrivedAt);
        Assert.Null(after.ActiveReadyHandoffId);
        Assert.Equal(ready.Handoff!.HandoffId, after.AcceptedReadyHandoffId);
        Assert.Equal(ready.Handoff.HandoffId, handoff.HandoffId);
        Assert.Equal(ReadyHandoffStatus.Accepted, handoff.ContractStatus);
        Assert.Equal(after.DoctorArrivedAt, handoff.AcceptedAt);
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "doctor-arrived", RoomStates.ReadyForDoctor, RoomStates.DoctorInRoom, true, null);
    }

    [Fact]
    public async Task Canonical_doctor_arrived_reports_missing_lifecycle_and_repeat_conflicts_without_mutation()
    {
        using var h = new Harness();
        AssertError(await h.DoctorArrived(999, "{}"), 404, PrestagingLifecycleErrorCodes.RoomNotFound);
        AssertError(await h.DoctorArrived(1, "{}"), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);
        await h.Begin(1, null);
        AssertError(await h.DoctorArrived(1, "{}"), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);
        await h.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
        AssertError(await h.DoctorArrived(1, "{}"), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);
        Action(await h.Ready(1, "{}"));
        Action(await h.DoctorArrived(1, "{}"));
        var before = ReadyRoomSnapshot.From(h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
        var historyBefore = JsonSerializer.Serialize(h.Context.Repository.LoadReadyHandoffsByEpisode(before.EpisodeId!), JsonOptions);

        AssertError(await h.DoctorArrived(1, "{}"), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);

        Assert.Equal(before, ReadyRoomSnapshot.From(h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Equal(historyBefore, JsonSerializer.Serialize(h.Context.Repository.LoadReadyHandoffsByEpisode(before.EpisodeId!), JsonOptions));

        Assert.NotNull(h.Context.Store.MarkDoctorComplete(1));
        var turnoverBefore = ReadyRoomSnapshot.From(h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
        AssertError(await h.DoctorArrived(1, "{}"), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);
        Assert.Equal(turnoverBefore, ReadyRoomSnapshot.From(h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
    }

    [Fact]
    public async Task Bodyless_doctor_conflict_preserves_the_room_panel_response_contract()
    {
        using var h = new Harness();
        await h.Begin(2, null);
        await h.Seat(2, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
        Action(await h.Ready(2, "{}"));
        Action(await h.DoctorArrived(2, "{}"));
        await h.Begin(1, null);
        await h.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
        Action(await h.Ready(1, "{}"));

        var bodyless = await h.DoctorArrived(1, null);
        var conflict = Assert.IsType<DoctorArrivedConflictResponse>(bodyless.Value);
        Assert.Equal(409, bodyless.StatusCode);
        Assert.Equal(2, conflict.ConflictingRoomId);
        Assert.Equal("otte", conflict.DoctorId);
        Assert.Contains("conflictingRoomId", Names(bodyless.Json));

        AssertError(await h.DoctorArrived(1, "{}"), 409, PrestagingLifecycleErrorCodes.LifecycleConflict);
        Assert.Equal(RoomStates.ReadyForDoctor, h.Context.Store.GetRoom(1)!.State);
    }

    [Theory]
    [InlineData(8, ReadyUrgency.Aging)]
    [InlineData(13, ReadyUrgency.Stale)]
    public async Task Canonical_doctor_arrived_clears_ready_urgency_and_keeps_the_accepted_snapshot(
        int elapsedMinutes,
        ReadyUrgency expectedBefore)
    {
        using var workspace = TestWorkspace.Create();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 15, 14, 0, 0, TimeSpan.Zero));
        var context = StoreContext.Create(workspace, Environments.Production, timeProvider: clock);
        using var h = new Harness(workspace, context);
        await h.Begin(1, null);
        await h.Seat(1, """{"assignment":{"doctorId":"pledger","procedureCode":"EXT","sedationChoice":"yes","confirmedExpectedAllocationUnits":4}}""");
        var ready = Action(await h.Ready(1, "{}"));
        clock.SetUtcNow(clock.GetUtcNow().AddMinutes(elapsedMinutes));
        Assert.Equal(expectedBefore, context.Store.GetRoom(1)!.ReadyUrgency);

        var arrived = Action(await h.DoctorArrived(1, "{}"));
        var durableRoom = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var accepted = Assert.Single(context.Repository.LoadReadyHandoffsByEpisode(durableRoom.EpisodeId!));
        var cycle = Assert.Single(context.Repository.LoadCompletedCycles());

        Assert.Equal(ReadyUrgency.None, arrived.Lifecycle.ReadyUrgency);
        Assert.True(arrived.Lifecycle.AssignmentLocked);
        Assert.Equal(ReadyHandoffStatus.Accepted, accepted.ContractStatus);
        Assert.Equal(ready.Handoff!.HandoffId, accepted.HandoffId);
        Assert.Equal(accepted.HandoffId, cycle.AcceptedReadyHandoffId);
        Assert.Equal(accepted.Assignment.DoctorId, cycle.AssignedDoctor);
        Assert.Equal(accepted.Assignment.ProcedureCode, cycle.ProcedureCode);
        Assert.Equal(accepted.Assignment.ExpectedAllocationConfirmedUnits, cycle.ExpectedAllocationUnits);
        Assert.Equal(expectedBefore is ReadyUrgency.Aging or ReadyUrgency.Stale, cycle.AgingThresholdReached);
        Assert.Equal(expectedBefore == ReadyUrgency.Stale, cycle.StaleThresholdReached);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-episode")]
    [InlineData("wrong-room")]
    [InlineData("withdrawn")]
    [InlineData("accepted")]
    [InlineData("terminated")]
    [InlineData("assignment-mismatch")]
    [InlineData("conflicting-accepted")]
    [InlineData("foreign-withdrawn-history")]
    [InlineData("conflicting-active")]
    public async Task Canonical_doctor_arrived_rejects_untruthful_handoff_history_without_mutation(string corruption)
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
        var historyBefore = JsonSerializer.Serialize(recovered.Repository.LoadReadyHandoffsByEpisode(ready.EpisodeId!), JsonOptions);
        var eventsBefore = recovered.Store.GetSnapshot().RecentEvents.Count;
        var reportsBefore = JsonSerializer.Serialize(recovered.Store.GetReports(), JsonOptions);

        using var h = new Harness(workspace, recovered);
        var response = await h.DoctorArrived(1, "{}");

        AssertError(response, 409, PrestagingLifecycleErrorCodes.IntegrityFault);
        Assert.Equal(durableBefore, ReadyRoomSnapshot.From(recovered.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Equal(liveBefore, ReadyRoomSnapshot.From(GetLiveRoom(recovered.Store)));
        Assert.Equal(historyBefore, JsonSerializer.Serialize(recovered.Repository.LoadReadyHandoffsByEpisode(ready.EpisodeId!), JsonOptions));
        Assert.Equal(eventsBefore, recovered.Store.GetSnapshot().RecentEvents.Count);
        Assert.Equal(reportsBefore, JsonSerializer.Serialize(recovered.Store.GetReports(), JsonOptions));
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "doctor-arrived", RoomStates.ReadyForDoctor, null, false, PrestagingLifecycleErrorCodes.IntegrityFault);
    }

    [Theory]
    [InlineData("assignment")]
    [InlineData("ownership")]
    public async Task Concurrent_durable_change_makes_doctor_arrived_stale_without_retry(string change)
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
        if (change == "assignment") changed.AssignedDoctorDisplayName = "Concurrent durable correction";
        else changed.ActiveReadyHandoffId = "concurrent-owner";
        contextB.Repository.SaveRoom(changed, contextB.Doctors, contextB.Procedures);
        var durableChanged = ReadyRoomSnapshot.From(contextB.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
        var liveBefore = ReadyRoomSnapshot.From(GetLiveRoom(contextA.Store));
        var eventsBefore = contextA.Store.GetSnapshot().RecentEvents.Count;

        using var h = new Harness(workspace, contextA);
        AssertError(await h.DoctorArrived(1, "{}"), 409, PrestagingLifecycleErrorCodes.StaleWrite);

        Assert.Equal(durableChanged, ReadyRoomSnapshot.From(contextA.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Equal(liveBefore, ReadyRoomSnapshot.From(GetLiveRoom(contextA.Store)));
        Assert.Equal(eventsBefore, contextA.Store.GetSnapshot().RecentEvents.Count);
        Assert.Equal(ReadyHandoffStatus.Active, Assert.Single(contextA.Repository.LoadReadyHandoffsByEpisode(changed.EpisodeId!)).ContractStatus);
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "doctor-arrived", RoomStates.ReadyForDoctor, null, false, PrestagingLifecycleErrorCodes.StaleWrite);
    }

    [Fact]
    public async Task Doctor_arrived_endpoint_database_abort_rolls_back_room_handoff_cycle_live_state_and_event()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        using var h = new Harness(workspace, context);
        await h.Begin(1, null);
        await h.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
        Action(await h.Ready(1, "{}"));
        var before = ReadyRoomSnapshot.From(context.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
        var handoffBefore = Assert.Single(context.Repository.LoadReadyHandoffsByEpisode(before.EpisodeId!));
        Assert.Empty(context.Repository.LoadCompletedCycles());
        var eventsBefore = context.Store.GetSnapshot().RecentEvents.Count;
        var reportsBefore = JsonSerializer.Serialize(context.Store.GetReports(), JsonOptions);
        InstallDoctorArrivedFailureTrigger(context.DatabasePath);
        try
        {
            AssertError(await h.DoctorArrived(1, "{}"), 500, PrestagingLifecycleErrorCodes.PersistenceFailure);
        }
        finally
        {
            DropDoctorArrivedFailureTrigger(context.DatabasePath);
        }

        Assert.Equal(before, ReadyRoomSnapshot.From(context.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Equal(before, ReadyRoomSnapshot.From(GetLiveRoom(context.Store)));
        Assert.Equal(eventsBefore, context.Store.GetSnapshot().RecentEvents.Count);
        Assert.Equal(reportsBefore, JsonSerializer.Serialize(context.Store.GetReports(), JsonOptions));
        Assert.Empty(context.Repository.LoadCompletedCycles());
        var handoffAfter = Assert.Single(context.Repository.LoadReadyHandoffsByEpisode(before.EpisodeId!));
        Assert.Equal(handoffBefore.HandoffId, handoffAfter.HandoffId);
        Assert.Equal(ReadyHandoffStatus.Active, handoffAfter.ContractStatus);
        var reloaded = StoreContext.Create(workspace, Environments.Production, context.DatabasePath);
        Assert.Equal(before, ReadyRoomSnapshot.From(reloaded.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Empty(reloaded.Repository.LoadCompletedCycles());
        Assert.Equal(ReadyHandoffStatus.Active, Assert.Single(
            reloaded.Repository.LoadReadyHandoffsByEpisode(before.EpisodeId!)).ContractStatus);
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "doctor-arrived", RoomStates.ReadyForDoctor, null, false, PrestagingLifecycleErrorCodes.PersistenceFailure);
    }

    [Fact]
    public async Task Overlapping_contexts_serialize_same_doctor_ownership_before_cross_room_validation()
    {
        using var workspace = TestWorkspace.Create();
        var seed = StoreContext.Create(workspace, Environments.Production);
        using (var setup = new Harness(workspace, seed))
        {
            await setup.Begin(1, null);
            await setup.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
            Action(await setup.Ready(1, "{}"));
            await setup.Begin(2, null);
            await setup.Seat(2, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
            Action(await setup.Ready(2, "{}"));
        }

        var contextA = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);
        var contextB = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);
        var roomBefore = new Dictionary<int, ReadyRoomSnapshot>
        {
            [1] = ReadyRoomSnapshot.From(contextA.Repository.LoadRooms(3).Single(room => room.RoomId == 1)),
            [2] = ReadyRoomSnapshot.From(contextB.Repository.LoadRooms(3).Single(room => room.RoomId == 2))
        };
        var liveBefore = new Dictionary<int, ReadyRoomSnapshot>
        {
            [1] = ReadyRoomSnapshot.From(GetLiveRoom(contextA.Store, 1)),
            [2] = ReadyRoomSnapshot.From(GetLiveRoom(contextB.Store, 2))
        };
        var eventsBefore = new Dictionary<int, int>
        {
            [1] = contextA.Store.GetSnapshot().RecentEvents.Count,
            [2] = contextB.Store.GetSnapshot().RecentEvents.Count
        };
        var handoffBefore = roomBefore.ToDictionary(
            pair => pair.Key,
            pair => JsonSerializer.Serialize(
                (pair.Key == 1 ? contextA : contextB).Repository.LoadReadyHandoffsByEpisode(pair.Value.EpisodeId!),
                JsonOptions));
        using var first = new Harness(workspace, contextA);
        using var second = new Harness(workspace, contextB);

        using var blockerConnection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = seed.DatabasePath,
                DefaultTimeout = 5
            }.ToString());
        blockerConnection.Open();
        using var blockerTransaction = blockerConnection.BeginTransaction(deferred: false);
        using var started = new CountdownEvent(2);
        var firstAttempt = Task.Run(async () =>
        {
            started.Signal();
            return (RoomId: 1, Response: await first.DoctorArrived(1, null));
        });
        var secondAttempt = Task.Run(async () =>
        {
            started.Signal();
            return (RoomId: 2, Response: await second.DoctorArrived(2, null));
        });

        Assert.True(started.Wait(TimeSpan.FromSeconds(2)), "Both Doctor Arrived attempts must start while the writer reservation is held.");
        await Task.Delay(100);
        Assert.False(firstAttempt.IsCompleted, "The first context must wait for the existing immediate writer transaction.");
        Assert.False(secondAttempt.IsCompleted, "The second context must wait for the existing immediate writer transaction.");
        blockerTransaction.Commit();

        var attempts = await Task.WhenAll(firstAttempt, secondAttempt);
        var succeeded = Assert.Single(attempts.Where(attempt => attempt.Response.StatusCode == 200));
        var failed = Assert.Single(attempts.Where(attempt => attempt.Response.StatusCode == 409));
        Assert.IsType<RoomStatus>(succeeded.Response.Value);
        var conflict = Assert.IsType<DoctorArrivedConflictResponse>(failed.Response.Value);
        Assert.Equal(succeeded.RoomId, conflict.ConflictingRoomId);
        Assert.Equal("otte", conflict.DoctorId);
        var losingRoomId = failed.RoomId;
        var losingContext = losingRoomId == 1 ? contextA : contextB;
        var losingHarness = losingRoomId == 1 ? first : second;
        var durableRooms = losingContext.Repository.LoadRooms(3);
        Assert.Equal(1, durableRooms.Count(room => room.State == RoomStates.DoctorInRoom));
        Assert.Equal(RoomStates.DoctorInRoom, durableRooms.Single(room => room.RoomId == succeeded.RoomId).State);
        Assert.Equal(roomBefore[losingRoomId], ReadyRoomSnapshot.From(durableRooms.Single(room => room.RoomId == losingRoomId)));
        Assert.Equal(liveBefore[losingRoomId], ReadyRoomSnapshot.From(GetLiveRoom(losingContext.Store, losingRoomId)));
        Assert.Equal(eventsBefore[losingRoomId], losingContext.Store.GetSnapshot().RecentEvents.Count);
        Assert.Equal(
            handoffBefore[losingRoomId],
            JsonSerializer.Serialize(losingContext.Repository.LoadReadyHandoffsByEpisode(roomBefore[losingRoomId].EpisodeId!), JsonOptions));
        Assert.Equal(ReadyHandoffStatus.Accepted, Assert.Single(losingContext.Repository.LoadReadyHandoffsByEpisode(
            durableRooms.Single(room => room.RoomId == succeeded.RoomId).EpisodeId!)).ContractStatus);
        Assert.Equal(ReadyHandoffStatus.Active, Assert.Single(losingContext.Repository.LoadReadyHandoffsByEpisode(
            roomBefore[losingRoomId].EpisodeId!)).ContractStatus);
        var cycle = Assert.Single(losingContext.Repository.LoadCompletedCycles());
        Assert.Equal(succeeded.RoomId, cycle.RoomId);

        var reloaded = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);
        var reloadedRooms = reloaded.Repository.LoadRooms(3);
        Assert.Equal(1, reloadedRooms.Count(room => room.State == RoomStates.DoctorInRoom));
        Assert.Equal(roomBefore[losingRoomId], ReadyRoomSnapshot.From(reloadedRooms.Single(room => room.RoomId == losingRoomId)));
        Assert.Equal(ReadyHandoffStatus.Accepted, Assert.Single(reloaded.Repository.LoadReadyHandoffsByEpisode(
            reloadedRooms.Single(room => room.RoomId == succeeded.RoomId).EpisodeId!)).ContractStatus);
        Assert.Equal(ReadyHandoffStatus.Active, Assert.Single(reloaded.Repository.LoadReadyHandoffsByEpisode(
            roomBefore[losingRoomId].EpisodeId!)).ContractStatus);
        Assert.Equal(succeeded.RoomId, Assert.Single(reloaded.Repository.LoadCompletedCycles()).RoomId);
        Assert.Contains(
            await losingHarness.ReadAuditEntriesAsync(),
            entry => entry.Action == "doctor-arrived"
                && entry.RoomNumber == losingRoomId
                && !entry.Success
                && entry.Reason == PrestagingLifecycleErrorCodes.LifecycleConflict);
    }

    [Fact]
    public async Task Doctor_arrived_uses_the_other_working_rooms_accepted_handoff_and_rejects_assignment_divergence()
    {
        using var workspace = TestWorkspace.Create();
        var seed = StoreContext.Create(workspace, Environments.Production);
        using (var setup = new Harness(workspace, seed))
        {
            await setup.Begin(1, null);
            await setup.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
            Action(await setup.Ready(1, "{}"));
            await setup.Begin(2, null);
            await setup.Seat(2, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
            Action(await setup.Ready(2, "{}"));
            Action(await setup.DoctorArrived(2, "{}"));
        }

        ExecuteSql(seed.DatabasePath, "UPDATE active_rooms SET assigned_doctor_id = 'pledger', assigned_doctor_display_name = 'Dr. Pledger' WHERE room_id = 2;");
        var context = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);
        using var h = new Harness(workspace, context);
        var durableBefore = context.Repository.LoadRooms(3)
            .ToDictionary(room => room.RoomId, ReadyRoomSnapshot.From);
        var liveBefore = new Dictionary<int, ReadyRoomSnapshot>
        {
            [1] = ReadyRoomSnapshot.From(GetLiveRoom(context.Store, 1)),
            [2] = ReadyRoomSnapshot.From(GetLiveRoom(context.Store, 2))
        };
        var episodeIds = durableBefore.Values
            .Select(room => room.EpisodeId)
            .Where(episodeId => !string.IsNullOrWhiteSpace(episodeId))
            .Select(episodeId => episodeId!)
            .ToArray();
        var handoffsBefore = JsonSerializer.Serialize(
            episodeIds.SelectMany(context.Repository.LoadReadyHandoffsByEpisode).OrderBy(handoff => handoff.HandoffId),
            JsonOptions);
        var cyclesBefore = JsonSerializer.Serialize(context.Repository.LoadCompletedCycles(), JsonOptions);
        var eventsBefore = context.Store.GetSnapshot().RecentEvents.Count;

        AssertError(await h.DoctorArrived(1, "{}"), 409, PrestagingLifecycleErrorCodes.IntegrityFault);

        var durableAfter = context.Repository.LoadRooms(3).ToDictionary(room => room.RoomId, ReadyRoomSnapshot.From);
        Assert.Equal(durableBefore[1], durableAfter[1]);
        Assert.Equal(durableBefore[2], durableAfter[2]);
        Assert.Equal(liveBefore[1], ReadyRoomSnapshot.From(GetLiveRoom(context.Store, 1)));
        Assert.Equal(liveBefore[2], ReadyRoomSnapshot.From(GetLiveRoom(context.Store, 2)));
        Assert.Equal(handoffsBefore, JsonSerializer.Serialize(
            episodeIds.SelectMany(context.Repository.LoadReadyHandoffsByEpisode).OrderBy(handoff => handoff.HandoffId),
            JsonOptions));
        Assert.Equal(cyclesBefore, JsonSerializer.Serialize(context.Repository.LoadCompletedCycles(), JsonOptions));
        Assert.Equal(eventsBefore, context.Store.GetSnapshot().RecentEvents.Count);
        var reloaded = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);
        var reloadedRooms = reloaded.Repository.LoadRooms(3).ToDictionary(room => room.RoomId, ReadyRoomSnapshot.From);
        Assert.Equal(durableBefore[1], reloadedRooms[1]);
        Assert.Equal(durableBefore[2], reloadedRooms[2]);
        Assert.Equal(handoffsBefore, JsonSerializer.Serialize(
            episodeIds.SelectMany(reloaded.Repository.LoadReadyHandoffsByEpisode).OrderBy(handoff => handoff.HandoffId),
            JsonOptions));
        Assert.Equal(cyclesBefore, JsonSerializer.Serialize(reloaded.Repository.LoadCompletedCycles(), JsonOptions));
        Assert.Contains(
            await h.ReadAuditEntriesAsync(),
            entry => entry.Action == "doctor-arrived"
                && entry.RoomNumber == 1
                && entry.PreviousState == RoomStates.ReadyForDoctor
                && entry.NewState is null
                && !entry.Success
                && entry.Reason == PrestagingLifecycleErrorCodes.IntegrityFault);
    }

    [Fact]
    public async Task Doctor_arrived_uses_a_narrow_room_assignment_fallback_for_legacy_working_rooms()
    {
        using var workspace = TestWorkspace.Create();
        var seed = StoreContext.Create(workspace, Environments.Production);
        using (var setup = new Harness(workspace, seed))
        {
            await setup.Begin(1, null);
            await setup.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
            Action(await setup.Ready(1, "{}"));
        }

        var context = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);
        var legacyWriter = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);
        var legacyArrivedAt = new DateTimeOffset(2026, 7, 15, 15, 0, 0, TimeSpan.Zero);
        var legacyRoom = new RoomState(2)
        {
            AssignedDoctor = "otte",
            AssignedDoctorDisplayName = "Dr. Otte",
            ProcedureCode = "CON",
            ProcedureCategory = "Consult",
            SedationState = SedationState.UnavailableProcedureIneligible,
            ExpectedAllocationState = ExpectedAllocationState.ConfirmedSuggestedValue,
            ExpectedAllocationSuggestedUnits = 1,
            ExpectedAllocationConfirmedUnits = 1,
            State = RoomStates.DoctorInRoom,
            SeatedAt = legacyArrivedAt.AddMinutes(-5),
            DoctorArrivedAt = legacyArrivedAt,
            OriginalDefaultExpectedUnits = 1,
            ExpectedAllocationUnits = 1,
            ExpectedAllocationMinutes = 10
        };
        legacyWriter.Repository.SaveRoom(legacyRoom, legacyWriter.Doctors, legacyWriter.Procedures);
        var targetBefore = ReadyRoomSnapshot.From(context.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
        var legacyBefore = ReadyRoomSnapshot.From(context.Repository.LoadRooms(3).Single(room => room.RoomId == 2));
        var targetLiveBefore = ReadyRoomSnapshot.From(GetLiveRoom(context.Store, 1));
        var legacyLiveBefore = ReadyRoomSnapshot.From(GetLiveRoom(context.Store, 2));
        var handoffsBefore = JsonSerializer.Serialize(
            context.Repository.LoadReadyHandoffsByEpisode(targetBefore.EpisodeId!),
            JsonOptions);
        var eventsBefore = context.Store.GetSnapshot().RecentEvents.Count;
        using var h = new Harness(workspace, context);

        var response = await h.DoctorArrived(1, null);

        Assert.Equal(409, response.StatusCode);
        var conflict = Assert.IsType<DoctorArrivedConflictResponse>(response.Value);
        Assert.Equal(2, conflict.ConflictingRoomId);
        Assert.Equal("otte", conflict.DoctorId);
        var durableAfter = context.Repository.LoadRooms(3);
        Assert.Equal(targetBefore, ReadyRoomSnapshot.From(durableAfter.Single(room => room.RoomId == 1)));
        Assert.Equal(legacyBefore, ReadyRoomSnapshot.From(durableAfter.Single(room => room.RoomId == 2)));
        Assert.Equal(targetLiveBefore, ReadyRoomSnapshot.From(GetLiveRoom(context.Store, 1)));
        Assert.Equal(legacyLiveBefore, ReadyRoomSnapshot.From(GetLiveRoom(context.Store, 2)));
        Assert.Equal(handoffsBefore, JsonSerializer.Serialize(
            context.Repository.LoadReadyHandoffsByEpisode(targetBefore.EpisodeId!),
            JsonOptions));
        Assert.Empty(context.Repository.LoadCompletedCycles());
        Assert.Equal(eventsBefore, context.Store.GetSnapshot().RecentEvents.Count);
        var reloaded = StoreContext.Create(workspace, Environments.Production, seed.DatabasePath);
        Assert.Equal(targetBefore, ReadyRoomSnapshot.From(reloaded.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Equal(legacyBefore, ReadyRoomSnapshot.From(reloaded.Repository.LoadRooms(3).Single(room => room.RoomId == 2)));
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "doctor-arrived", RoomStates.ReadyForDoctor, null, false, PrestagingLifecycleErrorCodes.LifecycleConflict);
    }

    [Fact]
    public async Task Doctor_arrived_rejects_a_preexisting_cycle_without_rewriting_contradictory_history()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        using var h = new Harness(workspace, context);
        await h.Begin(1, null);
        await h.Seat(1, """{"assignment":{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}}""");
        Action(await h.Ready(1, "{}"));
        var ready = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var activeHandoff = Assert.Single(context.Repository.LoadReadyHandoffsByEpisode(ready.EpisodeId!));
        var contradictoryCycle = new CompletedRoomCycle
        {
            EpisodeId = "contradictory-episode",
            AcceptedReadyHandoffId = "contradictory-handoff",
            RoomId = 1,
            AssignedDoctor = "gibson",
            ProcedureCode = "BX",
            PrestageStartedAt = ready.PrestageStartedAt,
            SeatedAt = ready.SeatedAt!.Value,
            ReadyForDoctorAt = ready.ReadyForDoctorAt,
            DoctorArrivedAt = ready.ReadyForDoctorAt!.Value.AddMinutes(1),
            DoctorCompleteAt = ready.ReadyForDoctorAt.Value.AddMinutes(2),
            RoomAvailableAt = ready.ReadyForDoctorAt.Value.AddMinutes(3),
            SeatedToDoctorSeconds = 60,
            PrepSeconds = 30,
            ReadyToDoctorSeconds = 30,
            DoctorInRoomSeconds = 60,
            TurnoverSeconds = 60,
            TotalRoomCycleSeconds = 180,
            OriginalDefaultExpectedUnits = 2,
            ExpectedAllocationUnits = 2,
            ExpectedAllocationMinutes = 20,
            AllocationAdjustedFromDefault = false,
            FinalWaitState = "ContradictoryHistory",
            IsException = true,
            RequiresReview = true,
            ExceptionReason = "preserve-exactly",
            SuggestedAction = "do-not-rewrite"
        };
        context.Repository.SaveCompletedCycle(contradictoryCycle, context.Doctors, context.Procedures);
        var durableBefore = ReadyRoomSnapshot.From(ready);
        var liveBefore = ReadyRoomSnapshot.From(GetLiveRoom(context.Store));
        var cycleBefore = JsonSerializer.Serialize(context.Repository.LoadCompletedCycles(), JsonOptions);
        var handoffBefore = JsonSerializer.Serialize(activeHandoff, JsonOptions);
        var eventsBefore = context.Store.GetSnapshot().RecentEvents.Count;

        AssertError(await h.DoctorArrived(1, "{}"), 409, PrestagingLifecycleErrorCodes.IntegrityFault);

        Assert.Equal(durableBefore, ReadyRoomSnapshot.From(context.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Equal(liveBefore, ReadyRoomSnapshot.From(GetLiveRoom(context.Store)));
        Assert.Equal(cycleBefore, JsonSerializer.Serialize(context.Repository.LoadCompletedCycles(), JsonOptions));
        Assert.Equal(handoffBefore, JsonSerializer.Serialize(context.Repository.LoadReadyHandoff(activeHandoff.HandoffId), JsonOptions));
        Assert.Equal(eventsBefore, context.Store.GetSnapshot().RecentEvents.Count);
        Assert.Equal(ReadyHandoffStatus.Active, context.Repository.LoadReadyHandoff(activeHandoff.HandoffId)!.ContractStatus);
        Assert.Null(context.Repository.LoadRooms(3).Single(room => room.RoomId == 1).DoctorArrivedAt);
        var reloaded = StoreContext.Create(workspace, Environments.Production, context.DatabasePath);
        Assert.Equal(durableBefore, ReadyRoomSnapshot.From(reloaded.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Equal(cycleBefore, JsonSerializer.Serialize(reloaded.Repository.LoadCompletedCycles(), JsonOptions));
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "doctor-arrived", RoomStates.ReadyForDoctor, null, false, PrestagingLifecycleErrorCodes.IntegrityFault);
    }

    [Fact]
    public async Task Canonical_doctor_arrived_preserves_the_accepted_snapshot_through_final_reporting()
    {
        using var workspace = TestWorkspace.Create();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 15, 14, 0, 0, TimeSpan.Zero));
        var context = StoreContext.Create(workspace, Environments.Production, timeProvider: clock);
        using var h = new Harness(workspace, context);
        await h.Begin(1, null);
        await h.Seat(1, """{"assignment":{"doctorId":"pledger","procedureCode":"EXT","sedationChoice":"yes","confirmedExpectedAllocationUnits":4}}""");
        var ready = Action(await h.Ready(1, "{}"));
        clock.SetUtcNow(clock.GetUtcNow().AddMinutes(5));
        Action(await h.DoctorArrived(1, "{}"));
        var episodeId = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1).EpisodeId!;
        clock.SetUtcNow(clock.GetUtcNow().AddMinutes(20));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(clock.GetUtcNow().AddMinutes(5));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var accepted = Assert.Single(context.Repository.LoadReadyHandoffsByEpisode(episodeId));
        var durableCycle = Assert.Single(context.Repository.LoadCompletedCycles());
        var reports = context.Store.GetReports();
        var reportedCycle = Assert.Single(reports.RecentCompletedCycles);

        Assert.Equal(ReadyHandoffStatus.Accepted, accepted.ContractStatus);
        Assert.Equal(ready.Handoff!.HandoffId, accepted.HandoffId);
        Assert.Equal("pledger", accepted.Assignment.DoctorId);
        Assert.Equal("EXT+SED", accepted.Assignment.ProcedureCode);
        Assert.Equal(SedationState.EligibleYes, accepted.Assignment.SedationState);
        Assert.Equal(4, accepted.Assignment.ExpectedAllocationConfirmedUnits);
        Assert.Equal(accepted.HandoffId, durableCycle.AcceptedReadyHandoffId);
        Assert.Equal(accepted.Assignment.DoctorId, durableCycle.AssignedDoctor);
        Assert.Equal(accepted.Assignment.ProcedureCode, durableCycle.ProcedureCode);
        Assert.Equal(accepted.Assignment.ExpectedAllocationConfirmedUnits, durableCycle.ExpectedAllocationUnits);
        Assert.Equal(accepted.Assignment.ExpectedAllocationConfirmedUnits!.Value * 10, durableCycle.ExpectedAllocationMinutes);
        Assert.Equal((int)(accepted.AcceptedAt!.Value - accepted.ReadyAt).TotalSeconds, durableCycle.ReadyToDoctorSeconds);
        Assert.Equal(300, durableCycle.ReadyToDoctorSeconds);
        Assert.Equal(durableCycle.AcceptedReadyHandoffId, reportedCycle.AcceptedReadyHandoffId);
        Assert.Equal(durableCycle.AssignedDoctor, reportedCycle.AssignedDoctor);
        Assert.Equal(durableCycle.ProcedureCode, reportedCycle.ProcedureCode);
        Assert.Equal(durableCycle.ExpectedAllocationUnits, reportedCycle.ExpectedAllocationUnits);
        Assert.Equal(durableCycle.ReadyToDoctorSeconds, reportedCycle.ReadyToDoctorSeconds);
        Assert.Equal(1, reports.SedationCaseCount);
        Assert.Equal(0, reports.NonSedationCaseCount);

        var reloaded = StoreContext.Create(workspace, Environments.Production, context.DatabasePath);
        var reloadedReportCycle = Assert.Single(reloaded.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(durableCycle.AcceptedReadyHandoffId, reloadedReportCycle.AcceptedReadyHandoffId);
        Assert.Equal(durableCycle.AssignedDoctor, reloadedReportCycle.AssignedDoctor);
        Assert.Equal(durableCycle.ProcedureCode, reloadedReportCycle.ProcedureCode);
        Assert.Equal(durableCycle.ExpectedAllocationUnits, reloadedReportCycle.ExpectedAllocationUnits);
        Assert.Equal(durableCycle.ReadyToDoctorSeconds, reloadedReportCycle.ReadyToDoctorSeconds);
    }

    [Fact]
    public async Task Canonical_doctor_arrived_keeps_assignment_details_locked_without_mutation()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        using var h = new Harness(workspace, context);
        await h.Begin(1, null);
        await h.Seat(1, """{"assignment":{"doctorId":"pledger","procedureCode":"EXT","sedationChoice":"yes","confirmedExpectedAllocationUnits":4}}""");
        Action(await h.Ready(1, "{}"));
        Action(await h.DoctorArrived(1, "{}"));
        var roomBefore = ReadyRoomSnapshot.From(h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1));
        var liveBefore = ReadyRoomSnapshot.From(GetLiveRoom(h.Context.Store));
        var handoffBefore = JsonSerializer.Serialize(h.Context.Repository.LoadReadyHandoffsByEpisode(roomBefore.EpisodeId!), JsonOptions);
        var cyclesBefore = JsonSerializer.Serialize(h.Context.Repository.LoadCompletedCycles(), JsonOptions);
        var reportsBefore = JsonSerializer.Serialize(h.Context.Store.GetReports(), JsonOptions);
        var eventsBefore = h.Context.Store.GetSnapshot().RecentEvents.Count;

        AssertError(
            await h.Save(1, """{"doctorId":"otte","procedureCode":"CON","confirmedExpectedAllocationUnits":1}"""),
            409,
            PrestagingLifecycleErrorCodes.AssignmentLocked);

        Assert.Equal(roomBefore, ReadyRoomSnapshot.From(h.Context.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Equal(liveBefore, ReadyRoomSnapshot.From(GetLiveRoom(h.Context.Store)));
        Assert.Equal(handoffBefore, JsonSerializer.Serialize(h.Context.Repository.LoadReadyHandoffsByEpisode(roomBefore.EpisodeId!), JsonOptions));
        Assert.Equal(cyclesBefore, JsonSerializer.Serialize(h.Context.Repository.LoadCompletedCycles(), JsonOptions));
        Assert.Equal(reportsBefore, JsonSerializer.Serialize(h.Context.Store.GetReports(), JsonOptions));
        Assert.Equal(eventsBefore, h.Context.Store.GetSnapshot().RecentEvents.Count);
        var reloaded = StoreContext.Create(workspace, Environments.Production, context.DatabasePath);
        Assert.Equal(roomBefore, ReadyRoomSnapshot.From(reloaded.Repository.LoadRooms(3).Single(room => room.RoomId == 1)));
        Assert.Equal(handoffBefore, JsonSerializer.Serialize(reloaded.Repository.LoadReadyHandoffsByEpisode(roomBefore.EpisodeId!), JsonOptions));
        Assert.Equal(cyclesBefore, JsonSerializer.Serialize(reloaded.Repository.LoadCompletedCycles(), JsonOptions));
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "save-assignment-details", RoomStates.DoctorInRoom, null, false, PrestagingLifecycleErrorCodes.AssignmentLocked);
    }

    [Theory]
    [InlineData("text/plain", "{}")]
    [InlineData("application/json", " ")]
    [InlineData("application/json", "[]")]
    [InlineData("application/json", "{not-json}")]
    [InlineData("application/json", "{\"unknown\":true}")]
    [InlineData("application/json", "{\"unknown\":true,\"Unknown\":false}")]
    public async Task Canonical_doctor_arrived_rejects_invalid_wire_shapes(string contentType, string body)
    {
        using var h = new Harness();
        AssertError(await h.DoctorArrived(1, body, contentType), 400, PrestagingLifecycleErrorCodes.MalformedRequest);
        AssertLastAudit(await h.ReadAuditEntriesAsync(), "doctor-arrived", RoomStates.Available, null, false, PrestagingLifecycleErrorCodes.MalformedRequest);
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

    private static RoomState GetLiveRoom(DemoBoardStore store, int roomId = 1)
    {
        var field = typeof(DemoBoardStore).GetField("_rooms", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var rooms = Assert.IsType<List<RoomState>>(field.GetValue(store));
        return rooms.Single(room => room.RoomId == roomId);
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

    private static void InstallDoctorArrivedFailureTrigger(string databasePath) =>
        ExecuteSql(databasePath, """
            CREATE TRIGGER fail_doctor_arrived_endpoint_cycle_insert
            AFTER INSERT ON completed_room_cycles
            FOR EACH ROW
            WHEN NEW.room_id = 1
            BEGIN
                SELECT RAISE(ABORT, 'injected doctor arrived cycle failure');
            END;
            """);

    private static void DropDoctorArrivedFailureTrigger(string databasePath) =>
        ExecuteSql(databasePath, "DROP TRIGGER IF EXISTS fail_doctor_arrived_endpoint_cycle_insert;");

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
            "conflicting-active" => $"""
                UPDATE ready_handoffs
                SET handoff_id = 'unreferenced-active'
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
        public async Task<Response> DoctorArrived(int room, string? body, string? contentType = "application/json") => Capture(await global::RoomLifecycleEndpointHandler.DoctorArrivedAsync(room, Request(room, body, contentType), _validator, Context.Store, _logger, new NoopBoardHubContext()));
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
