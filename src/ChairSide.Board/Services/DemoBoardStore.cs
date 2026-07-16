using System.Globalization;

using ChairSide.Board.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Services;

/// <summary>Operational (non-PHI) reasons a cycle may be classified as an exception.</summary>
public static class ExceptionReasons
{
    /// <summary>Manually flagged for review via the reports UI.</summary>
    public const string ManualReview = "ManualReview";

    /// <summary>Active cycle exceeded the configured maximum active duration.</summary>
    public const string ExceededMaxActiveDuration = "ExceededMaxActiveDuration";

    /// <summary>Active cycle was terminated by the configured after-hours sweep.</summary>
    public const string AfterHoursSweep = "AfterHoursSweep";
}

/// <summary>
/// Neutral, non-punitive reporting-time classifications derived from a completed cycle's own
/// data (never persisted). These protect doctor-facing summaries from legacy/sample/training
/// records without deleting or hiding them: flagged cycles stay visible in raw/audit output but
/// are excluded from standard aggregates. Distinct from the manual <see cref="ExceptionReasons"/>
/// review workflow.
/// </summary>
public static class ReportingExceptionReasons
{
    /// <summary>Procedure resolves in the roster but is no longer active (e.g. standalone Sedation).</summary>
    public const string LegacyProcedure = "LegacyProcedure";

    /// <summary>Procedure code cannot be resolved to any roster entry.</summary>
    public const string UnmappedProcedure = "UnmappedProcedure";

    /// <summary>Measured duration exceeds a conservative threshold.</summary>
    public const string ExtremeDuration = "ExtremeDuration";

    /// <summary>Seated and completed/available timestamps fall on different calendar days.</summary>
    public const string OvernightLifecycle = "OvernightLifecycle";

    /// <summary>Timestamps required for standard timing calculations are missing.</summary>
    public const string MissingTiming = "MissingTiming";
}

public sealed class DemoBoardStore
{
    private const int MaxRoomEvents = 200;

    private readonly object _syncRoot = new();
    private readonly IOptionsMonitor<BoardThresholdOptions> _thresholdOptions;
    private readonly IOptionsMonitor<RoomExpirationOptions> _expirationOptions;
    private readonly SqliteBoardRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly int _roomCount;
    private readonly bool _demoTimerEnabled;
    private readonly List<Doctor> _doctors;
    private readonly List<Doctor> _activeDoctors;
    private readonly List<ProcedureCategory> _procedures;
    private readonly List<ProcedureCategory> _activeProcedures;
    private readonly bool _demoOffsetsAllowed;

    private readonly List<RoomState> _rooms;
    private readonly List<RoomEvent> _events = [];
    private readonly List<CompletedRoomCycle> _completedCycles = [];

    // After-hours sweep: track the last clinic day the sweep ran to ensure at-most-once per day.
    // Volatile - intentionally resets on app restart; available rooms are unaffected by re-check.
    private DateOnly _lastSweepDate = DateOnly.MinValue;

    public DemoBoardStore(
        IOptionsMonitor<BoardThresholdOptions> thresholdOptions,
        IOptionsMonitor<RoomExpirationOptions> expirationOptions,
        IOptions<BoardOptions> boardOptions,
        IOptions<BoardUiOptions> boardUiOptions,
        IOptions<DoctorRosterOptions> doctorRosterOptions,
        IOptions<ProcedureRosterOptions> procedureRosterOptions,
        SqliteBoardRepository repository,
        IWebHostEnvironment environment,
        TimeProvider? timeProvider = null)
    {
        _thresholdOptions = thresholdOptions;
        _expirationOptions = expirationOptions;
        _repository = repository;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _roomCount = boardOptions.Value.RoomCount;
        _demoTimerEnabled = boardUiOptions.Value.DemoTimerEnabled ?? !environment.IsProduction();
        _demoOffsetsAllowed = environment.IsDevelopment();
        _doctors = BuildDoctors(doctorRosterOptions.Value).ToList();
        _activeDoctors = BuildDoctors(doctorRosterOptions.Value, activeOnly: true).ToList();
        _procedures = BuildProcedures(procedureRosterOptions.Value).ToList();
        _activeProcedures = BuildProcedures(procedureRosterOptions.Value, activeOnly: true).ToList();
        var now = Now;

        var hasOperationalData = _repository.HasOperationalData();
        _repository.EnsureConfiguredRooms(_roomCount);
        _rooms = _repository.LoadRooms(_roomCount).ToList();
        AddMissingRooms();

        if (!hasOperationalData && !environment.IsProduction())
        {
            SeedDemoRooms(now);
            _repository.SaveRooms(_rooms, _doctors, _procedures);
        }

        _completedCycles = _repository.LoadCompletedCycles().ToList();
    }

