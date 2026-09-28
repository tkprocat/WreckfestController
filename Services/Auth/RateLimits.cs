using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace WreckfestController.Services.Auth;

/// <summary>
/// Per-client-IP rate limits. The IP is the one <see cref="TrustedProxies"/> resolved, so
/// clients behind the reverse proxy do not share one bucket.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="LoginPolicy"/> caps sign-in attempts. Lockout protects one account;
/// this slows a client that sprays many accounts.</item>
/// <item><see cref="PublicPolicy"/> caps the anonymous public overview, which reads the
/// server config and the database on every call.</item>
/// </list>
/// </remarks>
public static class RateLimits
{
    public const string LoginPolicy = "login";
    public const int LoginPermitsPerWindow = 10;

    public const string PublicPolicy = "public";
    public const int PublicPermitsPerWindow = 60;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static void AddRateLimits(this IServiceCollection services)
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

                var policy = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
                var title = policy == LoginPolicy
                    ? "Too many sign-in attempts. Wait a minute and try again."
                    : "Too many requests. Wait a minute and try again.";

                http.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(RateLimits))
                    .LogWarning("Rate limit {Policy} reached by {ClientIp}", policy, PartitionKey(http));

                await http.Response.WriteAsJsonAsync(
                    new ProblemDetails { Status = StatusCodes.Status429TooManyRequests, Title = title },
                    options: null,
                    contentType: "application/problem+json",
                    cancellationToken);
            };

            options.AddPolicy(LoginPolicy, context => PerClient(context, LoginPermitsPerWindow));
            options.AddPolicy(PublicPolicy, context => PerClient(context, PublicPermitsPerWindow));
        });
    }

    private static RateLimitPartition<string> PerClient(HttpContext context, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(
            PartitionKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = Window,
                QueueLimit = 0,
            });

    private static string PartitionKey(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } ip
            ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString()
            : "unknown";
}
