using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace WreckfestController.Services;

/// <summary>
/// Caps sign-in attempts per client IP. Lockout protects one account; this slows a
/// client that sprays many accounts. The IP is the one <see cref="TrustedProxies"/>
/// resolved, so clients behind the reverse proxy do not share one bucket.
/// </summary>
public static class LoginRateLimit
{
    public const string PolicyName = "login";
    public const int PermitsPerWindow = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static void AddLoginRateLimit(this IServiceCollection services)
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
                    .CreateLogger(typeof(LoginRateLimit))
                    .LogWarning("Too many sign-in attempts from {ClientIp}", PartitionKey(http));

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

            options.AddPolicy(PolicyName, context => RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitsPerWindow,
                    Window = Window,
                    QueueLimit = 0,
                }));
        });
    }

    private static string PartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } ip
            ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString()
            : "unknown";
}
