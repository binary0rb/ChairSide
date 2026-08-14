import { wirePressInterruptionGuard } from "./common-interactions.js";
import { escapeAttribute, escapeHtml, renderHelpIcon } from "./dom-utils.js";
import { formatDateTime, formatDuration } from "./format-utils.js";
import {
  adminRequestHeaders,
  clearAdminToken,
  readErrorMessage,
  storeAdminToken
} from "./request-utils.js";

const trendAboutSameThresholdSeconds = 60;
const MAX_UNRESOLVED_REPORT_ACTIONS = 10;
const REPORT_ACTION_CAPACITY_KEY = "report-action-capacity";

export function createReports({
  context,
  reportData,
  getSnapshot,
  renderPage,
  getDoctorName,
  getDoctorIdentity,
  procedure,
  request = (...args) => fetch(...args)
}) {
  const state = {
    reportFilters: { sedation: "All", grouping: "Family" },
    reportDoctorId: null,
    reportScopeDoctors: new Map(),
    reportDoctorTab: "overview",
    reportPressActive: false,
    procedureIntelligenceExpanded: new Set(),
    auditViews: new Map()
  };
  const reportActionStates = new Map();
  const reportActionElements = new Map();
  let nextReportActionOperationId = 0;
  let reportActionCapacityVisible = false;

  async function selectDateRangePreset(preset) {
  clearCompletedReportAction();
  if (preset === "custom") {
    reportData.setDateRange({ ...reportData.getDateRange(), preset: "custom" });
    syncDateRangeControls();
    return; // wait for explicit Apply
  }

  if (preset === "all") {
    reportData.setDateRange({ preset: "all", start: null, end: null });
  } else {
    reportData.usePreset(preset);
  }

  syncDateRangeControls();
  await reportData.reloadAfterCurrent();
}

  async function applyCustomDateRange() {
  const startInput = document.getElementById("reportRangeStart");
  const endInput = document.getElementById("reportRangeEnd");
  const start = startInput && startInput.value ? startInput.value : null;
  const end = endInput && endInput.value ? endInput.value : null;
  if (!start && !end) {
    return; // nothing to apply; leave current window
  }

  clearCompletedReportAction();
  reportData.setDateRange({ preset: "custom", start, end });
  syncDateRangeControls();
  await reportData.reloadAfterCurrent();
}

// Reflects reportData.getDateRange() onto the static controls so a re-render never desyncs the chips/inputs.
  function syncDateRangeControls() {
  document.querySelectorAll(".report-range-chip").forEach(chip => {
    const active = chip.dataset.rangePreset === reportData.getDateRange().preset;
    chip.classList.toggle("is-active", active);
    chip.setAttribute("aria-pressed", String(active));
  });

  const custom = document.getElementById("reportRangeCustom");
  if (custom) {
    custom.hidden = reportData.getDateRange().preset !== "custom";
  }

  if (reportData.getDateRange().preset === "custom") {
    const startInput = document.getElementById("reportRangeStart");
    const endInput = document.getElementById("reportRangeEnd");
    if (startInput && reportData.getDateRange().start) {
      startInput.value = reportData.getDateRange().start;
    }
    if (endInput && reportData.getDateRange().end) {
      endInput.value = reportData.getDateRange().end;
    }
  }
}

  function wireDateRange() {
  const container = document.getElementById("reportDateRange");
  if (!container) {
    return;
  }

  container.addEventListener("click", event => {
    const chip = event.target.closest(".report-range-chip");
    if (chip) {
      selectDateRangePreset(chip.dataset.rangePreset);
      return;
    }

    if (event.target.closest("#reportRangeApply")) {
      applyCustomDateRange();
    }
  });

  syncDateRangeControls();
}

// Plain-English window label, using the server's range metadata and all-time context.
  function renderReportWindow(r) {
  const el = document.getElementById("reportRangeWindow");
  if (!el) {
    return;
  }

  const label = r && r.rangeLabel ? r.rangeLabel : "All time";
  const total = r ? (r.totalCompletedCycleCount ?? 0) : 0;
  const query = r?.query;
  const scopeLabel = query?.scope === "Doctor"
    ? `Doctor: ${query.doctorId ? getDoctorName(query.doctorId) : "Not selected"}`
    : "Practice";
  const sedationLabel = query?.sedation === "Sedation"
    ? "Sedation"
    : query?.sedation === "NonSedation"
      ? "Non-sedation"
      : "All sedation states";
  const groupingLabel = query?.procedureGrouping === "DetailedVariant"
    ? "Detailed variant"
    : "Procedure family";
  const scopeContext = query
    ? ` Scope: ${scopeLabel}; ${sedationLabel}. Grouping: ${groupingLabel}.`
    : "";
  if (label === "All time") {
    el.textContent = `Showing all completed cases (${total} total).${scopeContext}`;
    return;
  }

  const shown = r ? (r.completedRoomCyclesCount ?? 0) : 0;
  el.textContent = `Showing completed cases from ${label} (${shown} of ${total} all-time).${scopeContext}`;
}

  function renderDoctorCockpit(doctor) {
  state.reportDoctorId = doctor.id;
  const r = reportData.getReports();
  if (!r) {
    const unavailableCockpit = document.getElementById("doctorCockpit");
    if (unavailableCockpit) {
      unavailableCockpit.hidden = true;
    }
    return;
  }
  const cockpit = document.getElementById("doctorCockpit");
  if (!cockpit) {
    return;
  }

  const allDoctors = canonicalDoctorFlowSummaries(r);
  const summary = allDoctors.find(item => item.doctorId === doctor.id)
    || emptyDoctorFlowSummary(doctor.id, doctor.name);
  const identity = getDoctorIdentity(doctor.id, doctor.name);

  cockpit.hidden = false;

  // Non-interactive summary card. Token guards against rewriting it on every 1-second tick
  // when nothing has changed. The panel below has its own token guard via renderSelectedDoctorPanel.
  const contextCard = document.getElementById("doctorContextCard");
  if (contextCard) {
    const cardToken = `${reportData.getVersion()}|${doctor.id}`;
    if (contextCard.dataset.renderKey !== cardToken) {
      contextCard.dataset.renderKey = cardToken;
      const rangeSuffix = r.rangeLabel ? ` · ${r.rangeLabel}` : "";
      contextCard.innerHTML = `
        <p class="doctor-cockpit-range-label">Reporting range: Month to date${escapeHtml(rangeSuffix)}</p>
        <article class="doctor-report-card is-selected is-panel-summary"
          style="--doctor-color: ${escapeAttribute(identity.color)}"
          aria-label="${escapeAttribute(`${doctor.name} - observed clinical flow summary`)}">
          ${renderDoctorFlowCardBody(summary, doctor.name, identity)}
        </article>`;
    }
  }

  // Full selected-doctor detail panel (head + tabs + tab content) reused directly.
  // state.reportDoctorId was pinned to doctor.id in renderDoctorView, so the panel's
  // internal doctors.find(item => item.doctorId === state.reportDoctorId) always resolves.
  renderSelectedDoctorPanel(r, [summary]);
}

  function renderReports() {
  if (!reportData.getReports()) {
    return;
  }

  const r = reportData.getReports();
  if (r.query) {
    state.reportFilters.sedation = r.query.sedation || "All";
    state.reportFilters.grouping = r.query.procedureGrouping || "Family";
  }
  if (r.query?.doctorId) {
    state.reportDoctorId = r.query.doctorId;
  }
  const hasData = (r.completedRoomCyclesCount || 0) > 0;

  renderReportActionFeedback();
  revealReportDisclosures();
  renderReportWindow(r);
  syncDateRangeControls();
  renderReportHeadline(r, hasData);
  renderReportTrendCards(r);
  renderDoctorReportDashboard(r, hasData);
  syncReportFilterButtons();
  renderReportFilterBar();
  renderProcedureMix(r);
  renderProcedureIntelligence(r);
  renderAllocationReports(r);
  renderAuditEvidence(r);
  renderReviewEvidence(r);
  renderGroupedInsights(r, hasData);
  renderFullMetrics(r, hasData);

  const exceptionCycles = r.exceptionReviewRecords || r.exceptionCycles || [];
  const compatibilityCycles = r.recentCompletedCycles || [];
  const focusTransfer = captureReportActionFocusBeforeRender(
    compatibilityCycles,
    exceptionCycles);
  // Compatibility renderers remain callable for older embedded markup, but the canonical Reports
  // document no longer contains these capped-table targets.
  renderCompletedCycles(compatibilityCycles);
  renderExceptionCycles(exceptionCycles);
  if (focusTransfer) {
    queueMicrotask(() => completeReportActionFocusAfterRender(focusTransfer));
  }
  renderProcedureSummaries(r.procedureSummaries || []);
}

// Headline band: curated cards when data exists, friendly empty-state when not.
  function renderReportHeadline(r, hasData) {
  const headline = document.getElementById("reportHeadline");
  if (!headline) {
    return;
  }

  if (!hasData) {
    headline.classList.add("is-empty");
    headline.innerHTML = `
      <article class="report-empty-state">
        <h2>No observation</h2>
        <p>Operational metrics will appear here as rooms complete their cycle. Exceptions and audit detail remain available below.</p>
      </article>
    `;
    return;
  }

  headline.classList.remove("is-empty");
  const doctorScope = r.query?.scope === "Doctor";
  const completedCount = doctorScope
    ? (r.includedCompletedCycleCount ?? 0)
    : (r.completedRoomCyclesCount ?? 0);
  const completedSample = doctorScope
    ? r.samples?.includedCompletedCases
    : r.samples?.completedCases;
  headline.innerHTML = [
    renderHeadlineCard("Completed Cases", String(completedCount), null, completedSample, doctorScope ? "IncludedCompletedCases" : "PracticeCompletedCases"),
    renderHeadlineCard("Median Ready Wait", formatObservedDuration(r.medianReadyToDoctorSeconds), "Accepted Ready to Doctor Arrived.", r.samples?.readyWait, "ReadyWait"),
    renderHeadlineCard("Median Seated -> Doctor", formatObservedDuration(r.medianSeatedToDoctorSeconds), "Total observed interval from Seated to Doctor Arrived.", r.samples?.seatedToDoctor, "SeatedToDoctor"),
    renderHeadlineCard("Median Turnover", formatObservedDuration(r.medianTurnoverSeconds), "Doctor Complete to Room Available.", r.samples?.turnover, "Turnover")
  ].join("");
}

  function renderHeadlineCard(label, value, helpText, sample = null, contributorKind = null) {
  const presentation = sampledPresentation(sample, value);
  return `
    <article class="metric-card headline-card">
      <span>${escapeHtml(label)}</span>${helpText ? renderHelpIcon(helpText) : ""}
      <strong>${escapeHtml(presentation.value)}</strong>
      ${renderSampleContext(presentation)}
      ${contributorKind ? renderAuditAction(contributorKind, `View cases contributing to ${label}`) : ""}
    </article>
  `;
}

  function sampledPresentation(sample, measuredValue) {
  if (!sample) {
    return { value: String(measuredValue), state: null, detail: "" };
  }

  if (sample.state === "Empty") {
    return { value: "No observation", state: "Empty", detail: "N=0" };
  }
  if (sample.state === "Unavailable") {
    return {
      value: "Unavailable",
      state: "Unavailable",
      detail: `0 of ${sample.populationCount || 0} contributors`
    };
  }

  return {
    value: String(measuredValue),
    state: sample.state,
    detail: `N=${sample.contributingCount || 0}`
  };
}

  function renderSampleContext(presentation) {
  if (!presentation.state) {
    return "";
  }
  return `<small class="metric-sample metric-sample--${escapeAttribute(presentation.state.toLowerCase())}">${escapeHtml(`${presentation.state} - ${presentation.detail}`)}</small>`;
}

  function renderReportTrendCards(r) {
  const panel = document.getElementById("reportTrendPanel");
  if (!panel) {
    return;
  }

  panel.hidden = false;
  panel.innerHTML = [
    renderReadyWaitTrendCard(r),
    renderTurnoverTrendCard(r),
    renderSeatedToDoctorTrendCard(r)
  ].join("");
}

  function renderReadyWaitTrendCard(r) {
  const buckets = chronologicalTrendBuckets(r?.trends?.buckets);
  const latest = buckets[buckets.length - 1];

  if (!latest) {
    return `
      <article class="report-card report-trend-card ready-wait-trend-card is-primary is-empty">
        <div>
          <span class="layer-pill layer-pill--population">Ready Wait Trend</span>
          <h2>Ready Wait trend</h2>
          <p>No observation.</p>
        </div>
        <p class="report-trend-note">Weekly median accepted Ready to Doctor Arrived waits will appear here as completed room cycles accumulate.</p>
      </article>
    `;
  }

  const previous = buckets.length > 1 ? buckets[buckets.length - 2] : null;
  const comparison = describeTrendComparison(latest, previous, {
    sampleField: "readyWaitSample",
    medianField: "medianReadyWaitSeconds",
    noPreviousText: "Not enough prior trend data for a week-to-week comparison yet.",
    lowSampleText: "Comparison is not shown unless both weekly Ready Wait samples are Sufficient.",
    missingText: "Ready Wait comparison is unavailable for these weekly buckets.",
    aboutSameText: "Median Ready Wait was about the same as the previous week.",
    improvedPrefix: "Median Ready Wait decreased by",
    increasedPrefix: "Median Ready Wait increased by",
    comparisonSuffix: "compared with the previous week."
  });

  return renderTrendCard({
    title: "Ready Wait trend",
    eyebrow: "Ready Wait Trend",
    description: "Median accepted Ready to Doctor Arrived for the latest weekly bucket.",
    value: formatTrendMinutes(latest.medianReadyWaitSeconds),
    latest,
    previous,
    countField: "readyWaitCycleCount",
    sampleField: "readyWaitSample",
    countLabel: "Ready Wait contributors",
    comparisonLabel: "Previous weekly bucket",
    comparison,
    cardClass: "ready-wait-trend-card is-primary"
  });
}

  function renderTurnoverTrendCard(r) {
  const buckets = chronologicalTrendBuckets(r?.trends?.buckets);
  const latest = buckets[buckets.length - 1];

  if (!latest) {
    return `
      <article class="report-card report-trend-card turnover-trend-card is-primary is-empty">
        <div>
          <span class="layer-pill layer-pill--population">Turnover Trend</span>
          <h2>Turnover trend</h2>
          <p>No observation.</p>
        </div>
        <p class="report-trend-note">Weekly median room reset / handoff flow will appear here as completed room cycles accumulate.</p>
      </article>
    `;
  }

  const previous = buckets.length > 1 ? buckets[buckets.length - 2] : null;
  const comparison = describeTrendComparison(latest, previous, {
    countField: "turnoverCycleCount",
    sampleField: "turnoverSample",
    medianField: "medianTurnoverSeconds",
    noPreviousText: "Not enough prior turnover trend data for a week-to-week comparison yet.",
    lowSampleText: "Comparison is not shown unless both weekly Turnover samples are Sufficient.",
    missingText: "Turnover comparison is unavailable for these weekly buckets.",
    aboutSameText: "Median Turnover was about the same as the previous week.",
    improvedPrefix: "Median Turnover decreased by",
    increasedPrefix: "Median turnover increased by",
    comparisonSuffix: "compared with the previous week."
  });

  return renderTrendCard({
    title: "Turnover trend",
    eyebrow: "Turnover Trend",
    description: "Median room reset / handoff flow for the latest weekly bucket.",
    value: formatTrendMinutes(latest.medianTurnoverSeconds),
    latest,
    previous,
    countField: "turnoverCycleCount",
    sampleField: "turnoverSample",
    countLabel: "Turnover cases in bucket",
    comparisonLabel: "Previous weekly bucket",
    comparison,
    cardClass: "turnover-trend-card is-primary"
  });
}

  function renderSeatedToDoctorTrendCard(r) {
  const buckets = chronologicalTrendBuckets(r?.trends?.buckets);
  const latest = buckets[buckets.length - 1];

  if (!latest) {
    return `
      <article class="report-card report-trend-card seated-to-doctor-trend-card is-secondary is-empty">
        <div>
          <span class="layer-pill layer-pill--population">Seated -> Doctor Trend</span>
          <h2>Seated -> Doctor trend</h2>
          <p>No observation.</p>
        </div>
        <p class="report-trend-note">Weekly median Seated to Doctor Arrived intervals will appear here as completed room cycles accumulate.</p>
      </article>
    `;
  }

  const previous = buckets.length > 1 ? buckets[buckets.length - 2] : null;
  const comparison = describeTrendComparison(latest, previous, {
    sampleField: "seatedToDoctorSample",
    fallbackSampleField: "completedSample",
    medianField: "medianSeatedToDoctorSeconds",
    noPreviousText: "Not enough prior trend data for a week-to-week comparison yet.",
    lowSampleText: "Comparison is not shown unless both weekly Seated -> Doctor samples are Sufficient.",
    missingText: "Seated -> Doctor comparison is unavailable for these weekly buckets.",
    aboutSameText: "Median Seated -> Doctor was about the same as the previous week.",
    improvedPrefix: "Median Seated -> Doctor decreased by",
    increasedPrefix: "Median Seated -> Doctor increased by",
    comparisonSuffix: "compared with the previous week."
  });

  return renderTrendCard({
    title: "Seated -> Doctor trend",
    eyebrow: "Seated -> Doctor Trend",
    description: "Median Seated to Doctor Arrived for the latest weekly bucket.",
    value: formatTrendMinutes(latest.medianSeatedToDoctorSeconds),
    latest,
    previous,
    countField: "completedCycleCount",
    sampleField: "seatedToDoctorSample",
    fallbackSampleField: "completedSample",
    countLabel: "Seated -> Doctor contributors",
    comparisonLabel: "Previous weekly bucket",
    comparison,
    cardClass: "seated-to-doctor-trend-card is-secondary"
  });
}

  function renderTrendCard(options) {
  const latestRange = formatTrendBucketRange(options.latest);
  const previousRange = options.previous ? formatTrendBucketRange(options.previous) : "";
  const presentation = sampledPresentation(
    trendSample(options.latest, options.sampleField, options.fallbackSampleField),
    options.value);
  return `
    <article class="report-card report-trend-card ${escapeAttribute(options.cardClass || "")}">
      <div class="report-trend-header">
        <div>
          <span class="layer-pill layer-pill--population">${escapeHtml(options.eyebrow)}</span>
          <h2>${escapeHtml(options.title)}</h2>
          <p>${escapeHtml(options.description)}</p>
        </div>
        <div class="report-trend-result">
          <strong class="report-trend-value">${escapeHtml(presentation.value)}</strong>
          ${renderSampleContext(presentation)}
        </div>
      </div>
      <dl class="report-trend-facts">
        <div>
          <dt>Latest bucket</dt>
          <dd>${escapeHtml(latestRange)}</dd>
        </div>
        <div>
          <dt>${escapeHtml(options.countLabel)}</dt>
          <dd>${escapeHtml(String(options.latest[options.countField] || 0))}</dd>
        </div>
        <div>
          <dt>${escapeHtml(options.comparisonLabel)}</dt>
          <dd>${escapeHtml(previousRange || "Unavailable")}</dd>
        </div>
      </dl>
      <p class="report-trend-comparison ${escapeAttribute(options.comparison.tone)}">${escapeHtml(options.comparison.text)}</p>
    </article>
  `;
}

  function chronologicalTrendBuckets(buckets) {
  if (!Array.isArray(buckets)) {
    return [];
  }

  return buckets
    .filter(bucket => parseReportDateOnly(bucket?.startDate))
    .slice()
    .sort((a, b) => String(a.startDate || "").localeCompare(String(b.startDate || "")));
}

  function trendSample(bucket, sampleField, fallbackSampleField = null) {
  return bucket?.[sampleField] || (fallbackSampleField ? bucket?.[fallbackSampleField] : null);
}

  function describeTrendComparison(latest, previous, options) {
  if (!previous) {
    return {
      tone: "is-neutral",
      text: options.noPreviousText
    };
  }

  const latestSample = trendSample(latest, options.sampleField, options.fallbackSampleField);
  const previousSample = trendSample(previous, options.sampleField, options.fallbackSampleField);
  if (!sampleSupportsComparison(latestSample) || !sampleSupportsComparison(previousSample)) {
    return {
      tone: "is-neutral",
      text: options.lowSampleText
    };
  }

  const differenceSeconds = Number(latest[options.medianField]) - Number(previous[options.medianField]);
  if (!Number.isFinite(differenceSeconds)) {
    return {
      tone: "is-neutral",
      text: options.missingText
    };
  }

  if (Math.abs(differenceSeconds) < trendAboutSameThresholdSeconds) {
    return {
      tone: "is-neutral",
      text: options.aboutSameText
    };
  }

  const amount = formatTrendMinutes(Math.abs(differenceSeconds));
  return differenceSeconds < 0
    ? {
        tone: "is-improved",
        text: `${options.improvedPrefix} ${amount} ${options.comparisonSuffix}`
      }
    : {
        tone: "is-increased",
        text: `${options.increasedPrefix} ${amount} ${options.comparisonSuffix}`
      };
}

  function formatTrendMinutes(totalSeconds) {
  if (totalSeconds === null || totalSeconds === undefined || totalSeconds === "") {
    return "Unavailable";
  }
  const numericSeconds = Number(totalSeconds);
  if (!Number.isFinite(numericSeconds) || numericSeconds < 0) {
    return "Unavailable";
  }
  const seconds = Math.max(0, numericSeconds);
  const roundedMinutes = Math.round((seconds / 60) * 10) / 10;
  return Number.isInteger(roundedMinutes)
    ? `${roundedMinutes.toFixed(0)} min`
    : `${roundedMinutes.toFixed(1)} min`;
}

  function formatTrendBucketRange(bucket) {
  const start = parseReportDateOnly(bucket?.startDate);
  const endExclusive = parseReportDateOnly(bucket?.endDate);
  if (!start || !endExclusive) {
    return "Unknown week";
  }

  const endInclusive = new Date(endExclusive.getTime() - 86_400_000);
  return `${formatReportDateOnly(start)} - ${formatReportDateOnly(endInclusive)}`;
}

  function parseReportDateOnly(value) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(value || ""));
  if (!match) {
    return null;
  }

  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const date = new Date(Date.UTC(year, month - 1, day));
  return Number.isNaN(date.getTime()) ? null : date;
}

  function formatReportDateOnly(value) {
  return new Intl.DateTimeFormat(undefined, {
    month: "short",
    day: "numeric",
    timeZone: "UTC"
  }).format(value);
}

  function renderDoctorReportDashboard(r, hasData) {
  const section = document.getElementById("doctorReportDashboard");
  const grid = document.getElementById("doctorReportCards");
  const panel = document.getElementById("selectedDoctorPanel");
  if (!section || !grid || !panel) {
    return;
  }

  const doctors = canonicalDoctorFlowSummaries(r);
  section.hidden = !hasData && doctors.length === 0;
  if (section.hidden) {
    grid.innerHTML = "";
    grid.dataset.renderKey = "";
    panel.hidden = true;
    panel.innerHTML = "";
    return;
  }

  syncSelectedReportDoctor(doctors);

  // Skip grid rebuild when neither the payload nor the selected doctor has changed.
  // Card HTML depends on reportsVersion (data) and reportDoctorId (is-selected class /
  // aria-pressed). Tab does not affect card markup, so it is excluded from this token.
  // During an active pointer press, skip to avoid destroying the pressed element mid-click;
  // do NOT update the key so the catch-up render (on pointerup) sees the stale token and
  // rebuilds if data arrived during the press.
  const gridToken = `${reportData.getVersion()}|${state.reportDoctorId}`;
  if (grid.dataset.renderKey !== gridToken) {
    if (!state.reportPressActive) {
      grid.dataset.renderKey = gridToken;
      grid.innerHTML = doctors.length
        ? doctors.map(summary => renderDoctorFlowCard(summary)).join("")
        : `<p class="report-empty-note">No doctor report data for this range.</p>`;
    }
  }

  renderSelectedDoctorPanel(r, doctors);
}

  function syncSelectedReportDoctor(doctors) {
  if (!doctors.length) {
    state.reportDoctorId = null;
    return;
  }

  const current = doctors.find(item => item.doctorId === state.reportDoctorId);
  if (current) {
    return;
  }

  state.reportDoctorId = doctors.find(item => (item.completedCaseCount || 0) > 0)?.doctorId || doctors[0].doctorId;
}

