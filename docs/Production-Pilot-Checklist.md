# ChairSide Board Production Pilot Checklist

Use this checklist for a small internal pilot. Do not enter PHI.

## Server Layout

- [ ] Create `C:\ChairSide\App`.
- [ ] Create `C:\ChairSide\Data`.
- [ ] Create `C:\ChairSide\Backups`.
- [ ] Optionally create `C:\ChairSide\Logs`.
- [ ] Confirm the production database path is `C:\ChairSide\Data\chairside.db`.
- [ ] Confirm no existing component from `C:\` through `chairside.db` is a reparse point.

## IIS

- [ ] Create a dedicated IIS app pool, for example `ChairSideBoard`.
- [ ] Set `.NET CLR Version` to `No Managed Code`.
- [ ] Set start mode to `AlwaysRunning`.
- [ ] Set idle timeout to `0`.
- [ ] Set maximum worker processes to `1`.
- [ ] Disable overlapped recycle: `Disallow Overlapping Rotation = true`.
- [ ] Confirm the site is internal-only, for example `http://chairside`.
- [ ] Confirm firewall rules allow only intended internal access.

## Permissions

- [ ] Grant `IIS AppPool\ChairSideBoard` Modify permission on `C:\ChairSide\Data`.
- [ ] Grant backup operators appropriate access to `C:\ChairSide\Backups`.
- [ ] Grant log access to `C:\ChairSide\Logs` only if logging is enabled.
- [ ] Confirm SQLite WAL sidecar files can be written beside `chairside.db`.

## Publish And Config

- [ ] Run `dotnet publish .\src\ChairSide.Board\ChairSide.Board.csproj -c Release -o .\publish`.
- [ ] Copy publish output to `C:\ChairSide\App`.
- [ ] Do not commit `.\publish`.
- [ ] Confirm `ASPNETCORE_ENVIRONMENT` is exactly `Production` (case-insensitive, with no surrounding whitespace); all other names except `Development` and `Training` are refused.
- [ ] Review `appsettings.Production.json`.
- [ ] Confirm `BoardPersistenceOptions:DatabasePath` is `C:\ChairSide\Data\chairside.db`.
- [ ] Confirm startup refuses any non-canonical, application-root, Training-root, or reparse-point database path before creating SQLite files.
- [ ] Review and accept `RoomExpirationOptions`: active rooms expire after the configured max duration, and the after-hours sweep expires still-active rooms once per clinic day at the configured local time.
- [ ] Confirm production secrets/tokens are environment-specific or deployment-time values.
- [ ] Confirm no real tokens are committed to source control.
- [ ] Confirm every destructive maintenance command is refused in Production before application build and database access.

## Backup And Restore

- [ ] Install `sqlite3.exe` on the IIS server for safe online backups.
- [ ] Run a backup smoke test: `.\scripts\Backup-ChairSideSqlite.ps1`.
- [ ] Confirm a timestamped backup appears under `C:\ChairSide\Backups`.
- [ ] Document the restore drill owner and timing.
- [ ] Review restore safety: stop app pool, create pre-restore safety backup, restore, start app pool.
- [ ] Confirm backups are protected even though they contain non-PHI operational data.
- [ ] Decide backup retention policy. Baseline suggestion: keep at least 7 days of daily backups unless clinic policy requires more.

## Access Control Decisions

- [ ] Decide whether `RoomDeviceBindingOptions:Enabled` is enabled for the pilot.
- [ ] If room-device binding is enabled, configure unique per-room tokens outside source control.
- [ ] If room-device binding is disabled, explicitly accept that any internal LAN device able to reach the app can mutate room state.
- [ ] Decide whether `AdminAccessOptions:Enabled` is enabled for the pilot.
- [ ] If admin/report protection is enabled, configure the shared report/admin token outside source control.
- [ ] If admin/report protection is disabled, explicitly accept that report data is visible to anyone on the internal LAN.

## Room Tablets

- [ ] Confirm canonical room URL format: `http://chairside/room.html?roomId={roomNumber}`.
- [ ] Confirm Room 1 tablet loads `http://chairside/room.html?roomId=1`.
- [ ] Choose room-token delivery method: deployment-time meta tag injection or tablet prompt/sessionStorage.
- [ ] If using tablet prompt/sessionStorage delivery, no extra deployment tooling is required.
- [ ] If using deployment-time meta tag room-token injection, confirm your deployment process handles injection. The client supports the meta tag, but this repo does not currently include a generator or injection script.
- [ ] Confirm room tokens are never placed in URLs.
- [ ] Confirm each room token is unique and environment-specific.
- [ ] Confirm staff know how to use `Room access token required` prompt if binding is enabled.

## Reports Access

- [ ] Confirm `/reports.html` loads as a browser shell.
- [ ] Confirm `/api/reports` is protected when admin/report protection is enabled.
- [ ] Configure the admin/report shared token outside source control.
- [ ] Confirm admin/report tokens are never placed in URLs.
- [ ] Confirm reports access is limited to approved internal operators.
- [ ] Confirm reports are used for operational/team-process review, not doctor or staff rankings, scoreboards, awards, or punitive performance framing.

## Rosters

- [ ] Review configured doctors.
- [ ] Review configured procedures.
- [ ] Confirm active/inactive doctor entries are correct.
- [ ] Confirm active/inactive procedure entries are correct.
- [ ] Confirm procedure codes and labels display correctly.

## No-PHI Signoff

- [ ] Confirm no patient names are entered.
- [ ] Confirm no DOB is entered.
- [ ] Confirm no MRN/chart number is entered.
- [ ] Confirm no clinical notes or free-text patient notes are entered.
- [ ] Confirm staff understand ChairSide tracks rooms, doctors, procedures, and timing only.

## Startup Log Review

- [ ] Start or recycle the `ChairSideBoard` app pool.
- [ ] Confirm startup logs show the expected database path.
- [ ] Confirm startup logs show whether room-device binding is enabled or disabled.
- [ ] Confirm startup logs show whether admin/report protection is enabled or disabled.
- [ ] Review and accept any Production warnings before pilot use.

## Smoke Test

- [ ] Browse `http://chairside/master.html`.
- [ ] Browse `http://chairside/room.html?roomId=1`.
- [ ] Browse `http://chairside/doctor.html`.
- [ ] Browse `http://chairside/reports.html`.
- [ ] Seat Room 1.
- [ ] Mark Doctor Arrived.
- [ ] Mark Doctor Complete.
- [ ] Mark Room Available.
- [ ] Confirm Room 1 returns to available.
- [ ] Confirm `/reports.html` can load report data with the admin/report token when protection is enabled.
- [ ] Restart the app pool.
- [ ] Confirm active room state and completed reports persist after restart.

## Rollback

- [ ] Stop the `ChairSideBoard` app pool.
- [ ] Restore the previous app folder or redeploy the previous known-good publish output.
- [ ] Restore the previous SQLite backup if data rollback is required.
- [ ] Start the `ChairSideBoard` app pool.
- [ ] Re-run the smoke test.

## Accepted Pilot Risks

- [ ] ChairSide is internal-only for this pilot.
- [ ] Shared-token controls are operational controls, not full authentication.
- [ ] HTTP/plaintext LAN traffic is accepted unless HTTPS is configured.
- [ ] CSRF protection is not implemented yet.
- [ ] Pilot scope is limited to approved rooms, tablets, and internal operators.
