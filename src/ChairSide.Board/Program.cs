using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

using ChairSide.Board.Hubs;
using ChairSide.Board.Options;
using ChairSide.Board.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<BoardThresholdOptions>()
    .Bind(builder.Configuration.GetSection(BoardThresholdOptions.SectionName))
    .Validate(options => options.AgingMinutes > 0, "AgingMinutes must be greater than 0.")
    .Validate(options => options.StaleMinutes > options.AgingMinutes, "StaleMinutes must be greater than AgingMinutes.")
    .ValidateOnStart();

builder.Services
    .AddOptions<BoardOptions>()
    .Bind(builder.Configuration.GetSection(BoardOptions.SectionName))
    .Validate(options => options.RoomCount > 0, "RoomCount must be greater than 0.")
    .ValidateOnStart();

builder.Services
    .AddOptions<BoardUiOptions>()
    .Bind(builder.Configuration.GetSection(BoardUiOptions.SectionName));

builder.Services
    .AddOptions<BoardPersistenceOptions>()
    .Bind(builder.Configuration.GetSection(BoardPersistenceOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.DatabasePath), "DatabasePath is required.")
    .ValidateOnStart();

builder.Services
    .AddOptions<RoomDeviceBindingOptions>()
    .Bind(builder.Configuration.GetSection(RoomDeviceBindingOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<RoomDeviceBindingOptions>, RoomDeviceBindingOptionsValidator>();

builder.Services
    .AddOptions<AdminAccessOptions>()
    .Bind(builder.Configuration.GetSection(AdminAccessOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AdminAccessOptions>, AdminAccessOptionsValidator>();

builder.Services
    .AddOptions<DoctorRosterOptions>()
    .Bind(builder.Configuration.GetSection(DoctorRosterOptions.SectionName))
    .ValidateOnStart();
builder.Services.PostConfigure<DoctorRosterOptions>(options =>
{
    if (options.Doctors.Count == 0)
    {
        options.Doctors = DoctorRosterOptions.DefaultDoctors();
    }
});
builder.Services.AddSingleton<IValidateOptions<DoctorRosterOptions>, DoctorRosterOptionsValidator>();

builder.Services
    .AddOptions<ProcedureRosterOptions>()
    .Bind(builder.Configuration.GetSection(ProcedureRosterOptions.SectionName))
    .ValidateOnStart();
builder.Services.PostConfigure<ProcedureRosterOptions>(options =>
{
    if (options.Procedures.Count == 0)
    {
        options.Procedures = ProcedureRosterOptions.DefaultProcedures();
    }
});
builder.Services.AddSingleton<IValidateOptions<ProcedureRosterOptions>, ProcedureRosterOptionsValidator>();

builder.Services
    .AddOptions<DiagnosticOptions>()
    .Bind(builder.Configuration.GetSection(DiagnosticOptions.SectionName));

builder.Services
    .AddOptions<RoomExpirationOptions>()
    .Bind(builder.Configuration.GetSection(RoomExpirationOptions.SectionName));

builder.Services.AddSignalR();
builder.Services.AddSingleton<SqliteBoardRepository>();
builder.Services.AddSingleton<DemoBoardStore>();
builder.Services.AddSingleton<RoomDeviceTokenValidator>();
builder.Services.AddSingleton<AdminAccessTokenValidator>();
builder.Services.AddSingleton<DiagnosticLogger>();
builder.Services.AddSingleton<ClientErrorRateLimiter>();
builder.Services.AddHostedService<RoomExpirationService>();

var app = builder.Build();
_ = app.Services.GetRequiredService<DemoBoardStore>();

var roomDeviceBindingOptions = app.Services.GetRequiredService<IOptions<RoomDeviceBindingOptions>>().Value;
var adminAccessOptions = app.Services.GetRequiredService<IOptions<AdminAccessOptions>>().Value;
app.Logger.LogInformation("Room device binding enabled: {Enabled}", roomDeviceBindingOptions.Enabled);
app.Logger.LogInformation("Admin/report access protection enabled: {Enabled}", adminAccessOptions.Enabled);
if (app.Environment.IsProduction())
{
    if (!roomDeviceBindingOptions.Enabled)
    {
        app.Logger.LogWarning("Room device binding is disabled in Production.");
    }

    if (!adminAccessOptions.Enabled)
    {
        app.Logger.LogWarning("Admin/report access protection is disabled in Production.");
    }
}

app.Use(async (context, next) =>
{
    if (!AdminAccessGuard.IsProtectedPath(context.Request.Path))
    {
        await next();
        return;
    }

    var validator = context.RequestServices.GetRequiredService<AdminAccessTokenValidator>();
    var failure = AdminAccessGuard.ValidateRequest(context.Request, validator);
    if (failure is null)
    {
        await next();
        return;
    }

    await failure.ExecuteAsync(context);
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<BoardHub>("/boardHub");

app.MapGet("/api/board", (DemoBoardStore store) => store.GetSnapshot());

app.MapGet("/api/reports", (DemoBoardStore store) => store.GetReports());

// Admin-protected: mark a completed cycle as an exception, removing it from normal metrics.
// Protected by AdminAccessGuard via the /api/reports/* path prefix.
app.MapPost("/api/reports/cycles/mark-exception", async Task<IResult> (
    MarkExceptionRequest request,
    DemoBoardStore store,
    DiagnosticLogger diagnosticLogger,
    Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
{
    // Prefer the stable CompletedCycleId when supplied; otherwise fall back to the legacy
    // (RoomId, SeatedAt) compound key so existing UI and older clients keep working.
    bool success;
    int auditRoomNumber;
    if (request.CompletedCycleId is > 0)
    {
        success = store.MarkCycleAsExceptionById(
            request.CompletedCycleId.Value,
            ExceptionReasons.ManualReview,
            "Exclude from normal metrics");
        auditRoomNumber = request.RoomId ?? 0;
    }
    else if (request.RoomId is > 0 && request.SeatedAt.HasValue)
    {
        success = store.MarkCycleAsException(
            request.RoomId.Value,
            request.SeatedAt.Value,
            ExceptionReasons.ManualReview,
            "Exclude from normal metrics");
        auditRoomNumber = request.RoomId.Value;
    }
    else
    {
        return Results.BadRequest("Provide a positive completedCycleId, or both a positive roomId and seatedAt.");
    }

    if (!success)
    {
        return Results.NotFound("No matching completed cycle found for the supplied identity.");
    }

    await diagnosticLogger.LogRoomAuditAsync(new RoomAuditEntry
    {
        Timestamp = DateTimeOffset.UtcNow.ToString("O"),
        Action = "mark-exception",
        RoomNumber = auditRoomNumber,
        Success = true,
        Reason = ExceptionReasons.ManualReview
    });

    await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    return Results.NoContent();
});

// Admin-protected: confirm the exclusion of an exception cycle, completing its review.
// The cycle remains an exception (still excluded from normal metrics); confirming review only
// clears it from the pending-review queue. Targeted solely by the stable completedCycleId.
// Protected by AdminAccessGuard via the /api/reports/* path prefix.
app.MapPost("/api/reports/cycles/{completedCycleId:long}/confirm-exclusion", async Task<IResult> (
    long completedCycleId,
    DemoBoardStore store,
    DiagnosticLogger diagnosticLogger,
    Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
{
    var result = store.ReviewExceptionCycleById(completedCycleId);

    if (result.Outcome == ReviewExceptionOutcome.NotFound)
    {
        return Results.NotFound("No matching completed cycle found for the supplied completedCycleId.");
    }

    if (result.Outcome == ReviewExceptionOutcome.NotAnException)
    {
        return Results.BadRequest("The completed cycle exists but is not an exception.");
    }

    await diagnosticLogger.LogRoomAuditAsync(new RoomAuditEntry
    {
        Timestamp = DateTimeOffset.UtcNow.ToString("O"),
        Action = "confirm-exclusion",
        RoomNumber = result.RoomId,
        Success = true,
        Reason = ExceptionReasons.ManualReview
    });

    await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    return Results.NoContent();
});

app.MapGet("/api/rooms/{roomNumber:int}", IResult (int roomNumber, DemoBoardStore store) =>
{
    var room = store.GetRoom(roomNumber);
    return room is null ? Results.NotFound("Room is not configured.") : Results.Ok(room);
});

// Client-side JavaScript error reporting. Not admin-protected so normal clients can post.
app.MapPost("/api/client-errors", async (
    ClientErrorRequest request,
    HttpContext httpContext,
    ClientErrorRateLimiter rateLimiter,
    DiagnosticLogger diagnosticLogger) =>
{
    var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
    if (!rateLimiter.IsAllowed(clientIp))
    {
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    // Truncate free-text fields to prevent abuse; no PHI is expected in these fields.
    var entry = new ClientErrorEntry
    {
        ServerTimestamp = DateTimeOffset.UtcNow.ToString("O"),
        ClientTimestamp = request.Timestamp,
        Url = Truncate(request.Url, 500),
        RoomId = Truncate(request.RoomId, 20),
        View = Truncate(request.View, 50),
        Message = Truncate(request.Message, 500),
        Source = Truncate(request.Source, 300),
        Line = request.Line,
        Column = request.Column,
        Stack = Truncate(request.Stack, 2000),
        UserAgent = Truncate(request.UserAgent, 300),
        ConnectionStatus = Truncate(request.ConnectionStatus, 30),
        LastSnapshotAt = request.LastSnapshotAt,
        SnapshotAgeMs = request.SnapshotAgeMs,
        ClientIp = clientIp
    };
    await diagnosticLogger.LogClientErrorAsync(entry);
    return Results.NoContent();
});

app.MapPost("/api/rooms/{roomNumber:int}/seat", async Task<IResult> (
    int roomNumber,
    SeatRoomRequest request,
    HttpContext httpContext,
    RoomDeviceTokenValidator roomDeviceTokenValidator,
    DemoBoardStore store,
    DiagnosticLogger diagnosticLogger,
    Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
{
    var auditCtx = AuditRequestContext.From(httpContext);
    var previousRoom = store.GetRoom(roomNumber);
    var procedureCode = (request.ProcedureCode ?? request.ProcedureId)?.Trim();
    var doctorId = request.DoctorId?.Trim();

    var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
        roomNumber,
        httpContext.Request,
        roomDeviceTokenValidator);
    if (bindingFailure is not null)
    {
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "seat", roomNumber, previousRoom, null, false, "binding-rejected",
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return bindingFailure;
    }

    var validationError = RoomMutationRequestValidator.ValidateDoctorAndProcedure(doctorId, procedureCode);
    if (validationError is not null)
    {
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "seat", roomNumber, previousRoom, null, false, "validation-failed",
            doctorId, procedureCode));
        return Results.BadRequest(validationError);
    }

    var result = store.SeatRoom(roomNumber, doctorId!, procedureCode!, request.DemoElapsedMinutes, request.Sedation);
    if (result is null)
    {
        var reason = store.IsConfiguredRoom(roomNumber) ? "state-rejected" : "room-not-found";
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "seat", roomNumber, previousRoom, null, false, reason,
            doctorId, procedureCode));
        return store.IsConfiguredRoom(roomNumber)
            ? Results.BadRequest("Seat Room is only available when the room is available and the selected doctor and procedure are valid.")
            : Results.NotFound("Room is not configured.");
    }

    await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
        "seat", roomNumber, previousRoom, result, true, null,
        result.AssignedDoctor, result.ProcedureCode));
    await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    return Results.Ok(result);
});

app.MapPost("/api/rooms/{roomNumber:int}/assignment", async Task<IResult> (
    int roomNumber,
    UpdateAssignmentRequest request,
    HttpContext httpContext,
    RoomDeviceTokenValidator roomDeviceTokenValidator,
    DemoBoardStore store,
    DiagnosticLogger diagnosticLogger,
    Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
{
    var auditCtx = AuditRequestContext.From(httpContext);
    var previousRoom = store.GetRoom(roomNumber);
    var procedureCode = (request.ProcedureCode ?? request.ProcedureId)?.Trim();
    var doctorId = request.DoctorId?.Trim();

    var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
        roomNumber,
        httpContext.Request,
        roomDeviceTokenValidator);
    if (bindingFailure is not null)
    {
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "update-assignment", roomNumber, previousRoom, null, false, "binding-rejected",
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return bindingFailure;
    }

    var validationError = RoomMutationRequestValidator.ValidateDoctorAndProcedure(doctorId, procedureCode);
    if (validationError is not null)
    {
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "update-assignment", roomNumber, previousRoom, null, false, "validation-failed",
            doctorId, procedureCode));
        return Results.BadRequest(validationError);
    }

    var result = store.UpdateAssignment(roomNumber, doctorId!, procedureCode!, request.Sedation);
    if (result is null)
    {
        var reason = store.IsConfiguredRoom(roomNumber) ? "state-rejected" : "room-not-found";
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "update-assignment", roomNumber, previousRoom, null, false, reason,
            doctorId, procedureCode));
        return store.IsConfiguredRoom(roomNumber)
            ? Results.BadRequest("Update Assignment is only available for seated, aging, stale, or ready-for-doctor rooms with a valid doctor and procedure.")
            : Results.NotFound("Room is not configured.");
    }

    await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
        "update-assignment", roomNumber, previousRoom, result, true, null,
        result.AssignedDoctor, result.ProcedureCode));
    await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    return Results.Ok(result);
});

