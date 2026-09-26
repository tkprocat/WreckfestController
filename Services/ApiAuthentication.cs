using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WreckfestController.Data;

namespace WreckfestController.Services;

/// <summary>
/// Who may call the API: browsers signed in with an Identity cookie, and scripts that
/// send <c>X-Api-Key</c>. Every endpoint requires one or the other unless it opts out
/// with <c>[AllowAnonymous]</c>.
/// </summary>
public static class ApiAuthentication
{
    /// <summary>Forwards to the API key handler when the header is present, otherwise to the cookie.</summary>
    public const string SelectorScheme = "CookieOrApiKey";

    /// <summary>Everything an operator can do. Any signed-in user in v1; roles come later.</summary>
    public const string AdminPolicy = "Admin";

    /// <summary>Day-to-day server control. Any signed-in user in v1; roles come later.</summary>
    public const string OperatorPolicy = "Operator";

    /// <summary>The JS-readable cookie the SPA copies into <see cref="XsrfHeaderName"/>.</summary>
    public const string XsrfCookieName = "XSRF-TOKEN";
    public const string XsrfHeaderName = "X-XSRF-TOKEN";

    public const int PasswordMinLength = 10;
    public const int MaxFailedSignIns = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(14);

    /// <summary>Where the cookie-encryption keys live: a <c>keys</c> folder beside the database.</summary>
    public static string KeysFolder(string databasePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? ".", "keys");

    /// <summary>
    /// The account rules. Shared by the API host and the desktop app's
    /// <see cref="AccountService"/>, so both accept and reject the same passwords.
    /// </summary>
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

    public static void AddApiAuthentication(
        this IServiceCollection services,
        IServiceProvider main,
        IConfiguration configuration)
    {
        // Identity's EF stores want a scoped context. The factory is the WPF app's, so
        // both hosts open the same database the same way.
        services.AddSingleton(main.GetRequiredService<IDbContextFactory<ControllerDbContext>>());
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<ControllerDbContext>>().CreateDbContext());

        services.TryAddSingleton(TimeProvider.System);

        services.AddIdentityCore<AppUser>(ConfigureIdentity)
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ControllerDbContext>()
            .AddSignInManager();

        // AddSignInManager registers the security stamp validator, and the application
        // cookie runs it; it is what makes a password change or an admin lock end the
        // user's other sessions.
        services.Configure<SecurityStampValidatorOptions>(options =>
            // Check on every request: a handful of admins costs one indexed lookup each,
            // and a revoked session should end now rather than in half an hour.
            options.ValidationInterval = TimeSpan.Zero);

        // Without persisted keys every restart would invalidate every cookie.
        var databasePath = main.GetRequiredService<DatabaseState>().DatabasePath;
        services.AddDataProtection()
            .SetApplicationName("WreckfestController")
            .PersistKeysToFileSystem(new DirectoryInfo(KeysFolder(databasePath)))
            .ProtectKeysWithDpapi();

        services.AddAuthentication(SelectorScheme)
            .AddPolicyScheme(SelectorScheme, SelectorScheme, options =>
                options.ForwardDefaultSelector = context =>
                    context.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName)
                        ? ApiKeyAuthenticationHandler.SchemeName
                        : IdentityConstants.ApplicationScheme)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName,
                options => options.Key = configuration["Api:Key"])
            // All four Identity cookies, not just the application one: when the security
            // stamp check fails, SignInManager signs out of every one of them.
            .AddIdentityCookies(cookies => cookies.ApplicationCookie!.Configure(options =>
            {
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                // HTTPS arrives through the user's reverse proxy; loopback is plain HTTP.
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = CookieLifetime;
                options.SlidingExpiration = true;

                // This is an API: answer with a status code, never a redirect to a login page.
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
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

        services.AddAuthorizationBuilder()
            // Applies to every endpoint without its own authorization metadata, so a new
            // endpoint is protected unless it explicitly says [AllowAnonymous].
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AdminPolicy, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(OperatorPolicy, policy => policy.RequireAuthenticatedUser());
    }
}
