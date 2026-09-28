using System.Text.Json.Nodes;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;

namespace Movewise.Core.Compare;

/// <summary>How an item compares between the first side (the source, or a snapshot) and the second (the destination, or the tenant now).</summary>
public enum Difference
{
    /// <summary>Only on the first side: missing from the destination, or deleted since the snapshot.</summary>
    OnlyInFirst,

    /// <summary>Only on the second side: extra in the destination, or added since the snapshot.</summary>
    OnlyInSecond,

    Different,
    Same,
}

/// <summary>One policy or group of settings, on either side or both.</summary>
/// <param name="First">As it is on the first side, in the second side's terms where the tenants differ.</param>
/// <param name="ChangedSettings">For <see cref="Difference.Different"/>: the top-level settings whose values differ.</param>
/// <param name="Notes">What the admin should know, such as a group that has no match in the destination.</param>
public sealed record ComparedItem(
    ResourceType Type,
    string Name,
    Difference Difference,
    JsonObject? First,
    JsonObject? Second,
    IReadOnlyList<string> ChangedSettings,
    IReadOnlyList<string> Notes)
{
    /// <summary>The first side's item as read, before being put in the second side's terms: what a restore recreates.</summary>
    public ExportedResource? FirstSource { get; init; }

    /// <summary>
    /// True when restoring from a snapshot can put it back: recreated when it was removed, or set back when it was
    /// changed (settings, and policies of types Movewise can update). Ones added since are never deleted.
    /// </summary>
    public bool CanRestore => FirstSource is not null
        && (Difference == Difference.OnlyInFirst || Difference == Difference.Different && (Type.IsSettings || Type.CanUpdate));
}

public sealed record ComparisonReport(
    string FirstName,
    string SecondName,
    bool IsDrift,
    IReadOnlyList<ComparedItem> Items,
    IReadOnlyList<ExportWarning> Warnings)
{
    public int Count(Difference difference) => Items.Count(i => i.Difference == difference);

    /// <summary>For drift: the snapshot the tenant was compared with.</summary>
    public Snapshot? Snapshot { get; init; }
}

