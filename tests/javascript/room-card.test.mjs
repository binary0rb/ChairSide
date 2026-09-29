import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const roomCardUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/room-card.js",
  import.meta.url);
const domUtilsUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/dom-utils.js",
  import.meta.url);
const boardUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/board.js",
  import.meta.url);
const stylesUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/styles.css",
  import.meta.url);
const roomCardSource = await readFile(roomCardUrl, "utf8");
const domUtilsSource = await readFile(domUtilsUrl, "utf8");
const boardSource = await readFile(boardUrl, "utf8");
const stylesSource = await readFile(stylesUrl, "utf8");
const domUtilsDataUrl =
  `data:text/javascript;base64,${Buffer.from(domUtilsSource).toString("base64")}`;
const roomCardWithDataImport = roomCardSource.replace(
  "\"./dom-utils.js\"",
  JSON.stringify(domUtilsDataUrl));
const roomCardDataUrl =
  `data:text/javascript;base64,${Buffer.from(roomCardWithDataImport).toString("base64")}`;
const { createRoomCardPresentation } = await import(roomCardDataUrl);

const nowMs = Date.parse("2026-07-29T15:30:45Z");
const snapshot = {
  doctors: [
    { id: "otte", name: "Dr. Otte", shortName: "Otte", color: "#dc2626" },
    { id: "pledger", name: "Dr. Pledger", shortName: "Pledger", color: "#16a34a" }
  ],
  procedures: [
    { code: "EXT", label: "Extraction", icon: "forceps" },
    { code: "IMP", label: "Implant", icon: "bolt" }
  ]
};
const presentation = createRoomCardPresentation({
  getSnapshot: () => snapshot,
  getRoomId: room => room.roomId || room.number,
  getNowMs: () => nowMs,
  getAgingMinutes: () => 7,
  getStaleMinutes: () => 12,
  getDoctorInitials: doctorId => doctorId === "pledger" ? "JWP" : "LDO",
  procedure: {
    fromCode: code => snapshot.procedures.find(item => item.code === code) || null,
    formatCode: code => String(code || "").replaceAll("+", " + "),
    hasSedationModifier: code => /\+SED$/i.test(String(code || "")),
    renderEmptyIcon: () => "<svg data-icon=\"empty\"></svg>",
    renderIcon: procedure => `<svg data-icon="${procedure.icon}"></svg>`,
    resolveAccent: code => code === "EXT" ? "#ca8a04" : "#6d28d9",
    stripSedationModifier: code => String(code || "").replace(/\+SED$/i, "")
  }
});

function readyRoom(readyUrgency, isAddOn = false) {
  return {
    roomId: 4,
    state: "ReadyForDoctor",
    seatedAt: "2026-07-29T15:00:00Z",
    readyForDoctorAt: "2026-07-29T15:20:00Z",
    readyUrgency,
    assignmentLocked: true,
    assignedDoctor: "otte",
    procedureCode: "IMP+SED",
    doctor: snapshot.doctors[0],
    procedure: snapshot.procedures[1],
    assignment: {
      doctorId: "pledger",
      procedureCode: "EXT",
      sedation: { state: "EligibleYes" },
      expectedAllocation: {
        state: "ConfirmedAdjustedValue",
        suggestedValue: 3,
        confirmedValue: 4
      },
      isAddOn
    }
  };
}

test("canonical assignment wins over legacy display fields and doctor membership", () => {
  const room = readyRoom("Aging");
  const html = presentation.renderRoomTile(room);

  assert.equal(presentation.roomAssignedDoctorId(room), "pledger");
  assert.match(html, /Pledger/);
  assert.match(html, /JWP/);
  assert.match(html, /EXT \+ SED/);
  assert.match(html, /Sedation on \| 4 units confirmed/);
  assert.doesNotMatch(html, />Otte</);
  assert.doesNotMatch(html, />IMP/);
});

