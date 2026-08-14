using System.Text.Json;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

using ChairSide.Board.Hubs;
using ChairSide.Board.Options;
using ChairSide.Board.Services;

var builder = WebApplication.CreateBuilder(args);
DeploymentEnvironment deploymentEnvironment;
try
{
    deploymentEnvironment = DeploymentEnvironmentPolicy.Resolve(builder.Environment.EnvironmentName);
}
catch (DeploymentEnvironmentException exception)
{
    Console.Error.WriteLine($"[ChairSide Startup] Refused: {exception.Message}");
    return 2;
}

var maintenance = MaintenanceCommands.Resolve(args);

builder.Services.AddSingleton(deploymentEnvironment);

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
builder.Services.AddSingleton(DatabaseIsolationLayout.Approved);
builder.Services.AddSingleton<IReparsePointInspector, FileSystemReparsePointInspector>();
builder.Services.AddSingleton<DatabaseIsolationPolicy>();
builder.Services.AddSingleton<DatabaseDeploymentIdentityPolicy>();
builder.Services.AddSingleton<SqliteBoardRepository>();
builder.Services.AddSingleton<DemoBoardStore>();
builder.Services.AddSingleton<RoomDeviceTokenValidator>();
builder.Services.AddSingleton<AdminAccessTokenValidator>();
builder.Services.AddSingleton<DiagnosticLogger>();
builder.Services.AddSingleton<ClientErrorRateLimiter>();
builder.Services.AddHostedService<RoomExpirationService>();

// Operator-run maintenance CLI (console-only; never serves HTTP). Resolve enforces an explicit
// per-command confirmation token. Environment authorization wraps application build/service
// resolution, so a refusal cannot construct logs, SQLite, schema, or rooms. This is the only reset
// mechanism and it is deliberately not a web endpoint or UI button.
if (maintenance.Outcome != MaintenanceOutcome.NotRequested)
{
    return MaintenanceExecutionPolicy.Execute(
        deploymentEnvironment,
        maintenance,
        () => RunWithDatabaseIsolationRefusal(
            () => RunMaintenance(builder.Build(), deploymentEnvironment, maintenance)));
}

