using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using WreckfestController.Models;

namespace WreckfestController.Services;

public class ServerInfoTracker
{
    private readonly ILogger<ServerInfoTracker> _logger;
    private readonly object _lock = new();

    // State for parsing multi-line ? command responses
    private bool _collectingInfoResponse = false;
    private readonly List<string> _infoResponseLines = new();
    private DateTime _infoResponseStartTime = DateTime.MinValue;
    private TaskCompletionSource<ServerConfig>? _infoResponseTask;

    public ServerInfoTracker(ILogger<ServerInfoTracker> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parse a log line and update server info tracking
    /// </summary>
    public void ProcessLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (_lock)
        {
            // Check if we're collecting ? command response
            if (_collectingInfoResponse)
            {
                // Check for config lines: "server_name=value" or similar
                if (Regex.IsMatch(line, @"^\s*\w+\s*="))
                {
                    _infoResponseLines.Add(line);
                    return;
                }
                // Check for end of response (empty line or non-config content)
                else if (string.IsNullOrWhiteSpace(line.Trim()) ||
                         (!line.Trim().Contains("=") && !line.Trim().Equals("?")))
                {
                    // Process the collected response
                    if (_infoResponseLines.Count > 0)
                    {
                        var config = ParseInfoResponse(_infoResponseLines.ToArray());
                        _infoResponseTask?.TrySetResult(config);
                    }
                    else
                    {
                        _infoResponseTask?.TrySetException(new InvalidOperationException("No config lines received"));
                    }
                    _collectingInfoResponse = false;
                    _infoResponseLines.Clear();
                    _infoResponseTask = null;
                }
            }

            // Check for start of ? command response
            // The server might echo the command or start with config directly
            if (line.Trim() == "?" || Regex.IsMatch(line, @"^\s*server_name\s*="))
            {
                _collectingInfoResponse = true;
                _infoResponseStartTime = DateTime.Now;
                _infoResponseLines.Clear();

                // If this line is already a config line, add it
                if (Regex.IsMatch(line, @"^\s*\w+\s*="))
                {
                    _infoResponseLines.Add(line);
                }
                return;
            }

            // If we've been collecting for too long (>2 seconds), abandon it
            if (_collectingInfoResponse && (DateTime.Now - _infoResponseStartTime).TotalSeconds > 2)
            {
                _logger.LogWarning("Server info response collection timed out, abandoning");
                _collectingInfoResponse = false;
                _infoResponseLines.Clear();
                _infoResponseTask?.TrySetException(new TimeoutException("Server info response collection timed out"));
                _infoResponseTask = null;
            }
        }
    }

    /// <summary>
    /// Request server info by sending ? command and waiting for response
    /// </summary>
    public async Task<ServerConfig> RequestServerInfoAsync(TimeSpan timeout)
    {
        lock (_lock)
        {
            _infoResponseTask = new TaskCompletionSource<ServerConfig>();
        }

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var registration = cts.Token.Register(() =>
                _infoResponseTask?.TrySetException(new TimeoutException("Server info request timed out")));

            return await _infoResponseTask.Task;
        }
        catch
        {
            lock (_lock)
            {
                _collectingInfoResponse = false;
                _infoResponseLines.Clear();
                _infoResponseTask = null;
            }
            throw;
        }
    }

    /// <summary>
    /// Parse the response from "?" command
    /// Example output:
    /// "server_name=My Server"
    /// "max_players=24"
    /// "track=laajamaa"
    /// "gamemode=derby"
    /// etc.
    /// </summary>
    private ServerConfig ParseInfoResponse(string[] lines)
    {
        var config = new ServerConfig();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || !trimmed.Contains('='))
                continue;

            var parts = trimmed.Split('=', 2);
            if (parts.Length != 2) continue;

            var key = parts[0].Trim();
            var value = parts[1].Trim();
            config.ApplyConfigValue(key, value);
        }

        _logger.LogInformation("Parsed server info: {ServerName}, Track: {Track}, Gamemode: {Gamemode}",
            config.ServerName, config.Track, config.Gamemode);

        return config;
    }
}
