import {
  wireTileGroup,
  wireTilePressCleanup
} from "./common-interactions.js";
import { escapeAttribute, escapeHtml, setDisabled, setHidden } from "./dom-utils.js";
import { mutationHeaders, readErrorMessage } from "./request-utils.js";

const EXPECTED_UNITS_MIN = 1;
const EXPECTED_UNITS_MAX = 24;
const MINUTES_PER_UNIT = 10;

export function createRoomWorkflow({
  getSnapshot,
  getRoomNumber,
  getRoomId,
  normalizeState,
  isTilePressActive,
  isDemoTimerEnabled,
  applyRoomMutation,
  showRoomTokenPrompt,
  procedure
}) {
  const draft = {
    selectedDoctorId: null,
    selectedProcedureId: null,
    sedationOn: false,
    expectedUnits: null,
    expectedUnitsManual: false,
    expectedUnitsConfirmed: false,
    expectedUnitsProcedureCode: null,
    expectedUnitsSedation: false,
    persistedAssignmentSignature: "",
    selectionContext: null
  };

  function getCurrentRoom() {
    const snapshot = getSnapshot();
    const roomNumber = getRoomNumber();
    return snapshot?.rooms.find(room => getRoomId(room) === roomNumber) || null;
  }

  function isConfiguredRoom(roomNumber) {
    return Boolean(getSnapshot()?.rooms.some(room => getRoomId(room) === roomNumber));
  }

  function roomCapabilities(room) {
    return {
      canBeginPrestage: room?.capabilities?.canBeginPrestage === true,
      canEditAssignment: room?.capabilities?.canEditAssignment === true,
      canSaveDetails: room?.capabilities?.canSaveDetails === true,
      canSeat: room?.capabilities?.canSeat === true,
      canCancelPrestage: room?.capabilities?.canCancelPrestage === true,
      canCancelSeating: room?.capabilities?.canCancelSeating === true,
      canReady: room?.capabilities?.canReady === true,
      canWithdrawReady: room?.capabilities?.canWithdrawReady === true,
      canDoctorArrive: room?.capabilities?.canDoctorArrive === true,
      canDoctorComplete: room?.capabilities?.canDoctorComplete === true,
      canRoomAvailable: room?.capabilities?.canRoomAvailable === true
    };
  }

  function canEditAssignment(room) {
    return roomCapabilities(room).canEditAssignment;
  }

  function setRoomControlsEnabled(room) {
    const capabilities = roomCapabilities(room);
    const isDirty = capabilities.canEditAssignment && isAssignmentDraftDirty(room);
    const canCancel = capabilities.canCancelPrestage || capabilities.canCancelSeating;

    setDisabled(
      document.getElementById("demoElapsedSelect"),
      !capabilities.canBeginPrestage || !isDemoTimerEnabled());
    setDisabled(document.getElementById("beginPrestageButton"), !capabilities.canBeginPrestage);
    setDisabled(document.getElementById("seatButton"), !capabilities.canSeat);
    setDisabled(document.getElementById("readyForDoctorButton"), !capabilities.canReady);
    setDisabled(document.getElementById("saveDetailsButton"), !capabilities.canSaveDetails || !isDirty);
    setDisabled(document.getElementById("discardChangesButton"), !isDirty);
    setDisabled(document.getElementById("withdrawReadyButton"), !capabilities.canWithdrawReady);
    setDisabled(document.getElementById("cancelSeatingButton"), !canCancel);
    setDisabled(document.getElementById("doctorArrivedButton"), !capabilities.canDoctorArrive);
    setDisabled(document.getElementById("doctorCompleteButton"), !capabilities.canDoctorComplete);
    setDisabled(document.getElementById("roomAvailableButton"), !capabilities.canRoomAvailable);
    setHidden(document.getElementById("saveDetailsButton"), !isDirty);
    setHidden(document.getElementById("discardChangesButton"), !isDirty);
    setHidden(document.getElementById("withdrawReadyButton"), !capabilities.canWithdrawReady);
    setHidden(document.getElementById("cancelSeatingButton"), !canCancel);
    setNextPrimaryAction(room);
  }

  function setNextPrimaryAction(room) {
    const capabilities = roomCapabilities(room);
    const assignmentDraft = draftAssignmentShape();
    const draftCanBecomeReady = Boolean(
      assignmentDraft.doctorId
      && assignmentDraft.procedureCode
      && assignmentDraft.confirmedValue !== null);
    let nextActionId = null;

    if (capabilities.canBeginPrestage) {
      nextActionId = "beginPrestageButton";
    } else if (capabilities.canSeat) {
      nextActionId = "seatButton";
    } else if (capabilities.canReady && draftCanBecomeReady) {
      nextActionId = "readyForDoctorButton";
    } else if (capabilities.canDoctorArrive) {
      nextActionId = "doctorArrivedButton";
    } else if (capabilities.canDoctorComplete) {
      nextActionId = "doctorCompleteButton";
    } else if (capabilities.canRoomAvailable) {
      nextActionId = "roomAvailableButton";
    }

    [
      "beginPrestageButton",
      "seatButton",
      "readyForDoctorButton",
      "doctorArrivedButton",
      "doctorCompleteButton",
      "roomAvailableButton"
    ].forEach(id => {
      document.getElementById(id)?.classList.toggle("is-next-action", id === nextActionId);
    });
  }

  function syncRoomSelection(room) {
    const assignment = canonicalAssignmentFromRoom(room);
    const signature = canonicalAssignmentSignature(assignment);
    const context = room
      ? `${getRoomId(room)}:${room.episodeId || room.prestageStartedAt || "available"}:${signature}`
      : `invalid:${getRoomNumber()}`;
    if (draft.selectionContext === context) {
      return;
    }

    draft.selectionContext = context;
    draft.persistedAssignmentSignature = signature;
    draft.selectedDoctorId = assignment?.doctorId || null;
    draft.selectedProcedureId = assignment?.procedureCode || null;
    draft.sedationOn = assignment?.sedation?.state === "EligibleYes";
    const allocation = assignment?.expectedAllocation;
    draft.expectedUnits = allocation?.confirmedValue ?? allocation?.suggestedValue ?? null;
    draft.expectedUnitsConfirmed = allocation?.state === "ConfirmedSuggestedValue"
      || allocation?.state === "ConfirmedAdjustedValue";
    draft.expectedUnitsManual = allocation?.state === "ConfirmedAdjustedValue";
    draft.expectedUnitsProcedureCode = draft.selectedProcedureId;
    draft.expectedUnitsSedation = draft.sedationOn;
  }

  function canonicalAssignmentFromRoom(room) {
    if (room?.assignment) {
      return room.assignment;
    }
    if (!room?.assignedDoctor && !room?.procedureCode) {
      return null;
    }

    // Pre-canonical active rows can carry truthful display fields without the durable state needed
    // for a canonical handoff. Restore only those known values for read-only presentation; never
    // invent allocation confirmation or treat the row as editable/Ready-capable.
    const procedureCode = procedure.stripSedationModifier(room?.procedureCode) || null;
    const selectedProcedure = procedure.fromCode(procedureCode);
    const sedationState = !selectedProcedure
      ? "UnavailableNoProcedure"
      : selectedProcedure.sedationEligible
        ? (procedure.hasSedationModifier(room?.procedureCode) ? "EligibleYes" : "EligibleUnresolved")
        : "UnavailableProcedureIneligible";
    return {
      doctorId: room?.assignedDoctor || null,
      procedureCode,
      sedation: { state: sedationState },
      expectedAllocation: { state: "Unknown", suggestedValue: null, confirmedValue: null },
      completeness: "Partial"
    };
  }

  function canonicalAssignmentSignature(assignment) {
    if (!assignment) {
      return "unavailable";
    }

    return JSON.stringify({
      doctorId: assignment.doctorId || null,
      procedureCode: assignment.procedureCode || null,
      sedationState: assignment.sedation?.state || null,
      allocationState: assignment.expectedAllocation?.state || null,
      suggestedValue: assignment.expectedAllocation?.suggestedValue ?? null,
      confirmedValue: assignment.expectedAllocation?.confirmedValue ?? null
    });
  }

  function draftAssignmentShape() {
    const selectedProcedure = procedure.fromCode(draft.selectedProcedureId);
    const procedureCode = selectedProcedure?.code || null;
    const suggestedValue = procedureCode ? selectedProcedureDefaultUnits() : null;
    const confirmedValue = procedureCode && draft.expectedUnitsConfirmed
      ? clampExpectedUnits(draft.expectedUnits ?? suggestedValue)
      : null;
    let sedationState = "UnavailableNoProcedure";
    if (selectedProcedure) {
      sedationState = selectedProcedure.sedationEligible
        ? (draft.sedationOn ? "EligibleYes" : "EligibleNo")
        : "UnavailableProcedureIneligible";
    }
    let allocationState = "Unknown";
    if (procedureCode) {
      allocationState = confirmedValue === null
        ? "Suggested"
        : confirmedValue === suggestedValue
          ? "ConfirmedSuggestedValue"
          : "ConfirmedAdjustedValue";
    }

    return {
      doctorId: draft.selectedDoctorId || null,
      procedureCode,
      sedationState,
      allocationState,
      suggestedValue,
      confirmedValue
    };
  }

  function draftAssignmentSignature() {
    return JSON.stringify(draftAssignmentShape());
  }

  function isAssignmentDraftDirty(room = getCurrentRoom()) {
    return canEditAssignment(room)
      && draftAssignmentSignature() !== draft.persistedAssignmentSignature;
  }

  function discardAssignmentDraft() {
    draft.selectionContext = null;
    render(getCurrentRoom());
  }

  function canonicalAssignmentRequest() {
    const selectedProcedure = procedure.fromCode(draft.selectedProcedureId);
    return {
      doctorId: draft.selectedDoctorId || null,
      procedureCode: selectedProcedure?.code || null,
      sedationChoice: selectedProcedure?.sedationEligible && draft.sedationOn ? "yes" : null,
      confirmedExpectedAllocationUnits: selectedProcedure && draft.expectedUnitsConfirmed
        ? clampExpectedUnits(draft.expectedUnits ?? selectedProcedureDefaultUnits())
        : null
    };
  }

  function focusFirstUnresolvedAssignmentControl() {
    const assignmentDraft = draftAssignmentShape();
    const target = !assignmentDraft.doctorId
      ? document.querySelector("#doctorTiles [data-doctor-id]:not(:disabled)")
      : !assignmentDraft.procedureCode
        ? document.querySelector("#procedureTiles [data-procedure-id]:not(:disabled)")
        : assignmentDraft.confirmedValue === null
          ? document.getElementById("allocationConfirm")
          : null;

    target?.focus();
  }

  function renderAssignmentGuidance(room) {
    const target = document.getElementById("assignmentGuidance");
    if (!target || !room) {
      return;
    }
    const faults = room.integrityFaults || [];
    if (faults.length) {
      target.textContent = "Assignment integrity requires review before this room can progress.";
      target.dataset.tone = "error";
      return;
    }
    const state = normalizeState(room);
    if (isLegacyActiveRoom(room)) {
      target.textContent = "This pre-canonical room remains visible but cannot issue a canonical handoff. Cancel seating, then begin a new Prestaging episode.";
      target.dataset.tone = "error";
      return;
    }
    if (state === "empty") {
      target.textContent = "Begin Prestage to start room preparation. Assignment details can follow.";
      target.dataset.tone = "neutral";
      return;
    }
    const capabilities = roomCapabilities(room);
    if (room.assignmentLocked
      || capabilities.canWithdrawReady
      || state === "doctor-in-room"
      || state === "turnover") {
      target.textContent = capabilities.canWithdrawReady
        ? "Assignment locked for the active Ready handoff. Withdraw Ready to make a correction."
        : "Assignment locked.";
      target.dataset.tone = "neutral";
      return;
    }
    const assignmentDraft = draftAssignmentShape();
    if (!assignmentDraft.doctorId && !assignmentDraft.procedureCode) {
      target.textContent = state === "seated"
        ? "Assignment pending. Complete details before Ready for Doctor."
        : "Assignment pending. Details may be added now or after seating.";
      target.dataset.tone = "neutral";
      return;
    }
    const unresolved = [];
    if (!assignmentDraft.doctorId) unresolved.push("doctor");
    if (!assignmentDraft.procedureCode) unresolved.push("procedure");
    if (assignmentDraft.procedureCode && assignmentDraft.confirmedValue === null) {
      unresolved.push("allocation confirmation");
    }
    if (unresolved.length) {
      target.textContent = `Details pending: ${unresolved.join(", ")}.`;
      target.dataset.tone = "neutral";
      return;
    }
    target.textContent = isAssignmentDraftDirty(room)
      ? "Details ready. Save them now or continue with the next lifecycle action."
      : "Assignment details complete.";
    target.dataset.tone = "success";
  }

  function renderSelectionTiles(room) {
    renderDoctorTiles(room);
    renderProcedureTiles(room);
    renderSedationToggle(room);
    renderAllocationSelector(room);
  }

  function clampExpectedUnits(units) {
    const value = Math.round(Number(units));
    if (!Number.isFinite(value)) {
      return EXPECTED_UNITS_MIN;
    }

    return Math.min(EXPECTED_UNITS_MAX, Math.max(EXPECTED_UNITS_MIN, value));
  }

  function isVariableProcedure(selectedProcedure) {
    return String(selectedProcedure?.allocationBehavior || "").toLowerCase() === "variable";
  }

  function selectedProcedureDefaultUnits() {
    const selectedProcedure = procedure.fromCode(draft.selectedProcedureId);
    const raw = Number(selectedProcedure?.defaultExpectedUnits);
    return clampExpectedUnits(Number.isFinite(raw) && raw > 0 ? raw : EXPECTED_UNITS_MIN);
  }

  function syncExpectedUnits() {
    const procedureCode = draft.selectedProcedureId || null;
    if (!procedureCode) {
      draft.expectedUnits = null;
      draft.expectedUnitsManual = false;
      draft.expectedUnitsProcedureCode = null;
      draft.expectedUnitsSedation = false;
      return;
    }

    const procedureChanged = procedureCode !== draft.expectedUnitsProcedureCode;
    const sedationChanged = draft.sedationOn !== draft.expectedUnitsSedation;

    if (draft.expectedUnits === null || procedureChanged) {
      draft.expectedUnits = selectedProcedureDefaultUnits();
      draft.expectedUnitsManual = false;
      if (procedureChanged) {
        draft.expectedUnitsConfirmed = false;
      }
    } else if (sedationChanged && !draft.expectedUnitsManual) {
      draft.expectedUnits = selectedProcedureDefaultUnits();
    }

    draft.expectedUnitsProcedureCode = procedureCode;
    draft.expectedUnitsSedation = draft.sedationOn;
  }

  function renderAllocationSelector(room) {
    const section = document.getElementById("allocationSection");
    if (!section) {
      return;
    }

    syncExpectedUnits();

    const editing = canEditAssignment(room);
    if (editing && !draft.selectedProcedureId) {
      section.hidden = true;
      section.classList.remove("allocation-variable");
      return;
    }
    let units;
    if (editing) {
      units = draft.expectedUnits ?? selectedProcedureDefaultUnits();
    } else {
      const persisted = Number(room?.assignment?.expectedAllocation?.confirmedValue
        ?? room?.assignment?.expectedAllocation?.suggestedValue);
      units = Number.isFinite(persisted) && persisted > 0 ? persisted : null;
    }

    if (units === null) {
      section.hidden = true;
      section.classList.remove("allocation-variable");
      return;
    }

    section.hidden = false;
    const minutes = units * MINUTES_PER_UNIT;

    const unitsEl = document.getElementById("allocationUnits");
    const minutesEl = document.getElementById("allocationMinutes");
    const hintEl = document.getElementById("allocationHint");
    const minusBtn = document.getElementById("allocationMinus");
    const plusBtn = document.getElementById("allocationPlus");

    if (unitsEl) {
      unitsEl.textContent = `${units} ${units === 1 ? "unit" : "units"}`;
    }

    if (minutesEl) {
      minutesEl.textContent = `${minutes} min`;
    }

    const variable = isVariableProcedure(procedure.fromCode(draft.selectedProcedureId));
    section.classList.toggle("allocation-variable", editing && variable);

    if (hintEl) {
      hintEl.textContent = !editing
        ? "Confirmed for the locked assignment."
        : draft.expectedUnitsConfirmed
          ? "Allocation confirmed. Adjusting the units keeps the new value confirmed."
          : variable
            ? `Suggested: ${units} ${units === 1 ? "unit" : "units"}. Confirm or adjust.`
            : `Suggested: ${units} ${units === 1 ? "unit" : "units"}. Confirm to continue.`;
    }

    if (minusBtn) {
      minusBtn.disabled = !editing || units <= EXPECTED_UNITS_MIN;
    }

    if (plusBtn) {
      plusBtn.disabled = !editing || units >= EXPECTED_UNITS_MAX;
    }

    const confirmButton = document.getElementById("allocationConfirm");
    if (confirmButton) {
      confirmButton.hidden = !editing || draft.expectedUnitsConfirmed;
      confirmButton.disabled = !editing || draft.expectedUnitsConfirmed;
    }
  }

  function adjustExpectedUnits(delta) {
    if (!canEditAssignment(getCurrentRoom())) {
      return;
    }

    const base = draft.expectedUnits ?? selectedProcedureDefaultUnits();
    const next = clampExpectedUnits(base + delta);
    if (next === draft.expectedUnits) {
      return;
    }

    draft.expectedUnits = next;
    draft.expectedUnitsManual = true;
    draft.expectedUnitsConfirmed = true;
    render(getCurrentRoom());
  }

  function renderDoctorTiles(room) {
    const target = document.getElementById("doctorTiles");
    if (!target) {
      return;
    }

    const isEnabled = canEditAssignment(room);
    const doctors = getSnapshot()?.doctors || [];
    const signature = `doctor|${isEnabled}|${draft.selectedDoctorId || ""}|`
      + doctors.map(doctor => `${doctor.id}:${doctor.color}:${doctor.name}`).join(";");
    setInnerHtmlIfChanged(target, doctors.map(doctor => `
      <button
        class="selection-tile doctor-tile ${doctor.id === draft.selectedDoctorId ? "selected" : ""}"
        style="--doctor-color: ${escapeAttribute(doctor.color)}"
        type="button"
        role="radio"
        aria-checked="${doctor.id === draft.selectedDoctorId}"
        data-doctor-id="${escapeAttribute(doctor.id)}"
        ${isEnabled ? "" : "disabled"}>
        <span class="doctor-color-swatch"></span>
        <span class="selection-copy">
          <strong>${escapeHtml(doctor.name)}</strong>
        </span>
        ${doctor.id === draft.selectedDoctorId ? `<span class="selected-indicator" aria-hidden="true">&#10003;</span>` : ""}
      </button>
    `).join(""), signature);
  }

  function renderProcedureTiles(room) {
    const target = document.getElementById("procedureTiles");
    if (!target) {
      return;
    }

    const isEnabled = canEditAssignment(room);
    const procedures = (getSnapshot()?.procedures || [])
      .filter(selectedProcedure => String(selectedProcedure.code || "").toUpperCase() !== "SED");
    const signature = `procedure|${isEnabled}|${draft.selectedProcedureId || ""}|`
      + procedures
        .map(selectedProcedure =>
          `${selectedProcedure.code}:${selectedProcedure.label}:${selectedProcedure.icon}:${procedure.resolveAccent(selectedProcedure.code)}`)
        .join(";");
    setInnerHtmlIfChanged(target, procedures.map(selectedProcedure => `
      <button
        class="selection-tile procedure-tile ${selectedProcedure.code === draft.selectedProcedureId ? "selected" : ""}"
        style="${procedureAccentStyle(selectedProcedure.code)}"
        type="button"
        role="radio"
        aria-checked="${selectedProcedure.code === draft.selectedProcedureId}"
        data-procedure-id="${escapeAttribute(selectedProcedure.code)}"
        ${isEnabled ? "" : "disabled"}>
        ${procedure.renderIcon(selectedProcedure)}
        <span class="selection-copy">
          <strong>${escapeHtml(selectedProcedure.code)}</strong>
          <small>${escapeHtml(selectedProcedure.label)}</small>
        </span>
        ${selectedProcedure.code === draft.selectedProcedureId ? `<span class="selected-indicator" aria-hidden="true">&#10003;</span>` : ""}
      </button>
    `).join(""), signature);
  }

  function procedureAccentStyle(code) {
    const accent = procedure.resolveAccent(code);
    return accent ? `--procedure-accent: ${accent}` : "";
  }

  function selectedProcedureIsSedationEligible() {
    const selectedProcedure = procedure.fromCode(draft.selectedProcedureId);
    return Boolean(selectedProcedure && selectedProcedure.sedationEligible);
  }

  function renderSedationToggle(room) {
    const toggle = document.getElementById("sedationToggle");
    if (!toggle) {
      return;
    }

    const canEdit = canEditAssignment(room);
    const eligible = selectedProcedureIsSedationEligible();
    const stateName = room ? normalizeState(room) : "empty";
    const assignmentLocked = room?.assignmentLocked === true
      || (Boolean(room) && !canEdit && stateName !== "empty");
    let interactable;
    let isOn;

    if (canEdit) {
      interactable = eligible;
      if (!eligible) {
        draft.sedationOn = false;
      }
      isOn = eligible && draft.sedationOn;
    } else {
      interactable = false;
      isOn = room?.assignment?.sedation?.state === "EligibleYes"
        || procedure.hasSedationModifier(room?.procedureCode);
    }

    toggle.disabled = !interactable;
    toggle.classList.toggle("selected", isOn);
    toggle.setAttribute("aria-checked", String(isOn));

    const state = toggle.querySelector(".sedation-state");
    if (state) {
      state.textContent = isOn ? "On" : "Off";
    }

    const hint = toggle.querySelector(".sedation-hint");
    if (hint) {
      hint.textContent = assignmentLocked
        ? "Locked with the Ready handoff"
        : isLegacyActiveRoom(room)
          ? "Legacy assignment is read-only until the room is restarted."
          : !canEdit
            ? "Available after Begin Prestage and an eligible procedure."
            : eligible
              ? "Optional modifier. Leave off when sedation is not used."
              : "Not available for this procedure";
    }
  }

  function isLegacyActiveRoom(room) {
    return Boolean(room)
      && (normalizeState(room) === "prestaging" || normalizeState(room) === "seated")
      && !room.episodeId;
  }

  function setInnerHtmlIfChanged(target, html, signature) {
    if (target.dataset.renderKey === signature) {
      return;
    }

    target.dataset.renderKey = signature;
    target.innerHTML = html;
  }

  function render(room = getCurrentRoom()) {
    if (!isTilePressActive()) {
      syncRoomSelection(room);
      renderSelectionTiles(room);
    }
    renderAssignmentGuidance(room);
    setRoomControlsEnabled(room);
  }

  function wire() {
    const beginPrestageButton = document.getElementById("beginPrestageButton");
    const seatButton = document.getElementById("seatButton");
    const readyForDoctorButton = document.getElementById("readyForDoctorButton");
    const saveDetailsButton = document.getElementById("saveDetailsButton");
    const discardChangesButton = document.getElementById("discardChangesButton");
    const withdrawReadyButton = document.getElementById("withdrawReadyButton");
    const cancelSeatingButton = document.getElementById("cancelSeatingButton");
    const doctorArrivedButton = document.getElementById("doctorArrivedButton");
    const doctorCompleteButton = document.getElementById("doctorCompleteButton");
    const roomAvailableButton = document.getElementById("roomAvailableButton");

    if (!beginPrestageButton
      || !seatButton
      || !readyForDoctorButton
      || !saveDetailsButton
      || !discardChangesButton
      || !withdrawReadyButton
      || !cancelSeatingButton
      || !doctorArrivedButton
      || !doctorCompleteButton
      || !roomAvailableButton) {
      console.error("[ChairSide] Room panel buttons were not found.", {
        beginPrestageButton,
        seatButton,
        readyForDoctorButton,
        saveDetailsButton,
        discardChangesButton,
        withdrawReadyButton,
        cancelSeatingButton,
        doctorArrivedButton,
        doctorCompleteButton,
        roomAvailableButton
      });
      return;
    }

    console.log("[ChairSide] Room panel click handlers bound.", { roomNumber: getRoomNumber() });
    wireSelectionTiles();

    beginPrestageButton.addEventListener("click", async () => {
      if (!roomCapabilities(getCurrentRoom()).canBeginPrestage) {
        setRoomActionStatus("Begin Prestage is only available when the room is available.", "error");
        return;
      }
      setRoomActionStatus("Starting room preparation...", "pending");
      try {
        const result = await sendCanonicalRoomAction("prestage", {});
        applyRoomMutationResult(result);
        setRoomActionStatus(
          "Prestaging started. Assignment details can be added now or after seating.",
          "success");
      } catch (error) {
        setRoomActionStatus(error.message || "Failed to begin Prestaging.", "error");
      }
    });

    saveDetailsButton.addEventListener("click", async () => {
      if (!canEditAssignment(getCurrentRoom()) || !isAssignmentDraftDirty()) {
        return;
      }
      setRoomActionStatus("Saving details...", "pending");
      try {
        const result = await sendSaveDetails(canonicalAssignmentRequest());
        applyRoomMutationResult(result);
        setRoomActionStatus("Details saved.", "success");
      } catch (error) {
        setRoomActionStatus(error.message || "Failed to save details.", "error");
      }
    });

    discardChangesButton.addEventListener("click", () => {
      discardAssignmentDraft();
      setRoomActionStatus("Changes discarded.", "pending");
    });

    seatButton.addEventListener("click", async () => {
      if (!isConfiguredRoom(getRoomNumber())) {
        setRoomActionStatus("This room is not configured.", "error");
        return;
      }

      if (!roomCapabilities(getCurrentRoom()).canSeat) {
        setRoomActionStatus("Seat Room is only available after Prestaging begins.", "error");
        return;
      }
      setRoomActionStatus("Seating room...", "pending");

      try {
        const result = await sendSeatRoom(canonicalAssignmentRequest());
        applyRoomMutationResult(result);
        setRoomActionStatus("Room seated.", "success");
      } catch (error) {
        setRoomActionStatus(error.message || "Failed to seat room.", "error");
      }
    });

    readyForDoctorButton.addEventListener("click", async () => {
      if (!isConfiguredRoom(getRoomNumber())) {
        setRoomActionStatus("This room is not configured.", "error");
        return;
      }

      if (!roomCapabilities(getCurrentRoom()).canReady) {
        setRoomActionStatus(
          "Ready for Doctor is only available while the room is in prep (Patient Seated).",
          "error");
        return;
      }

      console.log("[ChairSide] Ready for Doctor clicked.", { roomNumber: getRoomNumber() });
      setRoomActionStatus("Marking ready for doctor...", "pending");

      try {
        const result = await sendReadyForDoctor(canonicalAssignmentRequest());
        applyRoomMutationResult(result);
        setRoomActionStatus("Ready for doctor.", "success");
      } catch (error) {
        console.error("[ChairSide] Ready for Doctor failed.", { roomNumber: getRoomNumber(), error });
        setRoomActionStatus(error.message || "Failed to mark ready for doctor.", "error");
        focusFirstUnresolvedAssignmentControl();
      }
    });

    withdrawReadyButton.addEventListener("click", async () => {
      if (!roomCapabilities(getCurrentRoom()).canWithdrawReady) {
        setRoomActionStatus("Withdraw Ready is only available before Doctor Arrived.", "error");
        return;
      }
      setRoomActionStatus("Withdrawing the active handoff...", "pending");
      try {
        const result = await sendCanonicalRoomAction("withdraw-ready", {});
        applyRoomMutationResult(result);
        setRoomActionStatus("Ready withdrawn. Details are editable again.", "success");
      } catch (error) {
        setRoomActionStatus(error.message || "Failed to withdraw Ready.", "error");
      }
    });

    cancelSeatingButton.addEventListener("click", async () => {
      if (!isConfiguredRoom(getRoomNumber())) {
        setRoomActionStatus("This room is not configured.", "error");
        return;
      }

      const capabilities = roomCapabilities(getCurrentRoom());
      if (!capabilities.canCancelPrestage && !capabilities.canCancelSeating) {
        setRoomActionStatus("Cancel Seating is only available before Doctor Arrived.", "error");
        return;
      }

      const isConfirmed = window.confirm(`Cancel seating for Room ${getRoomNumber()}? This will return the room to available without creating a report entry.`);
      if (!isConfirmed) {
        setRoomActionStatus("Cancel seating aborted.", "pending");
        return;
      }

      console.log("[ChairSide] Cancel Seating clicked.", { roomNumber: getRoomNumber() });
      setRoomActionStatus("Canceling seating...", "pending");

      try {
        const action = capabilities.canCancelPrestage ? "cancel-prestage" : "cancel-seating";
        const result = await sendRoomAction(getRoomNumber(), action, "Cancel Seating");
        applyRoomMutationResult(result);
        console.log("[ChairSide] Cancel Seating succeeded.", { roomNumber: getRoomNumber() });
        setRoomActionStatus("Seating canceled. Room available.", "success");
      } catch (error) {
        console.error("[ChairSide] Cancel Seating failed.", { roomNumber: getRoomNumber(), error });
        setRoomActionStatus(error.message || "Failed to cancel seating.", "error");
      }
    });

    doctorArrivedButton.addEventListener("click", async () => {
      if (!isConfiguredRoom(getRoomNumber())) {
        setRoomActionStatus("This room is not configured.", "error");
        return;
      }

      if (!roomCapabilities(getCurrentRoom()).canDoctorArrive) {
        setRoomActionStatus("Doctor Arrived is only available after Ready for Doctor.", "error");
        return;
      }

      console.log("[ChairSide] Doctor Arrived clicked.", { roomNumber: getRoomNumber() });
      setRoomActionStatus("Marking doctor arrived...", "pending");

      try {
        const response = await fetch(`/api/rooms/${getRoomNumber()}/doctor-arrived`, {
          method: "POST",
          headers: mutationHeaders()
        });

        if (response.ok) {
          applyRoomMutationResult(await response.json());
          console.log("[ChairSide] Doctor Arrived succeeded.", { roomNumber: getRoomNumber() });
          setRoomActionStatus("Doctor arrived.", "success");
          return;
        }

        if (response.status === 409) {
          await handleDoctorArrivalConflict(response);
          return;
        }

        if (response.status === 401 || response.status === 403) {
          showRoomTokenPrompt(response.status);
        }

        throw new Error(await readErrorMessage(
          response,
          `Doctor Arrived failed with HTTP ${response.status}.`));
      } catch (error) {
        console.error("[ChairSide] Doctor Arrived failed.", { roomNumber: getRoomNumber(), error });
        setRoomActionStatus(error.message || "Failed to mark doctor arrived.", "error");
      }
    });

    doctorCompleteButton.addEventListener("click", async () => {
      if (!isConfiguredRoom(getRoomNumber())) {
        setRoomActionStatus("This room is not configured.", "error");
        return;
      }

      if (!roomCapabilities(getCurrentRoom()).canDoctorComplete) {
        setRoomActionStatus(
          "Doctor Complete is only available when the doctor is in the room.",
          "error");
        return;
      }

      console.log("[ChairSide] Doctor Complete clicked.", { roomNumber: getRoomNumber() });
      setRoomActionStatus("Marking doctor complete...", "pending");

      try {
        const result = await sendRoomAction(getRoomNumber(), "doctor-complete", "Doctor Complete");
        applyRoomMutationResult(result);
        console.log("[ChairSide] Doctor Complete succeeded.", { roomNumber: getRoomNumber() });
        setRoomActionStatus("Doctor complete. Turnover started.", "success");
      } catch (error) {
        console.error("[ChairSide] Doctor Complete failed.", { roomNumber: getRoomNumber(), error });
        setRoomActionStatus(error.message || "Failed to mark doctor complete.", "error");
      }
    });

    roomAvailableButton.addEventListener("click", async () => {
      if (!isConfiguredRoom(getRoomNumber())) {
        setRoomActionStatus("This room is not configured.", "error");
        return;
      }

      if (!roomCapabilities(getCurrentRoom()).canRoomAvailable) {
        setRoomActionStatus("Room Available is only available during turnover.", "error");
        return;
      }

      console.log("[ChairSide] Room Available clicked.", { roomNumber: getRoomNumber() });
      setRoomActionStatus("Marking room available...", "pending");

      try {
        const result = await sendRoomAction(getRoomNumber(), "available", "Room Available");
        applyRoomMutationResult(result);
        console.log("[ChairSide] Room Available succeeded.", { roomNumber: getRoomNumber() });
        setRoomActionStatus("Room available.", "success");
      } catch (error) {
        console.error("[ChairSide] Room Available failed.", { roomNumber: getRoomNumber(), error });
        setRoomActionStatus(error.message || "Failed to mark room available.", "error");
      }
    });
  }

  function wireSelectionTiles() {
    const doctorTiles = document.getElementById("doctorTiles");
    const procedureTiles = document.getElementById("procedureTiles");

    wireTileGroup(doctorTiles, "[data-doctor-id]", "doctorId", button => {
      draft.selectedDoctorId = button.dataset.doctorId;
      render(getCurrentRoom());
    });

    wireTileGroup(procedureTiles, "[data-procedure-id]", "procedureId", button => {
      const procedureChanged = draft.selectedProcedureId !== button.dataset.procedureId;
      draft.selectedProcedureId = button.dataset.procedureId;
      if (procedureChanged) {
        draft.sedationOn = false;
        draft.expectedUnitsConfirmed = false;
      }
      render(getCurrentRoom());
    });

    wireTilePressCleanup(() => {
      if (isAssignmentDraftDirty()) {
        discardAssignmentDraft();
        setRoomActionStatus("Changes discarded.", "pending");
      }
    });

    const sedationToggle = document.getElementById("sedationToggle");
    sedationToggle?.addEventListener("click", () => {
      if (sedationToggle.disabled || !selectedProcedureIsSedationEligible()) {
        return;
      }

      draft.sedationOn = !draft.sedationOn;
      render(getCurrentRoom());
    });

    document.getElementById("allocationMinus")
      ?.addEventListener("click", () => adjustExpectedUnits(-1));
    document.getElementById("allocationPlus")
      ?.addEventListener("click", () => adjustExpectedUnits(1));
    document.getElementById("allocationConfirm")?.addEventListener("click", () => {
      if (!canEditAssignment(getCurrentRoom()) || !draft.selectedProcedureId) {
        return;
      }
      draft.expectedUnits = draft.expectedUnits ?? selectedProcedureDefaultUnits();
      draft.expectedUnitsConfirmed = true;
      render(getCurrentRoom());
    });
  }

  async function sendCanonicalRoomAction(action, body) {
    return sendCanonicalMutation(
      `/api/rooms/${getRoomNumber()}/${action}`,
      "POST",
      body,
      action);
  }

  async function sendSaveDetails(assignment) {
    return sendCanonicalMutation(
      `/api/rooms/${getRoomNumber()}/assignment-details`,
      "PUT",
      assignment,
      "save details");
  }

  async function sendSeatRoom(assignment) {
    return sendCanonicalMutation(
      `/api/rooms/${getRoomNumber()}/seat`,
      "POST",
      { assignment },
      "seat room");
  }

  async function sendReadyForDoctor(assignment) {
    return sendCanonicalMutation(
      `/api/rooms/${getRoomNumber()}/ready-for-doctor`,
      "POST",
      { assignment },
      "ready for doctor");
  }

  async function sendCanonicalMutation(url, method, body, label) {
    const response = await fetch(url, {
      method,
      headers: mutationHeaders({ "Content-Type": "application/json" }),
      body: JSON.stringify(body)
    });
    if (!response.ok) {
      if (response.status === 401 || response.status === 403) {
        showRoomTokenPrompt(response.status);
      }
      throw new Error(await readErrorMessage(
        response,
        `${label} failed with HTTP ${response.status}.`));
    }
    return response.json();
  }

  function applyRoomMutationResult(result) {
    const room = result?.room || result;
    const rooms = getSnapshot()?.rooms;
    if (!room || !rooms) {
      return;
    }
    const roomId = getRoomId(room);
    if (!rooms.some(item => getRoomId(item) === roomId)) {
      return;
    }
    draft.selectionContext = null;
    applyRoomMutation(room);
  }

  async function handleDoctorArrivalConflict(response) {
    let conflict;
    try {
      conflict = await response.json();
    } catch {
      conflict = null;
    }

    if (!conflict || !conflict.conflictingRoomId) {
      setRoomActionStatus(
        "Doctor is already marked in another room. Refresh and try again.",
        "error");
      return;
    }

    const doctorName = conflict.doctorDisplayName || "The doctor";
    const oldRoom = conflict.conflictingRoomId;
    const newRoom = getRoomNumber();
    const confirmed = confirm(`${doctorName} is already marked in Room ${oldRoom}. Mark Doctor Complete for Room ${oldRoom} and move them to Room ${newRoom}?`);
    if (!confirmed) {
      setRoomActionStatus("Doctor Arrived canceled.", "error");
      return;
    }

    setRoomActionStatus("Moving doctor...", "pending");
    try {
      const resolveResponse = await fetch(
        `/api/rooms/${newRoom}/doctor-arrived/resolve-conflict`,
        {
          method: "POST",
          headers: mutationHeaders({ "Content-Type": "application/json" }),
          body: JSON.stringify({ conflictingRoomId: oldRoom })
        });

      if (resolveResponse.ok) {
        setRoomActionStatus(
          "Doctor moved. Previous room marked Doctor Complete.",
          "success");
        return;
      }

      if (resolveResponse.status === 401 || resolveResponse.status === 403) {
        showRoomTokenPrompt(resolveResponse.status);
      }

      throw new Error(await readErrorMessage(
        resolveResponse,
        `Move failed with HTTP ${resolveResponse.status}.`));
    } catch (error) {
      console.error("[ChairSide] Resolve doctor conflict failed.", { roomNumber: newRoom, error });
      setRoomActionStatus(
        error.message || "Could not move the doctor. Refresh and try again.",
        "error");
    }
  }

  async function sendRoomAction(roomNumber, action, label) {
    const payload = { roomNumber, action };
    console.log(`[ChairSide] Sending ${label} payload.`, payload);
    const response = await fetch(`/api/rooms/${roomNumber}/${action}`, {
      method: "POST",
      headers: mutationHeaders()
    });

    if (!response.ok) {
      if (response.status === 401 || response.status === 403) {
        showRoomTokenPrompt(response.status);
      }

      throw new Error(await readErrorMessage(
        response,
        `${label} failed with HTTP ${response.status}.`));
    }

    return response.json();
  }

  function setRoomActionStatus(message, tone) {
    const target = document.getElementById("roomActionStatus");
    if (!target) {
      return;
    }

    target.textContent = message;
    target.dataset.tone = tone;
  }

  return {
    render,
    setStatus: setRoomActionStatus,
    wire
  };
}
