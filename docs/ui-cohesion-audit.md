# ChairSide UI Cohesion Audit

Audit only. No application behavior, CSS, JavaScript, C#, tests, or assets were changed to produce this document.

Scope inspected: `src/ChairSide.Board/wwwroot/*.html`, `styles.css` (full file), `board.js` (class/inline-style usage only), `wwwroot/assets/`, and the option classes that feed protected visual semantics (`DoctorRosterOptions.cs`, `ProcedureRosterOptions.cs`, `BoardThresholdOptions.cs`).

## 1. Summary

ChairSide has two visual dialects living in one `styles.css`. Reports, the selected-doctor detail panel (shared by `reports.html` and `doctor.html`), and Workshop share a consistent "soft card" language: `border-radius` in the 10-14px range, 1px `var(--line)` borders, gentle `box-shadow`, a small set of reusable card/table/chip/help-bubble classes, and calm, muted color use. The main board (`master.html`) and room panel (`room.html`, `room-1.html`) use a deliberately different "operational tile" language: heavier 2-6px borders, `box-shadow: none`, saturated state colors, pulsing aging/stale animations, and large 64px-minimum touch targets. That split is largely intentional -- the board and room panel exist to be readable across a clinic floor and glanceable at speed, not to look like a report.

The actual cohesion problem is narrower than "the whole app looks inconsistent": it's that (a) there is no shared token layer (color, spacing, radius, type scale) underneath either dialect, so each page/component re-derives its own numbers; (b) the board/room/doctor-list "visual polish" rules are copy-pasted three times (`body[data-view="master"]`, `body[data-view="doctor"] .doctor-list`, `body[data-view="room"] .panel-status`) including four near-duplicate `@keyframes` blocks for the same aging/stale pulse; and (c) a few components (chips, buttons, metric grids) have multiple independently-defined variants that could be one reusable component.

The desired direction is not a redesign. It's extracting the tokens and patterns that already work in Reports/Workshop/doctor views into shared, reusable building blocks, and letting the board and room panel keep their conservative, high-contrast, animation-forward operational language -- informed by the same tokens, not replaced by the report aesthetic.

## 2. Existing visual strengths

These already work well and should be the reference points for future cohesion work, not replaced:

- **Shared app shell.** `.app-header`, `.app-header.compact`, `.brand-lockup`/`.brand-logo`, and `.primary-nav`/`.nav-pill`/`.nav-menu` are identical across all seven pages (`master.html`, `index.html`, `room.html`, `room-1.html`, `reports.html`, `workshop.html`, `doctor.html`). This is the strongest existing cohesion anchor in the app.
- **Layer-pill semantic color coding.** `.layer-pill--population` (slate), `.layer-pill--data-quality` (amber), `.layer-pill--allocation` (indigo), and matching `.layer-rail--*` left-border accents give Reports a consistent way to signal "what kind of data is this" without inventing a new color per section. Workshop reuses the same indigo (`#6366f1`) for its own accent, which is a good, already-working cross-page reuse.
- **The help-bubble pattern (`.help-icon` / `.help-icon-bubble`, plus the `--corner` placement variant).** Small, calm, hover/focus-revealed, pale-yellow note card. This is genuinely reusable and already spans Reports, Workshop stats, and the doctor cockpit.
- **`.report-table` / `.report-table-wrap`.** One table treatment used for every data table in Reports (Procedure Baselines, Exceptions, Recent Completed Cycles) and reused verbatim for the selected-doctor Case Audit and Flow/Procedure Mix tabs.
- **`.report-card` / `.metric-card` / `.headline-card` / `.doctor-report-card` / `.workshop-card` / `.workshop-stat`.** All independently defined, but all converge on the same visual grammar: 1px border, 10-12px radius, white/panel background, soft shadow. They read as one family even though they aren't literally one class.
- **The selected-doctor panel is deliberately mirrored, not diverged.** `body[data-view="doctor"] .selected-doctor-panel` (and its `.selected-doctor-head`, `.selected-doctor-tabs`, `.selected-doctor-kpis`, `.selected-doctor-audit`, `.report-empty-note`) is a near-exact copy of the `body[data-view="reports"]` block, explicitly commented as mirroring on purpose "so the reused `renderSelectedDoctorPanel` output looks identical on both pages." This is good discipline, just implemented as duplication instead of a shared selector -- a strong candidate for the tokens/foundation pass.
- **The newest empty-state pattern is the most reusable one in the app.** `renderSelectedDoctorEmptyState()` in `board.js` composes `.selected-doctor-overview` / `.selected-doctor-summary` / `.report-empty-note` plus an optional `renderHelpIcon()` -- a title, a plain-English explanation, and an optional bubble. It's used for Trends, Procedure Mix, Flow Breakdown, and Case Audit empty states. This is a good template for a future formal "empty state" component.
- **Doctor/procedure identity is genuinely systemic, not decorative.** `--doctor-color` and `--procedure-accent` are CSS custom properties set inline from server-driven config (`DoctorRosterOptions.cs`, and a client-side procedure accent map in `board.js`) and consumed consistently across room tiles, selection tiles, doctor chips, doctor report cards, sparklines, and the doctor-view nav menu swatches.
- **Workshop's restraint is intentional and well-executed.** Its own comment states it stays "deliberately softer than the Reports metric cards ... so it reads as gentle context, not a staff scorecard," and the CSS backs that up (smaller values, muted tone, no motion). That's a design decision worth protecting, not a gap to close.