app.MapPost("/api/rooms/{roomNumber:int}/cancel-seating", async Task<IResult> (
    int roomNumber,
    HttpContext httpContext,
    RoomDeviceTokenValidator roomDeviceTokenValidator,
    DemoBoardStore store,
    DiagnosticLogger diagnosticLogger,
    Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
{
    var auditCtx = AuditRequestContext.From(httpContext);
    var previousRoom = store.GetRoom(roomNumber);

    var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
        roomNumber,
        httpContext.Request,
        roomDeviceTokenValidator);
    if (bindingFailure is not null)
    {
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "cancel-seating", roomNumber, previousRoom, null, false, "binding-rejected",
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return bindingFailure;
    }

    var result = store.CancelSeating(roomNumber);
    if (result is null)
    {
        var reason = store.IsConfiguredRoom(roomNumber) ? "state-rejected" : "room-not-found";
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "cancel-seating", roomNumber, previousRoom, null, false, reason,
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return store.IsConfiguredRoom(roomNumber)
            ? Results.BadRequest("Cancel Seating is only available for seated, aging, stale, or ready-for-doctor rooms.")
            : Results.NotFound("Room is not configured.");
    }

    await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
        "cancel-seating", roomNumber, previousRoom, result, true, null,
        previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
    await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    return Results.Ok(result);
});

