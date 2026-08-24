---
title: Report audit and metric evidence
tags: [reports, reporting-population, reporting-metrics, exception-handling, schedule-fit, permissions, domain-rule, active, last-verified]
last_verified_commit: fe60949
---

# Report audit and metric evidence

## Evidence modes and populations

ChairSide uses one shared presentation but does not treat the evidence modes as interchangeable. A Cleared encounter may appear in ordinary evidence and retain anomaly history at the same time.

- Completed-case audit is normal completed history with Room Available. Practice includes reporting-excluded facts with explicit neutral standing; standard included audit omits them.
- Metric evidence is the exact contributor population for a selected server metric. Phase contributors may lack Room Available and are never described as completed throughput.
- Procedure Intelligence timing evidence begins with its standard included completed row population and then applies the selected metric's truthful interval requirement. Its Ready Wait and Doctor Time drill-down therefore do not reuse the broader generic phase populations, and its Seated to Doctor Complete evidence does not require a positive scheduling allocation.
- Anomaly review contains Needs Review, Confirmed Exception, Cleared, correction, and reviewed provenance for completed and aborted/incomplete historical encounters. One continuous append-only ledger owns each encounter's history. Resolved reviews may be reopened, and later correction or system-finding events append rather than replace prior decisions.

Needs Review and Confirmed Exception encounters never appear in completed-case audit or metric evidence. A Cleared encounter may return to ordinary eligibility under its truthful lifecycle facts; clearing an aborted/incomplete encounter never promotes it to completed throughput.

## Query and scope transfer

The current admin-protected read-only `POST /api/reports/audit/query` accepts the normalized parent window, Practice or Doctor scope, doctor ID, Sedation scope, Procedure Grouping, contributor kind, optional segment doctor and procedure identity, analytical standing, Calibration evidence identities, sort, offset, and limit. Default page size is 50 and the server caps it at 100. Issue #234 is a design gate for later administrative mutation contracts; it does not change this endpoint by itself.

The response returns normalized selection, evidence mode, projected rows, counts, page state, active sort, and supported sorts. JavaScript uses this contract without changing the parent report query or reloading the main report. Practice plus `segmentDoctorId` stays Practice base scope.

## Time and identity authority

Normal audit retains the completed report's `DoctorCompleteAt` window. Review selection is separate: completed exceptions use the latest truthful lifecycle anchor and aborted assignments use `TerminatedAt`.

Audit rows expose exact ordered seconds. Missing or reversed intervals are null, never zero-clamped. Stable encounter, completed-cycle, and optional accepted Ready-handoff identities let every entry point reach the same durable review workflow without PHI. The canonical manual action is Mark for Review. Historical correction changes current effective metadata through an overlay while the accepted Ready handoff and lifecycle evidence remain immutable.

Data Quality and its default review drill-down inherit the active report's applicable date, Doctor, Sedation, procedure/drill-down, and approved analytical filters. The exhaustive raw-history surface may deliberately broaden scope. Needs Review is elevated; Cleared, Confirmed Exception, Historical Correction, and Reviewed counts are not blindly additive because disposition, reporting eligibility, and provenance are separate concepts.

## Compatibility

`RecentCompletedCycles` remains a bounded compatibility projection. The canonical Practice audit, Doctor Case Audit, metric drill-down, historical sort, and Calibration evidence flow do not use it.

## Source and test anchors

- `src/ChairSide.Board/Services/ReportAudit.cs`
- `src/ChairSide.Board/Services/ReportsSnapshotBuilder.Audit.cs`
- `src/ChairSide.Board/wwwroot/report-data.js`
- `src/ChairSide.Board/wwwroot/reports.js`
- `tests/ChairSide.Board.Tests/ReportAuditBuilderTests.cs`
- `tests/javascript/report-data.test.mjs`
- `tests/javascript/reports.test.mjs`
