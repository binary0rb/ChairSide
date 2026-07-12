namespace ChairSide.Board.Services;

public static class ReadyHandoffTerminationKinds
{
    public const string Canceled = "Canceled";
    public const string Expired = "Expired";

    public static bool IsValid(string? value) =>
        value is Canceled or Expired;
}

public sealed record PersistedRoomAssignment(
    string? DoctorId,
    string? DoctorDisplayName,
    string? ProcedureCode,
    string? ProcedureCategory,
    SedationState? SedationState,
    ExpectedAllocationState? ExpectedAllocationState,
    int? ExpectedAllocationSuggestedUnits,
    int? ExpectedAllocationConfirmedUnits)
{
    public static PersistedRoomAssignment FromCanonicalContract(
        RoomAssignmentContract assignment,
        string? doctorDisplayName = null,
        string? procedureCategory = null)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        return new PersistedRoomAssignment(
            assignment.DoctorId,
            doctorDisplayName,
            assignment.ProcedureCode,
            procedureCategory,
            assignment.Sedation.State,
            assignment.ExpectedAllocation.State,
            assignment.ExpectedAllocation.SuggestedValue,
            assignment.ExpectedAllocation.ConfirmedValue);
    }

    public static PersistedRoomAssignment FromContract(
        RoomAssignmentContract assignment,
        string? doctorDisplayName = null,
        string? procedureCategory = null) =>
        FromCanonicalContract(assignment, doctorDisplayName, procedureCategory);

    public RoomAssignmentContract ToContract()
    {
        if (SedationState is not { } sedationState)
        {
            throw new InvalidOperationException("Cannot build a canonical assignment from ambiguous legacy sedation state.");
        }

        if (ExpectedAllocationState is not { } allocationState)
        {
            throw new InvalidOperationException("Cannot build a canonical assignment from ambiguous legacy allocation state.");
        }

        var sedation = SedationContract.Create(
            sedationState,
            sedationState switch
            {
                ChairSide.Board.Services.SedationState.EligibleYes => true,
                ChairSide.Board.Services.SedationState.EligibleNo => false,
                _ => null
            });
        var allocation = ExpectedAllocationContract.Create(
            allocationState,
            ExpectedAllocationSuggestedUnits,
            ExpectedAllocationConfirmedUnits);
        return RoomAssignmentContract.Create(DoctorId, ProcedureCode, sedation, allocation);
    }

    public void ValidateCanonicalValues()
    {
        if (SedationState is null && ExpectedAllocationState is null)
        {
            return;
        }

        _ = ToContract();
    }

    public void ValidateCanonicalWrite()
    {
        _ = ToContract();
    }
}

public sealed class PersistedReadyHandoff
{
    public string HandoffId { get; init; } = "";

    public string EpisodeId { get; init; } = "";

    public int RoomId { get; init; }

    public DateTimeOffset ReadyAt { get; init; }

    public DateTimeOffset? WithdrawnAt { get; init; }

    public DateTimeOffset? AcceptedAt { get; init; }

    public DateTimeOffset? TerminatedAt { get; init; }

    public string? TerminationKind { get; init; }

    public PersistedRoomAssignment Assignment { get; init; } = new(
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null);

    public ReadyHandoffStatus? ContractStatus =>
        TerminatedAt.HasValue
            ? null
            : AcceptedAt.HasValue
                ? ReadyHandoffStatus.Accepted
                : WithdrawnAt.HasValue
                    ? ReadyHandoffStatus.Withdrawn
                    : ReadyHandoffStatus.Active;

    public ReadyHandoffContract ToContract() =>
        ContractStatus switch
        {
            ReadyHandoffStatus.Active => ReadyHandoffContract.Active(HandoffId, ReadyAt, Assignment.ToContract()),
            ReadyHandoffStatus.Withdrawn => ReadyHandoffContract.Withdrawn(
                HandoffId,
                ReadyAt,
                Assignment.ToContract(),
                WithdrawnAt!.Value),
            ReadyHandoffStatus.Accepted => ReadyHandoffContract.Accepted(
                HandoffId,
                ReadyAt,
                Assignment.ToContract(),
                AcceptedAt!.Value),
            _ => throw new InvalidOperationException("A terminated Ready handoff is audit history, not a ReadyHandoffContract status.")
        };
}
