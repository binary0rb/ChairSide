import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const workflowUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/room-workflow.js",
  import.meta.url);
const domUtilsUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/dom-utils.js",
  import.meta.url);
const boardUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/board.js",
  import.meta.url);
const applicationStateUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/application-state.js",
  import.meta.url);

const workflowSource = await readFile(workflowUrl, "utf8");
const domUtilsSource = await readFile(domUtilsUrl, "utf8");
const boardSource = await readFile(boardUrl, "utf8");
const applicationStateSource = await readFile(applicationStateUrl, "utf8");

const commonInteractionsStub = `
  export function wireTileGroup(container, selector, idKey, activate) {
    globalThis.__roomWorkflowTileGroups.set(idKey, activate);
  }
  export function wireTilePressCleanup(onEscape) {
    globalThis.__roomWorkflowEscape = onEscape;
  }
`;
const requestUtilsStub = `
  export function mutationHeaders(baseHeaders = {}) {
    return { ...baseHeaders, "X-ChairSide-Room-Token": "room-token" };
  }
  export async function readErrorMessage(response, fallback) {
    const text = await response.text();
    return text || fallback;
  }
`;

function dataUrl(source) {
  return `data:text/javascript;base64,${Buffer.from(source).toString("base64")}`;
}

const workflowWithDataImports = workflowSource
  .replace("\"./common-interactions.js\"", JSON.stringify(dataUrl(commonInteractionsStub)))
  .replace("\"./dom-utils.js\"", JSON.stringify(dataUrl(domUtilsSource)))
  .replace("\"./request-utils.js\"", JSON.stringify(dataUrl(requestUtilsStub)));
const { createRoomWorkflow } = await import(dataUrl(workflowWithDataImports));

class ClassListStub {
  constructor() {
    this.values = new Set();
  }

  add(value) {
    this.values.add(value);
  }

  remove(value) {
    this.values.delete(value);
  }

  toggle(value, force) {
    const enabled = force === undefined ? !this.values.has(value) : Boolean(force);
    if (enabled) {
      this.values.add(value);
    } else {
      this.values.delete(value);
    }
    return enabled;
  }

  contains(value) {
    return this.values.has(value);
  }
}

