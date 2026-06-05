using System.Collections.Concurrent;

using ChairSide.Board.Options;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Services;

/// <summary>
/// Simple in-memory per-IP rate limiter for the /api/client-errors endpoint.
/// Uses a fixed one-minute window per source IP address.
/// </summary>
public sealed class ClientErrorRateLimiter
{
    // Mutable bucket, always accessed under its own lock.
    private sealed class Bucket
    {
        public int Count;
        public long WindowStartTicks;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IOptionsMonitor<DiagnosticOptions> _options;
    private readonly TimeProvider _timeProvider;

    public ClientErrorRateLimiter(
        IOptionsMonitor<DiagnosticOptions> options,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns <see langword="true"/> if the request from <paramref name="ip"/>
    /// should be allowed; <see langword="false"/> if the per-minute limit has been
    /// exceeded. Null or empty IPs are always allowed (unknown/proxied source).
    /// </summary>
    public bool IsAllowed(string? ip)
    {
        var limit = _options.CurrentValue.ClientErrorRateLimitPerMinute;
        if (limit <= 0)
        {
            return true; // rate limiting disabled
        }

        if (string.IsNullOrEmpty(ip))
        {
            return true; // unknown source — allow and let the logger truncate fields
        }

        var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
        var windowTicks = TimeSpan.TicksPerMinute;

        var bucket = _buckets.GetOrAdd(ip, _ => new Bucket());
        lock (bucket)
        {
            if (nowTicks - bucket.WindowStartTicks >= windowTicks)
            {
                // Start a new one-minute window.
                bucket.Count = 1;
                bucket.WindowStartTicks = nowTicks;
                return true;
            }

            bucket.Count++;
            return bucket.Count <= limit;
        }
    }
}
