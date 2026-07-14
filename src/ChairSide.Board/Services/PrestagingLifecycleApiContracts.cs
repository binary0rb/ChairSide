using System.Text.Json;

namespace ChairSide.Board.Services;

/// <summary>
/// Writable canonical assignment shape for the Prestaging lifecycle API. These four values express
/// user input only; the server derives canonical domain states and assignment completeness.
/// </summary>
public sealed record CanonicalAssignmentRequest(
    string? DoctorId,
    string? ProcedureCode,
    string? SedationChoice,
    int? ConfirmedExpectedAllocationUnits);

public sealed record EmptyLifecycleActionRequest
{
    public static EmptyLifecycleActionRequest Instance { get; } = new();
}

public sealed record SeatRoomActionRequest(CanonicalAssignmentRequest? Assignment);

public sealed record ReadyForDoctorActionRequest(CanonicalAssignmentRequest? Assignment);

public sealed record PrestagingLifecycleErrorResponse(
    string Code,
    string Message,
    IReadOnlyList<string> UnresolvedFields,
    IReadOnlyList<RoomIntegrityFault> IntegrityFaults);

public static class PrestagingLifecycleErrorCodes
{
    public const string MalformedRequest = "malformed-request";
    public const string RoomNotFound = "room-not-found";
    public const string InvalidAssignment = "invalid-assignment";
    public const string AssignmentIncomplete = "assignment-incomplete";
    public const string LifecycleConflict = "lifecycle-conflict";
    public const string AssignmentLocked = "assignment-locked";
    public const string IntegrityFault = "integrity-fault";
    public const string StaleWrite = "stale-write";
    public const string PersistenceFailure = "persistence-failure";

    public static IReadOnlyList<string> All { get; } =
    [
        MalformedRequest, RoomNotFound, InvalidAssignment, AssignmentIncomplete,
        LifecycleConflict, AssignmentLocked, IntegrityFault, StaleWrite, PersistenceFailure
    ];
}

public sealed record PrestagingLifecycleParseResult<T>(
    T? Value,
    PrestagingLifecycleErrorResponse? Error)
    where T : class;

public sealed record CanonicalAssignmentConversionResult(
    RoomAssignmentContract? Value,
    PrestagingLifecycleErrorResponse? Error);

public sealed record CanonicalRoomLifecycleProjection(
    CanonicalRoomLifecycleState State,
    RoomAssignmentContract Assignment,
    bool AssignmentLocked,
    ReadyUrgency ReadyUrgency,
    IReadOnlyList<RoomIntegrityFault> IntegrityFaults);

public sealed record PrestagingLifecycleActionResponse(
    RoomStatus Room,
    CanonicalRoomLifecycleProjection Lifecycle,
    ReadyHandoffContract? Handoff);

