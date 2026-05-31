# ChairSide Board

ChairSide Board is an internal surgical room status and doctor dispatch dashboard for an oral surgery practice.

It tracks rooms, assigned doctors, procedure categories, timers, and seated-to-doctor operational timing. It does not track patients and does not store, request, display, import, or infer PHI.

## Scaffold

- ASP.NET Core 8 minimal web app
- SignalR hub for live board updates
- SQLite-backed active room state for the configured room count
- Static browser UI with master board, room panel, doctor read-only view, and basic reports
- No patient fields, free-text notes, dates of birth, chart numbers, diagnoses, insurance, or billing data

Each room tracks only non-PHI operational state:

- `roomId`
- `assignedDoctor`
- `procedureCode`
- `state`
- `seatedAt`
- `agingStartedAt`
- `staleStartedAt`
- `doctorArrivedAt`
- `doctorCompleteAt`
- `roomAvailableAt`

Completed room cycles are persisted for reporting with:

- `roomId`
- `assignedDoctor`
- `procedureCode`
- `seatedAt`
- `doctorArrivedAt`
- `doctorCompleteAt`
- `roomAvailableAt`
- `seatedToDoctorSeconds`
- `doctorInRoomSeconds`
- `turnoverSeconds`
- `totalRoomCycleSeconds`
- `finalWaitState`
- `agingThresholdReached`
- `staleThresholdReached`

Default server-side thresholds:

- Aging starts 7 minutes after `seatedAt`
- Stale starts 12 minutes after `seatedAt`

The room lifecycle is:

- Available
- Seated, Aging, or Stale
- Doctor In Room
- Turnover
- Available

`Seat Room` starts the seated-to-doctor timer and records only doctor, procedure, and operational timestamps. `Doctor Arrived` is available only for seated, aging, or stale rooms; it records `doctorArrivedAt`, calculates `seatedToDoctorSeconds`, and moves the room to Doctor In Room. `Doctor Complete` is available only while the doctor is in the room; it records `doctorCompleteAt` and starts Turnover. `Room Available` is available only during Turnover; it records `roomAvailableAt`, calculates turnover and total cycle durations, and resets the active room card to available.

Before `Doctor Arrived`, staff can safely correct common seating mistakes:

- `Update Assignment` changes the assigned doctor and procedure while preserving the original `seatedAt` timestamp.
- `Cancel Seating` requires confirmation, resets the room to available, and does not create a report entry.

After `Doctor Arrived`, correction actions are blocked. Seated-to-doctor reporting is recorded only at `Doctor Arrived`.

The server calculates the seated wait state from `seatedAt`, `agingStartedAt`, `staleStartedAt`, and the configured thresholds:

- Available when there is no active `seatedAt`
- Seated before the configured aging threshold
- Aging from the configured aging threshold until the stale threshold
- Stale after the configured stale threshold
- Doctor In Room after `doctorArrivedAt`
- Turnover after `doctorCompleteAt`

Visual state rules:

- Available = gray
- Seated = solid doctor color
- Aging = yellow pulsing border
- Stale = red pulsing border
- Doctor In Room = stable doctor color with IN ROOM badge
- Turnover = diagonal gray/black stripe

The room panel is touch-first for tablet use. Doctor and procedure choices render as large selectable tiles with configured doctor names/colors, procedure icons, procedure codes, and procedure labels. Lifecycle buttons are large touch targets and remain keyboard/mouse compatible.

Procedure icons are high-contrast inline SVGs used consistently across legends, room cards, room panels, and reports:

- `CON` = Consult, clipboard/document
- `EXT` = Extraction, bold forceps/pliers
- `SED` = Sedation, crescent moon
- `POST` = Post-op, checkmark document
- `IMP` = Implant, screw/post
- `BX` = Biopsy, vial/sample

Full doctor names remain visible in the doctor legend, room panel tiles, reports, and configuration-facing context. Master board and doctor-view room cards use compact last names without the `Dr.` prefix, such as `Pledger` and `Schroeder`, to keep room cards readable.

Thresholds are configured in `src/ChairSide.Board/appsettings.json`:

```json
{
  "BoardThresholdOptions": {
    "AgingMinutes": 7,
    "StaleMinutes": 12
  },
  "BoardOptions": {
    "RoomCount": 12
  },
  "BoardPersistenceOptions": {
    "DatabasePath": "./data/chairside-dev.db"
  },
  "RoomDeviceBindingOptions": {
    "Enabled": false,
    "RoomTokens": {
      "1": "dev-room-1-token",
      "2": "dev-room-2-token"
    }
  }
}
```

Change those values to adjust room timing without code edits. `StaleMinutes` must be greater than `AgingMinutes`; the app validates this at startup.

`RoomCount` controls how many room states are configured. The default is 12, so Room 1 through Room 12 are active. Change `BoardOptions:RoomCount` and restart the app to use a different room count.

`BoardPersistenceOptions:DatabasePath` controls the SQLite database location. The default development path is local to the app project, `./data/chairside-dev.db`. For staging or production, set this to an operational data location such as `C:\ChairSide\Data\chairside.db` through environment-specific configuration or command-line configuration:

```powershell
dotnet run --project .\src\ChairSide.Board\ChairSide.Board.csproj --BoardPersistenceOptions:DatabasePath="C:\ChairSide\Data\chairside.db"
```

The app creates the SQLite database and schema on startup if they do not exist. Persisted data remains non-PHI and is limited to room assignments, procedure categories, lifecycle state, operational timestamps, and completed-cycle durations.

Room-device binding is a first-pass operational control for room tablets. When `RoomDeviceBindingOptions:Enabled` is `true`, room-local mutation actions require the configured token for that room:

- Seat Room
- Update Assignment
- Cancel Seating
- Doctor Arrived
- Doctor Complete
- Room Available

Read-only views and APIs remain open for the internal board: `/master.html`, `/doctor.html`, `/reports.html`, and board state reads do not require room tokens. Room mutation API calls must send the token in the `X-ChairSide-Room-Token` header. Do not place room tokens in URLs. URLs may be logged by IIS, browsers, proxies, network tools, and browser history.

Production tokens must not use the `dev-room-*-token` sample values. Supply production tokens through environment-specific configuration or deployment-time configuration, and do not commit real room tokens as source-controlled secrets.

Room tokens are operational controls, not full authentication. They do not protect reports/admin surfaces, do not replace HTTPS, and do not provide user-level access control. Full access control, report protection, CSRF protection, and stronger room-device binding remain future hardening work before broader rollout.

Production database guidance:

- Recommended path: `C:\ChairSide\Data\chairside.db`
- Do not store the production database under the deployed app/content root.
- The IIS app pool identity needs Modify permission on `C:\ChairSide\Data`.
- SQLite WAL mode creates `chairside.db-wal` and `chairside.db-shm` beside the database, so the directory must be writable, not just the database file.
- In Production, the app fails fast if the configured database path resolves inside the app content root or if the database directory cannot be created/written by the running process.

## Seed Data

Doctors:

- Dr. Otte = blue
- Dr. Pledger = green
- Dr. Gibson = orange
- Dr. Schroeder = purple

Rooms:

- Room 1 through the configured `RoomCount`
- Mixed demo room states using fake seated timers
- Procedure categories: `CON`, `EXT`, `SED`, `POST`, `IMP`, `BX`

## Run Locally

Install the .NET 8 SDK, then run:

```powershell
dotnet restore
dotnet build
dotnet run --project .\src\ChairSide.Board\ChairSide.Board.csproj
```

If `ChairSide.Board.exe` is locked by a running local app during a normal build, stop the running .NET process and build again:

```powershell
Stop-Process -Name dotnet
dotnet build
```

Open:

- Master board: `http://localhost:5000/master.html`
- Doctor read-only view: `http://localhost:5000/doctor.html`
- Reports: `http://localhost:5000/reports.html`
- Room panels: `http://localhost:5000/room.html?roomId=1` through `http://localhost:5000/room.html?roomId={RoomCount}`

Use `/room.html?roomId={roomNumber}` as the authoritative room panel URL. Room IDs above the configured `RoomCount` show a friendly invalid room message and do not update board state.

Compatibility and legacy URLs:

- Root master board: `http://localhost:5000/`
- Legacy Room 1 panel: `http://localhost:5000/room-1.html`
- Legacy room query format: `http://localhost:5000/room.html?room=1`

## IIS Deployment

ChairSide Board is intended for internal-only deployment on the practice network. Do not expose it directly to the public internet, and do not configure public DNS for the app. A typical internal URL is `http://chairside`, backed by internal DNS or a local network alias.

Recommended server folder layout:

```text
C:\ChairSide\App
C:\ChairSide\Data
C:\ChairSide\Logs
```

`C:\ChairSide\App` contains the published ASP.NET Core app. `C:\ChairSide\Data` contains the SQLite database and WAL files. `C:\ChairSide\Logs` is optional for IIS/stdout or operational logs if enabled later.

Publish from the repository root:

```powershell
dotnet publish .\src\ChairSide.Board\ChairSide.Board.csproj -c Release -o .\publish
```

Copy the contents of `.\publish` to `C:\ChairSide\App`. Publishing to `.\publish` is a local staging convention; do not commit the publish output.

Production configuration is supplied by `src/ChairSide.Board/appsettings.Production.json`:

```json
{
  "BoardPersistenceOptions": {
    "DatabasePath": "C:\\ChairSide\\Data\\chairside.db"
  }
}
```

The app validates this path at startup in Production. It must resolve outside the deployed app/content root. The app creates the SQLite database and schema if missing, but the data directory must be writable by the IIS app pool identity.

`appsettings.Production.json` is environment-specific. If the production server does not use `C:\ChairSide\Data`, update `BoardPersistenceOptions:DatabasePath` before deployment.

IIS app pool guidance:

- Create a dedicated app pool, for example `ChairSideBoard`.
- Set `.NET CLR version` to `No Managed Code`.
- Set `Start Mode` to `AlwaysRunning`.
- Set idle timeout to disabled, such as `0` minutes.
- Use a single worker process: `Maximum Worker Processes = 1`.
- Avoid web garden mode. SQLite persistence is designed for a single app instance.
- Set `Disallow Overlapping Rotation` to `true` (`disallowOverlappingRotation = true`). This is required for the SQLite single-instance deployment because it prevents two IIS worker processes from briefly writing to the same SQLite database during recycle.

NTFS permissions:

- Grant `IIS AppPool\ChairSideBoard` Modify permission on `C:\ChairSide\Data`.
- SQLite WAL mode writes `chairside.db-wal` and `chairside.db-shm` beside `chairside.db`, so directory write access is required, not only file write access.
- Keep the production database out of `C:\ChairSide\App`.

Internal network access:

- Use internal DNS such as `http://chairside`.
- Bind IIS to the internal site/host name only.
- If Windows Firewall blocks inbound HTTP, add an internal HTTP/80 rule as needed.
- Keep access limited to trusted internal clinical workstations/tablets and wall displays.

Security posture and accepted pilot risks:

- ChairSide Board is internal-only.
- Do not enter PHI. The system tracks rooms, doctors, procedures, and operational timestamps only.
- HTTP/plaintext LAN traffic is an accepted pilot posture unless HTTPS is configured later.
- Authentication, CSRF protection, and room-device binding are not currently enforced.
- Reports/admin surfaces and room-local actions should be protected before broader clinic rollout.

First-boot troubleshooting:

- If the IIS site fails to start, check Windows Event Viewer first.
- If more detail is needed, temporarily enable ASP.NET Core stdout logging and point logs to `C:\ChairSide\Logs`.
- Disable stdout logging after troubleshooting.

Deployment smoke checklist:

1. Browse `http://chairside/master.html`.
2. Browse `http://chairside/room.html?roomId=1`.
3. Browse `http://chairside/doctor.html`.
4. Browse `http://chairside/reports.html`.
5. Seat Room 1 from the room panel.
6. Restart the IIS site or `ChairSideBoard` app pool.
7. Confirm Room 1 is still seated after restart.
8. Mark Doctor Arrived, Doctor Complete, and Room Available.
9. Confirm `/reports.html` shows the completed room cycle.
10. Restart the IIS site or app pool again.
11. Confirm the report remains after restart.
12. Confirm no patient-identifying fields are requested or displayed.

## Reset Demo Data

The development database is stored at `src/ChairSide.Board/data/chairside-dev.db` by default. Stop the app and delete that file, plus any matching `chairside-dev.db-wal` or `chairside-dev.db-shm` files, to reset local demo data.

Demo room states are only seeded when a non-Production database is brand new. Production starts with configured rooms in the available state and does not seed demo room activity.

## Reports

The reports page shows completed room cycle count, seated-to-doctor average and median, doctor-in-room average and median, turnover average and median, aging event count, stale event count, and recent completed cycles. Active room state and completed cycles are stored in SQLite and survive app restarts.

The underlying completed-cycle records include assigned doctor and completion timestamps so monthly doctor-level reporting can be added later without introducing PHI.

## Demo Aging

Room panels include a `Demo Timer` select. Use `Start now`, `Simulate aging wait`, or `Simulate stale wait` before seating a room to test the aging and stale board states without waiting for the configured thresholds.

## Demo Script

1. Open `http://localhost:5000/master.html` and `http://localhost:5000/room.html?roomId=1`.
2. On the Room 1 panel, choose `Start now` and click `Seat Room`. Expected result: Room 1 appears on the master board as a solid doctor-color card with a seated timer.
3. Change the selected doctor or procedure and click `Update Assignment`. Expected result: Room 1 keeps the same seated timer but updates doctor/procedure on the master board.
4. Click `Cancel Seating` and confirm. Expected result: Room 1 returns to available without creating a report entry.
5. Choose `Simulate aging wait` and click `Seat Room`. Expected result: Room 1 appears as a solid doctor-color card with a slow pulsing yellow border.
6. Click `Doctor Arrived`, `Doctor Complete`, then `Room Available` to reset Room 1 again.
7. Choose `Simulate stale wait` and click `Seat Room`. Expected result: Room 1 appears as a solid doctor-color card with a faster pulsing red border.
8. Click `Doctor Arrived`. Expected result: Room 1 changes to stable doctor color with the `IN ROOM` badge, and seated-to-doctor metrics are recorded.
9. Click `Doctor Complete`. Expected result: Room 1 changes to the neutral diagonal stripe turnover card with the `TURNOVER` badge.
10. Click `Room Available`. Expected result: Room 1 returns to the gray available state.
11. Open `http://localhost:5000/reports.html`. Expected result: reports show non-PHI room-flow metrics and completed Room 1 cycles.

## Persistence

SQLite persistence is intentionally narrow for the MVP. It stores only room number, doctor id/display name, procedure code/category, room state, lifecycle timestamps, completed-cycle durations, and operational audit timestamps. It does not store patient names, DOBs, chart numbers, medical notes, diagnosis, insurance, billing data, or free-text patient notes.