app.MapPost("/api/rooms/{roomNumber:int}/ready-for-doctor", async Task<IResult> (
    int roomNumber,
    HttpContext httpContext,
    RoomDeviceTokenValidator roomDeviceTokenValidator,
    DemoBoardStore store,
    DiagnosticLogger diagnosticLogger,
    Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
{
    var auditCtx = AuditRequestContext.From(httpContext);
    var previousRoom = store.GetRoom(roomNumber);

    var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
        roomNumber,
        httpContext.Request,
        roomDeviceTokenValidator);
    if (bindingFailure is not null)
    {
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "ready-for-doctor", roomNumber, previousRoom, null, false, "binding-rejected",
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return bindingFailure;
    }

    var result = store.MarkReadyForDoctor(roomNumber);
    if (result is null)
    {
        var reason = store.IsConfiguredRoom(roomNumber) ? "state-rejected" : "room-not-found";
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "ready-for-doctor", roomNumber, previousRoom, null, false, reason,
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return store.IsConfiguredRoom(roomNumber)
            ? Results.BadRequest("Ready for Doctor is only available for seated, aging, or stale rooms.")
            : Results.NotFound("Room is not configured.");
    }

    await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
        "ready-for-doctor", roomNumber, previousRoom, result, true, null,
        result.AssignedDoctor, result.ProcedureCode));
    await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    return Results.Ok(result);
});

