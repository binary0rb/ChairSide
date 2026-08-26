using System.Text.Json;

using Microsoft.AspNetCore.Http;

namespace ChairSide.Board.Services;

public static class HistoricalMetadataCorrectionEndpointHandler
{
    private static readonly IReadOnlyCollection<string> DoctorProperties =
        Properties("expectedRevision", "doctorId", "note");
    private static readonly IReadOnlyCollection<string> ProcedureProperties =
        Properties("expectedRevision", "procedureCode", "note");
    private static readonly IReadOnlyCollection<string> ProcedureSedationProperties =
        Properties("expectedRevision", "procedureCode", "sedationState", "note");
    private static readonly IReadOnlyCollection<string> SedationProperties =
        Properties("expectedRevision", "sedationState", "note");
    private static readonly IReadOnlyCollection<string> AddOnProperties =
        Properties("expectedRevision", "isAddOn", "note");
    private static readonly IReadOnlyCollection<string> AllocationProperties =
        Properties("expectedRevision", "expectedAllocation", "note");
    private static readonly IReadOnlyCollection<string> AllocationValueProperties =
        Properties("state", "suggestedUnits", "confirmedUnits");

    public static Task<IResult> CorrectDoctorAsync(
        string sourceType,
        long sourceRecordId,
        HttpContext httpContext,
        HistoricalMetadataCorrectionService service) =>
        ExecuteAsync(
            sourceType,
            sourceRecordId,
            httpContext,
            DoctorProperties,
            (key, root, revision, note) =>
                TryRequiredString(root, "doctorId", out var doctorId)
                    ? service.CorrectDoctor(key, revision, doctorId, note)
                    : null);

    public static Task<IResult> CorrectProcedureAsync(
        string sourceType,
        long sourceRecordId,
        HttpContext httpContext,
        HistoricalMetadataCorrectionService service) =>
        ExecuteAsync(
            sourceType,
            sourceRecordId,
            httpContext,
            ProcedureProperties,
            (key, root, revision, note) =>
                TryRequiredString(root, "procedureCode", out var procedureCode)
                    ? service.CorrectProcedure(key, revision, procedureCode, note)
                    : null);

    public static Task<IResult> CorrectProcedureAndSedationAsync(
        string sourceType,
        long sourceRecordId,
        HttpContext httpContext,
        HistoricalMetadataCorrectionService service) =>
        ExecuteAsync(
            sourceType,
            sourceRecordId,
            httpContext,
            ProcedureSedationProperties,
            (key, root, revision, note) =>
                TryRequiredString(root, "procedureCode", out var procedureCode)
                && TryEnum(root, "sedationState", out SedationState sedationState)
                    ? service.CorrectProcedureAndSedation(
                        key,
                        revision,
                        procedureCode,
                        sedationState,
                        note)
                    : null);

    public static Task<IResult> CorrectSedationAsync(
        string sourceType,
        long sourceRecordId,
        HttpContext httpContext,
        HistoricalMetadataCorrectionService service) =>
        ExecuteAsync(
            sourceType,
            sourceRecordId,
            httpContext,
            SedationProperties,
            (key, root, revision, note) =>
                TryEnum(root, "sedationState", out SedationState sedationState)
                    ? service.CorrectSedation(key, revision, sedationState, note)
                    : null);

    public static Task<IResult> CorrectAddOnAsync(
        string sourceType,
        long sourceRecordId,
        HttpContext httpContext,
        HistoricalMetadataCorrectionService service) =>
        ExecuteAsync(
            sourceType,
            sourceRecordId,
            httpContext,
            AddOnProperties,
            (key, root, revision, note) =>
                TryBoolean(root, "isAddOn", out var isAddOn)
                    ? service.CorrectAddOn(key, revision, isAddOn, note)
                    : null);

    public static Task<IResult> CorrectExpectedAllocationAsync(
        string sourceType,
        long sourceRecordId,
        HttpContext httpContext,
        HistoricalMetadataCorrectionService service) =>
        ExecuteAsync(
            sourceType,
            sourceRecordId,
            httpContext,
            AllocationProperties,
            (key, root, revision, note) =>
            {
                if (!TryProperty(root, "expectedAllocation", out var allocation)
                    || allocation.ValueKind != JsonValueKind.Object
                    || global::StrictJsonRequestReader.ValidatePropertySet(
                        allocation,
                        AllocationValueProperties) is not null
                    || !TryEnum(allocation, "state", out ExpectedAllocationState state)
                    || !TryNullableInt32(allocation, "suggestedUnits", out var suggestedUnits)
                    || !TryNullableInt32(allocation, "confirmedUnits", out var confirmedUnits))
                {
                    return null;
                }
                return service.CorrectExpectedAllocation(
                    key,
                    revision,
                    state,
                    suggestedUnits,
                    confirmedUnits,
                    note);
            });

