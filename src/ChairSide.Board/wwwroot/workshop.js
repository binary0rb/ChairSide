import { escapeHtml, renderHelpIcon } from "./dom-utils.js";

export function createWorkshop({
  getReports
}) {
  function render() {
    const target = document.getElementById("workshopCurrentReality");
    if (!target) {
      return;
    }

    const reports = getReports();

    // Unavailable: reports failed to load, or internal admin access is required (no reports payload).
    // The Reports access prompt is reports-page-only; Workshop shows a calm fallback and recovers on
    // the next 60s refresh.
    if (!reports) {
      target.innerHTML = `<p class="workshop-note">Current Reality couldn't load right now.</p>`;
      return;
    }

    const fit = reports.scheduleFit;
    const rangeLabel = reports.rangeLabel || "the selected window";

    // Empty: no completed cases carrying expected allocation in this window, so there is nothing to
    // summarize. Framed gently, never as a problem.
    if (!fit || !fit.overall || (fit.scheduleFitCycleCount || 0) === 0) {
      target.innerHTML = `
        <p class="workshop-reality-window">${escapeHtml(rangeLabel)}</p>
        <p class="workshop-note">No completed cases with expected allocation in this window yet, so there's nothing to summarize.</p>
      `;
      return;
    }

    const overall = fit.overall;
    const utilization = formatUtilizationPercent(overall.utilizationRatio);
    const stats = [
      ["Cases analyzed", `${fit.scheduleFitCycleCount} of ${fit.includedCycleCount}`],
      ["Expected blocks", formatBlocks(overall.totalExpectedBlocks)],
      ["Actual case-flow blocks", formatBlocks(overall.totalActualBlocks), "Observed case-flow time converted into schedule-sized blocks for easier comparison."],
      ["Schedule debt", formatWholeMinutes(overall.totalDebtMinutes), "Time cases ran over expected allocation. Useful for planning, not blame."],
      ["Raw slack observed", formatWholeMinutes(overall.totalSlackMinutes), "Time cases ran under expected allocation. It is observed slack, not automatically reusable capacity."],
      ["Utilization vs expected", utilization, "How observed case-flow time compares with expected allocated time for the selected range."]
    ];

    const tiles = stats.map(([label, value, helpText]) => `
      <div class="workshop-stat">
        <span class="workshop-stat-label">${escapeHtml(label)}${helpText ? renderHelpIcon(helpText) : ""}</span>
        <strong class="workshop-stat-value">${escapeHtml(value)}</strong>
      </div>
    `).join("");

    target.innerHTML = `
      <p class="workshop-reality-window">${escapeHtml(rangeLabel)}</p>
      <div class="workshop-reality-grid">${tiles}</div>
      <p class="workshop-reality-explainer">
        Across these cases, measured case flow ran about ${escapeHtml(utilization)}.
        &ldquo;Schedule debt&rdquo; is time cases ran over their expected allocation; &ldquo;raw slack
        observed&rdquo; is time they ran under &mdash; raw slack is an observation here, not capacity
        that can automatically be reclaimed.
      </p>
    `;
  }

  function wire() {
    // Cards are static and never re-rendered, but delegated listeners keep this consistent with the
    // rest of the frontend and robust to any future re-render. Scoped by the data-preset-id selector.
    document.addEventListener("click", handlePresetActivate);
    document.addEventListener("keydown", handlePresetKeydown);
  }

  function handlePresetActivate(event) {
    if (!(event.target instanceof Element)) {
      return;
    }
    const card = event.target.closest('.workshop-card[data-preset-id]');
    if (!card) {
      return;
    }
    selectPreset(card);
  }

  function handlePresetKeydown(event) {
    if (event.key !== "Enter" && event.key !== " " && event.key !== "Spacebar") {
      return;
    }
    if (!(event.target instanceof Element)) {
      return;
    }
    // Only the focused card itself activates (it carries role="button" and tabindex).
    const card = event.target.closest('.workshop-card[data-preset-id]');
    if (!card || card !== event.target) {
      return;
    }
    event.preventDefault(); // Space must not scroll the page; Enter must not double-fire.
    selectPreset(card);
  }

  function selectPreset(card) {
    const cards = document.querySelectorAll('.workshop-card[data-preset-id]');
    cards.forEach(item => {
      const selected = item === card;
      item.classList.toggle("is-selected", selected);
      item.setAttribute("aria-pressed", selected ? "true" : "false");
    });

    const panel = document.getElementById("workshopPresetDetail");
    if (!panel) {
      return;
    }

    const title = card.querySelector(".workshop-card-head h4")?.textContent?.trim() || "Preset";
    const detail = readPresetSource(card, ".workshop-preset-detail-source");
    // The only preset-specific projection content. The four readiness buckets below are
    // preset-agnostic UI copy, so they stay inline rather than in a per-preset definition map.
    const assumption = readPresetSource(card, ".workshop-preset-assumption-source");

    panel.innerHTML = `
      <header class="workshop-preset-detail-head">
        <h4 class="workshop-preset-detail-title">${escapeHtml(title)}</h4>
        <span class="workshop-status">Planned</span>
      </header>
      <p class="workshop-preset-detail-text">${escapeHtml(detail)}</p>
      ${renderProjectionReadiness(assumption)}
      <p class="workshop-preset-detail-disclaimer">Planned: selecting this preset shows this explanation only. It does not run a projection, change the schedule, or alter any live data.</p>
    `;
  }

  // Reads and normalizes the whitespace of a hidden source block inside a preset card.
  function readPresetSource(card, selector) {
    const source = card.querySelector(selector);
    return source ? source.textContent.trim().replace(/\s+/g, " ") : "";
  }

  // Projection readiness scaffold: the four-part honesty separation the design principle requires.
  // Display-only and computes nothing - it explains what a scenario would need and is explicit that
  // no output is produced. The first three buckets are fixed UI copy; the "assumptions" bucket adds
  // the selected preset's one assumption line. Raw slack observed is never treated as recoverable
  // capacity here, and there is no run/apply/generate affordance.
  function renderProjectionReadiness(assumption) {
    const presetAssumption = assumption
      ? `<p class="workshop-readiness-assumption">${escapeHtml(assumption)}</p>`
      : "";

    return `
      <div class="workshop-readiness" aria-label="Projection readiness">
        <section class="workshop-readiness-bucket">
          <h5 class="workshop-readiness-heading">Observed today</h5>
          <p>ChairSide can show completed-case schedule-fit data for the selected report window: expected blocks, actual case-flow blocks, schedule debt, raw slack observed, and utilization versus expected allocation.</p>
        </section>
        <section class="workshop-readiness-bucket">
          <h5 class="workshop-readiness-heading">Assumptions a projection would require</h5>
          <p>A real scenario would need explicit assumptions before any output could be trusted: future demand, room/staff availability, turnover and sedation-recovery constraints, slack contiguity, and a chosen policy for whether any observed slack is usable.</p>
          ${presetAssumption}
        </section>
        <section class="workshop-readiness-bucket">
          <h5 class="workshop-readiness-heading">Scenario output &mdash; not computed yet</h5>
          <p>This preset does not compute an outcome yet. Selecting it only explains the lens and the assumptions a future scenario would need.</p>
        </section>
        <section class="workshop-readiness-bucket">
          <h5 class="workshop-readiness-heading">What ChairSide cannot know</h5>
          <p>ChairSide cannot know whether observed slack was contiguous, bookable, staffed, clinically appropriate, or desirable to reuse. The team would need to decide those assumptions before any scenario output could be meaningful.</p>
        </section>
      </div>
    `;
  }

  return {
    render,
    wire
  };
}

// Minutes as a whole number (e.g. "45 min"). Non-finite input degrades to an em dash.
function formatWholeMinutes(value) {
  return Number.isFinite(value) ? `${Math.round(value)} min` : "—";
}

// Blocks to one decimal (e.g. "8.0 blocks"). Non-finite input degrades to an em dash.
function formatBlocks(value) {
  return Number.isFinite(value) ? `${(Math.round(value * 10) / 10).toFixed(1)} blocks` : "—";
}

// Utilization ratio (measured / expected) as a whole percent (e.g. "112% of expected"). Null or
// non-finite ratio degrades to an em dash.
function formatUtilizationPercent(ratio) {
  return Number.isFinite(ratio) ? `${Math.round(ratio * 100)}% of expected` : "—";
}
