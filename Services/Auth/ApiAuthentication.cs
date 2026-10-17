using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace WreckfestController.Services.Auth;

/// <summary>
/// Who may call the API: people the portal vouches for, with a short-lived token signed with
/// this server's <c>Api:Portal:Secret</c> and addressed to its <c>Api:Portal:ServerId</c>, and
/// scripts that send <c>X-Api-Key</c>. The accounts are the portal's; the controller has none.
/// Every endpoint requires one or the other unless it opts out with <c>[AllowAnonymous]</c>.
/// </summary>
public static class ApiAuthentication
{
    /// <summary>Forwards to the API key handler when the header is present, otherwise to the portal's token.</summary>
    public const string SelectorScheme = "PortalOrApiKey";

    /// <summary>The portal's token, as a bearer token.</summary>
    public const string PortalScheme = JwtBearerDefaults.AuthenticationScheme;

    /// <summary>Managing this server. Anyone the portal vouches for may: it checked their access.</summary>
    public const string AdminPolicy = "Admin";

    /// <summary>Day-to-day server control. The same people as <see cref="AdminPolicy"/>.</summary>
    public const string OperatorPolicy = "Operator";

    public const string PortalSecretKey = "Api:Portal:Secret";
    public const string PortalServerIdKey = "Api:Portal:ServerId";

    /// <summary>What the portal signs its tokens as (WreckfestPortal's ControllerTokens.Issuer).</summary>
    public const string PortalIssuer = "WreckfestPortal";

    /// <summary>The claim with the user's display name (WreckfestPortal's ControllerTokens.DisplayNameClaim).</summary>
    public const string DisplayNameClaim = "display_name";

    /// <summary>The shortest secret accepted: the portal refuses shorter ones too.</summary>
    public const int MinimumSecretLength = 32;

    /// <summary>
    /// The portal's settings, checked: both or neither. Without them only the API key signs
    /// in, which leaves the web site's admin pages unusable but scripts working.
    /// </summary>
    /// <exception cref="PortalTrustException">One is set without the other, or the secret is too short.</exception>
    public static (string Secret, string ServerId)? ResolvePortal(IConfiguration configuration)
    {
        var secret = configuration[PortalSecretKey];
        var serverId = configuration[PortalServerIdKey]?.Trim();
        if (string.IsNullOrEmpty(secret) && string.IsNullOrEmpty(serverId))
        {
            return null;
        }

        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(serverId))
        {
            throw new PortalTrustException($"Set both {PortalSecretKey} and {PortalServerIdKey}, as in the portal's settings for this server, or neither.");
        }

        if (secret.Length < MinimumSecretLength)
        {
            throw new PortalTrustException($"{PortalSecretKey} must be at least {MinimumSecretLength} characters.");
        }

        return (secret, serverId);
    }

    public static void AddApiAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        var portal = ResolvePortal(configuration);

        services.AddAuthentication(SelectorScheme)
            .AddPolicyScheme(SelectorScheme, SelectorScheme, options =>
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName)
                        ? ApiKeyAuthenticationHandler.SchemeName
                        : PortalScheme)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName,
                options => options.Key = configuration["Api:Key"])
            .AddJwtBearer(PortalScheme, options =>
            {
                // The claims as the portal wrote them: "sub" and "name", not long URIs.
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = PortalIssuer,
                    // Only tokens addressed to this server: another server's are not for here.
                    ValidAudience = portal?.ServerId,
                    // Without the portal's settings no token can be valid: a random key nobody has.
                    IssuerSigningKey = new SymmetricSecurityKey(portal is { } trusted
                        ? Encoding.UTF8.GetBytes(trusted.Secret)
                        : System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "name",
                };
            });

        services.AddAuthorizationBuilder()
            // Applies to every endpoint without its own authorization metadata, so a new
            // endpoint is protected unless it explicitly says [AllowAnonymous].
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AdminPolicy, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(OperatorPolicy, policy => policy.RequireAuthenticatedUser());
    }
}

/// <summary>Portal settings the API cannot start with; the message says why.</summary>
public sealed class PortalTrustException(string message) : Exception(message);
