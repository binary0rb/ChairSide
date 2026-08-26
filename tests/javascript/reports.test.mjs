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
const stylesUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/styles.css",
  import.meta.url);
const moduleSource = await readFile(moduleUrl, "utf8");
const reportsHtmlSource = await readFile(reportsHtmlUrl, "utf8");
const boardSource = await readFile(boardUrl, "utf8");
const applicationStateSource = await readFile(applicationStateUrl, "utf8");
const domUtilsSource = await readFile(domUtilsUrl, "utf8");
const formatUtilsSource = await readFile(formatUtilsUrl, "utf8");
const stylesSource = await readFile(stylesUrl, "utf8");

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
const anomalyReviewDataUrl = `data:text/javascript;base64,${Buffer.from(`
export function createAnomalyReview() {
  return {
    onReportRendered() {},
    wire() {},
    showStatus(status) { globalThis.__chairsideReportsHarness.anomalyStatuses.push(status); },
    openEncounter() {},
    openForMark() {}
  };
}`).toString("base64")}`;
const moduleWithDataImports = moduleSource
  .replace('"./common-interactions.js"', JSON.stringify(commonInteractionsDataUrl))
  .replace('"./request-utils.js"', JSON.stringify(requestUtilsDataUrl))
  .replace('"./dom-utils.js"', JSON.stringify(domUtilsDataUrl))
  .replace('"./format-utils.js"', JSON.stringify(formatUtilsDataUrl))
  .replace('"./anomaly-review.js"', JSON.stringify(anomalyReviewDataUrl));
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

function scheduleFitSummary(populationCount, pairedCaseCount = populationCount, {
  expectedSeconds = pairedCaseCount * 1800,
  observedSeconds = pairedCaseCount * 1800,
  slackSeconds = 0,
  debtSeconds = 0,
  netVarianceSeconds = observedSeconds - expectedSeconds
} = {}) {
  return {
    populationCount,
    pairedCaseCount,
    populationCoverage: populationCount ? pairedCaseCount / populationCount : 0,
    totalExpectedSeconds: expectedSeconds,
    totalObservedSeconds: observedSeconds,
    totalSlackSeconds: slackSeconds,
    totalDebtSeconds: debtSeconds,
    netVarianceSeconds,
    medianExpectedSeconds: pairedCaseCount ? 1800 : null,
    medianObservedSeconds: pairedCaseCount ? 1800 : null,
    medianPairedVarianceSeconds: pairedCaseCount ? 0 : null,
    lessTimeCaseCount: 0,
    atExpectedCaseCount: pairedCaseCount,
    moreTimeCaseCount: 0,
    sample: sample(populationCount, pairedCaseCount)
  };
}

function calibrationEvaluation({
  decision = "InsufficientDirectionalConsistency",
  currentDefaultAllocationMinutes = 30,
  totalPairedCaseCount = 10,
  aboveBaselineCaseCount = 0,
  belowBaselineCaseCount = 0,
  equalBaselineCaseCount = 10,
  moreThanToleranceCaseCount = 0,
  lessThanToleranceCaseCount = 0,
  atExpectedCaseCount = 10,
  directionalShare = 0,
  medianPairedVarianceSeconds = 0,
  candidateDirection = null,
  insight = null
} = {}) {
  return {
    decision,
    currentDefaultAllocationMinutes,
    totalPairedCaseCount,
    aboveBaselineCaseCount,
    belowBaselineCaseCount,
    equalBaselineCaseCount,
    moreThanToleranceCaseCount,
    lessThanToleranceCaseCount,
    atExpectedCaseCount,
    directionalShare,
    medianPairedVarianceSeconds,
    candidateDirection,
    insight
  };
}

