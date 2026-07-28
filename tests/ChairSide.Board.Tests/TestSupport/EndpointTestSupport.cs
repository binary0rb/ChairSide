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
    private static RoomDeviceTokenValidator CreateBindingValidator(bool enabled) =>
        new(new TestOptionsMonitor<RoomDeviceBindingOptions>(new RoomDeviceBindingOptions
        {
            Enabled = enabled,
            RoomTokens = new Dictionary<string, string>
            {
                ["1"] = "room-1-token",
                ["2"] = "room-2-token"
            }
        }));

    private static AdminAccessTokenValidator CreateAdminValidator(bool enabled) =>
        new(new TestOptionsMonitor<AdminAccessOptions>(new AdminAccessOptions
        {
            Enabled = enabled,
            SharedToken = "admin-token"
        }));

    private static ValidateOptionsResult ValidateBindingOptions(
        int roomCount,
        RoomDeviceBindingOptions options) =>
        new RoomDeviceBindingOptionsValidator(
            Microsoft.Extensions.Options.Options.Create(new BoardOptions { RoomCount = roomCount }))
            .Validate(null, options);

    private static ValidateOptionsResult ValidateAdminAccessOptions(
        AdminAccessOptions options,
        string environmentName = "Development") =>
        new AdminAccessOptionsValidator(DeploymentEnvironmentPolicy.Resolve(environmentName))
            .Validate(null, options);

    private static DiagnosticLogger CreateDiagnosticLogger(
        string logDirectory,
        string contentRoot,
        long maxFileSizeBytes = 50_000_000) =>
        new(
            Microsoft.Extensions.Options.Options.Create(new DiagnosticOptions
            {
                LogDirectory = logDirectory,
                MaxFileSizeBytes = maxFileSizeBytes
            }),
            new TestWebHostEnvironment(contentRoot, Environments.Production));

    private static DefaultHttpContext NewResolveConflictHttpContext(int roomNumber, string? token)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = $"/api/rooms/{roomNumber}/doctor-arrived/resolve-conflict";
        if (token is not null)
        {
            context.Request.Headers[RoomDeviceTokenValidator.HeaderName] = token;
        }

        return context;
    }

    private static DefaultHttpContext NewRoomMutationHttpContext(int roomNumber, string? token)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = $"/api/rooms/{roomNumber}";
        if (token is not null)
        {
            context.Request.Headers[RoomDeviceTokenValidator.HeaderName] = token;
        }

        return context;
    }

    // Builds a room-mutation HttpContext carrying a real request body, so tests exercise the actual
    // strict JSON parsing path through DefaultHttpContext rather than constructing a bound DTO.
    private static DefaultHttpContext NewJsonBodyContext(int roomNumber, string? token, string body, string? contentType = "application/json")
    {
        var context = NewRoomMutationHttpContext(roomNumber, token);
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        if (contentType is not null)
        {
            context.Request.ContentType = contentType;
        }

        return context;
    }

    private static async Task<IReadOnlyList<RoomAuditEntry>> ReadRoomAuditEntries(string logDirectory)
    {
        var logPath = Path.Combine(logDirectory, "room-audit.log");
        if (!File.Exists(logPath))
        {
            return [];
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var entries = new List<RoomAuditEntry>();
        foreach (var line in await File.ReadAllLinesAsync(logPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<RoomAuditEntry>(line, options);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static ValidateOptionsResult ValidateDoctorRoster(DoctorRosterOptions options) =>
        new DoctorRosterOptionsValidator().Validate(null, options);

    private static ValidateOptionsResult ValidateProcedureRoster(ProcedureRosterOptions options) =>
        new ProcedureRosterOptionsValidator().Validate(null, options);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ChairSide.Board.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("Could not find ChairSide.Board.sln.");
        }

        return directory.FullName;
    }

    private static HttpRequest RequestWithHeader(string? token)
    {
        var context = new DefaultHttpContext();
        if (token is not null)
        {
            context.Request.Headers[RoomDeviceTokenValidator.HeaderName] = token;
        }

        return context.Request;
    }

    private static HttpRequest RequestWithQueryToken(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?roomToken={Uri.EscapeDataString(token)}");
        return context.Request;
    }

    private static HttpRequest RequestWithAdminHeader(string? token)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/reports";
        if (token is not null)
        {
            context.Request.Headers[AdminAccessTokenValidator.HeaderName] = token;
        }

        return context.Request;
    }

    private static HttpRequest RequestWithAdminQueryToken(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/reports";
        context.Request.QueryString = new QueryString($"?adminToken={Uri.EscapeDataString(token)}");
        return context.Request;
    }

    private static async Task<int?> ExecuteBindingResult(IResult? result)
    {
        if (result is null)
        {
            return null;
        }

        if (result is IStatusCodeHttpResult statusCodeHttpResult)
        {
            return statusCodeHttpResult.StatusCode;
        }

        var context = new DefaultHttpContext();
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }
}
