import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const moduleUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/reports.js",
  import.meta.url);
const boardUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/board.js",
  import.meta.url);
const applicationStateUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/application-state.js",
  import.meta.url);
const domUtilsUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/dom-utils.js",
  import.meta.url);
const formatUtilsUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/format-utils.js",
  import.meta.url);
const moduleSource = await readFile(moduleUrl, "utf8");
const boardSource = await readFile(boardUrl, "utf8");
const applicationStateSource = await readFile(applicationStateUrl, "utf8");
const domUtilsSource = await readFile(domUtilsUrl, "utf8");
const formatUtilsSource = await readFile(formatUtilsUrl, "utf8");

const commonInteractionsDataUrl = `data:text/javascript;base64,${Buffer.from(`
export function wirePressInterruptionGuard(options) {
  globalThis.__chairsideReportsHarness.pressGuards.push(options);
}`).toString("base64")}`;
const requestUtilsDataUrl = `data:text/javascript;base64,${Buffer.from(`
export function adminRequestHeaders() {
  return globalThis.__chairsideReportsHarness.adminHeaders;
}
export function clearAdminToken() {
  globalThis.__chairsideReportsHarness.clearCount += 1;
}
export function storeAdminToken(token) {
  globalThis.__chairsideReportsHarness.storedTokens.push(token);
}`).toString("base64")}`;
const domUtilsDataUrl = `data:text/javascript;base64,${Buffer.from(domUtilsSource).toString("base64")}`;
const formatUtilsDataUrl = `data:text/javascript;base64,${Buffer.from(formatUtilsSource).toString("base64")}`;
const moduleWithDataImports = moduleSource
  .replace('"./common-interactions.js"', JSON.stringify(commonInteractionsDataUrl))
  .replace('"./request-utils.js"', JSON.stringify(requestUtilsDataUrl))
  .replace('"./dom-utils.js"', JSON.stringify(domUtilsDataUrl))
  .replace('"./format-utils.js"', JSON.stringify(formatUtilsDataUrl));
const moduleDataUrl = `data:text/javascript;base64,${Buffer.from(moduleWithDataImports).toString("base64")}`;
const { createReports } = await import(moduleDataUrl);

