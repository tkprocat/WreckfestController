using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace WreckfestController.Services;

/// <summary>
/// Buffers console log lines and sends them in batches to Laravel via webhook.
/// This replaces the WebSocket-based log streaming with a simpler HTTP-based approach.
/// </summary>
public class ConsoleLogWebhookSender : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _webhookUrl;
    private readonly List<string> _logBuffer;
    private readonly object _bufferLock = new();
    private readonly Timer? _flushTimer;
    private readonly ILogger<ConsoleLogWebhookSender> _logger;
    private bool _disposed;

    // Configuration
    private const int FlushIntervalMs = 1000; // 1 second
    private const int MaxBufferSize = 1000; // Max lines to buffer before forced flush

    public ConsoleLogWebhookSender(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<ConsoleLogWebhookSender> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _logBuffer = new List<string>();

        // Get webhook URL from configuration
        var baseUrl = configuration["Laravel:WebhookBaseUrl"];
        if (string.IsNullOrEmpty(baseUrl))
        {
            _logger.LogWarning("Laravel:WebhookBaseUrl not configured, console log webhooks disabled");
            _webhookUrl = string.Empty;
            return;
        }

        _webhookUrl = $"{baseUrl.TrimEnd('/')}/console-logs";
        _logger.LogInformation("Console log webhook sender initialized: {Url}", _webhookUrl);

        // Start flush timer
        _flushTimer = new Timer(FlushLogs, null, TimeSpan.FromMilliseconds(FlushIntervalMs), TimeSpan.FromMilliseconds(FlushIntervalMs));
    }

    /// <summary>
    /// Adds a log line to the buffer for sending
    /// </summary>
    public void AddLog(string logLine)
    {
        if (string.IsNullOrEmpty(_webhookUrl) || _disposed)
            return;

        lock (_bufferLock)
        {
            _logBuffer.Add(logLine);

            // Force flush if buffer is getting too large
            if (_logBuffer.Count >= MaxBufferSize)
            {
                _logger.LogDebug("Buffer size limit reached ({Size} lines), forcing flush", _logBuffer.Count);
                _ = Task.Run(() => FlushLogsAsync());
            }
        }
    }

    /// <summary>
    /// Timer callback - flushes logs to Laravel
    /// </summary>
    private void FlushLogs(object? state)
    {
        _ = FlushLogsAsync();
    }

    /// <summary>
    /// Sends buffered logs to Laravel webhook
    /// </summary>
    private async Task FlushLogsAsync()
    {
        if (string.IsNullOrEmpty(_webhookUrl) || _disposed)
            return;

        List<string> logsToSend;

        lock (_bufferLock)
        {
            if (_logBuffer.Count == 0)
                return;

            logsToSend = new List<string>(_logBuffer);
            _logBuffer.Clear();
        }

        try
        {
            var payload = new { logs = logsToSend };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(_webhookUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to send console logs webhook: {StatusCode} ({Count} lines)",
                    response.StatusCode,
                    logsToSend.Count);
            }
            else
            {
                _logger.LogDebug("Sent {Count} console log lines to Laravel", logsToSend.Count);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error sending console logs webhook ({Count} lines)", logsToSend.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending console logs webhook ({Count} lines)", logsToSend.Count);
        }
    }

    /// <summary>
    /// Flushes any remaining logs before disposal
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _logger.LogInformation("Disposing console log webhook sender");

        // Stop timer
        _flushTimer?.Dispose();

        // Flush any remaining logs synchronously
        if (_logBuffer.Count > 0)
        {
            _logger.LogInformation("Flushing {Count} remaining console logs", _logBuffer.Count);
            FlushLogsAsync().Wait(TimeSpan.FromSeconds(5));
        }

        _httpClient?.Dispose();
    }
}
