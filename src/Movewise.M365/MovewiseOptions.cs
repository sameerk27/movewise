using System.Text.Json;
using System.Text.Json.Nodes;

namespace Movewise.M365;

/// <summary>Where the client ID in use came from.</summary>
public enum ClientIdSource { None, AppSettings, Saved, Environment }

/// <summary>
/// Settings, in order of precedence: the MOVEWISE_CLIENT_ID environment variable, then appsettings.json next to the app
/// (built into a release), then the registration Movewise set up by itself during a destination sign-in
/// (%LOCALAPPDATA%\MovewiseData\settings.json, outside the install folder).
/// </summary>
public sealed class MovewiseOptions
{
    const string EnvironmentVariable = "MOVEWISE_CLIENT_ID";

    static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    /// <summary>
    /// Where Movewise keeps what it saves for the Windows user (settings, logs). Not the install folder
    /// (%LOCALAPPDATA%\Movewise), which the installer and updater manage and an uninstall removes.
    /// </summary>
    public static string DataFolder => Movewise.Core.Export.ProjectStore.DataFolder;

    public static string UserSettingsPath { get; } = Path.Combine(DataFolder, "settings.json");

    // Where 0.1.1 to 0.1.3 saved settings, inside the install folder.
    static readonly string OldUserSettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Movewise", "settings.json");

    /// <summary>Application (client) ID of the Movewise app registration. See docs/app-registration.md.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Where releases are published (the folder Publish.ps1 fills), or empty to turn update checks off.</summary>
    public string UpdateUrl { get; set; } = "";

    public ClientIdSource ClientIdSource { get; private set; }

    public bool IsConfigured => IsValidClientId(ClientId);

    public static bool IsValidClientId(string? value) => Guid.TryParse(value?.Trim(), out var id) && id != Guid.Empty;

    public static MovewiseOptions Load(string appSettingsPath)
    {
        MoveOldSettings();
        return Load(appSettingsPath, UserSettingsPath);
    }

    /// <summary>Brings settings saved by an earlier version into the data folder, once.</summary>
    static void MoveOldSettings()
    {
        try
        {
            if (!File.Exists(UserSettingsPath) && File.Exists(OldUserSettingsPath))
            {
                Directory.CreateDirectory(DataFolder);
                File.Copy(OldUserSettingsPath, UserSettingsPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static MovewiseOptions Load(string appSettingsPath, string userSettingsPath)
    {
        var options = Read(appSettingsPath) ?? new MovewiseOptions();
        if (options.IsConfigured)
            options.ClientIdSource = ClientIdSource.AppSettings;

        // What Movewise set up by itself is only used when no registration was built into the release.
        if (!options.IsConfigured && Read(userSettingsPath) is { } saved && IsValidClientId(saved.ClientId))
        {
            options.ClientId = saved.ClientId.Trim();
            options.ClientIdSource = ClientIdSource.Saved;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (IsValidClientId(fromEnvironment))
        {
            options.ClientId = fromEnvironment!.Trim();
            options.ClientIdSource = ClientIdSource.Environment;
        }

        return options;
    }

    /// <summary>Saves the client ID for this Windows user and uses it from now on.</summary>
    public void SaveClientId(string clientId) => SaveClientId(clientId, UserSettingsPath);

    public void SaveClientId(string clientId, string userSettingsPath)
    {
        if (!IsValidClientId(clientId))
            throw new ArgumentException("That isn't a client ID. It looks like 00000000-0000-0000-0000-000000000000.", nameof(clientId));
        if (ClientIdSource == ClientIdSource.Environment)
            throw new InvalidOperationException($"The client ID is set by the {EnvironmentVariable} environment variable. Change or remove it there.");

        var id = Guid.Parse(clientId.Trim()).ToString();
        var settings = File.Exists(userSettingsPath) ? JsonNode.Parse(File.ReadAllText(userSettingsPath), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip }) as JsonObject : null;
        settings ??= new JsonObject();
        settings["ClientId"] = id;

        Directory.CreateDirectory(Path.GetDirectoryName(userSettingsPath)!);
        File.WriteAllText(userSettingsPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        ClientId = id;
        ClientIdSource = ClientIdSource.Saved;
    }

    static MovewiseOptions? Read(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<MovewiseOptions>(File.ReadAllText(path), ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
