using ChairSide.LoadLab;

namespace ChairSide.LoadLab.Tests;

public sealed class MetricsCollectorTests
{
    // -- board vs. reports routing --

    [Fact]
    public void Board_url_is_routed_to_board_latency_tracker()
    {
        var m = new MetricsCollector();
        m.RecordPoll("http://localhost:5000/api/board", 200, 50, isException: false);
        m.RecordPoll("http://localhost:5000/api/board", 200, 80, isException: false);

        var s = m.GetSummary();
        Assert.Equal(2, s.BoardLatency.Count);
        Assert.Equal(0, s.ReportsLatency.Count);
    }

    [Fact]
    public void Reports_url_is_routed_to_reports_latency_tracker()
    {
        var m = new MetricsCollector();
        m.RecordPoll("http://localhost:5000/api/reports", 200, 75, isException: false);

        var s = m.GetSummary();
        Assert.Equal(1, s.ReportsLatency.Count);
        Assert.Equal(0, s.BoardLatency.Count);
    }

    // -- total / failed request counting --

    [Fact]
    public void Successful_polls_increment_total_only()
    {
        var m = new MetricsCollector();
        m.RecordPoll("/api/board", 200, 10, isException: false);
        m.RecordPoll("/api/board", 200, 20, isException: false);

        Assert.Equal(2, m.TotalRequests);
        Assert.Equal(0, m.FailedRequests);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    [InlineData(503)]
    public void Error_status_codes_increment_failed_requests(int statusCode)
    {
        var m = new MetricsCollector();
        m.RecordPoll("/api/board", statusCode, 30, isException: false);

        Assert.Equal(1, m.TotalRequests);
        Assert.Equal(1, m.FailedRequests);
    }

    [Fact]
    public void Exception_flag_increments_failed_and_exception_counts()
    {
        var m = new MetricsCollector();
        m.RecordPoll("/api/board", 0, 0, isException: true);

        var s = m.GetSummary();
        Assert.Equal(1, s.TotalRequests);
        Assert.Equal(1, s.FailedRequests);
        Assert.Equal(1, s.ExceptionCount);
    }

    // -- mutation tracking --

    [Fact]
    public void Successful_mutation_increments_total_and_attempted_only()
    {
        var m = new MetricsCollector();
        m.RecordMutation(50, success: true);

        Assert.Equal(1, m.TotalRequests);
        Assert.Equal(1, m.MutationsAttempted);
        Assert.Equal(0, m.MutationsFailed);
        Assert.Equal(0, m.FailedRequests);
    }

    [Fact]
    public void Failed_mutation_increments_failed_requests_and_mutations_failed()
    {
        var m = new MetricsCollector();
        m.RecordMutation(50, success: false);

        Assert.Equal(1, m.TotalRequests);
        Assert.Equal(1, m.MutationsAttempted);
        Assert.Equal(1, m.MutationsFailed);
        Assert.Equal(1, m.FailedRequests);
    }

    [Fact]
    public void Mutation_latency_is_recorded_in_mutations_tracker()
    {
        var m = new MetricsCollector();
        m.RecordMutation(100, success: true);
        m.RecordMutation(200, success: false);

        var s = m.GetSummary();
        Assert.Equal(2, s.MutationsLatency.Count);
        Assert.Equal(0, s.BoardLatency.Count);
    }

    // -- status code aggregation --

    [Fact]
    public void Status_codes_are_grouped_and_counted()
    {
        var m = new MetricsCollector();
        m.RecordPoll("/api/board", 200, 10, false);
        m.RecordPoll("/api/board", 200, 20, false);
        m.RecordPoll("/api/board", 404, 5,  false);

        var s = m.GetSummary();
        Assert.Equal(2, s.StatusCodes[200]);
        Assert.Equal(1, s.StatusCodes[404]);
    }

    [Fact]
    public void Summary_status_codes_dictionary_is_a_snapshot_not_a_live_reference()
    {
        var m = new MetricsCollector();
        m.RecordPoll("/api/board", 200, 10, false);

        var s1 = m.GetSummary();
        m.RecordPoll("/api/board", 200, 20, false);
        var s2 = m.GetSummary();

        // The first snapshot should still show 1 (not 2).
        Assert.Equal(1, s1.StatusCodes[200]);
        Assert.Equal(2, s2.StatusCodes[200]);
    }

    // -- summary correctness with mixed activity --

    [Fact]
    public void Summary_aggregates_polls_and_mutations_together()
    {
        var m = new MetricsCollector();

        m.RecordPoll("/api/board",   200, 30, false);
        m.RecordPoll("/api/reports", 200, 90, false);
        m.RecordPoll("/api/board",   500, 10, false);  // failed
        m.RecordMutation(50, success: true);
        m.RecordMutation(70, success: false);           // failed mutation

        var s = m.GetSummary();
        Assert.Equal(5, s.TotalRequests);
        Assert.Equal(2, s.FailedRequests);  // one 500 poll + one failed mutation
        Assert.Equal(2, s.MutationsAttempted);
        Assert.Equal(1, s.MutationsFailed);
        Assert.Equal(2, s.BoardLatency.Count);
        Assert.Equal(1, s.ReportsLatency.Count);
        Assert.Equal(2, s.MutationsLatency.Count);
    }
}
