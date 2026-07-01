# ChairSide knowledge graph

This is the hand-authored map of ChairSide's important concepts. Keep it readable for humans first. The generated inventory in `generated/` helps locate files, but this document explains why the pieces matter.

## Conceptual graph

```mermaid
flowchart LR
    LightBoardReplacement["DomainConcept: Replace light board / pager"] --> BoardUi["UiSurface: ChairSide board"]
    BoardUi --> RoomCards["UiSurface: Room cards"]
    BoardUi --> DoctorRail["UiSurface: Doctor rail"]
    BoardUi --> MasterKey["UiSurface: Master key / legend"]

    RoomLifecycle["DomainConcept: Room lifecycle"] --> Seat["LifecycleEvent: Seat"]
    RoomLifecycle --> Update["LifecycleEvent: Update"]
    RoomLifecycle --> Cancel["LifecycleEvent: Cancel"]
    RoomLifecycle --> DoctorArrived["LifecycleEvent: Doctor Arrived"]
    RoomLifecycle --> DoctorComplete["LifecycleEvent: Doctor Complete"]
    RoomLifecycle --> RoomAvailable["LifecycleEvent: Room Available"]

    Seat --> InPrep["WorkflowState: In Prep"]
    Seat --> Ready["WorkflowState: Ready"]
    Ready --> Aging["WorkflowState: Aging"]
    Aging --> Stale["WorkflowState: Stale"]
    DoctorArrived --> InRoom["WorkflowState: In Room"]
    DoctorComplete --> Turnover["WorkflowState: Turnover"]
    RoomAvailable --> Available["WorkflowState: Available"]

    DoctorRoster["ConfigOption: Doctor roster"] --> DoctorRail
    ProcedureRoster["ConfigOption: Procedure roster"] --> ProcedureChips["UiSurface: Procedure chips"]
    ProcedureColors["DesignDecision: Procedure color language"] --> ProcedureChips
    SedationModifier["DomainConcept: Sedation modifier"] --> ProcedureChips
    SedationModifier --> SedationMetrics["ReportMetric: Sedation vs non-sedation"]

    BoardStore["StoreOrService: Board store"] --> RoomLifecycle
    BoardStore --> Persistence["StoreOrService: SQLite persistence"]
    Persistence --> ProdDb["DeploymentAsset: C:\\ChairSide\\Data\\chairside.db"]
    Realtime["StoreOrService: SignalR + polling fallback"] --> BoardUi
    DeviceBinding["DomainConcept: Room device binding"] --> HeaderToken["ConfigOption: Header token per room"]
    HeaderToken --> Mutations["LifecycleEvent: Write actions"]

    Reports["UiSurface: Reports"] --> SummaryCards["UiSurface: Summary cards"]
    Reports --> Filters["UiSurface: Report filters"]
    Reports --> PlainEnglish["DesignDecision: Plain-English explanations"]
    ReportsBuilder["StoreOrService: Report snapshot builder"] --> Reports

    DoctorComplete --> CompletedWindow["ReportMetric: Completed-cycle window anchor"]
    CompletedWindow --> UtcDays["DesignDecision: Whole UTC calendar days"]
    CompletedWindow --> MondayWeeks["DesignDecision: Monday-start UTC weeks"]
    PhaseTimings["ReportMetric: Phase-complete timings"] --> Reports
    FullyCompletedPopulation["ReportMetric: Fully completed population"] --> Reports
    ReportingException["ReportMetric: Reporting exception count"] --> Reports
    ReportsBuilder --> ProcedureMix["ReportMetric: Doctor procedure mix"]
    FullyCompletedPopulation --> ProcedureMix
    SedationModifier --> ProcedureMix
    ProcedureMix --> ProcedureMixTab["UiSurface: Procedure Mix tab"]

    ReportingTests["TestGuard: Reporting semantics tests"] --> PhaseTimings
    ReportingTests --> FullyCompletedPopulation
    ReportingTests --> CompletedWindow
    ReportingTests --> ProcedureMix
    ReportingDocs["TestGuard: Reporting time-window docs"] --> UtcDays
    ReportingDocs --> MondayWeeks

    BetaTraining["DeploymentAsset: Beta/training sandbox"] --> SandboxData["DesignDecision: Training may generate sandbox data"]
    SandboxData --> FreshBetaDb["DesignDecision: Archive/reset before official beta"]
```

## Core invariants

- ChairSide is non-PHI and must stay non-PHI.
- Room lifecycle order is the spine of the app: Seat, Doctor Arrived, Doctor Complete, Room Available.
- Board usability matters more than decorative polish. Avoid visual changes that reduce readability.
- Reports should answer operational questions without turning into a wall of numbers.
- Reporting semantics are test-guarded. Do not “simplify” them without updating tests and documentation intentionally.
- Generated graph artifacts are development aids only; they must not become runtime dependencies.

## Current reporting semantics captured by the graph

- Report date filters use whole UTC calendar days.
- Weekly trends use Monday-start UTC weeks.
- Completed-cycle reporting windows are anchored on `DoctorCompleteAt`.
- Phase-complete timings can contribute before full Room Available completion.
- Fully completed throughput, allocation, schedule-fit, and trend populations exclude incomplete cycles.
- Exception completed cycles can affect relationships between total completed cycles and included completed cycle counts.
- Selected-doctor procedure mix (`ReportsSnapshot.DoctorProcedureMix`, built by `BuildDoctorProcedureMix` in `DemoBoardStore.cs`) is an additive read model over the same standard/included completed-cycle population (`standardCompletedCycles`) as the other calculated metrics. It groups by doctor + procedure variant so sedation variants such as `EXT+SED` stay separate from the base `EXT`, keeping sedation a modifier of the primary procedure via `BaseProcedureCode`/`IsSedationCase`; each `DoctorProcedureMixRow` carries the case count, the doctor's completed-case denominator, and that procedure's share of the doctor's cases.
- Procedure mix renders in the selected-doctor detail panel's `Procedure Mix` tab through `renderSelectedDoctorProcedures` in `board.js`, is guarded by `BoardStoreTests.cs` tests (grouping/shares, per-doctor denominators, excluded/incomplete cycles, blank-doctor skip), and introduced no schema or existing-metric-semantics changes.

## Known development affordances

- Keep PRs small and reviewable.
- Prefer tests and comments that lock intended semantics before bigger refactors.
- Use the graph to preserve deferred ideas, not to force end users into graph concepts.
