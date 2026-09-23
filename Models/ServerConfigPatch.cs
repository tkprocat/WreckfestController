using System.Reflection;
using System.Text.Json;

namespace WreckfestController.Models;

/// <summary>
/// Applies a partial JSON update to a <see cref="ServerConfig"/>. Binding the body
/// straight to <see cref="ServerConfig"/> cannot tell an omitted field from one set
/// to its default, so a one-field request used to write defaults over everything
/// else in server_config.cfg.
/// </summary>
public static class ServerConfigPatch
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, PropertyInfo> Properties = typeof(ServerConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite)
        .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Copies every field present in <paramref name="patch"/> onto
    /// <paramref name="target"/>. Nothing is applied unless the whole patch is valid.
    /// </summary>
    public static bool TryApply(ServerConfig target, JsonElement patch, out string? error)
    {
        if (patch.ValueKind != JsonValueKind.Object)
        {
            error = "Body must be a JSON object of the fields to change.";
            return false;
        }

        var updates = new List<(PropertyInfo Property, object Value)>();
        foreach (var field in patch.EnumerateObject())
        {
            if (!Properties.TryGetValue(field.Name, out var property))
            {
                error = $"Unknown field '{field.Name}'.";
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
                error = $"Field '{field.Name}' must be a {(property.PropertyType == typeof(int) ? "number" : "string")}.";
                return false;
            }

            // server_config.cfg is line-based: a line break in a value would write
            // extra key=value lines the caller never named.
            if (value is string text && text.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                error = $"Field '{field.Name}' must not contain line breaks.";
                return false;
            }

            updates.Add((property, value));
        }

        foreach (var (property, value) in updates)
        {
            property.SetValue(target, value);
        }

        error = null;
        return true;
    }
}
