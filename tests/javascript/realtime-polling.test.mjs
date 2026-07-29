import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const moduleUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/realtime-polling.js",
  import.meta.url);
const boardUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/board.js",
  import.meta.url);
const moduleSource = await readFile(moduleUrl, "utf8");
const boardSource = await readFile(boardUrl, "utf8");
let moduleSequence = 0;

function initialApp(overrides = {}) {
  return {
    connection: null,
    hubReady: false,
    pollHandle: null,
    realtimeRetryHandle: null,
    realtimeDegraded: false,
    realtimeLostAt: 0,
    ...overrides
  };
}

async function importModule(app) {
  globalThis.__chairsideRealtimeTestApp = app;
  const source = moduleSource
    .replace(
      'import { app } from "./application-state.js";',
      "const app = globalThis.__chairsideRealtimeTestApp;")
    .concat(`\n// test-module-${moduleSequence += 1}`);
  const dataUrl = `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`;
  return import(dataUrl);
}

function connectionHarness({ startResult = "pending", state = "Disconnected" } = {}) {
  const handlers = {};
  const construction = [];
  let resolveStart;
  let rejectStart;
  const startPromise = new Promise((resolve, reject) => {
    resolveStart = resolve;
    rejectStart = reject;
  });
  const connection = {
    state,
    on(name, handler) {
      handlers[name] = handler;
    },
    onreconnecting(handler) {
      handlers.reconnecting = handler;
    },
    onreconnected(handler) {
      handlers.reconnected = handler;
    },
    onclose(handler) {
      handlers.close = handler;
    },
    start() {
      if (startResult === "success") {
        return Promise.resolve();
      }
      if (startResult === "failure") {
        return Promise.reject(new Error("start failed"));
      }
      return startPromise;
    }
  };

  class HubConnectionBuilder {
    withUrl(url) {
      construction.push(["withUrl", url]);
      return this;
    }

    withAutomaticReconnect() {
      construction.push(["withAutomaticReconnect"]);
      return this;
    }

    build() {
      construction.push(["build"]);
      return connection;
    }
  }

  return {
    connection,
    construction,
    handlers,
    signalR: { HubConnectionBuilder },
    resolveStart,
    rejectStart
  };
}

function callbackHarness({ reports = true } = {}) {
  const actions = [];
  const callbacks = {
    applySnapshot(snapshot) {
      actions.push(["applySnapshot", snapshot]);
    },
    render() {
      actions.push(["render"]);
    },
    refreshConnectionStatus() {
      actions.push(["refreshConnectionStatus"]);
    },
    setConnectionStatus(status) {
      actions.push(["setConnectionStatus", status]);
    },
    loadBoard() {
      actions.push(["loadBoard"]);
    }
  };

  if (reports) {
    callbacks.refreshReportsAfterBoardUpdate = async () => {
      actions.push(["refreshReportsAfterBoardUpdate"]);
    };
  }

  return { actions, callbacks };
}

async function flushPromises() {
  await Promise.resolve();
  await Promise.resolve();
}

test("SignalR unavailable degrades once and refreshes connection presentation", async t => {
  const originalWindow = globalThis.window;
  const originalNow = Date.now;
  t.after(() => {
    globalThis.window = originalWindow;
    Date.now = originalNow;
  });

  const app = initialApp({ hubReady: true });
  const { actions, callbacks } = callbackHarness();
  globalThis.window = {};
  Date.now = () => 12345;
  const { connectRealtime } = await importModule(app);

  connectRealtime(callbacks);
  assert.deepEqual(
    {
      hubReady: app.hubReady,
      realtimeDegraded: app.realtimeDegraded,
      realtimeLostAt: app.realtimeLostAt,
      connection: app.connection
    },
    {
      hubReady: false,
      realtimeDegraded: true,
      realtimeLostAt: 12345,
      connection: null
    });
  assert.deepEqual(actions, [["refreshConnectionStatus"]]);

  Date.now = () => 99999;
  connectRealtime(callbacks);
  assert.equal(app.realtimeLostAt, 12345);
});

test("ready or active connection states prevent duplicate construction", async t => {
  const originalWindow = globalThis.window;
  t.after(() => {
    globalThis.window = originalWindow;
  });

  for (const existing of [
    { hubReady: true, state: "Disconnected" },
    { hubReady: false, state: "Connected" },
    { hubReady: false, state: "Connecting" },
    { hubReady: false, state: "Reconnecting" }
  ]) {
    const harness = connectionHarness();
    const app = initialApp({
      hubReady: existing.hubReady,
      connection: { state: existing.state }
    });
    globalThis.window = { signalR: harness.signalR };
    const { connectRealtime } = await importModule(app);

    connectRealtime(callbackHarness().callbacks);
    assert.deepEqual(harness.construction, []);
    assert.equal(app.connection.state, existing.state);
  }
});

