using System.Text.Json;
using System.Text.Json.Serialization;

using ChairSide.Board.Options;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Services;

public static class HistoricalMetadataCorrectionFields
{
    public const string Doctor = "Doctor";
    public const string Procedure = "Procedure";
    public const string ProcedureAndSedation = "ProcedureAndSedation";
    public const string Sedation = "Sedation";
    public const string AddOn = "AddOn";
    public const string ExpectedAllocation = "ExpectedAllocation";
}

[JsonConverter(typeof(JsonStringEnumConverter<HistoricalMetadataEvidenceAuthority>))]
public enum HistoricalMetadataEvidenceAuthority
{
    AcceptedReadyHandoff,
    TerminalReadyHandoff,
    CompletedCycle,
    AbortedAssignment
}

public sealed record HistoricalEncounterMetadata(
    string? DoctorId,
    string? ProcedureCode,
    SedationState? SedationState,
    bool IsAddOn,
    ExpectedAllocationContract? ExpectedAllocation);

public sealed record HistoricalEncounterMetadataOverrides(
    string? DoctorId,
    string? ProcedureCode,
    SedationState? SedationState,
    bool? IsAddOn,
    ExpectedAllocationContract? ExpectedAllocation);

public sealed record HistoricalMetadataCorrectionIndicators(
    bool Doctor,
    bool Procedure,
    bool Sedation,
    bool AddOn,
    bool ExpectedAllocation);

public sealed record HistoricalMetadataCorrectionSupport(
    bool Doctor,
    bool Procedure,
    bool ProcedureAndSedation,
    bool Sedation,
    bool AddOn,
    bool ExpectedAllocation);

/// <summary>
/// Canonical current view of one historical encounter. Source and original metadata remain
/// immutable evidence; EffectiveMetadata applies only the nullable administrative overrides.
/// Ledger replay is deliberately unnecessary to determine today's effective values.
/// </summary>
public sealed record HistoricalEffectiveEncounter(
    HistoricalEncounterKey Key,
    HistoricalEncounterRecord Source,
    HistoricalMetadataEvidenceAuthority OriginalEvidenceAuthority,
    string? OriginalReadyHandoffId,
    HistoricalEncounterMetadata OriginalMetadata,
    HistoricalEncounterMetadata EffectiveMetadata,
    HistoricalEncounterMetadataOverrides Overrides,
    HistoricalMetadataCorrectionIndicators CorrectionIndicators,
    HistoricalMetadataCorrectionSupport CorrectionSupport,
    string Disposition,
    string? CurrentReason,
    string? ReasonSource,
    int AdministrativeRevision);

public enum HistoricalMetadataCorrectionOutcome
{
    Success,
    NotFound,
    StaleWrite,
    ReviewNotPending,
    InvalidDoctor,
    InvalidProcedure,
    InvalidSedation,
    PairedCorrectionNotRequired,
    InvalidExpectedAllocation,
    UnsupportedCorrection,
    InvalidNote,
    InvalidSource
}

public sealed record HistoricalMetadataCorrectionResult(
    HistoricalMetadataCorrectionOutcome Outcome,
    HistoricalEffectiveEncounter? Encounter = null,
    HistoricalEncounterAdministrativeState? State = null,
    HistoricalEncounterAdministrativeLedgerEvent? LedgerEvent = null,
    int CurrentRevision = 0);

/// <summary>
/// Owns historical metadata correction policy and effective encounter projection. Procedure and
/// Sedation normally remain separate one-field corrections. CorrectProcedureAndSedation is the
/// sole bounded atomic group because crossing a procedure sedation-eligibility boundary has no
/// coherent one-field intermediate state. It is not a general batch-edit mechanism.
/// </summary>
public sealed class HistoricalMetadataCorrectionService
{
    private const string SedationModifierSuffix = "+SED";

