import { app } from "./application-state.js";
import {
  registerConnectionStatusRefresh,
  setConnectionStatus,
  updateConnectionStatus
} from "./connection-status.js";
import { escapeAttribute, escapeHtml } from "./dom-utils.js";
import { pageContext } from "./page-context.js";
import { connectRealtime, registerBoardPolling } from "./realtime-polling.js";
import { createReportData } from "./report-data.js";
import { createReports } from "./reports.js";
import { createRoomCardPresentation } from "./room-card.js";
import { createRoomWorkflow } from "./room-workflow.js";
import {
  registerGeneralRender,
  registerReportRefresh
} from "./runtime-scheduling.js";
import { createWorkshop } from "./workshop.js";

const staffLoungeRoomNumber = 99;
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

let reports = null;
const reportData = pageContext.isReports || pageContext.isDoctor || pageContext.isWorkshop
  ? createReportData({
      onAccessDenied: statusCode => reports?.renderAccessPrompt(statusCode),
      onDataChanged: () => render()
    })
  : null;

reports = pageContext.isReports || pageContext.isDoctor
  ? createReports({
      context: pageContext,
      reportData,
      getSnapshot: () => app.snapshot,
      renderPage: render,
      getDoctorName: doctorName,
      getDoctorIdentity: reportDoctorIdentity,
      procedure: {
        accentStyle: procedureAccentStyle,
        formatCode: formatProcedureCode,
        hasSedationModifier,
        renderBadge: renderProcedureBadge
      }
    })
  : null;

const workshop = pageContext.isWorkshop
  ? createWorkshop({ getReports: reportData.getReports })
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
    reportData.useLastSevenDays();
    await reportData.load();
    reports.wire();
  }

  if (pageContext.isDoctor) {
    // Use month-to-date for the cockpit so metrics reflect the current calendar month.
    reportData.useMonthToDate();
    reportData.load();
    registerReportRefresh(reportData.reload);
    reports.wire();
  }

  if (pageContext.isWorkshop) {
    // Current Reality summarizes a recent, stable window while carrying enough completed cases
    // to be meaningful.
    reportData.useLastThirtyDays();
    reportData.load();
    registerReportRefresh(reportData.reload);
    workshop.wire();
  }

  wireDoctorViewMenu();
  connectRealtime({
    applySnapshot,
    refreshReportsAfterBoardUpdate: pageContext.isReports ? reportData.reload : null,
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
    reports.render();
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

  // Reporting remains pinned to the route-selected doctor while operational room rendering stays here.
  reports.renderDoctorCockpit(doctor);

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

const doctorReportIdentity = {
  otte: { initials: "LDO", color: "#dc2626" },
  pledger: { initials: "JWP", color: "#16a34a" },
  gibson: { initials: "JEG", color: "#7c3aed" },
  schroeder: { initials: "NDS", color: "#eab308" }
};

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


function reportDoctorIdentity(doctorId, name) {
  const doctor = (app.snapshot?.doctors || []).find(item => item.id === doctorId);
  const fallback = doctorReportIdentity[doctorId];
  return {
    initials: doctorInitials(doctorId, name),
    color: doctor?.color || fallback?.color || "#64748b"
  };
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
