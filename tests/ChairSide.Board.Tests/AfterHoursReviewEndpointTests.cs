using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class AfterHoursReviewEndpointTests
{
    [Fact]
    public void Aborted_and_completed_confirm_exclusion_routes_share_admin_protection()
    {
        const string completedPath = "/api/reports/cycles/1/confirm-exclusion";
        const string abortedPath = "/api/reports/aborted-assignments/1/confirm-exclusion";
        var validator = new AdminAccessTokenValidator(new TestOptionsMonitor<AdminAccessOptions>(new AdminAccessOptions
        {
            Enabled = true,
            SharedToken = "admin-token"
        }));

        Assert.True(AdminAccessGuard.IsProtectedPath(completedPath));
        Assert.True(AdminAccessGuard.IsProtectedPath(abortedPath));

        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            StatusCode(AdminAccessGuard.ValidateRequest(AdminRequest(abortedPath, token: null).Request, validator)!));
        Assert.Equal(
            StatusCodes.Status403Forbidden,
            StatusCode(AdminAccessGuard.ValidateRequest(AdminRequest(abortedPath, "wrong-token").Request, validator)!));
        Assert.Null(AdminAccessGuard.ValidateRequest(AdminRequest(abortedPath, "admin-token").Request, validator));
    }

    [Fact]
    public async Task Aborted_confirm_exclusion_is_durable_idempotent_and_source_disambiguated()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 16, 23, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: SweepOptions());

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        ActivateReady(context.Store, 2, "pledger", "EXT");
        Assert.NotNull(context.Store.MarkDoctorArrived(2));
        Assert.Equal([1, 2], context.Store.TryRunAfterHoursSweep());

        var pending = Assert.IsAssignableFrom<IReadOnlyList<ExceptionReviewRecord>>(
            context.Store.GetReports().ExceptionReviewRecords);
        var aborted = pending.Single(record => record.SourceType == ExceptionReviewSources.AbortedAssignment);
        var completed = pending.Single(record => record.SourceType == ExceptionReviewSources.CompletedCycle);

        // SQLite identities are table-local and can collide. SourceType plus the source-specific id
        // keeps routing explicit, so reviewing aborted id 1 cannot review completed-cycle id 1.
        Assert.Equal(aborted.ReviewRecordId, completed.ReviewRecordId);
        Assert.Equal(aborted.AbortedAssignmentId, aborted.ReviewRecordId);
        Assert.Equal(0, aborted.CompletedCycleId);
        Assert.Equal(completed.CompletedCycleId, completed.ReviewRecordId);
        Assert.Equal(0, completed.AbortedAssignmentId);

        var logger = CreateDiagnosticLogger(workspace);
        var result = await global::ExceptionReviewEndpointHandler.ConfirmAbortedAssignmentExclusionAsync(
            aborted.AbortedAssignmentId,
            context.Store,
            logger,
            new NoopBoardHubContext());

        Assert.Equal(StatusCodes.Status204NoContent, StatusCode(result));
        var durable = context.Repository.LoadAbortedAssignments()
            .Single(record => record.AbortedAssignmentId == aborted.AbortedAssignmentId);
        Assert.True(durable.IsException);
        Assert.False(durable.RequiresReview);
        Assert.Equal(ReviewStatuses.Reviewed, durable.ReviewStatus);
        Assert.Equal(now, durable.ReviewedAt);
        Assert.Equal(ExceptionReviewers.LocalAdmin, durable.ReviewedBy);

        var afterReview = context.Store.GetReports().ExceptionReviewRecords!;
        Assert.DoesNotContain(afterReview, record =>
            record.SourceType == ExceptionReviewSources.AbortedAssignment
            && record.ReviewRecordId == aborted.ReviewRecordId);
        Assert.Contains(afterReview, record =>
            record.SourceType == ExceptionReviewSources.CompletedCycle
            && record.ReviewRecordId == completed.ReviewRecordId);

        clock.SetUtcNow(now.AddMinutes(1));
        var repeated = await global::ExceptionReviewEndpointHandler.ConfirmAbortedAssignmentExclusionAsync(
            aborted.AbortedAssignmentId,
            context.Store,
            logger,
            new NoopBoardHubContext());
        Assert.Equal(StatusCodes.Status204NoContent, StatusCode(repeated));
        var repeatedDurable = context.Repository.LoadAbortedAssignments()
            .Single(record => record.AbortedAssignmentId == aborted.AbortedAssignmentId);
        Assert.Equal(now.AddMinutes(1), repeatedDurable.ReviewedAt);
        Assert.False(repeatedDurable.RequiresReview);

        Assert.NotNull(context.Store.BeginPrestage(3, "gibson", "CON"));
        Assert.NotNull(context.Store.CancelPrestage(3));
        var ordinaryAbort = context.Repository.LoadAbortedAssignments()
            .Single(record => record.RoomId == 3);
        Assert.False(ordinaryAbort.IsException);

        var rejected = await global::ExceptionReviewEndpointHandler.ConfirmAbortedAssignmentExclusionAsync(
            ordinaryAbort.AbortedAssignmentId,
            context.Store,
            logger,
            new NoopBoardHubContext());
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCode(rejected));

        var missing = await global::ExceptionReviewEndpointHandler.ConfirmAbortedAssignmentExclusionAsync(
            999_999,
            context.Store,
            logger,
            new NoopBoardHubContext());
        Assert.Equal(StatusCodes.Status404NotFound, StatusCode(missing));
    }

    [Fact]
    public void Review_ui_routes_numeric_ids_by_explicit_source_type()
    {
        var reportsJs = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ChairSide.Board",
            "wwwroot",
            "reports.js"));

        Assert.Contains("data-review-source", reportsJs, StringComparison.Ordinal);
        Assert.Contains("sourceType === \"AbortedAssignment\"", reportsJs, StringComparison.Ordinal);
        Assert.Contains("aborted-assignments/${reviewRecordId}", reportsJs, StringComparison.Ordinal);
        Assert.Contains("cycles/${reviewRecordId}", reportsJs, StringComparison.Ordinal);
    }

    private static RoomExpirationOptions SweepOptions() =>
        new()
        {
            Enabled = true,
            AfterHoursSweepEnabled = true,
            AfterHoursSweepTime = "23:00",
            TimeZone = "UTC"
        };

    private static void ActivateReady(DemoBoardStore store, int roomId, string doctorId, string procedureCode)
    {
        Assert.NotNull(store.BeginPrestage(roomId, doctorId, procedureCode));
        Assert.NotNull(store.SeatRoomCanonical(roomId, null).Room);
        Assert.NotNull(store.MarkReadyForDoctor(roomId));
    }

    private static DiagnosticLogger CreateDiagnosticLogger(TestWorkspace workspace) =>
        new(
            Microsoft.Extensions.Options.Options.Create(new DiagnosticOptions
            {
                LogDirectory = Path.Combine(workspace.DataRoot, "logs")
            }),
            new TestWebHostEnvironment(workspace.ContentRoot, Environments.Production));

    private static DefaultHttpContext AdminRequest(string path, string? token)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (token is not null)
        {
            context.Request.Headers[AdminAccessTokenValidator.HeaderName] = token;
        }

        return context;
    }

    private static int? StatusCode(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ChairSide.Board.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ChairSide repository root.");
    }
}
