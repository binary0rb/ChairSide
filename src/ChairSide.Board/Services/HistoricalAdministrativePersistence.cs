namespace ChairSide.Board.Services;

public static class HistoricalAdministrativeDispositions
{
    public const string NoAnomaly = "NoAnomaly";
    public const string NeedsReview = "NeedsReview";
    public const string ClearedForReporting = "ClearedForReporting";
    public const string ConfirmedException = "ConfirmedException";

    public static bool IsValid(string? value) =>
        value is NoAnomaly or NeedsReview or ClearedForReporting or ConfirmedException;
}

public static class HistoricalAdministrativeActorClasses
{
    public const string System = "System";
    public const string LocalAdmin = "LocalAdmin";

    public static bool IsValid(string? value) => value is System or LocalAdmin;
}

public static class HistoricalAdministrativeReasonSources
{
    public const string System = "System";
    public const string LocalAdmin = "LocalAdmin";
    public const string Legacy = "Legacy";

    public static bool IsValid(string? value) => value is System or LocalAdmin or Legacy;
}

public static class HistoricalAdministrativeLedgerEventTypes
{
    public const string ManualFlag = "ManualFlag";
    public const string SystemFinding = "SystemFinding";
    public const string ReasonRefined = "ReasonRefined";
    public const string MetadataCorrected = "MetadataCorrected";
    public const string NoteAdded = "NoteAdded";
    public const string ClearedForReporting = "ClearedForReporting";
    public const string ConfirmedException = "ConfirmedException";
    public const string ReviewReopened = "ReviewReopened";
    public const string LegacyStateImported = "LegacyStateImported";

    public static bool IsValid(string? value) =>
        value is ManualFlag
            or SystemFinding
            or ReasonRefined
            or MetadataCorrected
            or NoteAdded
            or ClearedForReporting
            or ConfirmedException
            or ReviewReopened
            or LegacyStateImported;
}

/// <summary>
/// Current administrative interpretation and optional effective-value overrides for one durable
/// historical encounter. Override values never rewrite the historical source or accepted Ready
/// evidence. A NoAnomaly row is legal when an encounter has an effective metadata override but no
/// active administrative gate.
/// </summary>
public sealed record HistoricalEncounterAdministrativeState(
    HistoricalEncounterKey Key,
    string Disposition,
    string? CurrentReason,
    string? ReasonSource,
    DateTimeOffset? KnownReviewedAt,
    string? KnownReviewedActorClass,
    string? OverrideDoctorId,
    string? OverrideProcedureCode,
    SedationState? OverrideSedationState,
    bool? OverrideIsAddOn,
    ExpectedAllocationState? OverrideExpectedAllocationState,
    int? OverrideExpectedAllocationSuggestedUnits,
    int? OverrideExpectedAllocationConfirmedUnits,
    int AdministrativeRevision);

/// <summary>
/// One chronological append-only administrative event. OccurredAt is the time of this event; for
/// LegacyStateImported it is explicitly the migration time, not an inferred anomaly occurrence.
/// </summary>
public sealed record HistoricalEncounterAdministrativeLedgerEvent(
    long LedgerId,
    HistoricalEncounterKey Key,
    string EventType,
    DateTimeOffset OccurredAt,
    string ActorClass,
    string? ReasonSource,
    string? StructuredReason,
    string? PreviousValue,
    string? NewValue,
    string? AdminNote,
    int AdministrativeRevision);

public sealed record CommittedHistoricalAdministrativeWrite(
    HistoricalEncounterAdministrativeState State,
    HistoricalEncounterAdministrativeLedgerEvent LedgerEvent);
