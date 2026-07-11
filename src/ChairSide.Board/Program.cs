using System.Text.Json;

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

// Operator-run maintenance CLI (console-only; never serves HTTP). Resolve enforces an explicit
// per-command confirmation token; a refusal mutates nothing. This is the only reset mechanism and
// it is deliberately not a web endpoint or UI button.
var maintenance = MaintenanceCommands.Resolve(args);
if (maintenance.Outcome != MaintenanceOutcome.NotRequested)
{
    return RunMaintenance(app, maintenance);
}

_ = app.Services.GetRequiredService<DemoBoardStore>();

var roomDeviceBindingOptions = app.Services.GetRequiredService<IOptions<RoomDeviceBindingOptions>>().Value;
var adminAccessOptions = app.Services.GetRequiredService<IOptions<AdminAccessOptions>>().Value;
var roomExpirationOptions = app.Services.GetRequiredService<IOptions<RoomExpirationOptions>>().Value;
app.Logger.LogInformation("Room device binding enabled: {Enabled}", roomDeviceBindingOptions.Enabled);
app.Logger.LogInformation("Admin/report access protection enabled: {Enabled}", adminAccessOptions.Enabled);
app.Logger.LogInformation(
    "Room expiration enabled: {Enabled}; max active duration hours: {MaxActiveDurationHours}; after-hours sweep enabled: {AfterHoursSweepEnabled}; after-hours sweep time: {AfterHoursSweepTime}; timezone: {TimeZone}",
    roomExpirationOptions.Enabled,
    roomExpirationOptions.MaxActiveDurationHours,
    roomExpirationOptions.AfterHoursSweepEnabled,
    roomExpirationOptions.AfterHoursSweepTime,
    roomExpirationOptions.TimeZone);
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

// Optional ISO yyyy-MM-dd `from`/`to` query params bound the completed-cycle population by
// completion date (DoctorCompleteAt) before any report calculation. Missing/invalid dates degrade
// to an all-time window; a reversed pair is normalized.
app.MapGet("/api/reports", (DemoBoardStore store, string? from, string? to) =>
    store.GetReports(ReportDateRange.FromDateStrings(from, to)));

// Development-only: populate deterministic, non-PHI synthetic completed cycles for local/beta
// reporting smoke tests. Mapped only in Development so it can never be reached in Production.
if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/dev/seed-report-data", async Task<IResult> (
        DemoBoardStore store,
        DiagnosticLogger diagnosticLogger,
        Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
    {
        var summary = store.SeedSyntheticReportData();
        await diagnosticLogger.LogRoomAuditAsync(new RoomAuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            Action = "dev-seed-report-data",
            RoomNumber = 0,
            Success = true,
            Reason = $"seeded {summary.CyclesInserted} synthetic cycles"
        });

        // Reports read from a fresh GetReports call; nudge any live boards to refresh too.
        await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
        return Results.Ok(summary);
    });
}

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

app.MapPost("/api/rooms/{roomNumber:int}/prestage", RoomLifecycleEndpointHandler.BeginPrestageAsync);
app.MapPost("/api/rooms/{roomNumber:int}/seat", RoomLifecycleEndpointHandler.SeatAsync);

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

app.MapPost("/api/rooms/{roomNumber:int}/cancel-prestage", RoomLifecycleEndpointHandler.CancelPrestageAsync);
app.MapPost("/api/rooms/{roomNumber:int}/cancel-seating", RoomLifecycleEndpointHandler.CancelSeatingAsync);

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
app.MapPost(
    "/api/rooms/{roomNumber:int}/doctor-arrived/resolve-conflict",
    DoctorArrivalConflictEndpointHandler.ResolveAsync);

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