/// <summary>
/// Compares two tenants, or a tenant with an earlier snapshot of itself. Only reads: nothing is changed on either side.
/// </summary>
public static class TenantComparer
{
    // The service sets these whenever anything changes; they'd make every item look different.
    static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "createdDateTime", "lastModifiedDateTime", "modifiedDateTime", "version", "@odata.context",
    };

    /// <summary>
    /// Compares the source with the destination. The source's policies are first put in the destination's terms, the same
    /// way deploying would (groups, users, locations and the like matched), so only real differences show.
    /// </summary>
    /// <param name="sourceItems">
    /// The source's policies when they're already known and can't be read again, as with a baseline. Only their
    /// types are compared.
    /// </param>
    public static async Task<ComparisonReport> CompareTenantsAsync(
        TenantClients source, string sourceName, TenantClients destination, string destinationName,
        IEnumerable<ResourceType> types, IProgress<string>? progress = null, CancellationToken ct = default,
        IReadOnlyList<ExportedResource>? sourceItems = null)
    {
        var typeList = types.Where(t => !t.ReferenceOnly).ToList();
        ExportResult first;
        if (sourceItems is not null)
        {
            typeList = typeList.Where(t => sourceItems.Any(i => i.Type == t)).ToList();
            first = new ExportResult(sourceItems.Where(i => typeList.Contains(i.Type)).ToList(), []);
        }
        else
        {
            progress?.Report($"Reading {sourceName}…");
            first = await Exporter.ExportAsync(source, typeList, progress, ct);
        }
        progress?.Report($"Reading {destinationName}…");
        var second = await Exporter.ExportAsync(destination, typeList, progress, ct);

        progress?.Report("Matching groups, users and other objects…");
        var plan = await Matcher.BuildAsync(source.Graph, destination, first.Items, first.Items, null, progress, ct);

        // As it would be created, but without the safe state (report-only, simulation) deploying adds.
        var options = new TransformOptions { ConditionalAccessReportOnly = false, MakeSafe = false };
        var translated = first.Items.Select(item =>
        {
            var result = Transformer.Transform(item, plan, options);
            var notes = result.Problems.ToList();
            // An object the destination doesn't have yet (a named location, say) stays a stand-in, so the policy differs.
            if (result.Desired.ToJsonString().Contains("{{movewise:new:", StringComparison.Ordinal))
                notes.Add("Points at objects the destination doesn't have yet, such as a named location or filter; deploying would create them.");
            return (Item: item, Settings: result.Desired, Notes: (IReadOnlyList<string>)notes);
        }).ToList();

        var report = Pair(translated, second.Items, byId: false);
        return new ComparisonReport(sourceName, destinationName, false, report, [.. first.Warnings, .. second.Warnings]);
    }

    /// <summary>Compares a tenant now with an earlier snapshot of it: what was added, removed or changed since.</summary>
    public static async Task<ComparisonReport> CompareWithSnapshotAsync(
        Snapshot snapshot, TenantClients tenant, string tenantName, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report($"Reading {tenantName}…");
        var now = await Exporter.ExportAsync(tenant, snapshot.Types, progress, ct);
        // Compared by name, but each item stays as it was saved, with its IDs: a restore recreates it from that.
        var named = ByName(snapshot.Items);
        var before = snapshot.Items.Select((item, i) => (Item: item, Settings: named[i].Settings, Notes: (IReadOnlyList<string>)[])).ToList();
        var report = Pair(before, ByName(now.Items), byId: true);
        return new ComparisonReport($"Snapshot of {snapshot.Taken.LocalDateTime:d MMM yyyy HH:mm}", tenantName, true, report, now.Warnings) { Snapshot = snapshot };
    }

    /// <summary>
    /// References to objects read on the same side (named locations, scope tags, filters, authentication strengths…)
    /// as their kind and name instead of their ID. A restore recreates such an object with a new ID; the policies that
    /// point at it are still right, and shouldn't show as changed.
    /// </summary>
    static IReadOnlyList<ExportedResource> ByName(IReadOnlyList<ExportedResource> items)
    {
        var names = items
            .Where(i => !i.Type.IsSettings)
            .GroupBy(i => (i.Type.Id, i.SourceId))
            .ToDictionary(g => g.Key, g => $"{g.First().Type.DisplayName}: {g.First().DisplayName}");
        return items.Select(item =>
        {
            var settings = (JsonObject)item.Settings.DeepClone();
            foreach (var rule in item.Type.References)
                Exporter.ReplaceAt(settings, rule.Path.Split('.'), 0, value => names.GetValueOrDefault((rule.TargetType, value)));
            return item with { Settings = settings };
        }).ToList();
    }

    /// <summary>
    /// Pairs the items of both sides: settings by their type, policies by type and name. In the same tenant a policy
    /// keeps its ID when it's renamed, so drift pairs by ID first.
    /// </summary>
    static List<ComparedItem> Pair(
        IReadOnlyList<(ExportedResource Item, JsonObject Settings, IReadOnlyList<string> Notes)> first,
        IReadOnlyList<ExportedResource> second, bool byId)
    {
        var results = new List<ComparedItem>();
        var unmatched = second.ToList();

        foreach (var (item, settings, notes) in first)
        {
            var match = item.Type.IsSettings
                ? unmatched.FirstOrDefault(s => s.Type == item.Type)
                : (byId ? unmatched.FirstOrDefault(s => s.Type == item.Type && s.SourceId == item.SourceId) : null)
                    ?? unmatched.FirstOrDefault(s => s.Type == item.Type && string.Equals(NameOf(s), NameOf(item.Type, settings, item.DisplayName), StringComparison.OrdinalIgnoreCase));

            // Kept without empty values, so the side-by-side view shows only what's set.
            var firstSettings = Pruned(Comparable(item.Type, settings));
            if (match is null)
            {
                results.Add(new ComparedItem(item.Type, NameOf(item.Type, settings, item.DisplayName), Difference.OnlyInFirst, firstSettings, null, [], notes) { FirstSource = item });
                continue;
            }
            unmatched.Remove(match);

            var secondSettings = Pruned(Comparable(match.Type, match.Settings));
            var changed = ChangesOfPruned(item.Type, firstSettings, secondSettings);
            results.Add(new ComparedItem(item.Type, NameOf(match), changed.Count == 0 ? Difference.Same : Difference.Different,
                firstSettings, secondSettings, changed, notes) { FirstSource = item });
        }

        results.AddRange(unmatched.Select(s => new ComparedItem(s.Type, NameOf(s), Difference.OnlyInSecond, null, Pruned(Comparable(s.Type, s.Settings)), [], [])));

        // By service, then kind, then name, as Discover lists them.
        var order = ResourceRegistry.All.Select((t, i) => (t.Id, i)).ToDictionary(p => p.Id, p => p.i);
        return results
            .OrderBy(r => r.Type.Service)
            .ThenBy(r => order.GetValueOrDefault(r.Type.Id))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// A copy without empty values at any depth: nothing, an empty text and an empty list all mean "not set", and one
    /// tenant lists them where another leaves them out.
    /// </summary>
    public static JsonObject Pruned(JsonObject settings) => (JsonObject)(Prune(settings.DeepClone()) ?? new JsonObject());

    /// <summary>
    /// Without what the service gives items it creates (IDs and times) inside an object's child items, such as an
    /// administrative template's settings: they're new every time the items are created, so they aren't differences.
    /// </summary>
    static JsonObject Comparable(ResourceType type, JsonObject settings)
    {
        if (type.Children is not { } children || settings[children.Property] is not JsonArray)
            return settings;
        var copy = (JsonObject)settings.DeepClone();
        StripServiceValues(copy[children.Property]);
        return copy;

        static void StripServiceValues(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var name in new[] { "id", "createdDateTime", "lastModifiedDateTime" })
                        obj.Remove(name);
                    foreach (var (_, child) in obj.ToList())
                        StripServiceValues(child);
                    break;
                case JsonArray array:
                    foreach (var child in array)
                        StripServiceValues(child);
                    break;
            }
        }
    }

    static JsonNode? Prune(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    var pruned = Prune(obj[name]);
                    if (pruned is null)
                        obj.Remove(name);
                    else if (!ReferenceEquals(pruned, obj[name]))
                        obj[name] = pruned;
                }
                return obj.Count == 0 ? null : obj;
            case JsonArray array:
                for (var i = array.Count - 1; i >= 0; i--)
                {
                    if (array[i] is JsonObject or JsonArray && Prune(array[i]) is null)
                        array.RemoveAt(i);
                }
                return array.Count == 0 ? null : array;
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length == 0:
                return null;
            default:
                return node;
        }
    }

    /// <summary>The top-level settings whose values differ, leaving out what the service sets itself and empty values.</summary>
    public static IReadOnlyList<string> Changes(ResourceType type, JsonObject first, JsonObject second) =>
        ChangesOfPruned(type, Pruned(first), Pruned(second));

    static IReadOnlyList<string> ChangesOfPruned(ResourceType type, JsonObject first, JsonObject second) =>
        first.Select(p => p.Key)
            .Union(second.Select(p => p.Key), StringComparer.OrdinalIgnoreCase)
            .Where(key => !Ignored.Contains(key) && !type.ReadOnlyProperties.Contains(key, StringComparer.OrdinalIgnoreCase))
            .Where(key => !SettingsDiff.Same(Canonical(first[key]), Canonical(second[key])))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Nested objects compare by content whatever the order of their properties.
    static JsonNode? Canonical(JsonNode? node) => node is JsonObject or JsonArray ? Normalizer.Canonicalize(node.DeepClone()) : node;

    static string NameOf(ExportedResource item) => item.Type.IsSettings ? item.Type.DisplayName : item.DisplayName;

    static string NameOf(ResourceType type, JsonObject settings, string fallback) =>
        type.IsSettings ? type.DisplayName
        : settings[type.IdentityProperty] is JsonValue value && value.TryGetValue<string>(out var name) ? name
        : fallback;
}