function scheduleFitProjection(otteCases, pledgerCases) {
  const population = otteCases + pledgerCases;
  const procedureSummary = scheduleFitSummary(population);
  return {
    overall: {
      cycleCount: population,
      blockMinutes: 10,
      totalExpectedMinutes: population * 30,
      totalMeasuredMinutes: population * 30,
      totalVarianceMinutes: 0,
      totalSlackMinutes: 0,
      totalDebtMinutes: 0,
      totalExpectedBlocks: population * 3,
      totalActualBlocks: population * 3,
      totalVarianceBlocks: 0,
      utilizationRatio: population ? 1 : null
    },
    includedCycleCount: population,
    scheduleFitCycleCount: population,
    practice: procedureSummary,
    doctorSummaries: [
      { doctorId: "otte", doctorName: "Dr. Otte", historicalAssignedFit: scheduleFitSummary(otteCases) },
      { doctorId: "pledger", doctorName: "Dr. Pledger", historicalAssignedFit: scheduleFitSummary(pledgerCases) }
    ],
    procedureSegments: population
      ? [{
          procedureCode: "EXT",
          procedureLabel: "Extraction",
          baseProcedureCode: "EXT",
          procedureGrouping: "Family",
          isSedationCase: null,
          currentDefaultAllocationMinutes: 30,
          historicalAssignedFit: procedureSummary,
          currentDefaultCalibration: calibrationEvaluation({
            totalPairedCaseCount: population,
            equalBaselineCaseCount: population,
            atExpectedCaseCount: population
          }),
          doctorBreakdown: [
            ...(otteCases ? [{
              doctorId: "otte",
              doctorName: "Dr. Otte",
              historicalAssignedFit: scheduleFitSummary(otteCases),
              currentDefaultCalibration: calibrationEvaluation({
                totalPairedCaseCount: otteCases,
                equalBaselineCaseCount: otteCases,
                atExpectedCaseCount: otteCases,
                decision: otteCases < 10 ? "BelowMinimumSample" : "InsufficientDirectionalConsistency"
              })
            }] : []),
            ...(pledgerCases ? [{
              doctorId: "pledger",
              doctorName: "Dr. Pledger",
              historicalAssignedFit: scheduleFitSummary(pledgerCases),
              currentDefaultCalibration: calibrationEvaluation({
                totalPairedCaseCount: pledgerCases,
                equalBaselineCaseCount: pledgerCases,
                atExpectedCaseCount: pledgerCases,
                decision: pledgerCases < 10 ? "BelowMinimumSample" : "InsufficientDirectionalConsistency"
              })
            }] : [])
          ]
        }]
      : [],
    rules: {
      version: "1",
      minimumPairedCases: 10,
      atExpectedToleranceSeconds: 600,
      minimumDirectionalShare: 0.8,
      directionalMethod: "RawPairedVarianceSign",
      directionalDenominator: "AllPairedCases",
      materialDeviationSeconds: 600,
      materialComparison: "StrictlyGreaterThan",
      centralMethod: "MedianPairedVariance",
      persistenceRequirement: "SelectedPopulationOnly",
      baseline: "CurrentRosterDefault"
    }
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

function procedureIntelligenceRow({
  completedCount = 6,
  doctorTimeCount = completedCount,
  lowerSeconds = 720,
  upperSeconds = 1080,
  doctorBreakdown = []
} = {}) {
  const assignedCount = Math.max(0, completedCount - 1);
  const firstAssignedCount = Math.min(2, assignedCount);
  const remainingAssignedCount = assignedCount - firstAssignedCount;
  const assignedValues = [
    ...(firstAssignedCount ? [{ minutes: 30, caseCount: firstAssignedCount }] : []),
    ...(remainingAssignedCount ? [{ minutes: 40, caseCount: remainingAssignedCount }] : [])
  ];
  const firstCapturedCount = Math.min(2, completedCount);
  const remainingCapturedCount = completedCount - firstCapturedCount;
  const capturedValues = [
    ...(firstCapturedCount ? [{ minutes: 20, caseCount: firstCapturedCount }] : []),
    ...(remainingCapturedCount ? [{ minutes: 30, caseCount: remainingCapturedCount }] : [])
  ];
  return {
    procedureCode: "EXT",
    procedureLabel: "Extraction",
    baseProcedureCode: "EXT",
    procedureGrouping: "Family",
    isSedationCase: null,
    currentDefaultAllocationMinutes: 30,
    allocationBehavior: "Variable",
    metrics: {
      completedCaseCount: completedCount,
      completedSample: sample(completedCount),
      medianDoctorTimeSeconds: doctorTimeCount ? 900 : null,
      averageDoctorTimeSeconds: doctorTimeCount ? 930 : null,
      typicalDoctorTimeLowerSeconds: lowerSeconds,
      typicalDoctorTimeUpperSeconds: upperSeconds,
      typicalDoctorTimeMethod: "Type7Iqr",
      doctorTimeSample: sample(completedCount, doctorTimeCount),
      medianReadyWaitSeconds: 180,
      averageReadyWaitSeconds: 210,
      readyWaitSample: sample(completedCount),
      medianSeatedToDoctorCompleteSeconds: 1500,
      averageSeatedToDoctorCompleteSeconds: 1560,
      seatedToDoctorCompleteSample: sample(completedCount),
      medianHistoricalAssignedAllocationMinutes: 40,
      historicalAssignedAllocationSample: sample(completedCount, assignedCount),
      historicalAssignedAllocationValues: assignedValues,
      historicalCapturedDefaultValues: capturedValues
    },
    doctorBreakdown
  };
}

function doctorFlowSummary(doctorId, doctorName, caseCount) {
  const completed = sample(caseCount);
  const observed = sample(caseCount > 0 ? 1 : 0);
  return {
    doctorId,
    doctorName,
    completedCaseCount: caseCount,
    medianReadyWaitSeconds: caseCount > 0 ? 120 : null,
    medianDoctorTimeSeconds: caseCount > 0 ? 900 : null,
    medianObservedClinicalSpanMinutes: caseCount > 0 ? 45 : null,
    peakConcurrentRooms: caseCount > 0 ? Math.min(caseCount, 2) : null,
    observedDoctorDayCount: caseCount > 0 ? 1 : 0,
    samples: {
      completedCases: completed,
      readyWait: completed,
      doctorTime: completed,
      observedDays: observed
    }
  };
}

function observedDoctorFlowDay(doctorId, doctorName, reportDate, caseCount) {
  return {
    doctorId,
    doctorName,
    reportDate,
    qualifyingCaseCount: caseCount,
    firstAcceptedReadyAt: `${reportDate}T14:00:00Z`,
    lastDoctorCompleteAt: `${reportDate}T14:45:00Z`,
    observedClinicalSpanMinutes: 45,
    minutesWithOneDoctorWorkingRoom: 30,
    minutesWithTwoDoctorWorkingRooms: 10,
    minutesWithThreeOrMoreDoctorWorkingRooms: 0,
    unstructuredTimeMinutes: 5,
    peakConcurrentRooms: caseCount > 1 ? 2 : 1
  };
}

function doctorFlowTrendBucket({
  startDate,
  endDate,
  effectiveStartDate = startDate,
  effectiveEndDate = endDate,
  isPartial = false,
  readyWait = null,
  doctorTime = null,
  completedCases = null,
  clinicalSpan = null,
  readyWaitSample = sample(readyWait === null ? 0 : 1),
  doctorTimeSample = sample(doctorTime === null ? 0 : 1),
  completedSample = sample(completedCases === null ? 0 : completedCases),
  clinicalSpanSample = sample(clinicalSpan === null ? 0 : 1)
}) {
  return {
    startDate,
    endDate,
    effectiveStartDate,
    effectiveEndDate,
    isPartial,
    medianReadyWaitSeconds: readyWait,
    medianDoctorTimeSeconds: doctorTime,
    completedCaseCount: completedCases,
    medianObservedClinicalSpanMinutes: clinicalSpan,
    samples: {
      readyWait: readyWaitSample,
      doctorTime: doctorTimeSample,
      completedCases: completedSample,
      observedClinicalSpan: clinicalSpanSample
    }
  };
}

function doctorFlowTrendSeries(doctorId, doctorName, caseCount) {
  const observed = caseCount > 0;
  return {
    doctorId,
    doctorName,
    bucketSize: "Week",
    effectiveStartDate: "2026-07-01",
    effectiveEndDate: "2026-07-30",
    buckets: [
      doctorFlowTrendBucket({
        startDate: "2026-06-29",
        endDate: "2026-07-06",
        effectiveStartDate: "2026-07-01",
        effectiveEndDate: "2026-07-06",
        isPartial: true
      }),
      doctorFlowTrendBucket({
        startDate: "2026-07-06",
        endDate: "2026-07-13",
        readyWait: observed ? 120 : null,
        doctorTime: observed ? 900 : null,
        completedCases: observed ? caseCount : null,
        clinicalSpan: observed ? 45 : null,
        readyWaitSample: sample(caseCount),
        doctorTimeSample: sample(caseCount),
        completedSample: sample(caseCount),
        clinicalSpanSample: sample(observed ? 1 : 0)
      }),
      doctorFlowTrendBucket({
        startDate: "2026-07-13",
        endDate: "2026-07-20"
      }),
      doctorFlowTrendBucket({
        startDate: "2026-07-20",
        endDate: "2026-07-27"
      }),
      doctorFlowTrendBucket({
        startDate: "2026-07-27",
        endDate: "2026-08-03",
        effectiveStartDate: "2026-07-27",
        effectiveEndDate: "2026-07-30",
        isPartial: true
      })
    ]
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
    scheduleFit: scheduleFitProjection(otteCases, pledgerCases),
    doctorFlowSummaries: [
      doctorFlowSummary("otte", "Dr. Otte", otteCases),
      doctorFlowSummary("pledger", "Dr. Pledger", pledgerCases)
    ],
    doctorFlowTrends: [
      doctorFlowTrendSeries("otte", "Dr. Otte", otteCases),
      doctorFlowTrendSeries("pledger", "Dr. Pledger", pledgerCases)
    ],
    observedDoctorFlowDays: [
      ...(otteCases > 0 ? [observedDoctorFlowDay("otte", "Dr. Otte", "2026-07-14", otteCases)] : []),
      ...(pledgerCases > 0 ? [observedDoctorFlowDay("pledger", "Dr. Pledger", "2026-07-15", pledgerCases)] : [])
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
    procedureIntelligenceRows: completedCount > 0
      ? [procedureIntelligenceRow({
          completedCount,
          doctorTimeCount: completedCount,
          lowerSeconds: completedCount >= 5 ? 720 : null,
          upperSeconds: completedCount >= 5 ? 1080 : null
        })]
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
  reloadResponses = [],
  auditResponses = []
} = {}) {
  const elements = new Map();
  const ids = context.isReports
    ? [
        "reportFilterBar",
        "reportProcedureMix",
        "reportProcedureIntelligence",
        "reportTrendPanel",
        "doctorReportDashboard",
        "doctorReportCards",
        "selectedDoctorPanel",
        "allocationBalanceCard",
        "dataQualityCard",
        "dataQualityStatus",
        "reportAuditEvidence",
        "reportAuditBody",
        "reportReviewQueue",
        "reportReviewQueueBody",
        "reportReviewQueueCount",
        "reportReviewedHistory",
        "reportReviewedHistoryBody",
        "reportReviewedHistoryCount",
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
        "reportRangeCustom",
        "reportRangeStart",
        "reportRangeEnd",
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
  const auditQueries = [];
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
    auditQueries,
    anomalyStatuses: [],
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
    queryAudit: async selection => {
      auditQueries.push(structuredClone(selection));
      const result = auditResponses.shift();
      if (result instanceof Error) throw result;
      return result || {
        mode: selection.contributorKind === "PendingReview" || selection.contributorKind === "ReviewedExceptionHistory"
          ? "ExceptionReview"
          : selection.contributorKind === "PracticeCompletedCases" || selection.contributorKind === "IncludedCompletedCases"
            ? "CompletedCaseAudit"
            : "MetricEvidence",
        rows: [],
        reviewRows: [],
        returnedCount: 0,
        totalMatchingCount: 0,
        offset: selection.offset || 0,
        limit: selection.limit || 50,
        hasMore: false,
        activeSort: selection.sort || "MostRecent",
        supportedSorts: ["MostRecent", "Doctor", "Procedure"]
      };
    },
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

function dispatchAction(harness, button) {
  return harness.document.dispatch("click", {
    target: targetFor(new Map([
      [button.actionSelector, button]
    ]))
  });
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

async function openDoctorScheduleFitTab(harness) {
  const scheduleTab = new FakeElement();
  scheduleTab.dataset.reportDoctorTab = "schedule";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([
      ["[data-report-doctor-tab]", scheduleTab]
    ]))
  });
}

function allocationPayload(populationCount, contributingCount) {
  const net = contributingCount >= 5 ? 10 : contributingCount > 0 ? 4 : 0;
  const context = sample(populationCount, contributingCount);
  const payload = {
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
  const exact = scheduleFitSummary(populationCount, contributingCount, {
    expectedSeconds: contributingCount * 1800,
    observedSeconds: contributingCount * 1800 + net * 60,
    slackSeconds: net < 0 ? Math.abs(net) * 60 : 0,
    debtSeconds: net > 0 ? net * 60 : 0,
    netVarianceSeconds: net * 60
  });
  payload.scheduleFit.practice = exact;
  payload.scheduleFit.doctorSummaries[0].historicalAssignedFit = exact;
  if (payload.scheduleFit.procedureSegments.length) {
    payload.scheduleFit.procedureSegments[0].historicalAssignedFit = exact;
  }
  return payload;
}

function allocationSurfaceHtml(harness) {
  return [
    "allocationBalanceCard",
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
    "[data-report-doctor-id], [data-report-doctor-tab], .report-table button, .procedure-intelligence-toggle");

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
test("custom date draft survives ordinary Reports rerenders until Apply", async () => {
  const harness = createHarness();
  harness.reports.wire();
  const customChip = new FakeElement();
  customChip.dataset.rangePreset = "custom";
  await harness.elements.get("reportDateRange").dispatch("click", {
    target: targetFor(new Map([
      [".report-range-chip", customChip]
    ]))
  });

  const start = harness.elements.get("reportRangeStart");
  const end = harness.elements.get("reportRangeEnd");
  start.value = "2026-08-12";
  await harness.elements.get("reportDateRange").dispatch("input", { target: start });
  harness.reports.render();
  assert.equal(start.value, "2026-08-12");
  assert.equal(end.value, "2026-07-29");

  end.value = "2026-08-13";
  await harness.elements.get("reportDateRange").dispatch("input", { target: end });
  harness.reports.render();
  assert.equal(start.value, "2026-08-12");
  assert.equal(end.value, "2026-08-13");

  await harness.elements.get("reportDateRange").dispatch("click", {
    target: targetFor(new Map([
      ["#reportRangeApply", new FakeElement()]
    ]))
  });
  assert.deepEqual(harness.reloadQueries.at(-1).window, {
    preset: "custom",
    start: "2026-08-12",
    end: "2026-08-13"
  });
});

test("Doctor scope preserves the server name for represented historical doctors", () => {
  const harness = createHarness({
    payload: {
      ...reportPayload(),
      doctorFlowSummaries: [
        doctorFlowSummary("otte", "Dr. Otte", 5),
        doctorFlowSummary("schroeder", "Dr. Schroeder", 5)
      ]
    }
  });
  harness.reports.wire();
  harness.reports.render();

  const options = harness.elements.get("reportScopeDoctor").innerHTML;
  assert.match(options, /value="schroeder">Dr\. Schroeder<\/option>/);
  assert.doesNotMatch(options, /value="schroeder">schroeder<\/option>/);
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
  assert.match(panel.innerHTML, /Room Load \/ Flow/);
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

test("Doctor Overview uses the six canonical flow metrics without allocation or pressure shortcuts", () => {
  const payload = reportPayload({ otteCases: 5, pledgerCases: 0 });
  payload.doctorFlowSummaries = [{
    ...doctorFlowSummary("otte", "Dr. Otte", 5),
    medianReadyWaitSeconds: 123,
    medianDoctorTimeSeconds: 987,
    medianObservedClinicalSpanMinutes: 77,
    peakConcurrentRooms: 3,
    observedDoctorDayCount: 5,
    samples: {
      completedCases: sample(5),
      readyWait: sample(5),
      doctorTime: sample(5),
      observedDays: sample(5)
    }
  }];
  const harness = createHarness({ payload });

  harness.reports.render();

  const cards = harness.elements.get("doctorReportCards").innerHTML;
  const panel = harness.elements.get("selectedDoctorPanel").innerHTML;
  for (const label of [
    "Completed Cases",
    "Median Ready Wait",
    "Median Doctor Time",
    "Median Observed Clinical Span",
    "Peak Concurrent Rooms",
    "Observed Doctor Days"
  ]) {
    assert.match(cards, new RegExp(label));
    assert.match(panel, new RegExp(label));
  }
  assert.match(cards, /02:03/);
  assert.match(cards, /16:27/);
  assert.match(cards, /77 min/);
  assert.match(cards, /3\+ rooms/);
  assert.doesNotMatch(cards, /Allocation|Pressure point|sparkline/i);
  for (const tab of ["Overview", "Trends", "Procedures", "Room Load \/ Flow", "Schedule Fit", "Case Audit"]) {
    assert.match(panel, new RegExp(tab));
  }
});

test("Room Load Flow tab reads only canonical observedDoctorFlowDays and preserves its minute partition", async () => {
  const payload = {
    ...reportPayload({ otteCases: 2, pledgerCases: 0 }),
    observedDoctorDays: [{
      doctorId: "otte",
      reportDate: "2026-07-14",
      encounterCount: 99,
      observedTeamSpanMinutes: 999
    }]
  };
  const harness = createHarness({ payload });
  harness.reports.wire();
  harness.reports.render();

  const flowTab = new FakeElement();
  flowTab.dataset.reportDoctorTab = "flow";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([["[data-report-doctor-tab]", flowTab]]))
  });

  const panel = harness.elements.get("selectedDoctorPanel").innerHTML;
  assert.match(panel, /Clinical Span/);
  assert.match(panel, /Unstructured Time/);
  assert.match(panel, /1 Doctor Working room/);
  assert.match(panel, /2 Doctor Working rooms/);
  assert.match(panel, /3\+ Doctor Working rooms/);
  assert.match(panel, /45 min/);
  assert.match(panel, /5 min/);
  assert.match(panel, /30 min/);
  assert.match(panel, /10 min/);
  assert.match(panel, /0 min/);
  assert.doesNotMatch(panel, /Team Span|999|99 cases|idle|pressure point/i);
  assert.doesNotMatch(panel, /productivity|downtime|recoverable|unscheduled|attendance|hours worked/i);
});

test("Practice doctor cards preserve server roster membership, Empty state, and represented history", () => {
  const payload = reportPayload({ otteCases: 0, pledgerCases: 2 });
  payload.doctorFlowSummaries = [
    doctorFlowSummary("otte", "Dr. Otte", 0),
    doctorFlowSummary("pledger", "Dr. Pledger", 2),
    doctorFlowSummary("former", "Dr. Former", 1)
  ];
  const harness = createHarness({ payload });

  harness.reports.render();

  const cards = harness.elements.get("doctorReportCards").innerHTML;
  assert.ok(cards.indexOf('data-report-doctor-id="otte"') < cards.indexOf('data-report-doctor-id="pledger"'));
  assert.ok(cards.indexOf('data-report-doctor-id="pledger"') < cards.indexOf('data-report-doctor-id="former"'));
  assert.match(cards, /Dr\. Former/);
  const emptyCard = cards.match(/<article[^>]+data-report-doctor-id="otte"[\s\S]*?<\/article>/)?.[0];
  assert.ok(emptyCard);
  assert.match(emptyCard, /No observation/);
  assert.doesNotMatch(emptyCard, /<dd>0(?:<|\s)/);
});

test("phase-only doctor timing stays visible without classifying the card as empty", () => {
  const payload = reportPayload({ otteCases: 0, pledgerCases: 0 });
  payload.doctorFlowSummaries = [{
    doctorId: "otte",
    doctorName: "Dr. Otte",
    completedCaseCount: 0,
    medianReadyWaitSeconds: 360,
    medianDoctorTimeSeconds: 1200,
    medianObservedClinicalSpanMinutes: null,
    peakConcurrentRooms: null,
    observedDoctorDayCount: 0,
    samples: {
      completedCases: sample(0, 0),
      readyWait: sample(2, 2),
      doctorTime: sample(2, 1),
      observedDays: sample(0, 0)
    }
  }];
  const harness = createHarness({ payload });

  harness.reports.render();

  const cards = harness.elements.get("doctorReportCards").innerHTML;
  const card = cards.match(/<article[^>]+data-report-doctor-id="otte"[\s\S]*?<\/article>/)?.[0];
  assert.ok(card);
  assert.doesNotMatch(card, /No completed or phase observations/);
  assert.match(card, /No completed cases yet; phase timing observations are available in the current scope and range\./);
  assert.match(card, /Median Ready Wait[\s\S]*?06:00/);
  assert.match(card, /Median Doctor Time[\s\S]*?20:00/);
  assert.match(card, /Limited - N=2/);
  assert.match(card, /Limited - N=1/);
  assert.match(card, /Observed Doctor Days[\s\S]*?No observation/);
  assert.doesNotMatch(card, /class="doctor-report-card[^"]*\bis-empty\b/);

  const selectedPanel = harness.elements.get("selectedDoctorPanel").innerHTML;
  assert.match(selectedPanel, /No completed cases yet; phase timing observations are available in the current scope and range\./);

  const predicateSource = moduleSource.slice(
    moduleSource.indexOf("function hasDoctorPhaseTimingObservation"),
    moduleSource.indexOf("function doctorFlowSummarySentence"));
  assert.doesNotMatch(predicateSource, /limitedSampleThreshold|contributingCount\s*<\s*5|state\s*===\s*["']Limited["']/);
});

test("missing doctor-flow contributors stay Unavailable and Limited context stays neutral", () => {
  const unavailable = doctorFlowSummary("otte", "Dr. Otte", 6);
  unavailable.medianReadyWaitSeconds = null;
  unavailable.medianDoctorTimeSeconds = null;
  unavailable.medianObservedClinicalSpanMinutes = null;
  unavailable.peakConcurrentRooms = null;
  unavailable.observedDoctorDayCount = 0;
  unavailable.samples = {
    completedCases: sample(6),
    readyWait: sample(6, 0),
    doctorTime: sample(6, 0),
    observedDays: sample(2, 0)
  };
  const payload = reportPayload({ otteCases: 6, pledgerCases: 0 });
  payload.doctorFlowSummaries = [unavailable];
  const harness = createHarness({ payload });

  harness.reports.render();

  const cards = harness.elements.get("doctorReportCards").innerHTML;
  assert.match(cards, /Completed Cases[\s\S]*?>6</);
  assert.match(cards, /Median Ready Wait[\s\S]*?Unavailable/);
  assert.match(cards, /Median Doctor Time[\s\S]*?Unavailable/);
  assert.match(cards, /Median Observed Clinical Span[\s\S]*?Unavailable/);
  assert.match(cards, /Observed Doctor Days[\s\S]*?Unavailable/);
  assert.doesNotMatch(cards, /Pressure point|over expected|under expected|best|worst/i);

  const limitedPayload = reportPayload({ otteCases: 3, pledgerCases: 0 });
  limitedPayload.doctorFlowSummaries = [doctorFlowSummary("otte", "Dr. Otte", 3)];
  const limitedHarness = createHarness({ payload: limitedPayload });
  limitedHarness.reports.render();
  const limitedCards = limitedHarness.elements.get("doctorReportCards").innerHTML;
  assert.match(limitedCards, /Limited - N=3/);
  assert.doesNotMatch(limitedCards, /Pressure point|over expected|under expected|best|worst/i);
});

test("Doctor Trends renders four descriptive weekly metrics while Schedule Fit stays isolated", async () => {
  const harness = createHarness({ payload: reportPayload({ otteCases: 5, pledgerCases: 0 }) });
  harness.reports.wire();
  harness.reports.render();

  const overview = harness.elements.get("selectedDoctorPanel").innerHTML;
  assert.doesNotMatch(overview, /Net Variance|Over Expected|Under Expected|At Expected/);

  const trendsTab = new FakeElement();
  trendsTab.dataset.reportDoctorTab = "trends";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([["[data-report-doctor-tab]", trendsTab]]))
  });
  const trends = harness.elements.get("selectedDoctorPanel").innerHTML;
  assert.match(trends, /Weekly Doctor Trends/);
  assert.match(trends, /Median Ready Wait/);
  assert.match(trends, /Median Doctor Time/);
  assert.match(trends, /Completed Cases/);
  assert.match(trends, /Median Observed Clinical Span/);
  assert.match(trends, /Limited - N=5|Sufficient - N=5/);
  assert.match(trends, /Selected portion:/);
  assert.match(trends, /No observation/);
  assert.doesNotMatch(trends, /improved|declined|better|worse|forecast|target|ranking/i);

  const scheduleTab = new FakeElement();
  scheduleTab.dataset.reportDoctorTab = "schedule";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([["[data-report-doctor-tab]", scheduleTab]]))
  });
  const schedule = harness.elements.get("selectedDoctorPanel").innerHTML;
  assert.match(schedule, /Schedule Fit/);
  assert.match(schedule, /Signed net difference/);
  assert.match(schedule, /finalized historical scheduling allocation/);
  assert.doesNotMatch(schedule, /Calibration insight|automatic|recommendation/i);
});