// Executes a resolved maintenance command against app services and returns a process exit code.
// Console-only: no HTTP is served. A refusal performs no mutation.
static int RunMaintenance(WebApplication maintenanceApp, MaintenanceResolution resolution)
{
    if (resolution.Outcome == MaintenanceOutcome.Refused)
    {
        Console.Error.WriteLine($"[ChairSide Maintenance] Refused: {resolution.RefusalReason}");
        Console.Error.WriteLine("[ChairSide Maintenance] No data was changed.");
        return 2;
    }

    var store = maintenanceApp.Services.GetRequiredService<DemoBoardStore>();
    var repository = maintenanceApp.Services.GetRequiredService<SqliteBoardRepository>();

    Console.WriteLine("[ChairSide Maintenance] Starting.");
    Console.WriteLine($"[ChairSide Maintenance] Environment: {maintenanceApp.Environment.EnvironmentName}");
    Console.WriteLine($"[ChairSide Maintenance] Database:    {repository.DatabasePath}");
    Console.WriteLine($"[ChairSide Maintenance] Command:     {resolution.Command}");

    // Hard refusal: the large synthetic dataset and the stress-fixture command must never run
    // against a Production database, even with a correct confirmation token. Scoped to those two
    // commands; reset-training-data and reset-empty are unchanged.
    if (MaintenanceCommands.IsProductionForbidden(resolution.Command) && maintenanceApp.Environment.IsProduction())
    {
        Console.Error.WriteLine($"[ChairSide Maintenance] Refused: '{resolution.Command}' cannot run in Production.");
        Console.Error.WriteLine("[ChairSide Maintenance] No data was changed.");
        return 2;
    }

    if (string.Equals(resolution.Command, MaintenanceCommands.StressFixtureCommand, StringComparison.Ordinal))
    {
        var profile = resolution.Profile
            ?? throw new InvalidOperationException("reset-stress-fixture resolved without a profile.");
        Console.WriteLine($"[ChairSide Maintenance] Profile:      {profile}");

        var fixtureResult = store.ResetAndSeedStressFixture(profile, resolution.CompletedCycles);
        PrintStressFixtureSummary(fixtureResult);
        Console.WriteLine("[ChairSide Maintenance] Done. The web host was not started.");
        return 0;
    }

    MaintenanceResetResult result;
    if (string.Equals(resolution.Command, MaintenanceCommands.TrainingSeedCommand, StringComparison.Ordinal))
    {
        result = store.ResetAndSeedSyntheticTrainingData();
    }
    else if (string.Equals(resolution.Command, MaintenanceCommands.LargeSyntheticSeedCommand, StringComparison.Ordinal))
    {
        var completedCycleTarget = resolution.CompletedCycles ?? MaintenanceCommands.DefaultCompletedCycles;
        Console.WriteLine($"[ChairSide Maintenance] Completed-cycle target:    {completedCycleTarget}");
        result = store.ResetAndSeedLargeSyntheticReportData(completedCycleTarget);
    }
    else
    {
        result = store.ResetAllDataForEmptyBeta();
    }

    Console.WriteLine($"[ChairSide Maintenance] Completed cycles cleared:  {result.CompletedCyclesCleared}");
    Console.WriteLine($"[ChairSide Maintenance] Active rooms reset:        {result.ActiveRoomsReset}");
    Console.WriteLine($"[ChairSide Maintenance] Synthetic cycles seeded:   {result.CyclesSeeded}");
    Console.WriteLine($"[ChairSide Maintenance] Doctors represented:       {result.DoctorsRepresented}");
    Console.WriteLine($"[ChairSide Maintenance] Procedure families:        {result.ProcedureFamiliesRepresented}");
    Console.WriteLine($"[ChairSide Maintenance] Expected-allocation cases: {result.ExpectedAllocationCases}");
    Console.WriteLine($"[ChairSide Maintenance] Reporting exceptions:      {result.ExceptionsExpected}");
    Console.WriteLine("[ChairSide Maintenance] Done. The web host was not started.");
    return 0;
}

