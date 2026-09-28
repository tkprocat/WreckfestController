using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;

namespace WreckfestController.Services.Hosting;

/// <summary>
/// The Vue web app the API host serves: the files the build put in <c>wwwroot</c> next
/// to the exe (or <c>Web:Root</c>). Without them, the host serves only the API.
/// </summary>
public sealed class WebApp
{
    public const string RootKey = "Web:Root";

    private WebApp(string root, IFileProvider? files)
    {
        Root = root;
        Files = files;
    }

    /// <summary>Where the files are looked for.</summary>
    public string Root { get; }

    /// <summary>The files, or null when there is no built web app there.</summary>
    public IFileProvider? Files { get; }

    public static WebApp Resolve(IConfiguration configuration)
    {
        var configured = configuration[RootKey];
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "wwwroot")
            : Environment.ExpandEnvironmentVariables(configured);

        return new WebApp(root, File.Exists(Path.Combine(root, "index.html")) ? new PhysicalFileProvider(root) : null);
    }

    /// <summary>
    /// Serves the files, before anything else can refuse the request: the page must load in
    /// recovery mode too, to say why sign-in is unavailable.
    /// </summary>
    public void UseFiles(IApplicationBuilder app)
    {
        if (Files is null)
        {
            return;
        }

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = Files });
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
            .MapFallbackToFile(
                "{*path:nonfile:regex(^(?!(api|hubs)(/|$)))}",
                "index.html",
                new StaticFileOptions { FileProvider = Files })
            .AllowAnonymous();
    }

    /// <summary>
    /// Writes index.html, for a page route in recovery mode. False, writing nothing, when
    /// there is no web app or the request is not a page load.
    /// </summary>
    public async Task<bool> TryServeIndexAsync(HttpContext context)
    {
        if (Files is null
            || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            || Path.HasExtension(context.Request.Path.Value))
        {
            return false;
        }

        var index = Files.GetFileInfo("index.html");
        if (!index.Exists)
        {
            return false;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(index);
        return true;
    }

    /// <summary>True for a request the web app's routes may answer: anything outside the API and the hub.</summary>
    public static bool IsAppPath(PathString path) =>
        !path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && !path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase);
}
