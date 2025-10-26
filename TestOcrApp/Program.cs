using Microsoft.Extensions.Logging;
using System.Drawing;
using WreckfestController.Services;

Console.WriteLine("╔═══════════════════════════════════════════════════════╗");
Console.WriteLine("║   Wreckfest Console Automation Test                  ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════╝\n");

Console.WriteLine("This will test three approaches:");
Console.WriteLine("  1. ControlSend - Send 'list' command to console");
Console.WriteLine("  2. Screenshot - Capture console window");
Console.WriteLine("  3. OCR - Read text from console using Tesseract\n");

if (!Console.IsInputRedirected)
{
    Console.WriteLine("Press any key to start...");
    Console.ReadKey();
    Console.WriteLine();
}
else
{
    Console.WriteLine("Running in automated mode (stdin redirected)...\n");
}

// Create logger
var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

// Test 1: ControlSend
Console.WriteLine("\n┌─────────────────────────────────────────────────────┐");
Console.WriteLine("│ Test 1: Sending 'list' command with ControlSend    │");
Console.WriteLine("└─────────────────────────────────────────────────────┘");

var writer = new ConsoleWriter(loggerFactory.CreateLogger<ConsoleWriter>());
IntPtr handle = writer.FindConsoleWindow();

if (handle == IntPtr.Zero)
{
    Console.WriteLine("❌ Could not find console window with class 'ConsoleWindowClass'");
    Console.WriteLine("   Make sure Wreckfest dedicated server is running!");
}
else
{
    Console.WriteLine($"✓ Found console window: {handle}");
    Console.WriteLine("  Sending 'list' command...");

    bool success = writer.SendCommand(handle, "list" + Environment.NewLine);

    if (success)
    {
        Console.WriteLine("✓ Command sent successfully!");
        Console.WriteLine("  Waiting 2 seconds for output to appear...");
        Thread.Sleep(2000);
    }
    else
    {
        Console.WriteLine("❌ Failed to send command");
    }
}

// Test 2: Screenshot
Console.WriteLine("\n┌─────────────────────────────────────────────────────┐");
Console.WriteLine("│ Test 2: Capturing console screenshot               │");
Console.WriteLine("└─────────────────────────────────────────────────────┘");

var reader = new ConsoleReader();
handle = reader.FindConsoleWindow();

if (handle == IntPtr.Zero)
{
    Console.WriteLine("❌ Could not find console window");
}
else
{
    Console.WriteLine($"✓ Found console window: {handle}");
    Console.WriteLine("  Capturing screenshot...");

    Bitmap? screenshot = reader.CaptureConsoleWindow(handle);

    if (screenshot != null)
    {
        string filename = $"wreckfest_console_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        screenshot.Save(filename, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"✓ Screenshot saved: {filename}");
        Console.WriteLine($"  Size: {screenshot.Width}x{screenshot.Height} pixels");
        screenshot.Dispose();
    }
    else
    {
        Console.WriteLine("❌ Failed to capture screenshot");
    }
}

// Test 3: OCR
Console.WriteLine("\n┌─────────────────────────────────────────────────────┐");
Console.WriteLine("│ Test 3: Reading console text with OCR (Tesseract)  │");
Console.WriteLine("└─────────────────────────────────────────────────────┘");

handle = reader.FindConsoleWindow();

if (handle == IntPtr.Zero)
{
    Console.WriteLine("❌ Could not find console window");
}
else
{
    Console.WriteLine($"✓ Found console window: {handle}");
    Console.WriteLine("  Initializing Tesseract OCR engine...");

    try
    {
        using var ocr = new ConsoleOcr(loggerFactory.CreateLogger<ConsoleOcr>());
        Console.WriteLine("✓ OCR engine initialized");
        Console.WriteLine("  Reading console text with optimized size (993x1040)...");

        // Use optimized window size: calculated for 24 players (72% smaller than maximized!)
        string text = ocr.ReadConsoleWindow(handle, 993, 1040);

        if (!string.IsNullOrEmpty(text))
        {
            Console.WriteLine($"✓ Extracted {text.Length} characters\n");

            // Show preview
            Console.WriteLine("─── OCR Output Preview (first 800 chars) ───");
            Console.WriteLine(text.Length > 800 ? text.Substring(0, 800) + "..." : text);
            Console.WriteLine("─── End Preview ───\n");

            // Look for player indicators
            var playerLines = ocr.FindLines(text, "*");
            if (playerLines.Any())
            {
                Console.WriteLine($"✓ Found {playerLines.Count} lines with player indicators (*):");
                foreach (var line in playerLines.Take(10))
                {
                    Console.WriteLine($"  {line}");
                }
                if (playerLines.Count > 10)
                {
                    Console.WriteLine($"  ... and {playerLines.Count - 10} more");
                }
            }

            // Save full output
            string outputFile = $"ocr_output_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            File.WriteAllText(outputFile, text);
            Console.WriteLine($"\n✓ Full OCR output saved: {outputFile}");
        }
        else
        {
            Console.WriteLine("❌ No text extracted");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ OCR failed: {ex.Message}");
        Console.WriteLine($"   {ex.GetType().Name}");
    }
}

Console.WriteLine("\n╔═══════════════════════════════════════════════════════╗");
Console.WriteLine("║   All tests complete!                                 ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════╝\n");

if (!Console.IsInputRedirected)
{
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
}
