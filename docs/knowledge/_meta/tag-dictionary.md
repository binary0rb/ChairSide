# ChairSide Knowledge Tag Dictionary

## 1. Title and purpose

This is the controlled vocabulary for ChairSide knowledge documents under `docs/knowledge/`.

Its purpose is to give a coding agent a fast, consistent onboarding map so it does not spend context window re-inferring project concepts, terminology, or relationships that have already been written down. A small, fixed set of tags lets an agent (or a human) search and cross-reference knowledge notes reliably instead of guessing at synonyms.

This file does not replace `docs/knowledge-graph/chairside.graph.md`. The existing knowledge graph is the hand-authored conceptual map (Mermaid diagram plus node types and edge labels, described in `docs/knowledge-graph/README.md`). This tag dictionary is the vocabulary layer for the newer `docs/knowledge/` note tree, which stores narrower, per-topic notes rather than one graph document. Where the two overlap (for example, a `docs/knowledge/` note about reporting population and a `ReportMetric` node in the graph), they should describe the same facts consistently, not duplicate or contradict each other.

## 2. Agent reading order

For any ChairSide task, read in this order before broad code search:

1. `AGENTS.md` - canonical project instructions.
2. `CLAUDE.md` - thin wrapper pointing back to `AGENTS.md`; no separate content to learn.
3. `docs/knowledge/_meta/tag-dictionary.md` (this file) - the controlled vocabulary, so any knowledge notes read next can be searched and filtered by canonical tag instead of full-text guesswork.
4. `docs/knowledge-graph/chairside.graph.md`, `decisions.md`, and `backlog-signals.md` - the existing conceptual graph and durable decisions.
5. Relevant `docs/knowledge/` notes for the task at hand, filtered by the canonical tags in Sections 5 through 7 below.
6. Only then, source code and tests, scoped to the smallest relevant file set identified by the steps above.

This order exists so that project concepts, domain rules, and known risks are loaded from cheap, dense documentation before an agent pays the cost of scanning source files to rediscover the same facts.

## 3. Source-truth relationship

The knowledge tree (`docs/knowledge/` and `docs/knowledge-graph/`) is navigation and context. It is not final source of truth for anything it describes.

- Source code is implementation truth. If a knowledge note and the code disagree about what the code does, the code is right and the note is stale.
- Tests are behavior truth. If a knowledge note and a test disagree about intended behavior, treat the test as the current intended behavior and the note as needing review (the test itself may also be wrong, but that is a code-review question, not a documentation-trust question).
- Knowledge docs explain intent, domain rules, design decisions, risks, and relationships that are not obvious from reading code or tests in isolation: why a population is scoped the way it is, why a color or state must not change, what was tried and rejected, what is deliberately deferred.
- The tag dictionary keeps those knowledge docs consistent and searchable. It is metadata about the knowledge tree, not a fourth source of truth.

An agent should treat a knowledge note as a strong hint about where to look and what constraints apply, then verify against the actual source/tests before making a change, especially for anything marked `needs-review` or with an old `last_verified_commit` (see Section 11).

## 4. Canonical tag rules

- Tags are lowercase, ASCII, kebab-case (hyphen-separated words), for example `room-lifecycle`, not `RoomLifecycle` or `room_lifecycle`.
- A tag names either a project area (Section 5), a domain concept (Section 6), or an artifact/status (Section 7). It does not carry free-text prose; the note body carries the explanation.
- Only tags listed in Sections 5, 6, and 7 are canonical. Agents must use only canonical tags when writing or updating a `docs/knowledge/` note, unless explicitly asked to propose a new tag.
- If a new tag seems necessary and none of the existing canonical tags fit, list it under a "Proposed new tags" heading in the note (see Section 8) and stop there for human review. Do not add a new tag to this dictionary silently, and do not invent a synonym of an existing canonical tag (for example, do not add `sedation-modifier` when `sedation` already exists).
- A note typically carries one or more project-area tags, one or more domain-concept tags, and exactly one primary artifact/status tag describing what kind of note it is and its current lifecycle state.
- Suggested front matter shape for a `docs/knowledge/` note:

