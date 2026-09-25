using System.Text.Json.Nodes;
using Movewise.Core.Registry;

namespace Movewise.Core.Export;

/// <summary>A value inside a policy that points at another object, such as a group ID.</summary>
public sealed record Dependency(string TargetType, string Value, string Path);

public static class DependencyExtractor
{
    public static IReadOnlyList<Dependency> Extract(JsonObject item, ResourceType type)
    {
        var found = new List<Dependency>();
        foreach (var rule in type.References)
            Walk(item, rule.Path.Split('.'), 0, rule, found);
        return found.Distinct().ToList();
    }

    // Some settings that usually name recipients can also be just on or off (a DLP rule's alert setting, say).
    static bool IsSwitch(ReferenceRule rule, string text) =>
        rule.TargetType == ResourceRegistry.Recipient && bool.TryParse(text, out _);

    static void Walk(JsonNode? node, string[] segments, int index, ReferenceRule rule, List<Dependency> found)
    {
        if (node is null)
            return;

        if (index == segments.Length)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text)
                && !string.IsNullOrWhiteSpace(text) && !rule.Ignore.Contains(text) && !IsSwitch(rule, text))
            {
                found.Add(new Dependency(rule.TargetType, text, rule.Path));
            }
            return;
        }

        var segment = segments[index];
        var isArray = segment.EndsWith("[]", StringComparison.Ordinal);
        var name = isArray ? segment[..^2] : segment;

        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out var child) || child is null)
            return;

        if (isArray && child is JsonArray array)
        {
            foreach (var element in array)
                Walk(element, segments, index + 1, rule, found);
        }
        else
        {
            Walk(child, segments, index + 1, rule, found);
        }
    }
}
