using System.Text.Json.Serialization;

namespace ChairSide.Board.Services;

[JsonConverter(typeof(JsonStringEnumConverter<AssignmentCompleteness>))]
public enum AssignmentCompleteness
{
    Absent,
    Partial,
    Complete
}

[JsonConverter(typeof(JsonStringEnumConverter<SedationState>))]
public enum SedationState
{
    UnavailableNoProcedure,
    UnavailableProcedureIneligible,
    EligibleUnresolved,
    EligibleYes,
    EligibleNo
}

[JsonConverter(typeof(JsonStringEnumConverter<ExpectedAllocationState>))]
public enum ExpectedAllocationState
{
    Unknown,
    Suggested,
    ConfirmedSuggestedValue,
    ConfirmedAdjustedValue
}

[JsonConverter(typeof(JsonStringEnumConverter<CanonicalRoomLifecycleState>))]
public enum CanonicalRoomLifecycleState
{
    Available,
    Prestaging,
    SeatedInPrep,
    ReadyForDoctor,
    DoctorWorking,
    DoctorComplete,
    Turnover
}

[JsonConverter(typeof(JsonStringEnumConverter<ReadyUrgency>))]
public enum ReadyUrgency
{
    None,
    Aging,
    Stale
}

[JsonConverter(typeof(JsonStringEnumConverter<RoomIntegrityFaultCode>))]
public enum RoomIntegrityFaultCode
{
    ReadyAssignmentIncomplete
}

public sealed record SedationContract
{
    private SedationContract(SedationState state, bool? explicitDecision)
    {
        State = state;
        ExplicitDecision = explicitDecision;
    }

    public SedationState State { get; }

    public bool? ExplicitDecision { get; }

    public static SedationContract UnavailableNoProcedure() =>
        new(SedationState.UnavailableNoProcedure, explicitDecision: null);

    public static SedationContract UnavailableProcedureIneligible() =>
        new(SedationState.UnavailableProcedureIneligible, explicitDecision: null);

    public static SedationContract EligibleUnresolved() =>
        new(SedationState.EligibleUnresolved, explicitDecision: null);

    public static SedationContract EligibleYes() =>
        new(SedationState.EligibleYes, explicitDecision: true);

    public static SedationContract EligibleNo() =>
        new(SedationState.EligibleNo, explicitDecision: false);

    public static SedationContract FromProcedure(
        bool hasProcedure,
        bool procedureSedationEligible,
        bool? explicitDecision)
    {
        if (!hasProcedure)
        {
            if (explicitDecision.HasValue)
            {
                throw new ArgumentException(
                    "A sedation decision cannot exist before a procedure is selected.",
                    nameof(explicitDecision));
            }

            return UnavailableNoProcedure();
        }

        if (!procedureSedationEligible)
        {
            if (explicitDecision.HasValue)
            {
                throw new ArgumentException(
                    "An ineligible procedure cannot carry an explicit sedation decision.",
                    nameof(explicitDecision));
            }

            return UnavailableProcedureIneligible();
        }

        return explicitDecision switch
        {
            true => EligibleYes(),
            false => EligibleNo(),
            null => EligibleUnresolved()
        };
    }

    public static SedationContract Create(SedationState state, bool? explicitDecision) =>
        state switch
        {
            SedationState.UnavailableNoProcedure when explicitDecision is null => UnavailableNoProcedure(),
            SedationState.UnavailableProcedureIneligible when explicitDecision is null => UnavailableProcedureIneligible(),
            SedationState.EligibleUnresolved when explicitDecision is null => EligibleUnresolved(),
            SedationState.EligibleYes when explicitDecision == true => EligibleYes(),
            SedationState.EligibleNo when explicitDecision == false => EligibleNo(),
            _ => throw new ArgumentException(
                $"Sedation state '{state}' is not valid with decision '{explicitDecision}'.",
                nameof(explicitDecision))
        };

    public bool SatisfiesAssignmentCompleteness =>
        State is SedationState.UnavailableProcedureIneligible or SedationState.EligibleYes or SedationState.EligibleNo;
}

public sealed record ExpectedAllocationContract
{
    private ExpectedAllocationContract(
        ExpectedAllocationState state,
        int? suggestedValue,
        int? confirmedValue)
    {
        State = state;
        SuggestedValue = suggestedValue;
        ConfirmedValue = confirmedValue;
    }

    public ExpectedAllocationState State { get; }

