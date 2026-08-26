import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const sourceUrl = new URL("../../src/ChairSide.Board/wwwroot/anomaly-review.js", import.meta.url);
const domUrl = new URL("../../src/ChairSide.Board/wwwroot/dom-utils.js", import.meta.url);
const formatUrl = new URL("../../src/ChairSide.Board/wwwroot/format-utils.js", import.meta.url);
const source = await readFile(sourceUrl, "utf8");
const domSource = await readFile(domUrl, "utf8");
const formatSource = await readFile(formatUrl, "utf8");
const domDataUrl = `data:text/javascript;base64,${Buffer.from(domSource).toString("base64")}`;
const formatDataUrl = `data:text/javascript;base64,${Buffer.from(formatSource).toString("base64")}`;
const moduleDataUrl = `data:text/javascript;base64,${Buffer.from(source
  .replace('"./dom-utils.js"', JSON.stringify(domDataUrl))
  .replace('"./format-utils.js"', JSON.stringify(formatDataUrl))).toString("base64")}`;
const { createAnomalyReview } = await import(moduleDataUrl);

class FakeElement {
  constructor() {
    this.innerHTML = "";
    this.textContent = "";
    this.open = false;
  }

  focus() {}
}

function createDocument() {
  const elements = new Map([
    ["reportAnomalyReviewBody", new FakeElement()],
    ["reportAnomalyReviewCount", new FakeElement()],
    ["reportAnomalyReview", new FakeElement()],
    ["anomalyDetailHeading", new FakeElement()]
  ]);
  const listeners = new Map();
  return {
    elements,
    getElementById: id => elements.get(id) || null,
    querySelector: () => new FakeElement(),
    addEventListener(type, listener) {
      const current = listeners.get(type) || [];
      current.push(listener);
      listeners.set(type, current);
    },
    async dispatch(type, event) {
      for (const listener of listeners.get(type) || []) await listener(event);
    }
  };
}

function report() {
  return {
    query: {
      rangeStartDate: "2026-08-01",
      rangeEndDate: "2026-08-07",
      scope: "Doctor",
      doctorId: "otte",
      sedation: "Sedation",
      procedureGrouping: "DetailedVariant"
    }
  };
}

function listPage(disposition = "NeedsReview") {
  return {
    reviewRows: [{
      sourceType: "CompletedCycle",
      reviewRecordId: 42,
      disposition,
      reviewAnchor: "2026-08-05T14:00:00Z",
      roomId: 3,
      doctorName: "Dr. Otte",
      procedureLabel: "Extraction",
      reason: "IncorrectDoctor",
      hasHistoricalCorrection: false
    }],
    returnedCount: 1,
    totalMatchingCount: 1,
    offset: 0,
    limit: 50,
    hasMore: false,
    activeSort: "MostRecent",
    supportedSorts: ["MostRecent", "Doctor", "Procedure"]
  };
}

function detail(revision = 1) {
  return {
    sourceType: "CompletedCycle",
    sourceRecordId: 42,
    administrativeRevision: revision,
    disposition: "NeedsReview",
    reason: "IncorrectDoctor",
    reasonSource: "LocalAdmin",
    reportingExclusionReasons: ["administrative-review-pending"],
    originalEvidence: {
      authority: "AcceptedReadyHandoff",
      metadata: {
        doctorId: "otte",
        procedureCode: "EXT",
        sedationState: "EligibleNo",
        isAddOn: false,
        expectedAllocation: { state: "ConfirmedSuggestedValue", suggestedValue: 3, confirmedValue: 3 }
      },
      lifecycle: {
        roomId: 3,
        prestageStartedAt: "2026-08-05T13:30:00Z",
        seatedAt: "2026-08-05T13:40:00Z",
        readyForDoctorAt: "2026-08-05T13:45:00Z",
        doctorArrivedAt: "2026-08-05T14:00:00Z",
        doctorCompleteAt: null,
        roomAvailableAt: null
      }
    },
    effectiveMetadata: {
      doctorId: "pledger",
      procedureCode: "EXT",
      sedationState: "EligibleNo",
      isAddOn: false,
      expectedAllocation: { state: "ConfirmedSuggestedValue", suggestedValue: 3, confirmedValue: 3 }
    },
    correctionIndicators: { doctor: true },
    correctionSupport: { doctor: true, procedure: true, sedation: true, addOn: true, expectedAllocation: true }
  };
}

