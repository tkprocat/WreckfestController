using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace WreckfestPortal;

/// <summary>
/// Asks every controller for its public overview every <see cref="PortalOptions.PollSeconds"/>,
/// all at once, and keeps what each said. The page reads the cards, so visitors never cause
/// a request to a controller, however many there are.
/// </summary>
public sealed class ServerPoller : BackgroundService
{
    public const string HttpClientName = "controllers";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PortalOptions _options;
    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _time;
    private readonly ILogger<ServerPoller> _logger;
    private volatile IReadOnlyList<ServerCard> _cards;

    public ServerPoller(IOptions<PortalOptions> options, IHttpClientFactory http, TimeProvider time, ILogger<ServerPoller> logger)
    {
        _options = options.Value;
        _http = http;
        _time = time;
        _logger = logger;
        _cards = _options.Servers
            .Select(s => new ServerCard(s.Id, s.Name, s.Description, s.Link, ServerState.Checking, null, null, null, null, null, null, null))
            .ToList();
    }

    /// <summary>Every server, in the configured order, as at the last check.</summary>
    public IReadOnlyList<ServerCard> Cards => _cards;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PollSeconds), _time);
        do
        {
            await PollOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Checks every server once and replaces the cards.</summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var previous = _cards;
        var cards = await Task.WhenAll(_options.Servers.Select((server, i) => CheckAsync(server, previous[i], cancellationToken)));
        _cards = cards;
    }

    private async Task<ServerCard> CheckAsync(PortalServer server, ServerCard previous, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var client = _http.CreateClient(HttpClientName);
            var overview = await client.GetFromJsonAsync<Overview>(OverviewUrl(server), Json, timeout.Token)
                ?? throw new JsonException("The overview was empty.");
            if (overview.Status is null)
            {
                throw new JsonException("The overview has no status.");
            }

            if (previous.State is ServerState.Unreachable or ServerState.Checking)
            {
                _logger.LogInformation("{Server} answers", server.Name);
            }

            return previous with
            {
                State = overview.Status.IsRunning ? ServerState.Online : ServerState.ServerStopped,
                ServerName = string.IsNullOrWhiteSpace(overview.ServerName) ? null : overview.ServerName,
                Humans = overview.Players?.Humans ?? 0,
                Bots = overview.Players?.Bots ?? 0,
                MaxPlayers = overview.MaxPlayers,
                CurrentTrack = overview.CurrentTrack?.Name,
                CheckedAt = now,
                LastSeenAt = now,
            };
        }
        // Anything a controller or the network can do wrong: a body cut short, a bad charset,
        // a timeout. Only the portal stopping is not this server's state; nothing else may
        // escape, or it would stop the portal.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Logged once when it goes away, not every poll while it stays away.
            if (previous.State is not ServerState.Unreachable)
            {
                _logger.LogWarning("{Server} does not answer at {Url}: {Error}", server.Name, OverviewUrl(server), ex.Message);
            }

            // What it said last is kept, for the page to show as last known.
            return previous with { State = ServerState.Unreachable, CheckedAt = now };
        }
    }

    private static string OverviewUrl(PortalServer server) => server.ApiUrl.TrimEnd('/') + "/api/public/overview";
}
