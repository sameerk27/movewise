using System.Text;

namespace Movewise.Core.Export;

/// <summary>Writes exported policies to a project folder on disk, one JSON file per policy.</summary>
public static class ProjectStore
{
    /// <summary>
    /// Where Movewise keeps what it saves for the Windows user: %LOCALAPPDATA%\MovewiseData. Not Documents, which is
    /// often synced to OneDrive, and not the install folder, which updates and uninstalling replace.
    /// </summary>
    public static string DataFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MovewiseData");

    public static string DefaultRoot { get; } = Path.Combine(DataFolder, "Projects");

    public static string NewProjectFolder(string sourceDomain, string destinationDomain) =>
        Path.Combine(DefaultRoot, $"{SafeName(sourceDomain)} to {SafeName(destinationDomain)} {DateTime.Now:yyyy-MM-dd HHmm}");

    public static async Task SaveExportAsync(string projectFolder, IEnumerable<ExportedResource> items, CancellationToken ct = default, string subfolder = "export")
    {
        foreach (var item in items)
        {
            var folder = Path.Combine(projectFolder, subfolder, item.Type.Id);
            Directory.CreateDirectory(folder);
            var shortId = item.SourceId.Length > 8 ? item.SourceId[..8] : item.SourceId;
            var file = Path.Combine(folder, $"{SafeName(item.DisplayName)} [{shortId}].json");
            await File.WriteAllTextAsync(file, Normalizer.ToStableJson(item.Settings), Encoding.UTF8, ct);
        }
    }

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}
