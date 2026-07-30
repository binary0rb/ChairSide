import { wirePressInterruptionGuard } from "./common-interactions.js";
import { escapeAttribute, escapeHtml, renderHelpIcon } from "./dom-utils.js";
import { formatDateTime, formatDuration } from "./format-utils.js";
import {
  adminRequestHeaders,
  clearAdminToken,
  storeAdminToken
} from "./request-utils.js";

const trendMinimumComparisonCases = 3;
const trendAboutSameThresholdSeconds = 60;

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
    reportFilters: { sedation: "all", grouping: "base" },
    reportDoctorId: null,
    reportDoctorTab: "overview",
    reportPressActive: false
  };

  async function selectDateRangePreset(preset) {
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
  await reportData.reload();
}

  async function applyCustomDateRange() {
  const startInput = document.getElementById("reportRangeStart");
  const endInput = document.getElementById("reportRangeEnd");
  const start = startInput && startInput.value ? startInput.value : null;
  const end = endInput && endInput.value ? endInput.value : null;
  if (!start && !end) {
    return; // nothing to apply; leave current window
  }

  reportData.setDateRange({ preset: "custom", start, end });
  syncDateRangeControls();
  await reportData.reload();
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
  if (label === "All time") {
    el.textContent = `Showing all completed cases (${total} total)`;
    return;
  }

  const shown = r ? (r.completedRoomCyclesCount ?? 0) : 0;
  el.textContent = `Showing completed cases from ${label} (${shown} of ${total} all-time)`;
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

  const allDoctors = aggregateAllocationByDoctor(r.doctorSummaries || []);
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
  const hasData = (r.completedRoomCyclesCount || 0) > 0;

  revealReportDisclosures();
  renderReportWindow(r);
  syncDateRangeControls();
  renderReportHeadline(r, hasData);
  renderReportTrendCards(r);
  renderDoctorReportDashboard(r, hasData);
  syncReportFilterButtons();
  renderReportFilterBar(hasData);
  renderAllocationReports(r);
  renderGroupedInsights(r, hasData);
  renderFullMetrics(r, hasData);

  renderCompletedCycles(filterCyclesBySedation(r.recentCompletedCycles || []));
  renderExceptionCycles(filterCyclesBySedation(r.exceptionReviewRecords || r.exceptionCycles || []));
  renderProcedureSummaries(filterSummariesBySedation(r.procedureSummaries || []));
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
        <h2>No completed cycles yet</h2>
        <p>Operational metrics will appear here as rooms complete their cycle. Exceptions and audit detail remain available below.</p>
      </article>
    `;
    return;
  }

  headline.classList.remove("is-empty");
  const exceptions = (r.exceptionReviewRecords || r.exceptionCycles || []).length;
  headline.innerHTML = [
    renderHeadlineCard("Completed Cases", String(r.completedRoomCyclesCount ?? 0)),
    renderHeadlineCard("Avg Total to Doctor", formatDuration(r.averageSeatedToDoctorSeconds)),
    renderHeadlineCard("Avg Doctor Time", formatDuration(r.averageDoctorInRoomSeconds)),
    renderHeadlineCard("Exceptions to Review", String(exceptions), "Encounter records excluded or flagged because they require administrative review."),
    renderHeadlineCard("Sedation Cases", `${r.sedationCaseCount ?? 0} / ${r.completedRoomCyclesCount ?? 0}`, "Separates cases where sedation was selected from non-sedation cases for reporting context.")
  ].join("");
}

  function renderHeadlineCard(label, value, helpText) {
  return `
    <article class="metric-card headline-card">
      <span>${escapeHtml(label)}</span>${helpText ? renderHelpIcon(helpText) : ""}
      <strong>${escapeHtml(value)}</strong>
    </article>
  `;
}

  function renderReportTrendCards(r) {
  const panel = document.getElementById("reportTrendPanel");
  if (!panel) {
    return;
  }

  panel.hidden = false;
  panel.innerHTML = [
    renderWaitTrendCard(r),
    renderTurnoverTrendCard(r)
  ].join("");
}

  function renderWaitTrendCard(r) {
  const buckets = trendBucketsWithCases(r?.trends?.buckets, {
    countField: "completedCycleCount",
    medianField: "medianSeatedToDoctorSeconds"
  });
  const latest = buckets[buckets.length - 1];

  if (!latest) {
    return `
      <article class="report-card report-trend-card is-empty">
        <div>
          <span class="layer-pill layer-pill--population">Wait Trend</span>
          <h2>Wait trend</h2>
          <p>Not enough trend data yet.</p>
        </div>
        <p class="report-trend-note">Weekly median seated-to-doctor waits will appear here as completed room cycles accumulate.</p>
      </article>
    `;
  }

  const previous = buckets.length > 1 ? buckets[buckets.length - 2] : null;
  const comparison = describeTrendComparison(latest, previous, {
    countField: "completedCycleCount",
    medianField: "medianSeatedToDoctorSeconds",
    noPreviousText: "Not enough prior trend data for a week-to-week comparison yet.",
    lowSampleText: "More cases are needed for a reliable week-to-week comparison.",
    missingText: "Not enough trend data yet.",
    aboutSameText: "Median seated-to-doctor was about the same compared with the previous week with cases.",
    improvedPrefix: "Median seated-to-doctor improved by",
    increasedPrefix: "Median seated-to-doctor increased by",
    comparisonSuffix: "compared with the previous week with cases."
  });

  return renderTrendCard({
    title: "Wait trend",
    eyebrow: "Wait Trend",
    description: "Median seated-to-doctor for the latest weekly bucket.",
    value: formatTrendMinutes(latest.medianSeatedToDoctorSeconds),
    latest,
    previous,
    countField: "completedCycleCount",
    countLabel: "Cases in bucket",
    comparisonLabel: "Compared with previous week with cases",
    comparison
  });
}

  function renderTurnoverTrendCard(r) {
  const buckets = trendBucketsWithCases(r?.trends?.buckets, {
    countField: "turnoverCycleCount",
    medianField: "medianTurnoverSeconds"
  });
  const latest = buckets[buckets.length - 1];

  if (!latest) {
    return `
      <article class="report-card report-trend-card turnover-trend-card is-empty">
        <div>
          <span class="layer-pill layer-pill--population">Turnover Trend</span>
          <h2>Turnover trend</h2>
          <p>Not enough turnover trend data yet.</p>
        </div>
        <p class="report-trend-note">Weekly median room reset / handoff flow will appear here as completed room cycles accumulate.</p>
      </article>
    `;
  }

  const previous = buckets.length > 1 ? buckets[buckets.length - 2] : null;
  const comparison = describeTrendComparison(latest, previous, {
    countField: "turnoverCycleCount",
    medianField: "medianTurnoverSeconds",
    noPreviousText: "Not enough prior turnover trend data for a week-to-week comparison yet.",
    lowSampleText: "More turnover cases are needed for a reliable week-to-week comparison.",
    missingText: "Not enough turnover trend data yet.",
    aboutSameText: "Median turnover was about the same compared with the previous week with turnover cases.",
    improvedPrefix: "Median turnover improved by",
    increasedPrefix: "Median turnover increased by",
    comparisonSuffix: "compared with the previous week with turnover cases."
  });

  return renderTrendCard({
    title: "Turnover trend",
    eyebrow: "Turnover Trend",
    description: "Median room reset / handoff flow for the latest weekly bucket.",
    value: formatTrendMinutes(latest.medianTurnoverSeconds),
    latest,
    previous,
    countField: "turnoverCycleCount",
    countLabel: "Turnover cases in bucket",
    comparisonLabel: "Compared with previous week with turnover cases",
    comparison,
    cardClass: "turnover-trend-card"
  });
}

  function renderTrendCard(options) {
  const latestRange = formatTrendBucketRange(options.latest);
  const previousRange = options.previous ? formatTrendBucketRange(options.previous) : "";
  return `
    <article class="report-card report-trend-card ${escapeAttribute(options.cardClass || "")}">
      <div class="report-trend-header">
        <div>
          <span class="layer-pill layer-pill--population">${escapeHtml(options.eyebrow)}</span>
          <h2>${escapeHtml(options.title)}</h2>
          <p>${escapeHtml(options.description)}</p>
        </div>
        <strong class="report-trend-value">${escapeHtml(options.value)}</strong>
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

  function trendBucketsWithCases(buckets, options) {
  if (!Array.isArray(buckets)) {
    return [];
  }

  return buckets
    .filter(bucket => {
      const count = Number(bucket?.[options.countField]);
      const median = Number(bucket?.[options.medianField]);
      return count > 0 && Number.isFinite(median) && median >= 0;
    })
    .slice()
    .sort((a, b) => String(a.startDate || "").localeCompare(String(b.startDate || "")));
}

  function describeTrendComparison(latest, previous, options) {
  if (!previous) {
    return {
      tone: "is-neutral",
      text: options.noPreviousText
    };
  }

  const latestCount = Number(latest[options.countField] || 0);
  const previousCount = Number(previous[options.countField] || 0);
  if (latestCount < trendMinimumComparisonCases || previousCount < trendMinimumComparisonCases) {
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
  const seconds = Math.max(0, Number(totalSeconds) || 0);
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

  const doctors = aggregateAllocationByDoctor(r.doctorSummaries || []);
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
  if (!a || (a.allocationVarianceCycleCount || 0) === 0) {
    card.innerHTML = `
      ${pill}
      <h3>Overall Allocation Balance${renderHelpIcon("Planned time budget based on the selected procedure mix and allocation settings.")}</h3>
      <p class="allocation-empty">No allocation variance data available for this report view.</p>`;
    return;
  }

  const count = a.allocationVarianceCycleCount;
  card.innerHTML = `
    ${pill}
    <h3>Overall Allocation Balance</h3>
    <p class="allocation-lead">${count} ${count === 1 ? "case" : "cases"} measured against expected allocation across included cases in this report view.</p>
    <p class="allocation-net">Net ${renderVarianceBadge(a.netAllocationVarianceMinutes)} across included cases.</p>
    <p>Average ${renderAverageVarianceBadge(a.averageAllocationVarianceMinutes)}.</p>
    <p class="allocation-breakdown-line">${a.casesOverExpectedAllocation} over expected · ${a.casesUnderExpectedAllocation} under expected · ${a.casesAtExpectedAllocation} at expected</p>
    <p class="allocation-context">${a.adjustedAllocationCycleCount} adjusted allocation ${a.adjustedAllocationCycleCount === 1 ? "case" : "cases"} · ${a.totalExpectedAllocationMinutes} min expected · ${a.totalMeasuredCaseFlowMinutes} min measured</p>
    <p class="allocation-footnote">Practice-wide aggregate. Only includes completed cases that have an expected allocation snapshot and a Doctor Complete timestamp, so this can be fewer than total completed cases.</p>`;
}

  function renderDataQualityCard(r) {
  const card = document.getElementById("dataQualityCard");
  if (!card) {
    return;
  }

  const included = r.includedCompletedCycleCount || 0;
  const excluded = r.excludedCompletedCycleCount || 0;
  const exceptions = r.exceptionCount || 0;

  const detail = excluded === 0 && exceptions === 0
    ? `<p class="allocation-ok">All completed records in this range are included in standard metrics.</p>
       <p class="allocation-ok">No reporting exceptions found in this date range.</p>`
    : `<p class="allocation-note">${excluded} ${excluded === 1 ? "record" : "records"} excluded from standard metrics.</p>
       <p class="allocation-note">${exceptions} reporting ${exceptions === 1 ? "exception" : "exceptions"} flagged. Excluded records remain visible below with badges and reasons.</p>`;

  card.innerHTML = `
    <span class="layer-pill layer-pill--data-quality">Data Quality</span>
    <h3>Data Quality</h3>
    <p class="allocation-counts">${included} included · ${excluded} excluded</p>
    ${detail}
    <p class="allocation-footnote">Included/excluded records are a separate layer from allocation-calculable cases above.</p>`;
}

  function renderDoctorAllocation(r) {
  const list = document.getElementById("doctorAllocationList");
  if (!list) {
    return;
  }

  const aggregated = aggregateAllocationByDoctor(r.doctorSummaries || []);
  list.classList.remove("doctor-report-card-grid");
  list.innerHTML = aggregated.length
    ? aggregated.map(renderDoctorAllocationRow).join("")
    : `<p class="allocation-empty">No doctor allocation data for this range.</p>`;
}

// Sums each doctor's allocation across the returned (per-month) summaries so a doctor appears
// once. Ordered by the doctor roster - never by variance, to avoid implying a ranking.
  function aggregateAllocationByDoctor(summaries) {
  const byDoctor = new Map();
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

  const rosterCards = (getSnapshot()?.doctors || []).map(doctor => ({
    doctorId: doctor.id,
    count: 0,
    net: 0,
    over: 0,
    under: 0,
    at: 0,
    adjusted: 0,
    ...byDoctor.get(doctor.id)
  }));
  const rosterIds = new Set(rosterCards.map(item => item.doctorId));
  const historicalCards = [...byDoctor.values()]
    .filter(item => !rosterIds.has(item.doctorId))
    .sort((x, y) => rank(x.doctorId) - rank(y.doctorId));

  return [...rosterCards, ...historicalCards];
}

// Renders the inner body of a doctor report card: header (initials + name + summary), metrics dl,
// and sparkline. Used by both the interactive grid card and the non-interactive cockpit summary.
  function renderDoctorCardBody(agg, report, name, identity) {
  const count = agg.count || 0;
  const average = count > 0 ? agg.net / count : Number.NaN;
  const sparkPoints = (report?.doctorDailyAllocationSeries || []).find(item => item.doctorId === agg.doctorId)?.points;
  return `
    <header class="doctor-report-card-head">
      <span class="doctor-report-initials" aria-hidden="true">${escapeHtml(identity.initials)}</span>
      <div class="doctor-report-identity">
        <h4>${escapeHtml(name)}</h4>
        <p>${escapeHtml(doctorAllocationSummary(agg))}</p>
      </div>
    </header>
    <dl class="doctor-report-metrics">
      <div>
        <dt>Cases</dt>
        <dd>${escapeHtml(String(count))}</dd>
      </div>
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
      </div>
    </dl>
    ${renderDoctorSparkline(sparkPoints)}`;
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

  function doctorAllocationSummary(agg) {
  const count = agg.count || 0;
  if (count === 0) {
    return "No allocation variance cases in this report range.";
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
  return `
    <section class="selected-doctor-overview">
      <div class="selected-doctor-summary">
        <h3>Range Flow Summary</h3>
        <p>${escapeHtml(doctorAllocationSummary(agg))}</p>
        <p class="allocation-footnote">Uses existing doctor allocation aggregates for ${escapeHtml(r.rangeLabel || "the selected range")}.</p>
      </div>
      <dl class="selected-doctor-kpis">
        <div><dt>Cases</dt><dd>${escapeHtml(String(count))}</dd></div>
        <div><dt>Net balance</dt><dd class="${escapeAttribute(varianceClass(agg.net))}">${escapeHtml(formatSignedMinutes(agg.net))}</dd></div>
        <div><dt>Average variance</dt><dd class="${escapeAttribute(varianceClass(average))}">${escapeHtml(formatSignedMinutes(average))}</dd></div>
        <div><dt>Pressure point</dt><dd>${escapeHtml(mainPressurePoint(agg))}</dd></div>
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

// Selected-doctor Procedure Mix: the doctor's completed-case procedure breakdown for the range,
// filtered from the additive doctorProcedureMix payload. Rows are variant-level (sedation shown as
// a modifier chip, not a separate procedure); Share is each procedure's portion of this doctor's
// completed cases. Light by design - a summary line plus a compact table, no charts.
  function renderSelectedDoctorProcedures(r, agg) {
  const rows = (r.doctorProcedureMix || []).filter(row => row.doctorId === agg.doctorId);
  if (!rows.length) {
    return renderSelectedDoctorEmptyState(
      "Procedure Mix",
      "No procedure mix is available for this doctor in the current report range. This usually means there are no completed cases for this doctor/date selection yet.",
      "Share is each procedure's portion of this doctor's completed cases in the selected range."
    );
  }

  const totalCases = rows[0].doctorCompletedCaseCount || rows.reduce((sum, row) => sum + (row.caseCount || 0), 0);
  const distinct = rows.length;

  return `
    <section class="selected-doctor-overview">
      <div class="selected-doctor-summary">
        <h3>Procedure Mix${renderHelpIcon("Share is each procedure's portion of this doctor's completed cases in the selected range. Sedation is shown as a modifier of the base procedure, not a separate procedure.")}</h3>
        <p>${escapeHtml(String(totalCases))} completed case${totalCases === 1 ? "" : "s"} across ${escapeHtml(String(distinct))} procedure type${distinct === 1 ? "" : "s"} for this doctor in the selected range.</p>
      </div>
    </section>
    <div class="selected-doctor-audit">
      <table class="report-table">
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
              <td>${escapeHtml(row.procedureLabel || row.procedureCode || "Unknown")}${row.isSedationCase ? ` <span class="sedation-chip">Sedation</span>` : ""}</td>
              <td>${escapeHtml(String(row.caseCount ?? 0))}</td>
              <td>${escapeHtml(formatProcedureShare(row.shareOfDoctorCases))}</td>
            </tr>
          `).join("")}
        </tbody>
      </table>
    </div>`;
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
  if (agg.count === 0) {
    return `
      <div class="allocation-row">
        <span class="allocation-row-name">${escapeHtml(name)}</span>
        <span class="allocation-row-detail allocation-empty">No allocation variance cases.</span>
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
    .filter(summary => summary.allocation && (summary.allocation.allocationVarianceCycleCount || 0) > 0);

  list.innerHTML = families.length
    ? families.map(renderProcedureAllocationRow).join("")
    : `<p class="allocation-empty">No procedure family allocation data for this range.</p>`;
}

  function renderProcedureAllocationRow(summary) {
  const a = summary.allocation;
  const label = summary.procedureLabel || summary.procedureCode || "Unknown";
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

  function renderReportFilterBar(hasData) {
  const bar = document.getElementById("reportFilterBar");
  if (bar) {
    bar.hidden = !hasData;
  }
}

// Reflects state.reportFilters onto the static filter chips so re-renders never desync the
// pressed state from the stored filter.
  function syncReportFilterButtons() {
  document.querySelectorAll("#reportFilterBar .report-filter-chip").forEach(chip => {
    const active = state.reportFilters[chip.dataset.filterGroup] === chip.dataset.filterValue;
    chip.setAttribute("aria-pressed", String(active));
    chip.classList.toggle("is-active", active);
  });
}

// Chooses the summaries for the grouped-insights section using only backend-provided
// aggregates - never recomputing or recombining on the client. For a sedation-only or
// non-sedation-only filter, base and variant groupings coincide (each base has exactly one
// sedation and one non-sedation variant), so the variant summaries are the correct, accurate
// answer for both grouping modes. Only the unfiltered "all" view distinguishes base vs variant.
  function getInsightSummaries(r) {
  const variants = r.procedureSummaries || [];
  if (state.reportFilters.sedation === "sedation") {
    return variants.filter(summary => summary.isSedationCase);
  }
  if (state.reportFilters.sedation === "non-sedation") {
    return variants.filter(summary => !summary.isSedationCase);
  }
  return state.reportFilters.grouping === "base"
    ? (r.baseProcedureSummaries || [])
    : variants;
}

  function insightsHeadingText() {
  if (state.reportFilters.sedation === "sedation") {
    return "Sedation cases by procedure";
  }
  if (state.reportFilters.sedation === "non-sedation") {
    return "Non-sedation cases by procedure";
  }
  return state.reportFilters.grouping === "base"
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
        <div><dt>Cases</dt><dd>${escapeHtml(String(summary.completedCycleCount))}</dd></div>
        <div><dt>Avg Total</dt><dd>${escapeHtml(formatDuration(summary.averageTotalSeconds))}</dd></div>
        <div><dt>Median Total</dt><dd>${escapeHtml(formatDuration(summary.medianTotalSeconds))}</dd></div>
        <div><dt>Avg Doctor Time</dt><dd>${escapeHtml(formatDuration(summary.averageDoctorTimeSeconds))}</dd></div>
        <div><dt>Avg Ready-to-Doctor</dt><dd>${escapeHtml(formatDuration(summary.averageReadyToDoctorSeconds))}</dd></div>
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

  const dur = seconds => (hasData ? formatDuration(seconds) : "—");
  summary.innerHTML = [
    renderMetric("Completed Cycles", r.completedRoomCyclesCount, "Room cycles that reached completion and are available for reporting."),
    renderMetric("Sedation Cases", r.sedationCaseCount),
    renderMetric("Non-sedation Cases", r.nonSedationCaseCount),
    renderMetric("Exceptions Requiring Review", (r.exceptionCycles || []).length),
    renderMetric("Avg Prep Time", dur(r.averagePrepSeconds)),
    renderMetric("Median Prep Time", dur(r.medianPrepSeconds)),
    renderMetric("Avg Ready-to-Doctor Wait", dur(r.averageReadyToDoctorSeconds)),
    renderMetric("Median Ready-to-Doctor Wait", dur(r.medianReadyToDoctorSeconds)),
    renderMetric("Avg Doctor Occupied Wait", dur(r.averageDoctorOccupiedWaitSeconds), "Time a patient was ready while the doctor was already active in another room."),
    renderMetric("Median Doctor Occupied Wait", dur(r.medianDoctorOccupiedWaitSeconds)),
    renderMetric("Avg Doctor Available Wait", dur(r.averageDoctorAvailableWaitSeconds), "Time a patient was ready while the doctor was not occupied in another active room."),
    renderMetric("Median Doctor Available Wait", dur(r.medianDoctorAvailableWaitSeconds)),
    renderMetric("Avg Total to Doctor", dur(r.averageSeatedToDoctorSeconds)),
    renderMetric("Median Total to Doctor", dur(r.medianSeatedToDoctorSeconds)),
    renderMetric("Avg In Room", dur(r.averageDoctorInRoomSeconds)),
    renderMetric("Median In Room", dur(r.medianDoctorInRoomSeconds)),
    renderMetric("Avg Turnover", dur(r.averageTurnoverSeconds), "Time from Doctor Complete until the room is marked Available."),
    renderMetric("Median Turnover", dur(r.medianTurnoverSeconds)),
    renderMetric("Aging Events", r.agingEventCount, "Ready-room wait has crossed the aging threshold and may need attention."),
    renderMetric("Stale Events", r.staleEventCount, "Ready-room wait has crossed the stale threshold and should be treated as higher priority.")
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
  const token = `${reportData.getVersion()}|${state.reportFilters.sedation}`;
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

// True for composite "+SED" codes and bare legacy standalone "SED".
  function isSedationProcedureCodeClient(code) {
  return procedure.hasSedationModifier(code) || String(code || "").toUpperCase() === "SED";
}

  function filterCyclesBySedation(cycles) {
  if (state.reportFilters.sedation === "sedation") {
    return cycles.filter(cycle => isSedationProcedureCodeClient(cycle.procedureCode));
  }
  if (state.reportFilters.sedation === "non-sedation") {
    return cycles.filter(cycle => !isSedationProcedureCodeClient(cycle.procedureCode));
  }
  return cycles;
}

  function filterSummariesBySedation(summaries) {
  if (state.reportFilters.sedation === "sedation") {
    return summaries.filter(summary => summary.isSedationCase);
  }
  if (state.reportFilters.sedation === "non-sedation") {
    return summaries.filter(summary => !summary.isSedationCase);
  }
  return summaries;
}

// Empty-row copy that reflects whether an active sedation filter (not "all") is hiding rows.
  function noMatchMessage(defaultMessage) {
  return state.reportFilters.sedation === "all"
    ? defaultMessage
    : "No rows match the selected sedation filter.";
}

  function renderProcedureSummaries(summaries) {
  const body = document.getElementById("procedureSummariesBody");
  if (!body) {
    return;
  }

  const token = `${reportData.getVersion()}|${state.reportFilters.sedation}`;
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
      <td>${summary.completedCycleCount}</td>
      <td>${formatDuration(summary.averageTotalSeconds)}</td>
      <td>${formatDuration(summary.medianTotalSeconds)}</td>
      <td>${formatDuration(summary.averageReadyToDoctorSeconds)}</td>
      <td>${formatDuration(summary.averageDoctorTimeSeconds)}</td>
      <td>${formatDuration(summary.averageDoctorAvailableWaitSeconds)}</td>
      <td>${formatDuration(summary.averageDoctorOccupiedWaitSeconds)}</td>
    </tr>
  `;
}

  function renderExceptionCycles(exceptions) {
  const body = document.getElementById("exceptionCyclesBody");
  if (!body) {
    return;
  }

  const token = `${reportData.getVersion()}|${state.reportFilters.sedation}`;
  if (body.dataset.renderKey === token) {
    return;
  }
  if (state.reportPressActive) {
    return;
  }
  body.dataset.renderKey = token;
  body.innerHTML = exceptions.length
    ? exceptions.map(renderExceptionRow).join("")
    : `<tr><td colspan="12">${escapeHtml(noMatchMessage("No exceptions requiring review."))}</td></tr>`;
}

  function renderExceptionRow(cycle) {
  const doctor = getDoctorName(cycle.assignedDoctor);
  const sourceType = cycle.sourceType || "CompletedCycle";
  const reviewRecordId = Number(cycle.reviewRecordId || cycle.completedCycleId || cycle.abortedAssignmentId || 0);
  return `
    <tr>
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
                title="This keeps the record excluded from normal metrics.">
          Confirm Exclusion
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
  if (!card || card !== event.target) {
    return;
  }
  event.preventDefault();
  state.reportDoctorId = card.dataset.reportDoctorId;
  state.reportDoctorTab = "overview";
  if (reportData.getReports()) {
    renderReports();
  }
}

// Wires the static filter chips. Filter state lives in state.reportFilters (not the DOM), so a
// SignalR/poll-driven re-render preserves the user's selection; we just re-render the views.
  function wireReportFilters() {
  const bar = document.getElementById("reportFilterBar");
  if (!bar) {
    return;
  }

  bar.addEventListener("click", event => {
    const chip = event.target.closest(".report-filter-chip");
    if (!chip) {
      return;
    }

    const group = chip.dataset.filterGroup;
    const value = chip.dataset.filterValue;
    if (!group || !value || state.reportFilters[group] === value) {
      return;
    }

    state.reportFilters[group] = value;
    syncReportFilterButtons();
    if (reportData.getReports()) {
      renderReports();
    }
  });
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
  const doctorButton = event.target.closest("[data-report-doctor-id]");
  if (doctorButton) {
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

  const label = `Room ${roomId} (started ${formatDateTime(seatedAt)})`;
  if (!confirm(`Mark ${label} as an exception?\n\nIt will be removed from normal metrics and appear in Exceptions Requiring Review.`)) {
    return;
  }

  // When the stable id is present the server targets by it; roomId is included only so the
  // server-side audit log keeps room context. Otherwise fall back to the legacy compound key.
  const requestBody = hasCycleId ? { completedCycleId, roomId } : { roomId, seatedAt };

  button.disabled = true;
  try {
    const response = await request("/api/reports/cycles/mark-exception", {
      method: "POST",
      cache: "no-store",
      headers: {
        "Content-Type": "application/json",
        ...adminRequestHeaders()
      },
      body: JSON.stringify(requestBody)
    });

    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    await reportData.reload();
  } catch (error) {
    console.error("[ChairSide] Mark as exception failed.", error);
    alert("Mark as exception failed. Please try again.");
    button.disabled = false;
  }
}

  async function handleConfirmExclusionClick(button) {
  const sourceType = button.dataset.reviewSource || "CompletedCycle";
  const reviewRecordId = Number(button.dataset.reviewRecordId || button.dataset.completedCycleId);
  if (!Number.isInteger(reviewRecordId) || reviewRecordId <= 0) {
    return;
  }

  if (!confirm("Confirm exclusion of this exception?\n\nThis keeps the record excluded from normal metrics and clears it from the review queue.")) {
    return;
  }

  button.disabled = true;
  try {
    const recordPath = sourceType === "AbortedAssignment"
      ? `aborted-assignments/${reviewRecordId}`
      : `cycles/${reviewRecordId}`;
    const response = await request(`/api/reports/${recordPath}/confirm-exclusion`, {
      method: "POST",
      cache: "no-store",
      headers: {
        ...adminRequestHeaders()
      }
    });

    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    await reportData.reload();
  } catch (error) {
    console.error("[ChairSide] Confirm exclusion failed.", error);
    alert("Confirm exclusion failed. Please try again.");
    button.disabled = false;
  }
}

  function renderReportsAccessPrompt(statusCode) {
  const headline = document.getElementById("reportHeadline");
  if (!headline) {
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
      <h2>Reports Access</h2>
      <p>${escapeHtml(message)}</p>
      <form id="reportAccessForm" class="report-access-form">
        <label for="reportAccessToken">Reports token</label>
        <input id="reportAccessToken" name="reportAccessToken" type="password" autocomplete="off" required>
        <button type="submit" class="primary-button">Load Reports</button>
      </form>
      <button type="button" class="secondary-button utility-button" id="clearReportAccessToken">Clear Saved Token</button>
    </article>
  `;
  ["reportTrendPanel", "reportFilterBar", "reportInsights", "reportMetrics", "reportDetail"].forEach(id => {
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

  function renderMetric(label, value, helpText) {
  return `
    <article class="metric-card">
      <span>${escapeHtml(label)}</span>${helpText ? renderHelpIcon(helpText) : ""}
      <strong>${escapeHtml(value)}</strong>
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
  return `
    <tr>
      <td>Room ${cycle.roomId}</td>
      <td>${escapeHtml(doctor)}</td>
      <td>${renderCycleProcedureCell(cycle)}</td>
      <td>${formatDateTime(cycle.seatedAt)}</td>
      <td>${formatDateTime(cycle.readyForDoctorAt)}</td>
      <td>${formatDateTime(cycle.doctorArrivedAt)}</td>
      <td>${formatDateTime(cycle.doctorCompleteAt)}</td>
      <td>${formatDateTime(cycle.roomAvailableAt)}</td>
      <td>${formatDuration(cycle.prepSeconds)}</td>
      <td>${formatDuration(cycle.readyToDoctorSeconds)}</td>
      <td>${formatDuration(cycle.doctorOccupiedWaitSeconds)}</td>
      <td>${formatDuration(cycle.doctorAvailableWaitSeconds)}</td>
      <td>${formatDuration(cycle.seatedToDoctorSeconds)}</td>
      <td>${formatDuration(cycle.doctorInRoomSeconds)}</td>
      <td>${formatDuration(cycle.turnoverSeconds)}</td>
      <td>${formatDuration(cycle.totalRoomCycleSeconds)}</td>
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
                data-seated-at="${escapeAttribute(cycle.seatedAt || "")}">
          Mark Exception
        </button>
      </td>
    </tr>
  `;
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
