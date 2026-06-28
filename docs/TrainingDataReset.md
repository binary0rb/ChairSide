# ChairSide Training Data Reset Runbook

Operator-only maintenance for resetting ChairSide data. **Destructive, but backup-first.** There is
**no** staff-facing reset button and **no** production web endpoint that wipes data. All resets run
through an in-app maintenance CLI invoked by a PowerShell wrapper that the operator runs deliberately
on the server.

> Do **not** run the production VM as `Development`. The maintenance CLI runs under `Production` so it
> targets the real database path; it does not start the web server.

## Data lifecycle

1. **Alpha DB** — current click-through / demo / training-session data. Disposable, but **archive
   before removal**.
2. **Training fixture DB** — clean, deterministic synthetic data for staff/doctor training and
   reporting walkthroughs. Produced by the `TrainingSeed` reset.
3. **Official beta DB** — a fresh, empty database used when ChairSide becomes the real beta source of
   truth. Produced by the `EmptyBeta` reset.

## What the two modes do

| Mode | CLI command | Confirmation token | Clears completed cycles | Resets active rooms | Seeds synthetic data |
|---|---|---|---|---|---|
| `TrainingSeed` | `reset-training-data` | `RESET_TRAINING_DATA` | Yes | Yes (all rooms → Available) | **Yes** (clean, non-PHI, deterministic) |
| `EmptyBeta` | `reset-empty` | `RESET_EMPTY_BETA` | Yes | Yes (all rooms → Available) | **No** (empty board) |

Both modes operate through the app's own repository logic (WAL-consistent SQLite), never raw SQL file
edits.

## What gets backed up

Before any mutation the script stops the IIS app pool and copies a timestamped set under
`C:\ChairSide\Backups\chairside-pre-<Mode>-<timestamp>\`:

- `chairside.db`
- `chairside.db-wal` (if present)
- `chairside.db-shm` (if present)

The app is stopped first so the WAL sidecars are stable for the file-set copy.

## What gets cleared / seeded

- **Cleared:** every row in `completed_room_cycles`; every `active_rooms` row is reset so all
  configured rooms are `Available` with no lifecycle or allocation residue.
- **Seeded (TrainingSeed only):** ~48 deterministic completed cycles across recent weekdays, all four
  doctors, seven procedure families, sedation only as a `+SED` modifier, expected-allocation snapshots
  on every cycle, and a realistic over/under/at-expected variance spread. **No PHI. No standalone
  Sedation procedure. Zero reporting exceptions.**

## Prerequisites

- Published app at `C:\ChairSide\App` (contains `ChairSide.Board.dll`).
- .NET 8 runtime on the server (already required by the IIS-hosted app).
- IIS app pool `ChairSideBoard`.
- Permission to stop/start the app pool and write to the data/backup folders.

## Usage

Run from an elevated PowerShell on the server, from the repo `scripts` folder (or copy the script to
the server).

### Training fixture reset (seed synthetic data)

```powershell
.\Reset-ChairSideTrainingData.ps1 -Mode TrainingSeed -Confirm RESET_TRAINING_DATA
```

### Official beta reset (empty board)

```powershell
.\Reset-ChairSideTrainingData.ps1 -Mode EmptyBeta -Confirm RESET_EMPTY_BETA
```

### Overriding defaults

```powershell
.\Reset-ChairSideTrainingData.ps1 `
    -Mode TrainingSeed -Confirm RESET_TRAINING_DATA `
    -AppPath "C:\ChairSide\App" `
    -DatabasePath "C:\ChairSide\Data\chairside.db" `
    -BackupRoot "C:\ChairSide\Backups" `
    -AppPoolName "ChairSideBoard"
```

The script prints the resolved paths, mode, and CLI command before acting. A wrong or missing
`-Confirm` token aborts **before** stopping the app or touching data.

### Direct CLI (advanced / already-stopped app)

The PowerShell wrapper is preferred (it handles stop/backup/start). The underlying CLI, if invoked
manually after stopping the app pool and taking a backup:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"
dotnet C:\ChairSide\App\ChairSide.Board.dll --maintenance reset-training-data --confirm RESET_TRAINING_DATA
dotnet C:\ChairSide\App\ChairSide.Board.dll --maintenance reset-empty       --confirm RESET_EMPTY_BETA
```

An unknown command, missing token, or wrong token prints a refusal, changes nothing, and exits
non-zero. The maintenance CLI never starts the web server.

## Verifying after a TrainingSeed reset

1. Confirm the app pool is running and browse `http://chairside/reports.html`.
2. **Data Quality** card: `0 excluded`, `0 reporting exceptions`.
3. **Allocation Balance**: many calculable cases.
4. **Doctor Allocation Balance**: synthetic doctor examples load with varied, non-identical operational patterns.
5. **Procedure Family Allocation Balance**: multiple families.
6. Variance examples show over / under / at-expected cases without ranking doctors or staff.
7. No standalone Sedation procedure and no "sedation time" metric.

## Verifying after an EmptyBeta reset

1. Browse `http://chairside/reports.html`: no completed cycles; empty-state messaging.
2. Browse `http://chairside/master.html`: all rooms Available.

## Suggested operator sequence

1. Archive current alpha DB: `.\Backup-ChairSideSqlite.ps1` (or the pre-reset backup created by this
   script).
2. `TrainingSeed` reset → start app → verify reports → run training.
3. After training, run `EmptyBeta` reset (this also backs up the training DB first).
4. Start app → confirm empty board → ChairSide is the beta source of truth.

## Safety notes

- The reset is **destructive** but always takes a timestamped backup first.
- Never run the production VM as `Development`.
- `TrainingSeed` and `EmptyBeta` use **different** confirmation tokens so seeding can never happen
  during a go-live clean.
- Backups contain non-PHI operational data only; still protect them per clinic policy.
