# ChairSide durable decisions

This file records decisions that should survive across tasks, PRs, and debugging sessions.

## Knowledge graph scope

**Decision:** The knowledge graph is a private, human-readable development artifact stored under `docs/knowledge-graph/`; generated files are navigation aids and never runtime dependencies.

**Rationale:** Repo-local Markdown and Mermaid are reviewable and help future work recover intent without adding production fragility.

## Non-PHI boundary

**Decision:** ChairSide and its knowledge graph must never include patient identity, clinical notes, appointment data, chart numbers, secrets, or credentials.

**Rationale:** The system tracks rooms and non-PHI operational timing, not patients.

## Prestaging and assignment drafts

**Decision:** Begin Prestage creates the episode without requiring assignment. Prestaging and Seated may durably contain absent, partial, or complete canonical assignment. Save Details is the explicit assignment commit, and assignment-bearing Seat may commit its supplied draft atomically.

**Rationale:** Lifecycle truth and assignment completeness are independent before Ready. The system must not invent clinical defaults or require staff to lie about timing.

**Sedation interaction:** Sedation remains a modifier. For an eligible procedure, unchecked means no sedation and checked means sedation; the room workflow does not require a separate No action. Committing an eligible unchecked draft normalizes it to durable `EligibleNo`, while `EligibleUnresolved` remains compatible with partial or legacy state.

## Add-on case modifier

**Decision:** Add-on is sparse scheduling-context metadata attached to any valid case, not a procedure or lifecycle state. Every new assignment episode defaults to `false`; staff opt in only when an unscheduled case was worked into the day, and no explicit No acknowledgement is required. The modifier remains independent of sedation and procedure changes, remains correctable through Ready, locks after Doctor Arrived, and resets when the room returns to Available. The doctor/procedure/sedation/allocation handoff remains locked at Ready; Add-on is non-dispatch metadata and does not weaken that safety boundary.

**Persistence and reporting:** The flag travels with the canonical assignment read/write shape and survives active-room persistence, restart reconstruction, Ready handoff history, completed cycles, and aborted assignments. A correction while Ready must update the active room and owned Active handoff atomically without changing locked dispatch fields. Add-on cases remain in ordinary reporting by default and are not exceptions solely because they are add-ons.

**Rationale:** Add-ons occur only a few times per month. Requiring a negative response for nearly every routine case would add repetitive noise without a clinical or lifecycle safety benefit; durable opt-in metadata preserves the rare event without burdening normal seating.

## Ready handoff boundary

**Decision:** Ready requires a complete, currently valid assignment and creates an immutable owned Active handoff. The assignment may already be durable or may be persisted atomically by an assignment-bearing canonical Ready request. Ready is the assignment-lock boundary; Doctor Arrived accepts the handoff rather than reconstructing assignment.

**Rationale:** A doctor may act on the handoff as soon as Ready is issued, so silent assignment changes after that point are unsafe.

**Transport responses:** The checked-in room panel sends Ready with an explicit assignment-bearing body and consumes the lifecycle action envelope. It sends Doctor Arrived without a body, consumes the top-level `RoomStatus` success response, and relies on the retained bodyless conflict response contract. Bodyless Doctor Arrived is maintained panel transport, not an older-client-only path. Explicit-body Doctor Arrived remains supported and returns the lifecycle action envelope.

## Ready urgency

**Decision:** `ReadyForDoctor` remains the primary lifecycle state. Aging and Stale are projected `ReadyUrgency` values calculated from the Active handoff's `ReadyAt`.

**Rationale:** Urgency is elapsed-time presentation, not a new lifecycle transition. Withdrawal and Doctor Arrived clear urgency; reissued Ready creates a new handoff and interval.

**Compatibility:** Persisted legacy Aging/Stale rows remain readable and may withdraw only when they own a valid Active handoff. Recovery never fabricates one.

## Cancellation, expiration, and recovery

**Decision:** Pre-arrival cancellation and max-duration expiration create aborted assignment history outside throughput. Pre-arrival after-hours termination remains truthful aborted history and appends an objective system finding that enters Needs Review. Post-arrival expiration creates review-required history without fabricating `DoctorCompleteAt`. Faulted pre-arrival Ready remains visible and safely cancellable without rewriting invalid or unrelated handoffs.

