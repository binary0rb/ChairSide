const app = {
  snapshot: null,
  reports: null,
  connection: null,
  hubReady: false,
  tickHandle: null,
  pollHandle: null,
  statusHandle: null,
  realtimeRetryHandle: null,
  lastSnapshotAt: 0,
  lastPollAt: 0,
  serverOffsetMs: 0,
  connectionStatus: "stale",
  realtimeDegraded: false,
  realtimeLostAt: 0,
  pollInFlight: false,
  roomNumber: getRoomNumber(),
  roomToken: getRoomToken(),
  doctorId: new URLSearchParams(location.search).get("doctor") || "otte",
  selectedDoctorId: null,
  selectedProcedureId: null,
  selectionContext: null
};

const stateNames = ["empty", "seated", "aging", "stale", "doctor-in-room", "turnover"];
const activeSeatedStates = new Set(["seated", "aging", "stale"]);

function getRoomNumber() {
  const query = new URLSearchParams(location.search);
  const requestedRoom = document.body.dataset.roomNumber || query.get("roomId") || query.get("room") || "1";
  const roomNumber = Number(requestedRoom);

  return Number.isInteger(roomNumber) ? roomNumber : 0;
}

function getRoomToken() {
  if (document.body.dataset.view !== "room") {
    return "";
  }

  const query = new URLSearchParams(location.search);
  const metaToken = document.querySelector("meta[name='chairside-room-token']")?.content || "";
  return query.get("roomToken") || query.get("token") || metaToken;
}

async function boot() {
  await loadBoard();
  if (document.body.dataset.view === "reports") {
    await loadReports();
  }

  connectRealtime();
  app.pollHandle = window.setInterval(loadBoard, 5000);
  app.tickHandle = window.setInterval(render, 1000);
  app.statusHandle = window.setInterval(updateConnectionStatus, 1000);
  updateConnectionStatus();

  if (document.body.dataset.view === "room") {
    wireRoomPanel();
  }
}

async function loadBoard() {
  if (app.pollInFlight) {
    return false;
  }

  app.pollInFlight = true;
  try {
    const response = await fetch("/api/board", { cache: "no-store" });
    if (!response.ok) {
      throw new Error(`Board snapshot failed with HTTP ${response.status}.`);
    }

    applySnapshot(await response.json());
    app.lastPollAt = Date.now();
    render();
    updateConnectionStatus();
    return true;
  } catch (error) {
    console.warn("[ChairSide] Board polling failed.", error);
    updateConnectionStatus();
    return false;
  } finally {
    app.pollInFlight = false;
  }
}

async function loadReports() {
  const response = await fetch("/api/reports");
  app.reports = await response.json();
  render();
}

function connectRealtime() {
  if (!window.signalR) {
    markRealtimeDegraded();
    updateConnectionStatus();
    return;
  }

  if (app.hubReady || app.connection?.state === "Connecting" || app.connection?.state === "Reconnecting") {
    return;
  }

  const connection = new signalR.HubConnectionBuilder()
    .withUrl("/boardHub")
    .withAutomaticReconnect()
    .build();

  app.connection = connection;

  connection.on("boardUpdated", snapshot => {
    applySnapshot(snapshot);
    if (document.body.dataset.view === "reports") {
      loadReports();
    }
    render();
    updateConnectionStatus();
  });

  if (typeof connection.onreconnecting === "function") {
    connection.onreconnecting(() => {
      markRealtimeDegraded();
      updateConnectionStatus();
    });
  }

  if (typeof connection.onreconnected === "function") {
    connection.onreconnected(() => {
      app.hubReady = true;
      app.realtimeDegraded = false;
      app.realtimeLostAt = 0;
      setConnectionStatus("live");
      loadBoard();
    });
  }

  if (typeof connection.onclose === "function") {
    connection.onclose(() => {
      markRealtimeDegraded();
      updateConnectionStatus();
    });
  }

  connection.start()
    .then(() => {
      app.hubReady = true;
      app.realtimeDegraded = false;
      app.realtimeLostAt = 0;
      setConnectionStatus("live");
    })
    .catch(error => {
      console.warn("[ChairSide] SignalR connection failed; polling fallback remains active.", error);
      markRealtimeDegraded();
      updateConnectionStatus();
      scheduleRealtimeRetry();
    });
}

