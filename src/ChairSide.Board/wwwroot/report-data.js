import {
  adminRequestHeaders,
  clearAdminToken
} from "./request-utils.js";

export function createReportData(adapter = {}) {
  let reports = null;
  let reportsInFlight = false;
  let reportsInFlightPromise = null;
  let guaranteedReloadBatch = null;
  let reportsVersion = 0;
  let lastSuccessfulLoad = null;
  let dateRange = { preset: "last7", start: null, end: null };

  function getReports() {
    return reports;
  }

  function getVersion() {
    return reportsVersion;
  }

  function getLastSuccessfulLoad() {
    return lastSuccessfulLoad;
  }

  function getDateRange() {
    return dateRange;
  }

  function getRangeSignature(range = dateRange) {
    return createReportRequestContext(range).rangeSignature;
  }

  function setDateRange(nextRange) {
    dateRange = { ...nextRange };
  }

  function useLastSevenDays() {
    usePreset("last7");
  }

  function useMonthToDate() {
    usePreset("mtd");
  }

  function useLastThirtyDays() {
    usePreset("last30");
  }

  function usePreset(preset) {
    const resolved = computePresetRange(preset, currentDate());
    dateRange = { preset, start: resolved.start, end: resolved.end };
  }

  function startLoad() {
    const requestContext = createReportRequestContext(dateRange);
    reportsInFlight = true;
    const operation = (async () => {
      const response = await request(reportsRequestUrl(requestContext), {
        cache: "no-store",
        headers: currentAdminHeaders()
      });

      if (response.status === 401 || response.status === 403) {
        if (response.status === 403) {
          clearRejectedToken();
        }

        reports = null;
        adapter.onAccessDenied?.(response.status);
        return;
      }

      if (!response.ok) {
        throw new Error(`Reports failed with HTTP ${response.status}.`);
      }

      reports = await response.json();
      reportsVersion++;
      lastSuccessfulLoad = Object.freeze({
        payload: reports,
        version: reportsVersion,
        requestContext
      });
      adapter.onDataChanged?.();
      return lastSuccessfulLoad;
    })();

    reportsInFlightPromise = operation;
    return operation.finally(() => {
      reportsInFlight = false;
      if (reportsInFlightPromise === operation) {
        reportsInFlightPromise = null;
      }
    });
  }

  async function load() {
    if (reportsInFlight) {
      return;
    }

    return startLoad();
  }

  async function reload() {
    return load();
  }

  function reloadAfterCurrent() {
    if (guaranteedReloadBatch && !guaranteedReloadBatch.started) {
      return guaranteedReloadBatch.promise;
    }

    const predecessor = guaranteedReloadBatch?.promise || Promise.resolve();
    const batch = {
      started: false,
      promise: null
    };

    batch.promise = (async () => {
      try {
        await predecessor;
      } catch {
        // The requested fresh read must still run after a failed predecessor.
      }

      batch.started = true;
      while (reportsInFlightPromise) {
        try {
          await reportsInFlightPromise;
        } catch {
          // A failed ordinary load still counts as settled for sequencing.
        }
      }

      return startLoad();
    })().finally(() => {
      if (guaranteedReloadBatch === batch) {
        guaranteedReloadBatch = null;
      }
    });

    guaranteedReloadBatch = batch;
    return batch.promise;
  }

  function reportsRequestUrl(requestContext) {
    const params = new URLSearchParams();
    if (requestContext.from) {
      params.set("from", requestContext.from);
    }
    if (requestContext.to) {
      params.set("to", requestContext.to);
    }

    const query = params.toString();
    return query ? `/api/reports?${query}` : "/api/reports";
  }

  function request(url, options) {
    return adapter.fetch
      ? adapter.fetch(url, options)
      : fetch(url, options);
  }

  function currentAdminHeaders() {
    return adapter.adminRequestHeaders
      ? adapter.adminRequestHeaders()
      : adminRequestHeaders();
  }

  function clearRejectedToken() {
    if (adapter.clearAdminToken) {
      adapter.clearAdminToken();
      return;
    }

    clearAdminToken();
  }

  function currentDate() {
    return adapter.now ? new Date(adapter.now()) : new Date();
  }

  return {
    getDateRange,
    getLastSuccessfulLoad,
    getRangeSignature,
    getReports,
    getVersion,
    load,
    reload,
    reloadAfterCurrent,
    setDateRange,
    useLastSevenDays,
    useLastThirtyDays,
    useMonthToDate,
    usePreset
  };
}

function createReportRequestContext(range) {
  const allTime = range?.preset === "all";
  const from = allTime ? null : range?.start || null;
  const to = allTime ? null : range?.end || null;
  return Object.freeze({
    from,
    to,
    rangeSignature: JSON.stringify([from, to])
  });
}

function utcDateString(date) {
  return date.toISOString().slice(0, 10);
}

function computePresetRange(preset, now) {
  const today = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
  if (preset === "today") {
    return { start: utcDateString(today), end: utcDateString(today) };
  }
  if (preset === "mtd") {
    const start = new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1));
    return { start: utcDateString(start), end: utcDateString(today) };
  }
  if (preset === "last30") {
    const start = new Date(today);
    start.setUTCDate(start.getUTCDate() - 29);
    return { start: utcDateString(start), end: utcDateString(today) };
  }

  const start = new Date(today);
  start.setUTCDate(start.getUTCDate() - 6);
  return { start: utcDateString(start), end: utcDateString(today) };
}
