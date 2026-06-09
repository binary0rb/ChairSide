using ChairSide.Board.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ChairSide.Board.Services;

/// <summary>
/// Background service that checks for over-duration active room cycles every minute
/// and fires the after-hours sweep when the configured clinic time is reached.
/// Broadcasts boardUpdated to all connected clients when rooms are expired.
/// </summary>
public sealed class RoomExpirationService : BackgroundService
{
    private readonly DemoBoardStore _store;
    private readonly IHubContext<BoardHub> _hubContext;
    private readonly DiagnosticLogger _diagnosticLogger;
    private readonly ILogger<RoomExpirationService> _logger;

    public RoomExpirationService(
        DemoBoardStore store,
        IHubContext<BoardHub> hubContext,
        DiagnosticLogger diagnosticLogger,
        ILogger<RoomExpirationService> logger)
    {
        _store = store;
        _hubContext = hubContext;
        _diagnosticLogger = diagnosticLogger;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await CheckExpirationsAsync();
        }
    }

    private async Task CheckExpirationsAsync()
    {
        try
        {
            var expiredMaxDuration = _store.CheckAndExpireActiveCycles();
            var expiredSweep = _store.TryRunAfterHoursSweep();

            if (expiredMaxDuration.Count > 0 || expiredSweep.Count > 0)
            {
                foreach (var roomId in expiredMaxDuration)
                {
                    await _diagnosticLogger.LogRoomAuditAsync(new RoomAuditEntry
                    {
                        Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                        Action = "force-expire",
                        RoomNumber = roomId,
                        NewState = RoomStates.Available,
                        Success = true,
                        Reason = ExceptionReasons.ExceededMaxActiveDuration
                    });
                }

                foreach (var roomId in expiredSweep)
                {
                    await _diagnosticLogger.LogRoomAuditAsync(new RoomAuditEntry
                    {
                        Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                        Action = "after-hours-sweep",
                        RoomNumber = roomId,
                        NewState = RoomStates.Available,
                        Success = true,
                        Reason = ExceptionReasons.AfterHoursSweep
                    });
                }

                await _hubContext.Clients.All.SendAsync("boardUpdated", _store.GetSnapshot());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Room expiration check failed.");
        }
    }
}
