using ChairSide.Board.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ChairSide.Board.Tests;

internal sealed class NoopBoardHubContext : IHubContext<BoardHub>
{
    public IHubClients Clients { get; } = new NoopHubClients();

    public IGroupManager Groups { get; } = new NoopGroupManager();
}

internal sealed class NoopHubClients : IHubClients
{
    private static readonly IClientProxy Proxy = new NoopClientProxy();

    public IClientProxy All => Proxy;

    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;

    public IClientProxy Client(string connectionId) => Proxy;

    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;

    public IClientProxy Group(string groupName) => Proxy;

    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;

    public IClientProxy Groups(IReadOnlyList<string> groupNames) => Proxy;

    public IClientProxy User(string userId) => Proxy;

    public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
}

internal sealed class NoopGroupManager : IGroupManager
{
    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class NoopClientProxy : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
