---
title: Deterministic stress fixtures
tags: [tests, room-lifecycle, reporting-population, exception-handling, data-persistence, test-coverage, active, last-verified]
last_verified_commit: 4c1a6f7
---

# Deterministic stress fixtures

## Intent

`reset-stress-fixture` creates deterministic non-PHI board, Doctor View, and reporting scenarios. On a fixed clock, a profile has deterministic current-state shape and counts; generated GUID identities and the total count of preserved resolved history are not promised to converge.

## Canonical live-room representation

Ready-like fixture targets persist primary state `ReadyForDoctor`, a complete canonical assignment, an episode, an owned Active `ready_handoffs` row, and the matching `ActiveReadyHandoffId`. `ReadyUrgency` projects as None, Aging, or Stale from that handoff's threshold-relative `ReadyAt`. Fixtures do not manufacture new primary `Aging` or `Stale` rows and do not project integrity faults merely because setup omitted a handoff.

Directly seeded `DoctorInRoom` and `Turnover` rooms retain their matching in-progress completed-cycle rows so subsequent lifecycle actions have truthful reporting state.

## Profiles

- `reporting-volume` delegates to the large synthetic completed-cycle seeder.
- `live-board-stress` fills the board with every presentation posture: Available, Seated, Ready with None/Aging/Stale urgency, Doctor In Room, and Turnover.
- `doctor-view-stress` creates the fixed 1/3/4/4 assigned-room posture.
- `doctor-view-overflow-stress` gives one doctor five assigned rooms.
- `scenario-rich` creates bounded clean history, date-window markers, isolated derived exceptions, and one manual review candidate.
- `full-stress` composes live-board and scenario-rich fixtures.
- `all-scenarios` adds reporting-volume history and shifts bulk scenario history to avoid shared `(RoomId, SeatedAt)` slots.

## Atomic reset contract

Before seeding, one repository transaction:

1. counts and deletes completed cycles;
2. deletes every Active handoff;
3. deletes active rooms; and
4. recreates configured Available rooms.

Withdrawn, Accepted, and Terminated handoffs are preserved. Aborted assignments are preserved. The reset is broader than deleting only handoffs referenced by rooms because the maintenance command promises a clean current board and Active-handoff set. After reset, no Active handoff remains unless the selected fixture creates it and exactly one matching active room references it.

Repeated runs converge in current room state and Active-handoff counts. Preserved resolved history accumulates only through normal fixture behavior, so neither total historical rows nor generated GUIDs are deterministic promises.

The command is maintenance-only, non-HTTP, confirmation-token gated, allowlisted only in Development and Training, and hard-refused in Production before application build or repository construction. It never runs during normal startup.

## Source and test anchors

- `src/ChairSide.Board/Services/MaintenanceCommands.cs`
- `src/ChairSide.Board/Services/DemoBoardStore.cs` - `ResetAndSeedStressFixture`, fixture builders, and summary.
- `src/ChairSide.Board/Services/SqliteBoardRepository.cs` - `ResetMaintenanceState`.
- `tests/ChairSide.Board.Tests/BoardStoreTests.cs` - CLI/profile and scenario coverage.
- `tests/ChairSide.Board.Tests/LifecycleFixtureCorrectionTests.cs` - canonical Ready fixture, repeated reset, and development-seed durability coverage.
