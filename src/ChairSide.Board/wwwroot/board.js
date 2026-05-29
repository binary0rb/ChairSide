const app = {
  snapshot: null,
  reports: null,
  connection: null,
  hubReady: false,
  tickHandle: null,
  roomNumber: getRoomNumber(),
  doctorId: new URLSearchParams(location.search).get("doctor") || "otte"
};

const stateNames = ["empty", "seated", "aging", "stale", "doctor-in-room", "turnover"];
const activeSeatedStates = new Set(["seated", "aging", "stale"]);

function getRoomNumber() {
  const query = new URLSearchParams(location.search);
  const requestedRoom = document.body.dataset.roomNumber || query.get("roomId") || query.get("room") || "1";
  const roomNumber = Number(requestedRoom);

  return Number.isInteger(roomNumber) ? roomNumber : 0;
}

async function boot() {
  await loadBoard();
  if (document.body.dataset.view === "reports") {
    await loadReports();
  }

  connectRealtime();
  app.tickHandle = window.setInterval(render, 1000);

  if (document.body.dataset.view === "room") {
    wireRoomPanel();
  }
}

async function loadBoard() {
  const response = await fetch("/api/board");
  app.snapshot = await response.json();
  render();
}

async function loadReports() {
  const response = await fetch("/api/reports");
  app.reports = await response.json();
  render();
}

function connectRealtime() {
  if (!window.signalR) {
    window.setInterval(loadBoard, 5000);
    return;
  }

  const connection = new signalR.HubConnectionBuilder()
    .withUrl("/boardHub")
    .withAutomaticReconnect()
    .build();

  app.connection = connection;

  connection.on("boardUpdated", snapshot => {
    app.snapshot = snapshot;
    if (document.body.dataset.view === "reports") {
      loadReports();
    }
    render();
  });

  connection.start()
    .then(() => {
      app.hubReady = true;
    })
    .catch(() => window.setInterval(loadBoard, 5000));
}

function render() {
  if (!app.snapshot) {
    return;
  }

  const view = document.body.dataset.view;
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
        <span>${renderProcedureIcon(procedure.icon)}</span>${procedure.label}
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
  populateSelects();
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
      <td>${cycle.procedureCode}</td>
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
  const timerLabel = seatedToDoctorLabel(room);

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
        <span>${room.doctor ? room.doctor.name : "Unassigned"}</span>
        <time>${timerLabel}</time>
      </div>
    </article>
  `;
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

  return raw;
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
    return "--:--";
  }

  if (room.doctorArrivedAt) {
    return `To doctor ${formatDuration(secondsBetweenDates(room.seatedAt, room.doctorArrivedAt))}`;
  }

  return `Waiting ${formatElapsed(room.seatedAt)}`;
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
  setDisabled("doctorSelect", !isEnabled || state !== "empty");
  setDisabled("procedureSelect", !isEnabled || state !== "empty");
  setDisabled("demoElapsedSelect", !isEnabled || state !== "empty");
  setDisabled("seatButton", !isEnabled || state !== "empty");
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

function renderProcedureIcon(icon) {
  const icons = {
    speech: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 5h14v10H8l-3 3V5z"/></svg>`,
    forceps: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 3l5 8m5-8l-5 8M9 21l3-10 3 10M6 7h12"/></svg>`,
    moon: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M18 16.5A8 8 0 0 1 8 6a8 8 0 1 0 10 10.5z"/></svg>`,
    check: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 5h14v14H5z"/><path d="M8 12l3 3 5-6"/></svg>`,
    bolt: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M13 2L5 14h6l-1 8 9-13h-6l0-7z"/></svg>`,
    vial: `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M9 3h6M10 3v6l-4 8a3 3 0 0 0 3 4h6a3 3 0 0 0 3-4l-4-8V3M8 16h8"/></svg>`
  };

  return icons[icon] || renderEmptyIcon();
}

function renderEmptyIcon() {
  return `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12h14"/></svg>`;
}

function populateSelects() {
  const doctorSelect = document.getElementById("doctorSelect");
  const procedureSelect = document.getElementById("procedureSelect");

  if (doctorSelect && !doctorSelect.options.length) {
    doctorSelect.innerHTML = app.snapshot.doctors
      .map(doctor => `<option value="${doctor.id}">${doctor.name}</option>`)
      .join("");
  }

  if (procedureSelect && !procedureSelect.options.length) {
    procedureSelect.innerHTML = app.snapshot.procedures
      .map(procedure => `<option value="${procedure.id}">${procedure.label} - ${procedure.name}</option>`)
      .join("");
  }

  populateDemoTimerSelect();
}

function populateDemoTimerSelect() {
  const demoElapsedSelect = document.getElementById("demoElapsedSelect");
  if (!demoElapsedSelect || demoElapsedSelect.options.length) {
    return;
  }

  const options = [`<option value="0">Start now</option>`];
  const agingMinutes = thresholdMinutes(app.snapshot.agingThreshold);
  const staleMinutes = thresholdMinutes(app.snapshot.staleThreshold);
  if (agingMinutes !== null) {
    options.push(`<option value="${Math.ceil(agingMinutes) + 1}">Simulate aging wait</option>`);
  }

  if (staleMinutes !== null) {
    options.push(`<option value="${Math.ceil(staleMinutes) + 1}">Simulate stale wait</option>`);
  }

  demoElapsedSelect.innerHTML = options.join("");
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
  const doctorArrivedButton = document.getElementById("doctorArrivedButton");
  const doctorCompleteButton = document.getElementById("doctorCompleteButton");
  const roomAvailableButton = document.getElementById("roomAvailableButton");

  if (!seatButton || !doctorArrivedButton || !doctorCompleteButton || !roomAvailableButton) {
    console.error("[ChairSide] Room panel buttons were not found.", {
      seatButton,
      doctorArrivedButton,
      doctorCompleteButton,
      roomAvailableButton
    });
    return;
  }

  console.log("[ChairSide] Room panel click handlers bound.", { roomNumber: app.roomNumber });

  seatButton.addEventListener("click", async () => {
    if (!isConfiguredRoom(app.roomNumber)) {
      setRoomActionStatus("This room is not configured.", "error");
      return;
    }

    if (!isRoomInState("empty")) {
      setRoomActionStatus("Seat Room is only available when the room is available.", "error");
      return;
    }

    const doctorId = document.getElementById("doctorSelect").value;
    const procedureCode = document.getElementById("procedureSelect").value;
    const demoElapsedMinutes = Number(document.getElementById("demoElapsedSelect")?.value || "0");
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
    headers: { "Content-Type": "application/json" },
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

async function sendRoomAction(roomNumber, action, label) {
  const payload = { roomNumber, action };
  console.log(`[ChairSide] Sending ${label} payload.`, payload);
  const response = await fetch(`/api/rooms/${roomNumber}/${action}`, { method: "POST" });

  if (!response.ok) {
    throw new Error(await readErrorMessage(response, `${label} failed with HTTP ${response.status}.`));
  }

  return response.json();
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
  return Math.max(0, Math.floor((Date.now() - Date.parse(seatedAt)) / 1000));
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
