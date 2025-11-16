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
    private readonly string _webhookBaseUrl;

    public WreckfestWebWebhookService(ILogger<WreckfestWebWebhookService> logger, IConfiguration configuration, HttpClient httpClient)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = httpClient;
        _webhookBaseUrl = _configuration["WreckfestWeb:WebhookBaseUrl"] ?? "http://localhost:8000/api/webhooks";
    }

    public async Task SendPlayersUpdatedAsync(List<Models.Player> players)
    {
        try
        {
            var payload = new
            {
                players = players.Select(p => new
                {
                    name = p.Name,
                    isBot = p.IsBot
                }).ToList()
            };
            await PostWebhookAsync("players-updated", payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send players updated webhook");
        }
    }

    [Obsolete("Use SendPlayersUpdatedAsync instead")]
    public async Task SendPlayerJoinedAsync(string playerName, bool isBot)
    {
        try
        {
            var payload = new { playerName, isBot };
            await PostWebhookAsync("player-joined", payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send player joined webhook for {PlayerName}", playerName);
        }
    }

    [Obsolete("Use SendPlayersUpdatedAsync instead")]
    public async Task SendPlayerLeftAsync(string playerName)
    {
        try
        {
            var payload = new { playerName };
            await PostWebhookAsync("player-left", payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send player left webhook for {PlayerName}", playerName);
        }
    }

    public async Task SendTrackChangedAsync(string trackId)
    {
        try
        {
            var payload = new { trackId };
            await PostWebhookAsync("track-changed", payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send track changed webhook for {TrackId}", trackId);
        }
    }

    public async Task SendEventActivatedAsync(int eventId, string eventName)
    {
        try
        {
            var payload = new
            {
                eventId,
                eventName,
                timestamp = DateTime.UtcNow
            };
            await PostWebhookAsync("event-activated", payload);
            _logger.LogInformation("Sent event activation webhook for {EventName} (ID: {EventId})", eventName, eventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send event activated webhook for {EventName} (ID: {EventId})", eventName, eventId);
        }
    }

    public async Task SendServerStartedAsync(Models.ServerStartedEvent serverEvent)
    {
        try
        {
            var payload = new
            {
                processId = serverEvent.ProcessId,
                processName = serverEvent.ProcessName,
                startTime = serverEvent.StartTime,
                timestamp = DateTime.UtcNow
            };
            await PostWebhookAsync("server-lifecycle/started", payload);
            _logger.LogInformation("Sent server started webhook for PID {ProcessId}", serverEvent.ProcessId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send server started webhook for PID {ProcessId}", serverEvent.ProcessId);
        }
    }

    public async Task SendServerStoppedAsync(Models.ServerStoppedEvent serverEvent)
    {
        try
        {
            var payload = new
            {
                processId = serverEvent.ProcessId,
                stopMethod = serverEvent.StopMethod,
                timestamp = DateTime.UtcNow
            };
            await PostWebhookAsync("server-lifecycle/stopped", payload);
            _logger.LogInformation("Sent server stopped webhook for PID {ProcessId} ({StopMethod})", serverEvent.ProcessId, serverEvent.StopMethod);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send server stopped webhook for PID {ProcessId}", serverEvent.ProcessId);
        }
    }

    public async Task SendServerRestartedAsync(Models.ServerRestartedEvent serverEvent)
    {
        try
        {
            var payload = new
            {
                oldProcessId = serverEvent.OldProcessId,
                newProcessId = serverEvent.NewProcessId,
                restartMethod = serverEvent.RestartMethod,
                timestamp = DateTime.UtcNow
            };
            await PostWebhookAsync("server-lifecycle/restarted", payload);
            _logger.LogInformation("Sent server restarted webhook: {OldPid} -> {NewPid} ({RestartMethod})",
                serverEvent.OldProcessId, serverEvent.NewProcessId, serverEvent.RestartMethod);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send server restarted webhook");
        }
    }

    public async Task SendServerAttachedAsync(Models.ServerAttachedEvent serverEvent)
    {
        try
        {
            var payload = new
            {
                processId = serverEvent.ProcessId,
                processName = serverEvent.ProcessName,
                startTime = serverEvent.StartTime,
                timestamp = DateTime.UtcNow
            };
            await PostWebhookAsync("server-lifecycle/attached", payload);
            _logger.LogInformation("Sent server attached webhook for PID {ProcessId}", serverEvent.ProcessId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send server attached webhook for PID {ProcessId}", serverEvent.ProcessId);
        }
    }

    public async Task SendServerRestartPendingAsync(Models.ServerRestartPendingEvent serverEvent)
    {
        try
        {
            var payload = new
            {
                minutesRemaining = serverEvent.MinutesRemaining,
                eventName = serverEvent.EventName,
                eventId = serverEvent.EventId,
                scheduledRestartTime = serverEvent.ScheduledRestartTime,
                timestamp = DateTime.UtcNow
            };
            await PostWebhookAsync("server-lifecycle/restart-pending", payload);
            _logger.LogInformation("Sent server restart pending webhook: {Minutes} minutes remaining", serverEvent.MinutesRemaining);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send server restart pending webhook");
        }
    }

    private async Task PostWebhookAsync(string endpoint, object payload)
    {
        var url = $"{_webhookBaseUrl}/{endpoint}";
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
}
