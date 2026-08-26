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
    public async Task Disposition_routes_accept_optional_notes_and_reject_oversized_notes_without_mutation()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateSource(context);
        var service = new HistoricalAnomalyAdministrationService(context.Repository);
        var clearNote = new string('c', 500);
        const string confirmNote = "Confirmed after administrative review";

        Assert.Equal(StatusCodes.Status200OK, (await Invoke(
            request => HistoricalAnomalyEndpointHandler.MarkForReviewAsync(
                key.SourceType, key.SourceRecordId, request, service),
            """{"expectedRevision":0,"reason":"OtherNeedsReview"}""")).StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await Invoke(
            request => HistoricalAnomalyEndpointHandler.ClearForReportingAsync(
                key.SourceType, key.SourceRecordId, request, service),
            $"{{\"expectedRevision\":1,\"note\":\"{clearNote}\"}}")).StatusCode);

        var clearLedger = context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows;
        Assert.Equal(2, clearLedger.Count);
        Assert.Equal(HistoricalAdministrativeLedgerEventTypes.ClearedForReporting, clearLedger[1].EventType);
        Assert.Equal(clearNote, clearLedger[1].AdminNote);

        Assert.Equal(StatusCodes.Status200OK, (await Invoke(
            request => HistoricalAnomalyEndpointHandler.ReopenReviewAsync(
                key.SourceType, key.SourceRecordId, request, service),
            """{"expectedRevision":2}""")).StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await Invoke(
            request => HistoricalAnomalyEndpointHandler.ConfirmExceptionAsync(
                key.SourceType, key.SourceRecordId, request, service),
            $"{{\"expectedRevision\":3,\"note\":\"{confirmNote}\"}}")).StatusCode);

        var confirmLedger = context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows;
        Assert.Equal(4, confirmLedger.Count);
        Assert.Equal(HistoricalAdministrativeLedgerEventTypes.ConfirmedException, confirmLedger[3].EventType);
        Assert.Equal(confirmNote, confirmLedger[3].AdminNote);

        Assert.Equal(StatusCodes.Status200OK, (await Invoke(
            request => HistoricalAnomalyEndpointHandler.ReopenReviewAsync(
                key.SourceType, key.SourceRecordId, request, service),
            """{"expectedRevision":4}""")).StatusCode);
        var stateBefore = context.Repository.LoadHistoricalAdministrativeState(key);
        var ledgerBefore = context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows.ToArray();
        var oversizedNote = new string('x', 501);

        var clearRejected = await Invoke(
            request => HistoricalAnomalyEndpointHandler.ClearForReportingAsync(
                key.SourceType, key.SourceRecordId, request, service),
            $"{{\"expectedRevision\":5,\"note\":\"{oversizedNote}\"}}");
        Assert.Equal(StatusCodes.Status400BadRequest, clearRejected.StatusCode);
        Assert.Equal("invalid-note", clearRejected.Body.GetProperty("code").GetString());
        Assert.Equal(stateBefore, context.Repository.LoadHistoricalAdministrativeState(key));
        Assert.Equal(ledgerBefore, context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);

        var confirmRejected = await Invoke(
            request => HistoricalAnomalyEndpointHandler.ConfirmExceptionAsync(
                key.SourceType, key.SourceRecordId, request, service),
            $"{{\"expectedRevision\":5,\"note\":\"{oversizedNote}\"}}");
        Assert.Equal(StatusCodes.Status400BadRequest, confirmRejected.StatusCode);
        Assert.Equal("invalid-note", confirmRejected.Body.GetProperty("code").GetString());
        Assert.Equal(stateBefore, context.Repository.LoadHistoricalAdministrativeState(key));
        Assert.Equal(ledgerBefore, context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);
    }

    [Theory]
    [InlineData(false, "{\"expectedRevision\":0,\"note\":\"ok\",\"extra\":true}")]
    [InlineData(false, "{\"expectedRevision\":0,\"note\":\"first\",\"Note\":\"second\"}")]
    [InlineData(true, "{\"expectedRevision\":0,\"note\":42}")]
    [InlineData(true, "{\"note\":\"missing revision\"}")]
    public async Task Disposition_routes_preserve_strict_json_validation(bool confirm, string json)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateSource(context);
        var service = new HistoricalAnomalyAdministrationService(context.Repository);

        var response = await Invoke(
            request => confirm
                ? HistoricalAnomalyEndpointHandler.ConfirmExceptionAsync(
                    key.SourceType, key.SourceRecordId, request, service)
                : HistoricalAnomalyEndpointHandler.ClearForReportingAsync(
                    key.SourceType, key.SourceRecordId, request, service),
            json);

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal("malformed-request", response.Body.GetProperty("code").GetString());
        Assert.Null(context.Repository.LoadHistoricalAdministrativeState(key));
        Assert.Empty(context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);
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
        Assert.True(AdminAccessGuard.IsProtectedPath("/api/reports/anomalies/options"));
        Assert.True(AdminAccessGuard.IsProtectedPath("/api/reports/anomalies/CompletedCycle/1/ledger"));
        Assert.Equal(
            StatusCodes.Status401Unauthorized,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(
                AdminAccessGuard.ValidateRequest(context.Request, validator)!).StatusCode);
    }

    [Fact]
    public async Task Canonical_detail_and_bounded_ledger_are_read_only_and_typed()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateSource(context);
        var administration = new HistoricalAnomalyAdministrationService(context.Repository);
        Assert.Equal(
            HistoricalAdministrativeOperationOutcome.Success,
            administration.MarkForReview(key, 0, HistoricalManualReviewReasons.UnexpectedLifecycle).Outcome);
        Assert.Equal(
            HistoricalAdministrativeOperationOutcome.Success,
            administration.AddNote(key, 1, "Reviewed without PHI").Outcome);
        var before = JsonSerializer.Serialize(context.Repository.LoadHistoricalEncounter(key));
        var correctionService = new HistoricalMetadataCorrectionService(
            context.Repository,
            Microsoft.Extensions.Options.Options.Create(new DoctorRosterOptions
            {
                Doctors = DoctorRosterOptions.DefaultDoctors()
            }),
            Microsoft.Extensions.Options.Options.Create(new ProcedureRosterOptions
            {
                Procedures = ProcedureRosterOptions.DefaultProcedures()
            }));

        var detail = await ExecuteResult(HistoricalAnomalyReadEndpointHandler.GetDetail(
            key.SourceType,
            key.SourceRecordId,
            correctionService,
            context.Store));
        Assert.Equal(StatusCodes.Status200OK, detail.StatusCode);
        Assert.Equal(2, detail.Body.GetProperty("administrativeRevision").GetInt32());
        Assert.Equal(
            HistoricalAdministrativeDispositions.NeedsReview,
            detail.Body.GetProperty("disposition").GetString());
        Assert.True(detail.Body.TryGetProperty("originalEvidence", out _));
        Assert.True(detail.Body.TryGetProperty("effectiveMetadata", out _));
        Assert.True(detail.Body.TryGetProperty("reportingExclusionReasons", out _));

        var ledger = await ExecuteResult(HistoricalAnomalyReadEndpointHandler.GetLedger(
            key.SourceType,
            key.SourceRecordId,
            offset: 1,
            limit: 500,
            context.Repository));
        Assert.Equal(StatusCodes.Status200OK, ledger.StatusCode);
        Assert.Equal(100, ledger.Body.GetProperty("limit").GetInt32());
        Assert.Equal(1, ledger.Body.GetProperty("returnedCount").GetInt32());
        Assert.Equal(2, ledger.Body.GetProperty("totalMatchingCount").GetInt32());
        Assert.Equal(
            "Reviewed without PHI",
            ledger.Body.GetProperty("rows")[0].GetProperty("administrativeNote").GetString());

        Assert.Equal(before, JsonSerializer.Serialize(context.Repository.LoadHistoricalEncounter(key)));
        Assert.Equal(2, context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows.Count);

        var invalid = await ExecuteResult(HistoricalAnomalyReadEndpointHandler.GetLedger(
            "completed",
            key.SourceRecordId,
            0,
            50,
            context.Repository));
        Assert.Equal(StatusCodes.Status400BadRequest, invalid.StatusCode);
        Assert.Equal("invalid-source", invalid.Body.GetProperty("code").GetString());

        var missing = await ExecuteResult(HistoricalAnomalyReadEndpointHandler.GetLedger(
            HistoricalEncounterSourceTypes.CompletedCycle,
            999_999,
            0,
            50,
            context.Repository));
        Assert.Equal(StatusCodes.Status404NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Canonical_options_include_inactive_roster_entries_without_synthetic_sedation_variants()
    {
        var doctors = DoctorRosterOptions.DefaultDoctors();
        doctors.Add(new DoctorRosterItem
        {
            Id = "retired-doctor",
            DisplayName = "Dr. Retired",
            ShortName = "Retired",
            Color = "#64748b",
            Active = false
        });
        var procedures = ProcedureRosterOptions.DefaultProcedures();
        procedures.Add(new ProcedureRosterItem
        {
            Code = "OLD",
            Label = "Historical Procedure",
            Icon = "history",
            Active = false,
            SedationEligible = true
        });
        procedures.Add(new ProcedureRosterItem
        {
            Code = "OLD+SED",
            Label = "Historical Procedure + Sedation",
            Icon = "history",
            Active = false,
            SedationEligible = true
        });

        var response = await ExecuteResult(HistoricalAnomalyReadEndpointHandler.GetOptions(
            Microsoft.Extensions.Options.Options.Create(new DoctorRosterOptions { Doctors = doctors }),
            Microsoft.Extensions.Options.Options.Create(new ProcedureRosterOptions { Procedures = procedures })));

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Contains(
            response.Body.GetProperty("doctors").EnumerateArray(),
            item => item.GetProperty("id").GetString() == "retired-doctor"
                && !item.GetProperty("active").GetBoolean());
        Assert.Contains(
            response.Body.GetProperty("procedures").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "OLD"
                && !item.GetProperty("active").GetBoolean());
        Assert.DoesNotContain(
            response.Body.GetProperty("procedures").EnumerateArray(),
            item => item.GetProperty("code").GetString()!.Contains("+SED", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(500, response.Body.GetProperty("noteMaximumLength").GetInt32());
        Assert.Equal(5, response.Body.GetProperty("reasons").GetArrayLength());
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
        Assert.Contains("/api/reports/anomalies/options", program, StringComparison.Ordinal);
        Assert.Contains("/api/reports/anomalies/{sourceType}/{sourceRecordId:long}/detail", program, StringComparison.Ordinal);
        Assert.Contains("/api/reports/anomalies/{sourceType}/{sourceRecordId:long}/ledger", program, StringComparison.Ordinal);
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

    private static async Task<(int? StatusCode, JsonElement Body)> ExecuteResult(IResult result)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
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
