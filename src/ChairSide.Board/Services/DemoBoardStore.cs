using ChairSide.Board.Options;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Services;

public sealed class DemoBoardStore
{
    private const int MaxRoomEvents = 200;

    private readonly object _syncRoot = new();
    private readonly IOptionsMonitor<BoardThresholdOptions> _thresholdOptions;
    private readonly SqliteBoardRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly int _roomCount;
    private readonly bool _demoTimerEnabled;
    private readonly List<Doctor> _doctors;
    private readonly List<Doctor> _activeDoctors;
    private readonly List<ProcedureCategory> _procedures;
    private readonly List<ProcedureCategory> _activeProcedures;

    private readonly List<RoomState> _rooms;
    private readonly List<RoomEvent> _events = [];
    private readonly List<CompletedRoomCycle> _completedCycles = [];

    public DemoBoardStore(
        IOptionsMonitor<BoardThresholdOptions> thresholdOptions,
        IOptions<BoardOptions> boardOptions,
        IOptions<BoardUiOptions> boardUiOptions,
        IOptions<DoctorRosterOptions> doctorRosterOptions,
        IOptions<ProcedureRosterOptions> procedureRosterOptions,
        SqliteBoardRepository repository,
        IWebHostEnvironment environment,
        TimeProvider? timeProvider = null)
    {
        _thresholdOptions = thresholdOptions;
        _repository = repository;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _roomCount = boardOptions.Value.RoomCount;
        _demoTimerEnabled = boardUiOptions.Value.DemoTimerEnabled ?? !environment.IsProduction();
        _doctors = BuildDoctors(doctorRosterOptions.Value).ToList();
        _activeDoctors = BuildDoctors(doctorRosterOptions.Value, activeOnly: true).ToList();
        _procedures = BuildProcedures(procedureRosterOptions.Value).ToList();
        _activeProcedures = BuildProcedures(procedureRosterOptions.Value, activeOnly: true).ToList();
        var now = Now;

        var hasPersistedRooms = _repository.HasAnyRoomRows();
        _repository.EnsureConfiguredRooms(_roomCount);
        _rooms = _repository.LoadRooms(_roomCount).ToList();
        AddMissingRooms();
        RecomputeLoadedRoomStates(now);

        if (!hasPersistedRooms && !environment.IsProduction())
        {
            SeedDemoRooms(now);
        }

        _repository.SaveRooms(_rooms, _doctors, _procedures);
        _completedCycles = _repository.LoadCompletedCycles().ToList();
    }

    public BoardSnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            var now = Now;
            _rooms.ForEach(room => UpdateRoomState(room, now));

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
            UpdateRoomState(room, now);
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

    public RoomStatus? SeatRoom(int roomNumber, string doctorId, string procedureCode, int demoElapsedMinutes = 0)
    {
        lock (_syncRoot)
        {
            var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
            var doctor = _activeDoctors.FirstOrDefault(item => item.Id == doctorId);
            var procedure = FindActiveProcedure(procedureCode);
            if (room is null || doctor is null || procedure is null || !CanSeat(room))
            {
                return null;
            }

            var now = Now;
            var effectiveDemoElapsedMinutes = _demoTimerEnabled ? demoElapsedMinutes : 0;
            var simulatedElapsed = TimeSpan.FromMinutes(Math.Clamp(effectiveDemoElapsedMinutes, 0, 240));
            room.AssignedDoctor = doctor.Id;
            room.ProcedureCode = procedure.Code;
            room.SeatedAt = now - simulatedElapsed;
            room.AgingStartedAt = null;
            room.StaleStartedAt = null;
            room.DoctorArrivedAt = null;
            room.DoctorCompleteAt = null;
            room.RoomAvailableAt = null;
            room.State = RoomStates.Seated;
            UpdateRoomState(room, now);
            AddEvent(new RoomEvent(room.RoomId, "Seated", now, doctor.Id, procedure.Code));
            PersistRoom(room);

            return ToRoomStatus(room, now);
        }
    }

