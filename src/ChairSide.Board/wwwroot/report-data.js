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
  let analyticalScope = {
    scope: "Practice",
    doctorId: null,
    sedation: "All",
    procedureGrouping: "Family"
  };

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

  function getQuery() {
    return { ...analyticalScope, window: { ...dateRange } };
  }

  function getRangeSignature(range = dateRange) {
    return createReportRequestContext(range).rangeSignature;
  }

  function setDateRange(nextRange) {
    dateRange = { ...nextRange };
  }

  function setScope(scope, doctorId = null) {
    analyticalScope = {
      ...analyticalScope,
      scope: scope === "Doctor" ? "Doctor" : "Practice",
      doctorId: scope === "Doctor" && doctorId ? String(doctorId).trim() : null
    };
  }

  function setSedation(sedation) {
    analyticalScope = {
      ...analyticalScope,
      sedation: sedation === "Sedation" || sedation === "NonSedation" ? sedation : "All"
    };
  }

  function setProcedureGrouping(procedureGrouping) {
    analyticalScope = {
      ...analyticalScope,
      procedureGrouping: procedureGrouping === "DetailedVariant" ? "DetailedVariant" : "Family"
    };
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
    const requestContext = createReportRequestContext(dateRange, analyticalScope);
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
      applyNormalizedQuery(reports?.query);
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
    if (requestContext.scope !== "Practice") {
      params.set("scope", requestContext.scope);
    }
    if (requestContext.doctorId) {
      params.set("doctorId", requestContext.doctorId);
    }
    if (requestContext.sedation !== "All") {
      params.set("sedation", requestContext.sedation);
    }
    if (requestContext.procedureGrouping !== "Family") {
      params.set("procedureGrouping", requestContext.procedureGrouping);
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

  function applyNormalizedQuery(query) {
    if (!query || typeof query !== "object") {
      return;
    }

    dateRange = {
      ...dateRange,
      start: query.rangeStartDate || null,
      end: query.rangeEndDate || null
    };
    analyticalScope = {
      scope: query.scope === "Doctor" ? "Doctor" : "Practice",
      doctorId: query.scope === "Doctor" && query.doctorId ? String(query.doctorId) : null,
      sedation: query.sedation === "Sedation" || query.sedation === "NonSedation"
        ? query.sedation
        : "All",
      procedureGrouping: query.procedureGrouping === "DetailedVariant"
        ? "DetailedVariant"
        : "Family"
    };
  }

  return {
    getDateRange,
    getLastSuccessfulLoad,
    getQuery,
    getRangeSignature,
    getReports,
    getVersion,
    load,
    reload,
    reloadAfterCurrent,
    setDateRange,
    setProcedureGrouping,
    setScope,
    setSedation,
    useLastSevenDays,
    useLastThirtyDays,
    useMonthToDate,
    usePreset
  };
}

function createReportRequestContext(range, scope) {
  const allTime = range?.preset === "all";
  const from = allTime ? null : range?.start || null;
  const to = allTime ? null : range?.end || null;
  const normalizedScope = scope?.scope === "Doctor" ? "Doctor" : "Practice";
  const doctorId = normalizedScope === "Doctor" ? scope?.doctorId || null : null;
  const sedation = scope?.sedation === "Sedation" || scope?.sedation === "NonSedation"
    ? scope.sedation
    : "All";
  const procedureGrouping = scope?.procedureGrouping === "DetailedVariant"
    ? "DetailedVariant"
    : "Family";
  return Object.freeze({
    from,
    to,
    scope: normalizedScope,
    doctorId,
    sedation,
    procedureGrouping,
    rangeSignature: JSON.stringify([from, to]),
    querySignature: JSON.stringify([
      from,
      to,
      normalizedScope,
      doctorId,
      sedation,
      procedureGrouping
    ])
  });
}

function utcDateString(date) {
  return date.toISOString().slice(0, 10);
}

function computePresetRange(preset, now) {
  const today = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
  if (preset === "all") {
    return { start: null, end: null };
  }
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
