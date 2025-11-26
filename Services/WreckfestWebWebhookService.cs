using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WreckfestController.Services;

public class WreckfestWebWebhookService
{
    private readonly ILogger<WreckfestWebWebhookService> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;

    public WreckfestWebWebhookService(ILogger<WreckfestWebWebhookService> logger, IConfiguration configuration, HttpClient httpClient)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Gets the webhook base URL from configuration (supports hot-reload)
    /// </summary>
    private string GetWebhookBaseUrl()
    {
        return _configuration["WreckfestWeb:WebhookBaseUrl"] ?? "http://localhost:8000/api/webhooks";
    }

    public async Task SendPlayersUpdatedAsync(List<Models.Player> players)
    {
        var payload = new
        {
            players = players.Select(p => new
            {
                name = p.Name,
                playerId = p.PlayerId,
                score = p.Score,
                vehicle = p.Vehicle,
                slot = p.Slot,
                isBot = p.IsBot,
                joinedAt = p.JoinedAt
            }).ToList()
        };
        await SendWebhook("players-updated", payload, "", "Failed to send players updated webhook");
    }

    public async Task SendPlayerJoinedAsync(string playerName, bool isBot)
    {
        var payload = new { playerName, isBot };
        await SendWebhook("player-joined", payload, "", $"Failed to send player joined webhook for {playerName}");
    }

    public async Task SendPlayerLeftAsync(string playerName)
    {
        var payload = new { playerName };
        await SendWebhook("player-left", payload, "", $"Failed to send player left webhook for {playerName}");
    }

    public async Task SendTrackChangedAsync(string trackId)
    {
        var payload = new { trackId };
        await SendWebhook("track-changed", payload, "", $"Failed to send track changed webhook for {trackId}");
    }

    public async Task SendEventActivatedAsync(int eventId, string eventName)
    {
        var payload = new
        {
            eventId,
            eventName,
            timestamp = DateTime.UtcNow
        };
        await SendWebhook(
            "event-activated",
            payload,
            $"Sent event activation webhook for {eventName} (ID: {eventId})",
            $"Failed to send event activated webhook for {eventName} (ID: {eventId})");
    }

    public async Task SendServerStartedAsync(Models.ServerStartedEvent serverEvent)
    {
        var payload = new
        {
            processId = serverEvent.ProcessId,
            processName = serverEvent.ProcessName,
            startTime = serverEvent.StartTime,
            timestamp = DateTime.UtcNow
        };
        await SendWebhook(
            "server-started",
            payload,
            $"Sent server started webhook for PID {serverEvent.ProcessId}",
            $"Failed to send server started webhook for PID {serverEvent.ProcessId}");
    }

    public async Task SendServerStoppedAsync(Models.ServerStoppedEvent serverEvent)
    {
        var payload = new
        {
            processId = serverEvent.ProcessId,
            stopMethod = serverEvent.StopMethod,
            timestamp = DateTime.UtcNow
        };
        await SendWebhook(
            "server-stopped",
            payload,
            $"Sent server stopped webhook for PID {serverEvent.ProcessId} ({serverEvent.StopMethod})",
            $"Failed to send server stopped webhook for PID {serverEvent.ProcessId}");
    }

    public async Task SendServerRestartedAsync(Models.ServerRestartedEvent serverEvent)
    {
        var payload = new
        {
            oldProcessId = serverEvent.OldProcessId,
            newProcessId = serverEvent.NewProcessId,
            restartMethod = serverEvent.RestartMethod,
            timestamp = DateTime.UtcNow
        };
        await SendWebhook(
            "server-restarted",
            payload,
            $"Sent server restarted webhook: {serverEvent.OldProcessId} -> {serverEvent.NewProcessId} ({serverEvent.RestartMethod})",
            "Failed to send server restarted webhook");
    }

    public async Task SendServerAttachedAsync(Models.ServerAttachedEvent serverEvent)
    {
        var payload = new
        {
            processId = serverEvent.ProcessId,
            processName = serverEvent.ProcessName,
            startTime = serverEvent.StartTime,
            timestamp = DateTime.UtcNow
        };
        await SendWebhook(
            "server-attached",
            payload,
            $"Sent server attached webhook for PID {serverEvent.ProcessId}",
            $"Failed to send server attached webhook for PID {serverEvent.ProcessId}");
    }

    public async Task SendServerRestartPendingAsync(Models.ServerRestartPendingEvent serverEvent)
    {
        var payload = new
        {
            minutesRemaining = serverEvent.MinutesRemaining,
            eventName = serverEvent.EventName,
            eventId = serverEvent.EventId,
            scheduledRestartTime = serverEvent.ScheduledRestartTime,
            timestamp = DateTime.UtcNow
        };
        await SendWebhook(
            "server-restart-pending",
            payload,
            $"Sent server restart pending webhook: {serverEvent.MinutesRemaining} minutes remaining",
            "Failed to send server restart pending webhook");
    }

    private async Task PostWebhookAsync(string endpoint, object payload)
    {
        var webhookBaseUrl = GetWebhookBaseUrl();
        var url = $"{webhookBaseUrl}/{endpoint}";
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content);

        if (response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Webhook sent successfully to {Url}", url);
        }
        else
        {
            _logger.LogWarning("Webhook failed with status {StatusCode} to {Url}", response.StatusCode, url);
        }
    }

    private async Task SendWebhook(string endpoint, object payload, string successMessage, string errorMessage)
    {
        try
        {
            await PostWebhookAsync(endpoint, payload);
            if (!string.IsNullOrEmpty(successMessage))
            {
                _logger.LogInformation(successMessage);
            }
        }
        catch (Exception ex)
        {
            var webhookBaseUrl = GetWebhookBaseUrl();
            _logger.LogError(ex, "{ErrorMessage} to {Url}", errorMessage, $"{webhookBaseUrl}/{endpoint}");
        }
    }
}
