import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const requestUtilsUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/request-utils.js",
  import.meta.url);
const boardUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/board.js",
  import.meta.url);
const roomWorkflowUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/room-workflow.js",
  import.meta.url);
const reportDataUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/report-data.js",
  import.meta.url);
const reportsUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/reports.js",
  import.meta.url);
const requestUtilsSource = await readFile(requestUtilsUrl, "utf8");
const boardSource = await readFile(boardUrl, "utf8");
const roomWorkflowSource = await readFile(roomWorkflowUrl, "utf8");
const reportDataSource = await readFile(reportDataUrl, "utf8");
const reportsSource = await readFile(reportsUrl, "utf8");
const storedValues = new Map();

globalThis.sessionStorage = {
  getItem: key => storedValues.has(key) ? storedValues.get(key) : null,
  setItem: (key, value) => storedValues.set(key, String(value)),
  removeItem: key => storedValues.delete(key)
};

const applicationStateDataUrl = "data:text/javascript,export%20const%20app%20%3D%20%7B%20roomToken%3A%20%22%22%20%7D%3B";
const requestUtilsWithDataImport = requestUtilsSource.replace(
  "\"./application-state.js\"",
  JSON.stringify(applicationStateDataUrl));
const requestUtilsDataUrl = `data:text/javascript;base64,${Buffer.from(requestUtilsWithDataImport).toString("base64")}`;
const requestUtils = await import(requestUtilsDataUrl);
const { app } = await import(applicationStateDataUrl);

test("admin token storage preserves the established key and values", () => {
  storedValues.clear();
  assert.deepEqual(requestUtils.adminAccess, {
    storageKey: "chairside-admin-token",
    headerName: "X-ChairSide-Admin-Token"
  });
  assert.equal(requestUtils.readAdminToken(), null);

  requestUtils.storeAdminToken(" admin-token ");
  assert.equal(
    storedValues.get("chairside-admin-token"),
    " admin-token ");
  assert.equal(requestUtils.readAdminToken(), " admin-token ");

  requestUtils.clearAdminToken();
  assert.equal(requestUtils.readAdminToken(), null);
});

test("admin headers preserve token-present and token-absent behavior", () => {
  storedValues.clear();
  assert.deepEqual(requestUtils.adminRequestHeaders(), {});

  requestUtils.storeAdminToken("");
  assert.deepEqual(requestUtils.adminRequestHeaders(), {});

  requestUtils.storeAdminToken("admin-token");
  assert.deepEqual(requestUtils.adminRequestHeaders(), {
    "X-ChairSide-Admin-Token": "admin-token"
  });
});

test("room mutation headers preserve merging and room-token precedence", () => {
  const baseHeaders = {
    "Content-Type": "application/json",
    "X-ChairSide-Room-Token": "base-token"
  };

  app.roomToken = "";
  const withoutToken = requestUtils.mutationHeaders(baseHeaders);
  assert.deepEqual(withoutToken, baseHeaders);
  assert.notStrictEqual(withoutToken, baseHeaders);

  app.roomToken = "room-token";
  assert.deepEqual(requestUtils.mutationHeaders(baseHeaders), {
    "Content-Type": "application/json",
    "X-ChairSide-Room-Token": "room-token"
  });
  assert.equal(baseHeaders["X-ChairSide-Room-Token"], "base-token");
  app.roomToken = "";
});

test("error parsing preserves empty, JSON, unresolved, and fallback behavior", async () => {
  const response = text => ({ text: async () => text });

  assert.equal(
    await requestUtils.readErrorMessage(response(""), "Fallback."),
    "Fallback.");
  assert.equal(
    await requestUtils.readErrorMessage(
      response(JSON.stringify({ message: "Invalid assignment." })),
      "Fallback."),
    "Invalid assignment.");
  assert.equal(
    await requestUtils.readErrorMessage(
      response(JSON.stringify({
        message: "Assignment incomplete.",
        unresolvedFields: [
          "doctorId",
          "procedureCode",
          "sedationChoice",
          "confirmedExpectedAllocationUnits",
          "futureField"
        ]
      })),
      "Fallback."),
    "Assignment incomplete. Still needed: doctor, procedure, sedation, allocation confirmation, futureField.");
  assert.equal(
    await requestUtils.readErrorMessage(
      response(JSON.stringify({ unresolvedFields: ["doctorId"] })),
      "Fallback."),
    "Fallback. Still needed: doctor.");
  assert.equal(
    await requestUtils.readErrorMessage(response("{}"), "Fallback."),
    "Fallback.");
});

test("error parsing preserves plain text and malformed JSON exactly", async () => {
  const response = text => ({ text: async () => text });

  assert.equal(
    await requestUtils.readErrorMessage(
      response("Plain text failure."),
      "Fallback."),
    "Plain text failure.");
  assert.equal(
    await requestUtils.readErrorMessage(
      response("{ malformed json"),
      "Fallback."),
    "{ malformed json");
  assert.equal(
    await requestUtils.readErrorMessage(response("   "), "Fallback."),
    "   ");
});

test("request primitives own no endpoint or UI orchestration", () => {
  assert.match(
    requestUtilsSource,
    /import \{ app \} from "\.\/application-state\.js";/);
  assert.doesNotMatch(requestUtilsSource, /board\.js/);
  assert.doesNotMatch(requestUtilsSource, /\bfetch\s*\(/);
  assert.doesNotMatch(
    requestUtilsSource,
    /\b(loadReports|sendCanonicalMutation|sendRoomAction|render|showRoomTokenPrompt)\b/);

  assert.match(reportDataSource, /} from "\.\/request-utils\.js";/);
  assert.match(reportsSource, /} from "\.\/request-utils\.js";/);
  assert.doesNotMatch(boardSource, /from "\.\/request-utils\.js"/);
  assert.doesNotMatch(boardSource, /\bconst adminAccess = \{/);
  assert.doesNotMatch(
    boardSource,
    /function (adminRequestHeaders|mutationHeaders|readErrorMessage)\b/);
  assert.match(reportDataSource, /async function load\(\)/);
  assert.match(roomWorkflowSource, /async function sendCanonicalMutation\(/);
  assert.match(roomWorkflowSource, /async function sendRoomAction\(/);
  assert.match(roomWorkflowSource, /async function handleDoctorArrivalConflict\(/);
  assert.match(reportsSource, /function renderReportsAccessPrompt\(/);
});