// Prints the extended reset-stress-fixture summary. Every dimension always prints a line - scalar
// counts print 0 when a profile does not seed that dimension (0 is self-explanatory for a count);
// the nullable history-date fields and the count dictionaries print "not seeded by this profile"
// when null/empty instead of a blank or omitted line, so a profile that intentionally does not
// touch a dimension (e.g. live-board-stress never writes completed-cycle history) is never
// confused with a bug that silently produced nothing.
static void PrintStressFixtureSummary(StressFixtureResult result)
{
    Console.WriteLine($"[ChairSide Maintenance] Completed cycles cleared:    {result.CompletedCyclesCleared}");
    Console.WriteLine($"[ChairSide Maintenance] Active rooms reset:          {result.ActiveRoomsReset}");
    Console.WriteLine($"[ChairSide Maintenance] Completed cycles seeded:     {result.CyclesSeeded}");
    Console.WriteLine($"[ChairSide Maintenance] Doctors represented:         {result.DoctorsRepresented}");
    Console.WriteLine($"[ChairSide Maintenance] Procedure families:         {result.ProcedureFamiliesRepresented}");
    Console.WriteLine($"[ChairSide Maintenance] Room state counts:          {FormatCounts(result.RoomStateCounts)}");
    Console.WriteLine($"[ChairSide Maintenance] Active room doctor counts:  {FormatCounts(result.ActiveRoomDoctorCounts)}");
    Console.WriteLine($"[ChairSide Maintenance] Derived exception reasons:  {FormatCounts(result.DerivedExceptionReasonCounts)}");
    Console.WriteLine($"[ChairSide Maintenance] Manual audit candidates:    {result.ManualAuditCandidatesSeeded}");
    Console.WriteLine($"[ChairSide Maintenance] In-progress cycle rows:     {result.InProgressCycleRowsSeeded}");
    Console.WriteLine($"[ChairSide Maintenance] History earliest seated at: {FormatTimestamp(result.HistoryEarliestSeatedAt)}");
    Console.WriteLine($"[ChairSide Maintenance] History latest seated at:   {FormatTimestamp(result.HistoryLatestSeatedAt)}");
}

// Formats a count dictionary as "key=value, key=value, ..." sorted by key for deterministic,
// scriptable output. Empty means the profile does not seed that dimension at all.
static string FormatCounts(IReadOnlyDictionary<string, int> counts) =>
    counts.Count == 0
        ? "not seeded by this profile"
        : string.Join(", ", counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));

// "s" is the culture-independent sortable date/time format specifier - no CultureInfo import needed.
static string FormatTimestamp(DateTimeOffset? timestamp) =>
    timestamp is { } value ? value.UtcDateTime.ToString("s") + "Z" : "not seeded by this profile";

app.Run();
return 0;

// ---------------------------------------------------------------------------
// Request / response types
// ---------------------------------------------------------------------------

public sealed record BeginPrestageRequest(
    string? DoctorId = null,
    string? ProcedureCode = null,
    string? ProcedureId = null,
    bool Sedation = false,
    // Optional final confirmed expected allocation in 10-minute units. When omitted, the
    // selected procedure's default expected units are used. Operational metadata only - never PHI.
    int? ExpectedAllocationUnits = null);

// Canonical Seat transport contract. This is the ONLY publicly declared shape for POST .../seat: it
// carries no doctor, procedure, sedation, or allocation fields. The temporary assignment-bearing
// compatibility shape (for the currently checked-in room-panel UI) is parsed manually from the raw
// request body - see RoomLifecycleEndpointHandler.SeatAsync and SeatRequestParser - specifically so
// it is never exposed as part of this public contract.
public sealed record SeatRoomRequest(int DemoElapsedMinutes = 0);

public sealed record CancelRoomAssignmentRequest(string? CancellationReason = null);

public sealed record UpdateAssignmentRequest(
    string DoctorId,
    string? ProcedureCode = null,
    string? ProcedureId = null,
    bool Sedation = false);

