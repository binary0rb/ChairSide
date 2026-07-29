import { pageContext } from "./page-context.js";

export const app = {
  snapshot: null,
  reports: null,
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
  reportsInFlight: false,
  // Hybrid reports filter state. Kept in app (not the DOM) so a SignalR/poll re-render
  // never resets the user's selected filters. sedation: all | sedation | non-sedation.
  // grouping: base | variant.
  reportFilters: { sedation: "all", grouping: "base" },
  reportDoctorId: null,
  reportDoctorTab: "overview",
  // Report date window. Drives the backend completed-cycle filter, so changing it reloads from the
  // API. start/end are ISO yyyy-MM-dd (null = unbounded). Default preset is Last 7 days.
  dateRange: { preset: "last7", start: null, end: null },
  roomNumber: pageContext.roomNumber,
  roomToken: pageContext.roomToken,
  roomTokenPromptVisible: false,
  doctorId: pageContext.doctorId,
  selectedDoctorId: null,
  selectedProcedureId: null,
  sedationOn: false,
  // Expected allocation (1 unit = 10 minutes). Kept in app (not the DOM) so a SignalR/poll
  // re-render never discards an in-progress staff adjustment. expectedUnitsManual tracks whether
  // staff have changed units since selecting the current procedure: a procedure change always
  // re-seeds from the new default, but a sedation change only re-seeds when not manually adjusted.
  expectedUnits: null,
  expectedUnitsManual: false,
  expectedUnitsConfirmed: false,
  expectedUnitsProcedureCode: null,
  expectedUnitsSedation: false,
  persistedAssignmentSignature: "",
  selectionContext: null,
  // True while a pointer is pressed on a doctor/procedure tile. The 1s room poll
  // defers re-syncing and re-rendering the selection tiles while this is set so a
  // slow press is never interrupted by a mid-press DOM swap.
  tilePressActive: false,
  // True while a pointer is pressed on an interactive reports-page element (doctor card,
  // tab button, or table action button). Defers innerHTML writes for those regions so a
  // mid-press DOM swap cannot drop the click. Mirrors the tilePressActive pattern.
  reportPressActive: false,
  // Monotonically incremented each time loadReports() stores a new payload. Used as the
  // data-identity component of render tokens so guarded renders can skip innerHTML writes
  // on 1-second ticks where app.reports hasn't changed.
  reportsVersion: 0
};