**Rationale:** Recovery must preserve observed facts and reporting populations rather than manufacturing a plausible happy path.

**After-hours retry:** The nightly sweep uses independently retryable per-room transactions. It does not advance the in-memory clinic-day marker until the full pass succeeds. Earlier successful rooms remain durable and Available when a later room fails; failed and later active rooms retry in the same store or after restart.

## Concurrency and durable ordering

**Decision:** Canonical lifecycle persistence compares the complete originally loaded room expectation: room/episode/state identity, assignment and allocation values, both handoff references, and lifecycle timestamps. Zero affected rows returns `stale-write` with no INSERT/UPSERT fallback, reload, retry, event, durable mutation, or live mutation. Ready and Withdraw Ready validate handoff history transactionally. Doctor Arrived uses an immediate SQLite transaction to serialize durable cross-room doctor ownership and commits room, handoff acceptance, and reporting-cycle creation together. Live state and events change only after a successful repository commit.

**Rationale:** A stale context must not regress Ready, replace its locked assignment, clear its handoff link, or orphan the durable handoff. Failure injection proves multi-write transaction rollback and unchanged live memory.

**Compatibility:** General `SaveRoom` and `SaveRooms` UPSERT behavior remains for initialization and unrelated lifecycle paths.

## Canonical lifecycle transport

**Decision:** Canonical Begin Prestage, Save Details, Seat, Ready, Withdraw Ready, and Doctor Arrived use strict JSON contracts, typed mutation outcomes, and stable HTTP error codes. Canonical procedure codes are undecorated; internal `+SED` persistence representation never crosses the canonical transport boundary.

**Rationale:** Explicit wire shapes and typed failures let current and future clients recover without partial mutation or inference from free-form messages. Issue #158 confirmed that no maintained deployed caller uses flat assignment-bearing Seat, `/update-assignment`, or `/assignment`; issue #159 removed those paths and their non-atomic Begin Prestage-to-Seat bridge.

**Compatibility:** Omitted Ready retains its narrow, test-guarded top-level `RoomStatus` response. Bodyless Doctor Arrived remains the checked-in room panel transport, with top-level `RoomStatus` success and the retained room-panel conflict response; explicit-body Doctor Arrived remains supported with canonical lifecycle-envelope responses. This removal does not affect those response branches, legacy persisted Aging/Stale recovery, or legitimate legacy arrived-room completion.

## Room capability authority

**Decision:** `RoomCapabilitiesEvaluator`, projected through `DemoBoardStore.ToRoomStatus`, is authoritative for server-known base lifecycle capabilities. The browser consumes `RoomStatus.Capabilities` and retains only genuinely client-local guards such as unsaved draft completeness and dirtiness. Endpoint and store validation remain final for every mutation.

**Rationale:** One server projection prevents the browser from drifting into a parallel lifecycle matrix while preserving the distinction between durable server state and an unsubmitted browser draft. Capability projection is advisory, not authorization.

## Room panel workflow placement

**Decision:** The Room panel left rail is a normal-flow vertical stack. The dynamic room status and its correction context come first, followed by the existing Primary Workflow controls. Assignment setup stays in the main content area. The workflow panel must move naturally when status or correction content changes height; it does not use absolute positioning, fixed offsets, sticky behavior, or JavaScript height calculations.

**Rationale:** Untrained users need the next lifecycle action visible near the room's current status before they encounter doctor and procedure setup controls. Keeping progression controls separate from correction controls preserves the distinction between moving the room forward and repairing its current state without changing lifecycle semantics.

## Room integrity authority

**Decision:** `DemoBoardStore.DeriveIntegrityFaults` is the production authority for room-integrity projection. It evaluates canonical assignment facts together with repository-backed Active or Accepted handoff context. The context-free `RoomIntegrityFaultEvaluator` was unused by production and was removed.

**Rationale:** Integrity decisions after Ready depend on persisted ownership and handoff history. Production-path tests prove that a partial persisted Ready assignment projects `ReadyAssignmentIncomplete`, blocks Doctor Arrived without mutation, and retains safe cancellation.

