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

**Decision:** Pre-arrival cancellation and max-duration expiration create aborted assignment history outside throughput. Pre-arrival after-hours termination remains truthful aborted history but carries review metadata and appears in the unified exception queue. Post-arrival expiration creates a review-required exception without fabricating `DoctorCompleteAt`. Faulted pre-arrival Ready remains visible and safely cancellable without rewriting invalid or unrelated handoffs.

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

**Decision:** The accepted Ready handoff supplies finalized assignment attribution. Withdrawn handoffs are audit-only, pre-arrival aborts stay outside throughput, and post-arrival expiration appears only in review-required exception populations. Aging/Stale threshold flags derive from the accepted Ready interval.

**Rationale:** Metrics become misleading if draft assignment, withdrawn intent, incomplete cycles, exception cycles, and completed populations are mixed.

## Canonical reporting semantics

**Decision:** `docs/design/reporting-design.md` is the canonical reporting design authority for the #211 redesign. It consumes the existing lifecycle and accepted Ready-handoff facts rather than redefining them.

**Decision:** Practice Overview `Completed Cases` preserves the normal completed count and reconciles to included plus reporting-excluded completed cases. Reporting exclusions change analytical calculations, not the historical fact that a case completed. Segmented analytical denominators use the corresponding standard included completed population unless explicitly labeled otherwise.

**Decision:** Prominent operational timing is median-first. Ready Wait is accepted Ready to Doctor Arrived and remains distinct from Seated to Doctor. No-observation populations are never rendered as measured zero values.

**Decision:** An Observed Doctor Day exists only when a doctor has qualifying same-day Ready-anchored observed flow. Observed Clinical Span is first qualifying accepted Ready to last same-day Doctor Complete. Doctor Working elapsed time and concurrency use wall-clock unions of Doctor Arrived to Doctor Complete intervals. Unstructured Time is the remainder of the span with no active Doctor Working interval and must not be framed as idle, available, unused, absent, unscheduled, or recoverable time.

**Decision:** Doctor Trends use an additive doctor-specific weekly contract over one shared Monday-start UTC skeleton for the report response. The skeleton remains within the selected report range, is capped at the trailing 12 intersecting buckets, and exposes clipped effective boundaries for partial edge weeks. An explicit selected end date anchors the window even when the selected start is open. Ranges without an explicit end, including All Time and start-only ranges, anchor to the latest dateable in-scope Doctor Complete observation across the report population; an entirely undated population produces no invented current-week history. Missing periods are null-valued gaps, and #217 trend presentation is descriptive without comparison language.

**Decision:** Procedure Mix exists at Practice and Doctor scope with counts and percentages over the current scoped included completed-case population. Sedation remains a procedure modifier, not an additional timed case.

**Decision:** The reusable report query separates Window, analytical Scope, and Procedure Grouping. Scope is Practice or Doctor plus All, Sedation, or Non-sedation; Procedure Family versus Detailed Variant changes aggregation without changing population membership. Historical doctor IDs remain valid reporting scopes independently of the current active assignment roster.

**Decision:** The general descriptive sample guardrail is Empty at `N = 0`, Limited at `N = 1-4`, and Sufficient at `N >= 5`. A metric with a nonempty population and zero contributors is Unavailable. Comparisons require every compared population to be Sufficient. These rules are not statistical significance and do not replace #219 Calibration Insight evidence rules.

**Decision:** The action-required Review Queue remains global within the selected reporting date window, while analytical Case Audit inherits Doctor and Sedation scope. Reversed valid date ranges normalize and return normalized metadata; malformed date input retains graceful legacy behavior without a new HTTP 400 response.

**Decision:** Schedule Fit evaluates the scheduling model. The first-version observed basis remains Seated to Doctor Complete measured case flow, which is not interchangeable with Doctor Time. Slack and debt stay separate, signed net variance remains visible, and population coverage must be exposed. Calibration Insights may surface sufficiently supported directional patterns for human review but never mutate expected allocation automatically.

**Decision:** Healthy Data Quality remains quiet; exclusions, limited samples, and pending review use progressive disclosure while audit remains the evidence layer behind calculations and insights. Provider ranking, efficiency scoring, attendance inference, idle-time reporting, grades, quotas, and punitive staff metrics are prohibited.

**Rationale:** The reporting redesign must explain observed operational flow and scheduling-model fit without converting incomplete observation into judgments about doctors or staff. Precise shared semantics prevent later report slices from inventing incompatible denominators, time intervals, or interpretation language.

**Deferred parameters:** Typical Observed Range quantiles belong to #218; Calibration Insight sample, deviation, directional-consistency, tolerance, and multi-period persistence rules belong to #219.

## Reporting UI philosophy

**Decision:** Reporting favors summary cards, plain-English interpretation, progressive disclosure, and operational questions over dense rankings or punitive comparisons.

**Decision:** Report exception mutations use visible inline pending, success, failure, and reconciliation feedback. Consequential inclusion-changing and review-finalizing actions retain explicit confirmation. Mutation success and report-refresh success are represented separately, and uncertain outcomes require a fresh read before mutation retry.

**Rationale:** Blocking alerts erase context and cannot distinguish a committed mutation from a failed refresh. Per-record pending guards, inline accessible status, and fresh reconciliation prevent duplicate side effects while preserving server authority.

## Completed follow-up work

- Knowledge-graph comment/string false-positive extraction was corrected by issue #126 / PR #142 (`47da8f9`).