class FakeElement {
  constructor(id = "") {
    this.id = id;
    this.dataset = {};
    this.hidden = false;
    this.innerHTML = "";
    this.attributes = new Map();
    this.classes = new Set();
    this.listeners = new Map();
    this.classList = {
      add: name => this.classes.add(name),
      remove: name => this.classes.delete(name),
      toggle: (name, enabled) => {
        if (enabled) {
          this.classes.add(name);
        } else {
          this.classes.delete(name);
        }
      }
    };
    this.style = {
      values: new Map(),
      setProperty: (name, value) => this.style.values.set(name, value)
    };
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) || [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  async dispatch(type, event) {
    for (const listener of this.listeners.get(type) || []) {
      await listener(event);
    }
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  getAttribute(name) {
    return this.attributes.get(name);
  }
}

class FakeDocument {
  constructor(elements, filterChips, shell) {
    this.elements = elements;
    this.filterChips = filterChips;
    this.shell = shell;
    this.listeners = new Map();
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) || [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  async dispatch(type, event) {
    for (const listener of this.listeners.get(type) || []) {
      await listener(event);
    }
  }

  getElementById(id) {
    return this.elements.get(id) || null;
  }

  querySelector(selector) {
    return selector === ".reports-shell" ? this.shell : null;
  }

  querySelectorAll(selector) {
    if (selector === "#reportFilterBar .report-filter-chip") {
      return this.filterChips;
    }
    if (selector === ".report-range-chip") {
      return [];
    }
    return [];
  }
}

function targetFor(matches) {
  return {
    closest(selector) {
      return matches.get(selector) || null;
    }
  };
}

function allocation(count, net = 0) {
  return {
    allocationVarianceCycleCount: count,
    netAllocationVarianceMinutes: net,
    casesOverExpectedAllocation: net > 0 ? count : 0,
    casesUnderExpectedAllocation: net < 0 ? count : 0,
    casesAtExpectedAllocation: net === 0 ? count : 0,
    adjustedAllocationCycleCount: 0
  };
}

function reportPayload({
  otteCases = 0,
  pledgerCases = 2,
  marker = "initial"
} = {}) {
  return {
    marker,
    completedRoomCyclesCount: otteCases + pledgerCases,
    doctorSummaries: [
      {
        assignedDoctor: "otte",
        allocation: allocation(otteCases, otteCases ? 4 : 0)
      },
      {
        assignedDoctor: "pledger",
        allocation: allocation(pledgerCases, pledgerCases ? -3 : 0)
      }
    ],
    recentCompletedCycles: [],
    exceptionReviewRecords: [],
    procedureSummaries: [],
    baseProcedureSummaries: [],
    rangeLabel: "Jul 1 - Jul 29"
  };
}

function createFilterChip(group, value) {
  const chip = new FakeElement();
  chip.dataset.filterGroup = group;
  chip.dataset.filterValue = value;
  return chip;
}

function createHarness({
  context = { isReports: true, isDoctor: false },
  payload = reportPayload(),
  requestResponses = []
} = {}) {
  const elements = new Map();
  const ids = context.isReports
    ? [
        "reportFilterBar",
        "doctorReportDashboard",
        "doctorReportCards",
        "selectedDoctorPanel"
      ]
    : [
        "doctorCockpit",
        "doctorContextCard",
        "selectedDoctorPanel"
      ];
  ids.forEach(id => elements.set(id, new FakeElement(id)));
  const shell = new FakeElement("reportsShell");
  const filterChips = [
    createFilterChip("sedation", "all"),
    createFilterChip("sedation", "sedation"),
    createFilterChip("sedation", "non-sedation"),
    createFilterChip("grouping", "base"),
    createFilterChip("grouping", "variant")
  ];
  const document = new FakeDocument(elements, filterChips, shell);
  const snapshot = {
    doctors: [
      { id: "otte", name: "Dr. Otte", color: "#dc2626" },
      { id: "pledger", name: "Dr. Pledger", color: "#16a34a" }
    ]
  };
  let reportsPayload = payload;
  let version = 1;
  let dateRange = {
    preset: "last7",
    start: "2026-07-23",
    end: "2026-07-29"
  };
  let reloadCount = 0;
  let renderPageCount = 0;
  const requests = [];
  const alerts = [];
  const harness = {
    adminHeaders: { "X-ChairSide-Admin-Token": "admin-token" },
    alerts,
    clearCount: 0,
    document,
    elements,
    filterChips,
    pressGuards: [],
    requests,
    storedTokens: []
  };
  globalThis.__chairsideReportsHarness = harness;
  globalThis.document = document;
  globalThis.Element = FakeElement;
  globalThis.confirm = () => true;
  globalThis.alert = message => alerts.push(message);

  const reportData = {
    getDateRange: () => dateRange,
    getReports: () => reportsPayload,
    getVersion: () => version,
    load: async () => {
      reloadCount += 1;
    },
    reload: async () => {
      reloadCount += 1;
    },
    setDateRange: value => {
      dateRange = { ...value };
    },
    usePreset: preset => {
      dateRange = { preset, start: "preset-start", end: "preset-end" };
    }
  };
  const reports = createReports({
    context,
    reportData,
    getSnapshot: () => snapshot,
    renderPage: () => {
      renderPageCount += 1;
    },
    getDoctorName: doctorId =>
      snapshot.doctors.find(doctor => doctor.id === doctorId)?.name || doctorId,
    getDoctorIdentity: (doctorId, name) => ({
      initials: doctorId === "otte" ? "LDO" : doctorId === "pledger" ? "JWP" : name.slice(0, 2),
      color: snapshot.doctors.find(doctor => doctor.id === doctorId)?.color || "#64748b"
    }),
    procedure: {
      accentStyle: () => "",
      formatCode: code => code,
      hasSedationModifier: code => String(code || "").includes("+SED"),
      renderBadge: code => `<span>${code}</span>`
    },
    request: async (url, options) => {
      requests.push({ url, options });
      return requestResponses.shift() || { ok: true, status: 200 };
    }
  });

  return {
    ...harness,
    get reloadCount() {
      return reloadCount;
    },
    get renderPageCount() {
      return renderPageCount;
    },
    reports,
    setPayload(value) {
      reportsPayload = value;
      version += 1;
    }
  };
}

function selectedCard(html, doctorId) {
  const pattern = new RegExp(
    `class="doctor-report-card [^"]*is-selected[^"]*"[^>]*data-report-doctor-id="${doctorId}"`);
  return pattern.test(html);
}

test("wire initializes only the applicable Reports or Doctor interaction surface", () => {
  const reportsHarness = createHarness();
  reportsHarness.reports.wire();
  assert.equal(reportsHarness.document.listeners.get("keydown").length, 1);
  assert.equal(reportsHarness.pressGuards.length, 1);
  assert.equal(
    reportsHarness.pressGuards[0].selector,
    "[data-report-doctor-id], [data-report-doctor-tab], .report-table button");

  const doctorHarness = createHarness({
    context: { isReports: false, isDoctor: true }
  });
  doctorHarness.reports.wire();
  assert.equal(doctorHarness.document.listeners.has("keydown"), false);
  assert.equal(doctorHarness.pressGuards.length, 1);
  assert.equal(
    doctorHarness.pressGuards[0].selector,
    "[data-report-doctor-tab]");
});

test("filter, doctor, and tab state survive ordinary rerenders and refreshed payloads", async () => {
  const harness = createHarness();
  harness.reports.wire();
  harness.reports.render();

  const grid = harness.elements.get("doctorReportCards");
  const panel = harness.elements.get("selectedDoctorPanel");
  assert.equal(harness.filterChips[0].getAttribute("aria-pressed"), "true");
  assert.equal(harness.filterChips[3].getAttribute("aria-pressed"), "true");
  assert.equal(selectedCard(grid.innerHTML, "pledger"), true);

  await harness.elements.get("reportFilterBar").dispatch("click", {
    target: targetFor(new Map([
      [".report-filter-chip", harness.filterChips[1]]
    ]))
  });
  await harness.elements.get("reportFilterBar").dispatch("click", {
    target: targetFor(new Map([
      [".report-filter-chip", harness.filterChips[4]]
    ]))
  });

  const otteCard = new FakeElement();
  otteCard.dataset.reportDoctorId = "otte";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([
      ["[data-report-doctor-id]", otteCard]
    ]))
  });
  const flowTab = new FakeElement();
  flowTab.dataset.reportDoctorTab = "flow";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([
      ["[data-report-doctor-tab]", flowTab]
    ]))
  });

  harness.setPayload(reportPayload({
    otteCases: 1,
    pledgerCases: 3,
    marker: "refreshed"
  }));
  harness.reports.render();

  assert.equal(harness.filterChips[1].getAttribute("aria-pressed"), "true");
  assert.equal(harness.filterChips[4].getAttribute("aria-pressed"), "true");
  assert.equal(selectedCard(grid.innerHTML, "otte"), true);
  assert.match(
    panel.innerHTML,
    /aria-selected="true" data-report-doctor-tab="flow"/);
  assert.match(panel.innerHTML, /Observed Load/);
});

