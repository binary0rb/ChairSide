import { app } from "./application-state.js";

const retryDelayMs = 5000;
const boardPollingCadenceMs = 5000;

export function connectRealtime(callbacks) {
  if (!window.signalR) {
    markRealtimeDegraded();
    callbacks.refreshConnectionStatus();
    return;
  }

  if (app.hubReady
    || app.connection?.state === "Connected"
    || app.connection?.state === "Connecting"
    || app.connection?.state === "Reconnecting") {
    return;
  }

  const connection = new window.signalR.HubConnectionBuilder()
    .withUrl("/boardHub")
    .withAutomaticReconnect()
    .build();

  app.connection = connection;

  connection.on("boardUpdated", async snapshot => {
    callbacks.applySnapshot(snapshot);
    if (callbacks.refreshReportsAfterBoardUpdate) {
      await callbacks.refreshReportsAfterBoardUpdate().catch(error => {
        console.warn("[ChairSide] Reports refresh after board update failed.", error);
      });
    }
    callbacks.render();
    callbacks.refreshConnectionStatus();
  });

  if (typeof connection.onreconnecting === "function") {
    connection.onreconnecting(() => {
      markRealtimeDegraded();
      callbacks.refreshConnectionStatus();
    });
  }

  if (typeof connection.onreconnected === "function") {
    connection.onreconnected(() => {
      app.hubReady = true;
      app.realtimeDegraded = false;
      app.realtimeLostAt = 0;
      callbacks.setConnectionStatus("live");
      callbacks.loadBoard();
    });
  }

  if (typeof connection.onclose === "function") {
    connection.onclose(() => {
      markRealtimeDegraded();
      callbacks.refreshConnectionStatus();
    });
  }

  connection.start()
    .then(() => {
      app.hubReady = true;
      app.realtimeDegraded = false;
      app.realtimeLostAt = 0;
      callbacks.setConnectionStatus("live");
    })
    .catch(error => {
      console.warn("[ChairSide] SignalR connection failed; polling fallback remains active.", error);
      markRealtimeDegraded();
      callbacks.refreshConnectionStatus();
      scheduleRealtimeRetry(callbacks);
    });
}

export function registerBoardPolling(loadBoard) {
  app.pollHandle = window.setInterval(loadBoard, boardPollingCadenceMs);
}

function markRealtimeDegraded() {
  app.hubReady = false;
  app.realtimeDegraded = true;
  if (!app.realtimeLostAt) {
    app.realtimeLostAt = Date.now();
  }
}

function scheduleRealtimeRetry(callbacks) {
  if (app.realtimeRetryHandle) {
    return;
  }

  app.realtimeRetryHandle = window.setTimeout(() => {
    app.realtimeRetryHandle = null;
    if (!app.hubReady) {
      connectRealtime(callbacks);
    }
  }, retryDelayMs);
}
