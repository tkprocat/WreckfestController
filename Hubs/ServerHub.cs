using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using WreckfestController.Services;

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

    public ServerHub(IAuthorizationService authorization)
    {
        _authorization = authorization;
    }

    public override async Task OnConnectedAsync()
    {
        // The policy rather than IsAuthenticated, so roles added later decide this too.
        if (Context.User is { } user
            && (await _authorization.AuthorizeAsync(user, ApiAuthentication.AdminPolicy)).Succeeded)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AdminGroup);
        }

        // Last, so the first public message a client receives also tells it the admin
        // decision has been made. The hub tests rely on that.
        await Groups.AddToGroupAsync(Context.ConnectionId, PublicGroup);

        await base.OnConnectedAsync();
    }
}
