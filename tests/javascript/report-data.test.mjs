import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const moduleUrl = new URL(
  "../../src/ChairSide.Board/wwwroot/report-data.js",
  import.meta.url);
const moduleSource = await readFile(moduleUrl, "utf8");
const requestUtilsDataUrl = "data:text/javascript,export%20function%20adminRequestHeaders()%7Breturn%20%7B%7D%7D%3Bexport%20function%20clearAdminToken()%7B%7D";
const moduleWithDataImport = moduleSource.replace(
  '"./request-utils.js"',
  JSON.stringify(requestUtilsDataUrl));
const moduleDataUrl = `data:text/javascript;base64,${Buffer.from(moduleWithDataImport).toString("base64")}`;
const { createReportData } = await import(moduleDataUrl);

const fixedNow = "2026-07-29T18:45:00.000Z";

function response(status, payload = null) {
  return {
    status,
    ok: status >= 200 && status < 300,
    json: async () => payload
  };
}

function createHarness({
  fetch = async () => response(200, { marker: "reports" })
} = {}) {
  const calls = [];
  const access = [];
  const changes = [];
  let clears = 0;
  const adapter = {
    now: () => fixedNow,
    adminRequestHeaders: () => ({ "X-ChairSide-Admin-Token": "admin-token" }),
    clearAdminToken: () => {
      clears += 1;
    },
    fetch: async (url, options) => {
      calls.push({ url, options });
      return fetch(url, options);
    },
    onAccessDenied: status => access.push(["initial", status]),
    onDataChanged: () => changes.push("initial")
  };

  return {
    access,
    adapter,
    calls,
    changes,
    controller: createReportData(adapter),
    get clearCount() {
      return clears;
    }
  };
}

test("UTC presets and custom ranges preserve the established date boundaries", () => {
  const { controller } = createHarness();

  controller.useLastSevenDays();
  assert.deepEqual(controller.getDateRange(), {
    preset: "last7",
    start: "2026-07-23",
    end: "2026-07-29"
  });

  controller.useMonthToDate();
  assert.deepEqual(controller.getDateRange(), {
    preset: "mtd",
    start: "2026-07-01",
    end: "2026-07-29"
  });

  controller.useLastThirtyDays();
  assert.deepEqual(controller.getDateRange(), {
    preset: "last30",
    start: "2026-06-30",
    end: "2026-07-29"
  });

  controller.setDateRange({
    preset: "custom",
    start: "2026-08-12",
    end: "2026-07-04"
  });
  assert.deepEqual(controller.getDateRange(), {
    preset: "custom",
    start: "2026-08-12",
    end: "2026-07-04"
  });
});

test("report GET preserves URL, admin headers, no-store cache, payload replacement, and versioning", async () => {
  const payloads = [{ marker: "first" }, { marker: "second" }];
  const harness = createHarness({
    fetch: async () => response(200, payloads.shift())
  });
  harness.controller.setDateRange({
    preset: "custom",
    start: "2026-07-09",
    end: "2026-07-21"
  });

  await harness.controller.load();
  assert.deepEqual(harness.calls[0], {
    url: "/api/reports?from=2026-07-09&to=2026-07-21",
    options: {
      cache: "no-store",
      headers: { "X-ChairSide-Admin-Token": "admin-token" }
    }
  });
  assert.deepEqual(harness.controller.getReports(), { marker: "first" });
  assert.equal(harness.controller.getVersion(), 1);
  assert.deepEqual(harness.changes, ["initial"]);

  harness.controller.setDateRange({
    preset: "custom",
    start: null,
    end: "2026-07-21"
  });
  await harness.controller.reload();
  assert.equal(harness.calls[1].url, "/api/reports?to=2026-07-21");
  assert.deepEqual(harness.controller.getReports(), { marker: "second" });
  assert.equal(harness.controller.getVersion(), 2);
});

test("in-flight suppression shares one request and completion permits the next load", async () => {
  let resolveRequest;
  const deferred = new Promise(resolve => {
    resolveRequest = resolve;
  });
  const harness = createHarness({
    fetch: async () => deferred
  });

  const first = harness.controller.load();
  const suppressed = harness.controller.load();
  assert.equal(harness.calls.length, 1);
  assert.equal(await suppressed, undefined);

  resolveRequest(response(200, { marker: "resolved" }));
  await first;
  assert.deepEqual(harness.controller.getReports(), { marker: "resolved" });

  harness.adapter.fetch = async () => response(200, { marker: "next" });
  await harness.controller.load();
  assert.equal(harness.calls.length, 1);
  assert.deepEqual(harness.controller.getReports(), { marker: "next" });
});

test("401 and 403 preserve access signaling and rejected-token clearing distinction", async () => {
  const statuses = [401, 403];
  const harness = createHarness({
    fetch: async () => response(statuses.shift())
  });

  await harness.controller.load();
  assert.deepEqual(harness.access, [["initial", 401]]);
  assert.equal(harness.clearCount, 0);
  assert.equal(harness.controller.getReports(), null);
  assert.equal(harness.controller.getVersion(), 0);

  await harness.controller.load();
  assert.deepEqual(harness.access, [
    ["initial", 401],
    ["initial", 403]
  ]);
  assert.equal(harness.clearCount, 1);
  assert.equal(harness.controller.getReports(), null);
  assert.equal(harness.controller.getVersion(), 0);
});

test("generic failure releases in-flight state without replacing data or version", async () => {
  let attempts = 0;
  const harness = createHarness({
    fetch: async () => {
      attempts += 1;
      return attempts === 1
        ? response(500)
        : response(200, { marker: "recovered" });
    }
  });

  await assert.rejects(
    harness.controller.load(),
    /Reports failed with HTTP 500\./);
  assert.equal(harness.controller.getReports(), null);
  assert.equal(harness.controller.getVersion(), 0);

  await harness.controller.load();
  assert.equal(attempts, 2);
  assert.deepEqual(harness.controller.getReports(), { marker: "recovered" });
  assert.equal(harness.controller.getVersion(), 1);
});

test("controller invokes live adapter callbacks instead of construction-time copies", async () => {
  const harness = createHarness({
    fetch: async () => response(200, { marker: "live" })
  });
  harness.adapter.onDataChanged = () => harness.changes.push("replacement");
  harness.adapter.onAccessDenied = status => harness.access.push(["replacement", status]);

  await harness.controller.load();
  assert.deepEqual(harness.changes, ["replacement"]);

  harness.adapter.fetch = async () => response(401);
  await harness.controller.reload();
  assert.deepEqual(harness.access, [["replacement", 401]]);
});

test("module owns transport state without importing presentation or application authorities", () => {
  assert.match(
    moduleSource,
    /^import \{\s*adminRequestHeaders,\s*clearAdminToken\s*\} from "\.\/request-utils\.js";/);
  assert.doesNotMatch(
    moduleSource,
    /from "\.\/(board|reports|workshop|application-state|page-context|room-card|room-workflow)\.js"/);
  assert.doesNotMatch(moduleSource, /\b(document|renderReports|renderDoctorCockpit)\b/);
  assert.match(moduleSource, /cache: "no-store"/);
  assert.match(moduleSource, /reportsVersion\+\+/);
});
