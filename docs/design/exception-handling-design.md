# ChairSide Canonical Exception and Anomaly Handling Design

## Status and authority

This document is the canonical design authority for historical anomaly review, administrative correction, Confirmed Exceptions, clearing for reporting, administrative ledger history, Data Quality reconciliation, and related retention and concurrency semantics approved under issue #234.

It defines the intended meaning that later persistence, API, UI, reporting, migration, and test work must implement. It does not change production behavior by itself.

Issue #238 implements the durable storage foundation: one optional current administrative projection and one append-only ledger keyed by the typed durable historical source identity. It truthfully imports legacy exception/review state without implementing the canonical review operations, reporting consumption, or browser workflow deferred to later children of #236.

Lifecycle truth remains owned by `docs/design/prestage-assignment-lifecycle.md`. Reporting metrics and ordinary metric-specific eligibility remain owned by `docs/design/reporting-design.md`. This document adds the historical administrative interpretation layer over those durable facts. When another document describes accepted Ready attribution as permanently final, the narrower rule here supersedes that description only for an explicit historical metadata correction. The accepted Ready handoff itself remains immutable lifecycle evidence.

## Core model

- The encounter is the atomic administrative and analytical container.
- Preserve lifecycle truth. Never fabricate, infer, reconstruct, or backfill lifecycle events to make history appear complete.
- Prefer omission over reconstruction when lifecycle facts are missing or uncertain.
- Historical anomaly review is an administrative interpretation layer over durable history. It does not rewrite lifecycle truth.
- Normal reports show the best current effective interpretation of what happened. The administrative ledger explains how that interpretation changed.
- Review and correction history is append-only. Prior administrative events are never silently rewritten or deleted.

## Three-layer truth model

Keep three layers explicit and separate:

1. **Immutable lifecycle truth** is the accepted Ready handoff, lifecycle event stream, and truthful timestamps. It is never edited by historical review.
2. **Current effective encounter metadata** is the doctor, procedure, sedation, Add-on, allocation, and other approved metadata after applying the latest correction overlays.
3. **Administrative and reporting interpretation** is the anomaly source, review provenance, current disposition, and resulting administrative reporting gate.

Normal reporting uses current effective metadata only when the administrative gate and ordinary metric eligibility allow it. The ledger connects the layers without collapsing or rewriting them.

## Canonical vocabulary

- **Anomaly**: a candidate or observed abnormal condition that warrants review, not a final judgment.
- **Needs Review**: an unresolved anomaly. The encounter is provisionally excluded from normal analytics immediately.
- **Confirmed Exception**: a reviewed encounter judged to be a valid exception. The entire encounter remains excluded from the normal analytical population.
- **Cleared for Reporting**, compact label **Cleared**: review found that the encounter passes the anomaly gate. Administrative exclusion is removed and the encounter returns to ordinary reporting eligibility.
- **Reviewed**: durable provenance that review occurred, regardless of the current disposition.

Clearing never means deletion, erasure, lifecycle reconstruction, or automatic inclusion in every metric.

## Anomaly creation

An anomaly may be created only by:

1. An explicit deterministic ChairSide system rule that objectively knows the condition occurred, such as `AfterHoursSweep` or a deliberately designed integrity-recovery safeguard.
2. A human action by a Local Admin using Mark for Review.

ChairSide must not create a generalized anomaly finder. Unusual Ready Wait, Doctor Time, Turnover, allocation variance, Add-on status, statistical outliers, or derived reporting/Data Quality reasons do not automatically become administrative anomalies unless a future approved design explicitly promotes them.

A system finding records the exact original rule as immutable evidence. Later review may change the disposition, but it does not rewrite which rule produced the finding.

## Manual Mark for Review

The canonical action name is **Mark for Review**, replacing the older conceptual name **Mark Exception**.

Manual Mark for Review applies only to a durable historical encounter, whether completed or aborted/incomplete. It does not apply to an active live room. Active encounters continue to use normal live-workflow correction rules.

Mark for Review belongs to the encounter as a whole, not to an individual lifecycle event or transaction.

The action requires one small structured reason:

- Incorrect Doctor;
- Incorrect Procedure;
- Incorrect Case Details;
- Unexpected Lifecycle;
- Other / Needs Review.

An optional bounded admin note may accompany the reason and must remain strictly non-PHI. The initial reason may be refined later, but the original reason remains in ledger history.

Mark for Review atomically enters Needs Review and provisionally excludes the encounter from normal analytics.

## One continuous ledger per encounter

Each encounter has one continuous chronological administrative/anomaly ledger. ChairSide does not create parallel anomaly cases, correction subtasks, or nested correction workflows.

The ledger appends events such as:

- manual flag;
- system finding;
- reason refinement;
- metadata correction;
- note;
- clear;
- confirm exception;
- reopen;
- later correction or finding.

Multiple corrections may occur during one pending review. A new system finding while review is pending appends to the same ledger. A new objective system finding after resolution appends to that ledger and reopens the encounter into Needs Review.

## Historical metadata correction

