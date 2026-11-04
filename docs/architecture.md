# ChairSide architecture and room workflow

These [Mermaid](https://mermaid.js.org/) diagrams provide a compact map of the **current implementation**. GitHub renders the `mermaid` code blocks directly; no packages, build steps, or running services are required.

ChairSide tracks operational room state only; it does not track patients or PHI. The diagrams summarize normal behavior, not every validation, recovery, concurrency, or administrative path. Source code and tests remain authoritative.

## 1. Application architecture

```mermaid
flowchart LR
    subgraph Browsers["Browser views"]
        Master["Master board"]
        Room["Room panel"]
        Doctor["Doctor view (read-only)"]
        Reports["Reports"]
    end

    subgraph Server["ASP.NET Core 8 server"]
        Static["Static HTML / CSS / JS"]
        API["Minimal HTTP APIs"]
        Store["DemoBoardStore"]
        Hub["SignalR BoardHub"]
    end

    DB[("SQLite: room state, Ready handoffs, cycle history")]

    Static -->|Serves views| Master
    Static -->|Serves views| Room
    Static -->|Serves views| Doctor
    Static -->|Serves views| Reports
    Master -->|GET /api/board| API
    Doctor -->|GET /api/board| API
    Room -->|GET/POST/PUT room APIs| API
    Reports -->|GET /api/reports| API
    API -->|Snapshots and mutations| Store
    Store <-->|Read / write| DB
    API -->|After successful mutations: boardUpdated| Hub
    Hub -.->|Live board snapshots| Master
    Hub -.->|Live board snapshots| Room
    Hub -.->|Live board snapshots| Doctor
```

The browsers share server-owned operational state. `Program.cs` maps `/api/board`, `/api/rooms/{roomNumber}`, room lifecycle routes, reports, and the SignalR hub at `/boardHub`. `DemoBoardStore` applies business rules; `SqliteBoardRepository` persists room state and history. `BoardHub` is used to distribute `boardUpdated` snapshots to connected clients after successful mutations. The diagram omits supporting services such as audit logging, expiration, and access controls for clarity.

## 2. Canonical room lifecycle

```mermaid
stateDiagram-v2
    direction TB
    state "Available" as Available
    state "Prestaging" as Prestaging
    state "In Prep (Seated)" as Seated
    state "Ready for Doctor" as Ready
    state "Doctor In Room" as DoctorInRoom
    state "Turnover" as Turnover

    [*] --> Available
    Available --> Prestaging: Begin Prestage
    Prestaging --> Seated: Seat Room
    Prestaging --> Available: Cancel Prestage
    Seated --> Ready: Ready for Doctor
    Seated --> Available: Cancel Seating
    Ready --> Seated: Withdraw Ready
    Ready --> Available: Cancel Seating
    Ready --> DoctorInRoom: Doctor Arrived
    DoctorInRoom --> Turnover: Doctor Complete
    Turnover --> Available: Room Available

    note right of Ready
      Requires a complete, valid assignment
      and creates an active Ready handoff.
      Urgency is derived from handoff ReadyAt:
      normal until 7 min; Aging at 7 min;
      Stale at 12 min.
    end note
```

**Important distinctions:**

- **Prestaging** starts an episode before an assignment is necessarily complete. **Seat Room** records the actual seated time and advances to the internal `Seated` state, displayed as **In Prep**.
- **Save Details** can correct assignments while Prestaging or Seated without advancing the lifecycle. The **Ready for Doctor** action requires a complete, valid assignment; the room's dispatch-related details then lock. Add-on remains correctable until doctor arrival.
- **Aging** and **Stale** are urgency projections of the current active Ready handoff, **not additional required steps** in the canonical state machine. The defaults are 7 and 12 minutes from the handoff's Ready time (`BoardThresholdOptions` in `appsettings.json`).
- **Withdraw Ready** returns to Seated/In Prep and records the handoff as withdrawn. Reissuing Ready creates a new handoff and restarts urgency. **Cancel Seating** is allowed from Seated or Ready before the doctor arrives; cancelled episodes do not create completed-cycle report entries.
- **Doctor Arrived** accepts the Ready handoff and records seated-to-doctor timing. **Doctor Complete** enters Turnover. **Room Available** ends the cycle, finalizes turnover timing, and resets the room.

## 3. Example event sequence: Ready for Doctor

This illustrates the normal successful path, **not** every possible error or authorization response.

```mermaid
sequenceDiagram
    autonumber
    actor Staff as Staff member
    participant Room as Room browser
    participant API as Room lifecycle HTTP handler
    participant Store as DemoBoardStore
    participant DB as SQLite repository
    participant Hub as SignalR BoardHub
    participant Other as Connected boards

    Staff->>Room: Choose Ready for Doctor
    Room->>API: POST /api/rooms/{roomNumber}/ready-for-doctor
    API->>API: Validate request and room access
    API->>Store: MarkReadyForDoctorCanonical
    Store->>Store: Check lifecycle and assignment
    Store->>DB: Guarded Ready handoff + room write
    DB-->>Store: Committed state
    Store-->>API: Successful mutation
    API->>Hub: Send boardUpdated(snapshot)
    Hub-->>Other: Broadcast updated board snapshot
    Hub-->>Room: Broadcast updated board snapshot
    API-->>Room: Return successful HTTP result
```

Actual ordering of a client's rendering versus its HTTP response can vary. The server also records an audit entry; this diagram emphasizes the state/persistence/broadcast path.

## Where to verify or update these diagrams

- `src/ChairSide.Board/Program.cs`: HTTP routes, endpoint handlers, SignalR publication.
- `src/ChairSide.Board/Services/DemoBoardStore.cs`: lifecycle rules, validation, urgency projection, snapshots.
- `src/ChairSide.Board/Services/SqliteBoardRepository.cs`: persisted room states, guarded handoff operations, completed cycles.
- `src/ChairSide.Board/Hubs/BoardHub.cs`: SignalR hub type.
- `src/ChairSide.Board/appsettings.json`: default room count and timing thresholds.

Edit the Mermaid source blocks above whenever their related flow changes. GitHub renders them in Markdown; the [Mermaid Live Editor](https://mermaid.live/) is useful for experimenting with layouts. No runtime Mermaid dependency belongs in ChairSide.
