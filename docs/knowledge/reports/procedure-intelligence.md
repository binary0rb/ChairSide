---
title: Procedure intelligence
tags: [reports, procedures, reporting-metrics, reporting-population, procedure-mix, sedation, domain-rule, active]
---

# Procedure intelligence

## Intent

Procedure Intelligence describes observed scheduling characteristics for procedures without evaluating procedure or provider performance. It is additive to Procedure Mix and preserves the existing ProcedureSummaries, BaseProcedureSummaries, DoctorProcedureMix, ScopedProcedureGroups, AllocationVariance, Schedule Fit, and doctor-flow contracts.

## Population authority

Each row corresponds exactly to one ScopedProcedureGroup population from the reusable report query:

1. whole-day UTC Window;
2. reporting annotations and standard inclusion rules;
3. Practice or Doctor and Sedation analytical scope;
4. standard included completed cases, which require Room Available;
5. Procedure Family or Detailed Variant grouping.

Sedation filtering occurs before grouping. Family grouping folds modifier variants into their base procedure and recomputes every statistic from the underlying combined cases. Detailed Variant keeps modifier variants distinct. Grouping changes aggregation, never population membership.

## Timing metrics

Doctor Time is truthful ordered Doctor Arrived -> Doctor Complete. Median Doctor Time, average Doctor Time, and Typical Doctor Time Range share the same contributor population. Accepted Ready is not required, and otherwise-standard legacy completed cases may contribute when their timestamps are truthful.

Ready Wait is truthful accepted Ready -> Doctor Arrived. A legacy case without truthful accepted Ready does not contribute a fabricated observation.

Seated -> Doctor Complete is the procedure case-flow context. It does not use TotalRoomCycleSeconds because that value extends through turnover to Room Available.

## Typical Doctor Time Range

Typical Doctor Time Range is the middle 50 percent of observed Doctor Time. Sort unrounded elapsed seconds as `x0 <= ... <= x(n-1)`. For `p = 0.25` and `p = 0.75`, use the Type 7 rule:

`h = (n - 1) * p`

`j = floor(h)`

`g = h - j`

`Q(p) = x[j] + g * (x[j+1] - x[j])`

The range is `Q(0.25) -> Q(0.75)`. Exact-index quantiles use the indexed observation. Interpolated values remain unrounded server-side and round only for presentation through the shared duration formatter.

The range uses the shared Doctor Time ReportSampleContext. Limited samples at `N = 1-4` retain Median Doctor Time and Limited context but publish null range endpoints. Sufficient samples at `N >= 5` publish both endpoints. A sufficient repeated sample may publish a truthful zero-width range. Existing reporting exceptions run before calculation; otherwise-included observations are not removed merely for being long or short.

The UI explains the range as `Middle 50% of observed Doctor Time.` It is not min/max, normal, expected, a target, predicted, ideal, acceptable, or Schedule Fit.

## Expected allocation context

Current roster default is the base procedure's current DefaultExpectedUnits converted to minutes. It is a rough starting allocation, not an authoritative case expectation. Historical assigned expectation uses finalized ExpectedAllocationMinutes. Historical captured default uses OriginalDefaultExpectedUnits and belongs in deeper disclosure.

Procedure Intelligence does not subtract allocation from Doctor Time, compare aggregate medians, calculate slack or debt, classify over or under, apply an At expected tolerance, or produce Calibration Insights. Those compatible paired-case interpretations belong to #219.

## Doctor x procedure disclosure

Practice rows may disclose represented active doctors in roster order followed by represented inactive or historical doctors in deterministic display-name and ID order. Doctor segments inherit the parent population lens and own independent sample contexts. Doctor scope omits a redundant one-doctor breakdown. Doctors are never ordered by timing, allocation, variance, or performance-like values.

## Source anchors

- `src/ChairSide.Board/Services/ReportsSnapshotBuilder.cs` - shared scoped procedure populations and Procedure Intelligence construction.
- `src/ChairSide.Board/Services/ProcedureIntelligenceReport.cs` - additive DTOs and Type 7 range calculation.
- `src/ChairSide.Board/wwwroot/reports.js` - median-first presentation and accessible disclosure.
- `tests/ChairSide.Board.Tests/ProcedureIntelligenceReportTests.cs` - statistical, population, grouping, timing, allocation-context, and contract guards.
- `tests/javascript/reports.test.mjs` - presentation, sample, disclosure, scope, and accessibility guards.