test("Doctor Trends keeps gaps, server sample states, truthful zero, and local doctor selection", async () => {
  const payload = reportPayload({ otteCases: 5, pledgerCases: 5 });
  payload.doctorFlowTrends = [
    {
      doctorId: "otte",
      doctorName: "Dr. Otte",
      bucketSize: "Week",
      effectiveStartDate: "2026-07-06",
      effectiveEndDate: "2026-07-27",
      buckets: [
        doctorFlowTrendBucket({
          startDate: "2026-07-06",
          endDate: "2026-07-13",
          readyWait: 0,
          doctorTime: null,
          completedCases: 5,
          clinicalSpan: 45,
          readyWaitSample: sample(5),
          doctorTimeSample: sample(3, 0),
          completedSample: sample(5),
          clinicalSpanSample: sample(3)
        }),
        doctorFlowTrendBucket({
          startDate: "2026-07-13",
          endDate: "2026-07-20"
        }),
        doctorFlowTrendBucket({
          startDate: "2026-07-20",
          endDate: "2026-07-27",
          readyWait: 180,
          doctorTime: 1200,
          completedCases: 2,
          clinicalSpan: 60,
          readyWaitSample: sample(2),
          doctorTimeSample: sample(2),
          completedSample: sample(2),
          clinicalSpanSample: sample(2)
        })
      ]
    },
    {
      doctorId: "pledger",
      doctorName: "Dr. Pledger",
      bucketSize: "Week",
      effectiveStartDate: "2026-07-06",
      effectiveEndDate: "2026-07-27",
      buckets: [
        doctorFlowTrendBucket({ startDate: "2026-07-06", endDate: "2026-07-13" }),
        doctorFlowTrendBucket({ startDate: "2026-07-13", endDate: "2026-07-20" }),
        doctorFlowTrendBucket({
          startDate: "2026-07-20",
          endDate: "2026-07-27",
          readyWait: 777,
          doctorTime: 888,
          completedCases: 5,
          clinicalSpan: 99,
          readyWaitSample: sample(5),
          doctorTimeSample: sample(5),
          completedSample: sample(5),
          clinicalSpanSample: sample(5)
        })
      ]
    }
  ];
  const harness = createHarness({ payload });
  harness.reports.wire();
  harness.reports.render();

  const trendsTab = new FakeElement();
  trendsTab.dataset.reportDoctorTab = "trends";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([["[data-report-doctor-tab]", trendsTab]]))
  });
  const otte = harness.elements.get("selectedDoctorPanel").innerHTML;
  assert.match(otte, />00:00</);
  assert.match(otte, /Unavailable - 0 of 3 contributors/);
  assert.match(otte, /Limited - N=3/);
  assert.match(otte, /No observation/);
  assert.doesNotMatch(otte, />777</);
  assert.doesNotMatch(otte, /<svg|<path|<polyline/i);

  const pledgerCard = new FakeElement();
  pledgerCard.dataset.reportDoctorId = "pledger";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([["[data-report-doctor-id]", pledgerCard]]))
  });
  await harness.document.dispatch("click", {
    target: targetFor(new Map([["[data-report-doctor-tab]", trendsTab]]))
  });
  const pledger = harness.elements.get("selectedDoctorPanel").innerHTML;
  assert.match(pledger, /12:57/);
  assert.doesNotMatch(pledger, /Unavailable - 0 of 3 contributors/);
  assert.equal(harness.reloadCount, 0);

  const trendSource = moduleSource.slice(
    moduleSource.indexOf("function renderSelectedDoctorTrends"),
    moduleSource.indexOf("function renderSelectedDoctorEmptyState"));
  assert.doesNotMatch(trendSource, /observedDoctorDays|limitedSampleThreshold|contributingCount\s*<\s*5/);
  assert.match(trendSource, /canonicalDoctorFlowTrends/);
  assert.match(moduleSource, /return Array\.isArray\(report\?\.doctorFlowTrends\) \? report\.doctorFlowTrends : \[\]/);
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

