# AGENTS

Project guidance for AI agents working on ChairSide.
# ChairSide Project Brief

## Project name

ChairSide Board

## Purpose

ChairSide Board is an internal-only surgical room status and doctor dispatch system for an oral surgery practice.

It is a modern replacement for an older physical light-board system. The system should help doctors and staff quickly see:

1. Which rooms have seated patients
2. Which doctor is assigned
3. What procedure or visit type is involved
4. How long the patient has been seated
5. Whether the room has entered an aging or stale wait state

The system tracks rooms, not patients.

## Scope

This application is for internal use only at one oral surgery office.

It should be locally hosted on the internal network and should not require public internet exposure.

The first version should be a web application, not a native iOS or Android app.

Preferred deployment target:

- Local Windows Server VM
- Internal DNS name, for example `chairside.local` or `chairside.aospeoria.local`
- Browser-based room panels, master display, and doctor views

## Repository orientation and token discipline

Before broad source inspection, read `docs/knowledge/_meta/tag-dictionary.md` (the controlled vocabulary for ChairSide knowledge notes) right after this file and CLAUDE.md, then consult the private development knowledge graph:

- `docs/knowledge-graph/chairside.graph.md` for human-authored architecture intent.
- `docs/knowledge-graph/generated/file-inventory.md` for the repo/file map.
- `docs/knowledge-graph/generated/symbol-index.json` for symbols, routes, hubs, CSS variables, and script functions.
- `docs/knowledge-graph/generated/graph-data.json` only when structured graph data is useful.

Use the graph to identify the smallest relevant file set, then inspect only those files and their tests. Do not scan or summarize the whole repository unless the graph is insufficient or the task explicitly requires whole-repo review.

For small UI/status/version tasks, start with the graph, then likely inspect:

- `src/ChairSide.Board/wwwroot/board.js`
- `src/ChairSide.Board/wwwroot/styles.css`
- relevant shared HTML files under `src/ChairSide.Board/wwwroot/`

Do not load generated graph JSON wholesale unless needed. Prefer targeted search/read of the relevant entries.

## Verified mistakes and learning

`MISTAKES.md` is a historical evidence ledger for verified, materially preventable development failures. It is not authoritative for current behavior and must not override the current repository, tests and enforceable checks, canonical project/design documentation, or this file.

- Do not load or read the complete ledger for every task. After scoping the relevant subsystem or workflow, search it selectively when prior incidents may be relevant.
- During unusual or recurring debugging, search it for overlapping symptoms, files, commands, platforms, or workflows before repeating an investigation from scratch.
- Treat matches as evidence to investigate. Verify every retrieved lesson against the current code, tests, documentation, and task requirements before acting on it.
- Add an entry only after an actual, materially preventable failure whose symptom is understood and whose root cause is verified by evidence. Do not record speculation, routine design iteration, preference changes, trivial editing mistakes, or unverified causal claims.
- Consider repeated, systemic, or sufficiently important patterns for deliberate promotion into stronger controls such as instructions, tests, lint rules, validation scripts, deployment checks, or CI. Do not promote a rule automatically because one entry was added.
- Do not backfill speculative history from memory, old conversations, or suggestive changes without evidence establishing a useful verified incident.

## Private development knowledge graph

ChairSide includes a private development knowledge graph under `docs/knowledge-graph/`.

Use it as durable project memory for:
- architecture relationships
- reporting semantics
- lifecycle invariants
- deployment assumptions
- UI/UX constraints
- deferred ideas and backlog signals

Before planning non-trivial changes, review:
- `docs/knowledge-graph/README.md`
- `docs/knowledge-graph/chairside.graph.md`
- `docs/knowledge-graph/decisions.md`
- `docs/knowledge-graph/backlog-signals.md`

