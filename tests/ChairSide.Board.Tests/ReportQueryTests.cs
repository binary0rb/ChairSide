using ChairSide.Board.Services;

namespace ChairSide.Board.Tests;

public sealed class ReportQueryTests
{
    [Theory]
    [InlineData(0, 0, ReportSampleStates.Empty, false)]
    [InlineData(3, 0, ReportSampleStates.Unavailable, false)]
    [InlineData(4, 4, ReportSampleStates.Limited, false)]
    [InlineData(5, 5, ReportSampleStates.Sufficient, true)]
    public void Sample_context_uses_the_approved_general_descriptive_guardrail(
        int populationCount,
        int contributingCount,
        string expectedState,
        bool expectedComparisonSupport)
    {
        var sample = ReportSampleContext.Create(populationCount, contributingCount);

        Assert.Equal(expectedState, sample.State);
        Assert.Equal(5, sample.LimitedSampleThreshold);
        Assert.Equal(expectedComparisonSupport, sample.SupportsComparison);
    }

    [Fact]
    public void Query_normalizes_reversed_valid_dates_and_preserves_graceful_malformed_dates()
    {
        var reversed = ReportQuery.FromStrings(
            "2026-08-12",
            "2026-08-10",
            "doctor",
            " former-doctor ",
            "sedation",
            "detailedvariant");
        var malformed = ReportQuery.FromStrings(
            "not-a-date",
            "also-not-a-date",
            "unknown",
            null,
            "unknown",
            "unknown");

        Assert.Equal("2026-08-10", reversed.Window.StartDateText);
        Assert.Equal("2026-08-12", reversed.Window.EndDateText);
        Assert.Equal(ReportScopeKinds.Doctor, reversed.Scope);
        Assert.Equal("former-doctor", reversed.DoctorId);
        Assert.Equal(ReportSedationSegments.Sedation, reversed.Sedation);
        Assert.Equal(ReportProcedureGroupings.DetailedVariant, reversed.ProcedureGrouping);

        Assert.Null(malformed.Window.StartDateText);
        Assert.Null(malformed.Window.EndDateText);
        Assert.Equal(ReportScopeKinds.Practice, malformed.Scope);
        Assert.Equal(ReportSedationSegments.All, malformed.Sedation);
        Assert.Equal(ReportProcedureGroupings.Family, malformed.ProcedureGrouping);
    }
}
