using System.Text;
using System.Text.Json;

namespace WreckfestController.Services;

public class LaravelWebhookService
{
    private readonly ILogger<LaravelWebhookService> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly string _webhookBaseUrl;

    public LaravelWebhookService(ILogger<LaravelWebhookService> logger, IConfiguration configuration, HttpClient httpClient)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = httpClient;
        _webhookBaseUrl = _configuration["Laravel:WebhookBaseUrl"] ?? "http://localhost:8000/api/webhooks";
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