app.MapPost("/api/rooms/{roomNumber:int}/doctor-arrived", async Task<IResult> (
    int roomNumber,
    HttpContext httpContext,
    RoomDeviceTokenValidator roomDeviceTokenValidator,
    DemoBoardStore store,
    DiagnosticLogger diagnosticLogger,
    Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
{
    var auditCtx = AuditRequestContext.From(httpContext);
    var previousRoom = store.GetRoom(roomNumber);

    var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
        roomNumber,
        httpContext.Request,
        roomDeviceTokenValidator);
    if (bindingFailure is not null)
    {
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "doctor-arrived", roomNumber, previousRoom, null, false, "binding-rejected",
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return bindingFailure;
    }

    var outcome = store.TryMarkDoctorArrived(roomNumber);

    if (outcome.Outcome == DoctorArrivalOutcome.NotConfigured)
    {
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "doctor-arrived", roomNumber, previousRoom, null, false, "room-not-found",
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return Results.NotFound("Room is not configured.");
    }

    if (outcome.Outcome == DoctorArrivalOutcome.Rejected)
    {
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "doctor-arrived", roomNumber, previousRoom, null, false, "state-rejected",
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return Results.BadRequest("Doctor Arrived is only available when the room is marked ready for doctor.");
    }

    if (outcome.Outcome == DoctorArrivalOutcome.Conflict)
    {
        var conflict = outcome.Conflict!;
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "doctor-arrived", roomNumber, previousRoom, null, false, "doctor-conflict",
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return Results.Json(
            new DoctorArrivedConflictResponse(
                "Doctor is already marked in another room.",
                conflict.ConflictingRoomId,
                conflict.DoctorId,
                conflict.DoctorDisplayName),
            statusCode: StatusCodes.Status409Conflict);
    }

    var result = outcome.Status!;
    await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
        "doctor-arrived", roomNumber, previousRoom, result, true, null,
        result.AssignedDoctor, result.ProcedureCode));
    await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    return Results.Ok(result);
});

