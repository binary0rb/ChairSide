import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const moduleUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/connection-status.js",
  import.meta.url);
const boardUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/board.js",
  import.meta.url);
const realtimeUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/realtime-polling.js",
  import.meta.url);
const moduleSource = await readFile(moduleUrl, "utf8");
const boardSource = await readFile(boardUrl, "utf8");
const realtimeSource = await readFile(realtimeUrl, "utf8");
let moduleSequence = 0;

function initialApp(overrides = {}) {
  return {
    connectionStatus: "stale",
    lastSnapshotAt: 0,
    lastPollAt: 0,
    hubReady: false,
    realtimeDegraded: false,
    realtimeLostAt: 0,
    statusHandle: null,
    ...overrides
  };
}

async function importModule(app) {
  globalThis.__chairsideConnectionStatusTestApp = app;
  const source = moduleSource
    .replace(
      'import { app } from "./application-state.js";',
      "const app = globalThis.__chairsideConnectionStatusTestApp;")
    .concat(`\n// test-module-${moduleSequence += 1}`);
  const dataUrl = `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`;
  return import(dataUrl);
}

function createFakeDom({ includeHeader = true, existingIndicator = null, onCreate = null } = {}) {
  const elementsById = new Map();
  let createCount = 0;

  class FakeElement {
    constructor(tagName) {
      this.tagName = tagName.toUpperCase();
      this.id = "";
      this.className = "";
      this.title = "";
      this.textContent = "";
      this.attributes = new Map();
      this.children = [];
      this.span = null;
      this.icon = null;
      this.initialMarkup = "";
    }

    setAttribute(name, value) {
      this.attributes.set(name, String(value));
    }

    getAttribute(name) {
      return this.attributes.get(name) ?? null;
    }

    appendChild(child) {
      this.children.push(child);
      if (child.id) {
        elementsById.set(child.id, child);
      }
      return child;
    }

    querySelector(selector) {
      if (selector === "span") {
        return this.span;
      }
      if (selector === "i") {
        return this.icon;
      }
      return null;
    }

    set innerHTML(markup) {
      this.initialMarkup = markup;
      this.icon = new FakeElement("i");
      this.icon.setAttribute("aria-hidden", "true");
      this.span = new FakeElement("span");
      this.span.textContent = "Stale";
      this.children = [this.icon, this.span];
    }

    get innerHTML() {
      return this.initialMarkup;
    }
  }

  const body = new FakeElement("body");
  const header = includeHeader ? new FakeElement("header") : null;
  if (existingIndicator) {
    existingIndicator.id = "connectionStatus";
    elementsById.set(existingIndicator.id, existingIndicator);
    (header || body).appendChild(existingIndicator);
  }

  const document = {
    body,
    createElement(tagName) {
      createCount++;
      onCreate?.(tagName);
      return new FakeElement(tagName);
    },
    getElementById(id) {
      return elementsById.get(id) || null;
    },
    querySelector(selector) {
      return selector === ".app-header" ? header : null;
    }
  };

  return {
    document,
    body,
    header,
    FakeElement,
    get createCount() {
      return createCount;
    }
  };
}

function expectedDetails(status, lastSnapshotAt, now) {
  const descriptions = {
    live: "Board is current. Updates are being received through realtime connection or fresh polling fallback.",
    reconnecting: "Realtime connection is degraded. ChairSide is trying to reconnect.",
    stale: "No fresh board update in over 15 seconds. Refresh or check the network/server."
  };
  const description = descriptions[status];
  if (!lastSnapshotAt) {
    return `${description}\n\nLast updated: never\nAge: unavailable`;
  }

  const lastUpdated = new Date(lastSnapshotAt).toLocaleTimeString([], {
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit"
  });
  const ageMs = Math.max(0, now - lastSnapshotAt);
  const age = ageMs < 1000
    ? `${Math.round(ageMs)} ms ago`
    : `${(ageMs / 1000).toFixed(1)} seconds ago`;
  return `${description}\n\nLast updated: ${lastUpdated}\nAge: ${age}`;
}