    private readonly SqliteBoardRepository _repository;
    private readonly IReadOnlyList<DoctorRosterItem> _doctors;
    private readonly IReadOnlyList<ProcedureRosterItem> _procedures;
    private readonly TimeProvider _timeProvider;

    public HistoricalMetadataCorrectionService(
        SqliteBoardRepository repository,
        IOptions<DoctorRosterOptions> doctorOptions,
        IOptions<ProcedureRosterOptions> procedureOptions)
        : this(repository, doctorOptions, procedureOptions, TimeProvider.System)
    {
    }

    internal HistoricalMetadataCorrectionService(
        SqliteBoardRepository repository,
        IOptions<DoctorRosterOptions> doctorOptions,
        IOptions<ProcedureRosterOptions> procedureOptions,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _doctors = doctorOptions.Value.Doctors.ToArray();
        _procedures = procedureOptions.Value.Procedures.ToArray();
        _timeProvider = timeProvider;
    }

    public HistoricalEffectiveEncounter? GetEffectiveEncounter(HistoricalEncounterKey key)
    {
        if (!key.IsValid) return null;
        var source = _repository.LoadHistoricalEncounter(key);
        if (source is null) return null;
        return BuildProjection(source, _repository.LoadHistoricalAdministrativeState(key));
    }

    public HistoricalMetadataCorrectionResult CorrectDoctor(
        HistoricalEncounterKey key,
        int expectedRevision,
        string? doctorId,
        string? note = null)
    {
        var inputValidation = ValidateInput(key, expectedRevision, note);
        if (inputValidation is not null) return inputValidation;
        var governedDoctor = FindDoctor(doctorId);
        if (governedDoctor is null)
        {
            return new(HistoricalMetadataCorrectionOutcome.InvalidDoctor);
        }

        return Correct(key, expectedRevision, note, context =>
        {
            if (context.Projection.OriginalMetadata.DoctorId is null)
            {
                return Invalid(HistoricalMetadataCorrectionOutcome.UnsupportedCorrection);
            }

            var effective = context.Projection.EffectiveMetadata with { DoctorId = governedDoctor.Id };
            if (!IsCoherent(effective, context.RequiresCompleteReadyAssignment))
            {
                return Invalid(HistoricalMetadataCorrectionOutcome.InvalidDoctor);
            }

            var state = context.State with
            {
                OverrideDoctorId = TokenEquals(governedDoctor.Id, context.Projection.OriginalMetadata.DoctorId)
                    ? null
                    : governedDoctor.Id,
                AdministrativeRevision = expectedRevision + 1
            };
            return Mutation(
                state,
                HistoricalMetadataCorrectionFields.Doctor,
                context.Projection.EffectiveMetadata.DoctorId!,
                governedDoctor.Id,
                note);
        });
    }

    public HistoricalMetadataCorrectionResult CorrectProcedure(
        HistoricalEncounterKey key,
        int expectedRevision,
        string? procedureCode,
        string? note = null)
    {
        var inputValidation = ValidateInput(key, expectedRevision, note);
        if (inputValidation is not null) return inputValidation;
        var governedProcedure = FindProcedure(procedureCode);
        if (governedProcedure is null)
        {
            return new(HistoricalMetadataCorrectionOutcome.InvalidProcedure);
        }

        return Correct(key, expectedRevision, note, context =>
        {
            if (context.Projection.OriginalMetadata.ProcedureCode is null
                || context.Projection.EffectiveMetadata.SedationState is null)
            {
                return Invalid(HistoricalMetadataCorrectionOutcome.UnsupportedCorrection);
            }

            var effective = context.Projection.EffectiveMetadata with { ProcedureCode = governedProcedure.Code };
            if (!IsCoherent(effective, context.RequiresCompleteReadyAssignment))
            {
                return Invalid(HistoricalMetadataCorrectionOutcome.InvalidProcedure);
            }

            var state = context.State with
            {
                OverrideProcedureCode = TokenEquals(governedProcedure.Code, context.Projection.OriginalMetadata.ProcedureCode)
                    ? null
                    : governedProcedure.Code,
                AdministrativeRevision = expectedRevision + 1
            };
            return Mutation(
                state,
                HistoricalMetadataCorrectionFields.Procedure,
                context.Projection.EffectiveMetadata.ProcedureCode!,
                governedProcedure.Code,
                note);
        });
    }

