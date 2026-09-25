using System.Text.Json.Nodes;
using Movewise.Core.Deploy;
using Movewise.Core.Export;

namespace Movewise.Core.Tests;

/// <summary>
/// Answers Graph paths from canned JSON, and records which paths were read. Writes are recorded too:
/// a create returns a new ID ("new-1", "new-2", …), and actions such as assign return nothing.
/// </summary>
sealed class FakeGraph : IGraphWriter
{
    readonly Dictionary<string, JsonObject> _objects = new();
    readonly Dictionary<string, JsonObject[]> _collections = new();
    readonly HashSet<string> _failing = new();
    readonly Dictionary<string, (Exception Error, int Times)> _failingWrites = new();
    int _created;

    public List<string> Requests { get; } = [];
    public List<(string Path, JsonNode Body)> Posts { get; } = [];
    public List<string> Deletes { get; } = [];

    /// <summary>Makes posts and deletes to this path throw, the given number of times.</summary>
    public FakeGraph FailingWrite(string path, Exception error, int times = int.MaxValue)
    {
        _failingWrites[path] = (error, times);
        return this;
    }

    public Task<JsonObject?> PostAsync(string path, JsonNode body, CancellationToken ct = default)
    {
        ThrowIfFailing(path);
        Posts.Add((path, body.DeepClone()));
        var isAction = path.EndsWith("/assign", StringComparison.Ordinal) || path.EndsWith("/targetApps", StringComparison.Ordinal);
        return Task.FromResult(isAction ? null : new JsonObject { ["id"] = $"new-{++_created}" });
    }

    public List<(string Path, JsonNode Body)> Patches { get; } = [];

    /// <summary>Records the change and applies it to the object at that path, so a later read sees it.</summary>
    public Task PatchAsync(string path, JsonNode body, CancellationToken ct = default)
    {
        ThrowIfFailing(path);
        Patches.Add((path, body.DeepClone()));
        if (_objects.TryGetValue(path, out var target))
        {
            foreach (var (name, value) in body.AsObject())
                target[name] = value?.DeepClone();
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        ThrowIfFailing(path);
        Deletes.Add(path);
        return Task.CompletedTask;
    }

    void ThrowIfFailing(string path)
    {
        if (!_failingWrites.TryGetValue(path, out var failure) || failure.Times <= 0)
            return;
        _failingWrites[path] = failure with { Times = failure.Times - 1 };
        throw failure.Error;
    }

    public FakeGraph Object(string path, string json)
    {
        _objects[path] = JsonNode.Parse(json)!.AsObject();
        return this;
    }

    public FakeGraph Collection(string path, params string[] items)
    {
        _collections[path] = items.Select(i => JsonNode.Parse(i)!.AsObject()).ToArray();
        return this;
    }

    public FakeGraph Failing(string path)
    {
        _failing.Add(path);
        return this;
    }

    public Task<JsonObject> GetObjectAsync(string path, CancellationToken ct = default)
    {
        Record(path);
        return Task.FromResult((JsonObject)_objects[path].DeepClone());
    }

    public Task<IReadOnlyList<JsonObject>> GetCollectionAsync(string path, CancellationToken ct = default)
    {
        Record(path);
        IReadOnlyList<JsonObject> items = _collections.TryGetValue(path, out var found)
            ? found.Select(i => (JsonObject)i.DeepClone()).ToList()
            : [];
        return Task.FromResult(items);
    }

    void Record(string path)
    {
        lock (Requests)
            Requests.Add(path);
        if (_failing.Contains(path))
            throw new InvalidOperationException($"Microsoft Graph returned 403 Forbidden for {path}");
    }
}
