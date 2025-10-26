using System.Text.RegularExpressions;
using WreckfestController.Models;

namespace WreckfestController.Services;

/// <summary>
/// Tracks players using OCR from console window screenshots
/// Triggered by events like race start, player join/leave rather than polling
/// </summary>
public class OcrPlayerTracker : IDisposable
{
    private readonly ILogger<OcrPlayerTracker> _logger;
    private readonly IConfiguration _configuration;
    private readonly PlayerTracker _playerTracker;
    private readonly ConsoleOcr? _ocr;
    private readonly ConsoleWriter _consoleWriter;
    private readonly ConsoleReader _consoleReader;
    private bool _isEnabled;
    private DateTime _lastOcrUpdate = DateTime.MinValue;
    private readonly TimeSpan _minUpdateInterval = TimeSpan.FromSeconds(3); // Prevent rapid successive OCR calls
    private readonly object _ocrLock = new();

    public OcrPlayerTracker(
        ILogger<OcrPlayerTracker> logger,
        IConfiguration configuration,
        PlayerTracker playerTracker,
        ILogger<ConsoleWriter> consoleWriterLogger,
        ILogger<ConsoleOcr> consoleOcrLogger)
    {
        _logger = logger;
        _configuration = configuration;
        _playerTracker = playerTracker;
        _consoleWriter = new ConsoleWriter(consoleWriterLogger);
        _consoleReader = new ConsoleReader();

        // Check if OCR tracking is enabled
        _isEnabled = _configuration.GetValue<bool>("WreckfestServer:EnableOcrPlayerTracking");

        if (_isEnabled)
        {
            try
            {
                _ocr = new ConsoleOcr(consoleOcrLogger);
                _logger.LogInformation("OCR Player Tracking enabled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize OCR engine. OCR player tracking disabled.");
                _isEnabled = false;
            }
        }
        else
        {
            _logger.LogInformation("OCR Player Tracking disabled in configuration");
        }
    }

    /// <summary>
    /// Trigger OCR player list update
    /// </summary>
    public async Task TriggerUpdateAsync(string reason)
    {
        if (!_isEnabled || _ocr == null)
        {
            return;
        }

        // Prevent rapid successive OCR calls
        if (DateTime.Now - _lastOcrUpdate < _minUpdateInterval)
        {
            _logger.LogDebug("Skipping OCR update (too soon since last update): {Reason}", reason);
            return;
        }

        // Use lock to prevent concurrent OCR operations
        if (!Monitor.TryEnter(_ocrLock))
        {
            _logger.LogDebug("Skipping OCR update (another OCR operation in progress): {Reason}", reason);
            return;
        }

        try
        {
            _logger.LogInformation("Triggering OCR player list update: {Reason}", reason);

            // Find console window
            IntPtr windowHandle = _consoleReader.FindConsoleWindow();
            if (windowHandle == IntPtr.Zero)
            {
                _logger.LogWarning("Cannot perform OCR: Console window not found");
                return;
            }

            // Send "list" command to get player list
            _logger.LogDebug("Sending 'list' command to console");
            if (!_consoleWriter.SendCommand(windowHandle, "list" + Environment.NewLine))
            {
                _logger.LogWarning("Failed to send 'list' command to console");
                return;
            }

            // Wait for command to execute and output to appear
            await Task.Delay(2000);

            // Get window dimensions from config
            var targetWidth = _configuration.GetValue<int?>("WreckfestServer:OcrWindowWidth") ?? 993;
            var targetHeight = _configuration.GetValue<int?>("WreckfestServer:OcrWindowHeight") ?? 1040;

            // Capture and read console window
            _logger.LogDebug("Capturing console window ({Width}x{Height}) and running OCR", targetWidth, targetHeight);
            string ocrText = _ocr.ReadConsoleWindow(windowHandle, targetWidth, targetHeight);

            if (string.IsNullOrWhiteSpace(ocrText))
            {
                _logger.LogWarning("OCR extracted no text from console");
                return;
            }

            // Parse OCR output
            var players = ParseOcrPlayerList(ocrText);
            _logger.LogInformation("OCR extracted {Count} players", players.Count);

            // Update player tracking
            UpdatePlayerTracking(players);

            _lastOcrUpdate = DateTime.Now;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during OCR player list update");
        }
        finally
        {
            Monitor.Exit(_ocrLock);
        }
    }