function markRealtimeDegraded() {
  app.hubReady = false;
  app.realtimeDegraded = true;
  if (!app.realtimeLostAt) {
    app.realtimeLostAt = Date.now();
  }
}

function scheduleRealtimeRetry() {
  if (app.realtimeRetryHandle) {
    return;
  }

  app.realtimeRetryHandle = window.setTimeout(() => {
    app.realtimeRetryHandle = null;
    if (!app.hubReady) {
      connectRealtime();
    }
  }, 5000);
}

function render() {
  if (!app.snapshot) {
    return;
  }

  const view = document.body.dataset.view;
  updateConnectionStatus();
  renderLegend();

  if (view === "master") {
    renderMaster();
  }

  if (view === "room") {
    renderRoomPanel();
  }

  if (view === "doctor") {
    renderDoctorView();
  }

  if (view === "reports") {
    renderReports();
  }
}

function applySnapshot(snapshot) {
  app.snapshot = snapshot;
  app.lastSnapshotAt = Date.now();

  const serverTime = Date.parse(snapshot.serverTime);
  if (!Number.isNaN(serverTime)) {
    app.serverOffsetMs = serverTime - Date.now();
  }
}

function boardNowMs() {
  return Date.now() + app.serverOffsetMs;
}

function ensureConnectionStatusIndicator() {
  let target = document.getElementById("connectionStatus");
  if (target) {
    return target;
  }

  const header = document.querySelector(".app-header") || document.body;
  target = document.createElement("div");
  target.id = "connectionStatus";
  target.className = "connection-status stale";
  target.setAttribute("role", "status");
  target.setAttribute("aria-live", "polite");
  target.innerHTML = `<i aria-hidden="true"></i><span>Stale</span>`;
  header.appendChild(target);
  return target;
}

function setConnectionStatus(status) {
  app.connectionStatus = status;
  const target = ensureConnectionStatusIndicator();
  target.className = `connection-status ${status}`;
  target.querySelector("span").textContent = status === "live"
    ? "Live"
    : status === "reconnecting"
      ? "Reconnecting"
      : "Stale";
}

function updateConnectionStatus() {
  const snapshotAgeMs = app.lastSnapshotAt ? Date.now() - app.lastSnapshotAt : Number.POSITIVE_INFINITY;
  const staleAfterMs = 15000;
  const pollingFreshAfterRealtimeLoss = app.realtimeDegraded
    && app.lastPollAt > app.realtimeLostAt
    && Date.now() - app.lastPollAt <= 7000;

  if (snapshotAgeMs > staleAfterMs) {
    setConnectionStatus("stale");
    return;
  }

  if (app.hubReady || pollingFreshAfterRealtimeLoss || !app.realtimeDegraded) {
    setConnectionStatus("live");
    return;
  }

  setConnectionStatus("reconnecting");
}

function renderLegend() {
  const doctorTarget = document.getElementById("doctorLegend");
  if (doctorTarget) {
    doctorTarget.innerHTML = app.snapshot.doctors.map(doctor => `
      <span class="doctor-chip">
        <i style="--doctor-color: ${doctor.color}"></i>${doctor.name}
      </span>
    `).join("");
  }

  const procedureTarget = document.getElementById("procedureLegend");
  if (procedureTarget) {
    procedureTarget.innerHTML = app.snapshot.procedures.map(procedure => `
      <span class="procedure-chip">
        <span>${renderProcedureIcon(procedure.icon)}</span>
        <strong>${procedure.label}</strong>
        <small>${procedure.name}</small>
      </span>
    `).join("");
  }
}

function renderMaster() {
  const target = document.getElementById("roomGrid");
  target.innerHTML = app.snapshot.rooms.map(room => renderRoomTile(room)).join("");
  target.setAttribute("aria-label", `${getRoomCount()} room cards`);
}

function renderRoomPanel() {
  const title = document.getElementById("roomPanelTitle");
  const status = document.getElementById("roomPanelStatus");
  const room = app.snapshot.rooms.find(item => getRoomId(item) === app.roomNumber);

  title.textContent = `Room ${app.roomNumber}`;
  status.innerHTML = room ? renderRoomTile(room, true) : renderInvalidRoomMessage();
  syncRoomSelection(room);
  renderSelectionTiles(room);
  populateDemoTimerSelect();
  setRoomControlsEnabled(room);
}

