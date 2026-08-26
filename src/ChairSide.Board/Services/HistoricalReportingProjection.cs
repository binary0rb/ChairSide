namespace ChairSide.Board.Services;

/// <summary>
/// Canonical report-time interpretation of one immutable historical source. Persistence populates
/// this projection with set-based joins; report builders apply it only to detached copies before
/// deriving analytical annotations. The source row and accepted Ready handoff remain unchanged.
/// </summary>
internal sealed record HistoricalReportingProjection(
    string Disposition,
    string? EffectiveDoctorId,
    string? EffectiveProcedureCode,
    SedationState? EffectiveSedationState,
    bool HasExplicitSedationEvidence,
    bool PreserveLegacySedationTransport,
    bool EffectiveIsAddOn,
    ExpectedAllocationState? EffectiveExpectedAllocationState,
    int? EffectiveExpectedAllocationSuggestedUnits,
    int? EffectiveExpectedAllocationConfirmedUnits,
    string? CurrentReason,
    string? ReasonSource,
    DateTimeOffset? KnownReviewedAt,
    string? KnownReviewedActorClass,
    int AdministrativeRevision,
    bool HasHistoricalCorrectionProvenance,
    bool HasReviewedProvenance)
{
    private const string SedationModifierSuffix = "+SED";
    private const string LegacySedationCode = "SED";

    public bool IsAdministrativelyExcluded =>
        Disposition is HistoricalAdministrativeDispositions.NeedsReview
            or HistoricalAdministrativeDispositions.ConfirmedException;

    public bool IsAnomaly => Disposition != HistoricalAdministrativeDispositions.NoAnomaly;

    public bool RequiresReview => Disposition == HistoricalAdministrativeDispositions.NeedsReview;

    public bool IsSedationCaseForNormalReporting => EffectiveSedationState == SedationState.EligibleYes
        || (!HasExplicitSedationEvidence
            && PreserveLegacySedationTransport
            && IsLegacySedationProcedureCode(EffectiveProcedureCode));

    public bool MatchesAnalyticalScope(ReportQuery query, bool requireExplicitSedation)
    {
        if (query.Scope == ReportScopeKinds.Doctor
            && (query.DoctorId is null
                || !string.Equals(EffectiveDoctorId, query.DoctorId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (query.Sedation == ReportSedationSegments.All)
        {
            return true;
        }

        if (!HasExplicitSedationEvidence)
        {
            if (requireExplicitSedation || !PreserveLegacySedationTransport)
            {
                return false;
            }

            var legacySedation = IsLegacySedationProcedureCode(EffectiveProcedureCode);
            return query.Sedation == ReportSedationSegments.Sedation
                ? legacySedation
                : !legacySedation;
        }

        return query.Sedation == ReportSedationSegments.Sedation
            ? EffectiveSedationState == SedationState.EligibleYes
            : EffectiveSedationState is SedationState.EligibleNo
                or SedationState.UnavailableProcedureIneligible;
    }

    public void ApplyTo(CompletedRoomCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        cycle.ReportingProjection = this;
        cycle.AssignedDoctor = EffectiveDoctorId ?? "";
        cycle.ProcedureCode = BuildDetailedProcedureCode();
        cycle.IsAddOn = EffectiveIsAddOn;
        ApplyAllocation(
            EffectiveExpectedAllocationState,
            EffectiveExpectedAllocationSuggestedUnits,
            EffectiveExpectedAllocationConfirmedUnits,
            (_, confirmed, adjusted) =>
            {
                cycle.ExpectedAllocationUnits = confirmed;
                cycle.ExpectedAllocationMinutes = confirmed * 10;
                cycle.AllocationAdjustedFromDefault = adjusted;
            });
        cycle.IsException = IsAnomaly;
        cycle.RequiresReview = RequiresReview;
        cycle.ExceptionReason = CurrentReason;
        cycle.ReviewStatus = RequiresReview
            ? ReviewStatuses.PendingReview
            : IsAnomaly ? ReviewStatuses.Reviewed : ReviewStatuses.PendingReview;
        cycle.ReviewedAt = KnownReviewedAt;
        cycle.ReviewedBy = KnownReviewedActorClass;
    }

    public void ApplyTo(AbortedRoomAssignment record)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.ReportingProjection = this;
        record.AssignedDoctor = EffectiveDoctorId;
        record.ProcedureCode = BuildDetailedProcedureCode();
        record.SedationState = EffectiveSedationState;
        record.IsAddOn = EffectiveIsAddOn;
        record.ExpectedAllocationState = EffectiveExpectedAllocationState;
        record.ExpectedAllocationSuggestedUnits = EffectiveExpectedAllocationSuggestedUnits;
        record.ExpectedAllocationConfirmedUnits = EffectiveExpectedAllocationConfirmedUnits;
        ApplyAllocation(
            EffectiveExpectedAllocationState,
            EffectiveExpectedAllocationSuggestedUnits,
            EffectiveExpectedAllocationConfirmedUnits,
            (_, confirmed, adjusted) =>
            {
                record.ExpectedAllocationUnits = confirmed;
                record.ExpectedAllocationMinutes = confirmed * 10;
                record.AllocationAdjustedFromDefault = adjusted;
            });
        record.IsException = IsAnomaly;
        record.RequiresReview = RequiresReview;
        record.ExceptionReason = CurrentReason;
        record.ReviewStatus = RequiresReview
            ? ReviewStatuses.PendingReview
            : IsAnomaly ? ReviewStatuses.Reviewed : ReviewStatuses.PendingReview;
        record.ReviewedAt = KnownReviewedAt;
        record.ReviewedBy = KnownReviewedActorClass;
    }

    public static HistoricalReportingProjection FromSource(CompletedRoomCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        var allocation = LegacyCompletedAllocation(cycle);
        return new(
            HistoricalAdministrativeDispositions.NoAnomaly,
            NullIfBlank(cycle.AssignedDoctor),
            NullIfBlank(cycle.ProcedureCode),
            EffectiveSedationState: null,
            HasExplicitSedationEvidence: false,
            PreserveLegacySedationTransport: true,
            cycle.IsAddOn,
            allocation.State,
            allocation.Suggested,
            allocation.Confirmed,
            CurrentReason: null,
            ReasonSource: null,
            KnownReviewedAt: null,
            KnownReviewedActorClass: null,
            AdministrativeRevision: 0,
            HasHistoricalCorrectionProvenance: false,
            HasReviewedProvenance: false);
    }

    public static HistoricalReportingProjection FromSource(AbortedRoomAssignment record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new(
            HistoricalAdministrativeDispositions.NoAnomaly,
            NullIfBlank(record.AssignedDoctor),
            NullIfBlank(record.ProcedureCode),
            record.SedationState,
            record.SedationState.HasValue,
            PreserveLegacySedationTransport: false,
            record.IsAddOn,
            record.ExpectedAllocationState,
            record.ExpectedAllocationSuggestedUnits,
            record.ExpectedAllocationConfirmedUnits,
            CurrentReason: null,
            ReasonSource: null,
            KnownReviewedAt: null,
            KnownReviewedActorClass: null,
            AdministrativeRevision: 0,
            HasHistoricalCorrectionProvenance: false,
            HasReviewedProvenance: false);
    }

    private string BuildDetailedProcedureCode()
    {
        var procedure = EffectiveProcedureCode ?? "";
        if (!HasExplicitSedationEvidence || PreserveLegacySedationTransport)
        {
            return procedure;
        }

        var baseProcedure = procedure.EndsWith(SedationModifierSuffix, StringComparison.OrdinalIgnoreCase)
            ? procedure[..^SedationModifierSuffix.Length]
            : procedure;
        if (baseProcedure.Length == 0)
        {
            return "";
        }

        return EffectiveSedationState == SedationState.EligibleYes
            && !string.Equals(baseProcedure, LegacySedationCode, StringComparison.OrdinalIgnoreCase)
                ? baseProcedure + SedationModifierSuffix
                : baseProcedure;
    }

    private static void ApplyAllocation(
        ExpectedAllocationState? state,
        int? suggested,
        int? confirmed,
        Action<int, int, bool> apply)
    {
        if (state is ExpectedAllocationState.ConfirmedSuggestedValue
                or ExpectedAllocationState.ConfirmedAdjustedValue
            && confirmed is > 0)
        {
            apply(
                suggested.GetValueOrDefault(),
                confirmed.Value,
                state == ExpectedAllocationState.ConfirmedAdjustedValue);
            return;
        }

        apply(0, 0, false);
    }

    private static (ExpectedAllocationState? State, int? Suggested, int? Confirmed)
        LegacyCompletedAllocation(CompletedRoomCycle cycle)
    {
        if (cycle.ExpectedAllocationUnits <= 0)
        {
            return (null, null, null);
        }

        var suggested = cycle.OriginalDefaultExpectedUnits > 0
            ? cycle.OriginalDefaultExpectedUnits
            : (int?)null;
        return suggested == cycle.ExpectedAllocationUnits
            ? (ExpectedAllocationState.ConfirmedSuggestedValue, cycle.ExpectedAllocationUnits, cycle.ExpectedAllocationUnits)
            : (ExpectedAllocationState.ConfirmedAdjustedValue, suggested, cycle.ExpectedAllocationUnits);
    }

    private static bool IsLegacySedationProcedureCode(string? procedureCode) =>
        !string.IsNullOrWhiteSpace(procedureCode)
        && (procedureCode.EndsWith(SedationModifierSuffix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(procedureCode, LegacySedationCode, StringComparison.OrdinalIgnoreCase));

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