/// <summary>
/// Strict transport parser for the canonical lifecycle request bodies. Property names are the exact
/// lower-camel-case contract names. Case-variant duplicates are rejected before unknown properties.
/// An omitted action body is equivalent to an empty object only for actions whose body is optional.
/// </summary>
public static class PrestagingLifecycleRequestParser
{
    private static readonly IReadOnlySet<string> AssignmentProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "doctorId",
        "procedureCode",
        "sedationChoice",
        "confirmedExpectedAllocationUnits"
    };

    private static readonly IReadOnlySet<string> SeatProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "assignment"
    };

    private static readonly IReadOnlySet<string> ReadyProperties = new HashSet<string>(StringComparer.Ordinal)
    {
        "assignment"
    };

    private static IReadOnlySet<string> EmptyPropertySet { get; } = new HashSet<string>(StringComparer.Ordinal);

    public static PrestagingLifecycleParseResult<CanonicalAssignmentRequest> ParseAssignment(string? body)
    {
        var rootResult = ParseObject(body, allowEmptyBody: false);
        return rootResult.Error is not null
            ? Failure<CanonicalAssignmentRequest>(rootResult.Error)
            : ParseAssignment(rootResult.Value);
    }

    public static PrestagingLifecycleParseResult<EmptyLifecycleActionRequest> ParseEmptyAction(string? body)
    {
        var rootResult = ParseObject(body, allowEmptyBody: true);
        if (rootResult.Error is not null)
        {
            return Failure<EmptyLifecycleActionRequest>(rootResult.Error);
        }

        var propertyError = ValidatePropertySet(rootResult.Value, EmptyPropertySet);
        return propertyError is null
            ? Success(EmptyLifecycleActionRequest.Instance)
            : Failure<EmptyLifecycleActionRequest>(propertyError);
    }

    public static PrestagingLifecycleParseResult<SeatRoomActionRequest> ParseSeatAction(string? body)
    {
        var rootResult = ParseObject(body, allowEmptyBody: true);
        if (rootResult.Error is not null)
        {
            return Failure<SeatRoomActionRequest>(rootResult.Error);
        }

        var propertyError = ValidatePropertySet(rootResult.Value, SeatProperties);
        if (propertyError is not null)
        {
            return Failure<SeatRoomActionRequest>(propertyError);
        }

        CanonicalAssignmentRequest? assignment = null;
        foreach (var property in rootResult.Value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "assignment":
                    if (property.Value.ValueKind == JsonValueKind.Null)
                    {
                        break;
                    }
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        return Failure<SeatRoomActionRequest>(InvalidType("assignment", "an object or null"));
                    }

                    var assignmentResult = ParseAssignment(property.Value);
                    if (assignmentResult.Error is not null)
                    {
                        return Failure<SeatRoomActionRequest>(assignmentResult.Error);
                    }
                    assignment = assignmentResult.Value;
                    break;

            }
        }

        return Success(new SeatRoomActionRequest(assignment));
    }

    public static PrestagingLifecycleParseResult<ReadyForDoctorActionRequest> ParseReadyForDoctorAction(string? body)
    {
        var rootResult = ParseObject(body, allowEmptyBody: true);
        if (rootResult.Error is not null)
        {
            return Failure<ReadyForDoctorActionRequest>(rootResult.Error);
        }

        var propertyError = ValidatePropertySet(rootResult.Value, ReadyProperties);
        if (propertyError is not null)
        {
            return Failure<ReadyForDoctorActionRequest>(propertyError);
        }

        CanonicalAssignmentRequest? assignment = null;
        foreach (var property in rootResult.Value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                return Failure<ReadyForDoctorActionRequest>(InvalidType("assignment", "an object or null"));
            }

            var assignmentResult = ParseAssignment(property.Value);
            if (assignmentResult.Error is not null)
            {
                return Failure<ReadyForDoctorActionRequest>(assignmentResult.Error);
            }
            assignment = assignmentResult.Value;
        }

        return Success(new ReadyForDoctorActionRequest(assignment));
    }

    private static PrestagingLifecycleParseResult<CanonicalAssignmentRequest> ParseAssignment(JsonElement root)
    {
        var propertyError = ValidatePropertySet(root, AssignmentProperties);
        if (propertyError is not null)
        {
            return Failure<CanonicalAssignmentRequest>(propertyError);
        }

        string? doctorId = null;
        string? procedureCode = null;
        string? sedationChoice = null;
        int? confirmedUnits = null;

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "doctorId":
                    if (!TryReadNullableString(property.Value, out doctorId))
                    {
                        return Failure<CanonicalAssignmentRequest>(InvalidType("doctorId", "a string or null"));
                    }
                    break;
                case "procedureCode":
                    if (!TryReadNullableString(property.Value, out procedureCode))
                    {
                        return Failure<CanonicalAssignmentRequest>(InvalidType("procedureCode", "a string or null"));
                    }
                    if (CanonicalProcedureCodeTransport.IsDecorated(procedureCode))
                    {
                        return Failure<CanonicalAssignmentRequest>(
                            InvalidAssignment("procedureCode must not include the internal +SED decoration."));
                    }
                    break;
                case "sedationChoice":
                    if (!TryReadNullableString(property.Value, out sedationChoice)
                        || sedationChoice is not (null or "yes" or "no"))
                    {
                        return Failure<CanonicalAssignmentRequest>(InvalidSedationChoice());
                    }
                    break;
                case "confirmedExpectedAllocationUnits":
                    if (property.Value.ValueKind == JsonValueKind.Null)
                    {
                        break;
                    }
                    if (property.Value.ValueKind != JsonValueKind.Number
                        || !property.Value.TryGetInt32(out var parsedUnits))
                    {
                        return Failure<CanonicalAssignmentRequest>(
                            InvalidType("confirmedExpectedAllocationUnits", "a positive integer or null"));
                    }
                    if (parsedUnits <= 0)
                    {
                        return Failure<CanonicalAssignmentRequest>(Error(
                            PrestagingLifecycleErrorCodes.InvalidAssignment,
                            "confirmedExpectedAllocationUnits must be a positive integer when supplied."));
                    }
                    confirmedUnits = parsedUnits;
                    break;
            }
        }

        return Success(new CanonicalAssignmentRequest(doctorId, procedureCode, sedationChoice, confirmedUnits));
    }

    private static (JsonElement Value, PrestagingLifecycleErrorResponse? Error) ParseObject(
        string? body,
        bool allowEmptyBody)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            if (!allowEmptyBody)
            {
                return (default, Error(PrestagingLifecycleErrorCodes.MalformedRequest, "A JSON object request body is required."));
            }

            using var empty = JsonDocument.Parse("{}");
            return (empty.RootElement.Clone(), null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return (default, Error(PrestagingLifecycleErrorCodes.MalformedRequest, "The request body contains malformed JSON."));
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (default, Error(PrestagingLifecycleErrorCodes.MalformedRequest, "The request body must be a JSON object."));
            }

            return (document.RootElement.Clone(), null);
        }
    }

    private static PrestagingLifecycleErrorResponse? ValidatePropertySet(
        JsonElement root,
        IReadOnlySet<string> allowedProperties)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return Error(PrestagingLifecycleErrorCodes.MalformedRequest, $"Duplicate property '{property.Name}' is not allowed.");
            }
            if (!allowedProperties.Contains(property.Name))
            {
                return Error(PrestagingLifecycleErrorCodes.MalformedRequest, $"Unknown property '{property.Name}' is not allowed.");
            }
        }

        return null;
    }

    private static bool TryReadNullableString(JsonElement value, out string? parsed)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            parsed = null;
            return true;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            parsed = value.GetString();
            return true;
        }

        parsed = null;
        return false;
    }

    private static PrestagingLifecycleErrorResponse InvalidSedationChoice() =>
        InvalidAssignment("sedationChoice must be 'yes', 'no', or null when supplied.");

    private static PrestagingLifecycleErrorResponse InvalidType(string propertyName, string expected) =>
        Error(PrestagingLifecycleErrorCodes.MalformedRequest, $"{propertyName} must be {expected}.");

    private static PrestagingLifecycleErrorResponse InvalidAssignment(string message) =>
        Error(PrestagingLifecycleErrorCodes.InvalidAssignment, message);

    private static PrestagingLifecycleErrorResponse Error(string code, string message) =>
        new(code, message, [], []);

    private static PrestagingLifecycleParseResult<T> Success<T>(T value)
        where T : class =>
        new(value, null);

    private static PrestagingLifecycleParseResult<T> Failure<T>(PrestagingLifecycleErrorResponse error)
        where T : class =>
        new(null, error);
}

