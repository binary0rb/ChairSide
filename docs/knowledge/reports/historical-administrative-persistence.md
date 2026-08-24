---
title: Historical administrative persistence
tags: [data-persistence, reports, exception-handling, non-phi, design-decision, active]
---

# Historical administrative persistence

## Durable identity and tables

Canonical historical administration attaches directly to the #237 typed durable key:

- `CompletedCycle` plus `completed_room_cycles.id`; or
- `AbortedAssignment` plus `aborted_room_assignments.id`.

The pair is the identity. Equal numeric IDs across the two source tables remain distinct. There is no synthetic `historical_encounters` table.

`historical_encounter_admin_state` is the optional current projection. It stores the current disposition, structured reason/source, proven legacy review evidence, nullable effective-value overrides, and a non-negative per-encounter administrative revision. The initial revision is 0. A `NoAnomaly` projection is legal when an encounter has an override but no active administrative gate.

`historical_encounter_ledger` stores chronological append-only provenance. Normal repository access can read one bounded encounter ledger or atomically write one projection plus one event; it does not expose independent state mutation or arbitrary ledger update/delete operations. Ordering is `occurred_at`, then durable `ledger_id`.

## Integrity and atomicity

SQLite constraints restrict source type, positive source ID, disposition, actor class, event type, override shapes, revision, and the 500-character administrative note limit. Repository writes also verify that the typed source row exists. SQLite cannot express that polymorphic source relationship as one foreign key, so repository validation is the narrow integrity authority.

Projection mutation and ledger append share one SQLite transaction. A persistence failure rolls both pieces back. Full expected-revision stale-write policy remains deferred to #239 and is independent of live-room lifecycle compare-and-swap.

## Correction override foundation

Nullable current overrides cover doctor ID, procedure code, canonical sedation state, Add-on, and canonical confirmed expected-allocation units. Null means no override. The accepted Ready handoff, historical source assignment, and lifecycle timestamps remain unchanged. Reporting does not consume these overrides until later #236 work.

Historical doctor and procedure override values are not constrained to currently active roster entries. Later correction validation must use the known historical roster and must not accept free-text identities.

## Legacy migration

Only `is_exception = true` source rows acquire imported administrative state:

- `requires_review = true` becomes Needs Review;
- an internally consistent reviewed row with the application-owned `local-admin` marker and a parseable `reviewed_at` becomes Confirmed Exception;
- contradictory, incomplete, malformed, or otherwise uncertain exception state becomes Needs Review.

An ordinary `is_exception = false` row receives no projection merely because legacy review columns contain defaults.

Legacy import is set-based and transactional. Projection insert conflicts preserve existing newer state. A partial unique index allows exactly one `LegacyStateImported` event per typed encounter while allowing repeated future notes, findings, corrections, and reopenings. Repeated initialization does not change revision 0, rewrite the first import timestamp, overwrite overrides, or add provenance.

`LegacyStateImported.occurred_at` means the old state was imported at that time. It is not an original flag or detection time. A parseable legacy `reviewed_at` remains separate evidence, and only the proven application-owned Local Admin marker is normalized. Arbitrary legacy reviewer text is not converted into a person or actor identity.

## Retention and reset

Production initialization preserves administrative projection and ledger rows indefinitely. Existing Development and Training maintenance reset deletes completed-source administration before deleting completed cycles, while administration attached to preserved aborted history remains preserved. No browser/API administrative workflow changes are part of this storage foundation.

## Source and test anchors

- `src/ChairSide.Board/Services/HistoricalAdministrativePersistence.cs`
- `src/ChairSide.Board/Services/SqliteBoardSchema.cs`
- `src/ChairSide.Board/Services/SqliteBoardRepository.cs`
- `tests/ChairSide.Board.Tests/HistoricalAdministrativePersistenceTests.cs`