const options = {
  doctors: [
    { id: "otte", displayName: "Dr. Otte", active: true },
    { id: "pledger", displayName: "Dr. Pledger", active: false }
  ],
  procedures: [
    { code: "EXT", label: "Extraction", active: true, sedationEligible: true },
    { code: "IMP", label: "Implant", active: false, sedationEligible: true },
    { code: "CON", label: "Consult", active: true, sedationEligible: false }
  ],
  reasons: [{ token: "IncorrectDoctor", label: "Incorrect Doctor" }],
  noteMaximumLength: 500
};
const ledger = {
  rows: [{
    ledgerId: 1,
    eventType: "ManualFlag",
    occurredAt: "2026-08-05T14:05:00Z",
    actorClass: "LocalAdmin",
    structuredReason: "IncorrectDoctor",
    administrativeRevision: 1
  }],
  offset: 0,
  limit: 50,
  returnedCount: 1,
  totalMatchingCount: 1,
  hasMore: false
};

function ok(body) {
  return { ok: true, status: 200, json: async () => structuredClone(body) };
}

function harness({ request } = {}) {
  const document = createDocument();
  globalThis.document = document;
  const selections = [];
  let currentReport = report();
  let reloads = 0;
  const review = createAnomalyReview({
    reportData: {
      getReports: () => currentReport,
      queryAudit: async selection => {
        selections.push(structuredClone(selection));
        return listPage(selection.anomalyStatus);
      }
    },
    request: request || (async url => {
      if (url.endsWith("/options")) return ok(options);
      if (url.includes("/detail")) return ok(detail());
      if (url.includes("/ledger")) return ok(ledger);
      throw new Error(`Unexpected request: ${url}`);
    }),
    adminHeaders: () => ({ "X-ChairSide-Admin-Token": "admin-token" }),
    reloadReports: async () => { reloads += 1; },
    confirmAction: () => true
  });
  return { document, review, selections, get reloads() { return reloads; }, setReport: value => { currentReport = value; } };
}

test("Needs Review is the scoped default and all-history broadening is explicit", async () => {
  const h = harness();
  h.review.wire();
  h.review.onReportRendered(report(), 1);
  await new Promise(resolve => setImmediate(resolve));

  assert.deepEqual(h.selections[0], {
    from: "2026-08-01",
    to: "2026-08-07",
    scope: "Doctor",
    doctorId: "otte",
    sedation: "Sedation",
    procedureGrouping: "DetailedVariant",
    contributorKind: "AnomalyReview",
    anomalyStatus: "NeedsReview",
    sort: "MostRecent",
    offset: 0,
    limit: 50
  });

  const scopeButton = { closest: selector => selector === "[data-anomaly-scope-toggle]" ? scopeButton : null };
  await h.document.dispatch("click", { target: scopeButton });
  assert.deepEqual(h.selections.at(-1), {
    from: null,
    to: null,
    scope: "Practice",
    doctorId: null,
    sedation: "All",
    procedureGrouping: "Family",
    contributorKind: "AnomalyReview",
    anomalyStatus: "NeedsReview",
    sort: "MostRecent",
    offset: 0,
    limit: 50
  });
  assert.match(h.document.elements.get("reportAnomalyReviewBody").innerHTML, /Showing all anomaly history across all time/);

  await h.document.dispatch("click", { target: scopeButton });
  assert.equal(h.selections.at(-1).from, "2026-08-01");
  assert.equal(h.selections.at(-1).scope, "Doctor");
  assert.doesNotMatch(h.document.elements.get("reportAnomalyReviewBody").innerHTML, /Showing all anomaly history across all time/);
});

test("status navigation emits only canonical current-disposition selections", async () => {
  const h = harness();
  for (const status of ["NeedsReview", "ConfirmedException", "ClearedForReporting", "AllAnomalies"]) {
    await h.review.showStatus(status);
  }
  assert.deepEqual(h.selections.map(selection => selection.anomalyStatus), [
    "NeedsReview",
    "ConfirmedException",
    "ClearedForReporting",
    "AllAnomalies"
  ]);
  assert.ok(h.selections.every(selection => selection.scope === "Doctor"));
});