internal static class CanonicalProcedureCodeTransport
{
    private const string SedationDecoration = "+SED";

    public static bool IsDecorated(string? procedureCode) =>
        procedureCode?.EndsWith(SedationDecoration, StringComparison.OrdinalIgnoreCase) == true;

    public static string? ToBaseCode(string? procedureCode) =>
        IsDecorated(procedureCode)
            ? procedureCode![..^SedationDecoration.Length]
            : procedureCode;
}

/// <summary>
/// Converts the client-facing base procedure code and separate sedation choice into canonical
/// assignment states. This transport conversion deliberately retains the base code. A later
/// endpoint/store adapter may derive the internal +SED decoration where legacy persistence
/// compatibility requires it; canonical clients never send that decoration.
/// </summary>
public static class CanonicalAssignmentRequestConverter
{
    public static CanonicalAssignmentConversionResult Convert(
        CanonicalAssignmentRequest request,
        ProcedureCategory? procedure)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsBlankButSupplied(request.DoctorId) || IsBlankButSupplied(request.ProcedureCode))
        {
            return Failure("doctorId and procedureCode must be null or non-blank canonical identifiers.");
        }
        if (CanonicalProcedureCodeTransport.IsDecorated(request.ProcedureCode))
        {
            return Failure("procedureCode must not include the internal +SED decoration.");
        }
        if (request.SedationChoice is not (null or "yes" or "no"))
        {
            return Failure("sedationChoice must be 'yes', 'no', or null.");
        }
        if (request.ConfirmedExpectedAllocationUnits is <= 0)
        {
            return Failure("confirmedExpectedAllocationUnits must be positive when supplied.");
        }

        var doctorId = request.DoctorId?.Trim();
        var procedureCode = request.ProcedureCode?.Trim();
        if (procedureCode is null)
        {
            if (procedure is not null
                || request.SedationChoice is not null
                || request.ConfirmedExpectedAllocationUnits.HasValue)
            {
                return Failure("Sedation and expected allocation cannot be supplied before a procedure is selected.");
            }

            return Success(RoomAssignmentContract.Create(
                doctorId,
                procedureCode: null,
                SedationContract.UnavailableNoProcedure(),
                ExpectedAllocationContract.Unknown()));
        }

        if (procedure is null
            || !MatchesProcedure(procedure, procedureCode)
            || procedure.DefaultExpectedUnits <= 0)
        {
            return Failure("procedureCode must identify a current procedure with a valid expected allocation suggestion.");
        }

        SedationContract sedation;
        if (!procedure.SedationEligible)
        {
            if (request.SedationChoice is not null)
            {
                return Failure("sedationChoice is unavailable for the selected procedure and must be null.");
            }
            sedation = SedationContract.UnavailableProcedureIneligible();
        }
        else
        {
            sedation = request.SedationChoice switch
            {
                "yes" => SedationContract.EligibleYes(),
                "no" => SedationContract.EligibleNo(),
                null => SedationContract.EligibleUnresolved(),
                _ => throw new InvalidOperationException("Sedation choice was validated before canonical conversion.")
            };
        }

        var allocation = request.ConfirmedExpectedAllocationUnits switch
        {
            null => ExpectedAllocationContract.Suggested(procedure.DefaultExpectedUnits),
            var confirmed when confirmed == procedure.DefaultExpectedUnits =>
                ExpectedAllocationContract.ConfirmedSuggestedValue(procedure.DefaultExpectedUnits),
            var confirmed => ExpectedAllocationContract.ConfirmedAdjustedValue(
                procedure.DefaultExpectedUnits,
                confirmed.Value)
        };

        return Success(RoomAssignmentContract.Create(doctorId, procedureCode, sedation, allocation));
    }

    private static bool MatchesProcedure(ProcedureCategory procedure, string procedureCode) =>
        string.Equals(procedure.Code, procedureCode, StringComparison.OrdinalIgnoreCase);

    private static bool IsBlankButSupplied(string? value) =>
        value is not null && string.IsNullOrWhiteSpace(value);

    private static CanonicalAssignmentConversionResult Success(RoomAssignmentContract assignment) =>
        new(assignment, null);

    private static CanonicalAssignmentConversionResult Failure(string message) =>
        new(
            null,
            new PrestagingLifecycleErrorResponse(
                PrestagingLifecycleErrorCodes.InvalidAssignment,
                message,
                [], []));
}