    /// <summary>
    /// Parse OCR text to extract player information
    /// Expected format from real server:
    /// "1:    0 [0] Procat A                         | [C] 148 Sofa Car (L)         | ping: 6     | not ready"
    /// "2:    0 [0] *timmy64                         | [C] 103 Wildking             | ping: 0     | ready"
    /// Format: LineNum: ... [slot] PlayerName | [Class] Score VehicleName | ping: X | status
    /// </summary>
    private List<OcrPlayerData> ParseOcrPlayerList(string ocrText)
    {
        var players = new List<OcrPlayerData>();
        var lines = ocrText.Split('\n');

        foreach (var line in lines)
        {
            // Pattern breakdown:
            // ^\s*(\d+): - Line number (player ID)
            // \s+\d+\s+\[\d+\] - Skip the "0 [0]" part
            // \s+(\*?) - Optional asterisk for bot
            // (.+?) - Player name (can have spaces, non-greedy)
            // \s+\| - Pipe separator
            // \s+\[[ABC]\] - Class indicator
            // \s+(\d+) - Score
            // \s+(.+?) - Vehicle name (can have spaces and (L) suffix, non-greedy)
            // \s+\| - Pipe separator
            // \s+ping:\s+(\d+) - Ping

            var match = Regex.Match(line, @"^\s*(\d+):\s+\d+\s+\[\d+\]\s+(\*?)(.+?)\s+\|\s+\[[ABC]\]\s+(\d+)\s+(.+?)\s+\|\s+ping:\s+(\d+)", RegexOptions.IgnoreCase);

            if (match.Success)
            {
                var playerId = int.Parse(match.Groups[1].Value);
                var isBot = match.Groups[2].Value == "*";
                var playerName = match.Groups[3].Value.Trim();
                var score = int.Parse(match.Groups[4].Value);
                var vehicle = match.Groups[5].Value.Trim();

                players.Add(new OcrPlayerData
                {
                    PlayerId = playerId,
                    Name = playerName,
                    Score = score,
                    Vehicle = vehicle,
                    IsBot = isBot
                });

                _logger.LogDebug("Parsed player from OCR: ID={PlayerId}, Name={Name}, Score={Score}, Vehicle={Vehicle}, IsBot={IsBot}",
                    playerId, playerName, score, vehicle, isBot);
            }
        }

        return players;
    }

    /// <summary>
    /// Update PlayerTracker with OCR data
    /// </summary>
    private void UpdatePlayerTracking(List<OcrPlayerData> ocrPlayers)
    {
        var onlinePlayers = _playerTracker.GetOnlinePlayers();

        foreach (var ocrPlayer in ocrPlayers)
        {
            // Find matching player in tracking (by name)
            var trackedPlayer = onlinePlayers.FirstOrDefault(p => p.Name == ocrPlayer.Name);

            if (trackedPlayer != null)
            {
                // Update existing player with OCR data
                trackedPlayer.PlayerId = ocrPlayer.PlayerId;
                trackedPlayer.Score = ocrPlayer.Score;
                trackedPlayer.Vehicle = ocrPlayer.Vehicle;
                trackedPlayer.IsBot = ocrPlayer.IsBot; // Update IsBot from OCR (more reliable)
                _logger.LogDebug("Updated player {Name} with OCR data: ID={PlayerId}, Score={Score}, Vehicle={Vehicle}",
                    ocrPlayer.Name, ocrPlayer.PlayerId, ocrPlayer.Score, ocrPlayer.Vehicle);
            }
            else
            {
                _logger.LogDebug("OCR found player not in tracking: {Name} (may have just joined)", ocrPlayer.Name);
            }
        }
    }

    public void Dispose()
    {
        _ocr?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Player data extracted from OCR
/// </summary>
internal class OcrPlayerData
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
    public string Vehicle { get; set; } = string.Empty;
    public bool IsBot { get; set; }
}