test("Ready stays primary while Aging and Stale render as subordinate urgency", () => {
  for (const urgency of ["Aging", "Stale"]) {
    const normalizedUrgency = urgency.toLowerCase();
    const html = presentation.renderRoomTile(readyRoom(urgency));

    assert.match(
      html,
      new RegExp(`class="room-tile ready-for-doctor urgency-${normalizedUrgency}`));
    assert.match(html, /<span class="ready-primary-badge">READY<\/span>/);
    assert.match(
      html,
      new RegExp(`<span class="ready-urgency-badge ready-timer-badge ${normalizedUrgency}">${urgency.toUpperCase()}</span>`));
    assert.doesNotMatch(html, /class="room-tile (aging|stale)\b/);
  }
});

test("Ready without urgency renders the Master ON TIME timer presentation", () => {
  const room = readyRoom("None");
  room.readyForDoctorAt = "2026-07-29T15:29:00Z";
  const html = presentation.renderRoomTile(room);

  assert.match(html, /class="room-tile ready-for-doctor /);
  assert.doesNotMatch(html, /urgency-(aging|stale)/);
  assert.match(html, /aria-label="Ready for Doctor, on time"/);
  assert.match(html, /<span class="ready-primary-badge">READY<\/span>/);
  assert.match(html, /<span class="ready-urgency-badge ready-timer-badge on-time">ON TIME<\/span>/);
});

test("active procedure markup exposes the configured label beneath its code", () => {
  const html = presentation.renderRoomTile(readyRoom("Aging"));

  assert.match(html, />EXT \+ SED<\/span>/);
  assert.match(html, /<small class="room-procedure-label">Extraction<\/small>/);
});

test("active procedure artwork is wrapped in the explicit procedure-light frame", () => {
  const html = presentation.renderRoomTile(readyRoom("Aging"));

  assert.match(
    html,
    /<span class="procedure-icon-frame"><svg data-icon="forceps"><\/svg><\/span>/);
  assert.match(stylesSource, /\.procedure-icon-frame\s*\{[^}]*linear-gradient\(/);
  assert.match(
    stylesSource,
    /color-mix\(in srgb, var\(--procedure-accent, var\(--ink\)\) 42%, #ffffff\)/);
  assert.match(
    stylesSource,
    /color-mix\(in srgb, var\(--procedure-accent, var\(--ink\)\) 58%, #ffffff\)/);
  assert.match(
    stylesSource,
    /\.procedure-icon-frame \.procedure-icon--png\s*\{[^}]*filter:\s*drop-shadow\(/);
});

test("large Room card preserves canonical procedure, assignment, doctor, and timer details", () => {
  const html = presentation.renderRoomTile(readyRoom("None"), true);

  assert.match(html, /class="room-tile ready-for-doctor[^"]*\blarge\b/);
  assert.match(html, />Room 4<\/strong>/);
  assert.match(html, /<span class="ready-primary-badge">READY<\/span>/);
  assert.match(html, /<svg data-icon="forceps"><\/svg>/);
  assert.match(html, />EXT \+ SED<\/span>/);
  assert.match(html, /<small class="room-procedure-label">Extraction<\/small>/);
  assert.match(html, /Sedation on \| 4 units confirmed/);
  assert.match(html, />Dr\. Pledger<\/span>/);
  assert.match(html, />Room time<\/span>/);
  assert.match(html, />30:45<\/strong>/);
});

test("standard room cards expose lifecycle timers for representative active states", () => {
  const baseRoom = readyRoom("None");
  const lifecycleCases = [
    {
      name: "Prestaging",
      room: {
        roomId: 4,
        state: "Prestaging",
        prestageStartedAt: "2026-07-29T15:25:00Z"
      },
      label: "Prep time",
      value: "05:45"
    },
    {
      name: "In Prep",
      room: { ...baseRoom, state: "Seated", readyForDoctorAt: null },
      label: "Room time",
      value: "30:45"
    },
    {
      name: "Ready",
      room: baseRoom,
      label: "Room time",
      value: "30:45"
    },
    {
      name: "Doctor In Room",
      room: { ...baseRoom, state: "DoctorInRoom", doctorArrivedAt: "2026-07-29T15:25:00Z" },
      label: "Room time",
      value: "30:45"
    },
    {
      name: "Turnover",
      room: { ...baseRoom, state: "Turnover" },
      label: "Room time",
      value: "30:45"
    }
  ];

  for (const lifecycle of lifecycleCases) {
    const html = presentation.renderRoomTile(lifecycle.room);
    assert.match(html, /<time class="room-timer">/, lifecycle.name);
    assert.ok(html.includes(`<span>${lifecycle.label}</span>`), lifecycle.name);
    assert.ok(html.includes(`<strong>${lifecycle.value}</strong>`), lifecycle.name);
  }
});

test("room-card presentation CSS keeps lifecycle timers visible and stable", () => {
  const hiddenTimerRules = [...stylesSource.matchAll(/([^{}]+)\{([^{}]*)\}/g)]
    .filter(([, selectors, declarations]) =>
      selectors.includes(".room-timer") && /display\s*:\s*none/.test(declarations));

  assert.deepEqual(hiddenTimerRules, []);
  assert.match(stylesSource, /\.room-timer\s*\{[^}]*display:\s*grid;/);
  assert.match(stylesSource, /\.room-timer\s*\{[^}]*font-variant-numeric:\s*tabular-nums;/);
});

test("Master four-column cards reserve meaningful width for doctor and timer", () => {
  const fourColumnRule = stylesSource.match(
    /@media \(min-width:\s*(\d+)px\)\s*\{\s*body\[data-view="master"\] \.room-grid\s*\{[^}]*grid-template-columns:\s*repeat\(4,\s*minmax\((\d+)px,\s*1fr\)\);/);
  const intermediateRule = stylesSource.match(
    /@media \(min-width:\s*(\d+)px\) and \(max-width:\s*(\d+)px\)\s*\{\s*body\[data-view="master"\] \.room-grid\s*\{[^}]*grid-template-columns:\s*repeat\(2,/);
  const shellInset = stylesSource.match(
    /\.master-shell,[^{}]*\{[^}]*width:\s*min\(1540px,\s*calc\(100vw\s*-\s*(\d+)px\)\);/);
  const masterGap = stylesSource.match(
    /body\[data-view="master"\] \.room-grid\s*\{[^}]*gap:\s*(\d+)px;/);
  const footerOffsets = stylesSource.match(
    /\.doctor-list\) \.room-footer\s*\{[^}]*right:\s*(\d+)px;[^}]*left:\s*(\d+)px;/);
  const footerGap = stylesSource.match(
    /\.room-topline,\s*\.room-footer\s*\{[^}]*gap:\s*(\d+)px;/);
  const timerMinimum = stylesSource.match(
    /\.room-footer\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\) minmax\((\d+)px,\s*auto\);/);
  const urgencyBorder = stylesSource.match(
    /\.room-tile\.ready-for-doctor\.urgency-aging,[^{}]*\{[^}]*border-width:\s*(\d+)px;/);

  for (const match of [fourColumnRule, intermediateRule, shellInset, masterGap,
    footerOffsets, footerGap, timerMinimum, urgencyBorder]) {
    assert.ok(match, "expected responsive room-card layout contract");
  }

  const fourColumnBreakpoint = Number(fourColumnRule[1]);
  const minimumCardWidth = Number(fourColumnRule[2]);
  const cardWidthAtBreakpoint = (
    fourColumnBreakpoint - Number(shellInset[1]) - (3 * Number(masterGap[1]))) / 4;
  const doctorWidth = cardWidthAtBreakpoint
    - (2 * Number(urgencyBorder[1]))
    - Number(footerOffsets[1])
    - Number(footerOffsets[2])
    - Number(footerGap[1])
    - Number(timerMinimum[1]);

  assert.equal(Number(intermediateRule[1]), 561);
  assert.equal(Number(intermediateRule[2]), fourColumnBreakpoint - 1);
  assert.ok(doctorWidth >= 80, `doctor column is only ${doctorWidth}px wide`);
  assert.ok(cardWidthAtBreakpoint >= minimumCardWidth,
    "four-column breakpoint cannot fit its minimum card widths");
  assert.match(stylesSource,
    /body\[data-view="master"\] \.room-grid\s*\{[^}]*grid-auto-rows:\s*1fr;/);
  assert.match(stylesSource,
    /body\[data-view="master"\] \.room-tile\s*\{[^}]*height:\s*100%;/);
});

test("Add-on badge renders only for flagged canonical assignments", () => {
  assert.match(presentation.renderRoomTile(readyRoom("None", true)), />ADD-ON</);
  assert.doesNotMatch(presentation.renderRoomTile(readyRoom("None", false)), />ADD-ON</);
});

test("Available standard and large cards use one canonical lifecycle term", () => {
  const room = {
    roomId: 8,
    state: "Available",
    assignmentLocked: false
  };
  const standard = presentation.renderRoomTile(room);
  const large = presentation.renderRoomTile(room, true);

  assert.doesNotMatch(standard, /class="room-tile[^"]*\blarge\b/);
  assert.match(large, /class="room-tile[^"]*\blarge\b/);
  for (const html of [standard, large]) {
    assert.match(html, /<span class="room-state-badge">AVAILABLE<\/span>/);
    assert.doesNotMatch(html, />OPEN<|Available|--:--|procedure-lockup|room-footer|No assignment/);
  }
  assert.equal(large.replace(/\blarge\b/, ""), standard);
});

test("all room contexts invoke the extracted renderer without copied card markup", () => {
  assert.match(
    boardSource,
    /import \{ createRoomCardPresentation \} from "\.\/room-card\.js";/);
  assert.match(boardSource, /\$\{renderRoomTile\(room\)\}<\/a>/);
  assert.match(boardSource, /room \? renderRoomTile\(room, true\) : renderInvalidRoomMessage\(\)/);
  assert.match(boardSource, /rooms\.map\(room => renderRoomTile\(room\)\)\.join\(""\)/);
  assert.doesNotMatch(boardSource, /<article class="room-tile/);
  assert.equal(
    (roomCardSource.match(/<article class="room-tile/g) || []).length,
    1);
});
