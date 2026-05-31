using Microsoft.AspNetCore.SignalR;

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
    .AddOptions<BoardPersistenceOptions>()
    .Bind(builder.Configuration.GetSection(BoardPersistenceOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.DatabasePath), "DatabasePath is required.")
    .ValidateOnStart();

builder.Services
    .AddOptions<RoomDeviceBindingOptions>()
    .Bind(builder.Configuration.GetSection(RoomDeviceBindingOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddSignalR();
builder.Services.AddSingleton<SqliteBoardRepository>();
builder.Services.AddSingleton<DemoBoardStore>();
builder.Services.AddSingleton<RoomDeviceTokenValidator>();

var app = builder.Build();
_ = app.Services.GetRequiredService<DemoBoardStore>();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<BoardHub>("/boardHub");

app.MapGet("/api/board", (DemoBoardStore store) => store.GetSnapshot());

app.MapGet("/api/reports", (DemoBoardStore store) => store.GetReports());

app.MapGet("/api/rooms/{roomNumber:int}", IResult (int roomNumber, DemoBoardStore store) =>
{
    var room = store.GetRoom(roomNumber);
    return room is null ? Results.NotFound("Room is not configured.") : Results.Ok(room);
});

app.MapPost("/api/rooms/{roomNumber:int}/seat", async Task<IResult> (
    int roomNumber,
    SeatRoomRequest request,
    HttpContext httpContext,
    RoomDeviceTokenValidator roomDeviceTokenValidator,
    DemoBoardStore store,
    Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
{
    var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
        roomNumber,
        httpContext.Request,
        roomDeviceTokenValidator);
    if (bindingFailure is not null)
    {
        return bindingFailure;
    }

    var procedureCode = request.ProcedureCode ?? request.ProcedureId;
    if (string.IsNullOrWhiteSpace(procedureCode))
    {
        return Results.BadRequest("Procedure code is required.");
    }

    var result = store.SeatRoom(roomNumber, request.DoctorId, procedureCode, request.DemoElapsedMinutes);
    if (result is null)
    {
        return store.IsConfiguredRoom(roomNumber)
            ? Results.BadRequest("Seat Room is only available when the room is available and the selected doctor and procedure are valid.")
            : Results.NotFound("Room is not configured.");
    }

    await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    return Results.Ok(result);
});

app.MapPost("/api/rooms/{roomNumber:int}/assignment", async Task<IResult> (
    int roomNumber,
    UpdateAssignmentRequest request,
    HttpContext httpContext,
    RoomDeviceTokenValidator roomDeviceTokenValidator,
    DemoBoardStore store,
    Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
{
    var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
        roomNumber,
        httpContext.Request,
        roomDeviceTokenValidator);
    if (bindingFailure is not null)
    {
        return bindingFailure;
    }

    var procedureCode = request.ProcedureCode ?? request.ProcedureId;
    if (string.IsNullOrWhiteSpace(procedureCode))
    {
        return Results.BadRequest("Procedure code is required.");
    }

    var result = store.UpdateAssignment(roomNumber, request.DoctorId, procedureCode);
    if (result is null)
    {
        return store.IsConfiguredRoom(roomNumber)
            ? Results.BadRequest("Update Assignment is only available for seated, aging, or stale rooms with a valid doctor and procedure.")
            : Results.NotFound("Room is not configured.");
    }

    await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    return Results.Ok(result);
});

app.MapPost("/api/rooms/{roomNumber:int}/cancel-seating", async Task<IResult> (
    int roomNumber,
    HttpContext httpContext,
    RoomDeviceTokenValidator roomDeviceTokenValidator,
    DemoBoardStore store,
    Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
{
    var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
        roomNumber,
        httpContext.Request,
        roomDeviceTokenValidator);
    if (bindingFailure is not null)
    {
        return bindingFailure;
    }

    var result = store.CancelSeating(roomNumber);
    if (result is null)
    {
        return store.IsConfiguredRoom(roomNumber)
            ? Results.BadRequest("Cancel Seating is only available for seated, aging, or stale rooms.")
            : Results.NotFound("Room is not configured.");
    }

    await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    return Results.Ok(result);
});

app.MapPost("/api/rooms/{roomNumber:int}/doctor-arrived", async Task<IResult> (
    int roomNumber,
    HttpContext httpContext,
    RoomDeviceTokenValidator roomDeviceTokenValidator,
    DemoBoardStore store,
    Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
{
    var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
        roomNumber,
        httpContext.Request,
        roomDeviceTokenValidator);
    if (bindingFailure is not null)
    {
        return bindingFailure;
    }

    var result = store.MarkDoctorArrived(roomNumber);
    if (result is null)
    {
        return store.IsConfiguredRoom(roomNumber)
            ? Results.BadRequest("Doctor Arrived is only available for seated, aging, or stale rooms.")
            : Results.NotFound("Room is not configured.");
    }

    await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    return Results.Ok(result);
});

app.MapPost("/api/rooms/{roomNumber:int}/doctor-complete", async Task<IResult> (
    int roomNumber,
    HttpContext httpContext,
    RoomDeviceTokenValidator roomDeviceTokenValidator,
    DemoBoardStore store,
    Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
{
    var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
        roomNumber,
        httpContext.Request,
        roomDeviceTokenValidator);
    if (bindingFailure is not null)
    {
        return bindingFailure;
    }

    var result = store.MarkDoctorComplete(roomNumber);
    if (result is null)
    {
        return store.IsConfiguredRoom(roomNumber)
            ? Results.BadRequest("Doctor Complete is only available when the doctor is in the room.")
            : Results.NotFound("Room is not configured.");
    }

    await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    return Results.Ok(result);
});

app.MapPost("/api/rooms/{roomNumber:int}/available", async Task<IResult> (
    int roomNumber,
    HttpContext httpContext,
    RoomDeviceTokenValidator roomDeviceTokenValidator,
    DemoBoardStore store,
    Microsoft.AspNetCore.SignalR.IHubContext<BoardHub> hubContext) =>
{
    var bindingFailure = RoomDeviceBindingGuard.ValidateMutationRequest(
        roomNumber,
        httpContext.Request,
        roomDeviceTokenValidator);
    if (bindingFailure is not null)
    {
        return bindingFailure;
    }

    var result = store.MarkRoomAvailable(roomNumber);
    if (result is null)
    {
        return store.IsConfiguredRoom(roomNumber)
            ? Results.BadRequest("Room Available is only available during turnover.")
            : Results.NotFound("Room is not configured.");
    }

    await hubContext.Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    return Results.Ok(result);
});

app.Run();

public sealed record SeatRoomRequest(
    string DoctorId,
    string? ProcedureCode = null,
    string? ProcedureId = null,
    int DemoElapsedMinutes = 0);

public sealed record UpdateAssignmentRequest(
    string DoctorId,
    string? ProcedureCode = null,
    string? ProcedureId = null);
