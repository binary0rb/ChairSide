namespace ChairSide.Board.Services;

/// <summary>
/// Pure deterministic construction of fixture descriptions. It has no repository, clock, locking,
/// or live-store dependencies; DemoBoardStore remains responsible for applying every description.
/// </summary>
internal sealed class DeterministicFixtureBuilder
{
    private const int MinExpectedUnits = 1;
    private const int MaxExpectedUnits = 24;
    private const string ManualCandidateSuggestion =
        "Stress fixture: planted manual audit candidate for review-queue testing.";

    private readonly IReadOnlyList<string> _doctorIds;
    private readonly IReadOnlyDictionary<string, ProcedureCategory> _activeProcedures;

    internal DeterministicFixtureBuilder(
        IReadOnlyList<Doctor> activeDoctors,
        IReadOnlyList<ProcedureCategory> activeProcedures)
    {
        _doctorIds = activeDoctors.Select(doctor => doctor.Id).ToArray();
        _activeProcedures = activeProcedures.ToDictionary(procedure => procedure.Code, StringComparer.OrdinalIgnoreCase);
    }

    internal DeterministicFixturePlan BuildSmallReportingPlan(DateTimeOffset now, int roomCount)
    {
        var today = UtcToday(now);
        return BuildHistory(
            roomCount,
            (cases, add) =>
            {
                var globalIndex = 0;
                for (var dayOffset = 0; dayOffset <= DeterministicFixtureCatalog.SyntheticHistoryDays; dayOffset++)
                {
                    var day = today.AddDays(-dayOffset);
                    for (var caseInDay = 0; caseInDay < DeterministicFixtureCatalog.CasesForDayOffset(dayOffset); caseInDay++)
                    {
                        add(cases, dayOffset, day, caseInDay, globalIndex);
                        globalIndex++;
                    }
                }
            });
    }

    internal DeterministicFixturePlan BuildLargeReportingPlan(
        DateTimeOffset now,
        int roomCount,
        int completedCycleTarget)
    {
        var target = Math.Clamp(
            completedCycleTarget,
            MaintenanceCommands.MinCompletedCycles,
            MaintenanceCommands.MaxCompletedCycles);
        var today = UtcToday(now);

        return BuildHistory(
            roomCount,
            (cases, add) =>
            {
                var globalIndex = 0;
                for (var dayOffset = 0; cases.Count < target && dayOffset <= target; dayOffset++)
                {
                    var day = today.AddDays(-dayOffset);
                    for (var caseInDay = 0;
                         caseInDay < DeterministicFixtureCatalog.LargeSyntheticCasesPerDay && cases.Count < target;
                         caseInDay++)
                    {
                        add(cases, dayOffset, day, caseInDay, globalIndex);
                        globalIndex++;
                    }
                }
            });
    }

    internal DeterministicFixturePlan BuildScenarioRichPlan(
        DateTimeOffset now,
        int roomCount,
        int dayOffsetShift = 0)
    {
        var today = UtcToday(now);
        return BuildHistory(
            roomCount,
            (cases, add) =>
            {
                var globalIndex = 0;
                for (var dayOffset = 0; dayOffset <= DeterministicFixtureCatalog.ScenarioRichHistoryDays; dayOffset++)
                {
                    var day = today.AddDays(-(dayOffset + dayOffsetShift));
                    for (var caseInDay = 0; caseInDay < DeterministicFixtureCatalog.ScenarioRichCasesPerDay; caseInDay++)
                    {
                        add(cases, dayOffset, day, caseInDay, globalIndex);
                        globalIndex++;
                    }
                }
            });
    }

