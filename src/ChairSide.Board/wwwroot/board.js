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
  // Hybrid reports filter state. Kept in app (not the DOM) so a SignalR/poll re-render
  // never resets the user's selected filters. sedation: all | sedation | non-sedation.
  // grouping: base | variant.
  reportFilters: { sedation: "all", grouping: "base" },
  reportDoctorId: null,
  reportDoctorTab: "overview",
  // Report date window. Drives the backend completed-cycle filter, so changing it reloads from the
  // API. start/end are ISO yyyy-MM-dd (null = unbounded). Default preset is Last 7 days.
  dateRange: { preset: "last7", start: null, end: null },
  roomNumber: getRoomNumber(),
  roomToken: getRoomToken(),
  roomTokenPromptVisible: false,
  doctorId: new URLSearchParams(location.search).get("doctorId")
    || new URLSearchParams(location.search).get("doctor"),
  selectedDoctorId: null,
  selectedProcedureId: null,
  sedationOn: false,
  // Expected allocation (1 unit = 10 minutes). Kept in app (not the DOM) so a SignalR/poll
  // re-render never discards an in-progress staff adjustment. expectedUnitsManual tracks whether
  // staff have changed units since selecting the current procedure: a procedure change always
  // re-seeds from the new default, but a sedation change only re-seeds when not manually adjusted.
  expectedUnits: null,
  expectedUnitsManual: false,
  expectedUnitsProcedureCode: null,
  expectedUnitsSedation: false,
  // Doctor / procedure / sedation are only editable during initial seating or when the
  // staff explicitly enters the Update Assignment / Edit flow. Otherwise they are locked
  // read-only case metadata. This stays Off by default after seating.
  assignmentEditMode: false,
  selectionContext: null,
  // True while a pointer is pressed on a doctor/procedure tile. The 1s room poll
  // defers re-syncing and re-rendering the selection tiles while this is set so a
  // slow press is never interrupted by a mid-press DOM swap.
  tilePressActive: false
};

