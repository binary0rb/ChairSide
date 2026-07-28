namespace ChairSide.Board.Services;

/// <summary>
/// Static, deterministic fixture definitions. Profile names remain owned by
/// <see cref="MaintenanceCommands"/>; this catalog only maps those names to fixture composition.
/// </summary>
internal static class DeterministicFixtureCatalog
{
    internal const int SyntheticHistoryDays = 41;
    internal const int LargeSyntheticCasesPerDay = 12;
    internal const int ScenarioRichHistoryDays = 120;
    internal const int ScenarioRichCasesPerDay = 3;
    internal const int AllScenariosHistoryDayOffsetShift = 2000;

    internal static IReadOnlyList<DoctorStyleProfile> SyntheticProfiles { get; } =
    [
        new(DoctorIndex: 0, VarianceBiasMinutes: -5, VarianceSpread: 6, FamilyLeanWeight: 1, VariableUnitDelta: 1, SedationChancePercent: 25),
        new(DoctorIndex: 1, VarianceBiasMinutes: 0, VarianceSpread: 8, FamilyLeanWeight: 1, VariableUnitDelta: 0, SedationChancePercent: 20),
        new(DoctorIndex: 2, VarianceBiasMinutes: 1, VarianceSpread: 14, FamilyLeanWeight: 2, VariableUnitDelta: 0, SedationChancePercent: 30),
        new(DoctorIndex: 3, VarianceBiasMinutes: 7, VarianceSpread: 5, FamilyLeanWeight: 1, VariableUnitDelta: -1, SedationChancePercent: 15)
    ];

    internal static IReadOnlyList<SyntheticFamily> SyntheticFamilies { get; } =
    [
        new("CON", 10, 40, -3, false),
        new("POST", 5, 25, -2, false),
        new("IMPRES", 10, 35, -2, false),
        new("BX", 20, 60, 3, false),
        new("EXT", 20, 90, 5, true),
        new("IMP", 45, 120, 5, true),
        new("MISC", 10, 60, 2, false)
    ];

    internal static FixtureProfileComposition ComposeProfile(string profile, int completedCycles)
    {
        return profile switch
        {
            MaintenanceCommands.ProfileReportingVolume =>
                new([], [new(FixtureHistoryKind.LargeReporting, completedCycles, 0)], IncludeScenarioEdgeCases: false),
            MaintenanceCommands.ProfileLiveBoardStress =>
                new(LiveBoardStress, [], IncludeScenarioEdgeCases: false),
            MaintenanceCommands.ProfileDoctorViewStress =>
                new(DoctorViewStress, [], IncludeScenarioEdgeCases: false),
            MaintenanceCommands.ProfileDoctorViewOverflowStress =>
                new(DoctorViewOverflowStress, [], IncludeScenarioEdgeCases: false),
            MaintenanceCommands.ProfileScenarioRich =>
                new([], [new(FixtureHistoryKind.ScenarioRich, 0, 0)], IncludeScenarioEdgeCases: true),
            MaintenanceCommands.ProfileFullStress =>
                new(FullStress, [new(FixtureHistoryKind.ScenarioRich, 0, 0)], IncludeScenarioEdgeCases: true),
            MaintenanceCommands.ProfileAllScenarios =>
                new(
                    FullStress,
                    [
                        new(FixtureHistoryKind.LargeReporting, completedCycles, 0),
                        new(FixtureHistoryKind.ScenarioRich, 0, AllScenariosHistoryDayOffsetShift)
                    ],
                    IncludeScenarioEdgeCases: true),
            _ => throw new InvalidOperationException($"Unknown stress fixture profile '{profile}'.")
        };
    }

    internal static IReadOnlyList<LiveRoomFixture> LiveBoardStress { get; } =
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

    internal static IReadOnlyList<LiveRoomFixture> DoctorViewStress { get; } =
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

    internal static IReadOnlyList<LiveRoomFixture> DoctorViewOverflowStress { get; } =
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

    internal static IReadOnlyList<LiveRoomFixture> FullStress { get; } =
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

    internal static int CasesForDayOffset(int dayOffset)
    {
        if (dayOffset == 0)
        {
            return 8;
        }

        if (dayOffset <= 6)
        {
            return 4;
        }

        return dayOffset <= 29 ? 2 + (dayOffset % 2) : 2;
    }
}

internal enum FixtureHistoryKind
{
    LargeReporting,
    ScenarioRich
}

internal sealed record FixtureHistorySegment(FixtureHistoryKind Kind, int TargetCount, int DayOffsetShift);

internal sealed record FixtureProfileComposition(
    IReadOnlyList<LiveRoomFixture> LiveRooms,
    IReadOnlyList<FixtureHistorySegment> HistorySegments,
    bool IncludeScenarioEdgeCases);

/// <summary>One deterministic live-room allocation consumed by DemoBoardStore.</summary>
internal sealed record LiveRoomFixture(
    int RoomId,
    string? DoctorId,
    string? ProcedureCode,
    string TargetState,
    bool Sedation = false);
