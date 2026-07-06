---
title: Reporting population
tags: [reports, reporting-population, exception-handling, domain-rule, active, last-verified]
last_verified_commit: e2badc2
---

# Reporting population

## Intent

Almost every ChairSide report metric is computed over one shared population of completed room cycles. Getting that population wrong (mixing in incomplete cycles, or cycles that were excluded for data-hygiene reasons) is the most common way to make a metric quietly misleading. This note records how the population is scoped so future changes reuse the same funnel instead of inventing a new one.

## The population funnel

Starting from all persisted cycles, `GetReports` narrows in tiers:

1. `normalCycles` = all cycles that are not manual-review exceptions (`!IsException`).
2. `standardCycles` = `normalCycles` that are also not reporting-exceptions (`!IsExcludedFromStandardMetrics`). Reporting exceptions are data-hygiene exclusions such as legacy/unmapped procedure codes, extreme durations, and overnight lifecycles.
3. `normalCompletedCycles` / `standardCompletedCycles` = the above, further restricted to cycles that reached Room Available (`RoomAvailableAt` is set).

`standardCompletedCycles` is the shared denominator. At `e2badc2` it feeds procedure summaries, sedation vs non-sedation counts, base-procedure summaries, allocation variance, schedule fit, weekly wait trends, observed doctor-days, and doctor procedure mix. If a new calculated metric is added, it should use this same population unless there is a documented reason not to.

## Constraints

- Do not compute standard metrics over incomplete cycles.
- Do not silently include reporting-exception cycles in a calculated metric.
- Excluded cycles are not deleted or hidden: they remain visible in the raw/audit output with badges and reasons. Exclusion affects calculation, not visibility.
- Reporting population semantics are locked by characterization tests. Change them only by intentionally updating those tests and the human-authored knowledge docs together.

## Source anchors

- `src/ChairSide.Board/Services/DemoBoardStore.cs` - `GetReports`: `normalCycles` (line ~541), `standardCycles` (~544), `normalCompletedCycles` / `standardCompletedCycles` (~548-553), pending-review `exceptionCycles` (~558-561), and the `ReportsSnapshot` assembly consuming `standardCompletedCycles` (~589-605).
- `tests/ChairSide.Board.Tests/BoardStoreTests.cs` - reporting-exception exclusion tests (legacy/unmapped/extreme/overnight around lines ~1130-1366, each asserting `IsExcludedFromStandardMetrics`); standard-population trend and schedule-fit tests (`Reports_expose_weekly_wait_trends_over_standard_completed_population` ~4858, `Reports_expose_schedule_fit_over_standard_completed_population` ~4952).
- `docs/knowledge-graph/decisions.md` - "Reporting population semantics".
- `docs/knowledge-graph/chairside.graph.md` - "Current reporting semantics captured by the graph".

## Verification notes

Verified at `e2badc2`: the three-tier funnel and the `RoomAvailableAt` completion gate are present as described, and `standardCompletedCycles` is the argument passed to every standard-metric builder in the snapshot assembly. Line numbers are approximate.

Known limits: phase-complete timings (which can contribute before full Room Available completion) and the exact set of reporting-exception reasons are related but out of scope for this note; see the reporting-semantics section of `chairside.graph.md`.
