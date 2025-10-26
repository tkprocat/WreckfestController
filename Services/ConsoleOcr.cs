using System.Drawing;
using Tesseract;
using Microsoft.Extensions.Logging;

namespace WreckfestController.Services;

/// <summary>
/// OCR service to read text from console window screenshots
/// </summary>
public class ConsoleOcr : IDisposable
{
    private readonly ILogger<ConsoleOcr> _logger;
    private readonly string _tessdataPath;
    private TesseractEngine? _engine;

    public ConsoleOcr(ILogger<ConsoleOcr> logger, string? tessdataPath = null)
    {
        _logger = logger;
        _tessdataPath = tessdataPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

        InitializeEngine();
    }

    private void InitializeEngine()
    {
        try
        {
            if (!Directory.Exists(_tessdataPath))
            {
                _logger.LogError("Tessdata directory not found: {Path}", _tessdataPath);
                throw new DirectoryNotFoundException($"Tessdata directory not found: {_tessdataPath}");
            }

            _engine = new TesseractEngine(_tessdataPath, "eng", EngineMode.Default);
            _logger.LogInformation("Tesseract OCR engine initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Tesseract OCR engine");
            throw;
        }
    }

    /// <summary>
    /// Extract text from a bitmap image
    /// </summary>
    public string ExtractText(Bitmap image)
    {
        if (_engine == null)
        {
            throw new InvalidOperationException("OCR engine not initialized");
        }

        try
        {
            // Convert Bitmap to byte array, then to Pix
            using var ms = new System.IO.MemoryStream();
            image.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
            ms.Position = 0;
            byte[] imageBytes = ms.ToArray();

            using var pix = Pix.LoadFromMemory(imageBytes);
            using var page = _engine.Process(pix);

            string text = page.GetText();
            float confidence = page.GetMeanConfidence();

            _logger.LogDebug("OCR extraction completed with {Confidence}% confidence", confidence * 100);

            return text.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract text from image");
            return string.Empty;
        }
    }

    /// <summary>
    /// Extract text from console window
    /// </summary>
    public string ReadConsoleWindow(IntPtr windowHandle, int? targetWidth = null, int? targetHeight = null)
    {
        var reader = new ConsoleReader();
        Bitmap? screenshot = reader.CaptureConsoleWindow(windowHandle, targetWidth, targetHeight);

        if (screenshot == null)
        {
            _logger.LogWarning("Failed to capture console window");
            return string.Empty;
        }

        try
        {
            return ExtractText(screenshot);
        }
        finally
        {
            screenshot.Dispose();
        }
    }

    /// <summary>
    /// Extract specific lines containing a keyword
    /// </summary>
    public List<string> FindLines(string text, string keyword)
    {
        return text.Split('\n')
            .Where(line => line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Trim())
            .ToList();
    }

    public void Dispose()
    {
        _engine?.Dispose();
        GC.SuppressFinalize(this);
    }
}
