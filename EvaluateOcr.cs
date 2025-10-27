using Microsoft.Extensions.Logging;
using System.Drawing;
using WreckfestController.Services;

var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger<ConsoleOcr>();
var ocr = new ConsoleOcr(logger);

// Find all test screenshots with ground truth
var screenshotFiles = Directory.GetFiles("bin/Debug/net8.0", "ocr_debug_*.png")
    .Where(f => File.Exists(f.Replace(".png", ".gt.txt")))
    .ToList();

Console.WriteLine($"Found {screenshotFiles.Count} screenshots with ground truth data\n");

int totalCharacters = 0;
int correctCharacters = 0;

foreach (var screenshotPath in screenshotFiles)
{
    var groundTruthPath = screenshotPath.Replace(".png", ".gt.txt");
    var fileName = Path.GetFileName(screenshotPath);

    Console.WriteLine($"=== Testing: {fileName} ===");

    // Read ground truth
    var groundTruth = File.ReadAllText(groundTruthPath);

    // Run OCR
    using var screenshot = new Bitmap(screenshotPath);
    var ocrResult = ocr.ExtractText(screenshot);

    // Calculate character accuracy
    int maxLength = Math.Max(groundTruth.Length, ocrResult.Length);
    int matches = 0;
    for (int i = 0; i < maxLength; i++)
    {
        if (i < groundTruth.Length && i < ocrResult.Length && groundTruth[i] == ocrResult[i])
        {
            matches++;
        }
    }

    totalCharacters += groundTruth.Length;
    correctCharacters += matches;

    double accuracy = (double)matches / groundTruth.Length * 100;

    Console.WriteLine($"Ground Truth Length: {groundTruth.Length} characters");
    Console.WriteLine($"OCR Result Length: {ocrResult.Length} characters");
    Console.WriteLine($"Character Accuracy: {accuracy:F2}%");
    Console.WriteLine($"Correct: {matches}/{groundTruth.Length}\n");

    // Show first 200 characters of each for comparison
    Console.WriteLine("Ground Truth (first 200 chars):");
    Console.WriteLine(groundTruth.Substring(0, Math.Min(200, groundTruth.Length)));
    Console.WriteLine("\nOCR Result (first 200 chars):");
    Console.WriteLine(ocrResult.Substring(0, Math.Min(200, ocrResult.Length)));
    Console.WriteLine("\n" + new string('-', 80) + "\n");
}

Console.WriteLine("=== OVERALL RESULTS ===");
double overallAccuracy = (double)correctCharacters / totalCharacters * 100;
Console.WriteLine($"Total Characters: {totalCharacters}");
Console.WriteLine($"Correct Characters: {correctCharacters}");
Console.WriteLine($"Overall Accuracy: {overallAccuracy:F2}%");

ocr.Dispose();