    public RoomStatus? UpdateAssignment(int roomNumber, string doctorId, string procedureCode)
    {
        lock (_syncRoot)
        {
            var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
            var doctor = _activeDoctors.FirstOrDefault(item => item.Id == doctorId);
            var procedure = FindActiveProcedure(procedureCode);
            if (room is null || doctor is null || procedure is null)
            {
                return null;
            }

            var now = Now;
            UpdateRoomState(room, now);
            if (!CanEditSeatedRoom(room) || room.SeatedAt is null)
            {
                return null;
            }

            room.AssignedDoctor = doctor.Id;
            room.ProcedureCode = procedure.Code;
            UpdateRoomState(room, now);
            AddEvent(new RoomEvent(room.RoomId, "AssignmentUpdated", now, doctor.Id, procedure.Code));
            PersistRoom(room);

            return ToRoomStatus(room, now);
        }
    }

    public RoomStatus? CancelSeating(int roomNumber)
    {
        lock (_syncRoot)
        {
            var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
            if (room is null)
            {
                return null;
            }

            var now = Now;
            UpdateRoomState(room, now);
            if (!CanEditSeatedRoom(room) || room.SeatedAt is null)
            {
                return null;
            }

            AddEvent(new RoomEvent(room.RoomId, "SeatingCanceled", now, room.AssignedDoctor, room.ProcedureCode));
            ResetRoom(room);
            PersistRoom(room);

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
            UpdateRoomState(room, now);
            if (!CanMarkReadyForDoctor(room) || room.SeatedAt is null)
            {
                return null;
            }

            room.ReadyForDoctorAt = now;
            room.State = RoomStates.ReadyForDoctor;
            AddEvent(new RoomEvent(room.RoomId, "ReadyForDoctor", now, room.AssignedDoctor, room.ProcedureCode));
            PersistRoom(room);

            return ToRoomStatus(room, now);
        }
    }

    public RoomStatus? MarkDoctorArrived(int roomNumber)
    {
        lock (_syncRoot)
        {
            var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
            if (room is null)
            {
                return null;
            }

            var now = Now;
            UpdateRoomState(room, now);
            if (!CanMarkDoctorArrived(room) || room.SeatedAt is null || room.AssignedDoctor is null || room.ProcedureCode is null)
            {
                return null;
            }

            var finalWaitState = room.State;
            var seatedToDoctorSeconds = SecondsBetween(room.SeatedAt.Value, now);
            var prepSeconds = room.ReadyForDoctorAt.HasValue
                ? SecondsBetween(room.SeatedAt.Value, room.ReadyForDoctorAt.Value)
                : (int?)null;
            var readyToDoctorSeconds = room.ReadyForDoctorAt.HasValue
                ? SecondsBetween(room.ReadyForDoctorAt.Value, now)
                : (int?)null;

            room.DoctorArrivedAt = now;
            room.State = RoomStates.DoctorInRoom;
            AddEvent(new RoomEvent(room.RoomId, "DoctorArrived", now, room.AssignedDoctor, room.ProcedureCode, TimeSpan.FromSeconds(seatedToDoctorSeconds)));

            CompletedRoomCycle? cycle = null;
            if (!HasCycleReport(room.RoomId, room.SeatedAt.Value))
            {
                cycle = new CompletedRoomCycle
                {
                    RoomId = room.RoomId,
                    AssignedDoctor = room.AssignedDoctor,
                    ProcedureCode = room.ProcedureCode,
                    SeatedAt = room.SeatedAt.Value,
                    ReadyForDoctorAt = room.ReadyForDoctorAt,
                    DoctorArrivedAt = now,
                    SeatedToDoctorSeconds = seatedToDoctorSeconds,
                    PrepSeconds = prepSeconds,
                    ReadyToDoctorSeconds = readyToDoctorSeconds,
                    FinalWaitState = finalWaitState,
                    AgingThresholdReached = room.AgingStartedAt is not null,
                    StaleThresholdReached = room.StaleStartedAt is not null
                };
                _completedCycles.Add(cycle);
            }

            PersistRoom(room);
            if (cycle is not null)
            {
                PersistCycle(cycle);
            }

            return ToRoomStatus(room, now);
        }
    }