// ---------------------------------------------------------------------------
// Schedule Fit presentation. The server owns exact-second historical assigned fit,
// current-default Calibration decisions, and all thresholds. This renderer only formats
// the returned projection and never reconstructs eligibility or insight rules.
// ---------------------------------------------------------------------------
  function renderAllocationReports(r) {
  renderAllocationBalanceCard(r);
  renderDataQualityCard(r);
  renderDoctorAllocation(r);
  renderProcedureAllocation(r);
}

  function renderAllocationBalanceCard(r) {
  const card = document.getElementById("allocationBalanceCard");
  if (!card) {
    return;
  }

  const pill = `<span class="layer-pill layer-pill--allocation">Schedule Fit</span>`;
  const summary = r.scheduleFit?.practice;
  if (!summary) {
    card.innerHTML = `
      ${pill}
      <h3>Practice Schedule Fit${renderHelpIcon("Compares historical assigned scheduling allocation with exact observed Seated-to-Doctor Complete case flow.")}</h3>
      <p class="allocation-empty">Schedule Fit is unavailable for this report response.</p>`;
    return;
  }

  const coverage = formatScheduleFitCoverage(summary);
  const fitPresentation = scheduleFitPresentationState(summary);
  const samplePresentation = sampledPresentation(summary.sample, coverage);
  card.innerHTML = `
    ${pill}
    <h3>Practice Schedule Fit</h3>
    <p class="allocation-lead">${fitPresentation.state === "Empty"
      ? escapeHtml(fitPresentation.value)
      : `${escapeHtml(coverage)} have a valid historical assigned fit pair.`}</p>
    ${renderSampleContext(samplePresentation)}
    ${renderScheduleFitKpis(summary)}
    ${renderAuditAction("HistoricalScheduleFit", "View Practice paired cases")}
    <p class="allocation-footnote">Historical assigned Schedule Fit uses finalized Expected Allocation and exact Seated-to-Doctor Complete timing. Slack and debt are calculated per case before totals are combined.</p>`;
}

  function renderDataQualityCard(r) {
  const card = document.getElementById("dataQualityCard");
  if (!card) {
    return;
  }

  const quality = r.dataQuality || {};
  const included = quality.includedCount ?? r.includedCompletedCycleCount ?? 0;
  const excluded = quality.reportingExcludedCount ?? r.excludedCompletedCycleCount ?? 0;
  const reviewCount = quality.pendingReviewCount ?? (r.exceptionReviewRecords || r.exceptionCycles || []).length;
  const reviewedCount = quality.reviewedExceptionCount ?? 0;
  const status = document.getElementById("dataQualityStatus");
  if (status) {
  const completed = quality.completedCount ?? included + excluded;
    const exclusionText = excluded > 0 ? `; ${excluded} excluded from standard metrics` : "";
    const reviewText = reviewCount > 0 ? `; ${reviewCount} ${reviewCount === 1 ? "item requires" : "items require"} review` : "";
    status.textContent = `${included} of ${completed} completed cases included${exclusionText}${reviewText}.`;
  }
  if (excluded === 0 && reviewCount === 0) {
    card.hidden = true;
    card.innerHTML = "";
    return;
  }
  card.hidden = false;
  const exclusionDetail = excluded > 0
    ? `<p class="allocation-note">${excluded} ${excluded === 1 ? "record is" : "records are"} excluded from standard metrics.</p>`
    : "";
  const reviewDetail = reviewCount > 0
    ? `<p class="allocation-note">${reviewCount} ${reviewCount === 1 ? "item requires" : "items require"} review in the action queue.</p>
       <button type="button" class="secondary-button utility-button" data-action="open-review-queue" aria-controls="reportReviewQueue">Open review queue</button>`
    : "";
  const excludedAction = excluded > 0
    ? renderAuditAction("PracticeCompletedCases", "View reporting-excluded cases", { analyticalStanding: "ReportingExcluded" })
    : "";
  const reviewedAction = reviewedCount > 0
    ? `<button type="button" class="secondary-button utility-button" data-action="open-reviewed-history" aria-controls="reportReviewedHistory">View reviewed history (${reviewedCount})</button>`
    : "";

  card.innerHTML = `
    <span class="layer-pill layer-pill--data-quality">Data Quality</span>
    <h3>Data Quality</h3>
    <p class="allocation-counts">${included} included · ${excluded} reporting-excluded</p>
    ${exclusionDetail}
    ${reviewDetail}
    ${excludedAction}
    ${reviewedAction}
    <p class="allocation-footnote">Completed = included + reporting-excluded. Review counts use source-specific lifecycle anchors in the same UTC window.</p>`;
}

  function renderDoctorAllocation(r) {
  const list = document.getElementById("doctorAllocationList");
  if (!list) {
    return;
  }

  const summaries = Array.isArray(r.scheduleFit?.doctorSummaries)
    ? r.scheduleFit.doctorSummaries
    : [];
  list.classList.remove("doctor-report-card-grid");
  list.innerHTML = summaries.length
    ? summaries.map(renderDoctorScheduleFitRow).join("")
    : `<p class="allocation-empty">No doctor Schedule Fit data for this range.</p>`;
}

  function renderDoctorScheduleFitRow(item) {
  const summary = item.historicalAssignedFit;
  if (!summary) {
    return "";
  }
  const fitPresentation = scheduleFitPresentationState(summary);
  const samplePresentation = sampledPresentation(summary.sample, formatScheduleFitCoverage(summary));
  return `
    <div class="allocation-row schedule-fit-row">
      <span class="allocation-row-name">${escapeHtml(item.doctorName || getDoctorName(item.doctorId))}</span>
      <span class="allocation-row-detail">
        <strong>${escapeHtml(fitPresentation.state === "Empty"
          ? fitPresentation.value
          : formatScheduleFitCoverage(summary))}</strong>
        ${renderSampleContext(samplePresentation)}
        <small>${fitPresentation.measurementsAvailable
          ? `Historical assigned net ${escapeHtml(formatSignedScheduleFitSeconds(summary.netVarianceSeconds))} · slack ${escapeHtml(formatObservedDuration(summary.totalSlackSeconds))} · debt ${escapeHtml(formatObservedDuration(summary.totalDebtSeconds))}`
          : `Historical assigned measurements: ${escapeHtml(fitPresentation.value)}.`}</small>
        ${renderAuditAction("HistoricalScheduleFit", `View paired cases for ${item.doctorName || getDoctorName(item.doctorId)}`, { segmentDoctorId: item.doctorId })}
      </span>
    </div>`;
}

// Issue #216 canonical Doctor Overview authority. The server owns medians, day qualification, and
// sample states; this presentation never reconstructs metrics from monthly DoctorSummaries.
  function canonicalDoctorFlowSummaries(report) {
  return Array.isArray(report?.doctorFlowSummaries) ? report.doctorFlowSummaries : [];
}

// Issue #217 canonical Doctor Trends authority. This is deliberately separate from the existing
// practice-level report.trends compatibility contract and from observedDoctorDays.
  function canonicalDoctorFlowTrends(report) {
  return Array.isArray(report?.doctorFlowTrends) ? report.doctorFlowTrends : [];
}

  function emptyDoctorFlowSummary(doctorId, doctorName) {
  return {
    doctorId,
    doctorName,
    completedCaseCount: 0,
    medianReadyWaitSeconds: null,
    medianDoctorTimeSeconds: null,
    medianObservedClinicalSpanMinutes: null,
    peakConcurrentRooms: null,
    observedDoctorDayCount: 0,
    samples: null
  };
}

  function doctorFlowPresentation(sample, measuredValue) {
  if (!sample) {
    return {
      value: "Unavailable",
      state: "Unavailable",
      detail: "server sample context unavailable"
    };
  }
  return sampledPresentation(sample, measuredValue);
}

  function renderDoctorFlowMetric(label, measuredValue, sample, helpText = null, contributorKind = null, auditOptions = {}) {
  const presentation = doctorFlowPresentation(sample, measuredValue);
  return `
    <div>
      <dt>${escapeHtml(label)}${helpText ? renderHelpIcon(helpText) : ""}</dt>
      <dd>${escapeHtml(presentation.value)}${renderSampleContext(presentation)}</dd>
      ${contributorKind ? renderAuditAction(contributorKind, `View contributing cases for ${label}`, auditOptions) : ""}
    </div>`;
}

  function hasDoctorPhaseTimingObservation(summary) {
  return summary?.samples?.readyWait?.contributingCount > 0
    || summary?.samples?.doctorTime?.contributingCount > 0;
}

  function doctorFlowSummarySentence(summary) {
  const completedState = summary?.samples?.completedCases?.state;
  const observedState = summary?.samples?.observedDays?.state;
  if (!summary?.samples) {
    return "Doctor flow sample context is unavailable for this report response.";
  }
  if (completedState === "Empty") {
    return hasDoctorPhaseTimingObservation(summary)
      ? "No completed cases yet; phase timing observations are available in the current scope and range."
      : "No completed or phase timing observations match the current scope and range.";
  }
  if (observedState === "Unavailable") {
    return "Completed history is present, but no Ready-anchored observed doctor-day qualifies.";
  }
  return `${summary.observedDoctorDayCount} observed doctor day${summary.observedDoctorDayCount === 1 ? "" : "s"} in the current scope and range.`;
}

  function renderDoctorFlowCardBody(summary, name, identity) {
  const samples = summary.samples || {};
  return `
    <header class="doctor-report-card-head">
      <span class="doctor-report-initials" aria-hidden="true">${escapeHtml(identity.initials)}</span>
      <div class="doctor-report-identity">
        <h4>${escapeHtml(name)}</h4>
        <p>${escapeHtml(doctorFlowSummarySentence(summary))}</p>
      </div>
    </header>
    <dl class="doctor-report-metrics doctor-flow-metrics">
      ${renderDoctorFlowMetric("Completed Cases", String(summary.completedCaseCount ?? 0), samples.completedCases)}
      ${renderDoctorFlowMetric("Median Ready Wait", formatObservedDuration(summary.medianReadyWaitSeconds), samples.readyWait)}
      ${renderDoctorFlowMetric("Median Doctor Time", formatObservedDuration(summary.medianDoctorTimeSeconds), samples.doctorTime)}
      ${renderDoctorFlowMetric("Median Observed Clinical Span", formatDurationMinutes(summary.medianObservedClinicalSpanMinutes), samples.observedDays, "Median Ready-anchored Observed Clinical Span across qualifying observed doctor-days.")}
      ${renderDoctorFlowMetric("Peak Concurrent Rooms", describePeakConcurrentRooms(summary.peakConcurrentRooms), samples.observedDays)}
      ${renderDoctorFlowMetric("Observed Doctor Days", String(summary.observedDoctorDayCount ?? 0), samples.observedDays, "Counts only UTC dates with qualifying Ready-anchored observed clinical flow.")}
    </dl>`;
}

