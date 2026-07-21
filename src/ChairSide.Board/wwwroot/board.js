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
  expectedUnitsConfirmed: false,
  expectedUnitsProcedureCode: null,
  expectedUnitsSedation: false,
  persistedAssignmentSignature: "",
  selectionContext: null,
  // True while a pointer is pressed on a doctor/procedure tile. The 1s room poll
  // defers re-syncing and re-rendering the selection tiles while this is set so a
  // slow press is never interrupted by a mid-press DOM swap.
  tilePressActive: false,
  // True while a pointer is pressed on an interactive reports-page element (doctor card,
  // tab button, or table action button). Defers innerHTML writes for those regions so a
  // mid-press DOM swap cannot drop the click. Mirrors the tilePressActive pattern.
  reportPressActive: false,
  // Monotonically incremented each time loadReports() stores a new payload. Used as the
  // data-identity component of render tokens so guarded renders can skip innerHTML writes
  // on 1-second ticks where app.reports hasn't changed.
  reportsVersion: 0
};

const stateNames = ["empty", "seated", "aging", "stale", "ready-for-doctor", "doctor-in-room", "turnover"];
const editableAssignmentStates = new Set(["prestaging", "seated"]);
// States where "Ready for Doctor" button is enabled (only the neutral In Prep state).
const activeSeatedStates = new Set(["seated"]);
// States where cancellation is available. Assignment editing remains locked at Ready.
const cancelableStates = new Set(["prestaging", "seated", "ready-for-doctor", "aging", "stale"]);
// States where "Doctor Arrived" is enabled - all ready-for-doctor phase states.
const doctorArrivedStates = new Set(["ready-for-doctor", "aging", "stale"]);
const staffLoungeRoomNumber = 99;
const trendMinimumComparisonCases = 3;
const trendAboutSameThresholdSeconds = 60;
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

async function loadVersionBadge() {
  try {
    const res = await fetch("/version.txt");
    if (!res.ok) { return; }
    const hash = (await res.text()).trim();
    if (!hash) { return; }
    const badge = document.createElement("span");
    badge.className = "build-version";
    badge.textContent = `v ${hash}`;
    document.body.appendChild(badge);
  } catch {
    // Fail silently — version.txt is only present in published deployments.
  }
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
    wireReportPressGuard();
  }

  if (document.body.dataset.view === "doctor") {
    // Use month-to-date for the cockpit so metrics reflect the current calendar month.
    // app.dateRange is set before loadReports() so reportsRequestUrl() picks up the right from/to.
    const mtd = computePresetRange("mtd");
    app.dateRange = { preset: "mtd", start: mtd.start, end: mtd.end };
    loadReports();
    window.setInterval(loadReports, 60_000);
    wireDoctorCockpitActions();
    wireDoctorCockpitPressGuard();
  }

  if (document.body.dataset.view === "workshop") {
    // Current Reality summarizes a recent, stable window. last30 is current enough to read as
    // "now" while carrying enough completed cases to be meaningful. app.dateRange is set before
    // loadReports() so reportsRequestUrl() bounds the completed-cycle population to that window.
    const last30 = computePresetRange("last30");
    app.dateRange = { preset: "last30", start: last30.start, end: last30.end };
    loadReports();
    window.setInterval(loadReports, 60_000);
    wireWorkshopPresetSelection();
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
  loadVersionBadge();
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
    app.reportsVersion++;
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
  if (preset === "mtd") {
    const start = new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1));
    return { start: utcDateString(start), end: utcDateString(today) };
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

  if (view === "workshop") {
    renderWorkshop();
  }
}

function applySnapshot(snapshot) {
  app.snapshot = snapshot;
  app.lastSnapshotAt = Date.now();
  syncTrainingEnvironmentBadge(snapshot.isTraining === true);

  const serverTime = Date.parse(snapshot.serverTime);
  if (!Number.isNaN(serverTime)) {
    app.serverOffsetMs = serverTime - Date.now();
  }
}

function syncTrainingEnvironmentBadge(isTraining) {
  let badge = document.getElementById("trainingEnvironmentBadge");
  if (!isTraining) {
    badge?.remove();
    return;
  }

  if (badge) {
    return;
  }

  const brand = document.querySelector(".brand-lockup");
  if (!brand) {
    return;
  }

  badge = document.createElement("span");
  badge.id = "trainingEnvironmentBadge";
  badge.className = "training-environment-badge";
  badge.textContent = "TRAINING";
  badge.setAttribute("aria-label", "ChairSide Training environment");
  brand.appendChild(badge);
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
  const details = getConnectionStatusDetails(status);
  let label, ariaLabel;
  if (status === "live") {
    label = app.lastSnapshotAt
      ? new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" }).format(new Date(app.lastSnapshotAt))
      : "—";
    ariaLabel = `Live, last updated at ${label}`;
  } else {
    label = status === "reconnecting" ? "Reconnecting" : "Stale";
    ariaLabel = `${label}: ${details}`;
  }
  target.className = `connection-status ${status}`;
  target.title = details;
  target.setAttribute("aria-label", ariaLabel);
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
      agingLabel.innerHTML = `<i class="state-dot aging"></i> Aging: Ready wait &gt; ${Math.round(agingMinutes)} min`;
    }
  }

  const staleLabel = document.getElementById("staleLegendLabel");
  if (staleLabel) {
    const staleMinutes = getStaleMinutes();
    if (staleMinutes !== null) {
      staleLabel.innerHTML = `<i class="state-dot stale"></i> Stale: Ready wait &gt; ${Math.round(staleMinutes)} min`;
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
  renderAssignmentGuidance(room);
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
    setDoctorRoomCount(target, 0);
    target.innerHTML = `<div class="empty-message">Choose a doctor from the Doctor View selector above to see their rooms.</div>`;
    return;
  }

  const doctor = app.snapshot.doctors.find(item => item.id === app.doctorId);
  if (!doctor) {
    const message = `Doctor "${app.doctorId}" was not found.`;
    title.textContent = "Doctor View";
    setDoctorRoomCount(target, 0);
    target.innerHTML = `<div class="empty-message">${escapeHtml(message)}</div>`;
    return;
  }

  const rooms = app.snapshot.rooms.filter(room => roomAssignedDoctorId(room) === doctor.id);

  title.textContent = doctor.name;
  document.documentElement.style.setProperty("--active-doctor", doctor.color);

  // Pin the report selection to this page's doctor, then render the cockpit when reports are loaded.
  // Reports load asynchronously in boot(); the cockpit is hidden until app.reports is available.
  app.reportDoctorId = doctor.id;
  if (app.reports) {
    renderDoctorCockpit(app.reports, doctor);
  } else {
    const cockpit = document.getElementById("doctorCockpit");
    if (cockpit) {
      cockpit.hidden = true;
    }
  }

  setDoctorRoomCount(target, rooms.length);
  target.innerHTML = rooms.length
    ? rooms.map(room => renderRoomTile(room)).join("")
    : `<div class="empty-message">No active rooms for ${escapeHtml(doctor.name)}.</div>`;
}

// Drives the Doctor View room-status frame's adaptive grid posture. The count is capped at 4 so the
// grid holds a stable 2x2 quadrant shape for 3-4 rooms (the CSS leaves the 4th quadrant as quiet
// whitespace for 3 rooms); more than 4 rooms keep the 2-column posture and flow into further rows.
// Nothing is hidden - every active room tile still renders.
function setDoctorRoomCount(target, roomCount) {
  const capped = Math.min(Math.max(roomCount, 0), 4);
  target.className = `doctor-list room-count-${capped}`;
}