test("connection construction owns /boardHub, automatic reconnect, and lifecycle handlers", async t => {
  const originalWindow = globalThis.window;
  t.after(() => {
    globalThis.window = originalWindow;
  });

  const harness = connectionHarness({ startResult: "success" });
  const mutations = [];
  const app = new Proxy(initialApp(), {
    set(object, property, value) {
      mutations.push([property, value]);
      object[property] = value;
      return true;
    }
  });
  const { actions, callbacks } = callbackHarness();
  globalThis.window = { signalR: harness.signalR };
  const { connectRealtime } = await importModule(app);

  connectRealtime(callbacks);
  mutations.length = 0;
  await flushPromises();

  assert.deepEqual(harness.construction, [
    ["withUrl", "/boardHub"],
    ["withAutomaticReconnect"],
    ["build"]
  ]);
  assert.strictEqual(app.connection, harness.connection);
  assert.deepEqual(
    Object.keys(harness.handlers).sort(),
    ["boardUpdated", "close", "reconnected", "reconnecting"]);
  assert.deepEqual(
    {
      hubReady: app.hubReady,
      realtimeDegraded: app.realtimeDegraded,
      realtimeLostAt: app.realtimeLostAt
    },
    {
      hubReady: true,
      realtimeDegraded: false,
      realtimeLostAt: 0
    });
  assert.deepEqual(actions, [["setConnectionStatus", "live"]]);
  assert.deepEqual(mutations, [
    ["hubReady", true],
    ["realtimeDegraded", false],
    ["realtimeLostAt", 0]
  ]);
});

test("boardUpdated preserves callback order and optional Reports-only refresh", async t => {
  const originalWindow = globalThis.window;
  t.after(() => {
    globalThis.window = originalWindow;
  });

  for (const reports of [true, false]) {
    const harness = connectionHarness();
    const app = initialApp();
    const { actions, callbacks } = callbackHarness({ reports });
    globalThis.window = { signalR: harness.signalR };
    const { connectRealtime } = await importModule(app);
    connectRealtime(callbacks);

    const snapshot = { marker: reports ? "reports" : "other" };
    await harness.handlers.boardUpdated(snapshot);
    assert.deepEqual(actions, reports
      ? [
          ["applySnapshot", snapshot],
          ["refreshReportsAfterBoardUpdate"],
          ["render"],
          ["refreshConnectionStatus"]
        ]
      : [
          ["applySnapshot", snapshot],
          ["render"],
          ["refreshConnectionStatus"]
        ]);
  }
});

test("boardUpdated preserves the Reports warning and continues rendering after refresh failure", async t => {
  const originalWindow = globalThis.window;
  const originalWarn = console.warn;
  t.after(() => {
    globalThis.window = originalWindow;
    console.warn = originalWarn;
  });

  const warnings = [];
  const harness = connectionHarness();
  const app = initialApp();
  const { actions, callbacks } = callbackHarness();
  callbacks.refreshReportsAfterBoardUpdate = async () => {
    actions.push(["refreshReportsAfterBoardUpdate"]);
    throw new Error("reports failed");
  };
  console.warn = (...args) => warnings.push(args);
  globalThis.window = { signalR: harness.signalR };
  const { connectRealtime } = await importModule(app);
  connectRealtime(callbacks);

  const snapshot = { marker: "reports-failure" };
  await harness.handlers.boardUpdated(snapshot);
  assert.equal(
    warnings[0][0],
    "[ChairSide] Reports refresh after board update failed.");
  assert.deepEqual(actions, [
    ["applySnapshot", snapshot],
    ["refreshReportsAfterBoardUpdate"],
    ["render"],
    ["refreshConnectionStatus"]
  ]);
});

test("reconnecting, reconnected, and close preserve exact mutation and callback order", async t => {
  const originalWindow = globalThis.window;
  const originalNow = Date.now;
  t.after(() => {
    globalThis.window = originalWindow;
    Date.now = originalNow;
  });

  const mutations = [];
  const target = initialApp({
    hubReady: false,
    realtimeDegraded: false,
    realtimeLostAt: 0
  });
  const app = new Proxy(target, {
    set(object, property, value) {
      mutations.push([property, value]);
      object[property] = value;
      return true;
    }
  });
  const harness = connectionHarness();
  const { actions, callbacks } = callbackHarness();
  globalThis.window = { signalR: harness.signalR };
  Date.now = () => 45678;
  const { connectRealtime } = await importModule(app);
  connectRealtime(callbacks);

  app.hubReady = true;
  mutations.length = 0;
  harness.handlers.reconnecting();
  assert.deepEqual(mutations, [
    ["hubReady", false],
    ["realtimeDegraded", true],
    ["realtimeLostAt", 45678]
  ]);
  assert.deepEqual(actions, [["refreshConnectionStatus"]]);

  mutations.length = 0;
  actions.length = 0;
  harness.handlers.reconnected();
  assert.deepEqual(mutations, [
    ["hubReady", true],
    ["realtimeDegraded", false],
    ["realtimeLostAt", 0]
  ]);
  assert.deepEqual(actions, [
    ["setConnectionStatus", "live"],
    ["loadBoard"]
  ]);

  mutations.length = 0;
  actions.length = 0;
  Date.now = () => 56789;
  harness.handlers.close();
  assert.deepEqual(mutations, [
    ["hubReady", false],
    ["realtimeDegraded", true],
    ["realtimeLostAt", 56789]
  ]);
  assert.deepEqual(actions, [["refreshConnectionStatus"]]);
});

