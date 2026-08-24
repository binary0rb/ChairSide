---
title: Reporting population
tags: [reports, reporting-population, reporting-metrics, exception-handling, domain-rule, active, last-verified]
last_verified_commit: fe60949
---

# Reporting population

## Status boundary

**Current implementation:** The `active` tag and `last_verified_commit` apply to the current source/test behavior described below. Production still uses the existing exception flag, global Review Queue scope, accepted-handoff attribution, and pending/read-only-reviewed exception projections.

**Approved target under #234:** The separately labeled target section records the approved design gate. It is not implemented by PR #235 and is not covered by `last_verified_commit`.

## Population funnel

`GetReports` narrows persisted cycles in tiers:

1. `normalCycles` excludes manual-review exceptions (`!IsException`).
2. `standardCycles` also excludes derived reporting exceptions (`!IsExcludedFromStandardMetrics`).
3. `normalCompletedCycles` and `standardCompletedCycles` require `RoomAvailableAt`.

`standardCompletedCycles` remains the shared denominator for standard throughput, procedure, sedation, allocation, schedule-fit, trend, observed-day, and doctor procedure-mix calculations. Phase-complete timing surfaces retain their existing deliberately broader rules.

Ordinary startup and live-board operation do not retain lifetime completed history. Report reads obtain completed rows through the selected `DoctorCompleteAt` window, obtain exception review rows through their separate truthful review anchors, and ask SQLite for the exact scoped all-time completed count. Operational lifecycle updates and integrity checks retrieve only the single durable completed cycle belonging to the active room episode.

## Reusable report query and sample state

`ReportQuery` keeps three responsibilities distinct:

- `Window` selects whole UTC report days.
- Practice or historical/current Doctor plus Sedation scope selects the analytical population.
- Procedure Family or Detailed Variant selects aggregation without filtering population membership.

Doctor scopes accept historical doctor IDs independently of the active assignment roster. Audit requests inherit the normalized parent query. A doctor segment under Practice remains Practice scope plus `segmentDoctorId`; it is not rewritten as a Doctor query. The action-required Review Queue currently ignores analytical Doctor and Sedation scope and remains global within the selected date window.

## Evidence population boundaries

- Completed-case audit includes normal history with Room Available. Practice audit includes reporting-excluded facts with neutral standing and reasons; included or segmented audit uses the standard included population.
- Metric evidence selects the exact standard contributor population for Ready Wait, Seated -> Doctor, Doctor Time, Turnover, Procedure Mix, or Schedule Fit. Truthful phase evidence may be shown before Room Available and is labeled Metric evidence rather than completed throughput.
- Exception review is separate from both populations. Pending records remain actionable; reviewed records remain in a quiet read-only history.

Normal audit and analytical evidence preserve `DoctorCompleteAt` window authority. Review is selected independently: completed exceptions use the latest truthful anchor in `DoctorCompleteAt`, `DoctorArrivedAt`, `SeatedAt`, then `PrestageStartedAt`; aborted assignments use `TerminatedAt`. Review counts therefore do not disappear merely because an exception lacks Doctor Complete.

The paged audit endpoint owns population membership, exact-second projection, sort, and local standing narrowing. `RecentCompletedCycles` remains compatibility context only and is not audit authority.

`ReportSampleContext` carries population count, contributing count, state, threshold, and comparison eligibility. Empty is `N = 0`, Limited is `N = 1-4`, and Sufficient is `N >= 5`. A metric with a nonempty population and zero contributors is Unavailable. Comparison language requires every compared population to be Sufficient. Calibration Insights retain separate, stricter N >= 10 evidence eligibility without reclassifying the general descriptive sample state; the full server-owned rule is documented in `schedule-fit.md`.

## Assignment and handoff attribution

The accepted Ready handoff is the current finalized reporting assignment. Doctor Arrived accepts the current Active handoff; its immutable assignment supplies doctor, procedure, sedation, and confirmed expected allocation attribution. A withdrawn handoff remains auditable but never becomes accepted attribution and does not contribute its Ready interval to accepted Ready-to-arrival metrics.

Legacy completed cycles continue to use their existing finalized assignment data. ChairSide does not manufacture a Ready handoff or Ready timestamp for legacy history.

## Termination populations

- Pre-arrival cancellation and max-duration expiration create aborted assignment history and stay outside throughput.
- Pre-arrival after-hours terminations remain truthful aborted assignment history outside throughput, while a unified review projection surfaces them as pending `AfterHoursSweep` exceptions without manufacturing a completed clinical cycle.
- Post-arrival expiration creates only a review-required exception population. It preserves the last active state and does not fabricate `DoctorCompleteAt`.
- Excluded and exception records remain visible for audit/review; exclusion changes calculations, not durable visibility.
- Aging and stale threshold flags are captured for completed cycles from the accepted handoff interval without persisting new `Aging` or `Stale` primary room states.

## Approved target under #234

Issue #234 keeps accepted Ready and lifecycle facts immutable while allowing an explicit historical correction overlay to become the current effective reporting value. It replaces the current split pending/read-only-reviewed projection with one append-only encounter ledger whose resolved review may be reopened. Needs Review provisionally excludes immediately, Confirmed Exception excludes the whole encounter, and Cleared removes only the administrative gate without manufacturing completion or metric facts.

Under that approved target, Data Quality and its default review drill-down derive from the active report population and inherit applicable date, Doctor, Sedation, procedure/drill-down, and approved analytical filters. Exhaustive raw history may broaden deliberately. Filter membership uses current effective recorded facts and never infers missing doctor, procedure, or sedation values.

## Source and test anchors

- `src/ChairSide.Board/Services/DemoBoardStore.cs` - `GetReports`, arrival acceptance, completion, expiration, and reporting builders.
- `src/ChairSide.Board/Services/SqliteBoardRepository.cs` - handoff, aborted-assignment, completed-cycle, and exception persistence.
- `tests/ChairSide.Board.Tests/BoardStoreTests.cs` - reporting population and exclusion characterization.
- `tests/ChairSide.Board.Tests/ReadyHandoffPersistenceTests.cs` - accepted/withdrawn handoff persistence and attribution.
- `tests/ChairSide.Board.Tests/DurableFailureInjectionTests.cs` - transaction rollback at cancellation and expiration boundaries.

Reporting date filters remain whole UTC calendar days, weekly buckets remain Monday-start UTC, and completed-cycle windows remain anchored on `DoctorCompleteAt`.

Doctor Trends apply the selected report window before weekly aggregation and never fetch earlier history to fill an 8-12 week display. One shared server-owned skeleton of at most the trailing 12 intersecting weeks is used for every doctor series. Empty calendar buckets remain null-valued No observation gaps, and clipped effective boundaries disclose partial first and last weeks.

The report UI exposes Today, Last 7 Days, Last 30 Days, Month to Date, Custom, and All Time. Reversed valid ranges normalize and return normalized metadata; malformed date text keeps graceful legacy behavior.
