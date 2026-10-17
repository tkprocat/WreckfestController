using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace WreckfestPortal.Accounts;

/// <summary>
/// What the portal tells a controller about who is calling: a short-lived token, signed with
/// that server's own <see cref="PortalServer.Secret"/> and addressed to it, minted for each
/// request the portal forwards for someone who may manage that server. The controller checks
/// it with the same secret. A secret only ever signs for one server, so a controller that
/// leaked its secret could not be used to reach another.
/// </summary>
public sealed class ControllerTokens(TimeProvider time)
{
    /// <summary>The token's issuer, which the controller expects.</summary>
    public const string Issuer = "WreckfestPortal";

    /// <summary>Long enough for one request, or to open a live-update connection.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>The shortest secret a server may have: 256 bits as characters.</summary>
    public const int MinimumSecretLength = 32;

    /// <summary>The claim with the user's display name, if they set one.</summary>
    public const string DisplayNameClaim = "display_name";

    private static readonly JsonWebTokenHandler Handler = new();

    public string Mint(PortalUser user, PortalServer server)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Name, user.UserName ?? user.Id),
        };
        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            claims.Add(new Claim(DisplayNameClaim, user.DisplayName));
        }

        return Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = server.Id,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = now + Lifetime,
            SigningCredentials = new SigningCredentials(Key(server.Secret), SecurityAlgorithms.HmacSha256),
        });
    }

    /// <summary>The signing key for a secret, as the controller builds it too.</summary>
    public static SymmetricSecurityKey Key(string secret) => new(Encoding.UTF8.GetBytes(secret));
}