test("start failure preserves warning, state transitions, retry delay, and retry de-duplication", async t => {
  const originalWindow = globalThis.window;
  const originalNow = Date.now;
  const originalWarn = console.warn;
  t.after(() => {
    globalThis.window = originalWindow;
    Date.now = originalNow;
    console.warn = originalWarn;
  });

  const warnings = [];
  const timeouts = [];
  const mutations = [];
  const app = new Proxy(initialApp({ hubReady: false }), {
    set(object, property, value) {
      mutations.push([property, value]);
      object[property] = value;
      return true;
    }
  });
  const { actions, callbacks } = callbackHarness();
  const harness = connectionHarness({ startResult: "failure" });
  globalThis.window = {
    signalR: harness.signalR,
    setTimeout(handler, delay) {
      const handle = { handler, delay };
      timeouts.push(handle);
      return handle;
    }
  };
  Date.now = () => 67890;
  console.warn = (...args) => warnings.push(args);
  const { connectRealtime } = await importModule(app);

  connectRealtime(callbacks);
  mutations.length = 0;
  await flushPromises();
  assert.equal(warnings.length, 1);
  assert.equal(
    warnings[0][0],
    "[ChairSide] SignalR connection failed; polling fallback remains active.");
  assert.deepEqual(
    {
      hubReady: app.hubReady,
      realtimeDegraded: app.realtimeDegraded,
      realtimeLostAt: app.realtimeLostAt
    },
    {
      hubReady: false,
      realtimeDegraded: true,
      realtimeLostAt: 67890
    });
  assert.deepEqual(actions, [["refreshConnectionStatus"]]);
  assert.equal(timeouts.length, 1);
  assert.equal(timeouts[0].delay, 5000);
  assert.strictEqual(app.realtimeRetryHandle, timeouts[0]);
  assert.deepEqual(mutations, [
    ["hubReady", false],
    ["realtimeDegraded", true],
    ["realtimeLostAt", 67890],
    ["realtimeRetryHandle", timeouts[0]]
  ]);

  mutations.length = 0;
  connectRealtime(callbacks);
  await flushPromises();
  assert.equal(timeouts.length, 1);

  timeouts[0].handler();
  assert.equal(app.realtimeRetryHandle, null);
  assert.equal(
    harness.construction.filter(entry => entry[0] === "build").length,
    3);
});

test("board polling registration retains ownership, handle, callback, and 5000 ms cadence", async t => {
  const originalWindow = globalThis.window;
  t.after(() => {
    globalThis.window = originalWindow;
  });

  const intervals = [];
  const handle = { name: "poll-handle" };
  const app = initialApp();
  globalThis.window = {
    setInterval(callback, cadence) {
      intervals.push({ callback, cadence });
      return handle;
    }
  };
  const { registerBoardPolling } = await importModule(app);
  const loadBoard = () => {};

  registerBoardPolling(loadBoard);
  assert.deepEqual(intervals, [{ callback: loadBoard, cadence: 5000 }]);
  assert.strictEqual(app.pollHandle, handle);
});

test("module ownership and dependency direction stay narrow and one-way", () => {
  assert.match(
    moduleSource,
    /^import \{ app \} from "\.\/application-state\.js";/);
  assert.doesNotMatch(
    moduleSource,
    /from "\.\/(board|page-context|signalr-lite|request-utils|dom-utils|format-utils)\.js"/);
  assert.doesNotMatch(
    moduleSource,
    /\b(document|pageContext|loadReports)\b/);
  assert.doesNotMatch(
    moduleSource,
    /function (applySnapshot|render|setConnectionStatus|loadBoard)\b/);
  assert.match(
    boardSource,
    /import \{ connectRealtime, registerBoardPolling \} from "\.\/realtime-polling\.js";/);
  assert.match(
    boardSource,
    /connectRealtime\(\{\s*applySnapshot,\s*refreshReportsAfterBoardUpdate: pageContext\.isReports \? loadReports : null,\s*render,\s*refreshConnectionStatus: updateConnectionStatus,\s*setConnectionStatus,\s*loadBoard\s*\}\);/);
  assert.match(boardSource, /registerBoardPolling\(loadBoard\);/);
  assert.doesNotMatch(boardSource, /function (connectRealtime|markRealtimeDegraded|scheduleRealtimeRetry)\b/);
});