    public RoomStatus? MarkDoctorComplete(int roomNumber)
    {
        lock (_syncRoot)
        {
            var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
            if (room is null || room.State != RoomStates.DoctorInRoom || room.DoctorArrivedAt is null)
            {
                return null;
            }

            var now = Now;
            room.DoctorCompleteAt = now;
            room.State = RoomStates.Turnover;
            UpdateCycleReport(room, cycle =>
            {
                cycle.DoctorCompleteAt = now;
                cycle.DoctorInRoomSeconds = SecondsBetween(room.DoctorArrivedAt.Value, now);
            });
            AddEvent(new RoomEvent(room.RoomId, "DoctorComplete", now, room.AssignedDoctor, room.ProcedureCode));
            PersistRoom(room);
            PersistCycleForRoom(room);

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

            var now = Now;
            room.RoomAvailableAt = now;
            UpdateCycleReport(room, cycle =>
            {
                cycle.RoomAvailableAt = now;
                cycle.TurnoverSeconds = SecondsBetween(room.DoctorCompleteAt.Value, now);
                cycle.TotalRoomCycleSeconds = SecondsBetween(room.SeatedAt.Value, now);
            });
            AddEvent(new RoomEvent(room.RoomId, "RoomAvailable", now, room.AssignedDoctor, room.ProcedureCode));
            PersistCycleForRoom(room);

            ResetRoom(room);
            PersistRoom(room);

            return ToRoomStatus(room, now);
        }
    }

    public ReportsSnapshot GetReports()
    {
        lock (_syncRoot)
        {
            var allCycles = _completedCycles
                .OrderByDescending(cycle => cycle.DoctorArrivedAt)
                .ToList();

            // Exception cycles are excluded from normal operational metrics by default.
            var normalCycles = allCycles.Where(cycle => !cycle.IsException).ToList();
            var normalCompletedCycles = normalCycles
                .Where(cycle => cycle.RoomAvailableAt is not null)
                .ToList();

            var exceptionCycles = allCycles
                .Where(cycle => cycle.IsException)
                .OrderByDescending(cycle => cycle.SeatedAt)
                .ToList();

            return new ReportsSnapshot(
                normalCompletedCycles.Count,
                AverageSeconds(normalCycles.Select(cycle => (int?)cycle.SeatedToDoctorSeconds)),
                MedianSeconds(normalCycles.Select(cycle => (int?)cycle.SeatedToDoctorSeconds)),
                AverageSeconds(normalCycles.Select(cycle => cycle.PrepSeconds)),
                MedianSeconds(normalCycles.Select(cycle => cycle.PrepSeconds)),
                AverageSeconds(normalCycles.Select(cycle => cycle.ReadyToDoctorSeconds)),
                MedianSeconds(normalCycles.Select(cycle => cycle.ReadyToDoctorSeconds)),
                AverageSeconds(normalCycles.Select(cycle => cycle.DoctorInRoomSeconds)),
                MedianSeconds(normalCycles.Select(cycle => cycle.DoctorInRoomSeconds)),
                AverageSeconds(normalCycles.Select(cycle => cycle.TurnoverSeconds)),
                MedianSeconds(normalCycles.Select(cycle => cycle.TurnoverSeconds)),
                normalCycles.Count(cycle => cycle.AgingThresholdReached),
                normalCycles.Count(cycle => cycle.StaleThresholdReached),
                BuildDoctorSummaries(normalCycles),
                normalCompletedCycles.Take(25).ToList(),
                exceptionCycles);
        }
    }

    /// <summary>
    /// Marks an existing completed cycle as an exception, removing it from normal
    /// reporting metrics and surfacing it in the Exceptions Requiring Review section.
    /// Returns false if no matching cycle is found.
    /// No PHI is stored — reason and suggested action are operational notes only.
    /// </summary>
    public bool MarkCycleAsException(int roomId, DateTimeOffset seatedAt, string reason, string suggestedAction)
    {
        lock (_syncRoot)
        {
            var cycle = _completedCycles.FirstOrDefault(item => item.RoomId == roomId && item.SeatedAt == seatedAt);
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
    }

    private RoomStatus ToRoomStatus(RoomState room, DateTimeOffset now)
    {
        var elapsed = room.SeatedAt is null ? TimeSpan.Zero : now - room.SeatedAt.Value;
        var doctor = room.AssignedDoctor is null ? null : _doctors.FirstOrDefault(item => item.Id == room.AssignedDoctor);
        var procedure = room.ProcedureCode is null ? null : FindProcedure(room.ProcedureCode);

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
            elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);
    }