test("Reports markup preserves the accepted integrated reading order", () => {
  const filterIndex = reportsHtmlSource.indexOf('id="reportFilterBar"');
  const headlineIndex = reportsHtmlSource.indexOf('id="reportHeadline"');
  const trendsIndex = reportsHtmlSource.indexOf('id="reportTrendPanel"');
  const mixIndex = reportsHtmlSource.indexOf('id="reportProcedureMix"');
  const doctorIndex = reportsHtmlSource.indexOf('id="doctorReportDashboard"');
  const selectedDoctorIndex = reportsHtmlSource.indexOf('id="selectedDoctorPanel"');
  const intelligenceIndex = reportsHtmlSource.indexOf('id="reportProcedureIntelligence"');
  const allocationIndex = reportsHtmlSource.indexOf('id="reportAllocation"');
  const insightsIndex = reportsHtmlSource.indexOf('id="reportInsights"');
  const auditIndex = reportsHtmlSource.indexOf('class="report-audit-evidence"');

  assert.ok(filterIndex >= 0);
  assert.ok(filterIndex < headlineIndex);
  assert.ok(headlineIndex < trendsIndex);
  assert.ok(trendsIndex < mixIndex);
  assert.ok(mixIndex < doctorIndex);
  assert.ok(doctorIndex < selectedDoctorIndex);
  assert.ok(selectedDoctorIndex < intelligenceIndex);
  assert.ok(intelligenceIndex < allocationIndex);
  assert.ok(allocationIndex < insightsIndex);
  assert.ok(insightsIndex < auditIndex);
});

