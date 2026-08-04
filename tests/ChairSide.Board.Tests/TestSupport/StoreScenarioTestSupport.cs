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
    private static void DriveRoomToDoctorInRoom(StoreContext context, int room, string doctor, string procedure)
    {
        Assert.NotNull(SeatViaPrestage(context.Store, room, doctor, procedure));
        Assert.NotNull(context.Store.MarkReadyForDoctor(room));
        Assert.NotNull(context.Store.MarkDoctorArrived(room));
        Assert.Equal(RoomStates.DoctorInRoom, context.Store.GetRoom(room)!.State);
    }

    private static void RunProcedureCycle(
        StoreContext context,
        ManualTimeProvider clock,
        DateTimeOffset seatedAt,
        int room,
        string doctor,
        string procedure,
        int prepMin,
        int readyMin,
        int doctorMin,
        int turnoverMin,
        bool sedation = false,
        int? expectedAllocationUnits = null)
    {
        clock.SetUtcNow(seatedAt);
        Assert.NotNull(SeatViaPrestage(context.Store, room, doctor, procedure, sedation: sedation, expectedAllocationUnits: expectedAllocationUnits));
        clock.SetUtcNow(seatedAt.AddMinutes(prepMin));
        Assert.NotNull(context.Store.MarkReadyForDoctor(room));
        clock.SetUtcNow(seatedAt.AddMinutes(prepMin + readyMin));
        Assert.NotNull(context.Store.MarkDoctorArrived(room));
        clock.SetUtcNow(seatedAt.AddMinutes(prepMin + readyMin + doctorMin));
        Assert.NotNull(context.Store.MarkDoctorComplete(room));
        clock.SetUtcNow(seatedAt.AddMinutes(prepMin + readyMin + doctorMin + turnoverMin));
        Assert.NotNull(context.Store.MarkRoomAvailable(room));
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static void DriveRoomToCancellationState(
        StoreContext context,
        ManualTimeProvider clock,
        string targetState)
    {
        if (targetState == RoomStates.Available)
        {
            Assert.Equal(RoomStates.Available, context.Store.GetRoom(1)?.State);
            return;
        }

        var startedAt = clock.GetUtcNow();
        Assert.NotNull(context.Store.BeginPrestage(1, "otte", "EXT", expectedAllocationUnits: 4));
        if (targetState == RoomStates.Prestaging)
        {
            return;
        }

        Assert.NotNull(context.Store.SeatRoomCanonical(1, null).Room);
        if (targetState == RoomStates.Seated)
        {
            return;
        }

        clock.SetUtcNow(startedAt.AddMinutes(1));
        Assert.NotNull(context.Store.MarkReadyForDoctor(1));
        if (targetState == RoomStates.ReadyForDoctor)
        {
            return;
        }

        if (targetState is RoomStates.Aging or RoomStates.Stale)
        {
            clock.SetUtcNow(startedAt.AddMinutes(targetState == RoomStates.Aging ? 9 : 14));
            return;
        }

        clock.SetUtcNow(startedAt.AddMinutes(2));
        Assert.NotNull(context.Store.MarkDoctorArrived(1));
        if (targetState == RoomStates.DoctorInRoom)
        {
            return;
        }

        Assert.Equal(RoomStates.Turnover, targetState);
        clock.SetUtcNow(startedAt.AddMinutes(3));
        Assert.NotNull(context.Store.MarkDoctorComplete(1));
    }

    private static void AssertSameRoomState(RoomState expected, RoomState actual)
    {
        Assert.Equal(expected.RoomId, actual.RoomId);
        Assert.Equal(expected.EpisodeId, actual.EpisodeId);
        Assert.Equal(expected.AssignedDoctor, actual.AssignedDoctor);
        Assert.Equal(expected.AssignedDoctorDisplayName, actual.AssignedDoctorDisplayName);
        Assert.Equal(expected.ProcedureCode, actual.ProcedureCode);
        Assert.Equal(expected.ProcedureCategory, actual.ProcedureCategory);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.PrestageStartedAt, actual.PrestageStartedAt);
        Assert.Equal(expected.SeatedAt, actual.SeatedAt);
        Assert.Equal(expected.AgingStartedAt, actual.AgingStartedAt);
        Assert.Equal(expected.StaleStartedAt, actual.StaleStartedAt);
        Assert.Equal(expected.ReadyForDoctorAt, actual.ReadyForDoctorAt);
        Assert.Equal(expected.DoctorArrivedAt, actual.DoctorArrivedAt);
        Assert.Equal(expected.DoctorCompleteAt, actual.DoctorCompleteAt);
        Assert.Equal(expected.RoomAvailableAt, actual.RoomAvailableAt);
        Assert.Equal(expected.OriginalDefaultExpectedUnits, actual.OriginalDefaultExpectedUnits);
        Assert.Equal(expected.ExpectedAllocationUnits, actual.ExpectedAllocationUnits);
        Assert.Equal(expected.ExpectedAllocationMinutes, actual.ExpectedAllocationMinutes);
        Assert.Equal(expected.AllocationAdjustedFromDefault, actual.AllocationAdjustedFromDefault);
        Assert.Equal(expected.IsAddOn, actual.IsAddOn);
    }

    private static void AssertSameAbortedAssignment(AbortedRoomAssignment expected, AbortedRoomAssignment actual)
    {
        Assert.Equal(expected.AbortedAssignmentId, actual.AbortedAssignmentId);
        Assert.Equal(expected.EpisodeId, actual.EpisodeId);
        Assert.Equal(expected.RoomId, actual.RoomId);
        Assert.Equal(expected.AssignedDoctor, actual.AssignedDoctor);
        Assert.Equal(expected.AssignedDoctorDisplayName, actual.AssignedDoctorDisplayName);
        Assert.Equal(expected.ProcedureCode, actual.ProcedureCode);
        Assert.Equal(expected.ProcedureCategory, actual.ProcedureCategory);
        Assert.Equal(expected.OriginalDefaultExpectedUnits, actual.OriginalDefaultExpectedUnits);
        Assert.Equal(expected.ExpectedAllocationUnits, actual.ExpectedAllocationUnits);
        Assert.Equal(expected.ExpectedAllocationMinutes, actual.ExpectedAllocationMinutes);
        Assert.Equal(expected.AllocationAdjustedFromDefault, actual.AllocationAdjustedFromDefault);
        Assert.Equal(expected.IsAddOn, actual.IsAddOn);
        Assert.Equal(expected.PrestageStartedAt, actual.PrestageStartedAt);
        Assert.Equal(expected.SeatedAt, actual.SeatedAt);
        Assert.Equal(expected.ReadyForDoctorAt, actual.ReadyForDoctorAt);
        Assert.Equal(expected.TerminatedAt, actual.TerminatedAt);
        Assert.Equal(expected.TerminatedFromState, actual.TerminatedFromState);
        Assert.Equal(expected.TerminationKind, actual.TerminationKind);
        Assert.Equal(expected.CancellationReason, actual.CancellationReason);
    }

    private static RoomStatus? SeatViaPrestage(
        DemoBoardStore store,
        int room,
        string doctor,
        string procedure,
        bool sedation = false,
        int? expectedAllocationUnits = null)
    {
        var prestaged = store.BeginPrestage(room, doctor, procedure, sedation, expectedAllocationUnits);
        return prestaged is null
            ? null
            : store.SeatRoomCanonical(room, null).Room;
    }

    // Persists a single clean, allocation-calculable completed cycle anchored on completeAt (same UTC
    // day, no hygiene flags), for date-range tests. Reload a new StoreContext on the same DB to read it.
    private static void SaveCleanCycle(
        StoreContext context, int room, string doctor, string code, DateTimeOffset completeAt, int expectedUnits)
    {
        var seatedAt = completeAt.AddMinutes(-30);
        var cycle = new CompletedRoomCycle
        {
            RoomId = room,
            AssignedDoctor = doctor,
            ProcedureCode = code,
            SeatedAt = seatedAt,
            ReadyForDoctorAt = seatedAt.AddMinutes(5),
            DoctorArrivedAt = seatedAt.AddMinutes(15),
            DoctorCompleteAt = completeAt,
            RoomAvailableAt = completeAt.AddMinutes(5),
            SeatedToDoctorSeconds = 900,
            PrepSeconds = 300,
            ReadyToDoctorSeconds = 600,
            DoctorInRoomSeconds = 900,
            TurnoverSeconds = 300,
            TotalRoomCycleSeconds = 2100,
            FinalWaitState = "ready-for-doctor",
            OriginalDefaultExpectedUnits = expectedUnits,
            ExpectedAllocationUnits = expectedUnits,
            ExpectedAllocationMinutes = expectedUnits * 10
        };
        context.Repository.SaveCompletedCycle(cycle, context.Doctors, context.Procedures);
    }

    private static void SaveObservedCycle(
        StoreContext context,
        int room,
        string doctor,
        string code,
        DateTimeOffset seatedAt,
        DateTimeOffset readyForDoctorAt,
        DateTimeOffset doctorArrivedAt,
        DateTimeOffset doctorCompleteAt,
        DateTimeOffset roomAvailableAt,
        int expectedUnits)
    {
        var cycle = new CompletedRoomCycle
        {
            RoomId = room,
            AssignedDoctor = doctor,
            ProcedureCode = code,
            SeatedAt = seatedAt,
            ReadyForDoctorAt = readyForDoctorAt,
            DoctorArrivedAt = doctorArrivedAt,
            DoctorCompleteAt = doctorCompleteAt,
            RoomAvailableAt = roomAvailableAt,
            SeatedToDoctorSeconds = (int)(doctorArrivedAt - seatedAt).TotalSeconds,
            PrepSeconds = (int)(readyForDoctorAt - seatedAt).TotalSeconds,
            ReadyToDoctorSeconds = (int)(doctorArrivedAt - readyForDoctorAt).TotalSeconds,
            DoctorInRoomSeconds = (int)(doctorCompleteAt - doctorArrivedAt).TotalSeconds,
            TurnoverSeconds = (int)(roomAvailableAt - doctorCompleteAt).TotalSeconds,
            TotalRoomCycleSeconds = (int)(roomAvailableAt - seatedAt).TotalSeconds,
            FinalWaitState = "ready-for-doctor",
            OriginalDefaultExpectedUnits = expectedUnits,
            ExpectedAllocationUnits = expectedUnits,
            ExpectedAllocationMinutes = expectedUnits * 10
        };

        context.Repository.SaveCompletedCycle(cycle, context.Doctors, context.Procedures);
    }

    // Seats, readies, completes, and frees a single room, returning the resulting completed cycle.
    private static CompletedRoomCycle CompleteOneCycle(StoreContext context, int room, string doctor)
    {
        Assert.NotNull(SeatViaPrestage(context.Store, room, doctor, "CON"));
        Assert.NotNull(context.Store.MarkReadyForDoctor(room));
        Assert.NotNull(context.Store.MarkDoctorArrived(room));
        Assert.NotNull(context.Store.MarkDoctorComplete(room));
        Assert.NotNull(context.Store.MarkRoomAvailable(room));
        return context.Store.GetReports().RecentCompletedCycles.Single(cycle => cycle.RoomId == room);
    }

    private static AbortedRoomAssignment NewAbortedAssignment(
        string episodeId, int roomId, DateTimeOffset prestageStartedAt, DateTimeOffset terminatedAt) =>
        new()
        {
            EpisodeId = episodeId,
            RoomId = roomId,
            AssignedDoctor = "otte",
            ProcedureCode = "CON",
            OriginalDefaultExpectedUnits = 1,
            ExpectedAllocationUnits = 1,
            ExpectedAllocationMinutes = 10,
            AllocationAdjustedFromDefault = false,
            PrestageStartedAt = prestageStartedAt,
            SeatedAt = null,
            ReadyForDoctorAt = null,
            TerminatedAt = terminatedAt,
            TerminatedFromState = RoomStates.Seated,
            TerminationKind = TerminationKinds.StaffCanceled,
            CancellationReason = CancellationReasons.PatientCanceled
        };

    private static void InvokeResetRoom(RoomState room)
    {
        var method = typeof(DemoBoardStore).GetMethod(
            "ResetRoom",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        try
        {
            method.Invoke(null, [room]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

}