// The whole card is the selection control (role="button", focusable). The "View details" affordance
// is a non-interactive visual cue (aria-hidden span) so we never nest interactive controls; clicks
// anywhere in the card and Enter/Space on the focused card both resolve to data-report-doctor-id.
  function renderDoctorFlowCard(summary) {
  const doctor = (getSnapshot()?.doctors || []).find(item => item.id === summary.doctorId);
  const name = doctor ? doctor.name : summary.doctorName || getDoctorName(summary.doctorId);
  const identity = getDoctorIdentity(summary.doctorId, name);
  const isVisuallyEmpty = summary.samples?.completedCases?.state === "Empty"
    && !hasDoctorPhaseTimingObservation(summary);
  const selected = summary.doctorId === state.reportDoctorId;
  return `
    <article class="doctor-report-card ${isVisuallyEmpty ? "is-empty" : ""} ${selected ? "is-selected" : ""}" style="--doctor-color: ${escapeAttribute(identity.color)}" data-report-doctor-id="${escapeAttribute(summary.doctorId)}" role="button" tabindex="0" aria-pressed="${selected ? "true" : "false"}" aria-label="${escapeAttribute(`Show report details for ${name}`)}">
      ${renderDoctorFlowCardBody(summary, name, identity)}
      <span class="doctor-report-detail-link" aria-hidden="true">
        ${selected ? "Viewing details" : "View details"}
      </span>
    </article>`;
}

  function renderSelectedDoctorPanel(r, doctors) {
  const panel = document.getElementById("selectedDoctorPanel");
  if (!panel) {
    return;
  }

  const summary = doctors.find(item => item.doctorId === state.reportDoctorId) || doctors[0];
  if (!summary) {
    panel.hidden = true;
    panel.innerHTML = "";
    panel.dataset.renderKey = "";
    return;
  }

  const doctor = (getSnapshot()?.doctors || []).find(item => item.id === summary.doctorId);
  const name = doctor ? doctor.name : summary.doctorName || getDoctorName(summary.doctorId);
  const identity = getDoctorIdentity(summary.doctorId, name);
  const tabs = ["overview", "trends", "procedures", "flow", "schedule", "audit"];
  if (!tabs.includes(state.reportDoctorTab)) {
    state.reportDoctorTab = "overview";
  }

  // Skip panel rebuild when payload, selected doctor, and active tab are all unchanged.
  // Tab is included because it drives both the tab-button aria-selected states and the
  // entire tab-panel content. During an active pointer press, defer the rebuild to avoid
  // destroying the tab button mid-click; leave the key stale so the catch-up render applies.
  const panelToken = `${reportData.getVersion()}|${summary.doctorId}|${state.reportDoctorTab}`;
  if (panel.dataset.renderKey === panelToken) {
    return;
  }
  if (state.reportPressActive) {
    return;
  }
  panel.dataset.renderKey = panelToken;

  panel.hidden = false;
  panel.style.setProperty("--doctor-color", identity.color);
  panel.innerHTML = `
    <div class="selected-doctor-head">
      <span class="doctor-report-initials" aria-hidden="true">${escapeHtml(identity.initials)}</span>
      <div>
        <h2>${escapeHtml(name)}</h2>
        <p>${escapeHtml(doctorFlowSummarySentence(summary))}</p>
      </div>
    </div>
    <div class="selected-doctor-tabs" role="tablist" aria-label="${escapeAttribute(name)} report sections">
      ${tabs.map(tab => renderDoctorReportTabButton(tab)).join("")}
    </div>
    <div class="selected-doctor-tab-panel">
      ${renderSelectedDoctorTabContent(state.reportDoctorTab, r, summary)}
    </div>`;
}

  function renderDoctorReportTabButton(tab) {
  const labels = {
    overview: "Overview",
    trends: "Trends",
    procedures: "Procedures",
    flow: "Room Load / Flow",
    schedule: "Schedule Fit",
    audit: "Case Audit"
  };
  const selected = state.reportDoctorTab === tab;
  return `
    <button class="selected-doctor-tab ${selected ? "is-active" : ""}" type="button" role="tab" aria-selected="${selected ? "true" : "false"}" data-report-doctor-tab="${escapeAttribute(tab)}">
      ${escapeHtml(labels[tab])}
    </button>`;
}

  function renderSelectedDoctorTabContent(tab, r, summary) {
  if (tab === "audit") {
    return renderSelectedDoctorAudit(r, summary.doctorId);
  }

  if (tab === "overview") {
    return renderSelectedDoctorOverview(r, summary);
  }

  if (tab === "flow") {
    return renderSelectedDoctorFlow(r, summary);
  }

  if (tab === "schedule") {
    return renderSelectedDoctorScheduleFit(r, summary);
  }

  if (tab === "trends") {
    return renderSelectedDoctorTrends(r, summary);
  }

  if (tab === "procedures") {
    return renderSelectedDoctorProcedures(r, summary);
  }

  return renderSelectedDoctorEmptyState("Not Available", "This section isn't available with the current report payload.");
}

  function renderSelectedDoctorTrends(r, summary) {
  const series = canonicalDoctorFlowTrends(r)
    .find(item => item.doctorId === summary.doctorId);
  if (!series || !Array.isArray(series.buckets) || series.buckets.length === 0) {
    return renderSelectedDoctorEmptyState(
      "Weekly Doctor Trends",
      "No dateable Doctor Complete observation is available for this doctor trend window. No calendar history was invented."
    );
  }

  const metricDefinitions = [
    {
      key: "readyWait",
      label: "Median Ready Wait",
      valueKey: "medianReadyWaitSeconds",
      format: formatObservedDuration
    },
    {
      key: "doctorTime",
      label: "Median Doctor Time",
      valueKey: "medianDoctorTimeSeconds",
      format: formatObservedDuration
    },
    {
      key: "completedCases",
      label: "Completed Cases",
      valueKey: "completedCaseCount",
      format: value => Number.isFinite(value) ? String(value) : "--"
    },
    {
      key: "observedClinicalSpan",
      label: "Median Observed Clinical Span",
      valueKey: "medianObservedClinicalSpanMinutes",
      format: formatDurationMinutes
    }
  ];
  const effectiveRange = formatExclusiveDateRange(series.effectiveStartDate, series.effectiveEndDate);

  return `
    <section class="doctor-trends" aria-label="Weekly Doctor Trends for ${escapeAttribute(series.doctorName || summary.doctorName || summary.doctorId)}">
      <header class="doctor-trends__head">
        <div>
          <h3>Weekly Doctor Trends</h3>
          <p>Monday-start UTC buckets within ${escapeHtml(effectiveRange)}. Missing observations remain visible gaps.</p>
        </div>
        <span class="doctor-trends__window">${escapeHtml(String(series.buckets.length))} week${series.buckets.length === 1 ? "" : "s"}</span>
      </header>
      <div class="doctor-trend-metrics">
        ${metricDefinitions.map(metric => renderDoctorTrendMetric(series.buckets, metric)).join("")}
      </div>
    </section>`;
}

  function renderDoctorTrendMetric(buckets, metric) {
  return `
    <section class="doctor-trend-metric" aria-label="${escapeAttribute(`${metric.label} by week`)}">
      <h4>${escapeHtml(metric.label)}</h4>
      <div class="doctor-trend-grid" role="list" style="--doctor-trend-bucket-count: ${buckets.length}">
        ${buckets.map(bucket => renderDoctorTrendBucket(bucket, metric)).join("")}
      </div>
    </section>`;
}

  function renderDoctorTrendBucket(bucket, metric) {
  const sample = bucket?.samples?.[metric.key];
  const formattedValue = metric.format(bucket?.[metric.valueKey]);
  const presentation = doctorFlowPresentation(sample, formattedValue);
  const stateClass = presentation.state ? presentation.state.toLowerCase() : "unavailable";
  const gap = presentation.state === "Empty" || presentation.state === "Unavailable";
  const calendarRange = formatTrendBucketRange(bucket);
  const effectiveRange = formatExclusiveDateRange(bucket?.effectiveStartDate, bucket?.effectiveEndDate);
  const dateLabel = bucket?.isPartial
    ? `${calendarRange}; selected portion ${effectiveRange}`
    : calendarRange;
  const sampleLabel = presentation.state
    ? `${presentation.state}, ${presentation.detail}`
    : "sample context unavailable";

  return `
    <article class="doctor-trend-bucket doctor-trend-bucket--${escapeAttribute(stateClass)} ${gap ? "is-gap" : ""} ${bucket?.isPartial ? "is-partial" : ""}" role="listitem" aria-label="${escapeAttribute(`${metric.label}, ${dateLabel}, ${presentation.value}, ${sampleLabel}`)}">
      <span class="doctor-trend-bucket__date">${escapeHtml(calendarRange)}</span>
      ${bucket?.isPartial ? `<span class="doctor-trend-bucket__partial">Selected portion: ${escapeHtml(effectiveRange)}</span>` : ""}
      <strong>${escapeHtml(presentation.value)}</strong>
      ${renderSampleContext(presentation)}
    </article>`;
}

  function formatExclusiveDateRange(startValue, endExclusiveValue) {
  const start = parseReportDateOnly(startValue);
  const endExclusive = parseReportDateOnly(endExclusiveValue);
  if (!start || !endExclusive) {
    return "the selected report range";
  }
  const endInclusive = new Date(endExclusive.getTime() - 86_400_000);
  return `${formatReportDateOnly(start)} - ${formatReportDateOnly(endInclusive)}`;
}

// Shared empty/placeholder card for selected-doctor tabs: a heading (with optional help bubble)
// plus a plain-English note, reusing the same markup as the populated tab sections so an empty
// tab still reads as an intentional part of the report rather than a broken or missing view.
  function renderSelectedDoctorEmptyState(title, body, helpText) {
  return `
    <section class="selected-doctor-overview">
      <div class="selected-doctor-summary">
        <h3>${escapeHtml(title)}${helpText ? renderHelpIcon(helpText) : ""}</h3>
        <p class="report-empty-note">${escapeHtml(body)}</p>
      </div>
    </section>`;
}

  function renderSelectedDoctorOverview(r, summary) {
  const samples = summary.samples || {};
  return `
    <section class="selected-doctor-overview">
      <div class="selected-doctor-summary">
        <h3>Observed Flow Summary</h3>
        <p>${escapeHtml(doctorFlowSummarySentence(summary))}</p>
        <p class="allocation-footnote">Uses server-defined reporting populations for ${escapeHtml(r.rangeLabel || "the selected range")}.</p>
      </div>
      <dl class="selected-doctor-kpis doctor-flow-metrics">
        ${renderDoctorFlowMetric("Completed Cases", String(summary.completedCaseCount ?? 0), samples.completedCases, null, "IncludedCompletedCases", { segmentDoctorId: summary.doctorId })}
        ${renderDoctorFlowMetric("Median Ready Wait", formatObservedDuration(summary.medianReadyWaitSeconds), samples.readyWait, null, "ReadyWait", { segmentDoctorId: summary.doctorId })}
        ${renderDoctorFlowMetric("Median Doctor Time", formatObservedDuration(summary.medianDoctorTimeSeconds), samples.doctorTime, null, "DoctorTime", { segmentDoctorId: summary.doctorId })}
        ${renderDoctorFlowMetric("Median Observed Clinical Span", formatDurationMinutes(summary.medianObservedClinicalSpanMinutes), samples.observedDays)}
        ${renderDoctorFlowMetric("Peak Concurrent Rooms", describePeakConcurrentRooms(summary.peakConcurrentRooms), samples.observedDays)}
        ${renderDoctorFlowMetric("Observed Doctor Days", String(summary.observedDoctorDayCount ?? 0), samples.observedDays)}
      </dl>
    </section>`;
}

  function observedLoadNumber(value) {
  const number = Number(value);
  return Number.isFinite(number) ? number : 0;
}

  function renderSelectedDoctorFlow(r, summary) {
  const days = (r.observedDoctorFlowDays || []).filter(day => day.doctorId === summary.doctorId);
  if (!days.length) {
    return renderSelectedDoctorEmptyState(
      "Room Load / Flow",
      "No qualifying Ready-anchored observed doctor-day is available for this doctor in the current report range.",
      "Shows the exact partition of each qualifying Observed Clinical Span by concurrent Doctor Working rooms."
    );
  }

  const sorted = [...days].sort((a, b) => String(b.reportDate || "").localeCompare(String(a.reportDate || "")));
  const recent = sorted.slice(0, 10);

  const completedCases = days.reduce((sum, day) => sum + observedLoadNumber(day.qualifyingCaseCount), 0);
  const totalSpan = days.reduce((sum, day) => sum + observedLoadNumber(day.observedClinicalSpanMinutes), 0);
  const totalUnstructured = days.reduce((sum, day) => sum + observedLoadNumber(day.unstructuredTimeMinutes), 0);
  const totalOneRoom = days.reduce((sum, day) => sum + observedLoadNumber(day.minutesWithOneDoctorWorkingRoom), 0);
  const totalTwoRooms = days.reduce((sum, day) => sum + observedLoadNumber(day.minutesWithTwoDoctorWorkingRooms), 0);
  const totalThreePlusRooms = days.reduce((sum, day) => sum + observedLoadNumber(day.minutesWithThreeOrMoreDoctorWorkingRooms), 0);

  return `
    <section class="selected-doctor-overview">
      <div class="selected-doctor-summary">
        <h3>Room Load / Flow${renderHelpIcon("Shows the exact partition of each qualifying Observed Clinical Span by concurrent Doctor Working rooms.")}</h3>
        <p>Across ${escapeHtml(String(days.length))} observed doctor day${days.length === 1 ? "" : "s"}, ${escapeHtml(String(completedCases))} qualifying completed case${completedCases === 1 ? "" : "s"} contributed to Ready-anchored flow.</p>
        <p class="allocation-footnote">Unstructured Time is the portion of Observed Clinical Span with no active Doctor Working interval.</p>
      </div>
      <dl class="selected-doctor-kpis">
        <div><dt>Clinical Span</dt><dd>${escapeHtml(formatDurationMinutes(totalSpan))}</dd></div>
        <div><dt>Unstructured Time</dt><dd>${escapeHtml(formatDurationMinutes(totalUnstructured))}</dd></div>
        <div><dt>1 Doctor Working room</dt><dd>${escapeHtml(formatDurationMinutes(totalOneRoom))}</dd></div>
        <div><dt>2 Doctor Working rooms</dt><dd>${escapeHtml(formatDurationMinutes(totalTwoRooms))}</dd></div>
        <div><dt>3+ Doctor Working rooms</dt><dd>${escapeHtml(formatDurationMinutes(totalThreePlusRooms))}</dd></div>
        <div><dt>Peak Concurrent Rooms</dt><dd>${escapeHtml(describePeakConcurrentRooms(summary.peakConcurrentRooms))}</dd></div>
      </dl>
    </section>
    <div class="selected-doctor-audit">
      <table class="report-table">
        <thead>
          <tr>
            <th>Date</th>
            <th>Cases</th>
            <th>Clinical Span</th>
            <th>Unstructured</th>
            <th>1 Working</th>
            <th>2 Working</th>
            <th>3+ Working</th>
            <th>Peak Concurrent</th>
          </tr>
        </thead>
        <tbody>
          ${recent.map(day => `
            <tr>
              <td>${escapeHtml(formatObservedDayDate(day.reportDate))}</td>
              <td>${escapeHtml(Number.isFinite(day.qualifyingCaseCount) ? String(day.qualifyingCaseCount) : "--")}</td>
              <td>${escapeHtml(formatDurationMinutes(day.observedClinicalSpanMinutes))}</td>
              <td>${escapeHtml(formatDurationMinutes(day.unstructuredTimeMinutes))}</td>
              <td>${escapeHtml(formatDurationMinutes(day.minutesWithOneDoctorWorkingRoom))}</td>
              <td>${escapeHtml(formatDurationMinutes(day.minutesWithTwoDoctorWorkingRooms))}</td>
              <td>${escapeHtml(formatDurationMinutes(day.minutesWithThreeOrMoreDoctorWorkingRooms))}</td>
              <td>${escapeHtml(describePeakConcurrentRooms(day.peakConcurrentRooms))}</td>
            </tr>
          `).join("")}
        </tbody>
      </table>
      ${days.length > recent.length
        ? `<p class="allocation-footnote">Showing the ${recent.length} most recent observed days of ${days.length} total.</p>`
        : ""}
    </div>`;
}

  function formatObservedDayDate(value) {
  const parsed = parseReportDateOnly(value);
  return parsed ? formatReportDateOnly(parsed) : "--";
}

  function describePeakConcurrentRooms(peakConcurrentRooms) {
  if (!Number.isFinite(peakConcurrentRooms) || peakConcurrentRooms < 1) {
    return "--";
  }
  if (peakConcurrentRooms === 1) {
    return "1 room";
  }
  if (peakConcurrentRooms === 2) {
    return "2 rooms";
  }
  return "3+ rooms";
}

  function renderSelectedDoctorScheduleFit(r, summary) {
  const doctorSummary = (r.scheduleFit?.doctorSummaries || [])
    .find(item => item.doctorId === summary.doctorId);
  if (!doctorSummary?.historicalAssignedFit) {
    return renderSelectedDoctorEmptyState(
      "Schedule Fit",
      "No server-owned Schedule Fit projection is available for this doctor in the current report response."
    );
  }

  const historical = doctorSummary.historicalAssignedFit;
  const fitPresentation = scheduleFitPresentationState(historical);
  if (fitPresentation.state === "Empty") {
    return renderSelectedDoctorEmptyState(
      "Schedule Fit",
      "No observation. This doctor has no included completed cases in the selected report population."
    );
  }
  const segments = doctorScheduleFitSegments(r, summary.doctorId);
  return `
    <section class="selected-doctor-overview">
      <div class="selected-doctor-summary">
        <h3>Schedule Fit</h3>
        <p>${escapeHtml(formatScheduleFitCoverage(historical))} have a valid historical assigned fit pair.</p>
        ${renderSampleContext(sampledPresentation(historical.sample, formatScheduleFitCoverage(historical)))}
        <p class="allocation-footnote">Compares finalized historical scheduling allocation with exact observed Seated-to-Doctor Complete case flow. This evaluates the scheduling model, not the doctor.</p>
      </div>
      ${renderScheduleFitKpis(historical, "selected-doctor-kpis schedule-fit-kpis")}
      ${renderAuditAction("HistoricalScheduleFit", `View paired cases for ${summary.doctorName || summary.doctorId}`, { segmentDoctorId: summary.doctorId })}
      <div class="selected-doctor-schedule-segments">
        <h4>Procedure Schedule Fit and current-default calibration</h4>
        ${segments.length
          ? segments.map(segment => renderScheduleFitSegment(segment)).join("")
          : `<p class="report-empty-note">No procedure Schedule Fit segments are available for this doctor.</p>`}
      </div>
    </section>`;
}

  function doctorScheduleFitSegments(report, doctorId) {
  const segments = Array.isArray(report.scheduleFit?.procedureSegments)
    ? report.scheduleFit.procedureSegments
    : [];
  if (report.query?.scope === "Doctor") {
    return segments;
  }

  return segments.flatMap(segment => {
    const doctor = (segment.doctorBreakdown || []).find(item => item.doctorId === doctorId);
    return doctor
      ? [{
          ...segment,
          historicalAssignedFit: doctor.historicalAssignedFit,
          currentDefaultCalibration: doctor.currentDefaultCalibration,
          doctorBreakdown: []
        }]
      : [];
  });
}

  function renderProcedureMix(r) {
  const container = document.getElementById("reportProcedureMix");
  if (!container) {
    return;
  }

  container.hidden = false;
  container.innerHTML = renderProcedureMixMarkup(r, {
    headingTag: "h2",
    headingId: "reportProcedureMixHeading"
  });
}

