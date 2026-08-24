# ChairSide knowledge graph

This hand-authored map records ChairSide's important concepts and relationships. Generated inventory files help locate symbols but are not runtime inputs or substitutes for this intent.

## Conceptual graph

```mermaid
flowchart LR
    LightBoardReplacement["DomainConcept: Replace light board / pager"] --> BoardUi["UiSurface: ChairSide board"]
    BoardUi --> RoomCards["UiSurface: Room cards"]
    BoardUi --> RoomPanel["UiSurface: Room panel"]
    BoardUi --> DoctorView["UiSurface: Doctor read-only view"]
    RoomPanel --> RoomStatusContext["UiSurface: Room status and correction context"]
    RoomStatusContext --> PrimaryWorkflowPanel["UiSurface: Primary workflow panel below status"]
    RoomPanel --> AssignmentSetup["UiSurface: Assignment setup controls"]

    RoomLifecycle["DomainConcept: Room episode lifecycle"] --> BeginPrestage["LifecycleEvent: Begin Prestage"]
    RoomLifecycle --> SaveDetails["LifecycleEvent: Save Details"]
    RoomLifecycle --> Seat["LifecycleEvent: Seat"]
    RoomLifecycle --> Ready["LifecycleEvent: Ready for Doctor"]
    RoomLifecycle --> Withdraw["LifecycleEvent: Withdraw Ready"]
    RoomLifecycle --> DoctorArrived["LifecycleEvent: Doctor Arrived"]
    RoomLifecycle --> DoctorComplete["LifecycleEvent: Doctor Complete"]
    RoomLifecycle --> RoomAvailable["LifecycleEvent: Room Available"]

    BeginPrestage --> Prestaging["WorkflowState: Prestaging"]
    Seat --> Seated["WorkflowState: Seated / In Prep"]
    Ready --> ReadyState["WorkflowState: ReadyForDoctor"]
    ReadyState --> ReadyUrgency["DomainConcept: Ready urgency None / Aging / Stale"]
    Withdraw --> Seated
    DoctorArrived --> InRoom["WorkflowState: Doctor In Room"]
    DoctorComplete --> Turnover["WorkflowState: Turnover"]
    RoomAvailable --> Available["WorkflowState: Available"]

    SaveDetails --> CanonicalAssignment["DomainConcept: Canonical assignment draft"]
    Seat --> CanonicalAssignment
    CanonicalAssignment --> CasGuard["DesignDecision: Persistence compare-and-swap"]
    Ready --> ReadyHandoff["DomainConcept: Immutable Ready handoff"]
    ReadyHandoff --> ReadyUrgency
    Withdraw --> WithdrawnHandoff["DomainConcept: Withdrawn handoff history"]
    DoctorArrived --> AcceptedHandoff["DomainConcept: Accepted reporting assignment"]

    BoardStore["StoreOrService: Board store"] --> RoomLifecycle
    BoardStore --> Persistence["StoreOrService: SQLite persistence"]
    BoardStore --> IntegrityAuthority["DesignDecision: Repository-aware integrity authority"]
    IntegrityAuthority --> RoomCapabilities["Contract: Server-projected room capabilities"]
    RoomCapabilities --> BoardUi
    BrowserDraftGuards["DesignDecision: Browser-only unsaved draft guards"] --> BoardUi
    Mutations["LifecycleEvent: Room-local writes"] --> ServerEnforcement["DesignDecision: Endpoint and store enforcement"]
    Persistence --> ActiveRooms["PersistenceModel: active_rooms"]
    Persistence --> Handoffs["PersistenceModel: ready_handoffs"]
    Persistence --> Aborts["PersistenceModel: aborted_room_assignments"]
    Persistence --> Cycles["PersistenceModel: completed_room_cycles"]
    CasGuard --> ActiveRooms

    PreArrivalTermination["DomainConcept: Pre-arrival cancellation / expiration"] --> Aborts
    AfterHoursSweep["DomainConcept: Nightly after-hours safety sweep"] --> Aborts
    AfterHoursSweep --> SystemFinding["LifecycleEvent: Objective system finding"]
    PostArrivalExpiration["DomainConcept: Post-arrival expiration"] --> SystemFinding
    PostArrivalExpiration --> Cycles
    HistoricalEncounter["DomainConcept: Durable historical encounter"] --> ReviewLedger["DomainConcept: Append-only administrative ledger"]
    Aborts --> HistoricalEncounter
    Cycles --> HistoricalEncounter
    ManualReview["LifecycleEvent: Local Admin Mark for Review"] --> ReviewLedger
    SystemFinding --> ReviewLedger
    ReviewLedger --> NeedsReview["ReportPopulation: Needs Review"]
    ReviewLedger --> CorrectionOverlay["DomainConcept: Historical effective-value correction"]
    ReviewLedger --> Cleared["ReportPopulation: Cleared for Reporting"]
    ReviewLedger --> ConfirmedException["ReportPopulation: Confirmed Exception"]
    ReviewLedger --> ReopenReview["LifecycleEvent: Reopen Review"]
    ReopenReview --> NeedsReview
    NeedsReview --> AdministrativeExclusion["DesignDecision: Administrative exclusion gate"]
    ConfirmedException --> AdministrativeExclusion

    Reports["UiSurface: Reports"] --> SummaryCards["UiSurface: Summary cards"]
    ReportsBuilder["StoreOrService: Report snapshot builder"] --> Reports
    AcceptedHandoff --> StandardPopulation["ReportMetric: Standard completed population"]
    AcceptedHandoff --> ImmutableEvidence["DesignDecision: Immutable lifecycle evidence"]
    CorrectionOverlay --> EffectiveReportingValues["DomainConcept: Current effective reporting values"]
    EffectiveReportingValues --> ReportsBuilder
    Cleared --> OrdinaryEligibility["DesignDecision: Ordinary reporting eligibility re-evaluated"]
    OrdinaryEligibility --> ReportsBuilder
    StandardPopulation --> ReportsBuilder
    WithdrawnHandoff --> AuditOnly["DesignDecision: Not accepted attribution"]
    Aborts --> OutsideThroughput["DesignDecision: Outside throughput"]

    Reports --> ReportingDesign["DesignDecision: Canonical reporting design"]
    Reports --> ExceptionHandlingDesign["DesignDecision: Canonical exception handling design"]
    ExceptionHandlingDesign --> ReviewLedger
    AcceptedHandoff --> ReadyWait["ReportMetric: Accepted Ready Wait"]
    ReportingDesign --> SeatedDoctor["ReportMetric: Seated to Doctor"]
    ReportingDesign --> MedianFirst["DesignDecision: Median-first prominent timing"]
    ReportingDesign --> ObservedDoctorDay["ReportMetric: Observed Doctor Day"]
    ObservedDoctorDay --> ObservedClinicalSpan["ReportMetric: Observed Clinical Span"]
    ObservedDoctorDay --> DoctorTrends["UiSurface: Doctor weekly trends"]
    StandardPopulation --> DoctorTrends
    Reports --> DoctorTrends
    ObservedClinicalSpan --> DoctorWorkingUnion["ReportMetric: Doctor Working wall-clock union"]
    DoctorWorkingUnion --> ConcurrencyBuckets["ReportMetric: Doctor Working concurrency buckets"]
    ObservedClinicalSpan --> UnstructuredTime["ReportMetric: Unstructured Time"]
    StandardPopulation --> ProcedureMix["ReportMetric: Practice and doctor Procedure Mix"]
    ProcedureMix --> ProcedureIntelligence["UiSurface: Procedure Intelligence"]
    ProcedureIntelligence --> TypicalDoctorTimeRange["ReportMetric: Type 7 Q25-Q75 Doctor Time range"]
    ProcedureIntelligence --> ProcedureDoctorBreakdown["UiSurface: Doctor x procedure disclosure"]
    ProcedureIntelligence --> ProcedureAllocationContext["ReportMetric: Neutral expected allocation context"]
    StandardPopulation --> ScheduleFit["ReportMetric: Schedule Fit"]
    ScheduleFit --> HistoricalAssignedFit["ReportMetric: Exact historical assigned fit"]
    ProcedureRoster --> CurrentDefaultCalibration["ReportMetric: Current roster default calibration"]
    StandardPopulation --> CurrentDefaultCalibration
    CurrentDefaultCalibration --> CalibrationRules["DesignDecision: N10, raw-sign 80 percent, strict 600-second median"]
    CurrentDefaultCalibration --> CalibrationEvidence["Contract: Qualified non-PHI case evidence"]
    ScheduleFit --> CalibrationInsights["UiSurface: Calibration Insights"]
    CurrentDefaultCalibration --> CalibrationInsights
    Reports --> DataQualityAudit["UiSurface: Data Quality and case audit"]
    ReportScope --> DataQualityAudit
    ReviewLedger --> DataQualityAudit
    DataQualityAudit --> AuditQuery["Contract: Protected read-only paged audit query"]
    AuditQuery --> CompletedAudit["ReportPopulation: Normal completed history"]
    AuditQuery --> MetricEvidence["ReportPopulation: Exact metric contributors"]
    AuditQuery --> ExceptionEvidence["ReportPopulation: Anomaly and ledger history"]
    CalibrationEvidence --> AuditQuery
    ReportScope --> AuditQuery

    DoctorRoster["ConfigOption: Doctor roster"] --> CanonicalAssignment
    ProcedureRoster["ConfigOption: Procedure roster"] --> CanonicalAssignment
    SedationModifier["DomainConcept: Sedation modifier"] --> CanonicalAssignment
    AddOnModifier["DomainConcept: Add-on case modifier"] --> CanonicalAssignment
    AddOnModifier --> OptionalAddOn["DesignDecision: Defaults false / opt-in only"]
    OptionalAddOn --> SaveDetails
    SedationModifier --> OptionalSedation["DesignDecision: Eligible unchecked means no sedation"]
    OptionalSedation --> SaveDetails
    CanonicalAssignment --> RoomReadModel["Contract: Durable room assignment read model"]
    RoomReadModel --> BoardUi
    RoomReadModel --> SharedRoomCards["UiSurface: Master and Doctor room cards"]
    ReadyHandoff --> ReadyPrimary["DesignDecision: Ready stays primary"]
    ReadyPrimary --> ReadyUrgency["UiSurface: Subordinate Aging / Stale badge"]
    SharedRoomCards --> DoctorView["UiSurface: Doctor View live-room frame"]
    DeviceBinding["DomainConcept: Room device binding"] --> Mutations
    Realtime["StoreOrService: SignalR + polling fallback"] --> BoardUi

    DevelopmentSeed["DeploymentAsset: Development demo seed"] --> Persistence
    EnvironmentPolicy["DesignDecision: Recognized environment preflight"] --> DevelopmentSeed
    EnvironmentPolicy --> MaintenanceReset
    EnvironmentPolicy --> DeployedSecurity["ConfigOption: Training / Production security posture"]
    EnvironmentPolicy --> DatabaseIsolation["DesignDecision: Deployed database path isolation"]
    DatabaseIsolation --> Persistence
    DatabaseIsolation --> DatabaseIdentity["DesignDecision: Persisted deployment-role identity"]
    DatabaseIdentity --> Persistence
    MaintenanceReset["DeploymentAsset: Stress fixture reset"] --> Persistence
    DatabaseIdentity --> MaintenanceReset
    MaintenanceReset --> CanonicalFixtures["TestGuard: Canonical Ready fixtures"]
```

