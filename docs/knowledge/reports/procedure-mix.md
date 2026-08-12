---
title: Procedure mix
tags: [reports, doctor-flow, procedure-mix, reporting-population, sedation, domain-rule, active, last-verified]
last_verified_commit: f74561c
---

# Procedure mix

## Intent

Procedure mix shows the breakdown of a doctor's completed cases by procedure variant, surfaced in the selected-doctor Procedure Mix tab. It answers "what kinds of cases did this doctor complete in range, and in what proportion," as descriptive context. It is an additive read model: it introduced no schema change and did not alter existing metric semantics.

## Approved redesign boundary

This note describes the currently implemented doctor-scoped Procedure Mix read model. The approved target semantics for the reporting redesign are canonical in `docs/design/reporting-design.md`.

Under issues #213 and #215:

- Procedure Mix exists at both Practice and Doctor scope;
- each percentage uses the current scoped standard included completed-case population as its denominator;
- counts and percentages are presented together with visible sample-size context;
- Procedure Family and Detailed Variant grouping remain distinct lenses;
- Sedation remains a modifier/filter context rather than a separate procedure case.

Until those issues land, keep this note as implementation truth and use the canonical reporting design as target semantic truth.

## What it reports

Per doctor and per procedure variant (`DoctorProcedureMixRow`):

- `ProcedureCode` (variant-level, for example `EXT` or `EXT+SED`), `BaseProcedureCode`, and `IsSedationCase`.
- `CaseCount` for that variant, the doctor's total completed-case denominator, and that variant's share of the doctor's completed cases.

Grouping is by doctor plus procedure variant, so sedation variants stay separate from their base (`EXT+SED` is its own row, distinct from `EXT`), consistent with sedation being a modifier and not a separate procedure (see [sedation-as-modifier](sedation-as-modifier.md)).

Population: built over `standardCompletedCycles`, the same standard completed-cycle population as the other calculated metrics (see [reporting-population](reporting-population.md)).

## Constraints

- Keep grouping at doctor plus procedure variant in the currently implemented view; do not collapse sedation into the base in that view.
- Keep it over the standard completed population; do not include incomplete or reporting-exception cycles.
- Present it as descriptive mix, not as a ranking or a target.

## Source anchors

- `src/ChairSide.Board/Services/ReportsSnapshotBuilder.cs` - `BuildDoctorProcedureMix` and the standard completed population supplied by `Compose`.
- `tests/ChairSide.Board.Tests/BoardStoreReportingTests.cs` - doctor Procedure Mix grouping, denominator isolation, exclusion, and blank-doctor characterization tests.
- `src/ChairSide.Board/wwwroot/board.js` - `renderSelectedDoctorProcedures` (Procedure Mix tab rendering).
- `docs/knowledge-graph/chairside.graph.md` - procedure-mix nodes and reporting-semantics notes.

## Verification notes

Verified at `f74561c`: the current `DoctorProcedureMixRow` builder still groups by doctor plus detailed procedure variant over `standardCompletedCycles` and uses a per-doctor denominator. Practice-scope Procedure Mix is intentionally deferred to #215.
