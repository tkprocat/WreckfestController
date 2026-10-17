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
/// <item><see cref="PublicPolicy"/> caps the anonymous public overview, which reads the
/// server config and the database on every call.</item>
/// <item><see cref="ImagesPolicy"/> caps the anonymous catalogue images. A page of tracks
/// loads dozens at once, so it allows more than the overview.</item>
/// <item><see cref="AssistantPolicy"/> caps questions to the AI assistant. Each one is a
/// paid call upstream, so it allows far fewer; <c>AssistantUsage</c> caps the day's total.</item>
/// </list>
/// </remarks>
public static class RateLimits
{
    public const string PublicPolicy = "public";
    public const int PublicPermitsPerWindow = 60;

    public const string AssistantPolicy = "assistant";
    public const int AssistantPermitsPerWindow = 10;

    public const string ImagesPolicy = "images";
    public const int ImagesPermitsPerWindow = 600;

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
                const string title = "Too many requests. Wait a minute and try again.";

                http.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(RateLimits))
                    .LogWarning("Rate limit {Policy} reached by {ClientIp}", policy, PartitionKey(http));

                await http.Response.WriteAsJsonAsync(
                    new ProblemDetails { Status = StatusCodes.Status429TooManyRequests, Title = title },
                    options: null,
                    contentType: "application/problem+json",
                    cancellationToken);
            };

            options.AddPolicy(PublicPolicy, context => PerClient(context, PublicPermitsPerWindow));
            options.AddPolicy(AssistantPolicy, context => PerClient(context, AssistantPermitsPerWindow));
            options.AddPolicy(ImagesPolicy, context => PerClient(context, ImagesPermitsPerWindow));
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