test("status derivation preserves thresholds, precedence, and polling fallback boundaries", async t => {
  const originalDocument = globalThis.document;
  const originalWindow = globalThis.window;
  const originalNow = Date.now;
  t.after(() => {
    globalThis.document = originalDocument;
    globalThis.window = originalWindow;
    Date.now = originalNow;
  });

  const now = 100000;
  Date.now = () => now;
  globalThis.window = {};
  const cases = [
    {
      name: "never updated is stale",
      state: {},
      expected: "stale"
    },
    {
      name: "exact stale threshold is live",
      state: { lastSnapshotAt: now - 15000 },
      expected: "live"
    },
    {
      name: "over stale threshold wins over healthy realtime and polling",
      state: {
        lastSnapshotAt: now - 15001,
        hubReady: true,
        realtimeDegraded: true,
        realtimeLostAt: now - 8000,
        lastPollAt: now - 1000
      },
      expected: "stale"
    },
    {
      name: "non-degraded realtime state is live without hub readiness",
      state: { lastSnapshotAt: now - 10000 },
      expected: "live"
    },
    {
      name: "hub readiness is live while degraded",
      state: {
        lastSnapshotAt: now - 10000,
        hubReady: true,
        realtimeDegraded: true
      },
      expected: "live"
    },
    {
      name: "poll exactly seven seconds old after loss is live",
      state: {
        lastSnapshotAt: now - 10000,
        realtimeDegraded: true,
        realtimeLostAt: now - 8000,
        lastPollAt: now - 7000
      },
      expected: "live"
    },
    {
      name: "poll over seven seconds old reconnects",
      state: {
        lastSnapshotAt: now - 10000,
        realtimeDegraded: true,
        realtimeLostAt: now - 8000,
        lastPollAt: now - 7001
      },
      expected: "reconnecting"
    },
    {
      name: "poll equal to realtime loss does not qualify",
      state: {
        lastSnapshotAt: now - 10000,
        realtimeDegraded: true,
        realtimeLostAt: now - 1000,
        lastPollAt: now - 1000
      },
      expected: "reconnecting"
    },
    {
      name: "poll older than realtime loss does not qualify",
      state: {
        lastSnapshotAt: now - 10000,
        realtimeDegraded: true,
        realtimeLostAt: now - 1000,
        lastPollAt: now - 2000
      },
      expected: "reconnecting"
    }
  ];

  for (const scenario of cases) {
    const app = initialApp(scenario.state);
    const dom = createFakeDom();
    globalThis.document = dom.document;
    const { updateConnectionStatus } = await importModule(app);

    updateConnectionStatus();
    assert.equal(app.connectionStatus, scenario.expected, scenario.name);
    assert.equal(
      dom.document.getElementById("connectionStatus").className,
      `connection-status ${scenario.expected}`,
      scenario.name);
  }
});

