---
title: Doctor View operational header
tags: [ui-cohesion, doctors, room, design-decision, active, last-verified]
last_verified_commit: pending-issue-122
---

# Doctor View operational header

## Intent

Doctor View is live operational awareness first, doctor-specific reporting second. Its layout must keep current room status visible above the expandable reporting detail, so that opening or growing a report tab can never push live room status below the fold. The reporting detail is secondary context; the rooms a doctor is (or should be) walking into are primary.

This rule exists because a prior review found that expanded reporting tabs could push the live room cards far below the visible area, which defeats the point of a read-only live status view.

## Layout rule

- The page has an upper operational header in normal document flow (not a viewport-pinned overlay).
- Operational header, upper-left: the current room status frame.
- Operational header, upper-right: the compact reporting snapshot (the selected-doctor summary and reporting range).
- Reporting detail tabs (Overview, Trends, Procedures, Flow Breakdown, Case Audit) live in a region below the operational header and may grow downward freely.
- Expanding a reporting detail tab must not push current room status below itself. The reporting snapshot stays compact and does not grow vertically just because a detail tab is expanded.
- On narrow/mobile widths it is acceptable for the header to stack: current room status first, reporting snapshot second, details below.

## Current-room frame posture

The current-room frame shows up to four visible active room cards with a stable posture per active-room count:

- 1 room fills the frame.
- 2 rooms split the frame.
- 3 rooms use a 2x2 posture with the fourth quadrant intentionally left empty and quiet.
- 4 rooms fill the 2x2 posture.
- More than four active rooms keep the two-column posture and flow into further rows; nothing operationally critical is hidden.

The empty fourth quadrant in the 3-room state is reserved layout capacity - quiet whitespace, not an empty-room card and not an error state. The frame is a normal-flow region, not a fixed-height scroll box, so 1 to 3 room cases stay fully visible and readable at 100% desktop zoom.

## Room counting: assignment-based, not state-filtered

The current-room frame's per-doctor room count is assignment-based, not state-filtered. A room counts toward a doctor's frame whenever it is assigned to that doctor, in any non-`AVAILABLE` state - including `IN ROOM` and `TURNOVER`, not just the pre-arrival primary states (`PRESTAGING`, `IN PREP`, and `READY`). Aging and Stale are secondary Ready urgency projections, not new primary states. `AVAILABLE` rooms never count, but only because room reset clears the assigned doctor (the room's assignment becomes null), not because of the `AVAILABLE` state itself.

This means a doctor who still has an assigned `IN ROOM` or `TURNOVER` room, in addition to newly-seated rooms, shows a higher current-room count than the pre-arrival room count alone would suggest. Deterministic Doctor View fixtures that need an exact posture count (1, 3, 4, or 5+ rooms) keep every counted room in `Seated` or canonical `ReadyForDoctor`; threshold-relative Ready times exercise Aging/Stale urgency without persisting those labels as primary states. See `docs/knowledge/tests/deterministic-stress-fixtures.md`.

Doctor View membership uses the shared canonical-first room assignment projection. When `room.assignment` is present, its `doctorId` is authoritative; stale decorated procedure codes or legacy display fields cannot override it. Pre-canonical rows without the additive assignment read model retain a read-only legacy fallback. The filter has no state predicate.

## Canonical room-card presentation

- Prestaging and Seated / In Prep remain distinct primary states.
- Ready for Doctor stays the primary status while Aging or Stale appears beneath it as a secondary urgency badge.
- Withdrawal returns the card to Seated / In Prep and removes Ready urgency. Doctor Arrived advances to In Room and also removes Ready urgency.
- Doctor, procedure, sedation, and expected allocation come from the canonical room assignment read model when available.
- Missing or partial assignment values use neutral pending language. Cards do not fabricate doctor, procedure, sedation, or allocation defaults.
- Ready cards identify the locked handoff; later states retain the accepted assignment presentation.

## Scope

This is a UI and operational-awareness rule only. It does not change report semantics, the reporting population, room lifecycle behavior, device/room binding, the non-PHI boundary, doctor/procedure/sedation meanings, or report calculations. It reuses the existing room tile rendering (including the doctor coin) and the existing selected-doctor report panel; it only reorders and frames those existing sections.

## Source anchors

- `src/ChairSide.Board/wwwroot/doctor.html` - the operational header markup: `.doctor-operational-header` containing `.doctor-current-rooms-frame` (upper-left) and the reporting snapshot (`#doctorCockpit` / `.doctor-report-snapshot`, upper-right), with `.doctor-report-details` (the `#selectedDoctorPanel` tabs) below.
- `src/ChairSide.Board/wwwroot/board.js` - `renderDoctorView`, `roomAssignedDoctorId`, `roomDisplayAssignment`, `roomPresentationState`, `renderRoomTile`, and `setDoctorRoomCount` (sets the `room-count-{0..4}` class that drives the adaptive posture; count capped at 4).
- `src/ChairSide.Board/wwwroot/styles.css` - the `body[data-view="doctor"] .doctor-operational-header` / `.doctor-current-rooms-frame` / adaptive `.doctor-list.room-count-*` rules, and the `[hidden]` overrides that keep the snapshot and detail panel reliably hidden when there is no report access.
- Related UI intent: `docs/ui-cohesion-audit.md` (reuse-first, protected operational clarity).

## Verification notes

Verified during issue #122 implementation against the working-tree layout: the current-room frame remains above the reporting detail; the reporting snapshot remains right-aligned and compact; canonical assignment controls Doctor View membership and card details; Ready urgency is subordinate; and the adaptive room-count posture remains structurally unchanged. Focused browser smoke covers the rendered master and Doctor View states; full browser acceptance remains issue #125.

Known limits: this note documents the layout rule and intent, not every responsive breakpoint or the exact flex-basis values, which live in the CSS. The greater-than-four-rooms case is deliberately conservative and not heavily designed.

