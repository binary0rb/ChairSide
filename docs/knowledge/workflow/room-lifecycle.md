---
title: Room lifecycle
tags: [room, board, room-lifecycle, permissions, device-binding, domain-rule, active, last-verified]
last_verified_commit: e2badc2
---

# Room lifecycle

## Intent

The room lifecycle is the spine of ChairSide. Every room moves through an ordered sequence of states driven by explicit staff actions from the room-local tablet/panel. Reporting timings and board urgency cues are all derived from this sequence, so its order and the events that advance it are load-bearing and should not be reordered or shortcut casually.

## Lifecycle events and states

Happy-path sequence (event -> resulting state):

1. Seat Room -> IN PREP (patient preparing, not yet ready)
2. Ready for Doctor -> READY (starts the ready-to-doctor wait window; can escalate to AGING, then STALE)
3. Doctor Arrived -> IN ROOM
4. Doctor Complete -> TURNOVER
5. Room Available -> AVAILABLE

Before Doctor Arrived, staff may correct a seating with Update Assignment or Cancel Seating. AGING and STALE are time-threshold escalations of the READY wait, not separate staff actions.

## Mutation surface and authorization

Room lifecycle mutation happens from the room-local tablet/panel only. Doctors can view room status from a phone or workstation but cannot acknowledge or clear a room remotely. Write actions are authorized per room by a device/room binding token (see [non-phi-boundary](../product/non-phi-boundary.md) for why prompts and audits stay non-PHI).

The store methods that implement the lifecycle (all on `DemoBoardStore`):

- `SeatRoom`, `UpdateAssignment`, `CancelSeating`
- `MarkReadyForDoctor`, `MarkDoctorArrived`, `MarkDoctorComplete`, `MarkRoomAvailable`

## Conflict handling

Verified as implemented at `e2badc2`. Doctor Arrived is guarded because one doctor cannot be physically in two rooms at once:

- `TryMarkDoctorArrived` refuses the mutation and returns a `Conflict` outcome if the room's assigned doctor is already marked doctor-in-room in another room. The conflict context is non-PHI: conflicting room number plus doctor id/display name (`DoctorArrivalConflict`). The API returns HTTP 409 so the UI can prompt.
- `ResolveDoctorArrivalConflict` revalidates the conflict against current server state first (the client-supplied conflicting room id is not trusted). If the conflict is gone or now points at a different room, nothing is mutated and a `StaleConflict` outcome is returned so the caller refreshes and retries.
- On a valid resolve, the previous conflicting room is auto-completed with `MarkDoctorComplete` (which moves it to TURNOVER, never directly to AVAILABLE), then the current room is marked Doctor Arrived. Audit entries are written for both affected rooms.

## Source anchors

- `AGENTS.md` - "Core workflow", "Rooms", "Visual language" (room-state palette).
- `src/ChairSide.Board/Services/DemoBoardStore.cs` - lifecycle methods `SeatRoom` (~line 154), `UpdateAssignment` (~195), `CancelSeating` (~230), `MarkReadyForDoctor` (~255), `MarkDoctorArrived` (~281), `MarkDoctorComplete` (~455), `MarkRoomAvailable` (~481); conflict path `TryMarkDoctorArrived` (~296), `FindActiveDoctorRoom` (~444), `ResolveDoctorArrivalConflict` (~332); DTOs `DoctorArrivalOutcome`, `DoctorArrivalConflict`, `DoctorArrivalResult`.
- `src/ChairSide.Board/Program.cs` - endpoint `/api/rooms/{roomNumber}/doctor-arrived/resolve-conflict` and `DoctorArrivalConflictEndpointHandler.ResolveAsync`; dual audit entries `doctor-arrived-resolve-autocomplete` and `doctor-arrived-resolve`.
- `src/ChairSide.Board/Services/RoomDeviceBindingGuard.cs` - room-scoped write authorization.
- `docs/knowledge-graph/chairside.graph.md` - LifecycleEvent and WorkflowState nodes.

## Verification notes

Verified at `e2badc2`: all seven lifecycle method signatures exist as listed; the conflict-detection and conflict-resolution methods, their outcomes, the "TURNOVER not AVAILABLE" auto-complete behavior, and the both-rooms audit writes are present in source. Line numbers are approximate and may drift; prefer the method/record names as anchors.

Known limits: this note describes intended behavior and the room-local mutation model. It does not enumerate every validation branch (for example NotConfigured / no-room outcomes) or the exact device-token handshake, which live in the guard and endpoint handlers.
