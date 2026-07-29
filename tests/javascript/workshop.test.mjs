import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const moduleUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/workshop.js",
  import.meta.url);
const domUtilsUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/dom-utils.js",
  import.meta.url);
const boardUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/board.js",
  import.meta.url);
const workshopHtmlUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/workshop.html",
  import.meta.url);
const moduleSource = await readFile(moduleUrl, "utf8");
const domUtilsSource = await readFile(domUtilsUrl, "utf8");
const boardSource = await readFile(boardUrl, "utf8");
const workshopHtmlSource = await readFile(workshopHtmlUrl, "utf8");
const domUtilsDataUrl = `data:text/javascript;base64,${Buffer.from(domUtilsSource).toString("base64")}`;
const moduleWithDataImport = moduleSource.replace(
  '"./dom-utils.js"',
  JSON.stringify(domUtilsDataUrl));
const moduleDataUrl = `data:text/javascript;base64,${Buffer.from(moduleWithDataImport).toString("base64")}`;
const { createWorkshop } = await import(moduleDataUrl);

class FakeElement {
  constructor({
    title = "",
    detail = "",
    assumption = "",
    isCard = false
  } = {}) {
    this.title = title;
    this.detail = detail;
    this.assumption = assumption;
    this.isCard = isCard;
    this.innerHTML = "";
    this.attributes = new Map();
    this.classes = new Set();
    this.classList = {
      contains: name => this.classes.has(name),
      toggle: (name, enabled) => {
        if (enabled) {
          this.classes.add(name);
        } else {
          this.classes.delete(name);
        }
      }
    };
  }

  closest(selector) {
    return selector === '.workshop-card[data-preset-id]' && this.isCard ? this : null;
  }

  querySelector(selector) {
    if (selector === ".workshop-card-head h4") {
      return { textContent: this.title };
    }
    if (selector === ".workshop-preset-detail-source") {
      return { textContent: this.detail };
    }
    if (selector === ".workshop-preset-assumption-source") {
      return { textContent: this.assumption };
    }
    return null;
  }

  setAttribute(name, value) {
    this.attributes.set(name, value);
  }

  getAttribute(name) {
    return this.attributes.get(name);
  }
}

function createHarness(initialReports) {
  let reports = initialReports;
  const currentReality = new FakeElement();
  const presetDetail = new FakeElement();
  const cards = [
    new FakeElement({
      title: "Balanced Flow",
      detail: "  Balance   room flow across the day. ",
      assumption: " Keep current staffing. "
    }),
    new FakeElement({
      title: "More Throughput",
      detail: "Explore whether more cases could fit.",
      assumption: "Assume future demand is available."
    }),
    new FakeElement({
      title: "More Recovery",
      detail: "Protect recovery capacity.",
      assumption: "Preserve sedation recovery constraints."
    })
  ];
  cards.forEach(card => {
    card.isCard = true;
    card.setAttribute("aria-pressed", "false");
  });

  const listeners = new Map();
  globalThis.Element = FakeElement;
  globalThis.document = {
    addEventListener(type, listener) {
      listeners.set(type, listener);
    },
    getElementById(id) {
      if (id === "workshopCurrentReality") {
        return currentReality;
      }
      if (id === "workshopPresetDetail") {
        return presetDetail;
      }
      return null;
    },
    querySelectorAll(selector) {
      return selector === '.workshop-card[data-preset-id]' ? cards : [];
    }
  };

  return {
    cards,
    currentReality,
    listeners,
    presetDetail,
    setReports(value) {
      reports = value;
    },
    workshop: createWorkshop({
      getReports: () => reports
    })
  };
}

function activateWithKey(listener, card, key) {
  let prevented = false;
  listener({
    key,
    target: card,
    preventDefault() {
      prevented = true;
    }
  });
  return prevented;
}

