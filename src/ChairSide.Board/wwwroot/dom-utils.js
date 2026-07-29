export function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

export function escapeAttribute(value) {
  return escapeHtml(value);
}

// Reusable inline help bubble: a small "?" badge that reveals a short explanation on hover or
// keyboard focus. aria-label carries the full text so screen readers announce it on focus without
// needing a separate aria-describedby wire-up.
export function renderHelpIcon(helpText, placement) {
  const text = escapeHtml(helpText);
  const modifier = placement === "corner" ? " help-icon--corner" : "";
  return `<span class="help-icon${modifier}" tabindex="0" aria-label="Help: ${text}">
    <span aria-hidden="true">?</span>
    <span class="help-icon-bubble" aria-hidden="true">${text}</span>
  </span>`;
}

export function setDisabled(control, isDisabled) {
  if (control) {
    control.disabled = isDisabled;
  }
}

export function setHidden(control, isHidden) {
  if (control) {
    control.hidden = isHidden;
  }
}
