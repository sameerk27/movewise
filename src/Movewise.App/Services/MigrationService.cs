using Microsoft.Identity.Client;
using Movewise.App.State;
using Movewise.Core.Access;
using Movewise.Core.Baselines;
using Movewise.Core.Compare;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.M365;
using Movewise.M365.Auth;
using Movewise.M365.Graph;
using Movewise.M365.PowerShell;
using Movewise.Core.Tenants;

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
        Movewise.Core.Diagnostics.Redactor.Remember(info.DefaultDomain, info.InitialDomain);

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

        // Those token requests can take a while too: check again, so the demo or a deployment that started meanwhile
        // is never paired with this tenant.
        if (IsWorking || IsDemo)
        {
            await auth.SignOutAsync(account);
            throw new InvalidOperationException(IsDemo ? "Leave the demo first, then sign in to your tenants." : "Wait for the deployment to finish, then sign in again.");
        }

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
    internal string? RunsRoot => state.Destination?.IsDemo == true ? Demo.DemoTenants.RunsRoot : null;

    /// <summary>Signs out of one tenant. Signing out of a demo tenant leaves the demo: both demo tenants go.</summary>
    public async Task DisconnectAsync(TenantRole role)
    {
        ThrowIfWorking();
        var connection = state.Get(role);
        if (connection is null)
            return;
        // Leaving the demo leaves both sample tenants; dropping a demo baseline keeps the destination.
        if (connection.IsDemo && !connection.IsBaseline)
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
        // The services ticked when the read starts are the ones it reads, even if they're changed while it runs.
        var chosen = state.Services.ToHashSet();
        if (source.Baseline is { } saved)
        {
            // A baseline is already read: its policies come from the file, for the services chosen.
            state.SetExported(new ExportResult(saved.Items, []), chosen);
            state.SetManualSetup(new ManualSetupResult([], []));
            return;
        }
        var types = Supported.Where(chosen.Contains).SelectMany(ResourceRegistry.For);
        var result = await Exporter.ExportAsync(source.Clients, types, progress, ct);
        EnsureSameTenants(source, null);
        state.SetExported(result, chosen);
        progress.Report("Reading mail flow connectors and DKIM domains…");
        // Connectors and DKIM come from Exchange Online, so they're read only when Exchange is chosen.
        var manual = chosen.Contains(M365Service.Exchange)
            ? await Exporter.ReadManualSetupAsync(source.Clients, ct)
            : new ManualSetupResult([], []);
        EnsureSameTenants(source, null);
        state.SetManualSetup(manual);
        DiagnosticLog.Info($"Discovered {result.Items.Count} policies." + string.Concat(result.Warnings.Select(w => $" Couldn't read {w.Type.Id}: {w.Message}")));
    });

    // ---------- Compare (only reads; nothing changes in either tenant) ----------

    // Demo snapshots stay out of the real ones, like demo runs.
    string? SnapshotsRoot => state.Source?.IsDemo == true || state.Destination?.IsDemo == true
        ? System.IO.Path.Combine(Demo.DemoTenants.RunsRoot, "Snapshots")
        : null;

    static IEnumerable<ResourceType> TypesOf(IReadOnlyCollection<M365Service> services) =>
        Supported.Where(services.Contains).SelectMany(ResourceRegistry.For);

    /// <summary>Compares the source with the destination, in the destination's terms.</summary>
    public Task CompareTenantsAsync(IReadOnlyCollection<M365Service> services, IProgress<string> progress, CancellationToken ct = default) =>
        Logged("Comparing tenants", async () =>
        {
            RefuseWhileWorking();
            var source = state.Source ?? throw new InvalidOperationException("Sign in to the source tenant first.");
            var destination = state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");
            var report = await TenantComparer.CompareTenantsAsync(source.Clients, source.Info.DisplayName, destination.Clients, destination.Info.DisplayName,
                TypesOf(services), progress, ct, source.Baseline?.Items);
            EnsureSameTenants(source, destination);
            state.SetComparison(report);
            DiagnosticLog.Info($"Compared tenants: {report.Count(Difference.OnlyInFirst)} only in the source, {report.Count(Difference.OnlyInSecond)} only in the destination, " +
                $"{report.Count(Difference.Different)} different, {report.Count(Difference.Same)} the same.");
        });

    /// <summary>Saves what one tenant has now, to compare it with later.</summary>
    public async Task<Snapshot> SaveSnapshotAsync(TenantRole role, IReadOnlyCollection<M365Service> services, IProgress<string> progress, CancellationToken ct = default)
    {
        Snapshot? saved = null;
        await Logged("Saving a snapshot", async () =>
        {
            RefuseWhileWorking();
            var tenant = state.Get(role) ?? throw new InvalidOperationException($"Sign in to the {role.ToString().ToLowerInvariant()} tenant first.");
            // A saved baseline holds policies from a file, not what the tenant has now: a snapshot of it would be wrong.
            if (tenant.IsBaseline)
                throw new InvalidOperationException("A saved baseline isn't a tenant, so there's nothing to snapshot in it. Sign in to the tenant to take a snapshot.");
            progress.Report($"Reading {tenant.Info.DisplayName}…");
            var types = TypesOf(services).ToList();
            var result = await Exporter.ExportAsync(tenant.Clients, types, progress, ct);
            // Types that couldn't be read are left out, so they don't look emptied when compared later.
            var read = types.Where(t => result.Warnings.All(w => w.Type != t)).ToList();
            saved = await SnapshotStore.SaveAsync(new Snapshot(tenant.Info.TenantId, tenant.Info.DisplayName, DateTimeOffset.Now, read, result.Items), SnapshotsRoot, ct);
            DiagnosticLog.Info($"Saved a snapshot of {result.Items.Count} items.");
        });
        return saved!;
    }

    /// <summary>
    /// Every snapshot, newest first. A snapshot of a tenant that isn't signed in is listed too, marked as such: it's
    /// compared once that tenant is signed in again.
    /// </summary>
    public async Task<IReadOnlyList<Snapshot>> ListSnapshotsAsync(CancellationToken ct = default) =>
        await SnapshotStore.ListAsync(SnapshotsRoot, ct);

    /// <summary>Compares a tenant now with an earlier snapshot of it.</summary>
    public Task CompareWithSnapshotAsync(Snapshot snapshot, IProgress<string> progress, CancellationToken ct = default) =>
        Logged("Comparing with a snapshot", async () =>
        {
            RefuseWhileWorking();
            // A snapshot is only ever compared with the tenant it was taken of. A saved baseline has the same tenant ID,
            // but its policies come from a file, so it's never the one compared.
            var tenant = new[] { state.Source, state.Destination }.FirstOrDefault(t => t is { IsBaseline: false } && string.Equals(t.Info.TenantId, snapshot.TenantId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"This snapshot is of {snapshot.TenantName}. Sign in to it to compare.");
            var report = await TenantComparer.CompareWithSnapshotAsync(snapshot, tenant.Clients, tenant.Info.DisplayName, progress, ct);
            // The tenant may have been signed out while it was read; its drift and restore mustn't show for another.
            if (!ReferenceEquals(state.Source, tenant) && !ReferenceEquals(state.Destination, tenant))
                throw new InvalidOperationException("The tenant was signed out while this was running, so its results were discarded. Start again.");
            state.SetComparison(report);
            DiagnosticLog.Info($"Compared with a snapshot: {report.Count(Difference.OnlyInFirst)} removed, {report.Count(Difference.OnlyInSecond)} added, " +
                $"{report.Count(Difference.Different)} changed, {report.Count(Difference.Same)} the same.");
        });

    // ---------- Baselines (save a template once, apply it to other tenants) ----------

    /// <summary>
    /// Saves the policies selected on Discover as a baseline, with what they point at in the source. Only reads.
    /// Nothing in the source is needed again once it's saved.
    /// </summary>
    public async Task<Baseline> SaveBaselineAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        Baseline? saved = null;
        await Logged("Saving a baseline", async () =>
        {
            RefuseWhileWorking();
            var source = state.Source ?? throw new InvalidOperationException("Sign in to the source tenant first.");
            if (source.IsBaseline)
                throw new InvalidOperationException("This is already a saved baseline.");
            var selected = state.Exported.Where(e => state.Selected.Contains(MigrationState.Key(e))).ToList();
            if (selected.Count == 0)
                throw new InvalidOperationException("Choose at least one policy to save in the baseline.");
            var name = $"{source.Info.DisplayName} baseline";
            var baseline = await BaselineStore.CaptureAsync(name, source.Clients, source.Info, source.IsDemo, selected, state.Exported, progress, ct);
            EnsureSameTenants(source, null);
            saved = await BaselineStore.SaveAsync(baseline, BaselinesRoot, ct);
            DiagnosticLog.Info($"Saved a baseline of {selected.Count} policies.");
        });
        return saved!;
    }

    /// <summary>Baselines saved so far, newest first.</summary>
    public async Task<IReadOnlyList<Baseline>> ListBaselinesAsync(CancellationToken ct = default) =>
        await BaselineStore.ListAsync(BaselinesRoot, ct);

    /// <summary>
    /// Uses a saved baseline as the source instead of a signed-in tenant. The destination must be signed in first, as
    /// for any source. Nothing is read from Microsoft 365 for the source.
    /// </summary>
    public Task UseBaselineAsync(Baseline baseline) => Logged("Using a baseline", () =>
    {
        ThrowIfWorking();
        if (IsDemo)
            throw new InvalidOperationException("Leave the demo first, then use a baseline.");
        if (state.Destination is null)
            throw new InvalidOperationException("Sign in to the destination tenant first.");
        var info = new TenantInfo(baseline.TenantId, baseline.TenantName, baseline.InitialDomain, baseline.DefaultDomain,
            $"Saved baseline (from {baseline.TenantName})", "Saved baseline", false, [], [], []);
        state.Set(TenantRole.Source, new TenantConnection
        {
            Role = TenantRole.Source,
            Info = info,
            Roles = new RoleCheck(true, [], "Read from a saved baseline. Nothing is read from the source tenant."),
            Clients = new TenantClients(new OfflineGraph(baseline.Responses)),
            Baseline = baseline,
            IsDemo = baseline.IsDemo,
        });
        DiagnosticLog.Info($"Using the baseline \"{baseline.Name}\" as the source.");
        return Task.CompletedTask;
    });

    string BaselinesRoot => state.Destination?.IsDemo == true || state.Source?.IsDemo == true
        ? System.IO.Path.Combine(Demo.DemoTenants.RunsRoot, "Baselines")
        : BaselineStore.DefaultRoot;

    // ---------- Restore from a snapshot (writes, but only into the destination) ----------

    /// <summary>
    /// Checks putting back what changed since a snapshot: removed policies recreated, changed settings set back. Runs
    /// pre-flight against the tenant, which must be signed in as the destination: Movewise never writes to the source.
    /// </summary>
    public Task CheckRestoreAsync(IReadOnlyCollection<ComparedItem> items, bool keepOriginalState, bool leaveOutBlocked,
        IProgress<string> progress, CancellationToken ct = default) =>
        Logged("Checking a restore", async () =>
        {
            RefuseWhileWorking();
            var snapshot = state.Comparison?.Snapshot ?? throw new InvalidOperationException("Compare the tenant with a snapshot first.");
            var destination = RestoreTarget(snapshot);
            var restoring = items.Where(i => i.CanRestore).Select(i => i.FirstSource!).ToList();
            if (restoring.Count == 0)
                throw new InvalidOperationException("Choose at least one policy or setting to put back.");

            // The snapshot is of this tenant, so what its policies point at is looked up in the tenant itself.
            progress.Report("Matching groups, users and other objects…");
            var plan = await Matcher.BuildAsync(new ReadOnlyGraph(destination.Clients.Graph), destination.Clients, restoring, snapshot.Items, null, progress, ct);
            progress.Report("Checking the restore…");
            var report = await PreflightCheck.RunAsync(destination.Clients, destination.Info, restoring, plan,
                // A policy changed since is the same policy, perhaps renamed: it's updated by its ID rather than created again.
                new PreflightChoices { KeepOriginalState = keepOriginalState, SkipBlocked = leaveOutBlocked, UpdateById = true }, progress, ct);
            state.SetRestore(new RestorePlan(snapshot, report, plan));
            DiagnosticLog.Info($"Checked a restore of {restoring.Count} items: {report.CreateCount} to create or put back, {report.BlockedCount} blocked.");
        });

    /// <summary>Saves the checked restore as a run and opens it on the Deploy screen, where it's started, and can be rolled back.</summary>
    public async Task PrepareRestoreAsync(CancellationToken ct = default)
    {
        RefuseWhileWorking();
        if (IsUpdating)
            throw new InvalidOperationException(UpdatingMessage);
        var restore = state.Restore ?? throw new InvalidOperationException("Check the restore first.");
        var destination = RestoreTarget(restore.Snapshot);
        if (!restore.Report.CanDeploy)
            throw new InvalidOperationException("There's nothing to put back, or pre-flight found blockers. Fix them, or leave the blocked ones out.");

        await Logged("Preparing a restore", async () =>
        {
            // The run reads "Snapshot of … → tenant", so it's clear on the Deploy screen and in the run list what it was.
            var from = destination.Info with { DisplayName = $"Snapshot of {restore.Snapshot.Taken.LocalDateTime:d MMM yyyy HH:mm}" };
            var run = DeployPlanner.Plan(restore.Report, restore.Mapping, from, destination.Info);
            run.Folder = RunStore.NewRunFolder("snapshot", destination.Info.InitialDomain, run.RunId, RunsRoot);
            await RunStore.SaveAsync(run, ct);
            state.SetRestore(null);
            state.SetRun(run);
            DiagnosticLog.Info($"Prepared restore run {run.RunId}: {run.Steps.Count} objects.");
        });
    }

    /// <summary>The tenant a snapshot is restored into: its own, signed in as the destination.</summary>
    TenantConnection RestoreTarget(Snapshot snapshot)
    {
        var destination = state.Destination;
        if (destination is null || !string.Equals(destination.Info.TenantId, snapshot.TenantId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"To restore {snapshot.TenantName}, sign in to it as the destination. Movewise never changes the source.");
        if (state.Run is { } run && run.RemainingCount > 0 && run.RolledBack is null)
            throw new InvalidOperationException("Another run into this tenant isn't finished. Finish it or close it on the Deploy screen first.");
        return destination;
    }

    void RefuseWhileWorking()
    {
        if (IsWorking)
            throw new InvalidOperationException("A deployment is being prepared or running. Compare once it has finished.");
    }

    /// <summary>Matches everything the selected policies point at, keeping decisions the admin already made.</summary>
    public Task BuildMappingAsync(IProgress<string> progress, CancellationToken ct = default) => Logged("Matching", async () =>
    {
        var source = state.Source ?? throw new InvalidOperationException("Sign in to the source tenant first.");
        var destination = state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");
        var selected = state.Exported.Where(e => state.Selected.Contains(MigrationState.Key(e))).ToList();
        var plan = await Matcher.BuildAsync(source.Graph, destination.Clients, selected, state.Exported, state.Mapping, progress, ct,
            MigratedAppIds(source, destination));
        EnsureSameTenants(source, destination);
        state.SetMapping(plan);
        DiagnosticLog.Info($"Matched {plan.Items.Count} objects, {plan.UnresolvedCount} without a match.");
    });

    /// <summary>Dry run against the destination. Matches again first if the selection changed since mapping.</summary>
    public Task RunPreflightAsync(IProgress<string> progress, CancellationToken ct = default) => Logged("Pre-flight", async () =>
    {
        var destination = state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");
        state.SetPreflight(null);
        if (!state.MappingIsCurrent)
            await BuildMappingAsync(progress, ct);

        var selected = state.Exported.Where(e => state.Selected.Contains(MigrationState.Key(e))).ToList();
        var decisions = Decisions(state.Mapping);
        var report = await PreflightCheck.RunAsync(destination.Clients, destination.Info, selected, state.Mapping!, state.PreflightChoices, progress, ct);
        EnsureSameTenants(null, destination);
        // A match changed on the Map screen meanwhile wasn't checked, so this report can't stand for what would be deployed.
        if (Decisions(state.Mapping) != decisions || !state.MappingIsCurrent)
            throw new InvalidOperationException("The mapping or the selection changed while the dry run was running. Run it again.");
        state.SetPreflight(report);
        DiagnosticLog.Info($"Pre-flight: {report.CreateCount} to create, {report.SkipCount} skipped, {report.BlockedCount} blocked. Findings: " + string.Join("; ", report.Findings.Select(f => $"{f.Severity} {f.Title}")));
    });

    /// <summary>
    /// The app registrations Movewise migrated from this source into this destination (source app ID → destination
    /// app ID), so policies can be matched to their new app IDs. A map that can't be read only costs that matching.
    /// </summary>
    internal IReadOnlyDictionary<string, string> MigratedAppIds(TenantConnection source, TenantConnection destination)
    {
        try
        {
            return AppIdMap.Load(destination.Info.TenantId, RunsRoot).For(source.Info.TenantId);
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException or System.IO.InvalidDataException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Couldn't read the app ID map.", ex);
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Discovery and matching take a while; a tenant signed out and another signed in meanwhile gets none of their results.</summary>
    void EnsureSameTenants(TenantConnection? source, TenantConnection? destination)
    {
        if ((source is not null && !ReferenceEquals(state.Source, source)) || (destination is not null && !ReferenceEquals(state.Destination, destination)))
            throw new InvalidOperationException("A tenant was signed out or changed while this was running, so its results were discarded. Start again.");
    }

    static string Decisions(MappingPlan? plan) =>
        plan is null ? "" : string.Join("|", plan.Items.Select(m => $"{m.Key}={m.Kind}:{m.Destination?.Id}"));

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

    /// <summary>
    /// Takes back what <see cref="CreateInDestination"/> added to the migration, before the mapping gets another
    /// decision. Otherwise the object would still be created although the admin no longer wants it.
    /// </summary>
    public void UndoCreate(Mapping mapping)
    {
        if (mapping.Kind == MatchKind.CreateInDestination && mapping.TargetType != ResourceRegistry.Group)
            state.Selected.Remove(mapping.Key);
    }

    // ---------- Deploy ----------

    CancellationTokenSource? _running;

    /// <summary>True while a deployment or rollback is writing to the destination.</summary>
    public bool IsBusy => _running is not null;

    /// <summary>True while a deployment is being prepared (pre-flight again, the copy of the destination).</summary>
    public bool IsPreparing { get; private set; }

    /// <summary>True while preparing, deploying or rolling back, policies or apps: the tenants mustn't change meanwhile.</summary>
    public bool IsWorking => IsBusy || IsPreparing || AppsWorking;

    /// <summary>Set by <see cref="ApplicationsService"/> while it prepares, deploys or rolls back apps.</summary>
    public bool AppsWorking { get; internal set; }

    /// <summary>Set by <see cref="UpdateService"/> while an update downloads: Movewise restarts when it's done, so nothing may start.</summary>
    public bool IsUpdating { get; set; }

    internal const string UpdatingMessage = "Movewise is downloading an update and will restart when it's done. Start the deployment after the restart.";

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
        if (IsUpdating)
            throw new InvalidOperationException(UpdatingMessage);
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
        if (AppsWorking)
            throw new InvalidOperationException("Applications are being deployed. Wait for that to finish first.");
        if (IsUpdating)
            throw new InvalidOperationException(UpdatingMessage);

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
    internal static async Task Logged(string what, Func<Task> work)
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

    public async Task<string> SaveAsBuiltAsync(CancellationToken ct = default)
    {
        var source = state.Source ?? throw new InvalidOperationException("Sign in to the source tenant first.");
        var destination = state.Destination ?? throw new InvalidOperationException("Sign in to the destination tenant first.");
        var folder = ProjectStore.NewProjectFolder(source.Info.InitialDomain, destination.Info.InitialDomain);
        var selected = state.Exported.Where(e => state.Selected.Contains(MigrationState.Key(e)));
        
        System.IO.Directory.CreateDirectory(folder);
        var html = Movewise.Core.Reports.AsBuiltReport.GenerateHtml(source.Info, selected);
        var path = System.IO.Path.Combine(folder, "AsBuiltReport.html");
        await System.IO.File.WriteAllTextAsync(path, html, ct);
        
        state.SavedExportFolder = folder;
        state.NotifyChanged();
        return folder;
    }
}

/// <summary>One way Movewise reaches a tenant, and whether it works.</summary>
/// <param name="UsedFor">The services that depend on it.</param>
public sealed record ServiceCheck(string Service, string UsedFor, bool Ok, string Detail);