function renderDoctorView() {
  const doctor = app.snapshot.doctors.find(item => item.id === app.doctorId) || app.snapshot.doctors[0];
  const title = document.getElementById("doctorViewTitle");
  const target = document.getElementById("doctorRoomList");
  const rooms = app.snapshot.rooms.filter(room => room.assignedDoctor === doctor.id || (room.doctor && room.doctor.id === doctor.id));

  title.textContent = doctor.name;
  document.documentElement.style.setProperty("--active-doctor", doctor.color);

  target.innerHTML = rooms.length
    ? rooms.map(room => renderRoomTile(room)).join("")
    : `<div class="empty-message">No active rooms for ${doctor.name}.</div>`;
}

function renderReports() {
  if (!app.reports) {
    return;
  }

  const summary = document.getElementById("reportSummary");
  const body = document.getElementById("completedCyclesBody");
  const cycles = app.reports.recentCompletedCycles || [];

  summary.innerHTML = [
    renderMetric("Completed Cycles", app.reports.completedRoomCyclesCount),
    renderMetric("Avg Seated-to-Doctor", formatDuration(app.reports.averageSeatedToDoctorSeconds)),
    renderMetric("Median Seated-to-Doctor", formatDuration(app.reports.medianSeatedToDoctorSeconds)),
    renderMetric("Avg In Room", formatDuration(app.reports.averageDoctorInRoomSeconds)),
    renderMetric("Median In Room", formatDuration(app.reports.medianDoctorInRoomSeconds)),
    renderMetric("Avg Turnover", formatDuration(app.reports.averageTurnoverSeconds)),
    renderMetric("Median Turnover", formatDuration(app.reports.medianTurnoverSeconds)),
    renderMetric("Aging Events", app.reports.agingEventCount),
    renderMetric("Stale Events", app.reports.staleEventCount)
  ].join("");

  body.innerHTML = cycles.length
    ? cycles.map(renderCycleRow).join("")
    : `<tr><td colspan="14">No completed room cycles yet.</td></tr>`;
}

function renderMetric(label, value) {
  return `
    <article class="metric-card">
      <span>${label}</span>
      <strong>${value}</strong>
    </article>
  `;
}

function renderCycleRow(cycle) {
  const doctor = doctorName(cycle.assignedDoctor);
  return `
    <tr>
      <td>Room ${cycle.roomId}</td>
      <td>${doctor}</td>
      <td>${renderProcedureBadge(cycle.procedureCode)}</td>
      <td>${formatDateTime(cycle.seatedAt)}</td>
      <td>${formatDateTime(cycle.doctorArrivedAt)}</td>
      <td>${formatDateTime(cycle.doctorCompleteAt)}</td>
      <td>${formatDateTime(cycle.roomAvailableAt)}</td>
      <td>${formatDuration(cycle.seatedToDoctorSeconds)}</td>
      <td>${formatDuration(cycle.doctorInRoomSeconds)}</td>
      <td>${formatDuration(cycle.turnoverSeconds)}</td>
      <td>${formatDuration(cycle.totalRoomCycleSeconds)}</td>
      <td>${String(cycle.finalWaitState || "--").toUpperCase()}</td>
      <td>${cycle.agingThresholdReached ? "Yes" : "No"}</td>
      <td>${cycle.staleThresholdReached ? "Yes" : "No"}</td>
    </tr>
  `;
}

function doctorName(doctorId) {
  const doctor = app.snapshot?.doctors.find(item => item.id === doctorId);
  return doctor ? doctor.name : doctorId;
}