    public HistoricalMetadataCorrectionResult CorrectProcedureAndSedation(
        HistoricalEncounterKey key,
        int expectedRevision,
        string? procedureCode,
        SedationState sedationState,
        string? note = null)
    {
        var inputValidation = ValidateInput(key, expectedRevision, note);
        if (inputValidation is not null) return inputValidation;
        var governedProcedure = FindProcedure(procedureCode);
        if (governedProcedure is null)
        {
            return new(HistoricalMetadataCorrectionOutcome.InvalidProcedure);
        }
        if (!IsExplicitHistoricalSedation(sedationState))
        {
            return new(HistoricalMetadataCorrectionOutcome.InvalidSedation);
        }

        return Correct(key, expectedRevision, note, context =>
        {
            if (context.Projection.OriginalMetadata.ProcedureCode is null
                || context.Projection.OriginalMetadata.SedationState is null
                || context.Projection.EffectiveMetadata.ProcedureCode is null
                || context.Projection.EffectiveMetadata.SedationState is null)
            {
                return Invalid(HistoricalMetadataCorrectionOutcome.UnsupportedCorrection);
            }

            var currentProcedure = FindProcedure(context.Projection.EffectiveMetadata.ProcedureCode);
            if (currentProcedure is null)
            {
                return Invalid(HistoricalMetadataCorrectionOutcome.InvalidProcedure);
            }
            if (currentProcedure.SedationEligible == governedProcedure.SedationEligible)
            {
                return Invalid(HistoricalMetadataCorrectionOutcome.PairedCorrectionNotRequired);
            }

            var effective = context.Projection.EffectiveMetadata with
            {
                ProcedureCode = governedProcedure.Code,
                SedationState = sedationState
            };
            if (!IsCoherent(effective, context.RequiresCompleteReadyAssignment))
            {
                return Invalid(HistoricalMetadataCorrectionOutcome.InvalidSedation);
            }

            var state = context.State with
            {
                OverrideProcedureCode = TokenEquals(governedProcedure.Code, context.Projection.OriginalMetadata.ProcedureCode)
                    ? null
                    : governedProcedure.Code,
                OverrideSedationState = sedationState == context.Projection.OriginalMetadata.SedationState
                    ? null
                    : sedationState,
                AdministrativeRevision = expectedRevision + 1
            };
            return Mutation(
                state,
                HistoricalMetadataCorrectionFields.ProcedureAndSedation,
                SerializeProcedureAndSedation(
                    context.Projection.EffectiveMetadata.ProcedureCode,
                    context.Projection.EffectiveMetadata.SedationState.Value),
                SerializeProcedureAndSedation(governedProcedure.Code, sedationState),
                note);
        });
    }

    public HistoricalMetadataCorrectionResult CorrectSedation(
        HistoricalEncounterKey key,
        int expectedRevision,
        SedationState sedationState,
        string? note = null)
    {
        var inputValidation = ValidateInput(key, expectedRevision, note);
        if (inputValidation is not null) return inputValidation;
        if (!IsExplicitHistoricalSedation(sedationState))
        {
            return new(HistoricalMetadataCorrectionOutcome.InvalidSedation);
        }

        return Correct(key, expectedRevision, note, context =>
        {
            if (context.Projection.OriginalMetadata.SedationState is null
                || context.Projection.EffectiveMetadata.ProcedureCode is null)
            {
                return Invalid(HistoricalMetadataCorrectionOutcome.UnsupportedCorrection);
            }

            var procedure = FindProcedure(context.Projection.EffectiveMetadata.ProcedureCode);
            if (procedure is null || !IsSedationCoherent(procedure, sedationState))
            {
                return Invalid(HistoricalMetadataCorrectionOutcome.InvalidSedation);
            }

            var effective = context.Projection.EffectiveMetadata with { SedationState = sedationState };
            if (!IsCoherent(effective, context.RequiresCompleteReadyAssignment))
            {
                return Invalid(HistoricalMetadataCorrectionOutcome.InvalidSedation);
            }

            var state = context.State with
            {
                OverrideSedationState = sedationState == context.Projection.OriginalMetadata.SedationState
                    ? null
                    : sedationState,
                AdministrativeRevision = expectedRevision + 1
            };
            return Mutation(
                state,
                HistoricalMetadataCorrectionFields.Sedation,
                effective.SedationState!.Value.ToString(),
                sedationState.ToString(),
                note);
        });
    }

