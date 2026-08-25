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
        manualException.ReportingProjection = Projection(
            HistoricalAdministrativeDispositions.NeedsReview,
            doctor: "otte",
            procedure: "CON",
            sedation: SedationState.UnavailableProcedureIneligible);

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
            TerminationKind = ExceptionReasons.AfterHoursSweep
        };
        abortedException.ReportingProjection = Projection(
            HistoricalAdministrativeDispositions.NeedsReview,
            doctor: "otte",
            procedure: "EXT",
            sedation: SedationState.EligibleNo);

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
        Assert.Equal(3, composition.Window.TotalCompletedCycleCount);

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
        var doctorAllocationSample = Assert.Single(composition.DoctorDetail.DoctorAllocationSamples);
        Assert.Equal(3, doctorAllocationSample.Sample.PopulationCount);
        Assert.Equal(2, doctorAllocationSample.Sample.ContributingCount);
        Assert.Equal(ReportSampleStates.Limited, doctorAllocationSample.Sample.State);
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
        var observedDoctorFlowDays = new List<ObservedDoctorFlowDay>();
        var doctorFlowSummaries = new List<DoctorFlowSummary>();
        var doctorFlowTrends = new List<DoctorFlowTrendSeries>();
        var doctorProcedureMix = new List<DoctorProcedureMixRow>();
        var doctorAllocationSamples = new List<ReportDoctorAllocationSampleContext>();
        var exceptionReviewRecords = new List<ExceptionReviewRecord>();
        var query = ReportQuery.Default.ToContext();
        var samples = new ReportMetricSampleContext(
            ReportSampleContext.ForPopulation(1),
            ReportSampleContext.ForPopulation(2),
            ReportSampleContext.ForPopulation(3),
            ReportSampleContext.ForPopulation(4),
            ReportSampleContext.ForPopulation(5),
            ReportSampleContext.ForPopulation(6),
            ReportSampleContext.ForPopulation(7),
            ReportSampleContext.ForPopulation(8),
            ReportSampleContext.ForPopulation(9),
            ReportSampleContext.ForPopulation(10));
        var scopedProcedureGroups = new List<ScopedProcedureGroup>();
        var procedureIntelligenceRows = new List<ProcedureIntelligenceRow>();

        var composition = new ReportsSnapshotComposition
        {
            Query = query,
            Samples = samples,
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
                BaseProcedureSummaries = baseProcedureSummaries,
                ScopedProcedureGroups = scopedProcedureGroups,
                ProcedureIntelligenceRows = procedureIntelligenceRows
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
                DoctorAllocationSamples = doctorAllocationSamples,
                DoctorFlowSummaries = doctorFlowSummaries,
                DoctorFlowTrends = doctorFlowTrends,
                ObservedDoctorDays = observedDoctorDays,
                ObservedDoctorFlowDays = observedDoctorFlowDays,
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
        Assert.Same(
            composition.DoctorDetail.ObservedDoctorFlowDays,
            snapshot.ObservedDoctorFlowDays);
        Assert.Same(
            composition.DoctorDetail.DoctorFlowSummaries,
            snapshot.DoctorFlowSummaries);
        Assert.Same(
            composition.DoctorDetail.DoctorFlowTrends,
            snapshot.DoctorFlowTrends);
        Assert.Same(composition.DoctorDetail.DoctorProcedureMix, snapshot.DoctorProcedureMix);
        Assert.Same(
            composition.ReviewQueue.ExceptionReviewRecords,
            snapshot.ExceptionReviewRecords);
        Assert.Same(composition.Query, snapshot.Query);
        Assert.Same(composition.Samples, snapshot.Samples);
        Assert.Same(composition.Procedures.ScopedProcedureGroups, snapshot.ScopedProcedureGroups);
        Assert.Same(
            composition.Procedures.ProcedureIntelligenceRows,
            snapshot.ProcedureIntelligenceRows);
        Assert.Same(
            composition.DoctorDetail.DoctorAllocationSamples,
            snapshot.DoctorAllocationSamples);
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
    public void Reports_snapshot_web_json_property_contract_appends_data_quality()
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
            "exceptionReviewRecords",
            "query",
            "samples",
            "scopedProcedureGroups",
            "doctorAllocationSamples",
            "observedDoctorFlowDays",
            "doctorFlowSummaries",
            "doctorFlowTrends",
            "procedureIntelligenceRows",
            "dataQuality"
        ],
            actualNames);

        Assert.Equal(JsonValueKind.Null, json.GetProperty("rangeStartDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("rangeEndDate").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("doctorDailyAllocationSeries").ValueKind);
        Assert.Equal(JsonValueKind.Object, json.GetProperty("scheduleFit").ValueKind);
        Assert.Equal(JsonValueKind.Object, json.GetProperty("trends").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("observedDoctorDays").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("doctorProcedureMix").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("procedureIntelligenceRows").ValueKind);
        Assert.Equal(JsonValueKind.Object, json.GetProperty("dataQuality").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("exceptionReviewRecords").ValueKind);
        Assert.Equal(JsonValueKind.Object, json.GetProperty("query").ValueKind);
        Assert.Equal(JsonValueKind.Object, json.GetProperty("samples").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("scopedProcedureGroups").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("doctorAllocationSamples").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("observedDoctorFlowDays").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("doctorFlowSummaries").ValueKind);
    }

    [Fact]
    public void Build_applies_historical_doctor_and_sedation_scope_without_treating_grouping_as_a_filter()
    {
        var formerSedation = Cycle(
            1, 1, "EXT+SED",
            Utc(2026, 8, 10, 8, 0), Utc(2026, 8, 10, 8, 5), Utc(2026, 8, 10, 8, 10),
            Utc(2026, 8, 10, 8, 30), Utc(2026, 8, 10, 8, 40), 30);
        formerSedation.AssignedDoctor = "former-doctor";
        var formerNonSedation = Cycle(
            2, 2, "EXT",
            Utc(2026, 8, 10, 9, 0), Utc(2026, 8, 10, 9, 5), Utc(2026, 8, 10, 9, 10),
            Utc(2026, 8, 10, 9, 30), Utc(2026, 8, 10, 9, 40), 30);
        formerNonSedation.AssignedDoctor = "former-doctor";
        var rosteredSedation = Cycle(
            3, 3, "CON+SED",
            Utc(2026, 8, 10, 10, 0), Utc(2026, 8, 10, 10, 5), Utc(2026, 8, 10, 10, 10),
            Utc(2026, 8, 10, 10, 30), Utc(2026, 8, 10, 10, 40), 30);

        var query = ReportQuery.FromStrings(
            null,
            null,
            ReportScopeKinds.Doctor,
            "former-doctor",
            ReportSedationSegments.Sedation,
            ReportProcedureGroupings.DetailedVariant);
        var snapshot = CreateBuilder().Build(
            [formerSedation, formerNonSedation, rosteredSedation],
            [],
            query);

        Assert.Equal(1, snapshot.IncludedCompletedCycleCount);
        Assert.Equal("former-doctor", snapshot.Query!.DoctorId);
        var group = Assert.Single(snapshot.ScopedProcedureGroups!);
        Assert.Equal("EXT+SED", group.ProcedureCode);
        Assert.True(group.IsSedationCase);
        Assert.Equal(1, group.CaseCount);
        Assert.Equal(snapshot.IncludedCompletedCycleCount, group.ScopedPopulationCount);
        Assert.Equal(1d, group.ShareOfScopedCases);
        Assert.Equal(ReportSampleStates.Limited, snapshot.Samples!.IncludedCompletedCases.State);
        var doctorSample = Assert.Single(snapshot.DoctorAllocationSamples!);
        Assert.Equal("former-doctor", doctorSample.DoctorId);
        Assert.Equal(ReportSampleStates.Limited, doctorSample.Sample.State);
        var trend = Assert.Single(snapshot.Trends!.Buckets);
        Assert.Equal(1, trend.ReadyWaitSample!.PopulationCount);
        Assert.Equal(1, trend.ReadyWaitSample.ContributingCount);
        Assert.Equal(ReportSampleStates.Limited, trend.ReadyWaitSample.State);
        var flowSummary = Assert.Single(snapshot.DoctorFlowSummaries!);
        Assert.Equal("former-doctor", flowSummary.DoctorId);
        Assert.Equal(1, flowSummary.CompletedCaseCount);
        Assert.Equal(1, flowSummary.ObservedDoctorDayCount);
        Assert.Single(snapshot.ObservedDoctorFlowDays!);
    }

    [Fact]
    public void Build_scoped_procedure_groups_reconcile_family_and_detailed_variant_populations()
    {
        CompletedRoomCycle[] cycles =
        [
            CompletedCycle(1, "EXT"),
            CompletedCycle(2, "EXT"),
            CompletedCycle(3, "EXT+SED"),
            CompletedCycle(4, "CON"),
            CompletedCycle(5, "BX")
        ];

        var family = CreateBuilder().Build(
            cycles,
            [],
            ReportQuery.FromStrings(null, null, null, null, null, ReportProcedureGroupings.Family));
        var detailed = CreateBuilder().Build(
            cycles,
            [],
            ReportQuery.FromStrings(null, null, null, null, null, ReportProcedureGroupings.DetailedVariant));

        Assert.Equal(5, family.IncludedCompletedCycleCount);
        Assert.Equal(ReportSampleStates.Sufficient, family.Samples!.IncludedCompletedCases.State);
        Assert.Collection(
            family.ScopedProcedureGroups!,
            row => AssertProcedureGroup(row, "EXT", "Extraction", 3, 5, 0.6d, null),
            row => AssertProcedureGroup(row, "BX", "Biopsy", 1, 5, 0.2d, null),
            row => AssertProcedureGroup(row, "CON", "Consult", 1, 5, 0.2d, null));
        Assert.Equal(
            family.IncludedCompletedCycleCount,
            family.ScopedProcedureGroups!.Sum(row => row.CaseCount));
        Assert.Equal(1d, family.ScopedProcedureGroups!.Sum(row => row.ShareOfScopedCases), precision: 10);

        Assert.Equal(family.IncludedCompletedCycleCount, detailed.IncludedCompletedCycleCount);
        Assert.Collection(
            detailed.ScopedProcedureGroups!,
            row => AssertProcedureGroup(row, "EXT", "Extraction", 2, 5, 0.4d, false),
            row => AssertProcedureGroup(row, "BX", "Biopsy", 1, 5, 0.2d, false),
            row => AssertProcedureGroup(row, "CON", "Consult", 1, 5, 0.2d, false),
            row => AssertProcedureGroup(row, "EXT+SED", "Extraction + Sedation", 1, 5, 0.2d, true));
        Assert.Equal(
            detailed.IncludedCompletedCycleCount,
            detailed.ScopedProcedureGroups!.Sum(row => row.CaseCount));
        Assert.Equal(1d, detailed.ScopedProcedureGroups!.Sum(row => row.ShareOfScopedCases), precision: 10);
    }

    [Fact]
    public void Build_scoped_procedure_groups_recompute_sedation_populations_and_shares()
    {
        CompletedRoomCycle[] cycles =
        [
            CompletedCycle(1, "EXT"),
            CompletedCycle(2, "EXT+SED"),
            CompletedCycle(3, "CON")
        ];

        var all = BuildScoped(cycles, ReportSedationSegments.All);
        var sedation = BuildScoped(cycles, ReportSedationSegments.Sedation);
        var nonSedation = BuildScoped(cycles, ReportSedationSegments.NonSedation);

        Assert.Equal(3, all.IncludedCompletedCycleCount);
        Assert.Equal(3, all.ScopedProcedureGroups!.Sum(row => row.CaseCount));
        Assert.Equal(1d, all.ScopedProcedureGroups!.Sum(row => row.ShareOfScopedCases), precision: 10);

        var sedationRow = Assert.Single(sedation.ScopedProcedureGroups!);
        Assert.Equal(1, sedation.IncludedCompletedCycleCount);
        AssertProcedureGroup(sedationRow, "EXT+SED", "Extraction + Sedation", 1, 1, 1d, true);

        Assert.Equal(2, nonSedation.IncludedCompletedCycleCount);
        Assert.Collection(
            nonSedation.ScopedProcedureGroups!,
            row => AssertProcedureGroup(row, "CON", "Consult", 1, 2, 0.5d, false),
            row => AssertProcedureGroup(row, "EXT", "Extraction", 1, 2, 0.5d, false));
    }

    [Fact]
    public void Build_excludes_blank_unmapped_and_manual_review_cycles_from_procedure_mix_but_keeps_audit_history()
    {
        var included = CompletedCycle(1, "EXT");
        var blank = CompletedCycle(2, "   ");
        var unmapped = CompletedCycle(3, "MYSTERY");
        var manualReview = CompletedCycle(4, "CON");
        manualReview.IsException = true;
        manualReview.RequiresReview = true;
        manualReview.ExceptionReason = ExceptionReasons.ManualReview;

        var snapshot = CreateBuilder().Build(
            [included, blank, unmapped, manualReview],
            [],
            ReportQuery.Default);

        Assert.Equal(3, snapshot.CompletedRoomCyclesCount);
        Assert.Equal(1, snapshot.IncludedCompletedCycleCount);
        Assert.Equal(2, snapshot.ExcludedCompletedCycleCount);
        var group = Assert.Single(snapshot.ScopedProcedureGroups!);
        Assert.Equal("EXT", group.ProcedureCode);
        Assert.Equal(1, group.CaseCount);
        var intelligence = Assert.Single(snapshot.ProcedureIntelligenceRows!);
        Assert.Equal("EXT", intelligence.ProcedureCode);
        Assert.Equal(1, intelligence.Metrics.CompletedCaseCount);

        var blankAudit = Assert.Single(snapshot.RecentCompletedCycles, cycle => cycle.CompletedCycleId == blank.CompletedCycleId);
        Assert.True(blankAudit.IsUnmappedProcedure);
        Assert.True(blankAudit.IsExcludedFromStandardMetrics);
        Assert.Contains(ReportingExceptionReasons.UnmappedProcedure, blankAudit.ReportingExceptionReasons);
        Assert.Equal("Unknown (Unmapped)", blankAudit.DisplayProcedureLabel);

        var unmappedAudit = Assert.Single(snapshot.RecentCompletedCycles, cycle => cycle.CompletedCycleId == unmapped.CompletedCycleId);
        Assert.True(unmappedAudit.IsUnmappedProcedure);
        Assert.DoesNotContain(snapshot.ScopedProcedureGroups!, row => row.ProcedureLabel == "Unknown");
        Assert.Single(snapshot.ExceptionCycles);
    }

    [Fact]
    public void Build_procedure_mix_samples_keep_empty_and_limited_populations_truthful()
    {
        var empty = CreateBuilder().Build([], [], ReportQuery.Default);
        var limited = CreateBuilder().Build([CompletedCycle(1, "EXT")], [], ReportQuery.Default);

        Assert.Equal(0, empty.IncludedCompletedCycleCount);
        Assert.Equal(ReportSampleStates.Empty, empty.Samples!.IncludedCompletedCases.State);
        Assert.Empty(empty.ScopedProcedureGroups!);

        Assert.Equal(1, limited.IncludedCompletedCycleCount);
        Assert.Equal(ReportSampleStates.Limited, limited.Samples!.IncludedCompletedCases.State);
        var row = Assert.Single(limited.ScopedProcedureGroups!);
        Assert.Equal(1, row.CaseCount);
        Assert.Equal(ReportSampleStates.Limited, row.Sample.State);
        Assert.Equal(1d, row.ShareOfScopedCases);
    }

    [Fact]
    public void Scoped_procedure_group_web_json_contract_uses_existing_camel_case_fields()
    {
        var snapshot = CreateBuilder().Build([CompletedCycle(1, "EXT+SED")], [], ReportQuery.Default);
        var json = JsonSerializer.SerializeToElement(
            snapshot,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var group = json.GetProperty("scopedProcedureGroups")[0];

        Assert.Equal(
            [
                "procedureCode",
                "procedureLabel",
                "baseProcedureCode",
                "isSedationCase",
                "caseCount",
                "scopedPopulationCount",
                "shareOfScopedCases",
                "sample"
            ],
            group.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(
            [
                "populationCount",
                "contributingCount",
                "state",
                "limitedSampleThreshold",
                "supportsComparison"
            ],
            group.GetProperty("sample").EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void Build_keeps_broader_completed_count_while_timing_uses_truthful_ready_contributors()
    {
        var truthfulZero = Cycle(
            1, 1, "EXT",
            Utc(2026, 8, 10, 8, 0), Utc(2026, 8, 10, 8, 5), Utc(2026, 8, 10, 8, 5),
            Utc(2026, 8, 10, 8, 30), Utc(2026, 8, 10, 8, 40), 30);
        var legacyMissingReady = Cycle(
            2, 2, "CON",
            Utc(2026, 8, 10, 9, 0), Utc(2026, 8, 10, 9, 5), Utc(2026, 8, 10, 9, 10),
            Utc(2026, 8, 10, 9, 30), Utc(2026, 8, 10, 9, 40), 30);
        legacyMissingReady.ReadyForDoctorAt = null;
        legacyMissingReady.ReadyToDoctorSeconds = null;
        var reportingExcluded = Cycle(
            3, 3, "SED",
            Utc(2026, 8, 10, 10, 0), Utc(2026, 8, 10, 10, 5), Utc(2026, 8, 10, 10, 10),
            Utc(2026, 8, 10, 10, 30), Utc(2026, 8, 10, 10, 40), 30);

        var snapshot = CreateBuilder().Build(
            [truthfulZero, legacyMissingReady, reportingExcluded],
            [],
            ReportQuery.Default);

        Assert.Equal(3, snapshot.CompletedRoomCyclesCount);
        Assert.Equal(2, snapshot.IncludedCompletedCycleCount);
        Assert.Equal(1, snapshot.ExcludedCompletedCycleCount);
        Assert.Equal(0, snapshot.MedianReadyToDoctorSeconds);
        Assert.Equal(2, snapshot.Samples!.ReadyWait.PopulationCount);
        Assert.Equal(1, snapshot.Samples.ReadyWait.ContributingCount);
        Assert.Equal(ReportSampleStates.Limited, snapshot.Samples.ReadyWait.State);
        var trend = Assert.Single(snapshot.Trends!.Buckets);
        Assert.Equal(2, trend.ReadyWaitSample!.PopulationCount);
        Assert.Equal(1, trend.ReadyWaitSample.ContributingCount);
        Assert.Equal(0, trend.MedianReadyWaitSeconds);
    }

    [Fact]
    public void Build_marks_weekly_ready_wait_unavailable_when_completed_population_has_no_ready_observation()
    {
        var legacyMissingReady = Cycle(
            1, 1, "EXT",
            Utc(2026, 8, 10, 8, 0), Utc(2026, 8, 10, 8, 5), Utc(2026, 8, 10, 8, 10),
            Utc(2026, 8, 10, 8, 30), Utc(2026, 8, 10, 8, 40), 30);
        legacyMissingReady.ReadyForDoctorAt = null;
        legacyMissingReady.ReadyToDoctorSeconds = null;

        var snapshot = CreateBuilder().Build([legacyMissingReady], [], ReportQuery.Default);

        Assert.Equal(ReportSampleStates.Unavailable, snapshot.Samples!.ReadyWait.State);
        Assert.Equal(0, snapshot.MedianReadyToDoctorSeconds);
        var trend = Assert.Single(snapshot.Trends!.Buckets);
        Assert.Null(trend.MedianReadyWaitSeconds);
        Assert.Equal(ReportSampleStates.Unavailable, trend.ReadyWaitSample!.State);
    }

    [Fact]
    public void Build_preserves_unavailable_doctor_allocation_when_population_has_no_contributors()
    {
        var incomplete = Cycle(
            1, 1, "EXT",
            Utc(2026, 8, 10, 8, 0), Utc(2026, 8, 10, 8, 5), Utc(2026, 8, 10, 8, 10),
            null, null, 30);
        var query = ReportQuery.FromStrings(
            null,
            null,
            ReportScopeKinds.Doctor,
            "otte",
            ReportSedationSegments.All,
            ReportProcedureGroupings.Family);

        var snapshot = CreateBuilder().Build([incomplete], [], query);

        var doctorSample = Assert.Single(snapshot.DoctorAllocationSamples!);
        Assert.Equal(1, doctorSample.Sample.PopulationCount);
        Assert.Equal(0, doctorSample.Sample.ContributingCount);
        Assert.Equal(ReportSampleStates.Unavailable, doctorSample.Sample.State);
        Assert.False(doctorSample.Sample.SupportsComparison);
    }

    [Fact]
    public void Build_mixed_canonical_and_legacy_day_keeps_completed_and_phase_metrics_separate()
    {
        var canonical = Cycle(
            1, 1, "EXT",
            Utc(2026, 8, 10, 8, 0), Utc(2026, 8, 10, 8, 5), Utc(2026, 8, 10, 8, 10),
            Utc(2026, 8, 10, 8, 30), Utc(2026, 8, 10, 8, 40), 30);
        var legacy = Cycle(
            2, 2, "CON",
            Utc(2026, 8, 10, 8, 20), Utc(2026, 8, 10, 8, 25), Utc(2026, 8, 10, 8, 40),
            Utc(2026, 8, 10, 10, 0), Utc(2026, 8, 10, 10, 10), 30);
        legacy.ReadyForDoctorAt = null;
        legacy.PrepSeconds = null;
        legacy.ReadyToDoctorSeconds = null;

        var snapshot = CreateBuilder().Build([canonical, legacy], [], ReportQuery.Default);

        var summary = Assert.Single(snapshot.DoctorFlowSummaries!);
        Assert.Equal(2, summary.CompletedCaseCount);
        Assert.Equal(300, summary.MedianReadyWaitSeconds);
        Assert.Equal(3_000, summary.MedianDoctorTimeSeconds);
        Assert.Equal(2, summary.Samples.CompletedCases.ContributingCount);
        Assert.Equal(1, summary.Samples.ReadyWait.ContributingCount);
        Assert.Equal(2, summary.Samples.DoctorTime.ContributingCount);
        Assert.Equal(1, summary.ObservedDoctorDayCount);

        var day = Assert.Single(snapshot.ObservedDoctorFlowDays!);
        Assert.Equal(1, day.QualifyingCaseCount);
        Assert.Equal(canonical.ReadyForDoctorAt, day.FirstAcceptedReadyAt);
        Assert.Equal(canonical.DoctorCompleteAt, day.LastDoctorCompleteAt);
        Assert.Equal(25, day.ObservedClinicalSpanMinutes);
        Assert.Equal(5, day.UnstructuredTimeMinutes);
        Assert.Equal(20, day.MinutesWithOneDoctorWorkingRoom);
        Assert.Equal(0, day.MinutesWithTwoDoctorWorkingRooms);
        Assert.Equal(0, day.MinutesWithThreeOrMoreDoctorWorkingRooms);
        Assert.Equal(1, day.PeakConcurrentRooms);
        Assert.Null(legacy.ReadyForDoctorAt);

        var compatibilityDay = Assert.Single(snapshot.ObservedDoctorDays!);
        Assert.Equal(2, compatibilityDay.EncounterCount);
        Assert.Equal(legacy.DoctorCompleteAt, compatibilityDay.LastDoctorCompleteAt);
    }

    [Fact]
    public void Build_doctor_phase_timing_does_not_require_completed_throughput()
    {
        var readyOnly = Cycle(
            1, 1, "EXT",
            Utc(2026, 8, 10, 8, 0), Utc(2026, 8, 10, 8, 5), Utc(2026, 8, 10, 8, 12),
            null, null, 30);
        var doctorComplete = Cycle(
            2, 2, "CON",
            Utc(2026, 8, 10, 9, 0), Utc(2026, 8, 10, 9, 5), Utc(2026, 8, 10, 9, 10),
            Utc(2026, 8, 10, 9, 30), null, 30);

        var snapshot = CreateBuilder().Build([readyOnly, doctorComplete], [], ReportQuery.Default);
        var summary = Assert.Single(snapshot.DoctorFlowSummaries!);

        Assert.Equal(0, summary.CompletedCaseCount);
        Assert.Equal(360, summary.MedianReadyWaitSeconds);
        Assert.Equal(1_200, summary.MedianDoctorTimeSeconds);
        Assert.Equal(2, summary.Samples.ReadyWait.PopulationCount);
        Assert.Equal(2, summary.Samples.ReadyWait.ContributingCount);
        Assert.Equal(2, summary.Samples.DoctorTime.PopulationCount);
        Assert.Equal(1, summary.Samples.DoctorTime.ContributingCount);
        Assert.Empty(snapshot.ObservedDoctorFlowDays!);
    }

    [Fact]
    public void Build_canonical_room_load_partitions_single_two_and_three_plus_working_rooms()
    {
        var start = Utc(2026, 8, 10, 8, 0);
        CompletedRoomCycle[] cycles =
        [
            Cycle(1, 1, "EXT", start, start, start.AddMinutes(10), start.AddMinutes(50), start.AddMinutes(55), 30),
            Cycle(2, 2, "CON", start.AddMinutes(5), start.AddMinutes(5), start.AddMinutes(20), start.AddMinutes(40), start.AddMinutes(45), 30),
            Cycle(3, 3, "BX", start.AddMinutes(15), start.AddMinutes(15), start.AddMinutes(25), start.AddMinutes(35), start.AddMinutes(40), 30)
        ];

        var day = Assert.Single(CreateBuilder().Build(cycles, [], ReportQuery.Default).ObservedDoctorFlowDays!);

        Assert.Equal(50, day.ObservedClinicalSpanMinutes);
        Assert.Equal(10, day.UnstructuredTimeMinutes);
        Assert.Equal(20, day.MinutesWithOneDoctorWorkingRoom);
        Assert.Equal(10, day.MinutesWithTwoDoctorWorkingRooms);
        Assert.Equal(10, day.MinutesWithThreeOrMoreDoctorWorkingRooms);
        Assert.Equal(3, day.PeakConcurrentRooms);
        Assert.Equal(
            day.ObservedClinicalSpanMinutes,
            day.UnstructuredTimeMinutes
            + day.MinutesWithOneDoctorWorkingRoom
            + day.MinutesWithTwoDoctorWorkingRooms
            + day.MinutesWithThreeOrMoreDoctorWorkingRooms);
    }

    [Fact]
    public void Build_sequential_working_intervals_count_prearrival_and_between_case_gaps_as_unstructured()
    {
        var start = Utc(2026, 8, 10, 8, 0);
        CompletedRoomCycle[] cycles =
        [
            Cycle(1, 1, "EXT", start, start, start.AddMinutes(5), start.AddMinutes(15), start.AddMinutes(18), 30),
            Cycle(2, 2, "CON", start.AddMinutes(18), start.AddMinutes(20), start.AddMinutes(25), start.AddMinutes(35), start.AddMinutes(50), 30)
        ];

        var day = Assert.Single(CreateBuilder().Build(cycles, [], ReportQuery.Default).ObservedDoctorFlowDays!);

        Assert.Equal(35, day.ObservedClinicalSpanMinutes);
        Assert.Equal(15, day.UnstructuredTimeMinutes);
        Assert.Equal(20, day.MinutesWithOneDoctorWorkingRoom);
        Assert.Equal(1, day.PeakConcurrentRooms);
        Assert.Equal(start.AddMinutes(35), day.LastDoctorCompleteAt);
    }

    [Fact]
    public void Build_fractional_bucket_minutes_use_stable_largest_remainder_tie_break()
    {
        var start = Utc(2026, 8, 10, 8, 0, 0);
        var end = start.AddMinutes(2);
        CompletedRoomCycle[] cycles =
        [
            Cycle(1, 1, "EXT", start, start, start.AddSeconds(30), end, end.AddMinutes(1), 30),
            Cycle(2, 2, "CON", start, start, start.AddSeconds(60), end, end.AddMinutes(1), 30),
            Cycle(3, 3, "BX", start, start, start.AddSeconds(90), end, end.AddMinutes(1), 30)
        ];

        var day = Assert.Single(CreateBuilder().Build(cycles, [], ReportQuery.Default).ObservedDoctorFlowDays!);

        Assert.Equal(2, day.ObservedClinicalSpanMinutes);
        Assert.Equal(1, day.UnstructuredTimeMinutes);
        Assert.Equal(1, day.MinutesWithOneDoctorWorkingRoom);
        Assert.Equal(0, day.MinutesWithTwoDoctorWorkingRooms);
        Assert.Equal(0, day.MinutesWithThreeOrMoreDoctorWorkingRooms);
        Assert.Equal(3, day.PeakConcurrentRooms);
    }

    [Fact]
    public void Build_emits_no_canonical_day_for_missing_ready_missing_arrival_or_cross_day_flow()
    {
        var legacy = CompletedCycle(1, "EXT");
        legacy.ReadyForDoctorAt = null;
        legacy.PrepSeconds = null;
        legacy.ReadyToDoctorSeconds = null;

        var missingArrival = CompletedCycle(2, "CON");
        missingArrival.DoctorArrivedAt = null;
        missingArrival.DoctorInRoomSeconds = null;

        var crossDay = Cycle(
            3, 3, "BX",
            Utc(2026, 8, 11, 0, 0), Utc(2026, 8, 10, 23, 50), Utc(2026, 8, 11, 0, 5),
            Utc(2026, 8, 11, 0, 20), Utc(2026, 8, 11, 0, 30), 30);

        var snapshot = CreateBuilder().Build([legacy, missingArrival, crossDay], [], ReportQuery.Default);

        Assert.Empty(snapshot.ObservedDoctorFlowDays!);
        var summary = Assert.Single(snapshot.DoctorFlowSummaries!);
        Assert.Equal(ReportSampleStates.Unavailable, summary.Samples.ObservedDays.State);
        Assert.Null(summary.MedianObservedClinicalSpanMinutes);
        Assert.Null(summary.PeakConcurrentRooms);
    }

    [Fact]
    public void Build_groups_canonical_days_by_doctor_complete_utc_date()
    {
        var offset = TimeSpan.FromHours(-5);
        var ready = new DateTimeOffset(2026, 8, 10, 19, 10, 0, offset);
        var cycle = Cycle(
            1,
            1,
            "EXT",
            ready.AddMinutes(-5),
            ready,
            ready.AddMinutes(5),
            ready.AddMinutes(30),
            ready.AddMinutes(40),
            30);

        var day = Assert.Single(CreateBuilder().Build([cycle], [], ReportQuery.Default).ObservedDoctorFlowDays!);

        Assert.Equal("2026-08-11", day.ReportDate);
        Assert.Equal(ready, day.FirstAcceptedReadyAt);
        Assert.Equal(ready.AddMinutes(30), day.LastDoctorCompleteAt);
    }

    [Fact]
    public void Build_doctor_flow_summary_uses_underlying_medians_across_months()
    {
        var january = Cycle(
            1, 1, "EXT",
            Utc(2026, 1, 10, 8, 0), Utc(2026, 1, 10, 8, 5), Utc(2026, 1, 10, 9, 45),
            Utc(2026, 1, 10, 11, 5), Utc(2026, 1, 10, 11, 10), 30);
        var februaryStart = Utc(2026, 2, 10, 8, 0);
        CompletedRoomCycle[] cycles =
        [
            january,
            Cycle(2, 2, "CON", februaryStart, februaryStart.AddMinutes(1), februaryStart.AddMinutes(2), februaryStart.AddMinutes(12), februaryStart.AddMinutes(15), 30),
            Cycle(3, 3, "BX", februaryStart.AddHours(1), februaryStart.AddHours(1).AddMinutes(1), februaryStart.AddHours(1).AddMinutes(3), februaryStart.AddHours(1).AddMinutes(23), februaryStart.AddHours(1).AddMinutes(26), 30),
            Cycle(4, 4, "EXT", februaryStart.AddHours(2), februaryStart.AddHours(2).AddMinutes(1), februaryStart.AddHours(2).AddMinutes(4), februaryStart.AddHours(2).AddMinutes(34), februaryStart.AddHours(2).AddMinutes(37), 30)
        ];

        var summary = Assert.Single(CreateBuilder().Build(cycles, [], ReportQuery.Default).DoctorFlowSummaries!);

        Assert.Equal(150, summary.MedianReadyWaitSeconds);
        Assert.Equal(1_500, summary.MedianDoctorTimeSeconds);
        Assert.NotEqual(3_075, summary.MedianReadyWaitSeconds);
    }

    [Fact]
    public void Build_doctor_flow_summary_uses_median_canonical_span_and_server_sample_states()
    {
        var start = Utc(2026, 8, 1, 8, 0);
        var cycles = Enumerable.Range(0, 5)
            .Select(index =>
            {
                var ready = start.AddDays(index);
                return Cycle(
                    index + 1,
                    index + 1,
                    index % 2 == 0 ? "EXT" : "CON",
                    ready,
                    ready,
                    ready.AddMinutes(5),
                    ready.AddMinutes(10 + (index * 10)),
                    ready.AddMinutes(70 + (index * 10)),
                    30);
            })
            .ToArray();

        var sufficient = Assert.Single(CreateBuilder().Build(cycles, [], ReportQuery.Default).DoctorFlowSummaries!);
        Assert.Equal(30, sufficient.MedianObservedClinicalSpanMinutes);
        Assert.Equal(5, sufficient.ObservedDoctorDayCount);
        Assert.Equal(ReportSampleStates.Sufficient, sufficient.Samples.ObservedDays.State);

        var empty = Assert.Single(CreateBuilder().Build([], [], ReportQuery.Default).DoctorFlowSummaries!);
        Assert.Equal(ReportSampleStates.Empty, empty.Samples.CompletedCases.State);
        Assert.Equal(ReportSampleStates.Empty, empty.Samples.ObservedDays.State);
        Assert.Null(empty.MedianReadyWaitSeconds);
        Assert.Null(empty.MedianDoctorTimeSeconds);

        var limited = Assert.Single(CreateBuilder().Build([cycles[0]], [], ReportQuery.Default).DoctorFlowSummaries!);
        Assert.Equal(ReportSampleStates.Limited, limited.Samples.ObservedDays.State);
    }

    [Fact]
    public void Build_practice_summary_keeps_active_roster_order_then_historical_and_doctor_scope_is_exact()
    {
        Doctor[] allDoctors =
        [
            new("otte", "Dr. Otte", "LDO", "#dc2626"),
            new("pledger", "Dr. Pledger", "JWP", "#16a34a"),
            new("former", "Dr. Former", "OLD", "#64748b")
        ];
        Doctor[] activeDoctors = [allDoctors[0], allDoctors[1]];
        var historical = CompletedCycle(1, "EXT", "former");
        var practice = CreateBuilder(allDoctors, activeDoctors).Build([historical], [], ReportQuery.Default);
        var summaries = Assert.IsAssignableFrom<IReadOnlyList<DoctorFlowSummary>>(practice.DoctorFlowSummaries);

        Assert.Equal(["otte", "pledger", "former"], summaries.Select(row => row.DoctorId));
        Assert.Equal(ReportSampleStates.Empty, summaries[0].Samples.CompletedCases.State);
        Assert.Equal(ReportSampleStates.Empty, summaries[1].Samples.CompletedCases.State);
        Assert.Equal(ReportSampleStates.Limited, summaries[2].Samples.CompletedCases.State);

        var doctorQuery = ReportQuery.FromStrings(
            null, null, ReportScopeKinds.Doctor, "former", ReportSedationSegments.Sedation,
            ReportProcedureGroupings.Family);
        var scoped = CreateBuilder(allDoctors, activeDoctors).Build([historical], [], doctorQuery);
        var requested = Assert.Single(scoped.DoctorFlowSummaries!);
        Assert.Equal("former", requested.DoctorId);
        Assert.Equal(ReportSampleStates.Empty, requested.Samples.CompletedCases.State);
    }

    [Fact]
    public void Canonical_and_compatibility_observed_day_json_contracts_remain_distinct()
    {
        var snapshot = CreateBuilder().Build([CompletedCycle(1, "EXT")], [], ReportQuery.Default);
        var json = JsonSerializer.SerializeToElement(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var compatibility = json.GetProperty("observedDoctorDays")[0];
        var canonical = json.GetProperty("observedDoctorFlowDays")[0];

        Assert.Equal(
            [
                "doctorId", "doctorName", "reportDate", "encounterCount", "firstSeatedAt",
                "firstDoctorArrivedAt", "lastDoctorCompleteAt", "lastRoomAvailableAt",
                "observedClinicalSpanMinutes", "observedTeamSpanMinutes",
                "minutesWithOneActiveRoom", "minutesWithTwoActiveRooms",
                "minutesWithThreeOrMoreActiveRooms", "maxActiveRoomCount"
            ],
            compatibility.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(
            [
                "doctorId", "doctorName", "reportDate", "qualifyingCaseCount",
                "firstAcceptedReadyAt", "lastDoctorCompleteAt", "observedClinicalSpanMinutes",
                "minutesWithOneDoctorWorkingRoom", "minutesWithTwoDoctorWorkingRooms",
                "minutesWithThreeOrMoreDoctorWorkingRooms", "unstructuredTimeMinutes",
                "peakConcurrentRooms"
            ],
            canonical.EnumerateObject().Select(property => property.Name).ToArray());

        var summary = json.GetProperty("doctorFlowSummaries")[0];
        Assert.Equal(JsonValueKind.Object, summary.GetProperty("samples").ValueKind);
        Assert.Equal(
            ["completedCases", "readyWait", "doctorTime", "observedDays"],
            summary.GetProperty("samples").EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public void Build_scopes_review_queue_by_effective_attribution_and_window()
    {
        var inWindow = new AbortedRoomAssignment
        {
            AbortedAssignmentId = 1,
            EpisodeId = "aborted-1",
            RoomId = 1,
            AssignedDoctor = "other-doctor",
            ProcedureCode = "CON",
            TerminatedAt = Utc(2026, 8, 10, 12, 0)
        };
        inWindow.ReportingProjection = Projection(
            HistoricalAdministrativeDispositions.NeedsReview,
            doctor: "otte",
            procedure: "EXT",
            sedation: SedationState.EligibleYes);
        var outOfWindow = new AbortedRoomAssignment
        {
            AbortedAssignmentId = 2,
            EpisodeId = "aborted-2",
            RoomId = 2,
            AssignedDoctor = "other-doctor",
            ProcedureCode = "CON",
            TerminatedAt = Utc(2026, 8, 9, 12, 0)
        };
        outOfWindow.ReportingProjection = Projection(
            HistoricalAdministrativeDispositions.NeedsReview,
            doctor: "otte",
            procedure: "EXT",
            sedation: SedationState.EligibleYes);
        var query = ReportQuery.FromStrings(
            "2026-08-10",
            "2026-08-10",
            ReportScopeKinds.Doctor,
            "otte",
            ReportSedationSegments.Sedation,
            ReportProcedureGroupings.Family);

        var snapshot = CreateBuilder().Build([], [inWindow, outOfWindow], query);

        var review = Assert.Single(snapshot.ExceptionReviewRecords!);
        Assert.Equal(1, review.AbortedAssignmentId);
        Assert.Empty(snapshot.RecentCompletedCycles);
    }

    [Fact]
    public void Procedure_intelligence_reuses_scoped_groups_and_recomputes_family_range_from_cases()
    {
        var cycles = new List<CompletedRoomCycle>();
        var extDoctorMinutes = new[] { 10, 10, 10, 10, 50 };
        var sedationDoctorMinutes = new[] { 20, 20, 20, 20, 20 };
        var id = 1L;
        cycles.AddRange(extDoctorMinutes.Select(minutes => ProcedureCycle(id++, "EXT", "otte", minutes)));
        cycles.AddRange(sedationDoctorMinutes.Select(minutes => ProcedureCycle(id++, "EXT+SED", "otte", minutes)));

        var familyQuery = ReportQuery.FromStrings(
            null, null, ReportScopeKinds.Practice, null, ReportSedationSegments.All,
            ReportProcedureGroupings.Family);
        var detailedQuery = familyQuery with { ProcedureGrouping = ReportProcedureGroupings.DetailedVariant };
        var family = CreateBuilder().Build(cycles, [], familyQuery);
        var detailed = CreateBuilder().Build(cycles, [], detailedQuery);

        Assert.Equal(family.ScheduleFit!.Practice, detailed.ScheduleFit!.Practice);

        var familyGroup = Assert.Single(family.ScopedProcedureGroups!);
        var familyRow = Assert.Single(family.ProcedureIntelligenceRows!);
        Assert.Equal(familyGroup.ProcedureCode, familyRow.ProcedureCode);
        Assert.Equal(familyGroup.CaseCount, familyRow.Metrics.CompletedCaseCount);
        Assert.Equal(10, familyRow.Metrics.TypicalDoctorTimeLowerSeconds / 60d);
        Assert.Equal(20, familyRow.Metrics.TypicalDoctorTimeUpperSeconds / 60d);

        Assert.Equal(2, detailed.ScopedProcedureGroups!.Count);
        Assert.Equal(2, detailed.ProcedureIntelligenceRows!.Count);
        Assert.Equal(2, detailed.ScheduleFit!.ProcedureSegments!.Count);
        Assert.Equal(
            detailed.ScopedProcedureGroups.Select(row => row.ProcedureCode),
            detailed.ProcedureIntelligenceRows.Select(row => row.ProcedureCode));
        Assert.Equal(
            detailed.ScopedProcedureGroups.Select(row => row.ProcedureCode),
            detailed.ScheduleFit.ProcedureSegments.Select(row => row.ProcedureCode));
        Assert.Equal(10, detailed.ProcedureIntelligenceRows.Single(row => row.ProcedureCode == "EXT")
            .Metrics.TypicalDoctorTimeLowerSeconds / 60d);
        Assert.Equal(20, detailed.ProcedureIntelligenceRows.Single(row => row.ProcedureCode == "EXT+SED")
            .Metrics.TypicalDoctorTimeLowerSeconds / 60d);

        var sedation = CreateBuilder().Build(
            cycles,
            [],
            familyQuery with { Sedation = ReportSedationSegments.Sedation });
        var sedationGroup = Assert.Single(sedation.ScopedProcedureGroups!);
        var sedationRow = Assert.Single(sedation.ProcedureIntelligenceRows!);
        Assert.Equal(5, sedationGroup.CaseCount);
        Assert.Equal(5, sedationRow.Metrics.CompletedCaseCount);
        Assert.Equal(20, sedationRow.Metrics.MedianDoctorTimeSeconds / 60d);
        Assert.Equal(5, Assert.Single(sedation.ScheduleFit!.ProcedureSegments!)
            .HistoricalAssignedFit.PopulationCount);
    }

    [Fact]
    public void Procedure_intelligence_uses_metric_specific_truthful_contributors_and_completed_membership()
    {
        var canonical = ProcedureCycle(1, "EXT", "otte", doctorMinutes: 20, readyWaitMinutes: 5);
        canonical.RoomAvailableAt = canonical.DoctorCompleteAt!.Value.AddHours(1);
        canonical.TotalRoomCycleSeconds = SecondsBetween(canonical.SeatedAt, canonical.RoomAvailableAt.Value);

        var legacyWithoutReady = ProcedureCycle(2, "EXT", "otte", doctorMinutes: 30);
        legacyWithoutReady.ReadyForDoctorAt = null;
        legacyWithoutReady.ReadyToDoctorSeconds = null;

        var invalidDoctorTime = ProcedureCycle(3, "EXT", "otte", doctorMinutes: 10);
        invalidDoctorTime.DoctorCompleteAt = invalidDoctorTime.DoctorArrivedAt!.Value.AddMinutes(-1);
        invalidDoctorTime.DoctorInRoomSeconds = 60;

        var incompleteTurnover = ProcedureCycle(4, "EXT", "otte", doctorMinutes: 40);
        incompleteTurnover.RoomAvailableAt = null;
        incompleteTurnover.TotalRoomCycleSeconds = null;

        var snapshot = CreateBuilder().Build(
            [canonical, legacyWithoutReady, invalidDoctorTime, incompleteTurnover],
            [],
            ReportQuery.Default);
        var row = Assert.Single(snapshot.ProcedureIntelligenceRows!);

        Assert.Equal(3, row.Metrics.CompletedCaseCount);
        Assert.Equal(2, row.Metrics.DoctorTimeSample.ContributingCount);
        Assert.Equal(ReportSampleStates.Limited, row.Metrics.DoctorTimeSample.State);
        Assert.Equal(25 * 60, row.Metrics.MedianDoctorTimeSeconds);
        Assert.Null(row.Metrics.TypicalDoctorTimeLowerSeconds);
        Assert.Null(row.Metrics.TypicalDoctorTimeUpperSeconds);
        Assert.Equal(2, row.Metrics.ReadyWaitSample.ContributingCount);
        Assert.Equal(3, row.Metrics.SeatedToDoctorCompleteSample.ContributingCount);
        Assert.Equal(30 * 60, row.Metrics.MedianSeatedToDoctorCompleteSeconds);
        Assert.NotEqual(canonical.TotalRoomCycleSeconds, row.Metrics.MedianSeatedToDoctorCompleteSeconds);
    }

    [Fact]
    public void Procedure_intelligence_keeps_current_and_historical_allocation_context_separate()
    {
        var first = ProcedureCycle(1, "EXT", "otte", 20, expectedAllocationMinutes: 20);
        var second = ProcedureCycle(2, "EXT", "otte", 20, expectedAllocationMinutes: 40);
        var third = ProcedureCycle(3, "EXT", "otte", 20, expectedAllocationMinutes: 40);
        var known = ProcedureCycle(4, "CON", "otte", 10, expectedAllocationMinutes: 10);
        first.OriginalDefaultExpectedUnits = 2;
        second.OriginalDefaultExpectedUnits = 3;
        third.OriginalDefaultExpectedUnits = 3;
        known.OriginalDefaultExpectedUnits = 1;

        var rows = CreateBuilder().Build(
            [first, second, third, known], [], ReportQuery.Default).ProcedureIntelligenceRows!;
        var row = Assert.Single(rows, item => item.ProcedureCode == "EXT");
        var knownRow = Assert.Single(rows, item => item.ProcedureCode == "CON");

        Assert.Equal(30, row.CurrentDefaultAllocationMinutes);
        Assert.Equal("Variable", row.AllocationBehavior);
        Assert.Equal(10, knownRow.CurrentDefaultAllocationMinutes);
        Assert.Equal("Known", knownRow.AllocationBehavior);
        Assert.Equal(40, row.Metrics.MedianHistoricalAssignedAllocationMinutes);
        Assert.Equal(
            [(20, 1), (40, 2)],
            row.Metrics.HistoricalAssignedAllocationValues
                .Select(value => (value.Minutes, value.CaseCount)));
        Assert.Equal(
            [(20, 1), (30, 2)],
            row.Metrics.HistoricalCapturedDefaultValues
                .Select(value => (value.Minutes, value.CaseCount)));
    }

    [Fact]
    public void Schedule_fit_uses_exact_seconds_and_preserves_population_coverage_and_reconciliation()
    {
        var debt = ProcedureCycle(1, "EXT", "otte", 20, expectedAllocationMinutes: 30);
        var slack = ProcedureCycle(2, "EXT", "otte", 20, expectedAllocationMinutes: 30);
        var unpaired = ProcedureCycle(3, "EXT", "otte", 20, expectedAllocationMinutes: 0);
        var notAvailable = ProcedureCycle(4, "EXT", "otte", 20, expectedAllocationMinutes: 30);
        debt.DoctorCompleteAt = debt.SeatedAt.AddMinutes(30).AddSeconds(29);
        slack.DoctorCompleteAt = slack.SeatedAt.AddMinutes(29).AddSeconds(31);
        notAvailable.RoomAvailableAt = null;
        notAvailable.TotalRoomCycleSeconds = null;

        var snapshot = CreateBuilder().Build([debt, slack, unpaired, notAvailable], [], ReportQuery.Default);
        var practice = snapshot.ScheduleFit!.Practice!;

        Assert.Equal(3, practice.PopulationCount);
        Assert.Equal(2, practice.PairedCaseCount);
        Assert.Equal(2d / 3d, practice.PopulationCoverage, precision: 10);
        Assert.Equal(3600d, practice.TotalExpectedSeconds);
        Assert.Equal(3600d, practice.TotalObservedSeconds);
        Assert.Equal(29d, practice.TotalSlackSeconds);
        Assert.Equal(29d, practice.TotalDebtSeconds);
        Assert.Equal(0d, practice.NetVarianceSeconds);
        Assert.Equal(
            practice.TotalObservedSeconds - practice.TotalExpectedSeconds,
            practice.TotalDebtSeconds - practice.TotalSlackSeconds);
    }

    [Fact]
    public void Calibration_uses_current_roster_default_and_paired_median_not_historical_or_captured_defaults()
    {
        var cycles = Enumerable.Range(1, 10)
            .Select(id => ProcedureCycle(id, "EXT", "otte", doctorMinutes: 31,
                expectedAllocationMinutes: 40))
            .ToArray();
        foreach (var cycle in cycles)
        {
            cycle.OriginalDefaultExpectedUnits = 1;
            cycle.AcceptedReadyHandoffId = $"handoff-{cycle.CompletedCycleId}";
        }

        var segment = Assert.Single(CreateBuilder().Build(cycles, [], ReportQuery.Default)
            .ScheduleFit!.ProcedureSegments!);
        var historical = segment.HistoricalAssignedFit;
        var calibration = segment.CurrentDefaultCalibration;

        Assert.Equal(30, segment.CurrentDefaultAllocationMinutes);
        Assert.Equal(60d, historical.MedianPairedVarianceSeconds);
        Assert.Equal(CalibrationDecisions.Qualified, calibration.Decision);
        Assert.Equal(660d, calibration.MedianPairedVarianceSeconds);
        Assert.Equal(CalibrationInsightDirections.MoreTimeThanCurrentDefault,
            calibration.CandidateDirection);
        Assert.Equal(10, calibration.AboveBaselineCaseCount);
        Assert.Equal(10, calibration.MoreThanToleranceCaseCount);
        var insight = Assert.IsType<CalibrationInsight>(calibration.Insight);
        Assert.Equal(660d, insight.MedianDifferenceSeconds);
        Assert.Equal(10, insight.Evidence.Count);
        Assert.All(insight.Evidence, evidence =>
        {
            Assert.Equal(CalibrationBaselineSources.CurrentRosterDefault, evidence.BaselineSource);
            Assert.Equal(30, evidence.BaselineMinutesUsed);
            Assert.Equal(660d, evidence.PairedVarianceSeconds);
            Assert.Equal(CalibrationRawDirections.AboveBaseline, evidence.RawDirection);
            Assert.Equal(ScheduleFitToleranceClassifications.MoreTimeThanAllocation,
                evidence.ToleranceClassification);
        });
    }

    [Fact]
    public void Procedure_intelligence_doctor_breakdown_uses_roster_then_historical_order_and_doctor_scope_is_flat()
    {
        Doctor[] allDoctors =
        [
            new("pledger", "Dr. Pledger", "JWP", "#16a34a"),
            new("otte", "Dr. Otte", "LDO", "#dc2626"),
            new("former-z", "Dr. Zed", "ZZZ", "#64748b"),
            new("former-a", "Dr. Able", "AAA", "#64748b")
        ];
        Doctor[] activeDoctors = [allDoctors[0], allDoctors[1]];
        CompletedRoomCycle[] cycles =
        [
            ProcedureCycle(1, "EXT", "otte", 10),
            ProcedureCycle(2, "EXT", "pledger", 20),
            ProcedureCycle(3, "EXT", "former-z", 30),
            ProcedureCycle(4, "EXT", "former-a", 40),
            ProcedureCycle(5, "EXT", "otte", 50)
        ];
        var builder = CreateBuilder(allDoctors, activeDoctors);

        var practiceRow = Assert.Single(builder.Build(cycles, [], ReportQuery.Default)
            .ProcedureIntelligenceRows!);
        Assert.Equal(
            ["pledger", "otte", "former-a", "former-z"],
            practiceRow.DoctorBreakdown.Select(segment => segment.DoctorId));
        Assert.All(practiceRow.DoctorBreakdown, segment =>
            Assert.Equal(ReportSampleStates.Limited, segment.Metrics.DoctorTimeSample.State));
        var practiceScheduleFit = Assert.Single(builder.Build(cycles, [], ReportQuery.Default)
            .ScheduleFit!.ProcedureSegments!);
        Assert.Equal(
            ["pledger", "otte", "former-a", "former-z"],
            practiceScheduleFit.DoctorBreakdown.Select(segment => segment.DoctorId));

        var doctorQuery = ReportQuery.FromStrings(
            null, null, ReportScopeKinds.Doctor, "former-a", ReportSedationSegments.All,
            ReportProcedureGroupings.Family);
        var doctorRow = Assert.Single(builder.Build(cycles, [], doctorQuery).ProcedureIntelligenceRows!);
        Assert.Equal(1, doctorRow.Metrics.CompletedCaseCount);
        Assert.Empty(doctorRow.DoctorBreakdown);
        var doctorScheduleFit = builder.Build(cycles, [], doctorQuery).ScheduleFit!;
        Assert.Empty(Assert.Single(doctorScheduleFit.ProcedureSegments!).DoctorBreakdown);
        Assert.Equal("former-a", Assert.Single(doctorScheduleFit.DoctorSummaries!).DoctorId);
    }

    [Fact]
    public void Procedure_intelligence_json_contract_is_appended_without_variance_interpretation()
    {
        var snapshot = CreateBuilder().Build(
            [ProcedureCycle(1, "EXT", "otte", 20)], [], ReportQuery.Default);
        var json = JsonSerializer.SerializeToElement(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var row = json.GetProperty("procedureIntelligenceRows")[0];
        var metrics = row.GetProperty("metrics");

        Assert.Equal("Type7Iqr", metrics.GetProperty("typicalDoctorTimeMethod").GetString());
        Assert.True(metrics.TryGetProperty("medianSeatedToDoctorCompleteSeconds", out _));
        Assert.True(row.TryGetProperty("currentDefaultAllocationMinutes", out _));
        Assert.False(metrics.TryGetProperty("variance", out _));
        Assert.False(metrics.TryGetProperty("slack", out _));
        Assert.False(metrics.TryGetProperty("debt", out _));
        Assert.NotNull(snapshot.ProcedureSummaries);
        Assert.NotNull(snapshot.BaseProcedureSummaries);
        Assert.NotNull(snapshot.ScopedProcedureGroups);

        var scheduleFit = json.GetProperty("scheduleFit");
        Assert.True(scheduleFit.TryGetProperty("overall", out _));
        Assert.True(scheduleFit.TryGetProperty("practice", out _));
        Assert.True(scheduleFit.TryGetProperty("procedureSegments", out _));
        Assert.True(scheduleFit.TryGetProperty("doctorSummaries", out _));
        var rules = scheduleFit.GetProperty("rules");
        Assert.Equal(10, rules.GetProperty("minimumPairedCases").GetInt32());
        Assert.Equal(600, rules.GetProperty("atExpectedToleranceSeconds").GetInt32());
        Assert.Equal(0.8d, rules.GetProperty("minimumDirectionalShare").GetDouble(), precision: 10);
        Assert.Equal("RawPairedVarianceSign", rules.GetProperty("directionalMethod").GetString());
        Assert.Equal("AllPairedCases", rules.GetProperty("directionalDenominator").GetString());
        Assert.Equal("StrictlyGreaterThan", rules.GetProperty("materialComparison").GetString());
        Assert.Equal("SelectedPopulationOnly", rules.GetProperty("persistenceRequirement").GetString());
        Assert.Equal("CurrentRosterDefault", rules.GetProperty("baseline").GetString());
    }

    private static ReportsSnapshotBuilder CreateBuilder(
        IReadOnlyList<Doctor>? doctors = null,
        IReadOnlyList<Doctor>? activeDoctors = null)
    {
        Doctor[] defaultDoctors =
        [
            new("otte", "Dr. Otte", "LDO", "#dc2626")
        ];
        ProcedureCategory[] procedures =
        [
            new("extraction", "EXT", "Extraction", "forceps", SedationEligible: true,
                AllocationBehavior: "Variable", DefaultExpectedUnits: 3),
            new("consult", "CON", "Consult", "message-circle",
                AllocationBehavior: "Known", DefaultExpectedUnits: 1),
            new("sedation", "SED", "Sedation", "moon", SedationEligible: true),
            new("biopsy", "BX", "Biopsy", "vial",
                AllocationBehavior: "Variable", DefaultExpectedUnits: 2)
        ];
        ProcedureCategory[] activeProcedures = [procedures[0], procedures[1], procedures[3]];
        var allDoctors = doctors ?? defaultDoctors;
        var active = activeDoctors ?? allDoctors;
        return new ReportsSnapshotBuilder(allDoctors, active, procedures, activeProcedures);
    }

    private static HistoricalReportingProjection Projection(
        string disposition,
        string doctor,
        string procedure,
        SedationState sedation) =>
        new(
            disposition,
            doctor,
            procedure,
            sedation,
            HasExplicitSedationEvidence: true,
            PreserveLegacySedationTransport: false,
            EffectiveIsAddOn: false,
            ExpectedAllocationState.ConfirmedSuggestedValue,
            EffectiveExpectedAllocationSuggestedUnits: 3,
            EffectiveExpectedAllocationConfirmedUnits: 3,
            CurrentReason: HistoricalManualReviewReasons.OtherNeedsReview,
            ReasonSource: HistoricalAdministrativeReasonSources.LocalAdmin,
            KnownReviewedAt: null,
            KnownReviewedActorClass: null,
            AdministrativeRevision: 1,
            HasHistoricalCorrectionProvenance: false,
            HasReviewedProvenance: false);

    private static ReportsSnapshot BuildScoped(
        IReadOnlyList<CompletedRoomCycle> cycles,
        string sedation) =>
        CreateBuilder().Build(
            cycles,
            [],
            ReportQuery.FromStrings(
                null,
                null,
                ReportScopeKinds.Practice,
                null,
                sedation,
                ReportProcedureGroupings.DetailedVariant));

    private static void AssertProcedureGroup(
        ScopedProcedureGroup row,
        string procedureCode,
        string procedureLabel,
        int caseCount,
        int scopedPopulationCount,
        double share,
        bool? isSedationCase)
    {
        Assert.Equal(procedureCode, row.ProcedureCode);
        Assert.Equal(procedureLabel, row.ProcedureLabel);
        Assert.Equal(caseCount, row.CaseCount);
        Assert.Equal(scopedPopulationCount, row.ScopedPopulationCount);
        Assert.Equal(share, row.ShareOfScopedCases, precision: 10);
        Assert.Equal(isSedationCase, row.IsSedationCase);
    }

    private static CompletedRoomCycle CompletedCycle(long id, string procedureCode, string doctorId = "otte")
    {
        var hour = 8 + (int)(id % 8);
        var seatedAt = Utc(2026, 8, 10, hour, 0);
        var cycle = Cycle(
            id,
            (int)((id - 1) % 12) + 1,
            procedureCode,
            seatedAt,
            seatedAt.AddMinutes(5),
            seatedAt.AddMinutes(10),
            seatedAt.AddMinutes(30),
            seatedAt.AddMinutes(40),
            30);
        cycle.AssignedDoctor = doctorId;
        return cycle;
    }

    private static CompletedRoomCycle ProcedureCycle(
        long id,
        string procedureCode,
        string doctorId,
        int doctorMinutes,
        int readyWaitMinutes = 5,
        int expectedAllocationMinutes = 30)
    {
        var seatedAt = Utc(2026, 8, 10, 8, 0).AddMinutes(id * 70);
        var readyAt = seatedAt.AddMinutes(5);
        var arrivedAt = readyAt.AddMinutes(readyWaitMinutes);
        var completeAt = arrivedAt.AddMinutes(doctorMinutes);
        var cycle = Cycle(
            id,
            (int)((id - 1) % 12) + 1,
            procedureCode,
            seatedAt,
            readyAt,
            arrivedAt,
            completeAt,
            completeAt.AddMinutes(10),
            expectedAllocationMinutes);
        cycle.AssignedDoctor = doctorId;
        return cycle;
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

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute, int second) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);
}
