import { app } from "./application-state.js";

const staleAfterMs = 15000;
const freshPollingAfterRealtimeLossMs = 7000;
const connectionStatusRefreshCadenceMs = 1000;

const connectionStatusDescriptions = {
  live: "Board is current. Updates are being received through realtime connection or fresh polling fallback.",
  reconnecting: "Realtime connection is degraded. ChairSide is trying to reconnect.",
  stale: "No fresh board update in over 15 seconds. Refresh or check the network/server."
};

export function setConnectionStatus(status) {
  app.connectionStatus = status;
  const target = ensureConnectionStatusIndicator();
  const details = getConnectionStatusDetails(status);
  let label, ariaLabel;
  if (status === "live") {
    label = app.lastSnapshotAt
      ? new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" }).format(new Date(app.lastSnapshotAt))
      : "—";
    ariaLabel = `Live, last updated at ${label}`;
  } else {
    label = status === "reconnecting" ? "Reconnecting" : "Stale";
    ariaLabel = `${label}: ${details}`;
  }
  target.className = `connection-status ${status}`;
  target.title = details;
  target.setAttribute("aria-label", ariaLabel);
  target.querySelector("span").textContent = label;
}

export function updateConnectionStatus() {
  const snapshotAgeMs = app.lastSnapshotAt ? Date.now() - app.lastSnapshotAt : Number.POSITIVE_INFINITY;
  const pollingFreshAfterRealtimeLoss = app.realtimeDegraded
    && app.lastPollAt > app.realtimeLostAt
    && Date.now() - app.lastPollAt <= freshPollingAfterRealtimeLossMs;

  if (snapshotAgeMs > staleAfterMs) {
    setConnectionStatus("stale");
    return;
  }

  if (app.hubReady || pollingFreshAfterRealtimeLoss || !app.realtimeDegraded) {
    setConnectionStatus("live");
    return;
  }

  setConnectionStatus("reconnecting");
}

export function registerConnectionStatusRefresh() {
  app.statusHandle = window.setInterval(updateConnectionStatus, connectionStatusRefreshCadenceMs);
}

function ensureConnectionStatusIndicator() {
  let target = document.getElementById("connectionStatus");
  if (target) {
    return target;
  }

  const header = document.querySelector(".app-header") || document.body;
  target = document.createElement("div");
  target.id = "connectionStatus";
  target.className = "connection-status stale";
  target.setAttribute("role", "status");
  target.setAttribute("aria-live", "polite");
  target.innerHTML = `<i aria-hidden="true"></i><span>Stale</span>`;
  header.appendChild(target);
  return target;
}

function formatSnapshotAge(ageMs) {
  if (ageMs < 1000) {
    return `${Math.round(ageMs)} ms ago`;
  }

  return `${(ageMs / 1000).toFixed(1)} seconds ago`;
}

function getConnectionStatusDetails(status) {
  const description = connectionStatusDescriptions[status] || "";
  if (!app.lastSnapshotAt) {
    return `${description}\n\nLast updated: never\nAge: unavailable`;
  }

  const ageMs = Math.max(0, Date.now() - app.lastSnapshotAt);
  const lastUpdated = new Date(app.lastSnapshotAt).toLocaleTimeString([], {
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit"
  });

  return `${description}\n\nLast updated: ${lastUpdated}\nAge: ${formatSnapshotAge(ageMs)}`;
}