    public HistoricalMetadataCorrectionResult CorrectAddOn(
        HistoricalEncounterKey key,
        int expectedRevision,
        bool isAddOn,
        string? note = null)
    {
        var inputValidation = ValidateInput(key, expectedRevision, note);
        if (inputValidation is not null) return inputValidation;
        return Correct(key, expectedRevision, note, context =>
        {
            var effective = context.Projection.EffectiveMetadata with { IsAddOn = isAddOn };
            if (!IsCoherent(effective, context.RequiresCompleteReadyAssignment))
            {
                return Invalid(HistoricalMetadataCorrectionOutcome.UnsupportedCorrection);
            }

            var state = context.State with
            {
                OverrideIsAddOn = isAddOn == context.Projection.OriginalMetadata.IsAddOn ? null : isAddOn,
                AdministrativeRevision = expectedRevision + 1
            };
            return Mutation(
                state,
                HistoricalMetadataCorrectionFields.AddOn,
                SerializeBoolean(context.Projection.EffectiveMetadata.IsAddOn),
                SerializeBoolean(isAddOn),
                note);
        });
    }

    public HistoricalMetadataCorrectionResult CorrectExpectedAllocation(
        HistoricalEncounterKey key,
        int expectedRevision,
        ExpectedAllocationState allocationState,
        int? suggestedUnits,
        int? confirmedUnits,
        string? note = null)
    {
        var inputValidation = ValidateInput(key, expectedRevision, note);
        if (inputValidation is not null) return inputValidation;
        var allocation = TryCreateConfirmedAllocation(allocationState, suggestedUnits, confirmedUnits);
        if (allocation is null)
        {
            return new(HistoricalMetadataCorrectionOutcome.InvalidExpectedAllocation);
        }

        return Correct(key, expectedRevision, note, context =>
        {
            if (context.Projection.OriginalMetadata.ProcedureCode is null
                || context.Projection.OriginalMetadata.ExpectedAllocation is null
                || context.Projection.EffectiveMetadata.ExpectedAllocation is null)
            {
                return Invalid(HistoricalMetadataCorrectionOutcome.UnsupportedCorrection);
            }

            var effective = context.Projection.EffectiveMetadata with { ExpectedAllocation = allocation };
            if (!IsCoherent(effective, context.RequiresCompleteReadyAssignment))
            {
                return Invalid(HistoricalMetadataCorrectionOutcome.InvalidExpectedAllocation);
            }

            var matchesOriginal = AllocationEquals(allocation, context.Projection.OriginalMetadata.ExpectedAllocation);
            var state = context.State with
            {
                OverrideExpectedAllocationState = matchesOriginal ? null : allocation.State,
                OverrideExpectedAllocationSuggestedUnits = matchesOriginal ? null : allocation.SuggestedValue,
                OverrideExpectedAllocationConfirmedUnits = matchesOriginal ? null : allocation.ConfirmedValue,
                AdministrativeRevision = expectedRevision + 1
            };
            return Mutation(
                state,
                HistoricalMetadataCorrectionFields.ExpectedAllocation,
                SerializeAllocation(context.Projection.EffectiveMetadata.ExpectedAllocation),
                SerializeAllocation(allocation),
                note);
        });
    }

