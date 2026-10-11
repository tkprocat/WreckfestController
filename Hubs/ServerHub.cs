using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using WreckfestController.Services.Auth;

namespace WreckfestController.Hubs;

/// <summary>
/// Pushes server events to the web UI. Clients only listen: the hub has no methods
/// for them to call. Every connection joins <see cref="PublicGroup"/>. A caller that
/// passes the Admin policy (a signed-in cookie or the API key) also joins
/// <see cref="AdminGroup"/>, which is the only group that gets the console log.
/// </summary>
public sealed class ServerHub : Hub<IServerHubClient>
{
    public const string Route = "/hubs/server";
    public const string PublicGroup = "public";
    public const string AdminGroup = "admin";

    private readonly IAuthorizationService _authorization;
    private readonly AdminHubConnections _admins;

    public ServerHub(IAuthorizationService authorization, AdminHubConnections admins)
    {
        _authorization = authorization;
        _admins = admins;
    }

    public override async Task OnConnectedAsync()
    {
        // An account's sessions ended while this one was being let in would miss it:
        // compared again once the connection is on the list (#227).
        var userId = Context.UserIdentifier;
        var endings = userId is null ? 0 : _admins.EndingsOf(userId);

        // The policy rather than IsAuthenticated, so roles added later decide this too.
        if (Context.User is { } user
            && (await _authorization.AuthorizeAsync(user, ApiAuthentication.AdminPolicy)).Succeeded)
        {
            // An API-key caller has no account, so nothing can end its session but the key.
            if (userId is not null)
            {
                _admins.Add(userId, Context);
                if (_admins.EndingsOf(userId) != endings)
                {
                    _admins.Remove(userId, Context.ConnectionId);
                    Context.Abort();
                    return;
                }
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, AdminGroup);
        }

        // Last, so the first public message a client receives also tells it the admin
        // decision has been made. The hub tests rely on that.
        await Groups.AddToGroupAsync(Context.ConnectionId, PublicGroup);

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.UserIdentifier is { } userId)
        {
            _admins.Remove(userId, Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }
}
