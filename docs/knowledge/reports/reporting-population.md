---
title: Reporting population
tags: [reports, reporting-population, reporting-metrics, exception-handling, domain-rule, active, last-verified]
last_verified_commit: ca75b09
---

# Reporting population

## Population funnel

`GetReports` narrows persisted cycles in tiers:

1. `normalCycles` excludes manual-review exceptions (`!IsException`).
2. `standardCycles` also excludes derived reporting exceptions (`!IsExcludedFromStandardMetrics`).
3. `normalCompletedCycles` and `standardCompletedCycles` require `RoomAvailableAt`.

`standardCompletedCycles` remains the shared denominator for standard throughput, procedure, sedation, allocation, schedule-fit, trend, observed-day, and doctor procedure-mix calculations. Phase-complete timing surfaces retain their existing deliberately broader rules.

## Reusable report query and sample state

`ReportQuery` keeps three responsibilities distinct:

- `Window` selects whole UTC report days.
- Practice or historical/current Doctor plus Sedation scope selects the analytical population.
- Procedure Family or Detailed Variant selects aggregation without filtering population membership.

Doctor scopes accept historical doctor IDs independently of the active assignment roster. The analytical Case Audit follows Doctor and Sedation scope. The action-required Review Queue ignores analytical scope and remains global within the selected date window.

`ReportSampleContext` carries population count, contributing count, state, threshold, and comparison eligibility. Empty is `N = 0`, Limited is `N = 1-4`, and Sufficient is `N >= 5`. A metric with a nonempty population and zero contributors is Unavailable. Comparison language requires every compared population to be Sufficient. Calibration Insights retain separate, stricter evidence rules under #219.

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

The report UI exposes Today, Last 7 Days, Last 30 Days, Month to Date, Custom, and All Time. Reversed valid ranges normalize and return normalized metadata; malformed date text keeps graceful legacy behavior.
