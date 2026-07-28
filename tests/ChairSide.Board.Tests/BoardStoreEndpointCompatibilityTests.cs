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

        Assert.Equal(200, await ExecuteBindingResult(await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            1,
            new BeginPrestageRequest(DoctorId: "otte", ProcedureCode: "EXT", ExpectedAllocationUnits: 5),
            NewRoomMutationHttpContext(1, token: null), CreateBindingValidator(enabled: false),
            context.Store, logger, new NoopBoardHubContext())));
        var prestaged = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        var episodeId = prestaged.EpisodeId;
        var prestageAt = prestaged.PrestageStartedAt;

        // Canonical Seat contract: an empty object preserves the saved assignment.
        var response = await global::RoomLifecycleEndpointHandler.SeatAsync(
            1, NewJsonBodyContext(1, token: null, "{}"),
            CreateBindingValidator(enabled: false), context.Store, logger, new NoopBoardHubContext());

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
            CreateBindingValidator(enabled: false), context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(409, await ExecuteBindingResult(bypass));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(2)?.State);
    }

    [Fact]
    public async Task Procedure_alias_resolution_rejects_conflicts_and_accepts_single_or_matching_aliases()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
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
    }

    [Fact]
    public async Task Seat_endpoint_treats_no_body_empty_body_and_empty_object_as_canonical_empty_actions()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var validator = CreateBindingValidator(enabled: false);

        // No body at all (matches the current reasonless-style caller expectations for optional bodies).
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var noBody = await global::RoomLifecycleEndpointHandler.SeatAsync(
            1, NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(noBody));
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(1)?.State);
        Assert.NotNull(context.Store.CancelSeating(1));

        // Empty body (zero bytes).
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var emptyBody = await global::RoomLifecycleEndpointHandler.SeatAsync(
            1, NewJsonBodyContext(1, token: null, ""), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(emptyBody));
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(1)?.State);
        Assert.NotNull(context.Store.CancelSeating(1));

        // Empty JSON object.
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var emptyObject = await global::RoomLifecycleEndpointHandler.SeatAsync(
            1, NewJsonBodyContext(1, token: null, "{}"), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(emptyObject));
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(1)?.State);
    }

    [Fact]
    public async Task Seat_endpoint_accepts_canonical_null_assignment_and_rejects_flat_malformed_or_wrong_content_type()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 6, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var databasePath = Path.Combine(workspace.DataRoot, "development-seat-contract.db");
        var context = StoreContext.Create(
            workspace, environmentName: Environments.Development, databasePath: databasePath, timeProvider: clock);
        context.Store.ResetAllDataForEmptyBeta();
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var validator = CreateBindingValidator(enabled: false);

        // A canonical null assignment preserves the durably saved draft.
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        var canonical = await global::RoomLifecycleEndpointHandler.SeatAsync(
            1, NewJsonBodyContext(1, token: null, """{"assignment":null}"""),
            validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(200, await ExecuteBindingResult(canonical));
        Assert.Equal(now, context.Store.GetRoom(1)?.SeatedAt);

        // The former flat simulation property is now unknown and rejected without mutation.
        Assert.NotNull(context.Store.BeginPrestage(2, "pledger", "CON"));
        var unknownProperty = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, """{"demoElapsedMinutes":0}"""),
            validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(unknownProperty));
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(2)?.State);

        // Any former flat assignment property is rejected by the canonical parser.
        var flatAssignment = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, """{"sedation":true}"""),
            validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(flatAssignment));
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(2)?.State);

        // Malformed JSON: rejected, no mutation.
        var malformed = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, "{not-json"),
            validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(malformed));
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(2)?.State);

        // Unsupported content type on a non-empty body: rejected, no mutation.
        var wrongContentType = await global::RoomLifecycleEndpointHandler.SeatAsync(
            2, NewJsonBodyContext(2, token: null, """{"assignment":null}""", contentType: "text/plain"),
            validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(wrongContentType));
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(2)?.State);
    }

    [Fact]
    public async Task Seat_endpoint_rejects_every_flat_compatibility_property_without_mutation()
    {
        // Each former flat property is now outside the canonical Seat contract. The room is left
        // Prestaging so an accidental fallback to a bare Seat transition would be observable.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);
        var logger = CreateDiagnosticLogger(Path.Combine(workspace.DataRoot, "logs"), workspace.ContentRoot);
        var validator = CreateBindingValidator(enabled: false);

        string[] bodies =
        [
            """{"sedation":false}""",
            """{"expectedAllocationUnits":null}""",
            """{"doctorId":null}""",
            """{"procedureCode":null}""",
            """{"procedureId":null}""",
            """{"demoElapsedMinutes":0}""",
            """{"doctorId":"otte","procedureCode":"CON","sedation":false,"expectedAllocationUnits":1}"""
        ];

        foreach (var body in bodies)
        {
            Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
            var before = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);

            var response = await global::RoomLifecycleEndpointHandler.SeatAsync(
                1, NewJsonBodyContext(1, token: null, body), validator, context.Store, logger, new NoopBoardHubContext());

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
        Assert.NotNull(context.Store.SeatRoomCanonical(3, null).Room);
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
        var enabledValidator = CreateBindingValidator(enabled: true);

        async Task<int?> InvokeBegin(int room, DefaultHttpContext httpContext) =>
            await ExecuteBindingResult(await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
                room, new BeginPrestageRequest(DoctorId: "otte", ProcedureCode: "CON"),
                httpContext, enabledValidator, context.Store, logger, new NoopBoardHubContext()));

        async Task<int?> InvokeSeat(int room, DefaultHttpContext httpContext) =>
            await ExecuteBindingResult(await global::RoomLifecycleEndpointHandler.SeatAsync(
                room, httpContext, enabledValidator, context.Store, logger, new NoopBoardHubContext()));

        async Task<int?> InvokeCancelPrestage(int room, DefaultHttpContext httpContext) =>
            await ExecuteBindingResult(await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
                room, httpContext, enabledValidator, context.Store, logger, new NoopBoardHubContext()));

        async Task<int?> InvokeCancelSeating(int room, DefaultHttpContext httpContext) =>
            await ExecuteBindingResult(await global::RoomLifecycleEndpointHandler.CancelSeatingAsync(
                room, httpContext, enabledValidator, context.Store, logger, new NoopBoardHubContext()));

        // Table-driven: the same missing-token / invalid-token / cross-room-token cases run for all
        // four routes. None of these should ever mutate a room, so rooms 1 and 2 are safely reused
        // across every route in the table (CreateBindingValidator configures room 1 -> "room-1-token",
        // room 2 -> "room-2-token").
        var routes = new (string Name, Func<int, DefaultHttpContext, Task<int?>> Invoke)[]
        {
            ("begin-prestage", InvokeBegin),
            ("seat", InvokeSeat),
            ("cancel-prestage", InvokeCancelPrestage),
            ("cancel-seating", InvokeCancelSeating)
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
        var validator = CreateBindingValidator(enabled: false);
        const int unconfiguredRoom = 999;

        var begin = await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            unconfiguredRoom, new BeginPrestageRequest(DoctorId: "otte", ProcedureCode: "CON"),
            NewRoomMutationHttpContext(unconfiguredRoom, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(404, await ExecuteBindingResult(begin));

        var seat = await global::RoomLifecycleEndpointHandler.SeatAsync(
            unconfiguredRoom, NewRoomMutationHttpContext(unconfiguredRoom, token: null), validator,
            context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(404, await ExecuteBindingResult(seat));

        var cancelPrestage = await global::RoomLifecycleEndpointHandler.CancelPrestageAsync(
            unconfiguredRoom, NewRoomMutationHttpContext(unconfiguredRoom, token: null), validator,
            context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(404, await ExecuteBindingResult(cancelPrestage));

        var cancelSeating = await global::RoomLifecycleEndpointHandler.CancelSeatingAsync(
            unconfiguredRoom, NewRoomMutationHttpContext(unconfiguredRoom, token: null), validator,
            context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(404, await ExecuteBindingResult(cancelSeating));

    }

    [Fact]
    public async Task Begin_prestage_endpoint_returns_400_for_invalid_procedure_allocation()
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
        var validator = CreateBindingValidator(enabled: false);

        var begin = await global::RoomLifecycleEndpointHandler.BeginPrestageAsync(
            1, new BeginPrestageRequest(DoctorId: "otte", ProcedureCode: "BAD"),
            NewRoomMutationHttpContext(1, token: null), validator, context.Store, logger, new NoopBoardHubContext());
        Assert.Equal(400, await ExecuteBindingResult(begin));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
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

        Assert.NotNull(context.Store.SeatRoomCanonical(3, null).Room);
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

}
