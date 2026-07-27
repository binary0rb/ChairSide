# Prestaging Assignment Lifecycle

## Status and traceability

Issue #119 implemented the store and persistence lifecycle described here. Issue #120 exposed that lifecycle through canonical HTTP contracts, including optional assignment-bearing Seat and Ready actions. Issue #121 / PR #133 (`d902a27`) completed the canonical room-panel workflow. Issues #158 and #159 verified and removed the unused flat Seat and legacy assignment transports. Issue #161 made the server projection authoritative for server-known room capabilities, and issue #162 confirmed the repository-aware store as the room-integrity authority. Issue #126 / PR #142 (`47da8f9`) corrected knowledge-graph comment/string extraction.

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

Sedation is an optional modifier, not a separate required Yes/No question. With no procedure it is not applicable, and with an ineligible procedure it is unavailable. For an eligible procedure the room control is enabled; unchecked means no sedation and checked means sedation. When an eligible unchecked draft is committed by Save Details, Seat, or Ready, canonical transport normalizes it to durable `EligibleNo`. `EligibleUnresolved` remains readable for partial or legacy state, but the canonical room workflow does not require an extra No click.

When procedure is absent, sedation is unavailable and expected allocation must be Unknown. Procedure-derived Suggested, ConfirmedSuggestedValue, or ConfirmedAdjustedValue allocation cannot remain attached to an absent procedure.

`SaveAssignmentDetails` is the explicit draft commit. Assignment-bearing Seat or Ready may persist its supplied canonical assignment in the same transaction as the lifecycle action. Save Details, Seat, and Ready reject invalid data without changing durable or live room state.

## Ready boundary

Ready requires a complete, currently valid assignment. The assignment may already be durable or may be supplied by the canonical Ready request and persisted atomically with the transition. A successful Ready transition:

- persists primary state `ReadyForDoctor`;
- records `ReadyForDoctorAt`;
- creates an Active `ready_handoffs` row with the complete immutable assignment;
- links it through `ActiveReadyHandoffId`; and
- locks assignment editing.

An omitted or explicit empty Ready request uses the durably saved assignment. An assignment-bearing canonical request persists the supplied complete draft and creates the Active handoff in one transaction. The canonical room panel sends its current draft and consumes the lifecycle action envelope. A bodyless Ready request remains a deliberately supported compatibility branch and returns the top-level `RoomStatus` response.

## Canonical HTTP contract

Issue #120 exposes canonical Begin Prestage, Save Details, Seat, Ready, Withdraw Ready, and Doctor Arrived operations. Canonical assignment input uses `doctorId`, undecorated `procedureCode`, `sedationChoice`, and `confirmedExpectedAllocationUnits`; the internal `+SED` decoration is never accepted or returned by canonical transport. For an eligible procedure, `sedationChoice: "yes"` persists `EligibleYes`, while omitted or null `sedationChoice` represents the unchecked modifier and persists `EligibleNo`. Explicit `"no"` remains compatible.

The canonical Seat request nests assignment under `assignment`. The former flat assignment-bearing Seat parser, `/update-assignment`, and `/assignment` routes were removed after issue #158 found no maintained deployed callers. Bodyless Ready and Doctor Arrived were explicitly outside that removal: they retain the top-level `RoomStatus` success response, while explicit canonical request bodies return the lifecycle action envelope.

Room reads expose the durable canonical assignment alongside the existing room projection so initial load, polling, SignalR refresh, and restart recovery can restore doctor, procedure, sedation, allocation confirmation, completeness, lock state, and handoff references without inferring them from display fields.

Canonical mutation outcomes distinguish room-not-found, invalid or incomplete assignment, lifecycle conflict, assignment lock, integrity fault, stale write, and persistence failure. Malformed or invalid assignment input maps to HTTP 400, room-not-found to 404, lifecycle/integrity/concurrency conflicts to 409, and persistence failure to 500. Validation and persistence failures do not partially mutate the room, handoff, cycle, live state, or event stream.

## Capability and integrity authority

`RoomCapabilitiesEvaluator` is the single base policy for server-known lifecycle capabilities. `DemoBoardStore.ToRoomStatus` evaluates it from the projected canonical state, durable episode presence, and repository-aware integrity faults, then exposes the result as `RoomStatus.Capabilities`. The room browser consumes that projection instead of maintaining a parallel lifecycle-state matrix.

