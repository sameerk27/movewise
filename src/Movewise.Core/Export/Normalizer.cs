using System.Text.Json;
using System.Text.Json.Nodes;
using Movewise.Core.Registry;

namespace Movewise.Core.Export;

/// <summary>
/// Turns a policy as Graph returns it into a stable shape: read-only fields removed, OData noise removed
/// and keys sorted, so the same settings always produce the same JSON.
/// </summary>
public static class Normalizer
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static JsonObject Normalize(JsonObject raw, ResourceType type)
    {
        var result = (JsonObject)Canonicalize(raw)!;
        foreach (var property in type.ReadOnlyProperties)
            result.Remove(property);
        return result;
    }

    public static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => CanonicalizeObject(obj),
        JsonArray array => new JsonArray(array.Select(Canonicalize).ToArray()),
        _ => node.DeepClone(),
    };

    public static string ToStableJson(JsonNode node) => node.ToJsonString(Indented);

    static JsonObject CanonicalizeObject(JsonObject obj)
    {
        var result = new JsonObject();
        foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (IsDroppedAnnotation(key))
                continue;
            result[key] = Canonicalize(value);
        }
        return result;
    }

    // "@odata.type" is kept because Graph needs it to create derived types such as ipNamedLocation.
    static bool IsDroppedAnnotation(string key) =>
        key.Contains("@odata.", StringComparison.Ordinal) && key != "@odata.type";
}