```markdown
---
tags: [reports, reporting-population, domain-rule, active]
last_verified_commit: <short-sha>
---
```

## 5. Canonical project-area tags

Project-area tags say which part of ChairSide a note is about.

- `board` - the master board grid view and room-card presentation.
- `room` - the room-local tablet/panel view and its lifecycle controls.
- `reports` - the Reports page, its filters, tables, and summary cards.
- `doctors` - doctor roster, doctor identity, doctor color, and the doctor read-only view.
- `procedures` - procedure roster, procedure categories, icons, and procedure selection.
- `deployment` - hosting, IIS/VM setup, environment configuration, and production rollout.
- `ui-cohesion` - shared visual language, reusable CSS/components, and cross-page consistency.
- `knowledge-graph` - the knowledge tree itself: this dictionary, `docs/knowledge-graph/`, and the generator tooling.
- `tests` - test suite structure, coverage intent, and characterization tests.
- `data-persistence` - SQLite storage, restart/reload behavior, and data durability.
- `permissions` - access tokens, admin access, device/room write authorization.

## 6. Canonical domain-concept tags

Domain-concept tags say which ChairSide concept a note is about, independent of which page or file implements it.

- `room-lifecycle` - the Seat, Doctor Arrived, Doctor Complete, Room Available sequence and its states.
- `sedation` - sedation as a modifier of a primary procedure, never a standalone timed component.
- `reporting-metrics` - the calculated report values themselves (averages, medians, counts, durations).
- `reporting-population` - which cycles are included, excluded, or flagged before a metric is calculated.
- `exception-handling` - manual-review exceptions and reporting-exception (excluded) cycles.
- `schedule-fit` - expected-versus-measured case flow, schedule debt, and raw slack.
- `allocation-balance` - expected allocation versus measured case flow, by doctor and by procedure.
- `doctor-flow` - the selected-doctor detail panel: overview, trends, procedures, flow, and audit tabs.
- `observed-load` - the observed room-flow/overlap read model (`ObservedDoctorDay`) in the Flow Breakdown tab.
- `procedure-mix` - the per-doctor procedure-variant breakdown (`DoctorProcedureMixRow`) in the Procedure Mix tab.
- `device-binding` - room-local device/token identity and write authorization for a specific room.
- `signalr-polling` - the SignalR real-time channel and its polling fallback.
- `sqlite-wal` - SQLite write-ahead-log persistence behavior and restart/reload guarantees.
- `production-config` - environment-specific configuration (Production versus Development/demo).
- `non-phi` - the non-PHI boundary: what ChairSide may and may not store or display.

## 7. Canonical artifact/status tags

Artifact/status tags say what kind of note this is and where it stands.

- `domain-rule` - a rule about how the system must behave (a lifecycle, metric, or population rule).
- `design-decision` - a chosen approach plus rejected alternatives and rationale.
- `test-coverage` - a note describing what a test or test group guards and why.
- `deployment-note` - an operational or environment-specific fact tied to a deployment target.
- `risk` - a known risk, failure mode, or thing that has gone wrong before.
- `proposed` - not yet implemented; describes an idea under consideration.
- `active` - currently true and currently implemented; the default status for a verified note.
- `deprecated` - previously true, no longer accurate, kept for history rather than deleted outright.
- `needs-review` - suspected stale, incomplete, or inconsistent; should be checked before being relied on.
- `last-verified` - marks that a note carries a `last_verified_commit` value (see Section 11); used so notes with a verification record can be found and distinguished from notes that do not have one yet.

## 8. Proposed new tags process

If none of the canonical tags in Sections 5 through 7 fit a concept that needs one:

