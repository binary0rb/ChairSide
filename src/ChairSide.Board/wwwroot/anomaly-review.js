import { escapeAttribute, escapeHtml } from "./dom-utils.js";
import { formatDateTime } from "./format-utils.js";

const DEFAULT_STATUS = "NeedsReview";
const PAGE_SIZE = 50;

export function createAnomalyReview({
  reportData,
  request = (...args) => fetch(...args),
  adminHeaders = () => ({}),
  reloadReports = () => reportData.reloadAfterCurrent(),
  confirmAction = message => globalThis.confirm(message)
}) {
  const state = {
    status: DEFAULT_STATUS,
    broadened: false,
    reportVersion: -1,
    list: null,
    listLoading: false,
    listError: null,
    selected: null,
    detail: null,
    detailLoading: false,
    ledger: null,
    options: null,
    feedback: null,
    pending: false,
    requestGeneration: 0
  };

  function render(report = reportData.getReports()) {
    const target = document.getElementById("reportAnomalyReviewBody");
    if (!target) return;
    const count = document.getElementById("reportAnomalyReviewCount");
    const total = state.list?.totalMatchingCount;
    if (count) count.textContent = Number.isFinite(total) ? `(${total})` : "";

    target.innerHTML = `
      <section class="anomaly-review-shell" aria-label="Anomaly review and history">
        <div class="anomaly-review-head">
          <div>
            <h3>Anomaly review and history</h3>
            <p>${escapeHtml(scopeDescription(report))}</p>
          </div>
          <button type="button" class="secondary-button utility-button" data-anomaly-scope-toggle>
            ${state.broadened ? "Return to report scope" : "View all anomaly history"}
          </button>
        </div>
        ${state.broadened ? `<p class="anomaly-scope-notice">Showing all anomaly history across all time, Practice scope, all doctors, all sedation states, and all procedures.</p>` : ""}
        <div class="anomaly-status-filters" role="group" aria-label="Anomaly status">
          ${statusButton("NeedsReview", "Needs Review")}
          ${statusButton("ConfirmedException", "Confirmed Exceptions")}
          ${statusButton("ClearedForReporting", "Cleared")}
          ${statusButton("AllAnomalies", "All Anomalies")}
        </div>
        <div class="anomaly-review-grid">
          <section class="anomaly-list-panel" aria-labelledby="anomalyListHeading">
            <div class="anomaly-list-heading">
              <h4 id="anomalyListHeading">${escapeHtml(statusLabel(state.status))}</h4>
              ${renderListSort()}
            </div>
            ${renderList()}
          </section>
          <section class="anomaly-detail-panel" aria-label="Selected encounter detail">
            ${renderDetail()}
          </section>
        </div>
      </section>`;
  }

  function statusButton(value, label) {
    const active = state.status === value;
    return `<button type="button" class="report-filter-chip${active ? " is-active" : ""}"
      data-anomaly-status="${value}" aria-pressed="${String(active)}">${label}</button>`;
  }

  function scopeDescription(report) {
    if (state.broadened) return "Scope: deliberately broadened anomaly history.";
    const query = report?.query || {};
    const scope = query.scope === "Doctor"
      ? `Doctor ${query.doctorId || "not selected"}`
      : "Practice";
    const sedation = query.sedation === "Sedation"
      ? "Sedation"
      : query.sedation === "NonSedation" ? "Non-sedation" : "All sedation states";
    return `Scope inherited from the active report: ${report?.rangeLabel || "All time"}; ${scope}; ${sedation}.`;
  }

  function renderListSort() {
    const sort = state.list?.activeSort || "MostRecent";
    return `<label>Sort
      <select data-anomaly-sort>
        ${["MostRecent", "Doctor", "Procedure"].map(value =>
          `<option value="${value}"${sort === value ? " selected" : ""}>${formatToken(value)}</option>`).join("")}
      </select>
    </label>`;
  }

  function renderList() {
    if (state.listError) return `<p class="report-table-context anomaly-error">${escapeHtml(state.listError)}</p>`;
    if (!state.list) return `<p class="report-table-context">Loading anomaly history...</p>`;
    const rows = state.list.reviewRows || [];
    const content = rows.length ? rows.map(renderListRow).join("")
      : `<p class="report-table-context">No anomalies match this status and scope.</p>`;
    return `<div class="anomaly-list" aria-busy="${String(state.listLoading)}">${content}</div>
      ${state.list.hasMore ? `<button type="button" class="secondary-button utility-button" data-anomaly-load-more>Load more encounters</button>` : ""}
      ${state.listLoading ? `<p class="report-table-context">Loading...</p>` : ""}`;
  }

  function renderListRow(row) {
    const key = keyOf(row.sourceType, row.reviewRecordId);
    const selected = state.selected === key;
    return `<button type="button" class="anomaly-list-row status-${escapeAttribute(row.disposition)}${selected ? " is-selected" : ""}"
      data-anomaly-select data-source-type="${escapeAttribute(row.sourceType)}"
      data-source-record-id="${row.reviewRecordId}" aria-pressed="${String(selected)}">
      <span class="anomaly-list-row-top"><strong>${escapeHtml(statusLabel(row.disposition))}</strong><time>${formatRecordedDateTime(row.reviewAnchor)}</time></span>
      <span>Room ${row.roomId} - ${escapeHtml(row.doctorName || "Not recorded")}</span>
      <span>${escapeHtml(row.procedureLabel || "Not recorded")} - ${escapeHtml(sourceLabel(row.sourceType))}</span>
      <span>${escapeHtml(reasonLabel(row.reason))}${row.hasHistoricalCorrection ? " - Historical Correction" : ""}</span>
    </button>`;
  }

  function renderDetail() {
    if (state.detailLoading) return `<p class="report-table-context">Loading selected encounter...</p>`;
    if (!state.detail) {
      const feedback = state.feedback
        ? `<p class="anomaly-inline-feedback tone-${escapeAttribute(state.feedback.tone || "error")}" role="alert" tabindex="-1" data-anomaly-feedback>${escapeHtml(state.feedback.message)}</p>`
        : "";
      return `${feedback}<div class="anomaly-detail-empty"><h4>Select an encounter</h4><p>Choose one bounded history row to inspect its evidence, effective metadata, decisions, and ledger.</p></div>`;
    }
    const detail = state.detail;
    const original = detail.originalEvidence?.metadata || {};
    const effective = detail.effectiveMetadata || {};
    const lifecycle = detail.originalEvidence?.lifecycle || {};
    const needsReview = detail.disposition === "NeedsReview";
    const noAnomaly = detail.disposition === "NoAnomaly";
    const feedbackRole = state.feedback?.tone === "error" || state.feedback?.tone === "stale" ? "alert" : "status";
    return `
      <div class="anomaly-detail" data-anomaly-detail-key="${escapeAttribute(state.selected || "")}">
        <p class="anomaly-inline-feedback tone-${escapeAttribute(state.feedback?.tone || "quiet")}" role="${feedbackRole}" tabindex="-1" data-anomaly-feedback${state.feedback ? "" : " hidden"}>${escapeHtml(state.feedback?.message || "")}</p>
        <div class="anomaly-detail-summary">
          <div>
            <span class="anomaly-status-badge status-${escapeAttribute(detail.disposition)}">${escapeHtml(statusLabel(detail.disposition))}</span>
            <h4 id="anomalyDetailHeading" tabindex="-1">${escapeHtml(sourceIdentity(detail.sourceType, detail.sourceRecordId))}</h4>
          </div>
          <dl>
            <div><dt>Reason</dt><dd>${escapeHtml(reasonLabel(detail.reason))}</dd></div>
            <div><dt>Reason source</dt><dd>${escapeHtml(actorLabel(detail.reasonSource))}</dd></div>
            <div><dt>Room</dt><dd>${Number.isFinite(lifecycle.roomId) ? `Room ${lifecycle.roomId}` : "Not recorded"}</dd></div>
            <div><dt>Review anchor</dt><dd>${formatRecordedDateTime(reviewAnchor(lifecycle))}</dd></div>
          </dl>
          <p class="anomaly-reporting-effect">${escapeHtml(reportingEffect(detail.disposition))}</p>
          ${(detail.reportingExclusionReasons || []).length ? `<p class="anomaly-derived-exclusions"><strong>Ordinary reporting conditions:</strong> ${escapeHtml(detail.reportingExclusionReasons.map(formatToken).join(", "))}</p>` : ""}
          ${detail.reviewProvenance?.importedReviewedAt ? `<p class="report-table-context">Imported reviewed-at evidence: ${formatDateTime(detail.reviewProvenance.importedReviewedAt)}.</p>` : ""}
        </div>
        ${noAnomaly ? renderMarkForm() : ""}
        <details class="anomaly-evidence-section" open>
          <summary>Original and current effective metadata</summary>
          <div class="anomaly-metadata-grid">${metadataRows(original, effective, detail.correctionIndicators || {})}</div>
        </details>
        ${needsReview ? renderNeedsReviewActions(detail) : renderResolvedActions(detail)}
        <details class="anomaly-evidence-section">
          <summary>Immutable lifecycle evidence</summary>
          ${renderLifecycle(lifecycle)}
        </details>
        <details class="anomaly-evidence-section">
          <summary>Ledger history</summary>
          ${renderLedger()}
        </details>
      </div>`;
  }

  function renderMarkForm() {
    return `<section class="anomaly-action-section is-primary"><h5>Mark for Review</h5>
      <p>Choose one structured reason. This provisionally excludes the encounter while review is pending.</p>
      <form data-anomaly-form="mark">${reasonSelect("markReason")}${noteField("markNote", false)}
        <button class="primary-button" type="submit"${state.pending ? " disabled" : ""}>Mark for Review</button></form></section>`;
  }

  function renderNeedsReviewActions(detail) {
    const support = detail.correctionSupport || {};
    return `<section class="anomaly-actions" aria-label="Review decisions and corrections">
      <section class="anomaly-action-section is-primary"><h5>Review reason and notes</h5>
        <form data-anomaly-form="refine">${reasonSelect("refineReason", detail.reason)}<button class="secondary-button" type="submit"${state.pending ? " disabled" : ""}>Update reason</button></form>
        <form data-anomaly-form="note">${noteField("standaloneNote", true)}<button class="secondary-button" type="submit"${state.pending ? " disabled" : ""}>Add Note</button></form>
      </section>
      <section class="anomaly-action-section"><h5>Historical corrections</h5>
        <p>Corrections change current effective metadata only. Original Ready and lifecycle evidence remain unchanged.</p>
        <div class="anomaly-correction-grid">
          ${support.doctor ? renderDoctorCorrection(detail) : ""}
          ${support.procedure ? renderProcedureCorrection(detail) : ""}
          ${support.sedation ? renderSedationCorrection(detail) : ""}
          ${support.addOn ? renderAddOnCorrection(detail) : ""}
          ${support.expectedAllocation ? renderAllocationCorrection(detail) : ""}
        </div>
      </section>
      <section class="anomaly-action-section anomaly-decisions"><h5>Review decision</h5>
        <form data-anomaly-form="clear"><p><strong>Clear for Reporting</strong> removes the administrative review gate. Ordinary reporting eligibility still applies.</p>${noteField("clearNote", false)}<button class="primary-button" type="submit"${state.pending ? " disabled" : ""}>Clear for Reporting</button></form>
        <form data-anomaly-form="confirm"><p><strong>Confirm Exception</strong> keeps the encounter excluded from normal reporting.</p>${noteField("confirmNote", false)}<button class="danger-button" type="submit"${state.pending ? " disabled" : ""}>Confirm Exception</button></form>
      </section>
    </section>`;
  }

  function renderResolvedActions(detail) {
    if (!["ClearedForReporting", "ConfirmedException"].includes(detail.disposition)) return "";
    return `<section class="anomaly-action-section"><h5>Administrative disposition</h5>
      <p>The encounter remains inspectable with its complete provenance.</p>
      <form data-anomaly-form="reopen"><button class="secondary-button" type="submit"${state.pending ? " disabled" : ""}>Reopen Review</button></form></section>`;
  }

  function renderDoctorCorrection(detail) {
    return `<form class="anomaly-correction-card" data-anomaly-form="doctor"><h6>Correct Doctor</h6>
      <label>Current effective Doctor<select name="doctorId">${doctorOptions(detail.effectiveMetadata?.doctorId)}</select></label>
      ${noteField("doctorNote", false)}<button class="secondary-button" type="submit">Correct Doctor</button></form>`;
  }

  function renderProcedureCorrection(detail) {
    return `<form class="anomaly-correction-card" data-anomaly-form="procedure"><h6>Correct Procedure</h6>
      <label>Current effective Procedure<select name="procedureCode" data-procedure-target>${procedureOptions(detail.effectiveMetadata?.procedureCode)}</select></label>
      <label>Final sedation state<select name="sedationState" data-procedure-sedation-target><option value="">Choose when eligibility changes</option><option value="EligibleYes">Sedation</option><option value="EligibleNo">No sedation</option><option value="UnavailableProcedureIneligible">Sedation unavailable for this procedure</option></select></label>
      <p class="report-table-context" data-procedure-correction-help>Same-eligibility changes update Procedure only. Eligibility-boundary changes save Procedure and Sedation atomically.</p>
      ${noteField("procedureNote", false)}<button class="secondary-button" type="submit">Correct Procedure</button></form>`;
  }

  function renderSedationCorrection(detail) {
    const current = detail.effectiveMetadata?.sedationState;
    const procedure = findProcedure(detail.effectiveMetadata?.procedureCode);
    const choices = procedure?.sedationEligible
      ? [["EligibleYes", "Sedation"], ["EligibleNo", "No sedation"]]
      : [["UnavailableProcedureIneligible", "Sedation unavailable for this procedure"]];
    return `<form class="anomaly-correction-card" data-anomaly-form="sedation"><h6>Correct Sedation</h6>
      <label>Current effective Sedation<select name="sedationState">${choices.map(([value, label]) => `<option value="${value}"${value === current ? " selected" : ""}>${label}</option>`).join("")}</select></label>
      ${noteField("sedationNote", false)}<button class="secondary-button" type="submit">Correct Sedation</button></form>`;
  }

  function renderAddOnCorrection(detail) {
    return `<form class="anomaly-correction-card" data-anomaly-form="add-on"><h6>Correct Add-on</h6>
      <label>Current effective Add-on<select name="isAddOn"><option value="true"${detail.effectiveMetadata?.isAddOn ? " selected" : ""}>Yes</option><option value="false"${detail.effectiveMetadata?.isAddOn ? "" : " selected"}>No</option></select></label>
      ${noteField("addOnNote", false)}<button class="secondary-button" type="submit">Correct Add-on</button></form>`;
  }

  function renderAllocationCorrection(detail) {
    const allocation = detail.effectiveMetadata?.expectedAllocation || {};
    return `<form class="anomaly-correction-card" data-anomaly-form="allocation"><h6>Correct Expected Allocation</h6>
      <p>This changes the historical allocation used for this encounter. It does not recalculate from or change today's procedure default.</p>
      <label>Confirmation type<select name="allocationState"><option value="ConfirmedSuggestedValue"${allocation.state === "ConfirmedSuggestedValue" ? " selected" : ""}>Confirmed suggested value</option><option value="ConfirmedAdjustedValue"${allocation.state === "ConfirmedAdjustedValue" ? " selected" : ""}>Confirmed adjusted value</option></select></label>
      <label>Suggested units<input type="number" min="1" step="1" name="suggestedUnits" value="${escapeAttribute(String(allocation.suggestedValue || allocation.confirmedValue || ""))}" required></label>
      <label>Confirmed units<input type="number" min="1" step="1" name="confirmedUnits" value="${escapeAttribute(String(allocation.confirmedValue || ""))}" required></label>
      <p class="report-table-context" data-allocation-preview>${allocation.confirmedValue ? `${allocation.confirmedValue} units = ${allocation.confirmedValue * 10} minutes` : "Enter explicit historical units."}</p>
      ${noteField("allocationNote", false)}<button class="secondary-button" type="submit">Correct Expected Allocation</button></form>`;
  }

  function reasonSelect(id, selected = "") {
    const reasons = state.options?.reasons || [];
    return `<label for="${id}">Reason<select id="${id}" name="reason" required><option value="">Choose a reason</option>${reasons.map(reason => `<option value="${escapeAttribute(reason.token)}"${reason.token === selected ? " selected" : ""}>${escapeHtml(reason.label)}</option>`).join("")}</select></label>`;
  }

  function doctorOptions(selected) {
    return (state.options?.doctors || []).map(doctor =>
      `<option value="${escapeAttribute(doctor.id)}"${doctor.id === selected ? " selected" : ""}>${escapeHtml(doctor.displayName)}${doctor.active ? "" : " (inactive)"}</option>`).join("");
  }

  function procedureOptions(selected) {
    return (state.options?.procedures || []).map(item =>
      `<option value="${escapeAttribute(item.code)}"${item.code === selected ? " selected" : ""}>${escapeHtml(item.label)}${item.active ? "" : " (inactive)"}</option>`).join("");
  }

  function findProcedure(code) {
    return (state.options?.procedures || []).find(item => item.code === code) || null;
  }

  function noteField(id, required) {
    const maximum = state.options?.noteMaximumLength || 500;
    return `<label for="${id}">Operational note${required ? "" : " (optional)"}<textarea id="${id}" name="note" maxlength="${maximum}"${required ? " required" : ""} aria-describedby="${id}Help ${id}Count"></textarea></label>
      <p class="anomaly-note-help" id="${id}Help">Operational note only. Do not enter patient name, chart number, diagnosis, treatment narrative, or other PHI.</p>
      <p class="anomaly-note-count" id="${id}Count" data-note-count="${id}">0 / ${maximum}</p>`;
  }

  function metadataRows(original, effective, corrected) {
    return [
      ["Doctor", original.doctorId, effective.doctorId, corrected.doctor, value => doctorLabelFor(state.options, value)],
      ["Procedure", original.procedureCode, effective.procedureCode, corrected.procedure, value => procedureLabelFor(state.options, value)],
      ["Sedation", original.sedationState, effective.sedationState, corrected.sedation, sedationLabel],
      ["Add-on", original.isAddOn, effective.isAddOn, corrected.addOn, yesNo],
      ["Expected Allocation", original.expectedAllocation, effective.expectedAllocation, corrected.expectedAllocation, allocationLabel]
    ].map(([label, before, after, isCorrected, formatter]) => `<article class="anomaly-metadata-row${isCorrected ? " is-corrected" : ""}"><h5>${label}${isCorrected ? " <span>Corrected</span>" : ""}</h5><div><small>Original evidence</small><strong>${escapeHtml(formatter(before))}</strong></div><div><small>Current effective value</small><strong>${escapeHtml(formatter(after))}</strong></div></article>`).join("");
  }

  function renderLifecycle(lifecycle) {
    const facts = lifecycle.terminatedAt
      ? [["Prestage Started", lifecycle.prestageStartedAt], ["Seated", lifecycle.seatedAt], ["Ready for Doctor", lifecycle.readyForDoctorAt], ["Terminated", lifecycle.terminatedAt], ["Terminated from", lifecycle.terminatedFromState], ["Termination kind", lifecycle.terminationKind]]
      : [["Prestage Started", lifecycle.prestageStartedAt], ["Seated", lifecycle.seatedAt], ["Ready for Doctor", lifecycle.readyForDoctorAt], ["Doctor Arrived", lifecycle.doctorArrivedAt], ["Doctor Complete", lifecycle.doctorCompleteAt], ["Room Available", lifecycle.roomAvailableAt]];
    return `<dl class="anomaly-lifecycle">${facts.map(([label, value]) => `<div><dt>${label}</dt><dd>${value ? ((label === "Terminated from" || label === "Termination kind") ? escapeHtml(formatToken(value)) : formatDateTime(value)) : "Not recorded"}</dd></div>`).join("")}</dl><p class="report-table-context">Read-only lifecycle evidence. Historical review cannot edit these timestamps or the original Ready handoff.</p>`;
  }

  function renderLedger() {
    if (!state.ledger) return `<p class="report-table-context">Loading ledger history...</p>`;
    const rows = state.ledger.rows || [];
    return `<ol class="anomaly-ledger">${rows.length ? rows.map(row => `<li><div><strong>${escapeHtml(eventLabel(row.eventType))}</strong><time>${formatDateTime(row.occurredAt)}</time></div><p>${escapeHtml(actorLabel(row.actorClass))}${row.structuredReason ? ` - ${escapeHtml(reasonLabel(row.structuredReason))}` : ""}</p>${formatLedgerValues(row)}${row.administrativeNote ? `<p class="anomaly-ledger-note">${escapeHtml(row.administrativeNote)}</p>` : ""}</li>`).join("") : `<li>No administrative history recorded.</li>`}</ol>
      ${state.ledger.hasMore ? `<button type="button" class="secondary-button utility-button" data-anomaly-ledger-more>Load more history</button>` : ""}`;
  }

  function formatLedgerValues(row) {
    if (row.previousValue == null && row.newValue == null) return "";
    return `<p>${escapeHtml(readableLedgerValue(row.structuredReason, row.previousValue, state.options))} -> ${escapeHtml(readableLedgerValue(row.structuredReason, row.newValue, state.options))}</p>`;
  }

  async function loadList({ append = false } = {}) {
    const report = reportData.getReports();
    if (!report) return;
    const generation = ++state.requestGeneration;
    state.listLoading = true;
    state.listError = null;
    render(report);
    try {
      const previous = append ? state.list : null;
      const selection = anomalySelection(report, previous?.activeSort || "MostRecent", append ? previous.offset + previous.returnedCount : 0);
      const page = await reportData.queryAudit(selection);
      if (generation !== state.requestGeneration || !page) return;
      state.list = previous ? { ...page, reviewRows: [...(previous.reviewRows || []), ...(page.reviewRows || [])], returnedCount: (previous.returnedCount || 0) + (page.returnedCount || 0), offset: 0 } : page;
    } catch (error) {
      if (generation === state.requestGeneration) state.listError = error?.message || "Anomaly history could not be loaded.";
    } finally {
      if (generation === state.requestGeneration) {
        state.listLoading = false;
        render(reportData.getReports());
      }
    }
  }

  function anomalySelection(report, sort, offset) {
    const query = report?.query || {};
    return {
      from: state.broadened ? null : query.rangeStartDate || null,
      to: state.broadened ? null : query.rangeEndDate || null,
      scope: state.broadened ? "Practice" : query.scope || "Practice",
      doctorId: state.broadened ? null : query.doctorId || null,
      sedation: state.broadened ? "All" : query.sedation || "All",
      procedureGrouping: state.broadened ? "Family" : query.procedureGrouping || "Family",
      contributorKind: "AnomalyReview",
      anomalyStatus: state.status,
      sort,
      offset,
      limit: PAGE_SIZE
    };
  }

  async function ensureOptions() {
    if (state.options) return;
    state.options = await getJson("/api/reports/anomalies/options");
  }

  async function selectEncounter(sourceType, sourceRecordId, { focus = true } = {}) {
    const key = keyOf(sourceType, sourceRecordId);
    state.selected = key;
    state.detail = null;
    state.ledger = null;
    state.detailLoading = true;
    state.feedback = null;
    render();
    try {
      await ensureOptions();
      const [detail, ledger] = await Promise.all([
        getJson(`/api/reports/anomalies/${encodeURIComponent(sourceType)}/${sourceRecordId}/detail`),
        getJson(`/api/reports/anomalies/${encodeURIComponent(sourceType)}/${sourceRecordId}/ledger?offset=0&limit=${PAGE_SIZE}`)
      ]);
      if (state.selected !== key) return;
      state.detail = detail;
      state.ledger = ledger;
    } catch (error) {
      if (state.selected === key) state.feedback = { tone: "error", message: error?.message || "Encounter detail could not be loaded." };
    } finally {
      if (state.selected === key) {
        state.detailLoading = false;
        render();
        if (focus) document.getElementById("anomalyDetailHeading")?.focus({ preventScroll: true });
      }
    }
  }

  async function openForMark(sourceType, sourceRecordId) {
    const disclosure = document.getElementById("reportAnomalyReview");
    if (disclosure) disclosure.open = true;
    await selectEncounter(sourceType, sourceRecordId);
  }

  async function showStatus(status) {
    state.status = ["NeedsReview", "ConfirmedException", "ClearedForReporting", "AllAnomalies"].includes(status)
      ? status
      : DEFAULT_STATUS;
    state.selected = null;
    state.detail = null;
    state.ledger = null;
    await loadList();
  }

  async function submit(form) {
    if (!state.detail || state.pending) return;
    const type = form.dataset.anomalyForm;
    const data = new FormData(form);
    const note = String(data.get("note") || "");
    const maximum = state.options?.noteMaximumLength || 500;
    if (note.length > maximum) return setFeedback("error", `The operational note must be ${maximum} characters or fewer.`);
    const revision = state.detail.administrativeRevision;
    let path = type;
    let body = { expectedRevision: revision };
    let confirmation = null;

    if (type === "mark" || type === "refine") {
      const reason = String(data.get("reason") || "");
      if (!reason) return setFeedback("error", "Choose one structured reason.");
      path = type === "mark" ? "mark-for-review" : "refine-reason";
      body = { ...body, reason, ...(note ? { note } : {}) };
      if (type === "mark") confirmation = "Mark this encounter for review? It will be provisionally excluded while review is pending.";
    } else if (type === "note") {
      if (!note.trim()) return setFeedback("error", "Enter a non-PHI operational note.");
      body = { ...body, note };
    } else if (type === "clear" || type === "confirm") {
      body = { ...body, ...(note ? { note } : {}) };
      confirmation = type === "clear"
        ? "Clear this encounter for reporting? Ordinary reporting eligibility will still apply."
        : "Confirm this encounter as an exception? It will remain excluded from normal reporting.";
    } else if (type === "reopen") {
      confirmation = "Reopen review for this encounter?";
    } else if (type === "doctor") {
      path = "correct-doctor";
      body = { ...body, doctorId: String(data.get("doctorId")), ...(note ? { note } : {}) };
      if (body.doctorId === state.detail.effectiveMetadata?.doctorId) return setFeedback("error", "Choose a different effective Doctor.");
    } else if (type === "procedure") {
      const procedureCode = String(data.get("procedureCode"));
      const current = findProcedure(state.detail.effectiveMetadata?.procedureCode);
      const target = findProcedure(procedureCode);
      if (procedureCode === state.detail.effectiveMetadata?.procedureCode) return setFeedback("error", "Choose a different effective Procedure.");
      if (!target) return setFeedback("error", "Choose a governed Procedure.");
      if (current?.sedationEligible === target.sedationEligible) {
        path = "correct-procedure";
        body = { ...body, procedureCode, ...(note ? { note } : {}) };
      } else {
        const sedationState = String(data.get("sedationState") || "");
        if (!sedationState) return setFeedback("error", "Choose the explicit final Sedation state for this paired correction.");
        if (!target.sedationEligible && sedationState !== "UnavailableProcedureIneligible") return setFeedback("error", "Choose the procedure-ineligible Sedation state for this paired correction.");
        if (target.sedationEligible && !["EligibleYes", "EligibleNo"].includes(sedationState)) return setFeedback("error", "Choose Sedation or No sedation for this paired correction.");
        path = "correct-procedure-and-sedation";
        body = { ...body, procedureCode, sedationState, ...(note ? { note } : {}) };
      }
    } else if (type === "sedation") {
      path = "correct-sedation";
      body = { ...body, sedationState: String(data.get("sedationState")), ...(note ? { note } : {}) };
      if (body.sedationState === state.detail.effectiveMetadata?.sedationState) return setFeedback("error", "Choose a different effective Sedation state.");
    } else if (type === "add-on") {
      path = "correct-add-on";
      body = { ...body, isAddOn: data.get("isAddOn") === "true", ...(note ? { note } : {}) };
      if (body.isAddOn === state.detail.effectiveMetadata?.isAddOn) return setFeedback("error", "Choose a different effective Add-on value.");
    } else if (type === "allocation") {
      path = "correct-expected-allocation";
      const allocationState = String(data.get("allocationState"));
      const suggestedUnits = Number(data.get("suggestedUnits"));
      const confirmedUnits = Number(data.get("confirmedUnits"));
      if (!Number.isInteger(suggestedUnits) || suggestedUnits <= 0 || !Number.isInteger(confirmedUnits) || confirmedUnits <= 0) return setFeedback("error", "Enter positive whole suggested and confirmed units.");
      body = { ...body, expectedAllocation: { state: allocationState, suggestedUnits, confirmedUnits }, ...(note ? { note } : {}) };
    }

    if (confirmation && !confirmAction(confirmation)) return;
    await mutate(path, body);
  }

  async function mutate(path, body) {
    const detail = state.detail;
    const key = state.selected;
    state.pending = true;
    setFeedback("pending", "Saving the administrative action...");
    let response;
    try {
      response = await request(`/api/reports/anomalies/${encodeURIComponent(detail.sourceType)}/${detail.sourceRecordId}/${path}`, {
        method: "POST", cache: "no-store", headers: { ...adminHeaders(), "Content-Type": "application/json" }, body: JSON.stringify(body)
      });
    } catch {
      setFeedback("error", "The request outcome is unknown. ChairSide is refreshing current state before another action can be submitted.");
      await refreshSelectedAndList({ reload: true, preserveFeedback: true });
      state.pending = false;
      render();
      focusFeedback();
      return;
    }

    if (!response.ok) {
      const problem = await response.json().catch(() => null);
      if (response.status === 409 && problem?.code === "stale-write") {
        setFeedback("stale", "This encounter changed while you were reviewing it. ChairSide refreshed the current state. Review the updated values and try the action again.");
        await refreshSelectedAndList({ preserveFeedback: true });
      } else if (response.status === 409) {
        setFeedback("error", problem?.message || "The administrative state changed. ChairSide is refreshing current state.");
        await refreshSelectedAndList({ preserveFeedback: true });
      } else {
        state.pending = false;
        setFeedback("error", problem?.message || `The action failed with HTTP ${response.status}.`);
      }
      state.pending = false;
      render();
      focusFeedback();
      return;
    }

    state.feedback = { tone: "success", message: "Administrative action saved. Current reports and history were refreshed." };
    await refreshSelectedAndList({ reload: true, preserveFeedback: true });
    state.pending = false;
    render();
    if (key === state.selected) focusFeedback();
  }

  async function refreshSelectedAndList({ reload = false, preserveFeedback = false } = {}) {
    const feedback = preserveFeedback ? state.feedback : null;
    if (reload) await reloadReports().catch(() => null);
    const selected = parseKey(state.selected);
    await loadList();
    if (selected) await selectEncounter(selected.sourceType, selected.sourceRecordId, { focus: false });
    if (feedback) state.feedback = feedback;
    render();
  }

  async function loadMoreLedger() {
    if (!state.detail || !state.ledger?.hasMore) return;
    const offset = (state.ledger.offset || 0) + (state.ledger.returnedCount || 0);
    const next = await getJson(`/api/reports/anomalies/${encodeURIComponent(state.detail.sourceType)}/${state.detail.sourceRecordId}/ledger?offset=${offset}&limit=${PAGE_SIZE}`);
    state.ledger = { ...next, offset: 0, returnedCount: (state.ledger.returnedCount || 0) + (next.returnedCount || 0), rows: [...(state.ledger.rows || []), ...(next.rows || [])] };
    render();
  }

  function setFeedback(tone, message) {
    state.feedback = { tone, message };
    render();
    focusFeedback();
  }

  function focusFeedback() {
    queueMicrotask(() => document.querySelector("[data-anomaly-feedback]:not([hidden])")?.focus({ preventScroll: true }));
  }

  async function getJson(url) {
    const response = await request(url, { cache: "no-store", headers: adminHeaders() });
    if (!response.ok) {
      const problem = await response.json().catch(() => null);
      throw new Error(problem?.message || `Request failed with HTTP ${response.status}.`);
    }
    return response.json();
  }

  function wire() {
    document.addEventListener("click", async event => {
      const status = event.target.closest?.("[data-anomaly-status]");
      if (status) {
        state.status = status.dataset.anomalyStatus;
        state.selected = null; state.detail = null; state.ledger = null;
        await loadList(); return;
      }
      if (event.target.closest?.("[data-anomaly-scope-toggle]")) {
        state.broadened = !state.broadened;
        state.selected = null; state.detail = null; state.ledger = null;
        await loadList(); return;
      }
      const row = event.target.closest?.("[data-anomaly-select]");
      if (row) { await selectEncounter(row.dataset.sourceType, Number(row.dataset.sourceRecordId)); return; }
      if (event.target.closest?.("[data-anomaly-load-more]")) { await loadList({ append: true }); return; }
      if (event.target.closest?.("[data-anomaly-ledger-more]")) { await loadMoreLedger(); }
    });
    document.addEventListener("change", async event => {
      const sort = event.target.closest?.("[data-anomaly-sort]");
      if (sort && state.list) { state.list.activeSort = sort.value; await loadList(); }
    });
    document.addEventListener("input", event => {
      if (event.target.matches?.("textarea[maxlength]")) {
        const counter = document.querySelector(`[data-note-count='${event.target.id}']`);
        if (counter) counter.textContent = `${event.target.value.length} / ${event.target.maxLength}`;
        event.target.closest("form")?.querySelector("button[type='submit']")?.toggleAttribute("disabled", event.target.value.length > event.target.maxLength);
      }
      if (event.target.matches?.("[name='confirmedUnits']")) {
        const preview = event.target.closest("form")?.querySelector("[data-allocation-preview]");
        const units = Number(event.target.value);
        if (preview) preview.textContent = Number.isInteger(units) && units > 0 ? `${units} units = ${units * 10} minutes` : "Enter explicit historical units.";
      }
    });
    document.addEventListener("submit", event => {
      const form = event.target.closest?.("[data-anomaly-form]");
      if (!form) return;
      event.preventDefault();
      submit(form);
    });
  }

  function onReportRendered(report, version) {
    render(report);
    if (version !== state.reportVersion) {
      state.reportVersion = version;
      if (!state.broadened) queueMicrotask(() => loadList());
    }
  }

  return { loadList, onReportRendered, openForMark, render, selectEncounter, showStatus, wire, _state: state };
}