test("indicator creation, placement, initial attributes, and reuse remain exact", async t => {
  const originalDocument = globalThis.document;
  const originalWindow = globalThis.window;
  t.after(() => {
    globalThis.document = originalDocument;
    globalThis.window = originalWindow;
  });

  globalThis.window = {};
  const app = initialApp();
  let statusAtCreate = null;
  const dom = createFakeDom({
    onCreate() {
      statusAtCreate = app.connectionStatus;
    }
  });
  globalThis.document = dom.document;
  const { setConnectionStatus } = await importModule(app);

  setConnectionStatus("stale");
  const indicator = dom.document.getElementById("connectionStatus");
  assert.equal(statusAtCreate, "stale");
  assert.strictEqual(dom.header.children[0], indicator);
  assert.equal(dom.createCount, 1);
  assert.equal(indicator.id, "connectionStatus");
  assert.equal(indicator.className, "connection-status stale");
  assert.equal(indicator.getAttribute("role"), "status");
  assert.equal(indicator.getAttribute("aria-live"), "polite");
  assert.equal(indicator.querySelector("i").getAttribute("aria-hidden"), "true");
  assert.equal(indicator.querySelector("span").textContent, "Stale");
  assert.equal(
    indicator.initialMarkup,
    `<i aria-hidden="true"></i><span>Stale</span>`);

  setConnectionStatus("reconnecting");
  assert.strictEqual(dom.document.getElementById("connectionStatus"), indicator);
  assert.equal(dom.createCount, 1);
  assert.equal(dom.header.children.length, 1);

  const bodyFallbackApp = initialApp();
  const bodyFallbackDom = createFakeDom({ includeHeader: false });
  globalThis.document = bodyFallbackDom.document;
  const bodyFallbackModule = await importModule(bodyFallbackApp);
  bodyFallbackModule.setConnectionStatus("stale");
  assert.strictEqual(
    bodyFallbackDom.body.children[0],
    bodyFallbackDom.document.getElementById("connectionStatus"));
});

test("classes, visible labels, titles, descriptions, and accessibility labels remain exact", async t => {
  const originalDocument = globalThis.document;
  const originalWindow = globalThis.window;
  const originalNow = Date.now;
  t.after(() => {
    globalThis.document = originalDocument;
    globalThis.window = originalWindow;
    Date.now = originalNow;
  });

  const now = Date.UTC(2026, 6, 29, 16, 30, 15, 250);
  const lastSnapshotAt = now - 1250;
  Date.now = () => now;
  globalThis.window = {};
  const app = initialApp({ lastSnapshotAt });
  const dom = createFakeDom();
  globalThis.document = dom.document;
  const { setConnectionStatus } = await importModule(app);
  const expectedLiveLabel = new Intl.DateTimeFormat(undefined, {
    hour: "numeric",
    minute: "2-digit"
  }).format(new Date(lastSnapshotAt));

  for (const status of ["live", "reconnecting", "stale"]) {
    setConnectionStatus(status);
    const indicator = dom.document.getElementById("connectionStatus");
    const details = expectedDetails(status, lastSnapshotAt, now);
    const label = status === "live"
      ? expectedLiveLabel
      : status === "reconnecting" ? "Reconnecting" : "Stale";
    const ariaLabel = status === "live"
      ? `Live, last updated at ${label}`
      : `${label}: ${details}`;

    assert.equal(app.connectionStatus, status);
    assert.equal(indicator.className, `connection-status ${status}`);
    assert.equal(indicator.querySelector("span").textContent, label);
    assert.equal(indicator.title, details);
    assert.equal(indicator.getAttribute("aria-label"), ariaLabel);
  }
});

test("never-updated presentation preserves em dash, details, and line breaks", async t => {
  const originalDocument = globalThis.document;
  const originalWindow = globalThis.window;
  t.after(() => {
    globalThis.document = originalDocument;
    globalThis.window = originalWindow;
  });

  globalThis.window = {};
  const app = initialApp();
  const dom = createFakeDom();
  globalThis.document = dom.document;
  const { setConnectionStatus } = await importModule(app);

  setConnectionStatus("live");
  const indicator = dom.document.getElementById("connectionStatus");
  assert.equal(indicator.querySelector("span").textContent, "—");
  assert.equal(indicator.getAttribute("aria-label"), "Live, last updated at —");
  assert.equal(
    indicator.title,
    "Board is current. Updates are being received through realtime connection or fresh polling fallback.\n\nLast updated: never\nAge: unavailable");
});