## Development seed

**Decision:** Demo data seeds only in Development when all operational tables are empty before configured-room initialization. The seed is persisted, and demo Ready rooms have canonical episodes, assignments, and owned Active handoffs. Fresh Training and Production databases initialize configured rooms as Available.

**Rationale:** A first run should be useful, while restart must restore and never overwrite durable state.

## Maintenance reset

**Decision:** Stress-fixture reset atomically deletes completed cycles, all Active handoffs, and active rooms, then recreates configured Available rooms. Withdrawn, Accepted, and Terminated handoffs and aborted assignments survive.

**Rationale:** The command promises deterministic current fixture state and cannot leave an orphan Active handoff. It does not promise convergence of total preserved history or generated GUID identities.

## Environment and maintenance preflight

**Decision:** ChairSide recognizes exactly Development, Training, and Production. Names compare case-insensitively but may not be null, blank, padded, or unknown. The environment is resolved before application build and service resolution. Every known destructive maintenance command is allowlisted in Development and Training and refused in Production before the application or repository is constructed; unknown future commands default to denied.

**Rationale:** Environment ambiguity and late maintenance authorization can point destructive behavior at the wrong database. One canonical preflight gives normal and maintenance startup the same fail-closed deployment semantics while preserving command-specific confirmation tokens.

**Training posture:** Training does not receive Development demo seeding or the Development HTTP seed endpoint, initializes fresh rooms as Available, disables the Demo Timer, rejects simulated elapsed time, rejects the sample Development admin token when protection is enabled, and emits the same disabled-security warnings as Production. A server-supplied BoardSnapshot flag drives one persistent shared-shell `TRAINING` badge; Development and Production omit it, and the client never infers environment from the hostname.

**Training reset wrapper:** The routine operator wrapper code-owns the canonical Training application, data, backup, app-pool, and child-environment values and accepts no deployment overrides. It validates its plan and published Training configuration, accepts only an explicit Started or Stopped initial pool state, and preserves an already Stopped pool. A Started pool is stopped with bounded polling to exact Stopped state. Only a pool that began Started is restored in `finally`; restoration safely handles Started, Starting, Stopped, and Stopping states with bounded polling to exact Started state. Wait timeouts include the last observed state, operation and restoration failures are preserved separately, and a combined failure reports both causes. The wrapper backs up the stopped-pool SQLite file set, invokes only existing confirmation-gated maintenance commands, and restores the parent environment. The application remains responsible for canonical path and Training marker validation before mutation.

**Database isolation:** Production requires exactly `C:\ChairSide\Data\chairside.db`; Training requires exactly `C:\ChairSide\Training\Data\chairside-training.db` and uses `C:\ChairSide\Training\Logs`. These layout boundaries are code-owned. Deployed paths are fully qualified, case-insensitive, boundary-safe, outside the actual content root and both application roots, separate from the opposite deployment, and free of every existing reparse-point component. A missing canonical parent is created only after pure validation and is then rescanned before SQLite access. Development keeps relative and temporary absolute paths outside protected Production and Training roots.

**Database identity:** Path isolation proves location, while `chairside_deployment_identity` proves persisted role. Production and Training require exactly one immutable, versioned marker with a matching role before ordinary schema mutation, migrations, room initialization, maintenance mutation, or endpoint mapping. Fresh marker and current schema creation commit atomically before WAL is enabled. Development creates and requires no marker but refuses deployed or malformed reserved marker state.

**Pre-go-live data boundary:** All ChairSide data before the approved go-live date is training, testing, demonstration, or stress-fixture data. The beta database may be archived separately, but it is never reused or marked as formal Production. ChairSide has no deployed-database adoption path. Formal Production begins with a genuinely new canonical database, a `Production` / `FreshDatabase` identity, and a new reporting history whose official boundary is the approved go-live date. Existing unmarked deployed databases always fail closed.

## Reporting population semantics

**Decision:** The accepted Ready handoff supplies immutable lifecycle evidence and the initial assignment attribution for an uncorrected encounter. A later explicit historical metadata correction may become the current effective reporting value without rewriting the handoff. Withdrawn handoffs are audit-only, pre-arrival aborts stay outside throughput, and post-arrival expiration appears only in review-required exception populations. Aging/Stale threshold flags derive from the accepted Ready interval.

