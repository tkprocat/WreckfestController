using System.Text.Json;
using System.Text.Json.Nodes;

namespace WreckfestController.Tests.Services.Catalogue;

/// <summary>
/// Builds a WreckfestTrackDump tracks.json (formatVersion 1) by hand, with every property the
/// format's schema requires, so tests need no real dump of game data.
/// </summary>
public sealed class TrackDumpFixture
{
    private readonly JsonArray _sources = [];
    private readonly JsonArray _tracks = [];
    private readonly JsonArray _diagnostics = [];

    public int FormatVersion { get; set; } = 1;

    public TrackDumpFixture Base()
    {
        _sources.Add(new JsonObject
        {
            ["id"] = "base", ["kind"] = "base", ["name"] = null, ["workshopId"] = null, ["path"] = "C:/Games/Wreckfest",
        });
        return this;
    }

    public TrackDumpFixture Workshop(string workshopId, string name)
    {
        _sources.Add(new JsonObject
        {
            ["id"] = $"workshop:{workshopId}", ["kind"] = "workshop", ["name"] = name, ["workshopId"] = workshopId,
            ["path"] = $"C:/Steam/steamapps/workshop/content/228380/{workshopId}",
        });
        return this;
    }

    /// <param name="variants">Each variant's game id, name, game mode and track length.</param>
    public TrackDumpFixture Track(string source, string folder, string? title, params (string GameId, string? Name, int Mode, double? LengthM)[] variants)
    {
        var id = $"{source}/{folder}";
        var list = new JsonArray();
        foreach (var (gameId, name, mode, length) in variants)
        {
            list.Add(new JsonObject
            {
                ["id"] = $"{id}/{gameId.ToLowerInvariant()}",
                ["gameId"] = gameId,
                ["file"] = File($"data/property/event/{folder}/{gameId}.evse", 17),
                ["name"] = name is null ? null : Text(name),
                ["scene"] = $"data/art/tracks/{folder}/{gameId}.scne",
                ["gameMode"] = mode,
                ["laps"] = 5,
                ["images"] = Images(),
                ["trackData"] = new JsonObject
                {
                    ["file"] = File($"data/property/track/{gameId}.tcat", 20),
                    ["trackLengthM"] = mode == 4 ? null : length,
                    ["arenaAreaM2"] = mode == 4 ? length : null,
                    ["surfaces"] = null,
                    ["pitLane"] = new JsonObject { ["speedLimitKmh"] = null, ["detectionDistanceM"] = null, ["stoppingDistanceM"] = null },
                    ["lapReferenceTimeMs"] = null,
                    ["checkpointRace"] = new JsonObject { ["startTimeMs"] = null, ["firstSpanOptimalTimeMs"] = null },
                },
            });
        }

        _tracks.Add(new JsonObject
        {
            ["id"] = id,
            ["source"] = source,
            ["file"] = File($"data/property/event/{folder}/{folder}.envi", 3),
            ["title"] = title is null ? null : Text(title),
            ["description"] = null,
            ["images"] = Images(),
            ["variants"] = list,
        });
        return this;
    }

    /// <summary>
    /// Sets the menu images of a track (by dump id, <c>source/folder</c>) or a variant (by
    /// game id) to <c>data/menu/textures/&lt;name&gt;.bmap</c> paths; null for none.
    /// </summary>
    public TrackDumpFixture Images(string trackOrGameId, string? small, string? large)
    {
        foreach (var track in _tracks.Select(t => t!.AsObject()))
        {
            if ((string?)track["id"] == trackOrGameId)
            {
                SetImages(track, small, large);
                return this;
            }

            foreach (var variant in track["variants"]!.AsArray().Select(v => v!.AsObject()))
            {
                if ((string?)variant["gameId"] == trackOrGameId)
                {
                    SetImages(variant, small, large);
                    return this;
                }
            }
        }

        throw new ArgumentException($"No track or variant {trackOrGameId}.", nameof(trackOrGameId));

        static void SetImages(JsonObject row, string? small, string? large)
        {
            var images = row["images"]!.AsObject();
            images["small"] = small is null ? null : $"data/menu/textures/{small}.bmap";
            images["large"] = large is null ? null : $"data/menu/textures/{large}.bmap";
        }
    }

    public TrackDumpFixture Diagnostic(string code, string message, string severity = "warning")
    {
        _diagnostics.Add(new JsonObject
        {
            ["severity"] = severity, ["code"] = code, ["source"] = null, ["file"] = null, ["type"] = null,
            ["typeVersion"] = null, ["affects"] = null, ["omitted"] = null, ["message"] = message,
        });
        return this;
    }

    public JsonObject ToJsonObject() => new()
    {
        ["formatVersion"] = FormatVersion,
        ["tool"] = new JsonObject { ["name"] = "WreckfestTrackDump", ["version"] = "1.0.0" },
        ["generatedAt"] = "2026-10-06T14:03:12Z",
        ["sources"] = _sources.DeepClone(),
        ["tracks"] = _tracks.DeepClone(),
        ["diagnostics"] = _diagnostics.DeepClone(),
    };

    public string ToJson() => ToJsonObject().ToJsonString();

    public JsonElement ToElement() => JsonDocument.Parse(ToJson()).RootElement;

    private static JsonObject Text(string text) => new() { ["key"] = "EVENT_TITLE_1", ["text"] = text };

    private static JsonObject File(string path, int version) => new() { ["path"] = path, ["pack"] = null, ["schemaVersion"] = version };

    private static JsonObject Images() => new()
    {
        ["small"] = null, ["large"] = null, ["loading"] = null, ["skew"] = null, ["results"] = null,
    };
}