## Core invariants

- ChairSide tracks rooms, never patients, and remains non-PHI.
- The Room panel uses a normal-flow left rail: dynamic status and correction context first, then the existing primary progression controls. Assignment setup remains in the main content area.
- Begin Prestage creates an episode without requiring assignment. Prestaging and Seated allow absent, partial, or complete canonical assignment.
- For an eligible procedure, sedation is an optional modifier: unchecked canonical room drafts normalize to durable `EligibleNo` when saved, seated, or made Ready, while `EligibleUnresolved` remains readable for partial or legacy state.
- Ready requires a complete, valid assignment, persisted either before or atomically with Ready, and is the immutable handoff/assignment-lock boundary.
- `ReadyForDoctor` is the primary state. Aging and Stale are urgency projections from the owned Active handoff's `ReadyAt`.
- Master and Doctor room cards consume the canonical assignment read model when present; partial values use neutral pending language, and Doctor View membership begins only after a doctor assignment is durably saved.
- Withdrawal returns to Seated and starts no new urgency until a different handoff is issued. Doctor Arrived accepts the current handoff.
- Pre-arrival cancellation and max-duration expiration are aborted history outside throughput. Pre-arrival after-hours terminations remain aborted history and append an objective system finding that enters Needs Review. Post-arrival expiration is review-required history without a fabricated completion timestamp.
- The after-hours sweep is independently retryable per room: successful rooms remain committed, failed and later active rooms retry, and the clinic day is marked complete only after a successful full pass.
- Canonical lifecycle writes are compare-and-swap guarded against the complete originally loaded room, assignment, handoff, and timestamp expectation. Stale writes do not retry or mutate live state.
- Add-on is optional scheduling-context metadata, defaults to false for every new episode, remains independent of procedure and sedation, stays correctable through Ready, locks after Doctor Arrived, survives active and historical persistence, and remains in ordinary reporting by default. Ready still locks doctor/procedure/sedation/allocation dispatch facts.
- `RoomCapabilitiesEvaluator`, projected through the board store, is authoritative for server-known base action availability. Browser-only unsaved-draft guards remain local, and endpoint/store validation remains final.
- `DemoBoardStore.DeriveIntegrityFaults` is the repository-aware production integrity authority. Faulted rooms remain visible, unsafe progression is blocked without rewriting persisted facts, and supported cancellation and legacy recovery remain available.
- Doctor Arrived serializes durable cross-room doctor ownership and commits the room, Accepted handoff, and reporting cycle together.
- Live state changes only after durable persistence succeeds; SQLite failures roll back transaction-local writes.
- Startup and live-board operation do not load lifetime completed history. Reporting enumerates fixed-size persistence pages, spills large replayable calculation populations and exact ordered statistics to private temporary SQLite storage, and releases them after composition/response serialization. Audit retains request-bounded rows, label sorts globally merge projected values, review pages unite typed completed or aborted durable identities, and active lifecycle operations retrieve only their single durable cycle.
- Legacy persisted Aging/Stale rows remain readable; recovery never fabricates or rewrites invalid handoffs.
- Historical anomaly review uses one append-only ledger per encounter. Needs Review provisionally excludes immediately, Confirmed Exception excludes the whole encounter, Cleared removes only the administrative gate, and Reopen Review never reopens the live lifecycle.
- Historical metadata correction is an effective-value overlay that may change current reporting attribution without rewriting the accepted Ready handoff or any lifecycle timestamp.
- Generated knowledge artifacts are development aids and never runtime dependencies.

