# AGENTS

Project guidance for AI agents working on ChairSide.
# ChairSide Project Brief

## Project name

ChairSide Board

## Purpose

ChairSide Board is an internal-only surgical room status and doctor dispatch system for an oral surgery practice.

It is a modern replacement for an older physical light-board system. The system should help doctors and staff quickly see:

1. Which rooms have seated patients
2. Which doctor is assigned
3. What procedure or visit type is involved
4. How long the patient has been seated
5. Whether the room has entered an aging or stale wait state

The system tracks rooms, not patients.

## Scope

This application is for internal use only at one oral surgery office.

It should be locally hosted on the internal network and should not require public internet exposure.

The first version should be a web application, not a native iOS or Android app.

Preferred deployment target:

- Local Windows Server VM
- Internal DNS name, for example `chairside.local` or `chairside.aospeoria.local`
- Browser-based room panels, master display, and doctor views

## Critical privacy rule

Do not store, display, request, import, or infer PHI.

The system must not include:

- Patient names
- Dates of birth
- Chart numbers
- Medical histories
- Diagnoses
- Treatment notes
- Insurance data
- Billing data
- Free-text patient notes

The system may store:

- Room number
- Assigned doctor
- Procedure category
- Room state
- Timer values
- Event timestamps
- Device identity
- Non-PHI operational metrics

A useful project mantra:

> This system does not track patients. It tracks rooms.

## Doctors and colors

Use the following doctors in mockups, seed data, and UI examples:

- Dr. Otte = blue
- Dr. Pledger = green
- Dr. Gibson = orange
- Dr. Schroeder = purple

Keep doctor-color assignments consistent.

## Procedure categories and icons

Use distinctive procedure icons that cannot easily be confused with each other.

Initial procedure categories:

- Consult: speech bubble icon, label `CON`
- Extraction: forceps icon, label `EXT`
- Sedation: crescent moon icon, label `SED`
- Post-op: checkmark in square icon, label `POST`
- Implant: implant screw / bolt icon, label `IMP`
- Biopsy: vial / sample icon, label `BX`

Avoid tiny detailed tooth icons because they blur together from a distance.

## Visual language

The master view should be a responsive grid of room cards showing the configured surgical rooms.

Room states:

- Gray = empty / inactive
- Solid doctor color = patient seated for that doctor
- Slow pulse in doctor color = aging wait state
- Doctor color alternating with white = stale wait state
- Procedure icon remains stable and readable during all states

Do not use red for normal stale states. Red should be avoided because the office handles true emergencies physically by grabbing a doctor and following emergency protocol.

The board should answer at a glance:

- Room location = where
- Doctor color = who
- Procedure icon = why
- Timer / animation = how long or how urgent operationally

## Rooms

The MVP should support a configurable surgical room count.

Rooms should be configurable, with the default early prototype using:

- Room 1 through Room 12

## Core workflow

1. Staff mark a patient seated from the room tablet.
2. Staff select assigned doctor and procedure category.
3. The room appears on the master display in the doctor’s color.
4. The room timer starts.
5. When elapsed time crosses the aging threshold, the room slowly pulses.
6. When elapsed time crosses the stale threshold, the room alternates doctor color and white.
7. When the doctor physically enters the room, the request/state is cleared from the in-room tablet.
8. The system logs seated-to-doctor time.

Doctors should be able to view room status from a phone or workstation, but they should not be able to acknowledge or clear the room remotely.

Room clearing should occur from the room-local tablet/panel only.

## Metrics and reporting

The primary metric is:

- Seated-to-doctor time

Definition:

- The elapsed time between when a patient is marked seated and when the doctor physically arrives and clears the room from the in-room panel.

Track:

- PatientSeatedAt
- AgingStartedAt
- StaleStartedAt
- DoctorArrivedAt / ClearedAt
- Total seated-to-doctor duration
- Above-threshold duration
- Stale duration

Reports should eventually include:

- Average seated-to-doctor time
- Median seated-to-doctor time
- Aging event count
- Stale event count
- Total above-threshold wait time
- Trends by doctor, room, procedure, and time of day

Optional monthly recognition:

- Golden Forceps Award
- Awarded monthly for best room-flow performance based on seated-to-doctor time, low stale-room rate, and consistency

Use median seated-to-doctor time as the main performance metric rather than only average.

## MVP features

Build the MVP around:

- Configurable room-card grid master view
- Room-mounted tablet panel view
- Doctor read-only view
- Doctor color coding
- Distinctive procedure icons
- Seated timer
- Aging and stale states
- Room-local clearing
- Event logging
- Basic reporting

Out of scope for MVP:

- Patient names or PHI
- WinOMS integration
- Scheduling integration
- Billing integration
- Clinical documentation
- Emergency alerting
- Native mobile apps
- Public internet access
- Hardware buttons or custom LEDs

## Suggested technical direction

Preferred stack unless otherwise instructed:

- ASP.NET Core
- SignalR for real-time updates
- SQL Server Express or SQLite for early prototype
- Browser-based UI
- Responsive browser-based room card grid
- Local Windows VM deployment

A React frontend with ASP.NET Core backend is acceptable.

A Blazor-based implementation is also acceptable if it reduces complexity.

## Development priorities

Prioritize:

1. Reliability
2. Glanceability
3. Low-friction room workflow
4. Clean event logging
5. Simple maintainable architecture

Avoid:

- Overengineering
- Complex mobile app dependencies
- Cloud-only services
- PHI exposure
- Free-text patient fields
- Remote doctor acknowledgment/clear buttons
- Too many icons or statuses in version 1

## UX rules

Room panels should be simple and touch-friendly.

The room panel should already know which room it belongs to.

Staff should not have to select the room manually from a room-mounted panel.

The master board should be readable from across the room on a large TV.

Procedure icons should remain stable while the room background animates.

Use labels alongside icons until users learn the visual language.

## Testing expectations

When code exists, include instructions for:

- How to run locally
- How to seed demo data
- How to run tests
- How to reset the local database
- How to view the master display
- How to view a room panel
- How to view the doctor read-only display

Keep tests practical and focused on core room-state transitions and timing calculations.
