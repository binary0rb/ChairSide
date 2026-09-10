namespace ChairSide.LoadLab;

// Thread-safe bucket that accumulates request latency samples (milliseconds)
// and computes summary statistics on demand.
public sealed class LatencyTracker
{
    private readonly List<long> _samples = new();
    private readonly object _lock = new();

    public void Record(long milliseconds)
    {
        lock (_lock)
        {
            _samples.Add(milliseconds);
        }
    }

    public int Count
    {
        get { lock (_lock) return _samples.Count; }
    }

    // Returns zero-value stats if no samples have been recorded yet.
    public LatencyStats Compute()
    {
        lock (_lock)
        {
            if (_samples.Count == 0)
            {
                return new LatencyStats(Min: 0, Avg: 0.0, P95: 0, Max: 0, Count: 0);
            }

            var sorted = _samples.OrderBy(x => x).ToArray();

            // p95: ceil(n * 0.95) gives the 1-based rank; subtract 1 for 0-based index.
            // Example: 20 samples -> ceil(19.0) - 1 = 18 -> sorted[18] is the 19th value.
            var p95Index = (int)Math.Ceiling(sorted.Length * 0.95) - 1;
            p95Index = Math.Max(0, Math.Min(p95Index, sorted.Length - 1));

            return new LatencyStats(
                Min: sorted[0],
                Avg: sorted.Average(),
                P95: sorted[p95Index],
                Max: sorted[^1],
                Count: sorted.Length
            );
        }
    }
}

public sealed record LatencyStats(long Min, double Avg, long P95, long Max, int Count);