// Shared Procedure Mix presentation. The report response already owns scope, grouping, filtering,
// denominator, shares, and row order; this renderer deliberately performs no analytical work.
  function renderProcedureMixMarkup(r, { headingTag = "h2", headingId = null, compact = false } = {}) {
  const rows = Array.isArray(r?.scopedProcedureGroups) ? r.scopedProcedureGroups : [];
  const totalCases = Number.isFinite(r?.includedCompletedCycleCount)
    ? r.includedCompletedCycleCount
    : 0;
  const overallSample = r?.samples?.includedCompletedCases;
  const sampleContext = overallSample
    ? renderSampleContext(sampledPresentation(overallSample, String(totalCases)))
    : "";
  const scopeLabel = r?.query?.scope === "Doctor" ? "Doctor" : "Practice";
  const safeHeadingTag = headingTag === "h3" ? "h3" : "h2";
  const headingAttribute = headingId ? ` id="${escapeAttribute(headingId)}"` : "";
  const totalMarkup = totalCases === 0
    ? `<strong>No observation</strong><span>No completed cases in scope</span>${sampleContext}`
    : `<strong>${escapeHtml(String(totalCases))}</strong><span>completed case${totalCases === 1 ? "" : "s"} in scope</span>${sampleContext}`;
  const bodyMarkup = totalCases === 0
    ? `<p class="procedure-mix-empty">No observation. No standard included completed cases match the current filters.</p>`
    : rows.length === 0
      ? `<p class="procedure-mix-empty">No procedure details were returned for the current completed population.</p>`
      : `
        <div class="procedure-mix-table-wrap">
          <table class="procedure-mix-table">
            <thead>
              <tr>
                <th>Procedure</th>
                <th>Cases</th>
                <th>Share</th>
              </tr>
            </thead>
            <tbody>
              ${rows.map(row => `
                <tr>
                  <td>${escapeHtml(row.procedureLabel || "Procedure")}</td>
                  <td>${escapeHtml(Number.isFinite(row.caseCount) ? String(row.caseCount) : "--")}</td>
                  <td>
                    ${escapeHtml(formatProcedureShare(row.shareOfScopedCases))}
                    ${renderAuditAction("ProcedureMix", `View contributing cases for ${row.procedureLabel || "procedure"}`, r?.query?.procedureGrouping === "DetailedVariant"
                      ? { procedureCode: row.procedureCode }
                      : { baseProcedureCode: row.baseProcedureCode || row.procedureCode })}
                  </td>
                </tr>
              `).join("")}
            </tbody>
          </table>
        </div>`;

  return `
    <article class="procedure-mix-card${compact ? " is-compact" : ""}">
      <div class="procedure-mix-head">
        <div>
          <span class="layer-pill layer-pill--population">Scoped composition</span>
          <${safeHeadingTag}${headingAttribute}>Procedure Mix</${safeHeadingTag}>
          <p>Completed work in the current ${escapeHtml(scopeLabel)} scope and selected filters.</p>
        </div>
        <div class="procedure-mix-total">${totalMarkup}</div>
      </div>
      ${bodyMarkup}
      <p class="procedure-mix-note">Sedation is a modifier of the primary procedure, not a separate case.</p>
    </article>`;
}

  function renderSelectedDoctorProcedures(r, agg) {
  if (r?.query?.scope !== "Doctor" || r.query.doctorId !== agg.doctorId) {
    return renderSelectedDoctorEmptyState(
      "Procedure Mix",
      "Select Doctor scope to view this doctor's Procedure Mix with the current filters.",
      "Doctor Procedure Mix uses the server-owned Doctor scope and never reconstructs a doctor denominator from Practice results."
    );
  }

  return [
    renderProcedureMixMarkup(r, { headingTag: "h3", compact: true }),
    renderProcedureIntelligenceMarkup(r, {
      headingTag: "h3",
      compact: true,
      idPrefix: "doctor-procedure-intelligence"
    })
  ].join("");
}

  function renderProcedureIntelligence(r) {
  const container = document.getElementById("reportProcedureIntelligence");
  if (!container) {
    return;
  }

  container.hidden = false;
  container.innerHTML = renderProcedureIntelligenceMarkup(r, {
    headingTag: "h2",
    headingId: "reportProcedureIntelligenceHeading",
    idPrefix: "practice-procedure-intelligence"
  });
}

  function renderProcedureIntelligenceMarkup(r, {
  headingTag = "h2",
  headingId = null,
  compact = false,
  idPrefix = "procedure-intelligence"
} = {}) {
  const rows = Array.isArray(r?.procedureIntelligenceRows) ? r.procedureIntelligenceRows : [];
  const safeHeadingTag = /^h[2-4]$/.test(headingTag) ? headingTag : "h2";
  const headingAttribute = headingId ? ` id="${escapeAttribute(headingId)}"` : "";
  const scopeKey = r?.query?.scope === "Doctor"
    ? `Doctor:${r.query.doctorId || "unknown"}`
    : "Practice";
  const body = rows.length
    ? `<div class="procedure-intelligence-list${compact ? " is-compact" : ""}">
        ${rows.map((row, index) => renderProcedureIntelligenceRow(
          row,
          index,
          { idPrefix, scopeKey, showDoctors: r?.query?.scope !== "Doctor" }
        )).join("")}
      </div>`
    : `<div class="procedure-intelligence-empty">
        <strong>No observation</strong>
        <p>Procedure timing will appear when included completed cases match the current scope.</p>
      </div>`;

  return `
    <div class="procedure-intelligence-head">
      <div>
        <span class="layer-pill layer-pill--timing">Procedure Intelligence</span>
        <${safeHeadingTag}${headingAttribute}>Procedure Intelligence</${safeHeadingTag}>
        <p>Observed procedure timing and neutral scheduling context for the current scope.</p>
      </div>
    </div>
    ${body}`;
}

  function renderProcedureIntelligenceRow(row, index, { idPrefix, scopeKey, showDoctors }) {
  const metrics = row?.metrics || {};
  const procedureCode = row?.procedureCode || row?.baseProcedureCode || `row-${index}`;
  const expansionKey = `${scopeKey}|${row?.procedureGrouping || "Family"}|${procedureCode}`;
  const detailId = `${idPrefix}-detail-${index}`;
  const expanded = state.procedureIntelligenceExpanded.has(expansionKey);
  const label = row?.procedureLabel || procedureCode || "Unknown procedure";
  const doctorBreakdown = showDoctors && Array.isArray(row?.doctorBreakdown)
    ? row.doctorBreakdown
    : [];
  const auditProcedure = row?.procedureGrouping === "DetailedVariant"
    ? { procedureCode: row.procedureCode }
    : { baseProcedureCode: row.baseProcedureCode || row.procedureCode };

  return `
    <article class="procedure-intelligence-card">
      <header class="procedure-intelligence-card-head">
        <div>
          <span class="procedure-intelligence-code">${escapeHtml(procedureCode)}</span>
          <h3>${escapeHtml(label)}</h3>
        </div>
        ${renderSampleContext(sampledPresentation(
          metrics.completedSample,
          String(metrics.completedCaseCount ?? 0)
        ))}
      </header>
      <div class="procedure-intelligence-primary">
        ${renderProcedureIntelligenceMetric(
          "Completed N",
          String(metrics.completedCaseCount ?? 0),
          metrics.completedSample
        )}
        ${renderProcedureIntelligenceMetric(
          "Median Doctor Time",
          formatObservedDuration(metrics.medianDoctorTimeSeconds),
          metrics.doctorTimeSample,
          "Doctor Arrived to Doctor Complete."
        )}
        ${renderTypicalDoctorTimeRange(metrics)}
      </div>
      <div class="procedure-intelligence-secondary">
        ${renderCurrentRosterDefault(row)}
        ${renderProcedureIntelligenceMetric(
          "Median Ready Wait",
          formatObservedDuration(metrics.medianReadyWaitSeconds),
          metrics.readyWaitSample,
          "Accepted Ready to Doctor Arrived."
        )}
        ${renderProcedureIntelligenceMetric(
          "Median Seated -> Doctor Complete",
          formatObservedDuration(metrics.medianSeatedToDoctorCompleteSeconds),
          metrics.seatedToDoctorCompleteSample,
          "Observed case flow from Seated to Doctor Complete; turnover is not included."
        )}
      </div>
      <div class="procedure-intelligence-audit-actions">
        ${renderAuditAction("ProcedureMix", `View completed cases for ${label}`, auditProcedure)}
        ${renderAuditAction("ProcedureIntelligenceDoctorTime", `View Doctor Time cases for ${label}`, auditProcedure)}
        ${renderAuditAction("ProcedureIntelligenceReadyWait", `View Ready Wait cases for ${label}`, auditProcedure)}
        ${renderAuditAction("ProcedureIntelligenceSeatedToDoctorComplete", `View Seated to Doctor Complete cases for ${label}`, auditProcedure)}
      </div>
      <button type="button"
        class="procedure-intelligence-toggle"
        data-procedure-intelligence-key="${escapeAttribute(expansionKey)}"
        aria-expanded="${String(expanded)}"
        aria-controls="${escapeAttribute(detailId)}">
        ${expanded ? "Hide details" : "View details"}
      </button>
      <div class="procedure-intelligence-detail" id="${escapeAttribute(detailId)}"${expanded ? "" : " hidden"}>
        ${renderProcedureIntelligenceDetail(row, doctorBreakdown)}
      </div>
    </article>`;
}

  function renderProcedureIntelligenceMetric(label, value, sample, helpText = null) {
  const presentation = sampledPresentation(sample, value);
  return `
    <div class="procedure-intelligence-metric">
      <span>${escapeHtml(label)}</span>${helpText ? renderHelpIcon(helpText) : ""}
      <strong>${escapeHtml(presentation.value)}</strong>
      ${renderSampleContext(presentation)}
    </div>`;
}

  function renderTypicalDoctorTimeRange(metrics) {
  const sample = metrics?.doctorTimeSample;
  let value = "No observation";
  let explanation = "Middle 50% of observed Doctor Time.";

  if (sample?.state === "Sufficient"
      && Number.isFinite(metrics?.typicalDoctorTimeLowerSeconds)
      && Number.isFinite(metrics?.typicalDoctorTimeUpperSeconds)) {
    value = `${formatDuration(metrics.typicalDoctorTimeLowerSeconds)} - ${formatDuration(metrics.typicalDoctorTimeUpperSeconds)}`;
  } else if (sample?.state === "Limited") {
    value = "Withheld for Limited sample";
    explanation = "Middle 50% of observed Doctor Time. Numeric endpoints are shown at N=5.";
  } else if (sample?.state === "Unavailable") {
    value = "Unavailable";
  }

  const presentation = sampledPresentation(sample, value);
  return `
    <div class="procedure-intelligence-metric is-range">
      <span>Typical Doctor Time Range</span>${renderHelpIcon(explanation)}
      <strong>${escapeHtml(presentation.value)}</strong>
      ${renderSampleContext(presentation)}
      <small>Middle 50% of observed Doctor Time.</small>
    </div>`;
}

  function renderCurrentRosterDefault(row) {
  const hasDefault = Number.isFinite(row?.currentDefaultAllocationMinutes);
  const value = hasDefault
    ? formatDurationMinutes(row.currentDefaultAllocationMinutes)
    : "Unavailable";
  const behavior = row?.allocationBehavior === "Variable"
    ? "Variable procedure - confirm the case-specific allocation."
    : row?.allocationBehavior === "Known"
      ? "Known procedure starting allocation."
      : "Current rough roster starting allocation.";

  return `
    <div class="procedure-intelligence-metric is-allocation-context">
      <span>Current roster default</span>${renderHelpIcon("Current rough scheduling starting allocation; it is not expected Doctor Time or Schedule Fit.")}
      <strong>${escapeHtml(value)}</strong>
      <small>${escapeHtml(behavior)}</small>
    </div>`;
}

  function renderProcedureIntelligenceDetail(row, doctorBreakdown) {
  const metrics = row?.metrics || {};
  const capturedDefaults = usefulCapturedDefaults(
    metrics.historicalCapturedDefaultValues,
    row?.currentDefaultAllocationMinutes);

  return `
    <div class="procedure-intelligence-detail-grid">
      ${renderProcedureIntelligenceMetric(
        "Average Doctor Time",
        formatObservedDuration(metrics.averageDoctorTimeSeconds),
        metrics.doctorTimeSample
      )}
      ${renderProcedureIntelligenceMetric(
        "Average Ready Wait",
        formatObservedDuration(metrics.averageReadyWaitSeconds),
        metrics.readyWaitSample
      )}
      ${renderProcedureIntelligenceMetric(
        "Average Seated -> Doctor Complete",
        formatObservedDuration(metrics.averageSeatedToDoctorCompleteSeconds),
        metrics.seatedToDoctorCompleteSample
      )}
    </div>
    <section class="procedure-intelligence-allocation-context" aria-label="Historical assigned allocation context">
      <h4>Historical assigned allocation</h4>
      <p>${renderHistoricalAssignedAllocation(metrics)}</p>
      ${capturedDefaults.length
        ? `<p><span>Historical captured starting allocations:</span> ${renderAllocationValueCounts(capturedDefaults)}</p>`
        : ""}
    </section>
    ${doctorBreakdown.length ? renderDoctorProcedureIntelligence(doctorBreakdown) : ""}`;
}

  function renderHistoricalAssignedAllocation(metrics) {
  const sample = metrics?.historicalAssignedAllocationSample;
  if (!sample || sample.state === "Empty") {
    return "No observation.";
  }
  if (sample.state === "Unavailable") {
    return `Unavailable - 0 of ${sample.populationCount || 0} included cases have an assigned allocation.`;
  }

  const median = Number.isFinite(metrics?.medianHistoricalAssignedAllocationMinutes)
    ? formatDurationMinutes(metrics.medianHistoricalAssignedAllocationMinutes)
    : "Unavailable";
  return `Median ${escapeHtml(median)} (${escapeHtml(`N=${sample.contributingCount || 0}`)}). ${renderAllocationValueCounts(metrics.historicalAssignedAllocationValues)}`;
}

  function renderAllocationValueCounts(values) {
  if (!Array.isArray(values) || values.length === 0) {
    return "No captured values.";
  }
  return values.map(value => {
    const count = value?.caseCount || 0;
    return `${escapeHtml(formatDurationMinutes(value?.minutes))} (${count} ${count === 1 ? "case" : "cases"})`;
  }).join(", ");
}

  function usefulCapturedDefaults(values, currentDefaultMinutes) {
  if (!Array.isArray(values) || values.length === 0) {
    return [];
  }
  return values.length > 1 || values.some(value => value?.minutes !== currentDefaultMinutes)
    ? values
    : [];
}

  function renderDoctorProcedureIntelligence(segments) {
  return `
    <section class="procedure-intelligence-doctors" aria-label="Doctor breakdown">
      <h4>Doctor breakdown</h4>
      <div class="procedure-intelligence-doctor-list">
        ${segments.map(segment => {
          const metrics = segment?.metrics || {};
          return `
            <article class="procedure-intelligence-doctor-row">
              <h5>${escapeHtml(segment?.doctorName || segment?.doctorId || "Unknown doctor")}</h5>
              <div>
                ${renderProcedureIntelligenceMetric("Completed N", String(metrics.completedCaseCount ?? 0), metrics.completedSample)}
                ${renderProcedureIntelligenceMetric("Median Doctor Time", formatObservedDuration(metrics.medianDoctorTimeSeconds), metrics.doctorTimeSample)}
                ${renderTypicalDoctorTimeRange(metrics)}
                ${renderProcedureIntelligenceMetric("Median Ready Wait", formatObservedDuration(metrics.medianReadyWaitSeconds), metrics.readyWaitSample)}
                ${renderProcedureIntelligenceMetric("Median Seated -> Doctor Complete", formatObservedDuration(metrics.medianSeatedToDoctorCompleteSeconds), metrics.seatedToDoctorCompleteSample)}
              </div>
            </article>`;
        }).join("")}
      </div>
    </section>`;
}

  function handleProcedureIntelligenceClick(event) {
  const button = event.target.closest(".procedure-intelligence-toggle");
  if (!button) {
    return false;
  }

  const key = button.dataset.procedureIntelligenceKey;
  const detailId = button.getAttribute("aria-controls");
  const detail = detailId ? document.getElementById(detailId) : null;
  const expanded = button.getAttribute("aria-expanded") !== "true";
  button.setAttribute("aria-expanded", String(expanded));
  button.textContent = expanded ? "Hide details" : "View details";
  if (detail) {
    detail.hidden = !expanded;
  }
  if (key) {
    if (expanded) {
      state.procedureIntelligenceExpanded.add(key);
    } else {
      state.procedureIntelligenceExpanded.delete(key);
    }
  }
  return true;
}

  function formatProcedureShare(share) {
  return Number.isFinite(share) ? `${Math.round(share * 100)}%` : "--";
}

  function renderAuditAction(contributorKind, label = "View contributing cases", options = {}) {
  return `<button type="button" class="secondary-button utility-button report-audit-action"
    data-audit-kind="${escapeAttribute(contributorKind)}"
    ${options.segmentDoctorId ? `data-audit-doctor="${escapeAttribute(options.segmentDoctorId)}"` : ""}
    ${options.procedureCode ? `data-audit-procedure="${escapeAttribute(options.procedureCode)}"` : ""}
    ${options.baseProcedureCode ? `data-audit-base-procedure="${escapeAttribute(options.baseProcedureCode)}"` : ""}
    ${options.evidenceIds ? `data-audit-evidence="${escapeAttribute(JSON.stringify(options.evidenceIds))}"` : ""}
    ${options.analyticalStanding ? `data-audit-standing="${escapeAttribute(options.analyticalStanding)}"` : ""}>
    ${escapeHtml(label)}
  </button>`;
}

  function auditSelection(r, contributorKind, options = {}) {
  const query = r?.query || {};
  return {
    from: query.rangeStartDate || null,
    to: query.rangeEndDate || null,
    scope: query.scope || "Practice",
    doctorId: query.doctorId || null,
    sedation: query.sedation || "All",
    procedureGrouping: query.procedureGrouping || "Family",
    contributorKind,
    segmentDoctorId: options.segmentDoctorId || null,
    procedureCode: options.procedureCode || null,
    baseProcedureCode: options.baseProcedureCode || null,
    analyticalStanding: options.analyticalStanding || "All",
    evidenceIds: options.evidenceIds || [],
    sort: options.sort || "MostRecent",
    offset: Number.isFinite(options.offset) ? options.offset : 0,
    limit: Number.isFinite(options.limit) ? options.limit : 50
  };
}

  function auditViewSignature(selection) {
  return JSON.stringify({ ...selection, offset: 0 });
}

  async function ensureAuditView(viewId, targetId, selection, { append = false } = {}) {
  const signature = `${reportData.getVersion()}|${auditViewSignature(selection)}`;
  const current = state.auditViews.get(viewId);
  if (!append && current?.signature === signature && (current.loading || current.page || current.error)) {
    renderAuditView(viewId, targetId);
    return;
  }
  const entry = {
    signature,
    targetId,
    selection,
    loading: true,
    page: append ? current?.page || null : null,
    error: null
  };
  state.auditViews.set(viewId, entry);
  renderAuditView(viewId, targetId);
  try {
    if (typeof reportData.queryAudit !== "function") {
      throw new Error("Audit evidence is not available in this client.");
    }
    const page = await reportData.queryAudit(selection);
    if (state.auditViews.get(viewId) !== entry || !page) {
      return;
    }
    entry.selection = normalizedAuditSelection(entry.selection, page.normalizedSelection);
    entry.page = append && entry.page
      ? {
          ...page,
          rows: [...(entry.page.rows || []), ...(page.rows || [])],
          reviewRows: [...(entry.page.reviewRows || []), ...(page.reviewRows || [])]
        }
      : page;
  } catch (error) {
    if (state.auditViews.get(viewId) === entry) {
      entry.error = error?.message || "Audit evidence could not be loaded.";
    }
  } finally {
    if (state.auditViews.get(viewId) === entry) {
      entry.loading = false;
      renderAuditView(viewId, targetId);
    }
  }
}

  function normalizedAuditSelection(requestSelection, normalized) {
  if (!normalized?.query) {
    return requestSelection;
  }
  return {
    ...requestSelection,
    from: normalized.query.rangeStartDate || null,
    to: normalized.query.rangeEndDate || null,
    scope: normalized.query.scope || requestSelection.scope,
    doctorId: normalized.query.doctorId || null,
    sedation: normalized.query.sedation || requestSelection.sedation,
    procedureGrouping: normalized.query.procedureGrouping || requestSelection.procedureGrouping,
    contributorKind: normalized.contributorKind || requestSelection.contributorKind,
    segmentDoctorId: normalized.segmentDoctorId || null,
    procedureCode: normalized.procedureCode || null,
    baseProcedureCode: normalized.baseProcedureCode || null,
    analyticalStanding: normalized.analyticalStanding || requestSelection.analyticalStanding,
    evidenceIds: normalized.evidenceIds || requestSelection.evidenceIds
  };
}

  function renderAuditEvidence(r) {
  const kind = r.query?.scope === "Doctor" ? "IncludedCompletedCases" : "PracticeCompletedCases";
  queueMicrotask(() => ensureAuditView("primary", "reportAuditBody", auditSelection(r, kind)));
}

  function renderReviewEvidence(r) {
  const quality = r.dataQuality || {};
  const pendingCount = quality.pendingReviewCount ?? (r.exceptionReviewRecords || r.exceptionCycles || []).length;
  const reviewedCount = quality.reviewedExceptionCount ?? 0;
  const pendingLabel = document.getElementById("reportReviewQueueCount");
  const reviewedLabel = document.getElementById("reportReviewedHistoryCount");
  if (pendingLabel) pendingLabel.textContent = `(${pendingCount})`;
  if (reviewedLabel) reviewedLabel.textContent = `(${reviewedCount})`;
  queueMicrotask(() => ensureAuditView("pending", "reportReviewQueueBody", auditSelection(r, "PendingReview")));
  queueMicrotask(() => ensureAuditView("reviewed", "reportReviewedHistoryBody", auditSelection(r, "ReviewedExceptionHistory")));
}

  function renderAuditView(viewId, targetId) {
  const target = document.getElementById(targetId);
  const entry = state.auditViews.get(viewId);
  if (!target || !entry) {
    return;
  }
  if (entry.error) {
    target.innerHTML = `<p class="report-table-context">${escapeHtml(entry.error)}</p>`;
    return;
  }
  if (!entry.page) {
    target.innerHTML = `<p class="report-table-context">Loading source-backed evidence...</p>`;
    return;
  }
  const page = entry.page;
  const rows = page.mode === "ExceptionReview"
    ? renderReviewAuditRows(page.reviewRows || [], viewId === "pending")
    : renderCompletedAuditRows(page.rows || []);
  const sortOptions = (page.supportedSorts || []).map(sort =>
    `<option value="${escapeAttribute(sort)}" ${sort === page.activeSort ? "selected" : ""}>${escapeHtml(formatAuditSort(sort))}</option>`
  ).join("");
  const standingFilter = page.mode === "ExceptionReview" ? "" : `
      <label>Standing
        <select data-audit-standing-filter data-audit-view="${escapeAttribute(viewId)}">
          ${["All", "Included", "ReportingExcluded"].map(value => `<option value="${value}" ${entry.selection.analyticalStanding === value ? "selected" : ""}>${formatAuditSort(value)}</option>`).join("")}
        </select>
      </label>`;
  const modeLabel = page.mode === "MetricEvidence"
    ? "Metric evidence"
    : page.mode === "ExceptionReview"
      ? "Exception review evidence"
      : "Completed-case audit";
  const visibleCount = page.mode === "ExceptionReview"
    ? (page.reviewRows || []).length
    : (page.rows || []).length;
  target.innerHTML = `
    <div class="report-audit-toolbar">
      <p><strong>${modeLabel}</strong> - ${page.totalMatchingCount} matching ${page.totalMatchingCount === 1 ? "record" : "records"}; showing ${visibleCount}.</p>
      <label>Sort <select data-audit-sort data-audit-view="${escapeAttribute(viewId)}">${sortOptions}</select></label>
      ${standingFilter}
    </div>
    <div class="report-audit-list">${rows || `<p class="report-table-context">No evidence matches this selection.</p>`}</div>
    ${page.hasMore ? `<button type="button" class="secondary-button utility-button" data-audit-load-more data-audit-view="${escapeAttribute(viewId)}">Load more</button>` : ""}
    ${entry.loading ? `<p class="report-table-context">Loading more evidence...</p>` : ""}`;
}

  function renderCompletedAuditRows(rows) {
  return rows.map(row => `
    <details class="report-audit-row">
      <summary>
        <span>${formatDateTime(row.doctorCompleteAt)}</span>
        <span>Room ${row.roomId}</span>
        <span>${escapeHtml(row.doctorName || row.doctorId)}</span>
        <span>${escapeHtml(row.procedureLabel || row.procedureCode)}</span>
        <span>Ready ${formatObservedDuration(row.readyWaitSeconds)}</span>
        <span>Doctor ${formatObservedDuration(row.doctorTimeSeconds)}</span>
        <span>Fit ${formatAllocationMinutes(row.expectedAllocationMinutes)} / ${formatObservedDuration(row.exactObservedScheduleFitSeconds)} / ${formatExactVariance(row.exactScheduleFitVarianceSeconds)}</span>
        <span>${escapeHtml(row.analyticalStanding)}</span>
      </summary>
      <dl class="report-audit-facts">
        <div><dt>Prestage</dt><dd>${formatDateTime(row.prestageStartedAt)}</dd></div>
        <div><dt>Seated</dt><dd>${formatDateTime(row.seatedAt)}</dd></div>
        <div><dt>Accepted Ready</dt><dd>${formatDateTime(row.readyForDoctorAt)}</dd></div>
        <div><dt>Doctor Arrived</dt><dd>${formatDateTime(row.doctorArrivedAt)}</dd></div>
        <div><dt>Doctor Complete</dt><dd>${formatDateTime(row.doctorCompleteAt)}</dd></div>
        <div><dt>Room Available</dt><dd>${formatDateTime(row.roomAvailableAt)}</dd></div>
        <div><dt>Prep</dt><dd>${formatObservedDuration(row.prepSeconds)}</dd></div>
        <div><dt>Seated -> Doctor</dt><dd>${formatObservedDuration(row.seatedToDoctorSeconds)}</dd></div>
        <div><dt>Ready Wait</dt><dd>${formatObservedDuration(row.readyWaitSeconds)}</dd></div>
        <div><dt>Doctor Time</dt><dd>${formatObservedDuration(row.doctorTimeSeconds)}</dd></div>
        <div><dt>Seated -> Complete</dt><dd>${formatObservedDuration(row.seatedToDoctorCompleteSeconds)}</dd></div>
        <div><dt>Turnover</dt><dd>${formatObservedDuration(row.turnoverSeconds)}</dd></div>
        <div><dt>Total cycle</dt><dd>${formatObservedDuration(row.totalRoomCycleSeconds)}</dd></div>
        <div><dt>Expected</dt><dd>${formatAllocationMinutes(row.expectedAllocationMinutes)}</dd></div>
        <div><dt>Exact variance</dt><dd>${formatExactVariance(row.exactScheduleFitVarianceSeconds)}</dd></div>
        <div><dt>Captured default</dt><dd>${row.originalDefaultExpectedUnits ? `${row.originalDefaultExpectedUnits} units` : "--"}</dd></div>
        <div><dt>Add-on</dt><dd>${row.isAddOn ? "Yes" : "No"}</dd></div>
        <div><dt>Ready urgency</dt><dd>Aging ${row.agingThresholdReached ? "Yes" : "No"}; stale ${row.staleThresholdReached ? "Yes" : "No"}</dd></div>
        <div><dt>Evidence identity</dt><dd>Cycle ${row.completedCycleId}${row.acceptedReadyHandoffId ? `; handoff ${escapeHtml(row.acceptedReadyHandoffId)}` : ""}</dd></div>
      </dl>
      ${row.calibrationEvidence ? `<dl class="report-audit-facts calibration-evidence-facts">
        <div><dt>Calibration baseline</dt><dd>${escapeHtml(row.calibrationEvidence.baselineSource)} - ${row.calibrationEvidence.baselineMinutesUsed} min</dd></div>
        <div><dt>Observed exact</dt><dd>${formatExactSeconds(row.calibrationEvidence.observedCaseFlowSeconds)}</dd></div>
        <div><dt>Paired variance</dt><dd>${formatExactVariance(row.calibrationEvidence.pairedVarianceSeconds)}</dd></div>
        <div><dt>Raw direction</dt><dd>${escapeHtml(row.calibrationEvidence.rawDirection)}</dd></div>
        <div><dt>Tolerance</dt><dd>${escapeHtml(row.calibrationEvidence.toleranceClassification)}</dd></div>
      </dl>` : ""}
      ${(row.reportingExclusionReasons || []).length ? `<p class="report-table-context">Excluded: ${escapeHtml(row.reportingExclusionReasons.join("; "))}</p>` : ""}
      ${row.canMarkException ? `<button class="secondary-button utility-button" data-action="mark-exception" data-completed-cycle-id="${row.completedCycleId}" data-room-id="${row.roomId}" data-seated-at="${escapeAttribute(row.seatedAt || "")}">Mark Exception</button>` : ""}
    </details>`).join("");
}

  function renderReviewAuditRows(rows, pending) {
  return rows.map(row => {
    const recordKey = reviewRecordKey(row.sourceType, row.reviewRecordId);
    return `<details class="report-audit-row" data-report-action-row data-report-record-key="${escapeAttribute(recordKey)}">
      <summary><span>Room ${row.roomId}</span><span>${escapeHtml(row.doctorName)}</span><span>${escapeHtml(row.procedureLabel)}</span><span>${formatDateTime(row.reviewAnchor)}</span><span>${escapeHtml(row.reviewStatus)}</span></summary>
      <dl class="report-audit-facts">
        <div><dt>Source</dt><dd>${escapeHtml(row.sourceType)}</dd></div>
        <div><dt>Final state</dt><dd>${escapeHtml(row.finalState || "--")}</dd></div>
        <div><dt>Reason</dt><dd>${escapeHtml(row.reason || "--")}</dd></div>
        <div><dt>Suggested action</dt><dd>${escapeHtml(row.suggestedAction || "--")}</dd></div>
        <div><dt>Reviewed</dt><dd>${formatDateTime(row.reviewedAt)}</dd></div>
      </dl>
      ${pending ? `<button class="secondary-button utility-button" data-action="confirm-exclusion" data-review-source="${escapeAttribute(row.sourceType)}" data-review-record-id="${row.reviewRecordId}" data-room-id="${row.roomId}" data-report-record-key="${escapeAttribute(recordKey)}">Confirm Exclusion</button>` : ""}
    </details>`;
  }).join("");
}

  function formatAuditSort(value) {
  return String(value || "").replace(/([a-z])([A-Z])/g, "$1 $2");
}

  function formatExactVariance(seconds) {
  if (!Number.isFinite(seconds)) return "--";
  const sign = seconds > 0 ? "+" : "";
  return `${sign}${formatExactNumber(seconds)} sec`;
}

  function formatExactSeconds(seconds) {
  return Number.isFinite(seconds) ? `${formatExactNumber(seconds)} sec` : "--";
}

  function formatExactNumber(value) {
  return Number.isInteger(value)
    ? String(value)
    : value.toFixed(3).replace(/0+$/, "").replace(/\.$/, "");
}

  function renderSelectedDoctorAudit(r, doctorId) {
  queueMicrotask(() => ensureAuditView(
    "doctor",
    "doctorAuditBody",
    auditSelection(r, "IncludedCompletedCases", { segmentDoctorId: doctorId })
  ));
  return `<div class="selected-doctor-audit" id="doctorAuditBody" aria-live="polite"><p>Loading source-backed case audit...</p></div>`;
}

  function renderProcedureAllocation(r) {
  const list = document.getElementById("procedureAllocationList");
  if (!list) {
    return;
  }

  const segments = Array.isArray(r.scheduleFit?.procedureSegments)
    ? r.scheduleFit.procedureSegments
    : [];

  list.innerHTML = segments.length
    ? segments.map(segment => renderScheduleFitSegment(segment, { includeDoctorBreakdown: true })).join("")
    : `<p class="allocation-empty">No procedure Schedule Fit data for this range.</p>`;
}

  function renderScheduleFitSegment(segment, { includeDoctorBreakdown = false } = {}) {
  const historical = segment?.historicalAssignedFit;
  if (!historical) {
    return "";
  }
  const context = segment.procedureGrouping === "DetailedVariant"
    ? (segment.isSedationCase ? "Detailed variant · Sedation" : "Detailed variant")
    : "Procedure family";
  const procedureOptions = segment.procedureGrouping === "DetailedVariant"
    ? { procedureCode: segment.procedureCode }
    : { baseProcedureCode: segment.baseProcedureCode || segment.procedureCode };
  const doctorBreakdown = includeDoctorBreakdown && Array.isArray(segment.doctorBreakdown)
    && segment.doctorBreakdown.length
    ? `
      <details class="schedule-fit-doctor-breakdown">
        <summary>Doctor × procedure detail</summary>
        <div class="schedule-fit-doctor-segments">
          ${segment.doctorBreakdown.map(doctor => `
            <article class="schedule-fit-doctor-segment">
              <h5>${escapeHtml(doctor.doctorName || getDoctorName(doctor.doctorId))}</h5>
              <p>${escapeHtml(formatScheduleFitCoverage(doctor.historicalAssignedFit))}; ${scheduleFitPresentationState(doctor.historicalAssignedFit).measurementsAvailable
                ? `historical assigned net ${escapeHtml(formatSignedScheduleFitSeconds(doctor.historicalAssignedFit.netVarianceSeconds))}`
                : `historical assigned measurements: ${escapeHtml(scheduleFitPresentationState(doctor.historicalAssignedFit).value)}`}.</p>
              ${renderAuditAction("HistoricalScheduleFit", `View paired cases for ${doctor.doctorName || getDoctorName(doctor.doctorId)} and ${segment.procedureLabel || segment.procedureCode}`, { ...procedureOptions, segmentDoctorId: doctor.doctorId })}
              ${renderCalibrationEvaluation(doctor.currentDefaultCalibration, { ...procedureOptions, segmentDoctorId: doctor.doctorId })}
            </article>`).join("")}
        </div>
      </details>`
    : "";
  return `
    <article class="allocation-row schedule-fit-segment">
      <div class="schedule-fit-segment-head">
        <span class="allocation-row-name">${escapeHtml(segment.procedureLabel || segment.procedureCode || "Unknown")}</span>
        <small>${escapeHtml(context)}</small>
      </div>
      <div class="allocation-row-detail">
        <strong>${escapeHtml(formatScheduleFitCoverage(historical))}</strong>
        ${scheduleFitPresentationState(historical).measurementsAvailable ? `
          <span>Historical assigned: expected ${escapeHtml(formatScheduleFitAmount(historical.totalExpectedSeconds))} · observed ${escapeHtml(formatScheduleFitAmount(historical.totalObservedSeconds))}</span>
          <small>Net ${escapeHtml(formatSignedScheduleFitSeconds(historical.netVarianceSeconds))} · slack ${escapeHtml(formatObservedDuration(historical.totalSlackSeconds))} · debt ${escapeHtml(formatObservedDuration(historical.totalDebtSeconds))}</small>` : `
          <span class="allocation-empty">Historical assigned measurements: ${escapeHtml(scheduleFitPresentationState(historical).value)}.</span>`}
        ${renderAuditAction("HistoricalScheduleFit", `View paired cases for ${segment.procedureLabel || segment.procedureCode}`, procedureOptions)}
        ${renderCalibrationEvaluation(segment.currentDefaultCalibration, procedureOptions)}
        ${doctorBreakdown}
      </div>
    </article>`;
}

  function renderCalibrationEvaluation(evaluation, auditOptions = {}) {
  if (!evaluation) {
    return "";
  }
  if (evaluation.decision === "CurrentDefaultUnavailable") {
    return `<p class="calibration-context">Current roster default unavailable. Historical assigned Schedule Fit remains available.</p>`;
  }

  const baseline = Number.isFinite(evaluation.currentDefaultAllocationMinutes)
    ? `${evaluation.currentDefaultAllocationMinutes} min`
    : "Unavailable";
  const insight = evaluation.insight;
  if (!insight) {
    return `<p class="calibration-context">Current roster default: ${escapeHtml(baseline)}.</p>`;
  }

  const isMore = insight.direction === "MoreTimeThanCurrentDefault";
  const relation = isMore ? "above" : "below";
  const rawRelation = isMore ? "above" : "below";
  return `
    <aside class="calibration-insight" aria-label="Calibration insight">
      <strong>Calibration insight</strong>
      <p>Observed case flow was typically about ${escapeHtml(formatObservedDuration(Math.abs(insight.medianDifferenceSeconds)))} ${relation} the current ${escapeHtml(baseline)} roster default in this selected population.</p>
      <p>${escapeHtml(String(insight.directionalCaseCount))} of ${escapeHtml(String(insight.totalPairedCaseCount))} cases were ${rawRelation} the current roster default. Review the scheduling assumption.</p>
      ${renderCalibrationEvidence(insight, auditOptions)}
    </aside>`;
}

  function renderCalibrationEvidence(insight, auditOptions = {}) {
  const evidence = Array.isArray(insight?.evidence) ? insight.evidence : [];
  if (!evidence.length) {
    return "";
  }
  const evidenceIds = evidence.map(item => ({
    completedCycleId: item.completedCycleId,
    acceptedReadyHandoffId: item.acceptedReadyHandoffId
      ? String(item.acceptedReadyHandoffId)
      : null
  }));
  return `
    <p class="calibration-evidence-summary">${evidence.length} exact paired case ${evidence.length === 1 ? "record" : "records"}; evidence identities are server-qualified.</p>
    ${renderAuditAction("CalibrationEvidence", "Review contributing cases", { ...auditOptions, evidenceIds })}`;
}

  function scheduleFitPresentationState(summary) {
  const state = summary?.sample?.state;
  if (state === "Empty") {
    return { state, measurementsAvailable: false, value: "No observation" };
  }
  if (state === "Limited" || state === "Sufficient") {
    return { state, measurementsAvailable: true, value: null };
  }
  return { state: "Unavailable", measurementsAvailable: false, value: "Unavailable" };
}

  function renderScheduleFitKpis(summary, className = "schedule-fit-kpis") {
  const presentation = scheduleFitPresentationState(summary);
  if (!presentation.measurementsAvailable) {
    return `<p class="allocation-empty">Historical assigned Schedule Fit measurements: ${escapeHtml(presentation.value)}.</p>`;
  }
  return `
    <dl class="${escapeAttribute(className)}">
      <div><dt>Expected scheduling allocation</dt><dd>${escapeHtml(formatScheduleFitAmount(summary.totalExpectedSeconds))}</dd></div>
      <div><dt>Observed case flow</dt><dd>${escapeHtml(formatScheduleFitAmount(summary.totalObservedSeconds))}</dd></div>
      <div><dt>Total scheduling slack</dt><dd>${escapeHtml(formatScheduleFitAmount(summary.totalSlackSeconds))}</dd></div>
      <div><dt>Total scheduling debt</dt><dd>${escapeHtml(formatScheduleFitAmount(summary.totalDebtSeconds))}</dd></div>
      <div><dt>Signed net difference</dt><dd>${escapeHtml(formatSignedScheduleFitSeconds(summary.netVarianceSeconds))}</dd></div>
    </dl>`;
}

  function formatScheduleFitCoverage(summary) {
  if (!summary) {
    return "Schedule Fit unavailable";
  }
  const population = Number(summary.populationCount) || 0;
  const paired = Number(summary.pairedCaseCount) || 0;
  const coverage = Number.isFinite(summary.populationCoverage)
    ? summary.populationCoverage
    : population === 0 ? 0 : paired / population;
  return `${paired} of ${population} included completed ${population === 1 ? "case" : "cases"} (${Math.round(coverage * 100)}% coverage)`;
}

  function formatScheduleFitAmount(seconds) {
  if (!Number.isFinite(seconds)) {
    return "--";
  }
  const blocks = Math.round((seconds / 600) * 10) / 10;
  return `${formatObservedDuration(seconds)} (${blocks} ${blocks === 1 ? "block" : "blocks"})`;
}

  function formatSignedScheduleFitSeconds(seconds) {
  if (!Number.isFinite(seconds)) {
    return "--";
  }
  const sign = seconds > 0 ? "+" : seconds < 0 ? "-" : "";
  return `${sign}${formatObservedDuration(Math.abs(seconds))}`;
}

