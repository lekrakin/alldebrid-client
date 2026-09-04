using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AdbClient.Service.Services;

[Authorize(Policy = "AuthSetting")]
public class AdbHub : Hub
{
    private static readonly ConcurrentDictionary<string, string> Users = new();

    public static bool HasConnections => !Users.IsEmpty;

    public override async Task OnConnectedAsync()
    {
        Users.TryAdd(Context.ConnectionId, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        Users.TryRemove(Context.ConnectionId, out _);
        await base.OnDisconnectedAsync(exception);
    }
}
