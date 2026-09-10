using ChairSide.LoadLab;

namespace ChairSide.LoadLab.Tests;

public sealed class LatencyTrackerTests
{
    [Fact]
    public void Empty_tracker_returns_zero_stats()
    {
        var tracker = new LatencyTracker();
        var stats = tracker.Compute();
        Assert.Equal(0, stats.Count);
        Assert.Equal(0, stats.Min);
        Assert.Equal(0.0, stats.Avg);
        Assert.Equal(0, stats.P95);
        Assert.Equal(0, stats.Max);
    }

    [Fact]
    public void Single_sample_all_values_equal_that_sample()
    {
        var tracker = new LatencyTracker();
        tracker.Record(42);
        var stats = tracker.Compute();
        Assert.Equal(1, stats.Count);
        Assert.Equal(42, stats.Min);
        Assert.Equal(42.0, stats.Avg);
        Assert.Equal(42, stats.P95);
        Assert.Equal(42, stats.Max);
    }

    [Fact]
    public void Min_avg_max_are_computed_correctly()
    {
        var tracker = new LatencyTracker();
        tracker.Record(10);
        tracker.Record(20);
        tracker.Record(30);
        var stats = tracker.Compute();
        Assert.Equal(10, stats.Min);
        Assert.Equal(20.0, stats.Avg, precision: 1);
        Assert.Equal(30, stats.Max);
        Assert.Equal(3, stats.Count);
    }

    [Fact]
    public void P95_with_20_samples_excludes_top_5_percent()
    {
        // Samples 1..20 in random order. Sorted: [1..20].
        // p95 index = ceil(20 * 0.95) - 1 = ceil(19.0) - 1 = 18 (0-based).
        // sorted[18] = 19.
        var tracker = new LatencyTracker();
        // Insert in shuffled order to prove sorting is applied.
        foreach (var v in new[] { 5, 18, 3, 11, 7, 20, 14, 1, 9, 16, 2, 13, 6, 19, 4, 17, 8, 15, 10, 12 })
            tracker.Record(v);

        var stats = tracker.Compute();
        Assert.Equal(1,  stats.Min);
        Assert.Equal(20, stats.Max);
        Assert.Equal(19, stats.P95);
    }

    [Fact]
    public void P95_with_100_samples_is_95th_value()
    {
        // Samples 1..100.
        // p95 index = ceil(100 * 0.95) - 1 = 95 - 1 = 94 (0-based).
        // sorted[94] = 95.
        var tracker = new LatencyTracker();
        for (var i = 1; i <= 100; i++) tracker.Record(i);

        var stats = tracker.Compute();
        Assert.Equal(1,   stats.Min);
        Assert.Equal(100, stats.Max);
        Assert.Equal(95,  stats.P95);
    }

    [Fact]
    public void P95_with_single_high_outlier_in_100_samples()
    {
        // 99 samples at 10ms, 1 sample at 1000ms.
        // sorted[94] = 10 (the 95th value is still 10 because only 1 out of 100 is the outlier).
        var tracker = new LatencyTracker();
        for (var i = 0; i < 99; i++) tracker.Record(10);
        tracker.Record(1000);

        var stats = tracker.Compute();
        Assert.Equal(10,   stats.Min);
        Assert.Equal(1000, stats.Max);
        Assert.Equal(10,   stats.P95);  // top 5 samples (indices 95-99) include the outlier
    }

    [Fact]
    public void Count_property_reflects_current_sample_count()
    {
        var tracker = new LatencyTracker();
        Assert.Equal(0, tracker.Count);
        tracker.Record(1);
        Assert.Equal(1, tracker.Count);
        tracker.Record(2);
        Assert.Equal(2, tracker.Count);
    }

    [Fact]
    public async Task Concurrent_recording_does_not_throw_or_lose_samples()
    {
        const int workers = 8;
        const int samplesPerWorker = 100;
        var tracker = new LatencyTracker();

        var tasks = Enumerable
            .Range(1, workers)
            .Select(_ => Task.Run(() =>
            {
                for (var j = 0; j < samplesPerWorker; j++)
                    tracker.Record(j);
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(workers * samplesPerWorker, tracker.Count);
    }
}
