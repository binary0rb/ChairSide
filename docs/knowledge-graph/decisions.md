# ChairSide durable decisions

This file records decisions that should survive across threads, PRs, and debugging sessions.

## Knowledge graph scope

**Decision:** The knowledge graph is a private development artifact stored in the repo under `docs/knowledge-graph/`.

**Rationale:** It should travel with the codebase, be reviewable in PRs, and help future AI/code sessions recover project context without needing to reread every past chat.

**Rejected alternative:** A separate external graph database as the first implementation. That is more powerful, but too heavy for the current codebase and user workflow.

## Runtime isolation

**Decision:** The graph must not be loaded by the ChairSide web app or required for build, test, deployment, or production runtime.

**Rationale:** ChairSide is already close to beta. The first graph implementation should lower coordination risk, not add operational fragility.

## Human-readable first

**Decision:** The hand-authored graph is Markdown plus Mermaid, with generated JSON/Markdown indexes as support material.

**Rationale:** The user is still building coding confidence. A readable repo-local artifact is easier to inspect, edit, diff, and review than a specialized graph store.

## Non-PHI boundary

**Decision:** The graph must never include patient-specific information, appointment data, chart numbers, secrets, credentials, or deployment passwords.

**Rationale:** ChairSide is intentionally non-PHI. The development graph should preserve that safety boundary.

## Reporting UI philosophy

**Decision:** Reporting should favor summary cards, plain-English interpretation, progressive disclosure, and operational questions over dense metric tables.

**Rationale:** Staff and doctors need useful feedback without feeling punished or overwhelmed.

## Reporting population semantics

**Decision:** Reporting population semantics are locked by characterization tests and documentation before broader reporting UI changes.

**Rationale:** Metrics can become misleading if incomplete cycles, exception cycles, phase timings, and completed-cycle populations are mixed casually.