test("selected detail renders original, effective, lifecycle, inactive roster, and ledger evidence", async () => {
  const h = harness();
  await h.review.selectEncounter("CompletedCycle", 42);
  const html = h.document.elements.get("reportAnomalyReviewBody").innerHTML;

  assert.match(html, /Original evidence/);
  assert.match(html, /Current effective value/);
  assert.match(html, /Dr\. Pledger \(inactive\)/);
  assert.match(html, /Read-only lifecycle evidence/);
  assert.match(html, /Doctor Complete<\/dt><dd>Not recorded/);
  assert.match(html, /Mark for Review/);
  assert.match(html, /Incorrect Doctor/);
});

test("a stale write is never retried and refreshes authoritative state", async () => {
  let postCount = 0;
  let currentRevision = 1;
  const h = harness({
    request: async (url, init = {}) => {
      if (init.method === "POST") {
        postCount += 1;
        currentRevision = 2;
        return { ok: false, status: 409, json: async () => ({ code: "stale-write", currentRevision: 2 }) };
      }
      if (url.endsWith("/options")) return ok(options);
      if (url.includes("/detail")) return ok(detail(currentRevision));
      if (url.includes("/ledger")) return ok(ledger);
      throw new Error(`Unexpected request: ${url}`);
    }
  });
  h.review.wire();
  await h.review.selectEncounter("CompletedCycle", 42);
  globalThis.FormData = class {
    constructor(form) { this.values = form.values; }
    get(name) { return this.values[name] ?? null; }
  };
  const form = { dataset: { anomalyForm: "clear" }, values: { note: "" }, closest: () => form };
  await h.document.dispatch("submit", { target: form, preventDefault() {} });
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));

  assert.equal(postCount, 1);
  assert.equal(h.review._state.detail.administrativeRevision, 2);
  assert.equal(h.review._state.feedback.tone, "stale");
  assert.match(h.review._state.feedback.message, /changed while you were reviewing/);
});

test("procedure eligibility boundaries require one explicit paired correction", async () => {
  const posts = [];
  const h = harness({
    request: async (url, init = {}) => {
      if (init.method === "POST") {
        posts.push({ url, body: JSON.parse(init.body) });
        return ok(detail(2));
      }
      if (url.endsWith("/options")) return ok(options);
      if (url.includes("/detail")) return ok(detail(posts.length ? 2 : 1));
      if (url.includes("/ledger")) return ok(ledger);
      throw new Error(`Unexpected request: ${url}`);
    }
  });
  h.review.wire();
  await h.review.selectEncounter("CompletedCycle", 42);
  globalThis.FormData = class {
    constructor(form) { this.values = form.values; }
    get(name) { return this.values[name] ?? null; }
  };

  const missing = {
    dataset: { anomalyForm: "procedure" },
    values: { procedureCode: "CON", sedationState: "", note: "" },
    closest: () => missing
  };
  await h.document.dispatch("submit", { target: missing, preventDefault() {} });
  assert.equal(posts.length, 0);
  assert.match(h.review._state.feedback.message, /explicit final Sedation state/);

  const paired = {
    dataset: { anomalyForm: "procedure" },
    values: { procedureCode: "CON", sedationState: "UnavailableProcedureIneligible", note: "corrected classification" },
    closest: () => paired
  };
  await h.document.dispatch("submit", { target: paired, preventDefault() {} });
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));

  assert.equal(posts.length, 1);
  assert.match(posts[0].url, /correct-procedure-and-sedation$/);
  assert.deepEqual(posts[0].body, {
    expectedRevision: 1,
    procedureCode: "CON",
    sedationState: "UnavailableProcedureIneligible",
    note: "corrected classification"
  });
});

