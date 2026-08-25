namespace ChairSide.Board.Services;

public static class HistoricalManualReviewReasons
{
    public const string IncorrectDoctor = "IncorrectDoctor";
    public const string IncorrectProcedure = "IncorrectProcedure";
    public const string IncorrectCaseDetails = "IncorrectCaseDetails";
    public const string UnexpectedLifecycle = "UnexpectedLifecycle";
    public const string OtherNeedsReview = "OtherNeedsReview";

    public static bool IsValid(string? value) =>
        value is IncorrectDoctor
            or IncorrectProcedure
            or IncorrectCaseDetails
            or UnexpectedLifecycle
            or OtherNeedsReview;
}

public enum HistoricalSystemFindingKind
{
    AfterHoursSweep,
    ExceededMaxActiveDuration
}

public static class HistoricalSystemFindingReasons
{
    public const string AfterHoursSweep = ExceptionReasons.AfterHoursSweep;
    public const string ExceededMaxActiveDuration = ExceptionReasons.ExceededMaxActiveDuration;

    public static string FromKind(HistoricalSystemFindingKind kind) => kind switch
    {
        HistoricalSystemFindingKind.AfterHoursSweep => AfterHoursSweep,
        HistoricalSystemFindingKind.ExceededMaxActiveDuration => ExceededMaxActiveDuration,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public enum HistoricalAdministrativeOperationOutcome
{
    Success,
    NotFound,
    StaleWrite,
    InvalidTransition,
    InvalidReason,
    InvalidNote,
    InvalidSource
}

public sealed record HistoricalAdministrativeOperationResult(
    HistoricalAdministrativeOperationOutcome Outcome,
    HistoricalEncounterAdministrativeState? State = null,
    HistoricalEncounterAdministrativeLedgerEvent? LedgerEvent = null,
    int CurrentRevision = 0);

internal enum GuardedHistoricalAdministrativePersistenceOutcome
{
    Success,
    NotFound,
    StaleWrite
}

internal sealed record GuardedHistoricalAdministrativePersistenceResult(
    GuardedHistoricalAdministrativePersistenceOutcome Outcome,
    CommittedHistoricalAdministrativeWrite? CommittedWrite = null,
    HistoricalEncounterAdministrativeState? CurrentState = null,
    int CurrentRevision = 0);

/// <summary>
/// Owns canonical historical anomaly policy. The repository owns the decisive expected-revision
/// comparison and the atomic projection-plus-ledger commit.
/// </summary>
public sealed class HistoricalAnomalyAdministrationService
{
    public const int MaximumNoteLength = 500;

    private readonly SqliteBoardRepository _repository;
    private readonly TimeProvider _timeProvider;

    public HistoricalAnomalyAdministrationService(SqliteBoardRepository repository)
        : this(repository, TimeProvider.System)
    {
    }

    internal HistoricalAnomalyAdministrationService(
        SqliteBoardRepository repository,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _timeProvider = timeProvider;
    }

    public HistoricalAdministrativeOperationResult MarkForReview(
        HistoricalEncounterKey key,
        int expectedRevision,
        string? reason,
        string? note = null)
    {
        var validation = ValidateCommon(key, expectedRevision, reason, note, requireReason: true);
        if (validation is not null) return validation;

        return Mutate(key, expectedRevision, current =>
        {
            var state = BaseState(key, current) with
            {
                Disposition = HistoricalAdministrativeDispositions.NeedsReview,
                CurrentReason = reason,
                ReasonSource = HistoricalAdministrativeReasonSources.LocalAdmin,
                AdministrativeRevision = expectedRevision + 1
            };
            return CreateMutation(
                state,
                HistoricalAdministrativeLedgerEventTypes.ManualFlag,
                HistoricalAdministrativeActorClasses.LocalAdmin,
                HistoricalAdministrativeReasonSources.LocalAdmin,
                reason,
                current?.Disposition ?? HistoricalAdministrativeDispositions.NoAnomaly,
                HistoricalAdministrativeDispositions.NeedsReview,
                note);
        });
    }

    public HistoricalAdministrativeOperationResult RefineReason(
        HistoricalEncounterKey key,
        int expectedRevision,
        string? reason)
    {
        var validation = ValidateCommon(key, expectedRevision, reason, note: null, requireReason: true);
        if (validation is not null) return validation;

        return Mutate(key, expectedRevision, current =>
        {
            if (current is null) return InvalidTransition();
            var state = current with
            {
                CurrentReason = reason,
                ReasonSource = HistoricalAdministrativeReasonSources.LocalAdmin,
                AdministrativeRevision = expectedRevision + 1
            };
            return CreateMutation(
                state,
                HistoricalAdministrativeLedgerEventTypes.ReasonRefined,
                HistoricalAdministrativeActorClasses.LocalAdmin,
                HistoricalAdministrativeReasonSources.LocalAdmin,
                reason,
                current.CurrentReason,
                reason,
                adminNote: null);
        });
    }

    public HistoricalAdministrativeOperationResult AddNote(
        HistoricalEncounterKey key,
        int expectedRevision,
        string? note)
    {
        var validation = ValidateCommon(key, expectedRevision, reason: null, note, requireReason: false);
        if (validation is not null) return validation;
        if (note is null) return new(HistoricalAdministrativeOperationOutcome.InvalidNote);

        return Mutate(key, expectedRevision, current =>
        {
            if (current is null) return InvalidTransition();
            var state = current with { AdministrativeRevision = expectedRevision + 1 };
            return CreateMutation(
                state,
                HistoricalAdministrativeLedgerEventTypes.NoteAdded,
                HistoricalAdministrativeActorClasses.LocalAdmin,
                current.ReasonSource,
                current.CurrentReason,
                previousValue: null,
                newValue: null,
                note);
        });
    }

    public HistoricalAdministrativeOperationResult ClearForReporting(
        HistoricalEncounterKey key,
        int expectedRevision,
        string? note = null) =>
        ChangeDisposition(
            key,
            expectedRevision,
            HistoricalAdministrativeDispositions.ClearedForReporting,
            HistoricalAdministrativeLedgerEventTypes.ClearedForReporting,
            note,
            current => current.Disposition == HistoricalAdministrativeDispositions.NeedsReview);

    public HistoricalAdministrativeOperationResult ConfirmException(
        HistoricalEncounterKey key,
        int expectedRevision,
        string? note = null) =>
        ChangeDisposition(
            key,
            expectedRevision,
            HistoricalAdministrativeDispositions.ConfirmedException,
            HistoricalAdministrativeLedgerEventTypes.ConfirmedException,
            note,
            current => current.Disposition == HistoricalAdministrativeDispositions.NeedsReview);

    public HistoricalAdministrativeOperationResult ReopenReview(
        HistoricalEncounterKey key,
        int expectedRevision) =>
        ChangeDisposition(
            key,
            expectedRevision,
            HistoricalAdministrativeDispositions.NeedsReview,
            HistoricalAdministrativeLedgerEventTypes.ReviewReopened,
            note: null,
            current => current.Disposition is HistoricalAdministrativeDispositions.ClearedForReporting
                or HistoricalAdministrativeDispositions.ConfirmedException);

    internal HistoricalAdministrativeOperationResult RecordSystemFinding(
        HistoricalEncounterKey key,
        int expectedRevision,
        HistoricalSystemFindingKind finding)
    {
        var validation = ValidateCommon(key, expectedRevision, reason: null, note: null, requireReason: false);
        if (validation is not null) return validation;
        var reason = HistoricalSystemFindingReasons.FromKind(finding);

        return Mutate(key, expectedRevision, current =>
        {
            var state = BaseState(key, current) with
            {
                Disposition = HistoricalAdministrativeDispositions.NeedsReview,
                CurrentReason = reason,
                ReasonSource = HistoricalAdministrativeReasonSources.System,
                AdministrativeRevision = expectedRevision + 1
            };
            return CreateMutation(
                state,
                HistoricalAdministrativeLedgerEventTypes.SystemFinding,
                HistoricalAdministrativeActorClasses.System,
                HistoricalAdministrativeReasonSources.System,
                reason,
                current?.Disposition ?? HistoricalAdministrativeDispositions.NoAnomaly,
                HistoricalAdministrativeDispositions.NeedsReview,
                adminNote: null);
        });
    }

    private HistoricalAdministrativeOperationResult ChangeDisposition(
        HistoricalEncounterKey key,
        int expectedRevision,
        string disposition,
        string eventType,
        string? note,
        Func<HistoricalEncounterAdministrativeState, bool> transitionAllowed)
    {
        var validation = ValidateCommon(key, expectedRevision, reason: null, note, requireReason: false);
        if (validation is not null) return validation;

        return Mutate(key, expectedRevision, current =>
        {
            if (current is null || !transitionAllowed(current)) return InvalidTransition();
            var state = current with
            {
                Disposition = disposition,
                AdministrativeRevision = expectedRevision + 1
            };
            return CreateMutation(
                state,
                eventType,
                HistoricalAdministrativeActorClasses.LocalAdmin,
                current.ReasonSource,
                current.CurrentReason,
                current.Disposition,
                disposition,
                adminNote: note);
        });
    }

    private HistoricalAdministrativeOperationResult Mutate(
        HistoricalEncounterKey key,
        int expectedRevision,
        Func<HistoricalEncounterAdministrativeState?, HistoricalAdministrativeOperationResult> create)
    {
        if (_repository.LoadHistoricalEncounter(key) is null)
        {
            return new(HistoricalAdministrativeOperationOutcome.NotFound, CurrentRevision: 0);
        }

        var current = _repository.LoadHistoricalAdministrativeState(key);
        var currentRevision = current?.AdministrativeRevision ?? 0;
        if (currentRevision != expectedRevision)
        {
            return new(HistoricalAdministrativeOperationOutcome.StaleWrite, current, CurrentRevision: currentRevision);
        }

        var candidate = create(current);
        if (candidate.Outcome != HistoricalAdministrativeOperationOutcome.Success)
        {
            return candidate with { State = current, CurrentRevision = currentRevision };
        }

        var persisted = _repository.PersistHistoricalAdministrativeStateAndLedgerGuarded(
            expectedRevision,
            candidate.State!,
            candidate.LedgerEvent!);
        return persisted.Outcome switch
        {
            GuardedHistoricalAdministrativePersistenceOutcome.Success => new(
                HistoricalAdministrativeOperationOutcome.Success,
                persisted.CommittedWrite!.State,
                persisted.CommittedWrite.LedgerEvent,
                persisted.CommittedWrite.State.AdministrativeRevision),
            GuardedHistoricalAdministrativePersistenceOutcome.NotFound => new(
                HistoricalAdministrativeOperationOutcome.NotFound,
                CurrentRevision: 0),
            _ => new(
                HistoricalAdministrativeOperationOutcome.StaleWrite,
                persisted.CurrentState,
                CurrentRevision: persisted.CurrentRevision)
        };
    }

    private HistoricalAdministrativeOperationResult CreateMutation(
        HistoricalEncounterAdministrativeState state,
        string eventType,
        string actorClass,
        string? reasonSource,
        string? structuredReason,
        string? previousValue,
        string? newValue,
        string? adminNote) =>
        new(
            HistoricalAdministrativeOperationOutcome.Success,
            state,
            new HistoricalEncounterAdministrativeLedgerEvent(
                LedgerId: 0,
                Key: state.Key,
                EventType: eventType,
                OccurredAt: _timeProvider.GetUtcNow(),
                ActorClass: actorClass,
                ReasonSource: reasonSource,
                StructuredReason: structuredReason,
                PreviousValue: previousValue,
                NewValue: newValue,
                AdminNote: adminNote,
                AdministrativeRevision: state.AdministrativeRevision),
            state.AdministrativeRevision);

    private static HistoricalEncounterAdministrativeState BaseState(
        HistoricalEncounterKey key,
        HistoricalEncounterAdministrativeState? current) =>
        current ?? new HistoricalEncounterAdministrativeState(
            key,
            HistoricalAdministrativeDispositions.NoAnomaly,
            CurrentReason: null,
            ReasonSource: null,
            KnownReviewedAt: null,
            KnownReviewedActorClass: null,
            OverrideDoctorId: null,
            OverrideProcedureCode: null,
            OverrideSedationState: null,
            OverrideIsAddOn: null,
            OverrideExpectedAllocationState: null,
            OverrideExpectedAllocationSuggestedUnits: null,
            OverrideExpectedAllocationConfirmedUnits: null,
            AdministrativeRevision: 0);

    private static HistoricalAdministrativeOperationResult? ValidateCommon(
        HistoricalEncounterKey key,
        int expectedRevision,
        string? reason,
        string? note,
        bool requireReason)
    {
        if (!key.IsValid || expectedRevision < 0)
        {
            return new(HistoricalAdministrativeOperationOutcome.InvalidSource);
        }
        if (requireReason && !HistoricalManualReviewReasons.IsValid(reason))
        {
            return new(HistoricalAdministrativeOperationOutcome.InvalidReason);
        }
        if (note?.Length > MaximumNoteLength)
        {
            return new(HistoricalAdministrativeOperationOutcome.InvalidNote);
        }
        return null;
    }

    private static HistoricalAdministrativeOperationResult InvalidTransition() =>
        new(HistoricalAdministrativeOperationOutcome.InvalidTransition);
}