// Semantic color class for an allocation variance value, by operational meaning (not ranking):
// over expected = warm/red, under expected = green, at expected = neutral, not calculable = muted.
  function varianceClass(minutes) {
  if (!Number.isFinite(minutes)) {
    return "variance-none";
  }
  if (minutes > 0) {
    return "variance-over";
  }
  if (minutes < 0) {
    return "variance-under";
  }
  return "variance-at";
}

// Colored variance label keeping the explicit "over expected" / "under expected" wording.
  function renderVarianceBadge(minutes) {
  return `<span class="variance ${varianceClass(minutes)}">${escapeHtml(formatAllocationVariance(minutes))}</span>`;
}

  function renderReportFilterBar() {
  const bar = document.getElementById("reportFilterBar");
  if (bar) {
    bar.hidden = false;
  }
}

  function sampleSupportsComparison(sample) {
  return sample?.state === "Sufficient" && sample.supportsComparison !== false;
}

// Reflects state.reportFilters onto the static filter chips so re-renders never desync the
// pressed state from the stored filter.
  function syncReportFilterButtons() {
  const query = reportData.getQuery?.() || {
    scope: "Practice",
    doctorId: null
  };
  document.querySelectorAll("#reportFilterBar .report-filter-chip").forEach(chip => {
    const value = normalizeReportFilterValue(chip.dataset.filterGroup, chip.dataset.filterValue);
    const active = state.reportFilters[chip.dataset.filterGroup] === value;
    chip.setAttribute("aria-pressed", String(active));
    chip.classList.toggle("is-active", active);
  });
  document.querySelectorAll("#reportFilterBar [data-report-scope]").forEach(chip => {
    const active = query.scope === chip.dataset.reportScope;
    chip.setAttribute("aria-pressed", String(active));
    chip.classList.toggle("is-active", active);
  });

  syncDoctorScopeControl(reportData.getReports());
}

  function normalizeReportFilterValue(group, value) {
  if (group === "sedation") {
    if (value === "sedation") return "Sedation";
    if (value === "non-sedation") return "NonSedation";
    return value === "Sedation" || value === "NonSedation" ? value : "All";
  }
  if (group === "grouping") {
    return value === "variant" || value === "DetailedVariant" ? "DetailedVariant" : "Family";
  }
  return value;
}

  function syncDoctorScopeControl(r) {
  for (const doctor of getSnapshot()?.doctors || []) {
    state.reportScopeDoctors.set(doctor.id, doctor.name);
  }
  for (const summary of r?.doctorSummaries || []) {
    if (summary.assignedDoctor) {
      state.reportScopeDoctors.set(summary.assignedDoctor, getDoctorName(summary.assignedDoctor));
    }
  }
  for (const row of r?.doctorProcedureMix || []) {
    if (row.doctorId) {
      state.reportScopeDoctors.set(row.doctorId, getDoctorName(row.doctorId));
    }
  }
  if (r?.query?.doctorId) {
    state.reportScopeDoctors.set(r.query.doctorId, getDoctorName(r.query.doctorId));
  }

  const field = document.getElementById("reportScopeDoctorField");
  const select = document.getElementById("reportScopeDoctor");
  const query = reportData.getQuery?.() || {
    scope: "Practice",
    doctorId: null
  };
  if (field) {
    field.hidden = query.scope !== "Doctor";
  }
  if (!select) {
    return;
  }

  const selected = query.doctorId || state.reportDoctorId || "";
  select.innerHTML = [...state.reportScopeDoctors.entries()]
    .map(([id, name]) => `<option value="${escapeAttribute(id)}">${escapeHtml(name)}</option>`)
    .join("");
  if (selected && state.reportScopeDoctors.has(selected)) {
    select.value = selected;
  }
}