function keyOf(sourceType, sourceRecordId) { return `${sourceType}:${sourceRecordId}`; }
function parseKey(key) {
  if (!key) return null;
  const separator = key.lastIndexOf(":");
  return { sourceType: key.slice(0, separator), sourceRecordId: Number(key.slice(separator + 1)) };
}
function sourceLabel(value) { return value === "AbortedAssignment" ? "Aborted assignment" : "Completed cycle"; }
function sourceIdentity(type, id) { return `${sourceLabel(type)} #${id}`; }
function statusLabel(value) {
  return ({ NeedsReview: "Needs Review", ConfirmedException: "Confirmed Exception", ClearedForReporting: "Cleared for Reporting", AllAnomalies: "All Anomalies", NoAnomaly: "No Anomaly" })[value] || formatToken(value);
}
function reportingEffect(status) {
  if (status === "NeedsReview") return "Provisionally excluded from normal reporting while review is pending.";
  if (status === "ConfirmedException") return "Excluded from normal reporting.";
  if (status === "ClearedForReporting") return "Administrative review gate cleared. Ordinary reporting eligibility still applies.";
  return "No current administrative anomaly gate.";
}
function reasonLabel(value) {
  return ({ IncorrectDoctor: "Incorrect Doctor", IncorrectProcedure: "Incorrect Procedure", IncorrectCaseDetails: "Incorrect Case Details", UnexpectedLifecycle: "Unexpected Lifecycle", OtherNeedsReview: "Other / Needs Review", AfterHoursSweep: "After-hours sweep", ExceededMaxActiveDuration: "Exceeded maximum active duration" })[value] || (value ? formatToken(value) : "Not recorded");
}
function actorLabel(value) { return ({ LocalAdmin: "Local Admin", System: "System", Legacy: "Legacy" })[value] || (value ? formatToken(value) : "Not recorded"); }
function eventLabel(value) { return ({ LegacyStateImported: "Legacy State Imported", ManualFlag: "Mark for Review", SystemFinding: "System Finding", ReasonRefined: "Reason Refined", MetadataCorrected: "Metadata Corrected", NoteAdded: "Note Added", ClearedForReporting: "Cleared for Reporting", ConfirmedException: "Confirmed Exception", ReviewReopened: "Review Reopened" })[value] || formatToken(value); }
function formatToken(value) { return String(value || "Not recorded").replace(/([a-z])([A-Z])/g, "$1 $2"); }
function formatRecordedDateTime(value) { return value ? formatDateTime(value) : "Not recorded"; }
function reviewAnchor(lifecycle) { return lifecycle.terminatedAt || lifecycle.doctorCompleteAt || lifecycle.doctorArrivedAt || lifecycle.seatedAt || lifecycle.prestageStartedAt; }
function sedationLabel(value) { return ({ EligibleYes: "Sedation", EligibleNo: "No sedation", UnavailableProcedureIneligible: "Sedation unavailable for this procedure" })[value] || "Not recorded"; }
function yesNo(value) { return value === true ? "Yes" : value === false ? "No" : "Not recorded"; }
function allocationLabel(value) {
  if (!value?.confirmedValue) return "Not recorded";
  const suggested = value.suggestedValue ? `${value.suggestedValue} suggested / ` : "";
  return `${suggested}${value.confirmedValue} confirmed units (${value.confirmedValue * 10} min)`;
}
function readableLedgerValue(field, value, options) {
  if (value == null || value === "") return "Not recorded";
  if (field === "Doctor") return doctorLabelFor(options, value);
  if (field === "Procedure") return procedureLabelFor(options, value);
  if (field === "AddOn") return value === "true" ? "Yes" : value === "false" ? "No" : value;
  if (field === "Sedation") return sedationLabel(value);
  if (field === "ProcedureAndSedation" || field === "ExpectedAllocation") {
    try {
      const parsed = JSON.parse(value);
      if (field === "ProcedureAndSedation") return `${procedureLabelFor(options, parsed.procedureCode)}; ${sedationLabel(parsed.sedationState)}`;
      const confirmed = parsed.confirmedUnits ?? parsed.confirmedValue;
      const suggested = parsed.suggestedUnits ?? parsed.suggestedValue;
      return `${suggested ?? "Not recorded"} suggested / ${confirmed ?? "Not recorded"} confirmed units${confirmed ? ` (${confirmed * 10} min)` : ""}`;
    } catch { return value; }
  }
  return reasonLabel(value);
}

function doctorLabelFor(options, value) { return options?.doctors?.find(item => item.id === value)?.displayName || value || "Not recorded"; }
function procedureLabelFor(options, value) { return options?.procedures?.find(item => item.code === value)?.label || value || "Not recorded"; }
