using ChairSide.Board.Options;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Services;

public static class HistoricalAnomalyReadEndpointHandler
{
    private const int DefaultLedgerLimit = 50;
    private const int MaximumLedgerLimit = 100;

    public static IResult GetDetail(
        string sourceType,
        long sourceRecordId,
        HistoricalMetadataCorrectionService correctionService,
        DemoBoardStore store)
    {
        var key = new HistoricalEncounterKey(sourceType, sourceRecordId);
        if (!key.IsValid)
        {
            return InvalidSource();
        }

        var encounter = correctionService.GetEffectiveEncounter(key);
        return encounter is null
            ? MissingSource()
            : Results.Ok(HistoricalMetadataCorrectionEndpointHandler.ToResponse(
                encounter,
                store.GetHistoricalReportingExclusionReasons(key)));
    }

    public static IResult GetLedger(
        string sourceType,
        long sourceRecordId,
        int? offset,
        int? limit,
        SqliteBoardRepository repository)
    {
        var key = new HistoricalEncounterKey(sourceType, sourceRecordId);
        if (!key.IsValid)
        {
            return InvalidSource();
        }
        if (offset is < 0 || limit is <= 0)
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "invalid-page",
                "Offset must be non-negative and limit must be positive.");
        }
        if (repository.LoadHistoricalEncounter(key) is null)
        {
            return MissingSource();
        }

        var normalizedOffset = offset ?? 0;
        var normalizedLimit = Math.Min(limit ?? DefaultLedgerLimit, MaximumLedgerLimit);
        var page = repository.LoadHistoricalAdministrativeLedger(key, normalizedOffset, normalizedLimit);
        return Results.Ok(new
        {
            sourceType = key.SourceType,
            sourceRecordId = key.SourceRecordId,
            rows = page.Rows.Select(row => new
            {
                ledgerId = row.LedgerId,
                eventType = row.EventType,
                occurredAt = row.OccurredAt,
                actorClass = row.ActorClass,
                reasonSource = row.ReasonSource,
                structuredReason = row.StructuredReason,
                previousValue = row.PreviousValue,
                newValue = row.NewValue,
                administrativeNote = row.AdminNote,
                administrativeRevision = row.AdministrativeRevision
            }),
            offset = page.Offset,
            limit = page.Limit,
            returnedCount = page.Rows.Count,
            totalMatchingCount = page.TotalMatchingCount,
            hasMore = page.HasMore
        });
    }

    public static IResult GetOptions(
        IOptions<DoctorRosterOptions> doctorOptions,
        IOptions<ProcedureRosterOptions> procedureOptions) =>
        Results.Ok(new
        {
            doctors = doctorOptions.Value.Doctors.Select(doctor => new
            {
                id = doctor.Id,
                displayName = doctor.DisplayName,
                active = doctor.Active
            }),
            procedures = procedureOptions.Value.Procedures
                .Where(procedure => !procedure.Code.Contains("+SED", StringComparison.OrdinalIgnoreCase))
                .Select(procedure => new
                {
                    code = procedure.Code,
                    label = procedure.Label,
                    active = procedure.Active,
                    sedationEligible = procedure.SedationEligible
                }),
            reasons = new[]
            {
                new { token = HistoricalManualReviewReasons.IncorrectDoctor, label = "Incorrect Doctor" },
                new { token = HistoricalManualReviewReasons.IncorrectProcedure, label = "Incorrect Procedure" },
                new { token = HistoricalManualReviewReasons.IncorrectCaseDetails, label = "Incorrect Case Details" },
                new { token = HistoricalManualReviewReasons.UnexpectedLifecycle, label = "Unexpected Lifecycle" },
                new { token = HistoricalManualReviewReasons.OtherNeedsReview, label = "Other / Needs Review" }
            },
            noteMaximumLength = HistoricalAnomalyAdministrationService.MaximumNoteLength
        });

    private static IResult InvalidSource() =>
        Error(
            StatusCodes.Status400BadRequest,
            "invalid-source",
            "The source type and source record id must form a supported historical identity.");

    private static IResult MissingSource() =>
        Error(
            StatusCodes.Status404NotFound,
            "historical-source-not-found",
            "No durable historical source matches the typed identity.");

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new { code, message }, statusCode: statusCode);
}
