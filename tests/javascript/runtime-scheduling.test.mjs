import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const moduleUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/runtime-scheduling.js",
  import.meta.url);
const boardUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/board.js",
  import.meta.url);
const moduleSource = await readFile(moduleUrl, "utf8");
const boardSource = await readFile(boardUrl, "utf8");
let moduleSequence = 0;

async function importModule(app) {
  globalThis.__chairsideSchedulingTestApp = app;
  const source = moduleSource
    .replace(
      'import { app } from "./application-state.js";',
      "const app = globalThis.__chairsideSchedulingTestApp;")
    .concat(`\n// test-module-${moduleSequence += 1}`);
  const dataUrl = `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`;
  return import(dataUrl);
}

test("report and general render registrations preserve exact cadences and handle ownership", async t => {
  const originalWindow = globalThis.window;
  t.after(() => {
    globalThis.window = originalWindow;
  });

  const intervals = [];
  globalThis.window = {
    setInterval(callback, cadence) {
      const handle = { sequence: intervals.length + 1 };
      intervals.push({ callback, cadence, handle });
      return handle;
    }
  };
  const app = { tickHandle: null };
  const scheduling = await importModule(app);
  const refreshReports = () => {};
  const render = () => {};

  const reportResult = scheduling.registerReportRefresh(refreshReports);
  assert.equal(reportResult, undefined);
  assert.equal(app.tickHandle, null);
  assert.deepEqual(
    { callback: intervals[0].callback, cadence: intervals[0].cadence },
    { callback: refreshReports, cadence: 60_000 });

  scheduling.registerGeneralRender(render);
  assert.deepEqual(
    { callback: intervals[1].callback, cadence: intervals[1].cadence },
    { callback: render, cadence: 1000 });
  assert.strictEqual(app.tickHandle, intervals[1].handle);
});

test("boot preserves registration placement, ordering, page routing, and double-boot guard", () => {
  assert.match(
    boardSource,
    /if \(app\.pollHandle \|\| app\.tickHandle \|\| app\.statusHandle\) \{[\s\S]*?return;\s*\}/);
  assert.match(
    boardSource,
    /if \(pageContext\.isDoctor\) \{[\s\S]*?loadReports\(\);\s*registerReportRefresh\(loadReports\);\s*wireDoctorCockpitActions\(\);/);
  assert.match(
    boardSource,
    /if \(pageContext\.isWorkshop\) \{[\s\S]*?loadReports\(\);\s*registerReportRefresh\(loadReports\);\s*workshop\.wire\(\);/);
  assert.match(
    boardSource,
    /connectRealtime\([\s\S]*?\);\s*registerBoardPolling\(loadBoard\);\s*registerGeneralRender\(render\);\s*registerConnectionStatusRefresh\(\);\s*updateConnectionStatus\(\);/);
  assert.doesNotMatch(boardSource, /window\.setInterval/);
});

test("scheduling ownership and dependency direction remain narrow and one-way", () => {
  assert.match(
    moduleSource,
    /^import \{ app \} from "\.\/application-state\.js";/);
  assert.doesNotMatch(
    moduleSource,
    /from "\.\/(board|page-context|realtime-polling|connection-status)\.js"/);
  assert.match(
    boardSource,
    /import \{\s*registerGeneralRender,\s*registerReportRefresh\s*\} from "\.\/runtime-scheduling\.js";/);
  assert.doesNotMatch(boardSource, /app\.tickHandle\s*=/);
});