// Resolve a doctor-arrival conflict: complete the conflicting old room (it moves to TURNOVER, not
// Available) and then mark the current room Doctor Arrived. The store revalidates the conflict
// against current state before mutating - the client-supplied conflictingRoomId is not trusted.
app.MapPost("/api/rooms/{roomNumber:int}/doctor-arrived/resolve-conflict", async Task<IResult> (
    int roomNumber,
    ResolveDoctorArrivalConflictRequest request,
    HttpContext httpContext,
    RoomDeviceTokenValidator roomDeviceTokenValidator,
    DemoBoardStore store,
    DiagnosticLogger diagnosticLogger,
    Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
{
    var auditCtx = AuditRequestContext.From(httpContext);
    var previousRoom = store.GetRoom(roomNumber);

    var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
        roomNumber,
        httpContext.Request,
        roomDeviceTokenValidator);
    if (bindingFailure is not null)
    {
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "doctor-arrived-resolve", roomNumber, previousRoom, null, false, "binding-rejected",
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return bindingFailure;
    }

    if (request.ConflictingRoomId <= 0)
    {
        return Results.BadRequest("conflictingRoomId must be a positive room number.");
    }

    var outcome = store.ResolveDoctorArrivalConflict(roomNumber, request.ConflictingRoomId);

    if (outcome.Outcome == DoctorArrivalOutcome.NotConfigured)
    {
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "doctor-arrived-resolve", roomNumber, previousRoom, null, false, "room-not-found",
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return Results.NotFound("Room is not configured.");
    }

    if (outcome.Outcome == DoctorArrivalOutcome.Rejected)
    {
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "doctor-arrived-resolve", roomNumber, previousRoom, null, false, "state-rejected",
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return Results.BadRequest("Doctor Arrived is only available when the room is marked ready for doctor.");
    }

    if (outcome.Outcome == DoctorArrivalOutcome.StaleConflict)
    {
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "doctor-arrived-resolve", roomNumber, previousRoom, null, false, "conflict-stale",
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return Results.Json(
            new DoctorArrivedConflictResponse(
                "The conflict changed. Refresh and try again.",
                outcome.Conflict?.ConflictingRoomId ?? request.ConflictingRoomId,
                outcome.Conflict?.DoctorId,
                outcome.Conflict?.DoctorDisplayName),
            statusCode: StatusCodes.Status409Conflict);
    }

    var result = outcome.Status!;
    await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
        "doctor-arrived-resolve", roomNumber, previousRoom, result, true, null,
        result.AssignedDoctor, result.ProcedureCode));
    await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    return Results.Ok(result);
});

