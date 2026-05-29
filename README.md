# ChairSide Board

ChairSide Board is an internal surgical room status and doctor dispatch dashboard for an oral surgery practice.

It tracks rooms, assigned doctors, procedure categories, timers, and seated-to-doctor operational timing. It does not track patients and does not store, request, display, import, or infer PHI.

## Scaffold

- ASP.NET Core 8 minimal web app
- SignalR hub for live board updates
- Shared in-memory state for the configured room count
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

Completed room cycles are tracked in memory for reporting with:

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

Thresholds are configured in `src/ChairSide.Board/appsettings.json`:

```json
{
  "BoardThresholdOptions": {
    "AgingMinutes": 7,
    "StaleMinutes": 12
  },
  "BoardOptions": {
    "RoomCount": 12
  }
}
```

Change those values to adjust room timing without code edits. `StaleMinutes` must be greater than `AgingMinutes`; the app validates this at startup.

`RoomCount` controls how many room states are created in memory. The default is 12, so Room 1 through Room 12 are active. Change `BoardOptions:RoomCount` and restart the app to use a different room count.

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

Room IDs above the configured `RoomCount` show a friendly invalid room message and do not update board state.

Compatibility URLs:

- Root master board: `http://localhost:5000/`
- Room 1 panel: `http://localhost:5000/room-1.html`
- Older room query format: `http://localhost:5000/room.html?room=1`

## Reset Demo Data

The current scaffold uses in-memory seed data. Restart the app to reset the demo board.

## Reports

The reports page shows completed room cycle count, seated-to-doctor average and median, doctor-in-room average and median, turnover average and median, aging event count, stale event count, and recent completed cycles. Data is in memory for now and resets when the app restarts.

The underlying completed-cycle records include assigned doctor and completion timestamps so monthly doctor-level reporting can be added later without introducing PHI.

## Demo Aging

Room panels include a `Demo Timer` select. Use `Start now`, `Simulate aging wait`, or `Simulate stale wait` before seating a room to test the aging and stale board states without waiting for the configured thresholds.

## Demo Script

1. Open `http://localhost:5000/master.html` and `http://localhost:5000/room.html?roomId=1`.
2. On the Room 1 panel, choose `Start now` and click `Seat Room`. Expected result: Room 1 appears on the master board as a solid doctor-color card with a seated timer.
3. Click `Doctor Arrived`, `Doctor Complete`, then `Room Available` to reset Room 1 for the next demo state.
4. Choose `Simulate aging wait` and click `Seat Room`. Expected result: Room 1 appears as a solid doctor-color card with a slow pulsing yellow border.
5. Click `Doctor Arrived`, `Doctor Complete`, then `Room Available` to reset Room 1 again.
6. Choose `Simulate stale wait` and click `Seat Room`. Expected result: Room 1 appears as a solid doctor-color card with a faster pulsing red border.
7. Click `Doctor Arrived`. Expected result: Room 1 changes to stable doctor color with the `IN ROOM` badge, and seated-to-doctor metrics are recorded.
8. Click `Doctor Complete`. Expected result: Room 1 changes to the neutral diagonal stripe turnover card with the `TURNOVER` badge.
9. Click `Room Available`. Expected result: Room 1 returns to the gray available state.
10. Open `http://localhost:5000/reports.html`. Expected result: reports show non-PHI room-flow metrics and completed Room 1 cycles.

## Future Database

The intended early persistence target is SQLite or SQL Server Express. Keep persisted fields limited to room number, doctor assignment, procedure category, room state, event timestamps, device identity, and non-PHI operational metrics.