public static class CanonicalAssignmentRequirements
{
    public static IReadOnlyList<string> GetUnresolvedFields(RoomAssignmentContract assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var unresolved = new List<string>();
        if (assignment.DoctorId is null)
        {
            unresolved.Add("doctorId");
        }
        if (assignment.ProcedureCode is null)
        {
            unresolved.Add("procedureCode");
        }
        if (assignment.Sedation.State == SedationState.EligibleUnresolved)
        {
            unresolved.Add("sedationChoice");
        }
        if (!assignment.ExpectedAllocation.SatisfiesAssignmentCompleteness)
        {
            unresolved.Add("confirmedExpectedAllocationUnits");
        }

        return unresolved;
    }
}

public static class PrestagingLifecycleResponseProjector
{
    public static PrestagingLifecycleActionResponse Create(
        RoomStatus room,
        CanonicalRoomLifecycleState state,
        RoomAssignmentContract assignment,
        ReadyHandoffContract? handoff = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(assignment);
        var projectedRoom = ProjectRoom(room);
        var projectedAssignment = ProjectAssignment(assignment);
        var projectedHandoff = ProjectHandoff(handoff);
        var urgency = ReadyUrgencyEvaluator.Validate(state, room.ReadyUrgency);
        var assignmentLocked = state is
            CanonicalRoomLifecycleState.ReadyForDoctor or
            CanonicalRoomLifecycleState.DoctorWorking or
            CanonicalRoomLifecycleState.DoctorComplete or
            CanonicalRoomLifecycleState.Turnover;
        var faults = room.IntegrityFaults?
            .Select(fault => new RoomIntegrityFault(fault.Code, ProjectAssignment(fault.Assignment)))
            .ToArray()
            ?? [];
        var lifecycle = new CanonicalRoomLifecycleProjection(
            state,
            projectedAssignment,
            assignmentLocked,
            urgency,
            faults);
        return new PrestagingLifecycleActionResponse(projectedRoom, lifecycle, projectedHandoff);
    }

    private static RoomStatus ProjectRoom(RoomStatus room)
    {
        var procedure = room.Procedure is null
            ? null
            : room.Procedure with { Code = CanonicalProcedureCodeTransport.ToBaseCode(room.Procedure.Code)! };
        return room with
        {
            ProcedureCode = CanonicalProcedureCodeTransport.ToBaseCode(room.ProcedureCode),
            Procedure = procedure
        };
    }

    private static RoomAssignmentContract ProjectAssignment(RoomAssignmentContract assignment)
    {
        var baseCode = CanonicalProcedureCodeTransport.ToBaseCode(assignment.ProcedureCode);
        return string.Equals(baseCode, assignment.ProcedureCode, StringComparison.Ordinal)
            ? assignment
            : RoomAssignmentContract.Create(
                assignment.DoctorId,
                baseCode,
                assignment.Sedation,
                assignment.ExpectedAllocation);
    }

    private static ReadyHandoffContract? ProjectHandoff(ReadyHandoffContract? handoff)
    {
        if (handoff is null)
        {
            return null;
        }

        return ReadyHandoffContract.Create(
            handoff.HandoffId,
            handoff.ReadyAt,
            ProjectAssignment(handoff.Assignment),
            handoff.Status,
            handoff.WithdrawnAt,
            handoff.AcceptedAt);
    }
}
