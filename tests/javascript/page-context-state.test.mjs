import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const pageContextUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/page-context.js",
  import.meta.url);
const applicationStateUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/application-state.js",
  import.meta.url);
const boardUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/board.js",
  import.meta.url);
const pageContextSource = await readFile(pageContextUrl, "utf8");
const applicationStateSource = await readFile(applicationStateUrl, "utf8");
const boardSource = await readFile(boardUrl, "utf8");

globalThis.document = {
  body: {
    dataset: {
      view: "room",
      roomNumber: "7"
    }
  },
  querySelector: selector => selector === "meta[name='chairside-room-token']"
    ? { content: "meta-room-token" }
    : null
};
globalThis.location = {
  search: "?roomId=9&doctor=otte"
};
globalThis.sessionStorage = {
  getItem: () => "stored-room-token"
};

const pageContextDataUrl = `data:text/javascript;base64,${Buffer.from(pageContextSource).toString("base64")}`;
const pageContextModule = await import(pageContextDataUrl);
const applicationStateWithDataImport = applicationStateSource.replace(
  "\"./page-context.js\"",
  JSON.stringify(pageContextDataUrl));
const applicationStateDataUrl = `data:text/javascript;base64,${Buffer.from(applicationStateWithDataImport).toString("base64")}`;
const firstApplicationStateModule = await import(applicationStateDataUrl);
const secondApplicationStateModule = await import(applicationStateDataUrl);

test("page context derives every supported view without changing shared identity inputs", () => {
  const supportedViews = ["master", "doctor", "room", "reports", "workshop"];

  for (const view of supportedViews) {
    const context = pageContextModule.derivePageContext({
      view,
      bodyRoomNumber: "4",
      search: "?roomId=8&doctorId=gibson",
      roomToken: "room-token"
    });

    assert.deepEqual(
      {
        isMaster: context.isMaster,
        isDoctor: context.isDoctor,
        isRoom: context.isRoom,
        isReports: context.isReports,
        isWorkshop: context.isWorkshop
      },
      {
        isMaster: view === "master",
        isDoctor: view === "doctor",
        isRoom: view === "room",
        isReports: view === "reports",
        isWorkshop: view === "workshop"
      });
    assert.equal(context.view, view);
    assert.equal(context.roomNumber, 4);
    assert.equal(context.roomToken, view === "room" ? "room-token" : "");
    assert.equal(context.doctorId, "gibson");
  }
});

test("page context preserves room and doctor fallback semantics", () => {
  assert.equal(
    pageContextModule.derivePageContext({
      view: "room",
      bodyRoomNumber: "",
      search: "?room=12&doctor=pledger",
      roomToken: ""
    }).roomNumber,
    12);
  assert.equal(
    pageContextModule.derivePageContext({
      view: "master",
      bodyRoomNumber: "",
      search: "",
      roomToken: "ignored"
    }).roomNumber,
    1);
  assert.equal(
    pageContextModule.derivePageContext({
      view: "room",
      bodyRoomNumber: "not-a-room",
      search: "",
      roomToken: ""
    }).roomNumber,
    0);
});

test("application state preserves the exact initial shape and values", () => {
  assert.deepEqual(firstApplicationStateModule.app, {
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
    reportFilters: { sedation: "all", grouping: "base" },
    reportDoctorId: null,
    reportDoctorTab: "overview",
    dateRange: { preset: "last7", start: null, end: null },
    roomNumber: 7,
    roomToken: "meta-room-token",
    roomTokenPromptVisible: false,
    doctorId: "otte",
    tilePressActive: false,
    reportPressActive: false,
    reportsVersion: 0
  });
});

test("application state is one shared mutable singleton", () => {
  assert.strictEqual(
    firstApplicationStateModule.app,
    secondApplicationStateModule.app);

  firstApplicationStateModule.app.snapshot = { marker: "shared" };
  assert.strictEqual(
    secondApplicationStateModule.app.snapshot,
    firstApplicationStateModule.app.snapshot);
  firstApplicationStateModule.app.snapshot = null;
});

test("board owns behavior while context and state remain one-way modules", () => {
  assert.match(
    boardSource,
    /import \{ app \} from "\.\/application-state\.js";/);
  assert.match(
    boardSource,
    /import \{ pageContext \} from "\.\/page-context\.js";/);
  assert.match(
    boardSource,
    /import \{ connectRealtime, registerBoardPolling \} from "\.\/realtime-polling\.js";/);
  assert.match(
    boardSource,
    /import \{\s*registerConnectionStatusRefresh,\s*setConnectionStatus,\s*updateConnectionStatus\s*\} from "\.\/connection-status\.js";/);
  assert.doesNotMatch(boardSource, /\bconst app = \{/);
  assert.doesNotMatch(boardSource, /document\.body\.dataset\.view/);
  assert.doesNotMatch(pageContextSource, /\bimport\b/);
  assert.doesNotMatch(pageContextSource, /board\.js/);
  assert.match(
    applicationStateSource,
    /import \{ pageContext \} from "\.\/page-context\.js";/);
  assert.doesNotMatch(applicationStateSource, /board\.js/);
});

test("startup routing and awaited differences remain in board", () => {
  assert.match(
    boardSource,
    /if \(app\.pollHandle \|\| app\.tickHandle \|\| app\.statusHandle\) \{[\s\S]*?return;\s*\}/);
  assert.match(boardSource, /await loadBoard\(\);/);
  assert.match(boardSource, /if \(pageContext\.isReports\) \{[\s\S]*?await loadReports\(\);/);
  assert.match(boardSource, /if \(pageContext\.isDoctor\) \{[\s\S]*?loadReports\(\);[\s\S]*?registerReportRefresh\(loadReports\);/);
  assert.match(boardSource, /if \(pageContext\.isWorkshop\) \{[\s\S]*?loadReports\(\);[\s\S]*?registerReportRefresh\(loadReports\);/);
  assert.match(boardSource, /registerBoardPolling\(loadBoard\);/);
  assert.match(boardSource, /registerGeneralRender\(render\);/);
  assert.match(boardSource, /registerConnectionStatusRefresh\(\);/);
  assert.match(boardSource, /\nboot\(\);\s*$/);
});
