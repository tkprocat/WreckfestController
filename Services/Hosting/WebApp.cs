using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;

namespace WreckfestController.Services.Hosting;

/// <summary>
/// The Vue web app the API host serves: the files the build put in <c>wwwroot</c> next
/// to the exe (or <c>Web:Root</c>). Without them, the host serves only the API.
/// </summary>
public sealed partial class WebApp
{
    public const string RootKey = "Web:Root";

    /// <summary>
    /// The path the whole site lives under, such as <c>/tvtp2</c>, so several controllers can
    /// share one domain behind a reverse proxy that forwards the full path. Empty: the root.
    /// </summary>
    public const string PathBaseKey = "Web:PathBase";

    /// <summary>
    /// What the built index.html carries (web/index.html), rewritten to the path base, so
    /// one build of the app works under any of them.
    /// </summary>
    public const string BasePlaceholder = "<base href=\"/\" />";

    private WebApp(string root, IFileProvider? files, PathString pathBase)
    {
        Root = root;
        Files = files;
        PathBase = pathBase;
    }

    /// <summary>Where the files are looked for.</summary>
    public string Root { get; }

    /// <summary>The files, or null when there is no built web app there.</summary>
    public IFileProvider? Files { get; }

    /// <summary>The configured <see cref="PathBaseKey"/>; empty for the root.</summary>
    public PathString PathBase { get; }

    public static WebApp Resolve(IConfiguration configuration)
    {
        var configured = configuration[RootKey];
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "wwwroot")
            : Environment.ExpandEnvironmentVariables(configured);

        return new WebApp(
            root,
            File.Exists(Path.Combine(root, "index.html")) ? new PhysicalFileProvider(root) : null,
            ResolvePathBase(configuration));
    }

    /// <summary>
    /// <see cref="PathBaseKey"/>, checked: one or more <c>/segment</c>s of letters, digits,
    /// <c>-</c>, <c>_</c> and <c>.</c>, without a trailing slash. A trailing slash is
    /// forgiven; anything else stops the API with the reason.
    /// </summary>
    /// <exception cref="PathBaseException">The value is not a usable path.</exception>
    public static PathString ResolvePathBase(IConfiguration configuration)
    {
        var value = configuration[PathBaseKey]?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(value))
        {
            return PathString.Empty;
        }

        // "." and ".." would be resolved away by the browser, which then asks outside the base.
        if (!PathBasePattern().IsMatch(value) || value.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new PathBaseException(
                $"{PathBaseKey} \"{configuration[PathBaseKey]}\" is not a path such as /tvtp2: use / and then letters, digits, -, _ or .");
        }

        return new PathString(value);
    }

    [GeneratedRegex(@"^(/[A-Za-z0-9_.\-]+)+$")]
    private static partial Regex PathBasePattern();

    /// <summary>
    /// Takes the path base off every request under it, before anything else looks at the
    /// path. A request outside it is not this site's: 404.
    /// </summary>
    public void UsePathBase(IApplicationBuilder app)
    {
        if (!PathBase.HasValue)
        {
            return;
        }

        app.UsePathBase(PathBase);
        app.Use(async (context, next) =>
        {
            if (!context.Request.PathBase.HasValue)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });

        // WebApplication otherwise matches endpoints at the very start of the pipeline,
        // against the path with the base still on it.
        app.UseRouting();
    }

    /// <summary>
    /// Sends a page load of the base itself without its slash (<c>/tvtp2</c>) to the slash,
    /// where the app's relative links resolve. After HTTPS enforcement, so plain HTTP from
    /// another computer is sent to HTTPS first.
    /// </summary>
    public void UseBaseSlashRedirect(IApplicationBuilder app)
    {
        if (!PathBase.HasValue)
        {
            return;
        }

        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.HasValue
                && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
            {
                context.Response.Redirect(context.Request.PathBase + "/" + context.Request.QueryString);
                return;
            }

            await next(context);
        });
    }

    /// <summary>
    /// Serves the files, before anything else can refuse the request: the page must load in
    /// recovery mode too, to say why sign-in is unavailable. index.html is never served as
    /// it is on disk, because it needs the path base written into it.
    /// </summary>
    public void UseFiles(IApplicationBuilder app)
    {
        if (Files is null)
        {
            return;
        }

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if ((path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
                && await TryServeIndexAsync(context))
            {
                return;
            }

            await next(context);
        });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = Files });
    }

    /// <summary>
    /// Answers the app's own routes (a deep link, or a reload on <c>/settings</c>) with
    /// index.html. Never under <c>/api</c> or <c>/hubs</c>, so a mistyped API path is still
    /// a 404 and not a web page, and never for a path that looks like a missing file.
    /// </summary>
    public void MapFallback(IEndpointRouteBuilder endpoints)
    {
        if (Files is null)
        {
            return;
        }

        endpoints
            .MapFallback(
                "{*path:nonfile:regex(^(?!(api|hubs)(/|$)))}",
                async context =>
                {
                    if (!await TryServeIndexAsync(context))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                    }
                })
            .AllowAnonymous();
    }

    /// <summary>
    /// Writes index.html with the path base in its <c>&lt;base&gt;</c>. False, writing
    /// nothing, when there is no web app or the request is not a page load.
    /// </summary>
    public async Task<bool> TryServeIndexAsync(HttpContext context)
    {
        if (Files is null
            || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            || (Path.HasExtension(context.Request.Path.Value)
                && !context.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)))
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
            BasePlaceholder,
            $"<base href=\"{WebUtility.HtmlEncode(PathBase.Value)}/\" />",
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

    /// <summary>True for a request the web app's routes may answer: anything outside the API and the hub.</summary>
    public static bool IsAppPath(PathString path) =>
        !path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A <see cref="WebApp.PathBaseKey"/> the API cannot start with; the message says why.</summary>
public sealed class PathBaseException(string message) : Exception(message);
