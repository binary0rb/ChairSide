using ChairSide.Board.Options;
using ChairSide.Board.Services;

using Microsoft.Extensions.Hosting;

namespace ChairSide.Board.Tests;

public sealed class DeterministicFixtureBuilderTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Catalog_preserves_every_fixed_live_fixture_row()
    {
        Assert.Equal(
            [
                "1|||available|False", "2|otte|CON|seated|False", "3|pledger|EXT|readyForDoctor|False",
                "4|gibson|IMPRES|aging|False", "5|schroeder|BX|stale|True", "6|otte|IMP|doctorInRoom|False",
                "7|pledger|POST|turnover|False", "8|gibson|PCOC|seated|False",
                "9|schroeder|EXT|readyForDoctor|True", "10|otte|BX|aging|False",
                "11|pledger|IMP|stale|False", "12|gibson|MISC|doctorInRoom|False"
            ],
            Rows(DeterministicFixtureCatalog.LiveBoardStress));

        Assert.Equal(
            [
                "1|otte|CON|seated|False", "2|pledger|CON|seated|False",
                "3|pledger|EXT|readyForDoctor|False", "4|pledger|IMP|aging|False",
                "5|gibson|CON|seated|False", "6|gibson|EXT|readyForDoctor|False",
                "7|gibson|IMP|aging|False", "8|gibson|BX|stale|False",
                "9|schroeder|CON|seated|False", "10|schroeder|EXT|readyForDoctor|False",
                "11|schroeder|IMP|aging|False", "12|schroeder|BX|stale|False"
            ],
            Rows(DeterministicFixtureCatalog.DoctorViewStress));

        Assert.Equal(
            [
                "1|otte|CON|seated|False", "2|otte|EXT|seated|False",
                "3|otte|IMP|readyForDoctor|False", "4|otte|BX|aging|False",
                "5|otte|POST|stale|False", "6|pledger|CON|seated|False",
                "7|pledger|EXT|readyForDoctor|False", "8|pledger|IMP|aging|False",
                "9|gibson|CON|seated|False", "10|gibson|EXT|readyForDoctor|False",
                "11|schroeder|CON|seated|False", "12|schroeder|EXT|readyForDoctor|False"
            ],
            Rows(DeterministicFixtureCatalog.DoctorViewOverflowStress));

        Assert.Equal(
            [
                "1|||available|False", "2|otte|CON|seated|False", "3|otte|EXT|seated|False",
                "4|otte|IMP|readyForDoctor|False", "5|otte|BX|aging|False",
                "6|otte|POST|stale|False", "7|gibson|IMPRES|doctorInRoom|False",
                "8|pledger|POST|turnover|False", "9|schroeder|PCOC|readyForDoctor|False",
                "10|pledger|MISC|aging|False", "11|gibson|EXT|stale|True",
                "12|schroeder|BX|seated|False"
            ],
            Rows(DeterministicFixtureCatalog.FullStress));
    }

    [Theory]
    [InlineData(MaintenanceCommands.ProfileReportingVolume, 0, "LargeReporting:500:0", false)]
    [InlineData(MaintenanceCommands.ProfileLiveBoardStress, 12, "", false)]
    [InlineData(MaintenanceCommands.ProfileDoctorViewStress, 12, "", false)]
    [InlineData(MaintenanceCommands.ProfileDoctorViewOverflowStress, 12, "", false)]
    [InlineData(MaintenanceCommands.ProfileScenarioRich, 0, "ScenarioRich:0:0", true)]
    [InlineData(MaintenanceCommands.ProfileFullStress, 12, "ScenarioRich:0:0", true)]
    [InlineData(MaintenanceCommands.ProfileAllScenarios, 12, "LargeReporting:500:0,ScenarioRich:0:2000", true)]
    public void Catalog_composes_all_seven_accepted_profiles_in_order(
        string profile,
        int liveRoomCount,
        string expectedSegments,
        bool expectedEdges)
    {
        var composition = DeterministicFixtureCatalog.ComposeProfile(profile, 500);

        Assert.Equal(liveRoomCount, composition.LiveRooms.Count);
        Assert.Equal(
            expectedSegments,
            string.Join(",", composition.HistorySegments.Select(
                segment => $"{segment.Kind}:{segment.TargetCount}:{segment.DayOffsetShift}")));
        Assert.Equal(expectedEdges, composition.IncludeScenarioEdgeCases);
    }

    [Fact]
    public void Small_reporting_builder_is_repeatable_and_preserves_golden_first_cycle()
    {
        var builder = CreateBuilder();

        var first = builder.BuildSmallReportingPlan(FixedNow, roomCount: 12);
        var second = builder.BuildSmallReportingPlan(FixedNow, roomCount: 12);

        Assert.Equal(first.SyntheticCycles.ToArray(), second.SyntheticCycles.ToArray());
        Assert.Equal(first.DoctorsRepresented, second.DoctorsRepresented);
        Assert.Equal(first.ProcedureFamiliesRepresented, second.ProcedureFamiliesRepresented);
        Assert.Equal(114, first.SyntheticCycles.Count);
        Assert.Equal(4, first.DoctorsRepresented);
        Assert.Equal(7, first.ProcedureFamiliesRepresented);

        var cycle = first.SyntheticCycles[0];
        Assert.Equal(1, cycle.RoomId);
        Assert.Equal("otte", cycle.DoctorId);
        Assert.Equal("CON", cycle.StoredProcedureCode);
        Assert.Equal("CON", cycle.BaseFamilyCode);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 8, 35, 0, TimeSpan.Zero), cycle.SeatedAt);
        Assert.Equal(2, cycle.PrepMinutes);
        Assert.Equal(2, cycle.ReadyMinutes);
        Assert.Equal(6, cycle.DoctorMinutes);
        Assert.Equal(7, cycle.TurnoverMinutes);
        Assert.Equal(1, cycle.DefaultExpectedUnits);
        Assert.Equal(1, cycle.ExpectedUnits);
    }

    [Fact]
    public void Missing_family_skips_the_slot_but_advances_global_rotation()
    {
        var procedures = Procedures().Where(procedure => procedure.Code != "POST").ToArray();
        var builder = new DeterministicFixtureBuilder(Doctors(), procedures);

        var plan = builder.BuildSmallReportingPlan(FixedNow, roomCount: 12);

        Assert.Equal(97, plan.SyntheticCycles.Count);
        Assert.Equal("CON", plan.SyntheticCycles[0].BaseFamilyCode);
        Assert.Equal("IMPRES", plan.SyntheticCycles[1].BaseFamilyCode);
        Assert.Equal("gibson", plan.SyntheticCycles[1].DoctorId);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 10, 25, 0, TimeSpan.Zero), plan.SyntheticCycles[1].SeatedAt);
    }

    [Fact]
    public void Large_reporting_builder_clamps_to_both_accepted_volume_bounds()
    {
        var builder = CreateBuilder();

        Assert.Equal(
            MaintenanceCommands.MinCompletedCycles,
            builder.BuildLargeReportingPlan(FixedNow, 12, completedCycleTarget: 1).SyntheticCycles.Count);
        Assert.Equal(
            MaintenanceCommands.MaxCompletedCycles,
            builder.BuildLargeReportingPlan(FixedNow, 12, completedCycleTarget: 50_000).SyntheticCycles.Count);
    }

    [Fact]
    public void Builder_returns_empty_history_and_edges_when_no_active_doctors_exist()
    {
        var procedures = Procedures();
        var builder = new DeterministicFixtureBuilder([], procedures);

        Assert.Equal(DeterministicFixturePlan.Empty, builder.BuildSmallReportingPlan(FixedNow, 12));
        Assert.Equal(DeterministicFixturePlan.Empty, builder.BuildLargeReportingPlan(FixedNow, 12, 500));
        Assert.Equal(DeterministicFixturePlan.Empty, builder.BuildScenarioRichPlan(FixedNow, 12));
        Assert.Empty(builder.BuildScenarioEdgeCases(FixedNow, 12));
    }

    [Fact]
    public void Builder_does_not_mutate_roster_sources()
    {
        var doctors = Doctors();
        var procedures = Procedures();
        var doctorSnapshot = doctors.ToArray();
        var procedureSnapshot = procedures.ToArray();
        var builder = new DeterministicFixtureBuilder(doctors, procedures);

        builder.BuildSmallReportingPlan(FixedNow, 12);
        builder.BuildScenarioRichPlan(FixedNow, 12);
        builder.BuildScenarioEdgeCases(FixedNow, 12);

        Assert.Equal(doctorSnapshot, doctors);
        Assert.Equal(procedureSnapshot, procedures);
    }

    [Fact]
    public void Near_midnight_today_marker_never_places_a_timestamp_in_the_future()
    {
        var today = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var now = today.AddMinutes(5);

        var marker = DeterministicFixtureBuilder.TodayMarkerTimestamps(today, now, 6, 8, 15, 6);

        Assert.Equal(today, marker.SeatedAt);
        Assert.Equal(5, marker.PrepMin + marker.ReadyMin + marker.DoctorMin + marker.TurnoverMin);
        Assert.Equal(now, marker.SeatedAt.AddMinutes(
            marker.PrepMin + marker.ReadyMin + marker.DoctorMin + marker.TurnoverMin));
    }

    [Fact]
    public void Store_application_matches_every_pure_small_history_description()
    {
        using var workspace = TestWorkspace.Create();
        var clock = new ManualTimeProvider(FixedNow);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            roomCount: 12,
            timeProvider: clock);
        var builder = CreateBuilder();
        var expected = builder.BuildSmallReportingPlan(FixedNow, roomCount: 12).SyntheticCycles
            .OrderBy(cycle => cycle.RoomId)
            .ThenBy(cycle => cycle.SeatedAt)
            .ToArray();

        var result = context.Store.SeedSyntheticReportData();
        var actual = context.Repository.LoadCompletedCycles()
            .OrderBy(cycle => cycle.RoomId)
            .ThenBy(cycle => cycle.SeatedAt)
            .ToArray();

        Assert.Equal(expected.Length, result.CyclesInserted);
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            var description = expected[index];
            var persisted = actual[index];
            Assert.Equal(description.RoomId, persisted.RoomId);
            Assert.Equal(description.DoctorId, persisted.AssignedDoctor);
            Assert.Equal(description.StoredProcedureCode, persisted.ProcedureCode);
            Assert.Equal(description.SeatedAt, persisted.SeatedAt);
            Assert.Equal(description.PrepMinutes * 60, persisted.PrepSeconds);
            Assert.Equal(description.ReadyMinutes * 60, persisted.ReadyToDoctorSeconds);
            Assert.Equal(description.DoctorMinutes * 60, persisted.DoctorInRoomSeconds);
            Assert.Equal(description.TurnoverMinutes * 60, persisted.TurnoverSeconds);
            Assert.Equal(description.DefaultExpectedUnits, persisted.OriginalDefaultExpectedUnits);
            Assert.Equal(description.ExpectedUnits, persisted.ExpectedAllocationUnits);
        }
    }

    [Fact]
    public void Store_skips_catalog_rooms_beyond_the_configured_room_count()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            roomCount: 3,
            timeProvider: new ManualTimeProvider(FixedNow));

        var result = context.Store.ResetAndSeedStressFixture(
            MaintenanceCommands.ProfileLiveBoardStress,
            null);

        Assert.Equal(3, result.ActiveRoomsReset);
        Assert.Equal(3, result.RoomStateCounts.Values.Sum());
        Assert.All(context.Store.GetSnapshot().Rooms, room => Assert.InRange(room.RoomId, 1, 3));
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)!.State);
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(2)!.State);
        Assert.Equal(RoomStates.ReadyForDoctor, context.Store.GetRoom(3)!.State);
    }

    private static DeterministicFixtureBuilder CreateBuilder()
    {
        var procedures = Procedures();
        return new DeterministicFixtureBuilder(
            Doctors(),
            procedures.Where(procedure => procedure.Code != "SED").ToArray());
    }

    private static Doctor[] Doctors() =>
    [
        new("otte", "Dr. Otte", "LDO", "#dc2626"),
        new("pledger", "Dr. Pledger", "JWP", "#16a34a"),
        new("gibson", "Dr. Gibson", "JEG", "#7e22ce"),
        new("schroeder", "Dr. Schroeder", "NDS", "#ca8a04")
    ];

    private static ProcedureCategory[] Procedures() =>
        ProcedureRosterOptions.DefaultProcedures()
            .Select(procedure => new ProcedureCategory(
                procedure.Id!,
                procedure.Code,
                procedure.Label,
                procedure.Icon,
                procedure.SedationEligible,
                procedure.AllocationBehavior,
                procedure.DefaultExpectedUnits))
            .ToArray();

    private static string[] Rows(IEnumerable<LiveRoomFixture> fixtures) =>
        fixtures.Select(fixture =>
            $"{fixture.RoomId}|{fixture.DoctorId}|{fixture.ProcedureCode}|{fixture.TargetState}|{fixture.Sedation}").ToArray();
}
