using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Movewise.Core.Diagnostics;

namespace Movewise.App.Services;

/// <summary>
/// A daily log file under %LOCALAPPDATA%\MovewiseData\Logs for support. Every line is redacted before it's written
/// (see <see cref="Redactor"/>), so the log can be sent without exposing tenants, people or tokens.
/// </summary>
public static class DiagnosticLog
{
    const int KeepDays = 14;
    static readonly object Gate = new();

    public static string Folder { get; } = Path.Combine(Movewise.M365.MovewiseOptions.DataFolder, "Logs");

    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? error = null) => Write("ERROR", message, error);

    static void Write(string level, string message, Exception? error)
    {
        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append(' ').Append(level).Append(' ')
            .Append(Redactor.Redact(message));
        if (error is not null)
            line.AppendLine().Append(Redactor.Redact(error.ToString()));

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, $"movewise-{DateTime.Now:yyyy-MM-dd}.log"), line.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // Logging must never break the app.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes log files older than two weeks. Called once at startup.</summary>
    public static void Prune()
    {
        try
        {
            if (!Directory.Exists(Folder))
                return;
            foreach (var file in Directory.EnumerateFiles(Folder, "movewise-*.log"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-KeepDays))
                    File.Delete(file);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Zips the logs, with the app and Windows versions, for sending to support.</summary>
    public static void SaveSupportBundle(string zipPath)
    {
        lock (Gate)
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            var about = zip.CreateEntry("about.txt");
            using (var writer = new StreamWriter(about.Open()))
            {
                writer.WriteLine($"Movewise {Version}");
                writer.WriteLine($"Windows {Environment.OSVersion.Version}, {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
                writer.WriteLine($".NET {Environment.Version}");
                writer.WriteLine($"Saved {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
            }
            if (Directory.Exists(Folder))
            {
                foreach (var file in Directory.EnumerateFiles(Folder, "movewise-*.log"))
                    zip.CreateEntryFromFile(file, Path.GetFileName(file));
            }
        }
    }
}
