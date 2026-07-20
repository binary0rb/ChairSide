# ChairSide knowledge graph

This hand-authored map records ChairSide's important concepts and relationships. Generated inventory files help locate symbols but are not runtime inputs or substitutes for this intent.

## Conceptual graph

```mermaid
flowchart LR
    LightBoardReplacement["DomainConcept: Replace light board / pager"] --> BoardUi["UiSurface: ChairSide board"]
    BoardUi --> RoomCards["UiSurface: Room cards"]
    BoardUi --> DoctorView["UiSurface: Doctor read-only view"]

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
    Persistence --> ActiveRooms["PersistenceModel: active_rooms"]
    Persistence --> Handoffs["PersistenceModel: ready_handoffs"]
    Persistence --> Aborts["PersistenceModel: aborted_room_assignments"]
    Persistence --> Cycles["PersistenceModel: completed_room_cycles"]
    CasGuard --> ActiveRooms

    PreArrivalTermination["DomainConcept: Pre-arrival cancellation / expiration"] --> Aborts
    AfterHoursSweep["DomainConcept: Nightly after-hours safety sweep"] --> Aborts
    AfterHoursSweep --> ReviewException
    PostArrivalExpiration["DomainConcept: Post-arrival expiration"] --> ReviewException["ReportMetric: Review-required exception"]
    ReviewException --> Cycles
    Aborts --> UnifiedReview["UiSurface: Unified exception review queue"]
    ReviewException --> UnifiedReview

    Reports["UiSurface: Reports"] --> SummaryCards["UiSurface: Summary cards"]
    ReportsBuilder["StoreOrService: Report snapshot builder"] --> Reports
    AcceptedHandoff --> StandardPopulation["ReportMetric: Standard completed population"]
    StandardPopulation --> ReportsBuilder
    WithdrawnHandoff --> AuditOnly["DesignDecision: Not accepted attribution"]
    Aborts --> OutsideThroughput["DesignDecision: Outside throughput"]

    DoctorRoster["ConfigOption: Doctor roster"] --> CanonicalAssignment
    ProcedureRoster["ConfigOption: Procedure roster"] --> CanonicalAssignment
    SedationModifier["DomainConcept: Sedation modifier"] --> CanonicalAssignment
    SedationModifier --> OptionalSedation["DesignDecision: Eligible unchecked means no sedation"]
    OptionalSedation --> SaveDetails
    CanonicalAssignment --> RoomReadModel["Contract: Durable room assignment read model"]
    RoomReadModel --> BoardUi
    RoomReadModel --> SharedRoomCards["UiSurface: Master and Doctor room cards"]
    ReadyHandoff --> ReadyPrimary["DesignDecision: Ready stays primary"]
    ReadyPrimary --> ReadyUrgency["UiSurface: Subordinate Aging / Stale badge"]
    SharedRoomCards --> DoctorView["UiSurface: Doctor View live-room frame"]
    DeviceBinding["DomainConcept: Room device binding"] --> Mutations["LifecycleEvent: Room-local writes"]
    Realtime["StoreOrService: SignalR + polling fallback"] --> BoardUi

    DevelopmentSeed["DeploymentAsset: Development demo seed"] --> Persistence
    EnvironmentPolicy["DesignDecision: Recognized environment preflight"] --> DevelopmentSeed
    EnvironmentPolicy --> MaintenanceReset
    EnvironmentPolicy --> DeployedSecurity["ConfigOption: Training / Production security posture"]
    EnvironmentPolicy --> DatabaseIsolation["DesignDecision: Deployed database path isolation"]
    DatabaseIsolation --> Persistence
    MaintenanceReset["DeploymentAsset: Stress fixture reset"] --> Persistence
    MaintenanceReset --> CanonicalFixtures["TestGuard: Canonical Ready fixtures"]
```

## Core invariants

- ChairSide tracks rooms, never patients, and remains non-PHI.
- Begin Prestage creates an episode without requiring assignment. Prestaging and Seated allow absent, partial, or complete canonical assignment.
- For an eligible procedure, sedation is an optional modifier: unchecked canonical room drafts normalize to durable `EligibleNo` when saved, seated, or made Ready, while `EligibleUnresolved` remains readable for partial or legacy state.
- Ready requires a complete, valid assignment, persisted either before or atomically with Ready, and is the immutable handoff/assignment-lock boundary.
- `ReadyForDoctor` is the primary state. Aging and Stale are urgency projections from the owned Active handoff's `ReadyAt`.
- Master and Doctor room cards consume the canonical assignment read model when present; partial values use neutral pending language, and Doctor View membership begins only after a doctor assignment is durably saved.
- Withdrawal returns to Seated and starts no new urgency until a different handoff is issued. Doctor Arrived accepts the current handoff.
- Pre-arrival cancellation and max-duration expiration are aborted history outside throughput. Pre-arrival after-hours terminations remain aborted history but also enter the unified review queue. Post-arrival expiration is review-required exception history without a fabricated completion timestamp.
- The after-hours sweep is independently retryable per room: successful rooms remain committed, failed and later active rooms retry, and the clinic day is marked complete only after a successful full pass.
- Canonical lifecycle writes are compare-and-swap guarded against the complete originally loaded room, assignment, handoff, and timestamp expectation. Stale writes do not retry or mutate live state.
- Doctor Arrived serializes durable cross-room doctor ownership and commits the room, Accepted handoff, and reporting cycle together.
- Live state changes only after durable persistence succeeds; SQLite failures roll back transaction-local writes.
- Legacy persisted Aging/Stale rows remain readable; recovery never fabricates or rewrites invalid handoffs.
- Generated knowledge artifacts are development aids and never runtime dependencies.

## Fixture and seed invariants

- Development demo seeding occurs only when all operational tables are empty, persists canonical Ready handoffs, and does not overwrite durable state on restart. Fresh Training and Production databases initialize configured rooms as Available.
- Only Development, Training, and Production are recognized. Unknown names fail before application build, service resolution, database access, log creation, or endpoint mapping.
- Destructive maintenance is allowlisted in Development and Training and refused in Production before application build or repository construction.
- Production uses only `C:\ChairSide\Data\chairside.db`; Training uses only `C:\ChairSide\Training\Data\chairside-training.db` and logs to `C:\ChairSide\Training\Logs`. Deployed paths fail before SQLite access when they are non-canonical, cross environment boundaries, enter an application/content root, or contain an existing reparse-point component. Development paths remain flexible outside the protected deployed roots.
- Maintenance reset atomically deletes completed cycles, all Active handoffs, and active rooms, then recreates Available rooms.
- Withdrawn, Accepted, and Terminated handoffs and aborted assignments survive maintenance reset.
- Repeated fixtures converge in current state and Active-handoff counts, not total resolved-history rows or generated identities.

## Reporting semantics

- Accepted Ready handoff is finalized assignment attribution.
- Withdrawn handoffs are audit history and never accepted attribution.
- Whole UTC days, Monday-start UTC weeks, and `DoctorCompleteAt` completed-window anchoring remain unchanged.
- Standard completed metrics exclude incomplete cycles and exception populations.
- Ready urgency threshold flags are captured without newly persisting Aging/Stale primary states.

## Deferred and separate work

- Room-panel workflow changes are implemented by #121, and canonical master/doctor-view presentation is implemented by #122.
- Reporting-population work remains #123; issue #120 preserves the existing accepted-handoff attribution contract.
- Knowledge-graph comment/string false-positive extraction remains issue #126.
