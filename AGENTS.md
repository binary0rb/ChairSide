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

Ready is the assignment-lock boundary. Cancellation and expiration before Doctor Arrived produce aborted history outside throughput. Post-arrival expiration produces a review-required exception without fabricating `DoctorCompleteAt`. Legacy persisted Aging/Stale rows remain readable recovery states.

Canonical lifecycle persistence is compare-and-swap guarded by the complete originally loaded room expectation: episode and lifecycle identity, assignment and allocation values, both handoff references, and lifecycle timestamps. Stale writes return a typed failure and do not mutate live state, reload, or retry. Ready, Withdraw Ready, and Doctor Arrived validate handoff history transactionally; Doctor Arrived also serializes durable cross-room doctor ownership. SQLite failures roll back transaction-local writes and leave live memory unchanged.

Issue #120 exposes optional draft-bearing Ready atomically through the canonical API. Omitted Ready and Doctor Arrived bodies retain the current room-panel `RoomStatus` response, while explicit canonical bodies return the lifecycle action envelope. The room-panel migration remains issue #121.

Doctors should be able to view room status from a phone or workstation, but they should not be able to acknowledge or clear the room remotely.

Room lifecycle mutation should occur from the room-local tablet/panel only. When doctor-arrival conflict resolution is used, the server revalidates the conflict, auto-completes the previous room into turnover, and writes audit entries for both affected rooms.

## Metrics and reporting

The primary metric is:

- Seated-to-doctor time

Definition:

- The elapsed time between when a room is seated and when the doctor physically arrives.

Track:

- SeatedAt
- ReadyForDoctorAt
- AgingStartedAt
- StaleStartedAt
- DoctorArrivedAt
- DoctorCompleteAt
- RoomAvailableAt
- Total seated-to-doctor duration
- Prep duration
- Ready-to-doctor duration
- Doctor-in-room duration
- Turnover duration
- Total room-cycle duration

Reports should eventually include:

- Average seated-to-doctor time
- Median seated-to-doctor time
- Aging event count
- Stale event count
- Total above-threshold wait time
- Trends by doctor, room, procedure, and time of day

The accepted Ready handoff is the finalized reporting assignment. Withdrawn handoffs do not become accepted attribution, pre-arrival aborts stay outside throughput, and post-arrival expiration belongs only to the review-required exception population.

Reports should be operational, non-punitive, and team-process oriented. Avoid doctor or staff rankings, best/worst framing, scoreboards, awards, shame language, or productivity theater. Use summary cards, median/average timing context, plain-English explanations, progressive disclosure, and operational questions.

Workshop and projection language should frame outputs as scenario exploration, not prediction. Do not imply ChairSide can perfectly predict capacity or that observed slack is automatically recoverable time.

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