## 3. Reusable assets and patterns already available

Only classes/assets confirmed present in the repo are listed.

**Assets (`wwwroot/assets/`)**
- `assets/aos-logo.svg` -- the only brand asset, used via `.brand-logo` in every page header.
- `assets/icons/README.md` -- documents that procedure icons are inline SVGs in `board.js` based on Tabler Icons (MIT licensed), with a few custom drawings (forceps, impression tray, jackhammer) in the same 24x24 stroke-2 style.
- `assets/icons/interlock.png` -- present but explicitly noted in the README as **no longer referenced**; the integration-check icon it replaced is now an inline Tabler SVG. This is an orphaned asset.

**CSS custom properties (`:root`)**
- `--bg`, `--ink`, `--muted`, `--line`, `--panel`, `--empty`, `--active-doctor` -- seven tokens total. No spacing, radius, shadow, or type-scale tokens exist yet.
- `--doctor-color` (per-doctor, inline, server-config-driven) and `--procedure-accent` (per-procedure, inline, client-derived) -- the identity system.

**Shell / nav / header**
- `.app-header`, `.app-header.compact`, `.brand-lockup`, `.compact-brand`, `.primary-nav`, `.nav-pill`, `.nav-item`, `.nav-menu`, `.nav-menu-item`, `.nav-caret` -- shared verbatim across every page.
- `.master-shell` / `.panel-shell` / `.doctor-shell` / `.reports-shell` -- one shared width rule (`min(1540px, calc(100vw - 36px))`). `.workshop-shell` independently re-declares the identical width/margin/padding instead of joining this selector list.
- `.connection-status` (`.live` / `.reconnecting` / `.stale`) and `.build-version` -- shared status affordances in the header, present on every page via `board.js`.

**Cards / panels**
- `.report-card`, `.metric-card` / `.headline-card`, `.doctor-report-card` (+ `.is-empty`, `.is-selected`, `.is-panel-summary`), `.workshop-card`, `.workshop-stat`, `.insight-card`, `.selected-doctor-panel` / `.selected-doctor-summary` / `.selected-doctor-overview`, `.access-card`.
- `.report-disclosure` -- the shared `<details>`-based collapsible section treatment (All metrics, Detail & audit, Allocation Logic).

**Tables**
- `.report-table` / `.report-table-wrap` / `.report-table-section` -- the one table system, reused by every data table in the app including the selected-doctor tabs.

**Metric/KPI grids**
- `.doctor-report-metrics` (2-col, on the doctor report card) and `.selected-doctor-kpis` (4-col, on the selected-doctor tab panel) -- two independently defined but visually-related dt/dd metric-grid patterns.

