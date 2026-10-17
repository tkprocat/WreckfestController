using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using WreckfestController.Services.Auth;

namespace WreckfestController.Hubs;

/// <summary>
/// Pushes server events to the web UI. Clients only listen: the hub has no methods
/// for them to call. Every connection joins <see cref="PublicGroup"/>. A caller that
/// passes the Admin policy (someone the portal vouches for, or the API key) also joins
/// <see cref="AdminGroup"/>, which is the only group that gets the console log.
/// </summary>
/// <remarks>
/// The decision is made once, when the connection opens. A connection outlives the portal's
/// token, and someone who loses access keeps an open console until it reconnects or the
/// controller restarts: accepted, since only admins can take access away.
/// </remarks>
public sealed class ServerHub(IAuthorizationService authorization) : Hub<IServerHubClient>
{
    public const string Route = "/hubs/server";
    public const string PublicGroup = "public";
    public const string AdminGroup = "admin";

    public override async Task OnConnectedAsync()
    {
        // The policy rather than IsAuthenticated, so roles added later decide this too.
        if (Context.User is { } user
            && (await authorization.AuthorizeAsync(user, ApiAuthentication.AdminPolicy)).Succeeded)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AdminGroup);
        }

        // Last, so the first public message a client receives also tells it the admin
        // decision has been made. The hub tests rely on that.
        await Groups.AddToGroupAsync(Context.ConnectionId, PublicGroup);

        await base.OnConnectedAsync();
    }
}
