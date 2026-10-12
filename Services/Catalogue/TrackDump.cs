using System.Text.Json;

namespace WreckfestController.Services.Catalogue;

/// <summary>
/// The parts of a WreckfestTrackDump <c>tracks.json</c> (<c>wftracks dump</c>) the import
/// reads. The format is that tool's <c>docs/output.md</c>; properties not listed here are
/// ignored, as the format asks of readers. Source <c>path</c>s are a PC's local folders and
/// are deliberately not read.
/// </summary>
public sealed record TrackDump(
    int FormatVersion,
    TrackDumpTool? Tool,
    string? GeneratedAt,
    IReadOnlyList<TrackDumpSource>? Sources,
    IReadOnlyList<TrackDumpTrack>? Tracks,
    IReadOnlyList<TrackDumpDiagnostic>? Diagnostics)
{
    /// <summary>The <c>formatVersion</c> this controller reads. A later one may have retyped properties.</summary>
    public const int SupportedFormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
    };

    /// <summary>
    /// Reads a dump, checking its <c>formatVersion</c> before anything else, so a newer file
    /// is refused for its version rather than for whatever it renamed.
    /// </summary>
    /// <param name="refusal">When it is not a dump this controller reads, why: one sentence of ours, safe to answer with.</param>
    public static TrackDump? Read(JsonElement document, out string? refusal)
    {
        refusal = null;
        if (document.ValueKind != JsonValueKind.Object)
        {
            refusal = "This is not a WreckfestTrackDump tracks.json: it is not a JSON object.";
            return null;
        }

        if (!document.TryGetProperty("formatVersion", out var version) || version.ValueKind != JsonValueKind.Number)
        {
            refusal = "This is not a WreckfestTrackDump tracks.json: it has no formatVersion.";
            return null;
        }

        if (!version.TryGetInt32(out var number) || number != SupportedFormatVersion)
        {
            var named = version.TryGetInt32(out _) ? $"formatVersion {number}" : "an unknown formatVersion";
            refusal = $"This tracks.json is {named}, and this controller reads only formatVersion {SupportedFormatVersion}. Update the controller, or dump with a matching WreckfestTrackDump.";
            return null;
        }

        TrackDump? dump;
        try
        {
            dump = document.Deserialize<TrackDump>(Options);
        }
        catch (JsonException ex)
        {
            // The path is a JSON path inside the upload, such as $.tracks[3].variants, never a file.
            refusal = $"This tracks.json does not match formatVersion {SupportedFormatVersion}: the value at {ex.Path ?? "$"} has the wrong type.";
            return null;
        }

        if (dump?.Sources is null || dump.Tracks is null)
        {
            refusal = "This tracks.json has no sources or tracks list.";
            return null;
        }

        return dump;
    }
}

public sealed record TrackDumpTool(string? Name, string? Version);

/// <summary>A game install (<c>base</c>), a workshop item (<c>workshop:&lt;id&gt;</c>) or a mod folder (<c>mod:&lt;folder&gt;</c>).</summary>
public sealed record TrackDumpSource(string? Id, string? Kind, string? Name, string? WorkshopId);

public sealed record TrackDumpText(string? Key, string? Text);

public sealed record TrackDumpTrack(
    string? Id,
    string? Source,
    TrackDumpText? Title,
    IReadOnlyList<TrackDumpVariant>? Variants,
    TrackDumpImages? Images = null);

public sealed record TrackDumpVariant(
    string? Id,
    string? GameId,
    TrackDumpText? Name,
    long GameMode,
    TrackDumpTrackData? TrackData,
    TrackDumpImages? Images = null);

/// <summary>
/// A track's or variant's menu images as source-relative <c>.bmap</c> paths; null where it has
/// none. <c>wftracks imagedump</c> writes each as <c>&lt;source id, ":" as "_"&gt;/&lt;name&gt;.png</c>.
/// The loading, skew and results sizes are not read: the catalogue keeps small and large (#260).
/// </summary>
public sealed record TrackDumpImages(string? Small, string? Large);

public sealed record TrackDumpTrackData(double? TrackLengthM);

public sealed record TrackDumpDiagnostic(string? Severity, string? Code, string? Source, string? Message);
