using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Deploy;

/// <summary>Turns a pre-flight report into the ordered list of objects to create.</summary>
public static partial class DeployPlanner
{
    const string Unified = "Unified";
    const string Dynamic = "DynamicMembership";

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NotNicknameChar();

    public static DeployRun Plan(PreflightReport report, MappingPlan plan, TenantInfo source, TenantInfo destination)
    {
        var order = ResourceRegistry.All.Select((type, index) => (type.Id, index)).ToDictionary(p => p.Id, p => p.index);

        var policies = report.Policies
            .Where(p => p.Outcome == Outcome.Create)
            .OrderBy(p => order[p.Source.Type.Id])
            .ThenBy(p => p.Source.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(PolicyStep)
            .ToList();

        // Groups are created first, and only the ones a policy being created actually points at.
        var needed = policies.SelectMany(p => Placeholders(p.Body).Concat(Placeholders(p.Assignments))).ToHashSet();
        var groups = plan.OfType(ResourceRegistry.Group)
            .Where(m => m.Kind == MatchKind.CreateInDestination && m.SourceDetails is not null && needed.Contains(m.Key))
            .Select(GroupStep)
            .ToList();

        var steps = InDependencyOrder(groups.Concat(policies).ToList());
        SkipUnreachable(steps, plan);

        return new DeployRun
        {
            RunId = Guid.NewGuid().ToString("N")[..12],
            Started = DateTimeOffset.Now,
            SourceTenantId = source.TenantId,
            SourceName = source.DisplayName,
            DestinationTenantId = destination.TenantId,
            DestinationName = destination.DisplayName,
            Steps = steps,
        };
    }

    static DeployStep PolicyStep(PolicyResult result)
    {
        var type = result.Source.Type;

        // Settings: only the ones that differ in the destination are sent.
        if (type.IsSettings)
        {
            return new DeployStep
            {
                Key = result.Key,
                TargetType = type.Id,
                SourceId = result.Source.SourceId,
                DisplayName = type.DisplayName,
                Body = SettingsDiff.Only(result.Policy.Desired, result.ChangedSettings),
            };
        }

        var body = (JsonObject)result.Policy.Desired.DeepClone();

        // Assignments and apps can't be sent when creating; they follow once the policy exists.
        body.TryGetPropertyValue("assignments", out var found);
        body.Remove("assignments");
        var assignments = type.AssignPath is not null ? found as JsonArray : null;

        // Teams policies: the groups they're assigned to, assigned once the policy exists.
        if (type.TeamsPolicyType is not null && body.TryGetPropertyValue("groupAssignments", out var groupAssignments))
        {
            body.Remove("groupAssignments");
            assignments = groupAssignments as JsonArray;
        }

        JsonArray? apps = null;
        if (type.TargetAppsPath is not null && body.TryGetPropertyValue("apps", out var appList) && body.Remove("apps") && appList is JsonArray list)
        {
            // Only the app identifier is sent; the rest belongs to the source.
            apps = new JsonArray(list.OfType<JsonObject>()
                .Where(a => a["mobileAppIdentifier"] is JsonObject)
                .Select(a => (JsonNode)new JsonObject { ["mobileAppIdentifier"] = a["mobileAppIdentifier"]!.DeepClone() })
                .ToArray());
        }

        // Rules are created once their policy exists.
        JsonArray? rules = null;
        if (type.Rules is not null && body.TryGetPropertyValue("rules", out var ruleList))
        {
            body.Remove("rules");
            rules = ruleList as JsonArray;
        }

        // So are items that belong to it, such as an administrative template's settings.
        JsonArray? children = null;
        if (type.Children is { } childItems && body.TryGetPropertyValue(childItems.Property, out var childList))
        {
            body.Remove(childItems.Property);
            children = childList as JsonArray;
        }

        return new DeployStep
        {
            Key = result.Key,
            TargetType = type.Id,
            SourceId = result.Source.SourceId,
            DisplayName = body[type.IdentityProperty]?.GetValue<string>() ?? result.Source.DisplayName,
            Body = body,
            Assignments = assignments,
            Apps = apps,
            Rules = rules,
            Children = children,
            InTestMode = result.Policy.Changes.Any(c => c.IsTestMode),
        };
    }

    /// <summary>
    /// A new group from the source group's settings. Members aren't copied: the people in the destination
    /// are different, so the admin adds them.
    /// </summary>
    static DeployStep GroupStep(Mapping.Mapping mapping)
    {
        var source = mapping.SourceDetails!;
        var name = Text(source, "displayName") ?? mapping.Source.DisplayName;
        var groupTypes = (source["groupTypes"] as JsonArray ?? []).Select(t => t?.GetValue<string>() ?? "").Where(t => t.Length > 0).ToList();
        var isUnified = groupTypes.Contains(Unified, StringComparer.OrdinalIgnoreCase);
        var isDynamic = groupTypes.Contains(Dynamic, StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();

        var nickname = Text(source, "mailNickname");
        if (string.IsNullOrWhiteSpace(nickname))
        {
            nickname = NotNicknameChar().Replace(name, "");
            if (nickname.Length == 0)
                nickname = "group" + Guid.NewGuid().ToString("N")[..8];
        }
        if (nickname.Length > 64)
            nickname = nickname[..64];

        var mailEnabled = source["mailEnabled"]?.GetValue<bool>() == true;
        var securityEnabled = source["securityEnabled"]?.GetValue<bool>() != false;
        if (!isUnified && mailEnabled)
        {
            // Graph can only create Microsoft 365 groups and security groups.
            notes.Add("It was mail-enabled in the source. Microsoft Graph can't create mail-enabled security groups or distribution lists, so it was created as a security group.");
            mailEnabled = false;
            securityEnabled = true;
        }

        var body = new JsonObject
        {
            ["displayName"] = name,
            ["mailNickname"] = nickname,
            ["mailEnabled"] = isUnified || mailEnabled,
            ["securityEnabled"] = securityEnabled,
            ["groupTypes"] = new JsonArray(groupTypes.Select(t => (JsonNode)t).ToArray()),
        };
        if (Text(source, "description") is { Length: > 0 } description)
            body["description"] = description;

        if (isDynamic && Text(source, "membershipRule") is { Length: > 0 } rule)
        {
            body["membershipRule"] = rule;
            body["membershipRuleProcessingState"] = Text(source, "membershipRuleProcessingState") ?? "On";
            notes.Add($"Dynamic membership rule copied: {rule}. Check that it matches the destination's users or devices.");
        }
        else
        {
            notes.Add("Created with no members. Add members before turning on the policies that use it.");
        }

        return new DeployStep
        {
            Key = mapping.Key,
            TargetType = ResourceRegistry.Group,
            SourceId = mapping.Source.Id,
            DisplayName = name,
            Body = body,
            Notes = notes,
        };
    }

    /// <summary>
    /// Keeps the given order, except that an object comes after the objects it needs from the same run, such as a
    /// sublabel after its parent label. Where objects need each other, the given order stands.
    /// </summary>
    static List<DeployStep> InDependencyOrder(List<DeployStep> steps)
    {
        var keys = steps.Select(s => s.Key).ToHashSet();
        var placed = new HashSet<string>();
        var result = new List<DeployStep>();
        var remaining = steps.ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(s => Placeholders(s.Body).Concat(Placeholders(s.Assignments))
                .All(k => k == s.Key || !keys.Contains(k) || placed.Contains(k))) ?? remaining[0];
            result.Add(next);
            placed.Add(next.Key);
            remaining.Remove(next);
        }
        return result;
    }

    /// <summary>
    /// Marks steps that point at an object this run won't create (for example a named location that was skipped)
    /// as skipped, and keeps going until nothing else changes, since skipping one can strand another.
    /// </summary>
    static void SkipUnreachable(List<DeployStep> steps, MappingPlan plan)
    {
        bool changed;
        do
        {
            changed = false;
            var available = steps.Where(s => s.Status != StepStatus.Skipped).Select(s => s.Key).ToHashSet();
            foreach (var step in steps.Where(s => s.Status != StepStatus.Skipped))
            {
                var missing = Placeholders(step.Body).Concat(Placeholders(step.Assignments)).Where(k => !available.Contains(k)).Distinct().ToList();
                if (missing.Count == 0)
                    continue;
                step.Status = StepStatus.Skipped;
                step.Message = $"Needs {Names(missing, steps, plan)}, which this run isn't creating.";
                changed = true;
            }
        }
        while (changed);
    }

    internal static string Names(IEnumerable<string> keys, IReadOnlyList<DeployStep> steps, MappingPlan? plan) =>
        string.Join(", ", keys.Select(key =>
            steps.FirstOrDefault(s => s.Key == key)?.DisplayName
            ?? plan?.Items.FirstOrDefault(m => m.Key == key)?.Source.DisplayName
            ?? key).Select(n => $"\"{n}\""));

    /// <summary>Keys of every placeholder in a JSON tree.</summary>
    internal static IEnumerable<string> Placeholders(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, value) in obj)
                    foreach (var key in Placeholders(value))
                        yield return key;
                break;
            case JsonArray array:
                foreach (var item in array)
                    foreach (var key in Placeholders(item))
                        yield return key;
                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && Transformer.PlaceholderKey(text) is { } found:
                yield return found;
                break;
        }
    }

    static string? Text(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