    public static IResult GetEffectiveEncounter(
        string sourceType,
        long sourceRecordId,
        HistoricalMetadataCorrectionService service)
    {
        var key = new HistoricalEncounterKey(sourceType, sourceRecordId);
        if (!key.IsValid)
        {
            return Error(
                StatusCodes.Status400BadRequest,
                "invalid-source",
                "The source type and source record id must form a supported historical identity.");
        }
        var encounter = service.GetEffectiveEncounter(key);
        return encounter is null
            ? Error(
                StatusCodes.Status404NotFound,
                "historical-source-not-found",
                "No durable historical source matches the typed identity.")
            : Results.Ok(ToResponse(encounter));
    }

    private static async Task<IResult> ExecuteAsync(
        string sourceType,
        long sourceRecordId,
        HttpContext httpContext,
        IReadOnlyCollection<string> allowedProperties,
        Func<HistoricalEncounterKey, JsonElement, int, string?, HistoricalMetadataCorrectionResult?> operation)
    {
        var (root, bodyError, wasBodyEmpty) = await global::StrictJsonRequestReader.ReadObjectWithPresenceAsync(
            httpContext.Request,
            treatWhitespaceAsEmpty: false);
        if (bodyError is not null
            || wasBodyEmpty
            || global::StrictJsonRequestReader.ValidatePropertySet(root, allowedProperties) is not null
            || !TryNonNegativeRevision(root, out var expectedRevision)
            || !TryOptionalString(root, "note", out var note))
        {
            return MalformedRequest();
        }

        var key = new HistoricalEncounterKey(sourceType, sourceRecordId);
        var result = operation(key, root, expectedRevision, note);
        return result is null ? MalformedRequest() : ToHttpResult(key, result);
    }

    private static IResult ToHttpResult(
        HistoricalEncounterKey key,
        HistoricalMetadataCorrectionResult result) => result.Outcome switch
        {
            HistoricalMetadataCorrectionOutcome.Success => Results.Ok(ToResponse(result.Encounter!)),
            HistoricalMetadataCorrectionOutcome.NotFound => Error(
                StatusCodes.Status404NotFound,
                "historical-source-not-found",
                "No durable historical source matches the typed identity."),
            HistoricalMetadataCorrectionOutcome.StaleWrite => Results.Json(new
            {
                code = "stale-write",
                message = "The administrative revision has changed.",
                sourceType = key.SourceType,
                sourceRecordId = key.SourceRecordId,
                currentDisposition = result.State?.Disposition ?? HistoricalAdministrativeDispositions.NoAnomaly,
                currentRevision = result.CurrentRevision
            }, statusCode: StatusCodes.Status409Conflict),
            HistoricalMetadataCorrectionOutcome.ReviewNotPending => Error(
                StatusCodes.Status409Conflict,
                "review-not-pending",
                "Historical metadata correction is permitted only while the encounter Needs Review."),
            HistoricalMetadataCorrectionOutcome.InvalidDoctor => Error(
                StatusCodes.Status400BadRequest,
                "invalid-doctor",
                "The corrected doctor must be a governed current or historical roster identity."),
            HistoricalMetadataCorrectionOutcome.InvalidProcedure => Error(
                StatusCodes.Status400BadRequest,
                "invalid-procedure",
                "The corrected procedure must be a governed current or historical roster identity and remain coherent."),
            HistoricalMetadataCorrectionOutcome.InvalidSedation => Error(
                StatusCodes.Status400BadRequest,
                "invalid-sedation",
                "The corrected sedation state is not coherent with the effective procedure."),
            HistoricalMetadataCorrectionOutcome.PairedCorrectionNotRequired => Error(
                StatusCodes.Status409Conflict,
                "paired-correction-not-required",
                "Use the one-field procedure or sedation correction when eligibility does not change."),
            HistoricalMetadataCorrectionOutcome.InvalidExpectedAllocation => Error(
                StatusCodes.Status400BadRequest,
                "invalid-expected-allocation",
                "The corrected allocation must be one complete explicit confirmed historical value."),
            HistoricalMetadataCorrectionOutcome.UnsupportedCorrection => Error(
                StatusCodes.Status409Conflict,
                "unsupported-correction",
                "The durable historical source does not truthfully support this correction field."),
            HistoricalMetadataCorrectionOutcome.InvalidNote => Error(
                StatusCodes.Status400BadRequest,
                "invalid-note",
                "The administrative note must be 500 characters or fewer."),
            _ => Error(
                StatusCodes.Status400BadRequest,
                "invalid-source",
                "The source type and source record id must form a supported historical identity.")
        };

