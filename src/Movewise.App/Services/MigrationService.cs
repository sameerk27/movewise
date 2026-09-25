using Microsoft.Identity.Client;
using Movewise.App.State;
using Movewise.Core.Access;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.M365;
using Movewise.M365.Auth;
using Movewise.M365.Graph;
using Movewise.M365.PowerShell;

namespace Movewise.App.Services;

/// <summary>What the screens ask for: connect, discover, map, check, deploy and roll back.</summary>
public sealed class MigrationService(AuthService auth, MigrationState state, MovewiseOptions options, AppRegistrationCreator creator)
{
    /// <summary>What the first sign-in set up, when Movewise had no app registration yet.</summary>
    public AppRegistrationResult? LastSetup { get; private set; }

    /// <summary>Services whose policies this build can read. The rest arrive in later phases.</summary>
    public static IReadOnlyList<M365Service> Supported { get; } =
        [M365Service.Entra, M365Service.Intune, M365Service.Defender, M365Service.DefenderEndpoint, M365Service.Exchange, M365Service.Purview, M365Service.Teams, M365Service.SharePoint];

    /// <summary>
    /// True when an administrator fixed the app registration for this PC (built into the release, or set by the
    /// MOVEWISE_CLIENT_ID environment variable). Movewise then never creates one of its own.
    /// </summary>
    public bool RegistrationIsManaged => options.ClientIdSource is ClientIdSource.AppSettings or ClientIdSource.Environment;

    /// <summary>The source can only be signed in to once the destination has set Movewise up.</summary>
    public bool CanSignInToSource => RegistrationIsManaged || (state.Destination is not null && !state.Destination.IsDemo);

