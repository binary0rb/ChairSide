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
- Board snapshots identify Training explicitly so the shared application shell shows one persistent
  `TRAINING` badge. Development and Production snapshots do not request the badge.
- Training and Production reject the `dev-admin-token` sample when admin/report protection is
  enabled and warn when room-device binding or admin/report protection is disabled.
- Training credentials must be separate from Development and Production credentials. No deployed
  secret belongs in source control.

## Database path isolation

The deployed database layout is code-owned and is not bound from configuration:

- Production application root: `C:\ChairSide\App`
- Production data root: `C:\ChairSide\Data`
- Production database: `C:\ChairSide\Data\chairside.db`
- Training application root: `C:\ChairSide\Training\App`
- Training data root: `C:\ChairSide\Training\Data`
- Training database: `C:\ChairSide\Training\Data\chairside-training.db`
- Training diagnostic logs: `C:\ChairSide\Training\Logs`

Production and Training require their exact canonical, fully qualified database paths. Normalized
Windows paths compare case-insensitively with directory boundaries, so `Data2` is not treated as
`Data`. Deployed paths are refused inside the actual content root, either application root, or the
opposite deployment's application/data roots. Wrong filenames, relative and drive-relative paths,
and existing directories used as the database leaf are refused. Development keeps relative paths
resolved against the content root and temporary absolute paths, except under the protected
Production or Training application/data roots.

For deployed startup, every existing component from the volume root through the database leaf is
checked for `FileAttributes.ReparsePoint`. Only genuine not-found results count as missing; metadata
access errors fail closed. The pure policy creates nothing. After it succeeds, the repository may
create a missing canonical parent directory, must rescan all components, then performs its write
test before configuring SQLite or running schema/room initialization.

## Database deployment identity

Production and Training databases carry one immutable row in `chairside_deployment_identity`. The
row uses singleton key 1, exact role `Production` or `Training`, identity schema version 1, a UTC
round-trip establishment timestamp, and the sole provenance `FreshDatabase`. Development creates
and requires no marker, but refuses any database containing a deployed or malformed reserved
marker.

```sql
CREATE TABLE chairside_deployment_identity (
    singleton_id INTEGER NOT NULL PRIMARY KEY CHECK (singleton_id = 1),
    deployment_role TEXT NOT NULL CHECK (deployment_role IN ('Production', 'Training')),
    identity_schema_version INTEGER NOT NULL CHECK (identity_schema_version = 1),
    established_at_utc TEXT NOT NULL,
    established_via TEXT NOT NULL
        CHECK (established_via = 'FreshDatabase')
) WITHOUT ROWID;
```

A genuinely new deployed database is an absent main file with no WAL/SHM companions. Its matching
marker and current ChairSide schema/indexes commit in one immediate transaction before WAL is
enabled. Existing zero-byte files, valid empty SQLite databases, main-file absence with sidecars,
missing or malformed markers, and opposite roles fail closed. Existing markers are inspected
read-only and revalidated under the normal SQLite write lock before WAL, schema changes, migrations,
room initialization, maintenance mutation, or endpoint mapping.

All ChairSide data before the approved go-live date is training, testing, demonstration, or
stress-fixture data. The current beta database may be archived separately, but it will not be
reused as formal Production. There is no legacy deployed-database adoption command and no automatic
adoption behavior. Formal Production begins with a genuinely new canonical database and an empty
reporting history; the approved go-live date begins official reporting history. Existing unmarked
deployed databases are refused and must not be reused as Production.

This implementation does not archive or clear the beta database, deploy or initialize Production,
or complete Training IIS deployment.

## Maintenance posture

`reset-training-data`, `reset-empty`, `reset-large-synthetic-report-data`, and
`reset-stress-fixture` retain their existing confirmation and argument validation. They are
allowlisted in Development and Training and refused in Production before application build or
repository construction. Unknown commands default to denied.

Authorized Training maintenance resolves the repository and validates the matching Training marker
before any reset or seed mutation. Existing row-level reset methods preserve the marker exactly.
Tests characterize this invariant for all four reset entry points: Training data, clean, large
synthetic reporting data, and stress fixture.

`Reset-ChairSideTrainingData.ps1` is a Training-only, backup-first operator wrapper. It code-owns the
Training application, database, backup, app-pool, and child-environment values; accepts no deployment
overrides; validates the published Training configuration before stopping IIS; and never passes a
database-path override to the application. Its routine modes map to the existing clean, training,
full-stress, and reporting-volume maintenance shapes. Standard PowerShell `-WhatIf` prints the
complete plan without IIS, filesystem, or application side effects. Real execution accepts only an
explicit Started or Stopped Training app-pool state, stops and finally restarts only a pool that this
invocation confirmed it transitioned from Started to Stopped, and preserves an already Stopped pool.

## Source and test anchors

- `src/ChairSide.Board/Services/DeploymentEnvironmentPolicy.cs`
- `src/ChairSide.Board/Services/DatabaseIsolationLayout.cs`
- `src/ChairSide.Board/Services/DatabaseIsolationPolicy.cs`
- `src/ChairSide.Board/Services/DatabaseDeploymentIdentityPolicy.cs`
- `src/ChairSide.Board/Services/IReparsePointInspector.cs`
- `src/ChairSide.Board/Services/MaintenanceCommands.cs`
- `src/ChairSide.Board/Program.cs`
- `src/ChairSide.Board/Services/DemoBoardStore.cs`
- `src/ChairSide.Board/wwwroot/board.js`
- `scripts/Reset-ChairSideTrainingData.ps1`
- `tests/scripts/Reset-ChairSideTrainingData.Tests.ps1`
- `tests/ChairSide.Board.Tests/EnvironmentMaintenancePreflightTests.cs`
- `tests/ChairSide.Board.Tests/DatabaseIsolationPolicyTests.cs`
- `tests/ChairSide.Board.Tests/DatabaseDeploymentIdentityPolicyTests.cs`
- `tests/ChairSide.Board.Tests/DatabaseDeploymentIdentityProcessTests.cs`