**Rationale:** Metrics become misleading if draft assignment, withdrawn intent, incomplete cycles, exception cycles, and completed populations are mixed.

## Historical exception and anomaly handling

**Decision:** `docs/design/exception-handling-design.md` is the canonical design authority for historical anomaly review, correction overlays, dispositions, append-only administrative history, Data Quality reconciliation, retention, and administrative concurrency. The encounter is the atomic administrative and analytical container. Lifecycle truth and accepted Ready evidence remain immutable; missing facts are omitted rather than reconstructed.

**Decision:** Only an explicit deterministic system rule or Local Admin Mark for Review action creates an anomaly. Each encounter has one continuous chronological ledger. Needs Review provisionally excludes immediately, Confirmed Exception excludes the whole encounter, and Cleared removes only the administrative gate. Resolved review may be reopened without reopening the live lifecycle. Historical correction is available only inside anomaly review, changes current effective metadata through an overlay, and never edits lifecycle timestamps.

**Decision:** Data Quality derives from the active report population and inherits applicable date, Doctor, Sedation, procedure/drill-down, and other approved analytical filters. Its default review drill-down keeps that scope; exhaustive raw history may broaden deliberately. Disposition, ordinary reporting eligibility, and Reviewed/correction provenance remain separate concepts, so their counts are not blindly additive.

**Decision:** Administrative state and its ledger event commit atomically. Stale administrative writes are rejected. Ledger events are append-only, non-deletable through normal UI, bounded and non-PHI, retained indefinitely by default, and loaded through bounded/paged historical access rather than complete in-memory history.

**Decision:** Canonical Mark, reason refinement, note, Clear, Confirm, and Reopen operations require the caller's expected administrative revision. An absent projection is logical revision 0, the decisive comparison occurs in an immediate SQLite transaction, and a successful operation advances exactly once while preserving review evidence and every correction overlay. Approved system findings are limited to AfterHoursSweep and ExceededMaxActiveDuration and commit with the archive, handoff termination when applicable, and room reset. Reporting-only exception reasons and analytical outliers never create administrative state.

**Decision:** Canonical administrative persistence uses `historical_encounter_admin_state` for the optional current disposition, proven review evidence, current correction overrides, and revision, plus `historical_encounter_ledger` for chronological append-only provenance. Both use the #237 `(SourceType, SourceRecordId)` durable identity directly; repository writes validate the corresponding completed or aborted source row rather than adding a synthetic historical-encounter table. Legacy import starts at revision 0, uses one idempotent `LegacyStateImported` event whose timestamp means import time, preserves only parseable review time and the application-owned Local Admin marker, and maps uncertain exception state to Needs Review.

**Decision:** Historical metadata correction requires Needs Review and reuses the same immediate-transaction administrative CAS. Doctor and procedure targets use the complete governed active and inactive rosters. Correctable source fields must have truthful durable evidence. Each correction normally changes one field and appends one `MetadataCorrected` event. `CorrectProcedureAndSedation` is the only approved atomic field group because sedation eligibility has no coherent one-field transition across eligible and ineligible procedures; both values are explicit, allocation remains separate, and the operation must not become a generic batch editor.

**Decision:** `HistoricalEffectiveEncounter` is the canonical no-ledger-replay projection of original evidence, current overrides, effective doctor/procedure/sedation/Add-on/allocation, correction support, disposition, reason/source, and revision. Accepted Ready or terminal Ready is original assignment authority when truthful; otherwise the typed source contributes only facts it durably stores. Issue #241 remains the sole reporting and Data Quality consumption point, and #242 remains the browser workflow point.

**Rationale:** Normal reports need the best current effective interpretation, while the ledger must explain every change without fabricating lifecycle facts, partially including Confirmed Exceptions, losing prior decisions, or hiding review work outside the active analytical scope.

## Canonical reporting semantics

**Decision:** `docs/design/reporting-design.md` is the canonical reporting design authority for the #211 redesign. It consumes the existing lifecycle and accepted Ready-handoff facts rather than redefining them.

