using System.Text.Json.Nodes;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Registry;

namespace Movewise.Core.Preflight;

public sealed record TransformOptions
{
    /// <summary>Create enabled Conditional Access policies in report-only mode, so they can be checked before they enforce.</summary>
    public bool ConditionalAccessReportOnly { get; init; } = true;

    /// <summary>Keys of policies to create switched off, for example because the destination lacks a license.</summary>
    public IReadOnlySet<string> Disable { get; init; } = new HashSet<string>();

    /// <summary>Keys of policies to create under a new name because the name is taken in the destination.</summary>
    public IReadOnlySet<string> Rename { get; init; } = new HashSet<string>();

    public string RenameSuffix { get; init; } = " (migrated)";
}

/// <summary>A change the transformer made, as shown to the admin.</summary>
/// <param name="WidensPolicy">True when an exclusion was removed, so the policy now applies to more people or devices.</param>
/// <param name="IsTestMode">True when the policy is created in a mode that can't block or delete anything yet.</param>
/// <param name="ExcludesNewGroup">True when an exclusion now points at a group this migration creates, which starts with no members.</param>
public sealed record PolicyChange(string Description, bool WidensPolicy = false, bool IsTestMode = false, bool ExcludesNewGroup = false);

/// <summary>A policy as it will be created in the destination.</summary>
/// <param name="Problems">References that couldn't be translated. The policy can't be created while any remain.</param>
public sealed record TransformedPolicy(
    ExportedResource Source,
    JsonObject Desired,
    IReadOnlyList<PolicyChange> Changes,
    IReadOnlyList<string> Problems)
{
    /// <summary>
    /// Conditions that say who a rule applies to, left with no values because every one was removed. The service would
    /// drop such a condition, and the rule would apply to all mail. Each is also one of the <see cref="Problems"/>.
    /// </summary>
    public IReadOnlyList<string> EmptiedConditions { get; init; } = [];
}

/// <summary>Rewrites a source policy for the destination: mapped IDs, removed references, report-only, disable, rename.</summary>
public static class Transformer
{
    const string ConditionalAccessReportOnly = "enabledForReportingButNotEnforced";

    /// <summary>
    /// Stands in for the ID of an object Movewise creates during deployment; deployment swaps in the real ID.
    /// </summary>
    public static string Placeholder(string targetType, string sourceId) => $"{{{{movewise:new:{targetType}:{sourceId}}}}}";

    const string PlaceholderStart = "{{movewise:new:";

    public static bool IsPlaceholder(string value) => value.StartsWith(PlaceholderStart, StringComparison.Ordinal);

    /// <summary>The key (see <see cref="MappingPlan.KeyOf"/>) of the object a placeholder stands for, or null.</summary>
    public static string? PlaceholderKey(string value) =>
        IsPlaceholder(value) && value.EndsWith("}}", StringComparison.Ordinal) ? value[PlaceholderStart.Length..^2] : null;

    public static TransformedPolicy Transform(ExportedResource item, MappingPlan plan, TransformOptions options)
    {
        var desired = (JsonObject)item.Settings.DeepClone();
        var changes = new List<PolicyChange>();
        var problems = new List<string>();

        // Find every reference first, then change them, so edits don't disturb the walk.
        var hits = new List<(ReferenceRule Rule, Hit Hit)>();
        foreach (var rule in item.Type.References)
        {
            var found = new List<Hit>();
            Collect(desired, rule.Path.Split('.'), 0, rule, null, null, null, null, found);
            hits.AddRange(found.Select(h => (rule, h)));
        }

        foreach (var (rule, hit) in hits)
            Apply(rule, hit, plan, changes, problems);

        // A condition such as SentTo with every value removed would be dropped, and the rule would then apply to all mail.
        var emptied = hits
            .Where(h => h.Rule.LimitsScope && h.Hit.Array is { Count: 0 })
            .GroupBy(h => h.Hit.Array)
            .Select(g => g.First())
            .Select(h => $"{(h.Hit.OwnerName is { } owner ? $"Rule \"{owner}\"" : "The rule")}: every value of its {h.Rule.Path.Split('.')[^1].TrimEnd('[', ']')} condition was removed, so it would apply to all mail instead.")
            .ToList();
        problems.AddRange(emptied.Select(e => $"{e} Match at least one of them on the Map screen, or leave this policy out."));

