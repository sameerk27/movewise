using System.Text;
using System.Text.Json;

namespace Movewise.Core.Deploy;

/// <summary>One app registration Movewise recreated in the destination, where it has a new app ID.</summary>
public sealed record AppIdEntry(string SourceTenantId, string SourceAppId, string DestinationAppId, string DisplayName, string RunId, DateTimeOffset Recorded);

/// <summary>
/// Which app registrations Movewise migrated into one destination: source app ID → destination app ID. Kept in its
/// own file per destination beside the run folders, written as each app is created and taken out when it's rolled
/// back, so the policy migration can match policies to the new app IDs.
/// </summary>
public sealed class AppIdMap
{
    public const string Folder = "App IDs";

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    AppIdMap(string path, List<AppIdEntry> entries)
    {
        Path = path;
        _entries = entries;
    }

    readonly List<AppIdEntry> _entries;

    public string Path { get; }

    public IReadOnlyList<AppIdEntry> Entries => _entries;

    /// <param name="root">The runs folder (<see cref="RunStore.DefaultRoot"/> unless given).</param>
    public static string PathFor(string destinationTenantId, string? root = null) =>
        System.IO.Path.Combine(root ?? RunStore.DefaultRoot, Folder, $"{destinationTenantId}.json");

    /// <summary>The map for a destination; empty when nothing has been migrated there yet.</summary>
    public static AppIdMap Load(string destinationTenantId, string? root = null)
    {
        var path = PathFor(destinationTenantId, root);
        if (!File.Exists(path))
            return new AppIdMap(path, []);
        var entries = JsonSerializer.Deserialize<List<AppIdEntry>>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException($"{path} isn't a Movewise app ID map.");
        return new AppIdMap(path, entries);
    }

    /// <summary>Source app ID → destination app ID, for the apps migrated from one source.</summary>
    public IReadOnlyDictionary<string, string> For(string sourceTenantId) => _entries
        .Where(e => string.Equals(e.SourceTenantId, sourceTenantId, StringComparison.OrdinalIgnoreCase))
        .GroupBy(e => e.SourceAppId, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Recorded).Last().DestinationAppId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Records a migrated app, replacing an earlier entry for the same source app.</summary>
    public Task RecordAsync(AppIdEntry entry)
    {
        _entries.RemoveAll(e => Same(e, entry.SourceTenantId, entry.SourceAppId));
        _entries.Add(entry);
        return SaveAsync();
    }

    /// <summary>Takes out the entry a run made for a source app, once that app is rolled back.</summary>
    public Task RemoveAsync(string sourceTenantId, string sourceAppId, string runId)
    {
        if (_entries.RemoveAll(e => Same(e, sourceTenantId, sourceAppId) && e.RunId == runId) == 0)
            return Task.CompletedTask;
        return SaveAsync();
    }

    /// <summary>Writes a temporary file first, so a crash mid-write never leaves a broken map.</summary>
    async Task SaveAsync()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(_entries, Options), Encoding.UTF8);
        File.Move(temp, Path, overwrite: true);
    }

    static bool Same(AppIdEntry entry, string sourceTenantId, string sourceAppId) =>
        string.Equals(entry.SourceTenantId, sourceTenantId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(entry.SourceAppId, sourceAppId, StringComparison.OrdinalIgnoreCase);
}
