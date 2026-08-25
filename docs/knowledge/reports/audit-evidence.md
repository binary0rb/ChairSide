---
title: Report audit and metric evidence
tags: [reports, reporting-population, reporting-metrics, exception-handling, schedule-fit, permissions, domain-rule, active, last-verified]
last_verified_commit: 1c6a03d
---

# Report audit and metric evidence

## Status boundary

**Current implementation:** Issue #241 integrates current canonical disposition, effective metadata, correction provenance, and reviewed provenance into the existing read-only audit/query contract. Issue #242 still owns final browser mutation and ledger-detail workflow.

## Evidence modes and populations

ChairSide uses one shared presentation but does not merge the underlying evidence populations.

- Completed-case audit is normal completed history with Room Available. Practice includes reporting-excluded facts with explicit neutral standing; standard included audit omits them.
- Metric evidence is the exact contributor population for a selected server metric. Phase contributors may lack Room Available and are never described as completed throughput.
- Procedure Intelligence timing evidence begins with its standard included completed row population and then applies the selected metric's truthful interval requirement. Its Ready Wait and Doctor Time drill-down therefore do not reuse the broader generic phase populations, and its Seated to Doctor Complete evidence does not require a positive scheduling allocation.
- Exception review contains current Needs Review completed and aborted encounters. Reviewed exception history is a separate provenance-based disclosure; a later reopen does not erase the fact that review occurred.

Manual exceptions and aborted assignments never appear in completed-case audit or metric evidence.

## Query and scope transfer

The current admin-protected read-only `POST /api/reports/audit/query` accepts the normalized parent window, Practice or Doctor scope, doctor ID, Sedation scope, Procedure Grouping, contributor kind, optional segment doctor and procedure identity, analytical standing, Calibration evidence identities, sort, offset, and limit. Default page size is 50 and the server caps it at 100. Issue #234 is a design gate for later administrative mutation contracts; it does not change this endpoint by itself.

The response returns normalized selection, evidence mode, projected rows, counts, page state, active sort, and supported sorts. JavaScript uses this contract without changing the parent report query or reloading the main report. Practice plus `segmentDoctorId` stays Practice base scope.

## Time and identity authority

Normal audit retains the completed report's `DoctorCompleteAt` window. Review selection is separate: completed exceptions use the latest truthful lifecycle anchor and aborted assignments use `TerminatedAt`.

Audit rows expose exact ordered seconds. Missing or reversed intervals are null, never zero-clamped. Stable completed-cycle and optional accepted Ready-handoff identities support the current Mark Exception action and Calibration evidence reconciliation without PHI.

## Canonical historical administration

Issue #234 keeps lifecycle and accepted Ready evidence immutable while adding current-effective metadata overlays and one append-only encounter ledger. Mark for Review and Confirmed Exception stay outside normal evidence; Cleared may return to ordinary eligibility while retaining anomaly and correction history; resolved review may be reopened.

Data Quality and its default review drill-down inherit the active report's applicable date, Doctor, Sedation, procedure/drill-down, and approved analytical filters. Exhaustive raw history may deliberately broaden scope. Needs Review is elevated; Cleared, Confirmed Exception, Historical Correction, and Reviewed counts are not blindly additive because disposition, reporting eligibility, and provenance are separate concepts.

## Compatibility

`RecentCompletedCycles` remains a bounded compatibility projection. The canonical Practice audit, Doctor Case Audit, metric drill-down, historical sort, and Calibration evidence flow do not use it.

Historical audit and review retrieval is storage-bounded. SQLite applies effective doctor, procedure, explicit sedation, disposition, and report/review window predicates before fixed-size pagination, then retrieves selected typed completed or aborted sources in batches. Set-based joins supply valid Ready evidence and current administrative state; indexed `EXISTS` checks supply correction and reviewed provenance without replaying one ledger per encounter. Doctor and Procedure review sorts scan fixed pages and globally order builder-projected current labels, so stale persisted display text cannot change audit order. Calibration Evidence replays the complete selected candidate population through fixed pages and disk-backed calculation spools before reconciling explicit evidence identities, preserving server qualification without a wholesale memory load. The completed review anchor is `DoctorCompleteAt ?? DoctorArrivedAt ?? SeatedAt ?? PrestageStartedAt`; aborted review remains anchored by `TerminatedAt`.

## Source and test anchors

- `src/ChairSide.Board/Services/ReportAudit.cs`
- `src/ChairSide.Board/Services/HistoricalReportingProjection.cs`
- `src/ChairSide.Board/Services/HistoricalEncounterQuery.cs`
- `src/ChairSide.Board/Services/SqliteBoardRepository.cs`
- `src/ChairSide.Board/Services/ReportsSnapshotBuilder.Audit.cs`
- `src/ChairSide.Board/wwwroot/report-data.js`
- `src/ChairSide.Board/wwwroot/reports.js`
- `tests/ChairSide.Board.Tests/ReportAuditBuilderTests.cs`
- `tests/ChairSide.Board.Tests/HistoricalReportingIntegrationTests.cs`
- `tests/ChairSide.Board.Tests/HistoricalQueryPersistenceTests.cs`
- `tests/javascript/report-data.test.mjs`
- `tests/javascript/reports.test.mjs`