const stateNames = ["empty", "seated", "aging", "stale", "ready-for-doctor", "doctor-in-room", "turnover"];
// States where "Ready for Doctor" button is enabled (only the neutral In Prep state).
const activeSeatedStates = new Set(["seated"]);
// States where corrections (cancel/update) are available - all states before Doctor Arrived.
const cancelableStates = new Set(["seated", "ready-for-doctor", "aging", "stale"]);
// States where "Doctor Arrived" is enabled - all ready-for-doctor phase states.
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
    console.error("[ChairSide] boot() called more than once - skipping duplicate interval registration.");
    return;
  }

  await loadBoard();
  if (document.body.dataset.view === "reports") {
    initDateRange();
    await loadReports();
    wireReportsActions();
    wireReportFilters();
    wireDateRange();
  }

  wireDoctorViewMenu();
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
    const response = await fetch(reportsRequestUrl(), {
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

// ---------------------------------------------------------------------------
// Report date range. The selected window is a real backend filter (it bounds the completed-cycle
// population before any calculation), so changing it reloads /api/reports. Dates are ISO yyyy-MM-dd
// computed in UTC to match the server's UTC-day report window semantics.
// ---------------------------------------------------------------------------
function initDateRange() {
  if (!app.dateRange) {
    app.dateRange = { preset: "last7", start: null, end: null };
  }
  if (app.dateRange.preset !== "custom" && app.dateRange.preset !== "all") {
    const resolved = computePresetRange(app.dateRange.preset);
    app.dateRange.start = resolved.start;
    app.dateRange.end = resolved.end;
  }
}

function reportsRequestUrl() {
  const range = app.dateRange;
  if (!range || range.preset === "all") {
    return "/api/reports";
  }

  const params = new URLSearchParams();
  if (range.start) {
    params.set("from", range.start);
  }
  if (range.end) {
    params.set("to", range.end);
  }

  const query = params.toString();
  return query ? `/api/reports?${query}` : "/api/reports";
}

function utcDateString(date) {
  return date.toISOString().slice(0, 10);
}

function computePresetRange(preset) {
  const now = new Date();
  const today = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
  if (preset === "today") {
    return { start: utcDateString(today), end: utcDateString(today) };
  }
  if (preset === "last30") {
    const start = new Date(today);
    start.setUTCDate(start.getUTCDate() - 29);
    return { start: utcDateString(start), end: utcDateString(today) };
  }
  // Default and "last7".
  const start = new Date(today);
  start.setUTCDate(start.getUTCDate() - 6);
  return { start: utcDateString(start), end: utcDateString(today) };
}

async function selectDateRangePreset(preset) {
  if (preset === "custom") {
    app.dateRange = { ...app.dateRange, preset: "custom" };
    syncDateRangeControls();
    return; // wait for explicit Apply
  }

  if (preset === "all") {
    app.dateRange = { preset: "all", start: null, end: null };
  } else {
    const resolved = computePresetRange(preset);
    app.dateRange = { preset, start: resolved.start, end: resolved.end };
  }

  syncDateRangeControls();
  await loadReports();
}

async function applyCustomDateRange() {
  const startInput = document.getElementById("reportRangeStart");
  const endInput = document.getElementById("reportRangeEnd");
  const start = startInput && startInput.value ? startInput.value : null;
  const end = endInput && endInput.value ? endInput.value : null;
  if (!start && !end) {
    return; // nothing to apply; leave current window
  }

  app.dateRange = { preset: "custom", start, end };
  syncDateRangeControls();
  await loadReports();
}

// Reflects app.dateRange onto the static controls so a re-render never desyncs the chips/inputs.
function syncDateRangeControls() {
  document.querySelectorAll(".report-range-chip").forEach(chip => {
    const active = chip.dataset.rangePreset === app.dateRange.preset;
    chip.classList.toggle("is-active", active);
    chip.setAttribute("aria-pressed", String(active));
  });

  const custom = document.getElementById("reportRangeCustom");
  if (custom) {
    custom.hidden = app.dateRange.preset !== "custom";
  }

  if (app.dateRange.preset === "custom") {
    const startInput = document.getElementById("reportRangeStart");
    const endInput = document.getElementById("reportRangeEnd");
    if (startInput && app.dateRange.start) {
      startInput.value = app.dateRange.start;
    }
    if (endInput && app.dateRange.end) {
      endInput.value = app.dateRange.end;
    }
  }
}

function wireDateRange() {
  const container = document.getElementById("reportDateRange");
  if (!container) {
    return;
  }

  container.addEventListener("click", event => {
    const chip = event.target.closest(".report-range-chip");
    if (chip) {
      selectDateRangePreset(chip.dataset.rangePreset);
      return;
    }

    if (event.target.closest("#reportRangeApply")) {
      applyCustomDateRange();
    }
  });

  syncDateRangeControls();
}

// Plain-English window label, using the server's range metadata and all-time context.
function renderReportWindow(r) {
  const el = document.getElementById("reportRangeWindow");
  if (!el) {
    return;
  }

  const label = r && r.rangeLabel ? r.rangeLabel : "All time";
  const total = r ? (r.totalCompletedCycleCount ?? 0) : 0;
  if (label === "All time") {
    el.textContent = `Showing all completed cases (${total} total)`;
    return;
  }

  const shown = r ? (r.completedRoomCyclesCount ?? 0) : 0;
  el.textContent = `Showing completed cases from ${label} (${shown} of ${total} all-time)`;
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
  populateDoctorViewMenu();

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
      <span class="doctor-chip" style="--doctor-color: ${escapeAttribute(doctor.color)}">
        <i></i>${escapeHtml(doctor.name)}
      </span>
    `).join("");
  }

  const procedureTarget = document.getElementById("procedureLegend");
  if (procedureTarget) {
    procedureTarget.innerHTML = app.snapshot.procedures.map(procedure => `
      <span class="procedure-chip">
        <span>${renderProcedureIcon(procedure)}</span>
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

// Updates the Doctor View toggle label. On the doctor page it reflects the
// active doctor ("Doctor View: Dr. Otte"); everywhere else it stays generic.
function updateDoctorViewToggleLabel() {
  const label = document.getElementById("doctorViewToggleLabel");
  if (!label) {
    return;
  }

  if (document.body.dataset.view === "doctor" && app.doctorId && app.snapshot) {
    const doctor = app.snapshot.doctors.find(item => item.id === app.doctorId);
    if (doctor) {
      label.textContent = `Doctor View: ${doctor.name}`;
      return;
    }
  }

  label.textContent = "Doctor View";
}

// Fills the Doctor View dropdown from the live roster snapshot. Runs once per
// page load; render() calls it every tick but the populated guard makes the
// repeat calls no-ops so an open menu is never rebuilt mid-interaction.
function populateDoctorViewMenu() {
  const menu = document.getElementById("doctorViewMenu");
  if (!menu || menu.dataset.populated === "true" || !app.snapshot) {
    return;
  }

  const currentDoctorId = document.body.dataset.view === "doctor" ? app.doctorId : null;
  menu.innerHTML = app.snapshot.doctors.map(doctor => {
    const isCurrent = doctor.id === currentDoctorId;
    return `<a class="nav-menu-item${isCurrent ? " is-current" : ""}" role="menuitem" tabindex="-1"`
      + ` href="/doctor.html?doctorId=${encodeURIComponent(doctor.id)}"`
      + ` style="--doctor-color: ${escapeAttribute(doctor.color || "")}"`
      + `${isCurrent ? ` aria-current="true"` : ""}>`
      + `<span class="nav-menu-swatch" aria-hidden="true"></span>`
      + `<span class="nav-menu-item-label">${escapeHtml(doctor.name)}</span>`
      + `</a>`;
  }).join("");

  menu.dataset.populated = "true";
  updateDoctorViewToggleLabel();
}

// Wires the Doctor View control as a real menu: toggle button owns open/close,
// outside-click and Escape close it, and arrow keys move between doctors.
function wireDoctorViewMenu() {
  const toggle = document.getElementById("doctorViewToggle");
  const menu = document.getElementById("doctorViewMenu");
  if (!toggle || !menu) {
    return;
  }

  const closeMenu = (returnFocus = false) => {
    if (menu.hidden) {
      return;
    }
    menu.hidden = true;
    toggle.setAttribute("aria-expanded", "false");
    if (returnFocus) {
      toggle.focus();
    }
  };

  const openMenu = () => {
    menu.hidden = false;
    toggle.setAttribute("aria-expanded", "true");
    menu.querySelector(".nav-menu-item")?.focus();
  };

  toggle.addEventListener("click", event => {
    event.preventDefault();
    if (menu.hidden) {
      openMenu();
    } else {
      closeMenu();
    }
  });

  document.addEventListener("click", event => {
    if (menu.hidden) {
      return;
    }
    if (!menu.contains(event.target) && !toggle.contains(event.target)) {
      closeMenu();
    }
  });

  document.addEventListener("keydown", event => {
    if (event.key === "Escape" && !menu.hidden) {
      closeMenu(true);
    }
  });

  menu.addEventListener("keydown", event => {
    const items = Array.from(menu.querySelectorAll(".nav-menu-item"));
    if (!items.length) {
      return;
    }
    const currentIndex = items.indexOf(document.activeElement);
    if (event.key === "ArrowDown") {
      event.preventDefault();
      items[(currentIndex + 1) % items.length].focus();
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      items[(currentIndex - 1 + items.length) % items.length].focus();
    } else if (event.key === "Home") {
      event.preventDefault();
      items[0].focus();
    } else if (event.key === "End") {
      event.preventDefault();
      items[items.length - 1].focus();
    }
  });
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
  // While the user is actively pressing a tile, do not re-sync selection from the
  // polled room state or rebuild the tiles. The press completes on pointerup; the
  // next tick (<=1s later) resumes normal syncing.
  if (!app.tilePressActive) {
    syncRoomSelection(room);
    renderSelectionTiles(room);
  }
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
  const title = document.getElementById("doctorViewTitle");
  const target = document.getElementById("doctorRoomList");

  // No implicit default doctor: the view is explicit about who is selected.
  if (!app.doctorId) {
    title.textContent = "Doctor View";
    target.innerHTML = `<div class="empty-message">Choose a doctor from the Doctor View selector above to see their rooms.</div>`;
    return;
  }

  const doctor = app.snapshot.doctors.find(item => item.id === app.doctorId);
  if (!doctor) {
    const message = `Doctor "${app.doctorId}" was not found.`;
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

  const r = app.reports;
  const hasData = (r.completedRoomCyclesCount || 0) > 0;

  revealReportDisclosures();
  renderReportWindow(r);
  syncDateRangeControls();
  renderReportHeadline(r, hasData);
  renderDoctorReportDashboard(r, hasData);
  syncReportFilterButtons();
  renderReportFilterBar(hasData);
  renderAllocationReports(r);
  renderGroupedInsights(r, hasData);
  renderFullMetrics(r, hasData);

  renderCompletedCycles(filterCyclesBySedation(r.recentCompletedCycles || []));
  renderExceptionCycles(filterCyclesBySedation(r.exceptionCycles || []));
  renderProcedureSummaries(filterSummariesBySedation(r.procedureSummaries || []));
}

// Headline band: curated cards when data exists, friendly empty-state when not.
function renderReportHeadline(r, hasData) {
  const headline = document.getElementById("reportHeadline");
  if (!headline) {
    return;
  }

  if (!hasData) {
    headline.classList.add("is-empty");
    headline.innerHTML = `
      <article class="report-empty-state">
        <h2>No completed cycles yet</h2>
        <p>Operational metrics will appear here as rooms complete their cycle. Exceptions and audit detail remain available below.</p>
      </article>
    `;
    return;
  }

  headline.classList.remove("is-empty");
  const exceptions = (r.exceptionCycles || []).length;
  headline.innerHTML = [
    renderHeadlineCard("Completed Cases", String(r.completedRoomCyclesCount ?? 0)),
    renderHeadlineCard("Avg Total to Doctor", formatDuration(r.averageSeatedToDoctorSeconds)),
    renderHeadlineCard("Avg Doctor Time", formatDuration(r.averageDoctorInRoomSeconds)),
    renderHeadlineCard("Exceptions to Review", String(exceptions)),
    renderHeadlineCard("Sedation Cases", `${r.sedationCaseCount ?? 0} / ${r.completedRoomCyclesCount ?? 0}`)
  ].join("");
}

function renderHeadlineCard(label, value) {
  return `
    <article class="metric-card headline-card">
      <span>${escapeHtml(label)}</span>
      <strong>${escapeHtml(value)}</strong>
    </article>
  `;
}

function renderDoctorReportDashboard(r, hasData) {
  const section = document.getElementById("doctorReportDashboard");
  const grid = document.getElementById("doctorReportCards");
  const panel = document.getElementById("selectedDoctorPanel");
  if (!section || !grid || !panel) {
    return;
  }

  const doctors = aggregateAllocationByDoctor(r.doctorSummaries || []);
  section.hidden = !hasData && doctors.length === 0;
  if (section.hidden) {
    grid.innerHTML = "";
    panel.hidden = true;
    panel.innerHTML = "";
    return;
  }

  syncSelectedReportDoctor(doctors);
  grid.innerHTML = doctors.length
    ? doctors.map(agg => renderDoctorAllocationCard(agg, r)).join("")
    : `<p class="report-empty-note">No doctor report data for this range.</p>`;
  renderSelectedDoctorPanel(r, doctors);
}

function syncSelectedReportDoctor(doctors) {
  if (!doctors.length) {
    app.reportDoctorId = null;
    return;
  }

  const current = doctors.find(item => item.doctorId === app.reportDoctorId);
  if (current) {
    return;
  }

  app.reportDoctorId = doctors.find(item => (item.count || 0) > 0)?.doctorId || doctors[0].doctorId;
}

// ---------------------------------------------------------------------------
// Allocation balance presentation (Phase 3B). Surfaces the Phase 3A math in
// plain, neutral language: expected allocation vs measured case flow. No
// ranking, no scoring, no "sedation time".
// ---------------------------------------------------------------------------
function renderAllocationReports(r) {
  renderAllocationBalanceCard(r);
  renderDataQualityCard(r);
  renderDoctorAllocation(r);
  renderProcedureAllocation(r);
}

function renderAllocationBalanceCard(r) {
  const card = document.getElementById("allocationBalanceCard");
  if (!card) {
    return;
  }

  const pill = `<span class="layer-pill layer-pill--allocation">Allocation Logic</span>`;
  const a = r.allocationVariance;
  if (!a || (a.allocationVarianceCycleCount || 0) === 0) {
    card.innerHTML = `
      ${pill}
      <h3>Overall Allocation Balance</h3>
      <p class="allocation-empty">No allocation variance data available for this report view.</p>`;
    return;
  }

  const count = a.allocationVarianceCycleCount;
  card.innerHTML = `
    ${pill}
    <h3>Overall Allocation Balance</h3>
    <p class="allocation-lead">${count} ${count === 1 ? "case" : "cases"} measured against expected allocation across included cases in this report view.</p>
    <p class="allocation-net">Net ${renderVarianceBadge(a.netAllocationVarianceMinutes)} across included cases.</p>
    <p>Average ${renderAverageVarianceBadge(a.averageAllocationVarianceMinutes)}.</p>
    <p class="allocation-breakdown-line">${a.casesOverExpectedAllocation} over expected · ${a.casesUnderExpectedAllocation} under expected · ${a.casesAtExpectedAllocation} at expected</p>
    <p class="allocation-context">${a.adjustedAllocationCycleCount} adjusted allocation ${a.adjustedAllocationCycleCount === 1 ? "case" : "cases"} · ${a.totalExpectedAllocationMinutes} min expected · ${a.totalMeasuredCaseFlowMinutes} min measured</p>
    <p class="allocation-footnote">Practice-wide aggregate. Only includes completed cases that have an expected allocation snapshot and a Doctor Complete timestamp, so this can be fewer than total completed cases.</p>`;
}

function renderDataQualityCard(r) {
  const card = document.getElementById("dataQualityCard");
  if (!card) {
    return;
  }

  const included = r.includedCompletedCycleCount || 0;
  const excluded = r.excludedCompletedCycleCount || 0;
  const exceptions = r.exceptionCount || 0;

  const detail = excluded === 0 && exceptions === 0
    ? `<p class="allocation-ok">All completed records in this range are included in standard metrics.</p>
       <p class="allocation-ok">No reporting exceptions found in this date range.</p>`
    : `<p class="allocation-note">${excluded} ${excluded === 1 ? "record" : "records"} excluded from standard metrics.</p>
       <p class="allocation-note">${exceptions} reporting ${exceptions === 1 ? "exception" : "exceptions"} flagged. Excluded records remain visible below with badges and reasons.</p>`;

  card.innerHTML = `
    <span class="layer-pill layer-pill--data-quality">Data Quality</span>
    <h3>Data Quality</h3>
    <p class="allocation-counts">${included} included · ${excluded} excluded</p>
    ${detail}
    <p class="allocation-footnote">Included/excluded records are a separate layer from allocation-calculable cases above.</p>`;
}

function renderDoctorAllocation(r) {
  const list = document.getElementById("doctorAllocationList");
  if (!list) {
    return;
  }

  const aggregated = aggregateAllocationByDoctor(r.doctorSummaries || []);
  list.classList.remove("doctor-report-card-grid");
  list.innerHTML = aggregated.length
    ? aggregated.map(renderDoctorAllocationRow).join("")
    : `<p class="allocation-empty">No doctor allocation data for this range.</p>`;
}

// Sums each doctor's allocation across the returned (per-month) summaries so a doctor appears
// once. Ordered by the doctor roster - never by variance, to avoid implying a ranking.
function aggregateAllocationByDoctor(summaries) {
  const byDoctor = new Map();
  for (const summary of summaries) {
    const a = summary.allocation;
    if (!a) {
      continue;
    }
    const key = summary.assignedDoctor;
    const agg = byDoctor.get(key) || { doctorId: key, count: 0, net: 0, over: 0, under: 0, at: 0, adjusted: 0 };
    agg.count += a.allocationVarianceCycleCount || 0;
    agg.net += a.netAllocationVarianceMinutes || 0;
    agg.over += a.casesOverExpectedAllocation || 0;
    agg.under += a.casesUnderExpectedAllocation || 0;
    agg.at += a.casesAtExpectedAllocation || 0;
    agg.adjusted += a.adjustedAllocationCycleCount || 0;
    byDoctor.set(key, agg);
  }

  const order = (app.snapshot?.doctors || []).map(doctor => doctor.id);
  const rank = id => {
    const index = order.indexOf(id);
    return index === -1 ? Number.MAX_SAFE_INTEGER : index;
  };

  const rosterCards = (app.snapshot?.doctors || []).map(doctor => ({
    doctorId: doctor.id,
    count: 0,
    net: 0,
    over: 0,
    under: 0,
    at: 0,
    adjusted: 0,
    ...byDoctor.get(doctor.id)
  }));
  const rosterIds = new Set(rosterCards.map(item => item.doctorId));
  const historicalCards = [...byDoctor.values()]
    .filter(item => !rosterIds.has(item.doctorId))
    .sort((x, y) => rank(x.doctorId) - rank(y.doctorId));

  return [...rosterCards, ...historicalCards];
}

const doctorReportIdentity = {
  otte: { initials: "LDO", color: "#dc2626" },
  pledger: { initials: "JWP", color: "#16a34a" },
  gibson: { initials: "JEG", color: "#7c3aed" },
  schroeder: { initials: "NDS", color: "#eab308" }
};

function renderDoctorAllocationCard(agg, report) {
  const doctor = (app.snapshot?.doctors || []).find(item => item.id === agg.doctorId);
  const name = doctor ? doctor.name : doctorName(agg.doctorId);
  const identity = doctorReportIdentity[agg.doctorId] || {
    initials: initialsFromDoctorName(name),
    color: doctor?.color || "#64748b"
  };
  const count = agg.count || 0;
  const average = count > 0 ? agg.net / count : Number.NaN;
  const selected = agg.doctorId === app.reportDoctorId;
  const sparkPoints = (report?.doctorDailyAllocationSeries || []).find(item => item.doctorId === agg.doctorId)?.points;

  // The whole card is the selection control (role="button", focusable). The "View details" affordance
  // is a non-interactive visual cue (aria-hidden span) so we never nest interactive controls; clicks
  // anywhere in the card and Enter/Space on the focused card both resolve to data-report-doctor-id.
  return `
    <article class="doctor-report-card ${count === 0 ? "is-empty" : ""} ${selected ? "is-selected" : ""}" style="--doctor-color: ${escapeAttribute(identity.color)}" data-report-doctor-id="${escapeAttribute(agg.doctorId)}" role="button" tabindex="0" aria-pressed="${selected ? "true" : "false"}" aria-label="${escapeAttribute(`Show report details for ${name}`)}">
      <header class="doctor-report-card-head">
        <span class="doctor-report-initials" aria-hidden="true">${escapeHtml(identity.initials)}</span>
        <div class="doctor-report-identity">
          <h4>${escapeHtml(name)}</h4>
          <p>${escapeHtml(doctorAllocationSummary(agg))}</p>
        </div>
      </header>
      <dl class="doctor-report-metrics">
        <div>
          <dt>Cases</dt>
          <dd>${escapeHtml(String(count))}</dd>
        </div>
        <div>
          <dt>Balance</dt>
          <dd class="${escapeAttribute(varianceClass(agg.net))}">${escapeHtml(formatSignedMinutes(agg.net))}</dd>
        </div>
        <div>
          <dt>Avg</dt>
          <dd class="${escapeAttribute(varianceClass(average))}">${escapeHtml(formatSignedMinutes(average))}</dd>
        </div>
        <div>
          <dt>O / U / A</dt>
          <dd>${escapeHtml(`${agg.over} / ${agg.under} / ${agg.at}`)}</dd>
        </div>
      </dl>
      ${renderDoctorSparkline(sparkPoints)}
      <span class="doctor-report-detail-link" aria-hidden="true">
        ${selected ? "Viewing details" : "View details"}
      </span>
    </article>`;
}

// Plots daily net allocation variance minutes (measured case flow - expected allocation) for one
// doctor. Zero variance sits on a centered neutral baseline; positive (over expected) rises above it
// and negative (under expected) drops below, scaled symmetrically by the largest absolute day so the
// baseline stays meaningful. Honest by construction: a flat run of equal values renders flat, a single
// day renders a short level mark, and no manufactured wobble is added.
//
// preserveAspectRatio="none" lets the SVG stretch to the full card width (matching the metric slab)
// instead of meet-fitting to its height and floating as a narrow centered line; vector-effect
// "non-scaling-stroke" keeps the stroke a crisp, uniform weight despite the non-uniform scaling.
function renderDoctorSparkline(points) {
  const w = 100, h = 32, pad = 3;
  const mid = (h / 2).toFixed(1);
  const open = `<svg class="doctor-sparkline" viewBox="0 0 ${w} ${h}" preserveAspectRatio="none" aria-hidden="true">`;
  const baseline = `<line x1="${pad}" y1="${mid}" x2="${(w - pad).toFixed(1)}" y2="${mid}" stroke="var(--doctor-color)" stroke-width="0.75" vector-effect="non-scaling-stroke" opacity="0.25"/>`;

  if (!points || points.length === 0) {
    return `${open}${baseline}</svg>`;
  }

  const sorted = [...points].sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0));
  const values = sorted.map(p => Number(p.netVarianceMinutes) || 0);
  const maxAbs = Math.max(1, ...values.map(v => Math.abs(v)));
  const half = (h - 2 * pad) / 2;
  const zeroY = h / 2;
  const yOf = v => zeroY - (v / maxAbs) * half;

  if (sorted.length === 1) {
    const y = yOf(values[0]).toFixed(1);
    return `${open}${baseline}<line x1="${(w / 2 - 18).toFixed(1)}" y1="${y}" x2="${(w / 2 + 18).toFixed(1)}" y2="${y}" stroke="var(--doctor-color)" stroke-width="1.5" stroke-linecap="round" vector-effect="non-scaling-stroke" opacity="0.85"/></svg>`;
  }

  const minMs = new Date(sorted[0].date).getTime();
  const maxMs = new Date(sorted[sorted.length - 1].date).getTime();
  const msRange = maxMs - minMs || 1;
  const xScale = w - 2 * pad;
  const coords = sorted.map((p, i) => {
    const x = (pad + ((new Date(p.date).getTime() - minMs) / msRange) * xScale).toFixed(1);
    const y = yOf(values[i]).toFixed(1);
    return `${x},${y}`;
  }).join(" ");
  return `${open}${baseline}<polyline points="${coords}" fill="none" stroke="var(--doctor-color)" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" vector-effect="non-scaling-stroke" opacity="0.85"/></svg>`;
}

function doctorAllocationSummary(agg) {
  const count = agg.count || 0;
  if (count === 0) {
    return "No allocation variance cases in this report range.";
  }
  if (agg.net === 0) {
    return `Measured case flow stayed at expected allocation across ${count} ${count === 1 ? "case" : "cases"}.`;
  }

  const direction = agg.net > 0 ? "over expected" : "under expected";
  return `Measured case flow ran ${formatAbsoluteMinutes(agg.net)} ${direction} across ${count} ${count === 1 ? "case" : "cases"}.`;
}

function mainPressurePoint(agg) {
  if (!agg.count) {
    return "No cases";
  }

  const points = [
    { label: "Over expected", value: agg.over || 0 },
    { label: "Under expected", value: agg.under || 0 },
    { label: "At expected", value: agg.at || 0 }
  ].sort((a, b) => b.value - a.value);

  return points[0].value > 0 ? points[0].label : "No variance";
}

function renderSelectedDoctorPanel(r, doctors) {
  const panel = document.getElementById("selectedDoctorPanel");
  if (!panel) {
    return;
  }

  const agg = doctors.find(item => item.doctorId === app.reportDoctorId) || doctors[0];
  if (!agg) {
    panel.hidden = true;
    panel.innerHTML = "";
    return;
  }

  const doctor = (app.snapshot?.doctors || []).find(item => item.id === agg.doctorId);
  const name = doctor ? doctor.name : doctorName(agg.doctorId);
  const identity = doctorReportIdentity[agg.doctorId] || {
    initials: initialsFromDoctorName(name),
    color: doctor?.color || "#64748b"
  };
  const tabs = ["overview", "trends", "procedures", "flow", "audit"];
  if (!tabs.includes(app.reportDoctorTab)) {
    app.reportDoctorTab = "overview";
  }

  panel.hidden = false;
  panel.style.setProperty("--doctor-color", identity.color);
  panel.innerHTML = `
    <div class="selected-doctor-head">
      <span class="doctor-report-initials" aria-hidden="true">${escapeHtml(identity.initials)}</span>
      <div>
        <h2>${escapeHtml(name)}</h2>
        <p>${escapeHtml(doctorAllocationSummary(agg))}</p>
      </div>
    </div>
    <div class="selected-doctor-tabs" role="tablist" aria-label="${escapeAttribute(name)} report sections">
      ${tabs.map(tab => renderDoctorReportTabButton(tab)).join("")}
    </div>
    <div class="selected-doctor-tab-panel">
      ${renderSelectedDoctorTabContent(app.reportDoctorTab, r, agg)}
    </div>`;
}

function renderDoctorReportTabButton(tab) {
  const labels = {
    overview: "Overview",
    trends: "Trends",
    procedures: "Procedures",
    flow: "Flow Breakdown",
    audit: "Case Audit"
  };
  const selected = app.reportDoctorTab === tab;
  return `
    <button class="selected-doctor-tab ${selected ? "is-active" : ""}" type="button" role="tab" aria-selected="${selected ? "true" : "false"}" data-report-doctor-tab="${escapeAttribute(tab)}">
      ${escapeHtml(labels[tab])}
    </button>`;
}

function renderSelectedDoctorTabContent(tab, r, agg) {
  if (tab === "audit") {
    return renderSelectedDoctorAudit(r, agg.doctorId);
  }

  if (tab === "overview") {
    return renderSelectedDoctorOverview(r, agg);
  }

  const messages = {
    trends: "Trend lines need full-range per-day case data. The current payload only guarantees the 25 most recent completed cycles, so this view is intentionally left blank for now.",
    procedures: "Procedure mix by selected doctor is not included in the current report payload.",
    flow: "Detailed flow breakdown by selected doctor is not included in the current report payload."
  };
  return `<p class="report-empty-note">${escapeHtml(messages[tab] || "Not enough data in the current payload.")}</p>`;
}

function renderSelectedDoctorOverview(r, agg) {
  const count = agg.count || 0;
  const average = count > 0 ? agg.net / count : Number.NaN;
  return `
    <section class="selected-doctor-overview">
      <div class="selected-doctor-summary">
        <h3>Range Flow Summary</h3>
        <p>${escapeHtml(doctorAllocationSummary(agg))}</p>
        <p class="allocation-footnote">Uses existing doctor allocation aggregates for ${escapeHtml(r.rangeLabel || "the selected range")}.</p>
      </div>
      <dl class="selected-doctor-kpis">
        <div><dt>Cases</dt><dd>${escapeHtml(String(count))}</dd></div>
        <div><dt>Net balance</dt><dd class="${escapeAttribute(varianceClass(agg.net))}">${escapeHtml(formatSignedMinutes(agg.net))}</dd></div>
        <div><dt>Average variance</dt><dd class="${escapeAttribute(varianceClass(average))}">${escapeHtml(formatSignedMinutes(average))}</dd></div>
        <div><dt>Pressure point</dt><dd>${escapeHtml(mainPressurePoint(agg))}</dd></div>
      </dl>
    </section>`;
}

function renderSelectedDoctorAudit(r, doctorId) {
  const cycles = (r.recentCompletedCycles || []).filter(cycle => cycle.assignedDoctor === doctorId);
  if (!cycles.length) {
    return `<p class="report-empty-note">No recent completed cycles for this doctor are available in the current payload.</p>`;
  }

  return `
    <div class="selected-doctor-audit">
      <table class="report-table">
        <thead>
          <tr>
            <th>Room</th>
            <th>Procedure</th>
            <th>Doctor Complete</th>
            <th>Expected</th>
            <th>Measured</th>
            <th>Variance</th>
          </tr>
        </thead>
        <tbody>
          ${cycles.map(cycle => `
            <tr>
              <td>Room ${cycle.roomId}</td>
              <td>${renderCycleProcedureCell(cycle)}</td>
              <td>${formatDateTime(cycle.doctorCompleteAt)}</td>
              <td>${formatAllocationMinutes(cycle.expectedAllocationMinutes)}</td>
              <td>${formatAllocationMinutes(cycle.measuredCaseFlowMinutes)}</td>
              <td>${renderVarianceBadge(cycle.allocationVarianceMinutes)}</td>
            </tr>
          `).join("")}
        </tbody>
      </table>
      <p class="allocation-footnote">Case Audit is limited to the recent completed cycles returned by the current report payload.</p>
    </div>`;
}

function initialsFromDoctorName(name) {
  const cleaned = String(name || "")
    .replace(/^Dr\.\s+/i, "")
    .replace(/[^a-z\s]/gi, " ")
    .trim();
  const initials = cleaned.split(/\s+/)
    .filter(Boolean)
    .map(part => part[0])
    .join("")
    .toUpperCase();
  return initials || "--";
}

function formatAbsoluteMinutes(minutes) {
  if (!Number.isFinite(minutes)) {
    return "--";
  }
  const rounded = Math.round(Math.abs(minutes) * 10) / 10;
  return `${rounded} min`;
}

function formatSignedMinutes(minutes) {
  if (!Number.isFinite(minutes)) {
    return "--";
  }
  const rounded = Math.round(minutes * 10) / 10;
  if (rounded > 0) {
    return `+${rounded} min`;
  }
  return `${rounded} min`;
}

function renderDoctorAllocationRow(agg) {
  const name = doctorName(agg.doctorId);
  if (agg.count === 0) {
    return `
      <div class="allocation-row">
        <span class="allocation-row-name">${escapeHtml(name)}</span>
        <span class="allocation-row-detail allocation-empty">No allocation variance cases.</span>
      </div>`;
  }

  return `
    <div class="allocation-row">
      <span class="allocation-row-name">${escapeHtml(name)}</span>
      <span class="allocation-row-detail">
        ${describeAllocation(agg.count, agg.net)}
        <small>${agg.over} over · ${agg.under} under · ${agg.at} at · ${agg.adjusted} adjusted</small>
      </span>
    </div>`;
}

function renderProcedureAllocation(r) {
  const list = document.getElementById("procedureAllocationList");
  if (!list) {
    return;
  }

  // Procedure family (base) summaries only - sedation variants roll up under their family.
  const families = (r.baseProcedureSummaries || [])
    .filter(summary => summary.allocation && (summary.allocation.allocationVarianceCycleCount || 0) > 0);

  list.innerHTML = families.length
    ? families.map(renderProcedureAllocationRow).join("")
    : `<p class="allocation-empty">No procedure family allocation data for this range.</p>`;
}

function renderProcedureAllocationRow(summary) {
  const a = summary.allocation;
  const label = summary.procedureLabel || summary.procedureCode || "Unknown";
  return `
    <div class="allocation-row">
      <span class="allocation-row-name">${escapeHtml(label)}</span>
      <span class="allocation-row-detail">
        ${describeAllocation(a.allocationVarianceCycleCount, a.netAllocationVarianceMinutes)}
        <small>${a.casesOverExpectedAllocation} over · ${a.casesUnderExpectedAllocation} under · ${a.casesAtExpectedAllocation} at · ${a.adjustedAllocationCycleCount} adjusted</small>
      </span>
    </div>`;
}

// Compact neutral one-liner for a doctor/procedure row. Average is derived from the row's own
// net and case count so it stays correct after per-doctor aggregation.
function describeAllocation(count, net) {
  const cases = `${count} ${count === 1 ? "case" : "cases"}`;
  if (net === 0) {
    return `<strong>At expected allocation across ${cases}.</strong>`;
  }
  const avg = count > 0 ? net / count : 0;
  return `<strong>Net ${renderVarianceBadge(net)} across ${cases}.</strong>
          <span>Average ${renderAverageVarianceBadge(avg)}.</span>`;
}

// Neutral average-per-case label, rounded to one decimal.
function formatAverageVariancePerCase(averageMinutes) {
  if (!Number.isFinite(averageMinutes)) {
    return "--";
  }
  const rounded = Math.round(averageMinutes * 10) / 10;
  const magnitude = Math.abs(rounded);
  if (rounded > 0) {
    return `+${magnitude} min over expected per case`;
  }
  if (rounded < 0) {
    return `-${magnitude} min under expected per case`;
  }
  return "0 min at expected per case";
}

// Semantic color class for an allocation variance value, by operational meaning (not ranking):
// over expected = warm/red, under expected = green, at expected = neutral, not calculable = muted.
function varianceClass(minutes) {
  if (!Number.isFinite(minutes)) {
    return "variance-none";
  }
  if (minutes > 0) {
    return "variance-over";
  }
  if (minutes < 0) {
    return "variance-under";
  }
  return "variance-at";
}

// Colored variance label keeping the explicit "over expected" / "under expected" wording.
function renderVarianceBadge(minutes) {
  return `<span class="variance ${varianceClass(minutes)}">${escapeHtml(formatAllocationVariance(minutes))}</span>`;
}

function renderAverageVarianceBadge(averageMinutes) {
  const rounded = Number.isFinite(averageMinutes) ? Math.round(averageMinutes * 10) / 10 : averageMinutes;
  return `<span class="variance ${varianceClass(rounded)}">${escapeHtml(formatAverageVariancePerCase(averageMinutes))}</span>`;
}

function renderReportFilterBar(hasData) {
  const bar = document.getElementById("reportFilterBar");
  if (bar) {
    bar.hidden = !hasData;
  }
}

// Reflects app.reportFilters onto the static filter chips so re-renders never desync the
// pressed state from the stored filter.
function syncReportFilterButtons() {
  document.querySelectorAll("#reportFilterBar .report-filter-chip").forEach(chip => {
    const active = app.reportFilters[chip.dataset.filterGroup] === chip.dataset.filterValue;
    chip.setAttribute("aria-pressed", String(active));
    chip.classList.toggle("is-active", active);
  });
}

// Chooses the summaries for the grouped-insights section using only backend-provided
// aggregates - never recomputing or recombining on the client. For a sedation-only or
// non-sedation-only filter, base and variant groupings coincide (each base has exactly one
// sedation and one non-sedation variant), so the variant summaries are the correct, accurate
// answer for both grouping modes. Only the unfiltered "all" view distinguishes base vs variant.
function getInsightSummaries(r) {
  const variants = r.procedureSummaries || [];
  if (app.reportFilters.sedation === "sedation") {
    return variants.filter(summary => summary.isSedationCase);
  }
  if (app.reportFilters.sedation === "non-sedation") {
    return variants.filter(summary => !summary.isSedationCase);
  }
  return app.reportFilters.grouping === "base"
    ? (r.baseProcedureSummaries || [])
    : variants;
}

function insightsHeadingText() {
  if (app.reportFilters.sedation === "sedation") {
    return "Sedation cases by procedure";
  }
  if (app.reportFilters.sedation === "non-sedation") {
    return "Non-sedation cases by procedure";
  }
  return app.reportFilters.grouping === "base"
    ? "Procedure insights — procedure family"
    : "Procedure insights — detailed variant";
}

function renderGroupedInsights(r, hasData) {
  const section = document.getElementById("reportInsights");
  const grid = document.getElementById("reportInsightsGrid");
  const heading = document.getElementById("reportInsightsHeading");
  if (!section || !grid) {
    return;
  }

  section.hidden = !hasData;
  if (!hasData) {
    grid.innerHTML = "";
    return;
  }

  if (heading) {
    heading.textContent = insightsHeadingText();
  }

  const summaries = getInsightSummaries(r);
  grid.innerHTML = summaries.length
    ? summaries.map(renderInsightCard).join("")
    : `<p class="report-empty-note">No cases match the selected filter.</p>`;
}

// One insight card per procedure group. Labels come from the backend-resolved ProcedureLabel
// (so legacy "SED" reads "Sedation" and composites read "Extraction + Sedation"); the code
// badge uses formatProcedureCode ("EXT+SED" -> "EXT + SED"). Cards only render when cases
// exist, so every duration here is a real measured value.
function renderInsightCard(summary) {
  const code = formatProcedureCode(summary.procedureCode) || "--";
  const label = summary.procedureLabel || code || "Unknown";
  const sedationChip = summary.isSedationCase
    ? `<span class="sedation-chip">Sedation</span>`
    : "";
  return `
    <article class="insight-card" style="${procedureAccentStyle(summary.procedureCode)}">
      <div class="insight-card-head">
        <span class="insight-code">${escapeHtml(code)}</span>
        ${sedationChip}
      </div>
      <h3 class="insight-label">${escapeHtml(label)}</h3>
      <dl class="insight-metrics">
        <div><dt>Cases</dt><dd>${escapeHtml(String(summary.completedCycleCount))}</dd></div>
        <div><dt>Avg Total</dt><dd>${escapeHtml(formatDuration(summary.averageTotalSeconds))}</dd></div>
        <div><dt>Median Total</dt><dd>${escapeHtml(formatDuration(summary.medianTotalSeconds))}</dd></div>
        <div><dt>Avg Doctor Time</dt><dd>${escapeHtml(formatDuration(summary.averageDoctorTimeSeconds))}</dd></div>
        <div><dt>Avg Ready-to-Doctor</dt><dd>${escapeHtml(formatDuration(summary.averageReadyToDoctorSeconds))}</dd></div>
      </dl>
    </article>
  `;
}

// Full metric set (kept behind the "All metrics" expander). Duration metrics show "—" when
// there is no completed-cycle data; counts always show their real number (a genuine 0 stays 0).
function renderFullMetrics(r, hasData) {
  const summary = document.getElementById("reportSummary");
  if (!summary) {
    return;
  }

  const dur = seconds => (hasData ? formatDuration(seconds) : "—");
  summary.innerHTML = [
    renderMetric("Completed Cycles", r.completedRoomCyclesCount),
    renderMetric("Sedation Cases", r.sedationCaseCount),
    renderMetric("Non-sedation Cases", r.nonSedationCaseCount),
    renderMetric("Exceptions Requiring Review", (r.exceptionCycles || []).length),
    renderMetric("Avg Prep Time", dur(r.averagePrepSeconds)),
    renderMetric("Median Prep Time", dur(r.medianPrepSeconds)),
    renderMetric("Avg Ready-to-Doctor Wait", dur(r.averageReadyToDoctorSeconds)),
    renderMetric("Median Ready-to-Doctor Wait", dur(r.medianReadyToDoctorSeconds)),
    renderMetric("Avg Doctor Occupied Wait", dur(r.averageDoctorOccupiedWaitSeconds)),
    renderMetric("Median Doctor Occupied Wait", dur(r.medianDoctorOccupiedWaitSeconds)),
    renderMetric("Avg Doctor Available Wait", dur(r.averageDoctorAvailableWaitSeconds)),
    renderMetric("Median Doctor Available Wait", dur(r.medianDoctorAvailableWaitSeconds)),
    renderMetric("Avg Total to Doctor", dur(r.averageSeatedToDoctorSeconds)),
    renderMetric("Median Total to Doctor", dur(r.medianSeatedToDoctorSeconds)),
    renderMetric("Avg In Room", dur(r.averageDoctorInRoomSeconds)),
    renderMetric("Median In Room", dur(r.medianDoctorInRoomSeconds)),
    renderMetric("Avg Turnover", dur(r.averageTurnoverSeconds)),
    renderMetric("Median Turnover", dur(r.medianTurnoverSeconds)),
    renderMetric("Aging Events", r.agingEventCount),
    renderMetric("Stale Events", r.staleEventCount)
  ].join("");
}

function renderCompletedCycles(cycles) {
  const body = document.getElementById("completedCyclesBody");
  if (!body) {
    return;
  }

  body.innerHTML = cycles.length
    ? cycles.map(renderCycleRow).join("")
    : `<tr><td colspan="23">${escapeHtml(noMatchMessage("No completed room cycles yet."))}</td></tr>`;
}

// Allocation minutes cell: "30 min" when present and positive, otherwise "--". Used for the raw
// expected/measured columns in the completed-cycle audit table.
function formatAllocationMinutes(minutes) {
  return Number.isFinite(minutes) && minutes > 0 ? `${minutes} min` : "--";
}

// Neutral allocation variance label. Positive = over expected, negative = under, zero = at.
function formatAllocationVariance(varianceMinutes) {
  if (!Number.isFinite(varianceMinutes)) {
    return "--";
  }
  if (varianceMinutes > 0) {
    return `+${varianceMinutes} min over expected`;
  }
  if (varianceMinutes < 0) {
    return `${varianceMinutes} min under expected`;
  }
  return "0 min at expected";
}

function revealReportDisclosures() {
  const metrics = document.getElementById("reportMetrics");
  const detail = document.getElementById("reportDetail");
  if (metrics) {
    metrics.hidden = false;
  }
  if (detail) {
    detail.hidden = false;
  }
}

// True for composite "+SED" codes and bare legacy standalone "SED".
function isSedationProcedureCodeClient(code) {
  return hasSedationModifier(code) || String(code || "").toUpperCase() === "SED";
}

function filterCyclesBySedation(cycles) {
  if (app.reportFilters.sedation === "sedation") {
    return cycles.filter(cycle => isSedationProcedureCodeClient(cycle.procedureCode));
  }
  if (app.reportFilters.sedation === "non-sedation") {
    return cycles.filter(cycle => !isSedationProcedureCodeClient(cycle.procedureCode));
  }
  return cycles;
}

function filterSummariesBySedation(summaries) {
  if (app.reportFilters.sedation === "sedation") {
    return summaries.filter(summary => summary.isSedationCase);
  }
  if (app.reportFilters.sedation === "non-sedation") {
    return summaries.filter(summary => !summary.isSedationCase);
  }
  return summaries;
}

// Empty-row copy that reflects whether an active sedation filter (not "all") is hiding rows.
function noMatchMessage(defaultMessage) {
  return app.reportFilters.sedation === "all"
    ? defaultMessage
    : "No rows match the selected sedation filter.";
}

function renderProcedureSummaries(summaries) {
  const body = document.getElementById("procedureSummariesBody");
  if (!body) {
    return;
  }

  body.innerHTML = summaries.length
    ? summaries.map(renderProcedureSummaryRow).join("")
    : `<tr><td colspan="8">${escapeHtml(noMatchMessage("No procedure baselines yet."))}</td></tr>`;
}

function renderProcedureSummaryRow(summary) {
  return `
    <tr>
      <td>${escapeHtml(summary.procedureLabel || "Unknown")}</td>
      <td>${summary.completedCycleCount}</td>
      <td>${formatDuration(summary.averageTotalSeconds)}</td>
      <td>${formatDuration(summary.medianTotalSeconds)}</td>
      <td>${formatDuration(summary.averageReadyToDoctorSeconds)}</td>
      <td>${formatDuration(summary.averageDoctorTimeSeconds)}</td>
      <td>${formatDuration(summary.averageDoctorAvailableWaitSeconds)}</td>
      <td>${formatDuration(summary.averageDoctorOccupiedWaitSeconds)}</td>
    </tr>
  `;
}

function renderExceptionCycles(exceptions) {
  const body = document.getElementById("exceptionCyclesBody");
  if (!body) {
    return;
  }

  body.innerHTML = exceptions.length
    ? exceptions.map(renderExceptionRow).join("")
    : `<tr><td colspan="12">${escapeHtml(noMatchMessage("No exceptions requiring review."))}</td></tr>`;
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
      <td>
        <button class="secondary-button utility-button"
                data-action="confirm-exclusion"
                data-completed-cycle-id="${escapeAttribute(String(cycle.completedCycleId || ""))}"
                title="This keeps the cycle excluded from normal metrics.">
          Confirm Exclusion
        </button>
      </td>
    </tr>
  `;
}

// ---------------------------------------------------------------------------
// Reports admin actions (mark-as-exception)
// ---------------------------------------------------------------------------

function wireReportsActions() {
  // One-time delegated listeners on the document. The reports views are re-rendered
  // on every poll, so we cannot attach to individual elements.
  document.addEventListener("click", handleReportsActionClick);
  // Keyboard activation for the role="button" doctor cards (clicks are already covered above).
  document.addEventListener("keydown", handleReportsCardKeydown);
}

function handleReportsCardKeydown(event) {
  if (event.key !== "Enter" && event.key !== " " && event.key !== "Spacebar") {
    return;
  }
  if (!(event.target instanceof Element)) {
    return;
  }
  // Only act when the focused element is the card itself (it carries the doctor id and tabindex).
  const card = event.target.closest(".doctor-report-card[data-report-doctor-id]");
  if (!card || card !== event.target) {
    return;
  }
  event.preventDefault();
  app.reportDoctorId = card.dataset.reportDoctorId;
  app.reportDoctorTab = "overview";
  if (app.reports) {
    renderReports();
  }
}

// Wires the static filter chips. Filter state lives in app.reportFilters (not the DOM), so a
// SignalR/poll-driven re-render preserves the user's selection; we just re-render the views.
function wireReportFilters() {
  const bar = document.getElementById("reportFilterBar");
  if (!bar) {
    return;
  }

  bar.addEventListener("click", event => {
    const chip = event.target.closest(".report-filter-chip");
    if (!chip) {
      return;
    }

    const group = chip.dataset.filterGroup;
    const value = chip.dataset.filterValue;
    if (!group || !value || app.reportFilters[group] === value) {
      return;
    }

    app.reportFilters[group] = value;
    syncReportFilterButtons();
    if (app.reports) {
      renderReports();
    }
  });
}

async function handleReportsActionClick(event) {
  const doctorButton = event.target.closest("[data-report-doctor-id]");
  if (doctorButton) {
    app.reportDoctorId = doctorButton.dataset.reportDoctorId;
    app.reportDoctorTab = "overview";
    if (app.reports) {
      renderReports();
    }
    return;
  }

  const doctorTab = event.target.closest("[data-report-doctor-tab]");
  if (doctorTab) {
    app.reportDoctorTab = doctorTab.dataset.reportDoctorTab || "overview";
    if (app.reports) {
      renderReports();
    }
    return;
  }

  const confirmButton = event.target.closest("[data-action='confirm-exclusion']");
  if (confirmButton) {
    await handleConfirmExclusionClick(confirmButton);
    return;
  }

  const button = event.target.closest("[data-action='mark-exception']");
  if (!button) {
    return;
  }

  const roomId = Number(button.dataset.roomId);
  const seatedAt = button.dataset.seatedAt;
  const completedCycleId = Number(button.dataset.completedCycleId);
  // Prefer the stable cycle id; fall back to the legacy roomId + seatedAt key when it is absent.
  const hasCycleId = Number.isInteger(completedCycleId) && completedCycleId > 0;
  if (!hasCycleId && (!roomId || !seatedAt)) {
    return;
  }

  const label = `Room ${roomId} (started ${formatDateTime(seatedAt)})`;
  if (!confirm(`Mark ${label} as an exception?\n\nIt will be removed from normal metrics and appear in Exceptions Requiring Review.`)) {
    return;
  }

  // When the stable id is present the server targets by it; roomId is included only so the
  // server-side audit log keeps room context. Otherwise fall back to the legacy compound key.
  const requestBody = hasCycleId ? { completedCycleId, roomId } : { roomId, seatedAt };

  button.disabled = true;
  try {
    const response = await fetch("/api/reports/cycles/mark-exception", {
      method: "POST",
      cache: "no-store",
      headers: {
        "Content-Type": "application/json",
        ...adminRequestHeaders()
      },
      body: JSON.stringify(requestBody)
    });

    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    await loadReports();
  } catch (error) {
    console.error("[ChairSide] Mark as exception failed.", error);
    alert("Mark as exception failed. Please try again.");
    button.disabled = false;
  }
}

async function handleConfirmExclusionClick(button) {
  const completedCycleId = Number(button.dataset.completedCycleId);
  if (!Number.isInteger(completedCycleId) || completedCycleId <= 0) {
    return;
  }

  if (!confirm("Confirm exclusion of this exception?\n\nThis keeps the cycle excluded from normal metrics and clears it from the review queue.")) {
    return;
  }

  button.disabled = true;
  try {
    // Targeted solely by the stable completedCycleId; no request body is required.
    const response = await fetch(`/api/reports/cycles/${completedCycleId}/confirm-exclusion`, {
      method: "POST",
      cache: "no-store",
      headers: {
        ...adminRequestHeaders()
      }
    });

    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    await loadReports();
  } catch (error) {
    console.error("[ChairSide] Confirm exclusion failed.", error);
    alert("Confirm exclusion failed. Please try again.");
    button.disabled = false;
  }
}

function renderReportsAccessPrompt(statusCode) {
  const headline = document.getElementById("reportHeadline");
  if (!headline) {
    return;
  }

  const message = statusCode === 403
    ? "The saved reports token was rejected. Enter the current internal reports token."
    : "Reports access is required for this internal page.";

  // The access prompt owns the always-visible headline band; the filter bar, insights, and
  // collapsible metric/detail areas are hidden until a valid token loads report data.
  headline.classList.remove("is-empty");
  headline.innerHTML = `
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
  ["reportFilterBar", "reportInsights", "reportMetrics", "reportDetail"].forEach(id => {
    const element = document.getElementById(id);
    if (element) {
      element.hidden = true;
    }
  });
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

// Neutral, non-punitive labels for reporting-time data-hygiene flags.
const reportingExceptionBadgeLabels = {
  LegacyProcedure: "Legacy",
  UnmappedProcedure: "Unmapped",
  ExtremeDuration: "Extreme duration",
  OvernightLifecycle: "Overnight",
  MissingTiming: "Missing timing"
};

function renderReportingExceptionBadges(cycle) {
  const reasons = Array.isArray(cycle.reportingExceptionReasons) ? cycle.reportingExceptionReasons : [];
  const badges = reasons.map(reason =>
    `<span class="report-badge report-badge-exception">${escapeHtml(reportingExceptionBadgeLabels[reason] || reason)}</span>`
  );
  if (cycle.isExcludedFromStandardMetrics) {
    badges.push(`<span class="report-badge report-badge-excluded">Excluded</span>`);
  }

  return badges.length ? `<div class="report-badges">${badges.join("")}</div>` : "";
}

// Raw/audit procedure cell. Flagged cycles show the server display label (e.g. "Sedation (Legacy)")
// since their code does not resolve to an active roster tile; clean cycles keep the normal badge.
function renderCycleProcedureCell(cycle) {
  const label = cycle.hasReportingException && cycle.displayProcedureLabel
    ? escapeHtml(cycle.displayProcedureLabel)
    : renderProcedureBadge(cycle.procedureCode);
  return `${label}${renderReportingExceptionBadges(cycle)}`;
}

function renderCycleRow(cycle) {
  const doctor = doctorName(cycle.assignedDoctor);
  return `
    <tr>
      <td>Room ${cycle.roomId}</td>
      <td>${escapeHtml(doctor)}</td>
      <td>${renderCycleProcedureCell(cycle)}</td>
      <td>${formatDateTime(cycle.seatedAt)}</td>
      <td>${formatDateTime(cycle.readyForDoctorAt)}</td>
      <td>${formatDateTime(cycle.doctorArrivedAt)}</td>
      <td>${formatDateTime(cycle.doctorCompleteAt)}</td>
      <td>${formatDateTime(cycle.roomAvailableAt)}</td>
      <td>${formatDuration(cycle.prepSeconds)}</td>
      <td>${formatDuration(cycle.readyToDoctorSeconds)}</td>
      <td>${formatDuration(cycle.doctorOccupiedWaitSeconds)}</td>
      <td>${formatDuration(cycle.doctorAvailableWaitSeconds)}</td>
      <td>${formatDuration(cycle.seatedToDoctorSeconds)}</td>
      <td>${formatDuration(cycle.doctorInRoomSeconds)}</td>
      <td>${formatDuration(cycle.turnoverSeconds)}</td>
      <td>${formatDuration(cycle.totalRoomCycleSeconds)}</td>
      <td>${formatAllocationMinutes(cycle.expectedAllocationMinutes)}</td>
      <td>${formatAllocationMinutes(cycle.measuredCaseFlowMinutes)}</td>
      <td>${renderVarianceBadge(cycle.allocationVarianceMinutes)}</td>
      <td>${escapeHtml(String(cycle.finalWaitState || "--").toUpperCase())}</td>
      <td>${cycle.agingThresholdReached ? "Yes" : "No"}</td>
      <td>${cycle.staleThresholdReached ? "Yes" : "No"}</td>
      <td>
        <button class="secondary-button utility-button"
                data-action="mark-exception"
                data-completed-cycle-id="${escapeAttribute(String(cycle.completedCycleId || ""))}"
                data-room-id="${cycle.roomId}"
                data-seated-at="${escapeAttribute(cycle.seatedAt || "")}">
          Mark Exception
        </button>
      </td>
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

  const accent = procedure ? resolveProcedureAccent(room.procedureCode) : "";
  const tileStyle = `--doctor-color: ${escapeAttribute(doctorColor)}`
    + (accent ? `; --procedure-accent: ${accent}` : "");

  return `
    <article class="room-tile ${state} ${large ? "large" : ""}" style="${tileStyle}">
      <div class="room-topline">
        <strong>Room ${roomId}</strong>
        <span>${badge}</span>
      </div>
      <div class="procedure-lockup${procedure ? " procedure-lockup--chip" : ""}">
        ${procedure ? renderProcedureIcon(procedure) : renderEmptyIcon()}
        <span>${procedure ? escapeHtml(formatProcedureCode(procedure.code)) : "OPEN"}</span>
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
  if (state === "empty") {
    return "AVAILABLE";
  }

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
  const isPrep = activeSeatedStates.has(state); // only "seated" - not aging/stale anymore

  // The Edit flow only exists in correctable seated states; drop it everywhere else so a
  // lifecycle transition (Ready / Doctor Arrived / Available) always re-locks the controls.
  if (!canCorrect) {
    app.assignmentEditMode = false;
  }
  const isEditing = app.assignmentEditMode && canCorrect;

  setDisabled("demoElapsedSelect", !isEnabled || state !== "empty" || !isDemoTimerEnabled());
  setDisabled("seatButton", !isEnabled || state !== "empty");
  setDisabled("readyForDoctorButton", !isEnabled || !isPrep);
  setDisabled("updateAssignmentButton", !isEnabled || !canCorrect);
  setDisabled("cancelSeatingButton", !isEnabled || !canCorrect);
  setDisabled("doctorArrivedButton", !isEnabled || !doctorArrivedStates.has(state));
  setDisabled("doctorCompleteButton", !isEnabled || state !== "doctor-in-room");
  setDisabled("roomAvailableButton", !isEnabled || state !== "turnover");

  // Contextual labels: the correction buttons enter the Edit flow, then save/cancel it.
  setButtonLabel("updateAssignmentButton", isEditing ? "Save Assignment" : "Update Assignment");
  setButtonLabel("cancelSeatingButton", isEditing ? "Cancel Edit" : "Cancel Seating");
}

function setButtonLabel(id, label) {
  const control = document.getElementById(id);
  if (control && control.textContent !== label) {
    control.textContent = label;
  }
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
  let procedure = procedureFromCode(procedureCode);
  // Sedation cases are stored as a composite code ("EXT+SED") that has no direct roster
  // entry; resolve the base procedure for the icon and show a combined label.
  if (!procedure && hasSedationModifier(procedureCode)) {
    const baseProcedure = procedureFromCode(stripSedationModifier(procedureCode));
    if (baseProcedure) {
      procedure = {
        ...baseProcedure,
        code: formatProcedureCode(procedureCode),
        label: `${baseProcedure.label} + Sedation`
      };
    }
  }

  if (!procedure) {
    return escapeHtml(formatProcedureCode(procedureCode) || "--");
  }

  return `
    <span class="procedure-badge" style="${procedureAccentStyle(procedureCode)}">
      ${renderProcedureIcon(procedure)}
      <span>
        <strong>${escapeHtml(procedure.code)}</strong>
        <small>${escapeHtml(procedure.label)}</small>
      </span>
    </span>
  `;
}

// One outline icon per default-roster procedure code. Codes that share an
// icon name in the roster (CON/POE, POST/MISC, BX/BXPOST) get distinct icons
// here without touching roster semantics.
const procedureIconsByCode = {
  CON: "magnifier",
  EXT: "forceps",
  SED: "moon",
  POST: "thumbsup",
  IMP: "bolt",
  BX: "microscope",
  MISC: "ellipsis",
  POE: "calendar",
  IMPRES: "mold",
  INTCK: "links",
  BXPOST: "eye",
  IMPRM: "jackhammer",
  PCOC: "phone",
  UNCOV: "uncover",
  EXBOND: "bond",
  AO4: "archfour"
};

// Legacy roster icon names map to the nearest icon in the current set so a
// customized roster never falls back to the empty placeholder.
const legacyIconAliases = {
  speech: "magnifier",
  check: "thumbsup",
  vial: "microscope",
  teeth: "mold",
  sync: "links",
  interlock: "links",
  wrench: "jackhammer"
};

// Icon paths are taken from Tabler Icons (MIT licensed, see
// assets/icons/README.md) where an equivalent exists. forceps, mold, and
// jackhammer have no Tabler equivalent and are custom drawings in the same
// 24x24 stroke-2 outline language. All icons render through currentColor.
const tablerIconAttrs = `class="procedure-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"`;

const procedureIconSvgs = {
  magnifier: `<svg ${tablerIconAttrs}><path d="M10 10m-7 0a7 7 0 1 0 14 0a7 7 0 1 0 -14 0"/><path d="M21 21l-6 -6"/></svg>`,
  forceps: `<svg ${tablerIconAttrs}><path d="M8.5 3c-1.2 4.2 -0.3 7.6 3.5 10.5"/><path d="M15.5 3c1.2 4.2 0.3 7.6 -3.5 10.5"/><path d="M12 13.5l-3 7.5"/><path d="M12 13.5l3 7.5"/><circle cx="12" cy="13.5" r="1.1" fill="currentColor" stroke="none"/></svg>`,
  moon: `<svg ${tablerIconAttrs}><path d="M12 3c.132 0 .263 0 .393 0a7.5 7.5 0 0 0 7.92 12.446a9 9 0 1 1 -8.313 -12.454z"/><path d="M15 4h4l-4 4h4"/><path d="M19 9.5h2.5l-2.5 2.5h2.5"/></svg>`,
  thumbsup: `<svg ${tablerIconAttrs}><path d="M7 11v8a1 1 0 0 1 -1 1h-2a1 1 0 0 1 -1 -1v-7a1 1 0 0 1 1 -1h3a4 4 0 0 0 4 -4v-1a2 2 0 0 1 4 0v5h3a2 2 0 0 1 2 2l-1 5a2 3 0 0 1 -2 2h-7a3 3 0 0 1 -3 -3"/></svg>`,
  bolt: `<svg ${tablerIconAttrs}><path d="M13 3l0 7l6 0l-8 11l0 -7l-6 0l8 -11"/></svg>`,
  microscope: `<svg ${tablerIconAttrs}><path d="M5 21h14"/><path d="M6 18h2"/><path d="M7 18v3"/><path d="M9 11l3 3l6 -6l-3 -3z"/><path d="M10.5 12.5l-1.5 1.5"/><path d="M17 3l3 3"/><path d="M12 21a6 6 0 0 0 3.715 -10.712"/></svg>`,
  ellipsis: `<svg ${tablerIconAttrs}><path d="M5 12m-1 0a1 1 0 1 0 2 0a1 1 0 1 0 -2 0"/><path d="M12 12m-1 0a1 1 0 1 0 2 0a1 1 0 1 0 -2 0"/><path d="M19 12m-1 0a1 1 0 1 0 2 0a1 1 0 1 0 -2 0"/></svg>`,
  calendar: `<svg ${tablerIconAttrs}><path d="M4 7a2 2 0 0 1 2 -2h12a2 2 0 0 1 2 2v12a2 2 0 0 1 -2 2h-12a2 2 0 0 1 -2 -2z"/><path d="M16 3v4"/><path d="M8 3v4"/><path d="M4 11h16"/><path d="M11 15h1"/><path d="M12 15v3"/></svg>`,
  mold: `<svg ${tablerIconAttrs}><path d="M4.5 18c0 -7 3 -12 7.5 -12s7.5 5 7.5 12"/><path d="M8.5 18c0 -4.5 1.5 -7.5 3.5 -7.5s3.5 3 3.5 7.5"/><path d="M4.5 18h15"/></svg>`,
  links: `<svg ${tablerIconAttrs}><path d="M9 15l6 -6"/><path d="M11 6l.463 -.536a5 5 0 0 1 7.071 7.072l-.534 .464"/><path d="M13 18l-.397 .534a5.068 5.068 0 0 1 -7.127 0a4.972 4.972 0 0 1 0 -7.071l.524 -.463"/></svg>`,
  eye: `<svg ${tablerIconAttrs}><path d="M10 12a2 2 0 1 0 4 0a2 2 0 0 0 -4 0"/><path d="M21 12c-2.4 4 -5.4 6 -9 6c-3.6 0 -6.6 -2 -9 -6c2.4 -4 5.4 -6 9 -6c3.6 0 6.6 2 9 6"/></svg>`,
  jackhammer: `<svg ${tablerIconAttrs}><path d="M6 4.5h12"/><path d="M8.5 4.5V7M15.5 4.5V7"/><path d="M9.5 7h5v6h-5z"/><path d="M12 13v4.5"/><path d="M10.2 17.5h3.6L12 21z"/></svg>`,
  phone: `<svg ${tablerIconAttrs}><path d="M5 4h4l2 5l-2.5 1.5a11 11 0 0 0 5 5l1.5 -2.5l5 2v4a2 2 0 0 1 -2 2a16 16 0 0 1 -15 -15a2 2 0 0 1 2 -2"/></svg>`,
  // Uncover: a surface with a flap lifting open and an up arrow (reveal concept).
  uncover: `<svg ${tablerIconAttrs}><path d="M4 20h16"/><path d="M5 16l7 -2.5l7 2.5"/><path d="M12 3v6"/><path d="M9 6l3 -3l3 3"/></svg>`,
  // Bond: an orthodontic bracket (slotted square) joined to a short chain of links.
  bond: `<svg ${tablerIconAttrs}><rect x="3" y="9" width="6" height="6" rx="1"/><path d="M3 12h6"/><path d="M9 12h2"/><path d="M13 12m-2 0a2 2 0 1 0 4 0a2 2 0 1 0 -4 0"/><path d="M17 12m-2 0a2 2 0 1 0 4 0a2 2 0 1 0 -4 0"/></svg>`,
  // All on Four: a dental arch carried on four implant posts.
  archfour: `<svg ${tablerIconAttrs}><path d="M4 11a8 7 0 0 1 16 0"/><path d="M7 12v5"/><path d="M11 12v6"/><path d="M13 12v6"/><path d="M17 12v5"/></svg>`
};

function renderProcedureIcon(procedure) {
  const iconName = typeof procedure === "string" ? procedure : procedure?.icon;
  const code = typeof procedure === "string" ? "" : String(procedure?.code || "").toUpperCase();
  const key = procedureIconsByCode[code] || legacyIconAliases[iconName] || iconName;
  return procedureIconSvgs[key] || renderEmptyIcon();
}

function renderEmptyIcon() {
  return `<svg ${tablerIconAttrs}><path d="M5 12h14"/></svg>`;
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
  // A room's stored procedure code may carry a sedation modifier ("EXT+SED"). Select the
  // base procedure tile and reflect the sedation toggle from the stored modifier.
  const rawCode = room?.procedureCode || "";
  const sedationFromRoom = hasSedationModifier(rawCode);
  const baseCode = stripSedationModifier(rawCode);
  const procedure = procedureFromCode(baseCode)
    || app.snapshot.procedures.find(item => !isSedationCode(item.code))
    || null;
  app.selectedProcedureId = procedure?.code || null;
  app.sedationOn = sedationFromRoom && Boolean(procedure && procedure.sedationEligible);
}

function hasSedationModifier(code) {
  return /\+SED$/i.test(String(code || ""));
}

function stripSedationModifier(code) {
  return String(code || "").replace(/\+SED$/i, "");
}

function formatProcedureCode(code) {
  return String(code || "").replace(/\+/g, " + ");
}

// Procedure accent colors are a frontend-only, code-keyed presentation concern (Option A):
// they never live in the roster config or DTO. The accent is a small chip/icon cue only and
// must not compete with doctor identity (rail/tint) or lifecycle/state colors (badge/border/timer).
const procedureBaseColorByCode = {
  CON: "#e11d48",
  POE: "#e11d48",
  PCOC: "#e11d48",
  EXT: "#ca8a04",
  BX: "#ca8a04",
  POST: "#2563eb",
  IMPRES: "#2563eb",
  AO4: "#2563eb",
  IMP: "#6d28d9",
  INTCK: "#6d28d9",
  IMPRM: "#6d28d9",
  UNCOV: "#6d28d9",
  BXPOST: "#db2777",
  EXBOND: "#db2777",
  MISC: "#b45309",
  // Legacy standalone sedation renders green and stays readable.
  SED: "#15803d"
};

// Sedation only overrides the accent for these bases; every other eligible base keeps its
// base color when sedated (e.g. IMP+SED stays purple, AO4+SED stays blue).
const procedureSedationOverrideByBase = {
  EXT: "#15803d",
  BX: "#15803d",
  MISC: "#8a4b1f"
};

// Resolves the accent hex for any stored code: base ("EXT"), composite ("EXT+SED"), legacy
// standalone ("SED"), or unknown/blank (returns "" so callers omit the accent safely).
function resolveProcedureAccent(code) {
  const raw = String(code || "").toUpperCase();
  if (!raw) {
    return "";
  }
  if (raw === "SED") {
    return procedureBaseColorByCode.SED;
  }

  const sedation = hasSedationModifier(raw);
  const base = sedation ? stripSedationModifier(raw).toUpperCase() : raw;
  if (sedation && procedureSedationOverrideByBase[base]) {
    return procedureSedationOverrideByBase[base];
  }
  return procedureBaseColorByCode[base] || "";
}

// Builds an inline custom-property declaration for the accent, or "" when there is no accent.
function procedureAccentStyle(code) {
  const accent = resolveProcedureAccent(code);
  return accent ? `--procedure-accent: ${accent}` : "";
}

function renderSelectionTiles(room) {
  renderDoctorTiles(room);
  renderProcedureTiles(room);
  renderSedationToggle(room);
  renderAllocationSelector(room);
}

// Expected allocation bounds (1 unit = 10 minutes). Mirrors the server-side clamp in
// DemoBoardStore so a value never disagrees between the stepper and the stored snapshot.
const EXPECTED_UNITS_MIN = 1;
const EXPECTED_UNITS_MAX = 24;
const MINUTES_PER_UNIT = 10;

function clampExpectedUnits(units) {
  const value = Math.round(Number(units));
  if (!Number.isFinite(value)) {
    return EXPECTED_UNITS_MIN;
  }

  return Math.min(EXPECTED_UNITS_MAX, Math.max(EXPECTED_UNITS_MIN, value));
}

function isVariableProcedure(procedure) {
  return String(procedure?.allocationBehavior || "").toLowerCase() === "variable";
}

// Default expected units for the currently selected procedure, clamped and defended against
// missing roster metadata (falls back to the minimum).
function selectedProcedureDefaultUnits() {
  const procedure = procedureFromCode(app.selectedProcedureId);
  const raw = Number(procedure?.defaultExpectedUnits);
  return clampExpectedUnits(Number.isFinite(raw) && raw > 0 ? raw : EXPECTED_UNITS_MIN);
}

// Re-seeds app.expectedUnits from the selected procedure default when the procedure changes
// (always) or when sedation changes and units have not been manually adjusted. A manual
// adjustment otherwise survives live re-renders.
function syncExpectedUnits() {
  const procedureCode = app.selectedProcedureId || null;
  if (!procedureCode) {
    app.expectedUnits = null;
    app.expectedUnitsManual = false;
    app.expectedUnitsProcedureCode = null;
    app.expectedUnitsSedation = false;
    return;
  }

  const procedureChanged = procedureCode !== app.expectedUnitsProcedureCode;
  const sedationChanged = app.sedationOn !== app.expectedUnitsSedation;

  if (app.expectedUnits === null || procedureChanged) {
    app.expectedUnits = selectedProcedureDefaultUnits();
    app.expectedUnitsManual = false;
  } else if (sedationChanged && !app.expectedUnitsManual) {
    app.expectedUnits = selectedProcedureDefaultUnits();
  }

  app.expectedUnitsProcedureCode = procedureCode;
  app.expectedUnitsSedation = app.sedationOn;
}

// Renders the expected allocation stepper. Interactive only while seating an empty room; for an
// already-seated room it shows the persisted snapshot read-only (and hides when none exists).
function renderAllocationSelector(room) {
  const section = document.getElementById("allocationSection");
  if (!section) {
    return;
  }

  syncExpectedUnits();

  const seating = room ? normalizeState(room) === "empty" : false;
  let units;
  if (seating) {
    units = app.expectedUnits ?? selectedProcedureDefaultUnits();
  } else {
    const persisted = Number(room?.expectedAllocationUnits);
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

  const variable = isVariableProcedure(procedureFromCode(app.selectedProcedureId));
  section.classList.toggle("allocation-variable", seating && variable);

  if (hintEl) {
    hintEl.textContent = !seating
      ? "Confirmed at seating."
      : variable
        ? "This procedure may vary. Confirm units against the scheduled block."
        : "Standard expected allocation. Adjust only if needed.";
  }

  if (minusBtn) {
    minusBtn.disabled = !seating || units <= EXPECTED_UNITS_MIN;
  }

  if (plusBtn) {
    plusBtn.disabled = !seating || units >= EXPECTED_UNITS_MAX;
  }
}

// Adjusts expected units by delta during seating, marking the value as manually set so a later
// live re-render does not snap it back to the procedure default.
function adjustExpectedUnits(delta) {
  if (currentRoomState() !== "empty") {
    return;
  }

  const base = app.expectedUnits ?? selectedProcedureDefaultUnits();
  const next = clampExpectedUnits(base + delta);
  if (next === app.expectedUnits) {
    return;
  }

  app.expectedUnits = next;
  app.expectedUnitsManual = true;
  renderAllocationSelector(getCurrentRoom());
}

function renderDoctorTiles(room) {
  const target = document.getElementById("doctorTiles");
  if (!target) {
    return;
  }

  const isEnabled = canEditAssignment(room);
  const doctors = app.snapshot.doctors;
  const signature = `doctor|${isEnabled}|${app.selectedDoctorId || ""}|`
    + doctors.map(doctor => `${doctor.id}:${doctor.color}:${doctor.name}`).join(";");
  setInnerHtmlIfChanged(target, doctors.map(doctor => `
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
  `).join(""), signature);
}

function renderProcedureTiles(room) {
  const target = document.getElementById("procedureTiles");
  if (!target) {
    return;
  }

  const isEnabled = canEditAssignment(room);
  // Sedation is never a standalone procedure tile; it is applied via the sedation
  // toggle on eligible primary procedures. Filter it out defensively even if a roster
  // were misconfigured to mark it active.
  const procedures = app.snapshot.procedures.filter(procedure => !isSedationCode(procedure.code));
  const signature = `procedure|${isEnabled}|${app.selectedProcedureId || ""}|`
    + procedures.map(procedure => `${procedure.code}:${procedure.label}:${procedure.icon}:${resolveProcedureAccent(procedure.code)}`).join(";");
  setInnerHtmlIfChanged(target, procedures.map(procedure => `
    <button
      class="selection-tile procedure-tile ${procedure.code === app.selectedProcedureId ? "selected" : ""}"
      style="${procedureAccentStyle(procedure.code)}"
      type="button"
      role="radio"
      aria-checked="${procedure.code === app.selectedProcedureId}"
      data-procedure-id="${escapeAttribute(procedure.code)}"
      ${isEnabled ? "" : "disabled"}>
      ${renderProcedureIcon(procedure)}
      <span class="selection-copy">
        <strong>${escapeHtml(procedure.code)}</strong>
        <small>${escapeHtml(procedure.label)}</small>
      </span>
      ${procedure.code === app.selectedProcedureId ? `<span class="selected-indicator" aria-hidden="true">&#10003;</span>` : ""}
    </button>
  `).join(""), signature);
}

function isSedationCode(code) {
  return String(code || "").toUpperCase() === "SED";
}

function selectedProcedureIsSedationEligible() {
  const procedure = procedureFromCode(app.selectedProcedureId);
  return Boolean(procedure && procedure.sedationEligible);
}

// Renders the sedation modifier toggle. It is only interactable during initial seating or
// the explicit Edit flow, and only for a sedation-eligible primary procedure; it defaults
// Off and only turns on when staff explicitly tap it. When the assignment is locked it
// shows the room's actual sedation status as read-only case metadata.
function renderSedationToggle(room) {
  const toggle = document.getElementById("sedationToggle");
  if (!toggle) {
    return;
  }

  const canEdit = canEditAssignment(room);
  const eligible = selectedProcedureIsSedationEligible();
  let interactable;
  let isOn;

  if (canEdit) {
    // Seating / editing: the toggle reflects the in-progress choice and is tappable only
    // for eligible procedures.
    interactable = eligible;
    if (!eligible) {
      app.sedationOn = false;
    }
    isOn = eligible && app.sedationOn;
  } else {
    // Locked: read-only mirror of the persisted case so EXT + SED still reads as On.
    interactable = false;
    isOn = hasSedationModifier(room?.procedureCode);
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
    hint.textContent = !canEdit
      ? "Use Update Assignment to change"
      : eligible
        ? "Tap to mark this as a sedation case"
        : "Not available for this procedure";
  }
}

// True when the doctor / procedure / sedation selection controls are live. They are live
// during initial seating (room empty) and during the explicit Update Assignment / Edit
// flow. In every other seated state they are locked read-only case metadata.
function canEditAssignment(room) {
  if (!room) {
    return false;
  }

  const state = normalizeState(room);
  if (state === "empty") {
    return true;
  }

  return cancelableStates.has(state) && app.assignmentEditMode;
}

// Only writes innerHTML when the logical content actually changed. The room panel
// re-renders on a 1s tick; rebuilding the selection tiles every tick would replace the
// button under a slow press, so the native click (which needs pointerdown + pointerup on
// the same node) never fires.
//
// We compare a caller-supplied signature instead of re-reading target.innerHTML: the
// browser normalizes serialized markup (the "&#10003;" checkmark entity becomes "✓",
// boolean attributes like "disabled" gain ="" ), so a string compare against innerHTML
// would never match and would rewrite the tiles on every tick.
function setInnerHtmlIfChanged(target, html, signature) {
  if (target.dataset.renderKey === signature) {
    return;
  }

  target.dataset.renderKey = signature;
  target.innerHTML = html;
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

    const sedation = app.sedationOn && selectedProcedureIsSedationEligible();
    const expectedAllocationUnits = clampExpectedUnits(app.expectedUnits ?? selectedProcedureDefaultUnits());
    const payload = {
      roomNumber: app.roomNumber,
      doctorId,
      procedureCode,
      procedureId: procedureCode,
      demoElapsedMinutes,
      sedation,
      expectedAllocationUnits
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

    // First press enters the explicit Edit flow and unlocks procedure / sedation.
    if (!app.assignmentEditMode) {
      app.assignmentEditMode = true;
      console.log("[ChairSide] Entered assignment edit mode.", { roomNumber: app.roomNumber });
      setRoomActionStatus("Editing assignment - adjust procedure or sedation, then Save.", "pending");
      renderRoomPanel();
      return;
    }

    // Second press saves the edited assignment and re-locks the controls.
    if (!hasAssignmentSelection()) {
      setRoomActionStatus("Choose a doctor and procedure first.", "error");
      return;
    }

    const payload = {
      roomNumber: app.roomNumber,
      doctorId: app.selectedDoctorId,
      procedureCode: app.selectedProcedureId,
      procedureId: app.selectedProcedureId,
      sedation: app.sedationOn && selectedProcedureIsSedationEligible()
    };

    console.log("[ChairSide] Save Assignment clicked.", payload);
    setRoomActionStatus("Updating assignment...", "pending");

    try {
      await sendAssignmentUpdate(payload);
      app.assignmentEditMode = false;
      console.log("[ChairSide] Update Assignment succeeded.", payload);
      setRoomActionStatus("Assignment updated.", "success");
      renderRoomPanel();
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

    // In the Edit flow this button discards in-progress edits and re-locks, rather than
    // canceling the seating outright.
    if (app.assignmentEditMode) {
      app.assignmentEditMode = false;
      app.selectionContext = null; // force re-sync from the persisted room (discard edits)
      console.log("[ChairSide] Canceled assignment edit.", { roomNumber: app.roomNumber });
      setRoomActionStatus("Edit canceled.", "pending");
      renderRoomPanel();
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
      const response = await fetch(`/api/rooms/${app.roomNumber}/doctor-arrived`, {
        method: "POST",
        headers: mutationHeaders()
      });

      if (response.ok) {
        console.log("[ChairSide] Doctor Arrived succeeded.", { roomNumber: app.roomNumber });
        setRoomActionStatus("Doctor arrived.", "success");
        return;
      }

      // The same doctor is already checked into another room. Offer to move them.
      if (response.status === 409) {
        await handleDoctorArrivalConflict(response);
        return;
      }

      if (response.status === 401 || response.status === 403) {
        showRoomTokenPrompt(response.status);
      }

      throw new Error(await readErrorMessage(response, `Doctor Arrived failed with HTTP ${response.status}.`));
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

// Tracks the logical tile currently being pressed: { selector, idKey, id, activate }.
// Stored by id (not element reference) so a re-render that swaps the DOM node mid-press
// can't strand the interaction. Only one pointer press is tracked at a time.
let pendingTilePress = null;
let tilePressFailsafe = null;
// Upper bound on the interaction lock. A deliberate slow press is ~1-2s; this is well
// clear of that but short enough that a missing pointerup/cancel can never freeze tile
// rendering indefinitely.
const TILE_PRESS_FAILSAFE_MS = 4000;

// Releases the interaction lock so the normal render/poll cycle resumes. Idempotent and
// safe to call from any resume trigger (pointerup, pointercancel, blur, Escape, fail-safe).
// Note: polling/fetching is never paused; only re-rendering the pressed tile is deferred.
function clearTilePress() {
  pendingTilePress = null;
  app.tilePressActive = false;
  if (tilePressFailsafe !== null) {
    clearTimeout(tilePressFailsafe);
    tilePressFailsafe = null;
  }
}

// Resolves a press on pointerup wherever the pointer is released, so a slow press that
// drifts slightly still completes if it lands on the same logical tile.
function completeTilePress(event) {
  const press = pendingTilePress;
  clearTilePress();
  if (!press) {
    return;
  }

  const button = event.target?.closest?.(press.selector);
  if (!button || button.disabled || button.dataset[press.idKey] !== press.id) {
    return;
  }

  press.activate(button);
}

function wireTileGroup(container, selector, idKey, activate) {
  if (!container) {
    return;
  }

  container.addEventListener("pointerdown", event => {
    const button = event.target.closest(selector);
    if (!button || button.disabled) {
      return;
    }
    pendingTilePress = { selector, idKey, id: button.dataset[idKey], activate };
    app.tilePressActive = true;
    if (tilePressFailsafe !== null) {
      clearTimeout(tilePressFailsafe);
    }
    tilePressFailsafe = window.setTimeout(clearTilePress, TILE_PRESS_FAILSAFE_MS);
  });

  // Keyboard activation (Enter / Space) dispatches a click with detail === 0 and no
  // pointer sequence. Pointer-driven clicks (detail >= 1) are already handled on
  // pointerup, so we ignore them here to avoid double-firing.
  container.addEventListener("click", event => {
    if (event.detail !== 0) {
      return;
    }
    const button = event.target.closest(selector);
    if (!button || button.disabled) {
      return;
    }
    activate(button);
  });
}

function wireSelectionTiles() {
  const doctorTiles = document.getElementById("doctorTiles");
  const procedureTiles = document.getElementById("procedureTiles");

  wireTileGroup(doctorTiles, "[data-doctor-id]", "doctorId", button => {
    app.selectedDoctorId = button.dataset.doctorId;
    renderSelectionTiles(getCurrentRoom());
  });

  wireTileGroup(procedureTiles, "[data-procedure-id]", "procedureId", button => {
    app.selectedProcedureId = button.dataset.procedureId;
    if (!selectedProcedureIsSedationEligible()) {
      app.sedationOn = false;
    }
    renderSelectionTiles(getCurrentRoom());
  });

  // Resolve / clean up the press at the document level so a release outside the
  // originating tile (or container) never leaves rendering deferred. pointerup applies
  // the selection when released on the same logical tile; every other path just releases
  // the lock and lets normal rendering resume.
  document.addEventListener("pointerup", completeTilePress);
  document.addEventListener("pointercancel", clearTilePress);
  window.addEventListener("blur", clearTilePress);
  document.addEventListener("keydown", event => {
    if (event.key === "Escape") {
      clearTilePress();
    }
  });

  const sedationToggle = document.getElementById("sedationToggle");
  sedationToggle?.addEventListener("click", () => {
    if (sedationToggle.disabled || !selectedProcedureIsSedationEligible()) {
      return;
    }

    app.sedationOn = !app.sedationOn;
    renderSelectionTiles(getCurrentRoom());
  });

  document.getElementById("allocationMinus")?.addEventListener("click", () => adjustExpectedUnits(-1));
  document.getElementById("allocationPlus")?.addEventListener("click", () => adjustExpectedUnits(1));
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
      demoElapsedMinutes: payload.demoElapsedMinutes,
      sedation: payload.sedation,
      expectedAllocationUnits: payload.expectedAllocationUnits
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
      procedureId: payload.procedureId,
      sedation: payload.sedation
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

// Handles a 409 from the doctor-arrived endpoint: confirms with the user, then asks the server
// to complete the conflicting room and arrive the current room. The server revalidates the
// conflict, so a stale confirmation fails safely with a clear message.
async function handleDoctorArrivalConflict(response) {
  let conflict = null;
  try {
    conflict = await response.json();
  } catch {
    conflict = null;
  }

  if (!conflict || !conflict.conflictingRoomId) {
    setRoomActionStatus("Doctor is already marked in another room. Refresh and try again.", "error");
    return;
  }

  const doctorName = conflict.doctorDisplayName || "The doctor";
  const oldRoom = conflict.conflictingRoomId;
  const newRoom = app.roomNumber;
  const confirmed = confirm(`${doctorName} is already marked in Room ${oldRoom}. Mark Doctor Complete for Room ${oldRoom} and move them to Room ${newRoom}?`);
  if (!confirmed) {
    setRoomActionStatus("Doctor Arrived canceled.", "error");
    return;
  }

  setRoomActionStatus("Moving doctor...", "pending");
  try {
    const resolveResponse = await fetch(`/api/rooms/${newRoom}/doctor-arrived/resolve-conflict`, {
      method: "POST",
      headers: mutationHeaders({ "Content-Type": "application/json" }),
      body: JSON.stringify({ conflictingRoomId: oldRoom })
    });

    if (resolveResponse.ok) {
      setRoomActionStatus("Doctor moved. Previous room marked Doctor Complete.", "success");
      return;
    }

    if (resolveResponse.status === 401 || resolveResponse.status === 403) {
      showRoomTokenPrompt(resolveResponse.status);
    }

    // 409 here means the conflict changed underneath us; the board will refresh via live updates.
    throw new Error(await readErrorMessage(resolveResponse, `Move failed with HTTP ${resolveResponse.status}.`));
  } catch (error) {
    console.error("[ChairSide] Resolve doctor conflict failed.", { roomNumber: newRoom, error });
    setRoomActionStatus(error.message || "Could not move the doctor. Refresh and try again.", "error");
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