Historical metadata correction is permitted only through anomaly review. Correctable fields include ordinary historical case metadata that may have been entered incorrectly:

- doctor;
- procedure;
- sedation status;
- Add-on status;
- expected allocation;
- equivalent future metadata explicitly approved for historical correction.

Historical lifecycle timestamps are never editable.

Corrections are effective-value overlays. They do not rewrite the accepted Ready handoff, lifecycle event stream, or other immutable lifecycle evidence. Each correction records the previous value and new value in the ledger. The current effective value drives ordinary reports and Data Quality scope. Superseded and original values remain available only in audit/history detail.

Corrected metadata must remain internally coherent under the historical meaning of ChairSide assignment rules. A corrected historical doctor or procedure is not mechanically required to remain active in today's roster.

Correction and disposition are separate actions. A correction does not automatically Clear the encounter or Confirm it as an Exception.

## Accepted Ready evidence and effective attribution

For an uncorrected canonical encounter, the accepted Ready handoff remains the immutable source of doctor, procedure, sedation, confirmed allocation, and applicable Add-on reporting attribution under the lifecycle design.

A later explicit historical metadata correction may become the current effective reporting value for the corrected field. This overlay does not mutate, replace, or reinterpret the accepted Ready handoff. Reports use the effective value; audit/history preserves both the accepted Ready evidence and every administrative change that produced the effective value.

Ready timestamps and other lifecycle timestamps are never fabricated or corrected through this layer. Metrics that require missing truthful lifecycle timestamps continue to omit the encounter or observation under their ordinary metric-specific eligibility rules.

## Pending review and dispositions

The same two final dispositions - Cleared for Reporting and Confirmed Exception - apply whether the anomaly originated from manual Mark for Review or an objective system finding.

### Needs Review

- Needs Review is immediately and provisionally excluded from normal analytics.
- The encounter remains fully visible through raw history, exception-review/audit history, Data Quality, and review surfaces.
- It does not need to remain inside the normal completed-case audit population.

### Confirmed Exception

- Confirmed Exception is an encounter-level, whole-record exclusion from normal analytics.
- No field or metric from a Confirmed Exception is intentionally factored back into normal analytics.
- The underlying encounter and full ledger remain inspectable.

### Cleared for Reporting

- Cleared removes only the administrative anomaly gate.
- Ordinary reporting population, completion, timestamp-ordering, and metric-specific eligibility rules still apply.
- Clearing an aborted or incomplete encounter never promotes it into completed throughput and never manufactures missing facts.
- A normal non-exception encounter may still omit an individual metric when its truthful required timestamp is absent. That is ordinary metric-specific eligibility, not partial inclusion of a Confirmed Exception.

## Reopen Review

A resolved review may be explicitly Reopened. Reopen Review affects administrative review only; it never reopens the live room lifecycle.

Reopen appends a new event, preserves every prior decision and correction, returns the encounter to Needs Review, and immediately provisionally excludes it again. Prior Reviewed provenance remains historical context while Needs Review becomes the current actionable status.

## Notes, actors, and minimum ledger fields

Admin notes are optional, bounded, and strictly non-PHI. ChairSide must not request or suggest patient names, chart identifiers, diagnoses, treatment narratives, or other patient information. Disposition notes are optional for both Cleared and Confirmed Exception.

The actor class is currently only:

- `System`;
- `Local Admin`.

ChairSide has no user accounts, so the design must not imply a personal reviewer identity.

As applicable, every ledger event records:

- encounter identifier;
- event type;
- occurred-at timestamp;
- actor class;
- structured reason;
- previous value;
- new value;
- optional admin note.

The ledger is not a general clickstream and does not require giant serialized snapshots.

## Atomicity and concurrency

- An administrative state change and its corresponding ledger event commit atomically.
- Effective administrative state and provenance must not diverge.
- Stale administrative writes are rejected rather than silently overwriting a newer correction or disposition.
- After a stale result, refresh-and-retry is preferred to silent overwrite.

These rules apply independently of canonical live-room lifecycle compare-and-swap. Administrative review never mutates the live lifecycle or its immutable evidence.

## Reporting period and effective truth

- A Cleared encounter returns to the reporting period dictated by its truthful lifecycle/reporting date, not the later administrative action date.
- Historical reports may legitimately change when an older encounter is corrected or Cleared.
- ChairSide reporting is a live analytical view, not a period-close accounting system.
- Ordinary reports do not need prominent historical-adjustment banners. Provenance remains available in Data Quality and audit/history.

Successful administrative actions affect the live analytical view immediately:

- Mark for Review and Reopen Review immediately enter Needs Review and exclude the encounter provisionally.
- Metadata corrections save immediately as current effective values, while the encounter remains excluded pending an explicit disposition.
- Cleared immediately removes the administrative gate and restores ordinary reporting eligibility.
- Confirmed Exception remains excluded as a whole encounter.

There is no separate publish, apply, period-close, or nightly-processing step for these effects.

## Data Quality scope and reconciliation

Data Quality is derivative of the active report population, not an independently scoped second report. It inherits all applicable active report filters, including:

