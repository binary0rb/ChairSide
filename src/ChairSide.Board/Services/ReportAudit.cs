namespace ChairSide.Board.Services;

public static class ReportAuditContributorKinds
{
    public const string PracticeCompletedCases = "PracticeCompletedCases";
    public const string IncludedCompletedCases = "IncludedCompletedCases";
    public const string ReadyWait = "ReadyWait";
    public const string SeatedToDoctor = "SeatedToDoctor";
    public const string DoctorTime = "DoctorTime";
    public const string Turnover = "Turnover";
    public const string ProcedureMix = "ProcedureMix";
    public const string HistoricalScheduleFit = "HistoricalScheduleFit";
    public const string CalibrationEvidence = "CalibrationEvidence";
    public const string PendingReview = "PendingReview";
    public const string ReviewedExceptionHistory = "ReviewedExceptionHistory";
}

public static class ReportAuditModes
{
    public const string CompletedCaseAudit = "CompletedCaseAudit";
    public const string MetricEvidence = "MetricEvidence";
    public const string ExceptionReview = "ExceptionReview";
}

public static class ReportAuditStanding
{
    public const string All = "All";
    public const string Included = "Included";
    public const string ReportingExcluded = "ReportingExcluded";
}

public static class ReportAuditSorts
{
    public const string MostRecent = "MostRecent";
    public const string LongestReadyWait = "LongestReadyWait";
    public const string LongestDoctorTime = "LongestDoctorTime";
    public const string LargestPositiveScheduleFitVariance = "LargestPositiveScheduleFitVariance";
    public const string LargestNegativeScheduleFitVariance = "LargestNegativeScheduleFitVariance";
    public const string Doctor = "Doctor";
    public const string Procedure = "Procedure";

    public static IReadOnlyList<string> All { get; } =
    [
        MostRecent,
        LongestReadyWait,
        LongestDoctorTime,
        LargestPositiveScheduleFitVariance,
        LargestNegativeScheduleFitVariance,
        Doctor,
        Procedure
    ];

    public static IReadOnlyList<string> Review { get; } = [MostRecent, Doctor, Procedure];
}

public sealed record ReportAuditEvidenceIdentity(
    long CompletedCycleId,
    string? AcceptedReadyHandoffId = null);

public sealed record ReportAuditRequest(
    string? From = null,
    string? To = null,
    string? Scope = null,
    string? DoctorId = null,
    string? Sedation = null,
    string? ProcedureGrouping = null,
    string? ContributorKind = null,
    string? SegmentDoctorId = null,
    string? ProcedureCode = null,
    string? BaseProcedureCode = null,
    string? AnalyticalStanding = null,
    IReadOnlyList<ReportAuditEvidenceIdentity>? EvidenceIds = null,
    string? Sort = null,
    int Offset = 0,
    int Limit = 50);

public sealed record ReportAuditSelection(
    ReportQueryContext Query,
    string ContributorKind,
    string? SegmentDoctorId,
    string? ProcedureCode,
    string? BaseProcedureCode,
    string AnalyticalStanding,
    IReadOnlyList<ReportAuditEvidenceIdentity> EvidenceIds);

public sealed record ReportAuditRow(
    long CompletedCycleId,
    string? AcceptedReadyHandoffId,
    string EvidenceMode,
    string AnalyticalStanding,
    DateTimeOffset? DateAnchor,
    int RoomId,
    string DoctorId,
    string DoctorName,
    string ProcedureCode,
    string ProcedureLabel,
    string BaseProcedureCode,
    bool IsSedationCase,
    bool IsAddOn,
    DateTimeOffset? PrestageStartedAt,
    DateTimeOffset SeatedAt,
    DateTimeOffset? ReadyForDoctorAt,
    DateTimeOffset? DoctorArrivedAt,
    DateTimeOffset? DoctorCompleteAt,
    DateTimeOffset? RoomAvailableAt,
    double? PrepSeconds,
    double? SeatedToDoctorSeconds,
    double? ReadyWaitSeconds,
    double? DoctorTimeSeconds,
    double? SeatedToDoctorCompleteSeconds,
    double? TurnoverSeconds,
    double? TotalRoomCycleSeconds,
    int OriginalDefaultExpectedUnits,
    int ExpectedAllocationUnits,
    int ExpectedAllocationMinutes,
    bool AllocationAdjustedFromDefault,
    double? ExactObservedScheduleFitSeconds,
    double? ExactScheduleFitVarianceSeconds,
    bool AgingThresholdReached,
    bool StaleThresholdReached,
    IReadOnlyList<string> ReportingExclusionReasons,
    CalibrationEvidenceCase? CalibrationEvidence,
    bool CanMarkException);

public sealed record ReportReviewAuditRow(
    string SourceType,
    long ReviewRecordId,
    long CompletedCycleId,
    long AbortedAssignmentId,
    string? EpisodeId,
    DateTimeOffset? ReviewAnchor,
    int RoomId,
    string? DoctorId,
    string DoctorName,
    string? ProcedureCode,
    string ProcedureLabel,
    DateTimeOffset? PrestageStartedAt,
    DateTimeOffset? SeatedAt,
    DateTimeOffset? ReadyForDoctorAt,
    DateTimeOffset? DoctorArrivedAt,
    DateTimeOffset? DoctorCompleteAt,
    DateTimeOffset? RoomAvailableAt,
    DateTimeOffset? TerminatedAt,
    string FinalState,
    string? Reason,
    string? SuggestedAction,
    string ReviewStatus,
    DateTimeOffset? ReviewedAt,
    string? ReviewedBy,
    bool RequiresReview);

public sealed record ReportAuditPage(
    ReportAuditSelection NormalizedSelection,
    string Mode,
    IReadOnlyList<ReportAuditRow> Rows,
    IReadOnlyList<ReportReviewAuditRow> ReviewRows,
    int ReturnedCount,
    int TotalMatchingCount,
    int Offset,
    int Limit,
    bool HasMore,
    string ActiveSort,
    IReadOnlyList<string> SupportedSorts);

public sealed record ReportDataQualityReasonCount(string Reason, int Count);

public sealed record ReportDataQualitySummary(
    int CompletedCount,
    int IncludedCount,
    int ReportingExcludedCount,
    int PendingReviewCount,
    int ReviewedExceptionCount,
    IReadOnlyList<ReportDataQualityReasonCount> ExclusionReasonCounts,
    string AnalyticalScopeDescription,
    string ReviewWindowDescription);

public sealed class ReportAuditQueryException(string message) : Exception(message);

