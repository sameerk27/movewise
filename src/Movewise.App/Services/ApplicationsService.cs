using Movewise.App.State;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;

namespace Movewise.App.Services;

/// <summary>
/// What the Applications screens ask for: read the source's apps, match their users and groups, check, deploy and
/// roll back. Uses the tenants signed in on Connect, and never runs while a policy deployment does.
/// </summary>
public sealed class ApplicationsService
{
    readonly MigrationState _state;
    readonly ApplicationsState _apps;
    readonly MigrationService _migration;

    public ApplicationsService(MigrationState state, ApplicationsState apps, MigrationService migration)
    {
        _state = state;
        _apps = apps;
        _migration = migration;
        state.Changed += () => apps.Follow(state);
        apps.Follow(state);
    }

    public Task ReadAsync(IProgress<string> progress, CancellationToken ct = default) => MigrationService.Logged("Reading apps", async () =>
    {
        var source = _state.Source ?? throw new InvalidOperationException("Sign in to the source tenant first.");
        if (source.IsBaseline)
            throw new InvalidOperationException("The source is a saved baseline, which has no apps. Sign in to the source tenant to migrate its apps.");
        var result = await Exporter.ExportAppsAsync(source.Graph, source.Info.TenantId, progress, ct);
        EnsureSameTenants(source, null);
        _apps.SetApps(result);
        DiagnosticLog.Info($"Read {result.Items.Count} apps; {result.Warnings.Count} couldn't be read.");
    });

    /// <summary>Matches the users and groups the chosen apps point at, keeping decisions the admin already made.</summary>
    public Task BuildMappingAsync(IProgress<string> progress, CancellationToken ct = default) => MigrationService.Logged("Matching app users and groups", async () =>
    {
        var source = _state.Source ?? throw new InvalidOperationException("Sign in to the source tenant first.");
        var destination = _state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");
        var plan = await Matcher.BuildAsync(source.Graph, destination.Clients, _apps.SelectedApps, _apps.Apps, _apps.Mapping, progress, ct);
        EnsureSameTenants(source, destination);
        _apps.SetMapping(plan);
        DiagnosticLog.Info($"Matched {plan.Items.Count} app users and groups, {plan.UnresolvedCount} without a match.");
    });

    /// <summary>Dry run against the destination. Matches again first if the selection changed since mapping.</summary>
    public Task RunPreflightAsync(IProgress<string> progress, CancellationToken ct = default) => MigrationService.Logged("App pre-flight", async () =>
    {
        var destination = _state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");
        _apps.SetPreflight(null);
        if (!_apps.MappingIsCurrent)
            await BuildMappingAsync(progress, ct);

        var decisions = Decisions(_apps.Mapping);
        var migrated = _state.Source is { } source ? _migration.MigratedAppIds(source, destination) : null;
        var report = await AppPreflight.RunAsync(destination.Clients, destination.Info, _apps.SelectedApps, _apps.Mapping!, _apps.Choices, progress, ct, migrated);
        EnsureSameTenants(null, destination);
        if (Decisions(_apps.Mapping) != decisions || !_apps.MappingIsCurrent)
            throw new InvalidOperationException("The matches or the selection changed while the dry run was running. Run it again.");
        _apps.SetPreflight(report);
        DiagnosticLog.Info($"App pre-flight: {report.CreateCount} to create, {report.UpdateCount} to update, {report.SkipCount} skipped, {report.BlockedCount} blocked.");
    });

    public Task<IReadOnlyList<ObjectRef>> SearchDestinationAsync(string targetType, string text, CancellationToken ct = default)
    {
        var destination = _state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");
        return DestinationSearch.SearchAsync(destination.Clients, targetType, text, ct);
    }

    // ---------- Deploy ----------

    CancellationTokenSource? _running;

    /// <summary>True while apps are being deployed or rolled back.</summary>
    public bool IsBusy => _running is not null;

    /// <summary>True while a deployment is being prepared (pre-flight again).</summary>
    public bool IsPreparing { get; private set; }

    public bool IsWorking => IsBusy || IsPreparing;

    /// <summary>What the running deployment is doing, for the screen.</summary>
    public string? BusyText { get; private set; }

