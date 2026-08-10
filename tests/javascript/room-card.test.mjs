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
const roomCardSource = await readFile(roomCardUrl, "utf8");
const domUtilsSource = await readFile(domUtilsUrl, "utf8");
const boardSource = await readFile(boardUrl, "utf8");
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

test("Add-on badge renders only for flagged canonical assignments", () => {
  assert.match(presentation.renderRoomTile(readyRoom("None", true)), />ADD-ON</);
  assert.doesNotMatch(presentation.renderRoomTile(readyRoom("None", false)), />ADD-ON</);
});

test("standard and large cards use the same renderer with only the large modifier", () => {
  const room = {
    roomId: 8,
    state: "Available",
    assignmentLocked: false
  };
  const standard = presentation.renderRoomTile(room);
  const large = presentation.renderRoomTile(room, true);

  assert.doesNotMatch(standard, /class="room-tile[^"]*\blarge\b/);
  assert.match(large, /class="room-tile[^"]*\blarge\b/);
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
