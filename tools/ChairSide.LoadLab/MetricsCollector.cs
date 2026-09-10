namespace ChairSide.LoadLab;

// Thread-safe aggregator for all load lab metrics.
// Poll results are routed to the appropriate LatencyTracker by URL.
// Mutation results use the dedicated RecordMutation path.
public sealed class MetricsCollector
{
    // Per-endpoint latency trackers. Public so tests can inspect counts directly.
    public LatencyTracker Board { get; } = new();
    public LatencyTracker Reports { get; } = new();
    public LatencyTracker Mutations { get; } = new();

    private long _totalRequests;
    private long _failedRequests;
    private long _exceptionCount;
    private long _mutationsAttempted;
    private long _mutationsFailed;

    private readonly Dictionary<int, long> _statusCodes = new();
    private readonly object _statusLock = new();

    // Record a GET poll result. Routes latency to Board or Reports tracker by URL.
    public void RecordPoll(string url, int statusCode, long latencyMs, bool isException)
    {
        Interlocked.Increment(ref _totalRequests);

        if (isException || statusCode >= 400)
        {
            Interlocked.Increment(ref _failedRequests);
        }

        if (isException)
        {
            Interlocked.Increment(ref _exceptionCount);
        }

        lock (_statusLock)
        {
            _statusCodes.TryGetValue(statusCode, out var existing);
            _statusCodes[statusCode] = existing + 1;
        }

        if (url.Contains("/api/reports", StringComparison.OrdinalIgnoreCase))
        {
            Reports.Record(latencyMs);
        }
        else
        {
            Board.Record(latencyMs);
        }
    }

    // Record a lifecycle mutation result.
    public void RecordMutation(long latencyMs, bool success)
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _mutationsAttempted);

        if (!success)
        {
            Interlocked.Increment(ref _failedRequests);
            Interlocked.Increment(ref _mutationsFailed);
        }

        Mutations.Record(latencyMs);
    }

    public long TotalRequests  => Interlocked.Read(ref _totalRequests);
    public long FailedRequests => Interlocked.Read(ref _failedRequests);
    public long MutationsAttempted => Interlocked.Read(ref _mutationsAttempted);
    public long MutationsFailed    => Interlocked.Read(ref _mutationsFailed);

    public MetricsSummary GetSummary()
    {
        Dictionary<int, long> statusCodes;
        lock (_statusLock)
        {
            statusCodes = new Dictionary<int, long>(_statusCodes);
        }

        return new MetricsSummary(
            TotalRequests:      Interlocked.Read(ref _totalRequests),
            FailedRequests:     Interlocked.Read(ref _failedRequests),
            ExceptionCount:     Interlocked.Read(ref _exceptionCount),
            MutationsAttempted: Interlocked.Read(ref _mutationsAttempted),
            MutationsFailed:    Interlocked.Read(ref _mutationsFailed),
            BoardLatency:       Board.Compute(),
            ReportsLatency:     Reports.Compute(),
            MutationsLatency:   Mutations.Compute(),
            StatusCodes:        statusCodes
        );
    }
}

public sealed record MetricsSummary(
    long TotalRequests,
    long FailedRequests,
    long ExceptionCount,
    long MutationsAttempted,
    long MutationsFailed,
    LatencyStats BoardLatency,
    LatencyStats ReportsLatency,
    LatencyStats MutationsLatency,
    Dictionary<int, long> StatusCodes
);
