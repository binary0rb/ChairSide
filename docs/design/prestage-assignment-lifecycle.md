# Prestaging Assignment Lifecycle

## Status and traceability

Issue #119 implements the store and persistence lifecycle described here. The current boundary requires a complete canonical assignment to be durably saved before `MarkReadyForDoctor`. An assignment-bearing Ready overload and the corresponding Program/UI workflow are deliberately deferred to issues #120 and #121.

## Primary lifecycle

1. Available
2. Prestaging
3. Seated / In Prep
4. Ready for Doctor
5. Doctor Working
6. Doctor Complete / Turnover
7. Available

Available to Prestaging creates `EpisodeId`, records `PrestageStartedAt`, and reserves the room without inventing assignment data. Seat records truthful `SeatedAt`. Ready creates the formal immutable handoff. Doctor Arrived accepts that handoff. Doctor Complete and Room Available finish the cycle.

## Assignment completeness

Prestaging and Seated may persist an absent, partial, or complete assignment. A complete assignment contains:

- an active doctor;
- an active primary procedure;
- sedation semantics consistent with procedure eligibility; and
- a confirmed expected allocation consistent with the procedure.

When procedure is absent, sedation is unavailable and expected allocation must be Unknown. Procedure-derived Suggested, ConfirmedSuggestedValue, or ConfirmedAdjustedValue allocation cannot remain attached to an absent procedure.

`SaveAssignmentDetails` is the explicit draft commit. An assignment-bearing Begin Prestage or Seat may persist its supplied canonical assignment in the same transaction as the lifecycle action. Save Details and assignment-bearing Seat reject invalid data without changing durable or live room state.

## Ready boundary

Ready requires a complete, currently valid, durably saved assignment. A successful Ready transition:

- persists primary state `ReadyForDoctor`;
- records `ReadyForDoctorAt`;
- creates an Active `ready_handoffs` row with the complete immutable assignment;
- links it through `ActiveReadyHandoffId`; and
- locks assignment editing.

The current Ready action does not accept or persist a draft. Staff must Save Details first. This is the intentional issue #119 store boundary, not the future #120/#121 API/UI design.

## Ready urgency, withdrawal, and acceptance

Aging and Stale are `ReadyUrgency` projections from the Active handoff's `ReadyAt`; `ReadyForDoctor` remains the primary state. New canonical flows do not persist Aging or Stale as primary states.

Withdraw Ready changes the owned Active handoff to Withdrawn, returns the room to Seated, preserves `EpisodeId`, `PrestageStartedAt`, and `SeatedAt`, and clears urgency. The saved assignment becomes editable again. Reissuing Ready creates a different `HandoffId` and later `ReadyAt`; prior withdrawn time does not contribute to the new urgency interval.

Doctor Arrived accepts the owned Active handoff, records its identity as the accepted handoff, clears active urgency, and advances to Doctor Working without redefining the assignment.

Legacy persisted Aging and Stale rows remain readable compatibility states. If such a row owns a valid Active handoff, it may withdraw safely. A missing, malformed, or unrelated handoff is never fabricated or rewritten.

## Cancellation, expiration, and recovery

Pre-arrival cancellation and expiration persist aborted-assignment history and release the room. They do not create throughput or fabricate `SeatedAt`. Faulted pre-arrival Ready rows remain visible and can be canceled safely; recovery changes only a valid owned handoff.

Post-arrival expiration persists a review-required exception cycle and releases the room without fabricating `DoctorCompleteAt` or another missing timestamp. Restart recovery loads durable truth and projects urgency/integrity read-only; it does not inject defaults, change handoff state, or create a new interval.

## Persistence, compare-and-swap, and failure ordering

Every representative multi-write lifecycle operation uses a SQLite transaction. Store code constructs detached candidates, calls the repository, and applies the committed result to live memory only after persistence succeeds.

Canonical assignment writes capture an internal expectation from the original live room before constructing the candidate:

- `RoomId`;
- nullable `EpisodeId`;
- lifecycle `State`; and
- nullable `ActiveReadyHandoffId`.

The canonical-assignment path uses a guarded `UPDATE active_rooms` that matches all four fields. SQLite `IS` provides null-safe comparison for `episode_id` and `active_ready_handoff_id`. Exactly one affected row commits. Zero rows means stale durable identity and returns `null` without fallback INSERT/UPSERT, event, reload, retry, durable mutation, or live mutation. More than one row is an invariant violation. SQLite/database failures throw and roll back all writes in the transaction.

The general `SaveRoom`/`SaveRooms` UPSERT path remains available for unrelated initialization and lifecycle persistence.

## Reporting

The handoff accepted by Doctor Arrived supplies finalized doctor, procedure, sedation, and allocation attribution. Withdrawn handoffs remain auditable but are excluded from accepted attribution and their Ready intervals do not become accepted Ready-to-arrival time. Pre-arrival aborts remain outside throughput. Post-arrival expiration belongs only to review-required exception populations.

Completed-cycle Aging/Stale threshold flags are calculated from the accepted Ready interval without requiring newly persisted Aging/Stale primary room states. Legacy completed cycles retain their existing assignment and timing representation; no Ready timestamp or handoff is fabricated for them.

## Development and maintenance fixtures

Outside Production, the demo seed runs only when all operational tables are empty before configured room initialization. Seeded Ready rooms use canonical assignments, episodes, owned Active handoffs, and links. The seed is durable; restart restores it or subsequent changes without reseeding.

The stress-fixture reset transaction deletes completed cycles, all Active handoffs, and active rooms, then recreates configured Available rooms. It preserves Withdrawn, Accepted, and Terminated handoffs plus aborted assignments. Repeated runs converge in current state and Active-handoff counts, not in preserved history totals or generated GUID identities.

## Explicit non-goals and separate issues

- No patient identity or PHI.
- No assignment-bearing `MarkReadyForDoctor` API/UI in issue #119; see #120/#121.
- No automatic replay of stale assignment intent.
- No migration that rewrites legacy Aging/Stale rows or historical cycles.
- No after-hours sweep retry/batch-atomicity fix. `_lastSweepDate` can advance before persistence succeeds, same-day retry can be suppressed, and earlier rooms can commit before a later failure.
- No knowledge-graph comment/string extraction fix; false-positive extraction is issue #126.

## Acceptance summary

- Bare Prestage and incomplete Seated drafts persist truthfully.
- Ready rejects incomplete or invalid durable assignment and locks a complete one in an owned handoff.
- Withdrawal and reissue preserve episode identity while starting a fresh handoff/urgency interval.
- Doctor Arrived accepts, rather than reconstructs, the Ready assignment.
- Stale canonical writes are harmless CAS rejections.
- Database failure rolls back durable work and leaves live memory unchanged.
- Cancellation, expiration, recovery, and reporting preserve truthful population boundaries.
