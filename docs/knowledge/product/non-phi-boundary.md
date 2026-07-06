---
title: Non-PHI boundary
tags: [privacy, non-phi, domain-rule, risk, active, last-verified]
last_verified_commit: e2badc2
---

# Non-PHI boundary

## Intent

ChairSide tracks rooms, not patients. It is an internal room-status and doctor-dispatch board, and it must never store, display, request, import, or infer protected health information (PHI). This boundary is a hard product rule, not a preference: it keeps ChairSide out of PHI-handling scope and lets it run on the internal network without patient-data risk.

Project mantra: "This system does not track patients. It tracks rooms."

## Constraints

Must NOT be present anywhere in the system (storage, UI, API, logs, or seed data):

- Patient names, dates of birth, chart numbers
- Medical histories, diagnoses, treatment notes
- Insurance data, billing data
- Free-text patient notes

May be stored (non-PHI operational metadata only):

- Room number, assigned doctor, procedure category
- Room state, timer values, event timestamps
- Device identity
- Non-PHI operational metrics

Design implications that follow from this rule:

- No free-text patient fields anywhere in the room workflow.
- Conflict prompts and audit entries carry only room number and doctor id/display name, never patient context (see [room-lifecycle](../workflow/room-lifecycle.md), Conflict handling).
- Procedure is stored as a category code (for example `EXT`), not a clinical description.

## Source anchors

- `AGENTS.md` - "Critical privacy rule" (must-not-store / may-store lists) and the "tracks rooms, not patients" mantra.
- `docs/knowledge-graph/decisions.md` - "Non-PHI boundary" decision.
- Guards enforcing safe, non-PHI mutation context: `src/ChairSide.Board/Services/RoomDeviceBindingGuard.cs`, `src/ChairSide.Board/Services/AdminAccessGuard.cs`.

## Verification notes

Verified at `e2badc2`: a repository search across `src/` for PHI-style field names (`PatientName`, `DateOfBirth`, `ChartNumber`, `Diagnosis`) returns zero matches, so the boundary holds in code and is not merely aspirational. The doctor-arrival conflict DTOs (`DoctorArrivalConflict`, `DoctorArrivedConflictResponse`) carry room number and doctor identity only.

Known limits: this is a code-and-policy check, not an audit of any running database or of operator-entered content. The rule is about system design; it does not by itself prevent a future field from being added incorrectly. Treat any PR that adds a patient-identifying field as a boundary violation to reject.
