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

            return settings.PublicPort == 443 ? $"https://{host}" : $"https://{host}:{settings.PublicPort}";
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
    /// The TCP peer was this PC, and no trusted proxy spoke for someone else. The address is
    /// the connection's: forwarded headers from an untrusted peer are never applied to it.
    /// </summary>
    private static bool IsLocalPeer(HttpContext context)
    {
        // Applying a trusted proxy's X-Forwarded-* records the originals in X-Original-*.
        // Either there means the request came through a proxy, from somewhere else. A client
        // that sends them itself only loses the exemption; it cannot gain one.
        var headers = context.Request.Headers;
        if (headers.ContainsKey(ForwardedHeadersDefaults.XOriginalForHeaderName)
            || headers.ContainsKey(ForwardedHeadersDefaults.XOriginalProtoHeaderName))
        {
            return false;
        }

        var peer = context.Connection.RemoteIpAddress;
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

    private static bool IsLocalName(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}