function renderDoctorCockpit(r, doctor) {
  const cockpit = document.getElementById("doctorCockpit");
  if (!cockpit) {
    return;
  }

  const allDoctors = aggregateAllocationByDoctor(r.doctorSummaries || []);
  const agg = allDoctors.find(item => item.doctorId === doctor.id)
    || { doctorId: doctor.id, count: 0, net: 0, over: 0, under: 0, at: 0, adjusted: 0 };
  const identity = doctorReportIdentity[doctor.id] || {
    initials: initialsFromDoctorName(doctor.name),
    color: doctor.color || "#64748b"
  };

  cockpit.hidden = false;

  // Non-interactive summary card. Token guards against rewriting it on every 1-second tick
  // when nothing has changed. The panel below has its own token guard via renderSelectedDoctorPanel.
  const contextCard = document.getElementById("doctorContextCard");
  if (contextCard) {
    const cardToken = `${app.reportsVersion}|${doctor.id}`;
    if (contextCard.dataset.renderKey !== cardToken) {
      contextCard.dataset.renderKey = cardToken;
      const rangeSuffix = r.rangeLabel ? ` · ${r.rangeLabel}` : "";
      contextCard.innerHTML = `
        <p class="doctor-cockpit-range-label">Reporting range: Month to date${escapeHtml(rangeSuffix)}</p>
        <article class="doctor-report-card is-selected is-panel-summary"
          style="--doctor-color: ${escapeAttribute(identity.color)}"
          aria-label="${escapeAttribute(`${doctor.name} — allocation summary`)}">
          ${renderDoctorCardBody(agg, r, doctor.name, identity)}
        </article>`;
    }
  }

  // Full selected-doctor detail panel (head + tabs + tab content) reused directly.
  // app.reportDoctorId was pinned to doctor.id in renderDoctorView, so the panel's
  // internal doctors.find(item => item.doctorId === app.reportDoctorId) always resolves.
  renderSelectedDoctorPanel(r, [agg]);
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
  renderReportTrendCards(r);
  renderDoctorReportDashboard(r, hasData);
  syncReportFilterButtons();
  renderReportFilterBar(hasData);
  renderAllocationReports(r);
  renderGroupedInsights(r, hasData);
  renderFullMetrics(r, hasData);

  renderCompletedCycles(filterCyclesBySedation(r.recentCompletedCycles || []));
  renderExceptionCycles(filterCyclesBySedation(r.exceptionReviewRecords || r.exceptionCycles || []));
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
  const exceptions = (r.exceptionReviewRecords || r.exceptionCycles || []).length;
  headline.innerHTML = [
    renderHeadlineCard("Completed Cases", String(r.completedRoomCyclesCount ?? 0)),
    renderHeadlineCard("Avg Total to Doctor", formatDuration(r.averageSeatedToDoctorSeconds)),
    renderHeadlineCard("Avg Doctor Time", formatDuration(r.averageDoctorInRoomSeconds)),
    renderHeadlineCard("Exceptions to Review", String(exceptions), "Encounter records excluded or flagged because they require administrative review."),
    renderHeadlineCard("Sedation Cases", `${r.sedationCaseCount ?? 0} / ${r.completedRoomCyclesCount ?? 0}`, "Separates cases where sedation was selected from non-sedation cases for reporting context.")
  ].join("");
}

function renderHeadlineCard(label, value, helpText) {
  return `
    <article class="metric-card headline-card">
      <span>${escapeHtml(label)}</span>${helpText ? renderHelpIcon(helpText) : ""}
      <strong>${escapeHtml(value)}</strong>
    </article>
  `;
}

function renderReportTrendCards(r) {
  const panel = document.getElementById("reportTrendPanel");
  if (!panel) {
    return;
  }

  panel.hidden = false;
  panel.innerHTML = [
    renderWaitTrendCard(r),
    renderTurnoverTrendCard(r)
  ].join("");
}

function renderWaitTrendCard(r) {
  const buckets = trendBucketsWithCases(r?.trends?.buckets, {
    countField: "completedCycleCount",
    medianField: "medianSeatedToDoctorSeconds"
  });
  const latest = buckets[buckets.length - 1];

  if (!latest) {
    return `
      <article class="report-card report-trend-card is-empty">
        <div>
          <span class="layer-pill layer-pill--population">Wait Trend</span>
          <h2>Wait trend</h2>
          <p>Not enough trend data yet.</p>
        </div>
        <p class="report-trend-note">Weekly median seated-to-doctor waits will appear here as completed room cycles accumulate.</p>
      </article>
    `;
  }

  const previous = buckets.length > 1 ? buckets[buckets.length - 2] : null;
  const comparison = describeTrendComparison(latest, previous, {
    countField: "completedCycleCount",
    medianField: "medianSeatedToDoctorSeconds",
    noPreviousText: "Not enough prior trend data for a week-to-week comparison yet.",
    lowSampleText: "More cases are needed for a reliable week-to-week comparison.",
    missingText: "Not enough trend data yet.",
    aboutSameText: "Median seated-to-doctor was about the same compared with the previous week with cases.",
    improvedPrefix: "Median seated-to-doctor improved by",
    increasedPrefix: "Median seated-to-doctor increased by",
    comparisonSuffix: "compared with the previous week with cases."
  });

  return renderTrendCard({
    title: "Wait trend",
    eyebrow: "Wait Trend",
    description: "Median seated-to-doctor for the latest weekly bucket.",
    value: formatTrendMinutes(latest.medianSeatedToDoctorSeconds),
    latest,
    previous,
    countField: "completedCycleCount",
    countLabel: "Cases in bucket",
    comparisonLabel: "Compared with previous week with cases",
    comparison
  });
}

function renderTurnoverTrendCard(r) {
  const buckets = trendBucketsWithCases(r?.trends?.buckets, {
    countField: "turnoverCycleCount",
    medianField: "medianTurnoverSeconds"
  });
  const latest = buckets[buckets.length - 1];

  if (!latest) {
    return `
      <article class="report-card report-trend-card turnover-trend-card is-empty">
        <div>
          <span class="layer-pill layer-pill--population">Turnover Trend</span>
          <h2>Turnover trend</h2>
          <p>Not enough turnover trend data yet.</p>
        </div>
        <p class="report-trend-note">Weekly median room reset / handoff flow will appear here as completed room cycles accumulate.</p>
      </article>
    `;
  }

  const previous = buckets.length > 1 ? buckets[buckets.length - 2] : null;
  const comparison = describeTrendComparison(latest, previous, {
    countField: "turnoverCycleCount",
    medianField: "medianTurnoverSeconds",
    noPreviousText: "Not enough prior turnover trend data for a week-to-week comparison yet.",
    lowSampleText: "More turnover cases are needed for a reliable week-to-week comparison.",
    missingText: "Not enough turnover trend data yet.",
    aboutSameText: "Median turnover was about the same compared with the previous week with turnover cases.",
    improvedPrefix: "Median turnover improved by",
    increasedPrefix: "Median turnover increased by",
    comparisonSuffix: "compared with the previous week with turnover cases."
  });

  return renderTrendCard({
    title: "Turnover trend",
    eyebrow: "Turnover Trend",
    description: "Median room reset / handoff flow for the latest weekly bucket.",
    value: formatTrendMinutes(latest.medianTurnoverSeconds),
    latest,
    previous,
    countField: "turnoverCycleCount",
    countLabel: "Turnover cases in bucket",
    comparisonLabel: "Compared with previous week with turnover cases",
    comparison,
    cardClass: "turnover-trend-card"
  });
}

function renderTrendCard(options) {
  const latestRange = formatTrendBucketRange(options.latest);
  const previousRange = options.previous ? formatTrendBucketRange(options.previous) : "";
  return `
    <article class="report-card report-trend-card ${escapeAttribute(options.cardClass || "")}">
      <div class="report-trend-header">
        <div>
          <span class="layer-pill layer-pill--population">${escapeHtml(options.eyebrow)}</span>
          <h2>${escapeHtml(options.title)}</h2>
          <p>${escapeHtml(options.description)}</p>
        </div>
        <strong class="report-trend-value">${escapeHtml(options.value)}</strong>
      </div>
      <dl class="report-trend-facts">
        <div>
          <dt>Latest bucket</dt>
          <dd>${escapeHtml(latestRange)}</dd>
        </div>
        <div>
          <dt>${escapeHtml(options.countLabel)}</dt>
          <dd>${escapeHtml(String(options.latest[options.countField] || 0))}</dd>
        </div>
        <div>
          <dt>${escapeHtml(options.comparisonLabel)}</dt>
          <dd>${escapeHtml(previousRange || "Unavailable")}</dd>
        </div>
      </dl>
      <p class="report-trend-comparison ${escapeAttribute(options.comparison.tone)}">${escapeHtml(options.comparison.text)}</p>
    </article>
  `;
}

function trendBucketsWithCases(buckets, options) {
  if (!Array.isArray(buckets)) {
    return [];
  }

  return buckets
    .filter(bucket => {
      const count = Number(bucket?.[options.countField]);
      const median = Number(bucket?.[options.medianField]);
      return count > 0 && Number.isFinite(median) && median >= 0;
    })
    .slice()
    .sort((a, b) => String(a.startDate || "").localeCompare(String(b.startDate || "")));
}

function describeTrendComparison(latest, previous, options) {
  if (!previous) {
    return {
      tone: "is-neutral",
      text: options.noPreviousText
    };
  }

  const latestCount = Number(latest[options.countField] || 0);
  const previousCount = Number(previous[options.countField] || 0);
  if (latestCount < trendMinimumComparisonCases || previousCount < trendMinimumComparisonCases) {
    return {
      tone: "is-neutral",
      text: options.lowSampleText
    };
  }

  const differenceSeconds = Number(latest[options.medianField]) - Number(previous[options.medianField]);
  if (!Number.isFinite(differenceSeconds)) {
    return {
      tone: "is-neutral",
      text: options.missingText
    };
  }

  if (Math.abs(differenceSeconds) < trendAboutSameThresholdSeconds) {
    return {
      tone: "is-neutral",
      text: options.aboutSameText
    };
  }

  const amount = formatTrendMinutes(Math.abs(differenceSeconds));
  return differenceSeconds < 0
    ? {
        tone: "is-improved",
        text: `${options.improvedPrefix} ${amount} ${options.comparisonSuffix}`
      }
    : {
        tone: "is-increased",
        text: `${options.increasedPrefix} ${amount} ${options.comparisonSuffix}`
      };
}