## Fixture and seed invariants

- Development demo seeding occurs only when all operational tables are empty, persists canonical Ready handoffs, and does not overwrite durable state on restart. Fresh Training and Production databases initialize configured rooms as Available.
- Only Development, Training, and Production are recognized. Unknown names fail before application build, service resolution, database access, log creation, or endpoint mapping.
- Destructive maintenance is allowlisted in Development and Training and refused in Production before application build or repository construction.
- The operator reset wrapper code-owns only the canonical Training app, data, backup, app-pool, and child-environment values; it accepts only an exact Started or Stopped initial pool state, uses bounded polling for asynchronous stop/start transitions, preserves an already Stopped pool, and delegates mutation to existing marker-validating maintenance commands.
- A pool that begins Started is restored in `finally` across Started, Starting, Stopped, or Stopping restoration states. Waits time out with the last observed state, and operation and restoration failures remain separately reportable.
- The isolated `ChairSideBoard-Training` IIS site and app pool serve `http://chairside-training.aospeoria.local` from `C:\ChairSide\Training\App`; Training data, logs, and backups remain under the separate `C:\ChairSide\Training` tree.
- Training board snapshots drive one persistent shared-shell `TRAINING` badge. Development and Production snapshots remove or omit it; hostname inference is never used.
- Production uses only `C:\ChairSide\Data\chairside.db`; Training uses only `C:\ChairSide\Training\Data\chairside-training.db` and logs to `C:\ChairSide\Training\Logs`. Deployed paths fail before SQLite access when they are non-canonical, cross environment boundaries, enter an application/content root, or contain an existing reparse-point component. Development paths remain flexible outside the protected deployed roots.
- Production and Training require matching immutable deployment identity rows before ordinary schema mutation, migration, room initialization, maintenance mutation, or endpoint mapping. Development requires no marker but refuses deployed or malformed marker state.
- Fresh deployed marker and current schema creation are atomic. Existing unmarked deployed databases are always refused. Formal Production begins with a genuinely new canonical database and a new reporting history on the approved go-live date.
- Maintenance reset atomically deletes completed cycles, all Active handoffs, and active rooms, then recreates Available rooms.
- All four row-level maintenance reset/seed entry points preserve the deployment identity exactly.
- Withdrawn, Accepted, and Terminated handoffs and aborted assignments survive maintenance reset.
- Repeated fixtures converge in current state and Active-handoff counts, not total resolved-history rows or generated identities.