function renderRoomTile(room, large = false) {
  const state = normalizeState(room);
  const roomId = getRoomId(room);
  const doctorColor = room.doctor ? room.doctor.color : "#8b949e";
  const procedure = room.procedure || procedureFromCode(room.procedureCode);
  const badge = stateBadge(state);
  const timer = seatedToDoctorLabel(room);
  const fullDoctorName = room.doctor ? room.doctor.name : "Unassigned";
  const doctorDisplayName = large ? fullDoctorName : cardDoctorName(fullDoctorName);

  return `
    <article class="room-tile ${state} ${large ? "large" : ""}" style="--doctor-color: ${doctorColor}">
      <div class="room-topline">
        <strong>Room ${roomId}</strong>
        <span>${badge}</span>
      </div>
      <div class="procedure-lockup">
        ${procedure ? renderProcedureIcon(procedure.icon) : renderEmptyIcon()}
        <span>${procedure ? procedure.label : "OPEN"}</span>
      </div>
      <div class="room-footer">
        <span class="room-doctor-name" title="${fullDoctorName}">${doctorDisplayName}</span>
        <time class="room-timer">
          <span>${timer.label}</span>
          <strong>${timer.value}</strong>
        </time>
      </div>
    </article>
  `;
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

  if (raw === "turnover" || !room.seatedAt || room.clearedAt) {
    return raw;
  }

  if (room.doctorArrivedAt) {
    return "doctor-in-room";
  }

  const seatedAtMs = Date.parse(room.seatedAt);
  if (Number.isNaN(seatedAtMs)) {
    return raw;
  }

  const elapsedMinutes = Math.max(0, (boardNowMs() - seatedAtMs) / 60000);
  const agingMinutes = getAgingMinutes();
  const staleMinutes = getStaleMinutes();
  if (staleMinutes !== null && elapsedMinutes >= staleMinutes) {
    return "stale";
  }

  if (agingMinutes !== null && elapsedMinutes >= agingMinutes) {
    return "aging";
  }

  return "seated";
}

function stateBadge(state) {
  if (state === "doctor-in-room") {
    return "IN ROOM";
  }

  if (state === "turnover") {
    return "TURNOVER";
  }

  return state.toUpperCase();
}

function seatedToDoctorLabel(room) {
  if (!room.seatedAt) {
    return { label: "Available", value: "--:--" };
  }

  if (room.doctorArrivedAt) {
    return {
      label: "To doctor",
      value: formatDuration(secondsBetweenDates(room.seatedAt, room.doctorArrivedAt))
    };
  }

  return {
    label: "Waiting",
    value: formatElapsed(room.seatedAt)
  };
}

function getRoomId(room) {
  return room.roomId || room.number;
}

function getRoomCount() {
  return app.snapshot.roomCount || app.snapshot.rooms.length;
}

function renderInvalidRoomMessage() {
  const roomCount = getRoomCount();
  return `
    <div class="invalid-room-message">
      <h2>Room ${app.roomNumber || "?"} is not configured</h2>
      <p>Use Room 1 through Room ${roomCount} for this board.</p>
    </div>
  `;
}

function setRoomControlsEnabled(room) {
  const isEnabled = Boolean(room);
  const state = room ? normalizeState(room) : "empty";
  const canCorrect = activeSeatedStates.has(state);
  setDisabled("demoElapsedSelect", !isEnabled || state !== "empty");
  setDisabled("seatButton", !isEnabled || state !== "empty");
  setDisabled("updateAssignmentButton", !isEnabled || !canCorrect);
  setDisabled("cancelSeatingButton", !isEnabled || !canCorrect);
  setDisabled("doctorArrivedButton", !isEnabled || !activeSeatedStates.has(state));
  setDisabled("doctorCompleteButton", !isEnabled || state !== "doctor-in-room");
  setDisabled("roomAvailableButton", !isEnabled || state !== "turnover");
}

function setDisabled(id, isDisabled) {
  const control = document.getElementById(id);
  if (control) {
    control.disabled = isDisabled;
  }
}

function procedureFromCode(procedureCode) {
  if (!procedureCode || !app.snapshot) {
    return null;
  }

  return app.snapshot.procedures.find(procedure =>
    procedure.label === procedureCode || procedure.id === procedureCode
  ) || null;
}

function renderProcedureBadge(procedureCode) {
  const procedure = procedureFromCode(procedureCode);
  if (!procedure) {
    return procedureCode || "--";
  }

  return `
    <span class="procedure-badge">
      ${renderProcedureIcon(procedure.icon)}
      <span>
        <strong>${procedure.label}</strong>
        <small>${procedure.name}</small>
      </span>
    </span>
  `;
}