After meaningful source or documentation changes, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\knowledge-graph\New-ChairSideKnowledgeGraph.ps1
```

## PR knowledge-impact check

Every PR must include a knowledge-impact check (see the checklist in `.github/pull_request_template.md`).

Human-authored knowledge docs (`docs/knowledge/`, `docs/knowledge-graph/chairside.graph.md`, `decisions.md`, `backlog-signals.md`) are updated only when the PR changes project meaning, not merely because files changed. Meaningful changes include:

- ChairSide concepts, metrics, or lifecycle rules
- procedure behavior
- reporting populations
- deployment assumptions
- UI design rules
- the permission model
- product-risk principles
- canonical terminology

Do not update human-authored knowledge docs for changes that do not touch any of the above (typo fixes, dependency patches, internal refactors with no behavior/meaning change, and similar).

Do not invent new tags. Use only the canonical tags in `docs/knowledge/_meta/tag-dictionary.md`. If a new tag seems necessary, list it under a "Proposed new tags" heading in the note being edited, or in the task summary if no note exists, and stop for human review rather than adding it silently.

Regenerate the generated knowledge graph/index files (`docs/knowledge-graph/generated/`) after meaningful source or documentation changes, per the command above.

## Markdown and documentation formatting

Repo Markdown and other committed docs must use ASCII-safe punctuation by default.

- Use "-" instead of an en dash or em dash.
- Use straight quotes instead of curly/smart quotes.
- Use "Section 2" instead of section-symbol references.
- Use "24x24" instead of multiplication signs.
- Use "..." instead of an ellipsis character.
- Avoid decorative Unicode symbols in committed docs unless deliberately required.

Before stopping after editing Markdown, scan edited Markdown files for non-ASCII characters with:

```powershell
Select-String -Path <file> -Pattern "[^\x00-\x7F]" -AllMatches
```

Replace any matches with ASCII equivalents before presenting the final diff.

## Critical privacy rule

Do not store, display, request, import, or infer PHI.

The system must not include:

- Patient names
- Dates of birth
- Chart numbers
- Medical histories
- Diagnoses
- Treatment notes
- Insurance data
- Billing data
- Free-text patient notes

The system may store:

- Room number
- Assigned doctor
- Procedure category
- Room state
- Timer values
- Event timestamps
- Device identity
- Non-PHI operational metrics

A useful project mantra:

> This system does not track patients. It tracks rooms.

## Doctors and colors

Use the following doctors in mockups, seed data, and UI examples:

- Dr. Otte = red, initials `LDO`
- Dr. Pledger = green, initials `JWP`
- Dr. Gibson = purple, initials `JEG`
- Dr. Schroeder = gold / yellow, initials `NDS`

Keep doctor-color assignments consistent.

## Procedure categories and icons

Use distinctive procedure icons that cannot easily be confused with each other.

Initial procedure categories:

- Consult: speech bubble icon, label `CON`
- Extraction: forceps icon, label `EXT`
- Sedation: crescent moon icon, label `SED`
- Post-op: checkmark in square icon, label `POST`
- Implant: implant screw / bolt icon, label `IMP`
- Biopsy: vial / sample icon, label `BX`

Avoid tiny detailed tooth icons because they blur together from a distance.

## Visual language

The master view should be a responsive grid of room cards showing the configured surgical rooms.

Primary room states and Ready urgency presentation:

- AVAILABLE = slate
- IN PREP = blue
- READY = gold
- AGING = orange secondary urgency while primary state remains READY
- STALE = red secondary urgency while primary state remains READY
- IN ROOM = green
- TURNOVER = purple

Current board cards keep doctor identity visible while using status labels, borders, and badges for operational urgency. Aging and stale alerts should not rely on whole-card white flashing.

The board should answer at a glance:

- Room location = where
- Doctor color = who
- Procedure icon = why
- Timer / animation = how long or how urgent operationally

## Rooms

The MVP should support a configurable surgical room count.

Rooms should be configurable, with the default early prototype using:

- Room 1 through Room 12

## Core workflow

1. `Begin Prestage` creates an episode and starts prep without requiring assignment details.
2. During `PRESTAGING` and `SEATED`, `Save Details` may persist an absent, partial, or complete canonical assignment.
3. `Seat Room` records truthful `SeatedAt`; an assignment-bearing Seat may persist its supplied draft atomically.
4. `Ready for Doctor` requires a complete, currently valid, durably saved assignment. It creates an immutable Active handoff and starts the ready-to-doctor wait window.
5. Aging and stale are urgency projections from the Active handoff's `ReadyAt`, not newly persisted primary lifecycle states.
6. `Withdraw Ready` returns to `SEATED`, clears urgency, and permits assignment correction; reissuing Ready creates a new handoff and urgency interval.
7. `Doctor Arrived` accepts the Active handoff, clears urgency, records timing, and moves the room to `IN ROOM`.
8. `Doctor Complete` starts `TURNOVER`.
9. `Room Available` returns the room to `AVAILABLE`.
10. The system logs non-PHI operational timing.

Ready locks doctor, procedure, sedation, and allocation dispatch facts. Add-on remains correctable through Ready and locks after Doctor Arrived. Cancellation and expiration before Doctor Arrived produce aborted history outside throughput. Post-arrival expiration produces a review-required exception without fabricating `DoctorCompleteAt`. Legacy persisted Aging/Stale rows remain readable recovery states.

Canonical lifecycle persistence is compare-and-swap guarded by the complete originally loaded room expectation: episode and lifecycle identity, assignment and allocation values, both handoff references, and lifecycle timestamps. Stale writes return a typed failure and do not mutate live state, reload, or retry. Ready, Withdraw Ready, and Doctor Arrived validate handoff history transactionally; Doctor Arrived also serializes durable cross-room doctor ownership. SQLite failures roll back transaction-local writes and leave live memory unchanged.

Issue #120 exposed optional draft-bearing Ready atomically through the canonical API. Omitted Ready and Doctor Arrived bodies retain the legacy-compatible `RoomStatus` response, while explicit canonical bodies return the lifecycle action envelope. Issue #121 / PR #133 (`d902a27`) completed the room-panel migration to canonical Begin Prestage, Save Details, Seat, Ready, Withdraw Ready, and Doctor Arrived flows.

Doctors should be able to view room status from a phone or workstation, but they should not be able to acknowledge or clear the room remotely.

Room lifecycle mutation should occur from the room-local tablet/panel only. When doctor-arrival conflict resolution is used, the server revalidates the conflict, auto-completes the previous room into turnover, and writes audit entries for both affected rooms.

## Metrics and reporting

`docs/design/reporting-design.md` is the canonical design authority for approved reporting semantics. It consumes the lifecycle and accepted Ready-handoff facts defined by `docs/design/prestage-assignment-lifecycle.md` and issue #111; reporting work must not redefine those lifecycle facts independently.

`docs/design/exception-handling-design.md` is the canonical authority for historical anomaly review, administrative correction overlays, dispositions, ledger history, and Data Quality reconciliation.

Core reporting rules:

- ChairSide reports only what its room events actually observe. Do not infer attendance, productivity, availability, scheduled hours, or unobserved activity.
- Ready for Doctor is the formal handoff boundary for canonical reporting attribution.
- `Ready Wait` is accepted Ready -> Doctor Arrived. `Seated -> Doctor` remains the distinct total seated interval before Doctor Arrived.
- Prominent operational timing is median-first. Averages may remain as secondary/detail context.
- An Observed Doctor Day exists only when qualifying ChairSide activity is observed for that doctor on that UTC calendar date. No-observation days are omitted and never zero-filled.
- Observed Clinical Span is first qualifying accepted Ready -> last same-day Doctor Complete.
- Doctor Working wall-clock calculations use the union of Doctor Arrived -> Doctor Complete intervals so overlap is never double-counted.
- Unstructured Time is the portion of Observed Clinical Span with no active Doctor Working interval. It must not be described as idle, unproductive, unused, available, absent, unscheduled, or recoverable time.
- Procedure Mix exists at Practice and Doctor scope. Percentages always use the current scoped included completed-case population as their denominator, and counts plus sample size remain visible.
- Sedation remains a modifier of the primary procedure, not a second case or separately timed procedure.
- Schedule Fit compares the confirmed scheduling allocation with compatible observed case-flow timing. The approved first-version measured basis remains Seated -> Doctor Complete; Doctor Time is not interchangeable with that interval.
- Schedule Fit reports expected, observed, slack, debt, signed net variance, and population coverage while evaluating the scheduling model rather than the doctor.
- Calibration Insights may surface sufficiently supported over- or under-allocation patterns for human review, but never change scheduling assumptions automatically.
- Weak samples suppress unsupported comparisons or insight language. Empty and unobserved populations are distinct from truthful observed zero values.
- Healthy Data Quality stays quiet; exclusions and pending review become visible only when context or action is needed. Audit detail remains the evidence layer behind metrics and insights.
- No doctor efficiency score, provider grade, leaderboard, attendance inference, idle-time report, quota, best/worst framing, or punitive staff metric is allowed.

Existing whole UTC report days, Monday-start UTC weeks, `DoctorCompleteAt` completed-window anchoring, accepted Ready attribution, withdrawn-handoff exclusion, legacy no-fabrication behavior, and completed/exception population partitions remain authoritative.

General descriptive sample states are Empty at `N = 0`, Limited at `N = 1-4`, and Sufficient at `N >= 5`; zero contributors within a nonempty population are Unavailable, and comparisons require every population to be Sufficient. These are not statistical-significance or Calibration Insight rules. Typical Doctor Time Range is the Type 7 Q25-to-Q75 interval over truthful Doctor Time contributors and publishes numeric endpoints only for Sufficient samples. Version-one Calibration uses the current active base-procedure roster default, exact Seated -> Doctor Complete paired seconds, N >= 10, at least 80 percent raw-sign direction across all pairs, and an agreeing all-pair median strictly beyond +/-600 seconds. The +/-600-second tolerance is inclusive AtExpected; evaluation uses only the selected population, creates an insight only for a server Qualified decision, saves no insight history, and never mutates allocation automatically.

## Data Analytics skills for reporting work

The Data Analytics plugin is available as a bundled set of skills. Invoke the narrowest relevant skill explicitly; do not ask the bundle to choose or run every analytics workflow by default.

Use this sequence for ChairSide reporting and metrics work:

1. Use `$create-data-context` only when the task explicitly asks to create or maintain a durable ChairSide reporting semantic layer. Ordinary analysis does not require saved context.
2. Use `$analyze-data-quality` before relying on new extracts, joins, populations, or dashboards. Check freshness, grain, nulls, duplicates, timestamp ordering, handoff consistency, outliers, and source-definition conflicts.
3. Use `$metric-diagnostics` when explaining why an approved metric changed across time, doctors, rooms, procedures, sedation, allocation, or other reviewed dimensions.
4. Use `$validate-data` before sharing analytical conclusions. Verify source selection, population rules, calculations, comparisons, visuals, caveats, and whether the evidence supports the conclusion.
5. Use `$visualize-data` only after the metric definition and data quality are settled. Keep charts operational, non-punitive, and aligned with the approved reporting populations.
6. Use `$build-report` when a durable stakeholder-facing analysis is requested. Use `$jupyter-notebooks` when reproducible SQL, Python, statistics, or an auditable calculation materially improves the work.

Defer `$design-kpis`, `$build-dashboard`, and `$kpi-reporting` until canonical metric definitions, sources, and data quality have been reviewed and the user explicitly requests that artifact or framework. Do not create rankings, individual performance scores, quotas, best/worst framing, or causal claims from descriptive operational data. Separate durable source facts, derived metrics, presentation-only values, and proposed future measures. Never include or infer PHI.

## MVP features

Build the MVP around:

- Configurable room-card grid master view
- Room-mounted tablet panel view
- Doctor read-only view
- Doctor color coding
- Distinctive procedure icons
- Seated timer
- Aging and stale Ready urgency presentations
- Room-local lifecycle actions
- Event logging
- Basic reporting

Out of scope for MVP:

- Patient names or PHI
- WinOMS integration
- Scheduling integration
- Billing integration
- Clinical documentation
- Emergency alerting
- Native mobile apps
- Public internet access
- Hardware buttons or custom LEDs

## Suggested technical direction

Preferred stack unless otherwise instructed:

- ASP.NET Core
- SignalR for real-time updates
- SQL Server Express or SQLite for early prototype
- Browser-based UI
- Responsive browser-based room card grid
- Local Windows VM deployment

A React frontend with ASP.NET Core backend is acceptable.

A Blazor-based implementation is also acceptable if it reduces complexity.

## Development priorities

Prioritize:

1. Reliability
2. Glanceability
3. Low-friction room workflow
4. Clean event logging
5. Simple maintainable architecture

Avoid:

- Overengineering
- Complex mobile app dependencies
- Cloud-only services
- PHI exposure
- Free-text patient fields
- Remote doctor acknowledgment/clear buttons
- Too many icons or statuses in version 1

## UX rules

Room panels should be simple and touch-friendly.

The room panel should already know which room it belongs to.

Staff should not have to select the room manually from a room-mounted panel.

The master board should be readable from across the room on a large TV.

Procedure icons should remain stable while the room background animates.

Use labels alongside icons until users learn the visual language.

## Testing expectations

### Browser automation and UI verification

- Use the Playwright MCP server (`mcp__playwright`) as the default tool for automated browser testing, end-to-end verification, and interactive UI inspection.
- Playwright should launch and manage its own browser, pages, and tabs.
- Do not ask the user to open, select, or navigate browser tabs as part of the normal testing workflow.
- Do not use the built-in ChatGPT browser or Chrome integration unless the user explicitly requests it or Playwright is unavailable.
- Prefer accessible roles, labels, and test IDs over brittle CSS selectors.
- Verify rendered UI state after each important workflow transition.
- On failure, collect the relevant visible state, console errors, network failures, and a screenshot when useful.
- If `mcp__playwright` is unavailable, report that the current Codex task does not have Playwright loaded. Do not silently substitute another browser integration.
- MCP servers added after a task was created may require restarting Codex and opening a new task before their tools are available.

When code exists, include instructions for:

- How to run locally
- How to seed demo data
- How to run tests
- How to reset the local database
- How to view the master display
- How to view a room panel
- How to view the doctor read-only display

Keep tests practical and focused on core room-state transitions and timing calculations.
