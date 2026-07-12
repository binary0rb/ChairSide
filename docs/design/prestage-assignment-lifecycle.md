# Prestaging Assignment Lifecycle

## Status and traceability

Status: Approved design record. Implementation has not yet begun under this approved design.

- Governing parent: GitHub issue #106.
- Approved source issues: #107, #108, #109, #110, and #111.
- Synthesis issue: #112.

This document is the canonical synthesis of those approved issue bodies. It defines product rules and acceptance behavior. It does not select a storage schema, request shape, endpoint name, or migration strategy unless an approved issue already requires the behavior.

## Operational problem and approved intent

Begin Prestage starts room setup and patient-retrieval timing when the assistant begins that work. Staff MUST NOT be forced to complete WinOMS encounter details before starting Prestaging. Assignment details MAY be entered later, incrementally, during the normal pre-arrival workflow.

The earlier assignment-first Gate A behavior does not represent this approved workflow. In particular, beginning Prestaging is not contingent on a completed assignment, and ordinary post-Prestage entry is not an exceptional correction operation.

## Primary lifecycle

The primary workflow is:

1. Available
2. Prestaging
3. Seated / In Prep
4. Ready for Doctor
5. Doctor Working
6. Doctor Complete
7. Turnover

`PrestageStartedAt` means room preparation or patient retrieval began. `SeatedAt` means the patient was physically seated and MUST NOT be fabricated. `DoctorArrivedAt`, `DoctorCompleteAt`, and `RoomAvailableAt` retain their truthful physical event meanings.

Available to Prestaging creates the room episode, records `EpisodeId` and `PrestageStartedAt`, and reserves the room. Prestaging to Seated records truthful `SeatedAt`. Ready for Doctor is the formal handoff. Doctor Arrived consumes that existing handoff and advances to Doctor Working. Doctor Complete advances to Doctor Complete, then turnover returns the room to Available.

## Ready urgency model

Ready has one secondary urgency status: None, Aging, or Stale. Aging and Stale are not peer lifecycle states.

- They are valid only while the primary status is Ready for Doctor.
- They are secondary to, and MUST NOT replace, the primary Ready status.
- They clear on Withdraw Ready and Doctor Arrived.
- They MUST NOT coexist with Doctor Working, Doctor Complete, or Turnover.
- The active urgency interval starts at the current successful Ready handoff. Reissuing Ready starts a new active interval; a withdrawn interval is no longer active.

## Assignment completeness model

Prestaging and Seated / In Prep MAY persist no assignment, a partial assignment, or a complete assignment. Ready for Doctor requires a complete assignment.

A complete assignment contains:

- a valid doctor;
- a valid procedure;
- sedation resolved according to procedure eligibility; and
- an expected allocation explicitly confirmed by staff.

Doctor and procedure may be unknown before Ready. Unknown is not a placeholder doctor or default procedure. Expected allocation may be unknown; zero is not a substitute for unknown. A procedure-derived allocation is a suggestion, not confirmation. Staff MUST explicitly confirm the suggestion or replace it with a valid value before Ready.

Sedation semantics are exact:

- With no procedure, sedation is unavailable and disabled.
- With a non-sedation-eligible procedure, sedation is disabled and visibly unavailable; no Yes/No answer is required.
- With an eligible procedure, staff MUST explicitly choose Yes or No. Until then, sedation is unresolved.

Changing procedure before Ready invalidates the prior sedation resolution and expected-allocation confirmation. The new procedure's eligibility and allocation suggestion apply, and the dependent fields MUST be resolved again. Changing only doctor does not invalidate procedure, sedation, or allocation absent a separately approved doctor-specific rule.

## Normal assignment entry

There is no separate normal "Update Assignment" mode. During Prestaging and Seated / In Prep, assignment controls are directly available for ordinary entry.

- Field changes create one local draft.
- Staff MAY change multiple fields before saving.
- Save Details is an optional checkpoint and is available only when the draft differs from persisted values.
- Save Details MAY persist partial or complete details without advancing the room.
- Seat Room MAY atomically save the current absent, partial, or complete draft while recording `SeatedAt`.
- Ready for Doctor atomically validates, saves, advances, creates the handoff, and locks the complete assignment.
- Discard Changes and Escape restore persisted values and send no update.

Ready validation failure leaves the room Seated / In Prep. It MUST create no handoff, change no lifecycle timestamp, and partially persist no draft. The UI identifies unresolved fields and SHOULD focus the first unresolved field where practical.

## Ready handoff and assignment lock

Ready for Doctor is the formal room-to-doctor handoff. On success, the complete assignment is persisted as the active, locked handoff and the active doctor-arrival wait begins.

At Ready, doctor, procedure, sedation, and expected allocation lock. Save Details is unavailable, inline edits are prohibited, and no silent assignment change is permitted while the doctor may be traveling to the room. Doctor Arrived consumes the locked handoff, records arrival, clears urgency, and advances to Doctor Working. It MUST NOT create or redefine the assignment snapshot.