## Reporting semantics

- `docs/design/reporting-design.md` is the canonical reporting-semantics design authority; `docs/design/exception-handling-design.md` owns historical administrative interpretation, while lifecycle and accepted Ready-handoff truth remain owned by the lifecycle design and issue #111.
- Accepted Ready handoff is immutable lifecycle evidence and initial assignment attribution. A later explicit historical correction may supply current effective reporting metadata without rewriting it. Withdrawn handoffs are audit history and never accepted attribution.
- Whole UTC days, Monday-start UTC weeks, and `DoctorCompleteAt` completed-window anchoring remain unchanged.
- Standard completed metrics exclude incomplete cycles and exception populations while truthful phase-complete metrics retain their metric-specific eligibility.
- Ready Wait is accepted Ready -> Doctor Arrived; Seated -> Doctor remains a distinct total seated interval.
- Prominent operational timing is median-first, with averages secondary/detail.
- Observed Doctor Days exist only for qualifying same-day Ready-anchored observed flow and are never zero-filled for unobserved dates.
- Doctor weekly trends use one shared Monday-start UTC calendar skeleton of at most 12 buckets within the selected report range. DoctorCompleteAt anchors case timing and completed counts; canonical Observed Doctor Flow dates anchor clinical span. Missing buckets are gaps, clipped edge weeks are explicit, and #217 adds no comparison language.
- Observed Clinical Span is first qualifying accepted Ready -> last same-day Doctor Complete.
- Doctor Working elapsed time uses wall-clock unions of Doctor Arrived -> Doctor Complete intervals. Concurrency buckets partition the same wall-clock span without double-counting.
- Unstructured Time is the span remainder with no active Doctor Working interval and must not be described as idle, available, unused, absent, unscheduled, or recoverable time.
- Procedure Mix exists at Practice and Doctor scope with counts, percentages, and the current scoped included population as denominator.
- Report queries separate whole-day UTC Window, Practice or historical/current Doctor plus Sedation Scope, and Procedure Family or Detailed Variant grouping. Grouping changes aggregation, not population membership.
- General descriptive samples are Empty at `N = 0`, Limited at `N = 1-4`, and Sufficient at `N >= 5`; zero contributors within a nonempty population are Unavailable, and comparisons require all populations to be Sufficient.
- Data Quality and its default anomaly-review drill-down derive from the active report scope, while exhaustive raw history may deliberately broaden investigation. Valid reversed ranges normalize; malformed dates remain graceful.
- Historical assigned Schedule Fit uses finalized expected allocation and truthful exact Seated -> Doctor Complete seconds over the scoped standard included completed population. It keeps exact case-level slack and debt separate, exposes coverage and signed net, and evaluates the scheduling model rather than the doctor. Practice totals ignore Procedure Grouping, while the legacy integer-minute Overall remains a Workshop compatibility contract.
- Current-default Calibration separately uses the current active base-procedure roster default. It never substitutes historical assigned or captured defaults. Version one requires N >= 10 pairs, at least 80 percent raw-sign direction over all pairs, and an agreeing all-pair median strictly beyond +/-600 seconds; -600 through +600 seconds inclusive remains AtExpected. Evaluation is selected-population-only and is not statistical significance.
- Only a server Qualified decision creates a neutral Calibration Insight. Qualified non-PHI evidence reconciles to the decision counts and median population. Insights never save history or mutate expected allocation automatically, and browser code does not reconstruct policy thresholds.
- Healthy Data Quality stays quiet; exclusions and review exceptions remain visible through progressive disclosure and audit evidence.
- Needs Review, Cleared, Confirmed Exception, correction, and Reviewed provenance use separate semantics and are not blindly additive reconciliation buckets.
- Provider ranking, efficiency scoring, attendance inference, idle-time reporting, and punitive metrics are prohibited.
- Ready urgency threshold flags are captured without newly persisting Aging/Stale primary states.

## Completed follow-up work

- Issue #121 / PR #133 (`d902a27`) completed the room-panel workflow, and issue #122 / PR #134 (`2d59c70`) completed canonical master/Doctor View presentation.
- Issue #123 / PR #132 (`9eb5e66`) completed the reporting-population work while preserving the accepted-handoff attribution contract established by issue #120.
- Issue #126 / PR #142 (`47da8f9`) corrected knowledge-graph comment/string false-positive extraction.
- Issues #158 and #159 verified and removed the unused flat Seat and legacy assignment transports while retaining bodyless Ready and Doctor Arrived responses.
- Issue #160 moved assembly-wide test helpers from `BoardStoreTests.cs` into `tests/ChairSide.Board.Tests/TestSupport/`.
- Issue #161 made server-projected room capabilities authoritative, and issue #162 removed the unused context-free integrity evaluator in favor of production store-path coverage.