    public int? SuggestedValue { get; }

    public int? ConfirmedValue { get; }

    public static ExpectedAllocationContract Unknown() =>
        new(ExpectedAllocationState.Unknown, suggestedValue: null, confirmedValue: null);

    public static ExpectedAllocationContract Suggested(int suggestedValue) =>
        Create(ExpectedAllocationState.Suggested, suggestedValue, confirmedValue: null);

    public static ExpectedAllocationContract ConfirmedSuggestedValue(int suggestedValue) =>
        Create(ExpectedAllocationState.ConfirmedSuggestedValue, suggestedValue, suggestedValue);

    public static ExpectedAllocationContract ConfirmedAdjustedValue(int? suggestedValue, int confirmedValue) =>
        Create(ExpectedAllocationState.ConfirmedAdjustedValue, suggestedValue, confirmedValue);

    public static ExpectedAllocationContract Create(
        ExpectedAllocationState state,
        int? suggestedValue,
        int? confirmedValue)
    {
        ValidatePositiveValue(suggestedValue, nameof(suggestedValue));
        ValidatePositiveValue(confirmedValue, nameof(confirmedValue));

        return state switch
        {
            ExpectedAllocationState.Unknown when suggestedValue is null && confirmedValue is null =>
                Unknown(),
            ExpectedAllocationState.Suggested when suggestedValue.HasValue && confirmedValue is null =>
                new(ExpectedAllocationState.Suggested, suggestedValue, confirmedValue: null),
            ExpectedAllocationState.ConfirmedSuggestedValue
                when suggestedValue.HasValue && confirmedValue.HasValue && suggestedValue == confirmedValue =>
                new(ExpectedAllocationState.ConfirmedSuggestedValue, suggestedValue, confirmedValue),
            ExpectedAllocationState.ConfirmedAdjustedValue
                when confirmedValue.HasValue && suggestedValue != confirmedValue =>
                new(ExpectedAllocationState.ConfirmedAdjustedValue, suggestedValue, confirmedValue),
            _ => throw new ArgumentException(
                $"Allocation state '{state}' is not valid with suggested value '{suggestedValue}' and confirmed value '{confirmedValue}'.")
        };
    }

    public bool SatisfiesAssignmentCompleteness =>
        State is ExpectedAllocationState.ConfirmedSuggestedValue or ExpectedAllocationState.ConfirmedAdjustedValue;

    private static void ValidatePositiveValue(int? value, string parameterName)
    {
        if (value is <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Canonical allocation values must be positive.");
        }
    }
}

public sealed record RoomAssignmentContract
{
    private RoomAssignmentContract(
        string? doctorId,
        string? procedureCode,
        SedationContract sedation,
        ExpectedAllocationContract expectedAllocation,
        AssignmentCompleteness completeness)
    {
        DoctorId = doctorId;
        ProcedureCode = procedureCode;
        Sedation = sedation;
        ExpectedAllocation = expectedAllocation;
        Completeness = completeness;
    }

    public string? DoctorId { get; }

    public string? ProcedureCode { get; }

    public SedationContract Sedation { get; }

    public ExpectedAllocationContract ExpectedAllocation { get; }

    public AssignmentCompleteness Completeness { get; }

    public static RoomAssignmentContract Create(
        string? doctorId,
        string? procedureCode,
        SedationContract sedation,
        ExpectedAllocationContract expectedAllocation)
    {
        ArgumentNullException.ThrowIfNull(sedation);
        ArgumentNullException.ThrowIfNull(expectedAllocation);

        var normalizedDoctorId = NormalizeToken(doctorId);
        var normalizedProcedureCode = NormalizeToken(procedureCode);
        ValidateProcedureSedationCompatibility(normalizedProcedureCode, sedation);

        var completeness = AssignmentCompletenessEvaluator.Evaluate(
            normalizedDoctorId,
            normalizedProcedureCode,
            sedation,
            expectedAllocation);

        return new RoomAssignmentContract(
            normalizedDoctorId,
            normalizedProcedureCode,
            sedation,
            expectedAllocation,
            completeness);
    }

    private static string? NormalizeToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateProcedureSedationCompatibility(string? procedureCode, SedationContract sedation)
    {
        if (procedureCode is null && sedation.State != SedationState.UnavailableNoProcedure)
        {
            throw new ArgumentException(
                "A room assignment without a procedure must use the no-procedure sedation state.",
                nameof(sedation));
        }

        if (procedureCode is not null && sedation.State == SedationState.UnavailableNoProcedure)
        {
            throw new ArgumentException(
                "A room assignment with a procedure must resolve sedation against that procedure.",
                nameof(sedation));
        }
    }
}

