using System.Text.Json;

using ChairSide.Board.Services;

namespace ChairSide.Board.Tests;

public sealed class ReportsSnapshotBuilderTests
{
    [Fact]
    public void Compose_characterizes_all_reporting_populations_without_mutating_source_history()
    {
        var standard = Cycle(
            id: 1,
            roomId: 1,
            procedureCode: "EXT",
            seatedAt: Utc(2026, 7, 20, 8, 0),
            readyAt: Utc(2026, 7, 20, 8, 10),
            arrivedAt: Utc(2026, 7, 20, 8, 20),
            completeAt: Utc(2026, 7, 20, 9, 0),
            availableAt: Utc(2026, 7, 20, 9, 10),
            expectedAllocationMinutes: 60);
        standard.AgingThresholdReached = true;

        var sedationVariant = Cycle(
            id: 2,
            roomId: 2,
            procedureCode: "EXT+SED",
            seatedAt: Utc(2026, 7, 20, 8, 30),
            readyAt: Utc(2026, 7, 20, 8, 40),
            arrivedAt: Utc(2026, 7, 20, 9, 5),
            completeAt: Utc(2026, 7, 20, 9, 45),
            availableAt: Utc(2026, 7, 20, 9, 55),
            expectedAllocationMinutes: 60);
        sedationVariant.StaleThresholdReached = true;

        var incomplete = Cycle(
            id: 3,
            roomId: 3,
            procedureCode: "CON",
            seatedAt: Utc(2026, 7, 20, 10, 0),
            readyAt: Utc(2026, 7, 20, 10, 5),
            arrivedAt: Utc(2026, 7, 20, 10, 10),
            completeAt: null,
            availableAt: null,
            expectedAllocationMinutes: 30);

        var reportingException = Cycle(
            id: 4,
            roomId: 4,
            procedureCode: "SED",
            seatedAt: Utc(2026, 7, 20, 11, 0),
            readyAt: Utc(2026, 7, 20, 11, 5),
            arrivedAt: Utc(2026, 7, 20, 11, 10),
            completeAt: Utc(2026, 7, 20, 11, 30),
            availableAt: Utc(2026, 7, 20, 11, 40),
            expectedAllocationMinutes: 30);

        var manualException = Cycle(
            id: 5,
            roomId: 5,
            procedureCode: "CON",
            seatedAt: Utc(2026, 7, 20, 12, 0),
            readyAt: Utc(2026, 7, 20, 12, 5),
            arrivedAt: Utc(2026, 7, 20, 12, 10),
            completeAt: Utc(2026, 7, 20, 12, 25),
            availableAt: Utc(2026, 7, 20, 12, 35),
            expectedAllocationMinutes: 30);
        manualException.IsException = true;
        manualException.RequiresReview = true;
        manualException.ExceptionReason = ExceptionReasons.ManualReview;
        manualException.SuggestedAction = "Review";

        var abortedException = new AbortedRoomAssignment
        {
            AbortedAssignmentId = 9,
            EpisodeId = "aborted-episode",
            RoomId = 6,
            AssignedDoctor = "otte",
            ProcedureCode = "EXT",
            PrestageStartedAt = Utc(2026, 7, 20, 12, 40),
            SeatedAt = Utc(2026, 7, 20, 12, 45),
            ReadyForDoctorAt = Utc(2026, 7, 20, 12, 50),
            TerminatedAt = Utc(2026, 7, 20, 13, 0),
            TerminatedFromState = RoomStates.ReadyForDoctor,
            TerminationKind = ExceptionReasons.AfterHoursSweep,
            IsException = true,
            RequiresReview = true,
            ExceptionReason = ExceptionReasons.AfterHoursSweep,
            SuggestedAction = "Review"
        };

        var composition = CreateBuilder().Compose(
            [standard, sedationVariant, incomplete, reportingException, manualException],
            [abortedException],
            ReportDateRange.AllTime);
        var snapshot = ReportsSnapshotAdapter.ToSnapshot(composition);

        Assert.Equal(3, composition.Population.CompletedRoomCyclesCount);
        Assert.Equal(2, composition.Population.IncludedCompletedCycleCount);
        Assert.Equal(1, composition.Population.ExcludedCompletedCycleCount);
        Assert.Equal(1, composition.Population.ExceptionCount);
        Assert.Equal(3, composition.Population.RecentCompletedCycles.Count);

        Assert.Null(composition.Window.RangeStartDate);
        Assert.Null(composition.Window.RangeEndDate);
        Assert.Equal("All time", composition.Window.RangeLabel);
        Assert.Equal(4, composition.Window.TotalCompletedCycleCount);

        Assert.Equal(1, composition.Timing.AgingEventCount);
        Assert.Equal(1, composition.Timing.StaleEventCount);
        Assert.Equal(2, Assert.Single(composition.Timing.Trends!.Buckets).CompletedCycleCount);

        Assert.Equal(2, composition.Procedures.ProcedureSummaries.Count);
        Assert.Equal(1, composition.Procedures.SedationCaseCount);
        Assert.Equal(1, composition.Procedures.NonSedationCaseCount);
        Assert.Single(composition.Procedures.BaseProcedureSummaries);

        Assert.NotNull(composition.Allocation.AllocationVariance);
        Assert.Single(composition.Allocation.DoctorDailyAllocationSeries!);
        Assert.NotNull(composition.Allocation.ScheduleFit);

        Assert.Single(composition.DoctorDetail.DoctorSummaries);
        Assert.Single(composition.DoctorDetail.ObservedDoctorDays!);
        Assert.Equal(2, composition.DoctorDetail.DoctorProcedureMix!.Count);

        Assert.Single(composition.ReviewQueue.ExceptionCycles);
        Assert.Equal(2, composition.ReviewQueue.ExceptionReviewRecords!.Count);

        Assert.Equal(3, snapshot.CompletedRoomCyclesCount);
        Assert.Equal(2, snapshot.IncludedCompletedCycleCount);
        Assert.Equal(1, snapshot.ExcludedCompletedCycleCount);
        Assert.Equal(1, snapshot.ExceptionCount);
        Assert.Equal(1, snapshot.SedationCaseCount);
        Assert.Equal(1, snapshot.NonSedationCaseCount);

        Assert.Equal(2, snapshot.ProcedureSummaries.Count);
        var baseProcedure = Assert.Single(snapshot.BaseProcedureSummaries);
        Assert.Equal("EXT", baseProcedure.ProcedureCode);
        Assert.Equal(2, baseProcedure.CompletedCycleCount);

        Assert.NotNull(snapshot.AllocationVariance);
        Assert.Equal(2, snapshot.AllocationVariance.AllocationVarianceCycleCount);
        Assert.Equal(15, snapshot.AllocationVariance.NetAllocationVarianceMinutes);

        Assert.NotNull(snapshot.ScheduleFit);
        Assert.Equal(2, snapshot.ScheduleFit.IncludedCycleCount);
        Assert.Equal(2, snapshot.ScheduleFit.ScheduleFitCycleCount);

        Assert.NotNull(snapshot.Trends);
        Assert.Equal(2, Assert.Single(snapshot.Trends.Buckets).CompletedCycleCount);

        Assert.NotNull(snapshot.ObservedDoctorDays);
        var observedDay = Assert.Single(snapshot.ObservedDoctorDays);
        Assert.Equal(2, observedDay.EncounterCount);
        Assert.Equal(2, observedDay.MaxActiveRoomCount);

        Assert.NotNull(snapshot.DoctorProcedureMix);
        Assert.Equal(2, snapshot.DoctorProcedureMix.Count);
        Assert.All(snapshot.DoctorProcedureMix, row => Assert.Equal(2, row.DoctorCompletedCaseCount));
        Assert.Equal(1d, snapshot.DoctorProcedureMix.Sum(row => row.ShareOfDoctorCases), precision: 10);

        var exceptionCycle = Assert.Single(snapshot.ExceptionCycles);
        Assert.Equal(5, exceptionCycle.CompletedCycleId);
        Assert.Equal(
            [ExceptionReviewSources.AbortedAssignment, ExceptionReviewSources.CompletedCycle],
            snapshot.ExceptionReviewRecords!.Select(record => record.SourceType).ToArray());

        Assert.False(reportingException.HasReportingException);
        Assert.Empty(reportingException.ReportingExceptionReasons);
        Assert.Null(standard.MeasuredCaseFlowMinutes);
        Assert.Null(standard.AllocationVarianceMinutes);
        Assert.Null(standard.DoctorOccupiedWaitSeconds);
        Assert.Null(standard.DoctorAvailableWaitSeconds);
    }

