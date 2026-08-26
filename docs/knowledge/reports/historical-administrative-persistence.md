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

Issue #241 makes current canonical disposition the administrative reporting gate. Needs Review and Confirmed Exception exclude the whole encounter immediately; Cleared removes only that gate and does not manufacture completion, timing, Ready, or allocation facts. Legacy exception columns remain immutable migration and source evidence rather than a competing reporting authority. Existing legacy review routes remain narrowly available until the #242 UI migration.

Canonical reporting tests establish review state through `MarkForReview`, `ClearForReporting`, `ConfirmException`, `ReopenReview`, or an explicit canonical persistence projection. Legacy-column-only fixtures are reserved for migration, initialization/import compatibility, and preservation of truthful legacy evidence. Issue #241 intentionally retires globally unscoped Review Queue expectations: Data Quality and its default review drill-down inherit the current date, effective Doctor, explicit effective Sedation, and applicable procedure scope.

## Canonical corrections and effective projection

Nullable current overrides cover doctor ID, procedure code, canonical sedation state, Add-on, and canonical confirmed expected-allocation units. Null means no override. The accepted Ready handoff, historical source assignment, and lifecycle timestamps remain unchanged. Reporting consumes the resulting effective values only on detached analytical and audit rows.

Issue #240 adds `HistoricalMetadataCorrectionService` as the policy and projection owner. Corrections require Needs Review, use the caller's expected revision, and reuse the #239 immediate SQLite compare-and-swap seam. Success preserves disposition, reason/source, known review evidence, and unrelated overrides; updates only the approved override field or bounded field pair; appends one `MetadataCorrected` event; and advances the revision once. Stale or failed writes change neither projection nor ledger.

Historical doctor and procedure targets resolve against all governed roster entries, including inactive entries, rather than only today's active assignment lists. Arbitrary free-text identities are rejected. Field support derives from truthful durable evidence: accepted Ready for canonical completed history, terminal Ready for Ready-stage aborted history, and otherwise only canonical facts actually present on the completed or aborted source. Missing evidence is never manufactured.

One correction normally changes one field. `CorrectProcedureAndSedation` is the sole bounded atomic group because a move between sedation-eligible and sedation-ineligible procedures cannot pass through a coherent one-field intermediate state. Both values are required explicitly. The operation changes no allocation value and is not a generic batch-edit facility.

Ledger `StructuredReason` identifies `Doctor`, `Procedure`, `ProcedureAndSedation`, `Sedation`, `AddOn`, or `ExpectedAllocation`. Previous and new values are prior and resulting effective values. Procedure/sedation pairs and complete confirmed allocation values use compact deterministic JSON; scalar fields use canonical IDs, enum names, or lowercase booleans. Correcting back to original evidence clears the corresponding redundant override while preserving ledger provenance.

`HistoricalEffectiveEncounter` combines one typed source and its current administrative projection without ledger replay. It exposes original evidence authority and metadata, current effective metadata, explicit overrides and indicators, source-field support, disposition, current reason/source, and revision. The protected detail API and mutation responses use that contract.

Issue #241 adds an internal `HistoricalReportingProjection` at the persistence/reporting seam. Fixed-size completed and aborted pages join the typed source to a valid accepted or terminal Ready handoff and the current administrative projection. Indexed `EXISTS` checks provide durable `MetadataCorrected` and reviewed provenance without loading lifetime ledger history. Doctor, procedure, explicit sedation, Add-on, and confirmed allocation are applied before scope, reporting-exception annotation, grouping, audit projection, occupied-wait attribution, Schedule Fit, and Data Quality reconciliation. Database-side all-time counts and paged audit predicates use the same effective expressions and disposition gate.

Canonical Ready-backed or corrected sedation uses explicit effective `SedationState`. A procedure suffix never overrides that state. Only a legacy completed source without truthful Ready or explicit correction evidence may retain `+SED` or legacy `SED` transport classification in ordinary normal reporting. Scoped anomaly and Data Quality membership requires explicit effective sedation; unknown sedation matches neither Sedation nor NonSedation.

Historical assigned Schedule Fit uses the current effective explicit historical allocation. Current-default Calibration remains a separate comparison against today's active base-procedure roster default. Correction provenance is ledger existence, so it survives a correction back to immutable original evidence even when current override columns return to null. Browser correction workflow remains deferred to #242.

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
- `src/ChairSide.Board/Services/HistoricalMetadataCorrectionService.cs`
- `src/ChairSide.Board/Services/HistoricalMetadataCorrectionEndpointHandler.cs`
- `src/ChairSide.Board/Services/HistoricalReportingProjection.cs`
- `src/ChairSide.Board/Services/ReportsSnapshotBuilder.cs`
- `src/ChairSide.Board/Services/ReportsSnapshotBuilder.Audit.cs`
- `src/ChairSide.Board/Services/SqliteBoardSchema.cs`
- `src/ChairSide.Board/Services/SqliteBoardRepository.cs`
- `tests/ChairSide.Board.Tests/HistoricalAdministrativePersistenceTests.cs`
- `tests/ChairSide.Board.Tests/HistoricalAnomalyAdministrationTests.cs`
- `tests/ChairSide.Board.Tests/HistoricalAnomalyEndpointTests.cs`
- `tests/ChairSide.Board.Tests/HistoricalSystemFindingProducerTests.cs`
- `tests/ChairSide.Board.Tests/HistoricalMetadataCorrectionTests.cs`
- `tests/ChairSide.Board.Tests/HistoricalMetadataCorrectionEndpointTests.cs`
- `tests/ChairSide.Board.Tests/HistoricalReportingIntegrationTests.cs`
- `tests/ChairSide.Board.Tests/HistoricalQueryPersistenceTests.cs`
