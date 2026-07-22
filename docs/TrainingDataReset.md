# ChairSide Training Data Reset Runbook

`Reset-ChairSideTrainingData.ps1` is the routine operator entry point for returning the separate
ChairSide Training deployment to a known, non-PHI sandbox state. It cannot target caller-supplied
paths, app pools, or environments, and it does not expose a web reset endpoint or UI reset button.

The reset is destructive and backup-first. Run it only for a deliberate Training session.

## Code-owned Training deployment

The wrapper always uses:

- Application: `C:\ChairSide\Training\App`
- Application DLL: `C:\ChairSide\Training\App\ChairSide.Board.dll`
- Configuration: `C:\ChairSide\Training\App\appsettings.Training.json`
- Database: `C:\ChairSide\Training\Data\chairside-training.db`
- Backups: `C:\ChairSide\Training\Backups`
- IIS app pool: `ChairSideBoard-Training`
- Child environment: `Training`

These values are not command-line parameters. The wrapper does not pass a database-path override to
the application. `appsettings.Training.json` and ChairSide's code-owned database isolation policy
must independently resolve the canonical Training database.

## Verified Training deployment

The separate Training environment was operationally verified on 2026-07-22 with:

- Internal URL: `http://chairside-training.aospeoria.local`
- IIS site and app pool: `ChairSideBoard-Training`
- Application root: `C:\ChairSide\Training\App`
- Database: `C:\ChairSide\Training\Data\chairside-training.db`
- Logs: `C:\ChairSide\Training\Logs`
- Backups: `C:\ChairSide\Training\Backups`
- Environment: `ASPNETCORE_ENVIRONMENT=Training`
- Deployed application build: `16c41d5`

After the final `TrainingSeed` reset, the verified baseline was 12 Available rooms, 4 active doctors,
114 synthetic completed cycles, 7 procedure families, 114 expected-allocation cases, and 0 reporting
exceptions. That state survived an independent app-pool restart and was then deliberately returned to
the same baseline. Production was not mutated during Training deployment or reset verification.

## Prerequisites

The separate Training deployment must already exist. Before running a real reset, confirm:

- The published app and `appsettings.Training.json` exist under `C:\ChairSide\Training\App`.
- The Training app pool is named `ChairSideBoard-Training`.
- The operator can stop/start that pool and write to the Training data and backup folders.
- The .NET 8 runtime is installed.
- The Training URL and deployment configuration have already been established operationally.

`-WhatIf` does not require the deployment to exist; it resolves and prints the complete code-owned
plan without touching IIS, the filesystem, or the application.

## Approved modes

| Mode | Maintenance command | Required `-ConfirmationToken` | Result |
|---|---|---|---|
| `Clean` | `reset-empty` | `RESET_EMPTY_BETA` | All rooms Available; no completed history |
| `TrainingSeed` | `reset-training-data` | `RESET_TRAINING_DATA` | All rooms Available plus the clean deterministic training history |
| `FullStress` | `reset-stress-fixture --profile full-stress` | `RESET_STRESS_FIXTURE` | Live board states plus scenario-rich report history |
| `ReportingVolume` | `reset-stress-fixture --profile reporting-volume` | `RESET_STRESS_FIXTURE` | Large clean completed-cycle history |

`ReportingVolume` alone accepts `-CompletedCycles`, from 100 through 10000. When omitted, the
application uses its existing default of 1000. Every other mode refuses that parameter.

`-ConfirmationToken` is the ChairSide safety token. PowerShell reserves `-Confirm` as a common
`ShouldProcess` switch, so it is not the token parameter.

## Previewing a reset

Preview each plan before its first operational use:

```powershell
.\scripts\Reset-ChairSideTrainingData.ps1 `
    -Mode Clean `
    -ConfirmationToken RESET_EMPTY_BETA `
    -WhatIf