    [Fact]
    public void Adapter_preserves_every_named_section_value_in_flat_contract()
    {
        var recentCompletedCycles = new List<CompletedRoomCycle>();
        var exceptionCycles = new List<CompletedRoomCycle>();
        var procedureSummaries = new List<ProcedureCycleSummary>();
        var baseProcedureSummaries = new List<ProcedureCycleSummary>();
        var allocationVariance = new AllocationVarianceSummary(1, 2, 3, 4, 5, 6, 7, 8, 9);
        var doctorDailyAllocationSeries = new List<DoctorDailyAllocation>();
        var scheduleFit = ScheduleFitReportBuilder.Build([]);
        var doctorSummaries = new List<DoctorCycleSummary>();
        var trends = new ReportTrendSnapshot("Sentinel", []);
        var observedDoctorDays = new List<ObservedDoctorDay>();
        var doctorProcedureMix = new List<DoctorProcedureMixRow>();
        var exceptionReviewRecords = new List<ExceptionReviewRecord>();

        var composition = new ReportsSnapshotComposition
        {
            Population = new ReportPopulationSection
            {
                CompletedRoomCyclesCount = 101,
                RecentCompletedCycles = recentCompletedCycles,
                IncludedCompletedCycleCount = 102,
                ExcludedCompletedCycleCount = 103,
                ExceptionCount = 104
            },
            Window = new ReportWindowSection
            {
                RangeStartDate = "start-sentinel",
                RangeEndDate = "end-sentinel",
                RangeLabel = "label-sentinel",
                TotalCompletedCycleCount = 105
            },
            Timing = new ReportTimingSection
            {
                AverageSeatedToDoctorSeconds = 201,
                MedianSeatedToDoctorSeconds = 202,
                AveragePrepSeconds = 203,
                MedianPrepSeconds = 204,
                AverageReadyToDoctorSeconds = 205,
                MedianReadyToDoctorSeconds = 206,
                AverageDoctorInRoomSeconds = 207,
                MedianDoctorInRoomSeconds = 208,
                AverageTurnoverSeconds = 209,
                MedianTurnoverSeconds = 210,
                AgingEventCount = 106,
                StaleEventCount = 107,
                AverageDoctorOccupiedWaitSeconds = 211,
                MedianDoctorOccupiedWaitSeconds = 212,
                AverageDoctorAvailableWaitSeconds = 213,
                MedianDoctorAvailableWaitSeconds = 214,
                Trends = trends
            },
            Procedures = new ReportProcedureSection
            {
                ProcedureSummaries = procedureSummaries,
                SedationCaseCount = 108,
                NonSedationCaseCount = 109,
                BaseProcedureSummaries = baseProcedureSummaries
            },
            Allocation = new ReportAllocationSection
            {
                AllocationVariance = allocationVariance,
                DoctorDailyAllocationSeries = doctorDailyAllocationSeries,
                ScheduleFit = scheduleFit
            },
            DoctorDetail = new ReportDoctorDetailSection
            {
                DoctorSummaries = doctorSummaries,
                ObservedDoctorDays = observedDoctorDays,
                DoctorProcedureMix = doctorProcedureMix
            },
            ReviewQueue = new ReportReviewQueueSection
            {
                ExceptionCycles = exceptionCycles,
                ExceptionReviewRecords = exceptionReviewRecords
            }
        };
        var snapshot = ReportsSnapshotAdapter.ToSnapshot(composition);

        Assert.Equal(
            composition.Population.CompletedRoomCyclesCount,
            snapshot.CompletedRoomCyclesCount);
        Assert.Equal(
            composition.Timing.AverageSeatedToDoctorSeconds,
            snapshot.AverageSeatedToDoctorSeconds);
        Assert.Equal(
            composition.Timing.MedianSeatedToDoctorSeconds,
            snapshot.MedianSeatedToDoctorSeconds);
        Assert.Equal(composition.Timing.AveragePrepSeconds, snapshot.AveragePrepSeconds);
        Assert.Equal(composition.Timing.MedianPrepSeconds, snapshot.MedianPrepSeconds);
        Assert.Equal(
            composition.Timing.AverageReadyToDoctorSeconds,
            snapshot.AverageReadyToDoctorSeconds);
        Assert.Equal(
            composition.Timing.MedianReadyToDoctorSeconds,
            snapshot.MedianReadyToDoctorSeconds);
        Assert.Equal(
            composition.Timing.AverageDoctorInRoomSeconds,
            snapshot.AverageDoctorInRoomSeconds);
        Assert.Equal(
            composition.Timing.MedianDoctorInRoomSeconds,
            snapshot.MedianDoctorInRoomSeconds);
        Assert.Equal(composition.Timing.AverageTurnoverSeconds, snapshot.AverageTurnoverSeconds);
        Assert.Equal(composition.Timing.MedianTurnoverSeconds, snapshot.MedianTurnoverSeconds);
        Assert.Equal(composition.Timing.AgingEventCount, snapshot.AgingEventCount);
        Assert.Equal(composition.Timing.StaleEventCount, snapshot.StaleEventCount);
        Assert.Equal(
            composition.Timing.AverageDoctorOccupiedWaitSeconds,
            snapshot.AverageDoctorOccupiedWaitSeconds);
        Assert.Equal(
            composition.Timing.MedianDoctorOccupiedWaitSeconds,
            snapshot.MedianDoctorOccupiedWaitSeconds);
        Assert.Equal(
            composition.Timing.AverageDoctorAvailableWaitSeconds,
            snapshot.AverageDoctorAvailableWaitSeconds);
        Assert.Equal(
            composition.Timing.MedianDoctorAvailableWaitSeconds,
            snapshot.MedianDoctorAvailableWaitSeconds);
        Assert.Same(composition.DoctorDetail.DoctorSummaries, snapshot.DoctorSummaries);
        Assert.Same(composition.Population.RecentCompletedCycles, snapshot.RecentCompletedCycles);
        Assert.Same(composition.ReviewQueue.ExceptionCycles, snapshot.ExceptionCycles);
        Assert.Same(composition.Procedures.ProcedureSummaries, snapshot.ProcedureSummaries);
        Assert.Equal(composition.Procedures.SedationCaseCount, snapshot.SedationCaseCount);
        Assert.Equal(composition.Procedures.NonSedationCaseCount, snapshot.NonSedationCaseCount);
        Assert.Same(composition.Procedures.BaseProcedureSummaries, snapshot.BaseProcedureSummaries);
        Assert.Equal(
            composition.Population.IncludedCompletedCycleCount,
            snapshot.IncludedCompletedCycleCount);
        Assert.Equal(
            composition.Population.ExcludedCompletedCycleCount,
            snapshot.ExcludedCompletedCycleCount);
        Assert.Equal(composition.Population.ExceptionCount, snapshot.ExceptionCount);
        Assert.Same(composition.Allocation.AllocationVariance, snapshot.AllocationVariance);
        Assert.Equal(composition.Window.RangeStartDate, snapshot.RangeStartDate);
        Assert.Equal(composition.Window.RangeEndDate, snapshot.RangeEndDate);
        Assert.Equal(composition.Window.RangeLabel, snapshot.RangeLabel);
        Assert.Equal(
            composition.Window.TotalCompletedCycleCount,
            snapshot.TotalCompletedCycleCount);
        Assert.Same(
            composition.Allocation.DoctorDailyAllocationSeries,
            snapshot.DoctorDailyAllocationSeries);
        Assert.Same(composition.Allocation.ScheduleFit, snapshot.ScheduleFit);
        Assert.Same(composition.Timing.Trends, snapshot.Trends);
        Assert.Same(composition.DoctorDetail.ObservedDoctorDays, snapshot.ObservedDoctorDays);
        Assert.Same(composition.DoctorDetail.DoctorProcedureMix, snapshot.DoctorProcedureMix);
        Assert.Same(
            composition.ReviewQueue.ExceptionReviewRecords,
            snapshot.ExceptionReviewRecords);
    }

