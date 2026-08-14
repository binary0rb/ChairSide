---
title: Report audit and metric evidence
tags: [reports, reporting-population, reporting-metrics, exception-handling, schedule-fit, permissions, domain-rule, active, last-verified]
last_verified_commit: fe60949
---

# Report audit and metric evidence

## Three separate populations

ChairSide uses one shared presentation but does not merge the underlying evidence populations.

- Completed-case audit is normal completed history with Room Available. Practice includes reporting-excluded facts with explicit neutral standing; standard included audit omits them.
- Metric evidence is the exact contributor population for a selected server metric. Phase contributors may lack Room Available and are never described as completed throughput.
- Procedure Intelligence timing evidence begins with its standard included completed row population and then applies the selected metric's truthful interval requirement. Its Ready Wait and Doctor Time drill-down therefore do not reuse the broader generic phase populations, and its Seated to Doctor Complete evidence does not require a positive scheduling allocation.
- Exception review contains pending completed and aborted exceptions. Reviewed exception history is a separate read-only disclosure with no actions.

Manual exceptions and aborted assignments never appear in completed-case audit or metric evidence.

## Query and scope transfer

The admin-protected read-only `POST /api/reports/audit/query` accepts the normalized parent window, Practice or Doctor scope, doctor ID, Sedation scope, Procedure Grouping, contributor kind, optional segment doctor and procedure identity, analytical standing, Calibration evidence identities, sort, offset, and limit. Default page size is 50 and the server caps it at 100.

The response returns normalized selection, evidence mode, projected rows, counts, page state, active sort, and supported sorts. JavaScript uses this contract without changing the parent report query or reloading the main report. Practice plus `segmentDoctorId` stays Practice base scope.

## Time and identity authority

Normal audit retains the completed report's `DoctorCompleteAt` window. Review selection is separate: completed exceptions use the latest truthful lifecycle anchor and aborted assignments use `TerminatedAt`.

Audit rows expose exact ordered seconds. Missing or reversed intervals are null, never zero-clamped. Stable completed-cycle and optional accepted Ready-handoff identities support Mark Exception and Calibration evidence reconciliation without PHI.

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
