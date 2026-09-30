using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WreckfestController.Models;

/// <summary>
/// Applies a partial JSON update to a <see cref="ServerConfig"/>. Binding the body
/// straight to <see cref="ServerConfig"/> cannot tell an omitted field from one set
/// to its default, so a one-field request used to write defaults over everything
/// else in server_config.cfg.
/// </summary>
public static class ServerConfigPatch
{
    // Strict numbers, like the rest of the API: "5" is not a number. The Web defaults
    // would accept it.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict,
    };

    /// <summary>
    /// Fields the API never writes. <c>log</c> names the file GET /api/server/logfile
    /// returns: a path, so like the other launch settings it is set by hand in
    /// server_config.cfg, never over the web - or any file could be read through it.
    /// </summary>
    private static readonly HashSet<string> NotPatchable =
        new([nameof(ServerConfig.Log)], StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, PropertyInfo> Properties = typeof(ServerConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && !NotPatchable.Contains(p.Name))
        .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The server_config.cfg key a field is written to: its name in snake_case
    /// (<c>MaxPlayers</c> is <c>max_players</c>), as <see cref="ServerConfig.ApplyConfigValue"/>
    /// and ConfigService.ValueOf spell them.
    /// </summary>
    public static string KeyOf(string propertyName) => JsonNamingPolicy.SnakeCaseLower.ConvertName(propertyName);

    /// <summary>The fields a patch may set, by property name (<c>Log</c> is never one).</summary>
    public static IReadOnlyCollection<string> Fields => Properties.Values.Select(p => p.Name).ToList();

    /// <inheritdoc cref="TryApply(ServerConfig, JsonElement, out EventLoopError?, out IReadOnlyList{string})"/>
    public static bool TryApply(ServerConfig target, JsonElement patch, out EventLoopError? error) =>
        TryApply(target, patch, out error, out _);

    /// <summary>
    /// Copies every field present in <paramref name="patch"/> onto
    /// <paramref name="target"/>. Nothing is applied unless the whole patch is valid; the
    /// error names the field, as the request spelled it, for a field-level 400.
    /// <paramref name="applied"/> lists the properties the patch set.
    /// </summary>
    public static bool TryApply(ServerConfig target, JsonElement patch, out EventLoopError? error, out IReadOnlyList<string> applied)
    {
        applied = [];
        if (patch.ValueKind != JsonValueKind.Object)
        {
            error = new("body", "Body must be a JSON object of the fields to change.");
            return false;
        }

        var updates = new List<(PropertyInfo Property, object Value)>();
        foreach (var field in patch.EnumerateObject())
        {
            if (NotPatchable.Contains(field.Name))
            {
                error = new(field.Name, $"{field.Name} names a file and is set in server_config.cfg, not over the API.");
                return false;
            }

            if (!Properties.TryGetValue(field.Name, out var property))
            {
                error = new(field.Name, $"Unknown field '{field.Name}'.");
                return false;
            }

            object? value;
            try
            {
                value = field.Value.Deserialize(property.PropertyType, JsonOptions);
            }
            catch (JsonException)
            {
                value = null;
            }

            if (value is null)
            {
                error = new(field.Name, $"{field.Name} must be a {(property.PropertyType == typeof(int) ? "number" : "string")}.");
                return false;
            }

            // server_config.cfg is line-based: a line break in a value would write
            // extra key=value lines the caller never named.
            if (value is string text && text.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                error = new(field.Name, $"{field.Name} must not contain line breaks.");
                return false;
            }

            updates.Add((property, value));
        }

        foreach (var (property, value) in updates)
        {
            property.SetValue(target, value);
        }

        applied = updates.Select(u => u.Property.Name).ToList();
        error = null;
        return true;
    }
}
