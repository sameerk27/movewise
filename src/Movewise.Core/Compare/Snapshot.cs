using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Movewise.Core.Export;
using Movewise.Core.Registry;

namespace Movewise.Core.Compare;

/// <summary>A tenant's policies and settings as they were at one moment, kept to compare the tenant with later.</summary>
/// <param name="Types">
/// Every type that was read, including those the tenant had none of: a policy created later in one of them is
/// only seen as added if the comparison reads that type again.
/// </param>
public sealed record Snapshot(string TenantId, string TenantName, DateTimeOffset Taken, IReadOnlyList<ResourceType> Types, IReadOnlyList<ExportedResource> Items)
{
    /// <summary>Where the file is on disk, once saved or loaded.</summary>
    public string? Path { get; init; }
}

/// <summary>
/// Saves snapshots as one JSON file each, in %LOCALAPPDATA%\MovewiseData\Snapshots, and reads them back. Unlike a
/// project export, a snapshot keeps each item's ID and name, which comparing needs.
/// </summary>
public static class SnapshotStore
{
    const int Format = 1;

    public static string DefaultRoot { get; } = System.IO.Path.Combine(ProjectStore.DataFolder, "Snapshots");

    public static async Task<Snapshot> SaveAsync(Snapshot snapshot, string? root = null, CancellationToken ct = default)
    {
        var folder = root ?? DefaultRoot;
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, $"{ProjectStore.SafeName(snapshot.TenantName)} {snapshot.Taken.LocalDateTime:yyyy-MM-dd HHmmss}.json");

        var json = new JsonObject
        {
            ["format"] = Format,
            ["tenantId"] = snapshot.TenantId,
            ["tenantName"] = snapshot.TenantName,
            ["taken"] = snapshot.Taken.ToString("o"),
            ["types"] = new JsonArray(snapshot.Types.Select(t => (JsonNode)t.Id).ToArray()),
            ["items"] = new JsonArray(snapshot.Items.Select(i => (JsonNode)new JsonObject
            {
                ["type"] = i.Type.Id,
                ["id"] = i.SourceId,
                ["name"] = i.DisplayName,
                ["settings"] = i.Settings.DeepClone(),
            }).ToArray()),
        };
        await File.WriteAllTextAsync(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8, ct);
        return snapshot with { Path = path };
    }

    /// <summary>Reads a snapshot. Items of types this version doesn't know are left out, and so is anything unreadable.</summary>
    public static async Task<Snapshot> LoadAsync(string path, CancellationToken ct = default)
    {
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))?.AsObject()
            ?? throw new InvalidDataException("The snapshot file is empty.");
        if (json["format"]?.GetValue<int>() is not Format)
            throw new InvalidDataException("This isn't a Movewise snapshot, or it's from a newer version of Movewise.");

        // Types this version no longer knows are left out; older files without the list fall back to the items' types.
        var types = (json["types"] as JsonArray ?? [])
            .Select(t => ResourceRegistry.All.FirstOrDefault(r => r.Id == t?.GetValue<string>()))
            .OfType<ResourceType>()
            .ToList();
        var items = new List<ExportedResource>();
        foreach (var item in (json["items"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (ResourceRegistry.All.FirstOrDefault(t => t.Id == item["type"]?.GetValue<string>()) is not { } type
                || item["settings"] is not JsonObject settings)
                continue;
            var copy = (JsonObject)settings.DeepClone();
            items.Add(new ExportedResource(type, item["id"]?.GetValue<string>() ?? "", item["name"]?.GetValue<string>() ?? "", copy, DependencyExtractor.Extract(copy, type)));
        }
        foreach (var type in items.Select(i => i.Type).Distinct().Where(t => !types.Contains(t)))
            types.Add(type);

        return new Snapshot(
            json["tenantId"]?.GetValue<string>() ?? "",
            json["tenantName"]?.GetValue<string>() ?? "",
            DateTimeOffset.Parse(json["taken"]?.GetValue<string>() ?? DateTimeOffset.MinValue.ToString("o"), System.Globalization.CultureInfo.InvariantCulture),
            types,
            items) { Path = path };
    }

    /// <summary>The snapshots saved so far, newest first. Files that can't be read are skipped.</summary>
    public static async Task<IReadOnlyList<Snapshot>> ListAsync(string? root = null, CancellationToken ct = default)
    {
        var folder = root ?? DefaultRoot;
        if (!Directory.Exists(folder))
            return [];
        var snapshots = new List<Snapshot>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                snapshots.Add(await LoadAsync(file, ct));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or FormatException or InvalidOperationException)
            {
            }
        }
        return snapshots.OrderByDescending(s => s.Taken).ToList();
    }
}
