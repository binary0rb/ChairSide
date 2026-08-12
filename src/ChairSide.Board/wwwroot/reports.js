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
    reportPressActive: false
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

  const allDoctors = aggregateAllocationByDoctor(r.doctorSummaries || [], r);
  const agg = allDoctors.find(item => item.doctorId === doctor.id)
    || { doctorId: doctor.id, count: 0, net: 0, over: 0, under: 0, at: 0, adjusted: 0 };
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
          aria-label="${escapeAttribute(`${doctor.name} — allocation summary`)}">
          ${renderDoctorCardBody(agg, r, doctor.name, identity)}
        </article>`;
    }
  }

  // Full selected-doctor detail panel (head + tabs + tab content) reused directly.
  // state.reportDoctorId was pinned to doctor.id in renderDoctorView, so the panel's
  // internal doctors.find(item => item.doctorId === state.reportDoctorId) always resolves.
  renderSelectedDoctorPanel(r, [agg]);
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
  renderAllocationReports(r);
  renderGroupedInsights(r, hasData);
  renderFullMetrics(r, hasData);

  const completedCycles = r.recentCompletedCycles || [];
  const exceptionCycles = r.exceptionReviewRecords || r.exceptionCycles || [];
  const focusTransfer = captureReportActionFocusBeforeRender(
    completedCycles,
    exceptionCycles);
  renderCompletedCycles(completedCycles);
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
  headline.innerHTML = [
    renderHeadlineCard("Completed Cases", String(r.completedRoomCyclesCount ?? 0), null, r.samples?.completedCases),
    renderHeadlineCard("Median Ready Wait", formatObservedDuration(r.medianReadyToDoctorSeconds), "Accepted Ready to Doctor Arrived.", r.samples?.readyWait),
    renderHeadlineCard("Median Seated -> Doctor", formatObservedDuration(r.medianSeatedToDoctorSeconds), "Total observed interval from Seated to Doctor Arrived.", r.samples?.seatedToDoctor),
    renderHeadlineCard("Median Turnover", formatObservedDuration(r.medianTurnoverSeconds), "Doctor Complete to Room Available.", r.samples?.turnover)
  ].join("");
}

  function renderHeadlineCard(label, value, helpText, sample = null) {
  const presentation = sampledPresentation(sample, value);
  return `
    <article class="metric-card headline-card">
      <span>${escapeHtml(label)}</span>${helpText ? renderHelpIcon(helpText) : ""}
      <strong>${escapeHtml(presentation.value)}</strong>
      ${renderSampleContext(presentation)}
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

  const doctors = aggregateAllocationByDoctor(r.doctorSummaries || [], r);
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
        ? doctors.map(agg => renderDoctorAllocationCard(agg, r)).join("")
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

  state.reportDoctorId = doctors.find(item => (item.count || 0) > 0)?.doctorId || doctors[0].doctorId;
}

