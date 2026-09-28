using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Registry;

namespace Movewise.Core.Baselines;

/// <summary>
/// A set of policies saved from a template tenant, to apply to other tenants without signing in to the template
/// again. With the policies it keeps what matching needs from the template tenant (the groups, users, apps,
/// domains and sites they point at), recorded when it was saved and answered from the file afterwards.
/// </summary>
/// <param name="Responses">The template tenant's Graph answers matching asked for, by request path.</param>
public sealed record Baseline(
    string Name,
    string TenantId,
    string TenantName,
    string InitialDomain,
    string DefaultDomain,
    DateTimeOffset Saved,
    bool IsDemo,
    IReadOnlyList<ExportedResource> Items,
    IReadOnlyDictionary<string, JsonObject> Responses)
{
    public string? Path { get; init; }
}

/// <summary>Records what a Graph reader answers, to replay later without the tenant. Only reads pass through it.</summary>
public sealed class RecordingGraph(IGraphReader inner) : IGraphReader
{
    readonly ConcurrentDictionary<string, JsonObject> _responses = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, JsonObject> Responses => _responses;

    public async Task<JsonObject> GetObjectAsync(string path, CancellationToken ct = default)
    {
        var result = await inner.GetObjectAsync(path, ct);
        _responses[path] = (JsonObject)result.DeepClone();
        return result;
    }

    public async Task<IReadOnlyList<JsonObject>> GetCollectionAsync(string path, CancellationToken ct = default)
    {
        var result = await inner.GetCollectionAsync(path, ct);
        _responses[path] = new JsonObject { ["value"] = new JsonArray(result.Select(i => (JsonNode)i.DeepClone()).ToArray()) };
        return result;
    }
}

/// <summary>
/// Answers Graph requests from what a baseline recorded. A search it has no answer for finds nothing: some lookups
/// in the template tenant are only made when the destination lacks something, which it didn't when the baseline was
/// saved against the template itself. Reading one object it has no answer for fails, as a missing object would:
/// matching then says it couldn't read that object, and the admin decides on the Map screen.
/// </summary>
public sealed class OfflineGraph(IReadOnlyDictionary<string, JsonObject> responses) : IGraphReader
{
    public Task<JsonObject> GetObjectAsync(string path, CancellationToken ct = default)
    {
        if (responses.TryGetValue(path, out var found))
            return Task.FromResult((JsonObject)found.DeepClone());
        if (path.Contains("$filter=", StringComparison.Ordinal) || path.Contains("search=", StringComparison.Ordinal))
            return Task.FromResult(new JsonObject { ["value"] = new JsonArray() });
        return Task.FromException<JsonObject>(new InvalidOperationException("This isn't saved in the baseline."));
    }

    public async Task<IReadOnlyList<JsonObject>> GetCollectionAsync(string path, CancellationToken ct = default)
    {
        var page = await GetObjectAsync(path, ct);
        return page["value"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
    }
}

/// <summary>Saves and reads baselines, one JSON file each, in %LOCALAPPDATA%\MovewiseData\Baselines.</summary>
public static class BaselineStore
{
    const int Format = 1;

    public static string DefaultRoot { get; } = System.IO.Path.Combine(ProjectStore.DataFolder, "Baselines");

    /// <summary>
    /// Makes a baseline of the selected policies. Matching them against the template tenant itself makes every
    /// lookup matching will need later; those answers are recorded with the policies. Only reads.
    /// </summary>
    public static async Task<Baseline> CaptureAsync(
        string name, TenantClients template, Tenants.TenantInfo info, bool isDemo,
        IReadOnlyCollection<ExportedResource> selected, IReadOnlyCollection<ExportedResource> exported,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var recording = new RecordingGraph(template.Graph);
        progress?.Report("Saving what the policies point at…");
        await Matcher.BuildAsync(recording, template, selected, exported, null, null, ct);
        return new Baseline(name, info.TenantId, info.DisplayName, info.InitialDomain, info.DefaultDomain, DateTimeOffset.Now, isDemo,
            selected.ToList(), new Dictionary<string, JsonObject>(recording.Responses));
    }

