using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace WreckfestPortal.Accounts;

/// <summary>
/// Sign-in attempts per client IP, the one the forwarded headers settled on. Lockout
/// protects one account; this slows a client that tries many.
/// </summary>
public static class PortalRateLimits
{
    public const string LoginPolicy = "login";
    public const int LoginPermitsPerWindow = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static void AddPortalRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                var http = context.HttpContext;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    http.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                http.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(PortalRateLimits))
                    .LogWarning("Sign-in limit reached by {ClientIp}", PartitionKey(http));

                await http.Response.WriteAsJsonAsync(
                    new ProblemDetails
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too many sign-in attempts. Wait a minute and try again.",
                    },
                    options: null,
                    contentType: "application/problem+json",
                    cancellationToken);
            };

            options.AddPolicy(LoginPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = LoginPermitsPerWindow, Window = Window, QueueLimit = 0 }));
        });
    }

    private static string PartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } ip
            ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString()
            : "unknown";
}
