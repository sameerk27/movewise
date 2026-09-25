using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Movewise.Core.Export;

namespace Movewise.Core.Deploy;

/// <summary>A saved run, as listed when choosing one to resume or roll back.</summary>
public sealed record RunSummary(string Path, string RunId, DateTimeOffset Started, string SourceName, int Created, int Remaining, bool RolledBack);

/// <summary>
/// Saves each run to its own folder under %LOCALAPPDATA%\MovewiseData\Runs. Runs hold full policy settings, so they're
/// kept out of Documents, which is often synced to OneDrive.
/// </summary>
public static class RunStore
{
    public const string FileName = "run.json";
    public const string SnapshotFolder = "destination-before";

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultRoot { get; } = Path.Combine(ProjectStore.DataFolder, "Runs");

    /// <summary>Where earlier versions saved runs. They're still listed, and resumed or rolled back where they are.</summary>
    public static string OldRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Movewise", "Runs");

    public static string NewRunFolder(string sourceDomain, string destinationDomain, string runId, string? root = null)
    {
        var folder = Path.Combine(root ?? DefaultRoot,
            $"{ProjectStore.SafeName(sourceDomain)} to {ProjectStore.SafeName(destinationDomain)} {DateTime.Now:yyyy-MM-dd HHmm} {runId}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Writes the run to a temporary file first, so a crash mid-write never leaves a broken run.json.</summary>
    public static async Task SaveAsync(DeployRun run, CancellationToken ct = default)
    {
        var folder = run.Folder ?? throw new InvalidOperationException("The run has no folder.");
        var path = Path.Combine(folder, FileName);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(run, Options), Encoding.UTF8, ct);
        File.Move(temp, path, overwrite: true);
    }

    public static DeployRun Load(string path)
    {
        var run = JsonSerializer.Deserialize<DeployRun>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException($"{path} isn't a Movewise run.");
        run.Folder = Path.GetDirectoryName(path);
        return run;
    }

    /// <summary>Runs into the given destination, newest first. Files that can't be read are left out.</summary>
    public static IReadOnlyList<RunSummary> List(string destinationTenantId, string? root = null)
    {
        var folders = (root is null ? new[] { DefaultRoot, OldRoot } : [root]).Where(Directory.Exists);

        var runs = new List<RunSummary>();
        foreach (var path in folders.SelectMany(folder => Directory.EnumerateFiles(folder, FileName, SearchOption.AllDirectories)))
        {
            try
            {
                var run = Load(path);
                if (run.DestinationTenantId == destinationTenantId)
                    runs.Add(new RunSummary(path, run.RunId, run.Started, run.SourceName, run.CreatedInDestination, run.RemainingCount, run.RolledBack is not null));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
            }
        }
        return runs.OrderByDescending(r => r.Started).ToList();
    }
}