// ---------------------------------------------------------------------------
// Allocation balance presentation (Phase 3B). Surfaces the Phase 3A math in
// plain, neutral language: expected allocation vs measured case flow. No
// ranking, no scoring, no "sedation time".
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

  const pill = `<span class="layer-pill layer-pill--allocation">Allocation Logic</span>`;
  const a = r.allocationVariance;
  if (!a) {
    card.innerHTML = `
      ${pill}
      <h3>Overall Allocation Balance${renderHelpIcon("Planned time budget based on the selected procedure mix and allocation settings.")}</h3>
      <p class="allocation-empty">No allocation variance data available for this report view.</p>`;
    return;
  }

  const count = a.allocationVarianceCycleCount;
  const sample = r.samples?.scheduleFit;
  const samplePresentation = sampledPresentation(sample, String(count));
  const comparisonAvailable = sampleSupportsComparison(sample);
  card.innerHTML = `
    ${pill}
    <h3>Overall Allocation Balance</h3>
    <p class="allocation-lead">${escapeHtml(samplePresentation.value)}${samplePresentation.state === "Limited" || samplePresentation.state === "Sufficient" ? ` ${count === 1 ? "case" : "cases"} measured against expected allocation across included cases in this report view.` : ""}</p>
    ${renderSampleContext(samplePresentation)}
    ${comparisonAvailable ? `
      <p class="allocation-net">Net ${renderVarianceBadge(a.netAllocationVarianceMinutes)} across included cases.</p>
      <p>Average ${renderAverageVarianceBadge(a.averageAllocationVarianceMinutes)}.</p>
      <p class="allocation-breakdown-line">${a.casesOverExpectedAllocation} over expected · ${a.casesUnderExpectedAllocation} under expected · ${a.casesAtExpectedAllocation} at expected</p>` : renderAllocationComparisonNotice(sample)}
    ${sample?.state === "Limited" || sample?.state === "Sufficient" ? `<p class="allocation-context">${a.adjustedAllocationCycleCount} adjusted allocation ${a.adjustedAllocationCycleCount === 1 ? "case" : "cases"} · ${a.totalExpectedAllocationMinutes} min expected · ${a.totalMeasuredCaseFlowMinutes} min measured</p>` : ""}
    <p class="allocation-footnote">Current analytical scope. Only includes completed cases that have an expected allocation snapshot and a Doctor Complete timestamp, so this can be fewer than total completed cases.</p>`;
}

  function renderDataQualityCard(r) {
  const card = document.getElementById("dataQualityCard");
  if (!card) {
    return;
  }

  const included = r.includedCompletedCycleCount || 0;
  const excluded = r.excludedCompletedCycleCount || 0;
  const reviewCount = (r.exceptionReviewRecords || r.exceptionCycles || []).length;

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
       <button type="button" class="secondary-button utility-button" data-action="open-review-queue" aria-controls="reportDetail">Open review queue</button>`
    : "";

  card.innerHTML = `
    <span class="layer-pill layer-pill--data-quality">Data Quality</span>
    <h3>Data Quality</h3>
    <p class="allocation-counts">${included} included · ${excluded} excluded</p>
    ${exclusionDetail}
    ${reviewDetail}
    <p class="allocation-footnote">Included/excluded records are a separate layer from allocation-calculable cases above.</p>`;
}

  function renderDoctorAllocation(r) {
  const list = document.getElementById("doctorAllocationList");
  if (!list) {
    return;
  }

  const aggregated = aggregateAllocationByDoctor(r.doctorSummaries || [], r);
  list.classList.remove("doctor-report-card-grid");
  list.innerHTML = aggregated.length
    ? aggregated.map(agg => renderDoctorAllocationRow(agg)).join("")
    : `<p class="allocation-empty">No doctor allocation data for this range.</p>`;
}

// Sums each doctor's allocation across the returned (per-month) summaries so a doctor appears
// once. Ordered by the doctor roster - never by variance, to avoid implying a ranking.
  function aggregateAllocationByDoctor(summaries, report) {
  const byDoctor = new Map();
  const samplesByDoctor = new Map((report?.doctorAllocationSamples || [])
    .map(item => [item.doctorId, item.sample]));
  for (const summary of summaries) {
    const a = summary.allocation;
    if (!a) {
      continue;
    }
    const key = summary.assignedDoctor;
    const agg = byDoctor.get(key) || { doctorId: key, count: 0, net: 0, over: 0, under: 0, at: 0, adjusted: 0 };
    agg.count += a.allocationVarianceCycleCount || 0;
    agg.net += a.netAllocationVarianceMinutes || 0;
    agg.over += a.casesOverExpectedAllocation || 0;
    agg.under += a.casesUnderExpectedAllocation || 0;
    agg.at += a.casesAtExpectedAllocation || 0;
    agg.adjusted += a.adjustedAllocationCycleCount || 0;
    byDoctor.set(key, agg);
  }

  const order = (getSnapshot()?.doctors || []).map(doctor => doctor.id);
  const rank = id => {
    const index = order.indexOf(id);
    return index === -1 ? Number.MAX_SAFE_INTEGER : index;
  };

  const doctorScope = report?.query?.scope === "Doctor";
  const scopedDoctorId = doctorScope ? report.query.doctorId : null;
  const roster = doctorScope
    ? (scopedDoctorId ? [{ id: scopedDoctorId }] : [])
    : (getSnapshot()?.doctors || []);
  const rosterCards = roster.map(doctor => ({
    doctorId: doctor.id,
    count: 0,
    net: 0,
    over: 0,
    under: 0,
    at: 0,
    adjusted: 0,
    sample: samplesByDoctor.get(doctor.id),
    ...byDoctor.get(doctor.id)
  }));
  if (doctorScope) {
    return rosterCards;
  }
  const rosterIds = new Set(rosterCards.map(item => item.doctorId));
  const historicalCards = [...byDoctor.values()]
    .filter(item => !rosterIds.has(item.doctorId))
    .map(item => ({ ...item, sample: samplesByDoctor.get(item.doctorId) }))
    .sort((x, y) => rank(x.doctorId) - rank(y.doctorId));

  return [...rosterCards, ...historicalCards];
}

// Renders the inner body of a doctor report card: header (initials + name + summary), metrics dl,
// and sparkline. Used by both the interactive grid card and the non-interactive cockpit summary.
  function renderDoctorCardBody(agg, report, name, identity) {
  const count = agg.count || 0;
  const average = count > 0 ? agg.net / count : Number.NaN;
  const doctorSample = agg.sample || fallbackSampleForObservedCount(count);
  const comparisonAvailable = sampleSupportsComparison(doctorSample);
  const countPresentation = sampledPresentation(doctorSample, String(count));
  const sparkPoints = (report?.doctorDailyAllocationSeries || []).find(item => item.doctorId === agg.doctorId)?.points;
  return `
    <header class="doctor-report-card-head">
      <span class="doctor-report-initials" aria-hidden="true">${escapeHtml(identity.initials)}</span>
      <div class="doctor-report-identity">
        <h4>${escapeHtml(name)}</h4>
        <p>${escapeHtml(doctorAllocationSummary(agg, doctorSample))}</p>
      </div>
    </header>
    <dl class="doctor-report-metrics">
      <div>
        <dt>Cases</dt>
        <dd>${escapeHtml(countPresentation.value)}${renderSampleContext(countPresentation)}</dd>
      </div>
      ${comparisonAvailable ? `
        <div>
          <dt>Balance</dt>
          <dd class="${escapeAttribute(varianceClass(agg.net))}">${escapeHtml(formatSignedMinutes(agg.net))}</dd>
        </div>
        <div>
          <dt>Avg</dt>
          <dd class="${escapeAttribute(varianceClass(average))}">${escapeHtml(formatSignedMinutes(average))}</dd>
        </div>
        <div class="doctor-card-metric--help-corner">
          <dt>O / U / A</dt>
          <dd>${escapeHtml(`${agg.over} / ${agg.under} / ${agg.at}`)}</dd>
          ${renderHelpIcon("O/U/A means Over, Under, or At target compared with expected procedure allocation.", "corner")}
        </div>` : `
        <div>
          <dt>Comparison</dt>
          <dd class="is-unavailable">${escapeHtml(allocationComparisonValue(doctorSample))}</dd>
        </div>`}
    </dl>
    ${comparisonAvailable ? renderDoctorSparkline(sparkPoints) : ""}`;
}

// The whole card is the selection control (role="button", focusable). The "View details" affordance
// is a non-interactive visual cue (aria-hidden span) so we never nest interactive controls; clicks
// anywhere in the card and Enter/Space on the focused card both resolve to data-report-doctor-id.
  function renderDoctorAllocationCard(agg, report) {
  const doctor = (getSnapshot()?.doctors || []).find(item => item.id === agg.doctorId);
  const name = doctor ? doctor.name : getDoctorName(agg.doctorId);
  const identity = getDoctorIdentity(agg.doctorId, name);
  const count = agg.count || 0;
  const selected = agg.doctorId === state.reportDoctorId;
  return `
    <article class="doctor-report-card ${count === 0 ? "is-empty" : ""} ${selected ? "is-selected" : ""}" style="--doctor-color: ${escapeAttribute(identity.color)}" data-report-doctor-id="${escapeAttribute(agg.doctorId)}" role="button" tabindex="0" aria-pressed="${selected ? "true" : "false"}" aria-label="${escapeAttribute(`Show report details for ${name}`)}">
      ${renderDoctorCardBody(agg, report, name, identity)}
      <span class="doctor-report-detail-link" aria-hidden="true">
        ${selected ? "Viewing details" : "View details"}
      </span>
    </article>`;
}

// Plots daily net allocation variance minutes (measured case flow - expected allocation) for one
// doctor. Zero variance sits on a centered neutral baseline; positive (over expected) rises above it
// and negative (under expected) drops below, scaled symmetrically by the largest absolute day so the
// baseline stays meaningful. Honest by construction: a flat run of equal values renders flat, a single
// day renders a short level mark, and no manufactured wobble is added.
//
// preserveAspectRatio="none" lets the SVG stretch to the full card width (matching the metric slab)
// instead of meet-fitting to its height and floating as a narrow centered line; vector-effect
// "non-scaling-stroke" keeps the stroke a crisp, uniform weight despite the non-uniform scaling.
  function renderDoctorSparkline(points) {
  const w = 100, h = 32, pad = 3;
  const mid = (h / 2).toFixed(1);
  const open = `<svg class="doctor-sparkline" viewBox="0 0 ${w} ${h}" preserveAspectRatio="none" aria-hidden="true">`;
  const baseline = `<line x1="${pad}" y1="${mid}" x2="${(w - pad).toFixed(1)}" y2="${mid}" stroke="var(--doctor-color)" stroke-width="0.75" vector-effect="non-scaling-stroke" opacity="0.25"/>`;

  if (!points || points.length === 0) {
    return `${open}${baseline}</svg>`;
  }

  const sorted = [...points].sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0));
  const values = sorted.map(p => Number(p.netVarianceMinutes) || 0);
  const maxAbs = Math.max(1, ...values.map(v => Math.abs(v)));
  const half = (h - 2 * pad) / 2;
  const zeroY = h / 2;
  const yOf = v => zeroY - (v / maxAbs) * half;

  if (sorted.length === 1) {
    const y = yOf(values[0]).toFixed(1);
    return `${open}${baseline}<line x1="${(w / 2 - 18).toFixed(1)}" y1="${y}" x2="${(w / 2 + 18).toFixed(1)}" y2="${y}" stroke="var(--doctor-color)" stroke-width="1.5" stroke-linecap="round" vector-effect="non-scaling-stroke" opacity="0.85"/></svg>`;
  }

  const minMs = new Date(sorted[0].date).getTime();
  const maxMs = new Date(sorted[sorted.length - 1].date).getTime();
  const msRange = maxMs - minMs || 1;
  const xScale = w - 2 * pad;
  const coords = sorted.map((p, i) => {
    const x = (pad + ((new Date(p.date).getTime() - minMs) / msRange) * xScale).toFixed(1);
    const y = yOf(values[i]).toFixed(1);
    return `${x},${y}`;
  }).join(" ");
  return `${open}${baseline}<polyline points="${coords}" fill="none" stroke="var(--doctor-color)" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" vector-effect="non-scaling-stroke" opacity="0.85"/></svg>`;
}

  function doctorAllocationSummary(agg, sample = agg.sample || fallbackSampleForObservedCount(agg.count || 0)) {
  const count = agg.count || 0;
  if (sample?.state === "Empty") {
    return "No allocation variance cases in this report range.";
  }
  if (sample?.state === "Unavailable") {
    return "Allocation values are unavailable for this nonempty report population.";
  }
  if (!sampleSupportsComparison(sample)) {
    return `${count} allocation ${count === 1 ? "contributor" : "contributors"}. Comparison is not shown for a Limited sample.`;
  }
  if (agg.net === 0) {
    return `Measured case flow stayed at expected allocation across ${count} ${count === 1 ? "case" : "cases"}.`;
  }

  const direction = agg.net > 0 ? "over expected" : "under expected";
  return `Measured case flow ran ${formatAbsoluteMinutes(agg.net)} ${direction} across ${count} ${count === 1 ? "case" : "cases"}.`;
}

  function mainPressurePoint(agg) {
  if (!agg.count) {
    return "No cases";
  }

  const points = [
    { label: "Over expected", value: agg.over || 0 },
    { label: "Under expected", value: agg.under || 0 },
    { label: "At expected", value: agg.at || 0 }
  ].sort((a, b) => b.value - a.value);

  return points[0].value > 0 ? points[0].label : "No variance";
}

  function renderSelectedDoctorPanel(r, doctors) {
  const panel = document.getElementById("selectedDoctorPanel");
  if (!panel) {
    return;
  }

  const agg = doctors.find(item => item.doctorId === state.reportDoctorId) || doctors[0];
  if (!agg) {
    panel.hidden = true;
    panel.innerHTML = "";
    panel.dataset.renderKey = "";
    return;
  }

  const doctor = (getSnapshot()?.doctors || []).find(item => item.id === agg.doctorId);
  const name = doctor ? doctor.name : getDoctorName(agg.doctorId);
  const identity = getDoctorIdentity(agg.doctorId, name);
  const tabs = ["overview", "trends", "procedures", "flow", "audit"];
  if (!tabs.includes(state.reportDoctorTab)) {
    state.reportDoctorTab = "overview";
  }

  // Skip panel rebuild when payload, selected doctor, and active tab are all unchanged.
  // Tab is included because it drives both the tab-button aria-selected states and the
  // entire tab-panel content. During an active pointer press, defer the rebuild to avoid
  // destroying the tab button mid-click; leave the key stale so the catch-up render applies.
  const panelToken = `${reportData.getVersion()}|${agg.doctorId}|${state.reportDoctorTab}`;
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
        <p>${escapeHtml(doctorAllocationSummary(agg))}</p>
      </div>
    </div>
    <div class="selected-doctor-tabs" role="tablist" aria-label="${escapeAttribute(name)} report sections">
      ${tabs.map(tab => renderDoctorReportTabButton(tab)).join("")}
    </div>
    <div class="selected-doctor-tab-panel">
      ${renderSelectedDoctorTabContent(state.reportDoctorTab, r, agg)}
    </div>`;
}

  function renderDoctorReportTabButton(tab) {
  const labels = {
    overview: "Overview",
    trends: "Trends",
    procedures: "Procedures",
    flow: "Flow Breakdown",
    audit: "Case Audit"
  };
  const selected = state.reportDoctorTab === tab;
  return `
    <button class="selected-doctor-tab ${selected ? "is-active" : ""}" type="button" role="tab" aria-selected="${selected ? "true" : "false"}" data-report-doctor-tab="${escapeAttribute(tab)}">
      ${escapeHtml(labels[tab])}
    </button>`;
}

  function renderSelectedDoctorTabContent(tab, r, agg) {
  if (tab === "audit") {
    return renderSelectedDoctorAudit(r, agg.doctorId);
  }

  if (tab === "overview") {
    return renderSelectedDoctorOverview(r, agg);
  }

  if (tab === "flow") {
    return renderSelectedDoctorFlow(r, agg);
  }

  if (tab === "trends") {
    return renderSelectedDoctorEmptyState(
      "Trends",
      "Trend charts are planned for this doctor view. Once enabled, this tab will show week-to-week or month-to-month movement for timing and flow metrics."
    );
  }

  if (tab === "procedures") {
    return renderSelectedDoctorProcedures(r, agg);
  }

  return renderSelectedDoctorEmptyState("Not Available", "This section isn't available with the current report payload.");
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

  function renderSelectedDoctorOverview(r, agg) {
  const count = agg.count || 0;
  const average = count > 0 ? agg.net / count : Number.NaN;
  const sample = agg.sample || fallbackSampleForObservedCount(count);
  const samplePresentation = sampledPresentation(sample, String(count));
  const comparisonAvailable = sampleSupportsComparison(sample);
  return `
    <section class="selected-doctor-overview">
      <div class="selected-doctor-summary">
        <h3>Range Flow Summary</h3>
        <p>${escapeHtml(doctorAllocationSummary(agg))}</p>
        <p class="allocation-footnote">Uses existing doctor allocation aggregates for ${escapeHtml(r.rangeLabel || "the selected range")}.</p>
      </div>
      <dl class="selected-doctor-kpis">
        <div><dt>Cases</dt><dd>${escapeHtml(samplePresentation.value)}${renderSampleContext(samplePresentation)}</dd></div>
        ${comparisonAvailable ? `
          <div><dt>Net balance</dt><dd class="${escapeAttribute(varianceClass(agg.net))}">${escapeHtml(formatSignedMinutes(agg.net))}</dd></div>
          <div><dt>Average variance</dt><dd class="${escapeAttribute(varianceClass(average))}">${escapeHtml(formatSignedMinutes(average))}</dd></div>
          <div><dt>Pressure point</dt><dd>${escapeHtml(mainPressurePoint(agg))}</dd></div>` : `
          <div><dt>Comparison</dt><dd class="is-unavailable">${escapeHtml(allocationComparisonValue(sample))}</dd></div>`}
      </dl>
    </section>`;
}

  function observedLoadNumber(value) {
  const number = Number(value);
  return Number.isFinite(number) ? number : 0;
}

  function renderSelectedDoctorFlow(r, agg) {
  const days = (r.observedDoctorDays || []).filter(day => day.doctorId === agg.doctorId);
  if (!days.length) {
    return renderSelectedDoctorEmptyState(
      "Observed Load",
      "No observed load data is available for this doctor in the current report range. This usually means there are no completed cycles for this doctor/date selection yet.",
      "Shows the doctor's observed room-flow load for the selected range. Descriptive only; not a ranking or score."
    );
  }

  const sorted = [...days].sort((a, b) => String(b.reportDate || "").localeCompare(String(a.reportDate || "")));
  const recent = sorted.slice(0, 10);

  const completedCases = days.reduce((sum, day) => sum + observedLoadNumber(day.encounterCount), 0);
  const finiteSpans = days.map(day => day.observedClinicalSpanMinutes).filter(Number.isFinite);
  const avgClinicalSpan = finiteSpans.length
    ? finiteSpans.reduce((sum, minutes) => sum + minutes, 0) / finiteSpans.length
    : Number.NaN;
  const peakActiveRooms = days.reduce(
    (max, day) => Number.isFinite(day.maxActiveRoomCount) ? Math.max(max, day.maxActiveRoomCount) : max,
    0
  );
  const twoRoomMinutes = days.reduce((sum, day) => sum + observedLoadNumber(day.minutesWithTwoActiveRooms), 0);
  const threePlusRoomMinutes = days.reduce((sum, day) => sum + observedLoadNumber(day.minutesWithThreeOrMoreActiveRooms), 0);
  const stackedMinutes = twoRoomMinutes + threePlusRoomMinutes;
  const overlapSentence = stackedMinutes > 0
    ? `Observed active room time included ${formatDurationMinutes(stackedMinutes)} with overlapping rooms.`
    : "Observed active room time stayed in single-room flow for this range.";

  return `
    <section class="selected-doctor-overview">
      <div class="selected-doctor-summary">
        <h3>Observed Load${renderHelpIcon("Shows the doctor's observed room-flow load for the selected range. Descriptive only; not a ranking or score.")}</h3>
        <p>Across ${escapeHtml(String(days.length))} observed day${days.length === 1 ? "" : "s"}, this doctor completed ${escapeHtml(String(completedCases))} case${completedCases === 1 ? "" : "s"} with a typical clinical span of ${escapeHtml(formatApproxDurationMinutes(avgClinicalSpan))} per day.</p>
        <p>${escapeHtml(overlapSentence)}</p>
        <p class="allocation-footnote">Observed Load is descriptive only: it shows room overlap and span pressure, not provider ranking or staff performance scoring.</p>
      </div>
      <dl class="selected-doctor-kpis">
        <div><dt>Days observed</dt><dd>${escapeHtml(String(days.length))}</dd></div>
        <div><dt>Completed cases</dt><dd>${escapeHtml(String(completedCases))}</dd></div>
        <div><dt>Avg clinical span${renderHelpIcon("Average observed span per day from first seated case through last Doctor Complete.")}</dt><dd>${escapeHtml(formatDurationMinutes(avgClinicalSpan))}</dd></div>
        <div><dt>Peak active load${renderHelpIcon("Highest number of active rooms overlapping for this doctor on an observed day.")}</dt><dd>${escapeHtml(describePeakActiveLoad(peakActiveRooms))}</dd></div>
      </dl>
    </section>
    <div class="selected-doctor-audit">
      <table class="report-table">
        <thead>
          <tr>
            <th>Date</th>
            <th>Cases</th>
            <th>Clinical Span</th>
            <th>Team Span</th>
            <th>Peak Load</th>
            <th>1 room</th>
            <th>2 rooms</th>
            <th>3+ rooms</th>
          </tr>
        </thead>
        <tbody>
          ${recent.map(day => `
            <tr>
              <td>${escapeHtml(formatObservedDayDate(day.reportDate))}</td>
              <td>${escapeHtml(Number.isFinite(day.encounterCount) ? String(day.encounterCount) : "--")}</td>
              <td>${escapeHtml(formatDurationMinutes(day.observedClinicalSpanMinutes))}</td>
              <td>${escapeHtml(formatDurationMinutes(day.observedTeamSpanMinutes))}</td>
              <td>${escapeHtml(describePeakActiveLoad(day.maxActiveRoomCount))}</td>
              <td>${escapeHtml(formatAllocationMinutes(day.minutesWithOneActiveRoom))}</td>
              <td>${escapeHtml(formatAllocationMinutes(day.minutesWithTwoActiveRooms))}</td>
              <td>${escapeHtml(formatAllocationMinutes(day.minutesWithThreeOrMoreActiveRooms))}</td>
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

  function describePeakActiveLoad(maxActiveRoomCount) {
  if (!Number.isFinite(maxActiveRoomCount) || maxActiveRoomCount < 1) {
    return "--";
  }
  if (maxActiveRoomCount === 1) {
    return "1 room active";
  }
  if (maxActiveRoomCount === 2) {
    return "2 rooms active";
  }
  return "3+ rooms active";
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
                  <td>${escapeHtml(formatProcedureShare(row.shareOfScopedCases))}</td>
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

  return renderProcedureMixMarkup(r, { headingTag: "h3", compact: true });
}

  function formatProcedureShare(share) {
  return Number.isFinite(share) ? `${Math.round(share * 100)}%` : "--";
}

  function renderSelectedDoctorAudit(r, doctorId) {
  const cycles = (r.recentCompletedCycles || []).filter(cycle => cycle.assignedDoctor === doctorId);
  if (!cycles.length) {
    return renderSelectedDoctorEmptyState(
      "Case Audit",
      "No completed cycles are available for this doctor in the selected range yet. They'll appear here once cases wrap up."
    );
  }

  return `
    <div class="selected-doctor-audit">
      <table class="report-table">
        <thead>
          <tr>
            <th>Room</th>
            <th>Procedure</th>
            <th>Doctor Complete</th>
            <th>Expected</th>
            <th>Measured</th>
            <th>Variance</th>
          </tr>
        </thead>
        <tbody>
          ${cycles.map(cycle => `
            <tr>
              <td>Room ${cycle.roomId}</td>
              <td>${renderCycleProcedureCell(cycle)}</td>
              <td>${formatDateTime(cycle.doctorCompleteAt)}</td>
              <td>${formatAllocationMinutes(cycle.expectedAllocationMinutes)}</td>
              <td>${formatAllocationMinutes(cycle.measuredCaseFlowMinutes)}</td>
              <td>${renderVarianceBadge(cycle.allocationVarianceMinutes)}</td>
            </tr>
          `).join("")}
        </tbody>
      </table>
      <p class="allocation-footnote">Case Audit is limited to the recent completed cycles returned by the current report payload.</p>
    </div>`;
}

  function formatAbsoluteMinutes(minutes) {
  if (!Number.isFinite(minutes)) {
    return "--";
  }
  const rounded = Math.round(Math.abs(minutes) * 10) / 10;
  return `${rounded} min`;
}

  function formatSignedMinutes(minutes) {
  if (!Number.isFinite(minutes)) {
    return "--";
  }
  const rounded = Math.round(minutes * 10) / 10;
  if (rounded > 0) {
    return `+${rounded} min`;
  }
  return `${rounded} min`;
}

  function renderDoctorAllocationRow(agg) {
  const name = getDoctorName(agg.doctorId);
  const sample = agg.sample || fallbackSampleForObservedCount(agg.count || 0);
  const presentation = sampledPresentation(sample, String(agg.count || 0));
  if (!sampleSupportsComparison(sample)) {
    return `
      <div class="allocation-row">
        <span class="allocation-row-name">${escapeHtml(name)}</span>
        <span class="allocation-row-detail allocation-empty">
          ${escapeHtml(presentation.value)}${renderSampleContext(presentation)}
          <small>${escapeHtml(allocationComparisonNoticeText(sample))}</small>
        </span>
      </div>`;
  }

  return `
    <div class="allocation-row">
      <span class="allocation-row-name">${escapeHtml(name)}</span>
      <span class="allocation-row-detail">
        ${describeAllocation(agg.count, agg.net)}
        <small>${agg.over} over · ${agg.under} under · ${agg.at} at · ${agg.adjusted} adjusted</small>
      </span>
    </div>`;
}

  function renderProcedureAllocation(r) {
  const list = document.getElementById("procedureAllocationList");
  if (!list) {
    return;
  }

  // Procedure family (base) summaries only - sedation variants roll up under their family.
  const families = (r.baseProcedureSummaries || [])
    .filter(summary => summary.allocation);

  list.innerHTML = families.length
    ? families.map(renderProcedureAllocationRow).join("")
    : `<p class="allocation-empty">No procedure family allocation data for this range.</p>`;
}

  function renderProcedureAllocationRow(summary) {
  const a = summary.allocation;
  const label = summary.procedureLabel || summary.procedureCode || "Unknown";
  const sample = summary.samples?.allocation || fallbackSampleForObservedCount(a.allocationVarianceCycleCount || 0);
  const presentation = sampledPresentation(sample, String(a.allocationVarianceCycleCount || 0));
  if (!sampleSupportsComparison(sample)) {
    return `
      <div class="allocation-row">
        <span class="allocation-row-name">${escapeHtml(label)}</span>
        <span class="allocation-row-detail allocation-empty">
          ${escapeHtml(presentation.value)}${renderSampleContext(presentation)}
          <small>${escapeHtml(allocationComparisonNoticeText(sample))}</small>
        </span>
      </div>`;
  }
  return `
    <div class="allocation-row">
      <span class="allocation-row-name">${escapeHtml(label)}</span>
      <span class="allocation-row-detail">
        ${describeAllocation(a.allocationVarianceCycleCount, a.netAllocationVarianceMinutes)}
        <small>${a.casesOverExpectedAllocation} over · ${a.casesUnderExpectedAllocation} under · ${a.casesAtExpectedAllocation} at · ${a.adjustedAllocationCycleCount} adjusted</small>
      </span>
    </div>`;
}

// Compact neutral one-liner for a doctor/procedure row. Average is derived from the row's own
// net and case count so it stays correct after per-doctor aggregation.
  function describeAllocation(count, net) {
  const cases = `${count} ${count === 1 ? "case" : "cases"}`;
  if (net === 0) {
    return `<strong>At expected allocation across ${cases}.</strong>`;
  }
  const avg = count > 0 ? net / count : 0;
  return `<strong>Net ${renderVarianceBadge(net)} across ${cases}.</strong>
          <span>Average ${renderAverageVarianceBadge(avg)}.</span>`;
}

// Neutral average-per-case label, rounded to one decimal.
  function formatAverageVariancePerCase(averageMinutes) {
  if (!Number.isFinite(averageMinutes)) {
    return "--";
  }
  const rounded = Math.round(averageMinutes * 10) / 10;
  const magnitude = Math.abs(rounded);
  if (rounded > 0) {
    return `+${magnitude} min over expected per case`;
  }
  if (rounded < 0) {
    return `-${magnitude} min under expected per case`;
  }
  return "0 min at expected per case";
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

  function renderAverageVarianceBadge(averageMinutes) {
  const rounded = Number.isFinite(averageMinutes) ? Math.round(averageMinutes * 10) / 10 : averageMinutes;
  return `<span class="variance ${varianceClass(rounded)}">${escapeHtml(formatAverageVariancePerCase(averageMinutes))}</span>`;
}

  function renderReportFilterBar() {
  const bar = document.getElementById("reportFilterBar");
  if (bar) {
    bar.hidden = false;
  }
}

  function fallbackSampleForObservedCount(count) {
  const threshold = 5;
  const state = count === 0 ? "Empty" : count < threshold ? "Limited" : "Sufficient";
  return {
    populationCount: count,
    contributingCount: count,
    state,
    limitedSampleThreshold: threshold,
    supportsComparison: state === "Sufficient"
  };
}

  function sampleSupportsComparison(sample) {
  return sample?.state === "Sufficient" && sample.supportsComparison !== false;
}

  function allocationComparisonValue(sample) {
  if (sample?.state === "Empty") {
    return "No observation";
  }
  if (sample?.state === "Unavailable") {
    return "Unavailable";
  }
  return "Not shown";
}

  function allocationComparisonNoticeText(sample) {
  if (sample?.state === "Empty") {
    return "No allocation observation in this population.";
  }
  if (sample?.state === "Unavailable") {
    return "Allocation comparison is unavailable because this population has no contributing allocation observations.";
  }
  return "Comparison is not shown for a Limited sample.";
}

  function renderAllocationComparisonNotice(sample) {
  return `<p class="allocation-note">${escapeHtml(allocationComparisonNoticeText(sample))}</p>`;
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

// Approximation wording for narrative report sentences: "about 8 hr 30 min". Non-finite -> "--"
// (no "about" prefix on an empty value).
  function formatApproxDurationMinutes(value) {
  return Number.isFinite(value) ? `about ${formatDurationMinutes(value)}` : "--";
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
    selector: "[data-report-doctor-id], [data-report-doctor-tab], .report-table button",
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
  const openReviewQueueButton = event.target.closest("[data-action='open-review-queue']");
  if (openReviewQueueButton) {
    const detail = document.getElementById("reportDetail");
    if (detail) {
      detail.open = true;
    }
    document.getElementById("exceptionCyclesBody")?.focus();
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
  ["reportTrendPanel", "reportFilterBar", "reportProcedureMix", "reportInsights", "reportMetrics", "reportDetail"].forEach(id => {
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
