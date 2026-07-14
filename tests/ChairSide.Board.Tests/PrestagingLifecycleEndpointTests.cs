using System.Text;
using System.Text.Json;
using ChairSide.Board.Options;
using ChairSide.Board.Services;
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

    private static string[] Names(string json) => JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToArray();
    private static PrestagingLifecycleActionResponse Action(Response response)
    {
        Assert.Equal(200, response.StatusCode);
        return Assert.IsType<PrestagingLifecycleActionResponse>(response.Value);
    }
    private static void AssertError(Response response, int status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, Assert.IsType<PrestagingLifecycleErrorResponse>(response.Value).Code);
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

    private sealed class Harness : IDisposable
    {
        private readonly TestWorkspace _workspace = TestWorkspace.Create();
        private readonly RoomDeviceTokenValidator _validator;
        private readonly DiagnosticLogger _logger;
        private readonly IWebHostEnvironment _environment;
        public Harness()
        {
            Context = StoreContext.Create(_workspace, Environments.Production);
            _validator = new(new TestOptionsMonitor<RoomDeviceBindingOptions>(new RoomDeviceBindingOptions { Enabled = false }));
            _environment = new TestWebHostEnvironment(_workspace.ContentRoot, Environments.Production);
            _logger = new(Microsoft.Extensions.Options.Options.Create(new DiagnosticOptions { LogDirectory = Path.Combine(_workspace.DataRoot, "logs") }), _environment);
        }
        public StoreContext Context { get; }
        public async Task<Response> Begin(int room, string? body) => Capture(await global::RoomLifecycleEndpointHandler.BeginPrestageRouteAsync(room, Request(room, body), _validator, Context.Store, _logger, new NoopBoardHubContext()));
        public async Task<Response> Save(int room, string? body, string? contentType = "application/json") => Capture(await global::RoomLifecycleEndpointHandler.SaveAssignmentDetailsAsync(room, Request(room, body, contentType), _validator, Context.Store, _logger, new NoopBoardHubContext()));
        public async Task<Response> Seat(int room, string? body) => Capture(await global::RoomLifecycleEndpointHandler.SeatAsync(room, Request(room, body), _validator, Context.Store, _environment, _logger, new NoopBoardHubContext()));
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
        public void Dispose() => _workspace.Dispose();
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
}
