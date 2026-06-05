using System.Text.Json;
using System.Text.Json.Serialization;

using ChairSide.Board.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Services;

/// <summary>
/// Lightweight append-only file logger for production diagnostics.
/// Writes JSON lines to client-errors.log and room-audit.log.
/// Failures are swallowed and written to stderr so the application never crashes.
/// No PHI is written by this service.
/// </summary>
public sealed class DiagnosticLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _clientErrorLogPath;
    private readonly string _roomAuditLogPath;
    private readonly long _maxFileSizeBytes;
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private readonly SemaphoreSlim _auditLock = new(1, 1);

    public DiagnosticLogger(IOptions<DiagnosticOptions> options, IWebHostEnvironment environment)
    {
        var opts = options.Value;
        var directory = ResolveLogDirectory(opts.LogDirectory, environment.ContentRootPath);
        TryCreateDirectory(directory);
        _clientErrorLogPath = Path.Combine(directory, "client-errors.log");
        _roomAuditLogPath = Path.Combine(directory, "room-audit.log");
        _maxFileSizeBytes = opts.MaxFileSizeBytes;
    }

    public async Task LogClientErrorAsync(ClientErrorEntry entry)
    {
        await AppendJsonLineAsync(_clientErrorLogPath, entry, _clientLock, _maxFileSizeBytes);
    }

    public async Task LogRoomAuditAsync(RoomAuditEntry entry)
    {
        await AppendJsonLineAsync(_roomAuditLogPath, entry, _auditLock, _maxFileSizeBytes);
    }

    private static async Task AppendJsonLineAsync<T>(
        string filePath, T entry, SemaphoreSlim semaphore, long maxFileSizeBytes)
    {
        try
        {
            var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
            await semaphore.WaitAsync();
            try
            {
                TryRotate(filePath, maxFileSizeBytes);
                await File.AppendAllTextAsync(filePath, line);
            }
            finally
            {
                semaphore.Release();
            }
        }
        catch (Exception ex)
        {
            // Logging must never disrupt the application.
            Console.Error.WriteLine($"[ChairSide] DiagnosticLogger write failed ({filePath}): {ex.Message}");
        }
    }

    /// <summary>
    /// Rotates <paramref name="filePath"/> to <c>.log.1</c> when it meets or exceeds
    /// <paramref name="maxFileSizeBytes"/>. Must be called while the per-file semaphore
    /// is held. Any I/O failure is caught and written to stderr so it never blocks writes.
    /// </summary>
    private static void TryRotate(string filePath, long maxFileSizeBytes)
    {
        if (maxFileSizeBytes <= 0)
        {
            return; // rotation disabled
        }

        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            if (new FileInfo(filePath).Length < maxFileSizeBytes)
            {
                return;
            }

            var rotatedPath = filePath + ".1";
            if (File.Exists(rotatedPath))
            {
                File.Delete(rotatedPath);
            }

            File.Move(filePath, rotatedPath);
        }
        catch (Exception ex)
        {
            // Rotation failure must not block room workflow — log to stderr and continue.
            Console.Error.WriteLine($"[ChairSide] DiagnosticLogger rotation failed ({filePath}): {ex.Message}");
        }
    }

    private static void TryCreateDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ChairSide] DiagnosticLogger could not create directory ({directory}): {ex.Message}");
        }
    }

    private static string ResolveLogDirectory(string logDirectory, string contentRootPath) =>
        Path.IsPathRooted(logDirectory)
            ? logDirectory
            : Path.GetFullPath(Path.Combine(contentRootPath, logDirectory));
}

// ---------------------------------------------------------------------------
// Log entry types — only non-PHI technical fields.
// ---------------------------------------------------------------------------

public sealed class ClientErrorEntry
{
    public string? ServerTimestamp { get; init; }
    public string? ClientTimestamp { get; init; }
    public string? Url { get; init; }
    public string? RoomId { get; init; }
    public string? View { get; init; }
    public string? Message { get; init; }
    public string? Source { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public string? Stack { get; init; }
    public string? UserAgent { get; init; }
    public string? ConnectionStatus { get; init; }
    public long? LastSnapshotAt { get; init; }
    public long? SnapshotAgeMs { get; init; }
    public string? ClientIp { get; init; }
}

public sealed class RoomAuditEntry
{
    public string? Timestamp { get; init; }
    public string? Action { get; init; }
    public int RoomNumber { get; init; }
    public string? PreviousState { get; init; }
    public string? NewState { get; init; }
    public string? DoctorId { get; init; }
    public string? ProcedureCode { get; init; }
    public bool Success { get; init; }
    public string? Reason { get; init; }
    public string? ClientIp { get; init; }
    public string? UserAgent { get; init; }
    public string? RequestPath { get; init; }
    public string? Referrer { get; init; }
}