app.MapPost("/api/rooms/{roomNumber:int}/doctor-complete", async Task<IResult> (
    int roomNumber,
    HttpContext httpContext,
    RoomDeviceTokenValidator roomDeviceTokenValidator,
    DemoBoardStore store,
    DiagnosticLogger diagnosticLogger,
    Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
{
    var auditCtx = AuditRequestContext.From(httpContext);
    var previousRoom = store.GetRoom(roomNumber);

    var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
        roomNumber,
        httpContext.Request,
        roomDeviceTokenValidator);
    if (bindingFailure is not null)
    {
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "doctor-complete", roomNumber, previousRoom, null, false, "binding-rejected",
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return bindingFailure;
    }

    var result = store.MarkDoctorComplete(roomNumber);
    if (result is null)
    {
        var reason = store.IsConfiguredRoom(roomNumber) ? "state-rejected" : "room-not-found";
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "doctor-complete", roomNumber, previousRoom, null, false, reason,
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return store.IsConfiguredRoom(roomNumber)
            ? Results.BadRequest("Doctor Complete is only available when the doctor is in the room.")
            : Results.NotFound("Room is not configured.");
    }

    await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
        "doctor-complete", roomNumber, previousRoom, result, true, null,
        result.AssignedDoctor, result.ProcedureCode));
    await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    return Results.Ok(result);
});

app.MapPost("/api/rooms/{roomNumber:int}/available", async Task<IResult> (
    int roomNumber,
    HttpContext httpContext,
    RoomDeviceTokenValidator roomDeviceTokenValidator,
    DemoBoardStore store,
    DiagnosticLogger diagnosticLogger,
    Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
{
    var auditCtx = AuditRequestContext.From(httpContext);
    var previousRoom = store.GetRoom(roomNumber);

    var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
        roomNumber,
        httpContext.Request,
        roomDeviceTokenValidator);
    if (bindingFailure is not null)
    {
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "room-available", roomNumber, previousRoom, null, false, "binding-rejected",
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return bindingFailure;
    }

    var result = store.MarkRoomAvailable(roomNumber);
    if (result is null)
    {
        var reason = store.IsConfiguredRoom(roomNumber) ? "state-rejected" : "room-not-found";
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "room-available", roomNumber, previousRoom, null, false, reason,
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        return store.IsConfiguredRoom(roomNumber)
            ? Results.BadRequest("Room Available is only available during turnover.")
            : Results.NotFound("Room is not configured.");
    }

    await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
        "room-available", roomNumber, previousRoom, result, true, null,
        previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
    await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    return Results.Ok(result);
});

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static string? Truncate(string? value, int maxLength) =>
    value is null ? null : value.Length <= maxLength ? value : value[..maxLength];