    /// <summary>
    /// Signs in to one tenant. The destination is signed in to first, and that makes sure Movewise's app registration
    /// exists in the destination tenant (see <see cref="AppRegistrationCreator"/>): created the first time, reused and
    /// repaired after that. The source then only approves it, once, at its first sign-in, and is only ever read, so
    /// nothing is added to it.
    /// </summary>
    public Task ConnectAsync(TenantRole role, IProgress<string>? progress = null, CancellationToken ct = default) => Logged($"Connecting the {role.ToString().ToLowerInvariant()} tenant", async () =>
    {
        var scopes = Scopes.GraphFor(role);
        string? loginHint = null;
        ThrowIfWorking();
        // A real tenant is never paired with a demo one: demo policies must never reach a real destination.
        if (IsDemo)
            throw new InvalidOperationException("Leave the demo first, then sign in to your tenants.");
        if (role == TenantRole.Source && !CanSignInToSource)
            throw new InvalidOperationException("Sign in to the destination tenant first: that sets Movewise up.");

        if (role == TenantRole.Destination && !RegistrationIsManaged)
        {
            var saved = options.ClientIdSource == ClientIdSource.Saved ? options.ClientId : null;
            var setup = await creator.CreateAsync(saved, progress ?? new Progress<string>(), ct);
            if (!string.Equals(setup.ClientId, options.ClientId, StringComparison.OrdinalIgnoreCase))
            {
                // A different registration: sessions made with the old one can't continue.
                await DisconnectAsync(TenantRole.Source);
                options.SaveClientId(setup.ClientId);
                auth.Reset();
            }
            LastSetup = setup;
            loginHint = setup.SignedInAs;
            DiagnosticLog.Info($"Set up the app registration {setup.ClientId} ({(setup.Reused ? "updated" : "created")}). Consent granted: {setup.ConsentGranted}. {string.Join(" ", setup.Notes)}");
            state.NotifyChanged();

            // A brand-new registration takes a little while to be known to Microsoft sign-in everywhere.
            if (!setup.Reused)
            {
                progress?.Report("Movewise is set up. Waiting a few seconds for Microsoft to publish it…");
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
            }
        }

        progress?.Report("Waiting for sign-in…");
        var signIn = await auth.SignInAsync(scopes, ct, loginHint);
        var account = signIn.Account;
        var graph = new GraphClient(token => auth.GetTokenAsync(account, scopes, token));
        // Defender for Endpoint has an API of its own, with its own token; asked for only when indicators are read.
        var defenderApi = new GraphClient(token => auth.GetTokenAsync(account, Scopes.DefenderEndpoint, token), new Uri("https://api.security.microsoft.com/"));
        var info = await TenantInspector.InspectAsync(graph, ct);

        // Signing in took a while: make sure nothing started meanwhile (the buttons are off, but just in case).
        if (IsWorking || IsDemo)
        {
            await auth.SignOutAsync(account);
            throw new InvalidOperationException(IsDemo ? "Leave the demo first, then sign in to your tenants." : "Wait for the deployment to finish, then sign in again.");
        }

        // The destination must be the tenant Movewise was just set up in.
        if (role == TenantRole.Destination && !RegistrationIsManaged && LastSetup?.TenantId is { } setupTenant
            && !string.Equals(setupTenant, info.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            await auth.SignOutAsync(account);
            throw new InvalidOperationException(
                $"Movewise was set up in {LastSetup.Tenant}, but you then signed in to {info.DisplayName}. Sign in to the destination again, with the same admin account both times.");
        }

        if (info.IsGuest)
        {
            await auth.SignOutAsync(account);
            throw new InvalidOperationException(
                $"{info.UserPrincipalName} is a guest in {info.DisplayName}. Sign in with an admin account that belongs to that tenant.");
        }

        var other = state.Get(role == TenantRole.Source ? TenantRole.Destination : TenantRole.Source);
        if (other is not null && other.Info.TenantId == info.TenantId)
        {
            throw new InvalidOperationException(
                $"{info.DisplayName} is already connected as the {(role == TenantRole.Source ? "destination" : "source")}. Source and destination must be different tenants.");
        }

        // Ask for Exchange and Teams access now, so a first-time consent prompt appears while signing in rather than
        // halfway through discovering. A refusal isn't fatal here: Check services and discovery report it.
        progress?.Report("Checking Movewise's access to Exchange and Teams…");
        try
        {
            await auth.GetTokenAsync(account, Scopes.Exchange, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Error($"No Exchange Online access yet on the {role.ToString().ToLowerInvariant()} tenant.", ex);
        }
        await TryGetTokenAsync(account, Scopes.Teams, ct);

        // The source only ever gets read-only clients: its Graph has no write methods, and its PowerShell refuses
        // anything but Get cmdlets. The app registration's permissions alone don't stop writes (see Scopes).
        var readOnly = role == TenantRole.Source;
        var replaced = state.Get(role);
        var exchange = new PowerShellSession(PowerShellEndpoint.Exchange, readOnly, info,
            token => TryGetTokenAsync(account, Scopes.Exchange, token));
        var compliance = new PowerShellSession(PowerShellEndpoint.Compliance, readOnly, info,
            token => TryGetTokenAsync(account, Scopes.Compliance, token));
        var teams = new PowerShellSession(PowerShellEndpoint.Teams, readOnly, info,
            token => TryGetTokenAsync(account, Scopes.Teams, token),
            token => TryGetTokenAsync(account, scopes, token));
        state.Set(role, new TenantConnection
        {
            Role = role,
            Account = account,
            Info = info,
            Roles = RoleRequirements.Check(role, info.RoleTemplateIds),
            Clients = new TenantClients(readOnly ? new ReadOnlyGraph(graph) : graph, exchange, compliance, teams,
                readOnly ? new ReadOnlyGraph(defenderApi) : defenderApi),
            Sessions = [exchange, compliance, teams],
        });
        if (replaced is not null)
            await replaced.CloseAsync();
        DiagnosticLog.Info($"Connected the {role.ToString().ToLowerInvariant()} tenant {info.TenantId}. Roles sufficient: {RoleRequirements.Check(role, info.RoleTemplateIds).IsSufficient}.");
    });

    /// <summary>A token with its expiry, or null when the app registration has no permission for it (the caller has a fallback).</summary>
    async Task<SessionToken?> TryGetTokenAsync(IAccount account, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        try
        {
            var result = await auth.AcquireAsync(account, scopes, ct);
            return new SessionToken(result.AccessToken, result.ExpiresOn);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>True while the demo tenants are connected. The demo is all or nothing: it's never paired with a real tenant.</summary>
    public bool IsDemo => state.Source?.IsDemo == true || state.Destination?.IsDemo == true;

    /// <summary>Connects the two built-in demo tenants in place of real ones. Nothing leaves the app.</summary>
    public async Task ConnectDemoAsync()
    {
        ThrowIfWorking();
        await DisconnectAsync(TenantRole.Source);
        await DisconnectAsync(TenantRole.Destination);
        var (source, destination) = Demo.DemoTenants.Create();
        state.Set(TenantRole.Source, source);
        state.Set(TenantRole.Destination, destination);
        DiagnosticLog.Info("Connected the demo tenants.");
    }

    /// <summary>Demo runs are kept in a temporary folder, apart from real ones.</summary>
    string? RunsRoot => state.Destination?.IsDemo == true ? Demo.DemoTenants.RunsRoot : null;

    /// <summary>Signs out of one tenant. Signing out of a demo tenant leaves the demo: both demo tenants go.</summary>
    public async Task DisconnectAsync(TenantRole role)
    {
        ThrowIfWorking();
        var connection = state.Get(role);
        if (connection is null)
            return;
        if (connection.IsDemo)
        {
            var other = state.Get(role == TenantRole.Source ? TenantRole.Destination : TenantRole.Source);
            if (other is { IsDemo: true })
            {
                state.Set(other.Role, null);
                await other.CloseAsync();
            }
        }
        state.Set(role, null);
        await connection.CloseAsync();
        if (connection.Account is not null)
            await auth.SignOutAsync(connection.Account);
    }

    /// <summary>Signing in or out, or switching to the demo, would pull the tenants out from under a deployment.</summary>
    void ThrowIfWorking()
    {
        if (IsWorking)
            throw new InvalidOperationException("Wait for the deployment to finish (or stop it) first.");
    }

    /// <summary>
    /// Tries each way Movewise reaches this tenant (Graph, and the three PowerShell sessions), so a missing permission,
    /// module or license shows up now rather than halfway through discovery or deployment.
    /// </summary>
    public async Task<IReadOnlyList<ServiceCheck>> CheckServicesAsync(TenantRole role, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var connection = state.Get(role) ?? throw new InvalidOperationException("Sign in to this tenant first.");
        var clients = connection.Clients;
        var checks = new List<ServiceCheck>();

        async Task Check(string service, string usedFor, Func<Task<string>> probe)
        {
            progress?.Report($"Checking {service}…");
            try
            {
                checks.Add(new ServiceCheck(service, usedFor, true, await probe()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DiagnosticLog.Error($"Service check on the {role.ToString().ToLowerInvariant()} tenant: {service} failed.", ex);
                checks.Add(new ServiceCheck(service, usedFor, false, AuthService.DescribeRegistrationError(ex) ?? ex.Message));
            }
        }

        await Check("Microsoft Graph", "Entra ID and Intune", async () =>
        {
            await connection.Graph.GetObjectAsync("v1.0/organization?$select=id", ct);
            return "Connected.";
        });
        await Check("Exchange Online", "Defender for Office 365 and Exchange", async () =>
            Connected(await clients.PowerShellFor(ResourceRegistry.Get(ResourceRegistry.TransportRule)).InvokeAsync("Get-OrganizationConfig", null, ct)));
        await Check("Security & Compliance", "Purview", async () =>
        {
            var policies = await clients.PowerShellFor(ResourceRegistry.Get(ResourceRegistry.LabelPolicy)).InvokeAsync("Get-LabelPolicy", null, ct);
            return $"Connected. {policies.Count} label polic{(policies.Count == 1 ? "y" : "ies")}.";
        });
        await Check("Microsoft Teams", "Teams", async () =>
            Connected(await clients.PowerShellFor(ResourceRegistry.Get(ResourceRegistry.TeamsMeetingPolicy)).InvokeAsync("Get-CsTenant", null, ct)));

        DiagnosticLog.Info($"Service check on the {role.ToString().ToLowerInvariant()} tenant: " + string.Join(", ", checks.Select(c => $"{c.Service} {(c.Ok ? "ok" : "failed")}")));
        return checks;

        static string Connected(IReadOnlyList<System.Text.Json.Nodes.JsonObject> output) =>
            output.FirstOrDefault()?["DisplayName"]?.GetValue<string>() is { Length: > 0 } name ? $"Connected to {name}." : "Connected.";
    }

    public Task DiscoverAsync(IProgress<string> progress, CancellationToken ct = default) => Logged("Discovery", async () =>
    {
        var source = state.Source ?? throw new InvalidOperationException("Sign in to the source tenant first.");
        var types = Supported.SelectMany(ResourceRegistry.For);
        var result = await Exporter.ExportAsync(source.Clients, types, progress, ct);
        state.SetExported(result);
        progress.Report("Reading mail flow connectors and DKIM domains…");
        state.SetManualSetup(await Exporter.ReadManualSetupAsync(source.Clients, ct));
        DiagnosticLog.Info($"Discovered {result.Items.Count} policies." + string.Concat(result.Warnings.Select(w => $" Couldn't read {w.Type.Id}: {w.Message}")));
    });

    /// <summary>Matches everything the selected policies point at, keeping decisions the admin already made.</summary>
    public Task BuildMappingAsync(IProgress<string> progress, CancellationToken ct = default) => Logged("Matching", async () =>
    {
        var source = state.Source ?? throw new InvalidOperationException("Sign in to the source tenant first.");
        var destination = state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");
        var selected = state.Exported.Where(e => state.Selected.Contains(MigrationState.Key(e))).ToList();
        var plan = await Matcher.BuildAsync(source.Graph, destination.Clients, selected, state.Exported, state.Mapping, progress, ct);
        state.SetMapping(plan);
        DiagnosticLog.Info($"Matched {plan.Items.Count} objects, {plan.UnresolvedCount} without a match.");
    });

    /// <summary>Dry run against the destination. Matches again first if the selection changed since mapping.</summary>
    public Task RunPreflightAsync(IProgress<string> progress, CancellationToken ct = default) => Logged("Pre-flight", async () =>
    {
        var destination = state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");
        if (!state.MappingIsCurrent)
            await BuildMappingAsync(progress, ct);

        var selected = state.Exported.Where(e => state.Selected.Contains(MigrationState.Key(e))).ToList();
        var report = await PreflightCheck.RunAsync(destination.Clients, destination.Info, selected, state.Mapping!, state.PreflightChoices, progress, ct);
        state.SetPreflight(report);
        DiagnosticLog.Info($"Pre-flight: {report.CreateCount} to create, {report.SkipCount} skipped, {report.BlockedCount} blocked. Findings: " + string.Join("; ", report.Findings.Select(f => $"{f.Severity} {f.Title}")));
    });

    public Task<IReadOnlyList<ObjectRef>> SearchDestinationAsync(string targetType, string text, CancellationToken ct = default)
    {
        var destination = state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");
        return DestinationSearch.SearchAsync(destination.Clients, targetType, text, ct);
    }

    /// <summary>
    /// Marks the object to be created in the destination. Objects Movewise exports itself (locations, strengths,
    /// filters, scope tags) are added to the selection, so they're migrated with the policies.
    /// </summary>
    public void CreateInDestination(Mapping mapping)
    {
        mapping.CreateInDestination();
        if (mapping.TargetType != ResourceRegistry.Group)
            state.Selected.Add(mapping.Key);
        state.NotifyChanged();
    }

    // ---------- Deploy ----------

    CancellationTokenSource? _running;

    /// <summary>True while a deployment or rollback is writing to the destination.</summary>
    public bool IsBusy => _running is not null;

    /// <summary>True while a deployment is being prepared (pre-flight again, the copy of the destination).</summary>
    public bool IsPreparing { get; private set; }

    /// <summary>True while preparing, deploying or rolling back: the tenants mustn't change meanwhile.</summary>
    public bool IsWorking => IsBusy || IsPreparing;

    /// <summary>What the running deployment is doing, for the screen.</summary>
    public string? BusyText { get; private set; }

    /// <summary>
    /// Runs pre-flight once more (the destination may have changed), builds the list of objects to create,
    /// optionally saves a copy of the destination's current policies, and saves the run. Nothing is created yet.
    /// Kept here rather than on the screen, so leaving the Deploy screen and coming back can't start a second one.
    /// </summary>
    public async Task PrepareDeploymentAsync(bool snapshot, IProgress<string> progress, CancellationToken ct = default)
    {
        if (IsWorking)
            throw new InvalidOperationException("A deployment is already being prepared or running.");
        IsPreparing = true;
        BusyText = "Running pre-flight once more…";
        state.NotifyChanged();
        var shown = new Progress<string>(text =>
        {
            BusyText = text;
            progress.Report(text);
            state.NotifyChanged();
        });
        try
        {
            await PrepareAsync(snapshot, shown, ct);
        }
        finally
        {
            IsPreparing = false;
            BusyText = null;
            state.NotifyChanged();
        }
    }

    Task PrepareAsync(bool snapshot, IProgress<string> progress, CancellationToken ct) => Logged("Preparing the deployment", async () =>
    {
        var source = state.Source ?? throw new InvalidOperationException("Sign in to the source tenant first.");
        var destination = state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");

        await RunPreflightAsync(progress, ct);
        var report = state.Preflight!;
        if (report.HasBlockers && !report.BlockedSkipped)
            throw new InvalidOperationException("Pre-flight found new blockers. Fix them on the Pre-flight screen, then deploy.");
        if (report.CreateCount == 0)
            throw new InvalidOperationException("There's nothing to create: every selected policy is skipped or already in the destination.");

        var run = DeployPlanner.Plan(report, state.Mapping!, source.Info, destination.Info);
        run.Folder = RunStore.NewRunFolder(source.Info.InitialDomain, destination.Info.InitialDomain, run.RunId, RunsRoot);

        if (snapshot)
        {
            progress.Report($"Saving a copy of {destination.Info.DisplayName}'s current policies…");
            var services = report.Policies.Select(p => p.Source.Type.Service).Distinct();
            var before = await Exporter.ExportAsync(destination.Clients, services.SelectMany(ResourceRegistry.For), progress, ct);
            await ProjectStore.SaveExportAsync(run.Folder, before.Items, ct, RunStore.SnapshotFolder);
        }

        await RunStore.SaveAsync(run, ct);
        state.SetRun(run);
        DiagnosticLog.Info($"Prepared run {run.RunId}: {run.Steps.Count} objects.");
    });

    /// <summary>Creates everything in the current run that isn't finished. Also used to retry after failures or a stop.</summary>
    public Task DeployAsync() => WriteAsync("Deploying", (graph, run, save, changed, ct) => Deployer.RunAsync(graph, run, save, changed, ct));

    /// <summary>Deletes everything the current run created.</summary>
    public Task RollbackAsync() => WriteAsync("Rolling back", (graph, run, save, changed, ct) => Deployer.RollbackAsync(graph, run, save, changed, ct));

    /// <summary>Stops once the object in progress is finished. The run can be resumed or rolled back later.</summary>
    public void Stop() => _running?.Cancel();

    async Task WriteAsync(string verb, Func<TenantClients, DeployRun, Func<Task>, Action<DeployStep>, CancellationToken, Task> work)
    {
        var destination = state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");
        var run = state.Run ?? throw new InvalidOperationException("There's no deployment to run.");
        if (run.DestinationTenantId != destination.Info.TenantId)
            throw new InvalidOperationException($"This run was for {run.DestinationName}, but you're signed in to {destination.Info.DisplayName}.");
        if (IsBusy)
            throw new InvalidOperationException("A deployment is already running.");
        if (IsPreparing)
            throw new InvalidOperationException("A deployment is being prepared.");

        using var cts = new CancellationTokenSource();
        _running = cts;
        using var awake = KeepAwake.Start();
        try
        {
            var total = run.Steps.Count;
            void Changed(DeployStep step)
            {
                if (step.Status is not (StepStatus.Creating or StepStatus.Created))
                    DiagnosticLog.Info($"Run {run.RunId}: step {run.Steps.IndexOf(step) + 1} of {total} ({step.TargetType}) {step.Status}. {step.Message}");
                BusyText = $"{verb}: {step.DisplayName} ({run.Steps.IndexOf(step) + 1} of {total})";
                state.NotifyChanged();
            }

            BusyText = $"{verb}…";
            state.NotifyChanged();
            // Saving isn't cancelled with the run: what's on disk must always match the destination.
            DiagnosticLog.Info($"{verb} run {run.RunId} started.");
            await Logged($"{verb} run {run.RunId}", () => work(destination.Clients, run, () => RunStore.SaveAsync(run), Changed, cts.Token));
            DiagnosticLog.Info($"{verb} run {run.RunId} finished: {run.DoneCount} done, {run.FailedCount} failed, {run.SkippedCount} skipped, {run.RemainingCount} unfinished.");
        }
        finally
        {
            _running = null;
            BusyText = null;
            state.NotifyChanged();
        }
    }

    /// <summary>Saved runs into the connected destination, newest first.</summary>
    public IReadOnlyList<RunSummary> PreviousRuns() =>
        state.Destination is { } destination ? RunStore.List(destination.Info.TenantId, RunsRoot) : [];

    public void OpenRun(string path)
    {
        ThrowIfWorking();
        var destination = state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");
        var run = RunStore.Load(path);
        if (run.DestinationTenantId != destination.Info.TenantId)
            throw new InvalidOperationException($"This run was for {run.DestinationName}, not {destination.Info.DisplayName}.");
        state.SetRun(run);
    }

    /// <summary>Puts the current run away so a new one can be prepared. It stays on disk.</summary>
    public void CloseRun()
    {
        if (IsWorking)
            throw new InvalidOperationException("Stop the deployment first.");
        state.SetRun(null);
    }

    /// <summary>Writes failures (and stops) to the support log, then lets them through to the screen.</summary>
    static async Task Logged(string what, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Info($"{what}: stopped.");
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error($"{what} failed.", ex);
            throw;
        }
    }

    public async Task<string> SaveExportAsync(CancellationToken ct = default)
    {
        var source = state.Source ?? throw new InvalidOperationException("Sign in to the source tenant first.");
        var destination = state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");
        var folder = ProjectStore.NewProjectFolder(source.Info.InitialDomain, destination.Info.InitialDomain);
        var selected = state.Exported.Where(e => state.Selected.Contains(MigrationState.Key(e)));
        await ProjectStore.SaveExportAsync(folder, selected, ct);
        state.SavedExportFolder = folder;
        state.NotifyChanged();
        return folder;
    }
}

/// <summary>One way Movewise reaches a tenant, and whether it works.</summary>
/// <param name="UsedFor">The services that depend on it.</param>
public sealed record ServiceCheck(string Service, string UsedFor, bool Ok, string Detail);
