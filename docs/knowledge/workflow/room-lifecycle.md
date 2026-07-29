---
title: Room lifecycle
tags: [room, board, room-lifecycle, data-persistence, permissions, device-binding, domain-rule, active, last-verified]
last_verified_commit: 61dac09
---

# Room lifecycle

## Canonical sequence

ChairSide tracks room episodes, not patients. The canonical lifecycle is:

1. Begin Prestage creates `EpisodeId`, records `PrestageStartedAt`, and enters `Prestaging` without requiring an assignment.
2. Save Details explicitly persists an absent, partial, or complete assignment while `Prestaging` or `Seated`.
3. Seat Room records truthful `SeatedAt`. An assignment-bearing Seat may persist the supplied canonical draft in the same transaction.
4. Ready for Doctor requires a complete, currently valid assignment. It may use the durable draft or atomically persist a supplied canonical draft, then enters primary state `ReadyForDoctor`, creates an owned Active handoff, and locks the assignment.
5. Doctor Arrived accepts that handoff, clears Ready urgency, records `DoctorArrivedAt`, and enters `DoctorInRoom`.
6. Doctor Complete records `DoctorCompleteAt` and enters `Turnover`.
7. Room Available records completion and releases the room to `Available`.

Issue #120 exposed canonical Begin Prestage, Save Details, Seat, Ready, Withdraw Ready, and Doctor Arrived endpoints. Issue #121 / PR #133 (`d902a27`) completed the room-panel migration to those contracts. Issue #158 verified that maintained deployed callers no longer use flat assignment-bearing Seat, `/update-assignment`, or `/assignment`; issue #159 removed those legacy transports. The room panel sends Ready with its explicit assignment-bearing body and consumes the lifecycle action envelope. It sends Doctor Arrived without a body, consumes the top-level `RoomStatus` success response, and uses the retained bodyless conflict response when the doctor is already in another room.

Canonical Seat accepts only the nested `assignment` shape. The removed flat Seat parser and assignment routes are not compatibility surfaces. The retained response boundary is narrower: bodyless Ready remains compatible with the top-level `RoomStatus` response, while bodyless Doctor Arrived is maintained transport required by the checked-in room panel and returns top-level `RoomStatus` on success. Explicit canonical bodies remain supported and return the lifecycle action envelope.

## Ready handoff and urgency

`ReadyForDoctor` is the primary persisted state. `Aging` and `Stale` are `ReadyUrgency` projections from the owned Active handoff's `ReadyAt`; new canonical writes do not persist them as primary states. Legacy persisted `Aging` and `Stale` rows remain readable recovery states.

Withdraw Ready terminates the current handoff as `Withdrawn`, returns the room to `Seated`, preserves `EpisodeId`, `PrestageStartedAt`, and `SeatedAt`, and clears urgency immediately. Assignment details may then be corrected. Reissuing Ready creates a different `HandoffId` and a new `ReadyAt`, so the withdrawn interval does not contribute to the new urgency interval. Doctor Arrived accepts the current handoff and also clears urgency immediately.

A valid legacy `Aging` or `Stale` row with a matching owned Active handoff may withdraw safely. Recovery never fabricates a missing handoff or rewrites an invalid or unrelated handoff.

## Cancellation, expiration, and recovery

Pre-arrival cancellation and max-duration expiration create aborted-assignment history, not throughput. The nightly after-hours sweep also preserves pre-arrival truth in aborted-assignment history, but marks those records as `AfterHoursSweep` exceptions and projects them into the same administrative review queue as post-arrival exceptions. Faulted pre-arrival Ready rows remain visible and safely cancellable; unrelated or invalid handoff records are not rewritten. Post-arrival expiration creates a review-required exception cycle without inventing `DoctorCompleteAt` or other lifecycle timestamps.

The after-hours sweep processes each active room in its existing per-room transaction. A committed room remains Available if a later room fails. The failed and later active rooms remain retryable because the clinic day is marked complete only after the entire pass succeeds; restart recovery likewise skips durably Available rooms and processes only active rooms.

Restart recovery restores durable truth and projects urgency and integrity without mutating the database. Live room state changes only after the repository transaction succeeds.

Canonical `DoctorInRoom` and `Turnover` progression revalidates the room assignment and in-progress reporting-cycle attribution against the immutable Accepted handoff. Contradictory recovered state projects or returns `integrity-fault` and blocks Doctor Complete or Room Available without mutating room, handoff, cycle, timestamps, reports, events, or live state. Legitimate legacy arrived rooms without handoff metadata retain their compatibility completion path.

