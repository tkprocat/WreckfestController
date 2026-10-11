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
/// <remarks>
/// Callers end sessions after the account's change is saved, and the hub checks the
/// account after adding a connection here, so every connection is either caught by the
/// check or still on the list when its sessions end.
/// </remarks>
public sealed class AdminHubConnections
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, HubCallerContext>> _byUser = new();

    public void Add(string userId, HubCallerContext connection)
    {
        lock (_lock)
        {
            if (!_byUser.TryGetValue(userId, out var connections))
            {
                _byUser[userId] = connections = new Dictionary<string, HubCallerContext>();
            }

            connections[connection.ConnectionId] = connection;
        }
    }

    public void Remove(string userId, string connectionId)
    {
        lock (_lock)
        {
            if (_byUser.TryGetValue(userId, out var connections)
                && connections.Remove(connectionId)
                && connections.Count == 0)
            {
                _byUser.Remove(userId);
            }
        }
    }

    /// <summary>Drops every admin connection of the account. Returns how many there were.</summary>
    public int EndSessionsOf(string userId)
    {
        List<HubCallerContext> ended;
        lock (_lock)
        {
            if (!_byUser.Remove(userId, out var connections))
            {
                return 0;
            }

            ended = [.. connections.Values];
        }

        // Outside the lock: aborting runs the connection's own teardown.
        foreach (var connection in ended)
        {
            connection.Abort();
        }

        return ended.Count;
    }
}
