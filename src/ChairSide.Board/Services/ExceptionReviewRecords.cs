namespace ChairSide.Board.Services;

public static class ExceptionReviewSources
{
    public const string CompletedCycle = "CompletedCycle";
    public const string AbortedAssignment = "AbortedAssignment";
}

public sealed record ExceptionReviewRecord(
    string SourceType,
    long ReviewRecordId,
    long CompletedCycleId,
    long AbortedAssignmentId,
    string? EpisodeId,
    int RoomId,
    string? AssignedDoctor,
    string? ProcedureCode,
    DateTimeOffset? PrestageStartedAt,
    DateTimeOffset? SeatedAt,
    DateTimeOffset? ReadyForDoctorAt,
    DateTimeOffset? DoctorArrivedAt,
    DateTimeOffset? DoctorCompleteAt,
    DateTimeOffset? RoomAvailableAt,
    string FinalWaitState,
    bool IsException,
    bool RequiresReview,
    string? ExceptionReason,
    string ReviewStatus,
    string? SuggestedAction,
    DateTimeOffset? ReviewedAt,
    string? ReviewedBy);