    internal static object ToResponse(
        HistoricalEffectiveEncounter encounter,
        IReadOnlyList<string>? reportingExclusionReasons = null)
    {
        var reportingProjection = encounter.Source.CompletedCycle?.ReportingProjection
            ?? encounter.Source.AbortedAssignment?.ReportingProjection;
        return new
        {
            sourceType = encounter.Key.SourceType,
            sourceRecordId = encounter.Key.SourceRecordId,
            originalEvidence = new
            {
                authority = encounter.OriginalEvidenceAuthority,
                readyHandoffId = encounter.OriginalReadyHandoffId,
                metadata = encounter.OriginalMetadata,
                lifecycle = ToLifecycleEvidence(encounter.Source)
            },
            effectiveMetadata = encounter.EffectiveMetadata,
            overrides = encounter.Overrides,
            correctionIndicators = encounter.CorrectionIndicators,
            correctionSupport = encounter.CorrectionSupport,
            disposition = encounter.Disposition,
            reason = encounter.CurrentReason,
            reasonSource = encounter.ReasonSource,
            administrativeRevision = encounter.AdministrativeRevision,
            reviewProvenance = new
            {
                importedReviewedAt = reportingProjection?.KnownReviewedAt,
                importedReviewedActorClass = reportingProjection?.KnownReviewedActorClass,
                hasHistoricalCorrection = reportingProjection?.HasHistoricalCorrectionProvenance ?? false,
                hasReviewedProvenance = reportingProjection?.HasReviewedProvenance ?? false
            },
            reportingExclusionReasons = reportingExclusionReasons ?? []
        };
    }

    private static object ToLifecycleEvidence(HistoricalEncounterRecord source) =>
        source.CompletedCycle is { } completed
            ? new
            {
                episodeId = completed.EpisodeId,
                roomId = completed.RoomId,
                prestageStartedAt = completed.PrestageStartedAt,
                seatedAt = (DateTimeOffset?)completed.SeatedAt,
                readyForDoctorAt = completed.ReadyForDoctorAt,
                doctorArrivedAt = completed.DoctorArrivedAt,
                doctorCompleteAt = completed.DoctorCompleteAt,
                roomAvailableAt = completed.RoomAvailableAt,
                terminatedAt = (DateTimeOffset?)null,
                terminatedFromState = (string?)null,
                terminationKind = (string?)null
            }
            : new
            {
                episodeId = (string?)source.AbortedAssignment!.EpisodeId,
                roomId = source.AbortedAssignment.RoomId,
                prestageStartedAt = source.AbortedAssignment.PrestageStartedAt,
                seatedAt = source.AbortedAssignment.SeatedAt,
                readyForDoctorAt = source.AbortedAssignment.ReadyForDoctorAt,
                doctorArrivedAt = (DateTimeOffset?)null,
                doctorCompleteAt = (DateTimeOffset?)null,
                roomAvailableAt = (DateTimeOffset?)null,
                terminatedAt = (DateTimeOffset?)source.AbortedAssignment.TerminatedAt,
                terminatedFromState = (string?)source.AbortedAssignment.TerminatedFromState,
                terminationKind = (string?)source.AbortedAssignment.TerminationKind
            };

    private static bool TryNonNegativeRevision(JsonElement root, out int revision)
    {
        revision = 0;
        return TryProperty(root, "expectedRevision", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out revision)
            && revision >= 0;
    }

    private static bool TryRequiredString(JsonElement root, string name, out string? value)
    {
        if (TryProperty(root, name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return value is not null;
        }
        value = null;
        return false;
    }

    private static bool TryOptionalString(JsonElement root, string name, out string? value)
    {
        if (!TryProperty(root, name, out var property))
        {
            value = null;
            return true;
        }
        if (property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }
        value = null;
        return false;
    }

    private static bool TryBoolean(JsonElement root, string name, out bool value)
    {
        if (TryProperty(root, name, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }
        value = false;
        return false;
    }

    private static bool TryNullableInt32(JsonElement root, string name, out int? value)
    {
        if (!TryProperty(root, name, out var property))
        {
            value = null;
            return false;
        }
        if (property.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var parsed))
        {
            value = parsed;
            return true;
        }
        value = null;
        return false;
    }

    private static bool TryEnum<TEnum>(JsonElement root, string name, out TEnum value)
        where TEnum : struct, Enum
    {
        if (TryRequiredString(root, name, out var text)
            && Enum.GetNames<TEnum>().Contains(text, StringComparer.Ordinal)
            && Enum.TryParse<TEnum>(text, ignoreCase: false, out value)
            && Enum.IsDefined(value))
        {
            return true;
        }
        value = default;
        return false;
    }

    private static bool TryProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static IReadOnlyCollection<string> Properties(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    private static IResult MalformedRequest() =>
        Error(StatusCodes.Status400BadRequest, "malformed-request", "The request body is missing or invalid.");

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new { code, message }, statusCode: statusCode);
}