function formatTrendMinutes(totalSeconds) {
  const seconds = Math.max(0, Number(totalSeconds) || 0);
  const roundedMinutes = Math.round((seconds / 60) * 10) / 10;
  return Number.isInteger(roundedMinutes)
    ? `${roundedMinutes.toFixed(0)} min`
    : `${roundedMinutes.toFixed(1)} min`;
}

function formatTrendBucketRange(bucket) {
  const start = parseReportDateOnly(bucket?.startDate);
  const endExclusive = parseReportDateOnly(bucket?.endDate);
  if (!start || !endExclusive) {
    return "Unknown week";
  }

  const endInclusive = new Date(endExclusive.getTime() - 86_400_000);
  return `${formatReportDateOnly(start)} - ${formatReportDateOnly(endInclusive)}`;
}

function parseReportDateOnly(value) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(value || ""));
  if (!match) {
    return null;
  }

  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const date = new Date(Date.UTC(year, month - 1, day));
  return Number.isNaN(date.getTime()) ? null : date;
}

function formatReportDateOnly(value) {
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    timeZone: "UTC"
  }).format(value);
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
    grid.dataset.renderKey = "";
    panel.hidden = true;
    panel.innerHTML = "";
    return;
  }

  syncSelectedReportDoctor(doctors);

  // Skip grid rebuild when neither the payload nor the selected doctor has changed.
  // Card HTML depends on reportsVersion (data) and reportDoctorId (is-selected class /
  // aria-pressed). Tab does not affect card markup, so it is excluded from this token.
  // During an active pointer press, skip to avoid destroying the pressed element mid-click;
  // do NOT update the key so the catch-up render (on pointerup) sees the stale token and
  // rebuilds if data arrived during the press.
  const gridToken = `${app.reportsVersion}|${app.reportDoctorId}`;
  if (grid.dataset.renderKey !== gridToken) {
    if (!app.reportPressActive) {
      grid.dataset.renderKey = gridToken;
      grid.innerHTML = doctors.length
        ? doctors.map(agg => renderDoctorAllocationCard(agg, r)).join("")
        : `<p class="report-empty-note">No doctor report data for this range.</p>`;
    }
  }

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
      <h3>Overall Allocation Balance${renderHelpIcon("Planned time budget based on the selected procedure mix and allocation settings.")}</h3>
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

// Renders the inner body of a doctor report card: header (initials + name + summary), metrics dl,
// and sparkline. Used by both the interactive grid card and the non-interactive cockpit summary.
function renderDoctorCardBody(agg, report, name, identity) {
  const count = agg.count || 0;
  const average = count > 0 ? agg.net / count : Number.NaN;
  const sparkPoints = (report?.doctorDailyAllocationSeries || []).find(item => item.doctorId === agg.doctorId)?.points;
  return `
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
      <div class="doctor-card-metric--help-corner">
        <dt>O / U / A</dt>
        <dd>${escapeHtml(`${agg.over} / ${agg.under} / ${agg.at}`)}</dd>
        ${renderHelpIcon("O/U/A means Over, Under, or At target compared with expected procedure allocation.", "corner")}
      </div>
    </dl>
    ${renderDoctorSparkline(sparkPoints)}`;
}

