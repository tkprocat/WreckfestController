using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WreckfestController.Models;
using WreckfestController.Services.Config;
using WreckfestController.Services.Voting;

namespace WreckfestController.Controllers;

/// <summary>
/// One settings section as the API exposes it: its route name, the fields a client may
/// read and change, and what a change must satisfy.
/// </summary>
/// <remarks>
/// Only the sections in <see cref="All"/> exist here. The startup settings (the API's
/// binding and key, the database path) stay in the startup file and have no route, so a
/// web session can never change how it is itself reached.
/// <para>
/// Launch settings - which programs start, from where, with which config file, and which
/// file the log viewer reads - are not exposed here at all, neither read nor written.
/// They are set in the desktop app or the config files only. A web admin who could
/// change them could run any program, or read and overwrite any file, as the Windows
/// user the controller runs as; and reading them would only reveal local paths.
/// </para>
/// </remarks>
internal interface ISettingsApiSection
{
    /// <summary>The route name, such as <c>vote</c>.</summary>
    string Name { get; }

    Dictionary<string, object?> Read(ISettingsStore store, out int version);

    Task<IActionResult> PutAsync(ControllerBase controller, ISettingsStore store, JsonElement body, int expectedVersion);
}

internal static class SettingsApi
{
    public static readonly IReadOnlyList<ISettingsApiSection> All =
    [
        // No wreckfestServer section: every field in it is a launch setting (serverPath,
        // serverArguments - whose server_config= names the file ConfigService writes -
        // workingDirectory, logFilePath) or internal (outputMode).
        //
        // No steamCmd section either. SteamCmdPath is the program Update runs: a launch
        // setting, hidden from the web entirely, neither read nor written. WreckfestAppId
        // is Steam's fixed id for the dedicated server, with nothing to edit. Both are
        // set in the desktop app only; the web keeps the Update action itself.
        new SettingsApiSection<VoteSettings>("vote", hidden: [nameof(VoteSettings.Enabled)], ValidateVote),
    ];

    public static ISettingsApiSection? Find(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private static (string Field, string Message)? ValidateVote(VoteSettings vote)
    {
        var mode = new[] { VoteModes.Off, VoteModes.Voting, VoteModes.Direct }
            .FirstOrDefault(m => string.Equals(m, vote.Mode, StringComparison.OrdinalIgnoreCase));
        if (mode is null)
        {
            return ("mode", "mode must be Off, Voting or Direct.");
        }

        // Stored as spelled here, so the legacy Enabled flag follows it.
        vote.Mode = mode;
        vote.Enabled = mode != VoteModes.Off;

        return Range("directCooldownSeconds", vote.DirectCooldownSeconds, 0, 3600)
            ?? Range("voteTimeoutSeconds", vote.VoteTimeoutSeconds, 1, 3600)
            ?? Range("maxLapsAllowed", vote.MaxLapsAllowed, 1, 999)
            ?? Range("messageDelayMs", vote.MessageDelayMs, 0, 5000);
    }

    private static (string Field, string Message)? Range(string field, int value, int min, int max) =>
        value < min || value > max ? (field, $"{field} must be between {min} and {max}.") : null;
}

/// <summary>
/// A section of type <typeparamref name="T"/>. Fields are its public properties in
/// camelCase, less <c>hidden</c> ones. A PUT changes only the fields in the body; the
/// rest keep their stored values. A hidden field is unknown here: a PUT naming one is
/// rejected like any other unknown field.
/// </summary>
internal sealed class SettingsApiSection<T> : ISettingsApiSection
    where T : class
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, PropertyInfo> _fields;
    private readonly Func<T, (string Field, string Message)?> _validate;

    public SettingsApiSection(string name, string[] hidden, Func<T, (string Field, string Message)?> validate)
    {
        Name = name;
        _validate = validate;
        _fields = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && !hidden.Contains(p.Name))
            .ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), StringComparer.OrdinalIgnoreCase);
    }

    public string Name { get; }

    public Dictionary<string, object?> Read(ISettingsStore store, out int version)
    {
        var entry = store.GetEntry<T>();
        version = entry.Version;
        return ToBody(entry.Value, entry.Version);
    }

    public async Task<IActionResult> PutAsync(ControllerBase controller, ISettingsStore store, JsonElement body, int expectedVersion)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return controller.Invalid(Name, "Body must be a JSON object of the fields to change.");
        }

        // The merge base must be the version the client edited. Saving it against that
        // same version keeps the database's concurrency check: if the store's copy is
        // behind the database, the save conflicts instead of reverting newer changes.
        var entry = store.GetEntry<T>();
        if (entry.Version != expectedVersion)
        {
            return controller.VersionConflict(ToBody(entry.Value, entry.Version), entry.Version);
        }

        var value = entry.Value;
        foreach (var field in body.EnumerateObject())
        {
            // A client may send back what it read, version included; If-Match is what counts.
            if (string.Equals(field.Name, "version", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!_fields.TryGetValue(field.Name, out var property))
            {
                return controller.Invalid(field.Name, $"Unknown field '{field.Name}'.");
            }

            object? parsed;
            try
            {
                parsed = field.Value.Deserialize(property.PropertyType, JsonOptions);
            }
            catch (JsonException)
            {
                parsed = null;
            }

            if (parsed is null)
            {
                return controller.Invalid(field.Name, $"{field.Name} must be {Describe(property.PropertyType)}.");
            }

            property.SetValue(value, parsed);
        }

        if (_validate(value) is { } error)
        {
            return controller.Invalid(error.Field, error.Message);
        }

        SettingsSaveResult<T> result;
        try
        {
            result = await store.SaveAsync(value, entry.Version);
        }
        catch (SettingsUnavailableException ex)
        {
            return controller.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: ex.Message);
        }

        if (result.Status == SettingsSaveStatus.Conflict)
        {
            return controller.VersionConflict(ToBody(result.Current.Value, result.Current.Version), result.Current.Version);
        }

        controller.SetETag(result.Current.Version);
        return controller.Ok(ToBody(result.Current.Value, result.Current.Version));
    }

    private Dictionary<string, object?> ToBody(T value, int version)
    {
        var body = _fields.ToDictionary(f => f.Key, f => f.Value.GetValue(value));
        body["version"] = version;
        return body;
    }

    private static string Describe(Type type) =>
        type == typeof(int) ? "a whole number"
        : type == typeof(bool) ? "true or false"
        : "a string";
}