    [Fact]
    public void Build_applies_completion_window_before_every_population_and_keeps_all_time_total()
    {
        var inRange = Cycle(
            id: 1,
            roomId: 1,
            procedureCode: "EXT",
            seatedAt: Utc(2026, 7, 20, 8, 0),
            readyAt: Utc(2026, 7, 20, 8, 5),
            arrivedAt: Utc(2026, 7, 20, 8, 10),
            completeAt: Utc(2026, 7, 20, 8, 30),
            availableAt: Utc(2026, 7, 20, 8, 40),
            expectedAllocationMinutes: 30);
        var outOfRange = Cycle(
            id: 2,
            roomId: 2,
            procedureCode: "EXT+SED",
            seatedAt: Utc(2026, 7, 19, 8, 0),
            readyAt: Utc(2026, 7, 19, 8, 5),
            arrivedAt: Utc(2026, 7, 19, 8, 10),
            completeAt: Utc(2026, 7, 19, 8, 30),
            availableAt: Utc(2026, 7, 19, 8, 40),
            expectedAllocationMinutes: 30);
        // A bounded report must filter this cycle before the defensive copy reads derived fields.
        // Persisted source data initializes this collection, but a malformed historical object
        // makes the evaluation boundary observable and guards against all-time pre-annotation.
        outOfRange.ReportingExceptionReasons = null!;
        var nullAnchor = Cycle(
            id: 3,
            roomId: 3,
            procedureCode: "CON",
            seatedAt: Utc(2026, 7, 20, 9, 0),
            readyAt: Utc(2026, 7, 20, 9, 5),
            arrivedAt: Utc(2026, 7, 20, 9, 10),
            completeAt: null,
            availableAt: null,
            expectedAllocationMinutes: 30);

        var snapshot = CreateBuilder().Build(
            [inRange, outOfRange, nullAnchor],
            [],
            ReportDateRange.FromDates(new DateOnly(2026, 7, 20), new DateOnly(2026, 7, 20)));

        Assert.Equal(1, snapshot.CompletedRoomCyclesCount);
        Assert.Equal(1, snapshot.IncludedCompletedCycleCount);
        Assert.Equal(2, snapshot.TotalCompletedCycleCount);
        Assert.Equal("2026-07-20", snapshot.RangeStartDate);
        Assert.Equal("2026-07-20", snapshot.RangeEndDate);
        Assert.Equal("Jul 20 - Jul 20", snapshot.RangeLabel.Replace('\u2013', '-'));
        Assert.Single(snapshot.RecentCompletedCycles);
        Assert.Single(snapshot.ProcedureSummaries);
        Assert.Single(snapshot.DoctorSummaries);
        Assert.Single(snapshot.DoctorDailyAllocationSeries!);
        Assert.Single(snapshot.Trends!.Buckets);
        Assert.Single(snapshot.ObservedDoctorDays!);
        Assert.Single(snapshot.DoctorProcedureMix!);
        Assert.Null(outOfRange.ReportingExceptionReasons);
    }