1. Do not invent and use a new tag silently.
2. Add a "Proposed new tags" heading to the note being written, listing the proposed tag, which category it would belong to (project-area, domain-concept, or artifact/status), and a one-sentence reason it is needed.
3. Stop there. Do not add the tag to this dictionary and do not treat it as canonical yet.
4. A human reviews the proposal. If approved, add the tag to the relevant section of this dictionary in the same PR (or a small follow-up PR) and record that in the PR's knowledge-impact check (Section 9).
5. If rejected or an existing tag turns out to fit after discussion, remove the proposed tag from the note and use the canonical tag instead.

This keeps the vocabulary small on purpose. A short, stable tag list is more useful for fast onboarding than a large one that grows a new near-duplicate tag every PR.

## 9. Knowledge-impact check for PRs

Every PR should include exactly one of the following in its description:

- "No knowledge docs needed."
- "Updated relevant knowledge notes." (list which ones)
- "Updated tag dictionary if a new canonical tag was approved." (reference the approval)
- "Updated generated knowledge graph or index if applicable." (confirm the generator was run)

Knowledge updates are needed when a PR introduces, renames, removes, or materially changes a ChairSide concept, metric, lifecycle rule, procedure behavior, reporting population, deployment assumption, UI design rule, or product-risk principle.

Minor fixes, typo edits, dependency patches, and internal refactors usually do not require knowledge updates unless they change meaning (for example, a refactor that also silently changes which cycles count toward a metric's population would need an update; a rename of a private local variable would not).

## 10. Periodic delta audit guidance

Because knowledge notes are navigation, not source of truth (Section 3), they can drift out of sync with the code and tests they describe. Run a periodic delta audit to catch that drift before it wastes a future agent's time.

Use this reusable audit prompt, filling in the commit or tag to compare from:

```text
Review all changes since <commit-or-tag>. Compare changed code, tests, and docs against docs/knowledge. Do not edit files. Report any concepts, domain rules, tags, relationships, source-file references, or verification notes that appear stale, missing, duplicated, or inconsistent.
```

This audit is read-only by design: it produces a report for a human (or a follow-up PR) to act on, rather than editing knowledge notes automatically. Run it at natural checkpoints such as before a release, after a batch of related PRs land, or whenever a session's own broad-review task calls for it. The report should be judged the same way a code review is: findings are candidates for follow-up, not automatically applied.

## 11. last_verified_commit guidance

Where useful, a `docs/knowledge/` note should carry a `last_verified_commit` value (a short commit SHA) recording the last time a human or agent confirmed the note still matches the current code and tests.

- Set or update `last_verified_commit` whenever a note is read, checked against source/tests as part of other work, and confirmed still accurate. It does not require a dedicated verification pass by itself; confirming it while doing something else is enough.
- A note without a `last_verified_commit` value is not automatically wrong, just unverified. Treat it with the same caution as a `needs-review` note until it has been checked.
- A note whose `last_verified_commit` is many commits or a long time behind current `HEAD`, especially in an area that has since changed, is a good candidate for the `needs-review` tag and for inclusion in the next periodic delta audit (Section 10).
- Do not use `last_verified_commit` as a substitute for actually reading the note's content. It only records that someone checked, not that the note is permanently correct.

## 12. Expected benefit

A small, fixed tag vocabulary is expected to:

- Cut the context an agent burns re-deriving concepts, rules, and relationships that are already written down, by making relevant notes findable by tag instead of full-text guessing.
- Keep knowledge notes consistent with each other, since contributors draw from the same fixed list instead of each inventing their own labels for the same idea.
- Make it easy to see what already has documented intent versus what is undocumented, by which tags exist and which notes reference them.
- Make staleness visible and actionable, through the `needs-review` tag and `last_verified_commit` guidance, rather than staleness being silent and only discovered by accident.
- Keep the PR knowledge-impact check (Section 9) lightweight and specific, so documentation upkeep stays proportional to the size of the change instead of becoming its own large task.