test("tablet report filters keep the selected doctor readable before wrapping", () => {
  assert.match(
    reportsHtmlSource,
    /styles\.css\?v=20260817-report-acceptance/);
  assert.match(
    stylesSource,
    /\.report-scope-doctor\s*\{[\s\S]*?flex:\s*0 0 auto;/);
  assert.match(
    stylesSource,
    /\.report-scope-doctor select\s*\{[\s\S]*?width:\s*220px;/);
  assert.match(
    stylesSource,
    /@media \(max-width: 700px\)[\s\S]*?\.report-filter-group\s*\{[\s\S]*?flex-wrap:\s*wrap;/);
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
    }],
    procedureIntelligenceRows: [procedureIntelligenceRow({
      completedCount: 3,
      doctorTimeCount: 3,
      lowerSeconds: null,
      upperSeconds: null
    })]
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
  assert.match(moduleSource, /renderProcedureMixMarkup\(r, \{ headingTag: "h3", compact: true \}\)/);
  assert.match(harness.elements.get("selectedDoctorPanel").innerHTML, /Procedure Intelligence/);
  assert.doesNotMatch(harness.elements.get("selectedDoctorPanel").innerHTML, /Doctor breakdown/);
  assert.doesNotMatch(
    moduleSource.slice(
      moduleSource.indexOf("function renderSelectedDoctorProcedures"),
      moduleSource.indexOf("function formatProcedureShare")),
    /doctorProcedureMix|\.sort\(|\.reduce\(/);
});

test("Procedure Intelligence presents median-first timing, Type 7 range, and neutral allocation context", () => {
  const row = procedureIntelligenceRow({
    completedCount: 6,
    doctorBreakdown: [
      {
        doctorId: "otte",
        doctorName: "Dr. Otte",
        metrics: procedureIntelligenceRow({ completedCount: 5 }).metrics
      },
      {
        doctorId: "legacy",
        doctorName: "Dr. Historical",
        metrics: procedureIntelligenceRow({ completedCount: 1, lowerSeconds: null, upperSeconds: null }).metrics
      }
    ]
  });
  const harness = createHarness({
    payload: {
      ...reportPayload({ otteCases: 3, pledgerCases: 3 }),
      procedureIntelligenceRows: [row]
    }
  });

  harness.reports.render();

  const html = harness.elements.get("reportProcedureIntelligence").innerHTML;
  assert.match(html, /Procedure Intelligence/);
  assert.match(html, /Completed N/);
  assert.match(html, /Median Doctor Time/);
  assert.match(html, /Typical Doctor Time Range/);
  assert.match(html, /12:00 - 18:00/);
  assert.match(html, /Middle 50% of observed Doctor Time/);
  assert.ok(html.indexOf("Median Doctor Time") < html.indexOf("Typical Doctor Time Range"));
  assert.match(html, /Current roster default/);
  assert.match(html, /rough scheduling starting allocation/);
  assert.match(html, /Variable procedure - confirm the case-specific allocation/);
  assert.match(html, /Median Seated -&gt; Doctor Complete/);
  assert.doesNotMatch(html, /Total Room Cycle|efficiency|productive|ranking|score/i);
  assert.match(html, /type="button"/);
  assert.match(html, /aria-expanded="false"/);
  assert.match(html, /aria-controls="practice-procedure-intelligence-detail-0"/);
  assert.match(html, /data-audit-kind="ProcedureMix"/);
  assert.match(html, /data-audit-kind="ProcedureIntelligenceDoctorTime"/);
  assert.match(html, /data-audit-kind="ProcedureIntelligenceReadyWait"/);
  assert.match(html, /data-audit-kind="ProcedureIntelligenceSeatedToDoctorComplete"/);
  assert.doesNotMatch(html, /data-audit-kind="(?:DoctorTime|ReadyWait|HistoricalScheduleFit)"/);

  const detail = html.match(/<div class="procedure-intelligence-detail"[\s\S]*?<\/article>/)?.[0];
  assert.ok(detail);
  assert.match(detail, /Historical assigned allocation/);
  assert.match(detail, /Historical captured starting allocations/);
  assert.ok(html.indexOf("Dr. Otte") < html.indexOf("Dr. Historical"));
});

test("Procedure Intelligence withholds numeric range endpoints for Limited Doctor Time without calling it Unavailable", () => {
  const harness = createHarness({
    payload: {
      ...reportPayload({ otteCases: 2, pledgerCases: 2 }),
      procedureIntelligenceRows: [procedureIntelligenceRow({
        completedCount: 4,
        doctorTimeCount: 4,
        lowerSeconds: null,
        upperSeconds: null
      })]
    }
  });

  harness.reports.render();

  const html = harness.elements.get("reportProcedureIntelligence").innerHTML;
  assert.match(html, /Withheld for Limited sample/);
  assert.match(html, /Limited - N=4/);
  assert.doesNotMatch(html, /Typical Doctor Time Range[\s\S]{0,300}Unavailable/);
  assert.doesNotMatch(html, /12:00 - 18:00/);
});

test("Procedure Intelligence disclosure toggles native button state and its controlled region", () => {
  const harness = createHarness();
  const button = new FakeElement("", "button");
  const detail = new FakeElement("practice-procedure-intelligence-detail-0");
  button.dataset.procedureIntelligenceKey = "Practice|Family|EXT";
  button.setAttribute("aria-controls", detail.id);
  button.setAttribute("aria-expanded", "false");
  button.textContent = "View details";
  detail.hidden = true;
  harness.elements.set(detail.id, detail);
  harness.reports.wire();

  const clickHandler = harness.document.listeners.get("click").at(-1);
  clickHandler({
    target: targetFor(new Map([[".procedure-intelligence-toggle", button]]))
  });

  assert.equal(button.getAttribute("aria-expanded"), "true");
  assert.equal(button.textContent, "Hide details");
  assert.equal(detail.hidden, false);

  clickHandler({
    target: targetFor(new Map([[".procedure-intelligence-toggle", button]]))
  });

  assert.equal(button.getAttribute("aria-expanded"), "false");
  assert.equal(button.textContent, "View details");
  assert.equal(detail.hidden, true);
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

test("Practice Empty Schedule Fit shows No observation without measured zero aggregates", () => {
  const harness = createHarness({ payload: reportPayload({ otteCases: 0, pledgerCases: 0 }) });

  harness.reports.render();

  const html = harness.elements.get("allocationBalanceCard").innerHTML;
  assert.match(html, /No observation/);
  assert.match(html, /Empty - N=0/);
  assert.doesNotMatch(html, /schedule-fit-kpis|Expected scheduling allocation|Observed case flow|Signed net difference|00:00/);
});

test("Practice Unavailable Schedule Fit keeps coverage without measured zero aggregates", () => {
  const harness = createHarness({ payload: allocationPayload(6, 0) });

  harness.reports.render();
  const html = harness.elements.get("allocationBalanceCard").innerHTML;

  assert.match(html, /Unavailable/);
  assert.match(html, /0 of 6 included completed cases \(0% coverage\)/);
  assert.match(html, /0 of 6 contributors/);
  assert.doesNotMatch(html, /schedule-fit-kpis|Expected scheduling allocation|Observed case flow|Signed net difference|00:00|0 blocks/);
  assert.doesNotMatch(html, /No observation/);
});

test("Doctor Empty Schedule Fit row and selected tab never show measured zero", async () => {
  const harness = createHarness({ payload: reportPayload({ otteCases: 0, pledgerCases: 0 }) });
  harness.reports.wire();
  harness.reports.render();

  const rows = harness.elements.get("doctorAllocationList").innerHTML;
  assert.match(rows, /Dr\. Otte[\s\S]*?No observation/);
  assert.doesNotMatch(rows, /Historical assigned net|00:00/);

  await openDoctorScheduleFitTab(harness);
  const panel = harness.elements.get("selectedDoctorPanel").innerHTML;
  assert.match(panel, /Schedule Fit/);
  assert.match(panel, /No observation/);
  assert.doesNotMatch(panel, /Expected scheduling allocation|Signed net difference|00:00/);
});

test("Doctor Unavailable Schedule Fit row and selected tab preserve coverage without measured zero", async () => {
  const harness = createHarness({ payload: allocationPayload(6, 0) });
  harness.reports.wire();
  harness.reports.render();

  const rows = harness.elements.get("doctorAllocationList").innerHTML;
  assert.match(rows, /Dr\. Otte[\s\S]*?0 of 6 included completed cases \(0% coverage\)/);
  assert.match(rows, /Historical assigned measurements: Unavailable/);
  assert.doesNotMatch(rows, /Historical assigned net|00:00/);

  await openDoctorScheduleFitTab(harness);
  const panel = harness.elements.get("selectedDoctorPanel").innerHTML;
  assert.match(panel, /0 of 6 included completed cases \(0% coverage\)/);
  assert.match(panel, /Historical assigned Schedule Fit measurements: Unavailable/);
  assert.doesNotMatch(panel, /Expected scheduling allocation|Signed net difference|00:00|0 blocks/);
});

test("Procedure and doctor x procedure Unavailable fit preserve coverage without measured zero", () => {
  const payload = allocationPayload(6, 0);
  const unavailable = scheduleFitSummary(6, 0);
  payload.scheduleFit.procedureSegments = [{
    procedureCode: "EXT",
    procedureLabel: "Extraction",
    baseProcedureCode: "EXT",
    procedureGrouping: "Family",
    isSedationCase: null,
    currentDefaultAllocationMinutes: 30,
    historicalAssignedFit: unavailable,
    currentDefaultCalibration: calibrationEvaluation({
      decision: "BelowMinimumSample",
      totalPairedCaseCount: 6
    }),
    doctorBreakdown: [{
      doctorId: "otte",
      doctorName: "Dr. Otte",
      historicalAssignedFit: unavailable,
      currentDefaultCalibration: calibrationEvaluation({
        decision: "BelowMinimumSample",
        totalPairedCaseCount: 6
      })
    }]
  }];
  const harness = createHarness({ payload });

  harness.reports.render();

  const html = harness.elements.get("procedureAllocationList").innerHTML;
  assert.match(html, /Extraction/);
  assert.match(html, /0 of 6 included completed cases \(0% coverage\)/);
  assert.match(html, /Historical assigned measurements: Unavailable/);
  assert.match(html, /Doctor × procedure detail/);
  assert.match(html, /Dr\. Otte[\s\S]*?historical assigned measurements: Unavailable/);
  assert.doesNotMatch(html, /Historical assigned: expected|historical assigned net|00:00|0 blocks/);
});

test("Truthful zero Schedule Fit remains numeric when valid pairs exist", () => {
  const payload = allocationPayload(1, 1);
  payload.scheduleFit.practice = scheduleFitSummary(1, 1);
  const harness = createHarness({ payload });

  harness.reports.render();

  const html = harness.elements.get("allocationBalanceCard").innerHTML;
  assert.match(html, /1 of 1 included completed case \(100% coverage\)/);
  assert.match(html, /Expected scheduling allocation[\s\S]*?30:00/);
  assert.match(html, /Observed case flow[\s\S]*?30:00/);
  assert.match(html, /Total scheduling slack[\s\S]*?00:00/);
  assert.match(html, /Total scheduling debt[\s\S]*?00:00/);
  assert.match(html, /Signed net difference[\s\S]*?00:00/);
  assert.doesNotMatch(html, /No observation|Unavailable/);
});

test("Limited historical Schedule Fit remains descriptive and suppresses Calibration insight", () => {
  const harness = createHarness({ payload: allocationPayload(3, 3) });

  harness.reports.render();
  const html = allocationSurfaceHtml(harness);

  assert.match(html, /Limited - N=3/);
  assert.match(html, /3 of 3 included completed cases \(100% coverage\)/);
  assert.match(html, /Signed net difference/);
  assert.doesNotMatch(html, /Calibration insight|over expected|under expected/i);
});

test("Sufficient historical Schedule Fit shows server totals without provider judgment", () => {
  const harness = createHarness({ payload: allocationPayload(5, 5) });

  harness.reports.render();
  const html = allocationSurfaceHtml(harness);

  assert.match(html, /Sufficient - N=5/);
  assert.match(html, /Expected scheduling allocation/);
  assert.match(html, /Observed case flow/);
  assert.match(html, /Total scheduling debt/);
  assert.doesNotMatch(html, /efficient|inefficient|performance|grade|score|over expected/i);
});

test("Practice Schedule Fit preserves simultaneous slack and debt while reconciling signed net", () => {
  const payload = allocationPayload(2, 2);
  payload.scheduleFit.practice = scheduleFitSummary(2, 2, {
    expectedSeconds: 3600,
    observedSeconds: 3600,
    slackSeconds: 660,
    debtSeconds: 660,
    netVarianceSeconds: 0
  });
  const harness = createHarness({ payload });

  harness.reports.render();

  const html = harness.elements.get("allocationBalanceCard").innerHTML;
  assert.match(html, /Total scheduling slack/);
  assert.match(html, /Total scheduling debt/);
  assert.match(html, /11:00/);
  assert.match(html, /Signed net difference/);
  assert.match(html, />00:00</);
  assert.doesNotMatch(html, /utilization|efficiency|performance|grade|score/i);
});

test("Calibration callout and evidence render only from a server-owned Qualified insight", () => {
  const payload = reportPayload({ otteCases: 10, pledgerCases: 0 });
  const evidence = Array.from({ length: 10 }, (_, index) => ({
    completedCycleId: index + 1,
    acceptedReadyHandoffId: 100 + index,
    baselineSource: "CurrentRosterDefault",
    baselineAllocationMinutes: 30,
    observedCaseFlowSeconds: 2460,
    pairedVarianceSeconds: 660,
    rawDirection: "AboveBaseline",
    toleranceClassification: "MoreTimeThanAllocation"
  }));
  payload.scheduleFit.procedureSegments[0].currentDefaultCalibration = calibrationEvaluation({
    decision: "Qualified",
    totalPairedCaseCount: 10,
    aboveBaselineCaseCount: 8,
    equalBaselineCaseCount: 2,
    moreThanToleranceCaseCount: 8,
    atExpectedCaseCount: 2,
    directionalShare: 0.8,
    medianPairedVarianceSeconds: 660,
    candidateDirection: "MoreTimeThanCurrentDefault",
    insight: {
      direction: "MoreTimeThanCurrentDefault",
      medianDifferenceSeconds: 660,
      directionalCaseCount: 8,
      totalPairedCaseCount: 10,
      directionalShare: 0.8,
      evidence
    }
  });
  const harness = createHarness({ payload });

  harness.reports.render();

  const html = harness.elements.get("procedureAllocationList").innerHTML;
  assert.match(html, /Calibration insight/);
  assert.match(html, /11:00 above the current 30 min roster default/);
  assert.match(html, /8 of 10 cases were above/);
  assert.match(html, /10 exact paired case records/);
  assert.match(html, /Review contributing cases/);
  assert.match(html, /data-audit-kind="CalibrationEvidence"/);
  assert.match(html, /data-audit-evidence=/);
  assert.doesNotMatch(html, /Case record 1:/);
});

test("Suggestive calibration counts cannot create a client-side insight when the server omits it", () => {
  const payload = reportPayload({ otteCases: 10, pledgerCases: 0 });
  payload.scheduleFit.procedureSegments[0].currentDefaultCalibration = calibrationEvaluation({
    decision: "BelowMaterialDeviation",
    totalPairedCaseCount: 10,
    aboveBaselineCaseCount: 10,
    moreThanToleranceCaseCount: 10,
    atExpectedCaseCount: 0,
    directionalShare: 1,
    medianPairedVarianceSeconds: 3600,
    candidateDirection: "MoreTimeThanCurrentDefault",
    insight: null
  });
  const harness = createHarness({ payload });

  harness.reports.render();

  const html = harness.elements.get("procedureAllocationList").innerHTML;
  assert.match(html, /Current roster default: 30 min/);
  assert.doesNotMatch(html, /Calibration insight|Review the scheduling assumption/);
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
    doctorAllocationSamples: [{ doctorId: "otte", sample: sample(5) }],
    doctorFlowSummaries: [doctorFlowSummary("otte", "Dr. Otte", 5)],
    observedDoctorFlowDays: [observedDoctorFlowDay("otte", "Dr. Otte", "2026-07-14", 5)]
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
    doctorAllocationSamples: [{ doctorId: "pledger", sample: sample(5) }],
    doctorFlowSummaries: [doctorFlowSummary("pledger", "Dr. Pledger", 5)],
    observedDoctorFlowDays: [observedDoctorFlowDay("pledger", "Dr. Pledger", "2026-07-15", 5)]
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
  assert.equal(harness.elements.get("dataQualityStatus").textContent, "5 of 5 completed cases included.");
  assert.doesNotMatch(harness.elements.get("dataQualityStatus").textContent, /score|grade|%/i);
  const allMetrics = harness.elements.get("reportSummary").innerHTML;
  assert.doesNotMatch(allMetrics, /Exceptions Requiring Review/);
  assert.match(allMetrics, /Sedation Cases/);
  assert.match(allMetrics, /Avg In Room/);
  assert.match(harness.elements.get("reportInsightsGrid").innerHTML, /Avg Doctor Time/);
});

test("actionable data quality summarizes the canonical review population and opens it", async () => {
  const payload = {
    ...reportPayload({ otteCases: 2, pledgerCases: 3 }),
    excludedCompletedCycleCount: 1,
    dataQuality: {
      includedCount: 5,
      reportingExcludedCount: 1,
      completedCount: 6,
      needsReviewCount: 2,
      confirmedExceptionCount: 0,
      clearedAnomalyCount: 0
    },
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
  assert.match(card.innerHTML, /2 encounters Need Review/);
  assert.match(card.innerHTML, /data-action="open-anomaly-review"/);
  assert.match(harness.elements.get("dataQualityStatus").textContent, /5 of 6 completed cases included; 1 excluded from standard metrics; 2 items require review/);
  assert.doesNotMatch(card.innerHTML, /All completed records|No reporting exceptions/);

  const button = new FakeElement();
  button.actionSelector = "[data-action='open-anomaly-review']";
  button.dataset.anomalyOpenStatus = "NeedsReview";
  await dispatchAction(harness, button);

  assert.deepEqual(harness.anomalyStatuses, ["NeedsReview"]);
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

  const trendsTab = new FakeElement();
  trendsTab.dataset.reportDoctorTab = "trends";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([
      ["[data-report-doctor-tab]", trendsTab]
    ]))
  });
  harness.reports.renderDoctorCockpit(routeDoctor);
  assert.match(panel.innerHTML, /Weekly Doctor Trends for Dr\. Pledger/);
  assert.doesNotMatch(panel.innerHTML, /Weekly Doctor Trends for Dr\. Otte/);

  const auditTab = new FakeElement();
  auditTab.dataset.reportDoctorTab = "audit";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([
      ["[data-report-doctor-tab]", auditTab]
    ]))
  });
  assert.equal(harness.renderPageCount, 2);

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
    /import \{ createAnomalyReview \} from "\.\/anomaly-review\.js";/);
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
  assert.doesNotMatch(moduleSource, /aggregateAllocationByDoctor|doctorAllocationSummary/);
  assert.doesNotMatch(
    moduleSource,
    /minimumPairedCases|atExpectedToleranceSeconds|minimumDirectionalShare|materialDeviationSeconds/);
  assert.match(
    stylesSource,
    /@media \(max-width: 700px\)[\s\S]*?\.schedule-fit-kpis\s*\{[\s\S]*?grid-template-columns: 1fr;/);
});

