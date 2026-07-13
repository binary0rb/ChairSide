# ChairSide durable decisions

This file records decisions that should survive across tasks, PRs, and debugging sessions.

## Knowledge graph scope

**Decision:** The knowledge graph is a private, human-readable development artifact stored under `docs/knowledge-graph/`; generated files are navigation aids and never runtime dependencies.

**Rationale:** Repo-local Markdown and Mermaid are reviewable and help future work recover intent without adding production fragility.

## Non-PHI boundary

**Decision:** ChairSide and its knowledge graph must never include patient identity, clinical notes, appointment data, chart numbers, secrets, or credentials.

**Rationale:** The system tracks rooms and non-PHI operational timing, not patients.

## Prestaging and assignment drafts

**Decision:** Begin Prestage creates the episode without requiring assignment. Prestaging and Seated may durably contain absent, partial, or complete canonical assignment. Save Details is the explicit assignment commit, and assignment-bearing Seat may commit its supplied draft atomically.

**Rationale:** Lifecycle truth and assignment completeness are independent before Ready. The system must not invent clinical defaults or require staff to lie about timing.

## Ready handoff boundary

**Decision:** Ready requires a complete, currently valid, durably saved assignment and creates an immutable owned Active handoff. Ready is the assignment-lock boundary; Doctor Arrived accepts the handoff rather than reconstructing assignment.

**Rationale:** A doctor may act on the handoff as soon as Ready is issued, so silent assignment changes after that point are unsafe.

**Deferred:** Draft-bearing Ready and matching Program/UI changes belong to issues #120/#121, not #119.

## Ready urgency

**Decision:** `ReadyForDoctor` remains the primary lifecycle state. Aging and Stale are projected `ReadyUrgency` values calculated from the Active handoff's `ReadyAt`.

**Rationale:** Urgency is elapsed-time presentation, not a new lifecycle transition. Withdrawal and Doctor Arrived clear urgency; reissued Ready creates a new handoff and interval.

**Compatibility:** Persisted legacy Aging/Stale rows remain readable and may withdraw only when they own a valid Active handoff. Recovery never fabricates one.

## Cancellation, expiration, and recovery

**Decision:** Pre-arrival cancellation/expiration creates aborted assignment history outside throughput. Post-arrival expiration creates a review-required exception without fabricating `DoctorCompleteAt`. Faulted pre-arrival Ready remains visible and safely cancellable without rewriting invalid or unrelated handoffs.

**Rationale:** Recovery must preserve observed facts and reporting populations rather than manufacturing a plausible happy path.

## Concurrency and durable ordering

**Decision:** Canonical assignment persistence compares the originally loaded room id, nullable episode id, lifecycle state, and nullable Active handoff id. Zero affected rows returns `null` with no INSERT/UPSERT fallback, reload, retry, event, durable mutation, or live mutation. Database failures throw. Live state is applied only after a successful repository transaction.

**Rationale:** A stale context must not regress Ready, replace its locked assignment, clear its handoff link, or orphan the durable handoff. Failure injection proves multi-write transaction rollback and unchanged live memory.

**Compatibility:** General `SaveRoom` and `SaveRooms` UPSERT behavior remains for initialization and unrelated lifecycle paths.

## Development seed

**Decision:** Demo data seeds only outside Production when all operational tables are empty before configured-room initialization. The seed is persisted, and demo Ready rooms have canonical episodes, assignments, and owned Active handoffs.

**Rationale:** A first run should be useful, while restart must restore and never overwrite durable state.

## Maintenance reset

**Decision:** Stress-fixture reset atomically deletes completed cycles, all Active handoffs, and active rooms, then recreates configured Available rooms. Withdrawn, Accepted, and Terminated handoffs and aborted assignments survive.

**Rationale:** The command promises deterministic current fixture state and cannot leave an orphan Active handoff. It does not promise convergence of total preserved history or generated GUID identities.

## Reporting population semantics

**Decision:** The accepted Ready handoff supplies finalized assignment attribution. Withdrawn handoffs are audit-only, pre-arrival aborts stay outside throughput, and post-arrival expiration appears only in review-required exception populations. Aging/Stale threshold flags derive from the accepted Ready interval.

**Rationale:** Metrics become misleading if draft assignment, withdrawn intent, incomplete cycles, exception cycles, and completed populations are mixed.

## Reporting UI philosophy

**Decision:** Reporting favors summary cards, plain-English interpretation, progressive disclosure, and operational questions over dense rankings or punitive comparisons.

## Explicitly separate issues

- The after-hours sweep advances `_lastSweepDate` before persistence succeeds; a failed sweep can suppress same-day retry, and earlier rooms can commit before a later failure. Do not silently fold that retry/batch fix into #119.
- Knowledge-graph comment/string false-positive extraction is tracked in issue #126.
