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

`historical_encounter_admin_state` is the optional current projection. It stores the current disposition, structured reason/source, proven legacy review evidence, nullable effective-value overrides, and a non-negative per-encounter administrative revision. An absent projection has logical revision 0. Legacy import remains revision 0; the first canonical mutation advances to revision 1. A `NoAnomaly` projection is legal when an encounter has an override but no active administrative gate.

`historical_encounter_ledger` stores chronological append-only provenance. Normal repository access can read one bounded encounter ledger or atomically write one projection plus one event; it does not expose independent state mutation or arbitrary ledger update/delete operations. Ordering is `occurred_at`, then durable `ledger_id`.

## Integrity and atomicity

SQLite constraints restrict source type, positive source ID, disposition, actor class, event type, override shapes, revision, and the 500-character administrative note limit. Repository writes also verify that the typed source row exists. SQLite cannot express that polymorphic source relationship as one foreign key, so repository validation is the narrow integrity authority.

Projection mutation and ledger append share one immediate SQLite transaction. Every canonical operation supplies the expected administrative revision, compares it inside that transaction, and advances the revision exactly once. A stale write changes neither projection nor ledger. Competing first writers against an absent projection both expect revision 0, but only one can commit revision 1. A persistence failure rolls both pieces back. Administrative compare-and-swap remains independent of live-room lifecycle compare-and-swap.

## Canonical anomaly operations

The policy service owns Mark for Review, reason refinement, note addition, Clear for Reporting, Confirmed Exception, and Review Reopened transitions. The repository owns typed-source existence, the decisive revision comparison, and atomic persistence. Typed outcomes distinguish success, missing source, stale write, invalid transition, invalid reason, invalid note, and invalid source.

Local Admin reasons use the closed vocabulary `IncorrectDoctor`, `IncorrectProcedure`, `IncorrectCaseDetails`, `UnexpectedLifecycle`, and `OtherNeedsReview`. Notes are stored verbatim up to 500 characters and are rejected at 501 characters. Clear and Confirm may carry an optional note on their single disposition event and revision; standalone `NoteAdded` remains available for an independent note. Actor provenance remains only `System` or `LocalAdmin`; no personal identity is stored.

The admin-protected strict-JSON API is rooted at `/api/reports/anomalies/{sourceType}/{sourceRecordId}`. Its Mark, refine, note, clear, confirm, and reopen operations require `expectedRevision`, return the resulting disposition and revision on success, and map stale or invalid transitions to conflict responses.

System findings are closed to the approved `AfterHoursSweep` and `ExceededMaxActiveDuration` producers. Their source archive, any Ready-handoff termination, live-room reset, current administrative projection, and one `SystemFinding` ledger event commit in the same transaction. A finding creates or returns the encounter to Needs Review; another finding while pending appends another event. Reporting-only `ReportingExceptionReasons`, statistical outliers, and calibration results do not create administrative state.

Issue #239 does not make reports consume the canonical projection or correction overlays. Existing legacy exception columns remain the pre-#241 reporting compatibility source, and the existing legacy review routes remain narrowly available until the later UI migration.

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

Production initialization preserves administrative projection and ledger rows indefinitely. Existing Development and Training maintenance reset deletes completed-source administration before deleting completed cycles, while administration attached to preserved aborted history remains preserved. Issue #239 adds the server API and policy operations only; final review UI migration remains deferred to #242.

## Source and test anchors

- `src/ChairSide.Board/Services/HistoricalAdministrativePersistence.cs`
- `src/ChairSide.Board/Services/HistoricalAnomalyAdministrationService.cs`
- `src/ChairSide.Board/Services/HistoricalAnomalyEndpointHandler.cs`
- `src/ChairSide.Board/Services/SqliteBoardSchema.cs`
- `src/ChairSide.Board/Services/SqliteBoardRepository.cs`
- `tests/ChairSide.Board.Tests/HistoricalAdministrativePersistenceTests.cs`
- `tests/ChairSide.Board.Tests/HistoricalAnomalyAdministrationTests.cs`
- `tests/ChairSide.Board.Tests/HistoricalAnomalyEndpointTests.cs`
- `tests/ChairSide.Board.Tests/HistoricalSystemFindingProducerTests.cs`
