using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace WreckfestController.Hubs;

/// <summary>
/// The open admin connections to <see cref="ServerHub"/>, by account (#227). The hub
/// decides admin membership once, when a connection opens, so locking an account, or
/// anything else that ends its sessions, would leave an open connection streaming the
/// console. <see cref="EndSessionsOf"/> drops them: a session that is still valid
/// reconnects with its cookie and is let back in; one that was ended reconnects as
/// anonymous and gets the public events only.
/// </summary>
public sealed class AdminHubConnections
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, HubCallerContext>> _byUser = new();
    // Bumped by every EndSessionsOf, so a connection that was being let in while it ran
    // can tell, and drop itself (see ServerHub.OnConnectedAsync).
    private readonly ConcurrentDictionary<string, long> _endings = new();

    public long EndingsOf(string userId) => _endings.GetValueOrDefault(userId);

    public void Add(string userId, HubCallerContext connection) =>
        _byUser.GetOrAdd(userId, _ => new ConcurrentDictionary<string, HubCallerContext>())[connection.ConnectionId] = connection;

    public void Remove(string userId, string connectionId)
    {
        if (_byUser.TryGetValue(userId, out var connections))
        {
            connections.TryRemove(connectionId, out _);
        }
    }

    /// <summary>Drops every admin connection of the account. Returns how many there were.</summary>
    public int EndSessionsOf(string userId)
    {
        _endings.AddOrUpdate(userId, 1, (_, endings) => endings + 1);
        if (!_byUser.TryGetValue(userId, out var connections))
        {
            return 0;
        }

        var ended = 0;
        foreach (var (connectionId, connection) in connections)
        {
            if (connections.TryRemove(connectionId, out _))
            {
                connection.Abort();
                ended++;
            }
        }

        return ended;
    }
}
