using ChairSide.Board.Options;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Services;

public sealed class DemoBoardStore
{
    private readonly object _syncRoot = new();
    private readonly IOptionsMonitor<BoardThresholdOptions> _thresholdOptions;
    private readonly int _roomCount;
    private readonly List<Doctor> _doctors =
    [
        new("otte", "Dr. Otte", "#2563eb"),
        new("pledger", "Dr. Pledger", "#16a34a"),
        new("gibson", "Dr. Gibson", "#f97316"),
        new("schroeder", "Dr. Schroeder", "#7c3aed")
    ];

    private readonly List<ProcedureCategory> _procedures =
    [
        new("consult", "CON", "Consult", "speech"),
        new("extraction", "EXT", "Extraction", "forceps"),
        new("sedation", "SED", "Sedation", "moon"),
        new("post-op", "POST", "Post-op", "check"),
        new("implant", "IMP", "Implant", "bolt"),
        new("biopsy", "BX", "Biopsy", "vial")
    ];

    private readonly List<RoomState> _rooms;
    private readonly List<RoomEvent> _events = [];
    private readonly List<CompletedRoomCycle> _completedCycles = [];

    public DemoBoardStore(
        IOptionsMonitor<BoardThresholdOptions> thresholdOptions,
        IOptions<BoardOptions> boardOptions)
    {
        _thresholdOptions = thresholdOptions;
        _roomCount = boardOptions.Value.RoomCount;
        var now = DateTimeOffset.UtcNow;
        _rooms = Enumerable.Range(1, _roomCount)
            .Select(Available)
            .ToList();

        SeedDemoRooms(now);
    }

    public BoardSnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            _rooms.ForEach(room => UpdateRoomState(room, now));