// Resolves the procedureCode / procedureId alias pair supplied to Begin Prestage and to the
// assignment-bearing compatibility Seat request. Both fields name the same procedure historically;
// neither is preferred over the other. null means the alias was omitted entirely, which is always
// acceptable when the other alias is valid. A SUPPLIED blank or whitespace-only alias, by contrast,
// is invalid input and is rejected outright - it is not silently treated as omitted, because a caller
// that explicitly sent an empty string most likely made a mistake rather than intending "no alias."
// When both aliases are supplied and valid they must agree, case-insensitively after trimming, or the
// request is rejected with a conflict error - in every rejection case, before any store mutation.
public static class ProcedureAliasResolver
{
    public static ProcedureAliasResolution Resolve(string? procedureCode, string? procedureId)
    {
        if (IsBlankButSupplied(procedureCode))
        {
            return new ProcedureAliasResolution(null, "procedureCode must not be blank.");
        }

        if (IsBlankButSupplied(procedureId))
        {
            return new ProcedureAliasResolution(null, "procedureId must not be blank.");
        }

        var code = procedureCode?.Trim();
        var id = procedureId?.Trim();

        if (code is null)
        {
            return new ProcedureAliasResolution(id, null);
        }

        if (id is null)
        {
            return new ProcedureAliasResolution(code, null);
        }

        return string.Equals(code, id, StringComparison.OrdinalIgnoreCase)
            ? new ProcedureAliasResolution(code, null)
            : new ProcedureAliasResolution(null, $"procedureCode ('{code}') and procedureId ('{id}') do not match.");
    }

    // True only when the value is non-null but reduces to nothing after trimming (e.g. "" or "   ").
    // A null value is omitted, not blank, and is never rejected here.
    private static bool IsBlankButSupplied(string? value) =>
        value is not null && value.Trim().Length == 0;
}

// ProcedureCode is the resolved alias to use (may itself be null when neither alias was supplied -
// callers fall back to the existing missing-procedure validation for that case). ConflictError is
// set only when both aliases were supplied and disagreed; callers must return 400 before mutating.
public readonly record struct ProcedureAliasResolution(string? ProcedureCode, string? ConflictError);

// Strict JSON request-body reading shared by Seat and the cancellation routes. No new HTTP test
// harness or NuGet package is introduced: this reads HttpRequest.Body directly so the real parsing
// path is exercised by DefaultHttpContext-based tests, not by constructing a bound DTO.
internal static class StrictJsonRequestReader
{
    // No body, an empty body, and a whitespace-only body are all treated as an empty JSON object,
    // regardless of Content-Type - this preserves the existing reasonless caller (Cancel Seating
    // today sends no body and no Content-Type header at all). A non-empty body must declare a JSON
    // content type and parse to a JSON object; anything else is the project's normal 400 behavior.
    public static async Task<(JsonElement Root, IResult? Error)> ReadObjectAsync(HttpRequest request)
    {
        string raw;
        using (var reader = new StreamReader(request.Body, leaveOpen: true))
        {
            raw = await reader.ReadToEndAsync();
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            using var empty = JsonDocument.Parse("{}");
            return (empty.RootElement.Clone(), null);
        }

        if (!request.HasJsonContentType())
        {
            return (default, Results.BadRequest("Unsupported content type. Expected application/json."));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return (default, Results.BadRequest("Malformed JSON request body."));
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (default, Results.BadRequest("Request body must be a JSON object."));
            }

            return (document.RootElement.Clone(), null);
        }
    }

    // Rejects any property name outside the allow-list (case-insensitive) and rejects case-variant
    // duplicate property names within the same object (JsonDocument does not deduplicate these).
    public static IResult? ValidatePropertySet(JsonElement root, IReadOnlyCollection<string> allowedProperties)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return Results.BadRequest($"Duplicate property '{property.Name}' in request body.");
            }

            if (!allowedProperties.Contains(property.Name))
            {
                return Results.BadRequest($"Unknown property '{property.Name}' in request body.");
            }
        }

        return null;
    }
}

// Parsed Seat request. Internal: this is never bound automatically by ASP.NET model binding (that is
// exactly Defect 2/3 this replaces) - RoomLifecycleEndpointHandler.SeatAsync builds it explicitly
// from the raw JSON body after property-set validation.
//
// HasCompatibilityAssignmentPayload is set by SeatRequestParser from JSON PROPERTY PRESENCE, not from
// the resolved field values below - a caller sending {"sedation": false} or {"doctorId": null} has
// still supplied an assignment-bearing property and must be routed into compatibility validation
// (and rejected there as incomplete), not silently treated as a canonical bare Seat request.
internal sealed record ParsedSeatRequest(
    int DemoElapsedMinutes,
    string? DoctorId,
    string? ProcedureCode,
    string? ProcedureId,
    bool Sedation,
    int? ExpectedAllocationUnits,
    bool HasCompatibilityAssignmentPayload);