function renderProcedureIcon(icon) {
  const icons = {
    speech: `<svg class="procedure-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M4 5.5h16v10H9l-5 4v-14z"/><path d="M8 9h8M8 12h6"/></svg>`,
    forceps: `<svg class="procedure-icon forceps-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M6 3l6 8 6-8"/><path d="M8.5 21L12 11l3.5 10"/><path d="M5 7l5.5 5.5M19 7l-5.5 5.5"/><path d="M7 17h10"/></svg>`,
    moon: `<svg class="procedure-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M18.5 15.5A7.5 7.5 0 0 1 8.5 5.5 8.5 8.5 0 1 0 18.5 15.5z"/></svg>`,
    check: `<svg class="procedure-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M4.5 4.5h15v15h-15z"/><path d="M8 12.5l3 3 5.5-7"/></svg>`,
    bolt: `<svg class="procedure-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M14 2L6 13h5l-1 9 8-12h-5l1-8z"/><path d="M8 17h8"/></svg>`,
    vial: `<svg class="procedure-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M8 3h8"/><path d="M10 3v6l-4 8.5A2.8 2.8 0 0 0 8.5 21h7a2.8 2.8 0 0 0 2.5-3.5L14 9V3"/><path d="M8 16h8"/></svg>`
  };

  return icons[icon] || renderEmptyIcon();
}

function renderEmptyIcon() {
  return `<svg class="procedure-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12h14"/></svg>`;
}

function syncRoomSelection(room) {
  const context = room
    ? `${getRoomId(room)}:${room.seatedAt || "available"}:${room.assignedDoctor || ""}:${room.procedureCode || ""}`
    : `invalid:${app.roomNumber}`;
  if (app.selectionContext === context && app.selectedDoctorId && app.selectedProcedureId) {
    return;
  }

  app.selectionContext = context;
  app.selectedDoctorId = room?.assignedDoctor || room?.doctor?.id || app.snapshot.doctors[0]?.id || null;
  const procedure = room?.procedure || procedureFromCode(room?.procedureCode) || app.snapshot.procedures[0] || null;
  app.selectedProcedureId = procedure?.id || null;
}

function renderSelectionTiles(room) {
  renderDoctorTiles(room);
  renderProcedureTiles(room);
}

function renderDoctorTiles(room) {
  const target = document.getElementById("doctorTiles");
  if (!target) {
    return;
  }

  const isEnabled = canEditAssignment(room);
  target.innerHTML = app.snapshot.doctors.map(doctor => `
    <button
      class="selection-tile doctor-tile ${doctor.id === app.selectedDoctorId ? "selected" : ""}"
      style="--doctor-color: ${doctor.color}"
      type="button"
      role="radio"
      aria-checked="${doctor.id === app.selectedDoctorId}"
      data-doctor-id="${doctor.id}"
      ${isEnabled ? "" : "disabled"}>
      <span class="doctor-color-swatch"></span>
      <strong>${doctor.name}</strong>
    </button>
  `).join("");
}

function renderProcedureTiles(room) {
  const target = document.getElementById("procedureTiles");
  if (!target) {
    return;
  }

  const isEnabled = canEditAssignment(room);
  target.innerHTML = app.snapshot.procedures.map(procedure => `
    <button
      class="selection-tile procedure-tile ${procedure.id === app.selectedProcedureId ? "selected" : ""}"
      type="button"
      role="radio"
      aria-checked="${procedure.id === app.selectedProcedureId}"
      data-procedure-id="${procedure.id}"
      ${isEnabled ? "" : "disabled"}>
      ${renderProcedureIcon(procedure.icon)}
      <span>
        <strong>${procedure.label}</strong>
        <small>${procedure.name}</small>
      </span>
    </button>
  `).join("");
}

function canEditAssignment(room) {
  if (!room) {
    return false;
  }

  const state = normalizeState(room);
  return state === "empty" || activeSeatedStates.has(state);
}

function populateDemoTimerSelect() {
  const demoElapsedSelect = document.getElementById("demoElapsedSelect");
  if (!demoElapsedSelect || demoElapsedSelect.options.length) {
    return;
  }

  const options = [`<option value="0">Start now</option>`];
  const agingMinutes = getAgingMinutes();
  const staleMinutes = getStaleMinutes();
  if (agingMinutes !== null) {
    options.push(`<option value="${Math.ceil(agingMinutes) + 1}">Simulate aging wait</option>`);
  }

  if (staleMinutes !== null) {
    options.push(`<option value="${Math.ceil(staleMinutes) + 1}">Simulate stale wait</option>`);
  }

  demoElapsedSelect.innerHTML = options.join("");
}