    private HistoricalMetadataCorrectionResult Correct(
        HistoricalEncounterKey key,
        int expectedRevision,
        string? note,
        Func<CorrectionContext, HistoricalMetadataCorrectionResult> createMutation)
    {
        if (!key.IsValid || expectedRevision < 0)
        {
            return new(HistoricalMetadataCorrectionOutcome.InvalidSource);
        }
        if (note?.Length > HistoricalAnomalyAdministrationService.MaximumNoteLength)
        {
            return new(HistoricalMetadataCorrectionOutcome.InvalidNote);
        }

        var source = _repository.LoadHistoricalEncounter(key);
        if (source is null)
        {
            return new(HistoricalMetadataCorrectionOutcome.NotFound);
        }
        var current = _repository.LoadHistoricalAdministrativeState(key);
        var currentRevision = current?.AdministrativeRevision ?? 0;
        if (currentRevision != expectedRevision)
        {
            return new(
                HistoricalMetadataCorrectionOutcome.StaleWrite,
                State: current,
                CurrentRevision: currentRevision);
        }
        if (current?.Disposition != HistoricalAdministrativeDispositions.NeedsReview)
        {
            return new(
                HistoricalMetadataCorrectionOutcome.ReviewNotPending,
                State: current,
                CurrentRevision: currentRevision);
        }

        var projection = BuildProjection(source, current);
        var candidate = createMutation(new CorrectionContext(
            projection,
            current,
            projection.OriginalEvidenceAuthority is HistoricalMetadataEvidenceAuthority.AcceptedReadyHandoff
                or HistoricalMetadataEvidenceAuthority.TerminalReadyHandoff));
        if (candidate.Outcome != HistoricalMetadataCorrectionOutcome.Success)
        {
            return candidate with { Encounter = projection, State = current, CurrentRevision = currentRevision };
        }

        var persisted = _repository.PersistHistoricalAdministrativeStateAndLedgerGuarded(
            expectedRevision,
            candidate.State!,
            candidate.LedgerEvent!);
        return persisted.Outcome switch
        {
            GuardedHistoricalAdministrativePersistenceOutcome.Success => new(
                HistoricalMetadataCorrectionOutcome.Success,
                BuildProjection(source, persisted.CommittedWrite!.State),
                persisted.CommittedWrite.State,
                persisted.CommittedWrite.LedgerEvent,
                persisted.CommittedWrite.State.AdministrativeRevision),
            GuardedHistoricalAdministrativePersistenceOutcome.NotFound => new(
                HistoricalMetadataCorrectionOutcome.NotFound),
            _ => new(
                HistoricalMetadataCorrectionOutcome.StaleWrite,
                State: persisted.CurrentState,
                CurrentRevision: persisted.CurrentRevision)
        };
    }

    private static HistoricalMetadataCorrectionResult? ValidateInput(
        HistoricalEncounterKey key,
        int expectedRevision,
        string? note)
    {
        if (!key.IsValid || expectedRevision < 0)
        {
            return new(HistoricalMetadataCorrectionOutcome.InvalidSource);
        }
        if (note?.Length > HistoricalAnomalyAdministrationService.MaximumNoteLength)
        {
            return new(HistoricalMetadataCorrectionOutcome.InvalidNote);
        }
        return null;
    }

    private HistoricalMetadataCorrectionResult Mutation(
        HistoricalEncounterAdministrativeState state,
        string field,
        string previousValue,
        string newValue,
        string? note) =>
        new(
            HistoricalMetadataCorrectionOutcome.Success,
            State: state,
            LedgerEvent: new HistoricalEncounterAdministrativeLedgerEvent(
                LedgerId: 0,
                Key: state.Key,
                EventType: HistoricalAdministrativeLedgerEventTypes.MetadataCorrected,
                OccurredAt: _timeProvider.GetUtcNow(),
                ActorClass: HistoricalAdministrativeActorClasses.LocalAdmin,
                ReasonSource: HistoricalAdministrativeReasonSources.LocalAdmin,
                StructuredReason: field,
                PreviousValue: previousValue,
                NewValue: newValue,
                AdminNote: note,
                AdministrativeRevision: state.AdministrativeRevision),
            CurrentRevision: state.AdministrativeRevision);