test("shared audit builds drill-down from normalized report query without reloading Reports", async () => {
  const payload = reportPayload({ otteCases: 2, pledgerCases: 3 });
  payload.query = {
    scope: "Practice",
    doctorId: null,
    sedation: "Sedation",
    procedureGrouping: "DetailedVariant",
    rangeStartDate: "2026-07-04",
    rangeEndDate: "2026-07-29"
  };
  const evidencePage = {
    mode: "MetricEvidence",
    rows: [{
      completedCycleId: 88,
      acceptedReadyHandoffId: "handoff-88",
      analyticalStanding: "Included",
      roomId: 4,
      doctorId: "pledger",
      doctorName: "Dr. Pledger",
      procedureCode: "EXT+SED",
      procedureLabel: "Extraction + Sedation",
      seatedAt: "2026-07-20T08:00:00Z",
      readyForDoctorAt: "2026-07-20T08:05:00Z",
      doctorArrivedAt: "2026-07-20T08:05:00Z",
      doctorCompleteAt: "2026-07-20T08:25:00Z",
      readyWaitSeconds: 0,
      doctorTimeSeconds: 1200,
      seatedToDoctorCompleteSeconds: 1500,
      expectedAllocationMinutes: 30,
      exactScheduleFitVarianceSeconds: -300,
      reportingExclusionReasons: [],
      canMarkException: true
    }],
    reviewRows: [],
    returnedCount: 1,
    totalMatchingCount: 1,
    offset: 0,
    limit: 50,
    hasMore: false,
    activeSort: "LongestReadyWait",
    supportedSorts: ["MostRecent", "LongestReadyWait"]
  };
  const harness = createHarness({ payload, auditResponses: [undefined, evidencePage] });
  harness.reports.wire();
  harness.reports.render();
  await new Promise(resolve => setImmediate(resolve));
  const button = new FakeElement("", "button");
  button.dataset.auditKind = "ReadyWait";
  button.dataset.auditDoctor = "pledger";

  await harness.document.dispatch("click", {
    target: targetFor(new Map([["[data-audit-kind]", button]]))
  });

  const selection = harness.auditQueries.at(-1);
  assert.deepEqual(selection, {
    from: "2026-07-04",
    to: "2026-07-29",
    scope: "Practice",
    doctorId: null,
    sedation: "Sedation",
    procedureGrouping: "DetailedVariant",
    contributorKind: "ReadyWait",
    segmentDoctorId: "pledger",
    procedureCode: null,
    baseProcedureCode: null,
    analyticalStanding: "All",
    evidenceIds: [],
    sort: "MostRecent",
    offset: 0,
    limit: 50
  });
  assert.equal(harness.reloadCount, 0);
  assert.equal(harness.elements.get("reportAuditEvidence").open, true);
  assert.match(harness.elements.get("reportAuditBody").innerHTML, /report-audit-row/);
  assert.match(harness.elements.get("reportAuditBody").innerHTML, /Metric|Room 4/);
  assert.match(harness.elements.get("reportAuditBody").innerHTML, /0 sec/);
  assert.doesNotMatch(harness.elements.get("reportAuditBody").innerHTML, /recent completed/i);

  const queryCount = harness.auditQueries.length;
  harness.reports.render();
  await new Promise(resolve => setImmediate(resolve));

  assert.equal(harness.auditQueries.length, queryCount);
  assert.equal(harness.auditQueries.at(-1).contributorKind, "ReadyWait");
  assert.match(harness.elements.get("reportAuditBody").innerHTML, /Metric|Room 4/);
});

