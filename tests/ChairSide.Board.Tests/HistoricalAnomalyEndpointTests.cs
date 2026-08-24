using System.Text;
using System.Text.Json;

using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class HistoricalAnomalyEndpointTests
{
    [Fact]
    public async Task Canonical_routes_expose_full_transition_flow_and_stale_revision_conflicts()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateSource(context);
        var service = new HistoricalAnomalyAdministrationService(context.Repository);

        var marked = await Invoke(
            request => HistoricalAnomalyEndpointHandler.MarkForReviewAsync(
                key.SourceType, key.SourceRecordId, request, service),
            """{"expectedRevision":0,"reason":"IncorrectDoctor"}""");
        Assert.Equal(StatusCodes.Status200OK, marked.StatusCode);
        Assert.Equal(1, marked.Body.GetProperty("administrativeRevision").GetInt32());
        Assert.Equal(HistoricalAdministrativeDispositions.NeedsReview, marked.Body.GetProperty("disposition").GetString());

        var stale = await Invoke(
            request => HistoricalAnomalyEndpointHandler.RefineReasonAsync(
                key.SourceType, key.SourceRecordId, request, service),
            """{"expectedRevision":0,"reason":"IncorrectProcedure"}""");
        Assert.Equal(StatusCodes.Status409Conflict, stale.StatusCode);
        Assert.Equal("stale-write", stale.Body.GetProperty("code").GetString());
        Assert.Equal(1, stale.Body.GetProperty("currentRevision").GetInt32());

        Assert.Equal(StatusCodes.Status200OK, (await Invoke(
            request => HistoricalAnomalyEndpointHandler.RefineReasonAsync(
                key.SourceType, key.SourceRecordId, request, service),
            """{"expectedRevision":1,"reason":"IncorrectProcedure"}""")).StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await Invoke(
            request => HistoricalAnomalyEndpointHandler.AddNoteAsync(
                key.SourceType, key.SourceRecordId, request, service),
            $"{{\"expectedRevision\":2,\"note\":\"{new string('n', 500)}\"}}")).StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await Invoke(
            request => HistoricalAnomalyEndpointHandler.ClearForReportingAsync(
                key.SourceType, key.SourceRecordId, request, service),
            """{"expectedRevision":3}""")).StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await Invoke(
            request => HistoricalAnomalyEndpointHandler.ReopenReviewAsync(
                key.SourceType, key.SourceRecordId, request, service),
            """{"expectedRevision":4}""")).StatusCode);
        var confirmed = await Invoke(
            request => HistoricalAnomalyEndpointHandler.ConfirmExceptionAsync(
                key.SourceType, key.SourceRecordId, request, service),
            """{"expectedRevision":5}""");
        Assert.Equal(StatusCodes.Status200OK, confirmed.StatusCode);
        Assert.Equal(6, confirmed.Body.GetProperty("administrativeRevision").GetInt32());
        Assert.Equal(HistoricalAdministrativeDispositions.ConfirmedException, confirmed.Body.GetProperty("disposition").GetString());

        var invalidTransition = await Invoke(
            request => HistoricalAnomalyEndpointHandler.ConfirmExceptionAsync(
                key.SourceType, key.SourceRecordId, request, service),
            """{"expectedRevision":6}""");
        Assert.Equal(StatusCodes.Status409Conflict, invalidTransition.StatusCode);
        Assert.Equal("invalid-transition", invalidTransition.Body.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("", "malformed-request")]
    [InlineData("   ", "malformed-request")]
    [InlineData("{}", "malformed-request")]
    [InlineData("{\"expectedRevision\":0,\"reason\":\"IncorrectDoctor\",\"extra\":true}", "malformed-request")]
    [InlineData("{\"expectedRevision\":0,\"ExpectedRevision\":0,\"reason\":\"IncorrectDoctor\"}", "malformed-request")]
    [InlineData("{\"expectedRevision\":-1,\"reason\":\"IncorrectDoctor\"}", "malformed-request")]
    [InlineData("{\"expectedRevision\":0,\"reason\":\"AfterHoursSweep\"}", "invalid-reason")]
    [InlineData("{\"expectedRevision\":0,\"reason\":\"ExtremeDuration\"}", "invalid-reason")]
    public async Task Strict_mark_request_rejects_malformed_or_disallowed_inputs(string json, string code)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateSource(context);
        var service = new HistoricalAnomalyAdministrationService(context.Repository);

        var response = await Invoke(
            request => HistoricalAnomalyEndpointHandler.MarkForReviewAsync(
                key.SourceType, key.SourceRecordId, request, service),
            json);

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal(code, response.Body.GetProperty("code").GetString());
        Assert.Null(context.Repository.LoadHistoricalAdministrativeState(key));
    }

    [Fact]
    public async Task Endpoint_maps_invalid_source_missing_source_and_note_length_without_mutation()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateSource(context);
        var service = new HistoricalAnomalyAdministrationService(context.Repository);

        var invalidSource = await Invoke(
            request => HistoricalAnomalyEndpointHandler.MarkForReviewAsync(
                "completed", key.SourceRecordId, request, service),
            """{"expectedRevision":0,"reason":"IncorrectDoctor"}""");
        Assert.Equal(StatusCodes.Status400BadRequest, invalidSource.StatusCode);
        Assert.Equal("invalid-source", invalidSource.Body.GetProperty("code").GetString());

        var missing = await Invoke(
            request => HistoricalAnomalyEndpointHandler.MarkForReviewAsync(
                HistoricalEncounterSourceTypes.CompletedCycle, 999_999, request, service),
            """{"expectedRevision":0,"reason":"IncorrectDoctor"}""");
        Assert.Equal(StatusCodes.Status404NotFound, missing.StatusCode);

        Assert.Equal(StatusCodes.Status200OK, (await Invoke(
            request => HistoricalAnomalyEndpointHandler.MarkForReviewAsync(
                key.SourceType, key.SourceRecordId, request, service),
            """{"expectedRevision":0,"reason":"OtherNeedsReview"}""")).StatusCode);
        var tooLong = await Invoke(
            request => HistoricalAnomalyEndpointHandler.AddNoteAsync(
                key.SourceType, key.SourceRecordId, request, service),
            $"{{\"expectedRevision\":1,\"note\":\"{new string('x', 501)}\"}}");
        Assert.Equal(StatusCodes.Status400BadRequest, tooLong.StatusCode);
        Assert.Equal("invalid-note", tooLong.Body.GetProperty("code").GetString());
        Assert.Equal(1, context.Repository.LoadHistoricalAdministrativeState(key)!.AdministrativeRevision);
        Assert.Single(context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);
    }

    [Fact]
    public void Canonical_anomaly_routes_are_admin_protected()
    {
        var validator = new AdminAccessTokenValidator(new TestOptionsMonitor<AdminAccessOptions>(new AdminAccessOptions
        {
            Enabled = true,
            SharedToken = "admin-token"
        }));
        const string path = "/api/reports/anomalies/CompletedCycle/1/mark-for-review";
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        Assert.True(AdminAccessGuard.IsProtectedPath(path));
        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(
                AdminAccessGuard.ValidateRequest(context.Request, validator)!).StatusCode);
    }

    [Fact]
    public void Program_maps_only_local_admin_anomaly_mutations_and_no_public_system_finding_route()
    {
        var program = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ChairSide.Board",
            "Program.cs"));
        Assert.Contains("/api/reports/anomalies/{sourceType}/{sourceRecordId:long}/mark-for-review", program, StringComparison.Ordinal);
        Assert.Contains("/api/reports/anomalies/{sourceType}/{sourceRecordId:long}/refine-reason", program, StringComparison.Ordinal);
        Assert.Contains("/api/reports/anomalies/{sourceType}/{sourceRecordId:long}/note", program, StringComparison.Ordinal);
        Assert.Contains("/api/reports/anomalies/{sourceType}/{sourceRecordId:long}/clear", program, StringComparison.Ordinal);
        Assert.Contains("/api/reports/anomalies/{sourceType}/{sourceRecordId:long}/confirm", program, StringComparison.Ordinal);
        Assert.Contains("/api/reports/anomalies/{sourceType}/{sourceRecordId:long}/reopen", program, StringComparison.Ordinal);
        Assert.DoesNotContain("system-finding", program, StringComparison.OrdinalIgnoreCase);
    }

    private static HistoricalEncounterKey CreateSource(StoreContext context)
    {
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        Assert.NotNull(context.Store.CancelPrestage(1));
        var source = Assert.Single(context.Repository.LoadAbortedAssignments());
        return new HistoricalEncounterKey(HistoricalEncounterSourceTypes.AbortedAssignment, source.AbortedAssignmentId);
    }

    private static async Task<(int? StatusCode, JsonElement Body)> Invoke(
        Func<HttpContext, Task<IResult>> invoke,
        string json)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Response.Body = new MemoryStream();
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.RequestServices = services;
        var result = await invoke(context);
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (status, document.RootElement.Clone());
    }

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
