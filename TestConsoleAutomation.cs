using WreckfestController.Services;
using System.Drawing;
using System.Drawing.Imaging;

namespace WreckfestController;

/// <summary>
/// Test program to demonstrate console automation (ControlSend and OCR)
/// </summary>
public class TestConsoleAutomation
{
    public static void TestControlSend()
    {
        Console.WriteLine("=== Testing ControlSend ===");
        Console.WriteLine("Looking for Wreckfest console window...");

        var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<ConsoleWriter>();
        var writer = new ConsoleWriter(logger);

        // Find the console window
        IntPtr handle = writer.FindConsoleWindow();

        if (handle == IntPtr.Zero)
        {
            Console.WriteLine("❌ Could not find console window with class 'ConsoleWindowClass'");
            Console.WriteLine("Make sure Wreckfest dedicated server is running!");
            return;
        }

        Console.WriteLine($"✓ Found console window: {handle}");
        Console.WriteLine("Sending 'list' command...");

        // Send the list command
        bool success = writer.SendCommand(handle, "list");

        if (success)
        {
            Console.WriteLine("✓ Command sent successfully!");
            Console.WriteLine("Check the Wreckfest server console for output.");
        }
        else
        {
            Console.WriteLine("❌ Failed to send command");
        }
    }

    public static void TestScreenCapture()
    {
        Console.WriteLine("\n=== Testing Screen Capture ===");
        Console.WriteLine("Looking for Wreckfest console window...");

        var reader = new ConsoleReader();

        // Find the console window
        IntPtr handle = reader.FindConsoleWindow();

        if (handle == IntPtr.Zero)
        {
            Console.WriteLine("❌ Could not find console window");
            return;
        }

        Console.WriteLine($"✓ Found console window: {handle}");
        Console.WriteLine("Capturing screenshot...");

        // Capture screenshot
        Bitmap? screenshot = reader.CaptureConsoleWindow(handle);

        if (screenshot != null)
        {
            // Save screenshot to file
            string filename = $"console_capture_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            screenshot.Save(filename, ImageFormat.Png);
            Console.WriteLine($"✓ Screenshot saved to: {filename}");
            Console.WriteLine($"  Size: {screenshot.Width}x{screenshot.Height}");

            screenshot.Dispose();

            Console.WriteLine("\nNext step: Add OCR library (Tesseract or Windows.Media.Ocr) to read text from screenshot");
        }
        else
        {
            Console.WriteLine("❌ Failed to capture screenshot");
        }
    }

    public static void TestOCR()
    {
        Console.WriteLine("\n=== Testing OCR (Tesseract) ===");
        Console.WriteLine("Looking for Wreckfest console window...");

        var reader = new ConsoleReader();
        IntPtr handle = reader.FindConsoleWindow();

        if (handle == IntPtr.Zero)
        {
            Console.WriteLine("❌ Could not find console window");
            return;
        }

        Console.WriteLine($"✓ Found console window: {handle}");
        Console.WriteLine("Initializing OCR engine...");

        try
        {
            var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<ConsoleOcr>();
            using var ocr = new ConsoleOcr(logger);

            Console.WriteLine("✓ OCR engine initialized");
            Console.WriteLine("Reading console text...");

            string text = ocr.ReadConsoleWindow(handle);

            if (!string.IsNullOrEmpty(text))
            {
                Console.WriteLine($"✓ Extracted {text.Length} characters");
                Console.WriteLine("\n--- OCR Output Preview (first 500 chars) ---");
                Console.WriteLine(text.Length > 500 ? text.Substring(0, 500) + "..." : text);
                Console.WriteLine("--- End Preview ---\n");

                // Look for player names (lines with asterisks for bots)
                var playerLines = ocr.FindLines(text, "*");
                if (playerLines.Any())
                {
                    Console.WriteLine($"Found {playerLines.Count} lines with player indicators:");
                    foreach (var line in playerLines.Take(5))
                    {
                        Console.WriteLine($"  {line}");
                    }
                }

                // Save full output to file
                string outputFile = $"ocr_output_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                File.WriteAllText(outputFile, text);
                Console.WriteLine($"\n✓ Full OCR output saved to: {outputFile}");
            }
            else
            {
                Console.WriteLine("❌ No text extracted");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ OCR failed: {ex.Message}");
        }
    }

    public static void RunTests()
    {
        Console.WriteLine("Wreckfest Console Automation Test\n");
        Console.WriteLine("This will test three approaches:");
        Console.WriteLine("1. ControlSend - Send commands to console");
        Console.WriteLine("2. Screen Capture - Capture console window");
        Console.WriteLine("3. OCR (Tesseract) - Read text from console\n");

        Console.WriteLine("Press any key to start...");
        Console.ReadKey();

        TestControlSend();
        TestScreenCapture();
        TestOCR();

        Console.WriteLine("\n=== Tests Complete ===");
        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }
}