test("same-eligibility procedure changes stay single-field and inactive choices remain selectable", async () => {
  const posts = [];
  const h = harness({
    request: async (url, init = {}) => {
      if (init.method === "POST") {
        posts.push({ url, body: JSON.parse(init.body) });
        return ok(detail(2));
      }
      if (url.endsWith("/options")) return ok(options);
      if (url.includes("/detail")) return ok(detail());
      if (url.includes("/ledger")) return ok(ledger);
      throw new Error(`Unexpected request: ${url}`);
    }
  });
  h.review.wire();
  await h.review.selectEncounter("CompletedCycle", 42);
  globalThis.FormData = class {
    constructor(form) { this.values = form.values; }
    get(name) { return this.values[name] ?? null; }
  };
  const form = {
    dataset: { anomalyForm: "procedure" },
    values: { procedureCode: "IMP", sedationState: "", note: "" },
    closest: () => form
  };
  await h.document.dispatch("submit", { target: form, preventDefault() {} });
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));

  assert.equal(posts.length, 1);
  assert.match(posts[0].url, /correct-procedure$/);
  assert.deepEqual(posts[0].body, { expectedRevision: 1, procedureCode: "IMP" });
  assert.match(h.document.elements.get("reportAnomalyReviewBody").innerHTML, /Implant \(inactive\)/);
});

test("a network failure remains unknown until GET-only reconciliation completes", async () => {
  let postCount = 0;
  let getCount = 0;
  const h = harness({
    request: async (url, init = {}) => {
      if (init.method === "POST") {
        postCount += 1;
        throw new Error("connection dropped");
      }
      getCount += 1;
      if (url.endsWith("/options")) return ok(options);
      if (url.includes("/detail")) return ok(detail());
      if (url.includes("/ledger")) return ok(ledger);
      throw new Error(`Unexpected request: ${url}`);
    }
  });
  h.review.wire();
  await h.review.selectEncounter("CompletedCycle", 42);
  const readsBefore = getCount;
  globalThis.FormData = class {
    constructor(form) { this.values = form.values; }
    get(name) { return this.values[name] ?? null; }
  };
  const form = { dataset: { anomalyForm: "confirm" }, values: { note: "" }, closest: () => form };
  await h.document.dispatch("submit", { target: form, preventDefault() {} });
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));

  assert.equal(postCount, 1);
  assert.ok(getCount > readsBefore);
  assert.equal(h.reloads, 1);
  assert.equal(h.review._state.feedback.tone, "error");
  assert.match(h.review._state.feedback.message, /outcome is unknown/);
});

test("bounded non-PHI notes travel on the disposition event without a second request", async () => {
  const posts = [];
  const h = harness({
    request: async (url, init = {}) => {
      if (init.method === "POST") {
        posts.push({ url, body: JSON.parse(init.body) });
        return ok(detail(2));
      }
      if (url.endsWith("/options")) return ok(options);
      if (url.includes("/detail")) return ok(detail());
      if (url.includes("/ledger")) return ok(ledger);
      throw new Error(`Unexpected request: ${url}`);
    }
  });
  h.review.wire();
  await h.review.selectEncounter("CompletedCycle", 42);
  globalThis.FormData = class {
    constructor(form) { this.values = form.values; }
    get(name) { return this.values[name] ?? null; }
  };

  const tooLong = {
    dataset: { anomalyForm: "clear" },
    values: { note: "x".repeat(501) },
    closest: () => tooLong
  };
  await h.document.dispatch("submit", { target: tooLong, preventDefault() {} });
  assert.equal(posts.length, 0);
  assert.match(h.review._state.feedback.message, /500 characters or fewer/);

  const valid = {
    dataset: { anomalyForm: "clear" },
    values: { note: "Operational review complete" },
    closest: () => valid
  };
  await h.document.dispatch("submit", { target: valid, preventDefault() {} });
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));

  assert.equal(posts.length, 1);
  assert.match(posts[0].url, /\/clear$/);
  assert.deepEqual(posts[0].body, { expectedRevision: 1, note: "Operational review complete" });
  assert.doesNotMatch(posts[0].url, /\/note$/);
  assert.match(h.document.elements.get("reportAnomalyReviewBody").innerHTML, /Do not enter patient name, chart number, diagnosis/);
});

test("the canonical browser module never references compatibility exception routes", () => {
  assert.doesNotMatch(source, /mark-exception|confirm-exclusion|\/api\/reports\/exceptions/);
  assert.match(source, /correct-procedure-and-sedation/);
  assert.match(source, /maxlength="\$\{maximum\}"/);
});