    internal IReadOnlyList<ExplicitCompletedCycleFixture> BuildScenarioEdgeCases(
        DateTimeOffset now,
        int roomCount)
    {
        if (_doctorIds.Count == 0)
        {
            return [];
        }

        var today = UtcToday(now);
        var todayMarker = TodayMarkerTimestamps(today, now, 6, 8, 15, 6);

        return
        [
            Clean(1, 0, "CON", todayMarker.SeatedAt, todayMarker.PrepMin, todayMarker.ReadyMin, todayMarker.DoctorMin, todayMarker.TurnoverMin),
            Clean(2, 1, "EXT", today.AddDays(-3).AddHours(9), 8, 10, 30, 8),
            Clean(3, 2, "IMP", today.AddDays(-15).AddHours(9), 10, 15, 60, 10),
            Clean(4, 3, "BX", today.AddDays(-40).AddHours(9), 8, 12, 25, 8),
            Clean(5, 0, "ZZZSTRESS", today.AddDays(-2).AddHours(8), 10, 15, 35, 10),
            Clean(6, 1, "SED", today.AddDays(-2).AddHours(10), 10, 15, 35, 10),
            Clean(7, 2, "IMP", today.AddDays(-2).AddHours(2), 10, 15, 245, 10),
            Clean(8, 3, "EXT", today.AddDays(-2).AddHours(23).AddMinutes(50), 5, 5, 15, 5),
            MissingTiming(9, 0, "POST", today.AddDays(-2).AddHours(14), 50, 10),
            Clean(10, 1, "BX", today.AddDays(-1).AddHours(9), 8, 12, 28, 8, ManualCandidateSuggestion),
            Clean(11, 0, "CON", today.AddDays(-5).AddHours(9).AddMinutes(2), 5, 5, 60, 5),
            Clean(12, 0, "POST", today.AddDays(-5).AddHours(9).AddMinutes(12), 5, 5, 50, 5),
            Clean(13, 0, "BX", today.AddDays(-5).AddHours(9).AddMinutes(22), 5, 5, 40, 5)
        ];

        CleanCompletedCycleFixture Clean(
            int roomIndex,
            int doctorIndex,
            string procedureCode,
            DateTimeOffset seatedAt,
            int prepMin,
            int readyMin,
            int doctorMin,
            int turnoverMin,
            string? manualReviewSuggestion = null)
        {
            return new(
                RoomForEdgeCase(roomIndex, roomCount),
                _doctorIds[doctorIndex % _doctorIds.Count],
                procedureCode,
                seatedAt,
                prepMin,
                readyMin,
                doctorMin,
                turnoverMin,
                manualReviewSuggestion);
        }

        MissingTimingCompletedCycleFixture MissingTiming(
            int roomIndex,
            int doctorIndex,
            string procedureCode,
            DateTimeOffset seatedAt,
            int completeMin,
            int turnoverMin)
        {
            return new(
                RoomForEdgeCase(roomIndex, roomCount),
                _doctorIds[doctorIndex % _doctorIds.Count],
                procedureCode,
                seatedAt,
                completeMin,
                turnoverMin);
        }
    }

