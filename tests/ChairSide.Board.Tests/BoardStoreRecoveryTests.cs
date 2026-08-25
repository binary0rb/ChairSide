using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

using ChairSide.Board.Options;
using ChairSide.Board.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ChairSide.Board.Tests;

public sealed partial class BoardStoreTests
{
    [Fact]
    public void Normal_completed_cycles_appear_in_normal_reporting_metrics()
    {
        // A standard full-lifecycle cycle must appear in CompletedRoomCyclesCount
        // and in RecentCompletedCycles; exception flag must default to false.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var reports = context.Store.GetReports();

        Assert.Equal(1, reports.CompletedRoomCyclesCount);
        Assert.Single(reports.RecentCompletedCycles);
        Assert.Empty(reports.ExceptionCycles);

        // The loaded cycle must carry IsException = false.
        var cycle = Assert.Single(context.Repository.LoadCompletedCycles());
        Assert.False(cycle.IsException);
        Assert.False(cycle.RequiresReview);
        Assert.Equal(ReviewStatuses.PendingReview, cycle.ReviewStatus);
    }

    [Fact]
    public void Exception_cycles_are_excluded_from_normal_metrics_and_count()
    {
        // After a cycle is marked as an exception it must not appear in
        // CompletedRoomCyclesCount, averages, or RecentCompletedCycles.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Cycle A: full lifecycle on room 1 - should appear in normal metrics.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        // Cycle B: only reached DoctorArrived on room 2 - will be marked exception.
        clock.SetUtcNow(now.AddMinutes(30));
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "pledger", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        var exceptionArrived = context.Store.MarkDoctorArrived(2);
        Assert.NotNull(exceptionArrived);
        // SeatedAt is still set on the DoctorInRoom RoomStatus (room has not been reset).
        Assert.NotNull(exceptionArrived.SeatedAt);
        var exceptionSeatedAt = exceptionArrived.SeatedAt!.Value;

        // Mark cycle B as an exception.
        var marked = context.Store.MarkCycleAsException(2, exceptionSeatedAt, "Abnormal wait time", "Manual review required");
        Assert.True(marked);

        var reports = context.Store.GetReports();

        // Only cycle A (normal, completed) is counted.
        Assert.Equal(1, reports.CompletedRoomCyclesCount);
        Assert.Single(reports.RecentCompletedCycles);
        Assert.Equal(1, reports.RecentCompletedCycles[0].RoomId);

        // Cycle B is surfaced as an exception.
        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.Equal(2, exception.RoomId);
        Assert.Equal(HistoricalManualReviewReasons.OtherNeedsReview, exception.ExceptionReason);
        Assert.Null(exception.SuggestedAction);
        Assert.Equal(ReviewStatuses.PendingReview, exception.ReviewStatus);
        Assert.True(exception.IsException);
        Assert.True(exception.RequiresReview);
    }

    [Fact]
    public void Exception_cycles_appear_in_exceptions_requiring_review_section()
    {
        // GetReports().ExceptionCycles contains exactly the cycles with IsException = true,
        // regardless of whether they have a RoomAvailableAt timestamp.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Cycle A: complete lifecycle - normal.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        var arrivedA = context.Store.MarkDoctorArrived(1);
        Assert.NotNull(arrivedA);

        // Cycle B: only reached DoctorArrived - will be marked exception.
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "pledger", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        var arrivedB = context.Store.MarkDoctorArrived(2);
        Assert.NotNull(arrivedB);
        var seatedAtB = arrivedB.SeatedAt!.Value;

        context.Store.MarkCycleAsException(2, seatedAtB, "Timed out", "Investigate");

        var reports = context.Store.GetReports();

        // Normal cycle: Cycle A is in both DoctorSummaries (arrived) and RecentCompletedCycles is empty (no RoomAvailableAt yet).
        // Exception cycle: Cycle B is in ExceptionCycles only.
        Assert.Single(reports.ExceptionCycles);
        Assert.Equal(2, reports.ExceptionCycles[0].RoomId);
        Assert.Equal(
            HistoricalManualReviewReasons.OtherNeedsReview,
            reports.ExceptionCycles[0].ExceptionReason);
        Assert.DoesNotContain(reports.ExceptionCycles, cycle => cycle.RoomId == 1);
    }

    [Fact]
    public void Canonical_pending_review_status_survives_store_restart()
    {
        // The compatibility route persists canonical administrative state and leaves the source
        // review columns unchanged. The effective review projection must survive reload.
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();

        var first = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        Assert.NotNull(SeatViaPrestage(first.Store, 1, "otte", "CON"));
        Assert.NotNull(first.Store.MarkReadyForDoctor(1));
        var arrived = first.Store.MarkDoctorArrived(1);
        Assert.NotNull(arrived);
        var seatedAt = arrived.SeatedAt!.Value;

        var marked = first.Store.MarkCycleAsException(1, seatedAt, "Extended wait", "Review with doctor");
        Assert.True(marked);

        // Reload the store - simulates a server restart.
        var second = StoreContext.Create(workspace, environmentName: Environments.Production, databasePath: databasePath);
        var reports = second.Store.GetReports();

        Assert.Empty(reports.RecentCompletedCycles); // excluded from normal
        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.Equal(1, exception.RoomId);
        Assert.True(exception.IsException);
        Assert.True(exception.RequiresReview);
        Assert.Equal(HistoricalManualReviewReasons.OtherNeedsReview, exception.ExceptionReason);
        Assert.Null(exception.SuggestedAction);
        Assert.Equal(ReviewStatuses.PendingReview, exception.ReviewStatus);
        Assert.Null(exception.ReviewedAt);
        Assert.Null(exception.ReviewedBy);
    }

    // -----------------------------------------------------------------------
    // Exception cycle handling - manual + automatic expiration
    // -----------------------------------------------------------------------

    [Fact]
    public void Room_expiration_options_defaults_are_locked()
    {
        var options = new RoomExpirationOptions();

        Assert.True(options.Enabled);
        Assert.Equal(8, options.MaxActiveDurationHours);
        Assert.True(options.AfterHoursSweepEnabled);
        Assert.Equal("19:00", options.AfterHoursSweepTime);
        Assert.Equal("America/Chicago", options.TimeZone);
    }

