using System.Text.Json.Nodes;
using Movewise.Core.Registry;

namespace Movewise.Core.Preflight;

/// <summary>For settings that exist in every tenant: which of the source's values differ from the destination's.</summary>
public static class SettingsDiff
{
    /// <summary>
    /// The settings to change: those the Set cmdlet takes (or that switch it on and off) whose value in the destination
    /// differs. The name is never among them; Set would rename the destination's object.
    /// </summary>
    /// <param name="setTakes">The parameters the Set cmdlet takes; null for Graph, where PATCH takes any property the object has.</param>
    public static IReadOnlyList<string> Changes(ResourceType type, JsonObject desired, JsonObject current, IReadOnlySet<string>? setTakes)
    {
        var settings = type.Settings ?? throw new InvalidOperationException($"{type.PluralName} aren't settings.");
        return desired
            .Where(p => !IsName(type, p.Key))
            .Where(p => setTakes is null || setTakes.Contains(p.Key) || p.Key == "Enabled" && settings.Switch is not null)
            .Where(p => !Same(p.Value, current[p.Key]))
            .Select(p => p.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Only the given settings of an object, to deploy them or to show them side by side.</summary>
    public static JsonObject Only(JsonObject settings, IEnumerable<string> names)
    {
        var result = new JsonObject();
        foreach (var name in names)
            result[name] = settings[name]?.DeepClone();
        return result;
    }

    static bool IsName(ResourceType type, string property) =>
        property.Equals(type.IdentityProperty, StringComparison.OrdinalIgnoreCase)
        || property.Equals("Name", StringComparison.OrdinalIgnoreCase)
        || property.Equals("Identity", StringComparison.OrdinalIgnoreCase);

    // Nothing, an empty list and an empty text all mean "not set".
    static bool Same(JsonNode? a, JsonNode? b)
    {
        if (IsEmpty(a) && IsEmpty(b))
            return true;
        if (a is JsonArray left && b is JsonArray right)
        {
            // Lists of addresses, domains and file types: the order doesn't matter.
            var l = left.Select(n => n?.ToJsonString()).Order(StringComparer.OrdinalIgnoreCase);
            var r = right.Select(n => n?.ToJsonString()).Order(StringComparer.OrdinalIgnoreCase);
            return l.SequenceEqual(r, StringComparer.OrdinalIgnoreCase);
        }
        if (a is JsonValue x && b is JsonValue y && x.TryGetValue<string>(out var xs) && y.TryGetValue<string>(out var ys))
            return string.Equals(xs, ys, StringComparison.OrdinalIgnoreCase);
        return JsonNode.DeepEquals(a, b);
    }

    static bool IsEmpty(JsonNode? node) =>
        node is null
        || node is JsonArray { Count: 0 }
        || node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0;
}