    private void UpdateRoomState(RoomState room, DateTimeOffset now)
    {
        // Doctor In Room and Turnover are terminal — no further automatic transitions.
        if (room.State is RoomStates.DoctorInRoom or RoomStates.Turnover)
        {
            return;
        }

        if (room.SeatedAt is null)
        {
            room.State = RoomStates.Available;
            return;
        }

        // Patient Seated / In Prep: aging/stale thresholds do NOT apply here.
        // This state is a neutral prep limbo; it stays Seated until staff clicks Ready for Doctor.
        if (room.State is RoomStates.Available or RoomStates.Seated)
        {
            room.AgingStartedAt = null;
            room.StaleStartedAt = null;
            room.State = RoomStates.Seated;
            return;
        }

        // Ready for Doctor phase (ReadyForDoctor, Aging, Stale): escalate based on elapsed
        // time from ReadyForDoctorAt. These states all mean "doctor has been requested."
        if (room.ReadyForDoctorAt is null)
        {
            // Defensive: state is in the ready-for-doctor phase but the timestamp is missing.
            return;
        }

        var elapsed = now - room.ReadyForDoctorAt.Value;
        var thresholds = Thresholds;
        if (elapsed >= thresholds.StaleThreshold)
        {
            room.AgingStartedAt = room.ReadyForDoctorAt.Value.Add(thresholds.AgingThreshold);
            room.StaleStartedAt = room.ReadyForDoctorAt.Value.Add(thresholds.StaleThreshold);
            room.State = RoomStates.Stale;
            return;
        }

        if (elapsed >= thresholds.AgingThreshold)
        {
            room.AgingStartedAt = room.ReadyForDoctorAt.Value.Add(thresholds.AgingThreshold);
            room.StaleStartedAt = null;
            room.State = RoomStates.Aging;
            return;
        }

        room.AgingStartedAt = null;
        room.StaleStartedAt = null;
        room.State = RoomStates.ReadyForDoctor;
    }

    private ProcedureCategory? FindProcedure(string procedureCode) =>
        _procedures.FirstOrDefault(item =>
            string.Equals(item.Id, procedureCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Code, procedureCode, StringComparison.OrdinalIgnoreCase));

    private ProcedureCategory? FindActiveProcedure(string procedureCode) =>
        _activeProcedures.FirstOrDefault(item =>
            string.Equals(item.Id, procedureCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Code, procedureCode, StringComparison.OrdinalIgnoreCase));

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
                procedure.Icon));

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

    private void RecomputeLoadedRoomStates(DateTimeOffset now)
    {
        foreach (var room in _rooms)
        {
            UpdateRoomState(room, now);
        }
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

        // Rooms in Ready for Doctor phase — ReadyForDoctorAt triggers aging/stale escalation
        yield return new("pledger", "EXT",
            thresholds => StaleSample(thresholds),
            thresholds => StaleSample(thresholds));
        yield return new("gibson", "SED",
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

    private static void ResetRoom(RoomState room)
    {
        room.AssignedDoctor = null;
        room.ProcedureCode = null;
        room.State = RoomStates.Available;
        room.SeatedAt = null;
        room.AgingStartedAt = null;
        room.StaleStartedAt = null;
        room.ReadyForDoctorAt = null;
        room.DoctorArrivedAt = null;
        room.DoctorCompleteAt = null;
        room.RoomAvailableAt = null;
    }

    private void SeedDemoRoom(int roomId, string doctorId, string procedureCode, DateTimeOffset seatedAt, DateTimeOffset? readyForDoctorAt = null)
    {
        var index = _rooms.FindIndex(room => room.RoomId == roomId);
        if (index >= 0)
        {
            _rooms[index] = readyForDoctorAt.HasValue
                ? ReadyForDoctorRoom(roomId, doctorId, procedureCode, seatedAt, readyForDoctorAt.Value)
                : Seated(roomId, doctorId, procedureCode, seatedAt);
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

        UpdateRoomState(room, Now);
        return room;
    }

    private RoomState ReadyForDoctorRoom(int roomId, string doctorId, string procedureCode, DateTimeOffset seatedAt, DateTimeOffset readyForDoctorAt)
    {
        var room = new RoomState(roomId)
        {
            AssignedDoctor = doctorId,
            ProcedureCode = procedureCode,
            State = RoomStates.ReadyForDoctor,
            SeatedAt = seatedAt,
            ReadyForDoctorAt = readyForDoctorAt
        };

        UpdateRoomState(room, Now);
        return room;
    }

    private static bool CanSeat(RoomState room) => room.State == RoomStates.Available && room.SeatedAt is null;

    private static bool CanEditSeatedRoom(RoomState room) =>
        room.State is RoomStates.Seated or RoomStates.Aging or RoomStates.Stale or RoomStates.ReadyForDoctor;

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

    private bool HasCycleReport(int roomId, DateTimeOffset seatedAt) =>
        _completedCycles.Any(cycle => cycle.RoomId == roomId && cycle.SeatedAt == seatedAt);

    private void UpdateCycleReport(RoomState room, Action<CompletedRoomCycle> update)
    {
        if (room.SeatedAt is null)
        {
            return;
        }

        var cycle = _completedCycles.FirstOrDefault(item => item.RoomId == room.RoomId && item.SeatedAt == room.SeatedAt);
        if (cycle is not null)
        {
            update(cycle);
        }
    }

    private void PersistRoom(RoomState room) =>
        _repository.SaveRoom(room, _doctors, _procedures);

    private void PersistCycle(CompletedRoomCycle cycle) =>
        _repository.SaveCompletedCycle(cycle, _doctors, _procedures);

    private void PersistCycleForRoom(RoomState room)
    {
        if (room.SeatedAt is null)
        {
            return;
        }

        var cycle = _completedCycles.FirstOrDefault(item => item.RoomId == room.RoomId && item.SeatedAt == room.SeatedAt);
        if (cycle is not null)
        {
            PersistCycle(cycle);
        }
    }

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
            .GroupBy(cycle => new { cycle.AssignedDoctor, Month = new DateOnly(cycle.DoctorArrivedAt.Year, cycle.DoctorArrivedAt.Month, 1) })
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
                group.Count(cycle => cycle.StaleThresholdReached)))
            .OrderByDescending(summary => summary.Month)
            .ThenBy(summary => summary.AssignedDoctor)
            .ToList();
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

public sealed record ProcedureCategory(string Id, string Code, string Label, string Icon);

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
    TimeSpan Elapsed);

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
    IReadOnlyList<DoctorCycleSummary> DoctorSummaries,
    IReadOnlyList<CompletedRoomCycle> RecentCompletedCycles,
    IReadOnlyList<CompletedRoomCycle> ExceptionCycles);

