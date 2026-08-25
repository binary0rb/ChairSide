using System.Text;
using System.Text.Json;

using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class HistoricalMetadataCorrectionEndpointTests
{
    [Fact]
    public async Task Strict_correction_routes_return_the_canonical_effective_projection()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateCompletedSource(context, "EXT");
        var service = CreateService(context);
        MarkForReview(context, key);

        var paired = await Invoke(
            request => HistoricalMetadataCorrectionEndpointHandler.CorrectProcedureAndSedationAsync(
                key.SourceType,
                key.SourceRecordId,
                request,
                service),
            """{"expectedRevision":1,"procedureCode":"CON","sedationState":"UnavailableProcedureIneligible","note":"paired"}""");
        Assert.Equal(StatusCodes.Status200OK, paired.StatusCode);
        Assert.Equal(2, paired.Body.GetProperty("administrativeRevision").GetInt32());
        Assert.Equal("CON", paired.Body.GetProperty("effectiveMetadata").GetProperty("procedureCode").GetString());
        Assert.Equal(
            "UnavailableProcedureIneligible",
            paired.Body.GetProperty("effectiveMetadata").GetProperty("sedationState").GetString());
        Assert.True(paired.Body.GetProperty("correctionIndicators").GetProperty("procedure").GetBoolean());
        Assert.True(paired.Body.GetProperty("correctionIndicators").GetProperty("sedation").GetBoolean());

        var doctor = await Invoke(
            request => HistoricalMetadataCorrectionEndpointHandler.CorrectDoctorAsync(
                key.SourceType,
                key.SourceRecordId,
                request,
                service),
            """{"expectedRevision":2,"doctorId":"historical-doctor"}""");
        Assert.Equal(StatusCodes.Status200OK, doctor.StatusCode);
        Assert.Equal("historical-doctor", doctor.Body.GetProperty("effectiveMetadata").GetProperty("doctorId").GetString());

        var addOn = await Invoke(
            request => HistoricalMetadataCorrectionEndpointHandler.CorrectAddOnAsync(
                key.SourceType,
                key.SourceRecordId,
                request,
                service),
            """{"expectedRevision":3,"isAddOn":true}""");
        Assert.Equal(StatusCodes.Status200OK, addOn.StatusCode);

        var allocation = await Invoke(
            request => HistoricalMetadataCorrectionEndpointHandler.CorrectExpectedAllocationAsync(
                key.SourceType,
                key.SourceRecordId,
                request,
                service),
            """{"expectedRevision":4,"expectedAllocation":{"state":"ConfirmedAdjustedValue","suggestedUnits":3,"confirmedUnits":4}}""");
        Assert.Equal(StatusCodes.Status200OK, allocation.StatusCode);
        Assert.Equal(
            4,
            allocation.Body.GetProperty("effectiveMetadata")
                .GetProperty("expectedAllocation")
                .GetProperty("confirmedValue")
                .GetInt32());

        var get = await ExecuteResult(HistoricalMetadataCorrectionEndpointHandler.GetEffectiveEncounter(
            key.SourceType,
            key.SourceRecordId,
            service));
        Assert.Equal(StatusCodes.Status200OK, get.StatusCode);
        Assert.Equal("AcceptedReadyHandoff", get.Body.GetProperty("originalEvidence").GetProperty("authority").GetString());
        Assert.Equal("EXT", get.Body.GetProperty("originalEvidence").GetProperty("metadata").GetProperty("procedureCode").GetString());
        Assert.Equal("CON", get.Body.GetProperty("effectiveMetadata").GetProperty("procedureCode").GetString());
        Assert.Equal(5, get.Body.GetProperty("administrativeRevision").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"expectedRevision\":1,\"procedureCode\":\"CON\"}")]
    [InlineData("{\"expectedRevision\":1,\"procedureCode\":\"CON\",\"sedationState\":\"EligibleNo\",\"extra\":true}")]
    [InlineData("{\"expectedRevision\":1,\"procedureCode\":\"CON\",\"sedationState\":\"NotCanonical\"}")]
    [InlineData("{\"expectedRevision\":1,\"procedureCode\":\"CON\",\"sedationState\":\"1\"}")]
    [InlineData("{\"expectedRevision\":1,\"ExpectedRevision\":1,\"procedureCode\":\"CON\",\"sedationState\":\"UnavailableProcedureIneligible\"}")]
    public async Task Paired_route_rejects_missing_unknown_duplicate_or_noncanonical_values(string json)
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateCompletedSource(context, "EXT");
        var service = CreateService(context);
        MarkForReview(context, key);

        var response = await Invoke(
            request => HistoricalMetadataCorrectionEndpointHandler.CorrectProcedureAndSedationAsync(
                key.SourceType,
                key.SourceRecordId,
                request,
                service),
            json);

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal("malformed-request", response.Body.GetProperty("code").GetString());
        Assert.Equal(1, context.Repository.LoadHistoricalAdministrativeState(key)!.AdministrativeRevision);
        Assert.Single(context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows);
    }

    [Fact]
    public async Task Endpoint_maps_roster_gate_stale_note_and_allocation_failures_without_mutation()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, Environments.Production);
        var key = CreateCompletedSource(context, "EXT");
        var service = CreateService(context);

        var noReview = await Invoke(
            request => HistoricalMetadataCorrectionEndpointHandler.CorrectDoctorAsync(
                key.SourceType,
                key.SourceRecordId,
                request,
                service),
            """{"expectedRevision":0,"doctorId":"pledger"}""");
        Assert.Equal(StatusCodes.Status409Conflict, noReview.StatusCode);
        Assert.Equal("review-not-pending", noReview.Body.GetProperty("code").GetString());

        MarkForReview(context, key);
        var unknown = await Invoke(
            request => HistoricalMetadataCorrectionEndpointHandler.CorrectDoctorAsync(
                key.SourceType,
                key.SourceRecordId,
                request,
                service),
            """{"expectedRevision":1,"doctorId":"unknown"}""");
        Assert.Equal(StatusCodes.Status400BadRequest, unknown.StatusCode);
        Assert.Equal("invalid-doctor", unknown.Body.GetProperty("code").GetString());

        var invalidAllocation = await Invoke(
            request => HistoricalMetadataCorrectionEndpointHandler.CorrectExpectedAllocationAsync(
                key.SourceType,
                key.SourceRecordId,
                request,
                service),
            """{"expectedRevision":1,"expectedAllocation":{"state":"ConfirmedSuggestedValue","suggestedUnits":3,"confirmedUnits":4}}""");
        Assert.Equal(StatusCodes.Status400BadRequest, invalidAllocation.StatusCode);
        Assert.Equal("invalid-expected-allocation", invalidAllocation.Body.GetProperty("code").GetString());

        var oversized = await Invoke(
            request => HistoricalMetadataCorrectionEndpointHandler.CorrectAddOnAsync(
                key.SourceType,
                key.SourceRecordId,
                request,
                service),
            $"{{\"expectedRevision\":1,\"isAddOn\":true,\"note\":\"{new string('n', 501)}\"}}");
        Assert.Equal(StatusCodes.Status400BadRequest, oversized.StatusCode);
        Assert.Equal("invalid-note", oversized.Body.GetProperty("code").GetString());

        var success = await Invoke(
            request => HistoricalMetadataCorrectionEndpointHandler.CorrectAddOnAsync(
                key.SourceType,
                key.SourceRecordId,
                request,
                service),
            """{"expectedRevision":1,"isAddOn":true}""");
        Assert.Equal(StatusCodes.Status200OK, success.StatusCode);
        var stale = await Invoke(
            request => HistoricalMetadataCorrectionEndpointHandler.CorrectAddOnAsync(
                key.SourceType,
                key.SourceRecordId,
                request,
                service),
            """{"expectedRevision":1,"isAddOn":false}""");
        Assert.Equal(StatusCodes.Status409Conflict, stale.StatusCode);
        Assert.Equal("stale-write", stale.Body.GetProperty("code").GetString());
        Assert.Equal(2, context.Repository.LoadHistoricalAdministrativeState(key)!.AdministrativeRevision);
        Assert.Equal(2, context.Repository.LoadHistoricalAdministrativeLedger(key, 0, 10).Rows.Count);
    }

    [Fact]
    public void Program_maps_only_closed_admin_protected_correction_operations()
    {
        var program = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ChairSide.Board",
            "Program.cs"));
        var routes = new[]
        {
            "effective-encounter",
            "correct-doctor",
            "correct-procedure",
            "correct-procedure-and-sedation",
            "correct-sedation",
            "correct-add-on",
            "correct-expected-allocation"
        };
        Assert.All(routes, route => Assert.Contains(
            $"/api/reports/anomalies/{{sourceType}}/{{sourceRecordId:long}}/{route}",
            program,
            StringComparison.Ordinal));
        Assert.DoesNotContain("fieldName", program, StringComparison.OrdinalIgnoreCase);
        Assert.True(AdminAccessGuard.IsProtectedPath(
            "/api/reports/anomalies/CompletedCycle/1/correct-procedure-and-sedation"));
    }

    private static HistoricalMetadataCorrectionService CreateService(StoreContext context)
    {
        var doctors = DoctorRosterOptions.DefaultDoctors();
        doctors.Add(new DoctorRosterItem
        {
            Id = "historical-doctor",
            DisplayName = "Dr. Historical",
            ShortName = "Historical",
            Color = "#64748b",
            Active = false
        });
        return new HistoricalMetadataCorrectionService(
            context.Repository,
            Microsoft.Extensions.Options.Options.Create(new DoctorRosterOptions { Doctors = doctors }),
            Microsoft.Extensions.Options.Options.Create(new ProcedureRosterOptions
            {
                Procedures = ProcedureRosterOptions.DefaultProcedures()
            }));
    }

    private static HistoricalEncounterKey CreateCompletedSource(StoreContext context, string procedureCode)
    {
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", procedureCode));
        Assert.NotNull(context.Store.SeatRoomCanonical(1, null).Room);
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        var cycle = Assert.Single(context.Repository.LoadCompletedCycles());
        return new HistoricalEncounterKey(HistoricalEncounterSourceTypes.CompletedCycle, cycle.CompletedCycleId);
    }

    private static void MarkForReview(StoreContext context, HistoricalEncounterKey key)
    {
        var result = new HistoricalAnomalyAdministrationService(context.Repository).MarkForReview(
            key,
            0,
            HistoricalManualReviewReasons.IncorrectProcedure);
        Assert.Equal(HistoricalAdministrativeOperationOutcome.Success, result.Outcome);
    }

    private static async Task<(int? StatusCode, JsonElement Body)> Invoke(
        Func<HttpContext, Task<IResult>> invoke,
        string json)
    {
        var context = NewHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await ExecuteResult(await invoke(context), context);
    }

    private static Task<(int? StatusCode, JsonElement Body)> ExecuteResult(IResult result) =>
        ExecuteResult(result, NewHttpContext());

    private static async Task<(int? StatusCode, JsonElement Body)> ExecuteResult(
        IResult result,
        HttpContext context)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (status, document.RootElement.Clone());
    }

    private static DefaultHttpContext NewHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        return context;
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