// Chooses between backend-provided grouping projections. Doctor and sedation filters have already
// selected the analytical population on the server; grouping only changes its aggregation lens.
  function getInsightSummaries(r) {
  const variants = r.procedureSummaries || [];
  return state.reportFilters.grouping === "Family"
    ? (r.baseProcedureSummaries || [])
    : variants;
}

  function insightsHeadingText() {
  if (state.reportFilters.sedation === "Sedation") {
    return "Sedation cases by procedure";
  }
  if (state.reportFilters.sedation === "NonSedation") {
    return "Non-sedation cases by procedure";
  }
  return state.reportFilters.grouping === "Family"
    ? "Procedure insights — procedure family"
    : "Procedure insights — detailed variant";
}

  function renderGroupedInsights(r, hasData) {
  const section = document.getElementById("reportInsights");
  const grid = document.getElementById("reportInsightsGrid");
  const heading = document.getElementById("reportInsightsHeading");
  if (!section || !grid) {
    return;
  }

  section.hidden = !hasData;
  if (!hasData) {
    grid.innerHTML = "";
    return;
  }

  if (heading) {
    heading.textContent = insightsHeadingText();
  }

  const summaries = getInsightSummaries(r);
  grid.innerHTML = summaries.length
    ? summaries.map(renderInsightCard).join("")
    : `<p class="report-empty-note">No cases match the selected filter.</p>`;
}

// One insight card per procedure group. Labels come from the backend-resolved ProcedureLabel
// (so legacy "SED" reads "Sedation" and composites read "Extraction + Sedation"); the code
// badge uses formatProcedureCode ("EXT+SED" -> "EXT + SED"). Cards only render when cases
// exist, so every duration here is a real measured value.
  function renderInsightCard(summary) {
  const code = procedure.formatCode(summary.procedureCode) || "--";
  const label = summary.procedureLabel || code || "Unknown";
  const sedationChip = summary.isSedationCase
    ? `<span class="sedation-chip">Sedation</span>`
    : "";
  return `
    <article class="insight-card" style="${procedure.accentStyle(summary.procedureCode)}">
      <div class="insight-card-head">
        <span class="insight-code">${escapeHtml(code)}</span>
        ${sedationChip}
      </div>
      <h3 class="insight-label">${escapeHtml(label)}</h3>
      <dl class="insight-metrics">
        ${renderInsightMetric("Cases", String(summary.completedCycleCount), summary.samples?.completedCases)}
        ${renderInsightMetric("Avg Total", formatDuration(summary.averageTotalSeconds), summary.samples?.total)}
        ${renderInsightMetric("Median Total", formatDuration(summary.medianTotalSeconds), summary.samples?.total)}
        ${renderInsightMetric("Avg Doctor Time", formatDuration(summary.averageDoctorTimeSeconds), summary.samples?.doctorTime)}
        ${renderInsightMetric("Avg Ready-to-Doctor", formatDuration(summary.averageReadyToDoctorSeconds), summary.samples?.readyWait)}
      </dl>
    </article>
  `;
}

// Full metric set (kept behind the "All metrics" expander). Duration metrics show "—" when
// there is no completed-cycle data; counts always show their real number (a genuine 0 stays 0).
  function renderFullMetrics(r, hasData) {
  const summary = document.getElementById("reportSummary");
  if (!summary) {
    return;
  }

  const dur = seconds => (hasData ? formatDuration(seconds) : "No observation");
  summary.innerHTML = [
    renderMetric("Completed Cycles", r.completedRoomCyclesCount, "Room cycles that reached completion and are available for reporting.", r.samples?.completedCases),
    renderMetric("Sedation Cases", r.sedationCaseCount, null, r.samples?.includedCompletedCases),
    renderMetric("Non-sedation Cases", r.nonSedationCaseCount, null, r.samples?.includedCompletedCases),
    renderMetric("Avg Prep Time", dur(r.averagePrepSeconds), null, r.samples?.prep),
    renderMetric("Median Prep Time", dur(r.medianPrepSeconds), null, r.samples?.prep),
    renderMetric("Avg Ready-to-Doctor Wait", dur(r.averageReadyToDoctorSeconds), null, r.samples?.readyWait),
    renderMetric("Median Ready-to-Doctor Wait", dur(r.medianReadyToDoctorSeconds), null, r.samples?.readyWait),
    renderMetric("Avg Doctor Occupied Wait", dur(r.averageDoctorOccupiedWaitSeconds), "Time a patient was ready while the doctor was already active in another room.", r.samples?.doctorOccupiedWait),
    renderMetric("Median Doctor Occupied Wait", dur(r.medianDoctorOccupiedWaitSeconds), null, r.samples?.doctorOccupiedWait),
    renderMetric("Avg Doctor Available Wait", dur(r.averageDoctorAvailableWaitSeconds), "Time a patient was ready while the doctor was not occupied in another active room.", r.samples?.doctorAvailableWait),
    renderMetric("Median Doctor Available Wait", dur(r.medianDoctorAvailableWaitSeconds), null, r.samples?.doctorAvailableWait),
    renderMetric("Avg Total to Doctor", dur(r.averageSeatedToDoctorSeconds), null, r.samples?.seatedToDoctor),
    renderMetric("Median Total to Doctor", dur(r.medianSeatedToDoctorSeconds), null, r.samples?.seatedToDoctor),
    renderMetric("Avg In Room", dur(r.averageDoctorInRoomSeconds), null, r.samples?.doctorTime),
    renderMetric("Median In Room", dur(r.medianDoctorInRoomSeconds), null, r.samples?.doctorTime),
    renderMetric("Avg Turnover", dur(r.averageTurnoverSeconds), "Time from Doctor Complete until the room is marked Available.", r.samples?.turnover),
    renderMetric("Median Turnover", dur(r.medianTurnoverSeconds), null, r.samples?.turnover),
    renderMetric("Aging Events", r.agingEventCount, "Ready-room wait has crossed the aging threshold and may need attention.", r.samples?.includedCompletedCases),
    renderMetric("Stale Events", r.staleEventCount, "Ready-room wait has crossed the stale threshold and should be treated as higher priority.", r.samples?.includedCompletedCases)
  ].join("");
}

  function renderCompletedCycles(cycles) {
  const body = document.getElementById("completedCyclesBody");
  if (!body) {
    return;
  }

  // Token covers every client-side input that affects cycle row HTML:
  //   reportsVersion — new payload arrived; sedation — filter changes visible rows.
  // grouping does not affect cycle rows (it only affects insight summaries).
  const token = String(reportData.getVersion());
  if (body.dataset.renderKey === token) {
    return;
  }
  if (state.reportPressActive) {
    return;
  }
  body.dataset.renderKey = token;
  body.innerHTML = cycles.length
    ? cycles.map(renderCycleRow).join("")
    : `<tr><td colspan="23">${escapeHtml(noMatchMessage("No completed room cycles yet."))}</td></tr>`;
}

// Allocation minutes cell: "30 min" when present and positive, otherwise "--". Used for the raw
// expected/measured columns in the completed-cycle audit table.
  function formatAllocationMinutes(minutes) {
  return Number.isFinite(minutes) && minutes > 0 ? `${minutes} min` : "--";
}

// Human-readable duration from a minute value, for report copy that must never expose raw decimals
// (e.g. a client-computed average). Non-finite/null -> "--". Always rounds to the nearest whole
// minute. Under 90 minutes reads as "42 min"; 90 minutes or more reads as "8 hr 30 min" (or "8 hr"
// on the hour). A negative value keeps its sign ("-1 hr 10 min") for signed metrics.
  function formatDurationMinutes(value) {
  if (!Number.isFinite(value)) {
    return "--";
  }
  const sign = value < 0 ? "-" : "";
  const totalMinutes = Math.round(Math.abs(value));
  if (totalMinutes < 90) {
    return `${sign}${totalMinutes} min`;
  }
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  return minutes === 0 ? `${sign}${hours} hr` : `${sign}${hours} hr ${minutes} min`;
}

// Neutral allocation variance label. Positive = over expected, negative = under, zero = at.
  function formatAllocationVariance(varianceMinutes) {
  if (!Number.isFinite(varianceMinutes)) {
    return "--";
  }
  if (varianceMinutes > 0) {
    return `+${varianceMinutes} min over expected`;
  }
  if (varianceMinutes < 0) {
    return `${varianceMinutes} min under expected`;
  }
  return "0 min at expected";
}

  function revealReportDisclosures() {
  const metrics = document.getElementById("reportMetrics");
  const detail = document.getElementById("reportDetail");
  if (metrics) {
    metrics.hidden = false;
  }
  if (detail) {
    detail.hidden = false;
  }
}