// The whole card is the selection control (role="button", focusable). The "View details" affordance
// is a non-interactive visual cue (aria-hidden span) so we never nest interactive controls; clicks
// anywhere in the card and Enter/Space on the focused card both resolve to data-report-doctor-id.
function renderDoctorAllocationCard(agg, report) {
  const doctor = (app.snapshot?.doctors || []).find(item => item.id === agg.doctorId);
  const name = doctor ? doctor.name : doctorName(agg.doctorId);
  const identity = doctorReportIdentity[agg.doctorId] || {
    initials: initialsFromDoctorName(name),
    color: doctor?.color || "#64748b"
  };
  const count = agg.count || 0;
  const selected = agg.doctorId === app.reportDoctorId;
  return `
    <article class="doctor-report-card ${count === 0 ? "is-empty" : ""} ${selected ? "is-selected" : ""}" style="--doctor-color: ${escapeAttribute(identity.color)}" data-report-doctor-id="${escapeAttribute(agg.doctorId)}" role="button" tabindex="0" aria-pressed="${selected ? "true" : "false"}" aria-label="${escapeAttribute(`Show report details for ${name}`)}">
      ${renderDoctorCardBody(agg, report, name, identity)}
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
    panel.dataset.renderKey = "";
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

  // Skip panel rebuild when payload, selected doctor, and active tab are all unchanged.
  // Tab is included because it drives both the tab-button aria-selected states and the
  // entire tab-panel content. During an active pointer press, defer the rebuild to avoid
  // destroying the tab button mid-click; leave the key stale so the catch-up render applies.
  const panelToken = `${app.reportsVersion}|${agg.doctorId}|${app.reportDoctorTab}`;
  if (panel.dataset.renderKey === panelToken) {
    return;
  }
  if (app.reportPressActive) {
    return;
  }
  panel.dataset.renderKey = panelToken;

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

  if (tab === "flow") {
    return renderSelectedDoctorFlow(r, agg);
  }

  if (tab === "trends") {
    return renderSelectedDoctorEmptyState(
      "Trends",
      "Trend charts are planned for this doctor view. Once enabled, this tab will show week-to-week or month-to-month movement for timing and flow metrics."
    );
  }

  if (tab === "procedures") {
    return renderSelectedDoctorProcedures(r, agg);
  }

  return renderSelectedDoctorEmptyState("Not Available", "This section isn't available with the current report payload.");
}

// Shared empty/placeholder card for selected-doctor tabs: a heading (with optional help bubble)
// plus a plain-English note, reusing the same markup as the populated tab sections so an empty
// tab still reads as an intentional part of the report rather than a broken or missing view.
function renderSelectedDoctorEmptyState(title, body, helpText) {
  return `
    <section class="selected-doctor-overview">
      <div class="selected-doctor-summary">
        <h3>${escapeHtml(title)}${helpText ? renderHelpIcon(helpText) : ""}</h3>
        <p class="report-empty-note">${escapeHtml(body)}</p>
      </div>
    </section>`;
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

function observedLoadNumber(value) {
  const number = Number(value);
  return Number.isFinite(number) ? number : 0;
}

function renderSelectedDoctorFlow(r, agg) {
  const days = (r.observedDoctorDays || []).filter(day => day.doctorId === agg.doctorId);
  if (!days.length) {
    return renderSelectedDoctorEmptyState(
      "Observed Load",
      "No observed load data is available for this doctor in the current report range. This usually means there are no completed cycles for this doctor/date selection yet.",
      "Shows the doctor's observed room-flow load for the selected range. Descriptive only; not a ranking or score."
    );
  }

  const sorted = [...days].sort((a, b) => String(b.reportDate || "").localeCompare(String(a.reportDate || "")));
  const recent = sorted.slice(0, 10);

  const completedCases = days.reduce((sum, day) => sum + observedLoadNumber(day.encounterCount), 0);
  const finiteSpans = days.map(day => day.observedClinicalSpanMinutes).filter(Number.isFinite);
  const avgClinicalSpan = finiteSpans.length
    ? finiteSpans.reduce((sum, minutes) => sum + minutes, 0) / finiteSpans.length
    : Number.NaN;
  const peakActiveRooms = days.reduce(
    (max, day) => Number.isFinite(day.maxActiveRoomCount) ? Math.max(max, day.maxActiveRoomCount) : max,
    0
  );
  const oneRoomMinutes = days.reduce((sum, day) => sum + observedLoadNumber(day.minutesWithOneActiveRoom), 0);
  const twoRoomMinutes = days.reduce((sum, day) => sum + observedLoadNumber(day.minutesWithTwoActiveRooms), 0);
  const threePlusRoomMinutes = days.reduce((sum, day) => sum + observedLoadNumber(day.minutesWithThreeOrMoreActiveRooms), 0);
  const stackedMinutes = twoRoomMinutes + threePlusRoomMinutes;
  const overlapSentence = stackedMinutes > 0
    ? `Observed active room time included ${formatDurationMinutes(stackedMinutes)} with overlapping rooms.`
    : "Observed active room time stayed in single-room flow for this range.";

  return `
    <section class="selected-doctor-overview">
      <div class="selected-doctor-summary">
        <h3>Observed Load${renderHelpIcon("Shows the doctor's observed room-flow load for the selected range. Descriptive only; not a ranking or score.")}</h3>
        <p>Across ${escapeHtml(String(days.length))} observed day${days.length === 1 ? "" : "s"}, this doctor completed ${escapeHtml(String(completedCases))} case${completedCases === 1 ? "" : "s"} with a typical clinical span of ${escapeHtml(formatApproxDurationMinutes(avgClinicalSpan))} per day.</p>
        <p>${escapeHtml(overlapSentence)}</p>
        <p class="allocation-footnote">Observed Load is descriptive only: it shows room overlap and span pressure, not provider ranking or staff performance scoring.</p>
      </div>
      <dl class="selected-doctor-kpis">
        <div><dt>Days observed</dt><dd>${escapeHtml(String(days.length))}</dd></div>
        <div><dt>Completed cases</dt><dd>${escapeHtml(String(completedCases))}</dd></div>
        <div><dt>Avg clinical span${renderHelpIcon("Average observed span per day from first seated case through last Doctor Complete.")}</dt><dd>${escapeHtml(formatDurationMinutes(avgClinicalSpan))}</dd></div>
        <div><dt>Peak active load${renderHelpIcon("Highest number of active rooms overlapping for this doctor on an observed day.")}</dt><dd>${escapeHtml(describePeakActiveLoad(peakActiveRooms))}</dd></div>
      </dl>
    </section>
    <div class="selected-doctor-audit">
      <table class="report-table">
        <thead>
          <tr>
            <th>Date</th>
            <th>Cases</th>
            <th>Clinical Span</th>
            <th>Team Span</th>
            <th>Peak Load</th>
            <th>1 room</th>
            <th>2 rooms</th>
            <th>3+ rooms</th>
          </tr>
        </thead>
        <tbody>
          ${recent.map(day => `
            <tr>
              <td>${escapeHtml(formatObservedDayDate(day.reportDate))}</td>
              <td>${escapeHtml(Number.isFinite(day.encounterCount) ? String(day.encounterCount) : "--")}</td>
              <td>${escapeHtml(formatDurationMinutes(day.observedClinicalSpanMinutes))}</td>
              <td>${escapeHtml(formatDurationMinutes(day.observedTeamSpanMinutes))}</td>
              <td>${escapeHtml(describePeakActiveLoad(day.maxActiveRoomCount))}</td>
              <td>${escapeHtml(formatAllocationMinutes(day.minutesWithOneActiveRoom))}</td>
              <td>${escapeHtml(formatAllocationMinutes(day.minutesWithTwoActiveRooms))}</td>
              <td>${escapeHtml(formatAllocationMinutes(day.minutesWithThreeOrMoreActiveRooms))}</td>
            </tr>
          `).join("")}
        </tbody>
      </table>
      ${days.length > recent.length
        ? `<p class="allocation-footnote">Showing the ${recent.length} most recent observed days of ${days.length} total.</p>`
        : ""}
    </div>`;
}

function formatObservedDayDate(value) {
  const parsed = parseReportDateOnly(value);
  return parsed ? formatReportDateOnly(parsed) : "--";
}

function describePeakActiveLoad(maxActiveRoomCount) {
  if (!Number.isFinite(maxActiveRoomCount) || maxActiveRoomCount < 1) {
    return "--";
  }
  if (maxActiveRoomCount === 1) {
    return "1 room active";
  }
  if (maxActiveRoomCount === 2) {
    return "2 rooms active";
  }
  return "3+ rooms active";
}

// Selected-doctor Procedure Mix: the doctor's completed-case procedure breakdown for the range,
// filtered from the additive doctorProcedureMix payload. Rows are variant-level (sedation shown as
// a modifier chip, not a separate procedure); Share is each procedure's portion of this doctor's
// completed cases. Light by design - a summary line plus a compact table, no charts.
function renderSelectedDoctorProcedures(r, agg) {
  const rows = (r.doctorProcedureMix || []).filter(row => row.doctorId === agg.doctorId);
  if (!rows.length) {
    return renderSelectedDoctorEmptyState(
      "Procedure Mix",
      "No procedure mix is available for this doctor in the current report range. This usually means there are no completed cases for this doctor/date selection yet.",
      "Share is each procedure's portion of this doctor's completed cases in the selected range."
    );
  }

  const totalCases = rows[0].doctorCompletedCaseCount || rows.reduce((sum, row) => sum + (row.caseCount || 0), 0);
  const distinct = rows.length;

  return `
    <section class="selected-doctor-overview">
      <div class="selected-doctor-summary">
        <h3>Procedure Mix${renderHelpIcon("Share is each procedure's portion of this doctor's completed cases in the selected range. Sedation is shown as a modifier of the base procedure, not a separate procedure.")}</h3>
        <p>${escapeHtml(String(totalCases))} completed case${totalCases === 1 ? "" : "s"} across ${escapeHtml(String(distinct))} procedure type${distinct === 1 ? "" : "s"} for this doctor in the selected range.</p>
      </div>
    </section>
    <div class="selected-doctor-audit">
      <table class="report-table">
        <thead>
          <tr>
            <th>Procedure</th>
            <th>Cases</th>
            <th>Share</th>
          </tr>
        </thead>
        <tbody>
          ${rows.map(row => `
            <tr>
              <td>${escapeHtml(row.procedureLabel || row.procedureCode || "Unknown")}${row.isSedationCase ? ` <span class="sedation-chip">Sedation</span>` : ""}</td>
              <td>${escapeHtml(String(row.caseCount ?? 0))}</td>
              <td>${escapeHtml(formatProcedureShare(row.shareOfDoctorCases))}</td>
            </tr>
          `).join("")}
        </tbody>
      </table>
    </div>`;
}

function formatProcedureShare(share) {
  return Number.isFinite(share) ? `${Math.round(share * 100)}%` : "--";
}

function renderSelectedDoctorAudit(r, doctorId) {
  const cycles = (r.recentCompletedCycles || []).filter(cycle => cycle.assignedDoctor === doctorId);
  if (!cycles.length) {
    return renderSelectedDoctorEmptyState(
      "Case Audit",
      "No completed cycles are available for this doctor in the selected range yet. They'll appear here once cases wrap up."
    );
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

// Shared initials lookup for the doctor-coin pattern: prefer the curated 3-letter initials from
// doctorReportIdentity (only its initials, never its color - callers use the room/tile's own
// --doctor-color so the coin always agrees with the existing doctor-identity rail), falling back
// to name-derived initials for any doctor not in that map.
function doctorInitials(doctorId, name) {
  return doctorReportIdentity[doctorId]?.initials || initialsFromDoctorName(name);
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
    renderMetric("Completed Cycles", r.completedRoomCyclesCount, "Room cycles that reached completion and are available for reporting."),
    renderMetric("Sedation Cases", r.sedationCaseCount),
    renderMetric("Non-sedation Cases", r.nonSedationCaseCount),
    renderMetric("Exceptions Requiring Review", (r.exceptionCycles || []).length),
    renderMetric("Avg Prep Time", dur(r.averagePrepSeconds)),
    renderMetric("Median Prep Time", dur(r.medianPrepSeconds)),
    renderMetric("Avg Ready-to-Doctor Wait", dur(r.averageReadyToDoctorSeconds)),
    renderMetric("Median Ready-to-Doctor Wait", dur(r.medianReadyToDoctorSeconds)),
    renderMetric("Avg Doctor Occupied Wait", dur(r.averageDoctorOccupiedWaitSeconds), "Time a patient was ready while the doctor was already active in another room."),
    renderMetric("Median Doctor Occupied Wait", dur(r.medianDoctorOccupiedWaitSeconds)),
    renderMetric("Avg Doctor Available Wait", dur(r.averageDoctorAvailableWaitSeconds), "Time a patient was ready while the doctor was not occupied in another active room."),
    renderMetric("Median Doctor Available Wait", dur(r.medianDoctorAvailableWaitSeconds)),
    renderMetric("Avg Total to Doctor", dur(r.averageSeatedToDoctorSeconds)),
    renderMetric("Median Total to Doctor", dur(r.medianSeatedToDoctorSeconds)),
    renderMetric("Avg In Room", dur(r.averageDoctorInRoomSeconds)),
    renderMetric("Median In Room", dur(r.medianDoctorInRoomSeconds)),
    renderMetric("Avg Turnover", dur(r.averageTurnoverSeconds), "Time from Doctor Complete until the room is marked Available."),
    renderMetric("Median Turnover", dur(r.medianTurnoverSeconds)),
    renderMetric("Aging Events", r.agingEventCount, "Ready-room wait has crossed the aging threshold and may need attention."),
    renderMetric("Stale Events", r.staleEventCount, "Ready-room wait has crossed the stale threshold and should be treated as higher priority.")
  ].join("");
}

function renderCompletedCycles(cycles) {
  const body = document.getElementById("completedCyclesBody");
  if (!body) {
    return;
  }

  // Token covers every client-side input that affects cycle row HTML:
  //   reportsVersion — new payload arrived; sedation — filter changes visible rows.
  // grouping does not affect cycle rows (it only affects insight summaries).
  const token = `${app.reportsVersion}|${app.reportFilters.sedation}`;
  if (body.dataset.renderKey === token) {
    return;
  }
  if (app.reportPressActive) {
    return;
  }
  body.dataset.renderKey = token;
  body.innerHTML = cycles.length
    ? cycles.map(renderCycleRow).join("")
    : `<tr><td colspan="23">${escapeHtml(noMatchMessage("No completed room cycles yet."))}</td></tr>`;
}

// Allocation minutes cell: "30 min" when present and positive, otherwise "--". Used for the raw
// expected/measured columns in the completed-cycle audit table.
function formatAllocationMinutes(minutes) {
  return Number.isFinite(minutes) && minutes > 0 ? `${minutes} min` : "--";
}

// Human-readable duration from a minute value, for report copy that must never expose raw decimals
// (e.g. a client-computed average). Non-finite/null -> "--". Always rounds to the nearest whole
// minute. Under 90 minutes reads as "42 min"; 90 minutes or more reads as "8 hr 30 min" (or "8 hr"
// on the hour). A negative value keeps its sign ("-1 hr 10 min") for signed metrics.
function formatDurationMinutes(value) {
  if (!Number.isFinite(value)) {
    return "--";
  }
  const sign = value < 0 ? "-" : "";
  const totalMinutes = Math.round(Math.abs(value));
  if (totalMinutes < 90) {
    return `${sign}${totalMinutes} min`;
  }
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  return minutes === 0 ? `${sign}${hours} hr` : `${sign}${hours} hr ${minutes} min`;
}

// Approximation wording for narrative report sentences: "about 8 hr 30 min". Non-finite -> "--"
// (no "about" prefix on an empty value).
function formatApproxDurationMinutes(value) {
  return Number.isFinite(value) ? `about ${formatDurationMinutes(value)}` : "--";
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

  const token = `${app.reportsVersion}|${app.reportFilters.sedation}`;
  if (body.dataset.renderKey === token) {
    return;
  }
  body.dataset.renderKey = token;
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

  const token = `${app.reportsVersion}|${app.reportFilters.sedation}`;
  if (body.dataset.renderKey === token) {
    return;
  }
  if (app.reportPressActive) {
    return;
  }
  body.dataset.renderKey = token;
  body.innerHTML = exceptions.length
    ? exceptions.map(renderExceptionRow).join("")
    : `<tr><td colspan="12">${escapeHtml(noMatchMessage("No exceptions requiring review."))}</td></tr>`;
}

function renderExceptionRow(cycle) {
  const doctor = doctorName(cycle.assignedDoctor);
  const sourceType = cycle.sourceType || "CompletedCycle";
  const reviewRecordId = Number(cycle.reviewRecordId || cycle.completedCycleId || cycle.abortedAssignmentId || 0);
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
                data-review-source="${escapeAttribute(sourceType)}"
                data-review-record-id="${escapeAttribute(String(reviewRecordId || ""))}"
                title="This keeps the record excluded from normal metrics.">
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

// Sets app.reportPressActive while a pointer is held on an interactive reports element
// (doctor card, tab button, or table action button). This defers innerHTML writes so a
// mid-press DOM rebuild cannot orphan the pressed element and silently drop the click.
// Mirrors the tilePressActive / wireRoomPanel pattern used for room tile interactions.
function wireReportPressGuard() {
  const shell = document.querySelector(".reports-shell");
  if (!shell) {
    return;
  }

  shell.addEventListener("pointerdown", event => {
    if (!event.target.closest("[data-report-doctor-id], [data-report-doctor-tab], .report-table button")) {
      return;
    }
    app.reportPressActive = true;
    clearTimeout(reportPressFailsafe);
    reportPressFailsafe = window.setTimeout(() => {
      app.reportPressActive = false;
      reportPressFailsafe = null;
    }, 3000);
  });

  // On pointerup/cancel, lift the guard and immediately run a catch-up render so any
  // data that arrived during the press is shown without waiting for the next 1-second tick.
  // This catch-up fires before the click event, so the click's own renderReports() call
  // (triggered by handleReportsActionClick) still runs after with the freshly updated
  // app state (e.g. new reportDoctorId) and is not blocked.
  const clearPress = () => {
    clearTimeout(reportPressFailsafe);
    reportPressFailsafe = null;
    if (!app.reportPressActive) {
      return;
    }
    app.reportPressActive = false;
    if (app.reports) {
      renderReports();
    }
  };

  document.addEventListener("pointerup", clearPress);
  document.addEventListener("pointercancel", clearPress);
}

function wireDoctorCockpitActions() {
  document.addEventListener("click", event => {
    const tab = event.target.closest("[data-report-doctor-tab]");
    if (!tab) {
      return;
    }
    app.reportDoctorTab = tab.dataset.reportDoctorTab || "overview";
    if (app.reports) {
      renderDoctorView();
    }
  });
}

// Mirrors wireReportPressGuard but for the doctor-view report tabs. Uses the same shared
// app.reportPressActive flag so renderSelectedDoctorPanel's existing press guard applies.
// Delegated on document (the tabs live in the report-details region, not a single cockpit
// wrapper) and filtered to tab elements, which only exist on this page.
function wireDoctorCockpitPressGuard() {
  document.addEventListener("pointerdown", event => {
    if (!event.target.closest("[data-report-doctor-tab]")) {
      return;
    }
    app.reportPressActive = true;
    clearTimeout(reportPressFailsafe);
    reportPressFailsafe = window.setTimeout(() => {
      app.reportPressActive = false;
      reportPressFailsafe = null;
    }, 3000);
  });

  const clearPress = () => {
    clearTimeout(reportPressFailsafe);
    reportPressFailsafe = null;
    if (!app.reportPressActive) {
      return;
    }
    app.reportPressActive = false;
    if (app.reports) {
      renderDoctorView();
    }
  };

  document.addEventListener("pointerup", clearPress);
  document.addEventListener("pointercancel", clearPress);
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
  const sourceType = button.dataset.reviewSource || "CompletedCycle";
  const reviewRecordId = Number(button.dataset.reviewRecordId || button.dataset.completedCycleId);
  if (!Number.isInteger(reviewRecordId) || reviewRecordId <= 0) {
    return;
  }

  if (!confirm("Confirm exclusion of this exception?\n\nThis keeps the record excluded from normal metrics and clears it from the review queue.")) {
    return;
  }

  button.disabled = true;
  try {
    const recordPath = sourceType === "AbortedAssignment"
      ? `aborted-assignments/${reviewRecordId}`
      : `cycles/${reviewRecordId}`;
    const response = await fetch(`/api/reports/${recordPath}/confirm-exclusion`, {
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

// Workshop "Current Reality": a gentle, plain-English summary of recent schedule fit, read from the
// same /api/reports payload (scheduleFit) the Reports page consumes. View-gated - only ever called
// for the Workshop view, and it only writes into its own #workshopCurrentReality container.
function renderWorkshop() {
  const target = document.getElementById("workshopCurrentReality");
  if (!target) {
    return;
  }

  // Unavailable: reports failed to load, or internal admin access is required (no reports payload).
  // The Reports access prompt is reports-page-only; Workshop shows a calm fallback and recovers on
  // the next 60s refresh.
  if (!app.reports) {
    target.innerHTML = `<p class="workshop-note">Current Reality couldn't load right now.</p>`;
    return;
  }

  const fit = app.reports.scheduleFit;
  const rangeLabel = app.reports.rangeLabel || "the selected window";

  // Empty: no completed cases carrying expected allocation in this window, so there is nothing to
  // summarize. Framed gently, never as a problem.
  if (!fit || !fit.overall || (fit.scheduleFitCycleCount || 0) === 0) {
    target.innerHTML = `
      <p class="workshop-reality-window">${escapeHtml(rangeLabel)}</p>
      <p class="workshop-note">No completed cases with expected allocation in this window yet, so there's nothing to summarize.</p>
    `;
    return;
  }

  const overall = fit.overall;
  const utilization = formatUtilizationPercent(overall.utilizationRatio);
  const stats = [
    ["Cases analyzed", `${fit.scheduleFitCycleCount} of ${fit.includedCycleCount}`],
    ["Expected blocks", formatBlocks(overall.totalExpectedBlocks)],
    ["Actual case-flow blocks", formatBlocks(overall.totalActualBlocks), "Observed case-flow time converted into schedule-sized blocks for easier comparison."],
    ["Schedule debt", formatWholeMinutes(overall.totalDebtMinutes), "Time cases ran over expected allocation. Useful for planning, not blame."],
    ["Raw slack observed", formatWholeMinutes(overall.totalSlackMinutes), "Time cases ran under expected allocation. It is observed slack, not automatically reusable capacity."],
    ["Utilization vs expected", utilization, "How observed case-flow time compares with expected allocated time for the selected range."]
  ];

  const tiles = stats.map(([label, value, helpText]) => `
    <div class="workshop-stat">
      <span class="workshop-stat-label">${escapeHtml(label)}${helpText ? renderHelpIcon(helpText) : ""}</span>
      <strong class="workshop-stat-value">${escapeHtml(value)}</strong>
    </div>
  `).join("");

  target.innerHTML = `
    <p class="workshop-reality-window">${escapeHtml(rangeLabel)}</p>
    <div class="workshop-reality-grid">${tiles}</div>
    <p class="workshop-reality-explainer">
      Across these cases, measured case flow ran about ${escapeHtml(utilization)}.
      &ldquo;Schedule debt&rdquo; is time cases ran over their expected allocation; &ldquo;raw slack
      observed&rdquo; is time they ran under &mdash; raw slack is an observation here, not capacity
      that can automatically be reclaimed.
    </p>
  `;
}