            return new BoardSnapshot(
                now,
                _roomCount,
                Thresholds.AgingMinutes,
                Thresholds.StaleMinutes,
                Thresholds.AgingThreshold,
                Thresholds.StaleThreshold,
                _doctors,
                _procedures,
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

            var now = DateTimeOffset.UtcNow;
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
            var doctor = _doctors.FirstOrDefault(item => item.Id == doctorId);
            var procedure = FindProcedure(procedureCode);
            if (room is null || doctor is null || procedure is null || !CanSeat(room))
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var simulatedElapsed = TimeSpan.FromMinutes(Math.Clamp(demoElapsedMinutes, 0, 240));
            room.AssignedDoctor = doctor.Id;
            room.ProcedureCode = procedure.Label;
            room.SeatedAt = now - simulatedElapsed;
            room.AgingStartedAt = null;
            room.StaleStartedAt = null;
            room.DoctorArrivedAt = null;
            room.DoctorCompleteAt = null;
            room.RoomAvailableAt = null;
            room.State = RoomStates.Seated;
            UpdateRoomState(room, now);
            _events.Add(new RoomEvent(room.RoomId, "Seated", now, doctor.Id, procedure.Label));

            return ToRoomStatus(room, now);
        }
    }

    public RoomStatus? UpdateAssignment(int roomNumber, string doctorId, string procedureCode)
    {
        lock (_syncRoot)
        {
            var room = _rooms.FirstOrDefault(item => item.RoomId == roomNumber);
            var doctor = _doctors.FirstOrDefault(item => item.Id == doctorId);
            var procedure = FindProcedure(procedureCode);
            if (room is null || doctor is null || procedure is null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            UpdateRoomState(room, now);
            if (!CanMarkDoctorArrived(room) || room.SeatedAt is null)
            {
                return null;
            }

            room.AssignedDoctor = doctor.Id;
            room.ProcedureCode = procedure.Label;
            UpdateRoomState(room, now);
            _events.Add(new RoomEvent(room.RoomId, "AssignmentUpdated", now, doctor.Id, procedure.Label));

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

            var now = DateTimeOffset.UtcNow;
            UpdateRoomState(room, now);
            if (!CanMarkDoctorArrived(room) || room.SeatedAt is null)
            {
                return null;
            }

            _events.Add(new RoomEvent(room.RoomId, "SeatingCanceled", now, room.AssignedDoctor, room.ProcedureCode));
            ResetRoom(room);

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

            var now = DateTimeOffset.UtcNow;
            UpdateRoomState(room, now);
            if (!CanMarkDoctorArrived(room) || room.SeatedAt is null || room.AssignedDoctor is null || room.ProcedureCode is null)
            {
                return null;
            }

            var finalWaitState = room.State;
            var seatedToDoctorSeconds = SecondsBetween(room.SeatedAt.Value, now);
            room.DoctorArrivedAt = now;
            room.State = RoomStates.DoctorInRoom;
            _events.Add(new RoomEvent(room.RoomId, "DoctorArrived", now, room.AssignedDoctor, room.ProcedureCode, TimeSpan.FromSeconds(seatedToDoctorSeconds)));

            if (!HasCycleReport(room.RoomId, room.SeatedAt.Value))
            {
                _completedCycles.Add(new CompletedRoomCycle
                {
                    RoomId = room.RoomId,
                    AssignedDoctor = room.AssignedDoctor,
                    ProcedureCode = room.ProcedureCode,
                    SeatedAt = room.SeatedAt.Value,
                    DoctorArrivedAt = now,
                    SeatedToDoctorSeconds = seatedToDoctorSeconds,
                    FinalWaitState = finalWaitState,
                    AgingThresholdReached = room.AgingStartedAt is not null,
                    StaleThresholdReached = room.StaleStartedAt is not null
                });
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

            var now = DateTimeOffset.UtcNow;
            room.DoctorCompleteAt = now;
            room.State = RoomStates.Turnover;
            UpdateCycleReport(room, cycle =>
            {
                cycle.DoctorCompleteAt = now;
                cycle.DoctorInRoomSeconds = SecondsBetween(room.DoctorArrivedAt.Value, now);
            });
            _events.Add(new RoomEvent(room.RoomId, "DoctorComplete", now, room.AssignedDoctor, room.ProcedureCode));

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

            var now = DateTimeOffset.UtcNow;
            room.RoomAvailableAt = now;
            UpdateCycleReport(room, cycle =>
            {
                cycle.RoomAvailableAt = now;
                cycle.TurnoverSeconds = SecondsBetween(room.DoctorCompleteAt.Value, now);
                cycle.TotalRoomCycleSeconds = SecondsBetween(room.SeatedAt.Value, now);
            });
            _events.Add(new RoomEvent(room.RoomId, "RoomAvailable", now, room.AssignedDoctor, room.ProcedureCode));

            ResetRoom(room);

            return ToRoomStatus(room, now);
        }
    }

    public ReportsSnapshot GetReports()
    {
        lock (_syncRoot)
        {
            var cycles = _completedCycles
                .OrderByDescending(cycle => cycle.DoctorArrivedAt)
                .ToList();
            var completedCycles = cycles
                .Where(cycle => cycle.RoomAvailableAt is not null)
                .ToList();

            return new ReportsSnapshot(
                completedCycles.Count,
                AverageSeconds(cycles.Select(cycle => (int?)cycle.SeatedToDoctorSeconds)),
                MedianSeconds(cycles.Select(cycle => (int?)cycle.SeatedToDoctorSeconds)),
                AverageSeconds(cycles.Select(cycle => cycle.DoctorInRoomSeconds)),
                MedianSeconds(cycles.Select(cycle => cycle.DoctorInRoomSeconds)),
                AverageSeconds(cycles.Select(cycle => cycle.TurnoverSeconds)),
                MedianSeconds(cycles.Select(cycle => cycle.TurnoverSeconds)),
                cycles.Count(cycle => cycle.AgingThresholdReached),
                cycles.Count(cycle => cycle.StaleThresholdReached),
                BuildDoctorSummaries(cycles),
                completedCycles.Take(25).ToList());
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
            room.DoctorArrivedAt,
            room.DoctorCompleteAt,
            room.RoomAvailableAt,
            elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed);
    }

    private void UpdateRoomState(RoomState room, DateTimeOffset now)
    {
        if (room.State is RoomStates.DoctorInRoom or RoomStates.Turnover)
        {
            return;
        }

        if (room.SeatedAt is null)
        {
            room.State = RoomStates.Available;
            return;
        }

        var elapsed = now - room.SeatedAt.Value;
        var thresholds = Thresholds;
        if (elapsed >= thresholds.StaleThreshold)
        {
            room.AgingStartedAt = room.SeatedAt.Value.Add(thresholds.AgingThreshold);
            room.StaleStartedAt = room.SeatedAt.Value.Add(thresholds.StaleThreshold);
            room.State = RoomStates.Stale;
            return;
        }

        if (elapsed >= thresholds.AgingThreshold)
        {
            room.AgingStartedAt = room.SeatedAt.Value.Add(thresholds.AgingThreshold);
            room.StaleStartedAt = null;
            room.State = RoomStates.Aging;
            return;
        }

        room.AgingStartedAt = null;
        room.StaleStartedAt = null;
        room.State = RoomStates.Seated;
    }

    private ProcedureCategory? FindProcedure(string procedureCode) =>
        _procedures.FirstOrDefault(item =>
            string.Equals(item.Id, procedureCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Label, procedureCode, StringComparison.OrdinalIgnoreCase));

    private static RoomState Available(int roomId) => new(roomId);

    private void SeedDemoRooms(DateTimeOffset now)
    {
        var thresholds = Thresholds;
        var seededRooms = Enumerable.Range(1, _roomCount)
            .Skip(1)
            .Zip(DemoSeedPatterns(), (roomId, pattern) => new { RoomId = roomId, Pattern = pattern });

        foreach (var seed in seededRooms)
        {
            SeedDemoRoom(
                seed.RoomId,
                seed.Pattern.DoctorId,
                seed.Pattern.ProcedureCode,
                now - seed.Pattern.Elapsed(thresholds));
        }
    }

    private static IEnumerable<DemoSeedPattern> DemoSeedPatterns()
    {
        yield return new("otte", "CON", thresholds => BeforeAging(thresholds));
        yield return new("pledger", "EXT", thresholds => StaleSample(thresholds));
        yield return new("gibson", "SED", thresholds => StaleSample(thresholds, TimeSpan.FromMinutes(11)));
        yield return new("schroeder", "IMP", thresholds => AgingSample(thresholds));
        yield return new("otte", "POST", thresholds => StaleSample(thresholds, TimeSpan.FromMinutes(5)));
        yield return new("pledger", "BX", thresholds => StaleSample(thresholds, TimeSpan.FromMinutes(15)));
        yield return new("gibson", "CON", thresholds => EarlySeatedSample(thresholds));
        yield return new("schroeder", "EXT", thresholds => StaleSample(thresholds, TimeSpan.FromMinutes(4)));
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
        room.DoctorArrivedAt = null;
        room.DoctorCompleteAt = null;
        room.RoomAvailableAt = null;
    }

    private void SeedDemoRoom(int roomId, string doctorId, string procedureCode, DateTimeOffset seatedAt)
    {
        var index = _rooms.FindIndex(room => room.RoomId == roomId);
        if (index >= 0)
        {
            _rooms[index] = Seated(roomId, doctorId, procedureCode, seatedAt);
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

        UpdateRoomState(room, DateTimeOffset.UtcNow);
        return room;
    }

    private static bool CanSeat(RoomState room) => room.State == RoomStates.Available && room.SeatedAt is null;

    private static bool CanMarkDoctorArrived(RoomState room) =>
        room.State is RoomStates.Seated or RoomStates.Aging or RoomStates.Stale;

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

    private BoardThresholdOptions Thresholds => _thresholdOptions.CurrentValue;

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
    IReadOnlyList<Doctor> Doctors,
    IReadOnlyList<ProcedureCategory> Procedures,
    IReadOnlyList<RoomStatus> Rooms,
    IReadOnlyList<RoomEvent> RecentEvents);

public sealed record Doctor(string Id, string Name, string Color);

public sealed record ProcedureCategory(string Id, string Label, string Name, string Icon);

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
    double AverageDoctorInRoomSeconds,
    double MedianDoctorInRoomSeconds,
    double AverageTurnoverSeconds,
    double MedianTurnoverSeconds,
    int AgingEventCount,
    int StaleEventCount,
    IReadOnlyList<DoctorCycleSummary> DoctorSummaries,
    IReadOnlyList<CompletedRoomCycle> RecentCompletedCycles);

public sealed class CompletedRoomCycle
{
    public int RoomId { get; set; }
    public string AssignedDoctor { get; set; } = "";
    public string ProcedureCode { get; set; } = "";
    public DateTimeOffset SeatedAt { get; set; }
    public DateTimeOffset DoctorArrivedAt { get; set; }
    public DateTimeOffset? DoctorCompleteAt { get; set; }
    public DateTimeOffset? RoomAvailableAt { get; set; }
    public int SeatedToDoctorSeconds { get; set; }
    public int? DoctorInRoomSeconds { get; set; }
    public int? TurnoverSeconds { get; set; }
    public int? TotalRoomCycleSeconds { get; set; }
    public string FinalWaitState { get; set; } = "";
    public bool AgingThresholdReached { get; set; }
    public bool StaleThresholdReached { get; set; }
}

public sealed record DoctorCycleSummary(
    string AssignedDoctor,
    DateOnly Month,
    int CompletedRoomCyclesCount,
    double AverageSeatedToDoctorSeconds,
    double MedianSeatedToDoctorSeconds,
    double AverageDoctorInRoomSeconds,
    double MedianDoctorInRoomSeconds,
    double AverageTurnoverSeconds,
    double MedianTurnoverSeconds,
    int AgingEventCount,
    int StaleEventCount);

public sealed record DemoSeedPattern(
    string DoctorId,
    string ProcedureCode,
    Func<BoardThresholdOptions, TimeSpan> Elapsed);

public sealed class RoomState(int roomId)
{
    public int RoomId { get; } = roomId;
    public string? AssignedDoctor { get; set; }
    public string? ProcedureCode { get; set; }
    public string State { get; set; } = RoomStates.Available;
    public DateTimeOffset? SeatedAt { get; set; }
    public DateTimeOffset? AgingStartedAt { get; set; }
    public DateTimeOffset? StaleStartedAt { get; set; }
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
    public const string DoctorInRoom = "doctorInRoom";
    public const string Turnover = "turnover";
}