    private HistoricalEffectiveEncounter BuildProjection(
        HistoricalEncounterRecord source,
        HistoricalEncounterAdministrativeState? state)
    {
        var original = ReadOriginalMetadata(source);
        var overrides = ReadOverrides(state);
        var effective = new HistoricalEncounterMetadata(
            overrides.DoctorId ?? original.Metadata.DoctorId,
            overrides.ProcedureCode ?? original.Metadata.ProcedureCode,
            overrides.SedationState ?? original.Metadata.SedationState,
            overrides.IsAddOn ?? original.Metadata.IsAddOn,
            overrides.ExpectedAllocation ?? original.Metadata.ExpectedAllocation);
        return new HistoricalEffectiveEncounter(
            source.Key,
            source,
            original.Authority,
            original.ReadyHandoffId,
            original.Metadata,
            effective,
            overrides,
            new HistoricalMetadataCorrectionIndicators(
                overrides.DoctorId is not null,
                overrides.ProcedureCode is not null,
                overrides.SedationState is not null,
                overrides.IsAddOn.HasValue,
                overrides.ExpectedAllocation is not null),
            new HistoricalMetadataCorrectionSupport(
                original.Metadata.DoctorId is not null,
                original.Metadata.ProcedureCode is not null && original.Metadata.SedationState is not null,
                original.Metadata.ProcedureCode is not null && original.Metadata.SedationState is not null,
                original.Metadata.ProcedureCode is not null && original.Metadata.SedationState is not null,
                AddOn: true,
                original.Metadata.ProcedureCode is not null && original.Metadata.ExpectedAllocation is not null),
            state?.Disposition ?? HistoricalAdministrativeDispositions.NoAnomaly,
            state?.CurrentReason,
            state?.ReasonSource,
            state?.AdministrativeRevision ?? 0);
    }

    private OriginalMetadataEvidence ReadOriginalMetadata(HistoricalEncounterRecord source)
    {
        if (source.CompletedCycle is { } completed)
        {
            var handoff = LoadReadyEvidence(
                completed.AcceptedReadyHandoffId,
                completed.EpisodeId,
                completed.RoomId,
                requireAccepted: true);
            return handoff is not null
                ? new(
                    HistoricalMetadataEvidenceAuthority.AcceptedReadyHandoff,
                    handoff.HandoffId,
                    FromAssignment(handoff.Assignment))
                : new(
                    HistoricalMetadataEvidenceAuthority.CompletedCycle,
                    ReadyHandoffId: null,
                    new HistoricalEncounterMetadata(
                        NullIfBlank(completed.AssignedDoctor),
                        NormalizeProcedureCode(completed.ProcedureCode),
                        SedationState: null,
                        completed.IsAddOn,
                        TryCreateLegacyCompletedAllocation(completed)));
        }

        var aborted = source.AbortedAssignment!;
        var terminal = LoadReadyEvidence(
            aborted.TerminalReadyHandoffId,
            aborted.EpisodeId,
            aborted.RoomId,
            requireAccepted: false);
        return terminal is not null
            ? new(
                HistoricalMetadataEvidenceAuthority.TerminalReadyHandoff,
                terminal.HandoffId,
                FromAssignment(terminal.Assignment))
            : new(
                HistoricalMetadataEvidenceAuthority.AbortedAssignment,
                ReadyHandoffId: null,
                new HistoricalEncounterMetadata(
                    NullIfBlank(aborted.AssignedDoctor),
                    NormalizeProcedureCode(aborted.ProcedureCode),
                    aborted.SedationState,
                    aborted.IsAddOn,
                    TryCreateAllocation(
                        aborted.ExpectedAllocationState,
                        aborted.ExpectedAllocationSuggestedUnits,
                        aborted.ExpectedAllocationConfirmedUnits)));
    }