internal static class SeatRequestParser
{
    // Canonical (demoElapsedMinutes) plus the temporary compatibility fields, finalized from the
    // exact property set the currently checked-in room-panel JS sends (board.js sendSeatRoom).
    public static readonly IReadOnlyCollection<string> AllowedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "demoElapsedMinutes", "doctorId", "procedureCode", "procedureId", "sedation", "expectedAllocationUnits"
    };

    public static bool TryParse(JsonElement root, out ParsedSeatRequest parsed, out IResult? error)
    {
        var demoElapsedMinutes = 0;
        string? doctorId = null;
        string? procedureCode = null;
        string? procedureId = null;
        var sedation = false;
        int? expectedAllocationUnits = null;

        // Set on PROPERTY NAME PRESENCE alone, before the value is even inspected - a request that
        // supplies one of these five properties is assignment-bearing regardless of whether the
        // value turns out to be null or false. Never derived from the parsed values below.
        var hasCompatibilityAssignmentPayload = false;

        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals("demoElapsedMinutes"))
            {
                if (!TryGetInt(property.Value, out demoElapsedMinutes))
                {
                    parsed = null!;
                    error = Results.BadRequest("demoElapsedMinutes must be an integer.");
                    return false;
                }
            }
            else if (property.NameEquals("doctorId"))
            {
                hasCompatibilityAssignmentPayload = true;
                if (!TryGetNullableString(property.Value, out doctorId))
                {
                    parsed = null!;
                    error = Results.BadRequest("doctorId must be a string or null.");
                    return false;
                }
            }
            else if (property.NameEquals("procedureCode"))
            {
                hasCompatibilityAssignmentPayload = true;
                if (!TryGetNullableString(property.Value, out procedureCode))
                {
                    parsed = null!;
                    error = Results.BadRequest("procedureCode must be a string or null.");
                    return false;
                }
            }
            else if (property.NameEquals("procedureId"))
            {
                hasCompatibilityAssignmentPayload = true;
                if (!TryGetNullableString(property.Value, out procedureId))
                {
                    parsed = null!;
                    error = Results.BadRequest("procedureId must be a string or null.");
                    return false;
                }
            }
            else if (property.NameEquals("sedation"))
            {
                hasCompatibilityAssignmentPayload = true;
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    parsed = null!;
                    error = Results.BadRequest("sedation must be a boolean.");
                    return false;
                }

                sedation = property.Value.GetBoolean();
            }
            else if (property.NameEquals("expectedAllocationUnits"))
            {
                hasCompatibilityAssignmentPayload = true;
                if (!TryGetNullableInt(property.Value, out expectedAllocationUnits))
                {
                    parsed = null!;
                    error = Results.BadRequest("expectedAllocationUnits must be an integer or null.");
                    return false;
                }
            }

            // Any other property name is already rejected by ValidatePropertySet before this runs.
        }

        parsed = new ParsedSeatRequest(
            demoElapsedMinutes, doctorId, procedureCode, procedureId, sedation, expectedAllocationUnits,
            hasCompatibilityAssignmentPayload);
        error = null;
        return true;
    }

    private static bool TryGetInt(JsonElement value, out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }

    private static bool TryGetNullableInt(JsonElement value, out int? result)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            result = null;
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
        {
            result = parsed;
            return true;
        }

        result = null;
        return false;
    }

    private static bool TryGetNullableString(JsonElement value, out string? result)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            result = null;
            return true;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            result = value.GetString();
            return true;
        }

        result = null;
        return false;
    }
}

