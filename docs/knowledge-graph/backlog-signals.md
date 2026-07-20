# ChairSide knowledge graph backlog signals

Use this file as the parking lot for ideas that matter but are not ready for implementation.

## High-value future graph expansions

- Link each report metric to the exact tests that guard it.
- Link each UI state to its lifecycle event, threshold, color language, and expected staff interpretation.
- Link each production deployment validation command to the environment where it should run.
- Add a glossary for support staff terms versus code terms.
- Add an "AI handoff map" section optimized for future coding sessions.
- Add architecture diagrams after the beta workflow settles.

## Risks to preserve

- Visual polish can accidentally reduce readability.
- Reporting charts can become too dense and cause staff disengagement.
- Generated artifacts can become noisy if committed without review discipline.
- A graph database would be premature until the simple Markdown/Mermaid approach proves useful.
- Deployment facts can become stale; include dates when capturing production environment details.
- Path isolation does not establish database identity; issue #143 PR C must add the persisted Production/Training deployment-role marker and safe existing-Production adoption.

## Candidate relationships to add later

- Procedure type -> sedation eligibility -> procedure chip accent -> report grouping.
- Doctor roster -> doctor color identity -> board rail -> room card initials.
- Room binding token -> write authorization -> room device behavior.
- SignalR/polling fallback -> live status indicator -> user trust.
- UTC report windows -> Central Time caveat -> future ReportTimeZoneOptions.