function getAgingMinutes() {
  return Number.isFinite(Number(app.snapshot?.agingMinutes))
    ? Number(app.snapshot.agingMinutes)
    : thresholdMinutes(app.snapshot?.agingThreshold);
}

function getStaleMinutes() {
  return Number.isFinite(Number(app.snapshot?.staleMinutes))
    ? Number(app.snapshot.staleMinutes)
    : thresholdMinutes(app.snapshot?.staleThreshold);
}

function thresholdMinutes(value) {
  if (typeof value === "number") {
    return value / 60;
  }

  const parts = String(value || "").split(":").map(part => Number(part));
  if (parts.length !== 3 || parts.some(part => Number.isNaN(part))) {
    return null;
  }

  const [hours, minutes, seconds] = parts;
  return (hours * 60) + minutes + (seconds / 60);
}

function wireRoomPanel() {
  const seatButton = document.getElementById("seatButton");
  const updateAssignmentButton = document.getElementById("updateAssignmentButton");
  const cancelSeatingButton = document.getElementById("cancelSeatingButton");
  const doctorArrivedButton = document.getElementById("doctorArrivedButton");
  const doctorCompleteButton = document.getElementById("doctorCompleteButton");
  const roomAvailableButton = document.getElementById("roomAvailableButton");

  if (!seatButton || !updateAssignmentButton || !cancelSeatingButton || !doctorArrivedButton || !doctorCompleteButton || !roomAvailableButton) {
    console.error("[ChairSide] Room panel buttons were not found.", {
      seatButton,
      updateAssignmentButton,
      cancelSeatingButton,
      doctorArrivedButton,
      doctorCompleteButton,
      roomAvailableButton
    });
    return;
  }

  console.log("[ChairSide] Room panel click handlers bound.", { roomNumber: app.roomNumber });
  wireSelectionTiles();

  seatButton.addEventListener("click", async () => {
    if (!isConfiguredRoom(app.roomNumber)) {
      setRoomActionStatus("This room is not configured.", "error");
      return;
    }

    if (!isRoomInState("empty")) {
      setRoomActionStatus("Seat Room is only available when the room is available.", "error");
      return;
    }

    const doctorId = app.selectedDoctorId;
    const procedureCode = app.selectedProcedureId;
    const demoElapsedMinutes = Number(document.getElementById("demoElapsedSelect")?.value || "0");
    if (!hasAssignmentSelection()) {
      setRoomActionStatus("Choose a doctor and procedure first.", "error");
      return;
    }

    const payload = {
      roomNumber: app.roomNumber,
      doctorId,
      procedureCode,
      procedureId: procedureCode,
      demoElapsedMinutes
    };

    console.log("[ChairSide] Seat Room clicked.", payload);
    setRoomActionStatus("Seating room...", "pending");

    try {
      await sendSeatRoom(payload);
      console.log("[ChairSide] Seat Room succeeded.", payload);
      setRoomActionStatus("Room seated.", "success");
    } catch (error) {
      console.error("[ChairSide] Seat Room failed.", { payload, error });
      setRoomActionStatus(error.message || "Failed to seat room.", "error");
    }
  });

  updateAssignmentButton.addEventListener("click", async () => {
    if (!isConfiguredRoom(app.roomNumber)) {
      setRoomActionStatus("This room is not configured.", "error");
      return;
    }

    if (!activeSeatedStates.has(currentRoomState())) {
      setRoomActionStatus("Update Assignment is only available before Doctor Arrived.", "error");
      return;
    }

    if (!hasAssignmentSelection()) {
      setRoomActionStatus("Choose a doctor and procedure first.", "error");
      return;
    }

    const payload = {
      roomNumber: app.roomNumber,
      doctorId: app.selectedDoctorId,
      procedureCode: app.selectedProcedureId,
      procedureId: app.selectedProcedureId
    };

    console.log("[ChairSide] Update Assignment clicked.", payload);
    setRoomActionStatus("Updating assignment...", "pending");

    try {
      await sendAssignmentUpdate(payload);
      console.log("[ChairSide] Update Assignment succeeded.", payload);
      setRoomActionStatus("Assignment updated.", "success");
    } catch (error) {
      console.error("[ChairSide] Update Assignment failed.", { payload, error });
      setRoomActionStatus(error.message || "Failed to update assignment.", "error");
    }
  });

  cancelSeatingButton.addEventListener("click", async () => {
    if (!isConfiguredRoom(app.roomNumber)) {
      setRoomActionStatus("This room is not configured.", "error");
      return;
    }

    if (!activeSeatedStates.has(currentRoomState())) {
      setRoomActionStatus("Cancel Seating is only available before Doctor Arrived.", "error");
      return;
    }

    const isConfirmed = window.confirm(`Cancel seating for Room ${app.roomNumber}? This will return the room to available without creating a report entry.`);
    if (!isConfirmed) {
      setRoomActionStatus("Cancel seating aborted.", "pending");
      return;
    }

    console.log("[ChairSide] Cancel Seating clicked.", { roomNumber: app.roomNumber });
    setRoomActionStatus("Canceling seating...", "pending");

    try {
      await sendRoomAction(app.roomNumber, "cancel-seating", "Cancel Seating");
      console.log("[ChairSide] Cancel Seating succeeded.", { roomNumber: app.roomNumber });
      setRoomActionStatus("Seating canceled. Room available.", "success");
    } catch (error) {
      console.error("[ChairSide] Cancel Seating failed.", { roomNumber: app.roomNumber, error });
      setRoomActionStatus(error.message || "Failed to cancel seating.", "error");
    }
  });

  doctorArrivedButton.addEventListener("click", async () => {
    if (!isConfiguredRoom(app.roomNumber)) {
      setRoomActionStatus("This room is not configured.", "error");
      return;
    }

    if (!activeSeatedStates.has(currentRoomState())) {
      setRoomActionStatus("Doctor Arrived is only available for seated, aging, or stale rooms.", "error");
      return;
    }

    console.log("[ChairSide] Doctor Arrived clicked.", { roomNumber: app.roomNumber });
    setRoomActionStatus("Marking doctor arrived...", "pending");

    try {
      await sendRoomAction(app.roomNumber, "doctor-arrived", "Doctor Arrived");
      console.log("[ChairSide] Doctor Arrived succeeded.", { roomNumber: app.roomNumber });
      setRoomActionStatus("Doctor arrived.", "success");
    } catch (error) {
      console.error("[ChairSide] Doctor Arrived failed.", { roomNumber: app.roomNumber, error });
      setRoomActionStatus(error.message || "Failed to mark doctor arrived.", "error");
    }
  });

  doctorCompleteButton.addEventListener("click", async () => {
    if (!isConfiguredRoom(app.roomNumber)) {
      setRoomActionStatus("This room is not configured.", "error");
      return;
    }

    if (!isRoomInState("doctor-in-room")) {
      setRoomActionStatus("Doctor Complete is only available when the doctor is in the room.", "error");
      return;
    }

    console.log("[ChairSide] Doctor Complete clicked.", { roomNumber: app.roomNumber });
    setRoomActionStatus("Marking doctor complete...", "pending");

    try {
      await sendRoomAction(app.roomNumber, "doctor-complete", "Doctor Complete");
      console.log("[ChairSide] Doctor Complete succeeded.", { roomNumber: app.roomNumber });
      setRoomActionStatus("Doctor complete. Turnover started.", "success");
    } catch (error) {
      console.error("[ChairSide] Doctor Complete failed.", { roomNumber: app.roomNumber, error });
      setRoomActionStatus(error.message || "Failed to mark doctor complete.", "error");
    }
  });

  roomAvailableButton.addEventListener("click", async () => {
    if (!isConfiguredRoom(app.roomNumber)) {
      setRoomActionStatus("This room is not configured.", "error");
      return;
    }

    if (!isRoomInState("turnover")) {
      setRoomActionStatus("Room Available is only available during turnover.", "error");
      return;
    }

    console.log("[ChairSide] Room Available clicked.", { roomNumber: app.roomNumber });
    setRoomActionStatus("Marking room available...", "pending");

    try {
      await sendRoomAction(app.roomNumber, "available", "Room Available");
      console.log("[ChairSide] Room Available succeeded.", { roomNumber: app.roomNumber });
      setRoomActionStatus("Room available.", "success");
    } catch (error) {
      console.error("[ChairSide] Room Available failed.", { roomNumber: app.roomNumber, error });
      setRoomActionStatus(error.message || "Failed to mark room available.", "error");
    }
  });
}

