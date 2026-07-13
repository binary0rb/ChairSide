---
title: Room lifecycle
tags: [room, board, room-lifecycle, data-persistence, permissions, device-binding, domain-rule, active, last-verified]
last_verified_commit: pending-issue-119
---

# Room lifecycle

## Canonical sequence

ChairSide tracks room episodes, not patients. The canonical lifecycle is:

1. Begin Prestage creates `EpisodeId`, records `PrestageStartedAt`, and enters `Prestaging` without requiring an assignment.
2. Save Details explicitly persists an absent, partial, or complete assignment while `Prestaging` or `Seated`.
3. Seat Room records truthful `SeatedAt`. An assignment-bearing Seat may persist the supplied canonical draft in the same transaction.
4. Ready for Doctor requires a complete, currently valid, durably saved assignment. It enters primary state `ReadyForDoctor`, creates an owned Active handoff, and locks the assignment.
5. Doctor Arrived accepts that handoff, clears Ready urgency, records `DoctorArrivedAt`, and enters `DoctorInRoom`.
6. Doctor Complete records `DoctorCompleteAt` and enters `Turnover`.
7. Room Available records completion and releases the room to `Available`.

Draft-bearing Ready is not part of the current store/API/UI boundary. It is deferred to issues #120 and #121.

## Ready handoff and urgency

`ReadyForDoctor` is the primary persisted state. `Aging` and `Stale` are `ReadyUrgency` projections from the owned Active handoff's `ReadyAt`; new canonical writes do not persist them as primary states. Legacy persisted `Aging` and `Stale` rows remain readable recovery states.

Withdraw Ready terminates the current handoff as `Withdrawn`, returns the room to `Seated`, preserves `EpisodeId`, `PrestageStartedAt`, and `SeatedAt`, and clears urgency immediately. Assignment details may then be corrected. Reissuing Ready creates a different `HandoffId` and a new `ReadyAt`, so the withdrawn interval does not contribute to the new urgency interval. Doctor Arrived accepts the current handoff and also clears urgency immediately.

A valid legacy `Aging` or `Stale` row with a matching owned Active handoff may withdraw safely. Recovery never fabricates a missing handoff or rewrites an invalid or unrelated handoff.

## Cancellation, expiration, and recovery

Pre-arrival cancellation and expiration create aborted-assignment history, not throughput. Faulted pre-arrival Ready rows remain visible and safely cancellable; unrelated or invalid handoff records are not rewritten. Post-arrival expiration creates a review-required exception cycle without inventing `DoctorCompleteAt` or other lifecycle timestamps.

Restart recovery restores durable truth and projects urgency and integrity without mutating the database. Live room state changes only after the repository transaction succeeds.

## Concurrency and durability

Canonical assignment writes capture the originally loaded `RoomId`, nullable `EpisodeId`, state, and nullable `ActiveReadyHandoffId`. The guarded SQLite update compares all four values, using null-safe `IS` for nullable identities. A stale context receives `null`; it does not mutate live memory, retry, reload, append an event, regress Ready, overwrite the locked assignment, or orphan a handoff. SQLite failures throw and roll back transaction-local writes.

## Authorization and conflict handling

Room lifecycle mutation remains room-local and device-token guarded. Doctors are read-only. Doctor-arrival conflict resolution revalidates current server state, auto-completes the previous room only into `Turnover`, and audits both rooms.

## Source and test anchors

- `src/ChairSide.Board/Services/DemoBoardStore.cs`
- `src/ChairSide.Board/Services/SqliteBoardRepository.cs`
- `src/ChairSide.Board/Services/RoomAssignmentContracts.cs`
- `tests/ChairSide.Board.Tests/PrestagingLifecycleTransitionTests.cs`
- `tests/ChairSide.Board.Tests/ReadyHandoffPersistenceTests.cs`
- `tests/ChairSide.Board.Tests/CanonicalAssignmentDomainValidationTests.cs`
- `tests/ChairSide.Board.Tests/FaultedReadyCancellationTests.cs`
- `tests/ChairSide.Board.Tests/MalformedAssignmentIntegrityTests.cs`
- `tests/ChairSide.Board.Tests/DurableFailureInjectionTests.cs`

## Separate known issue

The after-hours sweep currently advances `_lastSweepDate` before persistence succeeds. A failure can suppress same-day retry, and earlier rooms can commit before a later room fails. That retry and batch-atomicity defect is separate from issue #119.