**Chips / badges / status**
- `.doctor-chip`, `.procedure-chip` (board/legend, squared 8px radius).
- `.report-badge` / `.report-badge-excluded`, `.sedation-chip`, `.workshop-status`, `.layer-pill` (all pill/999px-radius, reports/workshop scoped).
- `.procedure-badge`, `.insight-code`.
- `.state-dot` (`.empty` / `.seated` / `.aging` / `.stale` / `.ready-for-doctor` / `.doctor-in-room` / `.turnover`) -- the board's own state-identity chip language, deliberately distinct from the report chip language.

**Filters / forms**
- `.report-range-chip`, `.report-filter-chip`, `.report-filter-bar`, `.report-filter-group`, `.report-advanced-filters` (a `<details>`-based modifier filter).
- `.allocation-selector` / `.allocation-step` / `.allocation-readout` -- the expected-allocation stepper control.
- `.room-token-form` / `.report-access-form` / `.access-form` -- three independently defined but near-identical labeled-input form patterns.

**Buttons**
- Room-workflow buttons: `.primary-button`, `.secondary-button`, `.danger-button`, `.available-button`, `.utility-button` -- all full-width, 64px-minimum touch targets.
- Chip-style toggle buttons: `.nav-pill`, `.report-range-chip`, `.report-filter-chip` -- all pill-radius, 40-44px min-height, `is-active`/`aria-pressed` driven.
- These are two genuinely different control idioms (board/room touch buttons vs. reports/nav chip toggles), not one button system with variants.

**Help / empty states**
- `.help-icon` / `.help-icon-bubble` (+ `.help-icon--corner` placement modifier) -- the one reusable tooltip pattern in the app.
- `.report-empty-note` (shared reports/doctor), `.report-empty-state` (reports headline only), `.workshop-note` (workshop's own "this is planned" framing), `renderSelectedDoctorEmptyState()` in `board.js` (the composed title+body+help-bubble pattern described in Section 2).

## 4. Visual inconsistencies by category

**Typography.** No shared type scale. `h1` uses `clamp(28px, 4vw, 52px)` globally, then gets three separate scoped overrides to `clamp(24px, 3.2vw, 42px)` (`body[data-view="master"] h1`, `body[data-view="doctor"] h1`, `body[data-view="room"] h1` -- three copies of the same override). Section headings are set ad hoc per component: `.report-table-section h2` is `24px`, `.doctor-report-dashboard-head h2` is `20px`, `.selected-doctor-head h2` is `21px`, `.workshop-intro h2` is `22px`, `.report-insights h2` is `22px`, `.insight-label` is `18px`, `.workshop-card-head h4` is `16px`. None of these derive from a common scale.

**Spacing.** Gap and padding values are chosen per component with no evident increment: gaps of `4px, 6px, 8px, 10px, 12px, 14px, 16px, 18px, 24px` and paddings of `8px, 9px, 10px, 12px, 14px, 16px, 18px, 20px, 24px, 28px` all appear, often for visually equivalent purposes (card internal padding is `14px 16px` on `.report-card`, `16px` on `.doctor-report-card`, `16px` on `.workshop-card`, `18px` on `.selected-doctor-panel`).

**Page gutters.** Consistent at the shell level (`.master-shell`/`.panel-shell`/`.doctor-shell`/`.reports-shell` share one width rule) but `.workshop-shell` duplicates the identical declaration instead of joining the shared selector -- same value, separate source of truth.

**Card/panel treatment.** Two coherent-but-different families, and this split is largely load-bearing (see Section 5), not accidental: the "soft card" family (reports/workshop/doctor cockpit) uses 1px borders, 10-14px radius, and soft box-shadows; the "operational tile" family (`.room-tile`, `.touch-controls`, `.corrections-panel`, `.selection-tile`) uses 2px+ borders, an 8-10px radius, and explicitly sets `box-shadow: none` in the board/room/doctor-list scoped overrides. Within the operational-tile family itself there's unnecessary drift: `body[data-view="room"] .touch-controls` and `.corrections-panel` are hard-coded to `border-radius: 8px` while the unscoped base rules use `10px`, so room-panel supporting cards are subtly squarer than the same components would be by default.

**Buttons and controls.** As noted in Section 3, there are two unrelated button idioms (large rectangular workflow buttons vs. small pill chips) with no shared base class, so hover/focus/disabled states are redefined per idiom. Within the chip idiom, `.report-filter-chip` and `.report-range-chip` are two selectors with byte-for-byte identical rule bodies (base, `:hover`, `:focus-visible`, `.is-active`) -- a direct duplication candidate.

**Chips/badges/status indicators.** The pill idiom (`.report-badge`, `.sedation-chip`, `.workshop-status`, `.layer-pill`, all `border-radius: 999px`) is consistent within Reports/Workshop, but `.doctor-chip`/`.procedure-chip` on the board legend use a squared `8px` radius, breaking that pattern where the two systems sit near each other conceptually (both are "small labeled badges"). More concretely: **`.sedation-chip` is only styled under `body[data-view="reports"]`**, but `board.js`'s `renderSelectedDoctorProcedures()` (the Procedure Mix tab) renders `<span class="sedation-chip">` and is shared by both `reports.html` and `doctor.html` via `renderSelectedDoctorPanel`. On the doctor cockpit, that chip currently has no matching selector and renders as unstyled inline text instead of the blue pill it shows on Reports.

**Tables/metrics.** `.report-table` is consistently reused everywhere (a strength). But `.doctor-report-metrics` and `.selected-doctor-kpis` are two separately authored dt/dd metric-grid components (2-column vs. 4-column, different border/radius treatment) that visually serve the same purpose and could be one component with a column-count variant.

**Page shell/header/nav.** No real inconsistency here -- see Section 2/Section 3. The only nit is `.workshop-shell`'s duplicated width declaration.

**Color usage.** `--doctor-color`/`--procedure-accent` are used consistently as semantic identity, which is correct and should stay. But neutral/structural colors are not tokenized: `#e2e8f0`, `#64748b`, `#0f172a`, `#334155`, `#6366f1`, and `rgba(23, 32, 51, ...)` each recur verbatim across many independent selectors (card borders, KPI text, workshop accent) rather than referencing a shared variable. This is the main reason the two "families" described in Section 2 can't easily be reconciled into one token set later without a dedicated pass.

**Empty/help states.** `.report-empty-note` is shared correctly between Reports and the doctor cockpit. But `.report-empty-state` (the full headline "no data yet" card) only exists for Reports; Workshop's "planned" framing uses its own `.workshop-note` with different copy conventions. Not necessarily wrong (Workshop's framing is deliberately about *planned* features, not *missing data*), but worth deciding explicitly whether these should ever converge.

