using System.Text.Json;

using Microsoft.AspNetCore.Http;

namespace ChairSide.Board.Services;

public static class HistoricalAnomalyEndpointHandler
{
    private static readonly IReadOnlyCollection<string> ExpectedRevisionOnly =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "expectedRevision" };
    private static readonly IReadOnlyCollection<string> ReasonProperties =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "expectedRevision", "reason" };
    private static readonly IReadOnlyCollection<string> MarkProperties =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "expectedRevision", "reason", "note" };
    private static readonly IReadOnlyCollection<string> NoteProperties =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "expectedRevision", "note" };

    public static Task<IResult> MarkForReviewAsync(
        string sourceType,
        long sourceRecordId,
        HttpContext httpContext,
        HistoricalAnomalyAdministrationService service) =>
        ExecuteAsync(
            sourceType,
            sourceRecordId,
            httpContext,
            MarkProperties,
            requireReason: true,
            requireNote: false,
            (key, request) => service.MarkForReview(key, request.ExpectedRevision, request.Reason, request.Note));

    public static Task<IResult> RefineReasonAsync(
        string sourceType,
        long sourceRecordId,
        HttpContext httpContext,
        HistoricalAnomalyAdministrationService service) =>
        ExecuteAsync(
            sourceType,
            sourceRecordId,
            httpContext,
            ReasonProperties,
            requireReason: true,
            requireNote: false,
            (key, request) => service.RefineReason(key, request.ExpectedRevision, request.Reason));

    public static Task<IResult> AddNoteAsync(
        string sourceType,
        long sourceRecordId,
        HttpContext httpContext,
        HistoricalAnomalyAdministrationService service) =>
        ExecuteAsync(
            sourceType,
            sourceRecordId,
            httpContext,
            NoteProperties,
            requireReason: false,
            requireNote: true,
            (key, request) => service.AddNote(key, request.ExpectedRevision, request.Note));

    public static Task<IResult> ClearForReportingAsync(
        string sourceType,
        long sourceRecordId,
        HttpContext httpContext,
        HistoricalAnomalyAdministrationService service) =>
        ExecuteAsync(
            sourceType,
            sourceRecordId,
            httpContext,
            NoteProperties,
            requireReason: false,
            requireNote: false,
            (key, request) => service.ClearForReporting(key, request.ExpectedRevision, request.Note));

    public static Task<IResult> ConfirmExceptionAsync(
        string sourceType,
        long sourceRecordId,
        HttpContext httpContext,
        HistoricalAnomalyAdministrationService service) =>
        ExecuteAsync(
            sourceType,
            sourceRecordId,
            httpContext,
            NoteProperties,
            requireReason: false,
            requireNote: false,
            (key, request) => service.ConfirmException(key, request.ExpectedRevision, request.Note));

    public static Task<IResult> ReopenReviewAsync(
        string sourceType,
        long sourceRecordId,
        HttpContext httpContext,
        HistoricalAnomalyAdministrationService service) =>
        ExecuteAsync(
            sourceType,
            sourceRecordId,
            httpContext,
            ExpectedRevisionOnly,
            requireReason: false,
            requireNote: false,
            (key, request) => service.ReopenReview(key, request.ExpectedRevision));

    private static async Task<IResult> ExecuteAsync(
        string sourceType,
        long sourceRecordId,
        HttpContext httpContext,
        IReadOnlyCollection<string> allowedProperties,
        bool requireReason,
        bool requireNote,
        Func<HistoricalEncounterKey, ParsedRequest, HistoricalAdministrativeOperationResult> operation)
    {
        var (root, bodyError, wasBodyEmpty) = await global::StrictJsonRequestReader.ReadObjectWithPresenceAsync(
            httpContext.Request,
            treatWhitespaceAsEmpty: false);
        if (bodyError is not null || wasBodyEmpty)
        {
            return Error(StatusCodes.Status400BadRequest, "malformed-request", "A JSON object body is required.");
        }
        if (global::StrictJsonRequestReader.ValidatePropertySet(root, allowedProperties) is not null)
        {
            return Error(StatusCodes.Status400BadRequest, "malformed-request", "The request contains unknown or duplicate properties.");
        }
        if (!TryParse(root, requireReason, requireNote, out var request))
        {
            return Error(StatusCodes.Status400BadRequest, "malformed-request", "The request properties are missing or invalid.");
        }

        var key = new HistoricalEncounterKey(sourceType, sourceRecordId);
        return ToHttpResult(key, operation(key, request));
    }

    private static bool TryParse(
        JsonElement root,
        bool requireReason,
        bool requireNote,
        out ParsedRequest request)
    {
        int? expectedRevision = null;
        string? reason = null;
        string? note = null;
        var sawReason = false;
        var sawNote = false;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals("expectedRevision", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.Number
                    || !property.Value.TryGetInt32(out var parsedRevision)
                    || parsedRevision < 0)
                {
                    request = default;
                    return false;
                }
                expectedRevision = parsedRevision;
            }
            else if (property.Name.Equals("reason", StringComparison.OrdinalIgnoreCase))
            {
                sawReason = true;
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    request = default;
                    return false;
                }
                reason = property.Value.GetString();
            }
            else if (property.Name.Equals("note", StringComparison.OrdinalIgnoreCase))
            {
                sawNote = true;
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    request = default;
                    return false;
                }
                note = property.Value.GetString();
            }
        }

        if (!expectedRevision.HasValue || requireReason && !sawReason || requireNote && !sawNote)
        {
            request = default;
            return false;
        }
        request = new ParsedRequest(expectedRevision.Value, reason, note);
        return true;
    }

    private static IResult ToHttpResult(
        HistoricalEncounterKey key,
        HistoricalAdministrativeOperationResult result) => result.Outcome switch
        {
            HistoricalAdministrativeOperationOutcome.Success => Results.Ok(new
            {
                sourceType = key.SourceType,
                sourceRecordId = key.SourceRecordId,
                disposition = result.State!.Disposition,
                reason = result.State.CurrentReason,
                reasonSource = result.State.ReasonSource,
                administrativeRevision = result.CurrentRevision
            }),
            HistoricalAdministrativeOperationOutcome.NotFound => Error(
                StatusCodes.Status404NotFound,
                "historical-source-not-found",
                "No durable historical source matches the typed identity."),
            HistoricalAdministrativeOperationOutcome.StaleWrite => Results.Json(new
            {
                code = "stale-write",
                message = "The administrative revision has changed.",
                currentDisposition = result.State?.Disposition ?? HistoricalAdministrativeDispositions.NoAnomaly,
                currentRevision = result.CurrentRevision
            }, statusCode: StatusCodes.Status409Conflict),
            HistoricalAdministrativeOperationOutcome.InvalidTransition => Error(
                StatusCodes.Status409Conflict,
                "invalid-transition",
                "The administrative transition is not valid from the current disposition."),
            HistoricalAdministrativeOperationOutcome.InvalidReason => Error(
                StatusCodes.Status400BadRequest,
                "invalid-reason",
                "The manual review reason is not supported."),
            HistoricalAdministrativeOperationOutcome.InvalidNote => Error(
                StatusCodes.Status400BadRequest,
                "invalid-note",
                "The administrative note must be 500 characters or fewer."),
            _ => Error(
                StatusCodes.Status400BadRequest,
                "invalid-source",
                "The source type and source record id must form a supported historical identity.")
        };

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new { code, message }, statusCode: statusCode);

    private readonly record struct ParsedRequest(int ExpectedRevision, string? Reason, string? Note);
}
