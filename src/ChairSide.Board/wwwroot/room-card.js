import { escapeAttribute, escapeHtml } from "./dom-utils.js";

const stateNames = ["empty", "seated", "aging", "stale", "ready-for-doctor", "doctor-in-room", "turnover"];

export function createRoomCardPresentation({
  getSnapshot,
  getRoomId,
  getNowMs,
  getAgingMinutes,
  getStaleMinutes,
  getDoctorInitials,
  procedure
}) {
  function renderRoomTile(room, large = false) {
    const presentation = roomPresentationState(room);
    const state = presentation.primaryState;
    const roomId = getRoomId(room);
    const display = roomDisplayAssignment(room);
    const doctorColor = display.doctor ? display.doctor.color : "#8b949e";
    const displayedProcedure = display.procedure;
    const badge = renderRoomStatusBadge(presentation);
    const timer = roomTimerLabel(room);
    const fullDoctorName = display.doctor?.name || (state === "empty" ? "No assignment" : "Doctor pending");
    const doctorDisplayName = large
      ? fullDoctorName
      : (display.doctor?.shortName || cardDoctorName(fullDoctorName));
    const coinInitials = display.doctor ? getDoctorInitials(display.doctor.id, fullDoctorName) : "";
    const procedureDisplayCode = display.procedureCode
      ? `${display.procedureCode}${display.sedationState === "EligibleYes" ? "+SED" : ""}`
      : null;
    const procedureLabel = procedureDisplayCode
      ? procedure.formatCode(procedureDisplayCode)
      : state === "empty" ? "OPEN" : "PROCEDURE PENDING";
    const procedureName = displayedProcedure?.label || "";
    const assignmentSummary = roomAssignmentSummary(room, display, state);
    const addOnBadge = display.isAddOn
      ? '<span class="room-case-modifier-badge">ADD-ON</span>'
      : "";

    const accent = display.procedureCode ? procedure.resolveAccent(display.procedureCode) : "";
    const tileStyle = `--doctor-color: ${escapeAttribute(doctorColor)}`
      + (accent ? `; --procedure-accent: ${accent}` : "");

    return `
      <article class="room-tile ${state} ${presentation.readyUrgency ? `urgency-${presentation.readyUrgency}` : ""} ${room.assignmentLocked ? "assignment-locked" : ""} ${large ? "large" : ""}" style="${tileStyle}">
        <div class="room-topline">
          <strong>Room ${roomId}</strong>
          ${badge}
        </div>
        <div class="procedure-lockup${displayedProcedure ? " procedure-lockup--chip" : state === "empty" ? "" : " procedure-lockup--pending"}">
          ${displayedProcedure ? procedure.renderIcon(displayedProcedure) : procedure.renderEmptyIcon()}
          <span>${escapeHtml(procedureLabel)}</span>
          ${procedureName ? `<small class="room-procedure-label">${escapeHtml(procedureName)}</small>` : ""}
        </div>
        ${addOnBadge}
        ${assignmentSummary ? `<small class="room-assignment-summary">${escapeHtml(assignmentSummary)}</small>` : ""}
        <div class="room-footer">
          <span class="room-doctor">
            ${coinInitials ? `<span class="room-doctor-coin" aria-hidden="true">${escapeHtml(coinInitials)}</span>` : ""}
            <span class="room-doctor-name" title="${escapeAttribute(fullDoctorName)}">${escapeHtml(doctorDisplayName)}</span>
          </span>
          <time class="room-timer">
            <span>${timer.label}</span>
            <strong>${timer.value}</strong>
          </time>
        </div>
      </article>
    `;
  }

  function roomPresentationState(room) {
    const normalizedState = normalizeState(room);
    const isReady = normalizedState === "ready-for-doctor"
      || normalizedState === "aging"
      || normalizedState === "stale";
    if (!isReady) {
      return { primaryState: normalizedState, readyUrgency: null };
    }

    const projectedUrgency = String(room?.readyUrgency || "").toLowerCase();
    const readyUrgency = projectedUrgency === "aging" || projectedUrgency === "stale"
      ? projectedUrgency
      : normalizedState === "aging" || normalizedState === "stale"
        ? normalizedState
        : null;
    return { primaryState: "ready-for-doctor", readyUrgency };
  }

  function renderRoomStatusBadge(presentation) {
    if (presentation.primaryState !== "ready-for-doctor") {
      return `<span class="room-state-badge">${stateBadge(presentation.primaryState)}</span>`;
    }

    const timerState = presentation.readyUrgency || "on-time";
    const timerLabel = timerState === "on-time" ? "ON TIME" : timerState.toUpperCase();
    const accessibleTimerLabel = timerState === "on-time" ? "on time" : `${timerState} urgency`;
    return `<span class="ready-status-stack" aria-label="Ready for Doctor, ${accessibleTimerLabel}">
      <span class="ready-primary-badge">READY</span>
      <span class="ready-urgency-badge ready-timer-badge ${timerState}">${timerLabel}</span>
    </span>`;
  }

  function roomAssignedDoctorId(room) {
    return roomDisplayAssignment(room).doctorId;
  }

  function roomDisplayAssignment(room) {
    const assignment = room?.assignment || null;
    if (assignment) {
      const doctorId = assignment.doctorId || null;
      const procedureCode = assignment.procedureCode || null;
      return {
        assignment,
        doctorId,
        doctor: getSnapshot()?.doctors.find(doctor => doctor.id === doctorId) || null,
        procedureCode,
        procedure: procedure.fromCode(procedureCode),
        sedationState: assignment.sedation?.state || null,
        expectedAllocation: assignment.expectedAllocation || null,
        isAddOn: assignment.isAddOn === true
      };
    }

    // Legacy active rows may lack the additive canonical read model. Preserve their truthful saved
    // display fields without treating decorated procedure codes as canonical assignment truth.
    const doctorId = room?.doctor?.id || room?.assignedDoctor || null;
    const procedureCode = procedure.stripSedationModifier(room?.procedureCode) || null;
    return {
      assignment: null,
      doctorId,
      doctor: getSnapshot()?.doctors.find(doctor => doctor.id === doctorId) || room?.doctor || null,
      procedureCode,
      procedure: procedure.fromCode(procedureCode) || room?.procedure || null,
      sedationState: procedure.hasSedationModifier(room?.procedureCode) ? "EligibleYes" : null,
      expectedAllocation: null,
      isAddOn: false
    };
  }

  function roomAssignmentSummary(room, display, state) {
    if (state === "empty") {
      return "";
    }
    if (!display.assignment && !display.doctorId && !display.procedureCode) {
      return "Assignment pending";
    }

    const details = [];
    if (room.assignmentLocked) {
      details.push(state === "ready-for-doctor" ? "Handoff locked" : "Accepted assignment");
    }
    if (!display.doctorId) {
      details.push("Doctor pending");
    }
    if (!display.procedureCode) {
      details.push("Procedure pending");
    } else if (display.assignment) {
      if (display.sedationState === "EligibleYes") {
        details.push("Sedation on");
      } else if (display.sedationState === "EligibleNo") {
        details.push("No sedation");
      } else if (display.sedationState === "EligibleUnresolved") {
        details.push("Sedation pending");
      } else {
        details.push("Sedation unavailable");
      }

      const allocation = display.expectedAllocation;
      const allocationState = allocation?.state || "Unknown";
      const confirmedValue = allocation?.confirmedValue;
      const suggestedValue = allocation?.suggestedValue;
      if (allocationState === "ConfirmedSuggestedValue" || allocationState === "ConfirmedAdjustedValue") {
        details.push(`${confirmedValue} ${confirmedValue === 1 ? "unit" : "units"} confirmed`);
      } else if (allocationState === "Suggested" && suggestedValue !== null && suggestedValue !== undefined) {
        details.push(`Suggested ${suggestedValue} ${suggestedValue === 1 ? "unit" : "units"} - confirm`);
      } else {
        details.push("Allocation pending");
      }
    } else {
      details.push("Legacy assignment");
    }

    return details.join(" | ");
  }

  function cardDoctorName(displayName) {
    const trimmed = String(displayName || "").trim();
    const match = trimmed.match(/^Dr\.\s+(.+)$/i);
    return match ? match[1] : trimmed || "Unassigned";
  }

  function normalizeState(room) {
    if (typeof room.state === "number") {
      return stateNames[room.state] || "seated";
    }

    const raw = String(room.state || (room.seatedAt ? "seated" : "empty")).toLowerCase();
    if (raw === "available") {
      return "empty";
    }

    if (raw === "doctorinroom" || raw === "doctor-in-room") {
      return "doctor-in-room";
    }

    // Legacy persisted aging/stale values remain readable Ready-phase compatibility states.
    // Return them directly; do NOT re-compute from seatedAt (which never escalates).
    if (raw === "aging" || raw === "stale") {
      return raw;
    }

    if (raw === "turnover" || !room.seatedAt || room.clearedAt) {
      return raw;
    }

    if (room.doctorArrivedAt) {
      return "doctor-in-room";
    }

    // Ready for Doctor: client-side aging/stale fallback computed from readyForDoctorAt,
    // so the tile escalates smoothly between server polls.
    if (raw === "readyfordoctor" || raw === "ready-for-doctor") {
      const readyAtMs = room.readyForDoctorAt ? Date.parse(room.readyForDoctorAt) : NaN;
      if (!Number.isNaN(readyAtMs)) {
        const elapsedMinutes = Math.max(0, (getNowMs() - readyAtMs) / 60000);
        const agingMinutes = getAgingMinutes();
        const staleMinutes = getStaleMinutes();
        if (staleMinutes !== null && elapsedMinutes >= staleMinutes) {
          return "stale";
        }
        if (agingMinutes !== null && elapsedMinutes >= agingMinutes) {
          return "aging";
        }
      }
      return "ready-for-doctor";
    }

    // Patient Seated / In Prep: never escalates to aging or stale from seatedAt.
    return "seated";
  }

  function stateBadge(state) {
    if (state === "empty") {
      return "AVAILABLE";
    }

    if (state === "seated") {
      return "IN PREP";
    }

    if (state === "prestaging") {
      return "PRESTAGING";
    }

    if (state === "ready-for-doctor") {
      return "READY";
    }

    if (state === "doctor-in-room") {
      return "IN ROOM";
    }

    if (state === "turnover") {
      return "TURNOVER";
    }

    return state.toUpperCase();
  }

  function roomTimerLabel(room) {
    if (room.seatedAt) {
      return {
        label: "Room time",
        value: formatElapsed(room.seatedAt)
      };
    }

    if (room.prestageStartedAt) {
      return { label: "Prep time", value: formatElapsed(room.prestageStartedAt) };
    }

    return { label: "Available", value: "--:--" };
  }

  function elapsedSeconds(startedAt) {
    return Math.max(0, Math.floor((getNowMs() - Date.parse(startedAt)) / 1000));
  }

  function formatElapsed(startedAt) {
    const totalSeconds = elapsedSeconds(startedAt);
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
  }

  return {
    normalizeState,
    renderRoomTile,
    roomAssignedDoctorId
  };
}
