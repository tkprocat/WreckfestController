using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using WreckfestController.Controllers;

namespace WreckfestController.Services.Assistant;

/// <summary>
/// What the AI assistant can look up: read-only, and only what the public pages show.
/// </summary>
/// <remarks>
/// Every tool answers from <see cref="PublicController"/> or <see cref="PublicTracksController"/>,
/// the same actions and DTOs anonymous visitors already get, so the model can never see a
/// server password, a Steam id or a cup's server overrides. A new tool must keep to that.
/// </remarks>
public sealed class AssistantTools
{
    public static readonly IReadOnlyList<AssistantToolDefinition> Definitions =
    [
        new("get_server_status",
            "Whether the server is running and for how long, its name, the players on it now (bots marked), the current track and the track rotation.",
            NoParameters()),
        new("get_cups",
            "The cup (a scheduled event) running now, if any, and the upcoming cups with their start and end times and how they repeat.",
            NoParameters()),
        new("get_results",
            "Recent results, newest first: races outside a cup one by one, and each cup run with its standings and races.",
            NoParameters()),
        new("get_recent_races",
            $"Only the latest {PublicController.RaceLimit} finished races, newest first, with every car's position, name, vehicle, best lap and race time. Not for all-time totals: use get_player_standings.",
            NoParameters()),
        new("get_player_standings",
            $"All-time standings over every race on the server: the top {PublicController.StandingLimit} players by wins, with their podiums (top-3 finishes, wins included), races and when they last raced. Bots are left out. Use it for who has won the most, and for how a named player has done overall.",
            NoParameters()),
        new("list_tracks",
            "Every track on the server: its key, name, mod and how many layouts it has. get_track gives a track's layouts.",
            NoParameters()),
        new("get_track",
            "One track by its key (from list_tracks): its layouts and how many races each has had.",
            StringParameter("key", "The track's key, as list_tracks gives it.")),
        new("get_track_layout",
            "One layout by its variantId: its recent races, fastest laps, players with the most wins, typical lap time and the cups that use it.",
            StringParameter("variantId", "The layout's variantId, as get_track gives it.")),
    ];

    private readonly PublicController _public;
    private readonly PublicTracksController _tracks;
    private readonly JsonSerializerOptions _json;

    public AssistantTools(PublicController publicController, PublicTracksController tracks, JsonSerializerOptions json)
    {
        _public = publicController;
        _tracks = tracks;
        _json = json;
    }

    /// <summary>Runs one tool. Answers JSON text; a failed lookup answers <c>{"error": ...}</c>.</summary>
    public async Task<string> RunAsync(string name, string arguments, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case "get_server_status":
                return Answer(await _public.Overview(cancellationToken), o => new
                {
                    o.ServerName,
                    o.MaxPlayers,
                    o.Status,
                    o.CurrentTrack,
                    o.Players,
                    o.Rotation,
                    o.UpdatedAt,
                });
            case "get_cups":
                return Answer(await _public.Overview(cancellationToken), o => new { o.ActiveCup, o.UpcomingCups, o.UpdatedAt });
            case "get_results":
                return Answer(await _public.Feed(cancellationToken));
            case "get_recent_races":
                return Answer(await _public.Races(cancellationToken));
            case "get_player_standings":
                return Answer(await _public.Players(cancellationToken));
            case "list_tracks":
                // Without the layouts, which get_track gives: the base game alone has hundreds,
                // more than one tool result may hold.
                return Answer(await _tracks.Tracks(cancellationToken), tracks => tracks.Select(t => new
                {
                    t.Key,
                    t.Name,
                    t.ModName,
                    LayoutCount = t.Variants.Count,
                }).ToList());
            case "get_track":
                return Argument(arguments, "key") is { } key
                    ? Answer(await _tracks.Track(key, cancellationToken), t => new
                    {
                        t.Key,
                        t.Name,
                        t.ModName,
                        t.WorkshopId,
                        Layouts = t.Variants.Select(v => new { v.VariantId, v.Name, v.GameMode, v.TrackLengthM, v.RaceCount }).ToList(),
                    })
                    : Error("key is required.");
            case "get_track_layout":
                return Argument(arguments, "variantId") is { } variantId
                    ? Answer(await _tracks.Variant(variantId, cancellationToken))
                    : Error("variantId is required.");
            default:
                return Error($"There is no tool called {name}.");
        }
    }

    private string Answer<T>(ActionResult<T> result, Func<T, object>? shape = null)
    {
        if (result.Value is { } value)
        {
            object answer = shape is null ? value : shape(value);
            return JsonSerializer.Serialize(answer, answer.GetType(), _json);
        }

        return result.Result switch
        {
            NotFoundResult or NotFoundObjectResult => Error("Not found."),
            ObjectResult { Value: ProblemDetails { Title: { } title } } => Error(title),
            _ => Error("That is unavailable right now."),
        };
    }

    private static string? Argument(string arguments, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
                    ? value.GetString()!.Trim()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Error(string message) => new JsonObject { ["error"] = message }.ToJsonString();

    private static JsonObject NoParameters() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    private static JsonObject StringParameter(string name, string description) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            [name] = new JsonObject { ["type"] = "string", ["description"] = description },
        },
        ["required"] = new JsonArray((JsonNode)name),
    };
}
