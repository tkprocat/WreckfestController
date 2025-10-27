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

            // Configure for better console text recognition
            // PSM 6: Assume a single uniform block of text (best for structured console output)
            _engine.DefaultPageSegMode = PageSegMode.SingleBlock;

            // Additional settings to improve accuracy for console/monospace fonts
            // These settings help with fixed-width fonts and structured data
            _engine.SetVariable("tessedit_char_whitelist", "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz[]|():*. -");
            _engine.SetVariable("preserve_interword_spaces", "1"); // Important for structured data

            _logger.LogInformation("Tesseract OCR engine initialized successfully with PSM mode: {Mode}", _engine.DefaultPageSegMode);
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
            // Preprocess image for better OCR accuracy
            using var preprocessed = PreprocessImage(image);

            // Convert Bitmap to byte array, then to Pix
            using var ms = new System.IO.MemoryStream();
            preprocessed.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
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
    /// Preprocess image to improve OCR accuracy
    /// - Convert to grayscale
    /// - Increase contrast
    /// - Apply threshold for black and white
    /// - Invert colors (console has white text on black background)
    /// Uses fast LockBits method for pixel manipulation
    /// </summary>
    private Bitmap PreprocessImage(Bitmap original)
    {
        // Create a new bitmap for preprocessing (scale up 2x for better OCR accuracy)
        int scaleFactor = 2;
        var processed = new Bitmap(original.Width * scaleFactor, original.Height * scaleFactor, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

        // First, scale up the image for better OCR
        using (var g = System.Drawing.Graphics.FromImage(processed))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor; // Preserve sharp edges for text
            g.DrawImage(original, 0, 0, processed.Width, processed.Height);
        }

        // Now apply binary threshold with inversion
        var rect = new Rectangle(0, 0, processed.Width, processed.Height);
        var data = processed.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

        try
        {
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                int bytes = Math.Abs(data.Stride) * processed.Height;

                for (int i = 0; i < bytes; i += 3)
                {
                    // Get RGB values
                    byte b = ptr[i];
                    byte g = ptr[i + 1];
                    byte r = ptr[i + 2];

                    // Convert to grayscale
                    int gray = (int)(r * 0.299 + g * 0.587 + b * 0.114);

                    // Apply threshold - console text is bright on dark background
                    // Lower threshold to 100 to capture slightly dimmer text
                    byte newValue = (byte)(gray > 100 ? 255 : 0);

                    // Invert - OCR works better with black text on white background
                    newValue = (byte)(255 - newValue);

                    // Set all RGB channels to the same value (grayscale)
                    ptr[i] = newValue;     // B
                    ptr[i + 1] = newValue; // G
                    ptr[i + 2] = newValue; // R
                }
            }
        }
        finally
        {
            processed.UnlockBits(data);
        }

        _logger.LogDebug("Image preprocessed: scaled {ScaleFactor}x and converted to binary black/white", scaleFactor);
        return processed;
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