test("Current Reality preserves unavailable and empty presentations", () => {
  const harness = createHarness(null);

  harness.workshop.render();
  assert.equal(
    harness.currentReality.innerHTML,
    `<p class="workshop-note">Current Reality couldn't load right now.</p>`);

  harness.setReports({
    rangeLabel: "Jul 1 - Jul 30",
    scheduleFit: {
      includedCycleCount: 4,
      scheduleFitCycleCount: 0,
      overall: {}
    }
  });
  harness.workshop.render();

  assert.match(
    harness.currentReality.innerHTML,
    /<p class="workshop-reality-window">Jul 1 - Jul 30<\/p>/);
  assert.match(
    harness.currentReality.innerHTML,
    /No completed cases with expected allocation in this window yet, so there's nothing to summarize\./);
  assert.doesNotMatch(harness.currentReality.innerHTML, /workshop-reality-grid/);
});

test("Current Reality maps every populated field and preserves exact formatting", () => {
  const reports = {
    rangeLabel: `Jul 1 & "Jul 30"`,
    scheduleFit: {
      includedCycleCount: 12,
      scheduleFitCycleCount: 9,
      overall: {
        totalExpectedBlocks: 8.04,
        totalActualBlocks: 9.96,
        totalDebtMinutes: 44.6,
        totalSlackMinutes: 15.4,
        utilizationRatio: 1.124
      }
    }
  };
  const harness = createHarness(reports);

  harness.workshop.render();
  const html = harness.currentReality.innerHTML;

  assert.match(html, /Jul 1 &amp; &quot;Jul 30&quot;/);
  assert.match(html, /Cases analyzed[\s\S]*9 of 12/);
  assert.match(html, /Expected blocks[\s\S]*8\.0 blocks/);
  assert.match(html, /Actual case-flow blocks[\s\S]*10\.0 blocks/);
  assert.match(html, /Schedule debt[\s\S]*45 min/);
  assert.match(html, /Raw slack observed[\s\S]*15 min/);
  assert.match(html, /Utilization vs expected[\s\S]*112% of expected/);
  assert.match(
    html,
    /raw slack is an observation here, not capacity[\s\S]*that can automatically be reclaimed/);
  assert.equal((html.match(/class="workshop-stat"/g) || []).length, 6);
});

test("click activation updates one selection and renders sourced readiness content", () => {
  const reports = { marker: "unchanged" };
  const before = structuredClone(reports);
  const harness = createHarness(reports);
  harness.workshop.wire();

  harness.listeners.get("click")({ target: harness.cards[0] });

  assert.equal(harness.cards[0].classList.contains("is-selected"), true);
  assert.equal(harness.cards[0].getAttribute("aria-pressed"), "true");
  assert.equal(harness.cards[1].classList.contains("is-selected"), false);
  assert.equal(harness.cards[1].getAttribute("aria-pressed"), "false");
  assert.match(harness.presetDetail.innerHTML, /Balanced Flow/);
  assert.match(harness.presetDetail.innerHTML, /Balance room flow across the day\./);
  assert.match(harness.presetDetail.innerHTML, /Keep current staffing\./);
  assert.match(harness.presetDetail.innerHTML, /<span class="workshop-status">Planned<\/span>/);
  assert.match(harness.presetDetail.innerHTML, /Observed today/);
  assert.match(harness.presetDetail.innerHTML, /Assumptions a projection would require/);
  assert.match(harness.presetDetail.innerHTML, /Scenario output &mdash; not computed yet/);
  assert.match(harness.presetDetail.innerHTML, /What ChairSide cannot know/);
  assert.match(
    harness.presetDetail.innerHTML,
    /It does not run a projection, change the schedule, or alter any live data\./);
  assert.deepEqual(reports, before);
});

test("Enter and Space activate focused cards and Space prevents scrolling", () => {
  const harness = createHarness(null);
  harness.workshop.wire();
  const keydown = harness.listeners.get("keydown");

  assert.equal(activateWithKey(keydown, harness.cards[1], "Enter"), true);
  assert.equal(harness.cards[1].classList.contains("is-selected"), true);
  assert.equal(harness.cards[0].getAttribute("aria-pressed"), "false");

  assert.equal(activateWithKey(keydown, harness.cards[2], " "), true);
  assert.equal(harness.cards[2].classList.contains("is-selected"), true);
  assert.equal(harness.cards[1].classList.contains("is-selected"), false);
  assert.equal(harness.cards[2].getAttribute("aria-pressed"), "true");
});

test("ordinary Current Reality rerenders preserve preset selection and detail", () => {
  const harness = createHarness(null);
  harness.workshop.wire();
  harness.listeners.get("click")({ target: harness.cards[0] });
  const detailBefore = harness.presetDetail.innerHTML;

  harness.setReports({
    rangeLabel: "Last 30 days",
    scheduleFit: {
      includedCycleCount: 1,
      scheduleFitCycleCount: 0,
      overall: {}
    }
  });
  harness.workshop.render();

  assert.equal(harness.cards[0].classList.contains("is-selected"), true);
  assert.equal(harness.cards[0].getAttribute("aria-pressed"), "true");
  assert.equal(harness.presetDetail.innerHTML, detailBefore);
});

test("module ownership, adapter direction, and board delegation remain narrow", () => {
  assert.match(
    moduleSource,
    /^import \{ escapeHtml, renderHelpIcon \} from "\.\/dom-utils\.js";/);
  assert.doesNotMatch(
    moduleSource,
    /from "\.\/(board|application-state|page-context|realtime-polling|runtime-scheduling|request-utils)\.js"/);
  assert.doesNotMatch(moduleSource, /\b(fetch|XMLHttpRequest|sessionStorage|localStorage)\b/);
  assert.doesNotMatch(moduleSource, /\bapp\./);

  assert.match(boardSource, /import \{ createWorkshop \} from "\.\/workshop\.js";/);
  assert.match(
    boardSource,
    /const workshop = pageContext\.isWorkshop\s*\? createWorkshop\(\{ getReports: \(\) => app\.reports \}\)\s*: null;/);
  assert.match(
    boardSource,
    /if \(pageContext\.isWorkshop\) \{[\s\S]*?workshop\.render\(\);[\s\S]*?\}/);
  assert.match(
    boardSource,
    /if \(pageContext\.isWorkshop\) \{[\s\S]*?loadReports\(\);\s*registerReportRefresh\(loadReports\);\s*workshop\.wire\(\);/);
  assert.doesNotMatch(
    boardSource,
    /function (renderWorkshop|wireWorkshopPresetSelection|selectWorkshopPreset|renderProjectionReadiness|formatWholeMinutes|formatBlocks|formatUtilizationPercent)\b/);
  assert.equal(
    (workshopHtmlSource.match(/class="workshop-card" data-preset-id=/g) || []).length,
    5);
  assert.equal(
    (workshopHtmlSource.match(/aria-pressed="false"/g) || []).length,
    5);
  assert.doesNotMatch(workshopHtmlSource, /workshop-card[^"]*\bis-selected\b/);
});
