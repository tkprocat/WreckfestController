using WreckfestController.Services;
using WreckfestController.WebSockets;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register services as singletons
builder.Services.AddSingleton<WreckfestController.Services.PlayerTracker>();
builder.Services.AddSingleton<ServerManager>();
builder.Services.AddSingleton<WreckfestController.Services.ConfigService>();

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
    else
    {
        await next();
    }
});

app.UseAuthorization();
app.MapControllers();

app.Run();
