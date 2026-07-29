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