**Room/board-specific presentation.** The largest concrete duplication in the file: `body[data-view="master"]`, `body[data-view="doctor"] .doctor-list`, and `body[data-view="room"] .panel-status .room-tile.large` each independently re-declare the same "compact visual polish" treatment for `.room-tile` (border-width, border-radius, `box-shadow: none`, topline-span padding/border, timer font sizes) with only cosmetic per-view differences. Each of the three blocks also defines its own `@keyframes` pair for the aging/stale pulse (`masterAgingBorderPulse`/`masterStaleBorderPulse`, `doctorAgingBorderPulse`/`doctorStaleBorderPulse`, `roomAgingBorderPulse`/`roomStaleBorderPulse`), on top of the base, unscoped `agingBorderPulse`/`staleBorderPulse` used implicitly elsewhere -- four near-identical keyframe pairs animating the same visual idea with slightly different colors/timing per view.

## 5. Protected visual semantics

These must not be casually changed by a cohesion pass. Where relevant, the config/source of truth is cited so future PRs know what they'd actually be touching.

- **Room state colors.** `.state-dot.*` and the per-state `.room-tile.<state> .room-topline span` background/color pairs (Available/In Prep/Ready/Aging/Stale/In Room/Turnover), matching the state palette defined in `AGENTS.md`. These carry the primary "where is this room in its lifecycle" signal.
- **Doctor color identities.** Sourced from `DoctorRosterOptions.cs` (`Color` field per doctor), applied everywhere via the `--doctor-color` custom property. Any token/foundation work must consume this variable, not replace or hardcode it.
- **Procedure colors/icons.** `ProcedureRosterOptions.cs` (`Icon` field) plus the inline SVG set and accent-color map in `board.js`. Icon shapes and the "avoid tiny detailed icons" guidance from `AGENTS.md` are load-bearing for at-a-glance recognition.
- **Aging/stale recognition.** Backed by `BoardThresholdOptions.cs` (`AgingMinutes`, `StaleMinutes`) and expressed visually through the `agingBorderPulse`/`staleBorderPulse` family of animations plus the amber/red badge colors. The pulsing motion itself is part of the signal here -- this is the one place in the app where motion is intentional and necessary, not decorative.
- **Live/reconnecting/stale connection status.** `.connection-status.live/.reconnecting/.stale` and the `connectionPulse` animation on the reconnecting dot. Staff need to trust this at a glance; do not soften its color contrast for aesthetic consistency.
- **Glanceability of the main board.** The explicit `box-shadow: none` and heavier borders on `.room-tile` in the master/room/doctor-list scopes are a deliberate legibility choice (confirmed by the "Board usability matters more than decorative polish" invariant in `docs/knowledge-graph/chairside.graph.md`), not an oversight to "fix" by applying the soft-card shadow language.
- **Room workflow clarity.** The enabled next action in the Primary Workflow panel uses the positive green treatment, while disabled workflow actions remain neutral. The separate `.corrections-panel` (Save Details / Discard Changes / Withdraw Ready / Cancel Seating) keeps corrective actions subordinate to the main lifecycle sequence. Do not visually flatten this hierarchy into uniform chip buttons.
- **Device/token binding UI clarity.** `.room-token-panel` / `.room-token-form` (amber-bordered) is the room-local device/token correction surface; its distinct amber treatment should stay visually separate from ordinary form fields so staff recognize it as a special-purpose control.

