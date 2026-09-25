using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WreckfestController.Services;

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
    /// reason, when an endpoint must be public (auth state, login, setup, /api/public/*).
    /// </summary>
    private static readonly HashSet<string> AnonymousAllowList = new(StringComparer.OrdinalIgnoreCase);

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
