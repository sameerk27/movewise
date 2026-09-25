using System.Text.Json.Nodes;
using Movewise.Core.Registry;

namespace Movewise.Core.Export;

/// <summary>One policy read from the source tenant, normalized and with its dependencies found.</summary>
public sealed record ExportedResource(
    ResourceType Type,
    string SourceId,
    string DisplayName,
    JsonObject Settings,
    IReadOnlyList<Dependency> Dependencies);

public static class ExportAnalysis
{
    static readonly Dictionary<string, (string One, string Many)> Nouns = new()
    {
        [ResourceRegistry.Group] = ("group", "groups"),
        [ResourceRegistry.User] = ("user", "users"),
        [ResourceRegistry.Application] = ("app", "apps"),
        [ResourceRegistry.NamedLocation] = ("named location", "named locations"),
        [ResourceRegistry.AuthenticationStrength] = ("authentication strength", "authentication strengths"),
        [ResourceRegistry.AssignmentFilter] = ("assignment filter", "assignment filters"),
        [ResourceRegistry.ScopeTag] = ("scope tag", "scope tags"),
        [ResourceRegistry.SensitivityLabel] = ("sensitivity label", "sensitivity labels"),
        [ResourceRegistry.Domain] = ("domain", "domains"),
        [ResourceRegistry.Recipient] = ("mailbox or group", "mailboxes and groups"),
        [ResourceRegistry.Site] = ("site", "sites"),
    };

    /// <summary>
    /// True when the policy points at a group, user, location or strength that is not part of this export
    /// and therefore has to be matched to an object in the destination. App IDs are left out: most are
    /// Microsoft or multi-tenant apps with the same ID everywhere, and they are checked during mapping.
    /// </summary>
    public static bool NeedsMapping(ExportedResource item, IReadOnlyCollection<ExportedResource> all)
    {
        var exported = all.Select(e => (e.Type.Id, e.SourceId)).ToHashSet();
        return item.Dependencies.Any(d =>
            d.TargetType != ResourceRegistry.Application && !exported.Contains((d.TargetType, d.Value)));
    }

    /// <summary>A short summary such as "2 groups, 1 named location".</summary>
    public static string DescribeDependencies(ExportedResource item)
    {
        if (item.Dependencies.Count == 0)
            return "—";

        return string.Join(", ", item.Dependencies
            .GroupBy(d => d.TargetType)
            .Select(g =>
            {
                var count = g.Select(d => d.Value).Distinct().Count();
                var (one, many) = Nouns.TryGetValue(g.Key, out var noun) ? noun
                    : ResourceRegistry.All.FirstOrDefault(t => t.Id == g.Key) is { } type ? (Lower(type.DisplayName), Lower(type.PluralName))
                    : (g.Key, g.Key);
                return $"{count} {(count == 1 ? one : many)}";
            }));
    }

    // "Mailbox retention tag" reads "mailbox retention tag" mid-sentence; names that start with a proper noun keep it.
    static string Lower(string name) =>
        name.Length > 1 && char.IsUpper(name[0]) && char.IsLower(name[1]) && !name.StartsWith("Outlook", StringComparison.Ordinal)
            ? char.ToLowerInvariant(name[0]) + name[1..]
            : name;
}