.\scripts\Reset-ChairSideTrainingData.ps1 `
    -Mode FullStress `
    -ConfirmationToken RESET_STRESS_FIXTURE `
    -WhatIf

.\scripts\Reset-ChairSideTrainingData.ps1 `
    -Mode ReportingVolume `
    -ConfirmationToken RESET_STRESS_FIXTURE `
    -CompletedCycles 500 `
    -WhatIf
```

The preview prints the exact app, configuration, data, database, backup, app-pool, environment, and
maintenance CLI values. It performs no deployment existence check and no side effect.

## Running a reset

Run from an elevated PowerShell on the Training server:

```powershell
.\scripts\Reset-ChairSideTrainingData.ps1 `
    -Mode TrainingSeed `
    -ConfirmationToken RESET_TRAINING_DATA
```

For a scenario-rich demonstration:

```powershell
.\scripts\Reset-ChairSideTrainingData.ps1 `
    -Mode FullStress `
    -ConfirmationToken RESET_STRESS_FIXTURE
```

Before stopping IIS, the wrapper validates the mode, token, completed-cycle usage, every code-owned
Training value, the published DLL, and the Training configuration's database and log paths. It then:

1. Reads the exact initial `ChairSideBoard-Training` state. A Started pool receives a stop request and
   is polled every 250 milliseconds for up to 30 seconds until it reaches Stopped; an already Stopped
   pool remains stopped. Every other initial state is refused before backup or maintenance.
2. Copies each present SQLite main, WAL, and SHM file to a timestamped directory under
   `C:\ChairSide\Training\Backups`.
3. Runs the published DLL from the Training app directory with `ASPNETCORE_ENVIRONMENT=Training`.
4. Restores the parent PowerShell process's previous environment value.
5. Restores the Training app pool in a `finally` block only when its initial state was Started. The
   restoration path accepts Started, Starting, Stopped, or Stopping: it waits for an in-progress
   transition when necessary, starts a stopped pool, and polls for exact Started state.

Every app-pool wait is bounded to 30 seconds and reports the last observed state on timeout. A backup,
maintenance, wait, or restoration failure returns a nonzero exit. When the initial pool state was
Started, restoration is still attempted after backup or maintenance failure. Operation and restoration
failures are preserved separately; if both occur, the final error reports both causes.

## Database safety

The maintenance CLI validates the persisted `Training` deployment marker during repository
construction, before any reset or fixture mutation. A missing, malformed, or wrong-role marker is a
controlled refusal. All current reset implementations preserve the complete marker row unchanged.

Every destructive maintenance command remains refused in Production before application build or
repository construction. Do not work around this policy by changing an environment name or by
copying a marked database between deployments.

## Verification after reset

After any successful reset:

1. Confirm `ChairSideBoard-Training` is running.
2. Open the separate Training URL and confirm the persistent `TRAINING` badge is visible.
3. Confirm the master board and reports match the selected mode.
4. Complete one intended teaching or demonstration workflow.
5. Confirm no patient-identifying fields are requested or displayed.

Mode-specific expectations:

- `Clean`: every room is Available and Reports shows no completed cycles.
- `TrainingSeed`: rooms are Available and clean allocation/report examples are present.
- `FullStress`: the live board contains representative active states and Reports contains bounded
  scenario history.
- `ReportingVolume`: rooms are Available and Reports contains the requested completed-cycle volume.

## Engineering-only profiles

The underlying `reset-stress-fixture` command continues to support `live-board-stress`,
`doctor-view-stress`, `doctor-view-overflow-stress`, `scenario-rich`, and `all-scenarios` in addition
to the two routine wrapper profiles. They remain available for deliberate engineering validation
through the already-stopped, already-backed-up Training maintenance CLI. They are not exposed by the
routine operator wrapper, and no new fixture profile is introduced by this workflow.

Backups contain non-PHI operational room, doctor, procedure, and timing data. Protect them according
to clinic policy.