## 6. Reuse-first design principles

1. Prefer an existing shared class (`.report-card`, `.report-table`, `.help-icon`, `.report-empty-note`, `renderSelectedDoctorEmptyState()`) before writing a new one-off style for a new report/doctor-view feature.
2. Use the existing card/panel treatment (1px border, soft shadow, 10-14px radius) for any new "soft" surface unless it has a clear operational reason to use the board's heavier tile treatment instead (i.e., it's part of the live room-status system).
3. Keep `--doctor-color` and `--procedure-accent` semantic and identity-only. Never repurpose them for decorative accenting of unrelated UI.
4. Keep room-board and room-panel changes conservative. That surface is optimized for cross-room legibility on a TV/tablet, not for matching Reports' aesthetic.
5. When the same visual rule is needed on more than one `body[data-view]` scope, prefer one shared selector (or a shared class applied via multiple `body[data-view]` selectors in one rule, as `.master-shell, .panel-shell, .doctor-shell, .reports-shell` already does) over copy-pasting a scoped block.
6. Avoid introducing new motion. The aging/stale pulse and the connection-status pulse are the app's only intentional animations; anything new should default to static or a very short opacity fade (as `.help-icon-bubble` already does), matching the existing "calm" bar.
7. Avoid generic dashboard gloss -- no gradients, glassmorphism, card-hover-lift, or decorative iconography beyond the existing procedure icon set. Workshop's restraint (Section 2) is the right reference, not an exception.
8. Preserve readability over style. If a proposed change would reduce contrast, reduce touch-target size, or make a state/urgency cue less obvious, it doesn't ship regardless of how it affects "cohesion."

## 7. Proposed design tokens/components to standardize later

Candidates for a future, separate CSS-foundation PR (not this one):