// Minutes as a whole number (e.g. "45 min"). Non-finite input degrades to an em dash.
function formatWholeMinutes(value) {
  return Number.isFinite(value) ? `${Math.round(value)} min` : "—";
}

// Blocks to one decimal (e.g. "8.0 blocks"). Non-finite input degrades to an em dash.
function formatBlocks(value) {
  return Number.isFinite(value) ? `${(Math.round(value * 10) / 10).toFixed(1)} blocks` : "—";
}

// Utilization ratio (measured / expected) as a whole percent (e.g. "112% of expected"). Null or
// non-finite ratio degrades to an em dash.
function formatUtilizationPercent(ratio) {
  return Number.isFinite(ratio) ? `${Math.round(ratio * 100)}% of expected` : "—";
}

// ---------------------------------------------------------------------------
// Workshop projection preset selection (progressive enhancement, planned/explanatory only).
// The five preset cards are static HTML and stay useful with no JS. This only ADDS selection:
// clicking or keyboard-activating a card reveals that preset's hidden detail copy in the shared
// #workshopPresetDetail panel, with a Planned badge and a fixed disclaimer. It never runs a
// projection, mutates state, calls an API, or writes outside the Workshop preset panel. Wired
// only from the Workshop boot branch, so these listeners never exist on other pages.
// ---------------------------------------------------------------------------
function wireWorkshopPresetSelection() {
  // Cards are static and never re-rendered, but delegated listeners keep this consistent with the
  // rest of board.js and robust to any future re-render. Scoped by the data-preset-id selector.
  document.addEventListener("click", handleWorkshopPresetActivate);
  document.addEventListener("keydown", handleWorkshopPresetKeydown);
}

