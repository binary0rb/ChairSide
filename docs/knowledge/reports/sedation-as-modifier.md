---
title: Sedation as a modifier
tags: [reports, procedures, sedation, domain-rule, active, last-verified]
last_verified_commit: 61dac09
---

# Sedation as a modifier

## Intent

Sedation is a modifier of a primary procedure, never a standalone procedure and never a separately timed component. A case is "an extraction with sedation," not "an extraction plus a sedation case." This keeps procedure reporting honest: there is one case, one primary procedure, and a sedation flag - not two overlapping timed things.

## How it is represented

- A sedation case stores a composite procedure code: the base code with a `+SED` suffix (for example `EXT` becomes `EXT+SED`). The suffix constant is `SedationModifierSuffix = "+SED"`.
- `ComposeProcedureCode(baseCode, sedation)` builds the stored code; `HasSedationModifier` / `StripSedationModifier` detect and remove the suffix; `ResolveBaseProcedureCode` maps a stored code back to its base (`EXT+SED` -> `EXT`, a base code is its own base).
- Reporting rows carry both `BaseProcedureCode` and `IsSedationCase`, so a consumer can group by base procedure while still distinguishing the sedation variant.
- A historical standalone `SED` code (`LegacySedationCode`) can exist in old data. It is treated as sedation-related for counts, but it is no longer an active procedure family and is handled as a reporting exception (excluded from standard metrics; see [reporting-population](reporting-population.md)).

## Constraints

- Do not calculate a separate "sedation time" or split a case into procedure time plus sedation time.
- Do not model sedation as its own procedure family in new work; it is a modifier only.
- In the canonical room workflow, an eligible unchecked modifier means no sedation and an eligible checked modifier means sedation. Saving, seating, or issuing Ready normalizes the unchecked eligible state to durable `EligibleNo`; staff do not need a separate No action.
- Keep `EligibleUnresolved` readable for partial or legacy state without making it the normal room-panel interaction.
- Keep sedation variants distinct from their base in variant-level reporting (`EXT` and `EXT+SED` are separate rows), while base-procedure roll-ups fold them together with `IsSedationCase = false` on the roll-up row.

## Source anchors

- `src/ChairSide.Board/Services/DemoBoardStore.cs` - `SedationModifierSuffix`, `HasSedationModifier`, `StripSedationModifier`, `ComposeProcedureCode`, `LegacySedationCode`, `IsSedationProcedureCode`, and `ResolveBaseProcedureCode`; the `ProcedureCycleSummary` and `DoctorProcedureMixRow` records carry `BaseProcedureCode` and `IsSedationCase`.
- `tests/ChairSide.Board.Tests/BoardStoreTests.cs` - `Variant_summaries_carry_base_code_and_sedation_flag` and `Sedation_and_non_sedation_counts_partition_completed_cycles`.
- `AGENTS.md` - "Procedure categories and icons" (sedation as a category with a modifier role).

## Verification notes

Verified at `61dac09`: the `+SED` suffix, the compose/strip/resolve helpers, the legacy `SED` handling, and the `BaseProcedureCode` / `IsSedationCase` fields are all present.

Known limits: the exact list of sedation-eligible procedures and the UI accent behavior for sedation chips are related but out of scope here.
