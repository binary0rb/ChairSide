---
title: Observed load
tags: [reports, doctor-flow, observed-load, reporting-population, domain-rule, active, last-verified]
last_verified_commit: 7e566bd
---

# Observed load

## Intent

Observed Doctor Flow describes the room flow ChairSide actually observes for a doctor. It is a descriptive read model surfaced in Doctor Overview and the selected-doctor Room Load / Flow tab. It is not a ranking, score, productivity target, attendance record, or claim about a doctor's schedule. It reports only what ChairSide room events show.

## Canonical implementation

Issue #216 adds two canonical, server-owned projections without changing the older public contract:

- `ObservedDoctorFlowDay` / `observedDoctorFlowDays` is the Ready-anchored day authority for Doctor Flow presentation.
- `DoctorFlowSummary` / `doctorFlowSummaries` is the range-level Doctor Overview authority. It calculates medians from underlying phase observations and canonical day rows, never from monthly medians.

A case contributes to a canonical observed doctor-day only when it belongs to the scoped standard included completed population and has truthful, ordered Ready, Doctor Arrived, and Doctor Complete timestamps on the same UTC date. The finalized completed-cycle Ready timestamp is the accepted Ready reporting timestamp. Missing Ready is never inferred.

Canonical days are grouped by doctor and Doctor Complete UTC date. Dates without qualifying activity are omitted and never zero-filled.

## Time model

For each canonical observed doctor-day:

- Observed Clinical Span is `[earliest accepted Ready, latest qualifying Doctor Complete)`.
- Doctor Working is each `[Doctor Arrived, Doctor Complete)` interval.
- Exact elapsed time is swept into mutually exclusive zero, one, two, and three-or-more Doctor Working room buckets.
- Unstructured Time is the zero-active Doctor Working portion of Observed Clinical Span. It must not be interpreted as idle, unused, available, unproductive, recoverable, or unscheduled time.
- Peak Concurrent Rooms is the maximum simultaneous Doctor Working interval count.
- Room Available does not extend the span, and Seated starts neither the span nor Doctor Working.

Exact durations are accumulated before conversion to whole minutes. Largest-remainder apportionment uses stable bucket order - Unstructured, one room, two rooms, three-or-more rooms - and guarantees that the displayed buckets sum exactly to displayed Observed Clinical Span.

## Metric grains

Doctor Overview intentionally uses separate reporting grains:

- Completed Cases uses scoped standard included completed history and therefore requires Room Available.
- Median Ready Wait and Median Doctor Time use truthful contributors from the scoped standard phase population and do not require Room Available.
- Median Observed Clinical Span, Peak Concurrent Rooms, and Observed Doctor Days use canonical qualifying `ObservedDoctorFlowDay` rows.

The nested server-owned `ReportSampleContext` values preserve Empty, Unavailable, Limited, and Sufficient states at each metric's proper grain. JavaScript does not recreate the sample threshold.

## Compatibility projection

`ObservedDoctorDay` / `observedDoctorDays` remains an additive compatibility payload with its established first-Seated Observed Clinical Span, Seated-to-Doctor-Complete active-room concurrency, Observed Team Span, and legacy field names. Issue #216 does not redefine or remove those fields.

New Doctor Flow presentation must use `observedDoctorFlowDays`. Future Doctor Trends work should also build from the canonical projection rather than the compatibility model.

## Constraints

- Present canonical flow as descriptive operational context only.
- Do not create rankings, scores, attendance claims, or productivity interpretations.
- Keep canonical days over scoped standard included completed history. Phase timing summaries retain their distinct scoped standard phase population.
- Keep compatibility and canonical projection names explicit in code, tests, and UI selection.

## Source anchors

- `src/ChairSide.Board/Services/ReportsSnapshotBuilder.cs` - `BuildObservedDoctorFlowDays`, `BuildObservedDoctorWorkingConcurrency`, `BuildDoctorFlowSummaries`, and the separate compatibility `BuildObservedDoctorDays` path.
- `tests/ChairSide.Board.Tests/ReportsSnapshotBuilderTests.cs` - canonical qualification, mixed legacy/canonical populations, exact concurrency partitioning, medians, samples, ordering, and additive JSON contracts.
- `tests/ChairSide.Board.Tests/BoardStoreReportingTests.cs` - reissued accepted Ready integration coverage.
- `src/ChairSide.Board/wwwroot/reports.js` - Doctor Overview and Room Load / Flow presentation from the canonical projections.
- `docs/knowledge-graph/chairside.graph.md` - reporting-semantics notes reference the observed load read model.

## Verification notes

Verified while implementing issue #216 from baseline `7e566bd`: the new canonical projections are additive. The older `ObservedDoctorDay` semantics remain intact for compatibility, while Doctor Overview and Room Load / Flow use the Ready-anchored `ObservedDoctorFlowDay` and server-owned `DoctorFlowSummary` contracts.