    public static async Task<Baseline> SaveAsync(Baseline baseline, string? root = null, CancellationToken ct = default)
    {
        var folder = root ?? DefaultRoot;
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, $"{ProjectStore.SafeName(baseline.Name)} {baseline.Saved.LocalDateTime:yyyy-MM-dd HHmmss}.json");
        var responses = new JsonObject();
        foreach (var (request, answer) in baseline.Responses.OrderBy(r => r.Key, StringComparer.Ordinal))
            responses[request] = answer.DeepClone();

        var json = new JsonObject
        {
            ["format"] = Format,
            ["name"] = baseline.Name,
            ["tenantId"] = baseline.TenantId,
            ["tenantName"] = baseline.TenantName,
            ["initialDomain"] = baseline.InitialDomain,
            ["defaultDomain"] = baseline.DefaultDomain,
            ["saved"] = baseline.Saved.ToString("o"),
            ["demo"] = baseline.IsDemo,
            ["items"] = new JsonArray(baseline.Items.Select(i => (JsonNode)new JsonObject
            {
                ["type"] = i.Type.Id,
                ["id"] = i.SourceId,
                ["name"] = i.DisplayName,
                ["settings"] = i.Settings.DeepClone(),
            }).ToArray()),
            ["responses"] = responses,
        };
        await File.WriteAllTextAsync(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8, ct);
        return baseline with { Path = path };
    }

    /// <summary>Reads a baseline. Items of types this version doesn't know are left out.</summary>
    public static async Task<Baseline> LoadAsync(string path, CancellationToken ct = default)
    {
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))?.AsObject()
            ?? throw new InvalidDataException("The baseline file is empty.");
        if (json["format"]?.GetValue<int>() is not Format)
            throw new InvalidDataException("This isn't a Movewise baseline, or it's from a newer version of Movewise.");

        var items = new List<ExportedResource>();
        foreach (var item in (json["items"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (ResourceRegistry.All.FirstOrDefault(t => t.Id == item["type"]?.GetValue<string>()) is not { } type
                || item["settings"] is not JsonObject settings)
                continue;
            var copy = (JsonObject)settings.DeepClone();
            items.Add(new ExportedResource(type, item["id"]?.GetValue<string>() ?? "", item["name"]?.GetValue<string>() ?? "", copy, DependencyExtractor.Extract(copy, type)));
        }
        var responses = (json["responses"] as JsonObject ?? [])
            .Where(p => p.Value is JsonObject)
            .ToDictionary(p => p.Key, p => (JsonObject)p.Value!.DeepClone(), StringComparer.Ordinal);

        return new Baseline(
            json["name"]?.GetValue<string>() ?? "",
            json["tenantId"]?.GetValue<string>() ?? "",
            json["tenantName"]?.GetValue<string>() ?? "",
            json["initialDomain"]?.GetValue<string>() ?? "",
            json["defaultDomain"]?.GetValue<string>() ?? "",
            DateTimeOffset.Parse(json["saved"]?.GetValue<string>() ?? DateTimeOffset.MinValue.ToString("o"), System.Globalization.CultureInfo.InvariantCulture),
            json["demo"]?.GetValue<bool>() == true,
            items,
            responses) { Path = path };
    }

    /// <summary>The baselines saved so far, newest first. Files that can't be read are skipped.</summary>
    public static async Task<IReadOnlyList<Baseline>> ListAsync(string? root = null, CancellationToken ct = default)
    {
        var folder = root ?? DefaultRoot;
        if (!Directory.Exists(folder))
            return [];
        var baselines = new List<Baseline>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            try
            {
                baselines.Add(await LoadAsync(file, ct));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or FormatException or InvalidOperationException)
            {
            }
        }
        return baselines.OrderByDescending(b => b.Saved).ToList();
    }
}
