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
  reportsInFlight: false,
  roomNumber: getRoomNumber(),
  roomToken: getRoomToken(),
  roomTokenPromptVisible: false,
  doctorId: new URLSearchParams(location.search).get("doctor"),
  selectedDoctorId: null,
  selectedProcedureId: null,
  selectionContext: null
};

const stateNames = ["empty", "seated", "aging", "stale", "ready-for-doctor", "doctor-in-room", "turnover"];
// States where "Ready for Doctor" button is enabled (only the neutral In Prep state).
const activeSeatedStates = new Set(["seated"]);
// States where corrections (cancel/update) are available — all states before Doctor Arrived.
const cancelableStates = new Set(["seated", "ready-for-doctor", "aging", "stale"]);
// States where "Doctor Arrived" is enabled — all ready-for-doctor phase states.
const doctorArrivedStates = new Set(["ready-for-doctor", "aging", "stale"]);
const staffLoungeRoomNumber = 99;
const adminAccess = {
  storageKey: "chairside-admin-token",
  headerName: "X-ChairSide-Admin-Token"
};

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

  return document.querySelector("meta[name='chairside-room-token']")?.content || getStoredRoomToken();
}

async function boot() {
  // Guard: intervals must never be registered more than once.
  // boot() is called once at page load, but this explicit check prevents a
  // future accidental double-boot from leaking orphaned intervals.
  if (app.pollHandle || app.tickHandle || app.statusHandle) {
    console.error("[ChairSide] boot() called more than once — skipping duplicate interval registration.");
    return;
  }

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
  if (app.reportsInFlight) {
    return;
  }

  app.reportsInFlight = true;
  try {
    const response = await fetch("/api/reports", {
      cache: "no-store",
      headers: adminRequestHeaders()
    });

    if (response.status === 401 || response.status === 403) {
      if (response.status === 403) {
        sessionStorage.removeItem(adminAccess.storageKey);
      }

      app.reports = null;
      renderReportsAccessPrompt(response.status);
      return;
    }

    if (!response.ok) {
      throw new Error(`Reports failed with HTTP ${response.status}.`);
    }

    app.reports = await response.json();
    render();
  } finally {
    app.reportsInFlight = false;
  }
}

