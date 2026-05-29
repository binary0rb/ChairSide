using ChairSide.Board.Services;
using Microsoft.AspNetCore.SignalR;

namespace ChairSide.Board.Hubs;

public sealed class BoardHub(DemoBoardStore store) : Hub
{
    public BoardSnapshot GetBoard() => store.GetSnapshot();

    public async Task SeatRoom(int roomId, string doctorId, string procedureCode, int demoElapsedMinutes = 0)
    {
        var result = store.SeatRoom(roomId, doctorId, procedureCode, demoElapsedMinutes);
        if (result is null)
        {
            return;
        }

        await Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    }

    public async Task UpdateAssignment(int roomId, string doctorId, string procedureCode)
    {
        var result = store.UpdateAssignment(roomId, doctorId, procedureCode);
        if (result is null)
        {
            return;
        }

        await Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    }

    public async Task CancelSeating(int roomId)
    {
        var result = store.CancelSeating(roomId);
        if (result is null)
        {
            return;
        }

        await Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    }

    public async Task DoctorArrived(int roomId)
    {
        var result = store.MarkDoctorArrived(roomId);
        if (result is null)
        {
            return;
        }

        await Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    }

    public async Task DoctorComplete(int roomId)
    {
        var result = store.MarkDoctorComplete(roomId);
        if (result is null)
        {
            return;
        }

        await Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    }

    public async Task RoomAvailable(int roomId)
    {
        var result = store.MarkRoomAvailable(roomId);
        if (result is null)
        {
            return;
        }

        await Clients.All.SendAsync("boardUpdated", store.GetSnapshot());
    }
}
