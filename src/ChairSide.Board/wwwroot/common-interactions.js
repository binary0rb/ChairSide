import { app } from "./application-state.js";

const REPORT_PRESS_FAILSAFE_MS = 3000;
const TILE_PRESS_FAILSAFE_MS = 4000;

let reportPressFailsafe = null;
let pendingTilePress = null;
let tilePressFailsafe = null;

function clearReportPress(onCatchUp) {
  window.clearTimeout(reportPressFailsafe);
  reportPressFailsafe = null;
  if (!app.reportPressActive) {
    return;
  }

  app.reportPressActive = false;
  onCatchUp();
}

export function wirePressInterruptionGuard({
  pressTarget,
  selector,
  onCatchUp
}) {
  pressTarget.addEventListener("pointerdown", event => {
    if (!event.target.closest(selector)) {
      return;
    }

    app.reportPressActive = true;
    window.clearTimeout(reportPressFailsafe);
    reportPressFailsafe = window.setTimeout(() => {
      app.reportPressActive = false;
      reportPressFailsafe = null;
    }, REPORT_PRESS_FAILSAFE_MS);
  });

  const releasePress = () => clearReportPress(onCatchUp);
  document.addEventListener("pointerup", releasePress);
  document.addEventListener("pointercancel", releasePress);
}

function clearTilePress() {
  pendingTilePress = null;
  app.tilePressActive = false;
  if (tilePressFailsafe !== null) {
    window.clearTimeout(tilePressFailsafe);
    tilePressFailsafe = null;
  }
}

function completeTilePress(event) {
  const press = pendingTilePress;
  clearTilePress();
  if (!press) {
    return;
  }

  const button = event.target?.closest?.(press.selector);
  if (!button || button.disabled || button.dataset[press.idKey] !== press.id) {
    return;
  }

  press.activate(button);
}

export function wireTileGroup(container, selector, idKey, activate) {
  if (!container) {
    return;
  }

  container.addEventListener("pointerdown", event => {
    const button = event.target.closest(selector);
    if (!button || button.disabled) {
      return;
    }

    pendingTilePress = { selector, idKey, id: button.dataset[idKey], activate };
    app.tilePressActive = true;
    if (tilePressFailsafe !== null) {
      window.clearTimeout(tilePressFailsafe);
    }
    tilePressFailsafe = window.setTimeout(clearTilePress, TILE_PRESS_FAILSAFE_MS);
  });

  container.addEventListener("click", event => {
    if (event.detail !== 0) {
      return;
    }

    const button = event.target.closest(selector);
    if (!button || button.disabled) {
      return;
    }

    activate(button);
  });
}

export function wireTilePressCleanup(onEscape) {
  document.addEventListener("pointerup", completeTilePress);
  document.addEventListener("pointercancel", clearTilePress);
  window.addEventListener("blur", clearTilePress);
  document.addEventListener("keydown", event => {
    if (event.key !== "Escape") {
      return;
    }

    clearTilePress();
    onEscape();
  });
}
