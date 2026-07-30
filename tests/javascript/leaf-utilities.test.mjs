import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const domUtilsUrl = new URL("../../src/ChairSide.Board/wwwroot/dom-utils.js", import.meta.url);
const formatUtilsUrl = new URL("../../src/ChairSide.Board/wwwroot/format-utils.js", import.meta.url);
const domUtilsSource = await readFile(domUtilsUrl, "utf8");
const formatUtilsSource = await readFile(formatUtilsUrl, "utf8");
const domUtils = await import(`data:text/javascript;base64,${Buffer.from(domUtilsSource).toString("base64")}`);
const formatUtils = await import(`data:text/javascript;base64,${Buffer.from(formatUtilsSource).toString("base64")}`);
const {
  escapeAttribute,
  escapeHtml,
  renderHelpIcon,
  setDisabled,
  setHidden
} = domUtils;
const {
  formatDateTime,
  formatDuration
} = formatUtils;

test("escaping preserves the established text and attribute behavior", () => {
  assert.equal(escapeHtml(null), "");
  assert.equal(escapeHtml(undefined), "");
  assert.equal(escapeHtml(42), "42");
  assert.equal(
    escapeHtml(`&<>"'\``),
    "&amp;&lt;&gt;&quot;&#39;`");
  assert.equal(
    escapeAttribute(`Dr. "Otte" & <team>`),
    "Dr. &quot;Otte&quot; &amp; &lt;team&gt;");
});

test("DOM setters update supplied controls and ignore missing controls", () => {
  const control = { disabled: false, hidden: false };

  setDisabled(control, true);
  assert.equal(control.disabled, true);
  setDisabled(control, false);
  assert.equal(control.disabled, false);

  setHidden(control, true);
  assert.equal(control.hidden, true);
  setHidden(control, false);
  assert.equal(control.hidden, false);

  assert.doesNotThrow(() => setDisabled(null, true));
  assert.doesNotThrow(() => setHidden(undefined, true));
});

test("help renderer preserves markup, accessibility text, placement, and escaping", () => {
  assert.equal(
    renderHelpIcon(`Use "planned" <time> & care.`),
    `<span class="help-icon" tabindex="0" aria-label="Help: Use &quot;planned&quot; &lt;time&gt; &amp; care.">
    <span aria-hidden="true">?</span>
    <span class="help-icon-bubble" aria-hidden="true">Use &quot;planned&quot; &lt;time&gt; &amp; care.</span>
  </span>`);
  assert.match(
    renderHelpIcon("Corner help", "corner"),
    /class="help-icon help-icon--corner"/);
});

test("duration formatting preserves rounding, clamping, and fallback output", () => {
  assert.equal(formatDuration(undefined), "00:00");
  assert.equal(formatDuration("not-a-number"), "00:00");
  assert.equal(formatDuration(-5), "00:00");
  assert.equal(formatDuration(59.4), "00:59");
  assert.equal(formatDuration(59.6), "01:00");
  assert.equal(formatDuration(3661), "61:01");
});

test("date-time formatting preserves fallback and locale-aware output", () => {
  const value = "2026-07-28T15:04:00Z";
  const expected = new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  }).format(new Date(value));

  assert.equal(formatDateTime(null), "--");
  assert.equal(formatDateTime(""), "--");
  assert.equal(formatDateTime(0), "--");
  assert.equal(formatDateTime(value), expected);
});

test("callers import leaf utilities while their dependency direction remains one-way", async () => {
  const board = await readFile(
    new URL("../../src/ChairSide.Board/wwwroot/board.js", import.meta.url),
    "utf8");
  const reports = await readFile(
    new URL("../../src/ChairSide.Board/wwwroot/reports.js", import.meta.url),
    "utf8");
  assert.match(
    board,
    /import \{ escapeAttribute, escapeHtml \} from "\.\/dom-utils\.js";/);
  assert.match(
    reports,
    /import \{ escapeAttribute, escapeHtml, renderHelpIcon \} from "\.\/dom-utils\.js";/);
  assert.match(
    reports,
    /import \{ formatDateTime, formatDuration \} from "\.\/format-utils\.js";/);
  assert.doesNotMatch(
    board,
    /function (escapeHtml|escapeAttribute|renderHelpIcon|setDisabled|setHidden|formatDuration|formatDateTime)\b/);
  assert.doesNotMatch(
    reports,
    /function (escapeHtml|escapeAttribute|renderHelpIcon|setDisabled|setHidden|formatDateTime)\b/);

  for (const utilitySource of [domUtilsSource, formatUtilsSource]) {
    assert.doesNotMatch(utilitySource, /\bimport\b/);
    assert.doesNotMatch(utilitySource, /\b(board|app|document|window|location)\b/);
  }
});