    [Fact]
    public void Prestaging_before_max_duration_is_not_expired()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "EXT", expectedAllocationUnits: 5));
        clock.SetUtcNow(now.AddHours(7).AddMinutes(59));

        Assert.Empty(context.Store.CheckAndExpireActiveCycles());
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(1)?.State);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
        Assert.Empty(context.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Absent_assignment_prestaging_max_duration_expiration_preserves_truthful_abort_history()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 3, 18, 0, 0, TimeSpan.Zero);
        var seed = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);
        seed.Repository.SaveRooms(
            [
                new RoomState(1)
                {
                    State = RoomStates.Prestaging,
                    PrestageStartedAt = now.AddHours(-9)
                }
            ],
            seed.Doctors,
            seed.Procedures);

        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: new ManualTimeProvider(now),
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.Equal([1], context.Store.CheckAndExpireActiveCycles());
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Null(aborted.AssignedDoctor);
        Assert.Null(aborted.ProcedureCode);
        Assert.Null(aborted.SeatedAt);
        Assert.Empty(context.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Prestaging_over_max_duration_persists_complete_abort_and_reset_across_reload()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var prestageAt = new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero);
        var terminatedAt = prestageAt.AddHours(8).AddSeconds(1);
        var clock = new ManualTimeProvider(prestageAt);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        var addOn = RoomAssignmentContract.Create(
            "otte",
            "EXT+SED",
            SedationContract.EligibleYes(),
            ExpectedAllocationContract.ConfirmedAdjustedValue(3, 5),
            isAddOn: true);
        Assert.NotNull(context.Store.BeginPrestage(1));
        Assert.NotNull(context.Store.SaveAssignmentDetails(1, addOn));
        var active = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        clock.SetUtcNow(terminatedAt);

        Assert.Equal([1], context.Store.CheckAndExpireActiveCycles());
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        var abort = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(active.EpisodeId, abort.EpisodeId);
        Assert.Equal(1, abort.RoomId);
        Assert.Equal("otte", abort.AssignedDoctor);
        Assert.Equal("Dr. Otte", abort.AssignedDoctorDisplayName);
        Assert.Equal("EXT+SED", abort.ProcedureCode);
        Assert.Equal("Extraction + Sedation", abort.ProcedureCategory);
        Assert.Equal(3, abort.OriginalDefaultExpectedUnits);
        Assert.Equal(5, abort.ExpectedAllocationUnits);
        Assert.Equal(50, abort.ExpectedAllocationMinutes);
        Assert.True(abort.AllocationAdjustedFromDefault);
        Assert.True(abort.IsAddOn);
        Assert.Equal(prestageAt, abort.PrestageStartedAt);
        Assert.Null(abort.SeatedAt);
        Assert.Null(abort.ReadyForDoctorAt);
        Assert.Equal(terminatedAt, abort.TerminatedAt);
        Assert.Equal(RoomStates.Prestaging, abort.TerminatedFromState);
        Assert.Equal(TerminationKinds.MaxDurationExpired, abort.TerminationKind);
        Assert.Null(abort.CancellationReason);
        Assert.Empty(context.Repository.LoadCompletedCycles());

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock);
        var durableRoom = reloaded.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Available, durableRoom.State);
        Assert.Null(durableRoom.EpisodeId);
        Assert.Null(durableRoom.PrestageStartedAt);
        Assert.Null(durableRoom.SeatedAt);
        AssertSameAbortedAssignment(abort, Assert.Single(reloaded.Repository.LoadAbortedAssignments()));
        Assert.Empty(reloaded.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Prestaging_max_duration_expiration_is_idempotent_after_success()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));

        Assert.Equal([1], context.Store.CheckAndExpireActiveCycles());
        Assert.Empty(context.Store.CheckAndExpireActiveCycles());
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Empty(context.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Prestaging_after_hours_sweep_uses_clinic_time_and_runs_once_per_clinic_day()
    {
        using var workspace = TestWorkspace.Create();
        var beforeCutoff = new DateTimeOffset(2026, 7, 3, 23, 30, 0, TimeSpan.Zero); // 18:30 CDT
        var afterCutoff = beforeCutoff.AddHours(1); // 19:30 CDT, same clinic day
        var clock = new ManualTimeProvider(beforeCutoff);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "19:00",
                TimeZone = "America/Chicago"
            });

        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "CON"));
        Assert.Empty(context.Store.TryRunAfterHoursSweep());

        clock.SetUtcNow(afterCutoff);
        Assert.Equal([1], context.Store.TryRunAfterHoursSweep());
        var abort = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(RoomStates.Prestaging, abort.TerminatedFromState);
        Assert.Equal(TerminationKinds.AfterHoursExpired, abort.TerminationKind);
        Assert.Equal(afterCutoff, abort.TerminatedAt);
        Assert.Null(abort.SeatedAt);
        Assert.Null(abort.CancellationReason);
        Assert.Empty(context.Repository.LoadCompletedCycles());

        Assert.NotNull(context.Store.BeginPrestage(2, "pledger", "EXT"));
        clock.SetUtcNow(afterCutoff.AddMinutes(10));
        Assert.Empty(context.Store.TryRunAfterHoursSweep());
        Assert.Equal(RoomStates.Prestaging, context.Store.GetRoom(2)?.State);
        Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Empty(context.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Absent_assignment_prestaging_after_hours_expiration_preserves_truthful_abort_history()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 3, 19, 0, 0, TimeSpan.Zero);
        var seed = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);
        seed.Repository.SaveRooms(
            [
                new RoomState(1)
                {
                    State = RoomStates.Prestaging,
                    PrestageStartedAt = now.AddMinutes(-30)
                }
            ],
            seed.Doctors,
            seed.Procedures);

        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: new ManualTimeProvider(now),
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "19:00",
                TimeZone = "UTC"
            });

        Assert.Equal([1], context.Store.TryRunAfterHoursSweep());
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Null(aborted.AssignedDoctor);
        Assert.Null(aborted.ProcedureCode);
        Assert.Null(aborted.SeatedAt);
        Assert.Empty(context.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Manual_mark_as_exception_moves_cycle_from_normal_to_exceptions()
    {
        // The admin marks a completed cycle as ManualReview - it should disappear from
        // normal metrics and appear in ExceptionCycles with the default reason/action.
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        var arrived = context.Store.MarkDoctorArrived(1);
        Assert.NotNull(arrived);
        var seatedAt = arrived.SeatedAt!.Value;

        // Before marking: appears in normal metrics.
        Assert.Single(context.Store.GetReports().DoctorSummaries);
        Assert.Empty(context.Store.GetReports().ExceptionCycles);

        var marked = context.Store.MarkCycleAsException(1, seatedAt, ExceptionReasons.ManualReview, "Exclude from normal metrics");
        Assert.True(marked);

        var reports = context.Store.GetReports();
        Assert.Equal(0, reports.CompletedRoomCyclesCount);
        Assert.Empty(reports.RecentCompletedCycles);
        Assert.Empty(reports.DoctorSummaries);

        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.Equal(1, exception.RoomId);
        Assert.Equal(HistoricalManualReviewReasons.OtherNeedsReview, exception.ExceptionReason);
        Assert.Null(exception.SuggestedAction);
        Assert.Equal(ReviewStatuses.PendingReview, exception.ReviewStatus);
    }

    [Fact]
    public void Active_room_under_max_duration_is_not_expired()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));

        // Advance 7.5 hours - still under the 8-hour limit.
        clock.SetUtcNow(now.AddHours(7).AddMinutes(30));
        var expired = context.Store.CheckAndExpireActiveCycles();

        Assert.Empty(expired);
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(1)?.State);
        Assert.Equal(0, context.Store.GetReports().CompletedRoomCyclesCount);
        Assert.Empty(context.Store.GetReports().ExceptionCycles);
    }

    [Fact]
    public void Active_room_over_max_duration_without_doctor_arrived_is_expired_as_ExceededMaxActiveDuration()
    {
        // Room never reached DoctorArrived - should produce SuggestedAction "Exclude abandoned cycle"
        // and DoctorArrivedAt should be null on the resulting exception cycle.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        var active = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        // Advance past 8-hour limit.
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        var expired = context.Store.CheckAndExpireActiveCycles();

        Assert.Equal([1], expired);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(active.EpisodeId, aborted.EpisodeId);
        Assert.Equal(active.PrestageStartedAt, aborted.PrestageStartedAt);
        Assert.Equal(TerminationKinds.MaxDurationExpired, aborted.TerminationKind);
        Assert.Equal(0, context.Store.GetReports().CompletedRoomCyclesCount);
        Assert.Empty(context.Store.GetReports().ExceptionCycles);
        Assert.Empty(context.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Active_room_over_max_duration_with_doctor_arrived_is_expired_with_review_timing_suggestion()
    {
        // Room reached DoctorArrived - post-arrival expiration releases the room and records a
        // review-required exception cycle with SuggestedAction "Review timing". It must not fabricate
        // DoctorCompleteAt and must not create aborted pre-arrival history.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));

        // Advance past 8-hour limit.
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        var expired = context.Store.CheckAndExpireActiveCycles();

        Assert.Equal([1], expired);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        var reports = context.Store.GetReports();
        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.Equal(1, exception.RoomId);
        Assert.Equal(ExceptionReasons.ExceededMaxActiveDuration, exception.ExceptionReason);
        Assert.Equal("Review timing", exception.SuggestedAction);
        Assert.NotNull(exception.DoctorArrivedAt);
        Assert.Null(exception.DoctorCompleteAt);

        // Post-arrival expiration is not throughput and not aborted pre-arrival history.
        Assert.Equal(0, reports.CompletedRoomCyclesCount);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
    }

    [Fact]
    public void After_hours_sweep_expires_active_rooms_as_AfterHoursSweep()
    {
        using var workspace = TestWorkspace.Create();
        // Use UTC timezone and a clock set to exactly 19:00 UTC.
        var now = new DateTimeOffset(2026, 6, 9, 19, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "19:00",
                TimeZone = "UTC"
            });

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "pledger", "EXT"));
        var activeRooms = context.Repository.LoadRooms(3)
            .Where(room => room.RoomId is 1 or 2)
            .ToDictionary(room => room.RoomId);

        var expired = context.Store.TryRunAfterHoursSweep();

        Assert.Equal(2, expired.Count);
        Assert.Contains(1, expired);
        Assert.Contains(2, expired);

        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(2)?.State);

        var aborted = context.Repository.LoadAbortedAssignments();
        Assert.Equal(2, aborted.Count);
        Assert.All(aborted, record =>
        {
            Assert.Equal(TerminationKinds.AfterHoursExpired, record.TerminationKind);
            Assert.Equal(activeRooms[record.RoomId].EpisodeId, record.EpisodeId);
            Assert.Equal(activeRooms[record.RoomId].PrestageStartedAt, record.PrestageStartedAt);
        });
        Assert.Empty(context.Repository.LoadCompletedCycles());
        Assert.Empty(context.Store.GetReports().ExceptionCycles);
        Assert.Equal(0, context.Store.GetReports().CompletedRoomCyclesCount);
    }

    [Fact]
    public void Legacy_seated_room_with_null_episode_and_prestage_expires_safely()
    {
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 7, 3, 18, 0, 0, TimeSpan.Zero);
        var seatedAt = now.AddHours(-9);
        var seed = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);
        seed.Repository.SaveRooms(
            [
                new RoomState(1)
                {
                    AssignedDoctor = "otte",
                    ProcedureCode = "CON",
                    State = RoomStates.Seated,
                    SeatedAt = seatedAt,
                    OriginalDefaultExpectedUnits = 1,
                    ExpectedAllocationUnits = 1,
                    ExpectedAllocationMinutes = 10
                }
            ],
            seed.Doctors,
            seed.Procedures);

        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.Equal([1], context.Store.CheckAndExpireActiveCycles());
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.NotNull(aborted.EpisodeId);
        Assert.Null(aborted.PrestageStartedAt);
        Assert.Equal(seatedAt, aborted.SeatedAt);
        Assert.Equal(TerminationKinds.MaxDurationExpired, aborted.TerminationKind);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        Assert.Empty(context.Repository.LoadCompletedCycles());
    }

    [Fact]
    public void Sweep_runs_once_per_clinic_day_and_does_not_create_duplicate_exceptions()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 19, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var expirationOptions = new RoomExpirationOptions
        {
            Enabled = true,
            AfterHoursSweepEnabled = true,
            AfterHoursSweepTime = "19:00",
            TimeZone = "UTC"
        };
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: expirationOptions);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));

        // First sweep: expires room 1.
        var firstSweep = context.Store.TryRunAfterHoursSweep();
        Assert.Equal([1], firstSweep);

        // Re-seat room 1 (simulate activity resuming).
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));

        // Second sweep on the same clinic day (even 10 minutes later): should not fire.
        clock.SetUtcNow(now.AddMinutes(10));
        var secondSweep = context.Store.TryRunAfterHoursSweep();
        Assert.Empty(secondSweep);

        // Only the one aborted pre-arrival episode from the first sweep should exist.
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(TerminationKinds.AfterHoursExpired, aborted.TerminationKind);
    }

    [Fact]
    public void Invalid_timezone_does_not_run_after_hours_sweep()
    {
        // A misconfigured timezone must not silently become UTC and fire the sweep
        // at the wrong local time. The sweep must be suppressed entirely (fail closed).
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 23, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "19:00",
                TimeZone = "Not/A/Valid/TimeZone"
            });

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));

        var expired = context.Store.TryRunAfterHoursSweep();

        Assert.Empty(expired);
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(1)?.State);
        Assert.Empty(context.Store.GetReports().ExceptionCycles);
    }

    [Fact]
    public void Invalid_timezone_does_not_throw()
    {
        // TryRunAfterHoursSweep must silently no-op on a bad timezone - never throw.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 23, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "19:00",
                TimeZone = "Not/A/Valid/TimeZone"
            });

        var ex = Record.Exception(() => context.Store.TryRunAfterHoursSweep());
        Assert.Null(ex);
    }

    [Fact]
    public void Max_active_duration_expiration_still_works_with_invalid_timezone()
    {
        // CheckAndExpireActiveCycles uses UTC wall-clock only - invalid TimeZone must not affect it.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "19:00",
                TimeZone = "Not/A/Valid/TimeZone"
            });

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));

        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        var expired = context.Store.CheckAndExpireActiveCycles();

        Assert.Equal([1], expired);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(TerminationKinds.MaxDurationExpired, aborted.TerminationKind);
    }

    [Fact]
    public void After_hours_sweep_runs_with_valid_IANA_timezone()
    {
        // "America/Chicago" is CDT (UTC-5) in June. Setting the clock to
        // 2026-06-10 00:30 UTC places clinic local time at 2026-06-09 19:30 CDT,
        // which is past the 19:00 sweep threshold on clinic day June 9.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 10, 0, 30, 0, TimeSpan.Zero); // 19:30 CDT
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "19:00",
                TimeZone = "America/Chicago"
            });

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));

        var expired = context.Store.TryRunAfterHoursSweep();

        Assert.Equal([1], expired);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(TerminationKinds.AfterHoursExpired, aborted.TerminationKind);
    }

    [Fact]
    public void Available_rooms_are_not_affected_by_sweep_or_max_duration_check()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 19, 5, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 1,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "19:00",
                TimeZone = "UTC"
            });

        // All rooms start Available - nothing to expire.
        var sweepExpired = context.Store.TryRunAfterHoursSweep();
        var maxExpired = context.Store.CheckAndExpireActiveCycles();

        Assert.Empty(sweepExpired);
        Assert.Empty(maxExpired);
        Assert.Equal(0, context.Store.GetReports().CompletedRoomCyclesCount);
        Assert.Empty(context.Store.GetReports().ExceptionCycles);

        // Rooms remain available.
        Assert.All(context.Store.GetSnapshot().Rooms, room =>
            Assert.Equal(RoomStates.Available, room.State));
    }

    [Fact]
    public void Expired_active_cycles_do_not_manufacture_doctor_complete_at()
    {
        // Post-arrival expiration releases the room and records the review-required exception cycle,
        // but must NEVER set DoctorCompleteAt (Doctor Complete was never called).
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        // Note: MarkDoctorComplete is intentionally NOT called.

        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        Assert.Equal([1], context.Store.CheckAndExpireActiveCycles());

        // Room is released, not stranded in DoctorInRoom.
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        var exception = Assert.Single(context.Store.GetReports().ExceptionCycles);
        Assert.Null(exception.DoctorCompleteAt);
    }

    [Fact]
    public void After_hours_sweep_expires_arrived_room_as_review_required_exception_cycle()
    {
        // The after-hours sweep must handle an already-arrived room the same way as the max-duration
        // check: release it and record a review-required exception cycle, without fabricating
        // DoctorCompleteAt or aborted pre-arrival history.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 19, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                AfterHoursSweepEnabled = true,
                AfterHoursSweepTime = "19:00",
                TimeZone = "UTC"
            });

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));

        var expired = context.Store.TryRunAfterHoursSweep();

        Assert.Equal([1], expired);
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        var exception = Assert.Single(context.Store.GetReports().ExceptionCycles);
        Assert.Equal(1, exception.RoomId);
        Assert.Equal(ExceptionReasons.AfterHoursSweep, exception.ExceptionReason);
        Assert.Equal("Review timing", exception.SuggestedAction);
        Assert.NotNull(exception.DoctorArrivedAt);
        Assert.Null(exception.DoctorCompleteAt);
        Assert.Empty(context.Repository.LoadAbortedAssignments());
    }

    [Fact]
    public void Pre_arrival_seated_room_over_max_duration_expires_into_aborted_history_not_throughput()
    {
        // Seated (In Prep) but never marked Ready or Arrived: pre-arrival expiration must record
        // aborted history and create no completed/exception throughput cycle.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        var active = context.Repository.LoadRooms(3).Single(room => room.RoomId == 1);
        Assert.Equal(RoomStates.Seated, context.Store.GetRoom(1)?.State);

        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        Assert.Equal([1], context.Store.CheckAndExpireActiveCycles());

        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
        var aborted = Assert.Single(context.Repository.LoadAbortedAssignments());
        Assert.Equal(active.EpisodeId, aborted.EpisodeId);
        Assert.Equal(TerminationKinds.MaxDurationExpired, aborted.TerminationKind);

        // No throughput: no completed or exception cycles.
        Assert.Empty(context.Repository.LoadCompletedCycles());
        Assert.Equal(0, context.Store.GetReports().CompletedRoomCyclesCount);
        Assert.Empty(context.Store.GetReports().ExceptionCycles);
    }

    [Fact]
    public void Post_arrival_expiration_persists_review_required_exception_cycle_across_reload()
    {
        // Durable-before-live: after post-arrival expiration a fresh store on the same database must
        // observe the released room and the persisted review-required exception cycle, with a truthful
        // DoctorArrivedAt and no fabricated DoctorCompleteAt.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        Assert.Equal([1], context.Store.CheckAndExpireActiveCycles());

        var reloaded = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock);
        Assert.Equal(RoomStates.Available, reloaded.Store.GetRoom(1)?.State);
        var exception = Assert.Single(reloaded.Store.GetReports().ExceptionCycles);
        Assert.Equal(1, exception.RoomId);
        Assert.Equal("Review timing", exception.SuggestedAction);
        Assert.NotNull(exception.DoctorArrivedAt);
        Assert.Null(exception.DoctorCompleteAt);
    }

    [Fact]
    public void Expired_exception_cycles_are_excluded_from_normal_metrics()
    {
        // Normal completed cycle (room 1) + post-arrival force-expired exception cycle (room 2):
        // only the normal cycle contributes to throughput/metrics; the review-required exception is
        // excluded. Post-arrival expiration preserves DoctorArrivedAt and never fabricates
        // DoctorCompleteAt or aborted pre-arrival history.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        // Room 1: completes the full lifecycle - normal cycle.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        // Room 2: reaches Doctor Arrived, then gets force-expired - review-required exception cycle.
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "pledger", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2));
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        Assert.Equal([2], context.Store.CheckAndExpireActiveCycles());

        // Room 2 is released.
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(2)?.State);

        var reports = context.Store.GetReports();
        // Normal throughput/metric population excludes the exception cycle.
        Assert.Equal(1, reports.CompletedRoomCyclesCount);
        Assert.Single(reports.RecentCompletedCycles);
        Assert.Equal(1, reports.RecentCompletedCycles[0].RoomId);
        Assert.DoesNotContain(reports.RecentCompletedCycles, cycle => cycle.RoomId == 2);

        // The exception cycle exists for room 2, preserving DoctorArrivedAt and no DoctorCompleteAt.
        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.Equal(2, exception.RoomId);
        Assert.NotNull(exception.DoctorArrivedAt);
        Assert.Null(exception.DoctorCompleteAt);

        // Post-arrival expiration records no aborted pre-arrival history.
        Assert.Empty(context.Repository.LoadAbortedAssignments());
    }

    [Fact]
    public void Expired_exception_cycles_appear_in_exceptions_requiring_review()
    {
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        // Room reaches Doctor Arrived, then is force-expired past the max active duration.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        Assert.Equal([1], context.Store.CheckAndExpireActiveCycles());

        // Room is released.
        Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);

        var reports = context.Store.GetReports();
        // Excluded from the normal completed population...
        Assert.Empty(reports.RecentCompletedCycles);
        // ...and present in the pending-review exceptions population.
        var exception = Assert.Single(reports.ExceptionCycles);
        Assert.Equal(1, exception.RoomId);
        Assert.True(exception.RequiresReview);
        Assert.Equal(ReviewStatuses.PendingReview, exception.ReviewStatus);
        Assert.Equal(ExceptionReasons.ExceededMaxActiveDuration, exception.ExceptionReason);
        Assert.Equal("Review timing", exception.SuggestedAction);
        Assert.NotNull(exception.DoctorArrivedAt);
        Assert.Null(exception.DoctorCompleteAt);

        // Post-arrival expiration records no aborted pre-arrival history.
        Assert.Empty(context.Repository.LoadAbortedAssignments());
    }

    [Fact]
    public void Persistence_restart_does_not_resurrect_expired_active_rooms()
    {
        // After force-expiry the room must persist as Available; a fresh store reload
        // must not re-activate it.
        using var workspace = TestWorkspace.Create();
        var databasePath = workspace.ProductionDatabasePath();
        var now = new DateTimeOffset(2026, 6, 9, 10, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);

        var first = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath,
            timeProvider: clock,
            expirationOptions: new RoomExpirationOptions
            {
                Enabled = true,
                MaxActiveDurationHours = 8
            });

        Assert.NotNull(SeatViaPrestage(first.Store, 1, "otte", "CON"));

        clock.SetUtcNow(now.AddHours(8).AddSeconds(1));
        var expired = first.Store.CheckAndExpireActiveCycles();
        Assert.Equal([1], expired);

        // Verify in-memory: room available, aborted pre-arrival history recorded.
        Assert.Equal(RoomStates.Available, first.Store.GetRoom(1)?.State);
        Assert.Single(first.Repository.LoadAbortedAssignments());

        // Reload from DB: room must still be Available, aborted history preserved.
        var second = StoreContext.Create(
            workspace,
            environmentName: Environments.Production,
            databasePath: databasePath);

        Assert.Equal(RoomStates.Available, second.Store.GetRoom(1)?.State);
        var aborted = Assert.Single(second.Repository.LoadAbortedAssignments());
        Assert.Equal(1, aborted.RoomId);
        Assert.Equal(TerminationKinds.MaxDurationExpired, aborted.TerminationKind);
    }

    // -------------------------------------------------------------------------
    // Doctor-occupied wait and doctor-available wait reporting
    // -------------------------------------------------------------------------

    [Fact]
    public void DoctorOccupiedWait_no_same_doctor_overlap()
    {
        // No other same-doctor cycle is in-room during this cycle's ready window.
        // Expected: occupiedWait = 0, availableWait = readyToDoctorSeconds.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        clock.SetUtcNow(now.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(now.AddMinutes(15));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(now.AddMinutes(20));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        Assert.Equal(10 * 60, cycle.ReadyToDoctorSeconds);
        Assert.Equal(0, cycle.DoctorOccupiedWaitSeconds);
        Assert.Equal(10 * 60, cycle.DoctorAvailableWaitSeconds);
    }

    [Fact]
    public void DoctorOccupiedWait_full_same_doctor_overlap()
    {
        // The same doctor is in another room for the entire ready window.
        // Expected: occupiedWait = readyToDoctorSeconds, availableWait = 0.
        using var workspace = TestWorkspace.Create();
        var base_ = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(base_);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Room 2 (same doctor): arrives at t=0, completes at t=20.
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2)); // DoctorArrivedAt = base_+0

        // Room 1 (target): ready at t=5, arrives at t=20.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        clock.SetUtcNow(base_.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1)); // ReadyForDoctorAt = base_+5

        clock.SetUtcNow(base_.AddMinutes(20));
        Assert.NotNull(context.Store.MarkDoctorComplete(2)); // DoctorCompleteAt = base_+20
        Assert.NotNull(context.Store.MarkRoomAvailable(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(1)); // DoctorArrivedAt = base_+20, readyToDoctor=15min=900s

        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(base_.AddMinutes(25));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var reports = context.Store.GetReports();
        var cycle = reports.RecentCompletedCycles.Single(c => c.RoomId == 1);
        Assert.Equal(15 * 60, cycle.ReadyToDoctorSeconds); // 15 min = 900s
        Assert.Equal(15 * 60, cycle.DoctorOccupiedWaitSeconds); // fully covered by Room 2's interval
        Assert.Equal(0, cycle.DoctorAvailableWaitSeconds);
    }

    [Fact]
    public void DoctorOccupiedWait_partial_same_doctor_overlap()
    {
        // The same doctor is in another room for only part of the ready window.
        // Room 2 (blocker): DoctorArrivedAt=t+0, DoctorCompleteAt=t+10
        // Room 1 (target):  ReadyForDoctorAt=t+5, DoctorArrivedAt=t+15 => readyToDoctor=600s
        // Overlap: t+5 to t+10 = 5 min = 300s
        // Available: 600 - 300 = 300s
        using var workspace = TestWorkspace.Create();
        var base_ = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(base_);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Room 2 (same doctor, blocker): arrives at t=0.
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2)); // DoctorArrivedAt = base_

        // Room 1 (target): seat now, ready at t=5.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        clock.SetUtcNow(base_.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1)); // ReadyForDoctorAt = base_+5

        // Room 2 completes at t=10.
        clock.SetUtcNow(base_.AddMinutes(10));
        Assert.NotNull(context.Store.MarkDoctorComplete(2)); // DoctorCompleteAt = base_+10
        Assert.NotNull(context.Store.MarkRoomAvailable(2));

        // Room 1 arrives at t=15 => ReadyToDoctorSeconds = 10 min = 600s.
        clock.SetUtcNow(base_.AddMinutes(15));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(base_.AddMinutes(20));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = context.Store.GetReports().RecentCompletedCycles.Single(c => c.RoomId == 1);
        Assert.Equal(10 * 60, cycle.ReadyToDoctorSeconds);
        Assert.Equal(5 * 60, cycle.DoctorOccupiedWaitSeconds);
        Assert.Equal(5 * 60, cycle.DoctorAvailableWaitSeconds);
    }

    [Fact]
    public void DoctorOccupiedWait_different_doctor_does_not_block()
    {
        // Another doctor being in-room must not affect this cycle's occupied wait.
        using var workspace = TestWorkspace.Create();
        var base_ = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(base_);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Room 2 (different doctor otte): arrives at t=0, completes at t=20.
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2));

        // Room 1 (pledger): seat, ready at t=5, arrive at t=20.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "pledger", "EXT"));
        clock.SetUtcNow(base_.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        clock.SetUtcNow(base_.AddMinutes(20));
        Assert.NotNull(context.Store.MarkDoctorComplete(2));
        Assert.NotNull(context.Store.MarkRoomAvailable(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(1)); // readyToDoctor = 15 min = 900s

        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(base_.AddMinutes(25));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = context.Store.GetReports().RecentCompletedCycles.Single(c => c.RoomId == 1);
        Assert.Equal(15 * 60, cycle.ReadyToDoctorSeconds);
        Assert.Equal(0, cycle.DoctorOccupiedWaitSeconds);
        Assert.Equal(15 * 60, cycle.DoctorAvailableWaitSeconds);
    }

    [Fact]
    public void DoctorOccupiedWait_same_cycle_does_not_self_block()
    {
        // A cycle's own DoctorArrivedAt->DoctorCompleteAt interval must not reduce
        // its own ReadyForDoctorAt->DoctorArrivedAt occupied wait.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        clock.SetUtcNow(now.AddMinutes(10));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(now.AddMinutes(20));
        Assert.NotNull(context.Store.MarkDoctorArrived(1)); // readyToDoctor = 10 min = 600s
        clock.SetUtcNow(now.AddMinutes(30));
        Assert.NotNull(context.Store.MarkDoctorComplete(1)); // DoctorCompleteAt = now+30
        clock.SetUtcNow(now.AddMinutes(35));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = Assert.Single(context.Store.GetReports().RecentCompletedCycles);
        // Own arrived->complete window (t+20 to t+30) overlaps with ready window
        // (t+10 to t+20) by 0 seconds - no overlap since they are adjacent, not overlapping.
        // Even if there were overlap, self-exclusion must prevent it counting.
        Assert.Equal(10 * 60, cycle.ReadyToDoctorSeconds);
        Assert.Equal(0, cycle.DoctorOccupiedWaitSeconds);
        Assert.Equal(10 * 60, cycle.DoctorAvailableWaitSeconds);
    }

    [Fact]
    public void DoctorOccupiedWait_exception_cycle_excluded_from_normal_metrics()
    {
        // An exception cycle must not appear in normal aggregate metrics including
        // the new averageDoctorAvailableWaitSeconds aggregate.
        using var workspace = TestWorkspace.Create();
        var now = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Normal cycle: Room 1, 10 min ready-to-doctor, no blocker.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        clock.SetUtcNow(now.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(now.AddMinutes(15));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(now.AddMinutes(20));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        // Exception cycle: Room 2, same doctor, mark as exception.
        clock.SetUtcNow(now.AddMinutes(25));
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "CON"));
        clock.SetUtcNow(now.AddMinutes(30));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        clock.SetUtcNow(now.AddMinutes(35));
        var arrived2 = context.Store.MarkDoctorArrived(2);
        Assert.NotNull(arrived2);
        context.Store.MarkCycleAsException(2, arrived2.SeatedAt!.Value, "Test", "Exclude");

        var reports = context.Store.GetReports();

        // Normal cycle metrics must not include the exception cycle.
        Assert.Equal(1, reports.CompletedRoomCyclesCount);
        // Average available wait should equal the single normal cycle's available wait (600s = 0 occupied).
        Assert.Equal(10 * 60, reports.AverageDoctorAvailableWaitSeconds);
        Assert.Equal(10 * 60, reports.MedianDoctorAvailableWaitSeconds);
    }

    [Fact]
    public void DoctorOccupiedWait_exception_cycle_not_used_as_blocker_interval()
    {
        // If the only same-doctor occupied interval belongs to an exception cycle,
        // doctorOccupiedWaitSeconds must remain 0 for the normal cycle.
        using var workspace = TestWorkspace.Create();
        var base_ = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(base_);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock);

        // Room 2 (same doctor): will be marked as exception.
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(2));   // DoctorArrivedAt = base_
        clock.SetUtcNow(base_.AddMinutes(20));
        Assert.NotNull(context.Store.MarkDoctorComplete(2));  // DoctorCompleteAt = base_+20
        Assert.NotNull(context.Store.MarkRoomAvailable(2));
        // Mark Room 2's cycle as an exception - it must not serve as a blocker.
        var seatedAt2 = context.Store.GetReports().ExceptionCycles
            .Concat(context.Store.GetReports().RecentCompletedCycles)
            .First(c => c.RoomId == 2).SeatedAt;
        context.Store.MarkCycleAsException(2, seatedAt2, "Test", "Exclude");

        // Room 1 (normal): ready at t=5, arrives at t=20.
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        clock.SetUtcNow(base_.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(base_.AddMinutes(20));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(base_.AddMinutes(25));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        var cycle = context.Store.GetReports().RecentCompletedCycles.Single(c => c.RoomId == 1);
        Assert.Equal(15 * 60, cycle.ReadyToDoctorSeconds);
        Assert.Equal(0, cycle.DoctorOccupiedWaitSeconds); // exception cycle must not be a blocker
        Assert.Equal(15 * 60, cycle.DoctorAvailableWaitSeconds);
    }

    [Fact]
    public void DoctorOccupiedWait_aggregate_average_and_median_use_available_wait()
    {
        // Verify averageDoctorAvailableWaitSeconds and medianDoctorAvailableWaitSeconds
        // are computed from doctorAvailableWaitSeconds, not raw readyToDoctorSeconds.
        // Two normal cycles: one fully blocked (availableWait=0), one unblocked (availableWait=600s).
        // Average = 300s, Median = 300s.
        using var workspace = TestWorkspace.Create();
        var base_ = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(base_);
        var context = StoreContext.Create(workspace, environmentName: Environments.Production, timeProvider: clock, roomCount: 4);

        // === Cycle A: Room 1 (pledger), no blocker, readyToDoctor = 10 min = 600s, available = 600s ===
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "pledger", "CON"));
        clock.SetUtcNow(base_.AddMinutes(5));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        clock.SetUtcNow(base_.AddMinutes(15));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
        clock.SetUtcNow(base_.AddMinutes(20));
        Assert.NotNull(context.Store.MarkRoomAvailable(1));

        // === Cycle B: Room 2 (otte), fully blocked by Room 3 ===
        // Room 3 (otte, blocker): arrives at t=20, completes at t=40.
        clock.SetUtcNow(base_.AddMinutes(20));
        Assert.NotNull(SeatViaPrestage(context.Store, 3, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(3));
        Assert.NotNull(context.Store.MarkDoctorArrived(3)); // DoctorArrivedAt = base_+20

        // Room 2 (otte, target): ready at t=25.
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "CON"));
        clock.SetUtcNow(base_.AddMinutes(25));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        clock.SetUtcNow(base_.AddMinutes(40));
        Assert.NotNull(context.Store.MarkDoctorComplete(3)); // DoctorCompleteAt = base_+40
        Assert.NotNull(context.Store.MarkRoomAvailable(3));
        Assert.NotNull(context.Store.MarkDoctorArrived(2)); // readyToDoctor = 15 min = 900s
        Assert.NotNull(context.Store.MarkDoctorComplete(2));
        clock.SetUtcNow(base_.AddMinutes(45));
        Assert.NotNull(context.Store.MarkRoomAvailable(2));

        var reports = context.Store.GetReports();
        Assert.Equal(3, reports.CompletedRoomCyclesCount); // Room 1, 2, 3

        var cycleA = reports.RecentCompletedCycles.Single(c => c.RoomId == 1);
        Assert.Equal(10 * 60, cycleA.ReadyToDoctorSeconds);
        Assert.Equal(0, cycleA.DoctorOccupiedWaitSeconds);
        Assert.Equal(10 * 60, cycleA.DoctorAvailableWaitSeconds);

        var cycleB = reports.RecentCompletedCycles.Single(c => c.RoomId == 2);
        Assert.Equal(15 * 60, cycleB.ReadyToDoctorSeconds);
        Assert.Equal(15 * 60, cycleB.DoctorOccupiedWaitSeconds);
        Assert.Equal(0, cycleB.DoctorAvailableWaitSeconds);

        // Aggregates across all 3 cycles (Room 3 has readyToDoctor but no blocker).
        // Room 1: available=600, Room 2: available=0, Room 3: available=readyToDoctorSeconds of Room 3.
        // The test focuses on confirming the aggregate is not just raw readyToDoctor.
        // Room 2's available (0) differs from its readyToDoctor (900) - confirming the metric
        // reflects occupied-adjusted wait, not raw wait.
        Assert.True(reports.AverageDoctorAvailableWaitSeconds < reports.AverageReadyToDoctorSeconds,
            "Average doctor-available wait must be lower than average ready-to-doctor when blocking occurred.");
    }

    // -------------------------------------------------------------------------
    // Doctor-arrival conflict guard
    // -------------------------------------------------------------------------

    [Fact]
    public void DoctorArrived_succeeds_when_doctor_not_in_another_room()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        var result = context.Store.TryMarkDoctorArrived(1);

        Assert.Equal(DoctorArrivalOutcome.Arrived, result.Outcome);
        Assert.NotNull(result.Status);
        Assert.Equal(RoomStates.DoctorInRoom, result.Status!.State);
        Assert.Null(result.Conflict);
    }

    [Fact]
    public void DoctorArrived_is_rejected_when_same_doctor_already_in_another_room()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Room 1: same doctor checked in.
        DriveRoomToDoctorInRoom(context, 1, "otte", "CON");

        // Room 2: same doctor, ready for doctor.
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        var result = context.Store.TryMarkDoctorArrived(2);

        Assert.Equal(DoctorArrivalOutcome.Conflict, result.Outcome);
        Assert.Null(result.Status);
        // Room 2 must remain ready-for-doctor; it was not checked in.
        Assert.Equal(RoomStates.ReadyForDoctor, context.Store.GetRoom(2)!.State);
    }

    [Fact]
    public void DoctorArrived_conflict_includes_room_and_doctor_context()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        DriveRoomToDoctorInRoom(context, 1, "otte", "CON");
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        var conflict = context.Store.TryMarkDoctorArrived(2).Conflict;

        Assert.NotNull(conflict);
        Assert.Equal(1, conflict!.ConflictingRoomId);
        Assert.Equal("otte", conflict.DoctorId);
        Assert.False(string.IsNullOrWhiteSpace(conflict.DoctorDisplayName));
    }

    [Fact]
    public void DoctorArrived_is_not_blocked_by_a_different_doctor_in_another_room()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Room 1: a different doctor is checked in.
        DriveRoomToDoctorInRoom(context, 1, "otte", "CON");

        // Room 2: pledger is ready and must not be blocked by otte.
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "pledger", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        var result = context.Store.TryMarkDoctorArrived(2);

        Assert.Equal(DoctorArrivalOutcome.Arrived, result.Outcome);
        Assert.Equal(RoomStates.DoctorInRoom, context.Store.GetRoom(2)!.State);
    }

    [Fact]
    public void DoctorArrived_is_not_blocked_when_same_doctor_is_not_in_room()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Room 1: same doctor but only ready-for-doctor (not checked in).
        Assert.NotNull(SeatViaPrestage(context.Store, 1, "otte", "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));

        // Room 2: same doctor, ready.
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        var result = context.Store.TryMarkDoctorArrived(2);

        Assert.Equal(DoctorArrivalOutcome.Arrived, result.Outcome);
        Assert.Equal(RoomStates.DoctorInRoom, context.Store.GetRoom(2)!.State);
    }

    [Fact]
    public void Resolve_completes_old_room_and_arrives_new_room_without_marking_available()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        DriveRoomToDoctorInRoom(context, 1, "otte", "CON");
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        var result = context.Store.ResolveDoctorArrivalConflict(2, 1);

        Assert.Equal(DoctorArrivalOutcome.Arrived, result.Outcome);

        // Old room: Doctor Complete -> TURNOVER, with a complete timestamp but NOT available.
        var oldRoom = context.Store.GetRoom(1)!;
        Assert.Equal(RoomStates.Turnover, oldRoom.State);
        Assert.NotNull(oldRoom.DoctorCompleteAt);
        Assert.Null(oldRoom.RoomAvailableAt);

        // New room: now doctor-in-room.
        var newRoom = context.Store.GetRoom(2)!;
        Assert.Equal(RoomStates.DoctorInRoom, newRoom.State);
        Assert.NotNull(newRoom.DoctorArrivedAt);
    }

    [Fact]
    public void Resolve_revalidates_and_fails_safely_when_conflict_is_stale()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        DriveRoomToDoctorInRoom(context, 1, "otte", "CON");
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        // The conflict clears before resolve runs: Room 1 is completed independently.
        Assert.NotNull(context.Store.MarkDoctorComplete(1));

        var result = context.Store.ResolveDoctorArrivalConflict(2, 1);

        Assert.Equal(DoctorArrivalOutcome.StaleConflict, result.Outcome);
        // Room 2 must NOT have been checked in by the stale resolve.
        Assert.Equal(RoomStates.ReadyForDoctor, context.Store.GetRoom(2)!.State);
    }

    [Fact]
    public void Resolve_fails_safely_when_conflicting_room_id_does_not_match()
    {
        using var workspace = TestWorkspace.Create();
        var context = StoreContext.Create(workspace, environmentName: Environments.Production);

        // Real conflict is in Room 1, but the caller claims Room 3.
        DriveRoomToDoctorInRoom(context, 1, "otte", "CON");
        Assert.NotNull(SeatViaPrestage(context.Store, 2, "otte", "EXT"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(2));

        var result = context.Store.ResolveDoctorArrivalConflict(2, 3);

        Assert.Equal(DoctorArrivalOutcome.StaleConflict, result.Outcome);
        // Neither room was mutated by the mismatched resolve.
        Assert.Equal(RoomStates.DoctorInRoom, context.Store.GetRoom(1)!.State);
        Assert.Equal(RoomStates.ReadyForDoctor, context.Store.GetRoom(2)!.State);
    }

    // Drives a room from available through to doctor-in-room with the given doctor and procedure.
}
