import { pageContext } from "./page-context.js";

export const app = {
  snapshot: null,
  connection: null,
  hubReady: false,
  tickHandle: null,
  pollHandle: null,
  statusHandle: null,
  realtimeRetryHandle: null,
  lastSnapshotAt: 0,
  lastPollAt: 0,
  serverOffsetMs: 0,
  connectionStatus: "stale",
  realtimeDegraded: false,
  realtimeLostAt: 0,
  pollInFlight: false,
  roomNumber: pageContext.roomNumber,
  roomToken: pageContext.roomToken,
  roomTokenPromptVisible: false,
  doctorId: pageContext.doctorId,
  // True while a pointer is pressed on a doctor/procedure tile. The 1s room poll
  // defers re-syncing and re-rendering the selection tiles while this is set so a
  // slow press is never interrupted by a mid-press DOM swap.
  tilePressActive: false
};
