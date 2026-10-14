using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using WreckfestPortal;

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
app.Run();

/// <summary>The portal's services and pipeline, apart from Program so tests build the same host.</summary>
public static class PortalApp
{
    /// <summary>The page, as the web app's portal build names it.</summary>
    public const string PageFile = "portal.html";

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
    }

    public static void ConfigurePipeline(WebApplication app)
    {
        var page = app.Environment.WebRootFileProvider.GetFileInfo(PageFile);
        if (!page.Exists)
        {
            app.Logger.LogWarning("No {Page} in {WebRoot}: serving /api/servers only", PageFile, app.Environment.WebRootPath);
        }

        app.UseStaticFiles();

        // Cached cards, never a request to a controller: nothing to limit or protect.
        app.MapGet("/api/servers", (ServerPoller poller, IOptions<PortalOptions> options) =>
            new PortalPage(options.Value.Title, options.Value.Intro, poller.Cards));

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
        });
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