## Capability and integrity authority

`RoomCapabilitiesEvaluator` defines the server-known base action matrix. `DemoBoardStore.ToRoomStatus` evaluates that policy with canonical lifecycle state, durable episode presence, and repository-aware integrity faults, then projects it as `RoomStatus.Capabilities`. `board.js` consumes those booleans instead of reconstructing server lifecycle legality from room state.

Unsaved assignment-draft completeness and dirtiness are browser-only facts and remain local guards layered over the server projection. Dirtiness controls Save Details and Discard Changes; draft completeness controls Ready's next-action highlighting. Projected capabilities do not authorize a mutation: endpoint and store validation remain final and reject illegal, stale, invalid, or integrity-faulted requests.

`DemoBoardStore.DeriveIntegrityFaults` is the production room-integrity authority. Its repository context covers assignment completeness, owned Active or Accepted handoffs, and contradictory references. The unused context-free `RoomIntegrityFaultEvaluator` was removed. Production-path coverage persists a partial Ready assignment, verifies `ReadyAssignmentIncomplete`, proves Doctor Arrived returns the canonical integrity-fault outcome without changing the durable assignment, and confirms safe cancellation remains available.

## Concurrency and durability

Canonical lifecycle writes capture the complete originally loaded durable room expectation: room/episode/state identity, assignment and allocation values, both handoff references, and lifecycle timestamps. The guarded SQLite update uses null-safe comparisons. A stale context receives `stale-write`; it does not mutate live memory, retry, reload, append an event, regress Ready, overwrite the locked assignment, or orphan a handoff.

Ready and Withdraw Ready validate episode handoff history in the same transaction as the room mutation. Doctor Arrived uses an immediate SQLite transaction to serialize cross-room doctor ownership, validates canonical working rooms against their Accepted handoffs, and commits the room, Active-to-Accepted handoff transition, and reporting cycle atomically. SQLite failures roll back transaction-local writes; live memory and events change only after commit.

## Authorization and conflict handling

Room lifecycle mutation remains room-local and device-token guarded. Doctors are read-only. Doctor-arrival conflict resolution revalidates current server state, auto-completes the previous room only into `Turnover`, and audits both rooms.

## Source and test anchors

- `src/ChairSide.Board/Services/DemoBoardStore.cs` - `ToRoomStatus`, `DeriveIntegrityFaults`, and canonical mutation methods.
- `src/ChairSide.Board/Program.cs` - `RoomLifecycleEndpointHandler` and its bodyless Ready/Doctor Arrived response branches.
- `src/ChairSide.Board/Services/PrestagingLifecycleApiContracts.cs`
- `src/ChairSide.Board/Services/SqliteBoardRepository.cs`
- `src/ChairSide.Board/Services/RoomAssignmentContracts.cs` - `RoomCapabilitiesEvaluator` and assignment contracts.
- `src/ChairSide.Board/wwwroot/room-workflow.js` - Room Panel draft state, capability-driven controls, lifecycle transport, mutation reconciliation, and browser-local draft guards.
- `src/ChairSide.Board/wwwroot/board.js` - Room Panel composition, snapshot ownership, and the callback that applies one successful local room mutation.
- `tests/ChairSide.Board.Tests/PrestagingLifecycleTransitionTests.cs`
- `tests/ChairSide.Board.Tests/PrestagingLifecycleApiContractTests.cs`
- `tests/ChairSide.Board.Tests/PrestagingLifecycleEndpointTests.cs`
- `tests/ChairSide.Board.Tests/ReadyHandoffPersistenceTests.cs`
- `tests/ChairSide.Board.Tests/CanonicalAssignmentDomainValidationTests.cs`
- `tests/ChairSide.Board.Tests/FaultedReadyCancellationTests.cs`
- `tests/ChairSide.Board.Tests/MalformedAssignmentIntegrityTests.cs`
- `tests/ChairSide.Board.Tests/BoardReadyPresentationTests.cs`
- `tests/ChairSide.Board.Tests/RoomPanelPrestagingWorkflowTests.cs`
- `tests/ChairSide.Board.Tests/DurableFailureInjectionTests.cs`

## Knowledge-graph extraction follow-up

Issue #126 / PR #142 (`47da8f9`) corrected knowledge-graph comment/string false-positive extraction.