public static class AssignmentCompletenessEvaluator
{
    public static AssignmentCompleteness Evaluate(
        string? doctorId,
        string? procedureCode,
        SedationContract sedation,
        ExpectedAllocationContract expectedAllocation)
    {
        ArgumentNullException.ThrowIfNull(sedation);
        ArgumentNullException.ThrowIfNull(expectedAllocation);

        var hasDoctor = !string.IsNullOrWhiteSpace(doctorId);
        var hasProcedure = !string.IsNullOrWhiteSpace(procedureCode);

        if (hasDoctor
            && hasProcedure
            && sedation.SatisfiesAssignmentCompleteness
            && expectedAllocation.SatisfiesAssignmentCompleteness)
        {
            return AssignmentCompleteness.Complete;
        }

        if (!hasDoctor
            && !hasProcedure
            && sedation.State == SedationState.UnavailableNoProcedure
            && expectedAllocation.State == ExpectedAllocationState.Unknown)
        {
            return AssignmentCompleteness.Absent;
        }

        return AssignmentCompleteness.Partial;
    }
}

public static class ReadyUrgencyEvaluator
{
    public static ReadyUrgency Validate(CanonicalRoomLifecycleState primaryState, ReadyUrgency urgency)
    {
        if (primaryState != CanonicalRoomLifecycleState.ReadyForDoctor && urgency != ReadyUrgency.None)
        {
            throw new ArgumentException(
                "Aging and Stale urgency are legal only while the primary lifecycle state is Ready for Doctor.",
                nameof(urgency));
        }

        return urgency;
    }
}

public sealed record RoomCapabilities(
    bool CanEditAssignment,
    bool CanSaveDetails,
    bool CanSeat,
    bool CanReady,
    bool CanWithdrawReady,
    bool CanDoctorArrive);

public static class RoomCapabilitiesEvaluator
{
    public static RoomCapabilities Evaluate(
        CanonicalRoomLifecycleState primaryState,
        bool hasUnsavedAssignmentChanges)
    {
        return primaryState switch
        {
            CanonicalRoomLifecycleState.Prestaging => new RoomCapabilities(
                CanEditAssignment: true,
                CanSaveDetails: hasUnsavedAssignmentChanges,
                CanSeat: true,
                CanReady: false,
                CanWithdrawReady: false,
                CanDoctorArrive: false),
            CanonicalRoomLifecycleState.SeatedInPrep => new RoomCapabilities(
                CanEditAssignment: true,
                CanSaveDetails: hasUnsavedAssignmentChanges,
                CanSeat: false,
                CanReady: true,
                CanWithdrawReady: false,
                CanDoctorArrive: false),
            CanonicalRoomLifecycleState.ReadyForDoctor => new RoomCapabilities(
                CanEditAssignment: false,
                CanSaveDetails: false,
                CanSeat: false,
                CanReady: false,
                CanWithdrawReady: true,
                CanDoctorArrive: true),
            _ => new RoomCapabilities(
                CanEditAssignment: false,
                CanSaveDetails: false,
                CanSeat: false,
                CanReady: false,
                CanWithdrawReady: false,
                CanDoctorArrive: false)
        };
    }
}

public sealed record RoomIntegrityFault(
    RoomIntegrityFaultCode Code,
    RoomAssignmentContract Assignment);

public static class RoomIntegrityFaultEvaluator
{
    public static IReadOnlyList<RoomIntegrityFault> Evaluate(
        CanonicalRoomLifecycleState primaryState,
        RoomAssignmentContract assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        if (IsReadyOrLater(primaryState) && assignment.Completeness != AssignmentCompleteness.Complete)
        {
            return [new RoomIntegrityFault(RoomIntegrityFaultCode.ReadyAssignmentIncomplete, assignment)];
        }

        return [];
    }

    private static bool IsReadyOrLater(CanonicalRoomLifecycleState primaryState) =>
        primaryState is
            CanonicalRoomLifecycleState.ReadyForDoctor or
            CanonicalRoomLifecycleState.DoctorWorking or
            CanonicalRoomLifecycleState.DoctorComplete or
            CanonicalRoomLifecycleState.Turnover;
}
