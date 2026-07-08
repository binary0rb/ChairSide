---
title: Deterministic stress fixtures
tags: [tests, room-lifecycle, reporting-population, exception-handling, test-coverage, active, last-verified]
last_verified_commit: pending-pr
---

# Deterministic stress fixtures

## Intent

Casual manual testing with one or two seeded rooms missed a real layout bug: the master board's middle row rendered visually shorter than the other rows once all three rows were populated. No fixture ever filled the whole grid, so the bug only surfaced by accident. `reset-stress-fixture` exists to seed deliberate, deterministic high-pressure visual and reporting states so these edge cases are caught on purpose instead of by luck.

Deterministic means the same profile, run on the same clock, always reproduces the same fixture - no `Random`, no wall-clock-seeded jitter. This makes the fixtures reliable both for manual visual review and for automated tests.

## Profiles

`reset-stress-fixture --profile <name>` accepts exactly six profiles (`MaintenanceCommands.StressFixtureProfiles` is the single source of truth):

- `reporting-volume` - delegates to the existing large synthetic completed-cycle seeder (no new logic).
- `live-board-stress` - all 12 master-board rooms filled, every room state present at least once, including one intentionally unassigned `AVAILABLE` room, sedation cases, and a long-label procedure.
- `doctor-view-stress` - a fixed 1/3/4/4 active-room split across the four doctors, every counted room pre-arrival, exercising the 1-room, 3-room (quiet fourth quadrant), and 4-room Doctor View postures.
- `doctor-view-overflow-stress` - one doctor at 5 active rooms (the named overflow default - an odd count so the extra row is ragged, the higher-risk visual case), others at 3/2/2.
- `scenario-rich` - an extended (120-day) clean completed-cycle history, plus one clean cycle in each report date-range bucket boundary (Today; outside Today but inside Last-7; outside Last-7 but inside Last-30; older than Last-30), plus one cycle per derived reporting-exception reason (`UnmappedProcedure`, `LegacyProcedure`, `ExtremeDuration`, `OvernightLifecycle`, `MissingTiming`), each isolated so only its own predicate trips, plus one manual audit-review candidate.
- `full-stress` - composes `live-board-stress`'s live-room shape (built with the doctor-view-overflow allocation, so one doctor has 5 active rooms) with `scenario-rich`'s history and edge cases. No bespoke seeding logic of its own. Renders all 12 room cards: 11 assigned/active rooms plus 1 intentionally unassigned `AVAILABLE` room - not "all 12 active."

`--completed-cycles` is only valid with `--profile reporting-volume`; it is refused (not silently ignored) for every other profile.

## Constraints

- Maintenance-only, non-HTTP, Development/test-safe: hard-refuses to run in Production regardless of confirmation token (`MaintenanceCommands.IsProductionForbidden`), and there is no `/api/...` endpoint or startup-seeding path for it.
- Destructive to whatever database it targets: it clears all completed cycles and resets every active room to Available before seeding.
- Does not change report calculations, the reporting population rules, room lifecycle behavior, device/room binding, the non-PHI boundary, or doctor/procedure/sedation meanings. Reporting exceptions are triggered by feeding data that trips the *existing* derivation (`AnnotateReportingExceptions`); the derivation logic itself is untouched.
- A directly-seeded `IN ROOM`/`TURNOVER` room also gets a paired in-progress completed-cycle row (`RoomAvailableAt` still null) so a later manual Doctor Complete/Room Available click on that room still updates reporting data correctly. That in-progress row is not counted as seeded history: `StressFixtureResult.InProgressCycleRowsSeeded` reports it separately from the completed-history counts and date range.
- Doctor View's current-room count is assignment-based, not state-filtered - see [doctor-view-operational-header](../ui/doctor-view-operational-header.md). `doctor-view-stress` and `doctor-view-overflow-stress` keep every counted room pre-arrival for that reason, so a room's count is never accidentally inflated by an assigned `IN ROOM`/`TURNOVER` room.

## Source anchors

- `src/ChairSide.Board/Services/MaintenanceCommands.cs` - the `reset-stress-fixture` command/token, `--profile` parsing and validation, `StressFixtureProfiles`, and the `--completed-cycles`-only-for-`reporting-volume` refusal.
- `src/ChairSide.Board/Services/DemoBoardStore.cs` - `ResetAndSeedStressFixture` (entry point and `StressFixtureResult` summary), `SeedLiveRoomFixtures` / `BuildLiveRoom` (live-room profiles), the four `*Fixtures()` allocation tables, `SeedScenarioRichHistory` / `SeedScenarioRichEdgeCases` (history and edge cases), `ComputeArrivalWaitState` (wait-state fields for directly-seeded in-progress rows).
- `src/ChairSide.Board/Program.cs` - `RunMaintenance`'s `reset-stress-fixture` dispatch and `PrintStressFixtureSummary`.
- `tests/ChairSide.Board.Tests/BoardStoreTests.cs` - the "Deterministic stress fixtures" test region: CLI resolution tests and per-profile `DemoBoardStore` tests (room-state/doctor-count coverage, pre-arrival posture, bounded-range derived-exception isolation, date-range bucket population, in-progress-row exclusion from history, full-stress composition).

## Verification notes

All tests in the "Deterministic stress fixtures" region pass (`dotnet test`, 304/304 total at time of writing). Known limits: this note does not enumerate every fixture's exact room/procedure assignment table - read the `*Fixtures()` methods directly for that level of detail; this note explains intent and invariants, not the literal data.