function handleWorkshopPresetActivate(event) {
  if (!(event.target instanceof Element)) {
    return;
  }
  const card = event.target.closest('.workshop-card[data-preset-id]');
  if (!card) {
    return;
  }
  selectWorkshopPreset(card);
}

function handleWorkshopPresetKeydown(event) {
  if (event.key !== "Enter" && event.key !== " " && event.key !== "Spacebar") {
    return;
  }
  if (!(event.target instanceof Element)) {
    return;
  }
  // Only the focused card itself activates (it carries role="button" and tabindex).
  const card = event.target.closest('.workshop-card[data-preset-id]');
  if (!card || card !== event.target) {
    return;
  }
  event.preventDefault(); // Space must not scroll the page; Enter must not double-fire.
  selectWorkshopPreset(card);
}

function selectWorkshopPreset(card) {
  const cards = document.querySelectorAll('.workshop-card[data-preset-id]');
  cards.forEach(item => {
    const selected = item === card;
    item.classList.toggle("is-selected", selected);
    item.setAttribute("aria-pressed", selected ? "true" : "false");
  });

  const panel = document.getElementById("workshopPresetDetail");
  if (!panel) {
    return;
  }

  const title = card.querySelector(".workshop-card-head h4")?.textContent?.trim() || "Preset";
  const detail = readPresetSource(card, ".workshop-preset-detail-source");
  // The only preset-specific projection content. The four readiness buckets below are
  // preset-agnostic UI copy, so they stay inline rather than in a per-preset definition map.
  const assumption = readPresetSource(card, ".workshop-preset-assumption-source");

  panel.innerHTML = `
    <header class="workshop-preset-detail-head">
      <h4 class="workshop-preset-detail-title">${escapeHtml(title)}</h4>
      <span class="workshop-status">Planned</span>
    </header>
    <p class="workshop-preset-detail-text">${escapeHtml(detail)}</p>
    ${renderProjectionReadiness(assumption)}
    <p class="workshop-preset-detail-disclaimer">Planned: selecting this preset shows this explanation only. It does not run a projection, change the schedule, or alter any live data.</p>
  `;
}

// Reads and normalizes the whitespace of a hidden source block inside a preset card.
function readPresetSource(card, selector) {
  const source = card.querySelector(selector);
  return source ? source.textContent.trim().replace(/\s+/g, " ") : "";
}

