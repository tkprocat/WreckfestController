using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Yarp.ReverseProxy.Forwarder;

namespace WreckfestPortal;

/// <summary>
/// One controller's site on the portal, under <c>/{id}/</c>: the web app's files from
/// <see cref="ControllerSites.Files"/>, and its API and live updates forwarded to the
/// controller. The browser only ever talks to the portal, so the reverse proxy in front
/// needs a single route, and a controller is added by listing it in the portal's settings.
/// </summary>
public static class ControllerSite
{
    /// <summary>What the built index.html carries (web/index.html), rewritten to the site's path.</summary>
    public const string BasePlaceholder = "<base href=\"/\" />";

    /// <summary>The controller's, not the web app's: forwarded as they are.</summary>
    private static readonly string[] ForwardedPaths = ["/api", "/hubs", "/openapi"];

    private static readonly ForwarderRequestConfig RequestConfig = new()
    {
        // The controller answers a hub negotiation or an AI question in well under this; a
        // live-update connection stays open past it, but YARP times idle activity, not the
        // whole connection, and SignalR pings every 15 seconds.
        ActivityTimeout = TimeSpan.FromSeconds(120),
    };

    /// <summary>Adds the site of every configured server to the pipeline, ahead of the portal's own pages.</summary>
    public static void MapControllerSites(this WebApplication app, PortalOptions options)
    {
        var sites = app.Services.GetRequiredService<ControllerSites>();
        if (sites.Files is null)
        {
            app.Logger.LogWarning("No web app at {AppRoot}: the servers' pages will not load, only their API", sites.Root);
        }

        foreach (var server in options.Servers)
        {
            app.Map("/" + server.Id, branch => Configure(branch, server, sites));
        }
    }