    private DeterministicFixturePlan BuildHistory(
        int roomCount,
        Action<List<SyntheticCompletedCycleFixture>, Action<List<SyntheticCompletedCycleFixture>, int, DateTimeOffset, int, int>> populate)
    {
        if (_doctorIds.Count == 0)
        {
            return DeterministicFixturePlan.Empty;
        }

        var cases = new List<SyntheticCompletedCycleFixture>();
        populate(cases, (target, dayOffset, day, caseInDay, globalIndex) =>
        {
            var fixture = BuildSyntheticCase(roomCount, dayOffset, day, caseInDay, globalIndex);
            if (fixture is not null)
            {
                target.Add(fixture);
            }
        });

        return new(
            cases,
            cases.Select(item => item.DoctorId).Distinct(StringComparer.Ordinal).Count(),
            cases.Select(item => item.BaseFamilyCode).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private SyntheticCompletedCycleFixture? BuildSyntheticCase(
        int roomCount,
        int dayOffset,
        DateTimeOffset day,
        int caseInDay,
        int globalIndex)
    {
        var doctorIndex = globalIndex % _doctorIds.Count;
        var profile = DeterministicFixtureCatalog.SyntheticProfiles[
            doctorIndex % DeterministicFixtureCatalog.SyntheticProfiles.Count];
        var doctorId = _doctorIds[doctorIndex];
        var jitter = new SyntheticJitter(DeterministicSeed(dayOffset, doctorIndex, caseInDay));
        var family = DeterministicFixtureCatalog.SyntheticFamilies[
            globalIndex % DeterministicFixtureCatalog.SyntheticFamilies.Count];

        if (!_activeProcedures.TryGetValue(family.Code, out var procedure))
        {
            return null;
        }

        var sedation = family.SedationEligible
            && procedure.SedationEligible
            && jitter.Next(0, 99) < profile.SedationChancePercent;
        var storedCode = sedation ? $"{procedure.Code}+SED" : procedure.Code;
        var defaultUnits = Math.Clamp(procedure.DefaultExpectedUnits, MinExpectedUnits, MaxExpectedUnits);
        var unitDelta = family.Code is "EXT" or "IMP" ? profile.VariableUnitDelta : 0;
        if (sedation)
        {
            unitDelta++;
        }

        var expectedUnits = Math.Clamp(defaultUnits + unitDelta, MinExpectedUnits, MaxExpectedUnits);
        var expectedMinutes = expectedUnits * 10;
        var minFlow = sedation ? family.MinFlowMinutes + 15 : family.MinFlowMinutes;
        var maxFlow = sedation ? family.MaxFlowMinutes + 15 : family.MaxFlowMinutes;
        var varianceJitter = jitter.Next(-profile.VarianceSpread, profile.VarianceSpread);
        var measuredMinutes = globalIndex % 9 == 0
            ? Math.Clamp(expectedMinutes, minFlow, maxFlow)
            : Math.Clamp(
                expectedMinutes
                + profile.VarianceBiasMinutes
                + family.CharacterLeanMinutes * profile.FamilyLeanWeight
                + varianceJitter,
                minFlow,
                maxFlow);

        var prepMin = Math.Clamp(measuredMinutes * 12 / 100, 2, 12);
        var readyMin = Math.Clamp(measuredMinutes * 18 / 100, 2, 20);
        if (prepMin + readyMin >= measuredMinutes)
        {
            prepMin = Math.Max(1, measuredMinutes / 4);
            readyMin = Math.Max(1, measuredMinutes / 4);
        }

        // Draw turnover before seat-time jitter. This order is part of the accepted fixture output.
        var turnoverMin = jitter.Next(4, 12);
        var seatedAt = day.AddHours(8 + caseInDay).AddMinutes(jitter.Next(0, 11) * 5);

        return new(
            1 + (caseInDay % Math.Max(1, roomCount)),
            doctorId,
            storedCode,
            family.Code,
            seatedAt,
            prepMin,
            readyMin,
            Math.Max(1, measuredMinutes - prepMin - readyMin),
            turnoverMin,
            defaultUnits,
            expectedUnits);
    }

    private static DateTimeOffset UtcToday(DateTimeOffset now) =>
        new(now.UtcDateTime.Date, TimeSpan.Zero);

    private static int RoomForEdgeCase(int index, int roomCount) =>
        1 + (index % Math.Max(1, roomCount));

    internal static TodayMarkerFixture TodayMarkerTimestamps(
        DateTimeOffset today,
        DateTimeOffset now,
        int prepMin,
        int readyMin,
        int doctorMin,
        int turnoverMin)
    {
        var totalMin = prepMin + readyMin + doctorMin + turnoverMin;
        var elapsedTodayMin = Math.Max(0, (int)(now - today).TotalMinutes);
        var safeTotalMin = Math.Min(totalMin, elapsedTodayMin);
        var seatedAt = now.AddMinutes(-safeTotalMin);
        if (safeTotalMin >= totalMin)
        {
            return new(seatedAt, prepMin, readyMin, doctorMin, turnoverMin);
        }

        var scale = totalMin == 0 ? 0d : (double)safeTotalMin / totalMin;
        var scaledPrep = (int)Math.Round(prepMin * scale);
        var scaledReady = (int)Math.Round(readyMin * scale);
        var scaledDoctor = (int)Math.Round(doctorMin * scale);
        var scaledTurnover = Math.Max(0, safeTotalMin - scaledPrep - scaledReady - scaledDoctor);
        return new(seatedAt, scaledPrep, scaledReady, scaledDoctor, scaledTurnover);
    }

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
}

internal sealed record DeterministicFixturePlan(
    IReadOnlyList<SyntheticCompletedCycleFixture> SyntheticCycles,
    int DoctorsRepresented,
    int ProcedureFamiliesRepresented)
{
    internal static DeterministicFixturePlan Empty { get; } = new([], 0, 0);
}

internal sealed record SyntheticCompletedCycleFixture(
    int RoomId,
    string DoctorId,
    string StoredProcedureCode,
    string BaseFamilyCode,
    DateTimeOffset SeatedAt,
    int PrepMinutes,
    int ReadyMinutes,
    int DoctorMinutes,
    int TurnoverMinutes,
    int DefaultExpectedUnits,
    int ExpectedUnits);

internal abstract record ExplicitCompletedCycleFixture(
    int RoomId,
    string DoctorId,
    string ProcedureCode,
    DateTimeOffset SeatedAt,
    string? ManualReviewSuggestion);

internal sealed record CleanCompletedCycleFixture(
    int RoomId,
    string DoctorId,
    string ProcedureCode,
    DateTimeOffset SeatedAt,
    int PrepMinutes,
    int ReadyMinutes,
    int DoctorMinutes,
    int TurnoverMinutes,
    string? ManualReviewSuggestion = null)
    : ExplicitCompletedCycleFixture(RoomId, DoctorId, ProcedureCode, SeatedAt, ManualReviewSuggestion);

internal sealed record MissingTimingCompletedCycleFixture(
    int RoomId,
    string DoctorId,
    string ProcedureCode,
    DateTimeOffset SeatedAt,
    int CompleteMinutes,
    int TurnoverMinutes)
    : ExplicitCompletedCycleFixture(RoomId, DoctorId, ProcedureCode, SeatedAt, null);

internal sealed record TodayMarkerFixture(
    DateTimeOffset SeatedAt,
    int PrepMin,
    int ReadyMin,
    int DoctorMin,
    int TurnoverMin);

internal sealed record DoctorStyleProfile(
    int DoctorIndex,
    int VarianceBiasMinutes,
    int VarianceSpread,
    int FamilyLeanWeight,
    int VariableUnitDelta,
    int SedationChancePercent);

internal sealed record SyntheticFamily(
    string Code,
    int MinFlowMinutes,
    int MaxFlowMinutes,
    int CharacterLeanMinutes,
    bool SedationEligible);

internal struct SyntheticJitter(int seed)
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