**Decision:** Practice Overview `Completed Cases` preserves the normal completed count and reconciles to included plus reporting-excluded completed cases. Reporting exclusions change analytical calculations, not the historical fact that a case completed. Segmented analytical denominators use the corresponding standard included completed population unless explicitly labeled otherwise.

**Decision:** Prominent operational timing is median-first. Ready Wait is accepted Ready to Doctor Arrived and remains distinct from Seated to Doctor. No-observation populations are never rendered as measured zero values.

**Decision:** An Observed Doctor Day exists only when a doctor has qualifying same-day Ready-anchored observed flow. Observed Clinical Span is first qualifying accepted Ready to last same-day Doctor Complete. Doctor Working elapsed time and concurrency use wall-clock unions of Doctor Arrived to Doctor Complete intervals. Unstructured Time is the remainder of the span with no active Doctor Working interval and must not be framed as idle, available, unused, absent, unscheduled, or recoverable time.

**Decision:** Doctor Trends use an additive doctor-specific weekly contract over one shared Monday-start UTC skeleton for the report response. The skeleton remains within the selected report range, is capped at the trailing 12 intersecting buckets, and exposes clipped effective boundaries for partial edge weeks. An explicit selected end date anchors the window even when the selected start is open. Ranges without an explicit end, including All Time and start-only ranges, anchor to the latest dateable in-scope Doctor Complete observation across the report population; an entirely undated population produces no invented current-week history. Missing periods are null-valued gaps, and #217 trend presentation is descriptive without comparison language.

**Decision:** Procedure Mix exists at Practice and Doctor scope with counts and percentages over the current scoped included completed-case population. Sedation remains a procedure modifier, not an additional timed case.

**Decision:** The reusable report query separates Window, analytical Scope, and Procedure Grouping. Scope is Practice or Doctor plus All, Sedation, or Non-sedation; Procedure Family versus Detailed Variant changes aggregation without changing population membership. Historical doctor IDs remain valid reporting scopes independently of the current active assignment roster.

**Decision:** The general descriptive sample guardrail is Empty at `N = 0`, Limited at `N = 1-4`, and Sufficient at `N >= 5`. A metric with a nonempty population and zero contributors is Unavailable. Comparisons require every compared population to be Sufficient. These rules are not statistical significance and do not replace the separate version-one Calibration Insight evidence rules.

**Decision:** Typical Doctor Time Range is the Type 7 Q25-to-Q75 interval over truthful Doctor Arrived -> Doctor Complete observations in one scoped standard included completed procedure population. It is calculated from underlying cases after scope, Sedation, and Procedure Grouping. Numeric endpoints publish only when the shared Doctor Time sample is Sufficient; Limited samples retain their median and Limited context without range endpoints. The range is descriptive, is not min/max, and is not expected allocation, a target, or Schedule Fit.

**Decision:** Data Quality and its default anomaly-review drill-down inherit the active report's applicable scope. The exhaustive raw-history surface may deliberately broaden investigation afterward. Reversed valid date ranges normalize and return normalized metadata; malformed date input retains graceful legacy behavior without a new HTTP 400 response.

**Decision:** Historical assigned Schedule Fit evaluates the scheduling model with positive finalized `ExpectedAllocationMinutes` and truthful exact Seated -> Doctor Complete seconds from the scoped standard included completed population. Reversed intervals do not contribute. Exact case-level slack and debt remain separate, signed net is observed minus expected and debt minus slack, and population coverage is visible. Practice totals ignore Procedure Grouping. The legacy integer-minute `ScheduleFitReport.Overall` remains a Workshop compatibility contract.

**Decision:** Current-default Calibration is a separate server-owned procedure or doctor x procedure evaluation against the current active base-procedure roster `DefaultExpectedUnits * 10` minutes, never historical assigned or captured defaults. Version one requires N >= 10 current-default pairs; uses all pairs as the raw-sign denominator; requires at least 80 percent AboveBaseline or BelowBaseline; classifies -600 through +600 seconds inclusive as AtExpected; and requires the all-pair median variance to be strictly greater than +600 seconds for More or less than -600 seconds for Less. It evaluates only the selected report population, makes no statistical-significance claim, saves no insight history, and never mutates allocation.

