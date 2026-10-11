using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using WreckfestController.Data;
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
    private readonly SignInManager<AppUser> _signIn;

    public ServerHub(IAuthorizationService authorization, AdminHubConnections admins, SignInManager<AppUser> signIn)
    {
        _authorization = authorization;
        _admins = admins;
        _signIn = signIn;
    }

    public override async Task OnConnectedAsync()
    {
        // The policy rather than IsAuthenticated, so roles added later decide this too.
        if (Context.User is { } user
            && (await _authorization.AuthorizeAsync(user, ApiAuthentication.AdminPolicy)).Succeeded)
        {
            // An API-key caller has no account, so nothing can end its session but the key.
            if (Context.UserIdentifier is { } userId)
            {
                // The principal is the one the connection authenticated with, which can be
                // older than the handshake this runs after (#227). On the list first, then
                // checked: sessions ended before the check fail it, and sessions ended after
                // it find this connection on the list.
                _admins.Add(userId, Context);
                AppUser? valid;
                try
                {
                    valid = await _signIn.ValidateSecurityStampAsync(user);
                }
                catch
                {
                    // SignalR closes the connection without OnDisconnectedAsync when this
                    // throws: the list would keep it for good.
                    _admins.Remove(userId, Context.ConnectionId);
                    throw;
                }

                if (valid is null)
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
