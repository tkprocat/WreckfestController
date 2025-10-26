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
        // Create a new bitmap for preprocessing
        var processed = new Bitmap(original.Width, original.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

        // Lock bits for fast pixel access
        var rect = new Rectangle(0, 0, original.Width, original.Height);
        var srcData = original.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var dstData = processed.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

        try
        {
            unsafe
            {
                byte* srcPtr = (byte*)srcData.Scan0;
                byte* dstPtr = (byte*)dstData.Scan0;
                int bytes = Math.Abs(srcData.Stride) * original.Height;

                for (int i = 0; i < bytes; i += 3)
                {
                    // Get RGB values
                    byte b = srcPtr[i];
                    byte g = srcPtr[i + 1];
                    byte r = srcPtr[i + 2];

                    // Convert to grayscale
                    int gray = (int)(r * 0.299 + g * 0.587 + b * 0.114);

                    // Apply threshold - console text is bright on dark background
                    // Threshold at 128 - anything brighter becomes white, darker becomes black
                    byte newValue = (byte)(gray > 128 ? 255 : 0);

                    // Invert - OCR works better with black text on white background
                    newValue = (byte)(255 - newValue);

                    // Set all RGB channels to the same value (grayscale)
                    dstPtr[i] = newValue;     // B
                    dstPtr[i + 1] = newValue; // G
                    dstPtr[i + 2] = newValue; // R
                }
            }
        }
        finally
        {
            original.UnlockBits(srcData);
            processed.UnlockBits(dstData);
        }

        _logger.LogDebug("Image preprocessed: converted to binary black/white using fast LockBits method");
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