app.Run();

// ---------------------------------------------------------------------------
// Request / response types
// ---------------------------------------------------------------------------

public sealed record SeatRoomRequest(
    string DoctorId,
    string? ProcedureCode = null,
    string? ProcedureId = null,
    int DemoElapsedMinutes = 0,
    bool Sedation = false);

public sealed record UpdateAssignmentRequest(
    string DoctorId,
    string? ProcedureCode = null,
    string? ProcedureId = null,
    bool Sedation = false);

/// <summary>
/// Body for POST /api/reports/cycles/mark-exception.
/// Preferred targeting is the stable CompletedCycleId. The legacy (RoomId, SeatedAt) compound
/// key remains supported for backward compatibility with older clients.
/// No PHI - identity, room ID, and timestamp only.
/// </summary>
public sealed record MarkExceptionRequest(
    long? CompletedCycleId = null,
    int? RoomId = null,
    DateTimeOffset? SeatedAt = null);

/// <summary>
/// 409 body returned by the doctor-arrived endpoints when the assigned doctor is already marked
/// in another room. No PHI - room number and doctor id/display name only.
/// </summary>
public sealed record DoctorArrivedConflictResponse(
    string Message,
    int ConflictingRoomId,
    string? DoctorId,
    string? DoctorDisplayName);

/// <summary>
/// Body for POST /api/rooms/{room}/doctor-arrived/resolve-conflict.
/// Identifies the conflicting room to complete before arriving the current room.
/// </summary>
public sealed record ResolveDoctorArrivalConflictRequest(int ConflictingRoomId);

/// <summary>
/// Body posted by the browser error capture in board.js.
/// Contains only technical diagnostics - no PHI.
/// </summary>
public sealed record ClientErrorRequest(
    string? Timestamp = null,
    string? Url = null,
    string? RoomId = null,
    string? View = null,
    string? Message = null,
    string? Source = null,
    int? Line = null,
    int? Column = null,
    string? Stack = null,
    string? UserAgent = null,
    string? ConnectionStatus = null,
    long? LastSnapshotAt = null,
    long? SnapshotAgeMs = null);

// ---------------------------------------------------------------------------
// Audit logging helpers
// ---------------------------------------------------------------------------

/// <summary>
/// Captures per-request metadata for audit log entries.
/// </summary>
internal sealed class AuditRequestContext
{
    private readonly string? _clientIp;
    private readonly string? _userAgent;
    private readonly string _requestPath;
    private readonly string? _referrer;

    private AuditRequestContext(HttpContext ctx)
    {
        _clientIp = ctx.Connection.RemoteIpAddress?.ToString();
        _userAgent = ctx.Request.Headers["User-Agent"].FirstOrDefault();
        _requestPath = ctx.Request.Path.ToString();
        _referrer = ctx.Request.Headers["Referer"].FirstOrDefault();
    }

    public static AuditRequestContext From(HttpContext ctx) => new(ctx);

    public RoomAuditEntry Build(
        string action,
        int roomNumber,
        RoomStatus? previousRoom,
        RoomStatus? result,
        bool success,
        string? reason,
        string? doctorId,
        string? procedureCode) => new()
    {
        Timestamp = DateTimeOffset.UtcNow.ToString("O"),
        Action = action,
        RoomNumber = roomNumber,
        PreviousState = previousRoom?.State,
        NewState = result?.State,
        DoctorId = doctorId,
        ProcedureCode = procedureCode,
        Success = success,
        Reason = reason,
        ClientIp = _clientIp,
        UserAgent = _userAgent,
        RequestPath = _requestPath,
        Referrer = _referrer
    };
}