    private static void Configure(IApplicationBuilder branch, PortalServer server, ControllerSites sites)
    {
        var prefix = new PathString("/" + server.Id);
        var transformer = new ControllerTransformer(prefix);

        branch.Use(async (context, next) =>
        {
            var request = context.Request;
            if (ForwardedPaths.Any(p => request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
            {
                // The controller has its own limits on uploads (64 MB for an image batch);
                // the portal's default of 30 MB would refuse them before it is asked.
                if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                {
                    limit.MaxRequestBodySize = null;
                }

                await sites.Forwarder.SendAsync(context, server.ApiUrl, sites.Client, RequestConfig, transformer);
                return;
            }

            // A page load at /tvtp2, or at /TVTP2/: to the site's own spelling with its slash,
            // where the app's relative links resolve and its cookies (scoped to /tvtp2) apply.
            if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
                && (!request.Path.HasValue || !string.Equals(request.PathBase.Value, prefix.Value, StringComparison.Ordinal)))
            {
                context.Response.Redirect(UriHelper.BuildRelative(prefix, request.Path.HasValue ? request.Path : "/", request.QueryString));
                return;
            }

            await next(context);
        });

        if (sites.Files is { } files)
        {
            branch.Use(async (context, next) =>
            {
                var path = context.Request.Path;
                if ((path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
                    && await sites.TryServeIndexAsync(context, prefix))
                {
                    return;
                }

                await next(context);
            });
            branch.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        }

        // The app's own routes (a deep link, or a reload on /tvtp2/admin/cups) are the page.
        // A path that looks like a missing file is not.
        branch.Run(async context =>
        {
            if (Path.HasExtension(context.Request.Path.Value) || !await sites.TryServeIndexAsync(context, prefix))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        });
    }

    /// <summary>
    /// What the controller is told about the request: the visitor's address and scheme, and
    /// the path the site lives under here. Never what the visitor sent in those headers: the
    /// controller believes them from the portal, so a visitor could otherwise pick their own
    /// address for its sign-in limit.
    /// </summary>
    private sealed class ControllerTransformer(PathString prefix) : HttpTransformer
    {
        /// <summary>What every cookie of the portal's own is named with (Accounts/PortalAccounts.cs).</summary>
        private const string PortalCookiePrefix = ".WreckfestPortal.";

        private static readonly string[] ForwardingHeaders =
        [
            "Forwarded", "X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Forwarded-Prefix",
            "X-Original-For", "X-Original-Proto", "X-Original-Host", "X-Original-Prefix",
        ];

        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            // Copies the visitor's headers and points the request at destinationPrefix plus
            // the path inside the site: /tvtp2/api/x becomes {ApiUrl}/api/x.
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

            foreach (var header in ForwardingHeaders)
            {
                proxyRequest.Headers.Remove(header);
            }

            if (httpContext.Connection.RemoteIpAddress is { } client)
            {
                proxyRequest.Headers.TryAddWithoutValidation(
                    "X-Forwarded-For",
                    (client.IsIPv4MappedToIPv6 ? client.MapToIPv4() : client).ToString());
            }

            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", httpContext.Request.Scheme);
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", prefix.ToUriComponent());

            // The portal's own sign-in and antiforgery cookies belong to the whole site, so
            // the browser sends them here too. They are the portal's credentials, never a
            // controller's business: a controller that kept one could act as that user on
            // the portal. The controller's own cookies go through. Without case, like ASP.NET
            // reads cookie names: .wreckfestportal.Auth would sign in as well.
            if (proxyRequest.Headers.TryGetValues("Cookie", out var cookies))
            {
                var kept = cookies
                    .SelectMany(header => header.Split(';'))
                    .Select(cookie => cookie.Trim())
                    .Where(cookie => cookie.Length > 0 && !cookie.StartsWith(PortalCookiePrefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                proxyRequest.Headers.Remove("Cookie");
                if (kept.Count > 0)
                {
                    proxyRequest.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", kept));
                }
            }
        }
    }
}

/// <summary>What every controller's site shares: the web app's files, and the connection to the controllers.</summary>
public sealed class ControllerSites : IDisposable
{
    public ControllerSites(IHttpForwarder forwarder, HttpMessageInvoker client, string root)
    {
        Forwarder = forwarder;
        Client = client;
        Root = root;
        Files = File.Exists(System.IO.Path.Combine(root, "index.html")) ? new PhysicalFileProvider(root) : null;
    }

    /// <summary>The connection to the controllers. Decompresses nothing and follows no redirects: it passes on what they say.</summary>
    public static HttpMessageInvoker CreateClient() => new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        EnableMultipleHttp2Connections = true,
        ActivityHeadersPropagator = new ReverseProxyPropagator(DistributedContextPropagator.Current),
        ConnectTimeout = TimeSpan.FromSeconds(15),
    });

    public IHttpForwarder Forwarder { get; }

    public HttpMessageInvoker Client { get; }

    /// <summary>Where the web app is looked for.</summary>
    public string Root { get; }

    /// <summary>The web app's files, or null when it is not there.</summary>
    public IFileProvider? Files { get; }

    /// <summary>
    /// Writes index.html with the site's path in its <c>&lt;base&gt;</c>, so one build of the
    /// app works for every server. False, writing nothing, when there is no web app or the
    /// request is not a page load.
    /// </summary>
    public async Task<bool> TryServeIndexAsync(HttpContext context, PathString prefix)
    {
        if (Files is null || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            return false;
        }

        var index = Files.GetFileInfo("index.html");
        if (!index.Exists)
        {
            return false;
        }

        string html;
        await using (var stream = index.CreateReadStream())
        using (var reader = new StreamReader(stream))
        {
            html = await reader.ReadToEndAsync(context.RequestAborted);
        }

        html = html.Replace(
            ControllerSite.BasePlaceholder,
            $"<base href=\"{WebUtility.HtmlEncode(prefix.Value)}/\" />",
            StringComparison.Ordinal);

        context.Response.ContentType = "text/html; charset=utf-8";
        // A new build changes the asset names it links to, so the page is always checked.
        context.Response.Headers.CacheControl = "no-cache";
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return true;
        }

        await context.Response.WriteAsync(html, context.RequestAborted);
        return true;
    }

    public void Dispose() => Client.Dispose();
}