class ControlStub {
  constructor(id) {
    this.id = id;
    this.attributes = new Map();
    this.children = new Map();
    this.classList = new ClassListStub();
    this.dataset = {};
    this.disabled = false;
    this.hidden = false;
    this.innerHTML = "";
    this.listeners = new Map();
    this.options = [];
    this.textContent = "";
    this.focused = false;
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) || [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  async dispatch(type, event = {}) {
    for (const listener of this.listeners.get(type) || []) {
      await listener({ target: this, detail: 0, ...event });
    }
  }

  focus() {
    this.focused = true;
  }

  querySelector(selector) {
    return this.children.get(selector) || null;
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  getAttribute(name) {
    return this.attributes.get(name);
  }
}

const controlIds = [
  "demoElapsedSelect",
  "beginPrestageButton",
  "seatButton",
  "readyForDoctorButton",
  "saveDetailsButton",
  "discardChangesButton",
  "withdrawReadyButton",
  "cancelSeatingButton",
  "doctorArrivedButton",
  "doctorCompleteButton",
  "roomAvailableButton",
  "doctorTiles",
  "procedureTiles",
  "sedationToggle",
  "addOnToggle",
  "allocationSection",
  "allocationUnits",
  "allocationMinutes",
  "allocationHint",
  "allocationMinus",
  "allocationPlus",
  "allocationConfirm",
  "assignmentGuidance",
  "roomActionStatus"
];

const doctors = [
  { id: "otte", name: "Dr. Otte", color: "#dc2626" },
  { id: "pledger", name: "Dr. Pledger", color: "#16a34a" }
];
const procedures = [
  {
    code: "EXT",
    label: "Extraction",
    icon: "forceps",
    sedationEligible: true,
    defaultExpectedUnits: 3,
    allocationBehavior: "Fixed"
  },
  {
    code: "IMP",
    label: "Implant",
    icon: "bolt",
    sedationEligible: false,
    defaultExpectedUnits: 5,
    allocationBehavior: "Variable"
  }
];

function assignment({
  doctorId = "otte",
  procedureCode = "EXT",
  sedationState = "EligibleYes",
  allocationState = "ConfirmedAdjustedValue",
  suggestedValue = 3,
  confirmedValue = 4,
  isAddOn = false
} = {}) {
  return {
    doctorId,
    procedureCode,
    sedation: { state: sedationState },
    expectedAllocation: {
      state: allocationState,
      suggestedValue,
      confirmedValue
    },
    completeness: confirmedValue === null ? "Partial" : "Complete",
    isAddOn
  };
}

function room(overrides = {}) {
  return {
    roomId: 1,
    state: "Prestaging",
    episodeId: "episode-1",
    prestageStartedAt: "2026-07-29T15:00:00Z",
    assignmentLocked: false,
    integrityFaults: [],
    assignment: assignment(),
    capabilities: {
      canBeginPrestage: false,
      canEditAssignment: true,
      canEditAddOn: true,
      canSaveDetails: true,
      canSeat: true,
      canCancelPrestage: true,
      canCancelSeating: false,
      canReady: true,
      canWithdrawReady: false,
      canDoctorArrive: false,
      canDoctorComplete: false,
      canRoomAvailable: false
    },
    ...overrides
  };
}

function createHarness(initialRoom = room()) {
  const controls = new Map(controlIds.map(id => [id, new ControlStub(id)]));
  const sedationToggle = controls.get("sedationToggle");
  sedationToggle.children.set(".modifier-state", new ControlStub("sedationState"));
  sedationToggle.children.set(".sedation-hint", new ControlStub("sedationHint"));
  const addOnToggle = controls.get("addOnToggle");
  addOnToggle.children.set(".modifier-state", new ControlStub("addOnState"));
  addOnToggle.children.set(".add-on-hint", new ControlStub("addOnHint"));
  const focusDoctor = new ControlStub("focusDoctor");
  const focusProcedure = new ControlStub("focusProcedure");
  const snapshot = {
    doctors,
    procedures,
    rooms: [initialRoom]
  };
  const calls = [];
  const appliedRooms = [];
  const tokenPrompts = [];

  globalThis.__roomWorkflowTileGroups = new Map();
  globalThis.__roomWorkflowEscape = null;
  globalThis.document = {
    getElementById: id => controls.get(id) || null,
    querySelector: selector => {
      if (selector.startsWith("#doctorTiles")) {
        return focusDoctor;
      }
      if (selector.startsWith("#procedureTiles")) {
        return focusProcedure;
      }
      return null;
    }
  };
  globalThis.window = {
    confirm: () => true
  };
  globalThis.confirm = () => true;
  globalThis.fetch = async (url, options) => {
    calls.push({ url, options });
    return {
      ok: true,
      status: 200,
      json: async () => ({ room: snapshot.rooms[0] }),
      text: async () => ""
    };
  };

  const workflow = createRoomWorkflow({
    getSnapshot: () => snapshot,
    getRoomNumber: () => 1,
    getRoomId: value => value.roomId || value.number,
    normalizeState: value => String(value?.state || "Available")
      .replace("ReadyForDoctor", "ready-for-doctor")
      .replace("DoctorInRoom", "doctor-in-room")
      .toLowerCase(),
    isTilePressActive: () => false,
    isDemoTimerEnabled: () => false,
    applyRoomMutation: nextRoom => {
      snapshot.rooms[0] = nextRoom;
      appliedRooms.push(nextRoom);
    },
    showRoomTokenPrompt: status => tokenPrompts.push(status),
    procedure: {
      fromCode: code => procedures.find(item => item.code === code) || null,
      hasSedationModifier: code => /\+SED$/i.test(String(code || "")),
      renderIcon: value => `<svg data-icon="${value.icon}"></svg>`,
      resolveAccent: code => code === "EXT" ? "#ca8a04" : "#6d28d9",
      stripSedationModifier: code => String(code || "").replace(/\+SED$/i, "")
    }
  });

  workflow.wire();
  workflow.render(initialRoom);

  return {
    appliedRooms,
    calls,
    controls,
    focusDoctor,
    focusProcedure,
    snapshot,
    tokenPrompts,
    workflow
  };
}

function selected(html, value, attribute) {
  const escaped = value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return new RegExp(`class="selection-tile [^"]*selected[^"]*"[^>]*${attribute}="${escaped}"`)
    .test(html);
}

test("durable assignment initializes draft and unchanged polling preserves a dirty draft", () => {
  const harness = createHarness();

  assert.equal(selected(harness.controls.get("doctorTiles").innerHTML, "otte", "data-doctor-id"), true);
  assert.equal(selected(harness.controls.get("procedureTiles").innerHTML, "EXT", "data-procedure-id"), true);
  assert.equal(harness.controls.get("sedationToggle").getAttribute("aria-checked"), "true");
  assert.equal(harness.controls.get("allocationUnits").textContent, "4 units");
  assert.equal(harness.controls.get("saveDetailsButton").hidden, true);
  assert.equal(harness.controls.get("discardChangesButton").hidden, true);

  globalThis.__roomWorkflowTileGroups.get("doctorId")({
    dataset: { doctorId: "pledger" }
  });
  assert.equal(selected(harness.controls.get("doctorTiles").innerHTML, "pledger", "data-doctor-id"), true);
  assert.equal(harness.controls.get("saveDetailsButton").hidden, false);
  assert.equal(harness.controls.get("discardChangesButton").hidden, false);

  harness.workflow.render(harness.snapshot.rooms[0]);
  assert.equal(selected(harness.controls.get("doctorTiles").innerHTML, "pledger", "data-doctor-id"), true);
});

test("changed episode or durable assignment reconciles the draft", () => {
  const harness = createHarness();
  globalThis.__roomWorkflowTileGroups.get("doctorId")({
    dataset: { doctorId: "pledger" }
  });

  const nextEpisode = room({ episodeId: "episode-2" });
  harness.snapshot.rooms[0] = nextEpisode;
  harness.workflow.render(nextEpisode);
  assert.equal(selected(harness.controls.get("doctorTiles").innerHTML, "otte", "data-doctor-id"), true);

  globalThis.__roomWorkflowTileGroups.get("doctorId")({
    dataset: { doctorId: "pledger" }
  });
  const changedAssignment = room({
    episodeId: "episode-2",
    assignment: assignment({ doctorId: "pledger" })
  });
  harness.snapshot.rooms[0] = changedAssignment;
  harness.workflow.render(changedAssignment);
  assert.equal(selected(harness.controls.get("doctorTiles").innerHTML, "pledger", "data-doctor-id"), true);
  assert.equal(harness.controls.get("saveDetailsButton").hidden, true);
});

test("Discard Changes and Escape restore the persisted assignment", async () => {
  const harness = createHarness();
  const selectPledger = () => globalThis.__roomWorkflowTileGroups.get("doctorId")({
    dataset: { doctorId: "pledger" }
  });

  selectPledger();
  await harness.controls.get("discardChangesButton").dispatch("click");
  assert.equal(selected(harness.controls.get("doctorTiles").innerHTML, "otte", "data-doctor-id"), true);
  assert.equal(harness.controls.get("roomActionStatus").textContent, "Changes discarded.");

  selectPledger();
  globalThis.__roomWorkflowEscape();
  assert.equal(selected(harness.controls.get("doctorTiles").innerHTML, "otte", "data-doctor-id"), true);
});

test("procedure changes invalidate sedation and allocation confirmation", () => {
  const harness = createHarness();

  globalThis.__roomWorkflowTileGroups.get("procedureId")({
    dataset: { procedureId: "IMP" }
  });

  assert.equal(selected(harness.controls.get("procedureTiles").innerHTML, "IMP", "data-procedure-id"), true);
  assert.equal(harness.controls.get("sedationToggle").getAttribute("aria-checked"), "false");
  assert.equal(harness.controls.get("allocationUnits").textContent, "5 units");
  assert.equal(harness.controls.get("allocationConfirm").hidden, false);
  assert.equal(harness.controls.get("saveDetailsButton").hidden, false);
});

test("Add-on is optional, survives procedure changes, and uses Save Details", async () => {
  const harness = createHarness();

  assert.equal(harness.controls.get("addOnToggle").getAttribute("aria-checked"), "false");
  await harness.controls.get("addOnToggle").dispatch("click");
  assert.equal(harness.controls.get("addOnToggle").getAttribute("aria-checked"), "true");
  assert.equal(harness.controls.get("saveDetailsButton").hidden, false);

  globalThis.__roomWorkflowTileGroups.get("procedureId")({
    dataset: { procedureId: "IMP" }
  });
  assert.equal(harness.controls.get("addOnToggle").getAttribute("aria-checked"), "true");

  await harness.controls.get("saveDetailsButton").dispatch("click");
  assert.equal(JSON.parse(harness.calls[0].options.body).isAddOn, true);
});

test("Ready locks dispatch controls while Add-on remains editable until Doctor Arrived", async () => {
  const readyRoom = room({
    state: "ReadyForDoctor",
    assignmentLocked: true,
    capabilities: {
      ...room().capabilities,
      canEditAssignment: false,
      canEditAddOn: true,
      canSaveDetails: true,
      canSeat: false,
      canReady: false,
      canCancelPrestage: false,
      canDoctorArrive: true
    }
  });
  const harness = createHarness(readyRoom);

  assert.match(harness.controls.get("doctorTiles").innerHTML, /disabled/);
  assert.equal(harness.controls.get("sedationToggle").disabled, true);
  assert.equal(harness.controls.get("addOnToggle").disabled, false);
  await harness.controls.get("addOnToggle").dispatch("click");
  assert.equal(harness.controls.get("saveDetailsButton").hidden, false);
  await harness.controls.get("saveDetailsButton").dispatch("click");
  assert.deepEqual(JSON.parse(harness.calls[0].options.body), {
    doctorId: "otte",
    procedureCode: "EXT",
    sedationChoice: "yes",
    confirmedExpectedAllocationUnits: 4,
    isAddOn: true
  });

  const arrived = room({
    state: "DoctorInRoom",
    doctorArrivedAt: "2026-07-29T15:30:00Z",
    assignmentLocked: true,
    assignment: assignment({ isAddOn: true }),
    capabilities: {
      ...room().capabilities,
      canEditAssignment: false,
      canEditAddOn: false,
      canSaveDetails: false,
      canSeat: false,
      canReady: false,
      canCancelPrestage: false,
      canDoctorArrive: false,
      canDoctorComplete: true
    }
  });
  harness.snapshot.rooms[0] = arrived;
  harness.workflow.render(arrived);
  assert.equal(harness.controls.get("addOnToggle").disabled, true);
  assert.equal(harness.controls.get("addOnToggle").getAttribute("aria-checked"), "true");
});

test("suggested allocation remains distinct from confirmed and manual allocation", async () => {
  const suggestedRoom = room({
    assignment: assignment({
      sedationState: "EligibleNo",
      allocationState: "Suggested",
      suggestedValue: 3,
      confirmedValue: null
    })
  });
  const harness = createHarness(suggestedRoom);

  assert.equal(harness.controls.get("allocationHint").textContent, "Suggested: 3 units. Confirm to continue.");
  assert.equal(harness.controls.get("allocationConfirm").hidden, false);
  assert.equal(harness.controls.get("saveDetailsButton").hidden, true);

  await harness.controls.get("allocationConfirm").dispatch("click");
  assert.equal(harness.controls.get("allocationConfirm").hidden, true);
  assert.equal(harness.controls.get("saveDetailsButton").hidden, false);

  await harness.controls.get("allocationPlus").dispatch("click");
  assert.equal(harness.controls.get("allocationUnits").textContent, "4 units");
  assert.equal(
    harness.controls.get("allocationHint").textContent,
    "Allocation confirmed. Adjusting the units keeps the new value confirmed.");
});

test("eligible unresolved sedation remains distinct from explicit yes or no", async () => {
  const unresolvedRoom = room({
    assignment: assignment({
      sedationState: "EligibleUnresolved",
      allocationState: "ConfirmedSuggestedValue",
      suggestedValue: 3,
      confirmedValue: 3
    })
  });
  const harness = createHarness(unresolvedRoom);

  assert.equal(harness.controls.get("sedationToggle").getAttribute("aria-checked"), "false");
  assert.equal(harness.controls.get("saveDetailsButton").hidden, false);

  await harness.controls.get("saveDetailsButton").dispatch("click");
  const request = JSON.parse(harness.calls[0].options.body);
  assert.equal(request.sedationChoice, null);
});

test("Save Details, Seat, Ready, and Doctor Arrived retain exact transport shapes", async () => {
  const saveHarness = createHarness();
  globalThis.__roomWorkflowTileGroups.get("doctorId")({
    dataset: { doctorId: "pledger" }
  });
  await saveHarness.controls.get("saveDetailsButton").dispatch("click");
  assert.equal(saveHarness.calls[0].url, "/api/rooms/1/assignment-details");
  assert.equal(saveHarness.calls[0].options.method, "PUT");
  assert.deepEqual(JSON.parse(saveHarness.calls[0].options.body), {
    doctorId: "pledger",
    procedureCode: "EXT",
    sedationChoice: "yes",
    confirmedExpectedAllocationUnits: 4,
    isAddOn: false
  });

  const seatHarness = createHarness();
  await seatHarness.controls.get("seatButton").dispatch("click");
  assert.equal(seatHarness.calls[0].url, "/api/rooms/1/seat");
  assert.equal(seatHarness.calls[0].options.method, "POST");
  assert.deepEqual(JSON.parse(seatHarness.calls[0].options.body), {
    assignment: {
      doctorId: "otte",
      procedureCode: "EXT",
      sedationChoice: "yes",
      confirmedExpectedAllocationUnits: 4,
      isAddOn: false
    }
  });

  const readyHarness = createHarness();
  await readyHarness.controls.get("readyForDoctorButton").dispatch("click");
  assert.equal(readyHarness.calls[0].url, "/api/rooms/1/ready-for-doctor");
  assert.equal(readyHarness.calls[0].options.method, "POST");
  assert.deepEqual(JSON.parse(readyHarness.calls[0].options.body), {
    assignment: {
      doctorId: "otte",
      procedureCode: "EXT",
      sedationChoice: "yes",
      confirmedExpectedAllocationUnits: 4,
      isAddOn: false
    }
  });

  const arrivedRoom = room({
    state: "ReadyForDoctor",
    assignmentLocked: true,
    capabilities: {
      ...room().capabilities,
      canEditAssignment: false,
      canEditAddOn: true,
      canSaveDetails: true,
      canSeat: false,
      canReady: false,
      canCancelPrestage: false,
      canDoctorArrive: true
    }
  });
  const arrivedHarness = createHarness(arrivedRoom);
  await arrivedHarness.controls.get("doctorArrivedButton").dispatch("click");
  assert.equal(arrivedHarness.calls[0].url, "/api/rooms/1/doctor-arrived");
  assert.equal(arrivedHarness.calls[0].options.method, "POST");
  assert.equal(Object.hasOwn(arrivedHarness.calls[0].options, "body"), false);
  assert.deepEqual(arrivedHarness.calls[0].options.headers, {
    "X-ChairSide-Room-Token": "room-token"
  });
});

test("failed transport preserves the dirty draft and successful retry applies returned room", async () => {
  const harness = createHarness();
  globalThis.__roomWorkflowTileGroups.get("doctorId")({
    dataset: { doctorId: "pledger" }
  });
  globalThis.fetch = async (url, options) => {
    harness.calls.push({ url, options });
    return {
      ok: false,
      status: 500,
      text: async () => "Injected failure"
    };
  };

  await harness.controls.get("saveDetailsButton").dispatch("click");
  harness.workflow.render(harness.snapshot.rooms[0]);
  assert.equal(harness.controls.get("roomActionStatus").textContent, "Injected failure");
  assert.equal(harness.controls.get("roomActionStatus").dataset.tone, "error");
  assert.equal(selected(harness.controls.get("doctorTiles").innerHTML, "pledger", "data-doctor-id"), true);
  assert.equal(harness.appliedRooms.length, 0);

  const persisted = room({ assignment: assignment({ doctorId: "pledger" }) });
  globalThis.fetch = async (url, options) => {
    harness.calls.push({ url, options });
    return {
      ok: true,
      status: 200,
      json: async () => ({ room: persisted }),
      text: async () => ""
    };
  };
  await harness.controls.get("saveDetailsButton").dispatch("click");
  harness.workflow.render(harness.snapshot.rooms[0]);

  assert.equal(harness.appliedRooms.length, 1);
  assert.equal(harness.snapshot.rooms[0].assignment.doctorId, "pledger");
  assert.equal(harness.controls.get("saveDetailsButton").hidden, true);
});

test("board delegates Room Panel workflow ownership without copied implementation", () => {
  assert.match(
    boardSource,
    /import \{ createRoomWorkflow \} from "\.\/room-workflow\.js";/);
  assert.match(boardSource, /const roomWorkflow = createRoomWorkflow\(\{/);
  assert.match(boardSource, /roomWorkflow\.render\(room\);/);
  assert.match(boardSource, /roomWorkflow\.wire\(\);/);
  assert.doesNotMatch(boardSource, /function wireRoomPanel\(\)/);
  assert.doesNotMatch(boardSource, /function syncRoomSelection\(/);
  assert.doesNotMatch(boardSource, /function sendCanonicalMutation\(/);
  assert.doesNotMatch(boardSource, /\/assignment-details|\/ready-for-doctor|\/doctor-arrived/);
  assert.doesNotMatch(
    applicationStateSource,
    /\b(selectedDoctorId|selectedProcedureId|sedationOn|expectedUnitsConfirmed|selectionContext)\b/);
});