// Empty-row copy that reflects the selected server-side analytical scope.
  function noMatchMessage(defaultMessage) {
  return state.reportFilters.sedation === "All"
    ? defaultMessage
    : "No rows match the selected sedation filter.";
}

  function renderProcedureSummaries(summaries) {
  const body = document.getElementById("procedureSummariesBody");
  if (!body) {
    return;
  }

  const token = String(reportData.getVersion());
  if (body.dataset.renderKey === token) {
    return;
  }
  body.dataset.renderKey = token;
  body.innerHTML = summaries.length
    ? summaries.map(renderProcedureSummaryRow).join("")
    : `<tr><td colspan="8">${escapeHtml(noMatchMessage("No procedure baselines yet."))}</td></tr>`;
}

  function renderProcedureSummaryRow(summary) {
  return `
    <tr>
      <td>${escapeHtml(summary.procedureLabel || "Unknown")}</td>
      <td>${renderTableSampledValue(summary.samples?.completedCases, String(summary.completedCycleCount))}</td>
      <td>${renderTableSampledValue(summary.samples?.total, formatDuration(summary.averageTotalSeconds))}</td>
      <td>${renderTableSampledValue(summary.samples?.total, formatDuration(summary.medianTotalSeconds))}</td>
      <td>${renderTableSampledValue(summary.samples?.readyWait, formatDuration(summary.averageReadyToDoctorSeconds))}</td>
      <td>${renderTableSampledValue(summary.samples?.doctorTime, formatDuration(summary.averageDoctorTimeSeconds))}</td>
      <td>${renderTableSampledValue(summary.samples?.doctorAvailableWait, formatDuration(summary.averageDoctorAvailableWaitSeconds))}</td>
      <td>${renderTableSampledValue(summary.samples?.doctorOccupiedWait, formatDuration(summary.averageDoctorOccupiedWaitSeconds))}</td>
    </tr>
  `;
}

  function completedRecordKey(cycle) {
  const completedCycleId = Number(cycle.completedCycleId);
  if (Number.isInteger(completedCycleId) && completedCycleId > 0) {
    return `completed:${completedCycleId}`;
  }
  return `legacy:${cycle.roomId}:${normalizeReportIdentityTimestamp(cycle.seatedAt)}`;
}

  function reviewRecordKey(sourceType, reviewRecordId) {
  return sourceType === "AbortedAssignment"
    ? `aborted:${reviewRecordId}`
    : `completed:${reviewRecordId}`;
}

  function reportRangeSignature() {
  return reportData.getRangeSignature(reportData.getDateRange());
}

  function currentReportAction(recordKey) {
  return reportActionStates.get(recordKey) || null;
}

  function normalizeReportIdentityTimestamp(value) {
  const timestamp = Date.parse(value || "");
  return Number.isFinite(timestamp) ? new Date(timestamp).toISOString() : String(value || "").trim();
}

  function isUnresolvedReportAction(entry) {
  return entry?.phase === "pending"
    || entry?.phase === "definite-failure"
    || entry?.phase === "unknown-outcome"
    || entry?.phase === "refresh-failure";
}

  function unresolvedReportActionCount() {
  let count = 0;
  reportActionStates.forEach(entry => {
    if (isUnresolvedReportAction(entry)) {
      count++;
    }
  });
  return count;
}

  function canStartReportMutation(recordKey) {
  if (isUnresolvedReportAction(reportActionStates.get(recordKey))
      || unresolvedReportActionCount() < MAX_UNRESOLVED_REPORT_ACTIONS) {
    return true;
  }
  reportActionCapacityVisible = true;
  renderReportActionFeedback();
  return false;
}

  function isMutationLocked(entry, actionType = entry?.actionType) {
  if (!entry) {
    return false;
  }
  if (entry.phase === "success" && entry.actionType !== actionType) {
    return false;
  }
  return entry.mutationRetryAllowed !== true;
}

  function isCurrentOperation(entry) {
  return currentReportAction(entry.recordKey)?.operationId === entry.operationId;
}

  function reportActionLabel(entry) {
  if (entry.phase === "success") {
    return "Completed";
  }
  if (entry.phase === "definite-failure") {
    return "Could not complete";
  }
  if (entry.phase === "unknown-outcome") {
    return "Outcome uncertain";
  }
  if (entry.phase === "refresh-failure") {
    return "Refresh needed";
  }
  return "Working";
}

  function reportActionRegion(entry) {
  const assertive = entry.phase === "definite-failure"
    || entry.phase === "unknown-outcome"
    || entry.phase === "refresh-failure";
  return document.getElementById(
    assertive ? "reportActionStatusAssertive" : "reportActionStatusPolite");
}

  function createReportActionButton(action, recordKey, label) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "secondary-button utility-button";
  button.dataset.action = action;
  button.dataset.recordKey = recordKey;
  button.textContent = label;
  return button;
}

  function updateReportActionElement(element, entry) {
  element.className = "report-action-status-entry";
  element.dataset.recordKey = entry.recordKey;
  element.dataset.tone = entry.tone;
  element.dataset.operationId = String(entry.operationId);

  const label = document.createElement("strong");
  label.textContent = reportActionLabel(entry);
  const message = document.createElement("span");
  message.textContent = entry.message;
  const children = [label, message];

  if (entry.mutationRetryAllowed || entry.refreshRetryAllowed) {
    const controls = document.createElement("div");
    controls.className = "report-action-status-controls";
    if (entry.mutationRetryAllowed) {
      controls.append(createReportActionButton(
        "retry-report-mutation",
        entry.recordKey,
        "Try action again"));
    }
    if (entry.refreshRetryAllowed) {
      controls.append(createReportActionButton(
        "refresh-report-action",
        entry.recordKey,
        "Refresh reports"));
    }
    children.push(controls);
  }

  element.replaceChildren(...children);
}

  function renderReportActionCapacity(assertiveRegion) {
  let capacity = document.getElementById(REPORT_ACTION_CAPACITY_KEY);
  if (!reportActionCapacityVisible) {
    capacity?.remove();
    return;
  }
  if (!capacity) {
    capacity = document.createElement("article");
    capacity.id = REPORT_ACTION_CAPACITY_KEY;
    capacity.className = "report-action-status-entry";
    capacity.dataset.tone = "error";
    const label = document.createElement("strong");
    label.textContent = "Action limit reached";
    const message = document.createElement("span");
    message.textContent = "Resolve or refresh an existing report action before starting another.";
    capacity.replaceChildren(label, message);
    assertiveRegion.append(capacity);
  }
}

  function renderReportActionFeedback() {
  return renderReportActionFeedbackWithRetirement();
}

  function reportActionFeedbackOwnsFocus(wrapper) {
  const active = document.activeElement;
  return Boolean(active && (active === wrapper || wrapper.contains(active)));
}

  function usableReportFocusDestination(candidate) {
  return candidate
    && candidate.isConnected !== false
    && candidate.hidden !== true
    && candidate.disabled !== true
    ? candidate
    : null;
}

  function retireEmptyReportActionFeedback(
    wrapper,
    polite,
    assertive,
    focusDestination,
    focusWasOwned) {
  const empty = polite.childElementCount === 0 && assertive.childElementCount === 0;
  if (!empty) {
    wrapper.hidden = false;
    return;
  }

  if (focusWasOwned || reportActionFeedbackOwnsFocus(wrapper)) {
    const destination = usableReportFocusDestination(focusDestination)
      || usableReportFocusDestination(document.getElementById("reportsMain"));
    destination?.focus({ preventScroll: true });
    if (reportActionFeedbackOwnsFocus(wrapper)) {
      return;
    }
  }

  wrapper.hidden = true;
}

  function renderReportActionFeedbackWithRetirement({ focusDestination = null } = {}) {
  const wrapper = document.getElementById("reportActionFeedback");
  const polite = document.getElementById("reportActionStatusPolite");
  const assertive = document.getElementById("reportActionStatusAssertive");
  if (!wrapper || !polite || !assertive) {
    return;
  }
  const focusWasOwned = reportActionFeedbackOwnsFocus(wrapper);

  reportActionElements.forEach((element, recordKey) => {
    if (!reportActionStates.has(recordKey)) {
      element.remove();
      reportActionElements.delete(recordKey);
    }
  });

  [...reportActionStates.values()]
    .sort((left, right) => left.operationId - right.operationId)
    .forEach(entry => {
      let element = reportActionElements.get(entry.recordKey);
      if (!element) {
        element = document.createElement("article");
        reportActionElements.set(entry.recordKey, element);
      }
      const region = reportActionRegion(entry);
      const regionChanged = region && element.parentElement && element.parentElement !== region;
      const entryChanged = element.dataset.operationId !== String(entry.operationId)
          || element.dataset.phase !== entry.phase
          || element.dataset.tone !== entry.tone
          || element.dataset.message !== entry.message
          || element.dataset.mutationRetry !== String(entry.mutationRetryAllowed)
          || element.dataset.refreshRetry !== String(entry.refreshRetryAllowed);
      if (regionChanged && entryChanged) {
        element.remove();
      }
      if (entryChanged) {
        updateReportActionElement(element, entry);
        element.dataset.phase = entry.phase;
        element.dataset.message = entry.message;
        element.dataset.mutationRetry = String(entry.mutationRetryAllowed);
        element.dataset.refreshRetry = String(entry.refreshRetryAllowed);
      }
      if (region && element.parentElement !== region) {
        region.append(element);
      }
    });

  renderReportActionCapacity(assertive);
  retireEmptyReportActionFeedback(
    wrapper,
    polite,
    assertive,
    focusDestination,
    focusWasOwned);
}

  function syncReportActionControls(recordKey) {
  const entry = currentReportAction(recordKey);
  const controls = [...document.querySelectorAll("[data-report-record-key][data-action]")];
  if (entry?.focusOrigin?.dataset?.reportRecordKey === recordKey
      && !controls.includes(entry.focusOrigin)) {
    controls.push(entry.focusOrigin);
  }
  controls.forEach(control => {
    if (control.dataset.reportRecordKey !== recordKey) {
      return;
    }

    const shouldDisable = isMutationLocked(entry, control.dataset.action);
    if (shouldDisable && document.activeElement === control) {
      control.closest("[data-report-action-row]")?.focus({ preventScroll: true });
    }
    control.disabled = shouldDisable;
    const isMutationPending = entry?.phase === "pending" && entry.requestKind === "mutation";
    control.textContent = isMutationPending
      ? control.dataset.pendingLabel
      : control.dataset.defaultLabel;
  });
}

  function setReportActionState(entry) {
  if (entry.phase === "success") {
    reportActionStates.forEach((existing, key) => {
      if (key !== entry.recordKey && existing.phase === "success") {
        reportActionStates.delete(key);
      }
    });
  }

  reportActionStates.set(entry.recordKey, entry);
  if (unresolvedReportActionCount() < MAX_UNRESOLVED_REPORT_ACTIONS) {
    reportActionCapacityVisible = false;
  }
  renderReportActionFeedback();
  syncReportActionControls(entry.recordKey);
}

  function clearCompletedReportAction() {
  let changed = false;
  reportActionStates.forEach((entry, key) => {
    if (entry.phase === "success") {
      reportActionStates.delete(key);
      changed = true;
    }
  });
  if (changed) {
    renderReportActionFeedback();
  }
}

  function clearAllReportActions({ focusDestination = null } = {}) {
  const keys = [...reportActionStates.keys()];
  reportActionStates.clear();
  reportActionCapacityVisible = false;
  renderReportActionFeedbackWithRetirement({ focusDestination });
  keys.forEach(syncReportActionControls);
}

  function createPendingReportAction(descriptor, focusOrigin) {
  return {
    ...descriptor,
    operationId: ++nextReportActionOperationId,
    phase: "pending",
    message: descriptor.pendingMessage,
    tone: "pending",
    mutationRetryAllowed: false,
    refreshRetryAllowed: false,
    requestKind: "mutation",
    rangeSignature: reportRangeSignature(),
    focusOrigin
  };
}

  function captureReportActionFocusBeforeRender(completedCycles, exceptionCycles) {
  const active = document.activeElement;
  if (!active || active === document.body || active.isConnected === false) {
    return null;
  }
  const recordKey = active.dataset?.reportRecordKey
    || active.closest?.("[data-report-record-key]")?.dataset.reportRecordKey;
  const entry = currentReportAction(recordKey);
  if (!entry || !isCurrentOperation(entry)) {
    return null;
  }

  const actionWillRemain = entry.actionType === "mark-exception"
    ? completedCycles.some(cycle => completedRecordKey(cycle) === recordKey)
    : exceptionCycles.some(record => {
      const sourceType = record.sourceType || "CompletedCycle";
      const reviewRecordId = Number(
        record.reviewRecordId || record.completedCycleId || record.abortedAssignmentId || 0);
      return reviewRecordKey(sourceType, reviewRecordId) === recordKey;
    });
  if (actionWillRemain) {
    return null;
  }

  return {
    recordKey,
    operationId: entry.operationId,
    focusedElement: active
  };
}

  function completeReportActionFocusAfterRender(candidate) {
  if (!candidate) {
    return;
  }
  const current = currentReportAction(candidate.recordKey);
  if (!current
      || current.operationId !== candidate.operationId
      || candidate.focusedElement.isConnected !== false
      || document.activeElement !== document.body) {
    return;
  }
  document.getElementById("reportActionFeedback")?.focus();
}

  function mutationRequestOptions(entry) {
  const options = {
    method: "POST",
    cache: "no-store",
    headers: {
      ...adminRequestHeaders()
    }
  };
  if (entry.requestBody !== undefined) {
    options.headers["Content-Type"] = "application/json";
    options.body = JSON.stringify(entry.requestBody);
  }
  return options;
}

  function setUnknownOutcome(entry, error) {
  if (!isCurrentOperation(entry)) {
    return;
  }
  console.warn(`[ChairSide] ${entry.errorLogLabel}`, error);
  setReportActionState({
    ...entry,
    phase: "unknown-outcome",
    message: "The request outcome could not be confirmed. Refresh reports before trying this action again.",
    tone: "unknown",
    mutationRetryAllowed: false,
    refreshRetryAllowed: true,
    requestKind: null
  });
}

  async function setDefiniteFailure(entry, response) {
  if (!isCurrentOperation(entry)) {
    return;
  }

  let message;
  if (response.status === 401) {
    message = `${entry.roomLabel} could not be updated because Reports access is required. Reload Reports and enter the current internal token.`;
  } else if (response.status === 403) {
    message = `${entry.roomLabel} could not be updated because the saved Reports token was rejected. Reload Reports and enter authorized access.`;
  } else {
    const fallback = response.status === 404
      ? `${entry.roomLabel} is no longer available for this action. Refresh reports before trying again.`
      : `${entry.roomLabel} could not be updated. Refresh reports before trying again.`;
    try {
      message = await readErrorMessage(response, fallback);
    } catch {
      message = fallback;
    }
  }

  if (!isCurrentOperation(entry)) {
    return;
  }
  setReportActionState({
    ...entry,
    phase: "definite-failure",
    message,
    tone: "error",
    mutationRetryAllowed: false,
    refreshRetryAllowed: true,
    requestKind: null
  });
}

  async function executeReportMutation(entry) {
  let response;
  try {
    response = await request(entry.requestUrl, mutationRequestOptions(entry));
  } catch (error) {
    setUnknownOutcome(entry, error);
    return;
  }

  if (!isCurrentOperation(entry)) {
    return;
  }

  if (!response || !Number.isInteger(response.status)) {
    setUnknownOutcome(entry, new Error("Report action returned an invalid response."));
    return;
  }

  if (!response.ok) {
    if ([400, 401, 403, 404].includes(response.status)) {
      await setDefiniteFailure(entry, response);
    } else {
      setUnknownOutcome(entry, new Error(`HTTP ${response.status}`));
    }
    return;
  }

  if (response.status !== 204) {
    setUnknownOutcome(entry, new Error(`Unexpected successful HTTP ${response.status}`));
    return;
  }

  setReportActionState({
    ...entry,
    message: `${entry.mutationSuccessMessage} Refreshing reports...`,
    mutationCommitted: true
  });

  try {
    await reportData.reloadAfterCurrent();
  } catch (error) {
    if (!isCurrentOperation(entry)) {
      return;
    }
    console.warn("[ChairSide] Report action succeeded but refresh failed.", error);
    setReportActionState({
      ...entry,
      phase: "refresh-failure",
      message: "The action succeeded, but the reports could not refresh. Refresh reports to verify the updated row.",
      tone: "refresh",
      mutationCommitted: true,
      mutationRetryAllowed: false,
      refreshRetryAllowed: true,
      requestKind: null
    });
    return;
  }

  if (!isCurrentOperation(entry)) {
    return;
  }
  const success = {
    ...entry,
    phase: "success",
    message: entry.successMessage,
    tone: "success",
    mutationCommitted: true,
    mutationRetryAllowed: false,
    refreshRetryAllowed: false,
    requestKind: null
  };
  setReportActionState(success);
}

  function reportRecordMatchesMarkIdentity(entry, record) {
  if (entry.completedCycleId) {
    return Number(record.completedCycleId || record.reviewRecordId) === entry.completedCycleId;
  }
  return Number(record.roomId) === entry.roomId
    && normalizeReportIdentityTimestamp(record.seatedAt || record.startedAt)
      === normalizeReportIdentityTimestamp(entry.seatedAt);
}

  function markExceptionResolution(entry, reports) {
  const exceptionRecords = reports?.exceptionReviewRecords || reports?.exceptionCycles || [];
  const exceptionPresent = exceptionRecords.some(record =>
    (record.sourceType || "CompletedCycle") === "CompletedCycle"
    && reportRecordMatchesMarkIdentity(entry, record));
  if (exceptionPresent) {
    return "success";
  }
  const recentPresent = (reports?.recentCompletedCycles || []).some(record =>
    reportRecordMatchesMarkIdentity(entry, record));
  return recentPresent ? "unchanged" : "ambiguous";
}

  function confirmationActionStillApplicable(entry, reports) {
  return (reports?.exceptionReviewRecords || reports?.exceptionCycles || []).some(record =>
    (record.sourceType || "CompletedCycle") === entry.recordSource
    && Number(record.reviewRecordId || record.completedCycleId || record.abortedAssignmentId)
      === entry.reviewRecordId);
}

  function actionResolutionIsConclusive(entry, freshLoad) {
  return entry.recordSource === "AbortedAssignment"
    || entry.rangeSignature === freshLoad?.requestContext?.rangeSignature;
}

  async function reconcileReportAction(entry, focusOrigin) {
  if (!entry || !isCurrentOperation(entry)) {
    return;
  }

  const reconciliation = {
    ...entry,
    operationId: ++nextReportActionOperationId,
    phase: "pending",
    message: `Refreshing reports for ${entry.roomLabel}...`,
    tone: "refresh",
    mutationRetryAllowed: false,
    refreshRetryAllowed: false,
    requestKind: "refresh",
    focusOrigin
  };
  const focusOriginOwned = document.activeElement === focusOrigin;
  setReportActionState(reconciliation);
  if (focusOriginOwned) {
    document.getElementById("reportActionFeedback")?.focus();
  }

  let freshLoad;
  try {
    freshLoad = await reportData.reloadAfterCurrent();
  } catch (error) {
    if (!isCurrentOperation(reconciliation)) {
      return;
    }
    console.warn("[ChairSide] Report action reconciliation failed.", error);
    setReportActionState({
      ...reconciliation,
      phase: entry.mutationCommitted ? "refresh-failure" : entry.phase,
      message: entry.mutationCommitted
        ? "The action succeeded, but the reports could not refresh. Refresh reports to verify the updated row."
        : "Reports could not refresh. The action outcome is still unresolved.",
      tone: entry.mutationCommitted ? "refresh" : "unknown",
      mutationCommitted: entry.mutationCommitted,
      mutationRetryAllowed: false,
      refreshRetryAllowed: true,
      requestKind: null
    });
    return;
  }

  if (!isCurrentOperation(reconciliation)) {
    return;
  }

  if (entry.mutationCommitted) {
    setReportActionState({
      ...reconciliation,
      phase: "success",
      message: entry.successMessage,
      tone: "success",
      mutationCommitted: true,
      mutationRetryAllowed: false,
      refreshRetryAllowed: false,
      requestKind: null
    });
    return;
  }

  if (entry.actionType === "mark-exception") {
    const resolution = markExceptionResolution(entry, reportData.getReports());
    if (resolution === "success") {
      setReportActionState({
        ...reconciliation,
        phase: "success",
        message: entry.successMessage,
        tone: "success",
        mutationCommitted: true,
        mutationRetryAllowed: false,
        refreshRetryAllowed: false,
        requestKind: null
      });
      return;
    }
    if (resolution === "ambiguous") {
      setReportActionState({
        ...reconciliation,
        phase: "unknown-outcome",
        message: "Reports refreshed, but the record is not present in an authoritative action population. The request outcome is still uncertain.",
        tone: "unknown",
        mutationRetryAllowed: false,
        refreshRetryAllowed: true,
        requestKind: null
      });
      return;
    }
    setReportActionState({
      ...reconciliation,
      phase: "definite-failure",
      message: `Reports refreshed. ${entry.roomLabel} still allows this action, so it can be tried again.`,
      tone: "error",
      mutationRetryAllowed: true,
      refreshRetryAllowed: false,
      requestKind: null
    });
    return;
  }

  if (!actionResolutionIsConclusive(entry, freshLoad)) {
    setReportActionState({
      ...reconciliation,
      phase: "unknown-outcome",
      message: "Reports refreshed in a different range, so the request outcome is still uncertain.",
      tone: "unknown",
      mutationRetryAllowed: false,
      refreshRetryAllowed: true,
      requestKind: null
    });
    return;
  }

  if (confirmationActionStillApplicable(entry, reportData.getReports())) {
    setReportActionState({
      ...reconciliation,
      phase: "definite-failure",
      message: `Reports refreshed. ${entry.roomLabel} still allows this action, so it can be tried again.`,
      tone: "error",
      mutationRetryAllowed: true,
      refreshRetryAllowed: false,
      requestKind: null
    });
    return;
  }

  setReportActionState({
    ...reconciliation,
    phase: "success",
    message: entry.successMessage,
    tone: "success",
    mutationCommitted: true,
    mutationRetryAllowed: false,
    refreshRetryAllowed: false,
    requestKind: null
  });
}

  function beginReportMutation(descriptor, focusOrigin) {
  if (!canStartReportMutation(descriptor.recordKey)) {
    return;
  }
  const pending = createPendingReportAction(descriptor, focusOrigin);
  setReportActionState(pending);
  if (document.activeElement === focusOrigin && focusOrigin.dataset.recordKey) {
    document.getElementById("reportActionFeedback")?.focus();
  }
  return executeReportMutation(pending);
}

  function renderExceptionCycles(exceptions) {
  const body = document.getElementById("exceptionCyclesBody");
  if (!body) {
    return;
  }

  const token = String(reportData.getVersion());
  if (body.dataset.renderKey === token) {
    return;
  }
  if (state.reportPressActive) {
    return;
  }
  body.dataset.renderKey = token;
  body.innerHTML = exceptions.length
    ? exceptions.map(renderExceptionRow).join("")
    : `<tr><td colspan="12">No exceptions requiring review.</td></tr>`;
}

  function renderExceptionRow(cycle) {
  const doctor = getDoctorName(cycle.assignedDoctor);
  const sourceType = cycle.sourceType || "CompletedCycle";
  const reviewRecordId = Number(cycle.reviewRecordId || cycle.completedCycleId || cycle.abortedAssignmentId || 0);
  const recordKey = reviewRecordKey(sourceType, reviewRecordId);
  const actionState = currentReportAction(recordKey);
  const locked = isMutationLocked(actionState, "confirm-exclusion");
  const label = actionState?.phase === "pending" && actionState.requestKind === "mutation"
    ? "Confirming exclusion..."
    : "Confirm Exclusion";
  return `
    <tr data-report-action-row
        data-report-record-key="${escapeAttribute(recordKey)}"
        tabindex="-1">
      <td>${formatDateTime(cycle.seatedAt)}</td>
      <td>Room ${cycle.roomId}</td>
      <td>${escapeHtml(doctor)}</td>
      <td>${procedure.renderBadge(cycle.procedureCode)}</td>
      <td>${formatDateTime(cycle.doctorArrivedAt)}</td>
      <td>${formatDateTime(cycle.doctorCompleteAt)}</td>
      <td>${formatDateTime(cycle.roomAvailableAt)}</td>
      <td>${escapeHtml(String(cycle.finalWaitState || "--").toUpperCase())}</td>
      <td>${escapeHtml(cycle.exceptionReason || "--")}</td>
      <td>${escapeHtml(cycle.suggestedAction || "--")}</td>
      <td>${escapeHtml(cycle.reviewStatus || "--")}</td>
      <td>
        <button class="secondary-button utility-button"
                 data-action="confirm-exclusion"
                 data-review-source="${escapeAttribute(sourceType)}"
                 data-review-record-id="${escapeAttribute(String(reviewRecordId || ""))}"
                 data-room-id="${escapeAttribute(String(cycle.roomId || ""))}"
                 data-report-record-key="${escapeAttribute(recordKey)}"
                 data-default-label="Confirm Exclusion"
                 data-pending-label="Confirming exclusion..."
                 ${locked ? "disabled" : ""}
                 title="This keeps the record excluded from normal metrics.">
          ${label}
        </button>
      </td>
    </tr>
  `;
}

// ---------------------------------------------------------------------------
// Reports admin actions (mark-as-exception)
// ---------------------------------------------------------------------------

  function wireReportsActions() {
  // One-time delegated listeners on the document. The reports views are re-rendered
  // on every poll, so we cannot attach to individual elements.
  document.addEventListener("click", handleReportsActionClick);
  document.addEventListener("change", handleAuditSelectionChange);
  // Keyboard activation for the role="button" doctor cards (clicks are already covered above).
  document.addEventListener("keydown", handleReportsCardKeydown);
  globalThis.addEventListener?.("beforeunload", clearAllReportActions, { once: true });
}

  function handleReportsCardKeydown(event) {
  if (event.key !== "Enter" && event.key !== " " && event.key !== "Spacebar") {
    return;
  }
  if (!(event.target instanceof Element)) {
    return;
  }
  // Only act when the focused element is the card itself (it carries the doctor id and tabindex).
  const card = event.target.closest(".doctor-report-card[data-report-doctor-id]");
  if (!card || card !== event.target || !isDoctorCardInCurrentScope(card.dataset.reportDoctorId)) {
    return;
  }
  event.preventDefault();
  state.reportDoctorId = card.dataset.reportDoctorId;
  state.reportDoctorTab = "overview";
  if (reportData.getReports()) {
    renderReports();
  }
}

