using System.Net;
using System.Text;

namespace WreckfestPortal.Tests;

/// <summary>Answers for the controllers, by host and port: a body, a status, or nothing at all.</summary>
internal sealed class FakeController : HttpMessageHandler
{
    private readonly Dictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> _answers = new();

    public List<Uri> Requests { get; } = [];

    public void Answer(string apiUrl, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        _answers[Key(new Uri(apiUrl))] = _ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    /// <summary>Never answers: the request runs until the portal gives up on it.</summary>
    public void Hang(string apiUrl) =>
        _answers[Key(new Uri(apiUrl))] = async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        };

    /// <summary>Fails with this, as reading a broken response would.</summary>
    public void Throw(string apiUrl, Exception error) =>
        _answers[Key(new Uri(apiUrl))] = _ => Task.FromException<HttpResponseMessage>(error);

    public void Refuse(string apiUrl) =>
        _answers[Key(new Uri(apiUrl))] = _ => throw new HttpRequestException("Connection refused");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request.RequestUri!);
        }

        return _answers.TryGetValue(Key(request.RequestUri!), out var answer)
            ? answer(cancellationToken)
            : throw new HttpRequestException("Connection refused");
    }

    private static string Key(Uri uri) => uri.Authority;

    public static string Overview(bool isRunning = true, string? serverName = "^5Monday Night", int humans = 3, int bots = 1, string? track = "Big Valley - Main Route") =>
        $$"""
        {
          "serverName": {{Quote(serverName)}},
          "maxPlayers": 24,
          "status": { "isRunning": {{(isRunning ? "true" : "false")}}, "uptimeSeconds": 60 },
          "currentTrack": {{(track is null ? "null" : $$"""{ "id": "valley_1", "name": "{{track}}", "trackKey": null }""")}},
          "players": { "humans": {{humans}}, "bots": {{bots}}, "list": [] },
          "rotation": { "name": null, "tracks": [] },
          "activeCup": null,
          "upcomingCups": [],
          "updatedAt": "2026-10-14T18:00:00Z"
        }
        """;

    private static string Quote(string? value) => value is null ? "null" : "\"" + value + "\"";
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
