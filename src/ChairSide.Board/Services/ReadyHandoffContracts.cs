using System.Text.Json.Serialization;

namespace ChairSide.Board.Services;

[JsonConverter(typeof(JsonStringEnumConverter<ReadyHandoffStatus>))]
public enum ReadyHandoffStatus
{
    Active,
    Withdrawn,
    Accepted
}

public sealed record ReadyHandoffContract
{
    private ReadyHandoffContract(
        string handoffId,
        DateTimeOffset readyAt,
        RoomAssignmentContract assignment,
        ReadyHandoffStatus status,
        DateTimeOffset? withdrawnAt,
        DateTimeOffset? acceptedAt)
    {
        HandoffId = handoffId;
        ReadyAt = readyAt;
        Assignment = assignment;
        Status = status;
        WithdrawnAt = withdrawnAt;
        AcceptedAt = acceptedAt;
    }

    public string HandoffId { get; }

    public DateTimeOffset ReadyAt { get; }

    public RoomAssignmentContract Assignment { get; }

    public ReadyHandoffStatus Status { get; }

    public DateTimeOffset? WithdrawnAt { get; }

    public DateTimeOffset? AcceptedAt { get; }

    public static ReadyHandoffContract Active(
        string handoffId,
        DateTimeOffset readyAt,
        RoomAssignmentContract assignment) =>
        Create(handoffId, readyAt, assignment, ReadyHandoffStatus.Active);

    public static ReadyHandoffContract Withdrawn(
        string handoffId,
        DateTimeOffset readyAt,
        RoomAssignmentContract assignment,
        DateTimeOffset withdrawnAt) =>
        Create(handoffId, readyAt, assignment, ReadyHandoffStatus.Withdrawn, withdrawnAt: withdrawnAt);

    public static ReadyHandoffContract Accepted(
        string handoffId,
        DateTimeOffset readyAt,
        RoomAssignmentContract assignment,
        DateTimeOffset acceptedAt) =>
        Create(handoffId, readyAt, assignment, ReadyHandoffStatus.Accepted, acceptedAt: acceptedAt);

    public static ReadyHandoffContract Create(
        string handoffId,
        DateTimeOffset readyAt,
        RoomAssignmentContract assignment,
        ReadyHandoffStatus status,
        DateTimeOffset? withdrawnAt = null,
        DateTimeOffset? acceptedAt = null)
    {
        var normalizedHandoffId = string.IsNullOrWhiteSpace(handoffId)
            ? throw new ArgumentException("Handoff identifier is required.", nameof(handoffId))
            : handoffId.Trim();

        ValidateTimestamp(readyAt, nameof(readyAt));
        ArgumentNullException.ThrowIfNull(assignment);

        if (assignment.Completeness != AssignmentCompleteness.Complete)
        {
            throw new ArgumentException("A Ready handoff must contain a complete assignment.", nameof(assignment));
        }

        ValidateStatusTimestamps(status, readyAt, withdrawnAt, acceptedAt);

        return new ReadyHandoffContract(
            normalizedHandoffId,
            readyAt,
            assignment,
            status,
            withdrawnAt,
            acceptedAt);
    }

    private static void ValidateStatusTimestamps(
        ReadyHandoffStatus status,
        DateTimeOffset readyAt,
        DateTimeOffset? withdrawnAt,
        DateTimeOffset? acceptedAt)
    {
        switch (status)
        {
            case ReadyHandoffStatus.Active:
                if (withdrawnAt.HasValue || acceptedAt.HasValue)
                {
                    throw new ArgumentException("An active handoff cannot have withdrawn or accepted timestamps.");
                }

                break;
            case ReadyHandoffStatus.Withdrawn:
                if (withdrawnAt is not { } withdrawn)
                {
                    throw new ArgumentException("A truthful handoff timestamp is required.", nameof(withdrawnAt));
                }

                ValidateTimestamp(withdrawn, nameof(withdrawnAt));
                EnsureNotBeforeReady(readyAt, withdrawn, nameof(withdrawnAt));
                if (acceptedAt.HasValue)
                {
                    throw new ArgumentException("A withdrawn handoff cannot have an accepted timestamp.", nameof(acceptedAt));
                }

                break;
            case ReadyHandoffStatus.Accepted:
                if (acceptedAt is not { } accepted)
                {
                    throw new ArgumentException("A truthful handoff timestamp is required.", nameof(acceptedAt));
                }

                ValidateTimestamp(accepted, nameof(acceptedAt));
                EnsureNotBeforeReady(readyAt, accepted, nameof(acceptedAt));
                if (withdrawnAt.HasValue)
                {
                    throw new ArgumentException("An accepted handoff cannot have a withdrawn timestamp.", nameof(withdrawnAt));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown handoff status.");
        }
    }

    private static void ValidateTimestamp(DateTimeOffset? timestamp, string parameterName)
    {
        if (timestamp is null || timestamp.Value == default)
        {
            throw new ArgumentException("A truthful handoff timestamp is required.", parameterName);
        }
    }

    private static void EnsureNotBeforeReady(
        DateTimeOffset readyAt,
        DateTimeOffset timestamp,
        string parameterName)
    {
        if (timestamp < readyAt)
        {
            throw new ArgumentOutOfRangeException(parameterName, timestamp, "Handoff timestamps cannot precede ReadyAt.");
        }
    }
}