    private PersistedReadyHandoff? LoadReadyEvidence(
        string? handoffId,
        string? episodeId,
        int roomId,
        bool requireAccepted)
    {
        if (string.IsNullOrWhiteSpace(handoffId) || string.IsNullOrWhiteSpace(episodeId)) return null;
        var handoff = _repository.LoadReadyHandoff(handoffId);
        if (handoff is null
            || handoff.RoomId != roomId
            || !string.Equals(handoff.EpisodeId, episodeId, StringComparison.Ordinal))
        {
            return null;
        }
        return requireAccepted
            ? handoff.AcceptedAt.HasValue && !handoff.WithdrawnAt.HasValue && !handoff.TerminatedAt.HasValue
                ? handoff
                : null
            : handoff.TerminatedAt.HasValue && !handoff.AcceptedAt.HasValue && !handoff.WithdrawnAt.HasValue
                ? handoff
                : null;
    }

    private static HistoricalEncounterMetadata FromAssignment(PersistedRoomAssignment assignment) =>
        new(
            NullIfBlank(assignment.DoctorId),
            NormalizeProcedureCode(assignment.ProcedureCode),
            assignment.SedationState,
            assignment.IsAddOn,
            TryCreateAllocation(
                assignment.ExpectedAllocationState,
                assignment.ExpectedAllocationSuggestedUnits,
                assignment.ExpectedAllocationConfirmedUnits));

    private static HistoricalEncounterMetadataOverrides ReadOverrides(
        HistoricalEncounterAdministrativeState? state) =>
        new(
            state?.OverrideDoctorId,
            state?.OverrideProcedureCode,
            state?.OverrideSedationState,
            state?.OverrideIsAddOn,
            TryCreateAllocation(
                state?.OverrideExpectedAllocationState,
                state?.OverrideExpectedAllocationSuggestedUnits,
                state?.OverrideExpectedAllocationConfirmedUnits));

    private bool IsCoherent(HistoricalEncounterMetadata metadata, bool requireCompleteReadyAssignment)
    {
        if (metadata.ProcedureCode is null)
        {
            if (metadata.SedationState is not null and not SedationState.UnavailableNoProcedure)
            {
                return false;
            }
            if (metadata.ExpectedAllocation is { State: not ExpectedAllocationState.Unknown })
            {
                return false;
            }
        }
        else if (FindProcedure(metadata.ProcedureCode) is { } procedure
            && metadata.SedationState is { } sedation
            && !IsSedationCoherent(procedure, sedation))
        {
            return false;
        }

        if (requireCompleteReadyAssignment)
        {
            return metadata.DoctorId is not null
                && metadata.ProcedureCode is not null
                && metadata.SedationState is { } readySedation
                && (readySedation is SedationState.UnavailableProcedureIneligible
                    or SedationState.EligibleYes
                    or SedationState.EligibleNo)
                && metadata.ExpectedAllocation?.SatisfiesAssignmentCompleteness == true;
        }

        return true;
    }

    private static bool IsSedationCoherent(ProcedureRosterItem procedure, SedationState sedationState) =>
        procedure.SedationEligible
            ? sedationState is SedationState.EligibleYes or SedationState.EligibleNo or SedationState.EligibleUnresolved
            : sedationState == SedationState.UnavailableProcedureIneligible;

    private static bool IsExplicitHistoricalSedation(SedationState state) =>
        Enum.IsDefined(state)
        && state is SedationState.UnavailableProcedureIneligible
            or SedationState.EligibleYes
            or SedationState.EligibleNo;

