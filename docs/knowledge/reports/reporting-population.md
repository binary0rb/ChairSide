---
title: Reporting population
tags: [reports, reporting-population, reporting-metrics, exception-handling, domain-rule, active, last-verified]
last_verified_commit: pending-issue-129
---

# Reporting population

## Population funnel

`GetReports` narrows persisted cycles in tiers:

1. `normalCycles` excludes manual-review exceptions (`!IsException`).
2. `standardCycles` also excludes derived reporting exceptions (`!IsExcludedFromStandardMetrics`).
3. `normalCompletedCycles` and `standardCompletedCycles` require `RoomAvailableAt`.

`standardCompletedCycles` remains the shared denominator for standard throughput, procedure, sedation, allocation, schedule-fit, trend, observed-day, and doctor procedure-mix calculations. Phase-complete timing surfaces retain their existing deliberately broader rules.

## Assignment and handoff attribution

The accepted Ready handoff is the finalized reporting assignment. Doctor Arrived accepts the current Active handoff; its immutable assignment supplies doctor, procedure, sedation, and confirmed expected allocation attribution. A withdrawn handoff remains auditable but never becomes accepted attribution and does not contribute its Ready interval to accepted Ready-to-arrival metrics.

Legacy completed cycles continue to use their existing finalized assignment data. ChairSide does not manufacture a Ready handoff or Ready timestamp for legacy history.

## Termination populations

- Pre-arrival cancellation and max-duration expiration create aborted assignment history and stay outside throughput.
- Pre-arrival after-hours terminations remain truthful aborted assignment history outside throughput, while a unified review projection surfaces them as pending `AfterHoursSweep` exceptions without manufacturing a completed clinical cycle.
- Post-arrival expiration creates only a review-required exception population. It preserves the last active state and does not fabricate `DoctorCompleteAt`.
- Excluded and exception records remain visible for audit/review; exclusion changes calculations, not durable visibility.
- Aging and stale threshold flags are captured for completed cycles from the accepted handoff interval without persisting new `Aging` or `Stale` primary room states.

## Source and test anchors

- `src/ChairSide.Board/Services/DemoBoardStore.cs` - `GetReports`, arrival acceptance, completion, expiration, and reporting builders.
- `src/ChairSide.Board/Services/SqliteBoardRepository.cs` - handoff, aborted-assignment, completed-cycle, and exception persistence.
- `tests/ChairSide.Board.Tests/BoardStoreTests.cs` - reporting population and exclusion characterization.
- `tests/ChairSide.Board.Tests/ReadyHandoffPersistenceTests.cs` - accepted/withdrawn handoff persistence and attribution.
- `tests/ChairSide.Board.Tests/DurableFailureInjectionTests.cs` - transaction rollback at cancellation and expiration boundaries.

Reporting date filters remain whole UTC calendar days, weekly buckets remain Monday-start UTC, and completed-cycle windows remain anchored on `DoctorCompleteAt`.
