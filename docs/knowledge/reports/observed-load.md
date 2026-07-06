---
title: Observed load
tags: [reports, doctor-flow, observed-load, reporting-population, domain-rule, active, last-verified]
last_verified_commit: e2badc2
---

# Observed load

## Intent

Observed load describes how busy a doctor's observed clinic day looked, derived only from ChairSide room events. It is a descriptive read model surfaced in the selected-doctor Flow Breakdown tab. It is not a ranking, a score, or a productivity target, and it does not claim to know the doctor's true schedule, appointment book, or availability. It reports what the room events show, nothing more.

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

- `src/ChairSide.Board/Services/DemoBoardStore.cs` - `ObservedDoctorDay` record (~line 2308); `BuildObservedDoctorDays` (~1706); wired into the snapshot at `~604`.
- `tests/ChairSide.Board.Tests/BoardStoreTests.cs` - `Reports_observed_doctor_days_report_span_fields_for_included_completed_cycles` (~5034), `Reports_observed_doctor_days_bucket_active_room_minutes_by_concurrency` (~5082).
- `src/ChairSide.Board/wwwroot/board.js` - selected-doctor Flow Breakdown rendering of observed load.
- `docs/knowledge-graph/chairside.graph.md` - reporting-semantics notes reference the observed load read model.

## Verification notes

Verified at `e2badc2`: the `ObservedDoctorDay` record fields, the `BuildObservedDoctorDays` builder over `standardCompletedCycles`, and the span and concurrency-bucket tests are present. Line numbers are approximate.

Known limits: observed load and [procedure-mix](procedure-mix.md) are the newest report read models; their core intent (descriptive-only, standard population) is stable, but their UI presentation may still expand. Keep this note focused on intent and population, not on tab layout.