**Decision:** Only a server `Qualified` decision creates a Calibration Insight. Qualified non-PHI evidence includes completed-cycle and optional accepted-handoff identity, the current roster baseline snapshot, exact observed and paired-variance seconds, raw direction, and tolerance class, and reconciles to the decision counts and median population. JavaScript owns neutral formatting and does not reconstruct the rules.

**Decision:** Healthy Data Quality remains quiet; exclusions, limited samples, and pending review use progressive disclosure while audit remains the evidence layer behind calculations and insights. Provider ranking, efficiency scoring, attendance inference, idle-time reporting, grades, quotas, and punitive staff metrics are prohibited.

**Decision:** Issue #220 separates completed-case audit, exact Metric evidence, and anomaly-review evidence. Issue #234 supersedes the permanently read-only reviewed-history model: resolved history retains append-only provenance and may be reopened or receive later corrections/findings. The admin-protected audit query remains the current contributor, exact-second, stable-sort, and paging authority; later administrative mutation contracts must preserve the same encounter identity and never treat `RecentCompletedCycles` as historical authority.

**Decision:** Normal audit preserves `DoctorCompleteAt` window anchoring. Completed exception review instead uses the latest truthful lifecycle timestamp and aborted review uses `TerminatedAt`. Administrative action dates do not change the encounter's reporting period. Data Quality review uses the active analytical scope by default, while exhaustive raw history may broaden deliberately.

## Bounded historical persistence access

**Decision:** Startup and ordinary room operation keep no lifetime completed-cycle cache. Reporting enumerates selected completed, review-completed, and aborted sources in fixed SQLite pages, obtains all-time scoped totals with a database count, and spills replayable populations plus exact ordered statistics to private temporary SQLite storage above a small in-memory threshold. Audit and review retrieval retain only a request-bounded ordered working set and exact match count; label-based review sorts globally merge builder-projected labels across persistence pages. Calibration Evidence replays the complete selected candidate population through the same bounded path before reconciling explicit evidence identities. A historical source record is addressed by source type plus durable source record ID; no synthetic historical encounter table is introduced by this query foundation.

**Rationale:** Indefinite retention must not make startup, live-room integrity, All Time calculation, or paged evidence reads proportional in managed memory to lifetime encounter history. Exact medians, Type 7 ranges, trends, overlap/concurrency, Schedule Fit, and Calibration may replay disk-backed observations; they must not use caps or approximations. Storage anchoring remains distinct: normal reporting uses `DoctorCompleteAt`, completed review uses `DoctorCompleteAt ?? DoctorArrivedAt ?? SeatedAt ?? PrestageStartedAt`, and aborted review uses `TerminatedAt`.

**Decision:** Practice Completed Cases remains normal included plus reporting-excluded completed history. Doctor Completed Cases uses the scoped standard included completed population and its matching sample context.

**Rationale:** The reporting redesign must explain observed operational flow and scheduling-model fit without converting incomplete observation into judgments about doctors or staff. Precise shared semantics prevent later report slices from inventing incompatible denominators, time intervals, or interpretation language.

**Review point:** Reconsider the version-one N=10 and 80 percent operational review thresholds after enough production history exists to evaluate actual procedure and doctor x procedure segment volumes.

## Reporting UI philosophy

**Decision:** Reporting favors summary cards, plain-English interpretation, progressive disclosure, and operational questions over dense rankings or punitive comparisons.

**Decision:** Report exception mutations use visible inline pending, success, failure, and reconciliation feedback. Consequential inclusion-changing and review-finalizing actions retain explicit confirmation. Mutation success and report-refresh success are represented separately, and uncertain outcomes require a fresh read before mutation retry.

**Rationale:** Blocking alerts erase context and cannot distinguish a committed mutation from a failed refresh. Per-record pending guards, inline accessible status, and fresh reconciliation prevent duplicate side effects while preserving server authority.

## Completed follow-up work

- Knowledge-graph comment/string false-positive extraction was corrected by issue #126 / PR #142 (`47da8f9`).
