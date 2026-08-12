---
title: Observed load
tags: [reports, doctor-flow, observed-load, reporting-population, domain-rule, active, last-verified]
last_verified_commit: f74561c
---

# Observed load

## Intent

Observed load describes how busy a doctor's observed clinic day looked, derived only from ChairSide room events. It is a descriptive read model surfaced in the selected-doctor Flow Breakdown tab. It is not a ranking, a score, or a productivity target, and it does not claim to know the doctor's true schedule, appointment book, or availability. It reports what the room events show, nothing more.

## Approved redesign boundary

This note describes the currently implemented observed-load read model. The approved target semantics for the reporting redesign are canonical in `docs/design/reporting-design.md`.

Under issue #216:

- Observed Clinical Span changes from the current first-Seated start to the first qualifying accepted Ready handoff;
- room-load concurrency changes from Seated-to-Doctor-Complete room intervals to Doctor Working intervals (`DoctorArrivedAt -> DoctorCompleteAt`);
- overlapping Doctor Working intervals use wall-clock union/sweep-line accounting;
- Unstructured Time becomes the span remainder with no active Doctor Working interval;
- no-activity doctor-days are omitted rather than represented as zero.

Until #216 lands, keep this note as implementation truth and use the canonical reporting design as target semantic truth. Do not describe the current first-Seated span as the approved redesigned definition.

## What it reports

Per doctor and per observed day (`ObservedDoctorDay`):

- Encounter count and the day's first-seated / first-doctor-arrived / last-doctor-complete / last-room-available timestamps.
- Observed clinical span and observed team span (whole-minute durations across the day).
- Room-overlap concurrency buckets: minutes with one active room, two active rooms, and three-or-more active rooms, plus the peak active room count.

Population: built over `standardCompletedCycles`, the same standard completed-cycle population as the other calculated metrics (see [reporting-population](reporting-population.md)). It is additive - it introduced no schema change and did not alter existing metric semantics.

## Constraints

- Present it as descriptive context only. Do not frame observed load as a performance ranking, a capacity guarantee, or recoverable time.
- Overlap/concurrency is an observation about rooms, not a judgment about a person.
- Keep it over the standard completed population; do not widen it to include incomplete or reporting-exception cycles.

## Source anchors

- `src/ChairSide.Board/Services/ReportsSnapshotBuilder.cs` - `BuildObservedDoctorDays` and `BuildObservedRoomConcurrency`.
- `tests/ChairSide.Board.Tests/BoardStoreReportingTests.cs` - `Reports_observed_doctor_days_report_span_fields_for_included_completed_cycles` and `Reports_observed_doctor_days_bucket_active_room_minutes_by_concurrency`.
- `src/ChairSide.Board/wwwroot/board.js` - selected-doctor Flow Breakdown rendering of observed load.
- `docs/knowledge-graph/chairside.graph.md` - reporting-semantics notes reference the observed load read model.

## Verification notes

Verified at `f74561c`: the current `ObservedDoctorDay` builder still starts Observed Clinical Span at first Seated and builds concurrency from Seated-to-Doctor-Complete intervals over `standardCompletedCycles`. Those current semantics are intentionally superseded by the approved target design once #216 is implemented.