function wireSelectionTiles() {
  const doctorTiles = document.getElementById("doctorTiles");
  const procedureTiles = document.getElementById("procedureTiles");

  doctorTiles?.addEventListener("click", event => {
    const button = event.target.closest("[data-doctor-id]");
    if (!button || button.disabled) {
      return;
    }

    app.selectedDoctorId = button.dataset.doctorId;
    renderSelectionTiles(getCurrentRoom());
  });

  procedureTiles?.addEventListener("click", event => {
    const button = event.target.closest("[data-procedure-id]");
    if (!button || button.disabled) {
      return;
    }

    app.selectedProcedureId = button.dataset.procedureId;
    renderSelectionTiles(getCurrentRoom());
  });
}

function hasAssignmentSelection() {
  return Boolean(app.selectedDoctorId && app.selectedProcedureId);
}

function isConfiguredRoom(roomNumber) {
  return app.snapshot.rooms.some(room => getRoomId(room) === roomNumber);
}

function getCurrentRoom() {
  return app.snapshot?.rooms.find(room => getRoomId(room) === app.roomNumber) || null;
}

function currentRoomState() {
  const room = getCurrentRoom();
  return room ? normalizeState(room) : "empty";
}

function isRoomInState(state) {
  return currentRoomState() === state;
}