## Withdraw Ready

Withdraw Ready is the explicit pre-arrival correction transition. It is available only before Doctor Arrived and:

- returns the room to Seated / In Prep;
- withdraws the active handoff;
- clears Aging and Stale;
- restores direct assignment editing with the last complete assignment as the editable persisted baseline;
- preserves `EpisodeId`, `PrestageStartedAt`, and truthful `SeatedAt`;
- does not create a completed case or reset room history.

A subsequent Ready action validates the corrected complete assignment and creates a new active handoff and wait interval. The withdrawn handoff remains auditable but is no longer operationally active or eligible for normal completed-case metrics.

## Room-page behavior

Before Ready, the room page uses neutral, operational language. It MUST NOT show placeholder clinical values or error styling merely because details are incomplete.

- Prestaging with no assignment shows the primary Prestaging status, a timer from `PrestageStartedAt`, and `Assignment pending`.
- Partial details use neutral language such as `Doctor pending`, `Procedure pending`, `Sedation choice pending`, and `Suggested allocation - confirm`.
- Seated / In Prep with incomplete details shows `Complete assignment details before Ready for Doctor.` This is guidance, not an error state.
- Direct controls, local draft, Save Details, Discard Changes, and Escape apply only during Prestaging and Seated / In Prep.
- Ready remains selectable while incomplete, but failed validation leaves the room unchanged and identifies unresolved fields.
- Ready shows the complete locked handoff and hides or disables Save Details and Discard Changes.
- Withdraw Ready is visually distinct from ordinary editing and clearly communicates withdrawal of the doctor handoff.

## Master-board behavior

Active Prestaging and Seated / In Prep rooms appear immediately.

An unassigned room shows its truthful primary state, lifecycle timer, and neutral `Assignment pending` presentation. It MUST NOT show a placeholder doctor, fake procedure, doctor color, or warning styling merely because details are incomplete.

After a doctor selection is saved, the room gains that doctor's name and color and MAY appear in the doctor's operational view. The primary workflow status remains dominant. A Ready room displays the locked handoff; Aging or Stale appears beneath Ready as a secondary warning. Withdrawing Ready immediately returns the tile to Seated / In Prep, removes the active Ready presentation and urgency, and may retain the saved doctor association.

## Doctor-view behavior

A room joins a doctor view only after a doctor selection has been saved. A saved doctor does not imply Ready.

During Prestaging and Seated / In Prep, the doctor view MAY show neutral pending detail. When Ready succeeds, it shows the formal locked handoff, room, procedure, and any secondary Aging or Stale warning. Withdraw Ready removes the formal Ready indication immediately and restores Seated / In Prep presentation. Changing doctor before Ready moves the saved queue association without changing `EpisodeId` or lifecycle timestamps.

## Persistence, contracts, and atomicity

The following operations MUST be atomic: Begin Prestage, Save Details, Seat Room with draft persistence, Ready for Doctor with handoff creation, Withdraw Ready, reissued Ready, cancellation, expiration, Doctor Arrived, and ordinary lifecycle completion. Live state changes only after durable persistence succeeds.

Begin Prestage persists only the episode, `PrestageStartedAt`, Prestaging state, and room reservation. It requires and invents no assignment values. Before Ready, durable assignment data may be absent, partial, or complete. A failed Save Details leaves the prior persisted assignment unchanged.

Ready persists a reconstructable, auditable complete handoff assignment: doctor, procedure, resolved sedation semantics, and staff-confirmed allocation. The exact API contract, endpoint names, request shapes, schema, and durable handoff representation remain deferred. They MAY use existing fields, explicit snapshot fields, a related durable record, an accepted-handoff identifier, or another equivalent auditable representation, provided the approved behavior is preserved.

No transition may fabricate an assignment value, handoff, timestamp, episode, or reporting side effect. A failed combined operation leaves persisted state unchanged.

## Cancellation, expiration, recovery, and compatibility

Cancellation is allowed before Doctor Arrived in Prestaging, Seated / In Prep, and Ready, including Aging or Stale. It preserves whatever was actually persisted: none, partial, complete, active handoff, and retained withdrawn-handoff information. After durable termination, the room MAY atomically return to Available and clear its active episode. `SeatedAt` exists only when the patient was physically seated. Aborted episodes remain outside completed-case populations.

Expiration follows the same truthfulness and atomicity rules. Existing expiration thresholds and clinic-local after-hours sweep behavior remain unchanged.

Restart recovery restores the exact persisted state. It MUST NOT inject defaults, convert suggestions into confirmation, unlock Ready, alter a handoff, create a new Ready interval, invent values, or downgrade the room. A Ready room restores its complete locked handoff, active Ready time, derived current urgency, episode identity, and truthful timestamps. A Ready-or-later record with incomplete assignment is a visible integrity fault: preserve it, log it, prevent unsafe progression, surface it to an operator or administrator, and require explicit auditable recovery.

