import { app } from "./application-state.js";

export const adminAccess = {
  storageKey: "chairside-admin-token",
  headerName: "X-ChairSide-Admin-Token"
};

const roomTokenHeaderName = "X-ChairSide-Room-Token";
const unresolvedFieldLabels = {
  doctorId: "doctor",
  procedureCode: "procedure",
  sedationChoice: "sedation",
  confirmedExpectedAllocationUnits: "allocation confirmation"
};

export function readAdminToken() {
  return sessionStorage.getItem(adminAccess.storageKey);
}

export function storeAdminToken(token) {
  sessionStorage.setItem(adminAccess.storageKey, token);
}

export function clearAdminToken() {
  sessionStorage.removeItem(adminAccess.storageKey);
}

export function adminRequestHeaders() {
  const token = readAdminToken();
  return token ? { [adminAccess.headerName]: token } : {};
}

export function mutationHeaders(baseHeaders = {}) {
  const headers = { ...baseHeaders };
  if (app.roomToken) {
    headers[roomTokenHeaderName] = app.roomToken;
  }

  return headers;
}

export async function readErrorMessage(response, fallback) {
  const text = await response.text();
  if (!text) {
    return fallback;
  }
  try {
    const error = JSON.parse(text);
    const unresolved = (error.unresolvedFields || [])
      .map(field => unresolvedFieldLabels[field] || field);
    return unresolved.length
      ? `${error.message || fallback} Still needed: ${unresolved.join(", ")}.`
      : error.message || fallback;
  } catch {
    return text;
  }
}
