import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const boardUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/board.js",
  import.meta.url);
const assetRootUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/assets/procedure-icons/",
  import.meta.url);
const boardSource = await readFile(boardUrl, "utf8");
const rendererStart = boardSource.indexOf("const procedurePngAssetsByCode");
const rendererEnd = boardSource.indexOf("function hasSedationModifier");

assert.ok(rendererStart >= 0, "procedure PNG mapping was not found");
assert.ok(rendererEnd > rendererStart, "procedure renderer boundary was not found");

const rendererSource = boardSource.slice(rendererStart, rendererEnd);
const createRenderer = new Function(
  `${rendererSource}\nreturn { procedurePngAssetsByCode, renderProcedureIcon };`);
const { procedurePngAssetsByCode, renderProcedureIcon } = createRenderer();

test("approved Consult and All-on-Four codes resolve to their PNG assets", () => {
  const consult = renderProcedureIcon({ code: "CON", icon: "speech" });
  const allOnFour = renderProcedureIcon({ code: "AO4", icon: "archfour" });

  assert.match(consult, /src="\/assets\/procedure-icons\/64\/consult\.png"/);
  assert.match(allOnFour, /src="\/assets\/procedure-icons\/64\/ao4\.png"/);
});

test("unknown and custom procedures keep the supported SVG fallback", () => {
  const aliasedCustom = renderProcedureIcon({ code: "CUSTOM", icon: "speech" });
  const unknown = renderProcedureIcon({ code: "UNKNOWN", icon: "not-real" });

  assert.match(aliasedCustom, /^<svg /);
  assert.match(unknown, /^<svg /);
  assert.doesNotMatch(aliasedCustom, /<img /);
  assert.doesNotMatch(unknown, /<img /);
});

test("standalone legacy SED remains an SVG while sedation modifiers keep the base PNG", () => {
  const legacySedation = renderProcedureIcon({ code: "SED", icon: "moon" });
  const extraction = renderProcedureIcon({ code: "EXT", icon: "forceps" });
  const sedatedExtraction = renderProcedureIcon({ code: "EXT+SED", icon: "forceps" });
  const formattedSedatedExtraction = renderProcedureIcon({ code: "EXT + SED", icon: "forceps" });

  assert.match(legacySedation, /^<svg /);
  assert.doesNotMatch(legacySedation, /<img /);
  assert.equal(sedatedExtraction, extraction);
  assert.equal(formattedSedatedExtraction, extraction);
});

test("PNG markup is decorative and supplies all normalized runtime sizes", () => {
  const consult = renderProcedureIcon({ code: "CON", icon: "speech" });

  assert.match(consult, /alt=""/);
  assert.match(consult, /aria-hidden="true"/);
  assert.match(consult, /\/32\/consult\.png 32w/);
  assert.match(consult, /\/64\/consult\.png 64w/);
  assert.match(consult, /\/256\/consult\.png 256w/);
  assert.match(consult, /sizes="112px"/);
  assert.doesNotMatch(consult, /Consult/);
});

test("every approved mapping has square PNG assets at all normalized sizes", async () => {
  const entries = Object.entries(procedurePngAssetsByCode);
  assert.equal(entries.length, 15);

  for (const [, fileName] of entries) {
    for (const size of [32, 64, 256]) {
      const png = await readFile(new URL(`${size}/${fileName}`, assetRootUrl));
      assert.deepEqual([...png.subarray(0, 8)], [137, 80, 78, 71, 13, 10, 26, 10]);
      assert.equal(png.readUInt32BE(16), size);
      assert.equal(png.readUInt32BE(20), size);
    }
  }
});
