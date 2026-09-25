using Movewise.M365;
using Velopack;

namespace Movewise.App.Services;

/// <summary>
/// Checks the release feed set in appsettings.json (UpdateUrl) for a newer Movewise. Only applies an update when the
/// admin chooses to, and never while a deployment is writing to a tenant. Does nothing in a development build,
/// which isn't installed.
/// </summary>
public sealed class UpdateService(MovewiseOptions options, MigrationService migration)
{
    UpdateManager? _manager;
    UpdateInfo? _update;

    public string? AvailableVersion => _update?.TargetFullRelease.Version.ToString();

    public bool IsUpdating { get; private set; }

    public event Action? Changed;

    public async Task CheckAsync()
    {
        if (string.IsNullOrWhiteSpace(options.UpdateUrl))
            return;
        try
        {
            _manager ??= new UpdateManager(options.UpdateUrl);
            if (!_manager.IsInstalled)
                return;
            _update = await _manager.CheckForUpdatesAsync();
            if (_update is not null)
                DiagnosticLog.Info($"Update available: {AvailableVersion}.");
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // No network, or the feed is down: try again next start.
            DiagnosticLog.Error("Couldn't check for updates.", ex);
        }
    }

    /// <summary>Downloads the update and restarts Movewise into it.</summary>
    public async Task ApplyAsync()
    {
        if (_manager is null || _update is null)
            return;
        if (migration.IsWorking)
            throw new InvalidOperationException("Finish or stop the deployment before updating.");

        // Nothing can start while the update downloads, since the restart at the end would cut it off.
        IsUpdating = true;
        migration.IsUpdating = true;
        Changed?.Invoke();
        try
        {
            await _manager.DownloadUpdatesAsync(_update);
            if (migration.IsWorking)
                throw new InvalidOperationException("The update is downloaded. Update again once the deployment has finished.");
            DiagnosticLog.Info($"Restarting into {AvailableVersion}.");
            _manager.ApplyUpdatesAndRestart(_update);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("The update failed.", ex);
            throw;
        }
        finally
        {
            IsUpdating = false;
            migration.IsUpdating = false;
            Changed?.Invoke();
        }
    }
}