function connectRealtime() {
  if (!window.signalR) {
    markRealtimeDegraded();
    updateConnectionStatus();
    return;
  }

  if (app.hubReady || app.connection?.state === "Connected" || app.connection?.state === "Connecting" || app.connection?.state === "Reconnecting") {
    return;
  }

  const connection = new signalR.HubConnectionBuilder()
    .withUrl("/boardHub")
    .withAutomaticReconnect()
    .build();

  app.connection = connection;

  connection.on("boardUpdated", async snapshot => {
    applySnapshot(snapshot);
    if (document.body.dataset.view === "reports") {
      await loadReports().catch(error => {
        console.warn("[ChairSide] Reports refresh after board update failed.", error);
      });
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

const connectionStatusDescriptions = {
  live: "Board is current. Updates are being received through realtime connection or fresh polling fallback.",
  reconnecting: "Realtime connection is degraded. ChairSide is trying to reconnect.",
  stale: "No fresh board update in over 15 seconds. Refresh or check the network/server."
};

function formatSnapshotAge(ageMs) {
  if (ageMs < 1000) {
    return `${Math.round(ageMs)} ms ago`;
  }

  return `${(ageMs / 1000).toFixed(1)} seconds ago`;
}

function getConnectionStatusDetails(status) {
  const description = connectionStatusDescriptions[status] || "";
  if (!app.lastSnapshotAt) {
    return `${description}\n\nLast updated: never\nAge: unavailable`;
  }

  const ageMs = Math.max(0, Date.now() - app.lastSnapshotAt);
  const lastUpdated = new Date(app.lastSnapshotAt).toLocaleTimeString([], {
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit"
  });

  return `${description}\n\nLast updated: ${lastUpdated}\nAge: ${formatSnapshotAge(ageMs)}`;
}

function setConnectionStatus(status) {
  app.connectionStatus = status;
  const target = ensureConnectionStatusIndicator();
  const label = status === "live" ? "Live" : status === "reconnecting" ? "Reconnecting" : "Stale";
  const details = getConnectionStatusDetails(status);
  target.className = `connection-status ${status}`;
  target.title = details;
  target.setAttribute("aria-label", `${label}: ${details}`);
  target.querySelector("span").textContent = label;
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
        <i style="--doctor-color: ${escapeAttribute(doctor.color)}"></i>${escapeHtml(doctor.name)}
      </span>
    `).join("");
  }

  const procedureTarget = document.getElementById("procedureLegend");
  if (procedureTarget) {
    procedureTarget.innerHTML = app.snapshot.procedures.map(procedure => `
      <span class="procedure-chip">
        <span>${renderProcedureIcon(procedure.icon)}</span>
        <strong>${escapeHtml(procedure.code)}</strong>
        <small>${escapeHtml(procedure.label)}</small>
      </span>
    `).join("");
  }

  const agingLabel = document.getElementById("agingLegendLabel");
  if (agingLabel) {
    const agingMinutes = getAgingMinutes();
    if (agingMinutes !== null) {
      agingLabel.innerHTML = `<i class="state-dot aging"></i> Aging: doctor requested &gt; ${Math.round(agingMinutes)} min`;
    }
  }

  const staleLabel = document.getElementById("staleLegendLabel");
  if (staleLabel) {
    const staleMinutes = getStaleMinutes();
    if (staleMinutes !== null) {
      staleLabel.innerHTML = `<i class="state-dot stale"></i> Stale: doctor requested &gt; ${Math.round(staleMinutes)} min`;
    }
  }
}

function renderMaster() {
  const target = document.getElementById("roomGrid");
  target.innerHTML = app.snapshot.rooms.map(room => {
    const roomId = getRoomId(room);
    return `<a class="room-tile-link" href="/room.html?roomId=${escapeAttribute(String(roomId))}" aria-label="Open Room ${escapeHtml(String(roomId))} controls">${renderRoomTile(room)}</a>`;
  }).join("");
  target.setAttribute("aria-label", `${getRoomCount()} room cards`);
}

function renderRoomPanel() {
  if (isStaffLoungeRoom()) {
    renderStaffLoungePanel();
    return;
  }

  const title = document.getElementById("roomPanelTitle");
  const status = document.getElementById("roomPanelStatus");
  const room = app.snapshot.rooms.find(item => getRoomId(item) === app.roomNumber);

  title.textContent = `Room ${app.roomNumber}`;
  status.innerHTML = room ? renderRoomTile(room, true) : renderInvalidRoomMessage();
  syncRoomSelection(room);
  renderSelectionTiles(room);
  renderRoomTokenPrompt();
  applyDemoTimerVisibility();
  populateDemoTimerSelect();
  setRoomControlsEnabled(room);
}

function isStaffLoungeRoom() {
  return document.body.dataset.view === "room" && app.roomNumber === staffLoungeRoomNumber;
}

function renderStaffLoungePanel() {
  const title = document.getElementById("roomPanelTitle");
  const shell = document.querySelector(".panel-shell");
  if (title) {
    title.textContent = "Room 99";
  }

  if (!shell || shell.dataset.staffLounge === "true") {
    return;
  }

  shell.dataset.staffLounge = "true";
  shell.classList.add("staff-lounge-shell");
  shell.innerHTML = `
    <section class="staff-lounge-card" aria-labelledby="staffLoungeTitle">
      <div class="room-topline">
        <strong id="staffLoungeTitle">Room 99</strong>
        <span>STAFF LOUNGE</span>
      </div>
      <div class="staff-lounge-mark" aria-hidden="true">99</div>
      <p class="staff-lounge-subtitle">Staff Lounge</p>
      <ul class="staff-lounge-lines">
        <li><strong>Containment level:</strong> snack-adjacent</li>
        <li><strong>Snack condition:</strong> hydraulically unstable</li>
        <li>No PHI. No dignity was harmed in this simulation.</li>
      </ul>
      <div class="action-status" id="staffLoungeStatus" role="status" aria-live="polite" data-tone="pending">
        Awaiting lounge protocol.
      </div>
    </section>
    <section class="staff-lounge-actions" aria-labelledby="staffLoungeActionsTitle">
      <span class="control-label primary-workflow-label" id="staffLoungeActionsTitle">Local lounge controls</span>
      <div class="staff-lounge-action-grid">
        <button class="secondary-button" type="button" data-staff-lounge-response="Towel deployed. Morale partially restored.">Deploy towel</button>
        <button class="secondary-button" type="button" data-staff-lounge-response="Suction unavailable. Try emotional support.">Summon suction</button>
        <button class="secondary-button" type="button" data-staff-lounge-response="Basin offered. Crisis downgraded.">Offer emesis basin</button>
        <button class="secondary-button" type="button" data-staff-lounge-response="Dignity reset attempted. Results pending.">Reset dignity</button>
        <button class="primary-button" id="returnToCivilizationButton" type="button">Return to civilization</button>
      </div>
    </section>
  `;
  wireStaffLoungePanel(shell);
}

function wireStaffLoungePanel(shell) {
  const status = document.getElementById("staffLoungeStatus");
  shell.querySelectorAll("[data-staff-lounge-response]").forEach(button => {
    button.addEventListener("click", () => {
      if (status) {
        status.textContent = button.dataset.staffLoungeResponse;
        status.dataset.tone = "success";
      }
    });
  });

  document.getElementById("returnToCivilizationButton")?.addEventListener("click", () => {
    window.location.href = "/master.html";
  });
}

function renderDoctorView() {
  const doctor = app.doctorId
    ? app.snapshot.doctors.find(item => item.id === app.doctorId)
    : app.snapshot.doctors[0];
  const title = document.getElementById("doctorViewTitle");
  const target = document.getElementById("doctorRoomList");

  if (!doctor) {
    const message = app.doctorId
      ? `Doctor "${app.doctorId}" was not found.`
      : "No doctors are configured for this board.";

    title.textContent = "Doctor View";
    target.innerHTML = `<div class="empty-message">${escapeHtml(message)}</div>`;
    return;
  }

  const rooms = app.snapshot.rooms.filter(room => room.assignedDoctor === doctor.id || (room.doctor && room.doctor.id === doctor.id));

  title.textContent = doctor.name;
  document.documentElement.style.setProperty("--active-doctor", doctor.color);

  target.innerHTML = rooms.length
    ? rooms.map(room => renderRoomTile(room)).join("")
    : `<div class="empty-message">No active rooms for ${escapeHtml(doctor.name)}.</div>`;
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
    renderMetric("Exceptions Requiring Review", (app.reports.exceptionCycles || []).length),
    renderMetric("Avg Prep Time", formatDuration(app.reports.averagePrepSeconds)),
    renderMetric("Median Prep Time", formatDuration(app.reports.medianPrepSeconds)),
    renderMetric("Avg Ready-to-Doctor", formatDuration(app.reports.averageReadyToDoctorSeconds)),
    renderMetric("Median Ready-to-Doctor", formatDuration(app.reports.medianReadyToDoctorSeconds)),
    renderMetric("Avg Total to Doctor", formatDuration(app.reports.averageSeatedToDoctorSeconds)),
    renderMetric("Median Total to Doctor", formatDuration(app.reports.medianSeatedToDoctorSeconds)),
    renderMetric("Avg In Room", formatDuration(app.reports.averageDoctorInRoomSeconds)),
    renderMetric("Median In Room", formatDuration(app.reports.medianDoctorInRoomSeconds)),
    renderMetric("Avg Turnover", formatDuration(app.reports.averageTurnoverSeconds)),
    renderMetric("Median Turnover", formatDuration(app.reports.medianTurnoverSeconds)),
    renderMetric("Aging Events", app.reports.agingEventCount),
    renderMetric("Stale Events", app.reports.staleEventCount)
  ].join("");

  body.innerHTML = cycles.length
    ? cycles.map(renderCycleRow).join("")
    : `<tr><td colspan="17">No completed room cycles yet.</td></tr>`;

  renderExceptionCycles(app.reports.exceptionCycles || []);
}

function renderExceptionCycles(exceptions) {
  const body = document.getElementById("exceptionCyclesBody");
  if (!body) {
    return;
  }

  body.innerHTML = exceptions.length
    ? exceptions.map(renderExceptionRow).join("")
    : `<tr><td colspan="11">No exceptions requiring review.</td></tr>`;
}

function renderExceptionRow(cycle) {
  const doctor = doctorName(cycle.assignedDoctor);
  return `
    <tr>
      <td>${formatDateTime(cycle.seatedAt)}</td>
      <td>Room ${cycle.roomId}</td>
      <td>${escapeHtml(doctor)}</td>
      <td>${renderProcedureBadge(cycle.procedureCode)}</td>
      <td>${formatDateTime(cycle.doctorArrivedAt)}</td>
      <td>${formatDateTime(cycle.doctorCompleteAt)}</td>
      <td>${formatDateTime(cycle.roomAvailableAt)}</td>
      <td>${escapeHtml(String(cycle.finalWaitState || "--").toUpperCase())}</td>
      <td>${escapeHtml(cycle.exceptionReason || "--")}</td>
      <td>${escapeHtml(cycle.suggestedAction || "--")}</td>
      <td>${escapeHtml(cycle.reviewStatus || "--")}</td>
    </tr>
  `;
}

function renderReportsAccessPrompt(statusCode) {
  const summary = document.getElementById("reportSummary");
  const body = document.getElementById("completedCyclesBody");
  if (!summary || !body) {
    return;
  }

  const message = statusCode === 403
    ? "The saved reports token was rejected. Enter the current internal reports token."
    : "Reports access is required for this internal page.";

  summary.innerHTML = `
    <article class="report-access-panel">
      <h2>Reports Access</h2>
      <p>${escapeHtml(message)}</p>
      <form id="reportAccessForm" class="report-access-form">
        <label for="reportAccessToken">Reports token</label>
        <input id="reportAccessToken" name="reportAccessToken" type="password" autocomplete="off" required>
        <button type="submit" class="primary-button">Load Reports</button>
      </form>
      <button type="button" class="secondary-button utility-button" id="clearReportAccessToken">Clear Saved Token</button>
    </article>
  `;
  body.innerHTML = `<tr><td colspan="14">Reports are protected. Enter an internal reports token to continue.</td></tr>`;
  wireReportsAccessPrompt();
}

function renderRoomTokenPrompt() {
  const target = document.getElementById("roomTokenPrompt");
  if (!target) {
    return;
  }

  const hasSavedToken = Boolean(getStoredRoomToken());
  if (!app.roomTokenPromptVisible) {
    target.hidden = true;
    target.innerHTML = "";
    return;
  }

  target.hidden = false;
  target.innerHTML = `
    <h2>Room access token required</h2>
    <p>Enter the room token for Room ${escapeHtml(app.roomNumber)} on this tablet.</p>
    <form id="roomTokenForm" class="room-token-form">
      <label for="roomAccessToken">Room token</label>
      <input id="roomAccessToken" name="roomAccessToken" type="password" autocomplete="off" required>
      <button type="submit" class="primary-button utility-button">Load/Save Token</button>
    </form>
    <button type="button" class="secondary-button utility-button" id="clearRoomTokenButton" ${hasSavedToken ? "" : "disabled"}>Clear Token</button>
  `;
  wireRoomTokenPrompt();
}

function wireRoomTokenPrompt() {
  const form = document.getElementById("roomTokenForm");
  const clearButton = document.getElementById("clearRoomTokenButton");

  form?.addEventListener("submit", event => {
    event.preventDefault();
    const input = document.getElementById("roomAccessToken");
    const token = input?.value.trim() || "";
    if (!token) {
      return;
    }

    saveRoomToken(token);
    app.roomTokenPromptVisible = false;
    renderRoomTokenPrompt();
    setRoomActionStatus("Room token saved. Try the action again.", "success");
  });

  clearButton?.addEventListener("click", () => {
    clearStoredRoomToken();
    app.roomToken = "";
    app.roomTokenPromptVisible = true;
    renderRoomTokenPrompt();
    setRoomActionStatus("Room token cleared.", "pending");
  });
}

function showRoomTokenPrompt(statusCode) {
  if (statusCode === 403) {
    clearStoredRoomToken();
    app.roomToken = "";
  }

  app.roomTokenPromptVisible = true;
  renderRoomTokenPrompt();
  setRoomActionStatus(
    statusCode === 403
      ? "Room token was rejected. Enter the current room access token."
      : "Room access token required.",
    "error");
}

function roomTokenStorageKey(roomNumber = getRoomNumber()) {
  return `chairside-room-token-${roomNumber}`;
}

function getStoredRoomToken() {
  if (document.body.dataset.view !== "room") {
    return "";
  }

  return sessionStorage.getItem(roomTokenStorageKey()) || "";
}

function saveRoomToken(token) {
  sessionStorage.setItem(roomTokenStorageKey(), token);
  app.roomToken = token;
}

function clearStoredRoomToken() {
  sessionStorage.removeItem(roomTokenStorageKey());
}

function wireReportsAccessPrompt() {
  const form = document.getElementById("reportAccessForm");
  const clearButton = document.getElementById("clearReportAccessToken");

  form?.addEventListener("submit", event => {
    event.preventDefault();
    const input = document.getElementById("reportAccessToken");
    const token = input?.value.trim() || "";
    if (!token) {
      return;
    }

    sessionStorage.setItem(adminAccess.storageKey, token);
    loadReports();
  });

  clearButton?.addEventListener("click", () => {
    sessionStorage.removeItem(adminAccess.storageKey);
    renderReportsAccessPrompt(401);
  });
}

function adminRequestHeaders() {
  const token = sessionStorage.getItem(adminAccess.storageKey);
  return token ? { [adminAccess.headerName]: token } : {};
}

function renderMetric(label, value) {
  return `
    <article class="metric-card">
      <span>${escapeHtml(label)}</span>
      <strong>${escapeHtml(value)}</strong>
    </article>
  `;
}

function renderCycleRow(cycle) {
  const doctor = doctorName(cycle.assignedDoctor);
  return `
    <tr>
      <td>Room ${cycle.roomId}</td>
      <td>${escapeHtml(doctor)}</td>
      <td>${renderProcedureBadge(cycle.procedureCode)}</td>
      <td>${formatDateTime(cycle.seatedAt)}</td>
      <td>${formatDateTime(cycle.readyForDoctorAt)}</td>
      <td>${formatDateTime(cycle.doctorArrivedAt)}</td>
      <td>${formatDateTime(cycle.doctorCompleteAt)}</td>
      <td>${formatDateTime(cycle.roomAvailableAt)}</td>
      <td>${formatDuration(cycle.prepSeconds)}</td>
      <td>${formatDuration(cycle.readyToDoctorSeconds)}</td>
      <td>${formatDuration(cycle.seatedToDoctorSeconds)}</td>
      <td>${formatDuration(cycle.doctorInRoomSeconds)}</td>
      <td>${formatDuration(cycle.turnoverSeconds)}</td>
      <td>${formatDuration(cycle.totalRoomCycleSeconds)}</td>
      <td>${escapeHtml(String(cycle.finalWaitState || "--").toUpperCase())}</td>
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
  const timer = roomTimerLabel(room);
  const fullDoctorName = room.doctor ? room.doctor.name : "Unassigned";
  const doctorDisplayName = large ? fullDoctorName : (room.doctor?.shortName || cardDoctorName(fullDoctorName));

  return `
    <article class="room-tile ${state} ${large ? "large" : ""}" style="--doctor-color: ${escapeAttribute(doctorColor)}">
      <div class="room-topline">
        <strong>Room ${roomId}</strong>
        <span>${badge}</span>
      </div>
      <div class="procedure-lockup">
        ${procedure ? renderProcedureIcon(procedure.icon) : renderEmptyIcon()}
        <span>${procedure ? escapeHtml(procedure.code) : "OPEN"}</span>
      </div>
      <div class="room-footer">
        <span class="room-doctor-name" title="${escapeAttribute(fullDoctorName)}">${escapeHtml(doctorDisplayName)}</span>
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

  // aging and stale are server-authoritative states in the Ready for Doctor phase.
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
      const elapsedMinutes = Math.max(0, (boardNowMs() - readyAtMs) / 60000);
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
  if (state === "seated") {
    return "IN PREP";
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
  if (!room.seatedAt) {
    return { label: "Available", value: "--:--" };
  }

  return {
    label: "Room time",
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
  const canCorrect = cancelableStates.has(state);
  const isPrep = activeSeatedStates.has(state); // only "seated" — not aging/stale anymore
  setDisabled("demoElapsedSelect", !isEnabled || state !== "empty" || !isDemoTimerEnabled());
  setDisabled("seatButton", !isEnabled || state !== "empty");
  setDisabled("readyForDoctorButton", !isEnabled || !isPrep);
  setDisabled("updateAssignmentButton", !isEnabled || !canCorrect);
  setDisabled("cancelSeatingButton", !isEnabled || !canCorrect);
  setDisabled("doctorArrivedButton", !isEnabled || !doctorArrivedStates.has(state));
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
    procedure.code === procedureCode || procedure.id === procedureCode
  ) || null;
}

function renderProcedureBadge(procedureCode) {
  const procedure = procedureFromCode(procedureCode);
  if (!procedure) {
    return escapeHtml(procedureCode || "--");
  }

  return `
    <span class="procedure-badge">
      ${renderProcedureIcon(procedure.icon)}
      <span>
        <strong>${escapeHtml(procedure.code)}</strong>
        <small>${escapeHtml(procedure.label)}</small>
      </span>
    </span>
  `;
}

function renderProcedureIcon(icon) {
  const icons = {
    speech: `<svg class="procedure-icon" viewBox="0 0 24 24" aria-hidden="true"><path class="icon-stroke" d="M5 5.5h14v10H9l-4 3v-13z"/><path class="icon-stroke" d="M8.5 9h7M8.5 12h5"/></svg>`,
    forceps: `<svg class="procedure-icon tooth-icon" viewBox="0 0 24 24" aria-hidden="true"><path class="icon-stroke" d="M12 5.5C10.9 4.8 9.5 4 8 4 5.7 4 4 5.7 4 9.2 4 14 6.3 21 8.4 21c1.2 0 1.5-3.2 3.6-3.2S14.4 21 15.6 21C17.7 21 20 14 20 9.2 20 5.7 18.3 4 16 4c-1.5 0-2.9.8-4 1.5z"/><path class="icon-stroke" d="M10 8.2h4"/></svg>`,
    moon: `<svg class="procedure-icon" viewBox="0 0 24 24" aria-hidden="true"><path class="icon-stroke" d="M18.8 15.2A7.5 7.5 0 0 1 8.8 5.2 8.4 8.4 0 1 0 18.8 15.2z"/></svg>`,
    check: `<svg class="procedure-icon" viewBox="0 0 24 24" aria-hidden="true"><path class="icon-stroke" d="M5 5h14v14H5z"/><path class="icon-stroke check-mark" d="M8.2 12.5l3 3 5-6"/></svg>`,
    bolt: `<svg class="procedure-icon implant-icon" viewBox="0 0 24 24" aria-hidden="true"><path class="icon-stroke" d="M9 3.5h6M10 6h4v4l-1.4 1.4v7.1L12 20.5l-.6-2v-7.1L10 10V6z"/><path class="icon-stroke" d="M8.5 12h7M8.8 14.3h6.4M9.2 16.6h5.6"/></svg>`,
    vial: `<svg class="procedure-icon" viewBox="0 0 24 24" aria-hidden="true"><path class="icon-stroke" d="M9 3.5h6M10 3.5v5.8l-4.1 7.9A2.6 2.6 0 0 0 8.2 21h7.6a2.6 2.6 0 0 0 2.3-3.8L14 9.3V3.5"/><path class="icon-stroke" d="M7.4 16h9.2"/></svg>`,
    teeth: `<svg class="procedure-icon" viewBox="0 0 24 24" aria-hidden="true"><path class="icon-stroke" d="M4 12Q12 8 20 12v6H4z"/><path class="icon-stroke" d="M8 12v6M12 12v6M16 12v6"/></svg>`,
    sync: `<svg class="procedure-icon" viewBox="0 0 24 24" aria-hidden="true"><path class="icon-stroke" d="M17.7 6.3A8 8 0 1 0 20 12"/><path class="icon-stroke" d="M20 7v5h-5"/></svg>`,
    interlock: `<img class="procedure-icon" src="/assets/icons/interlock.png" alt="" aria-hidden="true">`,
    wrench: `<svg class="procedure-icon" viewBox="0 0 24 24" aria-hidden="true"><path class="icon-stroke" d="M14.7 6.3a4 4 0 0 1-5.4 5.4L4 17l3 3 5.3-5.3a4 4 0 0 1 5.4-5.4l-2.3 2.3 1.4 1.4 2.3-2.3z"/></svg>`,
    phone: `<svg class="procedure-icon" viewBox="0 0 24 24" aria-hidden="true"><path class="icon-stroke" d="M6.5 10.5c1.5 3 4 5.5 7 7l2-2a1 1 0 0 1 1.1-.2c1 .5 2.1.8 3.2.9a1 1 0 0 1 .9 1v3a1 1 0 0 1-1 1C10 22 2 14 2 4.3a1 1 0 0 1 1-1H6a1 1 0 0 1 1 .9c.1 1.1.4 2.2.9 3.2a1 1 0 0 1-.2 1.1L6.5 10.5z"/></svg>`
  };

  return icons[icon] || renderEmptyIcon();
}

function renderEmptyIcon() {
  return `<svg class="procedure-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12h14"/></svg>`;
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

function escapeAttribute(value) {
  return escapeHtml(value);
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
  app.selectedProcedureId = procedure?.code || null;
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
      style="--doctor-color: ${escapeAttribute(doctor.color)}"
      type="button"
      role="radio"
      aria-checked="${doctor.id === app.selectedDoctorId}"
      data-doctor-id="${escapeAttribute(doctor.id)}"
      ${isEnabled ? "" : "disabled"}>
      <span class="doctor-color-swatch"></span>
      <span class="selection-copy">
        <strong>${escapeHtml(doctor.name)}</strong>
      </span>
      ${doctor.id === app.selectedDoctorId ? `<span class="selected-indicator" aria-hidden="true">&#10003;</span>` : ""}
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
      class="selection-tile procedure-tile ${procedure.code === app.selectedProcedureId ? "selected" : ""}"
      type="button"
      role="radio"
      aria-checked="${procedure.code === app.selectedProcedureId}"
      data-procedure-id="${escapeAttribute(procedure.code)}"
      ${isEnabled ? "" : "disabled"}>
      ${renderProcedureIcon(procedure.icon)}
      <span class="selection-copy">
        <strong>${escapeHtml(procedure.code)}</strong>
        <small>${escapeHtml(procedure.label)}</small>
      </span>
      ${procedure.code === app.selectedProcedureId ? `<span class="selected-indicator" aria-hidden="true">&#10003;</span>` : ""}
    </button>
  `).join("");
}

function canEditAssignment(room) {
  if (!room) {
    return false;
  }

  const state = normalizeState(room);
  return state === "empty" || cancelableStates.has(state);
}

function populateDemoTimerSelect() {
  const demoElapsedSelect = document.getElementById("demoElapsedSelect");
  if (!demoElapsedSelect || demoElapsedSelect.options.length || !isDemoTimerEnabled()) {
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

function applyDemoTimerVisibility() {
  const control = document.querySelector(".demo-timer-control");
  if (control) {
    control.hidden = !isDemoTimerEnabled();
  }
}

function isDemoTimerEnabled() {
  return app.snapshot?.demoTimerEnabled === true;
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
  if (isStaffLoungeRoom()) {
    return;
  }

  const seatButton = document.getElementById("seatButton");
  const readyForDoctorButton = document.getElementById("readyForDoctorButton");
  const updateAssignmentButton = document.getElementById("updateAssignmentButton");
  const cancelSeatingButton = document.getElementById("cancelSeatingButton");
  const doctorArrivedButton = document.getElementById("doctorArrivedButton");
  const doctorCompleteButton = document.getElementById("doctorCompleteButton");
  const roomAvailableButton = document.getElementById("roomAvailableButton");

  if (!seatButton || !readyForDoctorButton || !updateAssignmentButton || !cancelSeatingButton || !doctorArrivedButton || !doctorCompleteButton || !roomAvailableButton) {
    console.error("[ChairSide] Room panel buttons were not found.", {
      seatButton,
      readyForDoctorButton,
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
    const demoElapsedMinutes = isDemoTimerEnabled()
      ? Number(document.getElementById("demoElapsedSelect")?.value || "0")
      : 0;
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

  readyForDoctorButton.addEventListener("click", async () => {
    if (!isConfiguredRoom(app.roomNumber)) {
      setRoomActionStatus("This room is not configured.", "error");
      return;
    }

    if (!activeSeatedStates.has(currentRoomState())) {
      setRoomActionStatus("Ready for Doctor is only available while the room is in prep (Patient Seated).", "error");
      return;
    }

    console.log("[ChairSide] Ready for Doctor clicked.", { roomNumber: app.roomNumber });
    setRoomActionStatus("Marking ready for doctor...", "pending");

    try {
      await sendRoomAction(app.roomNumber, "ready-for-doctor", "Ready for Doctor");
      console.log("[ChairSide] Ready for Doctor succeeded.", { roomNumber: app.roomNumber });
      setRoomActionStatus("Ready for doctor.", "success");
    } catch (error) {
      console.error("[ChairSide] Ready for Doctor failed.", { roomNumber: app.roomNumber, error });
      setRoomActionStatus(error.message || "Failed to mark ready for doctor.", "error");
    }
  });

  updateAssignmentButton.addEventListener("click", async () => {
    if (!isConfiguredRoom(app.roomNumber)) {
      setRoomActionStatus("This room is not configured.", "error");
      return;
    }

    if (!cancelableStates.has(currentRoomState())) {
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

    if (!cancelableStates.has(currentRoomState())) {
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

    if (!doctorArrivedStates.has(currentRoomState())) {
      setRoomActionStatus("Doctor Arrived is only available after Ready for Doctor.", "error");
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
    if (response.status === 401 || response.status === 403) {
      showRoomTokenPrompt(response.status);
    }

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
    if (response.status === 401 || response.status === 403) {
      showRoomTokenPrompt(response.status);
    }

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
    if (response.status === 401 || response.status === 403) {
      showRoomTokenPrompt(response.status);
    }

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

// ---------------------------------------------------------------------------
// Client-side diagnostic error capture.
// Posts technical details to /api/client-errors.
// Never logs PHI, form values, or patient data.
// ---------------------------------------------------------------------------
(function wireClientErrorCapture() {
  // Guard against recursive reports (e.g. if the fetch itself errors).
  let _pending = false;

  function reportError(payload) {
    if (_pending) {
      return;
    }
    _pending = true;
    fetch("/api/client-errors", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
      keepalive: true
    }).catch(function(err) {
      console.warn("[ChairSide] Client error reporting failed.", err);
    }).finally(function() {
      _pending = false;
    });
  }

  function buildPayload(message, source, line, column, stack) {
    var roomId = null;
    var view = null;
    var connectionStatus = null;
    var lastSnapshotAt = null;
    var snapshotAgeMs = null;
    try {
      roomId = app.roomNumber ? String(app.roomNumber) : null;
      view = document.body?.dataset?.view || null;
      connectionStatus = app.connectionStatus || null;
      lastSnapshotAt = app.lastSnapshotAt || null;
      snapshotAgeMs = app.lastSnapshotAt ? Date.now() - app.lastSnapshotAt : null;
    } catch (_) { /* app may not be initialised yet */ }

    return {
      timestamp: new Date().toISOString(),
      url: location.href,
      roomId: roomId,
      view: view,
      message: message || null,
      source: source || null,
      line: line || null,
      column: column || null,
      stack: stack || null,
      userAgent: navigator.userAgent,
      connectionStatus: connectionStatus,
      lastSnapshotAt: lastSnapshotAt,
      snapshotAgeMs: snapshotAgeMs
    };
  }

  window.addEventListener("error", function(event) {
    reportError(buildPayload(
      event.message,
      event.filename,
      event.lineno,
      event.colno,
      event.error && event.error.stack ? event.error.stack : null
    ));
  });

  window.addEventListener("unhandledrejection", function(event) {
    var reason = event.reason;
    var message = reason instanceof Error ? reason.message : String(reason || "Unhandled promise rejection");
    var stack = reason instanceof Error ? reason.stack : null;
    reportError(buildPayload(message, null, null, null, stack));
  });
})();

boot();