test("audit sort survives a same-version Reports render without a Most Recent reset", async () => {
  const sorted = {
    mode: "CompletedCaseAudit",
    rows: [{ completedCycleId: 7, roomId: 7, doctorName: "Dr. Otte", procedureLabel: "Extraction", analyticalStanding: "Included", reportingExclusionReasons: [] }],
    reviewRows: [], returnedCount: 1, totalMatchingCount: 1, offset: 0, limit: 50, hasMore: false,
    activeSort: "Doctor", supportedSorts: ["MostRecent", "Doctor"]
  };
  const harness = createHarness({ auditResponses: [undefined, sorted] });
  harness.reports.wire();
  harness.reports.render();
  await new Promise(resolve => setImmediate(resolve));
  const select = new FakeElement("", "select");
  select.dataset.auditSort = "";
  select.dataset.auditView = "primary";
  select.value = "Doctor";

  await harness.document.dispatch("change", {
    target: targetFor(new Map([["[data-audit-sort], [data-audit-standing-filter]", select]]))
  });

  const queryCount = harness.auditQueries.length;
  assert.equal(harness.auditQueries.at(-1).sort, "Doctor");
  harness.reports.render();
  await new Promise(resolve => setImmediate(resolve));

  assert.equal(harness.auditQueries.length, queryCount);
  assert.equal(harness.auditQueries.at(-1).sort, "Doctor");
  assert.match(harness.elements.get("reportAuditBody").innerHTML, /value="Doctor" selected/);
  assert.match(harness.elements.get("reportAuditBody").innerHTML, /Room 7/);
});