WebApplication app;
try
{
    app = builder.Build();
    _ = app.Services.GetRequiredService<DemoBoardStore>();
}
catch (DatabaseIsolationException exception)
{
    return RefuseDatabaseIsolation(exception);
}

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
if (deploymentEnvironment.IsDeployed)
{
    if (!roomDeviceBindingOptions.Enabled)
    {
        LogDeploymentWarning(app.Logger, "Room device binding", deploymentEnvironment);
    }

    if (!adminAccessOptions.Enabled)
    {
        LogDeploymentWarning(app.Logger, "Admin/report access protection", deploymentEnvironment);
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

app.MapGet("/room-1.html", LegacyRoomOneRedirect.CreateResult);

app.MapHub<BoardHub>("/boardHub");

app.MapGet("/api/board", (DemoBoardStore store) => store.GetSnapshot());

// Optional ISO yyyy-MM-dd `from`/`to` parameters select the whole-day completion window. Scope and
// sedation narrow analytical populations; procedureGrouping changes aggregation only. Invalid dates
// retain graceful all-time behavior and reversed valid bounds normalize in the returned query metadata.
app.MapGet("/api/reports", (
    DemoBoardStore store,
    string? from,
    string? to,
    string? scope,
    string? doctorId,
    string? sedation,
    string? procedureGrouping) =>
    store.GetReports(ReportQuery.FromStrings(
        from,
        to,
        scope,
        doctorId,
        sedation,
        procedureGrouping)));

// Read-only, server-owned evidence projection. POST keeps the selection contract typed and avoids
// placing potentially long calibration evidence identities in a query string.
app.MapPost("/api/reports/audit/query", (DemoBoardStore store, ReportAuditRequest request) =>
{
    try
    {
        return Results.Ok(store.QueryReportAudit(request));
    }
    catch (ReportAuditQueryException exception)
    {
        return Results.BadRequest(new
        {
            code = "invalid-audit-query",
            message = exception.Message
        });
    }
});

// Development-only: populate deterministic, non-PHI synthetic completed cycles for local
// reporting smoke tests. Training and Production never map this endpoint.
if (deploymentEnvironment.IsDevelopment)
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
    // The checked-in UI supplies the stable CompletedCycleId. Retain the (RoomId, SeatedAt)
    // compound-key fallback only for older report clients.
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

// Admin-protected counterpart for truthful pre-arrival after-hours history. These records remain
// aborted assignments outside throughput; confirming review clears only the pending-review flag.
app.MapPost(
    "/api/reports/aborted-assignments/{abortedAssignmentId:long}/confirm-exclusion",
    ExceptionReviewEndpointHandler.ConfirmAbortedAssignmentExclusionAsync);

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

app.MapPost("/api/rooms/{roomNumber:int}/prestage", RoomLifecycleEndpointHandler.BeginPrestageRouteAsync);
app.MapPut("/api/rooms/{roomNumber:int}/assignment-details", RoomLifecycleEndpointHandler.SaveAssignmentDetailsAsync);
app.MapPost("/api/rooms/{roomNumber:int}/seat", RoomLifecycleEndpointHandler.SeatAsync);

app.MapPost("/api/rooms/{roomNumber:int}/cancel-prestage", RoomLifecycleEndpointHandler.CancelPrestageAsync);
app.MapPost("/api/rooms/{roomNumber:int}/cancel-seating", RoomLifecycleEndpointHandler.CancelSeatingAsync);

app.MapPost("/api/rooms/{roomNumber:int}/ready-for-doctor", RoomLifecycleEndpointHandler.ReadyForDoctorAsync);
app.MapPost("/api/rooms/{roomNumber:int}/withdraw-ready", RoomLifecycleEndpointHandler.WithdrawReadyAsync);

app.MapPost("/api/rooms/{roomNumber:int}/doctor-arrived", RoomLifecycleEndpointHandler.DoctorArrivedAsync);

// Resolve a doctor-arrival conflict: complete the conflicting old room (it moves to TURNOVER, not
// Available) and then mark the current room Doctor Arrived. The store revalidates the conflict
// against current state before mutating - the client-supplied conflictingRoomId is not trusted.
app.MapPost(
    "/api/rooms/{roomNumber:int}/doctor-arrived/resolve-conflict",
    DoctorArrivalConflictEndpointHandler.ResolveAsync);

app.MapPost("/api/rooms/{roomNumber:int}/doctor-complete", RoomLifecycleEndpointHandler.DoctorCompleteAsync);

app.MapPost("/api/rooms/{roomNumber:int}/available", RoomLifecycleEndpointHandler.RoomAvailableAsync);

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static string? Truncate(string? value, int maxLength) =>
    value is null ? null : value.Length <= maxLength ? value : value[..maxLength];

static int RunWithDatabaseIsolationRefusal(Func<int> action)
{
    try
    {
        return action();
    }
    catch (DatabaseIsolationException exception)
    {
        return RefuseDatabaseIsolation(exception);
    }
}

static int RefuseDatabaseIsolation(DatabaseIsolationException exception)
{
    Console.Error.WriteLine($"[ChairSide Startup] Refused: {exception.Message}");
    return 2;
}

static void LogDeploymentWarning(
    ILogger logger,
    string controlName,
    DeploymentEnvironment deploymentEnvironment)
{
    try
    {
        logger.LogWarning(
            "{ControlName} is disabled in {Environment}.",
            controlName,
            deploymentEnvironment.EnvironmentName);
    }
    catch (Exception exception) when (exception is AggregateException or InvalidOperationException)
    {
        Console.Error.WriteLine(
            $"[ChairSide Startup] Warning: {controlName} is disabled in {deploymentEnvironment.EnvironmentName}. "
            + $"The configured logging provider could not record the warning: {exception.Message}");
    }
}

// Executes a resolved maintenance command against app services and returns a process exit code.
// Console-only: no HTTP is served. A refusal performs no mutation.
static int RunMaintenance(
    WebApplication maintenanceApp,
    DeploymentEnvironment deploymentEnvironment,
    MaintenanceResolution resolution)
{
    var store = maintenanceApp.Services.GetRequiredService<DemoBoardStore>();
    var repository = maintenanceApp.Services.GetRequiredService<SqliteBoardRepository>();

    Console.WriteLine("[ChairSide Maintenance] Starting.");
    Console.WriteLine($"[ChairSide Maintenance] Environment: {deploymentEnvironment.EnvironmentName}");
    Console.WriteLine($"[ChairSide Maintenance] Database:    {repository.DatabasePath}");
    Console.WriteLine($"[ChairSide Maintenance] Command:     {resolution.Command}");

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

public static class LegacyRoomOneRedirect
{
    public const string Destination = "/room.html?roomId=1";

    public static IResult CreateResult(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Redirect(Destination, permanent: false);
    }
}

public sealed record BeginPrestageRequest(
    string? DoctorId = null,
    string? ProcedureCode = null,
    string? ProcedureId = null,
    bool Sedation = false,
    // Optional final confirmed expected allocation in 10-minute units. When omitted, the
    // selected procedure's default expected units are used. Operational metadata only - never PHI.
    int? ExpectedAllocationUnits = null);

public sealed record CancelRoomAssignmentRequest(string? CancellationReason = null);

// Resolves the procedureCode / procedureId alias pair supplied to Begin Prestage. Both fields name
// the same procedure historically;
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
        var (root, error, _) = await ReadObjectWithPresenceAsync(request, treatWhitespaceAsEmpty: true);
        return (root, error);
    }

    public static async Task<(JsonElement Root, IResult? Error, bool WasBodyEmpty)> ReadObjectWithPresenceAsync(
        HttpRequest request,
        bool treatWhitespaceAsEmpty)
    {
        string raw;
        using (var reader = new StreamReader(request.Body, leaveOpen: true))
        {
            raw = await reader.ReadToEndAsync();
        }

        if (raw.Length == 0 || (treatWhitespaceAsEmpty && string.IsNullOrWhiteSpace(raw)))
        {
            using var empty = JsonDocument.Parse("{}");
            return (empty.RootElement.Clone(), null, true);
        }

        if (!request.HasJsonContentType())
        {
            return (default, Results.BadRequest("Unsupported content type. Expected application/json."), false);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return (default, Results.BadRequest("Malformed JSON request body."), false);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (default, Results.BadRequest("Request body must be a JSON object."), false);
            }

            return (document.RootElement.Clone(), null, false);
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

public static class ExceptionReviewEndpointHandler
{
    public static async Task<IResult> ConfirmAbortedAssignmentExclusionAsync(
        long abortedAssignmentId,
        DemoBoardStore store,
        DiagnosticLogger diagnosticLogger,
        IHubContext<BoardHub> hubContext)
    {
        var result = store.ReviewAbortedAssignmentById(abortedAssignmentId);

        if (result.Outcome == ReviewExceptionOutcome.NotFound)
        {
            return Results.NotFound("No matching aborted assignment found for the supplied abortedAssignmentId.");
        }

        if (result.Outcome == ReviewExceptionOutcome.NotAnException)
        {
            return Results.BadRequest("The aborted assignment exists but is not an exception.");
        }

        await diagnosticLogger.LogRoomAuditAsync(new RoomAuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            Action = "confirm-exclusion",
            RoomNumber = result.RoomId,
            Success = true,
            Reason = ExceptionReasons.AfterHoursSweep
        });

        await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
        return Results.NoContent();
    }
}

public static class RoomLifecycleEndpointHandler
{
    private static readonly IReadOnlyCollection<string> LegacyBeginProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "doctorId", "procedureCode", "procedureId", "sedation", "expectedAllocationUnits"
    };

    public static async Task<IResult> BeginPrestageRouteAsync(
        int roomNumber, HttpContext httpContext, RoomDeviceTokenValidator roomDeviceTokenValidator,
        DemoBoardStore store, DiagnosticLogger diagnosticLogger, IHubContext<BoardHub> hubContext)
    {
        var auditCtx = AuditRequestContext.From(httpContext);
        var previousRoom = store.GetRoom(roomNumber);
        var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(roomNumber, httpContext.Request, roomDeviceTokenValidator);
        if (bindingFailure is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "begin-prestage", roomNumber, previousRoom, "binding-rejected", auditCtx, diagnosticLogger);
            return bindingFailure;
        }
        var (root, bodyError, _) = await StrictJsonRequestReader.ReadObjectWithPresenceAsync(
            httpContext.Request,
            treatWhitespaceAsEmpty: false);
        if (bodyError is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "begin-prestage", roomNumber, previousRoom, PrestagingLifecycleErrorCodes.MalformedRequest, auditCtx, diagnosticLogger);
            return CanonicalError(PrestagingLifecycleErrorCodes.MalformedRequest, "The request body is malformed.");
        }
        if (!root.EnumerateObject().Any())
        {
            return await CompleteCanonicalMutationAsync(
                "begin-prestage", roomNumber, previousRoom, auditCtx,
                store.BeginPrestageCanonical(roomNumber), store, diagnosticLogger, hubContext);
        }
        var propertyError = StrictJsonRequestReader.ValidatePropertySet(root, LegacyBeginProperties);
        if (propertyError is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "begin-prestage", roomNumber, previousRoom, PrestagingLifecycleErrorCodes.MalformedRequest, auditCtx, diagnosticLogger);
            return CanonicalError(PrestagingLifecycleErrorCodes.MalformedRequest, "The request contains unknown or duplicate properties.");
        }
        BeginPrestageRequest? request;
        try { request = JsonSerializer.Deserialize<BeginPrestageRequest>(root.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException)
        {
            await LogCanonicalValidationFailureAsync(
                "begin-prestage", roomNumber, previousRoom, PrestagingLifecycleErrorCodes.MalformedRequest, auditCtx, diagnosticLogger);
            return CanonicalError(PrestagingLifecycleErrorCodes.MalformedRequest, "The compatibility request is malformed.");
        }
        return await BeginPrestageAsync(roomNumber, request ?? new(), httpContext, roomDeviceTokenValidator, store, diagnosticLogger, hubContext);
    }

    public static async Task<IResult> SaveAssignmentDetailsAsync(
        int roomNumber, HttpContext httpContext, RoomDeviceTokenValidator roomDeviceTokenValidator,
        DemoBoardStore store, DiagnosticLogger diagnosticLogger, IHubContext<BoardHub> hubContext)
    {
        var auditCtx = AuditRequestContext.From(httpContext);
        var previousRoom = store.GetRoom(roomNumber);
        var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(roomNumber, httpContext.Request, roomDeviceTokenValidator);
        if (bindingFailure is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "save-assignment-details", roomNumber, previousRoom, "binding-rejected", auditCtx, diagnosticLogger);
            return bindingFailure;
        }
        if (!httpContext.Request.HasJsonContentType())
        {
            await LogCanonicalValidationFailureAsync(
                "save-assignment-details", roomNumber, previousRoom, PrestagingLifecycleErrorCodes.MalformedRequest, auditCtx, diagnosticLogger);
            return CanonicalError(PrestagingLifecycleErrorCodes.MalformedRequest, "The request body must use an application/json-compatible content type.");
        }
        using var reader = new StreamReader(httpContext.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        var parsed = PrestagingLifecycleRequestParser.ParseAssignment(string.IsNullOrWhiteSpace(body) ? null : body);
        if (parsed.Error is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "save-assignment-details", roomNumber, previousRoom, parsed.Error.Code, auditCtx, diagnosticLogger);
            return CanonicalError(parsed.Error, StatusCodes.Status400BadRequest);
        }
        var converted = store.ConvertCanonicalAssignment(parsed.Value!);
        if (converted.Error is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "save-assignment-details", roomNumber, previousRoom, converted.Error.Code, auditCtx, diagnosticLogger);
            return CanonicalError(converted.Error, StatusCodes.Status400BadRequest);
        }
        return await CompleteCanonicalMutationAsync(
            "save-assignment-details", roomNumber, previousRoom, auditCtx,
            store.SaveAssignmentDetailsCanonical(roomNumber, converted.Value!), store, diagnosticLogger, hubContext);
    }

    public static async Task<IResult> ReadyForDoctorAsync(
        int roomNumber, HttpContext httpContext, RoomDeviceTokenValidator roomDeviceTokenValidator,
        DemoBoardStore store, DiagnosticLogger diagnosticLogger, IHubContext<BoardHub> hubContext)
    {
        var auditCtx = AuditRequestContext.From(httpContext);
        var previousRoom = store.GetRoom(roomNumber);
        var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(roomNumber, httpContext.Request, roomDeviceTokenValidator);
        if (bindingFailure is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "ready-for-doctor", roomNumber, previousRoom, "binding-rejected", auditCtx, diagnosticLogger);
            return bindingFailure;
        }

        var (root, bodyError, wasBodyEmpty) = await StrictJsonRequestReader.ReadObjectWithPresenceAsync(
            httpContext.Request,
            treatWhitespaceAsEmpty: false);
        if (bodyError is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "ready-for-doctor", roomNumber, previousRoom, PrestagingLifecycleErrorCodes.MalformedRequest, auditCtx, diagnosticLogger);
            return CanonicalError(PrestagingLifecycleErrorCodes.MalformedRequest, "The request body is malformed.");
        }

        var parsed = PrestagingLifecycleRequestParser.ParseReadyForDoctorAction(root.GetRawText());
        if (parsed.Error is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "ready-for-doctor", roomNumber, previousRoom, parsed.Error.Code, auditCtx, diagnosticLogger);
            return CanonicalError(parsed.Error, StatusCodes.Status400BadRequest);
        }

        RoomAssignmentContract? assignment = null;
        if (parsed.Value!.Assignment is not null)
        {
            var converted = store.ConvertCanonicalAssignment(parsed.Value.Assignment);
            if (converted.Error is not null)
            {
                await LogCanonicalValidationFailureAsync(
                    "ready-for-doctor", roomNumber, previousRoom, converted.Error.Code, auditCtx, diagnosticLogger);
                return CanonicalError(converted.Error, StatusCodes.Status400BadRequest);
            }
            assignment = converted.Value;
        }

        return await CompleteCanonicalMutationAsync(
            "ready-for-doctor", roomNumber, previousRoom, auditCtx,
            store.MarkReadyForDoctorCanonical(roomNumber, assignment), store, diagnosticLogger, hubContext,
            returnLegacyRoomResponse: wasBodyEmpty);
    }

    public static async Task<IResult> WithdrawReadyAsync(
        int roomNumber, HttpContext httpContext, RoomDeviceTokenValidator roomDeviceTokenValidator,
        DemoBoardStore store, DiagnosticLogger diagnosticLogger, IHubContext<BoardHub> hubContext)
    {
        var auditCtx = AuditRequestContext.From(httpContext);
        var previousRoom = store.GetRoom(roomNumber);
        var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(roomNumber, httpContext.Request, roomDeviceTokenValidator);
        if (bindingFailure is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "withdraw-ready", roomNumber, previousRoom, "binding-rejected", auditCtx, diagnosticLogger);
            return bindingFailure;
        }

        var (root, bodyError, _) = await StrictJsonRequestReader.ReadObjectWithPresenceAsync(
            httpContext.Request,
            treatWhitespaceAsEmpty: false);
        if (bodyError is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "withdraw-ready", roomNumber, previousRoom, PrestagingLifecycleErrorCodes.MalformedRequest, auditCtx, diagnosticLogger);
            return CanonicalError(PrestagingLifecycleErrorCodes.MalformedRequest, "The request body is malformed.");
        }

        var parsed = PrestagingLifecycleRequestParser.ParseEmptyAction(root.GetRawText());
        if (parsed.Error is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "withdraw-ready", roomNumber, previousRoom, parsed.Error.Code, auditCtx, diagnosticLogger);
            return CanonicalError(parsed.Error, StatusCodes.Status400BadRequest);
        }

        return await CompleteCanonicalMutationAsync(
            "withdraw-ready", roomNumber, previousRoom, auditCtx,
            store.WithdrawReadyCanonical(roomNumber), store, diagnosticLogger, hubContext);
    }

    public static async Task<IResult> DoctorArrivedAsync(
        int roomNumber, HttpContext httpContext, RoomDeviceTokenValidator roomDeviceTokenValidator,
        DemoBoardStore store, DiagnosticLogger diagnosticLogger, IHubContext<BoardHub> hubContext)
    {
        var auditCtx = AuditRequestContext.From(httpContext);
        var previousRoom = store.GetRoom(roomNumber);
        var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
            roomNumber,
            httpContext.Request,
            roomDeviceTokenValidator);
        if (bindingFailure is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "doctor-arrived", roomNumber, previousRoom, "binding-rejected", auditCtx, diagnosticLogger);
            return bindingFailure;
        }

        var (root, bodyError, wasBodyEmpty) = await StrictJsonRequestReader.ReadObjectWithPresenceAsync(
            httpContext.Request,
            treatWhitespaceAsEmpty: false);
        if (bodyError is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "doctor-arrived", roomNumber, previousRoom, PrestagingLifecycleErrorCodes.MalformedRequest, auditCtx, diagnosticLogger);
            return CanonicalError(PrestagingLifecycleErrorCodes.MalformedRequest, "The request body is malformed.");
        }

        var parsed = PrestagingLifecycleRequestParser.ParseEmptyAction(root.GetRawText());
        if (parsed.Error is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "doctor-arrived", roomNumber, previousRoom, parsed.Error.Code, auditCtx, diagnosticLogger);
            return CanonicalError(parsed.Error, StatusCodes.Status400BadRequest);
        }

        var mutation = store.MarkDoctorArrivedCanonical(roomNumber);
        if (wasBodyEmpty
            && mutation.Outcome == PrestagingLifecycleMutationOutcome.LifecycleConflict
            && mutation.DoctorArrivalConflict is { } conflict)
        {
            await LogCanonicalValidationFailureAsync(
                "doctor-arrived", roomNumber, previousRoom, PrestagingLifecycleErrorCodes.LifecycleConflict, auditCtx, diagnosticLogger);
            return Results.Json(
                new DoctorArrivedConflictResponse(
                    "Doctor is already marked in another room.",
                    conflict.ConflictingRoomId,
                    conflict.DoctorId,
                    conflict.DoctorDisplayName),
                statusCode: StatusCodes.Status409Conflict);
        }

        return await CompleteCanonicalMutationAsync(
            "doctor-arrived", roomNumber, previousRoom, auditCtx,
            mutation, store, diagnosticLogger, hubContext,
            returnLegacyRoomResponse: wasBodyEmpty);
    }

    public static async Task<IResult> DoctorCompleteAsync(
        int roomNumber, HttpContext httpContext, RoomDeviceTokenValidator roomDeviceTokenValidator,
        DemoBoardStore store, DiagnosticLogger diagnosticLogger, IHubContext<BoardHub> hubContext)
    {
        var auditCtx = AuditRequestContext.From(httpContext);
        var previousRoom = store.GetRoom(roomNumber);
        var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
            roomNumber,
            httpContext.Request,
            roomDeviceTokenValidator);
        if (bindingFailure is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "doctor-complete", roomNumber, previousRoom, "binding-rejected", auditCtx, diagnosticLogger);
            return bindingFailure;
        }

        if (previousRoom?.IntegrityFaults is { Count: > 0 })
        {
            return await CompleteIntegrityFaultAsync(
                "doctor-complete", roomNumber, previousRoom, auditCtx, diagnosticLogger);
        }

        var result = store.MarkDoctorComplete(roomNumber);
        if (result is null)
        {
            var currentRoom = store.GetRoom(roomNumber);
            if (currentRoom?.IntegrityFaults is { Count: > 0 })
            {
                return await CompleteIntegrityFaultAsync(
                    "doctor-complete", roomNumber, currentRoom, auditCtx, diagnosticLogger);
            }

            var reason = store.IsConfiguredRoom(roomNumber) ? "state-rejected" : "room-not-found";
            await LogCanonicalValidationFailureAsync(
                "doctor-complete", roomNumber, previousRoom, reason, auditCtx, diagnosticLogger);
            return store.IsConfiguredRoom(roomNumber)
                ? Results.BadRequest("Doctor Complete is only available when the doctor is in the room.")
                : Results.NotFound("Room is not configured.");
        }

        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "doctor-complete", roomNumber, previousRoom, result, true, null,
            result.AssignedDoctor, result.ProcedureCode));
        await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
        return Results.Ok(result);
    }

    public static async Task<IResult> RoomAvailableAsync(
        int roomNumber, HttpContext httpContext, RoomDeviceTokenValidator roomDeviceTokenValidator,
        DemoBoardStore store, DiagnosticLogger diagnosticLogger, IHubContext<BoardHub> hubContext)
    {
        var auditCtx = AuditRequestContext.From(httpContext);
        var previousRoom = store.GetRoom(roomNumber);
        var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
            roomNumber,
            httpContext.Request,
            roomDeviceTokenValidator);
        if (bindingFailure is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "room-available", roomNumber, previousRoom, "binding-rejected", auditCtx, diagnosticLogger);
            return bindingFailure;
        }

        if (previousRoom?.IntegrityFaults is { Count: > 0 })
        {
            return await CompleteIntegrityFaultAsync(
                "room-available", roomNumber, previousRoom, auditCtx, diagnosticLogger);
        }

        var result = store.MarkRoomAvailable(roomNumber);
        if (result is null)
        {
            var currentRoom = store.GetRoom(roomNumber);
            if (currentRoom?.IntegrityFaults is { Count: > 0 })
            {
                return await CompleteIntegrityFaultAsync(
                    "room-available", roomNumber, currentRoom, auditCtx, diagnosticLogger);
            }

            var reason = store.IsConfiguredRoom(roomNumber) ? "state-rejected" : "room-not-found";
            await LogCanonicalValidationFailureAsync(
                "room-available", roomNumber, previousRoom, reason, auditCtx, diagnosticLogger);
            return store.IsConfiguredRoom(roomNumber)
                ? Results.BadRequest("Room Available is only available during turnover.")
                : Results.NotFound("Room is not configured.");
        }

        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            "room-available", roomNumber, previousRoom, result, true, null,
            previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
        await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
        return Results.Ok(result);
    }

    private static async Task<IResult> CompleteIntegrityFaultAsync(
        string action,
        int roomNumber,
        RoomStatus room,
        AuditRequestContext auditCtx,
        DiagnosticLogger diagnosticLogger)
    {
        await LogCanonicalValidationFailureAsync(
            action,
            roomNumber,
            room,
            PrestagingLifecycleErrorCodes.IntegrityFault,
            auditCtx,
            diagnosticLogger);
        return CanonicalError(
            new PrestagingLifecycleErrorResponse(
                PrestagingLifecycleErrorCodes.IntegrityFault,
                "The room has an integrity fault.",
                [],
                room.IntegrityFaults ?? []),
            StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> CompleteCanonicalMutationAsync(
        string action, int roomNumber, RoomStatus? previousRoom, AuditRequestContext auditCtx,
        PrestagingLifecycleMutationResult mutation,
        DemoBoardStore store, DiagnosticLogger diagnosticLogger, IHubContext<BoardHub> hubContext,
        bool returnLegacyRoomResponse = false)
    {
        if (mutation.Outcome != PrestagingLifecycleMutationOutcome.Success)
        {
            var mapped = MapCanonicalFailure(mutation);
            await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
                action, roomNumber, previousRoom, null, false, mapped.Error.Code,
                previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
            return CanonicalError(mapped.Error, mapped.StatusCode);
        }
        var room = mutation.Room!;
        var state = room.State switch
        {
            RoomStates.Prestaging => CanonicalRoomLifecycleState.Prestaging,
            RoomStates.Seated => CanonicalRoomLifecycleState.SeatedInPrep,
            RoomStates.ReadyForDoctor or RoomStates.Aging or RoomStates.Stale => CanonicalRoomLifecycleState.ReadyForDoctor,
            RoomStates.DoctorInRoom => CanonicalRoomLifecycleState.DoctorWorking,
            _ => throw new InvalidOperationException($"Canonical mutation returned unsupported room state '{room.State}'.")
        };
        await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            action, roomNumber, previousRoom, room, true, null, room.AssignedDoctor, room.ProcedureCode));
        await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
        if (returnLegacyRoomResponse)
        {
            return Results.Ok(room);
        }
        return Results.Ok(PrestagingLifecycleResponseProjector.Create(room, state, mutation.Assignment!, mutation.Handoff));
    }

    private static Task LogCanonicalValidationFailureAsync(
        string action,
        int roomNumber,
        RoomStatus? previousRoom,
        string reason,
        AuditRequestContext auditCtx,
        DiagnosticLogger diagnosticLogger) =>
        diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
            action,
            roomNumber,
            previousRoom,
            null,
            false,
            reason,
            previousRoom?.AssignedDoctor,
            previousRoom?.ProcedureCode));

    internal static (PrestagingLifecycleErrorResponse Error, int StatusCode) MapCanonicalFailure(
        PrestagingLifecycleMutationResult mutation)
    {
        var (code, status, message) = mutation.Outcome switch
        {
            PrestagingLifecycleMutationOutcome.RoomNotFound => (PrestagingLifecycleErrorCodes.RoomNotFound, 404, "Room is not configured."),
            PrestagingLifecycleMutationOutcome.InvalidAssignment => (PrestagingLifecycleErrorCodes.InvalidAssignment, 400, "The assignment is invalid."),
            PrestagingLifecycleMutationOutcome.AssignmentIncomplete => (PrestagingLifecycleErrorCodes.AssignmentIncomplete, 409, "Ready for Doctor requires a complete assignment."),
            PrestagingLifecycleMutationOutcome.AssignmentLocked => (PrestagingLifecycleErrorCodes.AssignmentLocked, 409, "The Ready assignment is locked."),
            PrestagingLifecycleMutationOutcome.IntegrityFault => (PrestagingLifecycleErrorCodes.IntegrityFault, 409, "The room has an integrity fault."),
            PrestagingLifecycleMutationOutcome.StaleWrite => (PrestagingLifecycleErrorCodes.StaleWrite, 409, "The room changed; reload before retrying."),
            PrestagingLifecycleMutationOutcome.PersistenceFailure => (PrestagingLifecycleErrorCodes.PersistenceFailure, 500, "The room could not be persisted."),
            _ => (PrestagingLifecycleErrorCodes.LifecycleConflict, 409, "The room is not in a valid lifecycle state for this action.")
        };

        var unresolvedFields = mutation.Outcome == PrestagingLifecycleMutationOutcome.AssignmentIncomplete
            && mutation.Assignment is not null
                ? CanonicalAssignmentRequirements.GetUnresolvedFields(mutation.Assignment)
                : [];
        return (new(code, message, unresolvedFields, mutation.IntegrityFaults ?? []), status);
    }

    private static IResult CanonicalError(string code, string message) => CanonicalError(new(code, message, [], []), 400);
    private static IResult CanonicalError(PrestagingLifecycleErrorResponse error, int status) => Results.Json(error, statusCode: status);
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

        var (root, bodyError, _) = await StrictJsonRequestReader.ReadObjectWithPresenceAsync(
            httpContext.Request,
            treatWhitespaceAsEmpty: false);
        if (bodyError is not null)
        {
            await diagnosticLogger.LogRoomAuditAsync(auditCtx.Build(
                "seat", roomNumber, previousRoom, null, false, PrestagingLifecycleErrorCodes.MalformedRequest,
                previousRoom?.AssignedDoctor, previousRoom?.ProcedureCode));
            return CanonicalError(PrestagingLifecycleErrorCodes.MalformedRequest, "The request body is malformed.");
        }

        var parsedCanonical = PrestagingLifecycleRequestParser.ParseSeatAction(root.GetRawText());
        if (parsedCanonical.Error is not null)
        {
            await LogCanonicalValidationFailureAsync(
                "seat", roomNumber, previousRoom, parsedCanonical.Error.Code, auditCtx, diagnosticLogger);
            return CanonicalError(parsedCanonical.Error, 400);
        }
        RoomAssignmentContract? assignment = null;
        if (parsedCanonical.Value!.Assignment is { } requestAssignment)
        {
            var converted = store.ConvertCanonicalAssignment(requestAssignment);
            if (converted.Error is not null)
            {
                await LogCanonicalValidationFailureAsync(
                    "seat", roomNumber, previousRoom, converted.Error.Code, auditCtx, diagnosticLogger);
                return CanonicalError(converted.Error, 400);
            }
            assignment = converted.Value;
        }

        return await CompleteCanonicalMutationAsync(
            "seat", roomNumber, previousRoom, auditCtx,
            store.SeatRoomCanonical(roomNumber, assignment), store, diagnosticLogger, hubContext);
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