Compatibility and migration details remain deferred. Legacy completed cycles continue using their existing finalized assignment data. They MUST NOT receive fabricated Ready timestamps or be retroactively rewritten as Ready snapshots.

## Reporting semantics

For a completed case, the accepted assignment is the latest successful Ready-for-Doctor handoff that was not withdrawn and subsequently led to Doctor Arrived. Doctor Arrived accepts that existing snapshot.

The accepted snapshot supplies completed-case doctor, procedure, sedation, confirmed allocation, allocation-variance baseline, and schedule-fit classification. Earlier Prestaging or Seated details do not classify a completed case unless they become part of the accepted handoff. Withdrawn handoffs remain durable and auditable but are excluded from accepted Ready-to-arrival metrics, completed-case classification, doctor throughput, procedure mix, sedation totals, allocation variance, and schedule-fit reporting.

- Accepted Ready-to-arrival time is `DoctorArrivedAt - AcceptedReadyAt` and excludes withdrawn intervals.
- Total seated-to-doctor time remains truthful and continuous: `DoctorArrivedAt - SeatedAt`.
- Setup and retrieval contributes only when both `PrestageStartedAt` and `SeatedAt` exist.
- Doctor-working and turnover durations retain their truthful endpoint rules.
- Aborted and expired episodes remain separate from completed-cycle, throughput, allocation, schedule-fit, and trend populations.
- No withdrawal KPI, card, score, threshold, or staff-performance metric is added.
- Legacy cycles without a truthful Ready timestamp remain in their existing completed-case populations but do not contribute to accepted Ready-to-arrival metrics.
- Existing UTC calendar-day filters, Monday-start UTC weeks, completed-cycle windows anchored on `DoctorCompleteAt`, and completed-cycle/exception partition rules remain unchanged.

## Explicit non-goals

This design does not authorize:

- a demo-timer enhancement;
- a general visual redesign;
- favicon work;
- production deployment;
- a withdrawal KPI;
- unrelated backend compatibility cleanup; or
- a reporting-dashboard redesign.

## Acceptance scenarios

1. Begin Prestage with no assignment: staff starts Prestaging, an episode and `PrestageStartedAt` persist, the room appears immediately as `Assignment pending`, and no clinical defaults are inserted.
2. Partial Save Details: during Prestaging or Seated, staff saves only available fields; the room stays in its current primary state and the saved partial values survive refresh.
3. Seat Room with incomplete assignment: staff seats a room with absent or partial details; truthful `SeatedAt` persists and the room becomes Seated / In Prep without a handoff.
4. Failed Ready: incomplete fields prevent Ready, identify unresolved fields, leave the room Seated / In Prep, and persist neither draft changes nor a handoff.
5. Successful Ready and lock: a complete confirmed assignment is saved atomically, Ready is shown, the handoff is locked, and inline editing is unavailable.
6. Aging and Stale: configured thresholds show a secondary warning beneath Ready without replacing the primary status.
7. Withdraw Ready and correction: staff withdraws before Doctor Arrived; urgency clears, Seated / In Prep returns, timestamps and episode persist, and direct editing resumes from the prior complete baseline.
8. Reissued Ready: corrected complete details create a new active handoff and a new urgency interval; the withdrawn interval remains auditable only.
9. Doctor Arrived: from Ready, Doctor Arrived clears urgency, enters Doctor Working, and consumes without redefining the locked handoff.
10. Cancellation with no or partial assignment: cancellation before arrival preserves only real persisted values and does not create a completed case or fabricate `SeatedAt`.
11. Restart recovery: Prestaging, Seated, and Ready restore their exact persisted states; Ready restores its locked complete handoff and active urgency basis.
12. Legacy reporting: legacy completed cycles use existing finalized assignment data, receive no invented Ready time, and remain excluded from accepted Ready-to-arrival when that time is absent.

## Future implementation decomposition

Implementation issues are intentionally not created by this record. Bounded future workstreams are:

1. Contracts and domain model.
2. Persistence and schema strategy.
3. Transactional store transitions.
4. API endpoints and validation responses.
5. Room UI and local draft behavior.
6. Master-board and doctor-view presentation.
7. Recovery and expiration handling.
8. Reporting compatibility and accepted-handoff semantics.
9. Tests and characterization coverage.
10. Browser validation.
11. Superseded Gate A reconciliation.

Each workstream MUST preserve the approved rules above. Implementation details that the issues defer remain decisions for those bounded design and implementation tasks, not assumptions to add here.

## Superseded Gate A assumptions

The current uncommitted Gate A implementation contains assumptions that are no longer approved and MUST be reconciled before implementation under this design:

- full assignment required before Begin Prestage;
- normal post-Prestage entry framed as Update Assignment;
- assignment remaining editable while Ready;
- Doctor Arrived as the assignment-lock boundary; and
- Aging and Stale represented as peer lifecycle states rather than secondary Ready warnings.
