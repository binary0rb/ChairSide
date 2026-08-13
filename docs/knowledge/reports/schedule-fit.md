---
title: Schedule Fit and Calibration Insights
tags: [reports, procedures, doctors, reporting-metrics, reporting-population, schedule-fit, domain-rule, active]
---

# Schedule Fit and Calibration Insights

## Intent

Schedule Fit evaluates scheduling assumptions, not doctor performance. Historical assigned Schedule Fit and current-default Calibration are separate questions with separate expected-value authorities.

Historical assigned Schedule Fit asks whether the allocation finalized for completed historical cases fit their observed case flow. Current-default Calibration asks whether the current roster starting allocation appears to fit the selected historical observed population. Neither may rank providers, infer capacity, or mutate a scheduling assumption.

## Shared population and observed timing

Both projections start from the current scoped standard included completed population. Membership requires Room Available and retains the canonical Window, Practice or Doctor scope, Sedation filter, reporting exclusions, and accepted or finalized doctor attribution.

The exact observed authority is truthful `SeatedAt -> DoctorCompleteAt` elapsed seconds. `SeatedAt <= DoctorCompleteAt` is required. Reversed intervals are excluded rather than clamped. New Reports math never uses individually rounded `MeasuredCaseFlowMinutes`; rounding and 10-minute scheduling blocks are presentation only.

Practice Schedule Fit ignores Procedure Grouping. Procedure Calibration reuses the same underlying `ScopedProcedurePopulation` instances as Scoped Procedure Groups and Procedure Intelligence. Family grouping recomputes from underlying cases, Detailed Variant keeps variants separate, and Sedation filtering occurs before grouping. Practice may include a deterministic roster-then-historical doctor x procedure disclosure; Doctor scope stays flat.

## Historical assigned Schedule Fit

The expected authority is positive finalized `CompletedRoomCycle.ExpectedAllocationMinutes`, converted to seconds. Each valid pair uses:

`varianceSeconds = observedSeconds - expectedAllocationMinutes * 60`

Case-level slack is `max(-varianceSeconds, 0)` and debt is `max(varianceSeconds, 0)`. Total expected, observed, slack, and debt are sums of exact case values. Signed net is both `observed - expected` and `debt - slack`. Slack and debt remain separately visible even when signed net is zero.

The response exposes the wider population count, paired count, coverage, exact totals, paired medians, tolerance counts, and `ReportSampleContext`. The existing integer-minute `ScheduleFitReport.Overall`, `ScheduleFitResult`, legacy allocation DTOs, and Workshop consumer remain compatibility contracts; Reports uses the additive exact projection.

## Current-default Calibration

The baseline authority is the current active base-procedure roster `DefaultExpectedUnits * 10` minutes. Historical assigned `ExpectedAllocationMinutes` and captured `OriginalDefaultExpectedUnits` are never substituted. A missing positive current default returns `CurrentDefaultUnavailable`, retains historical assigned fit, and produces no insight.

Version-one rules are server-owned and exposed as immutable response metadata:

- minimum pairs: 10;
- tolerance: -600 through +600 seconds inclusive is AtExpected;
- raw direction: negative BelowBaseline, zero EqualBaseline, positive AboveBaseline;
- directional consistency: at least 80 percent Above or Below using all pairs as the denominator;
- central method: median of all paired variance seconds;
- material rule: strictly greater than +600 seconds for More or strictly less than -600 seconds for Less, agreeing with the raw-sign candidate;
- persistence: selected population only, with no saved insight history.

The N=10 and 80 percent gates are operational review thresholds, not statistical significance. A descriptively Sufficient N=5 population can still be below the Calibration minimum without changing its general sample state. Reconsider these thresholds after enough production history exists to assess real segment volumes.

Only a server decision of `Qualified` creates a visible Calibration Insight. Non-qualified evaluations still expose deterministic decision counts, current default, candidate direction when present, directional share, tolerance counts, and paired median. JavaScript formats these values but never reconstructs policy thresholds.

## Evidence and safety

Qualified insights include non-PHI evidence for every paired case: completed-cycle ID, accepted Ready handoff ID when available, `CurrentRosterDefault` baseline and minutes, exact observed seconds, paired variance seconds, raw direction, and tolerance classification. Evidence reconciles to pair, direction, AtExpected, and median populations.

Callouts use neutral language such as `Calibration insight` and `Review the scheduling assumption.` They remain visually subtle and never use efficiency, performance, grade, score, warning, failure, slow, or fast framing.

## Source anchors

- `src/ChairSide.Board/Services/CalibrationInsightReport.cs` - additive DTOs, exact-second historical fit, rule metadata, Calibration decisions, and evidence.
- `src/ChairSide.Board/Services/ReportsSnapshotBuilder.cs` - standard scoped populations, active roster default resolution, grouping, and doctor ordering.
- `src/ChairSide.Board/Services/ScheduleFitReport.cs` - preserved compatibility Overall plus additive Reports projection.
- `src/ChairSide.Board/wwwroot/reports.js` - neutral Practice, procedure, doctor, and evidence presentation.
- `tests/ChairSide.Board.Tests/CalibrationInsightReportTests.cs` - exact math, paired median, gates, boundary, separation, and evidence guards.
- `tests/ChairSide.Board.Tests/ReportsSnapshotBuilderTests.cs` - population, grouping, scope, roster, and exact projection integration guards.
- `tests/javascript/reports.test.mjs` - server-authority and presentation guards.
