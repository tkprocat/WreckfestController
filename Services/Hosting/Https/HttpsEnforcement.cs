using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;

namespace WreckfestController.Services.Hosting.Https;

/// <summary>
/// What HTTPS enforcement needs: whether HTTPS is on, the public origin to redirect to, and
/// whether to send HSTS. The redirect's host comes from configuration or the certificate,
/// never from the request's Host header, which the client controls.
/// </summary>
public sealed class HttpsPolicy(HttpsSettings? settings, HttpsStatusSource status)
{
    public bool Enabled => settings is not null;

    public bool Hsts => settings?.Hsts == true;

    /// <summary>
    /// Where a remote browser should go: https://host[:port]. The host is Api:Https:PublicHost,
    /// else the store's Host, else the certificate's first DNS name; null when none is known.
    /// </summary>
    public string? PublicOrigin
    {
        get
        {
            if (settings is null)
            {
                return null;
            }

            var host = settings.PublicHost
                ?? (settings.Source as StoreSourceSettings)?.Host
                ?? status.Status.DnsNames.FirstOrDefault(n => !n.StartsWith('*'));
            if (host is null)
            {
                return null;
            }

            // UriBuilder brackets an IPv6 address, which a URL's authority needs.
            var builder = new UriBuilder(Uri.UriSchemeHttps, host.Trim('[', ']'), settings.PublicPort == 443 ? -1 : settings.PublicPort);
            return builder.Uri.GetLeftPart(UriPartial.Authority);
        }
    }
}

/// <summary>
/// With HTTPS on, plain HTTP is for this PC only. Runs right after the forwarded headers,
/// before static files, recovery handling, authentication and the API:
/// - HTTPS, direct or from a trusted proxy: served (with HSTS when asked for).
/// - Plain HTTP from a loopback peer with no trusted forwarded headers (a tool on this PC):
///   served. A trusted proxy on loopback carries remote traffic and is not exempt.
/// - Plain HTTP from anyone else: a page load (GET/HEAD outside the API) is redirected to the
///   public HTTPS origin; anything else is refused, since a redirect cannot take back a
///   password already sent in the clear.
/// </summary>
public sealed class HttpsEnforcementMiddleware(RequestDelegate next, HttpsPolicy policy)
{
    private const string OriginalKey = "WreckfestController.Https.Original";

    /// <summary>The TCP peer, and whether any forwarding header came, before the forwarded headers are applied.</summary>
    private sealed record Original(IPAddress? Peer, bool Forwarded);

    private static readonly string[] ForwardingHeaders =
    [
        "X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Forwarded-Prefix", "Forwarded",
        ForwardedHeadersDefaults.XOriginalForHeaderName, ForwardedHeadersDefaults.XOriginalProtoHeaderName,
    ];

    /// <summary>
    /// Runs before UseForwardedHeaders: notes the connection's peer and whether any forwarding
    /// header came. Applying the headers rewrites the one and removes the others, and a
    /// malformed header is dropped without a trace; what the client sent is known only here.
    /// </summary>
    public static Task CaptureOriginal(HttpContext context, Func<Task> next)
    {
        var headers = context.Request.Headers;
        context.Items[OriginalKey] = new Original(context.Connection.RemoteIpAddress, ForwardingHeaders.Any(headers.ContainsKey));
        return next();
    }

    /// <summary>180 days. Documented; no includeSubDomains or preload.</summary>
    public const string HstsValue = "max-age=15552000";

    private static readonly string[] ApiPrefixes = ["/api", "/hubs", "/openapi"];

    public Task InvokeAsync(HttpContext context)
    {
        if (!policy.Enabled)
        {
            return next(context);
        }

        if (context.Request.IsHttps)
        {
            if (policy.Hsts && !IsLocalName(context.Request.Host.Host))
            {
                context.Response.Headers.StrictTransportSecurity = HstsValue;
            }

            return next(context);
        }

        if (IsLocalPeer(context))
        {
            return next(context);
        }

        var request = context.Request;
        var isPageLoad = (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            && !ApiPrefixes.Any(p => request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
        if (isPageLoad && policy.PublicOrigin is { } origin)
        {
            // 307: temporary, so a browser does not remember it past a configuration change.
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers.Location = origin + request.PathBase + request.Path + request.QueryString;
            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return context.Response.WriteAsJsonAsync(
            new { title = "Use HTTPS: this server does not accept requests over plain HTTP from other computers.", status = 400 },
            options: null,
            contentType: "application/problem+json");
    }

    /// <summary>
    /// The TCP peer was this PC, and the request came with no forwarding header at all: a tool
    /// on this PC, not a proxy speaking for someone else. Decided from what arrived, before
    /// the forwarded headers were applied, so a malformed header that ASP.NET drops (leaving
    /// no trace) still counts as "came through a proxy". Fails closed: a local tool that sends
    /// forwarding headers itself loses the exemption; nobody can gain it.
    /// </summary>
    private static bool IsLocalPeer(HttpContext context)
    {
        if (context.Items[OriginalKey] is not Original original || original.Forwarded)
        {
            return false;
        }

        var peer = original.Peer;
        if (peer is null)
        {
            return false;
        }

        if (peer.IsIPv4MappedToIPv6)
        {
            peer = peer.MapToIPv4();
        }

        return IPAddress.IsLoopback(peer);
    }

    private static bool IsLocalName(string host)
    {
        // "localhost." is the same name, fully qualified.
        host = host.TrimEnd('.');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address));
    }
}