- date range;
- doctor scope;
- sedation scope;
- procedure filter or drill-down identity;
- other approved analytical filters.

Procedure Family versus Detailed Variant remains grouping and presentation context; it does not itself change population membership.

A Data Quality review drill-down inherits the same scope by default. The exhaustive raw-history surface may deliberately broaden the investigation afterward. This supersedes the older rule that the action-required Review Queue always ignores Doctor and Sedation analytical scope.

Summary-first Data Quality may show counts such as:

- Needs Review;
- Cleared Anomalies;
- Confirmed Exceptions;
- Historical Corrections.

Needs Review is the strongest action-required state. Healthy Data Quality may remain visually quiet.

Presentation preserves the provenance hierarchy:

- Needs Review is prominent and action-required.
- Reviewed is quiet durable provenance, not a current warning by itself.
- Cleared does not retain warning treatment after the administrative gate is removed.
- Confirmed Exception may use stronger excluded treatment in audit/history while remaining fully inspectable.

Current anomaly disposition, reporting eligibility, and review provenance are separate concepts. A Cleared encounter may already be back in the normal eligible population, so Cleared, Reviewed, and Historical Correction counts are not blindly additive reconciliation buckets.

A non-normal encounter appears in a filtered Data Quality population only when its current effective recorded facts satisfy that filter. Missing doctor, procedure, or sedation membership is never inferred.

## Temporal navigation

Encounter/date-of-service is the primary search anchor, not the date on which an admin later handled the anomaly.

Preserve the existing truthful exception-review anchors:

- completed exception: `DoctorCompleteAt ?? DoctorArrivedAt ?? SeatedAt ?? PrestageStartedAt`;
- aborted assignment: `TerminatedAt`.

Review uses the same whole-UTC-day report-window semantics. These anchors do not redefine `DoctorCompleteAt` as the canonical window authority for normal completed-cycle reporting.

## Audit and raw-history experience

Encounter review detail must be self-contained enough for an admin to decide in one place. It includes:

- durable encounter identity and current effective metadata;
- anomaly source and structured reason;
- the truthful lifecycle timeline;
- triggering rule or other system evidence;
- current reporting and Data Quality exclusions;
- permitted metadata corrections;
- bounded non-PHI note entry and chronological review history;
- Clear, Confirm Exception, and applicable Reopen controls.

Deeper transaction-level and raw evidence remains available on demand without making it a prerequisite for ordinary review.

- Preserve the raw-data/audit drawer as the exhaustive historical investigation surface.
- Do not create a separate anomaly-management application.
- The default anomaly/review view is Needs Review, and unresolved Needs Review encounters are elevated or pinned.
- Historical status filters include Needs Review, Confirmed Exceptions, Cleared, and All Anomalies.
- Selecting an anomalous encounter opens its existing encounter detail/history context with review controls integrated.
- Every entry point targets the same durable encounter identity and the same review workflow.
- Marking from detail permits direct continuation into review without making the admin find the record again.
- The initial design has no bulk Clear or Confirm action.
- Efficient Resolve -> Next Needs Review navigation is desirable.

## Visibility, deletion, retention, and scale

- Anomaly status never hides or removes the underlying historical encounter.
- Pending and Confirmed Exception records remain fully inspectable.
- No anomaly, correction, review, or ledger event is deletable through normal ChairSide UI. Mistakes are corrected by appending later events.
- Exceptional manual database maintenance may remain possible outside normal application workflow for true corruption, but it is not a user-facing feature.
- Historical encounters and their administrative ledgers are retained indefinitely by default.
- Routine age-based deletion is not introduced for responsiveness.
- Historical and reporting views remain bounded, indexed, date-scoped, and/or paginated.
- The application does not load complete historical encounter or ledger history into memory. Detailed ledger history should be loaded only for the encounter under inspection where practical.
- Existing guarded Development, Training, test-fixture, and environment-reset contracts may continue clearing synthetic or resettable data.

## Migration

Existing exception and review state is migrated truthfully rather than discarded. Preserve what is actually known from fields such as:

- exception flag;
- requires-review state;
- reason;
- review status;
- reviewed timestamp;
- reviewer class or stored value.

Migration must not invent:

- a FlaggedAt or detected timestamp that was never stored;
- personal reviewer identity;
- event ordering that cannot be established;
- reconstructed lifecycle facts.

When exact event history cannot be recovered, create the minimum truthful legacy representation that preserves current meaning.

## Passive diagnostic value

Keep structured source, reason, detected/event time, disposition, and correction data so future analysis can identify patterns such as:

- excessive Cleared `AfterHoursSweep` anomalies;
- repeated integrity-recovery findings;
- repeated correction of the same metadata field;
- anomaly spikes after releases;
- Cleared versus Confirmed Exception patterns.

This design does not require a new anomaly analytics dashboard.

## Implementation boundaries

This design gate requires no production UI, API, persistence/schema, reporting-calculation, migration, or deployment change. Later implementation work must derive behavior and tests from this document without weakening lifecycle truth, partializing Confirmed Exceptions, restoring a globally unscoped Review Queue, or making resolved history permanently read-only.
