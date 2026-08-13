# Verified Mistakes Learning Ledger

## Purpose

This file records historical evidence from verified, materially preventable development failures. Its purpose is to preserve reusable lessons that may prevent repeated breakage or wasted investigation.

## Authority boundary

- The current repository shows what code and configuration exist now.
- Tests, lint rules, validation scripts, and similar checks enforce invariants where applicable.
- Canonical project and design documentation governs current project meaning where ChairSide designates it as authoritative.
- `AGENTS.md` contains durable instructions for agents working on ChairSide.
- `MISTAKES.md` contains historical incident evidence only. It does not define current product behavior and cannot override the current repository, enforceable checks, canonical documentation, or `AGENTS.md`.

Entries may become stale as the repository evolves. Before a retrieved lesson influences implementation, verify that it still applies against the current code, tests, documentation, and task requirements.

## When to consult

Do not read the entire ledger for every task. After identifying the relevant subsystem or workflow, selectively search by subsystem, filename, symptom, command, platform, or workflow term when prior incidents may be relevant.

During unusual or recurring debugging, search for overlapping symptoms before reinvestigating entirely from scratch, especially for deployment, database, transaction, test-environment, browser/runtime, tooling, or repository-process failures.

A match is a reason to investigate, not permission to assume the old lesson remains true.

## When to add an entry

Add an entry only when all of these conditions are met:

- An actual failure occurred.
- The failure was materially preventable.
- The observable symptom is understood.
- Evidence verifies the root cause.
- Recording the lesson could plausibly prevent future breakage or wasted work.

Describe only what the evidence supports. Do not embellish a failure to justify an entry.

## What not to record

Do not record speculative or unverified causes, guesses, ordinary design iteration, user preference changes, harmless typos caught during editing, existing product decisions, transient failures without an established cause, or lessons already fully represented by an enforceable invariant when the ledger adds no useful historical context.

## Promotion

Repeated, systemic, or sufficiently important lessons should be considered for stronger controls such as `AGENTS.md` instructions, regression tests, lint or static-analysis rules, validation scripts, deployment checks, CI, repository guardrails, or canonical design documentation when the lesson represents product meaning.

Do not promote a rule automatically after one incident, and do not edit repository controls merely because an entry was added. Promotion requires deliberate review that the lesson is generalizable or too important to rely on historical lookup alone. Retain the original entry after promotion so the reason for the stronger control remains auditable.

## Entry format

Place newest incidents first. Keep entries compact and evidence-based.

```markdown
## YYYY-MM-DD - Short incident title

- Area: <subsystem/files/workflow>
- What happened: <observable failure>
- Verified root cause: <cause established by evidence>
- Consequence: <impact>
- Prevention candidate: <specific future safeguard>
- Evidence: <issue/PR/test/command/file reference>
- Status: Logged
```

Status may also be `Promoted - <control/reference>` or `Superseded - <reason/reference>`.

## Incident entries

No verified incidents logged.