- **Neutral color tokens** extending the existing `:root` set -- formalize the recurring `#e2e8f0` / `#64748b` / `#0f172a` / `#334155` / `#6366f1` / `rgba(23, 32, 51, *)` values noted in Section 4 into named variables, without touching `--doctor-color`/`--procedure-accent`/state colors.
- **Typography scale** -- a small set of heading/body sizes to replace the dozen or so ad hoc `h2`/`h3`/`h4` sizes in Section 4.
- **Spacing scale** -- a 4/8px-based scale to replace the current ad hoc gap/padding values.
- **Border/radius/shadow rules** -- at minimum, name the two existing radius families ("soft card" ~10-14px vs. "operational tile" ~8-10px) so future components pick one on purpose instead of by accident.
- **Page shell classes** -- fold `.workshop-shell` into the existing `.master-shell, .panel-shell, .doctor-shell, .reports-shell` selector group (or a shared `.app-shell` token) instead of duplicating the width rule.
- **Card/panel classes** -- a shared base (`.report-card` is the closest existing candidate) that `.doctor-report-card`, `.workshop-card`, `.metric-card`, and `.selected-doctor-summary` could extend via modifiers instead of independent definitions.
- **Button classes** -- decide explicitly whether the touch-button idiom and the chip-toggle idiom stay two systems (likely correct, given room-panel vs. filter-bar contexts) and, if so, name them as two documented systems rather than leaving the split implicit.
- **Chip/badge classes** -- collapse `.report-badge`/`.sedation-chip`/`.workshop-status`/`.layer-pill` into one pill-badge base with color modifiers; fix the `.sedation-chip` doctor-view scoping gap found in Section 4 as part of this work (not this audit).
- **Table classes** -- `.report-table` is already the shared base; no new work needed beyond documenting it as the canonical table.
- **Metric card classes** -- unify `.doctor-report-metrics` and `.selected-doctor-kpis` into one dt/dd metric-grid component with a column-count modifier.
- **Empty state classes** -- formalize `renderSelectedDoctorEmptyState()`'s markup shape as the canonical empty-state pattern and consider whether `.report-empty-state` (Reports headline) and `.workshop-note` should adopt it.
- **Help bubble classes** -- already reusable (`.help-icon`/`.help-icon-bubble`/`.help-icon--corner`); no new work needed, just wider adoption where "invented operational terms" appear outside Reports/doctor views.
- **Form/filter classes** -- unify `.room-token-form`, `.report-access-form`, and `.access-form`'s near-identical labeled-input pattern into one shared form-field class.

## 8. Recommended implementation sequence

Small, ordered PRs, each independently safe to ship:

1. **Design-system foundation/tokens PR.** Add the neutral color/spacing/type/radius tokens from Section 7 to `:root`. Additive only -- no selector's computed output changes yet.
2. **Reuse existing card/panel/table/help patterns PR.** Fold `.workshop-shell`'s duplicate width rule into the shared shell selector; deduplicate `.report-filter-chip`/`.report-range-chip`; fix the `.sedation-chip` doctor-view scoping gap. All behavior-neutral, purely CSS-selector consolidation.
3. **Align low-risk supporting pages PR.** Apply the new tokens inside Reports/Workshop/doctor-cockpit component CSS (where the "soft card" language already lives) so those pages start reading from the shared scale instead of ad hoc values. No visual change intended -- same computed values, tokenized source.
4. **Align room view PR.** Carefully apply the same token layer to `.touch-controls`, `.corrections-panel`, `.selection-tile`, and other room-panel supporting chrome (not the room-status tiles themselves), fixing the `body[data-view="room"]`-only 8px radius drift noted in Section 4.
5. **Align main board last, conservatively.** Only after 1-4 are stable: fold the three duplicated "visual polish" blocks (`master`/`doctor-list`/`room .panel-status`) into one shared mixin-equivalent (a single scoped selector list, matching the existing `.master-shell, .panel-shell, ...` pattern) and collapse the four aging/stale `@keyframes` pairs into one shared pair -- verifying pixel-for-pixel that the board's current glanceable, high-contrast, `box-shadow: none` treatment is fully preserved before merging.

## 9. Non-goals

- No broad redesign of ChairSide.
- No generic SaaS gloss (gradients, glassmorphism, hover-lift cards, decorative iconography).
- No behavior changes anywhere in the app.
- No report calculation changes.
- No doctor or procedure color changes.
- No room-state semantic changes.
- No animation/motion pass -- the aging/stale and connection-status pulses stay exactly as they are.
- No replacing the board's operational clarity (heavy borders, no shadow, saturated state colors) with the Reports/Workshop decorative card styling.