        var key = MappingPlan.KeyOf(item.Type.Id, item.SourceId);
        var nameProperty = item.Type.IdentityProperty;

        if (item.Type.Id == ResourceRegistry.ConditionalAccessPolicy)
        {
            var state = desired["state"]?.GetValue<string>();
            if (options.Disable.Contains(key))
            {
                desired["state"] = "disabled";
                changes.Add(new PolicyChange("Created switched off."));
            }
            else if (options.ConditionalAccessReportOnly && state == "enabled")
            {
                desired["state"] = ConditionalAccessReportOnly;
                changes.Add(new PolicyChange("Created in report-only mode. Turn it on after checking sign-in logs.", IsTestMode: true));
            }
        }

        if (item.Type.MakeSafe is { } makeSafe)
            changes.AddRange(makeSafe(desired).Select(description => new PolicyChange(description, IsTestMode: true)));

        if (options.Rename.Contains(key) && desired[nameProperty] is JsonValue nameValue && nameValue.TryGetValue<string>(out var name))
        {
            desired[nameProperty] = name + options.RenameSuffix;
            changes.Add(new PolicyChange($"Renamed to \"{name + options.RenameSuffix}\" because the name is taken in the destination."));

            // Rule names must be unique too, and a rule usually shares its policy's name.
            foreach (var rule in (desired["rules"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (rule["Name"] is JsonValue ruleName && ruleName.TryGetValue<string>(out var text))
                    rule["Name"] = text + options.RenameSuffix;
            }
        }

        return new TransformedPolicy(item, desired, changes, problems) { EmptiedConditions = emptied };
    }

    static void Apply(ReferenceRule rule, Hit hit, MappingPlan plan, List<PolicyChange> changes, List<string> problems)
    {
        var mapping = plan.Find(rule.TargetType, hit.Value);
        var noun = MappingNames.Singular(rule.TargetType).ToLowerInvariant();
        var name = mapping?.Source.DisplayName ?? hit.Value;

        switch (mapping?.Kind)
        {
            case null:
            case MatchKind.Unresolved:
                problems.Add($"The {noun} \"{name}\" has no match in the destination.");
                break;

            case MatchKind.External:
                // A partner's domain or address: the same everywhere.
                break;

            case MatchKind.RemoveFromPolicies:
                hit.Remove();
                var widens = rule.IsExclusion || hit.InExclusionAssignment;
                changes.Add(new PolicyChange(
                    widens ? $"Excluded {noun} \"{name}\" removed. The policy now applies more widely." : $"{Capitalize(noun)} \"{name}\" removed.",
                    widens));
                break;

            case MatchKind.CreateInDestination:
            case MatchKind.CreatedByMigration:
                hit.Replace(Placeholder(rule.TargetType, hit.Value));
                // A new group has no members (unless its membership is dynamic), so as an exclusion it leaves nobody out yet.
                if (rule.TargetType == ResourceRegistry.Group && (rule.IsExclusion || hit.InExclusionAssignment) && !IsDynamic(mapping.SourceDetails))
                    changes.Add(new PolicyChange($"Excluded group \"{name}\" → new group created by this migration. It starts with no members, so it excludes nobody until you add them.", ExcludesNewGroup: true));
                else
                    changes.Add(new PolicyChange($"{Capitalize(noun)} \"{name}\" → new {noun} created by this migration."));
                break;

            // Domains, addresses and sites appear in the policy as themselves, so the new value is what's shown.
            case not null when rule.TargetType is ResourceRegistry.Domain or ResourceRegistry.Recipient or ResourceRegistry.Site:
                var value = mapping.Destination!.Id;
                if (!string.Equals(value, hit.Value, StringComparison.OrdinalIgnoreCase))
                {
                    hit.Replace(value);
                    changes.Add(new PolicyChange($"{Capitalize(noun)} {hit.Value} → {value}."));
                }
                break;

            default:
                var destination = mapping.Destination!;
                hit.Replace(destination.Id);
                changes.Add(new PolicyChange(destination.DisplayName == name
                    ? $"{Capitalize(noun)} \"{name}\" → the destination's own."
                    : $"{Capitalize(noun)} \"{name}\" → \"{destination.DisplayName}\"."));
                break;
        }
    }

    static bool IsDynamic(JsonObject? group) =>
        group?["groupTypes"] is JsonArray types && types.Any(t => t is JsonValue v && v.TryGetValue<string>(out var s) && s.Equals("DynamicMembership", StringComparison.OrdinalIgnoreCase))
        && group["membershipRule"] is JsonValue rule && rule.TryGetValue<string>(out var text) && text.Length > 0;

    /// <summary>One reference found in the policy, with ways to change it in place.</summary>
    sealed class Hit
    {
        public required string Value { get; init; }
        public required Action<string> Replace { get; init; }
        public required Action Remove { get; init; }

        /// <summary>True when the reference sits inside an exclusion assignment (Intune).</summary>
        public bool InExclusionAssignment { get; init; }

        /// <summary>The list the value is in, when it's one of several.</summary>
        public JsonArray? Array { get; init; }

        /// <summary>The name of the object holding the value, such as the rule it's a condition of.</summary>
        public string? OwnerName { get; init; }
    }

    static void Collect(
        JsonNode? node, string[] segments, int index, ReferenceRule rule,
        JsonObject? parent, string? parentKey, JsonArray? outerArray, JsonNode? outerItem, List<Hit> hits)
    {
        if (node is not JsonObject obj)
            return;

        var segment = segments[index];
        var isArray = segment.EndsWith("[]", StringComparison.Ordinal);
        var name = isArray ? segment[..^2] : segment;
        if (!obj.TryGetPropertyValue(name, out var child) || child is null)
            return;

        var inExclusion = outerItem is JsonObject item && item["target"] is JsonObject target
            && target["@odata.type"]?.GetValue<string>().Contains("exclusion", StringComparison.OrdinalIgnoreCase) == true;
        Action removeOuter = () => outerArray?.Remove(outerItem);

        if (index == segments.Length - 1)
        {
            if (isArray && child is JsonArray values)
            {
                foreach (var element in values.ToList())
                {
                    if (!IsReference(element, rule, out var value))
                        continue;
                    var current = element;
                    hits.Add(new Hit
                    {
                        Value = value,
                        InExclusionAssignment = inExclusion,
                        Array = values,
                        OwnerName = obj["Name"] is JsonValue owner && owner.TryGetValue<string>(out var ownerName) ? ownerName : null,
                        Replace = replacement =>
                        {
                            var at = values.IndexOf(current);
                            if (at < 0) return;
                            current = JsonValue.Create(replacement);
                            values[at] = current;
                        },
                        Remove = rule.OnRemove == RemoveBehavior.RemoveContainingItem && outerArray is not null
                            ? removeOuter
                            : () => values.Remove(current),
                    });
                }
            }
            else if (IsReference(child, rule, out var value))
            {
                hits.Add(new Hit
                {
                    Value = value,
                    InExclusionAssignment = inExclusion,
                    Replace = replacement => obj[name] = replacement,
                    Remove = rule.OnRemove switch
                    {
                        RemoveBehavior.RemoveContainingItem when outerArray is not null => removeOuter,
                        RemoveBehavior.ClearParent when parent is not null && parentKey is not null => () => parent[parentKey] = null,
                        _ => () =>
                        {
                            obj[name] = null;
                            if (rule.ClearedSibling is { } sibling)
                                obj[sibling.Property] = sibling.Value;
                        },
                    },
                });
            }
            return;
        }

        if (isArray && child is JsonArray array)
        {
            foreach (var element in array.ToList())
                Collect(element, segments, index + 1, rule, obj, name, outerArray ?? array, outerArray is null ? element : outerItem, hits);
        }
        else
        {
            Collect(child, segments, index + 1, rule, obj, name, outerArray, outerItem, hits);
        }
    }

    static bool IsReference(JsonNode? node, ReferenceRule rule, out string value)
    {
        value = "";
        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) || rule.Ignore.Contains(text))
            return false;
        // Settings that usually name recipients can also be just on or off, as DependencyExtractor treats them.
        if (rule.TargetType == ResourceRegistry.Recipient && bool.TryParse(text, out _))
            return false;
        value = text;
        return true;
    }

    static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