async function sendSeatRoom(payload) {
  console.log("[ChairSide] Sending Seat Room payload.", payload);
  const response = await fetch(`/api/rooms/${payload.roomNumber}/seat`, {
    method: "POST",
    headers: mutationHeaders({ "Content-Type": "application/json" }),
    body: JSON.stringify({
      doctorId: payload.doctorId,
      procedureCode: payload.procedureCode,
      procedureId: payload.procedureId,
      demoElapsedMinutes: payload.demoElapsedMinutes
    })
  });

  if (!response.ok) {
    throw new Error(await readErrorMessage(response, `Seat Room failed with HTTP ${response.status}.`));
  }

  return response.json();
}

async function sendAssignmentUpdate(payload) {
  console.log("[ChairSide] Sending Update Assignment payload.", payload);
  const response = await fetch(`/api/rooms/${payload.roomNumber}/assignment`, {
    method: "POST",
    headers: mutationHeaders({ "Content-Type": "application/json" }),
    body: JSON.stringify({
      doctorId: payload.doctorId,
      procedureCode: payload.procedureCode,
      procedureId: payload.procedureId
    })
  });

  if (!response.ok) {
    throw new Error(await readErrorMessage(response, `Update Assignment failed with HTTP ${response.status}.`));
  }

  return response.json();
}

async function sendRoomAction(roomNumber, action, label) {
  const payload = { roomNumber, action };
  console.log(`[ChairSide] Sending ${label} payload.`, payload);
  const response = await fetch(`/api/rooms/${roomNumber}/${action}`, {
    method: "POST",
    headers: mutationHeaders()
  });

  if (!response.ok) {
    throw new Error(await readErrorMessage(response, `${label} failed with HTTP ${response.status}.`));
  }

  return response.json();
}

function mutationHeaders(baseHeaders = {}) {
  const headers = { ...baseHeaders };
  if (app.roomToken) {
    headers["X-ChairSide-Room-Token"] = app.roomToken;
  }

  return headers;
}

async function readErrorMessage(response, fallback) {
  const message = await response.text();
  return message || fallback;
}

function setRoomActionStatus(message, tone) {
  const target = document.getElementById("roomActionStatus");
  if (!target) {
    return;
  }

  target.textContent = message;
  target.dataset.tone = tone;
}

function elapsedSeconds(seatedAt) {
  return Math.max(0, Math.floor((boardNowMs() - Date.parse(seatedAt)) / 1000));
}

function secondsBetweenDates(start, end) {
  return Math.max(0, Math.round((Date.parse(end) - Date.parse(start)) / 1000));
}

function formatElapsed(seatedAt) {
  const totalSeconds = elapsedSeconds(seatedAt);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
}

function formatDuration(totalSeconds) {
  const rounded = Math.max(0, Math.round(Number(totalSeconds) || 0));
  const minutes = Math.floor(rounded / 60);
  const seconds = rounded % 60;
  return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
}

function formatDateTime(value) {
  if (!value) {
    return "--";
  }

  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  }).format(new Date(value));
}

boot();
