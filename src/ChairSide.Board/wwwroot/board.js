import { app } from "./application-state.js";
import {
  registerConnectionStatusRefresh,
  setConnectionStatus,
  updateConnectionStatus
} from "./connection-status.js";
import {
  wirePressInterruptionGuard
} from "./common-interactions.js";
import { escapeAttribute, escapeHtml, renderHelpIcon } from "./dom-utils.js";
import { formatDateTime, formatDuration } from "./format-utils.js";
import { pageContext } from "./page-context.js";
import { connectRealtime, registerBoardPolling } from "./realtime-polling.js";
import { createRoomCardPresentation } from "./room-card.js";
import { createRoomWorkflow } from "./room-workflow.js";
import {
  adminRequestHeaders,
  clearAdminToken,
  storeAdminToken
} from "./request-utils.js";
import {
  registerGeneralRender,
  registerReportRefresh
} from "./runtime-scheduling.js";
import { createWorkshop } from "./workshop.js";

const staffLoungeRoomNumber = 99;
const trendMinimumComparisonCases = 3;
const trendAboutSameThresholdSeconds = 60;

const {
  normalizeState,
  renderRoomTile,
  roomAssignedDoctorId
} = createRoomCardPresentation({
  getSnapshot: () => app.snapshot,
  getRoomId,
  getNowMs: boardNowMs,
  getAgingMinutes,
  getStaleMinutes,
  getDoctorInitials: doctorInitials,
  procedure: {
    fromCode: procedureFromCode,
    formatCode: formatProcedureCode,
    hasSedationModifier,
    renderEmptyIcon,
    renderIcon: renderProcedureIcon,
    resolveAccent: resolveProcedureAccent,
    stripSedationModifier
  }
});

const roomWorkflow = createRoomWorkflow({
  getSnapshot: () => app.snapshot,
  getRoomNumber: () => app.roomNumber,
  getRoomId,
  normalizeState,
  isTilePressActive: () => app.tilePressActive,
  isDemoTimerEnabled,
  applyRoomMutation: applyRoomMutationToSnapshot,
  showRoomTokenPrompt,
  procedure: {
    fromCode: procedureFromCode,
    hasSedationModifier,
    renderIcon: renderProcedureIcon,
    resolveAccent: resolveProcedureAccent,
    stripSedationModifier
  }
});

const workshop = pageContext.isWorkshop
  ? createWorkshop({ getReports: () => app.reports })
  : null;

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
  if (pageContext.isReports) {
    initDateRange();
    await loadReports();
    wireReportsActions();
    wireReportFilters();
    wireDateRange();
    wireReportPressGuard();
  }

  if (pageContext.isDoctor) {
    // Use month-to-date for the cockpit so metrics reflect the current calendar month.
    // app.dateRange is set before loadReports() so reportsRequestUrl() picks up the right from/to.
    const mtd = computePresetRange("mtd");
    app.dateRange = { preset: "mtd", start: mtd.start, end: mtd.end };
    loadReports();
    registerReportRefresh(loadReports);
    wireDoctorCockpitActions();
    wireDoctorCockpitPressGuard();
  }

  if (pageContext.isWorkshop) {
    // Current Reality summarizes a recent, stable window. last30 is current enough to read as
    // "now" while carrying enough completed cases to be meaningful. app.dateRange is set before
    // loadReports() so reportsRequestUrl() bounds the completed-cycle population to that window.
    const last30 = computePresetRange("last30");
    app.dateRange = { preset: "last30", start: last30.start, end: last30.end };
    loadReports();
    registerReportRefresh(loadReports);
    workshop.wire();
  }

  wireDoctorViewMenu();
  connectRealtime({
    applySnapshot,
    refreshReportsAfterBoardUpdate: pageContext.isReports ? loadReports : null,
    render,
    refreshConnectionStatus: updateConnectionStatus,
    setConnectionStatus,
    loadBoard
  });
  registerBoardPolling(loadBoard);
  registerGeneralRender(render);
  registerConnectionStatusRefresh();
  updateConnectionStatus();

  if (pageContext.isRoom && !isStaffLoungeRoom()) {
    roomWorkflow.wire();
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
        clearAdminToken();
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

function render() {
  if (!app.snapshot) {
    return;
  }

  updateConnectionStatus();
  renderLegend();
  populateDoctorViewMenu();

  if (pageContext.isMaster) {
    renderMaster();
  }

  if (pageContext.isRoom) {
    renderRoomPanel();
  }

  if (pageContext.isDoctor) {
    renderDoctorView();
  }

  if (pageContext.isReports) {
    renderReports();
  }

  if (pageContext.isWorkshop) {
    workshop.render();
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

  if (pageContext.isDoctor && app.doctorId && app.snapshot) {
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

  const currentDoctorId = pageContext.isDoctor ? app.doctorId : null;
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
  roomWorkflow.render(room);
  renderRoomTokenPrompt();
  applyDemoTimerVisibility();
  populateDemoTimerSelect();
}

function isStaffLoungeRoom() {
  return pageContext.isRoom && app.roomNumber === staffLoungeRoomNumber;
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

  wirePressInterruptionGuard({
    pressTarget: shell,
    selector: "[data-report-doctor-id], [data-report-doctor-tab], .report-table button",
    onCatchUp: () => {
      if (app.reports) {
        renderReports();
      }
    }
  });
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
  wirePressInterruptionGuard({
    pressTarget: document,
    selector: "[data-report-doctor-tab]",
    onCatchUp: () => {
      if (app.reports) {
        renderDoctorView();
      }
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
    roomWorkflow.setStatus("Room token saved. Try the action again.", "success");
  });

  clearButton?.addEventListener("click", () => {
    clearStoredRoomToken();
    app.roomToken = "";
    app.roomTokenPromptVisible = true;
    renderRoomTokenPrompt();
    roomWorkflow.setStatus("Room token cleared.", "pending");
  });
}

function showRoomTokenPrompt(statusCode) {
  if (statusCode === 403) {
    clearStoredRoomToken();
    app.roomToken = "";
  }

  app.roomTokenPromptVisible = true;
  renderRoomTokenPrompt();
  roomWorkflow.setStatus(
    statusCode === 403
      ? "Room token was rejected. Enter the current room access token."
      : "Room access token required.",
    "error");
}

function roomTokenStorageKey(roomNumber = pageContext.roomNumber) {
  return `chairside-room-token-${roomNumber}`;
}

function getStoredRoomToken() {
  if (!pageContext.isRoom) {
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

    storeAdminToken(token);
    loadReports();
  });

  clearButton?.addEventListener("click", () => {
    clearAdminToken();
    renderReportsAccessPrompt(401);
  });
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

function applyRoomMutationToSnapshot(room) {
  if (!room || !app.snapshot?.rooms) {
    return;
  }
  const roomId = getRoomId(room);
  const index = app.snapshot.rooms.findIndex(item => getRoomId(item) === roomId);
  if (index < 0) {
    return;
  }
  app.snapshot.rooms[index] = room;
  app.lastSnapshotAt = Date.now();
  renderRoomPanel();
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
    } catch { /* app may not be initialised yet */ }

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