test("detail formatting preserves millisecond, second, rounding, and future-age behavior", async t => {
  const originalDocument = globalThis.document;
  const originalWindow = globalThis.window;
  const originalNow = Date.now;
  t.after(() => {
    globalThis.document = originalDocument;
    globalThis.window = originalWindow;
    Date.now = originalNow;
  });

  const now = Date.UTC(2026, 6, 29, 16, 30, 15, 250);
  Date.now = () => now;
  globalThis.window = {};
  for (const expected of [
    { lastSnapshotAt: now - 999.4, age: "999 ms ago" },
    { lastSnapshotAt: now - 1000, age: "1.0 seconds ago" },
    { lastSnapshotAt: now - 1250, age: "1.3 seconds ago" },
    { lastSnapshotAt: now + 1000, age: "0 ms ago" }
  ]) {
    const app = initialApp({ lastSnapshotAt: expected.lastSnapshotAt });
    const dom = createFakeDom();
    globalThis.document = dom.document;
    const { setConnectionStatus } = await importModule(app);
    setConnectionStatus("stale");
    assert.match(
      dom.document.getElementById("connectionStatus").title,
      new RegExp(`Age: ${expected.age.replace(".", "\\.")}$`));
  }
});

test("one-second refresh registration stores the returned handle and exact callback", async t => {
  const originalDocument = globalThis.document;
  const originalWindow = globalThis.window;
  t.after(() => {
    globalThis.document = originalDocument;
    globalThis.window = originalWindow;
  });

  const intervals = [];
  const handle = { name: "status-handle" };
  globalThis.window = {
    setInterval(callback, cadence) {
      intervals.push({ callback, cadence });
      return handle;
    }
  };
  globalThis.document = createFakeDom().document;
  const app = initialApp();
  const connectionStatus = await importModule(app);

  connectionStatus.registerConnectionStatusRefresh();
  assert.equal(intervals.length, 1);
  assert.strictEqual(intervals[0].callback, connectionStatus.updateConnectionStatus);
  assert.equal(intervals[0].cadence, 1000);
  assert.strictEqual(app.statusHandle, handle);
});

test("module ownership, call sites, and dependency direction remain narrow and one-way", () => {
  assert.match(
    moduleSource,
    /^import \{ app \} from "\.\/application-state\.js";/);
  assert.doesNotMatch(moduleSource, /from "\.\/(board|realtime-polling)\.js"/);
  assert.doesNotMatch(moduleSource, /\b(pageContext|loadBoard|loadReports|render)\b/);
  assert.match(
    boardSource,
    /import \{\s*registerConnectionStatusRefresh,\s*setConnectionStatus,\s*updateConnectionStatus\s*\} from "\.\/connection-status\.js";/);
  assert.doesNotMatch(
    boardSource,
    /function (ensureConnectionStatusIndicator|formatSnapshotAge|getConnectionStatusDetails|setConnectionStatus|updateConnectionStatus)\b/);
  assert.doesNotMatch(boardSource, /\bconnectionStatusDescriptions\b/);
  assert.doesNotMatch(realtimeSource, /connection-status\.js/);
  assert.match(
    boardSource,
    /connectRealtime\(\{\s*applySnapshot,\s*refreshReportsAfterBoardUpdate: pageContext\.isReports \? loadReports : null,\s*render,\s*refreshConnectionStatus: updateConnectionStatus,\s*setConnectionStatus,\s*loadBoard\s*\}\);/);
  assert.match(
    boardSource,
    /registerBoardPolling\(loadBoard\);\s*registerGeneralRender\(render\);\s*registerConnectionStatusRefresh\(\);\s*updateConnectionStatus\(\);/);
  assert.match(
    boardSource,
    /applySnapshot\(await response\.json\(\)\);\s*app\.lastPollAt = Date\.now\(\);\s*render\(\);\s*updateConnectionStatus\(\);/);
  assert.match(
    boardSource,
    /console\.warn\("\[ChairSide\] Board polling failed\.", error\);\s*updateConnectionStatus\(\);/);
  assert.match(
    boardSource,
    /function render\(\) \{[\s\S]*?updateConnectionStatus\(\);/);
  assert.match(
    boardSource,
    /if \(app\.pollHandle \|\| app\.tickHandle \|\| app\.statusHandle\) \{[\s\S]*?return;\s*\}/);
});