    /// <summary>Runs pre-flight once more (the destination may have changed), then builds and saves the run. Nothing is created yet.</summary>
    public async Task PrepareDeploymentAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        RefuseWhileWorking();
        IsPreparing = true;
        _migration.AppsWorking = true;
        BusyText = "Running pre-flight once more…";
        _apps.NotifyChanged();
        var shown = new Progress<string>(text =>
        {
            BusyText = text;
            progress.Report(text);
            _apps.NotifyChanged();
        });
        try
        {
            await MigrationService.Logged("Preparing the app deployment", async () =>
            {
                var source = _state.Source ?? throw new InvalidOperationException("Sign in to the source tenant first.");
                var destination = _state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");
                await RunPreflightAsync(shown, ct);
                var report = _apps.Preflight!;
                if (report.HasBlockers && !report.BlockedSkipped)
                    throw new InvalidOperationException("Pre-flight found new blockers. Fix them on the Pre-flight screen, then deploy.");
                if (!report.CanDeploy)
                    throw new InvalidOperationException("There's nothing to do: every chosen app is skipped or already as it should be.");

                var run = AppDeployer.Plan(report, source.Info, destination.Info);
                run.Folder = RunStore.NewRunFolder(source.Info.InitialDomain, destination.Info.InitialDomain, run.RunId, _migration.RunsRoot);
                await RunStore.SaveAsync(run, ct);
                _apps.SetRun(run);
                DiagnosticLog.Info($"Prepared app run {run.RunId}: {run.Steps.Count} apps.");
            });
        }
        finally
        {
            IsPreparing = false;
            _migration.AppsWorking = false;
            BusyText = null;
            _apps.NotifyChanged();
            _state.NotifyChanged();
        }
    }

    /// <summary>Creates everything in the current run that isn't finished. Also used to retry after failures or a stop.</summary>
    public Task DeployAsync() => WriteAsync("Deploying", AppDeployer.RunAsync);

    /// <summary>Deletes what the current run created, and puts back what it changed.</summary>
    public Task RollbackAsync() => WriteAsync("Rolling back", AppDeployer.RollbackAsync);

    /// <summary>
    /// The app ID map the run records new app IDs in. Read before anything is written: a map that can't be read would
    /// otherwise be overwritten, losing the app IDs of earlier runs.
    /// </summary>
    AppIdMap LoadAppIds(DeployRun run)
    {
        try
        {
            return AppIdMap.Load(run.DestinationTenantId, _migration.RunsRoot);
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException or System.IO.InvalidDataException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Couldn't read the app ID map at {AppIdMap.PathFor(run.DestinationTenantId, _migration.RunsRoot)}, so nothing was changed. {ex.Message}");
        }
    }

    /// <summary>Stops once the request in progress is finished. The run can be resumed or rolled back later.</summary>
    public void Stop() => _running?.Cancel();

    async Task WriteAsync(string verb, Func<IGraphWriter, DeployRun, Func<Task>, Action<DeployStep>, CancellationToken, AppIdMap, Task> work)
    {
        var destination = _state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");
        var run = _apps.Run ?? throw new InvalidOperationException("There's no deployment to run.");
        if (run.DestinationTenantId != destination.Info.TenantId)
            throw new InvalidOperationException($"This run was for {run.DestinationName}, but you're signed in to {destination.Info.DisplayName}.");
        RefuseWhileWorking();
        var appIds = LoadAppIds(run);

        using var cts = new CancellationTokenSource();
        _running = cts;
        _migration.AppsWorking = true;
        _state.NotifyChanged();
        using var awake = KeepAwake.Start();
        try
        {
            var total = run.Steps.Count;
            void Changed(DeployStep step)
            {
                if (step.Status is not (StepStatus.Creating or StepStatus.Created))
                    DiagnosticLog.Info($"App run {run.RunId}: app {run.Steps.IndexOf(step) + 1} of {total} {step.Status}. {step.Message}");
                BusyText = $"{verb}: {step.DisplayName} ({run.Steps.IndexOf(step) + 1} of {total})";
                _apps.NotifyChanged();
            }

            BusyText = $"{verb}…";
            _apps.NotifyChanged();
            // Saving isn't cancelled with the run: what's on disk must always match the destination.
            await MigrationService.Logged($"{verb} app run {run.RunId}",
                () => work(destination.Clients.GraphWriter, run, () => RunStore.SaveAsync(run), Changed, cts.Token, appIds));
            DiagnosticLog.Info($"{verb} app run {run.RunId} finished: {run.DoneCount} done, {run.FailedCount} failed, {run.RemainingCount} unfinished.");
        }
        finally
        {
            _running = null;
            _migration.AppsWorking = false;
            BusyText = null;
            _apps.NotifyChanged();
            _state.NotifyChanged();
        }
    }

    /// <summary>Saved app runs into the connected destination, newest first.</summary>
    public IReadOnlyList<RunSummary> PreviousRuns() =>
        _state.Destination is { } destination ? RunStore.List(destination.Info.TenantId, _migration.RunsRoot, RunKind.Applications) : [];

    public void OpenRun(string path)
    {
        RefuseWhileWorking();
        var destination = _state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");
        var run = RunStore.Load(path);
        if (run.Kind != RunKind.Applications)
            throw new InvalidOperationException("That run migrated policies. Open it in Migrations.");
        if (run.DestinationTenantId != destination.Info.TenantId)
            throw new InvalidOperationException($"This run was for {run.DestinationName}, not {destination.Info.DisplayName}.");
        _apps.SetRun(run);
    }

    /// <summary>Puts the current run away so a new one can be prepared. It stays on disk.</summary>
    public void CloseRun()
    {
        if (IsWorking)
            throw new InvalidOperationException("Stop the deployment first.");
        _apps.SetRun(null);
    }

    void RefuseWhileWorking()
    {
        if (_migration.IsWorking)
            throw new InvalidOperationException("A deployment is being prepared or running. Wait for it to finish first.");
        if (_migration.IsUpdating)
            throw new InvalidOperationException(MigrationService.UpdatingMessage);
    }

    /// <summary>Reading and matching take a while; a tenant changed meanwhile gets none of their results.</summary>
    void EnsureSameTenants(TenantConnection? source, TenantConnection? destination)
    {
        if ((source is not null && !ReferenceEquals(_state.Source, source)) || (destination is not null && !ReferenceEquals(_state.Destination, destination)))
            throw new InvalidOperationException("A tenant was signed out or changed while this was running, so its results were discarded. Start again.");
    }

    static string Decisions(MappingPlan? plan) =>
        plan is null ? "" : string.Join("|", plan.Items.Select(m => $"{m.Key}={m.Kind}:{m.Destination?.Id}"));
}
