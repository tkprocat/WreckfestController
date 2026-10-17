using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WreckfestController.Services.Auth;

namespace WreckfestController.Tests.Api;

/// <summary>
/// The fallback policy protects any endpoint without its own authorization metadata,
/// so the only way to open one up is <c>[AllowAnonymous]</c>. This pins the list of
/// endpoints allowed to do that, so a new anonymous endpoint cannot appear by accident.
/// </summary>
public class EndpointAuthorizationTests
{
    /// <summary>
    /// Every endpoint a signed-out caller may reach. Add to this deliberately, with the
    /// reason, when an endpoint must be public (/api/public/*). Signing in is the portal's.
    /// </summary>
    private static readonly HashSet<string> AnonymousAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        // The live-update hub. Signed-out clients get only the public group; ServerHub
        // adds the admin group (console log) only for a caller that passes Admin.
        "* /hubs/server/negotiate",
        "* /hubs/server",
        // The public home page. A dedicated DTO of public fields, and rate-limited.
        "GET /api/public/overview",
        "GET /api/public/races",
        "GET /api/public/feed",
        // All-time player standings: names and counts only, no Steam ids. Rate-limited.
        "GET /api/public/players",
        // The public track pages (#261): visible rows only, through public DTOs. Rate-limited.
        "GET /api/public/tracks",
        "GET /api/public/tracks/{key}",
        "GET /api/public/variants/{variantId}",
        // The public site's AI assistant: answers only from the public endpoints above,
        // its key stays on the server, and questions are rate-limited and capped per day.
        "GET /api/public/assistant",
        "POST /api/public/assistant/chat",
        // Catalogue menu images (#260): the game's pictures, or ones an admin chose to show. Rate-limited.
        "GET /api/images/tracks/{key}/{size}",
        "GET /api/images/variants/{variantId}/{size}",
        "GET /api/images/variants/{variantId}/layout",
    };

    [Fact]
    public async Task OnlyAllowListedEndpoints_AreAnonymous()
    {
        await using var host = await ApiTestHost.StartAsync();

        var unexpected = Endpoints(host)
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(Describe)
            .Where(e => !AnonymousAllowList.Contains(e))
            .ToList();

        Assert.Empty(unexpected);
    }

    [Fact]
    public async Task TheAllowList_MatchesRealEndpoints()
    {
        await using var host = await ApiTestHost.StartAsync();
        var all = Endpoints(host).Select(Describe).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stale = AnonymousAllowList.Where(e => !all.Contains(e)).ToList();

        Assert.Empty(stale);
    }

    // The fallback would catch these anyway; the explicit policy is what lets roles be
    // added later without touching the controllers.
    [Fact]
    public async Task EveryOtherEndpoint_RequiresTheAdminPolicy()
    {
        await using var host = await ApiTestHost.StartAsync();

        var endpoints = Endpoints(host)
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .ToList();
        Assert.NotEmpty(endpoints);

        var unprotected = endpoints
            .Where(e => !e.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(a => a.Policy == ApiAuthentication.AdminPolicy))
            .Select(Describe)
            .ToList();

        Assert.Empty(unprotected);
    }

    // What protects an endpoint that forgets its [Authorize], such as a future
    // minimal-API map or a controller added without one.
    [Fact]
    public async Task FallbackPolicy_RequiresAnAuthenticatedUser()
    {
        await using var host = await ApiTestHost.StartAsync();
        var provider = host.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var fallback = await provider.GetFallbackPolicyAsync();

        Assert.NotNull(fallback);
        Assert.Contains(
            fallback.Requirements,
            r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    private static IEnumerable<RouteEndpoint> Endpoints(ApiTestHost host) =>
        host.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>();

    private static string Describe(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
        return $"{string.Join(",", methods)} /{endpoint.RoutePattern.RawText?.TrimStart('/')}";
    }
}