test("default doctor selection uses first doctor with cases then first roster doctor", () => {
  const withCases = createHarness();
  withCases.reports.render();
  assert.equal(
    selectedCard(withCases.elements.get("doctorReportCards").innerHTML, "pledger"),
    true);

  const withoutCases = createHarness({
    payload: reportPayload({ otteCases: 0, pledgerCases: 0 })
  });
  withoutCases.reports.render();
  assert.equal(
    selectedCard(withoutCases.elements.get("doctorReportCards").innerHTML, "otte"),
    true);
});

test("Doctor View stays pinned to the route doctor while its selected tab survives refresh", async () => {
  const harness = createHarness({
    context: { isReports: false, isDoctor: true },
    payload: reportPayload({ otteCases: 3, pledgerCases: 2 })
  });
  const routeDoctor = { id: "pledger", name: "Dr. Pledger", color: "#16a34a" };
  harness.reports.wire();
  harness.reports.renderDoctorCockpit(routeDoctor);

  const panel = harness.elements.get("selectedDoctorPanel");
  assert.match(panel.innerHTML, /Dr\. Pledger/);
  assert.doesNotMatch(panel.innerHTML, /Dr\. Otte/);

  const auditTab = new FakeElement();
  auditTab.dataset.reportDoctorTab = "audit";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([
      ["[data-report-doctor-tab]", auditTab]
    ]))
  });
  assert.equal(harness.renderPageCount, 1);

  harness.setPayload(reportPayload({
    otteCases: 4,
    pledgerCases: 1,
    marker: "doctor-refresh"
  }));
  harness.reports.renderDoctorCockpit(routeDoctor);
  assert.match(panel.innerHTML, /Dr\. Pledger/);
  assert.match(
    panel.innerHTML,
    /aria-selected="true" data-report-doctor-tab="audit"/);
});