// Projection readiness scaffold: the four-part honesty separation the design principle requires.
// Display-only and computes nothing - it explains what a scenario would need and is explicit that
// no output is produced. The first three buckets are fixed UI copy; the "assumptions" bucket adds
// the selected preset's one assumption line. Raw slack observed is never treated as recoverable
// capacity here, and there is no run/apply/generate affordance.
function renderProjectionReadiness(assumption) {
  const presetAssumption = assumption
    ? `<p class="workshop-readiness-assumption">${escapeHtml(assumption)}</p>`
    : "";

  return `
    <div class="workshop-readiness" aria-label="Projection readiness">
      <section class="workshop-readiness-bucket">
        <h5 class="workshop-readiness-heading">Observed today</h5>
        <p>ChairSide can show completed-case schedule-fit data for the selected report window: expected blocks, actual case-flow blocks, schedule debt, raw slack observed, and utilization versus expected allocation.</p>
      </section>
      <section class="workshop-readiness-bucket">
        <h5 class="workshop-readiness-heading">Assumptions a projection would require</h5>
        <p>A real scenario would need explicit assumptions before any output could be trusted: future demand, room/staff availability, turnover and sedation-recovery constraints, slack contiguity, and a chosen policy for whether any observed slack is usable.</p>
        ${presetAssumption}
      </section>
      <section class="workshop-readiness-bucket">
        <h5 class="workshop-readiness-heading">Scenario output &mdash; not computed yet</h5>
        <p>This preset does not compute an outcome yet. Selecting it only explains the lens and the assumptions a future scenario would need.</p>
      </section>
      <section class="workshop-readiness-bucket">
        <h5 class="workshop-readiness-heading">What ChairSide cannot know</h5>
        <p>ChairSide cannot know whether observed slack was contiguous, bookable, staffed, clinically appropriate, or desirable to reuse. The team would need to decide those assumptions before any scenario output could be meaningful.</p>
      </section>
    </div>
  `;
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
  ["reportTrendPanel", "reportFilterBar", "reportInsights", "reportMetrics", "reportDetail"].forEach(id => {
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

function renderMetric(label, value, helpText) {
  return `
    <article class="metric-card">
      <span>${escapeHtml(label)}</span>${helpText ? renderHelpIcon(helpText) : ""}
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
  const presentation = roomPresentationState(room);
  const state = presentation.primaryState;
  const roomId = getRoomId(room);
  const display = roomDisplayAssignment(room);
  const doctorColor = display.doctor ? display.doctor.color : "#8b949e";
  const procedure = display.procedure;
  const badge = renderRoomStatusBadge(presentation);
  const timer = roomTimerLabel(room);
  const fullDoctorName = display.doctor?.name || (state === "empty" ? "No assignment" : "Doctor pending");
  const doctorDisplayName = large
    ? fullDoctorName
    : (display.doctor?.shortName || cardDoctorName(fullDoctorName));
  const coinInitials = display.doctor ? doctorInitials(display.doctor.id, fullDoctorName) : "";
  const procedureDisplayCode = display.procedureCode
    ? `${display.procedureCode}${display.sedationState === "EligibleYes" ? "+SED" : ""}`
    : null;
  const procedureLabel = procedureDisplayCode
    ? formatProcedureCode(procedureDisplayCode)
    : state === "empty" ? "OPEN" : "PROCEDURE PENDING";
  const assignmentSummary = roomAssignmentSummary(room, display, state);

  const accent = display.procedureCode ? resolveProcedureAccent(display.procedureCode) : "";
  const tileStyle = `--doctor-color: ${escapeAttribute(doctorColor)}`
    + (accent ? `; --procedure-accent: ${accent}` : "");

  return `
    <article class="room-tile ${state} ${presentation.readyUrgency ? `urgency-${presentation.readyUrgency}` : ""} ${room.assignmentLocked ? "assignment-locked" : ""} ${large ? "large" : ""}" style="${tileStyle}">
      <div class="room-topline">
        <strong>Room ${roomId}</strong>
        ${badge}
      </div>
      <div class="procedure-lockup${procedure ? " procedure-lockup--chip" : state === "empty" ? "" : " procedure-lockup--pending"}">
        ${procedure ? renderProcedureIcon(procedure) : renderEmptyIcon()}
        <span>${escapeHtml(procedureLabel)}</span>
      </div>
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
  if (!presentation.readyUrgency) {
    return `<span class="room-state-badge">${stateBadge(presentation.primaryState)}</span>`;
  }

  const urgency = presentation.readyUrgency;
  return `<span class="ready-status-stack" aria-label="Ready for Doctor, ${urgency} urgency">
    <span class="ready-primary-badge">READY</span>
    <span class="ready-urgency-badge ${urgency}">${urgency.toUpperCase()}</span>
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
      doctor: app.snapshot?.doctors.find(doctor => doctor.id === doctorId) || null,
      procedureCode,
      procedure: procedureFromCode(procedureCode),
      sedationState: assignment.sedation?.state || null,
      expectedAllocation: assignment.expectedAllocation || null
    };
  }

  // Legacy active rows may lack the additive canonical read model. Preserve their truthful saved
  // display fields without treating decorated procedure codes as canonical assignment truth.
  const doctorId = room?.doctor?.id || room?.assignedDoctor || null;
  const procedureCode = stripSedationModifier(room?.procedureCode) || null;
  return {
    assignment: null,
    doctorId,
    doctor: app.snapshot?.doctors.find(doctor => doctor.id === doctorId) || room?.doctor || null,
    procedureCode,
    procedure: procedureFromCode(procedureCode) || room?.procedure || null,
    sedationState: hasSedationModifier(room?.procedureCode) ? "EligibleYes" : null,
    expectedAllocation: null
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
  const canEdit = canEditAssignment(room);
  const isDirty = canEdit && isAssignmentDraftDirty(room);
  const isReady = doctorArrivedStates.has(state);

  setDisabled("demoElapsedSelect", !isEnabled || state !== "empty" || !isDemoTimerEnabled());
  setDisabled("beginPrestageButton", !isEnabled || state !== "empty");
  setDisabled("seatButton", !isEnabled || state !== "prestaging");
  setDisabled("readyForDoctorButton", !isEnabled || !activeSeatedStates.has(state) || !room?.episodeId);
  setDisabled("saveDetailsButton", !isDirty);
  setDisabled("discardChangesButton", !isDirty);
  setDisabled("withdrawReadyButton", !isReady);
  setDisabled("cancelSeatingButton", !isEnabled || !cancelableStates.has(state));
  setDisabled("doctorArrivedButton", !isEnabled || !isReady);
  setDisabled("doctorCompleteButton", !isEnabled || state !== "doctor-in-room");
  setDisabled("roomAvailableButton", !isEnabled || state !== "turnover");
  setHidden("saveDetailsButton", !isDirty);
  setHidden("discardChangesButton", !isDirty);
  setHidden("withdrawReadyButton", !isReady);
  setHidden("cancelSeatingButton", !isEnabled || !cancelableStates.has(state));
}

function setDisabled(id, isDisabled) {
  const control = document.getElementById(id);
  if (control) {
    control.disabled = isDisabled;
  }
}

function setHidden(id, isHidden) {
  const control = document.getElementById(id);
  if (control) {
    control.hidden = isHidden;
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

// Reusable inline help bubble: a small "?" badge that reveals a short explanation on hover or
// keyboard focus. aria-label carries the full text so screen readers announce it on focus without
// needing a separate aria-describedby wire-up.
function renderHelpIcon(helpText, placement) {
  const text = escapeHtml(helpText);
  const modifier = placement === "corner" ? " help-icon--corner" : "";
  return `<span class="help-icon${modifier}" tabindex="0" aria-label="Help: ${text}">
    <span aria-hidden="true">?</span>
    <span class="help-icon-bubble" aria-hidden="true">${text}</span>
  </span>`;
}

function syncRoomSelection(room) {
  const assignment = canonicalAssignmentFromRoom(room);
  const signature = canonicalAssignmentSignature(assignment);
  const context = room
    ? `${getRoomId(room)}:${room.episodeId || room.prestageStartedAt || "available"}:${signature}`
    : `invalid:${app.roomNumber}`;
  if (app.selectionContext === context) {
    return;
  }

  app.selectionContext = context;
  app.persistedAssignmentSignature = signature;
  app.selectedDoctorId = assignment?.doctorId || null;
  app.selectedProcedureId = assignment?.procedureCode || null;
  app.sedationOn = assignment?.sedation?.state === "EligibleYes";
  const allocation = assignment?.expectedAllocation;
  app.expectedUnits = allocation?.confirmedValue ?? allocation?.suggestedValue ?? null;
  app.expectedUnitsConfirmed = allocation?.state === "ConfirmedSuggestedValue"
    || allocation?.state === "ConfirmedAdjustedValue";
  app.expectedUnitsManual = allocation?.state === "ConfirmedAdjustedValue";
  app.expectedUnitsProcedureCode = app.selectedProcedureId;
  app.expectedUnitsSedation = app.sedationOn;
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
  const procedureCode = stripSedationModifier(room?.procedureCode) || null;
  const procedure = procedureFromCode(procedureCode);
  const sedationState = !procedure
    ? "UnavailableNoProcedure"
    : procedure.sedationEligible
      ? (hasSedationModifier(room?.procedureCode) ? "EligibleYes" : "EligibleUnresolved")
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
  const procedure = procedureFromCode(app.selectedProcedureId);
  const procedureCode = procedure?.code || null;
  const suggestedValue = procedureCode ? selectedProcedureDefaultUnits() : null;
  const confirmedValue = procedureCode && app.expectedUnitsConfirmed
    ? clampExpectedUnits(app.expectedUnits ?? suggestedValue)
    : null;
  let sedationState = "UnavailableNoProcedure";
  if (procedure) {
    sedationState = procedure.sedationEligible
      ? (app.sedationOn ? "EligibleYes" : "EligibleNo")
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
    doctorId: app.selectedDoctorId || null,
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
  return canEditAssignment(room) && draftAssignmentSignature() !== app.persistedAssignmentSignature;
}

function discardAssignmentDraft() {
  app.selectionContext = null;
  syncRoomSelection(getCurrentRoom());
  renderSelectionTiles(getCurrentRoom());
  renderAssignmentGuidance(getCurrentRoom());
  setRoomControlsEnabled(getCurrentRoom());
}

function canonicalAssignmentRequest() {
  const procedure = procedureFromCode(app.selectedProcedureId);
  return {
    doctorId: app.selectedDoctorId || null,
    procedureCode: procedure?.code || null,
    sedationChoice: procedure?.sedationEligible && app.sedationOn ? "yes" : null,
    confirmedExpectedAllocationUnits: procedure && app.expectedUnitsConfirmed
      ? clampExpectedUnits(app.expectedUnits ?? selectedProcedureDefaultUnits())
      : null
  };
}

function focusFirstUnresolvedAssignmentControl() {
  const draft = draftAssignmentShape();
  const target = !draft.doctorId
    ? document.querySelector("#doctorTiles [data-doctor-id]:not(:disabled)")
    : !draft.procedureCode
      ? document.querySelector("#procedureTiles [data-procedure-id]:not(:disabled)")
      : draft.confirmedValue === null
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
  if (room.assignmentLocked || doctorArrivedStates.has(state) || state === "doctor-in-room" || state === "turnover") {
    target.textContent = doctorArrivedStates.has(state)
      ? "Assignment locked for the active Ready handoff. Withdraw Ready to make a correction."
      : "Assignment locked.";
    target.dataset.tone = "neutral";
    return;
  }
  const draft = draftAssignmentShape();
  if (!draft.doctorId && !draft.procedureCode) {
    target.textContent = state === "seated"
      ? "Assignment pending. Complete details before Ready for Doctor."
      : "Assignment pending. Details may be added now or after seating.";
    target.dataset.tone = "neutral";
    return;
  }
  const unresolved = [];
  if (!draft.doctorId) unresolved.push("doctor");
  if (!draft.procedureCode) unresolved.push("procedure");
  if (draft.procedureCode && draft.confirmedValue === null) unresolved.push("allocation confirmation");
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
    if (procedureChanged) {
      app.expectedUnitsConfirmed = false;
    }
  } else if (sedationChanged && !app.expectedUnitsManual) {
    app.expectedUnits = selectedProcedureDefaultUnits();
  }

  app.expectedUnitsProcedureCode = procedureCode;
  app.expectedUnitsSedation = app.sedationOn;
}

// Renders the expected allocation suggestion/confirmation. It stays editable throughout
// Prestaging and Seated / In Prep, then becomes read-only at the Ready handoff boundary.
function renderAllocationSelector(room) {
  const section = document.getElementById("allocationSection");
  if (!section) {
    return;
  }

  syncExpectedUnits();

  const editing = canEditAssignment(room);
  if (editing && !app.selectedProcedureId) {
    section.hidden = true;
    section.classList.remove("allocation-variable");
    return;
  }
  let units;
  if (editing) {
    units = app.expectedUnits ?? selectedProcedureDefaultUnits();
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

  const variable = isVariableProcedure(procedureFromCode(app.selectedProcedureId));
  section.classList.toggle("allocation-variable", editing && variable);

  if (hintEl) {
    hintEl.textContent = !editing
      ? "Confirmed for the locked assignment."
      : app.expectedUnitsConfirmed
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

  const confirm = document.getElementById("allocationConfirm");
  if (confirm) {
    confirm.hidden = !editing || app.expectedUnitsConfirmed;
    confirm.disabled = !editing || app.expectedUnitsConfirmed;
  }
}

// Adjusts expected units by delta while the assignment is editable, marking the value as manually set so a later
// live re-render does not snap it back to the procedure default.
function adjustExpectedUnits(delta) {
  if (!canEditAssignment(getCurrentRoom())) {
    return;
  }

  const base = app.expectedUnits ?? selectedProcedureDefaultUnits();
  const next = clampExpectedUnits(base + delta);
  if (next === app.expectedUnits) {
    return;
  }

  app.expectedUnits = next;
  app.expectedUnitsManual = true;
  app.expectedUnitsConfirmed = true;
  renderSelectionTiles(getCurrentRoom());
  renderAssignmentGuidance(getCurrentRoom());
  setRoomControlsEnabled(getCurrentRoom());
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

// Renders the sedation modifier toggle. It is interactable throughout Prestaging and Seated,
// and only for a sedation-eligible primary procedure; it defaults
// Off and only turns on when staff explicitly tap it. When the assignment is locked it
// shows the room's actual sedation status as read-only case metadata.
function renderSedationToggle(room) {
  const toggle = document.getElementById("sedationToggle");
  if (!toggle) {
    return;
  }

  const canEdit = canEditAssignment(room);
  const eligible = selectedProcedureIsSedationEligible();
  const stateName = room ? normalizeState(room) : "empty";
  const assignmentLocked = room?.assignmentLocked === true
    || doctorArrivedStates.has(stateName)
    || stateName === "doctor-in-room"
    || stateName === "turnover";
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
    // Locked: prefer the canonical read contract. The mutation envelope intentionally returns
    // the base procedure code, so the legacy +SED display decoration is only a fallback.
    interactable = false;
    isOn = room?.assignment?.sedation?.state === "EligibleYes"
      || hasSedationModifier(room?.procedureCode);
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

// True when the doctor / procedure / sedation selection controls are live. They remain
// directly editable in Prestaging and Seated / In Prep and lock at Ready.
function canEditAssignment(room) {
  if (!room) {
    return false;
  }

  return editableAssignmentStates.has(normalizeState(room))
    && !isLegacyActiveRoom(room)
    && room.assignmentLocked !== true;
}

function isLegacyActiveRoom(room) {
  return Boolean(room)
    && editableAssignmentStates.has(normalizeState(room))
    && !room.episodeId;
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

  if (!beginPrestageButton || !seatButton || !readyForDoctorButton || !saveDetailsButton || !discardChangesButton || !withdrawReadyButton || !cancelSeatingButton || !doctorArrivedButton || !doctorCompleteButton || !roomAvailableButton) {
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

  console.log("[ChairSide] Room panel click handlers bound.", { roomNumber: app.roomNumber });
  wireSelectionTiles();

  beginPrestageButton.addEventListener("click", async () => {
    if (!isConfiguredRoom(app.roomNumber) || !isRoomInState("empty")) {
      setRoomActionStatus("Begin Prestage is only available when the room is available.", "error");
      return;
    }
    setRoomActionStatus("Starting room preparation...", "pending");
    try {
      const result = await sendCanonicalRoomAction("prestage", {});
      applyRoomMutationResult(result);
      setRoomActionStatus("Prestaging started. Assignment details can be added now or after seating.", "success");
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
    if (!isConfiguredRoom(app.roomNumber)) {
      setRoomActionStatus("This room is not configured.", "error");
      return;
    }

    if (!isRoomInState("prestaging")) {
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
      const result = await sendReadyForDoctor(canonicalAssignmentRequest());
      applyRoomMutationResult(result);
      setRoomActionStatus("Ready for doctor.", "success");
    } catch (error) {
      console.error("[ChairSide] Ready for Doctor failed.", { roomNumber: app.roomNumber, error });
      setRoomActionStatus(error.message || "Failed to mark ready for doctor.", "error");
      focusFirstUnresolvedAssignmentControl();
    }
  });

  withdrawReadyButton.addEventListener("click", async () => {
    if (!doctorArrivedStates.has(currentRoomState())) {
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
      const result = await sendRoomAction(app.roomNumber, "cancel-seating", "Cancel Seating");
      applyRoomMutationResult(result);
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
        applyRoomMutationResult(await response.json());
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
      const result = await sendRoomAction(app.roomNumber, "doctor-complete", "Doctor Complete");
      applyRoomMutationResult(result);
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
      const result = await sendRoomAction(app.roomNumber, "available", "Room Available");
      applyRoomMutationResult(result);
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
let reportPressFailsafe = null;
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
    renderAssignmentGuidance(getCurrentRoom());
    setRoomControlsEnabled(getCurrentRoom());
  });

  wireTileGroup(procedureTiles, "[data-procedure-id]", "procedureId", button => {
    const procedureChanged = app.selectedProcedureId !== button.dataset.procedureId;
    app.selectedProcedureId = button.dataset.procedureId;
    if (procedureChanged) {
      app.sedationOn = false;
      app.expectedUnitsConfirmed = false;
    }
    renderSelectionTiles(getCurrentRoom());
    renderAssignmentGuidance(getCurrentRoom());
    setRoomControlsEnabled(getCurrentRoom());
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
      if (isAssignmentDraftDirty()) {
        discardAssignmentDraft();
        setRoomActionStatus("Changes discarded.", "pending");
      }
    }
  });

  const sedationToggle = document.getElementById("sedationToggle");
  sedationToggle?.addEventListener("click", () => {
    if (sedationToggle.disabled || !selectedProcedureIsSedationEligible()) {
      return;
    }

    app.sedationOn = !app.sedationOn;
    renderSelectionTiles(getCurrentRoom());
    renderAssignmentGuidance(getCurrentRoom());
    setRoomControlsEnabled(getCurrentRoom());
  });

  document.getElementById("allocationMinus")?.addEventListener("click", () => adjustExpectedUnits(-1));
  document.getElementById("allocationPlus")?.addEventListener("click", () => adjustExpectedUnits(1));
  document.getElementById("allocationConfirm")?.addEventListener("click", () => {
    if (!canEditAssignment(getCurrentRoom()) || !app.selectedProcedureId) {
      return;
    }
    app.expectedUnits = app.expectedUnits ?? selectedProcedureDefaultUnits();
    app.expectedUnitsConfirmed = true;
    renderSelectionTiles(getCurrentRoom());
    renderAssignmentGuidance(getCurrentRoom());
    setRoomControlsEnabled(getCurrentRoom());
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

async function sendCanonicalRoomAction(action, body) {
  return sendCanonicalMutation(`/api/rooms/${app.roomNumber}/${action}`, "POST", body, action);
}

async function sendSaveDetails(assignment) {
  return sendCanonicalMutation(
    `/api/rooms/${app.roomNumber}/assignment-details`,
    "PUT",
    assignment,
    "save details");
}

async function sendSeatRoom(assignment) {
  return sendCanonicalMutation(
    `/api/rooms/${app.roomNumber}/seat`,
    "POST",
    { assignment },
    "seat room");
}

async function sendReadyForDoctor(assignment) {
  return sendCanonicalMutation(
    `/api/rooms/${app.roomNumber}/ready-for-doctor`,
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
    throw new Error(await readErrorMessage(response, `${label} failed with HTTP ${response.status}.`));
  }
  return response.json();
}

function applyRoomMutationResult(result) {
  const room = result?.room || result;
  if (!room || !app.snapshot?.rooms) {
    return;
  }
  const roomId = getRoomId(room);
  const index = app.snapshot.rooms.findIndex(item => getRoomId(item) === roomId);
  if (index < 0) {
    return;
  }
  app.snapshot.rooms[index] = room;
  app.selectionContext = null;
  app.lastSnapshotAt = Date.now();
  renderRoomPanel();
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
  const text = await response.text();
  if (!text) {
    return fallback;
  }
  try {
    const error = JSON.parse(text);
    const labels = {
      doctorId: "doctor",
      procedureCode: "procedure",
      sedationChoice: "sedation",
      confirmedExpectedAllocationUnits: "allocation confirmation"
    };
    const unresolved = (error.unresolvedFields || []).map(field => labels[field] || field);
    return unresolved.length
      ? `${error.message || fallback} Still needed: ${unresolved.join(", ")}.`
      : error.message || fallback;
  } catch {
    return text;
  }
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
