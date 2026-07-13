# ChairSide private knowledge graph

This folder is a private development aid for ChairSide. It is not part of the application runtime, not shown to clinic users, and must not contain PHI.

The purpose is to keep architecture, workflow decisions, reporting semantics, deployment notes, and deferred ideas from getting washed away while we chase bugs or build new features.

## What lives here

- `chairside.graph.md` - hand-authored conceptual graph in Mermaid plus plain-English notes.
- `decisions.md` - durable architecture and product decisions we do not want to repeatedly relitigate.
- `backlog-signals.md` - deferred ideas, risks, and future graph nodes worth expanding.
- `generated/` - mechanically generated repo inventory created by `tools/knowledge-graph/New-ChairSideKnowledgeGraph.ps1`.

## What does not live here

- Patient names, appointment details, chart numbers, or any PHI.
- Secrets, tokens, passwords, deployment credentials, or private keys.
- Large generated dumps that make normal PR review noisy.
- Runtime logic required by the app.

## Node types

Use these node categories when adding durable knowledge:

| Type | Use |
| --- | --- |
| `DomainConcept` | ChairSide concepts such as room lifecycle, procedure selection, sedation modifier, or reporting windows. |
| `WorkflowState` | Primary states such as Available, Prestaging, Seated/In Prep, Ready, In Room, and Turnover. Aging and Stale are Ready urgency projections. |
| `LifecycleEvent` | User actions such as Seat, Doctor Arrived, Doctor Complete, Room Available, Update, and Cancel. |
| `ReportMetric` | Metrics such as completed cycles, wait time, turnover, exception counts, schedule fit, and trend populations. |
| `UiSurface` | Screens, cards, panels, chips, legends, filters, and training-facing explanations. |
| `StoreOrService` | Back-end stores, services, hubs, persistence, and reporting builders. |
| `ConfigOption` | Options classes and deployed config values. |
| `TestGuard` | Regression tests that define intended semantics. |
| `DeploymentAsset` | IIS, VM, database path, DNS alias, and production validation knowledge. |
| `DesignDecision` | Chosen behavior, rejected alternatives, and rationale. |

## Edge labels

Prefer small, consistent relationship labels:

- `drives`
- `renders`
- `persists`
- `computes`
- `filters`
- `anchors`
- `excludes`
- `tests`
- `guards`
- `deploys-to`
- `depends-on`
- `should-not-break`

## Update rule

When a PR meaningfully changes behavior, reporting semantics, UI structure, deployment assumptions, or test intent, update this graph in the same PR or in a tiny follow-up PR.

For normal feature work, update three places only when relevant:

1. `chairside.graph.md` for conceptual relationships.
2. `decisions.md` for durable decisions.
3. `backlog-signals.md` for future ideas or unresolved risks.

Then regenerate the mechanical index:

```powershell
pwsh .\tools\knowledge-graph\New-ChairSideKnowledgeGraph.ps1
```

If PowerShell 7 is not available, Windows PowerShell should also work:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\knowledge-graph\New-ChairSideKnowledgeGraph.ps1
```
