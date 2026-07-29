import { app } from "./application-state.js";

const REPORT_REFRESH_INTERVAL_MS = 60_000;
const GENERAL_RENDER_INTERVAL_MS = 1000;

export function registerReportRefresh(refreshReports) {
  window.setInterval(refreshReports, REPORT_REFRESH_INTERVAL_MS);
}

export function registerGeneralRender(render) {
  app.tickHandle = window.setInterval(render, GENERAL_RENDER_INTERVAL_MS);
}
