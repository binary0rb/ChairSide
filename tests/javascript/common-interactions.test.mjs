import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const moduleUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/common-interactions.js",
  import.meta.url);
const boardUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/board.js",
  import.meta.url);
const roomWorkflowUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/room-workflow.js",
  import.meta.url);
const reportsUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/reports.js",
  import.meta.url);
const moduleSource = await readFile(moduleUrl, "utf8");
const boardSource = await readFile(boardUrl, "utf8");
const roomWorkflowSource = await readFile(roomWorkflowUrl, "utf8");
const reportsSource = await readFile(reportsUrl, "utf8");
let moduleSequence = 0;

class FakeEventTarget {
  constructor() {
    this.handlers = new Map();
  }

  addEventListener(type, handler) {
    const handlers = this.handlers.get(type) || [];
    handlers.push(handler);
    this.handlers.set(type, handlers);
  }

  dispatch(type, event = {}) {
    for (const handler of this.handlers.get(type) || []) {
      handler(event);
    }
  }
}

function createTimerHarness() {
  const timers = [];
  const cleared = [];
  return {
    timers,
    cleared,
    setTimeout(callback, cadence) {
      const handle = { callback, cadence, sequence: timers.length + 1 };
      timers.push(handle);
      return handle;
    },
    clearTimeout(handle) {
      cleared.push(handle);
    }
  };
}

function closestTarget(matches) {
  return {
    closest(selector) {
      return matches.get(selector) || null;
    }
  };
}

function tile(selector, idKey, id, { disabled = false } = {}) {
  const button = { dataset: { [idKey]: id }, disabled };
  button.closest = candidate => candidate === selector ? button : null;
  return button;
}

async function importModule(app) {
  globalThis.__chairsideInteractionTestApp = app;
  const source = moduleSource
    .replace(
      'import { app } from "./application-state.js";',
      "const app = globalThis.__chairsideInteractionTestApp;")
    .concat(`\n// test-module-${moduleSequence += 1}`);
  const dataUrl = `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`;
  return import(dataUrl);
}

async function createHarness(t) {
  const originalDocument = globalThis.document;
  const originalWindow = globalThis.window;
  t.after(() => {
    globalThis.document = originalDocument;
    globalThis.window = originalWindow;
  });

  const documentTarget = new FakeEventTarget();
  const windowTarget = new FakeEventTarget();
  const timers = createTimerHarness();
  windowTarget.setTimeout = timers.setTimeout;
  windowTarget.clearTimeout = timers.clearTimeout;
  globalThis.document = documentTarget;
  globalThis.window = windowTarget;
  const app = { tilePressActive: false };
  return {
    app,
    documentTarget,
    interactions: await importModule(app),
    timers,
    windowTarget
  };
}

test("report press guard preserves selectors, timeout replacement, cleanup, and catch-up behavior", async t => {
  const { documentTarget, interactions, timers } = await createHarness(t);
  const pressTarget = new FakeEventTarget();
  let catchUps = 0;
  let reportPressActive = false;
  const selector = "[data-report-doctor-id], [data-report-doctor-tab], .report-table button";
  interactions.wirePressInterruptionGuard({
    pressTarget,
    selector,
    isPressActive: () => reportPressActive,
    setPressActive: value => {
      reportPressActive = value;
    },
    onCatchUp: () => {
      catchUps += 1;
    }
  });

  pressTarget.dispatch("pointerdown", {
    target: closestTarget(new Map())
  });
  assert.equal(reportPressActive, false);
  assert.equal(timers.timers.length, 0);

  const eligible = {};
  pressTarget.dispatch("pointerdown", {
    target: closestTarget(new Map([[selector, eligible]]))
  });
  assert.equal(reportPressActive, true);
  assert.equal(timers.timers[0].cadence, 3000);

  pressTarget.dispatch("pointerdown", {
    target: closestTarget(new Map([[selector, eligible]]))
  });
  assert.equal(timers.timers.length, 2);
  assert.ok(timers.cleared.includes(timers.timers[0]));

  documentTarget.dispatch("pointerup");
  assert.equal(reportPressActive, false);
  assert.equal(catchUps, 1);
  assert.ok(timers.cleared.includes(timers.timers[1]));
  documentTarget.dispatch("pointerup");
  assert.equal(catchUps, 1);

  pressTarget.dispatch("pointerdown", {
    target: closestTarget(new Map([[selector, eligible]]))
  });
  documentTarget.dispatch("pointercancel");
  assert.equal(reportPressActive, false);
  assert.equal(catchUps, 2);

  pressTarget.dispatch("pointerdown", {
    target: closestTarget(new Map([[selector, eligible]]))
  });
  timers.timers.at(-1).callback();
  assert.equal(reportPressActive, false);
  assert.equal(catchUps, 2);
  documentTarget.dispatch("pointerup");
  assert.equal(catchUps, 2);
});

