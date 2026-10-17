using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace WreckfestPortal.Accounts;

/// <summary>
/// The web accounts: an Identity cookie for the whole site, the account rules, and who may
/// do what. Every endpoint needs a signed-in user unless it says <c>[AllowAnonymous]</c>.
/// </summary>
public static class PortalAccounts
{
    /// <summary>The accounts, and the portal's settings for every server.</summary>
    public const string AdminPolicy = "GlobalAdmin";

    /// <summary>The claim a global admin's sign-in carries.</summary>
    public const string AdminClaim = "wfp:admin";

    public const string CookieName = ".WreckfestPortal.Auth";
    public const string AntiforgeryCookieName = ".WreckfestPortal.Antiforgery";

    /// <summary>The JS-readable cookie the web app copies into <see cref="XsrfHeaderName"/>.</summary>
    public const string XsrfCookieName = "XSRF-TOKEN";
    public const string XsrfHeaderName = "X-XSRF-TOKEN";

    public const int PasswordMinLength = 10;
    public const int MaxFailedSignIns = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(14);

    /// <summary>The account rules, shared by the web endpoints and <c>create-admin</c>.</summary>
    public static void ConfigureIdentity(IdentityOptions options)
    {
        options.Password.RequiredLength = PasswordMinLength;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;

        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = MaxFailedSignIns;
        options.Lockout.DefaultLockoutTimeSpan = LockoutDuration;

        options.User.RequireUniqueEmail = true;
    }

    /// <summary>The database and the account rules: what <c>create-admin</c> needs, without the web parts.</summary>
    public static IdentityBuilder AddPortalDatabase(this IServiceCollection services)
    {
        services.AddDbContext<PortalDbContext>((sp, options) =>
            options.UseSqlite($"Data Source={sp.GetRequiredService<PortalDatabase>().Path}"));
        services.AddSingleton(sp => PortalDatabase.Resolve(
            sp.GetRequiredService<IOptions<PortalOptions>>().Value,
            AppContext.BaseDirectory));
        return services.AddIdentityCore<PortalUser>(ConfigureIdentity)
            .AddEntityFrameworkStores<PortalDbContext>()
            .AddClaimsPrincipalFactory<PortalClaimsFactory>();
    }

    public static void AddPortalAccounts(this IServiceCollection services)
    {
        services.AddPortalDatabase().AddSignInManager();

        // The security stamp validator, which the cookie runs, reloads the account on every
        // request: a password change, a lock or a lost admin role takes effect at once.
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);

        // Kept beside the database, so a restart does not sign everyone out, and encrypted
        // for the Windows account the portal runs as.
        services.AddDataProtection()
            .SetApplicationName("WreckfestPortal")
            .ProtectKeysWithDpapi();
        services.AddOptions<KeyManagementOptions>().Configure<PortalDatabase, ILoggerFactory>((options, database, loggers) =>
            options.XmlRepository = new FileSystemXmlRepository(
                new DirectoryInfo(database.KeysFolder), loggers));

        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddIdentityCookies(cookies => cookies.ApplicationCookie!.Configure(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.Path = "/";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                // HTTPS arrives through the reverse proxy; loopback is plain HTTP.
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = CookieLifetime;
                options.SlidingExpiration = true;

                // An API: a status code, never a redirect to a login page.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            }));

        services.AddAntiforgery(options =>
        {
            options.HeaderName = XsrfHeaderName;
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.Path = "/";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

        services.AddAuthorizationBuilder()
            // Every endpoint without its own authorization metadata: a new one is protected
            // unless it says [AllowAnonymous].
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AdminPolicy, policy => policy.RequireAuthenticatedUser().RequireClaim(AdminClaim));
    }
}

/// <summary>Adds <see cref="PortalAccounts.AdminClaim"/> to a global admin's sign-in.</summary>
internal sealed class PortalClaimsFactory(UserManager<PortalUser> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<PortalUser>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(PortalUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.IsAdmin)
        {
            identity.AddClaim(new Claim(PortalAccounts.AdminClaim, "true"));
        }

        return identity;
    }
}