test("audit load more appends server pages without a main report reload", async () => {
  const first = {
    mode: "CompletedCaseAudit",
    rows: [{ completedCycleId: 1, roomId: 1, doctorName: "Dr. Otte", procedureLabel: "Extraction", analyticalStanding: "Included", reportingExclusionReasons: [] }],
    reviewRows: [], returnedCount: 1, totalMatchingCount: 2, offset: 0, limit: 1, hasMore: true,
    activeSort: "MostRecent", supportedSorts: ["MostRecent"]
  };
  const second = {
    ...first,
    rows: [{ completedCycleId: 2, roomId: 2, doctorName: "Dr. Pledger", procedureLabel: "Consult", analyticalStanding: "ReportingExcluded", reportingExclusionReasons: ["unmapped"] }],
    offset: 1,
    hasMore: false
  };
  const harness = createHarness({ auditResponses: [first, second] });
  harness.reports.wire();
  harness.reports.render();
  await new Promise(resolve => setImmediate(resolve));
  const button = new FakeElement("", "button");
  button.dataset.auditLoadMore = "";
  button.dataset.auditView = "primary";

  await harness.document.dispatch("click", {
    target: targetFor(new Map([["[data-audit-load-more]", button]]))
  });

  assert.equal(harness.auditQueries.at(-1).offset, 1);
  assert.equal(harness.reloadCount, 0);
  const html = harness.elements.get("reportAuditBody").innerHTML;
  assert.match(html, /Room 1/);
  assert.match(html, /Room 2/);
  assert.match(html, /unmapped/);
  assert.match(html, /2 matching records; showing 2/);
  assert.doesNotMatch(html, /showing 1/);

  const queryCount = harness.auditQueries.length;
  harness.reports.render();
  await new Promise(resolve => setImmediate(resolve));

  assert.equal(harness.auditQueries.length, queryCount);
  assert.equal(harness.auditQueries.at(-1).offset, 1);
  const repeatedHtml = harness.elements.get("reportAuditBody").innerHTML;
  assert.match(repeatedHtml, /Room 1/);
  assert.match(repeatedHtml, /Room 2/);
  assert.match(repeatedHtml, /2 matching records; showing 2/);
});

test("Doctor Case Audit sort survives a same-version Reports render", async () => {
  const doctorDefault = {
    mode: "CompletedCaseAudit",
    rows: [], reviewRows: [], returnedCount: 0, totalMatchingCount: 0, offset: 0, limit: 50, hasMore: false,
    activeSort: "MostRecent", supportedSorts: ["MostRecent", "Doctor"]
  };
  const doctorSorted = {
    ...doctorDefault,
    rows: [{ completedCycleId: 21, roomId: 9, doctorName: "Dr. Otte", procedureLabel: "Consult", analyticalStanding: "Included", reportingExclusionReasons: [] }],
    returnedCount: 1,
    totalMatchingCount: 1,
    activeSort: "Doctor"
  };
  const harness = createHarness({ auditResponses: [undefined, doctorDefault, doctorSorted] });
  harness.elements.set("doctorAuditBody", new FakeElement("doctorAuditBody"));
  harness.reports.wire();
  harness.reports.render();
  await new Promise(resolve => setImmediate(resolve));
  const auditTab = new FakeElement();
  auditTab.dataset.reportDoctorTab = "audit";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([["[data-report-doctor-tab]", auditTab]]))
  });
  harness.reports.render();
  await new Promise(resolve => setImmediate(resolve));
  const select = new FakeElement("", "select");
  select.dataset.auditSort = "";
  select.dataset.auditView = "doctor";
  select.value = "Doctor";

  await harness.document.dispatch("change", {
    target: targetFor(new Map([["[data-audit-sort], [data-audit-standing-filter]", select]]))
  });

  const queryCount = harness.auditQueries.length;
  harness.reports.render();
  await new Promise(resolve => setImmediate(resolve));

  assert.equal(harness.auditQueries.length, queryCount);
  assert.equal(harness.auditQueries.at(-1).contributorKind, "IncludedCompletedCases");
  assert.equal(harness.auditQueries.at(-1).sort, "Doctor");
  assert.match(harness.elements.get("doctorAuditBody").innerHTML, /Room 9/);
});

test("new reportData version invalidates custom audit state and initializes new parent defaults", async () => {
  const metricPage = {
    mode: "MetricEvidence",
    rows: [{ completedCycleId: 31, roomId: 4, doctorName: "Dr. Pledger", procedureLabel: "Extraction", analyticalStanding: "Included", reportingExclusionReasons: [] }],
    reviewRows: [], returnedCount: 1, totalMatchingCount: 1, offset: 0, limit: 50, hasMore: false,
    activeSort: "MostRecent", supportedSorts: ["MostRecent"]
  };
  const newDefaultPage = {
    mode: "CompletedCaseAudit",
    rows: [{ completedCycleId: 32, roomId: 8, doctorName: "Dr. Otte", procedureLabel: "Extraction + Sedation", analyticalStanding: "Included", reportingExclusionReasons: [] }],
    reviewRows: [], returnedCount: 1, totalMatchingCount: 1, offset: 0, limit: 50, hasMore: false,
    activeSort: "MostRecent", supportedSorts: ["MostRecent"]
  };
  const harness = createHarness({
    auditResponses: [undefined, metricPage, newDefaultPage]
  });
  harness.reports.wire();
  harness.reports.render();
  await new Promise(resolve => setImmediate(resolve));
  const button = new FakeElement("", "button");
  button.dataset.auditKind = "ProcedureIntelligenceReadyWait";
  button.dataset.auditBaseProcedure = "EXT";
  await harness.document.dispatch("click", {
    target: targetFor(new Map([["[data-audit-kind]", button]]))
  });
  assert.match(harness.elements.get("reportAuditBody").innerHTML, /Metric evidence|Room 4/);

  const nextPayload = reportPayload({ otteCases: 1, pledgerCases: 0 });
  nextPayload.query = {
    scope: "Doctor",
    doctorId: "otte",
    sedation: "Sedation",
    procedureGrouping: "DetailedVariant",
    rangeStartDate: "2026-08-01",
    rangeEndDate: "2026-08-07"
  };
  harness.setPayload(nextPayload);
  harness.reports.render();
  await new Promise(resolve => setImmediate(resolve));

  assert.equal(harness.auditQueries.length, 3);
  assert.deepEqual(harness.auditQueries[2], {
    from: "2026-08-01",
    to: "2026-08-07",
    scope: "Doctor",
    doctorId: "otte",
    sedation: "Sedation",
    procedureGrouping: "DetailedVariant",
    contributorKind: "IncludedCompletedCases",
    segmentDoctorId: null,
    procedureCode: null,
    baseProcedureCode: null,
    analyticalStanding: "All",
    evidenceIds: [],
    sort: "MostRecent",
    offset: 0,
    limit: 50
  });
  assert.match(harness.elements.get("reportAuditBody").innerHTML, /Completed-case audit|Room 8/);
  assert.doesNotMatch(harness.elements.get("reportAuditBody").innerHTML, /Room 4/);
});

test("Calibration audit passes exact qualified evidence identities without browser qualification", async () => {
  const payload = reportPayload({ otteCases: 10, pledgerCases: 0 });
  const harness = createHarness({ payload });
  harness.reports.wire();
  harness.reports.render();
  await new Promise(resolve => setImmediate(resolve));
  const evidenceIds = [
    { completedCycleId: 41, acceptedReadyHandoffId: "handoff-41" },
    { completedCycleId: 42, acceptedReadyHandoffId: "handoff-42" }
  ];
  const button = new FakeElement("", "button");
  button.dataset.auditKind = "CalibrationEvidence";
  button.dataset.auditBaseProcedure = "EXT";
  button.dataset.auditEvidence = JSON.stringify(evidenceIds);

  await harness.document.dispatch("click", {
    target: targetFor(new Map([["[data-audit-kind]", button]]))
  });

  const selection = harness.auditQueries.at(-1);
  assert.deepEqual(selection.evidenceIds, evidenceIds);
  assert.equal(selection.baseProcedureCode, "EXT");
  assert.equal(harness.reloadCount, 0);
  assert.doesNotMatch(moduleSource, /minimumPairedCases|minimumDirectionalShare|materialDeviationSeconds/);
});

test("Doctor headline uses included completed count while Practice keeps broad completed count", () => {
  const doctorPayload = reportPayload({ otteCases: 5, pledgerCases: 0 });
  doctorPayload.query = { scope: "Doctor", doctorId: "otte", sedation: "All", procedureGrouping: "Family" };
  doctorPayload.completedRoomCyclesCount = 7;
  doctorPayload.includedCompletedCycleCount = 5;
  doctorPayload.samples.completedCases = sample(7);
  doctorPayload.samples.includedCompletedCases = sample(5);
  const doctor = createHarness({ payload: doctorPayload });
  doctor.reports.render();
  assert.match(doctor.elements.get("reportHeadline").innerHTML, />5</);
  assert.doesNotMatch(doctor.elements.get("reportHeadline").innerHTML, />7</);

  const practicePayload = { ...doctorPayload, query: { ...doctorPayload.query, scope: "Practice", doctorId: null } };
  const practice = createHarness({ payload: practicePayload });
  practice.reports.render();
  assert.match(practice.elements.get("reportHeadline").innerHTML, />7</);
});