test("tile press tracks logical identity and preserves pointer-versus-keyboard activation", async t => {
  const {
    app,
    documentTarget,
    interactions,
    timers
  } = await createHarness(t);
  const container = new FakeEventTarget();
  const selector = "[data-doctor-id]";
  const activations = [];
  interactions.wireTileGroup(
    container,
    selector,
    "doctorId",
    button => activations.push(button));
  interactions.wireTilePressCleanup(() => {});

  const disabled = tile(selector, "doctorId", "otte", { disabled: true });
  container.dispatch("pointerdown", { target: disabled });
  assert.equal(app.tilePressActive, false);

  const original = tile(selector, "doctorId", "otte");
  container.dispatch("pointerdown", { target: original });
  assert.equal(app.tilePressActive, true);
  assert.equal(timers.timers[0].cadence, 4000);

  const replacement = tile(selector, "doctorId", "otte");
  documentTarget.dispatch("pointerup", { target: replacement });
  assert.equal(app.tilePressActive, false);
  assert.deepEqual(activations, [replacement]);

  container.dispatch("click", { detail: 1, target: replacement });
  assert.deepEqual(activations, [replacement]);
  container.dispatch("click", { detail: 0, target: replacement });
  assert.deepEqual(activations, [replacement, replacement]);

  container.dispatch("pointerdown", { target: original });
  const mismatched = tile(selector, "doctorId", "gibson");
  documentTarget.dispatch("pointerup", { target: mismatched });
  assert.equal(app.tilePressActive, false);
  assert.equal(activations.length, 2);

  container.dispatch("pointerdown", { target: original });
  documentTarget.dispatch("pointerup", {
    target: closestTarget(new Map())
  });
  assert.equal(app.tilePressActive, false);
  assert.equal(activations.length, 2);
});

test("tile press replaces pending work and cleans up on cancel, blur, Escape, and fail-safe", async t => {
  const {
    app,
    documentTarget,
    interactions,
    timers,
    windowTarget
  } = await createHarness(t);
  const container = new FakeEventTarget();
  const selector = "[data-procedure-id]";
  const activations = [];
  let escapes = 0;
  interactions.wireTileGroup(
    container,
    selector,
    "procedureId",
    button => activations.push(button));
  interactions.wireTilePressCleanup(() => {
    escapes += 1;
  });
  const first = tile(selector, "procedureId", "EXT");
  const second = tile(selector, "procedureId", "CON");

  container.dispatch("pointerdown", { target: first });
  container.dispatch("pointerdown", { target: second });
  assert.ok(timers.cleared.includes(timers.timers[0]));
  documentTarget.dispatch("pointerup", { target: first });
  assert.equal(activations.length, 0);

  container.dispatch("pointerdown", { target: first });
  documentTarget.dispatch("pointercancel");
  assert.equal(app.tilePressActive, false);

  container.dispatch("pointerdown", { target: first });
  windowTarget.dispatch("blur");
  assert.equal(app.tilePressActive, false);

  container.dispatch("pointerdown", { target: first });
  documentTarget.dispatch("keydown", { key: "Enter" });
  assert.equal(app.tilePressActive, true);
  documentTarget.dispatch("keydown", { key: "Escape" });
  assert.equal(app.tilePressActive, false);
  assert.equal(escapes, 1);

  container.dispatch("pointerdown", { target: first });
  timers.timers.at(-1).callback();
  assert.equal(app.tilePressActive, false);
  documentTarget.dispatch("pointerup", { target: first });
  assert.equal(activations.length, 0);
});

test("workflow retains room decisions while common mechanics own only private interaction state", () => {
  assert.match(
    moduleSource,
    /^import \{ app \} from "\.\/application-state\.js";/);
  assert.doesNotMatch(
    moduleSource,
    /from "\.\/(board|page-context|realtime-polling|connection-status)\.js"/);
  assert.doesNotMatch(
    moduleSource,
    /\b(renderReports|renderDoctorView|selectedDoctorId|selectedProcedureId|sedationOn|expectedUnitsConfirmed|discardAssignmentDraft)\b/);
  assert.match(
    reportsSource,
    /selector: "\[data-report-doctor-id\], \[data-report-doctor-tab\], \.report-table button"/);
  assert.match(
    reportsSource,
    /function wireReportPressGuard\(\) \{[\s\S]*?onCatchUp: \(\) => \{[\s\S]*?renderReports\(\);/);
  assert.match(
    reportsSource,
    /function wireDoctorCockpitPressGuard\(\) \{[\s\S]*?selector: "\[data-report-doctor-tab\]"[\s\S]*?renderPage\(\);/);
  assert.match(
    roomWorkflowSource,
    /wireTileGroup\(doctorTiles,[\s\S]*?draft\.selectedDoctorId = button\.dataset\.doctorId;[\s\S]*?wireTileGroup\(procedureTiles,[\s\S]*?draft\.selectedProcedureId = button\.dataset\.procedureId;[\s\S]*?draft\.sedationOn = false;[\s\S]*?draft\.expectedUnitsConfirmed = false;/);
  assert.match(
    roomWorkflowSource,
    /wireTilePressCleanup\(\(\) => \{[\s\S]*?discardAssignmentDraft\(\);[\s\S]*?setRoomActionStatus\("Changes discarded\.", "pending"\);/);
  assert.doesNotMatch(
    boardSource,
    /\b(pendingTilePress|tilePressFailsafe|reportPressFailsafe|TILE_PRESS_FAILSAFE_MS|clearTilePress|completeTilePress)\b/);
  assert.match(
    roomWorkflowSource,
    /if \(!isTilePressActive\(\)\) \{[\s\S]*?syncRoomSelection\(room\);[\s\S]*?renderSelectionTiles\(room\);/);
  assert.equal(
    (reportsSource.match(/if \(state\.reportPressActive\)/g) || []).length,
    3);
  assert.equal(
    (reportsSource.match(/if \(!state\.reportPressActive\)/g) || []).length,
    1);
  assert.doesNotMatch(boardSource, /\breportPressActive\b/);
});