Browser-only facts remain browser-owned. In particular, local draft dirtiness controls Save Details and Discard Changes, while local draft completeness controls whether Ready is highlighted as the next primary action. These client guards are advisory UI behavior; endpoint and store validation remain the final authority for every submitted mutation.

`DemoBoardStore.DeriveIntegrityFaults` is the production authority for room-integrity projection because it can evaluate durable assignment and handoff context together. It keeps malformed or partial Ready rooms visible, blocks unsafe progression without rewriting persisted facts, and preserves supported cancellation and legacy recovery. The former context-free `RoomIntegrityFaultEvaluator` was unused by production and has been removed.

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

Canonical lifecycle writes capture an internal expectation from the original live room before constructing the candidate. The guarded `UPDATE active_rooms` compares room/episode/state identity, the complete assignment and allocation snapshot, both handoff references, and lifecycle timestamps with null-safe predicates. Exactly one affected row may commit. Zero rows returns the typed stale-write outcome without fallback INSERT/UPSERT, event, reload, retry, durable mutation, or live mutation. More than one row is an invariant violation.

Ready validates episode handoff history and writes the assignment, room, and new immutable Active handoff atomically. Withdraw Ready validates ownership and assignment equality before changing the Active handoff to Withdrawn and returning the room to Seated. Doctor Arrived uses an immediate SQLite transaction so cross-room doctor ownership is revalidated against Accepted handoff truth before the target room, handoff, and reporting cycle commit together. Database failures roll back all transaction-local writes; live memory and events change only after commit.

The general `SaveRoom`/`SaveRooms` UPSERT path remains available for unrelated initialization and lifecycle persistence.

## Reporting

The handoff accepted by Doctor Arrived supplies finalized doctor, procedure, sedation, and allocation attribution. Withdrawn handoffs remain auditable but are excluded from accepted attribution and their Ready intervals do not become accepted Ready-to-arrival time. Pre-arrival aborts remain outside throughput. Post-arrival expiration belongs only to review-required exception populations.

Completed-cycle Aging/Stale threshold flags are calculated from the accepted Ready interval without requiring newly persisted Aging/Stale primary room states. Legacy completed cycles retain their existing assignment and timing representation; no Ready timestamp or handoff is fabricated for them.

## Development and maintenance fixtures

Outside Production, the demo seed runs only when all operational tables are empty before configured room initialization. Seeded Ready rooms use canonical assignments, episodes, owned Active handoffs, and links. The seed is durable; restart restores it or subsequent changes without reseeding.

The stress-fixture reset transaction deletes completed cycles, all Active handoffs, and active rooms, then recreates configured Available rooms. It preserves Withdrawn, Accepted, and Terminated handoffs plus aborted assignments. Repeated runs converge in current state and Active-handoff counts, not in preserved history totals or generated GUID identities.

## Issue #120 non-goals and completed follow-up work

- No patient identity or PHI.
- Issue #120 intentionally excluded room-panel implementation; issue #121 / PR #133 (`d902a27`) subsequently completed it.
- Issue #120 intentionally excluded master/Doctor View presentation; issue #122 / PR #134 (`2d59c70`) subsequently completed it.
- Issue #120 intentionally excluded reporting-population changes; issue #123 / PR #132 (`9eb5e66`) subsequently completed that work without changing the accepted-handoff attribution contract.
- No automatic replay of stale assignment intent.
- No migration that rewrites legacy Aging/Stale rows or historical cycles.
- Issue #129 later made the after-hours sweep independently retryable per room and added a truthful unified review projection for pre-arrival after-hours history.
- Issue #120 intentionally excluded knowledge-graph extraction changes; issue #126 / PR #142 (`47da8f9`) subsequently corrected comment/string false-positive extraction.

## Acceptance summary

- Bare Prestage and incomplete Seated drafts persist truthfully.
- Ready rejects incomplete or invalid durable assignment and locks a complete one in an owned handoff.
- Server-projected capabilities govern server-known browser action availability; browser-local draft guards and endpoint/store enforcement retain their separate responsibilities.
- Repository-aware integrity projection blocks unsafe progression without repairing or hiding malformed persisted facts.
- Withdrawal and reissue preserve episode identity while starting a fresh handoff/urgency interval.
- Doctor Arrived accepts, rather than reconstructs, the Ready assignment.
- Stale canonical writes are harmless CAS rejections.
- Database failure rolls back durable work and leaves live memory unchanged.
- Cancellation, expiration, recovery, and reporting preserve truthful population boundaries.
