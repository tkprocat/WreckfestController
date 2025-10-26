using Microsoft.Extensions.Logging;
using WreckfestController.Services;

var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
var writer = new ConsoleWriter(loggerFactory.CreateLogger<ConsoleWriter>());

IntPtr handle = writer.FindConsoleWindow();

if (handle == IntPtr.Zero)
{
    Console.WriteLine("Could not find Wreckfest console window!");
    return;
}

Console.WriteLine($"Found console window: {handle}");
Console.WriteLine("Adding 20 bots...");

for (int i = 0; i < 20; i++)
{
    writer.SendCommand(handle, "/bot" + Environment.NewLine);
    Console.WriteLine($"  Bot {i + 1}/20 added");
    Thread.Sleep(500); // Wait 500ms between each bot
}

Console.WriteLine("Done! Server should now have 24 players total.");