// Wires report-query controls. Every analytical-scope or grouping change reloads the server-owned
// report population; the Review Queue remains window-global in that response.
  function wireReportFilters() {
  const bar = document.getElementById("reportFilterBar");
  if (!bar) {
    return;
  }

  bar.addEventListener("click", async event => {
    const scopeChip = event.target.closest("[data-report-scope]");
    if (scopeChip) {
      const scope = scopeChip.dataset.reportScope;
      const select = document.getElementById("reportScopeDoctor");
      const doctorId = select?.value || state.reportDoctorId || state.reportScopeDoctors.keys().next().value;
      if (scope === "Doctor" && !doctorId) {
        return;
      }
      reportData.setScope(scope, doctorId);
      syncReportFilterButtons();
      clearCompletedReportAction();
      await reportData.reloadAfterCurrent();
      return;
    }

    const chip = event.target.closest("[data-filter-group]")
      || event.target.closest(".report-filter-chip");
    if (!chip) {
      return;
    }

    const group = chip.dataset.filterGroup;
    const value = normalizeReportFilterValue(group, chip.dataset.filterValue);
    if (!group || !value || state.reportFilters[group] === value) {
      return;
    }

    if (group === "sedation") {
      reportData.setSedation?.(value);
    } else if (group === "grouping") {
      reportData.setProcedureGrouping?.(value);
    }
    state.reportFilters[group] = value;
    syncReportFilterButtons();
    clearCompletedReportAction();
    await reportData.reloadAfterCurrent();
  });

  document.getElementById("reportScopeDoctor")?.addEventListener("change", async event => {
    const doctorId = event.target.value;
    if (!doctorId || reportData.getQuery?.().scope !== "Doctor") {
      return;
    }
    reportData.setScope("Doctor", doctorId);
    clearCompletedReportAction();
    await reportData.reloadAfterCurrent();
  });
}

  function isDoctorCardInCurrentScope(doctorId) {
  const query = reportData.getReports()?.query || reportData.getQuery?.();
  return query?.scope !== "Doctor" || query.doctorId === doctorId;
}

  function renderTableSampledValue(sample, value) {
  const presentation = sampledPresentation(sample, value);
  return `${escapeHtml(presentation.value)}${renderSampleContext(presentation)}`;
}

  function renderInsightMetric(label, value, sample) {
  const presentation = sampledPresentation(sample, value);
  return `<div><dt>${escapeHtml(label)}</dt><dd>${escapeHtml(presentation.value)}${renderSampleContext(presentation)}</dd></div>`;
}

// Sets state.reportPressActive while a pointer is held on an interactive reports element
// (doctor card, tab button, or table action button). This defers innerHTML writes so a
// mid-press DOM rebuild cannot orphan the pressed element and silently drop the click.
// Mirrors the tilePressActive / wireRoomPanel pattern used for room tile interactions.
  function wireReportPressGuard() {
  const shell = document.querySelector(".reports-shell");
  if (!shell) {
    return;
  }

  wirePressInterruptionGuard({
    pressTarget: shell,
    selector: "[data-report-doctor-id], [data-report-doctor-tab], .report-table button, .procedure-intelligence-toggle",
    isPressActive: () => state.reportPressActive,
    setPressActive: value => {
      state.reportPressActive = value;
    },
    onCatchUp: () => {
      if (reportData.getReports()) {
        renderReports();
      }
    }
  });
}

  function wireDoctorCockpitActions() {
  document.addEventListener("click", event => {
    if (handleProcedureIntelligenceClick(event)) {
      return;
    }
    const tab = event.target.closest("[data-report-doctor-tab]");
    if (!tab) {
      return;
    }
    state.reportDoctorTab = tab.dataset.reportDoctorTab || "overview";
    if (reportData.getReports()) {
      renderPage();
    }
  });
}

// Mirrors wireReportPressGuard but for the doctor-view report tabs. Uses the same shared
// state.reportPressActive flag so renderSelectedDoctorPanel's existing press guard applies.
// Delegated on document (the tabs live in the report-details region, not a single cockpit
// wrapper) and filtered to tab elements, which only exist on this page.
  function wireDoctorCockpitPressGuard() {
  wirePressInterruptionGuard({
    pressTarget: document,
    selector: "[data-report-doctor-tab]",
    isPressActive: () => state.reportPressActive,
    setPressActive: value => {
      state.reportPressActive = value;
    },
    onCatchUp: () => {
      if (reportData.getReports()) {
        renderPage();
      }
    }
  });
}

  async function handleReportsActionClick(event) {
  if (handleProcedureIntelligenceClick(event)) {
    return;
  }
  const auditButton = event.target.closest("[data-audit-kind]");
  if (auditButton) {
    const reports = reportData.getReports();
    let evidenceIds = [];
    if (auditButton.dataset.auditEvidence) {
      try {
        evidenceIds = JSON.parse(auditButton.dataset.auditEvidence);
      } catch {
        evidenceIds = [];
      }
    }
    const selection = auditSelection(reports, auditButton.dataset.auditKind, {
      segmentDoctorId: auditButton.dataset.auditDoctor || null,
      procedureCode: auditButton.dataset.auditProcedure || null,
      baseProcedureCode: auditButton.dataset.auditBaseProcedure || null,
      analyticalStanding: auditButton.dataset.auditStanding || "All",
      evidenceIds
    });
    const disclosure = document.getElementById("reportAuditEvidence");
    if (disclosure) disclosure.open = true;
    await ensureAuditView("primary", "reportAuditBody", selection);
    document.getElementById("reportAuditBody")?.focus();
    return;
  }

  const loadMore = event.target.closest("[data-audit-load-more]");
  if (loadMore) {
    const viewId = loadMore.dataset.auditView;
    const entry = state.auditViews.get(viewId);
    if (entry?.page?.hasMore && !entry.loading) {
      await ensureAuditView(viewId, entry.targetId, {
        ...entry.selection,
        offset: entry.page.offset + entry.page.returnedCount
      }, { append: true });
    }
    return;
  }

  const openReviewQueueButton = event.target.closest("[data-action='open-review-queue']");
  if (openReviewQueueButton) {
    const detail = document.getElementById("reportReviewQueue");
    if (detail) {
      detail.open = true;
    }
    const compatibilityDetail = document.getElementById("reportDetail");
    if (compatibilityDetail) compatibilityDetail.open = true;
    const reviewBody = document.getElementById("reportReviewQueueBody");
    (reviewBody?.querySelector?.("button") || reviewBody)?.focus();
    return;
  }

  const openReviewedHistoryButton = event.target.closest("[data-action='open-reviewed-history']");
  if (openReviewedHistoryButton) {
    const detail = document.getElementById("reportReviewedHistory");
    if (detail) detail.open = true;
    document.getElementById("reportReviewedHistoryBody")?.focus();
    return;
  }

  const mutationRetryButton = event.target.closest("[data-action='retry-report-mutation']");
  if (mutationRetryButton) {
    const entry = currentReportAction(mutationRetryButton.dataset.recordKey);
    if (!entry?.mutationRetryAllowed || !confirm(entry.confirmationMessage)) {
      return;
    }
    await beginReportMutation(entry, mutationRetryButton);
    return;
  }

  const refreshButton = event.target.closest("[data-action='refresh-report-action']");
  if (refreshButton) {
    const entry = currentReportAction(refreshButton.dataset.recordKey);
    if (entry?.refreshRetryAllowed) {
      await reconcileReportAction(entry, refreshButton);
    }
    return;
  }

  const doctorButton = event.target.closest("[data-report-doctor-id]");
  if (doctorButton) {
    if (!isDoctorCardInCurrentScope(doctorButton.dataset.reportDoctorId)) {
      return;
    }
    state.reportDoctorId = doctorButton.dataset.reportDoctorId;
    state.reportDoctorTab = "overview";
    if (reportData.getReports()) {
      renderReports();
    }
    return;
  }

  const doctorTab = event.target.closest("[data-report-doctor-tab]");
  if (doctorTab) {
    state.reportDoctorTab = doctorTab.dataset.reportDoctorTab || "overview";
    if (reportData.getReports()) {
      renderReports();
    }
    return;
  }

  const confirmButton = event.target.closest("[data-action='confirm-exclusion']");
  if (confirmButton) {
    await handleConfirmExclusionClick(confirmButton);
    return;
  }

  const button = event.target.closest("[data-action='mark-exception']");
  if (!button) {
    return;
  }

  const roomId = Number(button.dataset.roomId);
  const seatedAt = button.dataset.seatedAt;
  const completedCycleId = Number(button.dataset.completedCycleId);
  // Prefer the stable cycle id; fall back to the legacy roomId + seatedAt key when it is absent.
  const hasCycleId = Number.isInteger(completedCycleId) && completedCycleId > 0;
  if (!hasCycleId && (!roomId || !seatedAt)) {
    return;
  }
  const recordKey = hasCycleId
    ? `completed:${completedCycleId}`
    : `legacy:${roomId}:${normalizeReportIdentityTimestamp(seatedAt)}`;
  if (button.disabled || isMutationLocked(currentReportAction(recordKey), "mark-exception")) {
    return;
  }
  if (!canStartReportMutation(recordKey)) {
    return;
  }
  button.dataset.reportRecordKey = recordKey;
  button.dataset.defaultLabel ||= "Mark Exception";
  button.dataset.pendingLabel ||= "Marking exception...";

  const label = `Room ${roomId} (started ${formatDateTime(seatedAt)})`;
  const confirmationMessage = `Mark ${label} as an exception?\n\nIt will be removed from normal metrics and appear in Exceptions Requiring Review.`;
  if (!confirm(confirmationMessage)) {
    return;
  }

  // When the stable id is present the server targets by it; roomId is included only so the
  // server-side audit log keeps room context. Otherwise fall back to the legacy compound key.
  const requestBody = hasCycleId ? { completedCycleId, roomId } : { roomId, seatedAt };
  await beginReportMutation({
    recordKey,
    actionType: "mark-exception",
    recordSource: "CompletedCycle",
    roomLabel: `Room ${roomId}`,
    confirmationMessage,
    pendingMessage: `Marking Room ${roomId} as an exception...`,
    pendingLabel: "Marking exception...",
    mutationSuccessMessage: `Room ${roomId} was marked as an exception.`,
    successMessage: `Room ${roomId} was marked as an exception.`,
    errorLogLabel: "Mark as exception failed.",
    requestUrl: "/api/reports/cycles/mark-exception",
    requestBody,
    completedCycleId: hasCycleId ? completedCycleId : null,
    roomId,
    seatedAt
  }, button);
}

  async function handleAuditSelectionChange(event) {
  const select = event.target.closest?.("[data-audit-sort], [data-audit-standing-filter]");
  if (!select) {
    return;
  }
  const viewId = select.dataset.auditView;
  const entry = state.auditViews.get(viewId);
  if (!entry || entry.loading) {
    return;
  }
  await ensureAuditView(viewId, entry.targetId, {
    ...entry.selection,
    sort: select.dataset.auditSort !== undefined ? select.value : entry.selection.sort,
    analyticalStanding: select.dataset.auditStandingFilter !== undefined
      ? select.value
      : entry.selection.analyticalStanding,
    offset: 0
  });
}

  async function handleConfirmExclusionClick(button) {
  const sourceType = button.dataset.reviewSource || "CompletedCycle";
  const reviewRecordId = Number(button.dataset.reviewRecordId || button.dataset.completedCycleId);
  if (!Number.isInteger(reviewRecordId) || reviewRecordId <= 0) {
    return;
  }
  const recordKey = reviewRecordKey(sourceType, reviewRecordId);
  if (button.disabled || isMutationLocked(currentReportAction(recordKey), "confirm-exclusion")) {
    return;
  }
  if (!canStartReportMutation(recordKey)) {
    return;
  }
  button.dataset.reportRecordKey = recordKey;
  button.dataset.defaultLabel ||= "Confirm Exclusion";
  button.dataset.pendingLabel ||= "Confirming exclusion...";

  const confirmationMessage = "Confirm exclusion of this exception?\n\nThis keeps the record excluded from normal metrics and clears it from the review queue.";
  if (!confirm(confirmationMessage)) {
    return;
  }

  const roomId = Number(button.dataset.roomId);
  const roomLabel = Number.isInteger(roomId) && roomId > 0 ? `Room ${roomId}` : "Record";
  const successMessage = roomLabel === "Record"
    ? "The record remains excluded and was removed from the review queue."
    : `The ${roomLabel} record remains excluded and was removed from the review queue.`;
  const recordPath = sourceType === "AbortedAssignment"
    ? `aborted-assignments/${reviewRecordId}`
    : `cycles/${reviewRecordId}`;
  await beginReportMutation({
    recordKey,
    actionType: "confirm-exclusion",
    recordSource: sourceType,
    roomLabel,
    confirmationMessage,
    pendingMessage: `Confirming exclusion for ${roomLabel}...`,
    pendingLabel: "Confirming exclusion...",
    mutationSuccessMessage: `Exclusion was confirmed for ${roomLabel}.`,
    successMessage,
    errorLogLabel: "Confirm exclusion failed.",
    requestUrl: `/api/reports/${recordPath}/confirm-exclusion`,
    reviewRecordId
  }, button);
}

  function renderReportsAccessPrompt(statusCode) {
  const headline = document.getElementById("reportHeadline");
  if (!headline) {
    clearAllReportActions();
    return;
  }

  const message = statusCode === 403
    ? "The saved reports token was rejected. Enter the current internal reports token."
    : "Reports access is required for this internal page.";

  // The access prompt owns the always-visible headline band; the filter bar, insights, and
  // collapsible metric/detail areas are hidden until a valid token loads report data.
  headline.classList.remove("is-empty");
  headline.innerHTML = `
    <article class="report-access-panel">
      <h2 id="reportAccessHeading" tabindex="-1">Reports Access</h2>
      <p>${escapeHtml(message)}</p>
      <form id="reportAccessForm" class="report-access-form">
        <label for="reportAccessToken">Reports token</label>
        <input id="reportAccessToken" name="reportAccessToken" type="password" autocomplete="off" required>
        <button type="submit" class="primary-button">Load Reports</button>
      </form>
      <button type="button" class="secondary-button utility-button" id="clearReportAccessToken">Clear Saved Token</button>
    </article>
  `;
  clearAllReportActions({
    focusDestination: document.getElementById("reportAccessToken")
      || document.getElementById("reportAccessHeading")
  });
  ["reportTrendPanel", "reportFilterBar", "reportProcedureMix", "reportProcedureIntelligence", "reportInsights", "reportMetrics", "reportDetail"].forEach(id => {
    const element = document.getElementById(id);
    if (element) {
      element.hidden = true;
    }
  });
  wireReportsAccessPrompt();
}

  function wireReportsAccessPrompt() {
  const form = document.getElementById("reportAccessForm");
  const clearButton = document.getElementById("clearReportAccessToken");

  form?.addEventListener("submit", event => {
    event.preventDefault();
    const input = document.getElementById("reportAccessToken");
    const token = input?.value.trim() || "";
    if (!token) {
      return;
    }

    storeAdminToken(token);
    reportData.reload();
  });

  clearButton?.addEventListener("click", () => {
    clearAdminToken();
    renderReportsAccessPrompt(401);
  });
}

  function renderMetric(label, value, helpText, sample = null) {
  const presentation = sampledPresentation(sample, value);
  return `
    <article class="metric-card">
      <span>${escapeHtml(label)}</span>${helpText ? renderHelpIcon(helpText) : ""}
      <strong>${escapeHtml(presentation.value)}</strong>
      ${renderSampleContext(presentation)}
    </article>
  `;
}

// Neutral, non-punitive labels for reporting-time data-hygiene flags.
  const reportingExceptionBadgeLabels = {
  LegacyProcedure: "Legacy",
  UnmappedProcedure: "Unmapped",
  ExtremeDuration: "Extreme duration",
  OvernightLifecycle: "Overnight",
  MissingTiming: "Missing timing"
};

  function renderReportingExceptionBadges(cycle) {
  const reasons = Array.isArray(cycle.reportingExceptionReasons) ? cycle.reportingExceptionReasons : [];
  const badges = reasons.map(reason =>
    `<span class="report-badge report-badge-exception">${escapeHtml(reportingExceptionBadgeLabels[reason] || reason)}</span>`
  );
  if (cycle.isExcludedFromStandardMetrics) {
    badges.push(`<span class="report-badge report-badge-excluded">Excluded</span>`);
  }

  return badges.length ? `<div class="report-badges">${badges.join("")}</div>` : "";
}

// Raw/audit procedure cell. Flagged cycles show the server display label (e.g. "Sedation (Legacy)")
// since their code does not resolve to an active roster tile; clean cycles keep the normal badge.
  function renderCycleProcedureCell(cycle) {
  const label = cycle.hasReportingException && cycle.displayProcedureLabel
    ? escapeHtml(cycle.displayProcedureLabel)
    : procedure.renderBadge(cycle.procedureCode);
  return `${label}${renderReportingExceptionBadges(cycle)}`;
}

  function renderCycleRow(cycle) {
  const doctor = getDoctorName(cycle.assignedDoctor);
  const recordKey = completedRecordKey(cycle);
  const actionState = currentReportAction(recordKey);
  const locked = isMutationLocked(actionState, "mark-exception");
  const label = actionState?.phase === "pending" && actionState.requestKind === "mutation"
    ? "Marking exception..."
    : "Mark Exception";
  return `
    <tr data-report-action-row
        data-report-record-key="${escapeAttribute(recordKey)}"
        tabindex="-1">
      <td>Room ${cycle.roomId}</td>
      <td>${escapeHtml(doctor)}</td>
      <td>${renderCycleProcedureCell(cycle)}</td>
      <td>${formatDateTime(cycle.seatedAt)}</td>
      <td>${formatDateTime(cycle.readyForDoctorAt)}</td>
      <td>${formatDateTime(cycle.doctorArrivedAt)}</td>
      <td>${formatDateTime(cycle.doctorCompleteAt)}</td>
      <td>${formatDateTime(cycle.roomAvailableAt)}</td>
      <td>${formatObservedDuration(cycle.prepSeconds)}</td>
      <td>${formatObservedDuration(cycle.readyToDoctorSeconds)}</td>
      <td>${formatObservedDuration(cycle.doctorOccupiedWaitSeconds)}</td>
      <td>${formatObservedDuration(cycle.doctorAvailableWaitSeconds)}</td>
      <td>${formatObservedDuration(cycle.seatedToDoctorSeconds)}</td>
      <td>${formatObservedDuration(cycle.doctorInRoomSeconds)}</td>
      <td>${formatObservedDuration(cycle.turnoverSeconds)}</td>
      <td>${formatObservedDuration(cycle.totalRoomCycleSeconds)}</td>
      <td>${formatAllocationMinutes(cycle.expectedAllocationMinutes)}</td>
      <td>${formatAllocationMinutes(cycle.measuredCaseFlowMinutes)}</td>
      <td>${renderVarianceBadge(cycle.allocationVarianceMinutes)}</td>
      <td>${escapeHtml(String(cycle.finalWaitState || "--").toUpperCase())}</td>
      <td>${cycle.agingThresholdReached ? "Yes" : "No"}</td>
      <td>${cycle.staleThresholdReached ? "Yes" : "No"}</td>
      <td>
        <button class="secondary-button utility-button"
                data-action="mark-exception"
                 data-completed-cycle-id="${escapeAttribute(String(cycle.completedCycleId || ""))}"
                 data-room-id="${cycle.roomId}"
                 data-seated-at="${escapeAttribute(cycle.seatedAt || "")}"
                 data-report-record-key="${escapeAttribute(recordKey)}"
                 data-default-label="Mark Exception"
                 data-pending-label="Marking exception..."
                 ${locked ? "disabled" : ""}>
          ${label}
        </button>
      </td>
    </tr>
  `;
}

  function formatObservedDuration(seconds) {
  return Number.isFinite(seconds) ? formatDuration(seconds) : "--";
}


  function wire() {
    if (context.isReports) {
      wireReportsActions();
      wireReportFilters();
      wireDateRange();
      wireReportPressGuard();
    }

    if (context.isDoctor) {
      wireDoctorCockpitActions();
      wireDoctorCockpitPressGuard();
    }
  }

  return {
    render: renderReports,
    renderAccessPrompt: renderReportsAccessPrompt,
    renderDoctorCockpit,
    wire
  };
}
