---
title: Environment and maintenance preflight
tags: [deployment, data-persistence, permissions, production-config, design-decision, active, last-verified]
last_verified_commit: pending-issue-143
---

# Environment and maintenance preflight

## Recognized environments

ChairSide recognizes exactly `Development`, `Training`, and `Production`. Comparison is
case-insensitive, but null, blank, whitespace-padded, and every other name fail before application
build, service resolution, database access, diagnostic log creation, or endpoint mapping.

`DeploymentEnvironmentPolicy` is the canonical source for the resolved deployment role. Normal web
startup, maintenance startup, environment-sensitive store behavior, option validation, endpoint
mapping, and tests consume that policy rather than independently interpreting arbitrary names.

## Runtime posture

- Development retains automatic demo-room seeding and the HTTP report-data seed endpoint.
- Fresh Training and Production databases initialize configured rooms as Available and do not
  receive Development demo data.
- Training does not map the Development seed endpoint, always disables the Demo Timer, and rejects
  nonzero simulated elapsed values.
- Training and Production reject the `dev-admin-token` sample when admin/report protection is
  enabled and warn when room-device binding or admin/report protection is disabled.
- Training credentials must be separate from Development and Production credentials. No deployed
  secret belongs in source control.

## Maintenance posture

`reset-training-data`, `reset-empty`, `reset-large-synthetic-report-data`, and
`reset-stress-fixture` retain their existing confirmation and argument validation. They are
allowlisted in Development and Training and refused in Production before application build or
repository construction. Unknown commands default to denied.

The existing `Reset-ChairSideTrainingData.ps1` wrapper invokes the CLI as Production and is therefore
intentionally unusable. Updating that wrapper and completing a safe Training reset workflow belongs
to a later issue #143 slice. This preflight does not implement environment-specific database paths or
database deployment-role markers.

## Source and test anchors

- `src/ChairSide.Board/Services/DeploymentEnvironmentPolicy.cs`
- `src/ChairSide.Board/Services/MaintenanceCommands.cs`
- `src/ChairSide.Board/Program.cs`
- `src/ChairSide.Board/Services/DemoBoardStore.cs`
- `tests/ChairSide.Board.Tests/EnvironmentMaintenancePreflightTests.cs`
