using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using WreckfestPortal;
using WreckfestPortal.Accounts;

if (CreateAdminCommand.Matches(args))
{
    return await CreateAdminCommand.RunAsync(args, Console.In, Console.Out);
}

// Next to the exe, not the working folder: a Windows service starts in System32.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Host.UseWindowsService();

PortalApp.ConfigureServices(builder.Services, builder.Configuration);

var app = builder.Build();
PortalApp.ConfigurePipeline(app);
await app.RunAsync();
return 0;

/// <summary>The portal's services and pipeline, apart from Program so tests build the same host.</summary>
public static class PortalApp
{
    /// <summary>The page, as the web app's portal build names it.</summary>
    public const string PageFile = "portal.html";

    /// <summary>The folder next to the exe the build puts the controllers' web app in.</summary>
    public const string AppFolder = "app";

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PortalOptions>()
            .Bind(configuration.GetSection(PortalOptions.Section))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PortalOptions>, PortalOptionsValidator>();
        services.AddHttpClient(ServerPoller.HttpClientName);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ServerPoller>();
        services.AddHostedService(sp => sp.GetRequiredService<ServerPoller>());

        services.AddPortalAccounts();
        services.AddPortalRateLimits();
        services.AddControllers(options => options.Filters.Add<AntiforgeryFilter>())
            // AddControllers looks in the entry assembly, which is the test runner in tests.
            .AddApplicationPart(typeof(PortalApp).Assembly)
            // Numbers are numbers, as in the controller's API: the web app's types say so.
            .AddJsonOptions(options => options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict);
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
        // /openapi/v1.json: the contract the web app's portal types are generated from.
        services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "WreckfestPortal API";
            document.Info.Version = "2.0";
            return Task.CompletedTask;
        }));

        services.AddHttpForwarder();
        services.AddSingleton(_ => ControllerSites.CreateClient());
        services.AddSingleton(sp =>
        {
            var configured = sp.GetRequiredService<IOptions<PortalOptions>>().Value.AppRoot;
            var root = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath, AppFolder)
                : Environment.ExpandEnvironmentVariables(configured);
            return new ControllerSites(
                sp.GetRequiredService<Yarp.ReverseProxy.Forwarder.IHttpForwarder>(),
                sp.GetRequiredService<HttpMessageInvoker>(),
                root);
        });

        services.AddOptions<ForwardedHeadersOptions>().Configure<IOptions<PortalOptions>>((forwarded, portal) =>
        {
            // Only the listed proxies, not the loopback ASP.NET trusts by default.
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            forwarded.ForwardLimit = null;
            forwarded.ForwardedHeaders = ForwardedHeaders.None;
            foreach (var entry in portal.Value.TrustedProxies)
            {
                if (PortalOptions.TryParseProxy(entry, out var address, out var network))
                {
                    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                    if (address is not null)
                    {
                        forwarded.KnownProxies.Add(address);
                    }
                    else
                    {
                        forwarded.KnownIPNetworks.Add(network!.Value);
                    }
                }
            }
        });
    }

    public static void ConfigurePipeline(WebApplication app)
    {
        var page = app.Environment.WebRootFileProvider.GetFileInfo(PageFile);
        if (!page.Exists)
        {
            app.Logger.LogWarning("No {Page} in {WebRoot}: serving /api/servers only", PageFile, app.Environment.WebRootPath);
        }

        // Before anything reads the visitor's address or scheme, which the controllers are told.
        app.UseForwardedHeaders();

        // /{id}/...: each server's site, and its API forwarded to its controller.
        app.MapControllerSites(app.Services.GetRequiredService<IOptions<PortalOptions>>().Value);

        app.UseStaticFiles();

        // The accounts' database, created or brought up to date before the first request.
        PortalDatabase.MigrateAsync(app.Services).GetAwaiter().GetResult();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapOpenApi().RequireAuthorization(PortalAccounts.AdminPolicy);

        // Cached cards, never a request to a controller: nothing to limit or protect.
        app.MapGet("/api/servers", (ServerPoller poller, IOptions<PortalOptions> options) =>
            new PortalPage(options.Value.Title, options.Value.Intro, poller.Cards))
            // Named here: by default a minimal API is tagged with the entry assembly, which
            // is the test runner when the contract test reads the document.
            .WithTags("Servers")
            .AllowAnonymous();

        app.MapGet("/", async context =>
        {
            var file = app.Environment.WebRootFileProvider.GetFileInfo(PageFile);
            if (!file.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            // A new build changes the asset names it links to, so the page is always checked.
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.SendFileAsync(file);
        }).AllowAnonymous().ExcludeFromDescription();
    }
}

/// <summary>Says what is wrong with the settings, line by line, when the portal will not start.</summary>
internal sealed class PortalOptionsValidator : IValidateOptions<PortalOptions>
{
    public ValidateOptionsResult Validate(string? name, PortalOptions options)
    {
        var problems = options.Problems();
        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