    public BoardSnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            var now = Now;
            return new BoardSnapshot(
                now,
                _roomCount,
                Thresholds.AgingMinutes,
                Thresholds.StaleMinutes,
                Thresholds.AgingThreshold,
                Thresholds.StaleThreshold,
                _demoTimerEnabled,
                _activeDoctors,
                _activeProcedures,
                _rooms.Select(room => ToRoomStatus(room, now)).ToList(),
                _events.OrderByDescending(item => item.Timestamp).Take(20).ToList());
        }
    }

    public RoomStatus? GetRoom(int roomNumber)
    {
        lock (_syncRoot)
        {
            var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
            if (room is null)
            {
                return null;
            }

            var now = Now;
            return ToRoomStatus(room, now);
        }
    }

    public bool IsConfiguredRoom(int roomNumber)
    {
        lock (_syncRoot)
        {
            return _rooms.Any(item => item.RoomId == roomNumber);
        }
    }

    public CanonicalAssignmentConversionResult ConvertCanonicalAssignment(CanonicalAssignmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var procedure = request.ProcedureCode is null ? null : FindActiveProcedure(request.ProcedureCode);
        var converted = CanonicalAssignmentRequestConverter.Convert(request, procedure);
        if (converted.Value is not { } assignment || assignment.Sedation.State != SedationState.EligibleYes) return converted;
        return new(RoomAssignmentContract.Create(assignment.DoctorId, ComposeProcedureCode(assignment.ProcedureCode!, true), assignment.Sedation, assignment.ExpectedAllocation), null);
    }

    public PrestagingLifecycleMutationResult BeginPrestageCanonical(int roomNumber)
    {
        var assignment = RoomAssignmentContract.Create(null, null, SedationContract.UnavailableNoProcedure(), ExpectedAllocationContract.Unknown());
        try
        {
            lock (_syncRoot)
            {
                var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
                if (room is null) return CanonicalFailure(PrestagingLifecycleMutationOutcome.RoomNotFound);
                if (!CanBeginPrestage(room)) return CanonicalFailure(PrestagingLifecycleMutationOutcome.LifecycleConflict);
                var expectation = ActiveRoomWriteExpectation.FromRoom(room);
                var now = Now;
                var candidate = new RoomState(room.RoomId) { EpisodeId = NewEpisodeId(), State = RoomStates.Prestaging, PrestageStartedAt = now };
                var persisted = PersistedRoomAssignment.FromCanonicalContract(assignment, null, null);
                ApplyPersistedAssignment(candidate, persisted);
                var committed = _repository.SaveCanonicalAssignment(candidate, persisted, expectation, _doctors, _procedures);
                if (committed is null) return CanonicalFailure(PrestagingLifecycleMutationOutcome.StaleWrite);
                ApplyCommittedRoom(room, committed.Room);
                AddEvent(new RoomEvent(room.RoomId, "PrestageStarted", now, null, null));
                return CanonicalSuccess(room, assignment, now);
            }
        }
        catch (SqliteException exception) { return CanonicalFailure(PrestagingLifecycleMutationOutcome.PersistenceFailure, exception: exception); }
    }

    public PrestagingLifecycleMutationResult SaveAssignmentDetailsCanonical(int roomNumber, RoomAssignmentContract assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        try
        {
            lock (_syncRoot)
            {
                var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
                if (room is null) return CanonicalFailure(PrestagingLifecycleMutationOutcome.RoomNotFound);
                var faults = DeriveIntegrityFaults(room);
                if (faults.Count > 0) return CanonicalFailure(PrestagingLifecycleMutationOutcome.IntegrityFault, faults);
                if (room.State is RoomStates.ReadyForDoctor or RoomStates.Aging or RoomStates.Stale || room.DoctorArrivedAt is not null) return CanonicalFailure(PrestagingLifecycleMutationOutcome.AssignmentLocked);
                if (room.State is not (RoomStates.Prestaging or RoomStates.Seated)) return CanonicalFailure(PrestagingLifecycleMutationOutcome.LifecycleConflict);
                if (!IsAssignmentValidForRoster(assignment)) return CanonicalFailure(PrestagingLifecycleMutationOutcome.InvalidAssignment);
                var expectation = ActiveRoomWriteExpectation.FromRoom(room);
                var persisted = PersistedRoomAssignment.FromCanonicalContract(assignment, ResolveDoctorDisplayName(assignment.DoctorId), ResolveProcedure(assignment.ProcedureCode)?.Label);
                var candidate = CopyRoomState(room);
                ApplyPersistedAssignment(candidate, persisted);
                var committed = _repository.SaveCanonicalAssignment(candidate, persisted, expectation, _doctors, _procedures);
                if (committed is null) return CanonicalFailure(PrestagingLifecycleMutationOutcome.StaleWrite);
                ApplyCommittedRoom(room, committed.Room);
                var now = Now;
                AddEvent(new RoomEvent(room.RoomId, "AssignmentSaved", now, room.AssignedDoctor, room.ProcedureCode));
                return CanonicalSuccess(room, assignment, now);
            }
        }
        catch (SqliteException exception) { return CanonicalFailure(PrestagingLifecycleMutationOutcome.PersistenceFailure, exception: exception); }
    }

    public PrestagingLifecycleMutationResult SeatRoomCanonical(int roomNumber, RoomAssignmentContract? assignment)
    {
        try
        {
            lock (_syncRoot)
            {
                var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
                if (room is null) return CanonicalFailure(PrestagingLifecycleMutationOutcome.RoomNotFound);
                var faults = DeriveIntegrityFaults(room);
                if (faults.Count > 0) return CanonicalFailure(PrestagingLifecycleMutationOutcome.IntegrityFault, faults);
                if (!CanSeat(room) || room.PrestageStartedAt is null) return CanonicalFailure(PrestagingLifecycleMutationOutcome.LifecycleConflict);
                if (assignment is not null && !IsAssignmentValidForRoster(assignment)) return CanonicalFailure(PrestagingLifecycleMutationOutcome.InvalidAssignment);
                var expectation = ActiveRoomWriteExpectation.FromRoom(room);
                var now = Now;
                var candidate = CopyRoomState(room);
                var persisted = assignment is null ? GetCanonicalAssignment(candidate) : PersistedRoomAssignment.FromCanonicalContract(assignment, ResolveDoctorDisplayName(assignment.DoctorId), ResolveProcedure(assignment.ProcedureCode)?.Label);
                ApplyPersistedAssignment(candidate, persisted);
                candidate.SeatedAt = now;
                candidate.State = RoomStates.Seated;
                var committed = _repository.SaveCanonicalAssignment(candidate, persisted, expectation, _doctors, _procedures);
                if (committed is null) return CanonicalFailure(PrestagingLifecycleMutationOutcome.StaleWrite);
                ApplyCommittedRoom(room, committed.Room);
                AddEvent(new RoomEvent(room.RoomId, "Seated", now, room.AssignedDoctor, room.ProcedureCode));
                return CanonicalSuccess(room, persisted.ToContract(), now);
            }
        }
        catch (SqliteException exception) { return CanonicalFailure(PrestagingLifecycleMutationOutcome.PersistenceFailure, exception: exception); }
    }

    private PrestagingLifecycleMutationResult CanonicalSuccess(
        RoomState room,
        RoomAssignmentContract assignment,
        DateTimeOffset now,
        ReadyHandoffContract? handoff = null) =>
        new(PrestagingLifecycleMutationOutcome.Success, ToRoomStatus(room, now), assignment, [], Handoff: handoff);

    private static PrestagingLifecycleMutationResult CanonicalFailure(
        PrestagingLifecycleMutationOutcome outcome,
        IReadOnlyList<RoomIntegrityFault>? faults = null,
        Exception? exception = null,
        DoctorArrivalConflict? conflict = null) =>
        new(outcome, IntegrityFaults: faults ?? [], PersistenceException: exception, DoctorArrivalConflict: conflict);
    public RoomStatus? BeginPrestage(int roomNumber)
    {
        return BeginPrestage(
            roomNumber,
            RoomAssignmentContract.Create(
                null,
                null,
                SedationContract.UnavailableNoProcedure(),
                ExpectedAllocationContract.Unknown()));
    }

    public RoomStatus? BeginPrestage(int roomNumber, string doctorId, string procedureCode, bool sedation = false, int? expectedAllocationUnits = null)
    {
        var doctor = _activeDoctors.FirstOrDefault(item => item.Id == doctorId);
        var procedure = FindActiveProcedure(procedureCode);
        if (doctor is null || procedure is null || !HasValidExpectedAllocation(procedure) || (sedation && !procedure.SedationEligible))
        {
            return null;
        }

        return BeginPrestage(roomNumber, CreateLegacyCompatibleAssignment(doctor, procedure, sedation, expectedAllocationUnits));
    }

    private RoomStatus? BeginPrestage(int roomNumber, RoomAssignmentContract assignment)
    {
        lock (_syncRoot)
        {
            var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
            if (room is null || !CanBeginPrestage(room))
            {
                return null;
            }

            var expectation = ActiveRoomWriteExpectation.FromRoom(room);
            var now = Now;
            var candidate = new RoomState(room.RoomId)
            {
                EpisodeId = NewEpisodeId(),
                State = RoomStates.Prestaging,
                PrestageStartedAt = now
            };
            var persisted = PersistedRoomAssignment.FromCanonicalContract(
                assignment,
                ResolveDoctorDisplayName(assignment.DoctorId),
                ResolveProcedure(assignment.ProcedureCode)?.Label);
            ApplyPersistedAssignment(candidate, persisted);
            var committed = _repository.SaveCanonicalAssignment(candidate, persisted, expectation, _doctors, _procedures);
            if (committed is null)
            {
                return null;
            }
            ApplyCommittedRoom(room, committed.Room);
            AddEvent(new RoomEvent(room.RoomId, "PrestageStarted", now, room.AssignedDoctor, room.ProcedureCode));
            return ToRoomStatus(room, now);
        }
    }

    public RoomStatus? SaveAssignmentDetails(int roomNumber, RoomAssignmentContract assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        lock (_syncRoot)
        {
            var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
            if (room is null || room.DoctorArrivedAt is not null || room.State is not (RoomStates.Prestaging or RoomStates.Seated))
            {
                return null;
            }

            if (!IsAssignmentValidForRoster(assignment))
            {
                return null;
            }

            var expectation = ActiveRoomWriteExpectation.FromRoom(room);
            var persisted = PersistedRoomAssignment.FromCanonicalContract(
                assignment,
                ResolveDoctorDisplayName(assignment.DoctorId),
                ResolveProcedure(assignment.ProcedureCode)?.Label);
            var candidate = CopyRoomState(room);
            ApplyPersistedAssignment(candidate, persisted);
            var committed = _repository.SaveCanonicalAssignment(candidate, persisted, expectation, _doctors, _procedures);
            if (committed is null)
            {
                return null;
            }
            ApplyCommittedRoom(room, committed.Room);
            AddEvent(new RoomEvent(room.RoomId, "AssignmentSaved", Now, candidate.AssignedDoctor, candidate.ProcedureCode));
            return ToRoomStatus(room, Now);
        }
    }

    /// <summary>
    /// Corrects the provisional doctor/procedure/sedation/allocation assignment while an active room
    /// is Prestaging or Seated. Ready for Doctor locks the durably saved assignment into an immutable
    /// handoff; staff must withdraw Ready before correcting it. Preserves the same episode and all
    /// existing phase timestamps; replaces only the assignment/allocation snapshot. Creates no
    /// completed cycle and no aborted-assignment record - this is a correction, not a termination.
    /// </summary>
    public RoomStatus? UpdateRoomAssignment(int roomNumber, string doctorId, string procedureCode, bool sedation = false, int? expectedAllocationUnits = null)
    {
        var doctor = _activeDoctors.FirstOrDefault(item => item.Id == doctorId);
        var procedure = FindActiveProcedure(procedureCode);
        if (doctor is null || procedure is null || !HasValidExpectedAllocation(procedure) || (sedation && !procedure.SedationEligible))
        {
            return null;
        }

        return SaveAssignmentDetails(roomNumber, CreateLegacyCompatibleAssignment(doctor, procedure, sedation, expectedAllocationUnits));
    }

    public RoomStatus? SeatRoom(int roomNumber, int demoElapsedMinutes = 0)
    {
        return SeatRoomWithAssignment(roomNumber, assignment: null, demoElapsedMinutes);
    }

    public RoomStatus? SeatRoom(int roomNumber, RoomAssignmentContract assignment, int demoElapsedMinutes = 0)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return SeatRoomWithAssignment(roomNumber, assignment, demoElapsedMinutes);
    }

    public RoomStatus? SeatRoom(int roomNumber, string doctorId, string procedureCode, int demoElapsedMinutes = 0, bool sedation = false, int? expectedAllocationUnits = null)
    {
        var doctor = _activeDoctors.FirstOrDefault(item => item.Id == doctorId);
        var procedure = FindActiveProcedure(procedureCode);
        if (doctor is null || procedure is null || !HasValidExpectedAllocation(procedure) || (sedation && !procedure.SedationEligible))
        {
            return null;
        }

        return SeatRoomWithAssignment(
            roomNumber,
            CreateLegacyCompatibleAssignment(doctor, procedure, sedation, expectedAllocationUnits),
            demoElapsedMinutes);
    }

    private RoomStatus? SeatRoomWithAssignment(int roomNumber, RoomAssignmentContract? assignment, int demoElapsedMinutes)
    {
        lock (_syncRoot)
        {
            if (!IsDemoElapsedMinutesAllowed(demoElapsedMinutes))
            {
                return null;
            }

            var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
            if (room is null || !CanSeat(room) || room.PrestageStartedAt is null)
            {
                return null;
            }

            if (assignment is not null && !IsAssignmentValidForRoster(assignment))
            {
                return null;
            }

            var expectation = ActiveRoomWriteExpectation.FromRoom(room);
            var now = Now;
            var offset = TimeSpan.FromMinutes(demoElapsedMinutes);
            var candidate = CopyRoomState(room);
            if (demoElapsedMinutes != 0)
            {
                candidate.PrestageStartedAt = candidate.PrestageStartedAt!.Value - offset;
            }

            var persisted = assignment is null
                ? GetCanonicalAssignment(candidate)
                : PersistedRoomAssignment.FromCanonicalContract(
                    assignment,
                    ResolveDoctorDisplayName(assignment.DoctorId),
                    ResolveProcedure(assignment.ProcedureCode)?.Label);
            ApplyPersistedAssignment(candidate, persisted);
            candidate.SeatedAt = now - offset;
            candidate.State = RoomStates.Seated;
            var committed = _repository.SaveCanonicalAssignment(candidate, persisted, expectation, _doctors, _procedures);
            if (committed is null)
            {
                return null;
            }
            ApplyCommittedRoom(room, committed.Room);
            AddEvent(new RoomEvent(room.RoomId, "Seated", now, room.AssignedDoctor, room.ProcedureCode));
            return ToRoomStatus(room, now);
        }
    }

    public RoomStatus? UpdateAssignment(int roomNumber, string doctorId, string procedureCode, bool sedation = false)
    {
        return UpdateRoomAssignment(roomNumber, doctorId, procedureCode, sedation);
    }

    public RoomStatus? CancelPrestage(int roomNumber, string? cancellationReason = null) =>
        CancelIncompleteAssignment(roomNumber, cancellationReason, prestageOnly: true);

    public RoomStatus? CancelSeating(int roomNumber, string? cancellationReason = null) =>
        CancelIncompleteAssignment(roomNumber, cancellationReason, prestageOnly: false);

    private RoomStatus? CancelIncompleteAssignment(int roomNumber, string? cancellationReason, bool prestageOnly)
    {
        lock (_syncRoot)
        {
            if (!IsValidCancellationReason(cancellationReason))
            {
                return null;
            }

            var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
            if (room is null)
            {
                return null;
            }

            var now = Now;
            var snapshot = CopyRoomState(room);
            var canCancel = prestageOnly ? CanCancelPrestage(snapshot) : CanCancelSeating(snapshot);
            if (!canCancel)
            {
                return null;
            }

            var record = new AbortedRoomAssignment
            {
                EpisodeId = snapshot.EpisodeId ?? NewEpisodeId(),
                RoomId = snapshot.RoomId,
                AssignedDoctor = snapshot.AssignedDoctor,
                AssignedDoctorDisplayName = snapshot.AssignedDoctorDisplayName,
                ProcedureCode = snapshot.ProcedureCode,
                ProcedureCategory = snapshot.ProcedureCategory,
                SedationState = snapshot.SedationState,
                ExpectedAllocationState = snapshot.ExpectedAllocationState,
                ExpectedAllocationSuggestedUnits = snapshot.ExpectedAllocationSuggestedUnits,
                ExpectedAllocationConfirmedUnits = snapshot.ExpectedAllocationConfirmedUnits,
                OriginalDefaultExpectedUnits = snapshot.OriginalDefaultExpectedUnits,
                ExpectedAllocationUnits = snapshot.ExpectedAllocationUnits,
                ExpectedAllocationMinutes = snapshot.ExpectedAllocationMinutes,
                AllocationAdjustedFromDefault = snapshot.AllocationAdjustedFromDefault,
                PrestageStartedAt = snapshot.PrestageStartedAt,
                SeatedAt = snapshot.SeatedAt,
                ReadyForDoctorAt = snapshot.ReadyForDoctorAt,
                TerminatedAt = now,
                TerminatedFromState = snapshot.State,
                TerminationKind = TerminationKinds.StaffCanceled,
                CancellationReason = cancellationReason
            };
            var resetRoom = new RoomState(room.RoomId);

            if (snapshot.ActiveReadyHandoffId is { Length: > 0 })
            {
                // Cancellation tolerates a faulted handoff reference: CancelReadyRoom terminates the
                // handoff only when the room genuinely owns it as Active, and otherwise preserves the
                // unrelated/historical row untouched while still recording the abort and releasing the
                // room. Doctor Arrived and Withdraw Ready remain gated on integrity faults elsewhere.
                _repository.CancelReadyRoom(
                    record,
                    snapshot,
                    snapshot.ActiveReadyHandoffId,
                    now,
                    _doctors,
                    _procedures);
            }
            else
            {
                _repository.TerminateIncompleteAssignment(record, resetRoom, _doctors, _procedures);
            }

            ResetRoom(room);
            AddEvent(new RoomEvent(
                room.RoomId,
                prestageOnly ? "PrestageCanceled" : "SeatingCanceled",
                now,
                record.AssignedDoctor,
                record.ProcedureCode));

            return ToRoomStatus(room, now);
        }
    }

    public RoomStatus? MarkReadyForDoctor(int roomNumber)
    {
        lock (_syncRoot)
        {
            var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
            if (room is null)
            {
                return null;
            }

            var now = Now;
            if (!CanMarkReadyForDoctor(room) || room.SeatedAt is null)
            {
                return null;
            }
            RoomAssignmentContract assignment;
            try
            {
                assignment = GetCanonicalAssignment(room).ToContract();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            if (assignment.Completeness != AssignmentCompleteness.Complete || room.ActiveReadyHandoffId is not null || room.AcceptedReadyHandoffId is not null)
            {
                return null;
            }

            // Ready is rejected when the persisted assignment is no longer valid against the current
            // active roster, even if its stored completeness is Complete (e.g. a since-deactivated
            // doctor/procedure, or a domain-impossible stored procedure/sedation pairing).
            if (!IsAssignmentValidForRoster(assignment))
            {
                return null;
            }

            var candidate = CopyRoomState(room);
            candidate.State = RoomStates.ReadyForDoctor;
            var handoff = _repository.CreateReadyHandoff(candidate, assignment, now, _doctors, _procedures);
            candidate.ReadyForDoctorAt = now;
            candidate.ActiveReadyHandoffId = handoff.HandoffId;
            candidate.AcceptedReadyHandoffId = null;
            ApplyPersistedAssignment(candidate, handoff.Assignment);
            ApplyCommittedRoom(room, candidate);
            AddEvent(new RoomEvent(room.RoomId, "ReadyForDoctor", now, room.AssignedDoctor, room.ProcedureCode));
            return ToRoomStatus(room, now);
        }
    }

    public PrestagingLifecycleMutationResult MarkReadyForDoctorCanonical(
        int roomNumber,
        RoomAssignmentContract? suppliedAssignment)
    {
        try
        {
            lock (_syncRoot)
            {
                var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
                if (room is null) return CanonicalFailure(PrestagingLifecycleMutationOutcome.RoomNotFound);
                var faults = DeriveIntegrityFaults(room);
                if (faults.Count > 0) return CanonicalFailure(PrestagingLifecycleMutationOutcome.IntegrityFault, faults);
                if (room.State is RoomStates.ReadyForDoctor or RoomStates.Aging or RoomStates.Stale || room.DoctorArrivedAt is not null)
                {
                    return CanonicalFailure(
                        suppliedAssignment is null
                            ? PrestagingLifecycleMutationOutcome.LifecycleConflict
                            : PrestagingLifecycleMutationOutcome.AssignmentLocked);
                }
                if (room.State != RoomStates.Seated || room.SeatedAt is null)
                {
                    return CanonicalFailure(PrestagingLifecycleMutationOutcome.LifecycleConflict);
                }

                RoomAssignmentContract assignment;
                if (suppliedAssignment is not null)
                {
                    assignment = suppliedAssignment;
                }
                else if (!GetCanonicalAssignment(room).TryToContract(out var persistedAssignment))
                {
                    return CanonicalFailure(PrestagingLifecycleMutationOutcome.InvalidAssignment);
                }
                else
                {
                    assignment = persistedAssignment;
                }

                if (!IsAssignmentValidForRoster(assignment))
                {
                    return CanonicalFailure(PrestagingLifecycleMutationOutcome.InvalidAssignment);
                }
                if (assignment.Completeness != AssignmentCompleteness.Complete)
                {
                    return new PrestagingLifecycleMutationResult(
                        PrestagingLifecycleMutationOutcome.AssignmentIncomplete,
                        Assignment: assignment,
                        IntegrityFaults: []);
                }
                if (room.ActiveReadyHandoffId is not null || room.AcceptedReadyHandoffId is not null)
                {
                    var fault = new RoomIntegrityFault(RoomIntegrityFaultCode.ContradictoryHandoffReferences, assignment);
                    return CanonicalFailure(PrestagingLifecycleMutationOutcome.IntegrityFault, [fault]);
                }

                var expectation = ActiveRoomWriteExpectation.FromRoom(room);
                var now = Now;
                var candidate = CopyRoomState(room);
                candidate.State = RoomStates.ReadyForDoctor;
                var persistence = _repository.CreateReadyHandoffGuarded(
                    candidate,
                    assignment,
                    now,
                    expectation,
                    _doctors,
                    _procedures);
                if (persistence.Outcome == GuardedReadyHandoffPersistenceOutcome.StaleWrite)
                {
                    return CanonicalFailure(PrestagingLifecycleMutationOutcome.StaleWrite);
                }
                if (persistence.Outcome == GuardedReadyHandoffPersistenceOutcome.IntegrityFault)
                {
                    var fault = new RoomIntegrityFault(
                        RoomIntegrityFaultCode.ContradictoryHandoffReferences,
                        assignment);
                    return CanonicalFailure(PrestagingLifecycleMutationOutcome.IntegrityFault, [fault]);
                }

                var committed = persistence.Committed
                    ?? throw new InvalidOperationException("Successful guarded Ready persistence must return committed state.");
                ApplyCommittedRoom(room, committed.Room);
                AddEvent(new RoomEvent(room.RoomId, "ReadyForDoctor", now, room.AssignedDoctor, room.ProcedureCode));
                return CanonicalSuccess(room, assignment, now, committed.Handoff.ToContract());
            }
        }
        catch (SqliteException exception)
        {
            return CanonicalFailure(PrestagingLifecycleMutationOutcome.PersistenceFailure, exception: exception);
        }
    }

    public PrestagingLifecycleMutationResult WithdrawReadyCanonical(int roomNumber)
    {
        try
        {
            lock (_syncRoot)
            {
                var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
                if (room is null) return CanonicalFailure(PrestagingLifecycleMutationOutcome.RoomNotFound);
                if (room.State is not (RoomStates.ReadyForDoctor or RoomStates.Aging or RoomStates.Stale))
                {
                    return CanonicalFailure(PrestagingLifecycleMutationOutcome.LifecycleConflict);
                }

                var faults = DeriveIntegrityFaults(room);
                if (faults.Count > 0) return CanonicalFailure(PrestagingLifecycleMutationOutcome.IntegrityFault, faults);
                if (string.IsNullOrWhiteSpace(room.ActiveReadyHandoffId))
                {
                    var fault = new RoomIntegrityFault(
                        RoomIntegrityFaultCode.ReadyHandoffMissing,
                        GetCanonicalAssignment(room).ToContract());
                    return CanonicalFailure(PrestagingLifecycleMutationOutcome.IntegrityFault, [fault]);
                }

                var expectation = ActiveRoomWriteExpectation.FromRoom(room);
                var now = Now;
                var candidate = CopyRoomState(room);
                candidate.State = RoomStates.Seated;
                candidate.ReadyForDoctorAt = null;
                candidate.AgingStartedAt = null;
                candidate.StaleStartedAt = null;
                candidate.ActiveReadyHandoffId = null;
                var persistence = _repository.WithdrawReadyHandoffGuarded(
                    candidate,
                    room.ActiveReadyHandoffId,
                    now,
                    expectation);
                if (persistence.Outcome == GuardedWithdrawReadyPersistenceOutcome.StaleWrite)
                {
                    return CanonicalFailure(PrestagingLifecycleMutationOutcome.StaleWrite);
                }
                if (persistence.Outcome == GuardedWithdrawReadyPersistenceOutcome.IntegrityFault)
                {
                    var fault = new RoomIntegrityFault(
                        RoomIntegrityFaultCode.ContradictoryHandoffReferences,
                        GetCanonicalAssignment(room).ToContract());
                    return CanonicalFailure(PrestagingLifecycleMutationOutcome.IntegrityFault, [fault]);
                }

                var committed = persistence.Committed
                    ?? throw new InvalidOperationException("Successful guarded Ready withdrawal must return committed state.");
                ApplyCommittedRoom(room, committed.Room);
                AddEvent(new RoomEvent(room.RoomId, "ReadyWithdrawn", now, room.AssignedDoctor, room.ProcedureCode));
                return CanonicalSuccess(room, GetCanonicalAssignment(room).ToContract(), now, committed.Handoff.ToContract());
            }
        }
        catch (SqliteException exception)
        {
            return CanonicalFailure(PrestagingLifecycleMutationOutcome.PersistenceFailure, exception: exception);
        }
    }

    public RoomStatus? WithdrawReady(int roomNumber)
    {
        var result = WithdrawReadyCanonical(roomNumber);
        return result.Outcome == PrestagingLifecycleMutationOutcome.Success ? result.Room : null;
    }

    public RoomStatus? MarkDoctorArrived(int roomNumber)
    {
        lock (_syncRoot)
        {
            var room = PrepareDoctorArrived(roomNumber, out var now);
            return room is null ? null : ApplyDoctorArrived(room, now);
        }
    }

    public PrestagingLifecycleMutationResult MarkDoctorArrivedCanonical(int roomNumber)
    {
        try
        {
            lock (_syncRoot)
            {
                var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
                if (room is null) return CanonicalFailure(PrestagingLifecycleMutationOutcome.RoomNotFound);
                if (!CanMarkDoctorArrived(room) || room.SeatedAt is null)
                {
                    return CanonicalFailure(PrestagingLifecycleMutationOutcome.LifecycleConflict);
                }

                var faults = DeriveIntegrityFaults(room);
                if (faults.Count > 0) return CanonicalFailure(PrestagingLifecycleMutationOutcome.IntegrityFault, faults);
                if (string.IsNullOrWhiteSpace(room.ActiveReadyHandoffId))
                {
                    return CanonicalFailure(
                        PrestagingLifecycleMutationOutcome.IntegrityFault,
                        [new RoomIntegrityFault(RoomIntegrityFaultCode.ReadyHandoffMissing, GetCanonicalAssignment(room).ToContract())]);
                }

                var conflictRoom = FindActiveDoctorRoom(room.AssignedDoctor!, room.RoomId);
                if (conflictRoom is not null)
                {
                    var conflict = new DoctorArrivalConflict(
                        conflictRoom.RoomId,
                        room.AssignedDoctor,
                        ResolveDoctorDisplayName(room.AssignedDoctor));
                    return CanonicalFailure(PrestagingLifecycleMutationOutcome.LifecycleConflict, conflict: conflict);
                }

                var expectation = ActiveRoomWriteExpectation.FromRoom(room);
                var now = Now;
                var candidate = CopyRoomState(room);
                var urgency = ProjectReadyUrgency(candidate, now);
                var seatedToDoctorSeconds = SecondsBetween(candidate.SeatedAt!.Value, now);
                var prepSeconds = candidate.ReadyForDoctorAt.HasValue
                    ? SecondsBetween(candidate.SeatedAt.Value, candidate.ReadyForDoctorAt.Value)
                    : (int?)null;
                var readyToDoctorSeconds = candidate.ReadyForDoctorAt.HasValue
                    ? SecondsBetween(candidate.ReadyForDoctorAt.Value, now)
                    : (int?)null;
                candidate.DoctorArrivedAt = now;
                candidate.State = RoomStates.DoctorInRoom;
                candidate.AgingStartedAt = null;
                candidate.StaleStartedAt = null;
                candidate.ActiveReadyHandoffId = null;
                candidate.AcceptedReadyHandoffId = room.ActiveReadyHandoffId;
                var cycle = new CompletedRoomCycle
                {
                    RoomId = candidate.RoomId,
                    AssignedDoctor = candidate.AssignedDoctor ?? string.Empty,
                    ProcedureCode = candidate.ProcedureCode ?? string.Empty,
                    SeatedAt = candidate.SeatedAt.Value,
                    ReadyForDoctorAt = candidate.ReadyForDoctorAt,
                    DoctorArrivedAt = now,
                    EpisodeId = candidate.EpisodeId,
                    PrestageStartedAt = candidate.PrestageStartedAt,
                    SeatedToDoctorSeconds = seatedToDoctorSeconds,
                    PrepSeconds = prepSeconds,
                    ReadyToDoctorSeconds = readyToDoctorSeconds,
                    FinalWaitState = RoomStates.ReadyForDoctor,
                    AgingThresholdReached = urgency is ReadyUrgency.Aging or ReadyUrgency.Stale,
                    StaleThresholdReached = urgency == ReadyUrgency.Stale,
                    OriginalDefaultExpectedUnits = candidate.OriginalDefaultExpectedUnits,
                    ExpectedAllocationUnits = candidate.ExpectedAllocationUnits,
                    ExpectedAllocationMinutes = candidate.ExpectedAllocationMinutes,
                    AllocationAdjustedFromDefault = candidate.AllocationAdjustedFromDefault
                };
                var persistence = _repository.AcceptReadyHandoffAndSaveCycleGuarded(
                    candidate,
                    cycle,
                    room.ActiveReadyHandoffId,
                    now,
                    expectation,
                    _doctors,
                    _procedures);
                if (persistence.Outcome == GuardedDoctorArrivedPersistenceOutcome.StaleWrite)
                {
                    return CanonicalFailure(PrestagingLifecycleMutationOutcome.StaleWrite);
                }
                if (persistence.Outcome == GuardedDoctorArrivedPersistenceOutcome.DoctorConflict)
                {
                    var conflict = new DoctorArrivalConflict(
                        persistence.ConflictingRoomId
                            ?? throw new InvalidOperationException("A guarded doctor conflict must identify the conflicting room."),
                        room.AssignedDoctor,
                        ResolveDoctorDisplayName(room.AssignedDoctor));
                    return CanonicalFailure(
                        PrestagingLifecycleMutationOutcome.LifecycleConflict,
                        conflict: conflict);
                }
                if (persistence.Outcome == GuardedDoctorArrivedPersistenceOutcome.IntegrityFault)
                {
                    return CanonicalFailure(
                        PrestagingLifecycleMutationOutcome.IntegrityFault,
                        [new RoomIntegrityFault(RoomIntegrityFaultCode.ContradictoryHandoffReferences, GetCanonicalAssignment(room).ToContract())]);
                }

                var committed = persistence.Committed
                    ?? throw new InvalidOperationException("Successful guarded Doctor Arrived persistence must return committed state.");
                ApplyCommittedRoom(room, committed.Room);
                if (committed.CompletedCycle is not null) _completedCycles.Add(committed.CompletedCycle);
                AddEvent(new RoomEvent(
                    room.RoomId,
                    "DoctorArrived",
                    now,
                    room.AssignedDoctor,
                    room.ProcedureCode,
                    TimeSpan.FromSeconds(seatedToDoctorSeconds)));
                return CanonicalSuccess(
                    room,
                    committed.Handoff.Assignment.ToContract(),
                    now,
                    committed.Handoff.ToContract());
            }
        }
        catch (SqliteException exception)
        {
            return CanonicalFailure(PrestagingLifecycleMutationOutcome.PersistenceFailure, exception: exception);
        }
    }

    /// <summary>
    /// Guarded Doctor Arrived entry used by the API. Behaves like MarkDoctorArrived, but if the
    /// room's assigned doctor is already marked doctor-in-room in another room it refuses the
    /// mutation and returns a Conflict outcome carrying safe, non-PHI context (the conflicting
    /// room number and the doctor id/display name) so the UI can prompt the user.
    /// </summary>
    public DoctorArrivalResult TryMarkDoctorArrived(int roomNumber)
    {
        lock (_syncRoot)
        {
            if (!IsConfiguredRoom(roomNumber))
            {
                return new DoctorArrivalResult(DoctorArrivalOutcome.NotConfigured, null, null);
            }

            var room = PrepareDoctorArrived(roomNumber, out var now);
            if (room is null)
            {
                return new DoctorArrivalResult(DoctorArrivalOutcome.Rejected, null, null);
            }

            var conflictRoom = FindActiveDoctorRoom(room.AssignedDoctor!, room.RoomId);
            if (conflictRoom is not null)
            {
                var conflict = new DoctorArrivalConflict(
                    conflictRoom.RoomId,
                    room.AssignedDoctor,
                    ResolveDoctorDisplayName(room.AssignedDoctor));
                return new DoctorArrivalResult(DoctorArrivalOutcome.Conflict, null, conflict);
            }

            return new DoctorArrivalResult(DoctorArrivalOutcome.Arrived, ApplyDoctorArrived(room, now), null);
        }
    }

    /// <summary>
    /// Resolves a doctor-arrival conflict: marks the old conflicting room Doctor Complete (moving
    /// it to TURNOVER, never to Available) and then marks the current room Doctor Arrived. The
    /// conflict is revalidated against current state first - the client is not trusted. If the
    /// conflict is gone or now points at a different room, no mutation happens and a StaleConflict
    /// outcome is returned so the caller can refresh and retry.
    /// </summary>
    public DoctorArrivalResult ResolveDoctorArrivalConflict(int roomNumber, int conflictingRoomId)
    {
        lock (_syncRoot)
        {
            if (!IsConfiguredRoom(roomNumber))
            {
                return new DoctorArrivalResult(DoctorArrivalOutcome.NotConfigured, null, null);
            }

            var room = PrepareDoctorArrived(roomNumber, out var now);
            if (room is null)
            {
                return new DoctorArrivalResult(DoctorArrivalOutcome.Rejected, null, null);
            }

            // Revalidate the conflict. It must still be the same doctor in the same other room.
            var conflictRoom = FindActiveDoctorRoom(room.AssignedDoctor!, room.RoomId);
            if (conflictRoom is null || conflictRoom.RoomId != conflictingRoomId)
            {
                var staleConflict = conflictRoom is null
                    ? null
                    : new DoctorArrivalConflict(
                        conflictRoom.RoomId,
                        room.AssignedDoctor,
                        ResolveDoctorDisplayName(room.AssignedDoctor));
                return new DoctorArrivalResult(DoctorArrivalOutcome.StaleConflict, null, staleConflict);
            }

            // Complete the old room first. MarkDoctorComplete moves it to TURNOVER only.
            var completed = MarkDoctorComplete(conflictingRoomId);
            if (completed is null)
            {
                return new DoctorArrivalResult(DoctorArrivalOutcome.StaleConflict, null, null);
            }

            return new DoctorArrivalResult(DoctorArrivalOutcome.Arrived, ApplyDoctorArrived(room, now), null);
        }
    }

    // Validates that a room can accept Doctor Arrived and advances any pending automatic state
    // transition. Returns the room when arrivable, otherwise null. Must be called inside _syncRoot.
    private RoomState? PrepareDoctorArrived(int roomNumber, out DateTimeOffset now)
    {
        now = Now;
        var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
        if (room is null)
        {
            return null;
        }

        if (!CanMarkDoctorArrived(room) || room.SeatedAt is null || string.IsNullOrWhiteSpace(room.ActiveReadyHandoffId))
        {
            return null;
        }
        if (DeriveIntegrityFaults(room).Count > 0)
        {
            return null;
        }

        return room;
    }

    // Applies the Doctor Arrived mutation (state, event, cycle creation, persistence) to a room
    // that PrepareDoctorArrived has already validated. Must be called inside _syncRoot.
    private RoomStatus ApplyDoctorArrived(RoomState room, DateTimeOffset now)
    {
        var snapshot = CopyRoomState(room);
        var urgency = ProjectReadyUrgency(snapshot, now);
        var seatedToDoctorSeconds = SecondsBetween(room.SeatedAt!.Value, now);
        var prepSeconds = room.ReadyForDoctorAt.HasValue
            ? SecondsBetween(room.SeatedAt.Value, room.ReadyForDoctorAt.Value)
            : (int?)null;
        var readyToDoctorSeconds = room.ReadyForDoctorAt.HasValue
            ? SecondsBetween(room.ReadyForDoctorAt.Value, now)
            : (int?)null;

        snapshot.DoctorArrivedAt = now;
        snapshot.State = RoomStates.DoctorInRoom;
        var cycle = new CompletedRoomCycle
        {
                RoomId = snapshot.RoomId,
                AssignedDoctor = snapshot.AssignedDoctor ?? "",
                ProcedureCode = snapshot.ProcedureCode ?? "",
                SeatedAt = snapshot.SeatedAt!.Value,
                ReadyForDoctorAt = snapshot.ReadyForDoctorAt,
                DoctorArrivedAt = now,
                EpisodeId = snapshot.EpisodeId,
                PrestageStartedAt = snapshot.PrestageStartedAt,
                SeatedToDoctorSeconds = seatedToDoctorSeconds,
                PrepSeconds = prepSeconds,
                ReadyToDoctorSeconds = readyToDoctorSeconds,
                FinalWaitState = RoomStates.ReadyForDoctor,
                AgingThresholdReached = urgency is ReadyUrgency.Aging or ReadyUrgency.Stale,
                StaleThresholdReached = urgency == ReadyUrgency.Stale,
                OriginalDefaultExpectedUnits = snapshot.OriginalDefaultExpectedUnits,
                ExpectedAllocationUnits = snapshot.ExpectedAllocationUnits,
                ExpectedAllocationMinutes = snapshot.ExpectedAllocationMinutes,
                AllocationAdjustedFromDefault = snapshot.AllocationAdjustedFromDefault
        };
        var committed = _repository.AcceptReadyHandoffAndSaveCycle(
            snapshot,
            cycle,
            snapshot.ActiveReadyHandoffId!,
            now,
            _doctors,
            _procedures);
        ApplyCommittedRoom(room, committed.Room);
        if (committed.CompletedCycle is not null)
        {
            _completedCycles.Add(committed.CompletedCycle);
        }
        AddEvent(new RoomEvent(room.RoomId, "DoctorArrived", now, room.AssignedDoctor, room.ProcedureCode, TimeSpan.FromSeconds(seatedToDoctorSeconds)));
        return ToRoomStatus(room, now);
    }

    // Returns the first room (deterministic by room id) where the given doctor is currently
    // checked in (doctor-in-room), excluding the supplied room. Null when none. Inside _syncRoot.
    private RoomState? FindActiveDoctorRoom(string doctorId, int excludeRoomId) =>
        _rooms
            .Where(item => item.RoomId != excludeRoomId
                && item.State == RoomStates.DoctorInRoom
                && string.Equals(item.AssignedDoctor, doctorId, StringComparison.Ordinal))
            .OrderBy(item => item.RoomId)
            .FirstOrDefault();

    private static bool CanCreateLegacyCompletionCycle(RoomState room) =>
        room.SeatedAt.HasValue
        && room.DoctorArrivedAt.HasValue
        && string.IsNullOrWhiteSpace(room.AcceptedReadyHandoffId)
        && !room.SedationState.HasValue
        && !room.ExpectedAllocationState.HasValue;

    private static CompletedRoomCycle CreateLegacyCompletionCycle(RoomState room) =>
        new()
        {
            EpisodeId = room.EpisodeId,
            RoomId = room.RoomId,
            AssignedDoctor = room.AssignedDoctor ?? string.Empty,
            ProcedureCode = room.ProcedureCode ?? string.Empty,
            PrestageStartedAt = room.PrestageStartedAt,
            SeatedAt = room.SeatedAt!.Value,
            ReadyForDoctorAt = room.ReadyForDoctorAt,
            DoctorArrivedAt = room.DoctorArrivedAt,
            SeatedToDoctorSeconds = SecondsBetween(room.SeatedAt.Value, room.DoctorArrivedAt!.Value),
            PrepSeconds = room.ReadyForDoctorAt.HasValue
                ? SecondsBetween(room.SeatedAt.Value, room.ReadyForDoctorAt.Value)
                : null,
            ReadyToDoctorSeconds = room.ReadyForDoctorAt.HasValue
                ? SecondsBetween(room.ReadyForDoctorAt.Value, room.DoctorArrivedAt.Value)
                : null,
            FinalWaitState = room.State,
            OriginalDefaultExpectedUnits = room.OriginalDefaultExpectedUnits,
            ExpectedAllocationUnits = room.ExpectedAllocationUnits,
            ExpectedAllocationMinutes = room.ExpectedAllocationMinutes,
            AllocationAdjustedFromDefault = room.AllocationAdjustedFromDefault
        };

    private string? ResolveDoctorDisplayName(string? doctorId) =>
        doctorId is null ? null : _doctors.FirstOrDefault(item => item.Id == doctorId)?.Name;

    public RoomStatus? MarkDoctorComplete(int roomNumber)
    {
        lock (_syncRoot)
        {
            var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
            if (room is null || room.State != RoomStates.DoctorInRoom || room.DoctorArrivedAt is null)
            {
                return null;
            }
            if (!CanCreateLegacyCompletionCycle(room) && DeriveIntegrityFaults(room).Count > 0)
            {
                return null;
            }

            var now = Now;
            var cycleIndex = _completedCycles.FindIndex(cycle => cycle.RoomId == room.RoomId && cycle.SeatedAt == room.SeatedAt);
            if (cycleIndex < 0 && !CanCreateLegacyCompletionCycle(room))
            {
                return null;
            }
            var candidateRoom = CopyRoomState(room);
            var candidateCycle = cycleIndex >= 0
                ? CopyCompletedCycle(_completedCycles[cycleIndex])
                : CreateLegacyCompletionCycle(room);
            candidateRoom.DoctorCompleteAt = now;
            candidateRoom.State = RoomStates.Turnover;
            candidateCycle.DoctorCompleteAt = now;
            candidateCycle.DoctorInRoomSeconds = SecondsBetween(room.DoctorArrivedAt.Value, now);
            var committed = _repository.SaveCompletedCycleAndRoom(candidateCycle, candidateRoom, _doctors, _procedures);
            ApplyCommittedRoom(room, committed.Room);
            if (cycleIndex >= 0)
            {
                _completedCycles[cycleIndex] = committed.CompletedCycle!;
            }
            else
            {
                _completedCycles.Add(committed.CompletedCycle!);
            }
            AddEvent(new RoomEvent(room.RoomId, "DoctorComplete", now, room.AssignedDoctor, room.ProcedureCode));
            return ToRoomStatus(room, now);
        }
    }

    public RoomStatus? MarkRoomAvailable(int roomNumber)
    {
        lock (_syncRoot)
        {
            var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
            if (room is null || room.State != RoomStates.Turnover || room.DoctorCompleteAt is null || room.SeatedAt is null)
            {
                return null;
            }
            if (!CanCreateLegacyCompletionCycle(room) && DeriveIntegrityFaults(room).Count > 0)
            {
                return null;
            }

            var now = Now;
            var cycleIndex = _completedCycles.FindIndex(cycle =>
                cycle.RoomId == room.RoomId && cycle.SeatedAt == room.SeatedAt.Value);
            if (cycleIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Cannot mark room {room.RoomId} available: turnover room has no completed-cycle record.");
            }

            var cycle = CopyCompletedCycle(_completedCycles[cycleIndex]);
            cycle.RoomAvailableAt = now;
            cycle.TurnoverSeconds = SecondsBetween(room.DoctorCompleteAt.Value, now);
            cycle.TotalRoomCycleSeconds = SecondsBetween(room.SeatedAt.Value, now);
            var resetRoom = new RoomState(room.RoomId);

            var committed = _repository.SaveCompletedCycleAndRoom(cycle, resetRoom, _doctors, _procedures);

            _completedCycles[cycleIndex] = committed.CompletedCycle!;
            ApplyCommittedRoom(room, committed.Room);
            AddEvent(new RoomEvent(room.RoomId, "RoomAvailable", now, cycle.AssignedDoctor, cycle.ProcedureCode));

            return ToRoomStatus(room, now);
        }
    }

    public ReportsSnapshot GetReports() => GetReports(ReportDateRange.AllTime);

    /// <summary>
    /// Builds the report snapshot over completed cycles whose completion anchor (DoctorCompleteAt -
    /// the established end of the measured case flow) falls within <paramref name="range"/>. The date
    /// filter is applied to the source population first, so every downstream count, hygiene
    /// classification, allocation-variance aggregate, doctor/procedure summary, and the recent-cycle
    /// list all reflect the selected window. An all-time range is a no-op filter (identical to the
    /// historical behavior). Cycles without a DoctorCompleteAt (in-progress or force-expired) have no
    /// completion date and are therefore only present in the unbounded all-time range.
    /// </summary>
    public ReportsSnapshot GetReports(ReportDateRange range)
    {
        lock (_syncRoot)
        {
            // All-time completed total (for "X of Y" context), independent of the selected window.
            var totalCompletedAllTime = _completedCycles.Count(cycle => cycle.RoomAvailableAt is not null);

            var allCycles = _completedCycles
                .Where(cycle => range.Includes(cycle.DoctorCompleteAt))
                .OrderByDescending(cycle => cycle.DoctorArrivedAt)
                .ToList();

            // Classify every cycle for reporting data hygiene (legacy/unmapped procedures,
            // extreme/overnight durations, missing timing). This only annotates derived metadata;
            // it never mutates persisted state or the manual review queue.
            AnnotateReportingExceptions(allCycles);

            // Compute per-cycle measured case flow and allocation variance (derived, never persisted).
            AnnotateAllocationVariance(allCycles);

            // Manual review exceptions are excluded from normal operational metrics by default.
            var normalCycles = allCycles.Where(cycle => !cycle.IsException).ToList();
            // Standard population additionally drops reporting-exception cycles (legacy/unmapped/
            // extreme/overnight) so doctor-facing aggregates are not skewed by sample/legacy data.
            var standardCycles = normalCycles.Where(cycle => !cycle.IsExcludedFromStandardMetrics).ToList();

            // Raw/audit completed set keeps reporting-exception cycles visible (with badges);
            // the standard completed set drives aggregates.
            var normalCompletedCycles = normalCycles
                .Where(cycle => cycle.RoomAvailableAt is not null)
                .ToList();
            var standardCompletedCycles = standardCycles
                .Where(cycle => cycle.RoomAvailableAt is not null)
                .ToList();

            // "Exceptions Requiring Review" is a pending-review queue: only exceptions that still
            // require review appear here. A reviewed exception remains IsException (and therefore
            // excluded from normal metrics) but drops out of this queue once its review is confirmed.
            var exceptionCycles = allCycles
                .Where(cycle => cycle.IsException && cycle.RequiresReview)
                .OrderByDescending(cycle => cycle.SeatedAt)
                .ToList();

            // Annotate the raw display set with doctor-occupied / available wait, but use only the
            // standard population as the blocker pool so an extreme/overnight outlier never distorts
            // another cycle's occupied wait.
            AnnotateOccupiedWait(normalCycles, standardCycles);

            return new ReportsSnapshot(
                normalCompletedCycles.Count,
                AverageSeconds(standardCycles.Select(cycle => (int?)cycle.SeatedToDoctorSeconds)),
                MedianSeconds(standardCycles.Select(cycle => (int?)cycle.SeatedToDoctorSeconds)),
                AverageSeconds(standardCycles.Select(cycle => cycle.PrepSeconds)),
                MedianSeconds(standardCycles.Select(cycle => cycle.PrepSeconds)),
                AverageSeconds(standardCycles.Select(cycle => cycle.ReadyToDoctorSeconds)),
                MedianSeconds(standardCycles.Select(cycle => cycle.ReadyToDoctorSeconds)),
                AverageSeconds(standardCycles.Select(cycle => cycle.DoctorInRoomSeconds)),
                MedianSeconds(standardCycles.Select(cycle => cycle.DoctorInRoomSeconds)),
                AverageSeconds(standardCycles.Select(cycle => cycle.TurnoverSeconds)),
                MedianSeconds(standardCycles.Select(cycle => cycle.TurnoverSeconds)),
                standardCycles.Count(cycle => cycle.AgingThresholdReached),
                standardCycles.Count(cycle => cycle.StaleThresholdReached),
                AverageSeconds(standardCycles.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
                MedianSeconds(standardCycles.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
                AverageSeconds(standardCycles.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
                MedianSeconds(standardCycles.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
                BuildDoctorSummaries(standardCycles),
                normalCompletedCycles.Take(25).ToList(),
                exceptionCycles,
                BuildProcedureSummaries(standardCompletedCycles),
                standardCompletedCycles.Count(cycle => IsSedationProcedureCode(cycle.ProcedureCode)),
                standardCompletedCycles.Count(cycle => !IsSedationProcedureCode(cycle.ProcedureCode)),
                BuildBaseProcedureSummaries(standardCompletedCycles),
                standardCompletedCycles.Count,
                normalCompletedCycles.Count - standardCompletedCycles.Count,
                normalCompletedCycles.Count(cycle => cycle.HasReportingException),
                BuildAllocationVarianceSummary(standardCompletedCycles),
                range.StartDateText,
                range.EndDateText,
                range.Label,
                totalCompletedAllTime,
                BuildDoctorDailyAllocationSeries(standardCycles),
                ScheduleFitReportBuilder.Build(standardCompletedCycles),
                ReportTrendSnapshotBuilder.BuildWeekly(standardCompletedCycles),
                BuildObservedDoctorDays(standardCompletedCycles),
                BuildDoctorProcedureMix(standardCompletedCycles));
        }
    }

    /// <summary>
    /// Maintenance only: clears all completed cycles and resets every active room to Available, then
    /// repopulates clean, deterministic, non-PHI synthetic training data. Taking a timestamped backup
    /// first is the caller's responsibility (the maintenance script). Idempotent - re-running converges
    /// to the same fixture. Returns before/after counts. Destructive execution is gated at the CLI
    /// layer (confirmation token); this method itself is environment-independent for testability.
    /// </summary>
    public MaintenanceResetResult ResetAndSeedSyntheticTrainingData()
    {
        lock (_syncRoot)
        {
            var clearedCompleted = ClearCompletedAndResetRoomsLocked();
            var seed = SeedSyntheticReportData();
            return new MaintenanceResetResult(
                clearedCompleted,
                _rooms.Count,
                seed.CyclesInserted,
                seed.DoctorsRepresented,
                seed.ProcedureFamiliesRepresented,
                seed.ExpectedAllocationCases,
                seed.ExceptionsExpected);
        }
    }

    /// <summary>
    /// Maintenance only: clears all completed cycles and resets every active room to Available for an
    /// official beta go-live. Does NOT seed synthetic data - the board starts empty. Returns
    /// before/after counts.
    /// </summary>
    public MaintenanceResetResult ResetAllDataForEmptyBeta()
    {
        lock (_syncRoot)
        {
            var clearedCompleted = ClearCompletedAndResetRoomsLocked();
            return new MaintenanceResetResult(clearedCompleted, _rooms.Count, 0, 0, 0, 0, 0);
        }
    }

    // Clears persisted + in-memory completed cycles and resets every active room (persisted and
    // in-memory) to Available. Must be called inside _syncRoot. Returns the number of cleared cycles.
    private int ClearCompletedAndResetRoomsLocked()
    {
        var clearedCompleted = _repository.ResetMaintenanceState(_roomCount);
        _completedCycles.Clear();
        foreach (var room in _rooms)
        {
            ResetRoom(room);
        }

        return clearedCompleted;
    }

    /// <summary>
    /// Development/test only: populates a deterministic set of clean, non-PHI completed cycles so
    /// the Reports page can be evaluated with realistic values. Every seeded cycle stays inside the
    /// current reporting rules (mapped/active procedures, full timing, no overnight/extreme records,
    /// expected-allocation snapshot present) so none are flagged as reporting exceptions. Idempotent:
    /// re-running writes the same deterministic set (keyed by room + seated time) without duplicating.
    /// Must never be exposed in Production - the calling endpoint is mapped only in Development.
    /// </summary>
    public SeedReportDataResult SeedSyntheticReportData()
    {
        lock (_syncRoot)
        {
            var doctorIds = _activeDoctors.Select(doctor => doctor.Id).ToList();
            if (doctorIds.Count == 0)
            {
                return new SeedReportDataResult(0, 0, 0, 0, 0);
            }

            // Anchor on today's UTC date and walk back across a fixed history horizon. Every calendar
            // day (including today, even on a weekend) gets a deterministic, front-loaded case count so
            // the report date-range presets are all exercised: more cases recently, fewer further back.
            var today = new DateTimeOffset(Now.UtcDateTime.Date, TimeSpan.Zero);
            var written = 0;
            var doctorsRepresented = new HashSet<string>(StringComparer.Ordinal);
            var familiesRepresented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var globalIndex = 0;

            for (var dayOffset = 0; dayOffset <= SyntheticHistoryDays; dayOffset++)
            {
                var day = today.AddDays(-dayOffset);
                var casesForDay = CasesForDayOffset(dayOffset);
                for (var caseInDay = 0; caseInDay < casesForDay; caseInDay++)
                {
                    // globalIndex advances once per slot (including skipped slots) so the doctor /
                    // family rotation and the deterministic jitter stream are unchanged from before
                    // this was extracted into WriteSyntheticCase.
                    var completed = WriteSyntheticCase(doctorIds, dayOffset, day, caseInDay, globalIndex);
                    globalIndex++;
                    if (completed is { } write)
                    {
                        written++;
                        doctorsRepresented.Add(write.DoctorId);
                        familiesRepresented.Add(write.BaseFamilyCode);
                    }
                }
            }

            return new SeedReportDataResult(
                written,
                doctorsRepresented.Count,
                familiesRepresented.Count,
                written,
                0);
        }
    }

    /// <summary>
    /// Maintenance only: clears all completed cycles and resets every active room to Available, then
    /// seeds a larger deterministic, non-PHI synthetic completed-cycle set (default 1000, clamped to
    /// the CLI-accepted range) so the Reports page can be evaluated at realistic volume. Every seeded
    /// cycle stays inside the standard reporting population (mapped/active procedures, full timing, no
    /// overnight/extreme records, expected-allocation snapshot present), so none are reporting
    /// exceptions. Deterministic: the same requested count always reproduces the same set. Destructive
    /// execution is gated at the CLI layer (confirmation token plus a Production hard-refusal); this
    /// method itself is environment-independent for testability.
    /// </summary>
    public MaintenanceResetResult ResetAndSeedLargeSyntheticReportData(int completedCycleTarget)
    {
        lock (_syncRoot)
        {
            var clearedCompleted = ClearCompletedAndResetRoomsLocked();
            var seed = SeedLargeSyntheticReportData(completedCycleTarget);
            return new MaintenanceResetResult(
                clearedCompleted,
                _rooms.Count,
                seed.CyclesInserted,
                seed.DoctorsRepresented,
                seed.ProcedureFamiliesRepresented,
                seed.ExpectedAllocationCases,
                seed.ExceptionsExpected);
        }
    }

    // Seeds completedCycleTarget clean synthetic completed cycles (clamped to the CLI-accepted range
    // as defense-in-depth). Reuses the exact per-case shaping/persistence as the small training seed
    // via WriteSyntheticCase; the only difference is volume: a flat per-day case cap keeps every seat
    // time inside its own UTC calendar day, and the requested total is spread across as many days
    // back as needed. Only successful writes count toward the target, so a roster with an unmapped
    // family still converges. Caller (ResetAndSeedLargeSyntheticReportData) already holds _syncRoot.
    private SeedReportDataResult SeedLargeSyntheticReportData(int completedCycleTarget)
    {
        var doctorIds = _activeDoctors.Select(doctor => doctor.Id).ToList();
        if (doctorIds.Count == 0)
        {
            return new SeedReportDataResult(0, 0, 0, 0, 0);
        }

        var target = Math.Clamp(
            completedCycleTarget,
            MaintenanceCommands.MinCompletedCycles,
            MaintenanceCommands.MaxCompletedCycles);

        var today = new DateTimeOffset(Now.UtcDateTime.Date, TimeSpan.Zero);
        var written = 0;
        var doctorsRepresented = new HashSet<string>(StringComparer.Ordinal);
        var familiesRepresented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalIndex = 0;

        // Safety bound: one day per requested cycle is an unreachable-in-practice ceiling (given the
        // flat per-day cap) that still guarantees termination if no family maps to an active procedure.
        for (var dayOffset = 0; written < target && dayOffset <= target; dayOffset++)
        {
            var day = today.AddDays(-dayOffset);
            for (var caseInDay = 0; caseInDay < LargeSyntheticCasesPerDay && written < target; caseInDay++)
            {
                var completed = WriteSyntheticCase(doctorIds, dayOffset, day, caseInDay, globalIndex);
                globalIndex++;
                if (completed is { } write)
                {
                    written++;
                    doctorsRepresented.Add(write.DoctorId);
                    familiesRepresented.Add(write.BaseFamilyCode);
                }
            }
        }

        return new SeedReportDataResult(
            written,
            doctorsRepresented.Count,
            familiesRepresented.Count,
            written,
            0);
    }

    // Deterministically shapes and upserts one clean synthetic completed cycle for the given day /
    // case slot, returning the doctor id and base procedure family written (for coverage
    // bookkeeping), or null when the rotated family has no active procedure (the slot is skipped).
    // Shared by both the small training seed and the large report-data seed; the jitter draw order
    // here is load-bearing (existing small-seed output is pinned by tests).
    private (string DoctorId, string BaseFamilyCode)? WriteSyntheticCase(
        IReadOnlyList<string> doctorIds,
        int dayOffset,
        DateTimeOffset day,
        int caseInDay,
        int globalIndex)
    {
        // Round-robin doctors so even a small window (e.g. Today) represents all four; the per-doctor
        // style profile is aligned to the doctor.
        var doctorIndex = globalIndex % doctorIds.Count;
        var profile = SyntheticProfiles[doctorIndex % SyntheticProfiles.Count];
        var doctorId = doctorIds[doctorIndex];

        // Deterministic pseudo-randomness: the jitter stream is seeded from stable inputs (day offset,
        // doctor, case-in-day), so the same slot always reproduces the same cycle.
        var jitter = new SyntheticJitter(DeterministicSeed(dayOffset, doctorIndex, caseInDay));

        // Rotate procedure families across the whole dataset to guarantee coverage.
        var family = SyntheticFamilies[globalIndex % SyntheticFamilies.Count];
        var procedure = FindActiveProcedure(family.Code);
        if (procedure is null)
        {
            return null;
        }

        var sedation = family.SedationEligible
            && procedure.SedationEligible
            && jitter.Next(0, 99) < profile.SedationChancePercent;
        var storedCode = ComposeProcedureCode(procedure.Code, sedation);

        // Expected allocation: roster default, nudged by doctor style (generous vs tight) on variable
        // families, plus a small bump for sedation burden.
        var defaultUnits = Math.Clamp(procedure.DefaultExpectedUnits, MinExpectedUnits, MaxExpectedUnits);
        var unitDelta = family.Code is "EXT" or "IMP" ? profile.VariableUnitDelta : 0;
        if (sedation)
        {
            unitDelta += 1;
        }
        var expectedUnits = Math.Clamp(defaultUnits + unitDelta, MinExpectedUnits, MaxExpectedUnits);
        var expectedMinutes = expectedUnits * 10;

        // Measured case flow: doctor bias + (family character * profile weight) + jitter, clamped to a
        // realistic per-family range. Always draw the jitter so the stream stays identical whether or
        // not this case is forced to land at expected.
        var minFlow = sedation ? family.MinFlowMinutes + 15 : family.MinFlowMinutes;
        var maxFlow = sedation ? family.MaxFlowMinutes + 15 : family.MaxFlowMinutes;
        var varianceJitter = jitter.Next(-profile.VarianceSpread, profile.VarianceSpread);
        int measuredMinutes;
        if (globalIndex % 9 == 0)
        {
            // Guarantee some exactly-at-expected cases for the neutral variance example.
            measuredMinutes = Math.Clamp(expectedMinutes, minFlow, maxFlow);
        }
        else
        {
            var lean = family.CharacterLeanMinutes * profile.FamilyLeanWeight;
            measuredMinutes = Math.Clamp(expectedMinutes + profile.VarianceBiasMinutes + lean + varianceJitter, minFlow, maxFlow);
        }

        // Split measured case flow into prep / ready / doctor minutes (doctor >= 1) so the
        // timestamp-derived measured flow exactly equals measuredMinutes. Seat hours stay within the
        // day (8am + case index) so no cycle crosses a UTC calendar day.
        var prepMin = Math.Clamp(measuredMinutes * 12 / 100, 2, 12);
        var readyMin = Math.Clamp(measuredMinutes * 18 / 100, 2, 20);
        if (prepMin + readyMin >= measuredMinutes)
        {
            prepMin = Math.Max(1, measuredMinutes / 4);
            readyMin = Math.Max(1, measuredMinutes / 4);
        }
        var doctorMin = Math.Max(1, measuredMinutes - prepMin - readyMin);
        var turnoverMin = jitter.Next(4, 12);

        var roomId = 1 + (caseInDay % Math.Max(1, _roomCount));
        var seatedAt = day.AddHours(8 + caseInDay).AddMinutes(jitter.Next(0, 11) * 5);

        UpsertSyntheticCycle(
            roomId, doctorId, storedCode, seatedAt,
            prepMin, readyMin, doctorMin, turnoverMin, defaultUnits, expectedUnits);

        return (doctorId, ResolveBaseProcedureCode(storedCode));
    }

    // Writes (insert or deterministic update) one clean synthetic completed cycle. Keyed by
    // (room, seated time) so re-running on the same day never duplicates records.
    private void UpsertSyntheticCycle(
        int roomId,
        string doctorId,
        string storedCode,
        DateTimeOffset seatedAt,
        int prepMin,
        int readyMin,
        int doctorMin,
        int turnoverMin,
        int defaultUnits,
        int expectedUnits)
    {
        var readyAt = seatedAt.AddMinutes(prepMin);
        var arrivedAt = readyAt.AddMinutes(readyMin);
        var completeAt = arrivedAt.AddMinutes(doctorMin);
        var availableAt = completeAt.AddMinutes(turnoverMin);

        var cycle = _completedCycles.FirstOrDefault(item => item.RoomId == roomId && item.SeatedAt == seatedAt);
        var isNew = cycle is null;
        cycle ??= new CompletedRoomCycle();

        cycle.RoomId = roomId;
        cycle.AssignedDoctor = doctorId;
        cycle.ProcedureCode = storedCode;
        cycle.SeatedAt = seatedAt;
        cycle.ReadyForDoctorAt = readyAt;
        cycle.DoctorArrivedAt = arrivedAt;
        cycle.DoctorCompleteAt = completeAt;
        cycle.RoomAvailableAt = availableAt;
        cycle.SeatedToDoctorSeconds = (prepMin + readyMin) * 60;
        cycle.PrepSeconds = prepMin * 60;
        cycle.ReadyToDoctorSeconds = readyMin * 60;
        cycle.DoctorInRoomSeconds = doctorMin * 60;
        cycle.TurnoverSeconds = turnoverMin * 60;
        cycle.TotalRoomCycleSeconds = (prepMin + readyMin + doctorMin + turnoverMin) * 60;
        cycle.FinalWaitState = RoomStates.ReadyForDoctor;
        cycle.AgingThresholdReached = false;
        cycle.StaleThresholdReached = false;
        cycle.OriginalDefaultExpectedUnits = defaultUnits;
        cycle.ExpectedAllocationUnits = expectedUnits;
        cycle.ExpectedAllocationMinutes = expectedUnits * 10;
        cycle.AllocationAdjustedFromDefault = expectedUnits != defaultUnits;
        // Synthetic data is clean: never a manual exception.
        cycle.IsException = false;
        cycle.RequiresReview = false;
        cycle.ExceptionReason = null;
        cycle.ReviewStatus = ReviewStatuses.PendingReview;
        cycle.SuggestedAction = null;
        cycle.ReviewedAt = null;
        cycle.ReviewedBy = null;

        if (isNew)
        {
            _completedCycles.Add(cycle);
        }

        PersistCycle(cycle);
    }

    // ---------------------------------------------------------------------------
    // Deterministic stress fixtures (maintenance-only, additive). These profiles never modify
    // WriteSyntheticCase, UpsertSyntheticCycle, or the existing seeders above; they compose the
    // same private helpers (FindActiveProcedure, ApplyExpectedAllocation, ComposeProcedureCode,
    // the threshold-relative Aging/Stale/EarlySeated samples, and - for
    // scenario-rich - WriteSyntheticCase itself) plus a small set of new, purpose-built builders.
    // See MaintenanceCommands.StressFixtureProfiles for the accepted --profile values.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Maintenance only: clears all completed cycles and resets every active room to Available, then
    /// seeds the requested deterministic stress-fixture profile. reporting-volume delegates to the
    /// existing large synthetic seeder; the live-room profiles seed a fixed room allocation table
    /// (including terminal DoctorInRoom/Turnover states, which also get a paired in-progress
    /// completed-cycle row so a later manual Doctor Complete/Room Available click on a seeded room
    /// still updates reporting data correctly); scenario-rich seeds an extended clean history plus a
    /// small set of explicit edge-case cycles; full-stress composes the live-room and scenario-rich
    /// builders with no additional bespoke logic. Deterministic: the same profile on a fixed clock
    /// always reproduces the same fixture. Destructive execution is gated at the CLI layer
    /// (confirmation token plus a Production hard-refusal); this method itself is environment-
    /// independent for testability.
    /// </summary>
    public StressFixtureResult ResetAndSeedStressFixture(string profile, int? completedCycles)
    {
        lock (_syncRoot)
        {
            var now = Now;
            var clearedCompleted = ClearCompletedAndResetRoomsLocked();

            var cyclesSeeded = 0;
            var doctorsRepresented = 0;
            var proceduresRepresented = 0;

            switch (profile)
            {
                case MaintenanceCommands.ProfileReportingVolume:
                {
                    var seed = SeedLargeSyntheticReportData(completedCycles ?? MaintenanceCommands.DefaultCompletedCycles);
                    cyclesSeeded = seed.CyclesInserted;
                    doctorsRepresented = seed.DoctorsRepresented;
                    proceduresRepresented = seed.ProcedureFamiliesRepresented;
                    break;
                }

                case MaintenanceCommands.ProfileLiveBoardStress:
                    SeedLiveRoomFixtures(LiveBoardStressFixtures(), now);
                    break;

                case MaintenanceCommands.ProfileDoctorViewStress:
                    SeedLiveRoomFixtures(DoctorViewStressFixtures(), now);
                    break;

                case MaintenanceCommands.ProfileDoctorViewOverflowStress:
                    SeedLiveRoomFixtures(DoctorViewOverflowStressFixtures(), now);
                    break;

                case MaintenanceCommands.ProfileScenarioRich:
                {
                    var seed = SeedScenarioRichHistory();
                    var edgeCasesWritten = SeedScenarioRichEdgeCases(now);
                    cyclesSeeded = seed.CyclesInserted + edgeCasesWritten;
                    doctorsRepresented = seed.DoctorsRepresented;
                    proceduresRepresented = seed.ProcedureFamiliesRepresented;
                    break;
                }

                case MaintenanceCommands.ProfileFullStress:
                {
                    // Composition only: the same live-room runner (with an overflow-shaped fixture
                    // table) plus the same scenario-rich history/edge-case builders used above. No
                    // bespoke seeding logic is introduced for this profile.
                    SeedLiveRoomFixtures(FullStressLiveFixtures(), now);
                    var seed = SeedScenarioRichHistory();
                    var edgeCasesWritten = SeedScenarioRichEdgeCases(now);
                    cyclesSeeded = seed.CyclesInserted + edgeCasesWritten;
                    doctorsRepresented = seed.DoctorsRepresented;
                    proceduresRepresented = seed.ProcedureFamiliesRepresented;
                    break;
                }

                case MaintenanceCommands.ProfileAllScenarios:
                    // Composition only: the same live-room runner (full-stress's overflow-shaped
                    // fixture table, so Otte = 5), the existing large-synthetic seeder unmodified,
                    // and the same scenario-rich history/edge-case builders used above - no bespoke
                    // seeding logic. The bulk scenario-rich history is shifted by
                    // AllScenariosHistoryDayOffsetShift so it can never land on the same calendar
                    // days as the large-synthetic seed (see that constant's comment). cyclesSeeded/
                    // doctorsRepresented/proceduresRepresented are intentionally left at 0 here and
                    // computed from the ground-truth persisted completed cycles below instead of
                    // summed from sub-seeder self-reports, so the summary is accurate regardless.
                    SeedLiveRoomFixtures(FullStressLiveFixtures(), now);
                    SeedLargeSyntheticReportData(completedCycles ?? MaintenanceCommands.DefaultCompletedCycles);
                    SeedScenarioRichHistory(AllScenariosHistoryDayOffsetShift);
                    SeedScenarioRichEdgeCases(now);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown stress fixture profile '{profile}'.");
            }

            // Re-derive reporting-exception metadata for the summary (mirrors what GetReports does at
            // read time; never persisted). Harmless for profiles that seed no history: annotating an
            // empty or in-progress-only cycle set simply yields no reasons/candidates.
            AnnotateReportingExceptions(_completedCycles);

            // Room state counts include AVAILABLE, so live-board-stress/full-stress can report their
            // intentionally unassigned no-coin room. Doctor counts stay assigned-only.
            var roomStateCounts = _rooms
                .GroupBy(room => room.State, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            var activeRoomDoctorCounts = _rooms
                .Where(room => room.AssignedDoctor is not null)
                .GroupBy(room => room.AssignedDoctor!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            // Distinguish genuinely completed historical cycles (RoomAvailableAt set) from the
            // in-progress rows paired with a directly-seeded DoctorInRoom/Turnover room (RoomAvailableAt
            // still null - Room Available has not been "clicked" yet). Only completed cycles count as
            // seeded history: they drive the derived-exception/manual-audit counts and the history date
            // range, so an in-progress live room never inflates or skews those numbers.
            var completedHistoryCycles = _completedCycles.Where(cycle => cycle.RoomAvailableAt is not null).ToList();
            var inProgressCycleRowsSeeded = _completedCycles.Count - completedHistoryCycles.Count;

            if (string.Equals(profile, MaintenanceCommands.ProfileAllScenarios, StringComparison.Ordinal))
            {
                // Ground truth, not a sum of sub-seeder self-reports: the large-synthetic seed and
                // the scenario-rich history both write through the same WriteSyntheticCase/(RoomId,
                // SeatedAt) upsert key, so a self-reported sum could overcount if any two writes ever
                // targeted the same slot. Counting/deriving directly from the persisted completed
                // cycles is correct regardless. SeedReportDataResult only exposes represented-doctor/
                // family *counts* (not the sets themselves), so a true cross-seeder union is not
                // available without a broader return-type change; deriving both numbers directly from
                // completedHistoryCycles avoids that and is exact.
                cyclesSeeded = completedHistoryCycles.Count;
                doctorsRepresented = completedHistoryCycles
                    .Select(cycle => cycle.AssignedDoctor)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                proceduresRepresented = completedHistoryCycles
                    .Select(cycle => ResolveBaseProcedureCode(cycle.ProcedureCode))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
            }

            var derivedExceptionReasonCounts = completedHistoryCycles
                .SelectMany(cycle => cycle.ReportingExceptionReasons)
                .GroupBy(reason => reason, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            var manualAuditCandidates = completedHistoryCycles.Count(cycle => cycle.IsException && cycle.RequiresReview);

            DateTimeOffset? earliestSeatedAt = completedHistoryCycles.Count == 0 ? null : completedHistoryCycles.Min(cycle => cycle.SeatedAt);
            DateTimeOffset? latestSeatedAt = completedHistoryCycles.Count == 0 ? null : completedHistoryCycles.Max(cycle => cycle.SeatedAt);

            return new StressFixtureResult(
                profile,
                clearedCompleted,
                _rooms.Count,
                cyclesSeeded,
                doctorsRepresented,
                proceduresRepresented,
                roomStateCounts,
                activeRoomDoctorCounts,
                derivedExceptionReasonCounts,
                manualAuditCandidates,
                inProgressCycleRowsSeeded,
                earliestSeatedAt,
                latestSeatedAt);
        }
    }

    // --- live-room fixture profiles -------------------------------------------------------------

    // Fills every listed room from a fixed allocation table. A fixture with no doctor/procedure (or
    // an explicit Available target) resets that room to Available; every other fixture builds a
    // fully consistent RoomState for the requested target state via BuildLiveRoom. Room ids beyond
    // the configured RoomCount are skipped rather than failing, so a fixture table sized for 12
    // rooms degrades safely under a smaller configured room count. One bulk SaveRooms at the end
    // mirrors the constructor's own demo-seeding persistence pattern.
    private void SeedLiveRoomFixtures(IReadOnlyList<LiveRoomFixture> fixtures, DateTimeOffset now)
    {
        foreach (var fixture in fixtures)
        {
            var index = _rooms.FindIndex(room => room.RoomId == fixture.RoomId);
            if (index < 0)
            {
                continue;
            }

            if (fixture.DoctorId is null
                || fixture.ProcedureCode is null
                || string.Equals(fixture.TargetState, RoomStates.Available, StringComparison.Ordinal))
            {
                ResetRoom(_rooms[index]);
                continue;
            }

            var room = BuildLiveRoom(fixture.RoomId, fixture.DoctorId, fixture.ProcedureCode, fixture.Sedation, fixture.TargetState, now);
            if (IsReadyFixtureTarget(fixture.TargetState))
            {
                var handoff = _repository.CreateReadyHandoff(
                    room,
                    GetCanonicalAssignment(room).ToContract(),
                    room.ReadyForDoctorAt!.Value,
                    _doctors,
                    _procedures);
                room.ActiveReadyHandoffId = handoff.HandoffId;
            }

            _rooms[index] = room;
        }

        _repository.SaveRooms(_rooms, _doctors, _procedures);
    }

    // Builds one fully consistent active room for a stress fixture. Seated and ReadyForDoctor
    // timestamps are threshold-relative (via the same EarlySeatedSample/AgingSample/StaleSample
    // helpers the first-run demo seed uses), so ReadyUrgency projects correctly regardless of
    // configured AgingMinutes/StaleMinutes while the primary state remains ReadyForDoctor.
    // DoctorInRoom/Turnover timestamps are set directly
    // from small fixed "recent" offsets - deterministic and stable across expiration/recompute
    // (well under the default 8-hour max-active-duration sweep) - and a matching in-progress
    // completed-cycle row is upserted so the room's reporting bookkeeping stays consistent with the
    // real lifecycle (see UpsertLiveCompletedCycle).
    private RoomState BuildLiveRoom(int roomId, string doctorId, string procedureCode, bool sedation, string targetState, DateTimeOffset now)
    {
        var thresholds = Thresholds;
        var procedure = FindActiveProcedure(procedureCode)
            ?? throw new InvalidOperationException($"Stress fixture procedure '{procedureCode}' is not active.");
        var storedCode = ComposeProcedureCode(procedure.Code, sedation);

        var room = new RoomState(roomId)
        {
            AssignedDoctor = doctorId,
            ProcedureCode = storedCode
        };
        ApplyExpectedAllocation(room, procedure, expectedAllocationUnits: null);

        switch (targetState)
        {
            case RoomStates.Seated:
                room.SeatedAt = now - EarlySeatedSample(thresholds);
                room.State = RoomStates.Seated;
                break;

            case RoomStates.ReadyForDoctor:
            {
                room.State = RoomStates.ReadyForDoctor;
                var readyElapsed = EarlySeatedSample(thresholds);
                room.ReadyForDoctorAt = now - readyElapsed;
                room.SeatedAt = now - (readyElapsed + StressLiveSeatToReadyPad);
                break;
            }

            case RoomStates.Aging:
            {
                room.State = RoomStates.ReadyForDoctor;
                var readyElapsed = AgingSample(thresholds);
                room.ReadyForDoctorAt = now - readyElapsed;
                room.SeatedAt = now - (readyElapsed + StressLiveSeatToReadyPad);
                break;
            }

            case RoomStates.Stale:
            {
                room.State = RoomStates.ReadyForDoctor;
                var readyElapsed = StaleSample(thresholds);
                room.ReadyForDoctorAt = now - readyElapsed;
                room.SeatedAt = now - (readyElapsed + StressLiveSeatToReadyPad);
                break;
            }

            case RoomStates.DoctorInRoom:
                room.SeatedAt = now - StressLiveDoctorInRoomSeatedElapsed;
                room.ReadyForDoctorAt = now - StressLiveDoctorInRoomReadyElapsed;
                room.DoctorArrivedAt = now - StressLiveDoctorInRoomArrivedElapsed;
                room.State = RoomStates.DoctorInRoom;
                break;

            case RoomStates.Turnover:
                room.SeatedAt = now - StressLiveTurnoverSeatedElapsed;
                room.ReadyForDoctorAt = now - StressLiveTurnoverReadyElapsed;
                room.DoctorArrivedAt = now - StressLiveTurnoverArrivedElapsed;
                room.DoctorCompleteAt = now - StressLiveTurnoverCompleteElapsed;
                room.State = RoomStates.Turnover;
                break;

            default:
                throw new InvalidOperationException($"Stress fixture target state '{targetState}' is not a supported live-room target state.");
        }

        if (IsReadyFixtureTarget(targetState))
        {
            var doctor = _activeDoctors.FirstOrDefault(item => item.Id == doctorId)
                ?? throw new InvalidOperationException($"Stress fixture doctor '{doctorId}' is not active.");
            var assignment = CreateLegacyCompatibleAssignment(doctor, procedure, sedation, expectedAllocationUnits: null);
            var persistedAssignment = PersistedRoomAssignment.FromCanonicalContract(
                assignment,
                doctor.Name,
                ResolveProcedure(assignment.ProcedureCode)?.Label);
            ApplyPersistedAssignment(room, persistedAssignment);
            room.EpisodeId = NewEpisodeId();
            room.PrestageStartedAt = room.SeatedAt!.Value - StressLiveSeatToReadyPad;
            room.State = RoomStates.ReadyForDoctor;
            room.AgingStartedAt = null;
            room.StaleStartedAt = null;
        }
        if (room.State is RoomStates.DoctorInRoom or RoomStates.Turnover)
        {
            UpsertLiveCompletedCycle(room);
        }

        return room;
    }

    private static bool IsReadyFixtureTarget(string targetState) =>
        targetState is RoomStates.ReadyForDoctor or RoomStates.Aging or RoomStates.Stale;

    // Small fixed "recent" elapsed offsets for the two terminal live-room target states.
    // These do not need to be threshold-relative like the Ready fixture cases above - they only need to
    // stay well inside the default 8-hour room-expiration ceiling, which minutes-scale offsets do.
    private static readonly TimeSpan StressLiveSeatToReadyPad = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StressLiveDoctorInRoomSeatedElapsed = TimeSpan.FromMinutes(40);
    private static readonly TimeSpan StressLiveDoctorInRoomReadyElapsed = TimeSpan.FromMinutes(32);
    private static readonly TimeSpan StressLiveDoctorInRoomArrivedElapsed = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan StressLiveTurnoverSeatedElapsed = TimeSpan.FromMinutes(70);
    private static readonly TimeSpan StressLiveTurnoverReadyElapsed = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan StressLiveTurnoverArrivedElapsed = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan StressLiveTurnoverCompleteElapsed = TimeSpan.FromMinutes(8);

    // Upserts the in-progress completed-cycle row paired with a directly-seeded DoctorInRoom/Turnover
    // room, mirroring the fields ApplyDoctorArrived/MarkDoctorComplete would have set through the real
    // lifecycle (RoomAvailableAt stays null - Room Available has not been clicked yet). Without this,
    // a later manual Doctor Complete/Room Available click on a seeded room would have no matching
    // lifecycle row to update for that RoomId/SeatedAt.
    private void UpsertLiveCompletedCycle(RoomState room)
    {
        var seatedAt = room.SeatedAt!.Value;

        // FinalWaitState/Aging/StaleThresholdReached are set once, at arrival, by ApplyDoctorArrived
        // and are never revisited by MarkDoctorComplete/MarkRoomAvailable - so a directly-seeded room
        // must compute the same values up front from its own ReadyForDoctorAt -> DoctorArrivedAt gap
        // (see ComputeArrivalWaitState), not assume a fixed pre-escalation state.
        var (finalWaitState, agingThresholdReached, staleThresholdReached) = room.ReadyForDoctorAt.HasValue
            ? ComputeArrivalWaitState(room.ReadyForDoctorAt.Value, room.DoctorArrivedAt!.Value)
            : (RoomStates.Seated, false, false);

        UpsertExplicitCompletedCycle(new CompletedRoomCycle
        {
            RoomId = room.RoomId,
            AssignedDoctor = room.AssignedDoctor!,
            ProcedureCode = room.ProcedureCode!,
            SeatedAt = seatedAt,
            ReadyForDoctorAt = room.ReadyForDoctorAt,
            DoctorArrivedAt = room.DoctorArrivedAt,
            DoctorCompleteAt = room.DoctorCompleteAt,
            RoomAvailableAt = null,
            SeatedToDoctorSeconds = SecondsBetween(seatedAt, room.DoctorArrivedAt!.Value),
            PrepSeconds = room.ReadyForDoctorAt.HasValue ? SecondsBetween(seatedAt, room.ReadyForDoctorAt.Value) : null,
            ReadyToDoctorSeconds = room.ReadyForDoctorAt.HasValue ? SecondsBetween(room.ReadyForDoctorAt.Value, room.DoctorArrivedAt.Value) : null,
            DoctorInRoomSeconds = room.DoctorCompleteAt.HasValue ? SecondsBetween(room.DoctorArrivedAt.Value, room.DoctorCompleteAt.Value) : null,
            TurnoverSeconds = null,
            TotalRoomCycleSeconds = null,
            FinalWaitState = finalWaitState,
            AgingThresholdReached = agingThresholdReached,
            StaleThresholdReached = staleThresholdReached,
            OriginalDefaultExpectedUnits = room.OriginalDefaultExpectedUnits,
            ExpectedAllocationUnits = room.ExpectedAllocationUnits,
            ExpectedAllocationMinutes = room.ExpectedAllocationMinutes,
            AllocationAdjustedFromDefault = room.AllocationAdjustedFromDefault,
            IsException = false,
            RequiresReview = false,
            ExceptionReason = null,
            ReviewStatus = ReviewStatuses.PendingReview,
            SuggestedAction = null,
            ReviewedAt = null,
            ReviewedBy = null
        });
    }

    // Computes the Ready urgency and threshold flags at DoctorArrivedAt from the same
    // elapsed-vs-threshold comparison used by the active projection (DoctorArrivedAt -
    // ReadyForDoctorAt). AgingThresholdReached is true whenever Stale is also reached.
    private (string FinalWaitState, bool AgingThresholdReached, bool StaleThresholdReached) ComputeArrivalWaitState(
        DateTimeOffset readyForDoctorAt, DateTimeOffset doctorArrivedAt)
    {
        var thresholds = Thresholds;
        var elapsed = doctorArrivedAt - readyForDoctorAt;
        if (elapsed >= thresholds.StaleThreshold)
        {
            return (RoomStates.Stale, true, true);
        }

        if (elapsed >= thresholds.AgingThreshold)
        {
            return (RoomStates.Aging, true, false);
        }

        return (RoomStates.ReadyForDoctor, false, false);
    }

    // Shared upsert (by RoomId + SeatedAt, matching every other cycle-writer in this file) for
    // explicitly-constructed completed cycles: the live-room in-progress pairing above, and the
    // scenario-rich clean/edge-case cycles below. Returns the stored cycle reference so callers (e.g.
    // the manual-audit candidate) can chain a follow-up mutation against the same persisted object.
    private CompletedRoomCycle UpsertExplicitCompletedCycle(CompletedRoomCycle template)
    {
        var cycle = _completedCycles.FirstOrDefault(item => item.RoomId == template.RoomId && item.SeatedAt == template.SeatedAt);
        var isNew = cycle is null;
        cycle ??= new CompletedRoomCycle();

        cycle.RoomId = template.RoomId;
        cycle.AssignedDoctor = template.AssignedDoctor;
        cycle.ProcedureCode = template.ProcedureCode;
        cycle.SeatedAt = template.SeatedAt;
        cycle.ReadyForDoctorAt = template.ReadyForDoctorAt;
        cycle.DoctorArrivedAt = template.DoctorArrivedAt;
        cycle.DoctorCompleteAt = template.DoctorCompleteAt;
        cycle.RoomAvailableAt = template.RoomAvailableAt;
        cycle.SeatedToDoctorSeconds = template.SeatedToDoctorSeconds;
        cycle.PrepSeconds = template.PrepSeconds;
        cycle.ReadyToDoctorSeconds = template.ReadyToDoctorSeconds;
        cycle.DoctorInRoomSeconds = template.DoctorInRoomSeconds;
        cycle.TurnoverSeconds = template.TurnoverSeconds;
        cycle.TotalRoomCycleSeconds = template.TotalRoomCycleSeconds;
        cycle.FinalWaitState = template.FinalWaitState;
        cycle.AgingThresholdReached = template.AgingThresholdReached;
        cycle.StaleThresholdReached = template.StaleThresholdReached;
        cycle.OriginalDefaultExpectedUnits = template.OriginalDefaultExpectedUnits;
        cycle.ExpectedAllocationUnits = template.ExpectedAllocationUnits;
        cycle.ExpectedAllocationMinutes = template.ExpectedAllocationMinutes;
        cycle.AllocationAdjustedFromDefault = template.AllocationAdjustedFromDefault;
        cycle.IsException = template.IsException;
        cycle.RequiresReview = template.RequiresReview;
        cycle.ExceptionReason = template.ExceptionReason;
        cycle.ReviewStatus = template.ReviewStatus;
        cycle.SuggestedAction = template.SuggestedAction;
        cycle.ReviewedAt = template.ReviewedAt;
        cycle.ReviewedBy = template.ReviewedBy;

        if (isNew)
        {
            _completedCycles.Add(cycle);
        }

        PersistCycle(cycle);
        return cycle;
    }

    // live-board-stress: all 12 rooms filled, every one of the 7 room states present at least once,
    // one intentionally unassigned Available room (no doctor coin), two sedation cases, and one
    // long-label procedure (PCOC) to stress card text. Mixed doctors throughout.
    private static IReadOnlyList<LiveRoomFixture> LiveBoardStressFixtures() =>
    [
        new(1, null, null, RoomStates.Available),
        new(2, "otte", "CON", RoomStates.Seated),
        new(3, "pledger", "EXT", RoomStates.ReadyForDoctor),
        new(4, "gibson", "IMPRES", RoomStates.Aging),
        new(5, "schroeder", "BX", RoomStates.Stale, Sedation: true),
        new(6, "otte", "IMP", RoomStates.DoctorInRoom),
        new(7, "pledger", "POST", RoomStates.Turnover),
        new(8, "gibson", "PCOC", RoomStates.Seated),
        new(9, "schroeder", "EXT", RoomStates.ReadyForDoctor, Sedation: true),
        new(10, "otte", "BX", RoomStates.Aging),
        new(11, "pledger", "IMP", RoomStates.Stale),
        new(12, "gibson", "MISC", RoomStates.DoctorInRoom)
    ];

    // doctor-view-stress: fixed 1/3/4/4 active-room split across all four doctors (Otte 1, Pledger 3,
    // Gibson 4, Schroeder 4 = 12), every counted room in a pre-arrival primary state (Seated or
    // ReadyForDoctor, with threshold-relative urgency) so each Doctor View posture count is exact - see the assignment-based
    // current-room-frame filter documented in docs/knowledge/ui/doctor-view-operational-header.md.
    private static IReadOnlyList<LiveRoomFixture> DoctorViewStressFixtures() =>
    [
        new(1, "otte", "CON", RoomStates.Seated),
        new(2, "pledger", "CON", RoomStates.Seated),
        new(3, "pledger", "EXT", RoomStates.ReadyForDoctor),
        new(4, "pledger", "IMP", RoomStates.Aging),
        new(5, "gibson", "CON", RoomStates.Seated),
        new(6, "gibson", "EXT", RoomStates.ReadyForDoctor),
        new(7, "gibson", "IMP", RoomStates.Aging),
        new(8, "gibson", "BX", RoomStates.Stale),
        new(9, "schroeder", "CON", RoomStates.Seated),
        new(10, "schroeder", "EXT", RoomStates.ReadyForDoctor),
        new(11, "schroeder", "IMP", RoomStates.Aging),
        new(12, "schroeder", "BX", RoomStates.Stale)
    ];

    // doctor-view-overflow-stress: one doctor (Otte) at 5 active rooms - the named overflow default,
    // an odd count so the two-column posture ends in a ragged final row (the higher-risk visual
    // case). Remaining doctors at 3/2/2 = 12 total, incidentally also covering the 3-room and
    // 2-room postures. All counted rooms stay pre-arrival for the same reason as doctor-view-stress.
    private static IReadOnlyList<LiveRoomFixture> DoctorViewOverflowStressFixtures() =>
    [
        new(1, "otte", "CON", RoomStates.Seated),
        new(2, "otte", "EXT", RoomStates.Seated),
        new(3, "otte", "IMP", RoomStates.ReadyForDoctor),
        new(4, "otte", "BX", RoomStates.Aging),
        new(5, "otte", "POST", RoomStates.Stale),
        new(6, "pledger", "CON", RoomStates.Seated),
        new(7, "pledger", "EXT", RoomStates.ReadyForDoctor),
        new(8, "pledger", "IMP", RoomStates.Aging),
        new(9, "gibson", "CON", RoomStates.Seated),
        new(10, "gibson", "EXT", RoomStates.ReadyForDoctor),
        new(11, "schroeder", "CON", RoomStates.Seated),
        new(12, "schroeder", "EXT", RoomStates.ReadyForDoctor)
    ];

    // full-stress live-room component: renders all 12 room cards (11 assigned/active rooms plus 1
    // intentionally unassigned Available room) - not "all 12 active". Reuses the doctor-view-overflow
    // shape (Otte = 5, all pre-arrival) plus the master-board IN ROOM/TURNOVER states so the same
    // fixture also exercises live-board-stress's state coverage. Doctor split: Otte 5, Gibson 2,
    // Pledger 2, Schroeder 2, unassigned 1.
    private static IReadOnlyList<LiveRoomFixture> FullStressLiveFixtures() =>
    [
        new(1, null, null, RoomStates.Available),
        new(2, "otte", "CON", RoomStates.Seated),
        new(3, "otte", "EXT", RoomStates.Seated),
        new(4, "otte", "IMP", RoomStates.ReadyForDoctor),
        new(5, "otte", "BX", RoomStates.Aging),
        new(6, "otte", "POST", RoomStates.Stale),
        new(7, "gibson", "IMPRES", RoomStates.DoctorInRoom),
        new(8, "pledger", "POST", RoomStates.Turnover),
        new(9, "schroeder", "PCOC", RoomStates.ReadyForDoctor),
        new(10, "pledger", "MISC", RoomStates.Aging),
        new(11, "gibson", "EXT", RoomStates.Stale, Sedation: true),
        new(12, "schroeder", "BX", RoomStates.Seated)
    ];

    // --- scenario-rich history profile ----------------------------------------------------------

    // Calendar days of clean synthetic history to seed (well beyond SyntheticHistoryDays=41, so
    // Today/Last-7/Last-30/All-time all diverge and weekly trend buckets populate). Reuses
    // WriteSyntheticCase unmodified - this is only a different orchestrating loop around it, the
    // same relationship SeedSyntheticReportData and SeedLargeSyntheticReportData already have.
    private const int ScenarioRichHistoryDays = 120;
    private const int ScenarioRichCasesPerDay = 3;

    // Fixed day-offset shift applied only by the all-scenarios profile so its scenario-rich bulk
    // history never lands on the same calendar days as its large-synthetic seed. Both loops call the
    // shared WriteSyntheticCase, whose (RoomId, SeatedAt) upsert key depends only on caseInDay (not
    // on which loop calls it), so unshifted overlapping day ranges could silently collide and
    // overwrite each other's rows. 2000 days is a fixed, deterministic margin comfortably beyond the
    // worst-case large-synthetic span (MaxCompletedCycles / LargeSyntheticCasesPerDay = 10000 / 12,
    // about 834 days), so it is safe regardless of the requested --completed-cycles count.
    private const int AllScenariosHistoryDayOffsetShift = 2000;

    // Seeds ScenarioRichHistoryDays of clean synthetic history ending dayOffsetShift days before
    // today (0 = ends today, matching the scenario-rich profile's own unshifted default). Only the
    // calendar day passed to WriteSyntheticCase shifts; the dayOffset value used for jitter/family
    // seeding stays the original 0..ScenarioRichHistoryDays index, so a shifted call produces
    // content-identical cycles to an unshifted call, just relocated to different calendar days.
    private SeedReportDataResult SeedScenarioRichHistory(int dayOffsetShift = 0)
    {
        var doctorIds = _activeDoctors.Select(doctor => doctor.Id).ToList();
        if (doctorIds.Count == 0)
        {
            return new SeedReportDataResult(0, 0, 0, 0, 0);
        }

        var today = new DateTimeOffset(Now.UtcDateTime.Date, TimeSpan.Zero);
        var written = 0;
        var doctorsRepresented = new HashSet<string>(StringComparer.Ordinal);
        var familiesRepresented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalIndex = 0;

        for (var dayOffset = 0; dayOffset <= ScenarioRichHistoryDays; dayOffset++)
        {
            var day = today.AddDays(-(dayOffset + dayOffsetShift));
            for (var caseInDay = 0; caseInDay < ScenarioRichCasesPerDay; caseInDay++)
            {
                var completed = WriteSyntheticCase(doctorIds, dayOffset, day, caseInDay, globalIndex);
                globalIndex++;
                if (completed is { } write)
                {
                    written++;
                    doctorsRepresented.Add(write.DoctorId);
                    familiesRepresented.Add(write.BaseFamilyCode);
                }
            }
        }

        return new SeedReportDataResult(written, doctorsRepresented.Count, familiesRepresented.Count, written, 0);
    }

    // Seeds the explicit, deterministic edge-case cycles scenario-rich adds on top of the bulk clean
    // history above: one clean, included cycle landing in each report date-range bucket boundary
    // (Today; outside Today but inside Last-7; outside Last-7 but inside Last-30; older than
    // Last-30 - offsets mirror the bucket comments already used by CasesForDayOffset), one cycle per
    // derived reporting-exception reason engineered so only that reason's predicate is true (no
    // accidental overlap - e.g. the ExtremeDuration cycle stays same-day, and the OvernightLifecycle
    // cycle stays well under the extreme-duration thresholds), and one manual audit candidate flagged
    // via the existing MarkCycleAsException path (composition, not duplicate logic). Returns the
    // actual number of rows written (0 when there are no active doctors) rather than an assumed
    // constant, so a caller's cycle-seeded count can never claim rows that were not written.
    private int SeedScenarioRichEdgeCases(DateTimeOffset now)
    {
        var doctorIds = _activeDoctors.Select(doctor => doctor.Id).ToList();
        if (doctorIds.Count == 0)
        {
            return 0;
        }

        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var written = 0;

        // Date-range bucket markers (Refinement 2). Each is a normal clean cycle - mapped active
        // procedure, same-day, arrival present - so none of them trips a derived exception. The
        // Today marker is anchored backward from `now` (with a same-day floor), never a fixed clock
        // hour, so it is never seeded in the future regardless of what time this command runs.
        var todayMarker = TodayMarkerTimestamps(today, now, prepMin: 6, readyMin: 8, doctorMin: 15, turnoverMin: 6);
        UpsertExplicitCompletedCycle(BuildCleanCompletedCycle(
            RoomForEdgeCase(1), doctorIds[0 % doctorIds.Count], "CON",
            todayMarker.SeatedAt, todayMarker.PrepMin, todayMarker.ReadyMin, todayMarker.DoctorMin, todayMarker.TurnoverMin)); // Today
        written++;

        UpsertExplicitCompletedCycle(BuildCleanCompletedCycle(
            RoomForEdgeCase(2), doctorIds[1 % doctorIds.Count], "EXT",
            today.AddDays(-3).AddHours(9), prepMin: 8, readyMin: 10, doctorMin: 30, turnoverMin: 8)); // outside Today, inside Last-7
        written++;

        UpsertExplicitCompletedCycle(BuildCleanCompletedCycle(
            RoomForEdgeCase(3), doctorIds[2 % doctorIds.Count], "IMP",
            today.AddDays(-15).AddHours(9), prepMin: 10, readyMin: 15, doctorMin: 60, turnoverMin: 10)); // outside Last-7, inside Last-30
        written++;

        UpsertExplicitCompletedCycle(BuildCleanCompletedCycle(
            RoomForEdgeCase(4), doctorIds[3 % doctorIds.Count], "BX",
            today.AddDays(-40).AddHours(9), prepMin: 8, readyMin: 12, doctorMin: 25, turnoverMin: 8)); // older than Last-30
        written++;

        // One cycle per derived reporting-exception reason (Refinement 1), each isolated to only its
        // own predicate.
        UpsertExplicitCompletedCycle(BuildCleanCompletedCycle(
            RoomForEdgeCase(5), doctorIds[0 % doctorIds.Count], "ZZZSTRESS",
            today.AddDays(-2).AddHours(8), prepMin: 10, readyMin: 15, doctorMin: 35, turnoverMin: 10)); // UnmappedProcedure only
        written++;

        UpsertExplicitCompletedCycle(BuildCleanCompletedCycle(
            RoomForEdgeCase(6), doctorIds[1 % doctorIds.Count], "SED",
            today.AddDays(-2).AddHours(10), prepMin: 10, readyMin: 15, doctorMin: 35, turnoverMin: 10)); // LegacyProcedure only
        written++;

        UpsertExplicitCompletedCycle(BuildCleanCompletedCycle(
            RoomForEdgeCase(7), doctorIds[2 % doctorIds.Count], "IMP",
            today.AddDays(-2).AddHours(2), prepMin: 10, readyMin: 15, doctorMin: 245, turnoverMin: 10)); // ExtremeDuration only (case flow 4h30m, same day)
        written++;

        UpsertExplicitCompletedCycle(BuildCleanCompletedCycle(
            RoomForEdgeCase(8), doctorIds[3 % doctorIds.Count], "EXT",
            today.AddDays(-2).AddHours(23).AddMinutes(50), prepMin: 5, readyMin: 5, doctorMin: 15, turnoverMin: 5)); // OvernightLifecycle only (crosses midnight, tiny durations)
        written++;

        UpsertExplicitCompletedCycle(BuildMissingTimingCycle(
            RoomForEdgeCase(9), doctorIds[0 % doctorIds.Count], "POST",
            today.AddDays(-2).AddHours(14), completeMin: 50, turnoverMin: 10)); // MissingTiming only (no DoctorArrivedAt; DoctorCompleteAt/RoomAvailableAt set)
        written++;

        // Manual audit candidate: a normal clean cycle, then flagged through the same shared
        // MarkCycleAsException path the real reports-page review workflow uses.
        var manualCandidate = UpsertExplicitCompletedCycle(BuildCleanCompletedCycle(
            RoomForEdgeCase(10), doctorIds[1 % doctorIds.Count], "BX",
            today.AddDays(-1).AddHours(9), prepMin: 8, readyMin: 12, doctorMin: 28, turnoverMin: 8));
        MarkCycleAsException(manualCandidate, ExceptionReasons.ManualReview, "Stress fixture: planted manual audit candidate for review-queue testing.");
        written++;

        return written;
    }

    // Deterministic room id for an edge-case cycle slot, wrapped into the configured room count so
    // it stays a plausible room id regardless of RoomCount (historical completed-cycle rows do not
    // need to reference a currently-configured room).
    private int RoomForEdgeCase(int index) => 1 + (index % Math.Max(1, _roomCount));

    // Computes the Today bucket marker's timestamps so it always lands on `today` and never in the
    // future relative to `now`. A fixed clock-hour anchor (e.g. 09:00 UTC) would itself be a future
    // timestamp whenever this command runs earlier than that, so this anchors backward from `now`
    // instead. In the narrow edge case where `now` is within the marker's own total duration of UTC
    // midnight, the phases are shrunk proportionally so every resulting timestamp still stays inside
    // [today, now] - the marker is never seeded in the future, even in that edge case.
    private static (DateTimeOffset SeatedAt, int PrepMin, int ReadyMin, int DoctorMin, int TurnoverMin) TodayMarkerTimestamps(
        DateTimeOffset today, DateTimeOffset now, int prepMin, int readyMin, int doctorMin, int turnoverMin)
    {
        var totalMin = prepMin + readyMin + doctorMin + turnoverMin;
        var elapsedTodayMin = Math.Max(0, (int)(now - today).TotalMinutes);
        var safeTotalMin = Math.Min(totalMin, elapsedTodayMin);
        var seatedAt = now.AddMinutes(-safeTotalMin);

        if (safeTotalMin >= totalMin)
        {
            return (seatedAt, prepMin, readyMin, doctorMin, turnoverMin);
        }

        var scale = totalMin == 0 ? 0d : (double)safeTotalMin / totalMin;
        var scaledPrep = (int)Math.Round(prepMin * scale);
        var scaledReady = (int)Math.Round(readyMin * scale);
        var scaledDoctor = (int)Math.Round(doctorMin * scale);
        var scaledTurnover = Math.Max(0, safeTotalMin - scaledPrep - scaledReady - scaledDoctor);
        return (seatedAt, scaledPrep, scaledReady, scaledDoctor, scaledTurnover);
    }

    // Builds a fully-completed, clean synthetic cycle with explicit minute splits (unlike
    // WriteSyntheticCase, no jitter - every field here is directly authored so edge-case
    // durations/day-crossings are exact and reproducible). Falls back to the procedure roster's
    // default expected units when the code is legacy/inactive or entirely unmapped, since
    // FindActiveProcedure would otherwise reject the two procedure-mapping edge cases outright.
    private CompletedRoomCycle BuildCleanCompletedCycle(
        int roomId, string doctorId, string procedureCode, DateTimeOffset seatedAt,
        int prepMin, int readyMin, int doctorMin, int turnoverMin)
    {
        var defaultUnits = Math.Clamp(FindProcedure(procedureCode)?.DefaultExpectedUnits ?? MinExpectedUnits, MinExpectedUnits, MaxExpectedUnits);
        var readyAt = seatedAt.AddMinutes(prepMin);
        var arrivedAt = readyAt.AddMinutes(readyMin);
        var completeAt = arrivedAt.AddMinutes(doctorMin);
        var availableAt = completeAt.AddMinutes(turnoverMin);

        return new CompletedRoomCycle
        {
            RoomId = roomId,
            AssignedDoctor = doctorId,
            ProcedureCode = procedureCode,
            SeatedAt = seatedAt,
            ReadyForDoctorAt = readyAt,
            DoctorArrivedAt = arrivedAt,
            DoctorCompleteAt = completeAt,
            RoomAvailableAt = availableAt,
            SeatedToDoctorSeconds = (prepMin + readyMin) * 60,
            PrepSeconds = prepMin * 60,
            ReadyToDoctorSeconds = readyMin * 60,
            DoctorInRoomSeconds = doctorMin * 60,
            TurnoverSeconds = turnoverMin * 60,
            TotalRoomCycleSeconds = (prepMin + readyMin + doctorMin + turnoverMin) * 60,
            FinalWaitState = RoomStates.ReadyForDoctor,
            AgingThresholdReached = false,
            StaleThresholdReached = false,
            OriginalDefaultExpectedUnits = defaultUnits,
            ExpectedAllocationUnits = defaultUnits,
            ExpectedAllocationMinutes = defaultUnits * 10,
            AllocationAdjustedFromDefault = false,
            IsException = false,
            RequiresReview = false,
            ExceptionReason = null,
            ReviewStatus = ReviewStatuses.PendingReview,
            SuggestedAction = null,
            ReviewedAt = null,
            ReviewedBy = null
        };
    }

    // Builds the one deliberately abnormal cycle in the edge-case set: DoctorArrivedAt stays null
    // (the MissingTiming predicate), but unlike a genuinely in-progress room, DoctorCompleteAt and
    // RoomAvailableAt ARE set, since report date-range windows are anchored on DoctorCompleteAt
    // (GetReports/ReportDateRange) - a null DoctorCompleteAt would only ever surface in the All-time
    // range. Durations that depend on arrival (SeatedToDoctorSeconds - sentinel 0, since the field is
    // non-nullable; ReadyToDoctorSeconds; DoctorInRoomSeconds) stay null/0; TurnoverSeconds and
    // TotalRoomCycleSeconds are still computed since they do not depend on arrival.
    private CompletedRoomCycle BuildMissingTimingCycle(
        int roomId, string doctorId, string procedureCode, DateTimeOffset seatedAt,
        int completeMin, int turnoverMin)
    {
        var defaultUnits = Math.Clamp(FindProcedure(procedureCode)?.DefaultExpectedUnits ?? MinExpectedUnits, MinExpectedUnits, MaxExpectedUnits);
        var completeAt = seatedAt.AddMinutes(completeMin);
        var availableAt = completeAt.AddMinutes(turnoverMin);

        return new CompletedRoomCycle
        {
            RoomId = roomId,
            AssignedDoctor = doctorId,
            ProcedureCode = procedureCode,
            SeatedAt = seatedAt,
            ReadyForDoctorAt = null,
            DoctorArrivedAt = null,
            DoctorCompleteAt = completeAt,
            RoomAvailableAt = availableAt,
            SeatedToDoctorSeconds = 0,
            PrepSeconds = null,
            ReadyToDoctorSeconds = null,
            DoctorInRoomSeconds = null,
            TurnoverSeconds = turnoverMin * 60,
            TotalRoomCycleSeconds = (completeMin + turnoverMin) * 60,
            FinalWaitState = RoomStates.Seated,
            AgingThresholdReached = false,
            StaleThresholdReached = false,
            OriginalDefaultExpectedUnits = defaultUnits,
            ExpectedAllocationUnits = defaultUnits,
            ExpectedAllocationMinutes = defaultUnits * 10,
            AllocationAdjustedFromDefault = false,
            IsException = false,
            RequiresReview = false,
            ExceptionReason = null,
            ReviewStatus = ReviewStatuses.PendingReview,
            SuggestedAction = null,
            ReviewedAt = null,
            ReviewedBy = null
        };
    }

    // Deterministic per-doctor synthetic style profiles (no punitive meaning). The profiles give
    // each doctor a distinct, plausible allocation shape so the Reports page is not symmetrical:
    //   Otte      - buffered: generous expected units, leans under/at expected.
    //   Pledger   - balanced: mixed, net near zero.
    //   Gibson    - variable case-mix: variance swings by procedure family (high family-lean weight).
    //   Schroeder - tight: lower expected units, leans modestly over expected.
    // DoctorIndex maps to the active-doctor roster order (Otte, Pledger, Gibson, Schroeder).
    private static readonly IReadOnlyList<DoctorStyleProfile> SyntheticProfiles =
    [
        new(DoctorIndex: 0, VarianceBiasMinutes: -5, VarianceSpread: 6, FamilyLeanWeight: 1, VariableUnitDelta: 1, SedationChancePercent: 25),
        new(DoctorIndex: 1, VarianceBiasMinutes: 0, VarianceSpread: 8, FamilyLeanWeight: 1, VariableUnitDelta: 0, SedationChancePercent: 20),
        new(DoctorIndex: 2, VarianceBiasMinutes: 1, VarianceSpread: 14, FamilyLeanWeight: 2, VariableUnitDelta: 0, SedationChancePercent: 30),
        new(DoctorIndex: 3, VarianceBiasMinutes: 7, VarianceSpread: 5, FamilyLeanWeight: 1, VariableUnitDelta: -1, SedationChancePercent: 15)
    ];

    // Procedure families used by the seeder. CharacterLeanMinutes is a small intrinsic over/under
    // tendency (procedures that run long are positive); realistic case-flow bounds keep every record
    // well under the extreme-duration thresholds. Default expected units come from the live roster.
    private static readonly IReadOnlyList<SyntheticFamily> SyntheticFamilies =
    [
        new("CON", 10, 40, -3, false),
        new("POST", 5, 25, -2, false),
        new("IMPRES", 10, 35, -2, false),
        new("BX", 20, 60, 3, false),
        new("EXT", 20, 90, 5, true),
        new("IMP", 45, 120, 5, true),
        new("MISC", 10, 60, 2, false)
    ];

    // Stable FNV-1a hash of the seed components, used to seed the per-cycle jitter stream.
    private static int DeterministicSeed(int dayIndex, int doctorIndex, int caseIndex)
    {
        unchecked
        {
            var hash = 2166136261u;
            hash = (hash ^ (uint)dayIndex) * 16777619u;
            hash = (hash ^ (uint)doctorIndex) * 16777619u;
            hash = (hash ^ (uint)caseIndex) * 16777619u;
            return (int)(hash & 0x7fffffff);
        }
    }

    // How many calendar days back the synthetic history spans (inclusive of today). ~6 weeks so the
    // "older than 30 days" bucket is populated for All-time vs Last-30 comparisons.
    private const int SyntheticHistoryDays = 41;

    // Flat per-day case cap for the large report-data seed. Kept low enough that the latest seat time
    // (8am + this many hours, plus jitter and the longest case flow) still completes inside the same
    // UTC calendar day, preserving the "no overnight cycle" cleanliness the reporting funnel expects.
    private const int LargeSyntheticCasesPerDay = 12;

    // Deterministic, front-loaded case count per day offset from today (0 = today). Recent days carry
    // more cases than older days so every report date-range preset is meaningfully different:
    //   today          -> 8           (Today preset is small but non-empty)
    //   1-6 days ago    -> 4 each      (Last 7 days noticeably larger than Today)
    //   7-29 days ago   -> 2-3 each    (Last 30 days larger than Last 7)
    //   30+ days ago    -> 2 each      (All time larger than Last 30)
    // Totals: Today 8, Last 7 ~32, Last 30 ~90, All time ~114.
    private static int CasesForDayOffset(int dayOffset)
    {
        if (dayOffset == 0)
        {
            return 8;
        }

        if (dayOffset <= 6)
        {
            return 4;
        }

        if (dayOffset <= 29)
        {
            return 2 + (dayOffset % 2);
        }

        return 2;
    }

    /// <summary>
    /// Marks an existing completed cycle as an exception, removing it from normal
    /// reporting metrics and surfacing it in the Exceptions Requiring Review section.
    /// Returns false if no matching cycle is found.
    /// No PHI is stored - reason and suggested action are operational notes only.
    /// </summary>
    public bool MarkCycleAsException(int roomId, DateTimeOffset seatedAt, string reason, string suggestedAction)
    {
        lock (_syncRoot)
        {
            var cycle = _completedCycles.FirstOrDefault(item => item.RoomId == roomId && item.SeatedAt == seatedAt);
            return MarkCycleAsException(cycle, reason, suggestedAction);
        }
    }

    /// <summary>
    /// Marks a completed cycle as an exception by its stable CompletedCycleId. This is the
    /// preferred targeting path; the (roomId, seatedAt) overload remains for backward compatibility.
    /// Returns false if no matching cycle is found or the id is not a positive value.
    /// No PHI is stored - reason and suggested action are operational notes only.
    /// </summary>
    public bool MarkCycleAsExceptionById(long completedCycleId, string reason, string suggestedAction)
    {
        lock (_syncRoot)
        {
            if (completedCycleId <= 0)
            {
                return false;
            }

            var cycle = _completedCycles.FirstOrDefault(item => item.CompletedCycleId == completedCycleId);
            return MarkCycleAsException(cycle, reason, suggestedAction);
        }
    }

    // Shared mutation for both the id-based and legacy (roomId, seatedAt) targeting paths.
    // Must be called inside _syncRoot.
    private bool MarkCycleAsException(CompletedRoomCycle? cycle, string reason, string suggestedAction)
    {
        if (cycle is null)
        {
            return false;
        }

        cycle.IsException = true;
        cycle.RequiresReview = true;
        cycle.ExceptionReason = reason;
        cycle.SuggestedAction = suggestedAction;
        cycle.ReviewStatus = ReviewStatuses.PendingReview;
        PersistCycle(cycle);
        return true;
    }

    /// <summary>
    /// Confirms the exclusion of an exception cycle by its stable CompletedCycleId, completing the
    /// review workflow. The cycle stays an exception and therefore stays excluded from normal
    /// metrics; confirming review only clears it from the pending-review queue.
    ///
    /// Outcomes:
    /// - NotFound when no cycle matches the id (including non-positive ids).
    /// - NotAnException when the cycle exists but was never flagged as an exception.
    /// - Reviewed on success. Idempotent: confirming an already-reviewed exception succeeds again.
    ///
    /// On success RequiresReview becomes false, ReviewStatus becomes Reviewed, ReviewedAt is set to
    /// the current time, and ReviewedBy is set to a safe non-PHI reviewer label. SuggestedAction and
    /// IsException are left unchanged so the cycle remains excluded from normal metrics.
    /// </summary>
    public ReviewExceptionResult ReviewExceptionCycleById(long completedCycleId)
    {
        lock (_syncRoot)
        {
            if (completedCycleId <= 0)
            {
                return new ReviewExceptionResult(ReviewExceptionOutcome.NotFound, 0);
            }

            var cycle = _completedCycles.FirstOrDefault(item => item.CompletedCycleId == completedCycleId);
            if (cycle is null)
            {
                return new ReviewExceptionResult(ReviewExceptionOutcome.NotFound, 0);
            }

            if (!cycle.IsException)
            {
                return new ReviewExceptionResult(ReviewExceptionOutcome.NotAnException, cycle.RoomId);
            }

            // Idempotent: re-applying these on an already-reviewed exception has no net effect.
            cycle.RequiresReview = false;
            cycle.ReviewStatus = ReviewStatuses.Reviewed;
            cycle.ReviewedAt = Now;
            cycle.ReviewedBy = ExceptionReviewers.LocalAdmin;
            PersistCycle(cycle);
            return new ReviewExceptionResult(ReviewExceptionOutcome.Reviewed, cycle.RoomId);
        }
    }

    /// <summary>
    /// Checks all active room assignments for exceeding MaxActiveDurationHours and expires any
    /// that do. Pre-arrival assignments are archived as aborted assignments; post-arrival rooms
    /// are archived as review-required exception cycles. Each room is reset to Available atomically
    /// with its archive.
    /// Returns the room IDs that were expired.
    /// </summary>
    public IReadOnlyList<int> CheckAndExpireActiveCycles()
    {
        lock (_syncRoot)
        {
            var options = ExpirationOptions;
            if (!options.Enabled)
            {
                return [];
            }

            var now = Now;
            var maxDuration = TimeSpan.FromHours(options.MaxActiveDurationHours);
            var expired = new List<int>();

            foreach (var room in _rooms)
            {
                if (room.State == RoomStates.Available)
                {
                    continue;
                }

                var activeStartedAt = room.State == RoomStates.Prestaging
                    ? room.PrestageStartedAt
                    : room.SeatedAt;
                if (activeStartedAt is null || now - activeStartedAt.Value <= maxDuration)
                {
                    continue;
                }

                if (ExpireRoom(room, now, ExceptionReasons.ExceededMaxActiveDuration))
                {
                    expired.Add(room.RoomId);
                }
            }

            return expired;
        }
    }

    /// <summary>
    /// Checks whether the after-hours sweep should fire for the current clinic day and,
    /// if so, expires all still-active room assignments. Prestaging assignments are archived as
    /// aborted assignments before arrival; post-arrival rooms become AfterHoursSweep exceptions.
    /// Runs at most once per clinic day; skips Available rooms.
    /// Returns the room IDs that were expired.
    /// </summary>
    public IReadOnlyList<int> TryRunAfterHoursSweep()
    {
        lock (_syncRoot)
        {
            var options = ExpirationOptions;
            if (!options.Enabled || !options.AfterHoursSweepEnabled)
            {
                return [];
            }

            var clinicZone = ResolveTimeZone(options.TimeZone);
            if (clinicZone is null)
            {
                // Invalid or unresolvable timezone - suppress the sweep entirely.
                // A misconfigured zone must not silently become UTC and fire at the wrong local time.
                return [];
            }

            var now = Now;
            var clinicNow = TimeZoneInfo.ConvertTime(now, clinicZone);
            var today = DateOnly.FromDateTime(clinicNow.DateTime);

            // At-most-once per clinic day.
            if (_lastSweepDate >= today)
            {
                return [];
            }

            // Only fire at or after the configured sweep time.
            if (!TryParseSweepTime(options.AfterHoursSweepTime, out var sweepTime))
            {
                return [];
            }

            var clinicTimeOfDay = TimeOnly.FromDateTime(clinicNow.DateTime);
            if (clinicTimeOfDay < sweepTime)
            {
                return [];
            }

            _lastSweepDate = today;

            var expired = new List<int>();
            foreach (var room in _rooms)
            {
                if (room.State == RoomStates.Available
                    || (room.State == RoomStates.Prestaging
                        ? room.PrestageStartedAt is null
                        : room.SeatedAt is null))
                {
                    continue;
                }

                if (ExpireRoom(room, now, ExceptionReasons.AfterHoursSweep))
                {
                    expired.Add(room.RoomId);
                }
            }

            return expired;
        }
    }

    /// <summary>
    /// Expires an active room. Before Doctor Arrived the room is archived as aborted history (no
    /// throughput). Once Doctor Arrived has occurred the room is recovered as a review-required
    /// exception cycle instead. Either way the room is released to Available. Never manufactures
    /// SeatedAt or DoctorCompleteAt. Must be called inside _syncRoot.
    /// </summary>
    private bool ExpireRoom(RoomState room, DateTimeOffset now, string reason)
    {
        var snapshot = CopyRoomState(room);
        if (snapshot.DoctorArrivedAt is not null)
        {
            // Post-arrival: recover the room as a review-required exception cycle. Never aborted
            // pre-arrival history, and never a fabricated Doctor Complete.
            ExpireArrivedRoom(room, snapshot, now, reason);
            return true;
        }

        // Pre-arrival (Prestaging, Seated, Ready, Aging, Stale): aborted history, not throughput.
        var record = new AbortedRoomAssignment
        {
            EpisodeId = snapshot.EpisodeId ?? NewEpisodeId(),
            RoomId = snapshot.RoomId,
            AssignedDoctor = snapshot.AssignedDoctor,
            AssignedDoctorDisplayName = snapshot.AssignedDoctorDisplayName,
            ProcedureCode = snapshot.ProcedureCode,
            ProcedureCategory = snapshot.ProcedureCategory,
            SedationState = snapshot.SedationState,
            ExpectedAllocationState = snapshot.ExpectedAllocationState,
            ExpectedAllocationSuggestedUnits = snapshot.ExpectedAllocationSuggestedUnits,
            ExpectedAllocationConfirmedUnits = snapshot.ExpectedAllocationConfirmedUnits,
            OriginalDefaultExpectedUnits = snapshot.OriginalDefaultExpectedUnits,
            ExpectedAllocationUnits = snapshot.ExpectedAllocationUnits,
            ExpectedAllocationMinutes = snapshot.ExpectedAllocationMinutes,
            AllocationAdjustedFromDefault = snapshot.AllocationAdjustedFromDefault,
            PrestageStartedAt = snapshot.PrestageStartedAt,
            SeatedAt = snapshot.SeatedAt,
            ReadyForDoctorAt = snapshot.ReadyForDoctorAt,
            TerminatedAt = now,
            TerminatedFromState = snapshot.State,
            TerminationKind = reason == ExceptionReasons.AfterHoursSweep ? TerminationKinds.AfterHoursExpired : TerminationKinds.MaxDurationExpired
        };
        if (!string.IsNullOrWhiteSpace(snapshot.ActiveReadyHandoffId))
        {
            _repository.TerminateReadyHandoffAndIncompleteAssignment(record, snapshot, snapshot.ActiveReadyHandoffId, now, ReadyHandoffTerminationKinds.Expired, _doctors, _procedures);
        }
        else
        {
            _repository.TerminateIncompleteAssignment(record, new RoomState(room.RoomId), _doctors, _procedures);
        }
        ResetRoom(room);
        AddEvent(new RoomEvent(room.RoomId, "ForceExpired", now, record.AssignedDoctor, record.ProcedureCode));
        return true;
    }

    // Recovers an already-arrived room whose active cycle exceeded the maximum duration or was swept
    // after hours. Doctor Arrived created a completed cycle; that cycle is re-marked as a review-
    // required exception (SuggestedAction "Review timing") rather than stranding the room in
    // DoctorInRoom. Truthful timestamps are preserved and DoctorCompleteAt is never manufactured.
    // Durable persistence happens before any live in-memory mutation. Must be called inside _syncRoot.
    private void ExpireArrivedRoom(RoomState room, RoomState snapshot, DateTimeOffset now, string reason)
    {
        var seatedAt = snapshot.SeatedAt
            ?? throw new InvalidOperationException($"Cannot expire arrived room {snapshot.RoomId} without SeatedAt.");
        var doctorArrivedAt = snapshot.DoctorArrivedAt!.Value;

        // The cycle created at Doctor Arrived is keyed by (RoomId, SeatedAt). Reuse it so truthful
        // timestamps and the accepted-handoff snapshot are preserved; only fall back to reconstructing
        // one for a legacy arrived room that has no persisted cycle.
        var cycleIndex = _completedCycles.FindIndex(c => c.RoomId == snapshot.RoomId && c.SeatedAt == seatedAt);
        var cycle = cycleIndex >= 0
            ? CopyCompletedCycle(_completedCycles[cycleIndex])
            : new CompletedRoomCycle
            {
                EpisodeId = snapshot.EpisodeId,
                RoomId = snapshot.RoomId,
                AssignedDoctor = snapshot.AssignedDoctor ?? "",
                ProcedureCode = snapshot.ProcedureCode ?? "",
                PrestageStartedAt = snapshot.PrestageStartedAt,
                SeatedAt = seatedAt,
                ReadyForDoctorAt = snapshot.ReadyForDoctorAt,
                DoctorArrivedAt = doctorArrivedAt,
                SeatedToDoctorSeconds = SecondsBetween(seatedAt, doctorArrivedAt),
                PrepSeconds = snapshot.ReadyForDoctorAt.HasValue
                    ? SecondsBetween(seatedAt, snapshot.ReadyForDoctorAt.Value)
                    : null,
                ReadyToDoctorSeconds = snapshot.ReadyForDoctorAt.HasValue
                    ? SecondsBetween(snapshot.ReadyForDoctorAt.Value, doctorArrivedAt)
                    : null,
                FinalWaitState = snapshot.State,
                AgingThresholdReached = snapshot.AgingStartedAt is not null,
                StaleThresholdReached = snapshot.StaleStartedAt is not null,
                OriginalDefaultExpectedUnits = snapshot.OriginalDefaultExpectedUnits,
                ExpectedAllocationUnits = snapshot.ExpectedAllocationUnits,
                ExpectedAllocationMinutes = snapshot.ExpectedAllocationMinutes,
                AllocationAdjustedFromDefault = snapshot.AllocationAdjustedFromDefault
            };

        cycle.IsException = true;
        cycle.RequiresReview = true;
        cycle.ExceptionReason = reason;
        cycle.SuggestedAction = "Review timing";
        cycle.ReviewStatus = ReviewStatuses.PendingReview;

        // Persist durably before mutating live state.
        _repository.SaveCompletedCycleAndRoom(cycle, new RoomState(room.RoomId), _doctors, _procedures);

        if (cycleIndex >= 0)
        {
            _completedCycles[cycleIndex] = cycle;
        }
        else
        {
            _completedCycles.Add(cycle);
        }

        ResetRoom(room);
        AddEvent(new RoomEvent(room.RoomId, "ForceExpired", now, snapshot.AssignedDoctor, snapshot.ProcedureCode));
    }

    // Returns null if timeZone is a non-blank, non-UTC value that cannot be resolved.
    // Callers must treat null as "fail closed" - do not substitute UTC silently.
    private static TimeZoneInfo? ResolveTimeZone(string timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone) ||
            string.Equals(timeZone, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch
        {
            return null;  // unresolvable - caller must not fall back to UTC
        }
    }

    private static bool TryParseSweepTime(string sweepTime, out TimeOnly result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(sweepTime))
        {
            return false;
        }

        var parts = sweepTime.Split(':');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var hour) ||
            !int.TryParse(parts[1], out var minute) ||
            hour < 0 || hour > 23 ||
            minute < 0 || minute > 59)
        {
            return false;
        }

        result = new TimeOnly(hour, minute);
        return true;
    }

    private RoomExpirationOptions ExpirationOptions => _expirationOptions.CurrentValue;

    private RoomStatus ToRoomStatus(RoomState room, DateTimeOffset now)
    {
        var elapsed = room.SeatedAt is null ? TimeSpan.Zero : now - room.SeatedAt.Value;
        var doctor = room.AssignedDoctor is null ? null : _doctors.FirstOrDefault(item => item.Id == room.AssignedDoctor);
        var procedure = ResolveProcedure(room.ProcedureCode);
        var urgency = ProjectReadyUrgency(room, now);
        var faults = DeriveIntegrityFaults(room);
        var persistedAssignment = GetCanonicalAssignment(room);
        var assignment = persistedAssignment.TryToContract(out var canonicalAssignment)
            ? PrestagingLifecycleResponseProjector.ProjectAssignment(canonicalAssignment)
            : null;
        var assignmentLocked = room.State is
            RoomStates.ReadyForDoctor or RoomStates.Aging or RoomStates.Stale or RoomStates.DoctorInRoom or RoomStates.Turnover;

        return new RoomStatus(
            room.RoomId,
            room.RoomId,
            room.AssignedDoctor,
            room.ProcedureCode,
            room.State,
            doctor,
            procedure,
            room.SeatedAt,
            room.AgingStartedAt,
            room.StaleStartedAt,
            room.ReadyForDoctorAt,
            room.DoctorArrivedAt,
            room.DoctorCompleteAt,
            room.RoomAvailableAt,
            elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed,
            room.OriginalDefaultExpectedUnits,
            room.ExpectedAllocationUnits,
            room.ExpectedAllocationMinutes,
            room.AllocationAdjustedFromDefault,
            urgency,
            faults,
            room.EpisodeId,
            room.PrestageStartedAt,
            assignment,
            assignmentLocked,
            room.ActiveReadyHandoffId,
            room.AcceptedReadyHandoffId);
    }

    private ReadyUrgency ProjectReadyUrgency(RoomState room, DateTimeOffset now)
    {
        if (room.State == RoomStates.Aging)
        {
            return ReadyUrgency.Aging;
        }
        if (room.State == RoomStates.Stale)
        {
            return ReadyUrgency.Stale;
        }
        if (room.State != RoomStates.ReadyForDoctor || string.IsNullOrWhiteSpace(room.ActiveReadyHandoffId))
        {
            return ReadyUrgency.None;
        }

        var handoff = _repository.LoadReadyHandoff(room.ActiveReadyHandoffId);
        if (handoff is null || handoff.ContractStatus != ReadyHandoffStatus.Active)
        {
            return ReadyUrgency.None;
        }
        var elapsed = now - handoff.ReadyAt;
        return elapsed >= Thresholds.StaleThreshold
            ? ReadyUrgency.Stale
            : elapsed >= Thresholds.AgingThreshold
                ? ReadyUrgency.Aging
                : ReadyUrgency.None;
    }

    private IReadOnlyList<RoomIntegrityFault> DeriveIntegrityFaults(RoomState room)
    {
        if (room.State is not (RoomStates.ReadyForDoctor or RoomStates.Aging or RoomStates.Stale or RoomStates.DoctorInRoom or RoomStates.Turnover))
        {
            return [];
        }

        var assignment = GetCanonicalAssignment(room);
        var faultAssignment = RoomAssignmentContract.Create(
            null,
            null,
            SedationContract.UnavailableNoProcedure(),
            ExpectedAllocationContract.Unknown());
        var faults = new List<RoomIntegrityFault>();
        // Malformed persisted assignment data must project a fault, not throw and hide the room.
        if (assignment.TryToContract(out var canonicalAssignment))
        {
            if (room.State is RoomStates.ReadyForDoctor or RoomStates.Aging or RoomStates.Stale
                && canonicalAssignment.Completeness != AssignmentCompleteness.Complete)
            {
                faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.ReadyAssignmentIncomplete, canonicalAssignment));
            }
        }
        else
        {
            faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.ReadyAssignmentIncomplete, faultAssignment));
        }

        var faultAssignmentValue = canonicalAssignment ?? faultAssignment;
        var readyCompatible = room.State is RoomStates.ReadyForDoctor or RoomStates.Aging or RoomStates.Stale;
        var arrivedOrLater = room.State is RoomStates.DoctorInRoom or RoomStates.Turnover;
        if (!string.IsNullOrWhiteSpace(room.ActiveReadyHandoffId)
            && !string.IsNullOrWhiteSpace(room.AcceptedReadyHandoffId))
        {
            faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.ContradictoryHandoffReferences, faultAssignmentValue));
        }

        if (readyCompatible)
        {
            AddActiveHandoffFaults(room, faultAssignmentValue, faults);
        }
        else if (arrivedOrLater)
        {
            AddAcceptedHandoffFaults(room, faultAssignmentValue, faults);
        }

        return faults;
    }

    private void AddActiveHandoffFaults(
        RoomState room,
        RoomAssignmentContract assignment,
        List<RoomIntegrityFault> faults)
    {
        if (string.IsNullOrWhiteSpace(room.ActiveReadyHandoffId))
        {
            faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.ReadyHandoffMissing, assignment));
            return;
        }

        var handoff = _repository.LoadReadyHandoff(room.ActiveReadyHandoffId);
        if (handoff is null)
        {
            faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.ReadyHandoffMissing, assignment));
            return;
        }

        if (handoff.RoomId != room.RoomId || !string.Equals(handoff.EpisodeId, room.EpisodeId, StringComparison.Ordinal))
        {
            faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.ReadyHandoffOwnershipMismatch, assignment));
        }
        if (handoff.WithdrawnAt.HasValue || handoff.AcceptedAt.HasValue || handoff.TerminatedAt.HasValue)
        {
            faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.ReadyHandoffNotActive, assignment));
        }
        if (!TryGetCompleteHandoffAssignment(handoff, out _))
        {
            faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.ReadyHandoffAssignmentIncomplete, assignment));
        }
        else if (!GetCanonicalAssignment(room).MatchesHandoffSnapshot(handoff.Assignment))
        {
            faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.ReadyHandoffAssignmentMismatch, assignment));
        }
    }

    private void AddAcceptedHandoffFaults(
        RoomState room,
        RoomAssignmentContract assignment,
        List<RoomIntegrityFault> faults)
    {
        if (string.IsNullOrWhiteSpace(room.AcceptedReadyHandoffId))
        {
            // Pre-handoff persistence rows remain completable for legacy compatibility.
            if (room.SedationState.HasValue || room.ExpectedAllocationState.HasValue)
            {
                faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.AcceptedHandoffMissing, assignment));
            }
            return;
        }

        var handoff = _repository.LoadReadyHandoff(room.AcceptedReadyHandoffId);
        if (handoff is null)
        {
            faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.AcceptedHandoffMissing, assignment));
            return;
        }

        if (handoff.RoomId != room.RoomId || !string.Equals(handoff.EpisodeId, room.EpisodeId, StringComparison.Ordinal))
        {
            faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.AcceptedHandoffOwnershipMismatch, assignment));
        }
        if (!handoff.AcceptedAt.HasValue || handoff.WithdrawnAt.HasValue || handoff.TerminatedAt.HasValue)
        {
            faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.AcceptedHandoffNotAccepted, assignment));
        }
        if (!TryGetCompleteHandoffAssignment(handoff, out _))
        {
            faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.AcceptedHandoffAssignmentIncomplete, assignment));
            return;
        }

        if (!GetCanonicalAssignment(room).MatchesHandoffSnapshot(handoff.Assignment)
            || !MatchesAcceptedAllocationSnapshot(room, handoff.Assignment))
        {
            faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.AcceptedHandoffAssignmentMismatch, assignment));
        }

        var history = string.IsNullOrWhiteSpace(room.EpisodeId)
            ? []
            : _repository.LoadReadyHandoffsByEpisode(room.EpisodeId);
        if (room.ReadyForDoctorAt != handoff.ReadyAt
            || room.DoctorArrivedAt != handoff.AcceptedAt
            || history.Any(existing =>
                existing.RoomId != room.RoomId
                || !string.Equals(existing.EpisodeId, room.EpisodeId, StringComparison.Ordinal)
                || (existing.HandoffId != handoff.HandoffId
                    && existing.ContractStatus != ReadyHandoffStatus.Withdrawn)))
        {
            faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.AcceptedHandoffHistoryMismatch, assignment));
        }

        var cycle = room.SeatedAt.HasValue
            ? _completedCycles.SingleOrDefault(existing =>
                existing.RoomId == room.RoomId
                && existing.SeatedAt == room.SeatedAt.Value)
            : null;
        if (cycle is null || !CompletedCycleMatchesAcceptedTruth(cycle, room, handoff))
        {
            faults.Add(new RoomIntegrityFault(RoomIntegrityFaultCode.AcceptedHandoffCycleMismatch, assignment));
        }
    }

    private static bool MatchesAcceptedAllocationSnapshot(
        RoomState room,
        PersistedRoomAssignment assignment)
    {
        if (assignment.ExpectedAllocationConfirmedUnits is not { } confirmedUnits)
        {
            return false;
        }

        var suggestedUnits = assignment.ExpectedAllocationSuggestedUnits;
        return room.OriginalDefaultExpectedUnits == (suggestedUnits ?? confirmedUnits)
            && room.ExpectedAllocationUnits == confirmedUnits
            && room.ExpectedAllocationMinutes == confirmedUnits * 10
            && room.AllocationAdjustedFromDefault ==
                (suggestedUnits.HasValue && suggestedUnits.Value != confirmedUnits);
    }

    private static bool CompletedCycleMatchesAcceptedTruth(
        CompletedRoomCycle cycle,
        RoomState room,
        PersistedReadyHandoff handoff)
    {
        if (handoff.AcceptedAt is not { } acceptedAt
            || handoff.Assignment.ExpectedAllocationConfirmedUnits is not { } confirmedUnits
            || room.SeatedAt is not { } seatedAt)
        {
            return false;
        }

        var suggestedUnits = handoff.Assignment.ExpectedAllocationSuggestedUnits;
        return cycle.RoomId == room.RoomId
            && string.Equals(cycle.EpisodeId, room.EpisodeId, StringComparison.Ordinal)
            && cycle.SeatedAt == seatedAt
            && string.Equals(cycle.AcceptedReadyHandoffId, handoff.HandoffId, StringComparison.Ordinal)
            && string.Equals(cycle.AssignedDoctor, handoff.Assignment.DoctorId, StringComparison.Ordinal)
            && string.Equals(cycle.ProcedureCode, handoff.Assignment.ProcedureCode, StringComparison.Ordinal)
            && cycle.PrestageStartedAt == room.PrestageStartedAt
            && cycle.ReadyForDoctorAt == handoff.ReadyAt
            && cycle.DoctorArrivedAt == acceptedAt
            && cycle.DoctorCompleteAt == room.DoctorCompleteAt
            && cycle.RoomAvailableAt == room.RoomAvailableAt
            && cycle.SeatedToDoctorSeconds == SecondsBetween(seatedAt, acceptedAt)
            && cycle.ReadyToDoctorSeconds == SecondsBetween(handoff.ReadyAt, acceptedAt)
            && cycle.OriginalDefaultExpectedUnits == (suggestedUnits ?? confirmedUnits)
            && cycle.ExpectedAllocationUnits == confirmedUnits
            && cycle.ExpectedAllocationMinutes == confirmedUnits * 10
            && cycle.AllocationAdjustedFromDefault ==
                (suggestedUnits.HasValue && suggestedUnits.Value != confirmedUnits);
    }

    private static bool TryGetCompleteHandoffAssignment(PersistedReadyHandoff handoff, out RoomAssignmentContract? assignment)
    {
        // Malformed persisted handoff assignment data must project a fault, not throw and hide the room.
        return handoff.Assignment.TryToContract(out assignment)
            && assignment.Completeness == AssignmentCompleteness.Complete;
    }

    private ProcedureCategory? FindProcedure(string procedureCode) =>
        _procedures.FirstOrDefault(item =>
            string.Equals(item.Id, procedureCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Code, procedureCode, StringComparison.OrdinalIgnoreCase));

    private ProcedureCategory? FindActiveProcedure(string procedureCode) =>
        _activeProcedures.FirstOrDefault(item =>
            string.Equals(item.Id, procedureCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Code, procedureCode, StringComparison.OrdinalIgnoreCase));

    // Sedation modifier: stored procedure codes for sedation cases are the eligible
    // primary code with a "+SED" suffix (e.g. "EXT+SED"). The base procedure code, the
    // sedation flag, and the combined case type are all derivable from this single token,
    // so no schema change or extra column is required and historical standalone "SED"
    // records remain readable.
    private const string SedationModifierSuffix = "+SED";

    private static bool HasSedationModifier(string? procedureCode) =>
        procedureCode is not null &&
        procedureCode.EndsWith(SedationModifierSuffix, StringComparison.OrdinalIgnoreCase);

    private static string StripSedationModifier(string procedureCode) =>
        HasSedationModifier(procedureCode)
            ? procedureCode[..^SedationModifierSuffix.Length]
            : procedureCode;

    private static string ComposeProcedureCode(string baseCode, bool sedation) =>
        sedation ? $"{baseCode}{SedationModifierSuffix}" : baseCode;

    // The historical standalone sedation code. New cases never store this (sedation is a
    // modifier), but legacy records may, so it is treated as sedation-related for counts.
    private const string LegacySedationCode = "SED";

    // True for composite "+SED" variants and for bare legacy standalone "SED".
    private static bool IsSedationProcedureCode(string? procedureCode) =>
        HasSedationModifier(procedureCode) ||
        string.Equals(procedureCode, LegacySedationCode, StringComparison.OrdinalIgnoreCase);

    // The base procedure a stored code rolls up under: "EXT+SED" -> "EXT". A non-composite
    // code (including bare legacy "SED") is its own base. Never throws; blank stays blank.
    private static string ResolveBaseProcedureCode(string? procedureCode) =>
        string.IsNullOrWhiteSpace(procedureCode)
            ? ""
            : HasSedationModifier(procedureCode)
                ? StripSedationModifier(procedureCode)
                : procedureCode;

    // Resolves a (possibly sedation-modified) stored code to a display category. For a
    // sedation case it synthesizes a combined category ("Extraction + Sedation") from the
    // base procedure while preserving the base icon and eligibility. Falls back to a plain
    // roster lookup for standalone codes, including historical "SED" records.
    private ProcedureCategory? ResolveProcedure(string? procedureCode)
    {
        if (string.IsNullOrWhiteSpace(procedureCode))
        {
            return null;
        }

        if (!HasSedationModifier(procedureCode))
        {
            return FindProcedure(procedureCode);
        }

        var baseProcedure = FindProcedure(StripSedationModifier(procedureCode));
        if (baseProcedure is null)
        {
            return FindProcedure(procedureCode);
        }

        return baseProcedure with
        {
            Id = $"{baseProcedure.Id}+sed",
            Code = ComposeProcedureCode(baseProcedure.Code, true),
            Label = $"{baseProcedure.Label} + Sedation"
        };
    }

    private static IEnumerable<Doctor> BuildDoctors(DoctorRosterOptions options, bool activeOnly = false) =>
        options.Doctors
            .Where(doctor => !activeOnly || doctor.Active)
            .Select(doctor => new Doctor(
                doctor.Id,
                doctor.DisplayName,
                doctor.ShortName,
                doctor.Color));

    private static IEnumerable<ProcedureCategory> BuildProcedures(ProcedureRosterOptions options, bool activeOnly = false) =>
        options.Procedures
            .Where(procedure => !activeOnly || procedure.Active)
            .Select(procedure => new ProcedureCategory(
                string.IsNullOrWhiteSpace(procedure.Id) ? procedure.Code.ToLowerInvariant() : procedure.Id,
                procedure.Code,
                procedure.Label,
                procedure.Icon,
                procedure.SedationEligible,
                procedure.AllocationBehavior,
                procedure.DefaultExpectedUnits));

    private static RoomState Available(int roomId) => new(roomId);

    private void AddMissingRooms()
    {
        var existingRoomIds = _rooms.Select(room => room.RoomId).ToHashSet();
        for (var roomId = 1; roomId <= _roomCount; roomId++)
        {
            if (!existingRoomIds.Contains(roomId))
            {
                _rooms.Add(Available(roomId));
            }
        }

        _rooms.Sort((left, right) => left.RoomId.CompareTo(right.RoomId));
    }

    private void SeedDemoRooms(DateTimeOffset now)
    {
        var thresholds = Thresholds;
        var seededRooms = Enumerable.Range(1, _roomCount)
            .Skip(1)
            .Zip(DemoSeedPatterns(), (roomId, pattern) => new { RoomId = roomId, Pattern = pattern });

        foreach (var seed in seededRooms)
        {
            var readyForDoctorAt = seed.Pattern.ReadyForDoctorElapsed is not null
                ? now - seed.Pattern.ReadyForDoctorElapsed(thresholds)
                : (DateTimeOffset?)null;
            SeedDemoRoom(
                seed.RoomId,
                seed.Pattern.DoctorId,
                seed.Pattern.ProcedureCode,
                now - seed.Pattern.Elapsed(thresholds),
                readyForDoctorAt);
        }
    }

    private static IEnumerable<DemoSeedPattern> DemoSeedPatterns()
    {
        // Rooms in Patient Seated / In Prep (no ReadyForDoctor yet)
        yield return new("otte", "CON", thresholds => BeforeAging(thresholds));
        yield return new("schroeder", "IMP", thresholds => AgingSample(thresholds));
        yield return new("gibson", "CON", thresholds => EarlySeatedSample(thresholds));
        yield return new("schroeder", "EXT", thresholds => StaleSample(thresholds, TimeSpan.FromMinutes(4)));

        // Canonical Ready rooms: ReadyForDoctorAt drives the projected ReadyUrgency.
        yield return new("pledger", "EXT",
            thresholds => StaleSample(thresholds),
            thresholds => StaleSample(thresholds));
        yield return new("gibson", "EXT+SED",
            thresholds => StaleSample(thresholds, TimeSpan.FromMinutes(11)),
            thresholds => StaleSample(thresholds, TimeSpan.FromMinutes(11)));
        yield return new("otte", "POST",
            thresholds => StaleSample(thresholds, TimeSpan.FromMinutes(5)),
            thresholds => AgingSample(thresholds));
        yield return new("pledger", "BX",
            thresholds => StaleSample(thresholds, TimeSpan.FromMinutes(15)),
            thresholds => StaleSample(thresholds, TimeSpan.FromMinutes(15)));
    }

    private static TimeSpan EarlySeatedSample(BoardThresholdOptions thresholds) =>
        TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(1).Ticks, thresholds.AgingThreshold.Ticks / 3));

    private static TimeSpan BeforeAging(BoardThresholdOptions thresholds) =>
        TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(1).Ticks, thresholds.AgingThreshold.Ticks - TimeSpan.FromMinutes(3).Ticks));

    private static TimeSpan AgingSample(BoardThresholdOptions thresholds) =>
        thresholds.AgingThreshold + TimeSpan.FromMinutes(1);

    private static TimeSpan StaleSample(BoardThresholdOptions thresholds) =>
        StaleSample(thresholds, TimeSpan.FromMinutes(1));

    private static TimeSpan StaleSample(BoardThresholdOptions thresholds, TimeSpan extraElapsed) =>
        thresholds.StaleThreshold + extraElapsed;

    // Bounds for the expected allocation snapshot (1 unit = 10 minutes). Kept consistent with
    // the seating UI stepper so a clamped value never disagrees between client and server.
    private const int MinExpectedUnits = 1;
    private const int MaxExpectedUnits = 24;

    // Captures the case-level expected allocation snapshot onto the active room at seating.
    // The default comes from the procedure roster; staff may override it with explicit units.
    // 1 unit = 10 minutes. Both the default and any override are clamped to [Min, Max] so an
    // explicit 0/negative value can never yield 0 expected minutes. AllocationAdjustedFromDefault
    // is true only when the final confirmed units differ from the (clamped) original default.
    // Operational metadata only - never PHI.
    private static void ApplyExpectedAllocation(RoomState room, ProcedureCategory procedure, int? expectedAllocationUnits)
    {
        var defaultUnits = Math.Clamp(procedure.DefaultExpectedUnits, MinExpectedUnits, MaxExpectedUnits);
        var finalUnits = Math.Clamp(expectedAllocationUnits ?? defaultUnits, MinExpectedUnits, MaxExpectedUnits);
        room.OriginalDefaultExpectedUnits = defaultUnits;
        room.ExpectedAllocationUnits = finalUnits;
        room.ExpectedAllocationMinutes = finalUnits * 10;
        room.AllocationAdjustedFromDefault = finalUnits != defaultUnits;
    }

    private static bool HasValidExpectedAllocation(ProcedureCategory procedure) =>
        procedure.DefaultExpectedUnits >= MinExpectedUnits;

    // Canonical-assignment domain validation. The RoomAssignmentContract only
    // guarantees its own shape; this confirms it is truthful against the current active roster before
    // it is persisted or promoted to Ready. Presence-conditional so partial drafts stay legal: any
    // present doctor must be active; any present procedure must resolve to an active procedure and
    // carry a sedation state - and "+SED" code representation - consistent with that procedure's
    // eligibility. When no procedure is present, expected allocation must be Unknown so clearing a
    // procedure cannot retain procedure-derived units.
    // Reuses the existing roster/procedure-resolution helpers; introduces no parallel validation model.
    private bool IsAssignmentValidForRoster(RoomAssignmentContract assignment)
    {
        if (assignment.DoctorId is { } doctorId
            && !_activeDoctors.Any(doctor => string.Equals(doctor.Id, doctorId, StringComparison.Ordinal)))
        {
            return false;
        }

        if (assignment.ProcedureCode is not { } procedureCode)
        {
            // No procedure: sedation is UnavailableNoProcedure by contract, and allocation must reset.
            return assignment.ExpectedAllocation.State == ExpectedAllocationState.Unknown;
        }

        var procedure = FindActiveProcedure(ResolveBaseProcedureCode(procedureCode));
        if (procedure is null)
        {
            return false;
        }

        var hasSedationModifier = HasSedationModifier(procedureCode);
        var sedationState = assignment.Sedation.State;
        if (!procedure.SedationEligible)
        {
            // Ineligible procedure: no "+SED" modifier and only the ineligible sedation state.
            return !hasSedationModifier && sedationState == SedationState.UnavailableProcedureIneligible;
        }

        // Eligible procedure: the "+SED" modifier must be present iff sedation resolved to Yes.
        return sedationState switch
        {
            SedationState.EligibleYes => hasSedationModifier,
            SedationState.EligibleNo or SedationState.EligibleUnresolved => !hasSedationModifier,
            _ => false
        };
    }

    private static RoomState CopyRoomState(RoomState room) =>
        new(room.RoomId)
        {
            EpisodeId = room.EpisodeId,
            AssignedDoctor = room.AssignedDoctor,
            AssignedDoctorDisplayName = room.AssignedDoctorDisplayName,
            ProcedureCode = room.ProcedureCode,
            ProcedureCategory = room.ProcedureCategory,
            SedationState = room.SedationState,
            ExpectedAllocationState = room.ExpectedAllocationState,
            ExpectedAllocationSuggestedUnits = room.ExpectedAllocationSuggestedUnits,
            ExpectedAllocationConfirmedUnits = room.ExpectedAllocationConfirmedUnits,
            ActiveReadyHandoffId = room.ActiveReadyHandoffId,
            AcceptedReadyHandoffId = room.AcceptedReadyHandoffId,
            State = room.State,
            PrestageStartedAt = room.PrestageStartedAt,
            SeatedAt = room.SeatedAt,
            AgingStartedAt = room.AgingStartedAt,
            StaleStartedAt = room.StaleStartedAt,
            ReadyForDoctorAt = room.ReadyForDoctorAt,
            DoctorArrivedAt = room.DoctorArrivedAt,
            DoctorCompleteAt = room.DoctorCompleteAt,
            RoomAvailableAt = room.RoomAvailableAt,
            OriginalDefaultExpectedUnits = room.OriginalDefaultExpectedUnits,
            ExpectedAllocationUnits = room.ExpectedAllocationUnits,
            ExpectedAllocationMinutes = room.ExpectedAllocationMinutes,
            AllocationAdjustedFromDefault = room.AllocationAdjustedFromDefault
        };

    private static void ApplyCommittedRoom(RoomState destination, RoomState source)
    {
        var committed = CopyRoomState(source);
        destination.EpisodeId = committed.EpisodeId;
        destination.AssignedDoctor = committed.AssignedDoctor;
        destination.AssignedDoctorDisplayName = committed.AssignedDoctorDisplayName;
        destination.ProcedureCode = committed.ProcedureCode;
        destination.ProcedureCategory = committed.ProcedureCategory;
        destination.SedationState = committed.SedationState;
        destination.ExpectedAllocationState = committed.ExpectedAllocationState;
        destination.ExpectedAllocationSuggestedUnits = committed.ExpectedAllocationSuggestedUnits;
        destination.ExpectedAllocationConfirmedUnits = committed.ExpectedAllocationConfirmedUnits;
        destination.ActiveReadyHandoffId = committed.ActiveReadyHandoffId;
        destination.AcceptedReadyHandoffId = committed.AcceptedReadyHandoffId;
        destination.State = committed.State;
        destination.PrestageStartedAt = committed.PrestageStartedAt;
        destination.SeatedAt = committed.SeatedAt;
        destination.AgingStartedAt = committed.AgingStartedAt;
        destination.StaleStartedAt = committed.StaleStartedAt;
        destination.ReadyForDoctorAt = committed.ReadyForDoctorAt;
        destination.DoctorArrivedAt = committed.DoctorArrivedAt;
        destination.DoctorCompleteAt = committed.DoctorCompleteAt;
        destination.RoomAvailableAt = committed.RoomAvailableAt;
        destination.OriginalDefaultExpectedUnits = committed.OriginalDefaultExpectedUnits;
        destination.ExpectedAllocationUnits = committed.ExpectedAllocationUnits;
        destination.ExpectedAllocationMinutes = committed.ExpectedAllocationMinutes;
        destination.AllocationAdjustedFromDefault = committed.AllocationAdjustedFromDefault;
    }

    private static void ApplyPersistedAssignment(RoomState room, PersistedRoomAssignment assignment)
    {
        room.AssignedDoctor = assignment.DoctorId;
        room.AssignedDoctorDisplayName = assignment.DoctorDisplayName;
        room.ProcedureCode = assignment.ProcedureCode;
        room.ProcedureCategory = assignment.ProcedureCategory;
        room.SedationState = assignment.SedationState;
        room.ExpectedAllocationState = assignment.ExpectedAllocationState;
        room.ExpectedAllocationSuggestedUnits = assignment.ExpectedAllocationSuggestedUnits;
        room.ExpectedAllocationConfirmedUnits = assignment.ExpectedAllocationConfirmedUnits;
        room.OriginalDefaultExpectedUnits = assignment.ExpectedAllocationSuggestedUnits ?? assignment.ExpectedAllocationConfirmedUnits ?? 0;
        room.ExpectedAllocationUnits = assignment.ExpectedAllocationConfirmedUnits ?? 0;
        room.ExpectedAllocationMinutes = room.ExpectedAllocationUnits * 10;
        room.AllocationAdjustedFromDefault = assignment.ExpectedAllocationSuggestedUnits.HasValue
            && assignment.ExpectedAllocationConfirmedUnits.HasValue
            && assignment.ExpectedAllocationSuggestedUnits != assignment.ExpectedAllocationConfirmedUnits;
    }

    private static PersistedRoomAssignment GetCanonicalAssignment(RoomState room) =>
        new(
            room.AssignedDoctor,
            room.AssignedDoctorDisplayName,
            room.ProcedureCode,
            room.ProcedureCategory,
            room.SedationState,
            room.ExpectedAllocationState,
            room.ExpectedAllocationSuggestedUnits,
            room.ExpectedAllocationConfirmedUnits);

    private static RoomAssignmentContract CreateLegacyCompatibleAssignment(
        Doctor doctor,
        ProcedureCategory procedure,
        bool sedation,
        int? expectedAllocationUnits)
    {
        var sedationContract = procedure.SedationEligible
            ? (sedation ? SedationContract.EligibleYes() : SedationContract.EligibleNo())
            : SedationContract.UnavailableProcedureIneligible();
        var defaultUnits = Math.Clamp(procedure.DefaultExpectedUnits, MinExpectedUnits, MaxExpectedUnits);
        var units = Math.Clamp(expectedAllocationUnits ?? procedure.DefaultExpectedUnits, MinExpectedUnits, MaxExpectedUnits);
        var allocation = units == defaultUnits
            ? ExpectedAllocationContract.ConfirmedSuggestedValue(units)
            : ExpectedAllocationContract.ConfirmedAdjustedValue(defaultUnits, units);
        return RoomAssignmentContract.Create(
            doctor.Id,
            ComposeProcedureCode(procedure.Code, sedation),
            sedationContract,
            allocation);
    }

    private static CompletedRoomCycle CopyCompletedCycle(CompletedRoomCycle cycle) =>
        new()
        {
            CompletedCycleId = cycle.CompletedCycleId,
            EpisodeId = cycle.EpisodeId,
            RoomId = cycle.RoomId,
            AcceptedReadyHandoffId = cycle.AcceptedReadyHandoffId,
            AssignedDoctor = cycle.AssignedDoctor,
            ProcedureCode = cycle.ProcedureCode,
            PrestageStartedAt = cycle.PrestageStartedAt,
            SeatedAt = cycle.SeatedAt,
            ReadyForDoctorAt = cycle.ReadyForDoctorAt,
            DoctorArrivedAt = cycle.DoctorArrivedAt,
            DoctorCompleteAt = cycle.DoctorCompleteAt,
            RoomAvailableAt = cycle.RoomAvailableAt,
            SeatedToDoctorSeconds = cycle.SeatedToDoctorSeconds,
            PrepSeconds = cycle.PrepSeconds,
            ReadyToDoctorSeconds = cycle.ReadyToDoctorSeconds,
            DoctorInRoomSeconds = cycle.DoctorInRoomSeconds,
            TurnoverSeconds = cycle.TurnoverSeconds,
            TotalRoomCycleSeconds = cycle.TotalRoomCycleSeconds,
            OriginalDefaultExpectedUnits = cycle.OriginalDefaultExpectedUnits,
            ExpectedAllocationUnits = cycle.ExpectedAllocationUnits,
            ExpectedAllocationMinutes = cycle.ExpectedAllocationMinutes,
            AllocationAdjustedFromDefault = cycle.AllocationAdjustedFromDefault,
            DoctorOccupiedWaitSeconds = cycle.DoctorOccupiedWaitSeconds,
            DoctorAvailableWaitSeconds = cycle.DoctorAvailableWaitSeconds,
            HasReportingException = cycle.HasReportingException,
            ReportingExceptionReasons = cycle.ReportingExceptionReasons.ToArray(),
            IsExcludedFromStandardMetrics = cycle.IsExcludedFromStandardMetrics,
            DisplayProcedureLabel = cycle.DisplayProcedureLabel,
            IsLegacyProcedure = cycle.IsLegacyProcedure,
            IsUnmappedProcedure = cycle.IsUnmappedProcedure,
            MeasuredCaseFlowMinutes = cycle.MeasuredCaseFlowMinutes,
            AllocationVarianceMinutes = cycle.AllocationVarianceMinutes,
            HasAllocationVariance = cycle.HasAllocationVariance,
            IsOverExpectedAllocation = cycle.IsOverExpectedAllocation,
            IsUnderExpectedAllocation = cycle.IsUnderExpectedAllocation,
            IsAtExpectedAllocation = cycle.IsAtExpectedAllocation,
            FinalWaitState = cycle.FinalWaitState,
            AgingThresholdReached = cycle.AgingThresholdReached,
            StaleThresholdReached = cycle.StaleThresholdReached,
            IsException = cycle.IsException,
            RequiresReview = cycle.RequiresReview,
            ExceptionReason = cycle.ExceptionReason,
            ReviewStatus = cycle.ReviewStatus,
            SuggestedAction = cycle.SuggestedAction,
            ReviewedAt = cycle.ReviewedAt,
            ReviewedBy = cycle.ReviewedBy
        };

    private static void ResetRoom(RoomState room)
    {
        room.EpisodeId = null;
        room.AssignedDoctor = null;
        room.AssignedDoctorDisplayName = null;
        room.ProcedureCode = null;
        room.ProcedureCategory = null;
        room.SedationState = null;
        room.ExpectedAllocationState = null;
        room.ExpectedAllocationSuggestedUnits = null;
        room.ExpectedAllocationConfirmedUnits = null;
        room.ActiveReadyHandoffId = null;
        room.AcceptedReadyHandoffId = null;
        room.State = RoomStates.Available;
        room.PrestageStartedAt = null;
        room.SeatedAt = null;
        room.AgingStartedAt = null;
        room.StaleStartedAt = null;
        room.ReadyForDoctorAt = null;
        room.DoctorArrivedAt = null;
        room.DoctorCompleteAt = null;
        room.RoomAvailableAt = null;
        room.OriginalDefaultExpectedUnits = 0;
        room.ExpectedAllocationUnits = 0;
        room.ExpectedAllocationMinutes = 0;
        room.AllocationAdjustedFromDefault = false;
    }

    private void SeedDemoRoom(int roomId, string doctorId, string procedureCode, DateTimeOffset seatedAt, DateTimeOffset? readyForDoctorAt = null)
    {
        var index = _rooms.FindIndex(room => room.RoomId == roomId);
        if (index >= 0)
        {
            if (!readyForDoctorAt.HasValue)
            {
                _rooms[index] = Seated(roomId, doctorId, procedureCode, seatedAt);
                return;
            }

            var doctor = _activeDoctors.FirstOrDefault(item => item.Id == doctorId)
                ?? throw new InvalidOperationException($"Demo seed doctor '{doctorId}' is not active.");
            var sedation = HasSedationModifier(procedureCode);
            var procedure = FindActiveProcedure(ResolveBaseProcedureCode(procedureCode))
                ?? throw new InvalidOperationException($"Demo seed procedure '{procedureCode}' is not active.");
            var assignment = CreateLegacyCompatibleAssignment(
                doctor,
                procedure,
                sedation,
                expectedAllocationUnits: null);
            var persistedAssignment = PersistedRoomAssignment.FromCanonicalContract(
                assignment,
                doctor.Name,
                ResolveProcedure(assignment.ProcedureCode)?.Label);
            var room = new RoomState(roomId)
            {
                EpisodeId = NewEpisodeId(),
                State = RoomStates.ReadyForDoctor,
                PrestageStartedAt = seatedAt - TimeSpan.FromMinutes(5),
                SeatedAt = seatedAt,
                ReadyForDoctorAt = readyForDoctorAt.Value
            };
            ApplyPersistedAssignment(room, persistedAssignment);
            var handoff = _repository.CreateReadyHandoff(
                room,
                assignment,
                readyForDoctorAt.Value,
                _doctors,
                _procedures);
            room.ActiveReadyHandoffId = handoff.HandoffId;
            _rooms[index] = room;
        }
    }

    private RoomState Seated(int roomId, string doctorId, string procedureCode, DateTimeOffset seatedAt)
    {
        var room = new RoomState(roomId)
        {
            AssignedDoctor = doctorId,
            ProcedureCode = procedureCode,
            State = RoomStates.Seated,
            SeatedAt = seatedAt
        };

        return room;
    }

    private static string NewEpisodeId() => Guid.NewGuid().ToString("N");

    private bool IsDemoElapsedMinutesAllowed(int demoElapsedMinutes) =>
        demoElapsedMinutes is >= 0 and <= 240
        && (demoElapsedMinutes == 0 || _demoOffsetsAllowed);

    private static bool CanBeginPrestage(RoomState room) =>
        room.State == RoomStates.Available && room.PrestageStartedAt is null && room.SeatedAt is null;

    private static bool CanSeat(RoomState room) =>
        room.State == RoomStates.Prestaging && room.PrestageStartedAt is not null && room.SeatedAt is null;

    private static bool CanCancelPrestage(RoomState room) =>
        room.State == RoomStates.Prestaging && room.PrestageStartedAt is not null && room.SeatedAt is null;

    private static bool CanCancelSeating(RoomState room) =>
        room.State is RoomStates.Seated or RoomStates.ReadyForDoctor or RoomStates.Aging or RoomStates.Stale;

    private static bool IsValidCancellationReason(string? cancellationReason) =>
        cancellationReason is null
            or CancellationReasons.PatientCanceled
            or CancellationReasons.NoShow
            or CancellationReasons.MovedRoom
            or CancellationReasons.SchedulingError
            or CancellationReasons.ProcedureChanged
            or CancellationReasons.Other;

    private static bool CanMarkReadyForDoctor(RoomState room) =>
        room.State is RoomStates.Seated;

    private static bool CanMarkDoctorArrived(RoomState room) =>
        room.State is RoomStates.ReadyForDoctor or RoomStates.Aging or RoomStates.Stale;

    private void AddEvent(RoomEvent roomEvent)
    {
        _events.Add(roomEvent);
        if (_events.Count > MaxRoomEvents)
        {
            _events.RemoveRange(0, _events.Count - MaxRoomEvents);
        }
    }

    private void PersistCycle(CompletedRoomCycle cycle) =>
        _repository.SaveCompletedCycle(cycle, _doctors, _procedures);

    private BoardThresholdOptions Thresholds => _thresholdOptions.CurrentValue;

    private DateTimeOffset Now => _timeProvider.GetUtcNow();

    private static int SecondsBetween(DateTimeOffset start, DateTimeOffset end) =>
        Math.Max(0, (int)Math.Round((end - start).TotalSeconds));

    private static double AverageSeconds(IEnumerable<int?> values)
    {
        var completed = values.Where(value => value.HasValue).Select(value => value!.Value).ToList();
        return completed.Count == 0 ? 0 : completed.Average();
    }

    private static double MedianSeconds(IEnumerable<int?> values)
    {
        var ordered = values
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Order()
            .ToList();
        if (ordered.Count == 0)
        {
            return 0;
        }

        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    private static IReadOnlyList<DoctorCycleSummary> BuildDoctorSummaries(IReadOnlyList<CompletedRoomCycle> cycles) =>
        cycles
            .Where(cycle => cycle.DoctorArrivedAt.HasValue)
            .GroupBy(cycle => new { cycle.AssignedDoctor, Month = new DateOnly(cycle.DoctorArrivedAt!.Value.Year, cycle.DoctorArrivedAt.Value.Month, 1) })
            .Select(group => new DoctorCycleSummary(
                group.Key.AssignedDoctor,
                group.Key.Month,
                group.Count(),
                AverageSeconds(group.Select(cycle => (int?)cycle.SeatedToDoctorSeconds)),
                MedianSeconds(group.Select(cycle => (int?)cycle.SeatedToDoctorSeconds)),
                AverageSeconds(group.Select(cycle => cycle.PrepSeconds)),
                MedianSeconds(group.Select(cycle => cycle.PrepSeconds)),
                AverageSeconds(group.Select(cycle => cycle.ReadyToDoctorSeconds)),
                MedianSeconds(group.Select(cycle => cycle.ReadyToDoctorSeconds)),
                AverageSeconds(group.Select(cycle => cycle.DoctorInRoomSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorInRoomSeconds)),
                AverageSeconds(group.Select(cycle => cycle.TurnoverSeconds)),
                MedianSeconds(group.Select(cycle => cycle.TurnoverSeconds)),
                group.Count(cycle => cycle.AgingThresholdReached),
                group.Count(cycle => cycle.StaleThresholdReached),
                AverageSeconds(group.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
                AverageSeconds(group.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
                BuildAllocationVarianceSummary(group)))
            .OrderByDescending(summary => summary.Month)
            .ThenBy(summary => summary.AssignedDoctor)
            .ToList();

    // Groups allocation-calculable standard cycles by (doctor, UTC calendar day of DoctorCompleteAt)
    // and produces a daily allocation-balance series per doctor. NetVarianceMinutes is the sum of each
    // day's per-cycle AllocationVarianceMinutes (measured case flow - expected allocation), the same
    // values BuildAllocationVarianceSummary nets, so a day's points roll up to the doctor's report
    // total. Only cycles with a calculable variance contribute (DoctorCompleteAt present and a positive
    // expected allocation), matching the card's "Cases" metric population. CaseCount is the number of
    // those calculable cycles on that day, so caseCount and netVarianceMinutes always share a population.
    private static IReadOnlyList<DoctorDailyAllocation> BuildDoctorDailyAllocationSeries(IReadOnlyList<CompletedRoomCycle> cycles) =>
        cycles
            .Where(cycle => cycle.AllocationVarianceMinutes.HasValue && cycle.DoctorCompleteAt.HasValue)
            .GroupBy(cycle => cycle.AssignedDoctor)
            .Select(doctorGroup => new DoctorDailyAllocation(
                doctorGroup.Key,
                doctorGroup
                    .GroupBy(cycle => DateOnly.FromDateTime(cycle.DoctorCompleteAt!.Value.UtcDateTime))
                    .OrderBy(dateGroup => dateGroup.Key)
                    .Select(dateGroup => new DoctorDailyAllocationPoint(
                        dateGroup.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        dateGroup.Count(),
                        dateGroup.Sum(cycle => cycle.AllocationVarianceMinutes!.Value)))
                    .ToList()))
            .OrderBy(item => item.DoctorId)
            .ToList();

    // Groups included completed cycles by (doctor, UTC calendar day of DoctorCompleteAt) and reports
    // the observed clinical-day shape from ChairSide events only. This intentionally does not infer
    // true schedule availability, vacation/meeting blocks, or appointment-book columns.
    private IReadOnlyList<ObservedDoctorDay> BuildObservedDoctorDays(IReadOnlyList<CompletedRoomCycle> cycles) =>
        cycles
            .Where(cycle => cycle.DoctorCompleteAt.HasValue && cycle.RoomAvailableAt.HasValue)
            .GroupBy(cycle => new
            {
                DoctorId = cycle.AssignedDoctor ?? "",
                ReportDate = DateOnly.FromDateTime(cycle.DoctorCompleteAt!.Value.UtcDateTime)
            })
            .Select(group =>
            {
                var dayCycles = group.ToList();
                var firstSeatedAt = dayCycles.Min(cycle => cycle.SeatedAt);
                var firstDoctorArrivedAt = dayCycles
                    .Where(cycle => cycle.DoctorArrivedAt.HasValue)
                    .Select(cycle => (DateTimeOffset?)cycle.DoctorArrivedAt!.Value)
                    .OrderBy(value => value)
                    .FirstOrDefault();
                var lastDoctorCompleteAt = dayCycles.Max(cycle => cycle.DoctorCompleteAt!.Value);
                var lastRoomAvailableAt = dayCycles.Max(cycle => cycle.RoomAvailableAt!.Value);
                var concurrency = BuildObservedRoomConcurrency(dayCycles);

                return new ObservedDoctorDay(
                    group.Key.DoctorId,
                    ResolveDoctorDisplayName(group.Key.DoctorId) ?? group.Key.DoctorId,
                    group.Key.ReportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    dayCycles.Count,
                    firstSeatedAt,
                    firstDoctorArrivedAt,
                    lastDoctorCompleteAt,
                    lastRoomAvailableAt,
                    ObservedLoadWholeMinutesBetween(firstSeatedAt, lastDoctorCompleteAt),
                    ObservedLoadWholeMinutesBetween(firstSeatedAt, lastRoomAvailableAt),
                    concurrency.MinutesWithOneActiveRoom,
                    concurrency.MinutesWithTwoActiveRooms,
                    concurrency.MinutesWithThreeOrMoreActiveRooms,
                    concurrency.MaxActiveRoomCount);
            })
            .OrderBy(day => day.ReportDate, StringComparer.Ordinal)
            .ThenBy(day => day.DoctorId, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static ObservedRoomConcurrency BuildObservedRoomConcurrency(IReadOnlyList<CompletedRoomCycle> cycles)
    {
        var events = new List<(DateTimeOffset At, int Delta)>();
        foreach (var cycle in cycles)
        {
            if (!cycle.DoctorCompleteAt.HasValue || cycle.DoctorCompleteAt.Value <= cycle.SeatedAt)
            {
                continue;
            }

            events.Add((cycle.SeatedAt, 1));
            events.Add((cycle.DoctorCompleteAt.Value, -1));
        }

        var activeRoomCount = 0;
        var maxActiveRoomCount = 0;
        var minutesWithOneActiveRoom = 0;
        var minutesWithTwoActiveRooms = 0;
        var minutesWithThreeOrMoreActiveRooms = 0;
        DateTimeOffset? previousAt = null;

        foreach (var point in events
            .GroupBy(item => item.At)
            .OrderBy(group => group.Key)
            .Select(group => new { At = group.Key, Delta = group.Sum(item => item.Delta) }))
        {
            if (previousAt.HasValue && point.At > previousAt.Value && activeRoomCount > 0)
            {
                var minutes = ObservedLoadWholeMinutesBetween(previousAt.Value, point.At);
                if (activeRoomCount == 1)
                {
                    minutesWithOneActiveRoom += minutes;
                }
                else if (activeRoomCount == 2)
                {
                    minutesWithTwoActiveRooms += minutes;
                }
                else
                {
                    minutesWithThreeOrMoreActiveRooms += minutes;
                }
            }

            activeRoomCount = Math.Max(0, activeRoomCount + point.Delta);
            maxActiveRoomCount = Math.Max(maxActiveRoomCount, activeRoomCount);
            previousAt = point.At;
        }

        return new ObservedRoomConcurrency(
            minutesWithOneActiveRoom,
            minutesWithTwoActiveRooms,
            minutesWithThreeOrMoreActiveRooms,
            maxActiveRoomCount);
    }

    private static int ObservedLoadWholeMinutesBetween(DateTimeOffset start, DateTimeOffset end) =>
        end <= start ? 0 : Math.Max(0, (int)(end - start).TotalMinutes);

    // Groups normal, non-exception completed cycles by procedure code and produces a per-procedure
    // baseline. The supplied cycles are the same normalCompletedCycles already used by the global
    // report, so exception and reviewed-exception cycles are excluded upstream, and the occupied /
    // available wait values are the ones already annotated by AnnotateOccupiedWait. This method
    // only groups and averages existing values; it does not recompute any wait metric.
    private IReadOnlyList<ProcedureCycleSummary> BuildProcedureSummaries(IReadOnlyList<CompletedRoomCycle> cycles) =>
        cycles
            .GroupBy(cycle => cycle.ProcedureCode ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProcedureCycleSummary(
                group.Key,
                ResolveProcedureLabel(group.Key),
                ResolveBaseProcedureCode(group.Key),
                IsSedationProcedureCode(group.Key),
                group.Count(),
                AverageSeconds(group.Select(cycle => cycle.TotalRoomCycleSeconds)),
                MedianSeconds(group.Select(cycle => cycle.TotalRoomCycleSeconds)),
                AverageSeconds(group.Select(cycle => cycle.ReadyToDoctorSeconds)),
                MedianSeconds(group.Select(cycle => cycle.ReadyToDoctorSeconds)),
                AverageSeconds(group.Select(cycle => cycle.DoctorInRoomSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorInRoomSeconds)),
                AverageSeconds(group.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
                AverageSeconds(group.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
                BuildAllocationVarianceSummary(group)))
            .OrderByDescending(summary => summary.CompletedCycleCount)
            .ThenBy(summary => summary.ProcedureLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Base-procedure roll-up: groups the same normal completed cycles by base procedure code
    // ("EXT" and "EXT+SED" both roll up under "EXT") and computes each metric directly from the
    // raw cycles - it does NOT recombine the per-variant BuildProcedureSummaries values, so
    // medians and averages stay accurate. IsSedationCase is false on these roll-up rows because a
    // base row is not a single sedation variant; per-variant sedation detail stays in
    // ProcedureSummaries and the report-level sedation counts.
    private IReadOnlyList<ProcedureCycleSummary> BuildBaseProcedureSummaries(IReadOnlyList<CompletedRoomCycle> cycles) =>
        cycles
            .GroupBy(cycle => ResolveBaseProcedureCode(cycle.ProcedureCode), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProcedureCycleSummary(
                group.Key,
                ResolveProcedureLabel(group.Key),
                group.Key,
                false,
                group.Count(),
                AverageSeconds(group.Select(cycle => cycle.TotalRoomCycleSeconds)),
                MedianSeconds(group.Select(cycle => cycle.TotalRoomCycleSeconds)),
                AverageSeconds(group.Select(cycle => cycle.ReadyToDoctorSeconds)),
                MedianSeconds(group.Select(cycle => cycle.ReadyToDoctorSeconds)),
                AverageSeconds(group.Select(cycle => cycle.DoctorInRoomSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorInRoomSeconds)),
                AverageSeconds(group.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
                AverageSeconds(group.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorOccupiedWaitSeconds)),
                MedianSeconds(group.Select(cycle => cycle.DoctorAvailableWaitSeconds)),
                BuildAllocationVarianceSummary(group)))
            .OrderByDescending(summary => summary.CompletedCycleCount)
            .ThenBy(summary => summary.ProcedureLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Per-doctor procedure-variant mix over the same standard completed-cycle population as
    // BuildProcedureSummaries / BuildObservedDoctorDays. Grouping is variant-level (matching
    // BuildProcedureSummaries), so "EXT" and "EXT+SED" stay separate rows with sedation carried
    // as a modifier via IsSedationCase + BaseProcedureCode - never a separately timed component.
    // Cycles with a blank doctor are dropped (no doctor to attribute a share to). Each row's
    // DoctorCompletedCaseCount is that doctor's total rows in this population, so per-doctor shares
    // sum to 1. This only counts and groups existing cycles; it computes no timing metric.
    private IReadOnlyList<DoctorProcedureMixRow> BuildDoctorProcedureMix(IReadOnlyList<CompletedRoomCycle> cycles) =>
        cycles
            .Where(cycle => !string.IsNullOrWhiteSpace(cycle.AssignedDoctor))
            .GroupBy(cycle => cycle.AssignedDoctor!, StringComparer.OrdinalIgnoreCase)
            .SelectMany(doctorGroup =>
            {
                var doctorCompletedCaseCount = doctorGroup.Count();
                return doctorGroup
                    .GroupBy(cycle => cycle.ProcedureCode ?? "", StringComparer.OrdinalIgnoreCase)
                    .Select(procedureGroup => new DoctorProcedureMixRow(
                        doctorGroup.Key,
                        procedureGroup.Key,
                        ResolveProcedureLabel(procedureGroup.Key),
                        ResolveBaseProcedureCode(procedureGroup.Key),
                        IsSedationProcedureCode(procedureGroup.Key),
                        procedureGroup.Count(),
                        doctorCompletedCaseCount,
                        doctorCompletedCaseCount == 0
                            ? 0d
                            : (double)procedureGroup.Count() / doctorCompletedCaseCount));
            })
            .OrderBy(row => row.DoctorId, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(row => row.CaseCount)
            .ThenBy(row => row.ProcedureLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Resolves a display label for a procedure code: prefer the roster label, then the raw code,
    // then "Unknown". Never throws on a blank or unknown code so reports cannot crash.
    private string ResolveProcedureLabel(string procedureCode)
    {
        var label = ResolveProcedure(procedureCode)?.Label;
        if (!string.IsNullOrWhiteSpace(label))
        {
            return label;
        }

        return string.IsNullOrWhiteSpace(procedureCode) ? "Unknown" : procedureCode;
    }

    // Conservative reporting-time duration thresholds (first pass). A case flow (Seat -> Doctor
    // Complete) over 4h, or a room cycle (Seat -> Room Available) over 6h, is treated as extreme.
    private static readonly TimeSpan ExtremeCaseFlowThreshold = TimeSpan.FromHours(4);
    private static readonly TimeSpan ExtremeRoomCycleThreshold = TimeSpan.FromHours(6);

    // Annotates each completed cycle with neutral, non-PHI reporting-time exception metadata,
    // derived from the cycle's own data. Never mutates persisted state or the manual review queue.
    // Flagged cycles stay visible in raw/audit output but are excluded from standard aggregates.
    private void AnnotateReportingExceptions(IReadOnlyList<CompletedRoomCycle> cycles)
    {
        foreach (var cycle in cycles)
        {
            var reasons = new List<string>();

            var (isLegacy, isUnmapped) = ClassifyProcedureMapping(cycle.ProcedureCode);
            if (isUnmapped)
            {
                reasons.Add(ReportingExceptionReasons.UnmappedProcedure);
            }
            else if (isLegacy)
            {
                reasons.Add(ReportingExceptionReasons.LegacyProcedure);
            }

            if (HasExtremeDuration(cycle))
            {
                reasons.Add(ReportingExceptionReasons.ExtremeDuration);
            }

            if (CrossesCalendarDay(cycle))
            {
                reasons.Add(ReportingExceptionReasons.OvernightLifecycle);
            }

            if (cycle.DoctorArrivedAt is null)
            {
                reasons.Add(ReportingExceptionReasons.MissingTiming);
            }

            cycle.IsLegacyProcedure = isLegacy;
            cycle.IsUnmappedProcedure = isUnmapped;
            cycle.ReportingExceptionReasons = reasons;
            cycle.HasReportingException = reasons.Count > 0;
            cycle.IsExcludedFromStandardMetrics = reasons.Count > 0;
            cycle.DisplayProcedureLabel = BuildDisplayProcedureLabel(cycle.ProcedureCode, isLegacy, isUnmapped);
        }
    }

    // Maps a stored procedure code to current-roster status. Legacy = resolvable in the full roster
    // but no longer active (e.g. standalone "SED" now that sedation is a modifier); Unmapped = not
    // resolvable at all. A blank code is neither (caught instead by MissingTiming).
    private (bool IsLegacy, bool IsUnmapped) ClassifyProcedureMapping(string? procedureCode)
    {
        if (string.IsNullOrWhiteSpace(procedureCode))
        {
            return (false, false);
        }

        var baseCode = ResolveBaseProcedureCode(procedureCode);
        if (FindActiveProcedure(baseCode) is not null)
        {
            return (false, false);
        }

        return FindProcedure(baseCode) is not null ? (true, false) : (false, true);
    }

    private static bool HasExtremeDuration(CompletedRoomCycle cycle)
    {
        if (cycle.DoctorCompleteAt is { } completeAt && completeAt - cycle.SeatedAt > ExtremeCaseFlowThreshold)
        {
            return true;
        }

        return cycle.RoomAvailableAt is { } availableAt && availableAt - cycle.SeatedAt > ExtremeRoomCycleThreshold;
    }

    // Calendar-day crossing is evaluated in UTC (the storage clock). A first-pass overnight signal;
    // a future patch may evaluate it in the clinic timezone.
    private static bool CrossesCalendarDay(CompletedRoomCycle cycle)
    {
        var seatedDate = cycle.SeatedAt.UtcDateTime.Date;
        if (cycle.DoctorCompleteAt is { } completeAt && completeAt.UtcDateTime.Date != seatedDate)
        {
            return true;
        }

        return cycle.RoomAvailableAt is { } availableAt && availableAt.UtcDateTime.Date != seatedDate;
    }

    private string BuildDisplayProcedureLabel(string? procedureCode, bool isLegacy, bool isUnmapped)
    {
        var label = ResolveProcedureLabel(procedureCode ?? "");
        if (isUnmapped)
        {
            return $"{label} (Unmapped)";
        }

        return isLegacy ? $"{label} (Legacy)" : label;
    }

    // Computes per-cycle measured case flow (SeatedAt -> DoctorCompleteAt) and allocation variance
    // (measured - expected allocation minutes). Derived, never persisted. Variance is only computed
    // when DoctorCompleteAt is present and the cycle carries a positive expected allocation; otherwise
    // the variance fields stay null/false. Measured case flow is exposed whenever DoctorCompleteAt
    // exists, independent of expected allocation.
    private static void AnnotateAllocationVariance(IReadOnlyList<CompletedRoomCycle> cycles)
    {
        foreach (var cycle in cycles)
        {
            cycle.MeasuredCaseFlowMinutes = null;
            cycle.AllocationVarianceMinutes = null;
            cycle.HasAllocationVariance = false;
            cycle.IsOverExpectedAllocation = false;
            cycle.IsUnderExpectedAllocation = false;
            cycle.IsAtExpectedAllocation = false;

            if (cycle.DoctorCompleteAt is not { } completeAt)
            {
                continue;
            }

            var measuredMinutes = Math.Max(0, (int)Math.Round((completeAt - cycle.SeatedAt).TotalMinutes));
            cycle.MeasuredCaseFlowMinutes = measuredMinutes;

            if (cycle.ExpectedAllocationMinutes <= 0)
            {
                continue;
            }

            var variance = measuredMinutes - cycle.ExpectedAllocationMinutes;
            cycle.AllocationVarianceMinutes = variance;
            cycle.IsOverExpectedAllocation = variance > 0;
            cycle.IsUnderExpectedAllocation = variance < 0;
            cycle.IsAtExpectedAllocation = variance == 0;
            cycle.HasAllocationVariance = variance != 0;
        }
    }

    // Builds an allocation variance aggregate over the supplied completed cycles. Only cycles with a
    // calculable variance (AllocationVarianceMinutes set by AnnotateAllocationVariance) contribute to
    // the totals and over/under/at counts; AdjustedAllocationCycleCount counts adjusted-from-default
    // cycles across the whole supplied population. Callers pass standard/included completed cycles.
    private static AllocationVarianceSummary BuildAllocationVarianceSummary(IEnumerable<CompletedRoomCycle> cycles)
    {
        var population = cycles.ToList();
        var contributing = population.Where(cycle => cycle.AllocationVarianceMinutes.HasValue).ToList();

        var count = contributing.Count;
        var totalExpected = contributing.Sum(cycle => cycle.ExpectedAllocationMinutes);
        var totalMeasured = contributing.Sum(cycle => cycle.MeasuredCaseFlowMinutes ?? 0);
        var net = totalMeasured - totalExpected;

        return new AllocationVarianceSummary(
            count,
            totalExpected,
            totalMeasured,
            net,
            count == 0 ? 0 : (double)net / count,
            contributing.Count(cycle => cycle.IsOverExpectedAllocation),
            contributing.Count(cycle => cycle.IsUnderExpectedAllocation),
            contributing.Count(cycle => cycle.IsAtExpectedAllocation),
            population.Count(cycle => cycle.AllocationAdjustedFromDefault));
    }

    // Sets DoctorOccupiedWaitSeconds and DoctorAvailableWaitSeconds on each cycle in
    // cyclesToAnnotate. Occupied time is the portion of each cycle's
    // ReadyForDoctorAt -> DoctorArrivedAt window where the same assigned doctor was
    // physically in another room. Blockers come from blockerPool; exception cycles in
    // that pool are excluded per spec.
    private static void AnnotateOccupiedWait(
        IReadOnlyList<CompletedRoomCycle> cyclesToAnnotate,
        IReadOnlyList<CompletedRoomCycle> blockerPool)
    {
        var eligibleBlockers = blockerPool
            .Where(c =>
                !c.IsException &&
                c.DoctorArrivedAt.HasValue &&
                c.DoctorCompleteAt.HasValue &&
                c.DoctorCompleteAt.Value > c.DoctorArrivedAt.Value)
            .ToList();

        foreach (var cycle in cyclesToAnnotate)
        {
            if (!cycle.ReadyForDoctorAt.HasValue || !cycle.DoctorArrivedAt.HasValue || cycle.ReadyToDoctorSeconds is null)
            {
                cycle.DoctorOccupiedWaitSeconds = null;
                cycle.DoctorAvailableWaitSeconds = null;
                continue;
            }

            var sameDocOtherIntervals = eligibleBlockers
                .Where(other =>
                    other.AssignedDoctor == cycle.AssignedDoctor &&
                    !(other.RoomId == cycle.RoomId && other.SeatedAt == cycle.SeatedAt))
                .Select(other => (Start: other.DoctorArrivedAt!.Value, End: other.DoctorCompleteAt!.Value))
                .ToList();

            var occupied = ComputeOverlapSeconds(
                cycle.ReadyForDoctorAt.Value,
                cycle.DoctorArrivedAt.Value,
                sameDocOtherIntervals);

            var readyToDoctor = cycle.ReadyToDoctorSeconds.Value;
            var clamped = Math.Min(occupied, readyToDoctor);
            cycle.DoctorOccupiedWaitSeconds = clamped;
            cycle.DoctorAvailableWaitSeconds = readyToDoctor - clamped;
        }
    }

    // Returns the total seconds that the union of intervals overlaps with [windowStart, windowEnd].
    private static int ComputeOverlapSeconds(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        List<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        if (intervals.Count == 0 || windowEnd <= windowStart)
        {
            return 0;
        }

        long totalTicks = 0;
        foreach (var (start, end) in MergeIntervals(intervals))
        {
            var overlapStart = start > windowStart ? start : windowStart;
            var overlapEnd = end < windowEnd ? end : windowEnd;
            if (overlapEnd > overlapStart)
            {
                totalTicks += (overlapEnd - overlapStart).Ticks;
            }
        }

        return (int)Math.Round(totalTicks / (double)TimeSpan.TicksPerSecond);
    }

    // Returns a new list of intervals with overlapping/adjacent entries merged,
    // sorted ascending by start time.
    private static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> MergeIntervals(
        IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        var sorted = intervals.OrderBy(i => i.Start).ToList();
        var result = new List<(DateTimeOffset Start, DateTimeOffset End)>();

        foreach (var (start, end) in sorted)
        {
            if (result.Count == 0 || start >= result[^1].End)
            {
                result.Add((start, end));
            }
            else if (end > result[^1].End)
            {
                result[^1] = (result[^1].Start, end);
            }
        }

        return result;
    }
}

public sealed record BoardSnapshot(
    DateTimeOffset ServerTime,
    int RoomCount,
    int AgingMinutes,
    int StaleMinutes,
    TimeSpan AgingThreshold,
    TimeSpan StaleThreshold,
    bool DemoTimerEnabled,
    IReadOnlyList<Doctor> Doctors,
    IReadOnlyList<ProcedureCategory> Procedures,
    IReadOnlyList<RoomStatus> Rooms,
    IReadOnlyList<RoomEvent> RecentEvents);

public sealed record Doctor(string Id, string Name, string ShortName, string Color);

public sealed record ProcedureCategory(
    string Id,
    string Code,
    string Label,
    string Icon,
    bool SedationEligible = false,
    string AllocationBehavior = AllocationBehaviors.Variable,
    int DefaultExpectedUnits = 1);

public sealed record RoomStatus(
    int RoomId,
    int Number,
    string? AssignedDoctor,
    string? ProcedureCode,
    string State,
    Doctor? Doctor,
    ProcedureCategory? Procedure,
    DateTimeOffset? SeatedAt,
    DateTimeOffset? AgingStartedAt,
    DateTimeOffset? StaleStartedAt,
    DateTimeOffset? ReadyForDoctorAt,
    DateTimeOffset? DoctorArrivedAt,
    DateTimeOffset? DoctorCompleteAt,
    DateTimeOffset? RoomAvailableAt,
    TimeSpan Elapsed,
    int OriginalDefaultExpectedUnits = 0,
    int ExpectedAllocationUnits = 0,
    int ExpectedAllocationMinutes = 0,
    bool AllocationAdjustedFromDefault = false,
    ReadyUrgency ReadyUrgency = ReadyUrgency.None,
    IReadOnlyList<RoomIntegrityFault>? IntegrityFaults = null,
    string? EpisodeId = null,
    DateTimeOffset? PrestageStartedAt = null,
    RoomAssignmentContract? Assignment = null,
    bool AssignmentLocked = false,
    string? ActiveReadyHandoffId = null,
    string? AcceptedReadyHandoffId = null);

public sealed record RoomEvent(
    int RoomNumber,
    string EventType,
    DateTimeOffset Timestamp,
    string? DoctorId,
    string? ProcedureCode,
    TimeSpan? Duration = null);

public sealed record ReportsSnapshot(
    int CompletedRoomCyclesCount,
    double AverageSeatedToDoctorSeconds,
    double MedianSeatedToDoctorSeconds,
    double AveragePrepSeconds,
    double MedianPrepSeconds,
    double AverageReadyToDoctorSeconds,
    double MedianReadyToDoctorSeconds,
    double AverageDoctorInRoomSeconds,
    double MedianDoctorInRoomSeconds,
    double AverageTurnoverSeconds,
    double MedianTurnoverSeconds,
    int AgingEventCount,
    int StaleEventCount,
    double AverageDoctorOccupiedWaitSeconds,
    double MedianDoctorOccupiedWaitSeconds,
    double AverageDoctorAvailableWaitSeconds,
    double MedianDoctorAvailableWaitSeconds,
    IReadOnlyList<DoctorCycleSummary> DoctorSummaries,
    IReadOnlyList<CompletedRoomCycle> RecentCompletedCycles,
    IReadOnlyList<CompletedRoomCycle> ExceptionCycles,
    IReadOnlyList<ProcedureCycleSummary> ProcedureSummaries,
    // Additive (appended) reporting fields. Counts and base-procedure summaries are
    // computed server-side over the same normal completed-cycle population as
    // ProcedureSummaries so the frontend never has to approximate from RecentCompletedCycles
    // or recombine per-variant averages/medians. SedationCaseCount + NonSedationCaseCount
    // equals IncludedCompletedCycleCount; reporting-exception completed cycles are excluded
    // from this included partition.
    int SedationCaseCount,
    int NonSedationCaseCount,
    IReadOnlyList<ProcedureCycleSummary> BaseProcedureSummaries,
    // Reporting data-hygiene counts. Standard aggregates above are computed over the included
    // (non-reporting-exception) population; excluded cycles remain visible in RecentCompletedCycles
    // with their reason metadata. IncludedCompletedCycleCount + ExcludedCompletedCycleCount equals
    // CompletedRoomCyclesCount.
    int IncludedCompletedCycleCount = 0,
    int ExcludedCompletedCycleCount = 0,
    int ExceptionCount = 0,
    // Allocation variance over standard/included completed cycles (expected vs measured case flow).
    AllocationVarianceSummary? AllocationVariance = null,
    // Active report window metadata. Dates are ISO yyyy-MM-dd (null = unbounded). RangeLabel is a
    // plain-English summary ("All time" or "Jun 17 – Jun 24"). TotalCompletedCycleCount is the
    // all-time completed total for "X of Y" context, independent of the selected window.
    string? RangeStartDate = null,
    string? RangeEndDate = null,
    string RangeLabel = "All time",
    int TotalCompletedCycleCount = 0,
    // Per-doctor daily allocation balance for the selected report range, used for sparklines on
    // doctor cards. Each point carries the day's net allocation variance minutes (measured case
    // flow - expected allocation) and case count over that day's allocation-calculable cycles.
    // Derived from the same standard (non-exception, non-reporting-exception) cycle population as
    // DoctorSummaries and respects the active date filter, so points are never capped or truncated.
    // Null when not yet populated (additive; existing callers unaffected).
    IReadOnlyList<DoctorDailyAllocation>? DoctorDailyAllocationSeries = null,
    // Schedule-fit read model (expected vs measured case flow expressed as minutes, blocks, slack,
    // debt, and utilization) over the same standard completed-cycle population as AllocationVariance,
    // so the two always agree on shared totals. A bridge for future Reports/Workshop UI; no UI consumes
    // it yet. GetReports always populates this; the nullable default only keeps the positional record
    // contract additive (existing callers unaffected).
    ScheduleFitReport? ScheduleFit = null,
    // Weekly historical wait trend over the same standard completed-cycle population as the main
    // report aggregates. Additive, summary-only, and not rendered by the frontend yet.
    ReportTrendSnapshot? Trends = null,
    // Observed doctor/day load over the same standard completed-cycle population as ScheduleFit and
    // Trends. Derived only from ChairSide room events; does not infer true schedule availability or
    // appointment-book columns.
    IReadOnlyList<ObservedDoctorDay>? ObservedDoctorDays = null,
    // Per-doctor procedure-variant mix over the same standard completed-cycle population. Additive
    // read model for the selected-doctor Procedure Mix tab; no existing metric semantics change.
    IReadOnlyList<DoctorProcedureMixRow>? DoctorProcedureMix = null);

public sealed record DoctorProcedureMixRow(
    string DoctorId,
    // Variant-level code ("EXT" vs "EXT+SED"). BaseProcedureCode strips the sedation modifier and
    // IsSedationCase marks the "+SED"/legacy "SED" variants, so sedation stays a modifier of the
    // primary procedure rather than a separate row concept.
    string ProcedureCode,
    string ProcedureLabel,
    string BaseProcedureCode,
    bool IsSedationCase,
    int CaseCount,
    // That doctor's total included completed cases in this population; per-doctor rows' CaseCounts
    // sum to it, and ShareOfDoctorCases = CaseCount / DoctorCompletedCaseCount (0..1).
    int DoctorCompletedCaseCount,
    double ShareOfDoctorCases);

public sealed record ObservedDoctorDay(
    string DoctorId,
    string DoctorName,
    string ReportDate,
    int EncounterCount,
    DateTimeOffset FirstSeatedAt,
    DateTimeOffset? FirstDoctorArrivedAt,
    DateTimeOffset LastDoctorCompleteAt,
    DateTimeOffset LastRoomAvailableAt,
    int ObservedClinicalSpanMinutes,
    int ObservedTeamSpanMinutes,
    int MinutesWithOneActiveRoom,
    int MinutesWithTwoActiveRooms,
    int MinutesWithThreeOrMoreActiveRooms,
    int MaxActiveRoomCount);

internal readonly record struct ObservedRoomConcurrency(
    int MinutesWithOneActiveRoom,
    int MinutesWithTwoActiveRooms,
    int MinutesWithThreeOrMoreActiveRooms,
    int MaxActiveRoomCount);

/// <summary>
/// A completed-cycle reporting window. Dates are interpreted as whole UTC calendar days (start
/// inclusive at 00:00, end inclusive through 23:59:59.9999999 via an exclusive next-day bound).
/// The UTC assumption is isolated here so a clinic-timezone abstraction can replace it later without
/// touching report calculations.
/// </summary>
public readonly record struct ReportDateRange(
    DateOnly? StartDate,
    DateOnly? EndDate,
    DateTimeOffset? FromInclusive,
    DateTimeOffset? ToExclusive)
{
    public static ReportDateRange AllTime => new(null, null, null, null);

    public bool IsAllTime => FromInclusive is null && ToExclusive is null;

    public string? StartDateText => StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string? EndDateText => EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Builds a range from calendar dates. A reversed (start > end) pair is normalized by swapping
    // so the report still renders a sensible window instead of erroring.
    public static ReportDateRange FromDates(DateOnly? start, DateOnly? end)
    {
        if (start is null && end is null)
        {
            return AllTime;
        }

        if (start is { } s && end is { } e && s > e)
        {
            (start, end) = (end, start);
        }

        DateTimeOffset? from = start is { } s2
            ? new DateTimeOffset(s2.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : null;
        DateTimeOffset? to = end is { } e2
            ? new DateTimeOffset(e2.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : null;

        return new ReportDateRange(start, end, from, to);
    }

    // Parses ISO yyyy-MM-dd query strings. Unparseable values are treated as absent (graceful, never
    // throws) so a malformed query degrades to a wider/all-time window rather than crashing.
    public static ReportDateRange FromDateStrings(string? from, string? to) =>
        FromDates(ParseDate(from), ParseDate(to));

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    public bool Includes(DateTimeOffset? anchor)
    {
        if (IsAllTime)
        {
            return true;
        }

        if (anchor is null)
        {
            return false;
        }

        var value = anchor.Value.ToUniversalTime();
        if (FromInclusive is { } from && value < from)
        {
            return false;
        }

        return ToExclusive is not { } to || value < to;
    }

    public string Label
    {
        get
        {
            if (StartDate is null && EndDate is null)
            {
                return "All time";
            }

            if (StartDate is { } start && EndDate is { } end)
            {
                return $"{FormatDay(start)} – {FormatDay(end)}";
            }

            return StartDate is { } startOnly
                ? $"From {FormatDay(startOnly)}"
                : $"Through {FormatDay(EndDate!.Value)}";
        }
    }

    private static string FormatDay(DateOnly day) => day.ToString("MMM d", CultureInfo.InvariantCulture);
}

public sealed class CompletedRoomCycle
{
    // Stable unique identity for this completed cycle, assigned by SQLite and mapped from
    // the completed_room_cycles.id column. Zero until the cycle has been persisted at least
    // once. Reporting and exception actions can target a single cycle by this value without
    // relying on the legacy (RoomId, SeatedAt) compound key.
    public long CompletedCycleId { get; set; }

    // Opaque per-episode identity minted at Begin Prestage and carried through the whole
    // occupancy episode into this completed cycle. Null on legacy rows persisted before the
    // Prestaging feature. Distinct from CompletedCycleId (a per-row SQLite id): EpisodeId ties a
    // completed cycle back to the single occupancy episode it belongs to, so an episode can be
    // proven to appear in exactly one of completed_room_cycles / aborted_room_assignments.
    public string? EpisodeId { get; set; }
    public string? AcceptedReadyHandoffId { get; set; }
    public int RoomId { get; set; }
    public string AssignedDoctor { get; set; } = "";
    public string ProcedureCode { get; set; } = "";

    // Room-unavailable clock start. Set when the episode began with Begin Prestage; null on legacy
    // rows or any cycle whose room was never prestaged. When present, room-unavailable time for a
    // successful cycle is measured from here rather than from SeatedAt. Not yet consumed by any
    // report calculation.
    public DateTimeOffset? PrestageStartedAt { get; set; }
    public DateTimeOffset SeatedAt { get; set; }
    public DateTimeOffset? ReadyForDoctorAt { get; set; }
    /// <summary>
    /// Null when the cycle was force-expired before the doctor arrived (e.g. abandoned or after-hours sweep).
    /// </summary>
    public DateTimeOffset? DoctorArrivedAt { get; set; }
    public DateTimeOffset? DoctorCompleteAt { get; set; }
    public DateTimeOffset? RoomAvailableAt { get; set; }
    public int SeatedToDoctorSeconds { get; set; }
    public int? PrepSeconds { get; set; }
    public int? ReadyToDoctorSeconds { get; set; }
    public int? DoctorInRoomSeconds { get; set; }
    public int? TurnoverSeconds { get; set; }
    public int? TotalRoomCycleSeconds { get; set; }

    // Expected allocation snapshot (operational, non-PHI), copied from the active room when the
    // cycle is created. Preserved through the rest of the lifecycle and across restart so reporting
    // can compare measured case flow against the final confirmed allocation. 1 unit = 10 minutes.
    public int OriginalDefaultExpectedUnits { get; set; }
    public int ExpectedAllocationUnits { get; set; }
    public int ExpectedAllocationMinutes { get; set; }
    public bool AllocationAdjustedFromDefault { get; set; }

    // Computed at report time from cross-cycle doctor-occupied intervals. Not persisted to storage.
    public int? DoctorOccupiedWaitSeconds { get; set; }
    public int? DoctorAvailableWaitSeconds { get; set; }

    // Reporting-time exception classification (operational, non-PHI). Derived from this cycle's own
    // data on each GetReports call and never persisted. Flagged cycles stay visible in raw/audit
    // output but are excluded from standard aggregates. See ReportingExceptionReasons.
    public bool HasReportingException { get; set; }
    public IReadOnlyList<string> ReportingExceptionReasons { get; set; } = [];
    public bool IsExcludedFromStandardMetrics { get; set; }
    public string DisplayProcedureLabel { get; set; } = "";
    public bool IsLegacyProcedure { get; set; }
    public bool IsUnmappedProcedure { get; set; }

    // Allocation variance (operational, non-PHI), computed at report time and never persisted.
    // Measured case flow = SeatedAt -> DoctorCompleteAt. Variance = measured - expected allocation.
    // Both are null when not calculable (no DoctorCompleteAt, or no expected allocation minutes).
    // Positive variance = ran over expected; negative = ran under. Neutral framing only.
    public int? MeasuredCaseFlowMinutes { get; set; }
    public int? AllocationVarianceMinutes { get; set; }
    public bool HasAllocationVariance { get; set; }
    public bool IsOverExpectedAllocation { get; set; }
    public bool IsUnderExpectedAllocation { get; set; }
    public bool IsAtExpectedAllocation { get; set; }

    public string FinalWaitState { get; set; } = "";
    public bool AgingThresholdReached { get; set; }
    public bool StaleThresholdReached { get; set; }

    // Exception classification - set by admin or future exception policy.
    // When IsException is true this cycle is excluded from normal metrics
    // and surfaced in the Exceptions Requiring Review section.
    public bool IsException { get; set; }
    public bool RequiresReview { get; set; }
    public string? ExceptionReason { get; set; }
    public string ReviewStatus { get; set; } = ReviewStatuses.PendingReview;
    public string? SuggestedAction { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewedBy { get; set; }
}

public sealed record DoctorCycleSummary(
    string AssignedDoctor,
    DateOnly Month,
    int CompletedRoomCyclesCount,
    double AverageSeatedToDoctorSeconds,
    double MedianSeatedToDoctorSeconds,
    double AveragePrepSeconds,
    double MedianPrepSeconds,
    double AverageReadyToDoctorSeconds,
    double MedianReadyToDoctorSeconds,
    double AverageDoctorInRoomSeconds,
    double MedianDoctorInRoomSeconds,
    double AverageTurnoverSeconds,
    double MedianTurnoverSeconds,
    int AgingEventCount,
    int StaleEventCount,
    double AverageDoctorOccupiedWaitSeconds,
    double MedianDoctorOccupiedWaitSeconds,
    double AverageDoctorAvailableWaitSeconds,
    double MedianDoctorAvailableWaitSeconds,
    AllocationVarianceSummary Allocation);

// Allocation variance aggregate (operational, non-PHI). Reused for the global report, per-doctor,
// and per-procedure-family summaries. Computed over standard/included completed cycles only.
// Counts reflect cycles where variance is calculable (expected allocation present and case flow
// measurable); AdjustedAllocationCycleCount reflects how many of the population had units adjusted
// from the procedure default. Neutral framing - "over"/"under"/"at", never "saved"/"waste".
public sealed record AllocationVarianceSummary(
    int AllocationVarianceCycleCount,
    int TotalExpectedAllocationMinutes,
    int TotalMeasuredCaseFlowMinutes,
    int NetAllocationVarianceMinutes,
    double AverageAllocationVarianceMinutes,
    int CasesOverExpectedAllocation,
    int CasesUnderExpectedAllocation,
    int CasesAtExpectedAllocation,
    int AdjustedAllocationCycleCount);

// Per-procedure baseline over normal, non-exception completed cycles. Additive reporting only;
// it reuses the same aggregate helpers and the same occupied/available wait values as the
// global metrics, so no separate definition of any metric is introduced here.
public sealed record ProcedureCycleSummary(
    string ProcedureCode,
    string ProcedureLabel,
    // BaseProcedureCode strips any sedation modifier ("EXT+SED" -> "EXT"); a base row's base
    // is itself. IsSedationCase marks composite "+SED" variants and bare legacy "SED" as
    // sedation-related. For base-procedure roll-ups (BaseProcedureSummaries) IsSedationCase is
    // false by convention because a roll-up is not a single sedation variant.
    string BaseProcedureCode,
    bool IsSedationCase,
    int CompletedCycleCount,
    double AverageTotalSeconds,
    double MedianTotalSeconds,
    double AverageReadyToDoctorSeconds,
    double MedianReadyToDoctorSeconds,
    double AverageDoctorTimeSeconds,
    double MedianDoctorTimeSeconds,
    double AverageDoctorOccupiedWaitSeconds,
    double AverageDoctorAvailableWaitSeconds,
    double MedianDoctorOccupiedWaitSeconds,
    double MedianDoctorAvailableWaitSeconds,
    AllocationVarianceSummary Allocation);

public sealed record DemoSeedPattern(
    string DoctorId,
    string ProcedureCode,
    Func<BoardThresholdOptions, TimeSpan> Elapsed,
    Func<BoardThresholdOptions, TimeSpan>? ReadyForDoctorElapsed = null);

// Development/test only synthetic data shaping (see DemoBoardStore.SeedSyntheticReportData).
// A per-doctor style profile that gives each doctor a distinct, non-punitive allocation shape.
public sealed record DoctorStyleProfile(
    int DoctorIndex,
    int VarianceBiasMinutes,
    int VarianceSpread,
    int FamilyLeanWeight,
    int VariableUnitDelta,
    int SedationChancePercent);

// A procedure family used by the seeder, with realistic case-flow bounds and a small intrinsic
// over/under lean. Default expected units are read from the live roster, not stored here.
public sealed record SyntheticFamily(
    string Code,
    int MinFlowMinutes,
    int MaxFlowMinutes,
    int CharacterLeanMinutes,
    bool SedationEligible);

// Tiny deterministic xorshift32 stream. Seeded from stable inputs so seeded data is reproducible
// (pseudo-random feel, fully deterministic) - never wall-clock or Random-default seeded.
public struct SyntheticJitter(int seed)
{
    private uint _state = (uint)seed | 1u;

    public int Next(int minInclusive, int maxInclusive)
    {
        _state ^= _state << 13;
        _state ^= _state >> 17;
        _state ^= _state << 5;
        var span = (uint)(maxInclusive - minInclusive + 1);
        return minInclusive + (int)(_state % span);
    }
}

/// <summary>Non-PHI summary returned by the dev-only synthetic report-data seeder.</summary>
public sealed record SeedReportDataResult(
    int CyclesInserted,
    int DoctorsRepresented,
    int ProcedureFamiliesRepresented,
    int ExpectedAllocationCases,
    int ExceptionsExpected);

/// <summary>
/// One day's allocation balance for a single doctor, for sparkline rendering. NetVarianceMinutes is
/// measured case flow minus expected allocation, summed over that day's allocation-calculable cycles
/// (positive = over expected, negative = under). CaseCount is the number of those cycles.
/// </summary>
public sealed record DoctorDailyAllocationPoint(string Date, int CaseCount, int NetVarianceMinutes);

/// <summary>Ordered daily allocation-balance series for one doctor within the selected report range.</summary>
public sealed record DoctorDailyAllocation(string DoctorId, IReadOnlyList<DoctorDailyAllocationPoint> Points);

/// <summary>Non-PHI before/after summary returned by the maintenance reset commands.</summary>
public sealed record MaintenanceResetResult(
    int CompletedCyclesCleared,
    int ActiveRoomsReset,
    int CyclesSeeded,
    int DoctorsRepresented,
    int ProcedureFamiliesRepresented,
    int ExpectedAllocationCases,
    int ExceptionsExpected);

/// <summary>
/// One deterministic live-room allocation for a stress-fixture profile: which room, which doctor
/// and procedure (null for an intentionally unassigned Available room), the target room state, and
/// whether the case is a sedation case. See DemoBoardStore.SeedLiveRoomFixtures / BuildLiveRoom.
/// </summary>
public sealed record LiveRoomFixture(
    int RoomId,
    string? DoctorId,
    string? ProcedureCode,
    string TargetState,
    bool Sedation = false);

/// <summary>
/// Non-PHI summary returned by the reset-stress-fixture maintenance command. RoomStateCounts covers
/// every configured room, including AVAILABLE (so an intentionally unassigned no-coin room is
/// reported, not silently dropped); ActiveRoomDoctorCounts is assigned-rooms-only. Cycle-history
/// fields (DerivedExceptionReasonCounts, ManualAuditCandidatesSeeded, HistoryEarliest/LatestSeatedAt)
/// are computed only from completed cycles (RoomAvailableAt set) - InProgressCycleRowsSeeded reports
/// the separate count of in-progress rows paired with a directly-seeded DoctorInRoom/Turnover room,
/// which are not seeded history and must not be counted or dated as if they were. Dictionaries only
/// contain keys that actually occurred; a profile that does not seed a given dimension simply
/// contributes no entries for it, which the CLI printout renders as an explicit zero /
/// "not seeded by this profile" rather than omitting the line.
/// </summary>
public sealed record StressFixtureResult(
    string Profile,
    int CompletedCyclesCleared,
    int ActiveRoomsReset,
    int CyclesSeeded,
    int DoctorsRepresented,
    int ProcedureFamiliesRepresented,
    IReadOnlyDictionary<string, int> RoomStateCounts,
    IReadOnlyDictionary<string, int> ActiveRoomDoctorCounts,
    IReadOnlyDictionary<string, int> DerivedExceptionReasonCounts,
    int ManualAuditCandidatesSeeded,
    int InProgressCycleRowsSeeded,
    DateTimeOffset? HistoryEarliestSeatedAt,
    DateTimeOffset? HistoryLatestSeatedAt);

public sealed class RoomState(int roomId)
{
    public int RoomId { get; } = roomId;

    // Opaque per-episode identity minted at Begin Prestage and cleared on ResetRoom. Carried
    // through Seat/Ready/Arrive into the eventual completed cycle or aborted-assignment record so
    // the whole occupancy episode shares one id. Null while Available and on legacy rows that
    // predate the Prestaging feature.
    public string? EpisodeId { get; set; }
    public string? AssignedDoctor { get; set; }
    public string? AssignedDoctorDisplayName { get; set; }
    public string? ProcedureCode { get; set; }
    public string? ProcedureCategory { get; set; }
    public SedationState? SedationState { get; set; }
    public ExpectedAllocationState? ExpectedAllocationState { get; set; }
    public int? ExpectedAllocationSuggestedUnits { get; set; }
    public int? ExpectedAllocationConfirmedUnits { get; set; }
    public string? ActiveReadyHandoffId { get; set; }
    public string? AcceptedReadyHandoffId { get; set; }
    public string State { get; set; } = RoomStates.Available;

    // Room-unavailable clock start, set at Begin Prestage and cleared on ResetRoom. Null while
    // Available and on legacy rows persisted before the Prestaging feature (which reload with their
    // existing state and are never forced through Prestaging).
    public DateTimeOffset? PrestageStartedAt { get; set; }
    public DateTimeOffset? SeatedAt { get; set; }
    public DateTimeOffset? AgingStartedAt { get; set; }
    public DateTimeOffset? StaleStartedAt { get; set; }
    public DateTimeOffset? ReadyForDoctorAt { get; set; }
    public DateTimeOffset? DoctorArrivedAt { get; set; }
    public DateTimeOffset? DoctorCompleteAt { get; set; }
    public DateTimeOffset? RoomAvailableAt { get; set; }

    // Expected allocation snapshot (operational, non-PHI). Captured at seating from the
    // procedure default and any staff override. 1 unit = 10 minutes.
    public int OriginalDefaultExpectedUnits { get; set; }
    public int ExpectedAllocationUnits { get; set; }
    public int ExpectedAllocationMinutes { get; set; }
    public bool AllocationAdjustedFromDefault { get; set; }
}

public static class RoomStates
{
    public const string Available = "available";
    public const string Prestaging = "prestaging";
    public const string Seated = "seated";
    public const string Aging = "aging";
    public const string Stale = "stale";
    public const string ReadyForDoctor = "readyForDoctor";
    public const string DoctorInRoom = "doctorInRoom";
    public const string Turnover = "turnover";
}

/// <summary>
/// How an occupancy episode was terminated before it produced a normal completed cycle. This is the
/// mechanism, deliberately kept separate from any staff-selected operational reason (see
/// <see cref="CancellationReasons"/>): a record always has a termination kind, and only staff
/// cancellations additionally carry a reason.
/// </summary>
public static class TerminationKinds
{
    /// <summary>A staff member explicitly canceled the prestage or seating from the room panel.</summary>
    public const string StaffCanceled = "StaffCanceled";

    /// <summary>The active-cycle safety sweep expired the episode for exceeding the max active duration.</summary>
    public const string MaxDurationExpired = "MaxDurationExpired";

    /// <summary>The after-hours clinic-local sweep expired the episode at end of day.</summary>
    public const string AfterHoursExpired = "AfterHoursExpired";
}

/// <summary>
/// Optional, staff-selected operational reason accompanying a <see cref="TerminationKinds.StaffCanceled"/>
/// termination. Never inferred; null when not supplied or when the termination was an automatic sweep.
/// Operational metadata only - never PHI.
/// </summary>
public static class CancellationReasons
{
    public const string PatientCanceled = "PatientCanceled";
    public const string NoShow = "NoShow";
    public const string MovedRoom = "MovedRoom";
    public const string SchedulingError = "SchedulingError";
    public const string ProcedureChanged = "ProcedureChanged";
    public const string Other = "Other";
}

/// <summary>
/// Durable record of an occupancy episode that left Available but returned to Available without
/// producing a normal completed cycle (a canceled prestage, a canceled seating, or a sweep-expired
/// prestage). Preserves the full assignment snapshot so room-unavailable time stays attributable.
/// Non-PHI: identity, procedure, allocation, timestamps, and operational termination metadata only.
///
/// Populations are disjoint by construction: an episode either reaches a normal completed cycle
/// (completed_room_cycles) or terminates incomplete (this record), never both, so summing spans
/// across the two tables counts each episode's room-unavailable time exactly once.
/// </summary>
public sealed class AbortedRoomAssignment
{
    // Stable per-row identity assigned by SQLite (aborted_room_assignments.id). Zero until persisted.
    public long AbortedAssignmentId { get; set; }

    // Per-episode identity and idempotency key (UNIQUE in storage). Minted at Begin Prestage for
    // new episodes, or at termination time for a legacy row that never had one. Two genuinely
    // distinct episodes always carry distinct EpisodeIds even under an identical (fixed-clock)
    // termination instant; re-terminating the same episode reuses the same id and is a no-op.
    public string EpisodeId { get; set; } = "";
    public int RoomId { get; set; }

    // Assignment snapshot. Canonical Prestaging can terminate with no or partial assignment, so
    // doctor/procedure are nullable. Legacy rows may also have null canonical state fields when a
    // truthful migration cannot prove sedation or allocation intent.
    public string? AssignedDoctor { get; set; }
    public string? AssignedDoctorDisplayName { get; set; }
    public string? ProcedureCode { get; set; }
    public string? ProcedureCategory { get; set; }
    public SedationState? SedationState { get; set; }
    public ExpectedAllocationState? ExpectedAllocationState { get; set; }
    public int? ExpectedAllocationSuggestedUnits { get; set; }
    public int? ExpectedAllocationConfirmedUnits { get; set; }
    public string? TerminalReadyHandoffId { get; set; }
    public int OriginalDefaultExpectedUnits { get; set; }
    public int ExpectedAllocationUnits { get; set; }
    public int ExpectedAllocationMinutes { get; set; }
    public bool AllocationAdjustedFromDefault { get; set; }

    // Phase timestamps captured up to the point of termination. PrestageStartedAt is null only for a
    // legacy row that was seated before the Prestaging feature; SeatedAt/ReadyForDoctorAt are present
    // only if the episode had reached those phases before termination.
    public DateTimeOffset? PrestageStartedAt { get; set; }
    public DateTimeOffset? SeatedAt { get; set; }
    public DateTimeOffset? ReadyForDoctorAt { get; set; }

    // Termination facts. TerminatedFromState is the room state at termination; TerminationKind is the
    // mechanism (see TerminationKinds); CancellationReason is the optional staff reason (see
    // CancellationReasons) and is only meaningful for a StaffCanceled termination.
    public DateTimeOffset TerminatedAt { get; set; }
    public string TerminatedFromState { get; set; } = "";
    public string TerminationKind { get; set; } = "";
    public string? CancellationReason { get; set; }
}

public static class ReviewStatuses
{
    /// <summary>Cycle has been flagged as an exception but not yet reviewed.</summary>
    public const string PendingReview = "PendingReview";

    /// <summary>Cycle has been acknowledged by an admin reviewer.</summary>
    public const string Reviewed = "Reviewed";
}

public static class ExceptionReviewers
{
    /// <summary>
    /// Safe non-PHI reviewer label recorded when an exception is confirmed through the
    /// admin-protected reports endpoint. ChairSide has no per-user identity, so the local
    /// admin operator is attributed generically.
    /// </summary>
    public const string LocalAdmin = "local-admin";
}

/// <summary>Outcome of an attempt to confirm the exclusion of an exception cycle.</summary>
public enum ReviewExceptionOutcome
{
    /// <summary>No completed cycle matched the supplied id.</summary>
    NotFound,

    /// <summary>The cycle exists but was never flagged as an exception.</summary>
    NotAnException,

    /// <summary>The exception was confirmed as reviewed (or already was).</summary>
    Reviewed
}

/// <summary>Result of ReviewExceptionCycleById, carrying the room id for audit logging.</summary>
public readonly record struct ReviewExceptionResult(ReviewExceptionOutcome Outcome, int RoomId);

/// <summary>Outcome of a guarded Doctor Arrived attempt or a conflict resolution attempt.</summary>
public enum DoctorArrivalOutcome
{
    /// <summary>The room id is not configured.</summary>
    NotConfigured,

    /// <summary>The room is not in a state that allows Doctor Arrived.</summary>
    Rejected,

    /// <summary>The assigned doctor is already marked doctor-in-room in another room.</summary>
    Conflict,

    /// <summary>A resolve attempt found the conflict gone or changed; nothing was mutated.</summary>
    StaleConflict,

    /// <summary>Doctor Arrived was applied successfully.</summary>
    Arrived
}

/// <summary>Safe, non-PHI context describing a doctor-arrival conflict for the UI prompt.</summary>
public sealed record DoctorArrivalConflict(
    int ConflictingRoomId,
    string? DoctorId,
    string? DoctorDisplayName);

/// <summary>Result of TryMarkDoctorArrived / ResolveDoctorArrivalConflict.</summary>
public readonly record struct DoctorArrivalResult(
    DoctorArrivalOutcome Outcome,
    RoomStatus? Status,
    DoctorArrivalConflict? Conflict);