// Parses the shared optional-cancellation-reason body for both Cancel Prestage and Cancel Seating.
internal static class CancelRequestParser
{
    public static readonly IReadOnlyCollection<string> AllowedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cancellationReason"
    };

    public static bool TryParse(JsonElement root, out string? cancellationReason, out IResult? error)
    {
        cancellationReason = null;
        error = null;

        foreach (var property in root.EnumerateObject())
        {
            if (!property.NameEquals("cancellationReason"))
            {
                // Any other property name is already rejected by ValidatePropertySet before this runs.
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                cancellationReason = null;
            }
            else if (property.Value.ValueKind == JsonValueKind.String)
            {
                cancellationReason = property.Value.GetString();
            }
            else
            {
                error = Results.BadRequest("cancellationReason must be a string or null.");
                return false;
            }
        }

        return true;
    }
}

public static class RoomLifecycleEndpointHandler
{
    public static async Task<IResult> BeginPrestageAsync(
        int roomNumber,
        BeginPrestageRequest request,
        HttpContext httpContext,
        RoomDeviceTokenValidator roomDeviceTokenValidator,
        DemoBoardStore store,
        DiagnosticLogger diagnosticLogger,
        IHubContext<BoardHub> hubContext)
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
                "begin-prestage", roomNumber, previousRoom, null, false, "binding-rejected",
                previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
            return bindingFailure;
        }

        var aliasResolution = ProcedureAliasResolver.Resolve(request.ProcedureCode, request.ProcedureId);
        if (aliasResolution.ConflictError is not null)
        {
            await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
                "begin-prestage", roomNumber, previousRoom, null, false, "validation-failed",
                previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
            return Results.BadRequest(aliasResolution.ConflictError);
        }

        var procedureCode = aliasResolution.ProcedureCode;
        var doctorId = request.DoctorId?.Trim();

        var validationError = RoomMutationRequestValidator.ValidateDoctorAndProcedure(doctorId, procedureCode);
        if (validationError is not null)
        {
            await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
                "begin-prestage", roomNumber, previousRoom, null, false, "validation-failed",
                doctorId, procedureCode));
            return Results.BadRequest(validationError);
        }

        var result = store.BeginPrestage(roomNumber, doctorId!, procedureCode!, request.Sedation, request.ExpectedAllocationUnits);
        if (result is null)
        {
            var reason = store.IsConfiguredRoom(roomNumber) ? "state-rejected" : "room-not-found";
            await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
                "begin-prestage", roomNumber, previousRoom, null, false, reason,
                doctorId, procedureCode));
            return store.IsConfiguredRoom(roomNumber)
                ? Results.BadRequest("Begin Prestage is only available when the room is available and the selected doctor and procedure are valid.")
                : Results.NotFound("Room is not configured.");
        }

        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "begin-prestage", roomNumber, previousRoom, result, true, null,
            result.AssignedDoctor, result.ProcedureCode));
        await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
        return Results.Ok(result);
    }

    public static async Task<IResult> SeatAsync(
        int roomNumber,
        HttpContext httpContext,
        RoomDeviceTokenValidator roomDeviceTokenValidator,
        DemoBoardStore store,
        IWebHostEnvironment environment,
        DiagnosticLogger diagnosticLogger,
        IHubContext<BoardHub> hubContext)
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
                "seat", roomNumber, previousRoom, null, false, "binding-rejected",
                previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
            return bindingFailure;
        }

        var (root, bodyError) = await StrictJsonRequestReader.ReadObjectAsync(httpContext.Request);
        if (bodyError is not null)
        {
            await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
                "seat", roomNumber, previousRoom, null, false, "validation-failed",
                previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
            return bodyError;
        }

        var propertyError = StrictJsonRequestReader.ValidatePropertySet(root, SeatRequestParser.AllowedProperties);
        if (propertyError is not null)
        {
            await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
                "seat", roomNumber, previousRoom, null, false, "validation-failed",
                previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
            return propertyError;
        }

        if (!SeatRequestParser.TryParse(root, out var request, out var parseError))
        {
            await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
                "seat", roomNumber, previousRoom, null, false, "validation-failed",
                previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
            return parseError!;
        }

        RoomStatus? result;
        string? doctorId = null;
        string? procedureCode = null;
        if (request.HasCompatibilityAssignmentPayload)
        {
            // TEMPORARY COMPATIBILITY: the currently checked-in room-panel UI still posts this old
            // assignment-bearing seat body. This two-step Begin Prestage -> Seat Room bridge is not
            // given a new atomic store method; a small residual concurrency window between the two
            // store calls is accepted as temporary. Remove this branch once the UI moves to Begin
            // Prestage + bare Seat Room directly.
            //
            // Validate the simulation and the procedure alias before BeginPrestage so a rejected
            // compatibility request cannot leave an unintended prestaged assignment behind. SeatRoom
            // remains authoritative too.
            var demoValidationError = ValidateCompatibilityDemoElapsedMinutes(request.DemoElapsedMinutes, environment);
            if (demoValidationError is not null)
            {
                await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
                    "seat", roomNumber, previousRoom, null, false, "validation-failed",
                    previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
                return Results.BadRequest(demoValidationError);
            }

            var aliasResolution = ProcedureAliasResolver.Resolve(request.ProcedureCode, request.ProcedureId);
            if (aliasResolution.ConflictError is not null)
            {
                await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
                    "seat", roomNumber, previousRoom, null, false, "validation-failed",
                    previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
                return Results.BadRequest(aliasResolution.ConflictError);
            }

            procedureCode = aliasResolution.ProcedureCode;
            doctorId = request.DoctorId?.Trim();
            var validationError = RoomMutationRequestValidator.ValidateDoctorAndProcedure(doctorId, procedureCode);
            if (validationError is not null)
            {
                await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
                    "seat", roomNumber, previousRoom, null, false, "validation-failed",
                    doctorId, procedureCode));
                return Results.BadRequest(validationError);
            }

            if (store.BeginPrestage(roomNumber, doctorId!, procedureCode!, request.Sedation, request.ExpectedAllocationUnits) is null)
            {
                return await SeatRejectedAsync(
                    roomNumber,
                    previousRoom,
                    doctorId,
                    procedureCode,
                    auditCtx,
                    store,
                    diagnosticLogger,
                    "Seat Room compatibility requests are only available when the room is available and the selected doctor and procedure are valid.");
            }

            result = store.SeatRoom(roomNumber, request.DemoElapsedMinutes);
        }
        else
        {
            result = store.SeatRoom(roomNumber, request.DemoElapsedMinutes);
        }

        if (result is null)
        {
            return await SeatRejectedAsync(
                roomNumber,
                previousRoom,
                doctorId,
                procedureCode,
                auditCtx,
                store,
                diagnosticLogger,
                "Seat Room is only available from Prestaging.");
        }

        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "seat", roomNumber, previousRoom, result, true, null,
            result.AssignedDoctor, result.ProcedureCode));
        await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
        return Results.Ok(result);
    }

    public static Task<IResult> CancelPrestageAsync(
        int roomNumber,
        HttpContext httpContext,
        RoomDeviceTokenValidator roomDeviceTokenValidator,
        DemoBoardStore store,
        DiagnosticLogger diagnosticLogger,
        IHubContext<BoardHub> hubContext) =>
        CancelIncompleteAssignmentAsync(
            "cancel-prestage",
            "Cancel Prestage is only available for prestaging rooms with a valid cancellation reason.",
            roomNumber,
            httpContext,
            roomDeviceTokenValidator,
            store,
            diagnosticLogger,
            hubContext,
            store.CancelPrestage);

    public static Task<IResult> CancelSeatingAsync(
        int roomNumber,
        HttpContext httpContext,
        RoomDeviceTokenValidator roomDeviceTokenValidator,
        DemoBoardStore store,
        DiagnosticLogger diagnosticLogger,
        IHubContext<BoardHub> hubContext) =>
        CancelIncompleteAssignmentAsync(
            "cancel-seating",
            "Cancel Seating is only available for seated, aging, stale, or ready-for-doctor rooms with a valid cancellation reason.",
            roomNumber,
            httpContext,
            roomDeviceTokenValidator,
            store,
            diagnosticLogger,
            hubContext,
            store.CancelSeating);

    private static async Task<IResult> CancelIncompleteAssignmentAsync(
        string action,
        string rejectedMessage,
        int roomNumber,
        HttpContext httpContext,
        RoomDeviceTokenValidator roomDeviceTokenValidator,
        DemoBoardStore store,
        DiagnosticLogger diagnosticLogger,
        IHubContext<BoardHub> hubContext,
        Func<int, string?, RoomStatus?> cancel)
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
                action, roomNumber, previousRoom, null, false, "binding-rejected",
                previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
            return bindingFailure;
        }

        var (root, bodyError) = await StrictJsonRequestReader.ReadObjectAsync(httpContext.Request);
        if (bodyError is not null)
        {
            await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
                action, roomNumber, previousRoom, null, false, "validation-failed",
                previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
            return bodyError;
        }

        var propertyError = StrictJsonRequestReader.ValidatePropertySet(root, CancelRequestParser.AllowedProperties);
        if (propertyError is not null)
        {
            await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
                action, roomNumber, previousRoom, null, false, "validation-failed",
                previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
            return propertyError;
        }

        if (!CancelRequestParser.TryParse(root, out var cancellationReason, out var parseError))
        {
            await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
                action, roomNumber, previousRoom, null, false, "validation-failed",
                previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
            return parseError!;
        }

        var result = cancel(roomNumber, cancellationReason);
        if (result is null)
        {
            var reason = store.IsConfiguredRoom(roomNumber) ? "state-rejected" : "room-not-found";
            await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
                action, roomNumber, previousRoom, null, false, reason,
                previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
            return store.IsConfiguredRoom(roomNumber)
                ? Results.BadRequest(rejectedMessage)
                : Results.NotFound("Room is not configured.");
        }

        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            action, roomNumber, previousRoom, result, true, null,
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
        return Results.Ok(result);
    }

    private static async Task<IResult> SeatRejectedAsync(
        int roomNumber,
        RoomStatus? previousRoom,
        string? doctorId,
        string? procedureCode,
        AuditRequestContext auditCtx,
        DemoBoardStore store,
        DiagnosticLogger diagnosticLogger,
        string rejectedMessage)
    {
        var reason = store.IsConfiguredRoom(roomNumber) ? "state-rejected" : "room-not-found";
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "seat", roomNumber, previousRoom, null, false, reason, doctorId, procedureCode));
        return store.IsConfiguredRoom(roomNumber)
            ? Results.BadRequest(rejectedMessage)
            : Results.NotFound("Room is not configured.");
    }

    private static string? ValidateCompatibilityDemoElapsedMinutes(int demoElapsedMinutes, IWebHostEnvironment environment)
    {
        if (demoElapsedMinutes is < 0 or > 240)
        {
            return "demoElapsedMinutes must be between 0 and 240.";
        }

        return demoElapsedMinutes > 0 && !environment.IsDevelopment()
            ? "demoElapsedMinutes is only available in Development."
            : null;
    }
}

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

public static class DoctorArrivalConflictEndpointHandler
{
    public static async Task<IResult> ResolveAsync(
        int roomNumber,
        ResolveDoctorArrivalConflictRequest request,
        HttpContext httpContext,
        RoomDeviceTokenValidator roomDeviceTokenValidator,
        DemoBoardStore store,
        DiagnosticLogger diagnosticLogger,
        IHubContext<BoardHub> hubContext)
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

        var previousConflictingRoom = store.GetRoom(request.ConflictingRoomId);
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
        var autoCompletedRoom = store.GetRoom(request.ConflictingRoomId);
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "doctor-arrived-resolve-autocomplete", request.ConflictingRoomId, previousConflictingRoom, autoCompletedRoom, true,
            $"auto-completed-by-resolving-room-{roomNumber}",
            autoCompletedRoom?.AssignedDoctor ?? previousConflictingRoom?.AssignedDoctor,
            autoCompletedRoom?.ProcedureCode ?? previousConflictingRoom?.ProcedureCode));
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "doctor-arrived-resolve", roomNumber, previousRoom, result, true, null,
            result.AssignedDoctor, result.ProcedureCode));
        await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
        return Results.Ok(result);
    }
}

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
