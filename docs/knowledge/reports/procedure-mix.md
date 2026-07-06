---
title: Procedure mix
tags: [reports, doctor-flow, procedure-mix, reporting-population, sedation, domain-rule, active, last-verified]
last_verified_commit: e2badc2
---

# Procedure mix

## Intent

Procedure mix shows the breakdown of a doctor's completed cases by procedure variant, surfaced in the selected-doctor Procedure Mix tab. It answers "what kinds of cases did this doctor complete in range, and in what proportion," as descriptive context. It is an additive read model: it introduced no schema change and did not alter existing metric semantics.

## What it reports

Per doctor and per procedure variant (`DoctorProcedureMixRow`):

- `ProcedureCode` (variant-level, for example `EXT` or `EXT+SED`), `BaseProcedureCode`, and `IsSedationCase`.
- `CaseCount` for that variant, the doctor's total completed-case denominator, and that variant's share of the doctor's completed cases.

Grouping is by doctor plus procedure variant, so sedation variants stay separate from their base (`EXT+SED` is its own row, distinct from `EXT`), consistent with sedation being a modifier and not a separate procedure (see [sedation-as-modifier](sedation-as-modifier.md)).

Population: built over `standardCompletedCycles`, the same standard completed-cycle population as the other calculated metrics (see [reporting-population](reporting-population.md)).

## Constraints

- Keep grouping at doctor plus procedure variant; do not collapse sedation into the base in this view.
- Keep it over the standard completed population; do not include incomplete or reporting-exception cycles.
- Present it as descriptive mix, not as a ranking or a target.

## Source anchors

- `src/ChairSide.Board/Services/DemoBoardStore.cs` - `DoctorProcedureMixRow` record (~line 2293); `BuildDoctorProcedureMix` (~1871); wired into the snapshot at `~605`.
- `tests/ChairSide.Board.Tests/BoardStoreTests.cs` - `Doctor_procedure_mix_groups_by_doctor_and_variant_with_shares` (~824), `Doctor_procedure_mix_isolates_rows_and_denominators_per_doctor` (~862), `Doctor_procedure_mix_excludes_incomplete_and_reporting_exception_cycles` (~890), `Doctor_procedure_mix_skips_cycles_with_blank_doctor` (~941).
- `src/ChairSide.Board/wwwroot/board.js` - `renderSelectedDoctorProcedures` (Procedure Mix tab rendering).
- `docs/knowledge-graph/chairside.graph.md` - procedure-mix nodes and reporting-semantics notes.

## Verification notes

Verified at `e2badc2`: the `DoctorProcedureMixRow` record, the `BuildDoctorProcedureMix` builder over `standardCompletedCycles`, the doctor/variant grouping with per-doctor denominators, and the four guarding tests (grouping/shares, per-doctor isolation, exclusion of incomplete and reporting-exception cycles, blank-doctor skip) are present. Line numbers are approximate.

Known limits: like [observed-load](observed-load.md), this is a recent read model whose UI may expand; keep the note focused on intent, grouping, and population rather than presentation.
