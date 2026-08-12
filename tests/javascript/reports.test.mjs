import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const moduleUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/reports.js",
  import.meta.url);
const reportsHtmlUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/reports.html",
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
const reportsHtmlSource = await readFile(reportsHtmlUrl, "utf8");
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
}
export async function readErrorMessage(response, fallback) {
  if (!response.text) {
    return fallback;
  }
  const text = await response.text();
  return text || fallback;
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

function recordDomMutation(type, element, details = {}) {
  globalThis.__chairsideReportsHarness?.domMutations?.push({
    type,
    element,
    parent: element.parentElement,
    ...details
  });
}

class FakeElement {
  constructor(id = "", tagName = "div", connected = true) {
    this.id = id;
    this.tagName = tagName.toLowerCase();
    this.dataset = {};
    this._hidden = false;
    this._disabled = false;
    this.type = "";
    this.className = "";
    this.isConnected = connected;
    this.parentElement = null;
    this.children = [];
    this._innerHTML = "";
    this._textContent = "";
    this.innerHTMLSetHook = null;
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

  get childElementCount() {
    return this.children.length;
  }

  get hidden() {
    return this._hidden;
  }

  set hidden(value) {
    const next = Boolean(value);
    recordDomMutation("hidden", this, { value: next });
    this._hidden = next;
    if (next && globalThis.document?.activeElement
        && this.contains(globalThis.document.activeElement)) {
      globalThis.document.activeElement = globalThis.document.body;
    }
  }

  get disabled() {
    return this._disabled;
  }

  set disabled(value) {
    this._disabled = Boolean(value);
    if (this._disabled && globalThis.document?.activeElement === this) {
      globalThis.document.activeElement = globalThis.document.body;
    }
  }

  get innerHTML() {
    return this.children.length
      ? this.children.map(child => child.serialize()).join("")
      : this._innerHTML;
  }

  set innerHTML(value) {
    this.replaceChildren();
    this._innerHTML = String(value);
    this._textContent = "";
    this.innerHTMLSetHook?.(this._innerHTML);
  }

  get textContent() {
    return this.children.length
      ? this.children.map(child => child.textContent).join("")
      : this._textContent;
  }

  set textContent(value) {
    this.replaceChildren();
    this._innerHTML = "";
    this._textContent = String(value);
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

  append(...nodes) {
    nodes.forEach(node => {
      recordDomMutation("append", node, {
        destination: this,
        previousParent: node.parentElement
      });
      if (node.parentElement) {
        const index = node.parentElement.children.indexOf(node);
        if (index >= 0) {
          node.parentElement.children.splice(index, 1);
        }
      }
      node.parentElement = this;
      node.setConnected(this.isConnected);
      this.children.push(node);
    });
    this._innerHTML = "";
    this._textContent = "";
  }

  replaceChildren(...nodes) {
    recordDomMutation("replaceChildren", this);
    this.children.forEach(child => child.setConnected(false));
    this.children = [];
    this._innerHTML = "";
    this._textContent = "";
    this.append(...nodes);
  }

  remove() {
    recordDomMutation("remove", this);
    if (this.parentElement) {
      const index = this.parentElement.children.indexOf(this);
      if (index >= 0) {
        this.parentElement.children.splice(index, 1);
      }
      this.parentElement = null;
    }
    this.setConnected(false);
  }

  setConnected(value) {
    this.isConnected = value;
    this.children.forEach(child => child.setConnected(value));
    if (!value && globalThis.document?.activeElement
        && this.contains(globalThis.document.activeElement)) {
      globalThis.document.activeElement = globalThis.document.body;
    }
  }

  contains(element) {
    return this === element || this.children.some(child => child.contains(element));
  }

  closest(selector) {
    if (selector === "[data-report-record-key]" && this.dataset.reportRecordKey) {
      return this;
    }
    if (selector === "[data-report-action-row]"
        && Object.hasOwn(this.dataset, "reportActionRow")) {
      return this;
    }
    return this.parentElement?.closest(selector) || null;
  }

  serialize() {
    const attributes = [];
    if (this.id) {
      attributes.push(`id="${this.id}"`);
    }
    if (this.className) {
      attributes.push(`class="${this.className}"`);
    }
    if (this.type) {
      attributes.push(`type="${this.type}"`);
    }
    Object.entries(this.dataset).forEach(([name, value]) => {
      const attribute = name.replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`);
      attributes.push(`data-${attribute}="${String(value)}"`);
    });
    this.attributes.forEach((value, name) => attributes.push(`${name}="${value}"`));
    if (this.disabled) {
      attributes.push("disabled");
    }
    const content = this.children.length
      ? this.children.map(child => child.serialize()).join("")
      : this._innerHTML || this._textContent;
    return `<${this.tagName}${attributes.length ? ` ${attributes.join(" ")}` : ""}>${content}</${this.tagName}>`;
  }

  focus() {
    if (this.hidden || this.isConnected === false) {
      return;
    }
    recordDomMutation("focus", this);
    globalThis.document.activeElement = this;
  }
}

class FakeDocument {
  constructor(elements, filterChips, shell) {
    this.elements = elements;
    this.filterChips = filterChips;
    this.shell = shell;
    this.listeners = new Map();
    this.actionControls = [];
    this.body = new FakeElement("body");
    this.activeElement = this.body;
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
    if (this.elements.has(id)) {
      return this.elements.get(id);
    }
    const find = element => {
      if (element.id === id) {
        return element;
      }
      for (const child of element.children) {
        const match = find(child);
        if (match) {
          return match;
        }
      }
      return null;
    };
    for (const element of this.elements.values()) {
      const match = find(element);
      if (match) {
        return match;
      }
    }
    return null;
  }

  createElement(tagName) {
    return new FakeElement("", tagName, false);
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
    if (selector === "[data-report-record-key][data-action]") {
      return this.actionControls;
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
    totalExpectedAllocationMinutes: count * 30,
    totalMeasuredCaseFlowMinutes: count * 30 + net,
    netAllocationVarianceMinutes: net,
    averageAllocationVarianceMinutes: count ? net / count : 0,
    casesOverExpectedAllocation: net > 0 ? count : 0,
    casesUnderExpectedAllocation: net < 0 ? count : 0,
    casesAtExpectedAllocation: net === 0 ? count : 0,
    adjustedAllocationCycleCount: 0
  };
}

function sample(populationCount, contributingCount = populationCount) {
  const state = populationCount === 0
    ? "Empty"
    : contributingCount === 0
      ? "Unavailable"
      : contributingCount < 5
        ? "Limited"
        : "Sufficient";
  return {
    populationCount,
    contributingCount,
    state,
    limitedSampleThreshold: 5,
    supportsComparison: state === "Sufficient"
  };
}

function reportPayload({
  otteCases = 0,
  pledgerCases = 2,
  marker = "initial"
} = {}) {
  const completedCount = otteCases + pledgerCases;
  return {
    marker,
    completedRoomCyclesCount: completedCount,
    includedCompletedCycleCount: completedCount,
    excludedCompletedCycleCount: 0,
    medianReadyToDoctorSeconds: 120,
    medianSeatedToDoctorSeconds: 300,
    medianTurnoverSeconds: 600,
    averageDoctorInRoomSeconds: 900,
    sedationCaseCount: 1,
    nonSedationCaseCount: Math.max(0, completedCount - 1),
    samples: {
      completedCases: sample(completedCount),
      includedCompletedCases: sample(completedCount),
      readyWait: sample(completedCount),
      seatedToDoctor: sample(completedCount),
      turnover: sample(completedCount),
      doctorTime: sample(completedCount)
    },
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
    doctorAllocationSamples: [
      { doctorId: "otte", sample: sample(otteCases) },
      { doctorId: "pledger", sample: sample(pledgerCases) }
    ],
    recentCompletedCycles: [],
    exceptionReviewRecords: [],
    procedureSummaries: [],
    baseProcedureSummaries: [],
    scopedProcedureGroups: completedCount > 0
      ? [{
          procedureCode: "EXT",
          procedureLabel: "Extraction",
          baseProcedureCode: "EXT",
          isSedationCase: null,
          caseCount: completedCount,
          scopedPopulationCount: completedCount,
          shareOfScopedCases: 1,
          sample: sample(completedCount)
        }]
      : [],
    rangeLabel: "Jul 1 - Jul 29",
    query: {
      scope: "Practice",
      doctorId: null,
      sedation: "All",
      procedureGrouping: "Family"
    }
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
  requestResponses = [],
  reloadResponses = []
} = {}) {
  const elements = new Map();
  const ids = context.isReports
    ? [
        "reportFilterBar",
        "reportProcedureMix",
        "reportTrendPanel",
        "doctorReportDashboard",
        "doctorReportCards",
        "selectedDoctorPanel",
        "allocationBalanceCard",
        "dataQualityCard",
        "doctorAllocationList",
        "procedureAllocationList",
        "reportScopeDoctorField",
        "reportScopeDoctor",
        "reportHeadline",
        "reportSummary",
        "reportDetail",
        "reportInsights",
        "reportInsightsGrid",
        "reportInsightsHeading",
        "reportsMain",
        "reportDateRange",
        "reportActionFeedback",
        "reportActionStatusPolite",
        "reportActionStatusAssertive",
        "completedCyclesBody",
        "exceptionCyclesBody"
      ]
    : [
        "doctorCockpit",
        "doctorContextCard",
        "selectedDoctorPanel"
      ];
  ids.forEach(id => elements.set(id, new FakeElement(id)));
  if (elements.has("reportActionFeedback")) {
    const feedback = elements.get("reportActionFeedback");
    feedback.hidden = true;
    feedback.append(
      elements.get("reportActionStatusPolite"),
      elements.get("reportActionStatusAssertive"));
  }
  const shell = new FakeElement("reportsShell");
  const filterChips = [
    createFilterChip("sedation", "all"),
    createFilterChip("sedation", "sedation"),
    createFilterChip("sedation", "non-sedation"),
    createFilterChip("grouping", "base"),
    createFilterChip("grouping", "variant")
  ];
  const document = new FakeDocument(elements, filterChips, shell);
  if (elements.has("reportHeadline")) {
    elements.get("reportHeadline").innerHTMLSetHook = html => {
      ["reportAccessToken", "reportAccessHeading"].forEach(id => {
        elements.get(id)?.setConnected(false);
        elements.delete(id);
      });
      if (html.includes('id="reportAccessToken"')) {
        elements.set("reportAccessToken", new FakeElement("reportAccessToken", "input"));
      }
      if (html.includes('id="reportAccessHeading"')) {
        elements.set("reportAccessHeading", new FakeElement("reportAccessHeading", "h2"));
      }
    };
  }
  const disconnectRemovedControls = (html, action) => {
    document.actionControls
      .filter(control => control.dataset.action === action)
      .forEach(control => {
        const recordKey = control.dataset.reportRecordKey;
        if (recordKey && !html.includes(`data-report-record-key="${recordKey}"`)) {
          (control.closest("[data-report-action-row]") || control).setConnected(false);
        }
      });
  };
  if (elements.has("completedCyclesBody")) {
    elements.get("completedCyclesBody").innerHTMLSetHook = html =>
      disconnectRemovedControls(html, "mark-exception");
    elements.get("exceptionCyclesBody").innerHTMLSetHook = html =>
      disconnectRemovedControls(html, "confirm-exclusion");
  }
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
  let query = {
    scope: payload.query?.scope || "Practice",
    doctorId: payload.query?.doctorId || null,
    sedation: payload.query?.sedation || "All",
    procedureGrouping: payload.query?.procedureGrouping || "Family"
  };
  let reloadCount = 0;
  let renderPageCount = 0;
  const requests = [];
  const reloadQueries = [];
  const alerts = [];
  const confirmations = [];
  const domMutations = [];
  let confirmationResult = true;
  const harness = {
    adminHeaders: { "X-ChairSide-Admin-Token": "admin-token" },
    alerts,
    confirmations,
    domMutations,
    clearCount: 0,
    document,
    elements,
    filterChips,
    pressGuards: [],
    requests,
    reloadQueries,
    storedTokens: []
  };
  globalThis.__chairsideReportsHarness = harness;
  globalThis.document = document;
  globalThis.Element = FakeElement;
  globalThis.confirm = message => {
    confirmations.push(message);
    return confirmationResult;
  };
  globalThis.alert = message => alerts.push(message);

  const requestContextForRange = range => {
    const allTime = range?.preset === "all";
    const from = allTime ? null : range?.start || null;
    const to = allTime ? null : range?.end || null;
    return {
      from,
      to,
      rangeSignature: JSON.stringify([from, to])
    };
  };
  const reportData = {
    getDateRange: () => dateRange,
    getReports: () => reportsPayload,
    getVersion: () => version,
    getQuery: () => ({ ...query, window: { ...dateRange } }),
    load: async () => {
      reloadCount += 1;
    },
    reload: async () => {
      reloadCount += 1;
    },
    reloadAfterCurrent: async () => {
      reloadCount += 1;
      reloadQueries.push({ ...query, window: { ...dateRange } });
      const requestRange = { ...dateRange };
      const result = reloadResponses.shift();
      if (result instanceof Error) {
        throw result;
      }
      if (typeof result === "function") {
        await result();
      }
      return {
        payload: reportsPayload,
        version,
        requestContext: requestContextForRange(requestRange)
      };
    },
    setDateRange: value => {
      dateRange = { ...value };
    },
    setScope: (scope, doctorId = null) => {
      query = {
        ...query,
        scope: scope === "Doctor" ? "Doctor" : "Practice",
        doctorId: scope === "Doctor" ? doctorId : null
      };
    },
    setSedation: sedation => {
      query = { ...query, sedation };
    },
    setProcedureGrouping: procedureGrouping => {
      query = { ...query, procedureGrouping };
    },
    getRangeSignature: range => requestContextForRange(range).rangeSignature,
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
      const result = requestResponses.shift();
      if (result instanceof Error) {
        throw result;
      }
      if (typeof result === "function") {
        return result();
      }
      return result || { ok: true, status: 204 };
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
    registerActionControl(control) {
      document.actionControls.push(control);
      return control;
    },
    setConfirmationResult(value) {
      confirmationResult = value;
    },
    setDateRange(value) {
      dateRange = { ...value };
    },
    setPayload(value) {
      reportsPayload = value;
      if (value.query) {
        query = {
          scope: value.query.scope || "Practice",
          doctorId: value.query.doctorId || null,
          sedation: value.query.sedation || "All",
          procedureGrouping: value.query.procedureGrouping || "Family"
        };
      }
      version += 1;
    }
  };
}

function actionButton({
  action = "mark-exception",
  completedCycleId = "42",
  roomId = "3",
  seatedAt = "2026-07-29T12:00:00Z",
  reviewSource,
  reviewRecordId
} = {}) {
  const button = new FakeElement();
  button.dataset.action = action;
  button.dataset.completedCycleId = completedCycleId;
  button.dataset.roomId = roomId;
  button.dataset.seatedAt = seatedAt;
  if (reviewSource) {
    button.dataset.reviewSource = reviewSource;
  }
  if (reviewRecordId) {
    button.dataset.reviewRecordId = reviewRecordId;
  }
  const row = new FakeElement("", "tr");
  row.dataset.reportActionRow = "";
  if (action === "confirm-exclusion") {
    row.dataset.reportRecordKey = reviewSource === "AbortedAssignment"
      ? `aborted:${reviewRecordId}`
      : `completed:${reviewRecordId}`;
  } else if (Number.isInteger(Number(completedCycleId)) && Number(completedCycleId) > 0) {
    row.dataset.reportRecordKey = `completed:${completedCycleId}`;
  } else {
    row.dataset.reportRecordKey = `legacy:${roomId}:${new Date(seatedAt).toISOString()}`;
  }
  row.append(button);
  button.actionSelector = `[data-action='${action}']`;
  return button;
}

function dispatchAction(harness, button) {
  return harness.document.dispatch("click", {
    target: targetFor(new Map([
      [button.actionSelector, button]
    ]))
  });
}

function statusAction(action, recordKey) {
  const button = new FakeElement();
  button.dataset.recordKey = recordKey;
  button.actionSelector = `[data-action='${action}']`;
  return button;
}

function feedbackEntry(harness, recordKey) {
  return [
    ...harness.elements.get("reportActionStatusPolite").children,
    ...harness.elements.get("reportActionStatusAssertive").children
  ].find(element => element.dataset.recordKey === recordKey) || null;
}

function completedCycle(completedCycleId, roomId, seatedAt) {
  return {
    completedCycleId,
    roomId,
    seatedAt
  };
}

function deferred() {
  let resolve;
  const promise = new Promise(resolvePromise => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}

function selectedCard(html, doctorId) {
  const pattern = new RegExp(
    `class="doctor-report-card [^"]*is-selected[^"]*"[^>]*data-report-doctor-id="${doctorId}"`);
  return pattern.test(html);
}

async function openDoctorProceduresTab(harness) {
  const proceduresTab = new FakeElement();
  proceduresTab.dataset.reportDoctorTab = "procedures";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([
      ["[data-report-doctor-tab]", proceduresTab]
    ]))
  });
}

function allocationPayload(populationCount, contributingCount) {
  const net = contributingCount >= 5 ? 10 : contributingCount > 0 ? 4 : 0;
  const context = sample(populationCount, contributingCount);
  return {
    ...reportPayload({ otteCases: contributingCount, pledgerCases: 0 }),
    completedRoomCyclesCount: populationCount,
    includedCompletedCycleCount: populationCount,
    allocationVariance: allocation(contributingCount, net),
    doctorSummaries: [{
      assignedDoctor: "otte",
      allocation: allocation(contributingCount, net)
    }],
    doctorAllocationSamples: [{ doctorId: "otte", sample: context }],
    samples: {
      completedCases: sample(populationCount),
      includedCompletedCases: sample(populationCount),
      scheduleFit: context
    },
    baseProcedureSummaries: [{
      procedureCode: "EXT",
      procedureLabel: "Extraction",
      allocation: allocation(contributingCount, net),
      samples: { allocation: context }
    }]
  };
}

function allocationSurfaceHtml(harness) {
  return [
    "allocationBalanceCard",
    "doctorReportCards",
    "selectedDoctorPanel",
    "doctorAllocationList",
    "procedureAllocationList"
  ].map(id => harness.elements.get(id).innerHTML).join("\n");
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
  assert.equal(harness.reloadQueries[0].sedation, "Sedation");
  assert.equal(harness.reloadQueries[1].procedureGrouping, "DetailedVariant");

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

  harness.setPayload({
    ...reportPayload({
      otteCases: 1,
      pledgerCases: 3,
      marker: "refreshed"
    }),
    query: {
      scope: "Practice",
      doctorId: null,
      sedation: "Sedation",
      procedureGrouping: "DetailedVariant"
    }
  });
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

test("first-class Procedure Mix renders the server total, shares, labels, and row order", () => {
  const payload = {
    ...reportPayload({ otteCases: 1, pledgerCases: 3 }),
    scopedProcedureGroups: [
      {
        procedureCode: "EXT",
        procedureLabel: "Extraction",
        baseProcedureCode: "EXT",
        isSedationCase: null,
        caseCount: 1,
        scopedPopulationCount: 4,
        shareOfScopedCases: 0.37,
        sample: sample(1)
      },
      {
        procedureCode: "CON",
        procedureLabel: "Consult",
        baseProcedureCode: "CON",
        isSedationCase: null,
        caseCount: 3,
        scopedPopulationCount: 4,
        shareOfScopedCases: 0.63,
        sample: sample(3)
      }
    ]
  };
  const harness = createHarness({ payload });

  harness.reports.render();

  const html = harness.elements.get("reportProcedureMix").innerHTML;
  assert.match(html, /4<\/strong><span>completed cases in scope/);
  assert.match(html, /Extraction/);
  assert.match(html, /Consult/);
  assert.match(html, /37%/);
  assert.match(html, /63%/);
  assert.ok(html.indexOf("Extraction") < html.indexOf("Consult"));
  assert.doesNotMatch(html, /25%/);
  assert.doesNotMatch(html, /ranking|performance|score/i);
});

test("Procedure Mix keeps Limited composition descriptive and empty scope truthful", () => {
  const limitedHarness = createHarness({
    payload: {
      ...reportPayload({ otteCases: 1, pledgerCases: 2 }),
      scopedProcedureGroups: [{
        procedureCode: "EXT+SED",
        procedureLabel: "Extraction + Sedation",
        baseProcedureCode: "EXT",
        isSedationCase: true,
        caseCount: 3,
        scopedPopulationCount: 3,
        shareOfScopedCases: 1,
        sample: sample(3)
      }]
    }
  });
  limitedHarness.reports.render();
  const limitedHtml = limitedHarness.elements.get("reportProcedureMix").innerHTML;
  assert.match(limitedHtml, /Limited - N=3/);
  assert.match(limitedHtml, /Extraction \+ Sedation/);
  assert.match(limitedHtml, /100%/);
  assert.doesNotMatch(limitedHtml, /warning|alarm/i);

  const emptyHarness = createHarness({
    payload: reportPayload({ otteCases: 0, pledgerCases: 0 })
  });
  emptyHarness.reports.render();
  const emptyHtml = emptyHarness.elements.get("reportProcedureMix").innerHTML;
  assert.match(emptyHtml, /No observation/);
  assert.doesNotMatch(emptyHtml, /<table/);
  assert.doesNotMatch(emptyHtml, /0%/);
});

test("Procedure Mix static section follows filters and precedes allocation and insights", () => {
  const filterIndex = reportsHtmlSource.indexOf('id="reportFilterBar"');
  const mixIndex = reportsHtmlSource.indexOf('id="reportProcedureMix"');
  const allocationIndex = reportsHtmlSource.indexOf('id="reportAllocation"');
  const insightsIndex = reportsHtmlSource.indexOf('id="reportInsights"');

  assert.ok(filterIndex >= 0);
  assert.ok(filterIndex < mixIndex);
  assert.ok(mixIndex < allocationIndex);
  assert.ok(allocationIndex < insightsIndex);
});

test("matching Doctor scope reuses canonical Procedure Mix markup in the Doctor Procedures tab", async () => {
  const payload = {
    ...reportPayload({ otteCases: 3, pledgerCases: 0 }),
    query: {
      scope: "Doctor",
      doctorId: "otte",
      sedation: "Sedation",
      procedureGrouping: "DetailedVariant"
    },
    scopedProcedureGroups: [{
      procedureCode: "EXT+SED",
      procedureLabel: "Extraction + Sedation",
      baseProcedureCode: "EXT",
      isSedationCase: true,
      caseCount: 3,
      scopedPopulationCount: 3,
      shareOfScopedCases: 1,
      sample: sample(3)
    }]
  };
  const harness = createHarness({ payload });
  harness.reports.wire();
  harness.reports.render();

  await openDoctorProceduresTab(harness);

  const mainTable = harness.elements.get("reportProcedureMix").innerHTML.match(/<table class="procedure-mix-table">[\s\S]*?<\/table>/)?.[0];
  const doctorTable = harness.elements.get("selectedDoctorPanel").innerHTML.match(/<table class="procedure-mix-table">[\s\S]*?<\/table>/)?.[0];
  assert.ok(mainTable);
  assert.equal(doctorTable, mainTable);
  assert.match(harness.elements.get("selectedDoctorPanel").innerHTML, /Limited - N=3/);
  assert.match(moduleSource, /return renderProcedureMixMarkup\(r, \{ headingTag: "h3", compact: true \}\);/);
  assert.doesNotMatch(
    moduleSource.slice(
      moduleSource.indexOf("function renderSelectedDoctorProcedures"),
      moduleSource.indexOf("function formatProcedureShare")),
    /doctorProcedureMix|\.sort\(|\.reduce\(/);
});

test("Practice scope Doctor Procedures guidance does not mutate scope or reuse Practice mix", async () => {
  const harness = createHarness({ payload: reportPayload({ otteCases: 0, pledgerCases: 2 }) });
  harness.reports.wire();
  harness.reports.render();

  await openDoctorProceduresTab(harness);

  const panel = harness.elements.get("selectedDoctorPanel").innerHTML;
  assert.match(panel, /Select Doctor scope to view this doctor&#39;s Procedure Mix with the current filters/);
  assert.doesNotMatch(panel, /Extraction/);
  assert.equal(harness.reloadCount, 0);
});

test("nonempty allocation populations with zero contributors render Unavailable, not Empty", () => {
  const harness = createHarness({ payload: allocationPayload(6, 0) });

  harness.reports.render();
  const html = allocationSurfaceHtml(harness);

  assert.match(html, /Unavailable/);
  assert.match(html, /0 of 6 contributors/);
  assert.doesNotMatch(harness.elements.get("allocationBalanceCard").innerHTML, /0 min expected|0 min measured/);
  for (const id of ["allocationBalanceCard", "selectedDoctorPanel", "procedureAllocationList"]) {
    assert.doesNotMatch(harness.elements.get(id).innerHTML, /No observation/);
  }
});

test("Limited allocation samples retain N while suppressing comparison language", () => {
  const harness = createHarness({ payload: allocationPayload(3, 3) });

  harness.reports.render();
  const html = allocationSurfaceHtml(harness);

  assert.match(html, /Limited - N=3/);
  assert.match(html, /3 allocation contributors/);
  assert.doesNotMatch(html, /\bNet\b|\bAvg\b|O \/ U \/ A|over expected|under expected/i);
});

test("Sufficient allocation samples retain supported descriptive comparison language", () => {
  const harness = createHarness({ payload: allocationPayload(5, 5) });

  harness.reports.render();
  const html = allocationSurfaceHtml(harness);

  assert.match(html, /Sufficient - N=5/);
  assert.match(html, /\bNet\b/);
  assert.match(html, /over expected/);
  assert.match(html, /O \/ U \/ A/);
});

test("Doctor scope exposes only its requested doctor until the scope control reloads another doctor", async () => {
  const activePayload = {
    ...reportPayload({ otteCases: 5, pledgerCases: 0 }),
    query: {
      scope: "Doctor",
      doctorId: "otte",
      sedation: "All",
      procedureGrouping: "Family"
    },
    doctorSummaries: [{ assignedDoctor: "otte", allocation: allocation(5, 5) }],
    doctorAllocationSamples: [{ doctorId: "otte", sample: sample(5) }]
  };
  const nextPayload = {
    ...reportPayload({ otteCases: 0, pledgerCases: 5 }),
    query: {
      scope: "Doctor",
      doctorId: "pledger",
      sedation: "All",
      procedureGrouping: "Family"
    },
    doctorSummaries: [{ assignedDoctor: "pledger", allocation: allocation(5, -5) }],
    doctorAllocationSamples: [{ doctorId: "pledger", sample: sample(5) }]
  };
  const responseGate = deferred();
  let harness;
  harness = createHarness({
    payload: activePayload,
    reloadResponses: [async () => {
      await responseGate.promise;
      harness.setPayload(nextPayload);
      harness.reports.render();
    }]
  });
  harness.reports.wire();
  harness.reports.render();

  const grid = harness.elements.get("doctorReportCards");
  assert.match(grid.innerHTML, /data-report-doctor-id="otte"/);
  assert.doesNotMatch(grid.innerHTML, /data-report-doctor-id="pledger"/);

  const offScopeCard = new FakeElement();
  offScopeCard.dataset.reportDoctorId = "pledger";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([["[data-report-doctor-id]", offScopeCard]]))
  });
  assert.match(grid.innerHTML, /data-report-doctor-id="otte"/);
  assert.doesNotMatch(grid.innerHTML, /data-report-doctor-id="pledger"/);

  const select = harness.elements.get("reportScopeDoctor");
  select.value = "pledger";
  const changingScope = select.dispatch("change", { target: select });

  assert.equal(harness.reloadQueries.length, 1);
  assert.equal(harness.reloadQueries[0].scope, "Doctor");
  assert.equal(harness.reloadQueries[0].doctorId, "pledger");
  assert.doesNotMatch(grid.innerHTML, /data-report-doctor-id="pledger"/);

  responseGate.resolve();
  await changingScope;
  assert.match(grid.innerHTML, /data-report-doctor-id="pledger"/);
  assert.doesNotMatch(grid.innerHTML, /data-report-doctor-id="otte"/);
});

test("Practice Overview renders exactly four median-first headline cards from authoritative populations", () => {
  const payload = {
    ...reportPayload({ otteCases: 1, pledgerCases: 2 }),
    completedRoomCyclesCount: 7,
    includedCompletedCycleCount: 3,
    medianReadyToDoctorSeconds: 0,
    medianSeatedToDoctorSeconds: 300,
    medianTurnoverSeconds: 600,
    averageDoctorInRoomSeconds: 900,
    samples: {
      completedCases: sample(7),
      readyWait: sample(3, 1),
      seatedToDoctor: sample(3, 0),
      turnover: sample(3, 3),
      doctorTime: sample(3)
    }
  };
  const harness = createHarness({ payload });

  harness.reports.render();

  const headline = harness.elements.get("reportHeadline").innerHTML;
  assert.equal((headline.match(/headline-card/g) || []).length, 4);
  assert.match(headline, /Completed Cases[\s\S]*<strong>7<\/strong>/);
  assert.match(headline, /Median Ready Wait[\s\S]*<strong>00:00<\/strong>/);
  assert.match(headline, /Median Seated -&gt; Doctor/);
  assert.match(headline, /Median Turnover/);
  assert.match(headline, /Limited - N=1/);
  assert.match(headline, /Unavailable/);
  assert.doesNotMatch(headline, /Avg Total|Avg Doctor|Sedation Cases|Exceptions to Review/);
});

function trendBucket(startDate, populationCount, contributingCount, median) {
  const metricSample = sample(populationCount, contributingCount);
  return {
    startDate,
    endDate: startDate === "2026-07-06" ? "2026-07-13" : "2026-07-20",
    completedCycleCount: contributingCount,
    medianSeatedToDoctorSeconds: median,
    completedSample: metricSample,
    seatedToDoctorSample: metricSample,
    readyWaitCycleCount: contributingCount,
    medianReadyWaitSeconds: contributingCount ? median : null,
    readyWaitSample: metricSample,
    turnoverCycleCount: contributingCount,
    medianTurnoverSeconds: median,
    turnoverSample: metricSample
  };
}

test("trend cards preserve priority order and do not skip the latest unavailable population", () => {
  const latest = trendBucket("2026-07-13", 4, 0, null);
  const harness = createHarness({
    payload: {
      ...reportPayload({ otteCases: 2, pledgerCases: 3 }),
      trends: {
        buckets: [
          trendBucket("2026-07-06", 5, 5, 600),
          latest
        ]
      }
    }
  });

  harness.reports.render();

  const panel = harness.elements.get("reportTrendPanel").innerHTML;
  assert.ok(panel.indexOf("Ready Wait trend") < panel.indexOf("Turnover trend"));
  assert.ok(panel.indexOf("Turnover trend") < panel.indexOf("Seated -&gt; Doctor trend"));
  assert.match(panel, /ready-wait-trend-card is-primary/);
  assert.match(panel, /turnover-trend-card is-primary/);
  assert.match(panel, /seated-to-doctor-trend-card is-secondary/);
  assert.match(panel, /Ready Wait trend[\s\S]*Unavailable[\s\S]*Jul 13 - Jul 19/);
  assert.match(panel, /0 of 4 contributors/);
  const readyCard = panel.slice(0, panel.indexOf("turnover-trend-card"));
  assert.doesNotMatch(readyCard, /0 min/);
});

test("Limited weekly samples retain values and N while suppressing directional language", () => {
  const harness = createHarness({
    payload: {
      ...reportPayload({ otteCases: 2, pledgerCases: 3 }),
      trends: {
        buckets: [
          trendBucket("2026-07-06", 5, 5, 600),
          trendBucket("2026-07-13", 4, 4, 300)
        ]
      }
    }
  });

  harness.reports.render();

  const panel = harness.elements.get("reportTrendPanel").innerHTML;
  assert.match(panel, /Limited - N=4/);
  assert.match(panel, /Comparison is not shown unless both weekly Ready Wait samples are Sufficient/);
  assert.doesNotMatch(panel, /Ready Wait (?:increased|decreased) by/);
});

test("Sufficient weekly samples show supported direction and apply the exact 60-second threshold", () => {
  const harness = createHarness({
    payload: {
      ...reportPayload({ otteCases: 2, pledgerCases: 3 }),
      trends: {
        buckets: [
          trendBucket("2026-07-06", 5, 5, 300),
          trendBucket("2026-07-13", 5, 5, 360)
        ]
      }
    }
  });

  harness.reports.render();

  const panel = harness.elements.get("reportTrendPanel").innerHTML;
  assert.match(panel, /Sufficient - N=5/);
  assert.match(panel, /Median Ready Wait increased by 1 min compared with the previous week/);
});

test("Sufficient weekly movement under 60 seconds remains descriptively about the same", () => {
  const harness = createHarness({
    payload: {
      ...reportPayload({ otteCases: 2, pledgerCases: 3 }),
      trends: {
        buckets: [
          trendBucket("2026-07-06", 5, 5, 300),
          trendBucket("2026-07-13", 5, 5, 359)
        ]
      }
    }
  });

  harness.reports.render();

  assert.match(
    harness.elements.get("reportTrendPanel").innerHTML,
    /Median Ready Wait was about the same as the previous week/);
});

test("healthy data quality stays quiet and the All Metrics view omits the duplicate review KPI", () => {
  const harness = createHarness({
    payload: {
      ...reportPayload({ otteCases: 2, pledgerCases: 3 }),
      baseProcedureSummaries: [{
        procedureCode: "EXT",
        procedureLabel: "Extraction",
        completedCycleCount: 5,
        averageTotalSeconds: 1200,
        medianTotalSeconds: 1140,
        averageDoctorTimeSeconds: 900,
        averageReadyToDoctorSeconds: 240,
        samples: {
          completedCases: sample(5),
          total: sample(5),
          doctorTime: sample(5),
          readyWait: sample(5)
        }
      }]
    }
  });

  harness.reports.render();

  assert.equal(harness.elements.get("dataQualityCard").hidden, true);
  assert.equal(harness.elements.get("dataQualityCard").innerHTML, "");
  const allMetrics = harness.elements.get("reportSummary").innerHTML;
  assert.doesNotMatch(allMetrics, /Exceptions Requiring Review/);
  assert.match(allMetrics, /Sedation Cases/);
  assert.match(allMetrics, /Avg In Room/);
  assert.match(harness.elements.get("reportInsightsGrid").innerHTML, /Avg Doctor Time/);
});

test("actionable data quality summarizes the unified queue and opens the existing review detail", async () => {
  const payload = {
    ...reportPayload({ otteCases: 2, pledgerCases: 3 }),
    excludedCompletedCycleCount: 1,
    exceptionReviewRecords: [
      {
        sourceType: "CompletedCycle",
        reviewRecordId: 42,
        completedCycleId: 42,
        roomId: 3,
        seatedAt: "2026-07-29T12:00:00Z"
      },
      {
        sourceType: "AbortedAssignment",
        reviewRecordId: 84,
        abortedAssignmentId: 84,
        roomId: 4,
        seatedAt: "2026-07-29T13:00:00Z"
      }
    ]
  };
  const harness = createHarness({ payload });
  harness.reports.wire();
  harness.reports.render();

  const card = harness.elements.get("dataQualityCard");
  assert.equal(card.hidden, false);
  assert.match(card.innerHTML, /2 items require review in the action queue/);
  assert.match(card.innerHTML, /data-action="open-review-queue"/);
  assert.doesNotMatch(card.innerHTML, /All completed records|No reporting exceptions/);

  const button = new FakeElement();
  button.actionSelector = "[data-action='open-review-queue']";
  await dispatchAction(harness, button);

  assert.equal(harness.elements.get("reportDetail").open, true);
});

test("empty analytical population uses the No observation state", () => {
  const harness = createHarness({
    payload: {
      ...reportPayload({ otteCases: 0, pledgerCases: 0 }),
      samples: {
        completedCases: {
          populationCount: 0,
          contributingCount: 0,
          state: "Empty",
          limitedSampleThreshold: 5,
          supportsComparison: false
        }
      }
    }
  });

  harness.reports.render();

  assert.match(harness.elements.get("reportHeadline").innerHTML, /No observation/);
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
  assert.equal(harness.confirmations.length, 4);
});

test("confirmation cancellation is a quiet no-op that preserves the originating control", async () => {
  const harness = createHarness();
  harness.reports.wire();
  harness.setConfirmationResult(false);
  const button = actionButton();
  button.focus();

  await dispatchAction(harness, button);

  assert.equal(harness.confirmations.length, 1);
  assert.equal(harness.requests.length, 0);
  assert.equal(button.disabled, false);
  assert.equal(harness.elements.get("reportActionFeedback").hidden, true);
  assert.equal(harness.elements.get("reportActionFeedback").innerHTML.includes("report-action-status-entry"), false);
  assert.equal(harness.document.activeElement, button);
});

test("pending state is synchronous, record-scoped, duplicate-safe, and survives rerender", async () => {
  const mutation = deferred();
  const harness = createHarness({
    requestResponses: [() => mutation.promise]
  });
  harness.reports.wire();
  const first = harness.registerActionControl(actionButton());
  first.dataset.reportRecordKey = "completed:42";
  first.dataset.defaultLabel = "Mark Exception";
  first.dataset.pendingLabel = "Marking exception...";
  const sameRecord = harness.registerActionControl(actionButton());
  sameRecord.dataset.reportRecordKey = "completed:42";
  sameRecord.dataset.defaultLabel = "Mark Exception";
  sameRecord.dataset.pendingLabel = "Marking exception...";
  const unrelated = harness.registerActionControl(actionButton({
    completedCycleId: "43",
    roomId: "4"
  }));
  unrelated.dataset.reportRecordKey = "completed:43";
  unrelated.dataset.defaultLabel = "Mark Exception";
  unrelated.dataset.pendingLabel = "Marking exception...";

  const firstDispatch = dispatchAction(harness, first);
  await Promise.resolve();

  const status = harness.elements.get("reportActionFeedback");
  assert.equal(status.hidden, false);
  assert.match(status.innerHTML, /Working/);
  assert.match(status.innerHTML, /Marking Room 3 as an exception/);
  assert.equal(first.disabled, true);
  assert.equal(sameRecord.disabled, true);
  assert.equal(first.textContent, "Marking exception...");
  assert.equal(unrelated.disabled, false);

  await dispatchAction(harness, sameRecord);
  assert.equal(harness.confirmations.length, 1);
  assert.equal(harness.requests.length, 1);

  harness.setPayload({
    ...reportPayload(),
    recentCompletedCycles: [{
      completedCycleId: 42,
      roomId: 3,
      seatedAt: "2026-07-29T12:00:00Z"
    }]
  });
  harness.reports.render();
  assert.match(
    harness.elements.get("completedCyclesBody").innerHTML,
    /data-report-record-key="completed:42"/);
  assert.match(
    harness.elements.get("completedCyclesBody").innerHTML,
    /disabled/);
  assert.match(
    harness.elements.get("completedCyclesBody").innerHTML,
    /Marking exception/);
  assert.match(status.innerHTML, /Working/);

  mutation.resolve({ ok: true, status: 204 });
  await firstDispatch;
  assert.equal(harness.reloadCount, 1);
  assert.match(status.innerHTML, /Completed/);
  assert.match(status.innerHTML, /Room 3 was marked as an exception/);
  assert.equal(harness.alerts.length, 0);
});

test("inline definite failures use safe wording and require refresh before retry", async () => {
  const cases = [
    {
      response: {
        ok: false,
        status: 400,
        text: async () => "The completed cycle is already excluded."
      },
      expected: /completed cycle is already excluded/
    },
    {
      response: { ok: false, status: 401 },
      expected: /Reports access is required/
    },
    {
      response: { ok: false, status: 403 },
      expected: /saved Reports token was rejected/
    },
    {
      response: { ok: false, status: 404, text: async () => "" },
      expected: /no longer available/
    }
  ];

  for (const [index, failure] of cases.entries()) {
    const harness = createHarness({
      requestResponses: [failure.response]
    });
    harness.reports.wire();
    const button = actionButton({
      completedCycleId: String(50 + index),
      roomId: String(5 + index)
    });
    await dispatchAction(harness, button);

    const status = harness.elements.get("reportActionFeedback");
    assert.match(status.innerHTML, /Could not complete/);
    assert.match(status.innerHTML, failure.expected);
    assert.match(status.innerHTML, /data-action="refresh-report-action"/);
    assert.doesNotMatch(status.innerHTML, /retry-report-mutation/);
    assert.equal(button.disabled, true);
    assert.equal(harness.reloadCount, 0);
    assert.equal(harness.alerts.length, 0);
  }
});

test("network and server failures remain uncertain until GET-only reconciliation", async t => {
  const originalWarn = console.warn;
  t.after(() => {
    console.warn = originalWarn;
  });
  console.warn = () => {};
  const reloadResponses = [];
  const harness = createHarness({
    requestResponses: [new Error("network unavailable")],
    reloadResponses
  });
  harness.reports.wire();
  const button = actionButton();
  await dispatchAction(harness, button);

  const status = harness.elements.get("reportActionFeedback");
  assert.match(status.innerHTML, /Outcome uncertain/);
  assert.match(status.innerHTML, /outcome could not be confirmed/);
  assert.doesNotMatch(status.innerHTML, /retry-report-mutation/);
  assert.equal(button.disabled, true);
  assert.equal(harness.requests.length, 1);

  reloadResponses.push(() => harness.setPayload({
    ...reportPayload(),
    recentCompletedCycles: [{
      completedCycleId: 42,
      roomId: 3,
      seatedAt: "2026-07-29T12:00:00Z"
    }]
  }));
  await dispatchAction(
    harness,
    statusAction("refresh-report-action", "completed:42"));

  assert.equal(harness.requests.length, 1);
  assert.equal(harness.reloadCount, 1);
  assert.match(status.innerHTML, /can be tried again/);
  assert.match(status.innerHTML, /data-action="retry-report-mutation"/);
  assert.doesNotMatch(status.innerHTML, /refresh-report-action/);

  await dispatchAction(
    harness,
    statusAction("retry-report-mutation", "completed:42"));
  assert.equal(harness.confirmations.length, 2);
  assert.equal(harness.requests.length, 2);
  assert.equal(harness.reloadCount, 2);
  assert.match(status.innerHTML, /Completed/);

  const serverHarness = createHarness({
    requestResponses: [{ ok: false, status: 500 }]
  });
  serverHarness.reports.wire();
  await dispatchAction(serverHarness, actionButton());
  assert.match(
    serverHarness.elements.get("reportActionFeedback").innerHTML,
    /Outcome uncertain/);
  assert.doesNotMatch(
    serverHarness.elements.get("reportActionFeedback").innerHTML,
    /retry-report-mutation/);
  assert.equal(serverHarness.alerts.length, 0);
});

test("Mark reconciliation requires positive evidence and never trusts truncated absence", async t => {
  const originalWarn = console.warn;
  t.after(() => {
    console.warn = originalWarn;
  });
  console.warn = () => {};

  const target = completedCycle(42, 3, "2026-07-29T12:00:00Z");
  const initialRecent = Array.from(
    { length: 24 },
    (_, index) => completedCycle(100 + index, 4, `2026-07-${String(29 - index).padStart(2, "0")}T12:00:00Z`));
  initialRecent.push(target);
  const reloadResponses = [
    () => ambiguousHarness.setPayload({
      ...reportPayload(),
      recentCompletedCycles: Array.from(
        { length: 25 },
        (_, index) => completedCycle(200 + index, 5, `2026-07-${String(29 - index).padStart(2, "0")}T13:00:00Z`)),
      exceptionReviewRecords: []
    })
  ];
  const ambiguousHarness = createHarness({
    payload: {
      ...reportPayload(),
      recentCompletedCycles: initialRecent
    },
    requestResponses: [new Error("connection lost")],
    reloadResponses
  });
  ambiguousHarness.reports.wire();
  ambiguousHarness.reports.render();
  await dispatchAction(ambiguousHarness, actionButton());
  await dispatchAction(
    ambiguousHarness,
    statusAction("refresh-report-action", "completed:42"));

  const ambiguousFeedback = ambiguousHarness.elements.get("reportActionFeedback").innerHTML;
  assert.match(ambiguousFeedback, /Outcome uncertain/);
  assert.match(ambiguousFeedback, /not present in an authoritative action population/);
  assert.match(ambiguousFeedback, /refresh-report-action/);
  assert.doesNotMatch(ambiguousFeedback, /retry-report-mutation/);
  assert.doesNotMatch(ambiguousFeedback, /Room 3 was marked as an exception/);

  const successReloads = [
    () => successHarness.setPayload({
      ...reportPayload(),
      recentCompletedCycles: [],
      exceptionReviewRecords: [{
        sourceType: "CompletedCycle",
        reviewRecordId: 42,
        completedCycleId: 42,
        roomId: 3,
        seatedAt: "2026-07-29T12:00:00Z"
      }]
    })
  ];
  const successHarness = createHarness({
    requestResponses: [new Error("connection lost")],
    reloadResponses: successReloads
  });
  successHarness.reports.wire();
  await dispatchAction(successHarness, actionButton());
  await dispatchAction(
    successHarness,
    statusAction("refresh-report-action", "completed:42"));
  assert.match(successHarness.elements.get("reportActionFeedback").innerHTML, /Completed/);
  assert.match(
    successHarness.elements.get("reportActionFeedback").innerHTML,
    /Room 3 was marked as an exception/);

  const legacySeatedAt = "2026-07-29T12:00:00-05:00";
  const legacyReloads = [
    () => legacyHarness.setPayload({
      ...reportPayload(),
      recentCompletedCycles: [],
      exceptionReviewRecords: [{
        sourceType: "CompletedCycle",
        roomId: 7,
        seatedAt: "2026-07-29T17:00:00Z"
      }]
    })
  ];
  const legacyHarness = createHarness({
    requestResponses: [new Error("connection lost")],
    reloadResponses: legacyReloads
  });
  legacyHarness.reports.wire();
  await dispatchAction(legacyHarness, actionButton({
    completedCycleId: "",
    roomId: "7",
    seatedAt: legacySeatedAt
  }));
  await dispatchAction(
    legacyHarness,
    statusAction(
      "refresh-report-action",
      "legacy:7:2026-07-29T17:00:00.000Z"));
  assert.match(legacyHarness.elements.get("reportActionFeedback").innerHTML, /Completed/);
});

test("completed confirmation uses the range actually fetched during reconciliation", async t => {
  const originalWarn = console.warn;
  t.after(() => {
    console.warn = originalWarn;
  });
  console.warn = () => {};
  const rangeA = {
    preset: "custom",
    start: "2026-07-01",
    end: "2026-07-07"
  };
  const rangeB = {
    preset: "custom",
    start: "2026-07-08",
    end: "2026-07-14"
  };
  const rangeBResponse = deferred();
  const reloadResponses = [
    async () => {
      await rangeBResponse.promise;
      raceHarness.setPayload({
        ...reportPayload(),
        exceptionReviewRecords: []
      });
    }
  ];
  const raceHarness = createHarness({
    requestResponses: [new Error("confirmation outcome unknown")],
    reloadResponses
  });
  raceHarness.setDateRange(rangeA);
  raceHarness.reports.wire();
  await dispatchAction(raceHarness, actionButton({
    action: "confirm-exclusion",
    reviewSource: "CompletedCycle",
    reviewRecordId: "77"
  }));

  raceHarness.setDateRange(rangeB);
  const reconciliation = dispatchAction(
    raceHarness,
    statusAction("refresh-report-action", "completed:77"));
  await Promise.resolve();
  raceHarness.setDateRange(rangeA);
  rangeBResponse.resolve();
  await reconciliation;

  const racedFeedback = feedbackEntry(raceHarness, "completed:77");
  assert.equal(racedFeedback.dataset.phase, "unknown-outcome");
  assert.match(racedFeedback.textContent, /different range/);
  assert.match(racedFeedback.innerHTML, /refresh-report-action/);
  assert.doesNotMatch(racedFeedback.innerHTML, /retry-report-mutation/);

  reloadResponses.push(() => raceHarness.setPayload({
    ...reportPayload(),
    exceptionReviewRecords: []
  }));
  await dispatchAction(
    raceHarness,
    statusAction("refresh-report-action", "completed:77"));
  assert.equal(feedbackEntry(raceHarness, "completed:77").dataset.phase, "success");

  const sameBoundsHarness = createHarness({
    requestResponses: [new Error("confirmation outcome unknown")],
    reloadResponses: [() => sameBoundsHarness.setPayload({
      ...reportPayload(),
      exceptionReviewRecords: []
    })]
  });
  sameBoundsHarness.setDateRange(rangeA);
  sameBoundsHarness.reports.wire();
  await dispatchAction(sameBoundsHarness, actionButton({
    action: "confirm-exclusion",
    reviewSource: "CompletedCycle",
    reviewRecordId: "78"
  }));
  sameBoundsHarness.setDateRange({
    preset: "last7",
    start: rangeA.start,
    end: rangeA.end
  });
  await dispatchAction(
    sameBoundsHarness,
    statusAction("refresh-report-action", "completed:78"));
  assert.equal(
    feedbackEntry(sameBoundsHarness, "completed:78").dataset.phase,
    "success");

  const allTimeHarness = createHarness({
    requestResponses: [new Error("confirmation outcome unknown")],
    reloadResponses: [() => allTimeHarness.setPayload({
      ...reportPayload(),
      exceptionReviewRecords: []
    })]
  });
  allTimeHarness.setDateRange({
    preset: "all",
    start: null,
    end: null
  });
  allTimeHarness.reports.wire();
  await dispatchAction(allTimeHarness, actionButton({
    action: "confirm-exclusion",
    reviewSource: "CompletedCycle",
    reviewRecordId: "79"
  }));
  allTimeHarness.setDateRange({
    preset: "all",
    start: "2026-01-01",
    end: "2026-07-30"
  });
  await dispatchAction(
    allTimeHarness,
    statusAction("refresh-report-action", "completed:79"));
  assert.equal(
    feedbackEntry(allTimeHarness, "completed:79").dataset.phase,
    "success");
});

test("feedback uses sibling live regions, safe text, and stable unrelated nodes", async () => {
  const secondRequest = deferred();
  const serverMessage = "<img src=x onerror=alert('unsafe')>";
  const harness = createHarness({
    requestResponses: [
      { ok: false, status: 400, text: async () => serverMessage },
      () => secondRequest.promise
    ]
  });
  harness.reports.wire();

  await dispatchAction(harness, actionButton());
  const retained = feedbackEntry(harness, "completed:42");
  const polite = harness.elements.get("reportActionStatusPolite");
  const assertive = harness.elements.get("reportActionStatusAssertive");
  assert.equal(retained.parentElement, assertive);
  assert.equal(polite.contains(retained), false);
  assert.equal(retained.children[1].textContent, serverMessage);
  assert.equal(retained.children[1].children.length, 0);

  const unrelatedDispatch = dispatchAction(harness, actionButton({
    completedCycleId: "43",
    roomId: "4"
  }));
  await Promise.resolve();
  assert.equal(feedbackEntry(harness, "completed:42"), retained);
  assert.equal(retained.parentElement, assertive);
  assert.equal(feedbackEntry(harness, "completed:43").parentElement, polite);
  assert.equal(harness.elements.get("reportActionFeedback").hidden, false);

  secondRequest.resolve({ ok: true, status: 204 });
  await unrelatedDispatch;
  harness.reports.renderAccessPrompt(401);
  assert.equal(polite.childElementCount, 0);
  assert.equal(assertive.childElementCount, 0);
  assert.equal(harness.elements.get("reportActionFeedback").hidden, true);
});

test("live-region transitions detach the changed entry before installing its new message", async () => {
  const targetResponse = deferred();
  const harness = createHarness({
    requestResponses: [
      { ok: false, status: 400, text: async () => "Retained failure." },
      () => targetResponse.promise
    ]
  });
  harness.reports.wire();
  await dispatchAction(harness, actionButton({
    completedCycleId: "41",
    roomId: "2"
  }));
  const retained = feedbackEntry(harness, "completed:41");
  const targetDispatch = dispatchAction(harness, actionButton());
  await Promise.resolve();
  const target = feedbackEntry(harness, "completed:42");
  const polite = harness.elements.get("reportActionStatusPolite");
  const assertive = harness.elements.get("reportActionStatusAssertive");
  assert.equal(target.parentElement, polite);

  harness.domMutations.length = 0;
  targetResponse.resolve({
    ok: false,
    status: 400,
    text: async () => "Target failure."
  });
  await targetDispatch;

  const failureTransition = harness.domMutations.filter(event =>
    event.element === target
    && ["remove", "replaceChildren", "append"].includes(event.type));
  assert.deepEqual(
    failureTransition.map(event => event.type),
    ["remove", "replaceChildren", "append"]);
  assert.equal(failureTransition[0].parent, polite);
  assert.equal(failureTransition[1].parent, null);
  assert.equal(failureTransition[2].destination, assertive);
  assert.equal(polite.contains(target), false);
  assert.equal(assertive.contains(target), true);
  assert.equal(feedbackEntry(harness, "completed:41"), retained);
  assert.equal(
    harness.domMutations.some(event => event.element === retained),
    false);

  harness.domMutations.length = 0;
  const refreshDispatch = dispatchAction(
    harness,
    statusAction("refresh-report-action", "completed:42"));
  const pendingTransition = harness.domMutations.filter(event =>
    event.element === target
    && ["remove", "replaceChildren", "append"].includes(event.type));
  assert.deepEqual(
    pendingTransition.map(event => event.type),
    ["remove", "replaceChildren", "append"]);
  assert.equal(pendingTransition[0].parent, assertive);
  assert.equal(pendingTransition[1].parent, null);
  assert.equal(pendingTransition[2].destination, polite);
  assert.equal(assertive.contains(target), false);
  assert.equal(polite.contains(target), true);
  await refreshDispatch;
});

test("same-region message updates do not detach their keyed entry", async () => {
  const mutation = deferred();
  const reload = deferred();
  const harness = createHarness({
    requestResponses: [() => mutation.promise],
    reloadResponses: [() => reload.promise]
  });
  harness.reports.wire();
  const dispatch = dispatchAction(harness, actionButton());
  await Promise.resolve();
  const entry = feedbackEntry(harness, "completed:42");
  const polite = harness.elements.get("reportActionStatusPolite");

  harness.domMutations.length = 0;
  mutation.resolve({ ok: true, status: 204 });
  await Promise.resolve();
  await Promise.resolve();

  const entryMutations = harness.domMutations.filter(event =>
    event.element === entry
    && ["remove", "replaceChildren", "append"].includes(event.type));
  assert.deepEqual(entryMutations.map(event => event.type), ["replaceChildren"]);
  assert.equal(entryMutations[0].parent, polite);
  assert.equal(entry.parentElement, polite);

  reload.resolve();
  await dispatch;
});

test("a repeated same-record message updates only that entry for the new operation", async t => {
  const originalWarn = console.warn;
  t.after(() => {
    console.warn = originalWarn;
  });
  console.warn = () => {};
  const reloadResponses = [];
  const requestResponses = [
    new Error("first uncertain request"),
    new Error("second uncertain request")
  ];
  const harness = createHarness({ requestResponses, reloadResponses });
  harness.reports.wire();
  await dispatchAction(harness, actionButton());

  const actionablePayload = () => harness.setPayload({
    ...reportPayload(),
    recentCompletedCycles: [completedCycle(42, 3, "2026-07-29T12:00:00Z")]
  });
  reloadResponses.push(actionablePayload);
  await dispatchAction(
    harness,
    statusAction("refresh-report-action", "completed:42"));
  const entry = feedbackEntry(harness, "completed:42");
  const firstOperation = entry.dataset.operationId;
  const firstMessage = entry.children[1].textContent;
  const firstMessageNode = entry.children[1];

  await dispatchAction(
    harness,
    statusAction("retry-report-mutation", "completed:42"));
  reloadResponses.push(actionablePayload);
  await dispatchAction(
    harness,
    statusAction("refresh-report-action", "completed:42"));

  assert.equal(feedbackEntry(harness, "completed:42"), entry);
  assert.equal(entry.children[1].textContent, firstMessage);
  assert.notEqual(entry.dataset.operationId, firstOperation);
  assert.notEqual(entry.children[1], firstMessageNode);
});

test("mutation success and refresh failure stay separate and refresh retry never repeats POST", async t => {
  const originalWarn = console.warn;
  t.after(() => {
    console.warn = originalWarn;
  });
  console.warn = () => {};
  const reloadResponses = [new Error("refresh failed")];
  const harness = createHarness({ reloadResponses });
  harness.reports.wire();
  const button = actionButton();

  await dispatchAction(harness, button);

  const status = harness.elements.get("reportActionFeedback");
  assert.match(status.innerHTML, /Refresh needed/);
  assert.match(status.innerHTML, /action succeeded/);
  assert.doesNotMatch(status.innerHTML, /Could not complete/);
  assert.equal(harness.requests.length, 1);
  assert.equal(button.disabled, true);

  reloadResponses.push(() => harness.setPayload(reportPayload()));
  await dispatchAction(
    harness,
    statusAction("refresh-report-action", "completed:42"));

  assert.equal(harness.requests.length, 1);
  assert.equal(harness.reloadCount, 2);
  assert.match(status.innerHTML, /Completed/);
  assert.match(status.innerHTML, /Room 3 was marked as an exception/);
});

test("completed Mark state does not disable the next Confirm action for the same record", async () => {
  const harness = createHarness();
  harness.reports.wire();
  await dispatchAction(harness, actionButton());

  const confirmButton = actionButton({
    action: "confirm-exclusion",
    reviewSource: "CompletedCycle",
    reviewRecordId: "42"
  });
  confirmButton.dataset.reportRecordKey = "completed:42";
  harness.registerActionControl(confirmButton);

  assert.equal(confirmButton.disabled, false);
  await dispatchAction(harness, confirmButton);
  assert.equal(
    harness.requests[1].url,
    "/api/reports/cycles/42/confirm-exclusion");
  assert.match(
    harness.elements.get("reportActionFeedback").innerHTML,
    /remains excluded/);
});

test("focus moves after affected removal but is not stolen when the user moves elsewhere", async () => {
  const firstReloads = [];
  const firstHarness = createHarness({ reloadResponses: firstReloads });
  firstHarness.reports.wire();
  const removed = firstHarness.registerActionControl(actionButton());
  removed.focus();
  firstReloads.push(() => {
    firstHarness.setPayload(reportPayload());
    firstHarness.reports.render();
  });
  await dispatchAction(firstHarness, removed);
  assert.equal(
    firstHarness.document.activeElement,
    firstHarness.elements.get("reportActionFeedback"));

  const secondReloads = [];
  const secondHarness = createHarness({ reloadResponses: secondReloads });
  secondHarness.reports.wire();
  const origin = secondHarness.registerActionControl(actionButton());
  const elsewhere = new FakeElement("elsewhere");
  origin.focus();
  secondReloads.push(() => {
    elsewhere.focus();
    secondHarness.setPayload(reportPayload());
    secondHarness.reports.render();
  });
  await dispatchAction(secondHarness, origin);
  assert.equal(secondHarness.document.activeElement, elsewhere);
});

test("background focus, other rows, stale operations, and retained rows never transfer focus", async () => {
  const backgroundRequest = deferred();
  const backgroundReloads = [];
  const backgroundHarness = createHarness({
    requestResponses: [() => backgroundRequest.promise],
    reloadResponses: backgroundReloads
  });
  backgroundHarness.reports.wire();
  const backgroundOrigin = backgroundHarness.registerActionControl(actionButton());
  backgroundOrigin.focus();
  backgroundReloads.push(() => {
    backgroundHarness.setPayload(reportPayload());
    backgroundHarness.reports.render();
  });
  const backgroundDispatch = dispatchAction(backgroundHarness, backgroundOrigin);
  await Promise.resolve();
  backgroundHarness.document.activeElement = backgroundHarness.document.body;
  backgroundRequest.resolve({ ok: true, status: 204 });
  await backgroundDispatch;
  assert.equal(
    backgroundHarness.document.activeElement,
    backgroundHarness.document.body);

  const otherRequest = deferred();
  const otherReloads = [];
  const otherHarness = createHarness({
    requestResponses: [() => otherRequest.promise],
    reloadResponses: otherReloads
  });
  otherHarness.reports.wire();
  const first = otherHarness.registerActionControl(actionButton());
  const other = otherHarness.registerActionControl(actionButton({
    completedCycleId: "43",
    roomId: "4"
  }));
  other.dataset.reportRecordKey = "completed:43";
  first.focus();
  otherReloads.push(() => {
    otherHarness.setPayload({
      ...reportPayload(),
      recentCompletedCycles: [
        completedCycle(43, 4, "2026-07-29T12:00:00Z")
      ]
    });
    otherHarness.reports.render();
  });
  const otherDispatch = dispatchAction(otherHarness, first);
  await Promise.resolve();
  other.focus();
  otherRequest.resolve({ ok: true, status: 204 });
  await otherDispatch;
  assert.equal(otherHarness.document.activeElement, other);

  const staleReloads = [];
  const staleHarness = createHarness({ reloadResponses: staleReloads });
  staleHarness.reports.wire();
  const staleOrigin = staleHarness.registerActionControl(actionButton());
  staleOrigin.focus();
  const completedBody = staleHarness.elements.get("completedCyclesBody");
  const disconnectHook = completedBody.innerHTMLSetHook;
  let resetDuringRender = false;
  completedBody.innerHTMLSetHook = html => {
    disconnectHook(html);
    if (!resetDuringRender) {
      resetDuringRender = true;
      staleHarness.reports.renderAccessPrompt(401);
    }
  };
  staleReloads.push(() => {
    staleHarness.setPayload(reportPayload());
    staleHarness.reports.render();
  });
  await dispatchAction(staleHarness, staleOrigin);
  assert.equal(staleHarness.document.activeElement, staleHarness.document.body);

  const failureHarness = createHarness({
    requestResponses: [{ ok: false, status: 400, text: async () => "No change." }]
  });
  failureHarness.reports.wire();
  const failureOrigin = failureHarness.registerActionControl(actionButton());
  failureOrigin.focus();
  await dispatchAction(failureHarness, failureOrigin);
  assert.equal(
    failureHarness.document.activeElement,
    failureOrigin.closest("[data-report-action-row]"));
});

test("feedback retirement moves owned focus before hiding and preserves unrelated focus", async t => {
  const originalWarn = console.warn;
  t.after(() => {
    console.warn = originalWarn;
  });
  console.warn = () => {};
  for (const focusDescendant of [false, true]) {
    const harness = createHarness({
      requestResponses: [new Error("unknown outcome")]
    });
    harness.reports.wire();
    await dispatchAction(harness, actionButton());
    const wrapper = harness.elements.get("reportActionFeedback");
    const entry = feedbackEntry(harness, "completed:42");
    const focusTarget = focusDescendant
      ? entry.children[2].children[0]
      : wrapper;
    focusTarget.focus();
    harness.domMutations.length = 0;

    harness.reports.renderAccessPrompt(401);

    const token = harness.elements.get("reportAccessToken");
    assert.equal(harness.document.activeElement, token);
    assert.equal(wrapper.hidden, true);
    const focusIndex = harness.domMutations.findIndex(event =>
      event.type === "focus" && event.element === token);
    const hideIndex = harness.domMutations.findIndex(event =>
      event.type === "hidden" && event.element === wrapper && event.value === true);
    assert.ok(focusIndex >= 0);
    assert.ok(hideIndex > focusIndex);
  }

  const ordinary = createHarness();
  ordinary.reports.wire();
  await dispatchAction(ordinary, actionButton());
  const ordinaryWrapper = ordinary.elements.get("reportActionFeedback");
  ordinaryWrapper.focus();
  const rangeChip = new FakeElement();
  rangeChip.dataset.rangePreset = "all";
  await ordinary.elements.get("reportDateRange").dispatch("click", {
    target: targetFor(new Map([
      [".report-range-chip", rangeChip]
    ]))
  });
  assert.equal(
    ordinary.document.activeElement,
    ordinary.elements.get("reportsMain"));
  assert.equal(ordinaryWrapper.hidden, true);

  const preserved = createHarness();
  preserved.reports.wire();
  await dispatchAction(preserved, actionButton());
  const elsewhere = new FakeElement("elsewhere");
  elsewhere.focus();
  await preserved.elements.get("reportDateRange").dispatch("click", {
    target: targetFor(new Map([
      [".report-range-chip", rangeChip]
    ]))
  });
  assert.equal(preserved.document.activeElement, elsewhere);
  assert.equal(preserved.elements.get("reportActionFeedback").hidden, true);

  const capacity = createHarness({
    requestResponses: Array.from(
      { length: 10 },
      (_, index) => new Error(`uncertain ${index}`))
  });
  capacity.reports.wire();
  for (let index = 0; index < 10; index++) {
    await dispatchAction(capacity, actionButton({
      completedCycleId: String(400 + index),
      roomId: String(index + 1)
    }));
  }
  await dispatchAction(capacity, actionButton({
    completedCycleId: "500",
    roomId: "11"
  }));
  const capacityWrapper = capacity.elements.get("reportActionFeedback");
  for (const regionId of ["reportActionStatusPolite", "reportActionStatusAssertive"]) {
    for (const entry of [...capacity.elements.get(regionId).children]) {
      if (entry.dataset.recordKey) {
        entry.remove();
      }
    }
  }
  assert.equal(
    capacity.elements.get("reportActionStatusAssertive").childElementCount,
    1);
  capacityWrapper.focus();
  capacity.reports.renderAccessPrompt(401);
  assert.equal(capacity.document.activeElement, capacity.elements.get("reportAccessToken"));
  assert.equal(capacityWrapper.hidden, true);
  assert.equal(capacity.document.getElementById("report-action-capacity"), null);
});

test("success replacement keeps wrapper focus while feedback remains non-empty", async () => {
  const harness = createHarness();
  harness.reports.wire();
  await dispatchAction(harness, actionButton({
    completedCycleId: "300",
    roomId: "1"
  }));
  const wrapper = harness.elements.get("reportActionFeedback");
  wrapper.focus();

  await dispatchAction(harness, actionButton({
    completedCycleId: "301",
    roomId: "2"
  }));

  assert.equal(harness.document.activeElement, wrapper);
  assert.equal(wrapper.hidden, false);
  assert.equal(
    harness.elements.get("reportActionStatusPolite").childElementCount,
    1);
});

test("unresolved action state is capped without evicting or blocking existing identities", async t => {
  const originalWarn = console.warn;
  t.after(() => {
    console.warn = originalWarn;
  });
  console.warn = () => {};
  const requestResponses = Array.from(
    { length: 10 },
    (_, index) => new Error(`uncertain ${index}`));
  const reloadResponses = [];
  const harness = createHarness({ requestResponses, reloadResponses });
  harness.reports.wire();

  for (let index = 0; index < 10; index++) {
    await dispatchAction(harness, actionButton({
      completedCycleId: String(100 + index),
      roomId: String(index + 1)
    }));
  }
  const assertive = harness.elements.get("reportActionStatusAssertive");
  assert.equal(
    assertive.children.filter(entry => entry.dataset.recordKey).length,
    10);

  const confirmationsAtCap = harness.confirmations.length;
  const requestsAtCap = harness.requests.length;
  await dispatchAction(harness, actionButton({
    completedCycleId: "200",
    roomId: "11"
  }));
  assert.equal(harness.confirmations.length, confirmationsAtCap);
  assert.equal(harness.requests.length, requestsAtCap);
  assert.equal(feedbackEntry(harness, "completed:200"), null);
  assert.equal(
    assertive.children.filter(entry => entry.id === "report-action-capacity").length,
    1);
  assert.match(
    harness.document.getElementById("report-action-capacity").textContent,
    /Resolve or refresh an existing report action/);
  for (let index = 0; index < 10; index++) {
    assert.ok(feedbackEntry(harness, `completed:${100 + index}`));
  }

  reloadResponses.push(() => harness.setPayload({
    ...reportPayload(),
    recentCompletedCycles: [
      completedCycle(100, 1, "2026-07-29T12:00:00Z")
    ]
  }));
  await dispatchAction(
    harness,
    statusAction("refresh-report-action", "completed:100"));
  assert.match(
    feedbackEntry(harness, "completed:100").innerHTML,
    /Try action again/);
  assert.ok(harness.document.getElementById("report-action-capacity"));

  requestResponses.push({ ok: true, status: 204 });
  reloadResponses.push(() => harness.setPayload({
    ...reportPayload(),
    exceptionReviewRecords: [{
      sourceType: "CompletedCycle",
      reviewRecordId: 100,
      completedCycleId: 100,
      roomId: 1,
      seatedAt: "2026-07-29T12:00:00Z"
    }]
  }));
  await dispatchAction(
    harness,
    statusAction("retry-report-mutation", "completed:100"));
  assert.equal(harness.document.getElementById("report-action-capacity"), null);
  for (let index = 1; index < 10; index++) {
    assert.match(
      feedbackEntry(harness, `completed:${100 + index}`).innerHTML,
      /Outcome uncertain/);
  }

  requestResponses.push(new Error("new identity is now allowed"));
  await dispatchAction(harness, actionButton({
    completedCycleId: "200",
    roomId: "11"
  }));
  assert.equal(harness.confirmations.length, confirmationsAtCap + 2);
  assert.equal(harness.requests.length, requestsAtCap + 2);
  assert.match(feedbackEntry(harness, "completed:200").innerHTML, /Outcome uncertain/);
});

test("completed-success retention remains independently limited to one entry", async () => {
  const harness = createHarness();
  harness.reports.wire();
  await dispatchAction(harness, actionButton({
    completedCycleId: "300",
    roomId: "1"
  }));
  await dispatchAction(harness, actionButton({
    completedCycleId: "301",
    roomId: "2"
  }));
  const successes = harness.elements.get("reportActionStatusPolite").children
    .filter(entry => entry.dataset.phase === "success");
  assert.equal(successes.length, 1);
  assert.equal(successes[0].dataset.recordKey, "completed:301");
});

test("a retained success cannot become an eleventh unresolved identity", async t => {
  const originalWarn = console.warn;
  t.after(() => {
    console.warn = originalWarn;
  });
  console.warn = () => {};
  const requestResponses = [
    { ok: true, status: 204 },
    ...Array.from({ length: 10 }, (_, index) => new Error(`unknown ${index}`))
  ];
  const harness = createHarness({ requestResponses });
  harness.reports.wire();
  await dispatchAction(harness, actionButton({
    completedCycleId: "50",
    roomId: "1"
  }));
  for (let index = 0; index < 10; index++) {
    await dispatchAction(harness, actionButton({
      completedCycleId: String(100 + index),
      roomId: String(index + 2)
    }));
  }

  const confirmationsAtCap = harness.confirmations.length;
  const requestsAtCap = harness.requests.length;
  await dispatchAction(harness, actionButton({
    action: "confirm-exclusion",
    completedCycleId: "50",
    reviewSource: "CompletedCycle",
    reviewRecordId: "50"
  }));
  assert.equal(harness.confirmations.length, confirmationsAtCap);
  assert.equal(harness.requests.length, requestsAtCap);
  assert.equal(feedbackEntry(harness, "completed:50").dataset.phase, "success");
  assert.ok(harness.document.getElementById("report-action-capacity"));
});

test("status state survives ordinary filter, doctor, and tab rerenders", async t => {
  const originalWarn = console.warn;
  t.after(() => {
    console.warn = originalWarn;
  });
  console.warn = () => {};
  const harness = createHarness({
    requestResponses: [new Error("unknown")]
  });
  harness.reports.wire();
  await dispatchAction(harness, actionButton());
  const status = harness.elements.get("reportActionFeedback");
  const before = status.innerHTML;

  await harness.elements.get("reportFilterBar").dispatch("click", {
    target: targetFor(new Map([
      [".report-filter-chip", harness.filterChips[1]]
    ]))
  });
  const doctor = new FakeElement();
  doctor.dataset.reportDoctorId = "otte";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([
      ["[data-report-doctor-id]", doctor]
    ]))
  });
  const tab = new FakeElement();
  tab.dataset.reportDoctorTab = "flow";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([
      ["[data-report-doctor-tab]", tab]
    ]))
  });

  assert.equal(status.innerHTML, before);
  assert.match(status.innerHTML, /Outcome uncertain/);
});

test("report action completion paths retain confirmations and never invoke blocking alerts", () => {
  assert.match(moduleSource, /confirm\(confirmationMessage\)/);
  assert.match(moduleSource, /Mark \$\{label\} as an exception/);
  assert.match(moduleSource, /Confirm exclusion of this exception/);
  assert.doesNotMatch(moduleSource, /(?:window\.)?alert\s*\(/);
  assert.match(moduleSource, /operationId: \+\+nextReportActionOperationId/);
  assert.match(moduleSource, /isCurrentOperation\(entry\)/);
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