    private DoctorRosterItem? FindDoctor(string? doctorId) =>
        string.IsNullOrWhiteSpace(doctorId)
            ? null
            : _doctors.FirstOrDefault(item =>
                string.Equals(item.Id, doctorId.Trim(), StringComparison.OrdinalIgnoreCase));

    private ProcedureRosterItem? FindProcedure(string? procedureCode)
    {
        var normalized = NullIfBlank(procedureCode);
        if (normalized is null
            || normalized.EndsWith(SedationModifierSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return _procedures.FirstOrDefault(item =>
            string.Equals(item.Code, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static ExpectedAllocationContract? TryCreateConfirmedAllocation(
        ExpectedAllocationState state,
        int? suggestedUnits,
        int? confirmedUnits)
    {
        if (!Enum.IsDefined(state)) return null;
        var allocation = TryCreateAllocation(state, suggestedUnits, confirmedUnits);
        return allocation?.SatisfiesAssignmentCompleteness == true ? allocation : null;
    }

    private static ExpectedAllocationContract? TryCreateAllocation(
        ExpectedAllocationState? state,
        int? suggestedUnits,
        int? confirmedUnits)
    {
        if (state is null) return null;
        try
        {
            return ExpectedAllocationContract.Create(state.Value, suggestedUnits, confirmedUnits);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static ExpectedAllocationContract? TryCreateLegacyCompletedAllocation(CompletedRoomCycle cycle)
    {
        if (cycle.ExpectedAllocationUnits <= 0) return null;
        var suggested = cycle.OriginalDefaultExpectedUnits > 0
            ? cycle.OriginalDefaultExpectedUnits
            : (int?)null;
        return suggested == cycle.ExpectedAllocationUnits
            ? ExpectedAllocationContract.ConfirmedSuggestedValue(cycle.ExpectedAllocationUnits)
            : ExpectedAllocationContract.ConfirmedAdjustedValue(suggested, cycle.ExpectedAllocationUnits);
    }

    private static bool AllocationEquals(
        ExpectedAllocationContract left,
        ExpectedAllocationContract? right) =>
        right is not null
        && left.State == right.State
        && left.SuggestedValue == right.SuggestedValue
        && left.ConfirmedValue == right.ConfirmedValue;

    private static bool TokenEquals(string left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeProcedureCode(string? procedureCode)
    {
        var normalized = NullIfBlank(procedureCode);
        return normalized?.EndsWith(SedationModifierSuffix, StringComparison.OrdinalIgnoreCase) == true
            ? normalized[..^SedationModifierSuffix.Length]
            : normalized;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string SerializeBoolean(bool value) => value ? "true" : "false";

    private static string SerializeProcedureAndSedation(string procedureCode, SedationState sedationState) =>
        JsonSerializer.Serialize(new ProcedureSedationLedgerValue(procedureCode, sedationState.ToString()));

    private static string SerializeAllocation(ExpectedAllocationContract allocation) =>
        JsonSerializer.Serialize(new ExpectedAllocationLedgerValue(
            allocation.State.ToString(),
            allocation.SuggestedValue,
            allocation.ConfirmedValue));

    private static HistoricalMetadataCorrectionResult Invalid(HistoricalMetadataCorrectionOutcome outcome) =>
        new(outcome);

    private sealed record OriginalMetadataEvidence(
        HistoricalMetadataEvidenceAuthority Authority,
        string? ReadyHandoffId,
        HistoricalEncounterMetadata Metadata);

    private sealed record CorrectionContext(
        HistoricalEffectiveEncounter Projection,
        HistoricalEncounterAdministrativeState State,
        bool RequiresCompleteReadyAssignment);

    private sealed record ProcedureSedationLedgerValue(
        [property: JsonPropertyName("procedure")] string Procedure,
        [property: JsonPropertyName("sedation")] string Sedation);

    private sealed record ExpectedAllocationLedgerValue(
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("suggestedUnits")] int? SuggestedUnits,
        [property: JsonPropertyName("confirmedUnits")] int? ConfirmedUnits);
}
