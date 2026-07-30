import {
  adminRequestHeaders,
  clearAdminToken
} from "./request-utils.js";

export function createReportData(adapter = {}) {
  let reports = null;
  let reportsInFlight = false;
  let reportsVersion = 0;
  let dateRange = { preset: "last7", start: null, end: null };

  function getReports() {
    return reports;
  }

  function getVersion() {
    return reportsVersion;
  }

  function getDateRange() {
    return dateRange;
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

  async function load() {
    if (reportsInFlight) {
      return;
    }

    reportsInFlight = true;
    try {
      const response = await request(reportsRequestUrl(), {
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
      adapter.onDataChanged?.();
    } finally {
      reportsInFlight = false;
    }
  }

  async function reload() {
    return load();
  }

  function reportsRequestUrl() {
    const range = dateRange;
    if (!range || range.preset === "all") {
      return "/api/reports";
    }

    const params = new URLSearchParams();
    if (range.start) {
      params.set("from", range.start);
    }
    if (range.end) {
      params.set("to", range.end);
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
    getReports,
    getVersion,
    load,
    reload,
    setDateRange,
    useLastSevenDays,
    useLastThirtyDays,
    useMonthToDate,
    usePreset
  };
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