public sealed class CompletedRoomCycle
{
    public int RoomId { get; set; }
    public string AssignedDoctor { get; set; } = "";
    public string ProcedureCode { get; set; } = "";
    public DateTimeOffset SeatedAt { get; set; }
    public DateTimeOffset? ReadyForDoctorAt { get; set; }
    public DateTimeOffset DoctorArrivedAt { get; set; }
    public DateTimeOffset? DoctorCompleteAt { get; set; }
    public DateTimeOffset? RoomAvailableAt { get; set; }
    public int SeatedToDoctorSeconds { get; set; }
    public int? PrepSeconds { get; set; }
    public int? ReadyToDoctorSeconds { get; set; }
    public int? DoctorInRoomSeconds { get; set; }
    public int? TurnoverSeconds { get; set; }
    public int? TotalRoomCycleSeconds { get; set; }
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
    int StaleEventCount);

public sealed record DemoSeedPattern(
    string DoctorId,
    string ProcedureCode,
    Func<BoardThresholdOptions, TimeSpan> Elapsed,
    Func<BoardThresholdOptions, TimeSpan>? ReadyForDoctorElapsed = null);

public sealed class RoomState(int roomId)
{
    public int RoomId { get; } = roomId;
    public string? AssignedDoctor { get; set; }
    public string? ProcedureCode { get; set; }
    public string State { get; set; } = RoomStates.Available;
    public DateTimeOffset? SeatedAt { get; set; }
    public DateTimeOffset? AgingStartedAt { get; set; }
    public DateTimeOffset? StaleStartedAt { get; set; }
    public DateTimeOffset? ReadyForDoctorAt { get; set; }
    public DateTimeOffset? DoctorArrivedAt { get; set; }
    public DateTimeOffset? DoctorCompleteAt { get; set; }
    public DateTimeOffset? RoomAvailableAt { get; set; }
}

public static class RoomStates
{
    public const string Available = "available";
    public const string Seated = "seated";
    public const string Aging = "aging";
    public const string Stale = "stale";
    public const string ReadyForDoctor = "readyForDoctor";
    public const string DoctorInRoom = "doctorInRoom";
    public const string Turnover = "turnover";
}

public static class ReviewStatuses
{
    /// <summary>Cycle has been flagged as an exception but not yet reviewed.</summary>
    public const string PendingReview = "PendingReview";

    /// <summary>Cycle has been acknowledged by an admin reviewer.</summary>
    public const string Reviewed = "Reviewed";
}