    [Fact]
    public void Reports_snapshot_web_json_property_contract_remains_unchanged()
    {
        var snapshot = CreateBuilder().Build([], [], ReportDateRange.AllTime);

        var json = JsonSerializer.SerializeToElement(
            snapshot,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var actualNames = json.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.Equal(
        [
            "completedRoomCyclesCount",
            "averageSeatedToDoctorSeconds",
            "medianSeatedToDoctorSeconds",
            "averagePrepSeconds",
            "medianPrepSeconds",
            "averageReadyToDoctorSeconds",
            "medianReadyToDoctorSeconds",
            "averageDoctorInRoomSeconds",
            "medianDoctorInRoomSeconds",
            "averageTurnoverSeconds",
            "medianTurnoverSeconds",
            "agingEventCount",
            "staleEventCount",
            "averageDoctorOccupiedWaitSeconds",
            "medianDoctorOccupiedWaitSeconds",
            "averageDoctorAvailableWaitSeconds",
            "medianDoctorAvailableWaitSeconds",
            "doctorSummaries",
            "recentCompletedCycles",
            "exceptionCycles",
            "procedureSummaries",
            "sedationCaseCount",
            "nonSedationCaseCount",
            "baseProcedureSummaries",
            "includedCompletedCycleCount",
            "excludedCompletedCycleCount",
            "exceptionCount",
            "allocationVariance",
            "rangeStartDate",
            "rangeEndDate",
            "rangeLabel",
            "totalCompletedCycleCount",
            "doctorDailyAllocationSeries",
            "scheduleFit",
            "trends",
            "observedDoctorDays",
            "doctorProcedureMix",
            "exceptionReviewRecords"
        ],
            actualNames);

        Assert.Equal(JsonValueKind.Null, json.GetProperty("rangeStartDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("rangeEndDate").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("doctorDailyAllocationSeries").ValueKind);
        Assert.Equal(JsonValueKind.Object, json.GetProperty("scheduleFit").ValueKind);
        Assert.Equal(JsonValueKind.Object, json.GetProperty("trends").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("observedDoctorDays").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("doctorProcedureMix").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("exceptionReviewRecords").ValueKind);
    }

    private static ReportsSnapshotBuilder CreateBuilder()
    {
        Doctor[] doctors =
        [
            new("otte", "Dr. Otte", "LDO", "#dc2626")
        ];
        ProcedureCategory[] procedures =
        [
            new("extraction", "EXT", "Extraction", "forceps", SedationEligible: true),
            new("consult", "CON", "Consult", "message-circle"),
            new("sedation", "SED", "Sedation", "moon", SedationEligible: true)
        ];
        ProcedureCategory[] activeProcedures = [procedures[0], procedures[1]];
        return new ReportsSnapshotBuilder(doctors, procedures, activeProcedures);
    }

    private static CompletedRoomCycle Cycle(
        long id,
        int roomId,
        string procedureCode,
        DateTimeOffset seatedAt,
        DateTimeOffset readyAt,
        DateTimeOffset arrivedAt,
        DateTimeOffset? completeAt,
        DateTimeOffset? availableAt,
        int expectedAllocationMinutes)
    {
        var seatedToDoctorSeconds = SecondsBetween(seatedAt, arrivedAt);
        var prepSeconds = SecondsBetween(seatedAt, readyAt);
        var readyToDoctorSeconds = SecondsBetween(readyAt, arrivedAt);
        int? doctorInRoomSeconds = completeAt.HasValue ? SecondsBetween(arrivedAt, completeAt.Value) : null;
        int? turnoverSeconds = completeAt.HasValue && availableAt.HasValue
            ? SecondsBetween(completeAt.Value, availableAt.Value)
            : null;
        int? totalRoomCycleSeconds = availableAt.HasValue ? SecondsBetween(seatedAt, availableAt.Value) : null;

        return new CompletedRoomCycle
        {
            CompletedCycleId = id,
            EpisodeId = $"episode-{id}",
            RoomId = roomId,
            AssignedDoctor = "otte",
            ProcedureCode = procedureCode,
            SeatedAt = seatedAt,
            ReadyForDoctorAt = readyAt,
            DoctorArrivedAt = arrivedAt,
            DoctorCompleteAt = completeAt,
            RoomAvailableAt = availableAt,
            SeatedToDoctorSeconds = seatedToDoctorSeconds,
            PrepSeconds = prepSeconds,
            ReadyToDoctorSeconds = readyToDoctorSeconds,
            DoctorInRoomSeconds = doctorInRoomSeconds,
            TurnoverSeconds = turnoverSeconds,
            TotalRoomCycleSeconds = totalRoomCycleSeconds,
            OriginalDefaultExpectedUnits = expectedAllocationMinutes / 10,
            ExpectedAllocationUnits = expectedAllocationMinutes / 10,
            ExpectedAllocationMinutes = expectedAllocationMinutes,
            FinalWaitState = RoomStates.ReadyForDoctor
        };
    }

    private static int SecondsBetween(DateTimeOffset start, DateTimeOffset end) =>
        Math.Max(0, (int)Math.Round((end - start).TotalSeconds));

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