test("exception actions preserve preferred, legacy, and bodyless request contracts", async () => {
  const harness = createHarness();
  harness.reports.wire();

  const preferred = new FakeElement();
  preferred.dataset.completedCycleId = "42";
  preferred.dataset.roomId = "3";
  preferred.dataset.seatedAt = "2026-07-29T12:00:00Z";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([
      ["[data-action='mark-exception']", preferred]
    ]))
  });
  assert.deepEqual(JSON.parse(harness.requests[0].options.body), {
    completedCycleId: 42,
    roomId: 3
  });

  const legacy = new FakeElement();
  legacy.dataset.completedCycleId = "";
  legacy.dataset.roomId = "4";
  legacy.dataset.seatedAt = "2026-07-29T13:00:00Z";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([
      ["[data-action='mark-exception']", legacy]
    ]))
  });
  assert.deepEqual(JSON.parse(harness.requests[1].options.body), {
    roomId: 4,
    seatedAt: "2026-07-29T13:00:00Z"
  });

  const completed = new FakeElement();
  completed.dataset.reviewSource = "CompletedCycle";
  completed.dataset.reviewRecordId = "77";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([
      ["[data-action='confirm-exclusion']", completed]
    ]))
  });

  const aborted = new FakeElement();
  aborted.dataset.reviewSource = "AbortedAssignment";
  aborted.dataset.reviewRecordId = "88";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([
      ["[data-action='confirm-exclusion']", aborted]
    ]))
  });

  assert.equal(
    harness.requests[2].url,
    "/api/reports/cycles/77/confirm-exclusion");
  assert.equal(
    harness.requests[3].url,
    "/api/reports/aborted-assignments/88/confirm-exclusion");
  for (const call of harness.requests) {
    assert.equal(call.options.method, "POST");
    assert.equal(call.options.cache, "no-store");
    assert.deepEqual(
      call.options.headers["X-ChairSide-Admin-Token"],
      "admin-token");
  }
  assert.equal("body" in harness.requests[2].options, false);
  assert.equal("body" in harness.requests[3].options, false);
  assert.equal(harness.reloadCount, 4);
});

test("failed action re-enables its button and preserves module-owned selection state", async t => {
  const originalError = console.error;
  t.after(() => {
    console.error = originalError;
  });
  console.error = () => {};
  const harness = createHarness({
    requestResponses: [{ ok: false, status: 500 }]
  });
  harness.reports.wire();
  harness.reports.render();

  const selectedBefore = harness.elements.get("doctorReportCards").innerHTML;
  const button = new FakeElement();
  button.dataset.completedCycleId = "42";
  button.dataset.roomId = "3";
  button.dataset.seatedAt = "2026-07-29T12:00:00Z";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([
      ["[data-action='mark-exception']", button]
    ]))
  });

  assert.equal(button.disabled, false);
  assert.deepEqual(harness.alerts, [
    "Mark as exception failed. Please try again."
  ]);
  harness.reports.render();
  assert.equal(
    harness.elements.get("doctorReportCards").innerHTML,
    selectedBefore);
  assert.equal(harness.reloadCount, 0);
});

test("report press state is module-local and defers guarded replacement until release", () => {
  const harness = createHarness();
  harness.reports.wire();
  harness.reports.render();
  const grid = harness.elements.get("doctorReportCards");
  const before = grid.innerHTML;

  harness.pressGuards[0].setPressActive(true);
  harness.setPayload(reportPayload({
    otteCases: 4,
    pledgerCases: 0,
    marker: "pressed-refresh"
  }));
  harness.reports.render();
  assert.equal(grid.innerHTML, before);

  harness.pressGuards[0].setPressActive(false);
  harness.reports.render();
  assert.notEqual(grid.innerHTML, before);
});

test("module and board preserve shared authority and one-way ownership boundaries", () => {
  assert.match(
    moduleSource,
    /^import \{ wirePressInterruptionGuard \} from "\.\/common-interactions\.js";/);
  assert.match(
    moduleSource,
    /import \{ escapeAttribute, escapeHtml, renderHelpIcon \} from "\.\/dom-utils\.js";/);
  assert.match(
    moduleSource,
    /import \{ formatDateTime, formatDuration \} from "\.\/format-utils\.js";/);
  assert.match(
    moduleSource,
    /} from "\.\/request-utils\.js";/);
  assert.doesNotMatch(
    moduleSource,
    /from "\.\/(board|application-state|page-context|room-card|room-workflow|workshop)\.js"/);
  assert.doesNotMatch(moduleSource, /\bapp\./);
  assert.doesNotMatch(
    applicationStateSource,
    /\b(reports|reportsInFlight|reportFilters|reportDoctorId|reportDoctorTab|dateRange|reportPressActive|reportsVersion)\b/);

  assert.match(boardSource, /import \{ createReportData \} from "\.\/report-data\.js";/);
  assert.match(boardSource, /import \{ createReports \} from "\.\/reports\.js";/);
  assert.match(
    boardSource,
    /reports = pageContext\.isReports \|\| pageContext\.isDoctor\s*\? createReports\(/);
  assert.match(
    boardSource,
    /const reportData = pageContext\.isReports \|\| pageContext\.isDoctor \|\| pageContext\.isWorkshop/);
  assert.doesNotMatch(
    boardSource,
    /function (renderReports|renderDoctorCockpit|handleReportsActionClick|handleConfirmExclusionClick|renderReportsAccessPrompt|loadReports)\b/);
  assert.doesNotMatch(boardSource, /\/api\/reports/);
});
