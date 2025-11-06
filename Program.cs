using Microsoft.AspNetCore.Builder;
using WreckfestController.Services;
using WreckfestController.WebSockets;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel to use settings from appsettings.json
var urls = builder.Configuration["Kestrel:Urls"];
if (!string.IsNullOrEmpty(urls))
{
    builder.WebHost.UseUrls(urls);
}

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register HTTP client
builder.Services.AddHttpClient<LaravelWebhookService>();

// Register services as singletons
builder.Services.AddSingleton<WreckfestController.Services.PlayerTracker>();
builder.Services.AddSingleton<WreckfestController.Services.TrackChangeTracker>();
builder.Services.AddSingleton<LaravelWebhookService>();
builder.Services.AddSingleton<ServerManager>();
builder.Services.AddSingleton<WreckfestController.Services.ConfigService>();
builder.Services.AddSingleton<WreckfestController.Services.EventStorageService>();
builder.Services.AddSingleton<WreckfestController.Services.RecurringEventService>();
builder.Services.AddSingleton<WreckfestController.Services.SmartRestartService>();

// Register hosted services (background services)
builder.Services.AddHostedService<WreckfestController.Services.EventSchedulerService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Enable WebSockets
app.UseWebSockets();

// WebSocket middleware for console streaming
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/ws/console")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            var serverManager = context.RequestServices.GetRequiredService<ServerManager>();
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var handler = new ConsoleWebSocketHandler(webSocket, serverManager);
            await handler.HandleAsync();
        }
        else
        {
            context.Response.StatusCode = 400;
        }
    }
    else if (context.Request.Path == "/ws/players")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            var playerTracker = context.RequestServices.GetRequiredService<PlayerTracker>();
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var handler = new PlayerTrackerWebSockerHandler(webSocket, playerTracker);
            await handler.HandleAsync();
        }
        else
        {
            context.Response.StatusCode = 400;
        }
    }
    else if (context.Request.Path == "/ws/track-changes")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            var trackChangeTracker = context.RequestServices.GetRequiredService<TrackChangeTracker>();
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var handler = new TrackChangeWebSocketHandler(webSocket, trackChangeTracker);
            await handler.HandleAsync();
        }
        else
        {
            context.Response.StatusCode = 400;
        }
    }
    else
    {
        await next();
    }
});

app.UseAuthorization();
app.MapControllers();

app.Run();
