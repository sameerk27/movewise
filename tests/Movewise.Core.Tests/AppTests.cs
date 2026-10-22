using System.Text.Json.Nodes;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

public class AppTests
{
    static readonly ResourceType EnterpriseApp = ResourceRegistry.Get(ResourceRegistry.EnterpriseApp);

    static readonly TenantInfo Source = new("src-tenant", "Contoso", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false, [], [], []);
    static readonly TenantInfo Destination = new("dst-tenant", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false,
        [Access.DirectoryRoles.CloudApplicationAdministrator], [], []);

    const string Anna = "user-anna";
    const string Sales = "group-sales";
    const string Role = "00000000-0000-0000-0000-000000000000";

    /// <summary>
    /// Contoso has Zoom (another organization's app, with a user, a group and an app assigned, an owner, permissions
    /// and a client secret), Miro (no access set up), and three apps that aren't migrated: Microsoft Graph, a managed
    /// identity and Contoso's own app registration.
    /// </summary>
    static FakeGraph SourceGraph() => new FakeGraph()
        .Collection(EnterpriseApp.ListPath,
            """{ "id": "sp-zoom", "appId": "app-zoom", "displayName": "Zoom", "appOwnerOrganizationId": "zoom-tenant", "servicePrincipalType": "Application", "appRoleAssignmentRequired": true, "notes": "Video calls", "tags": ["WindowsAzureActiveDirectoryIntegratedApp"], "passwordCredentials": [ { "keyId": "k1" } ], "keyCredentials": [] }""",
            """{ "id": "sp-miro", "appId": "app-miro", "displayName": "Miro", "appOwnerOrganizationId": "miro-tenant", "servicePrincipalType": "Application", "appRoleAssignmentRequired": false }""",
            """{ "id": "sp-graph", "appId": "00000003-0000-0000-c000-000000000000", "displayName": "Microsoft Graph", "appOwnerOrganizationId": "f8cdef31-a31e-4b4a-93e4-5f571e91255a", "servicePrincipalType": "Application" }""",
            """{ "id": "sp-mi", "appId": "app-mi", "displayName": "Backup job", "servicePrincipalType": "ManagedIdentity" }""",
            """{ "id": "sp-own", "appId": "app-own", "displayName": "Contoso HR Portal", "appOwnerOrganizationId": "src-tenant", "servicePrincipalType": "Application" }""")
        .Collection("v1.0/servicePrincipals/sp-zoom/appRoleAssignedTo",
            $$"""{ "id": "a1", "principalType": "User", "principalId": "{{Anna}}", "appRoleId": "{{Role}}" }""",
            $$"""{ "id": "a2", "principalType": "Group", "principalId": "{{Sales}}", "appRoleId": "{{Role}}" }""",
            $$"""{ "id": "a3", "principalType": "ServicePrincipal", "principalId": "sp-other", "appRoleId": "{{Role}}" }""")
        .Collection("v1.0/servicePrincipals/sp-zoom/owners?$select=id", $$"""{ "@odata.type": "#microsoft.graph.user", "id": "{{Anna}}" }""")
        .Collection($"v1.0/oauth2PermissionGrants?$filter={Uri.EscapeDataString("clientId eq 'sp-zoom'")}",
            """{ "clientId": "sp-zoom", "resourceId": "sp-graph", "consentType": "AllPrincipals", "scope": "User.Read openid" }""")
        .Collection("v1.0/servicePrincipals/sp-zoom/appRoleAssignments",
            """{ "resourceId": "sp-graph", "resourceDisplayName": "Microsoft Graph", "appRoleId": "role-users" }""")
        .Object(GraphQuery.Item("v1.0/servicePrincipals", "sp-graph", "id,displayName,appRoles"),
            """{ "id": "sp-graph", "displayName": "Microsoft Graph", "appRoles": [ { "id": "role-users", "value": "User.Read.All" } ] }""");

    static async Task<IReadOnlyList<ExportedResource>> ExportAsync() => (await Exporter.ExportAppsAsync(SourceGraph(), Source.TenantId)).Items;

    /// <summary>Anna and Sales have matches in the destination, unless Sales is left unresolved.</summary>
    static MappingPlan Plan(bool salesMatched = true)
    {
        var anna = new Mapping.Mapping { TargetType = ResourceRegistry.User, Source = new ObjectRef(Anna, "Anna"), UsedBy = ["Zoom"] };
        anna.MapTo(new ObjectRef("dst-anna", "Anna"));
        var sales = new Mapping.Mapping { TargetType = ResourceRegistry.Group, Source = new ObjectRef(Sales, "Sales"), UsedBy = ["Zoom"] };
        if (salesMatched)
            sales.MapTo(new ObjectRef("dst-sales", "Sales"));
        return new MappingPlan([anna, sales]);
    }

    /// <summary>A destination with none of the apps, or with Zoom already there, Anna assigned to it.</summary>
    static FakeGraph DestinationGraph(bool zoomThere = false)
    {
        var graph = new FakeGraph().Answer(path =>
        {
            if (path.StartsWith("v1.0/servicePrincipals?", StringComparison.Ordinal))
            {
                var found = zoomThere && path.Contains("app-zoom", StringComparison.Ordinal)
                    ? """[ { "id": "dst-zoom", "appId": "app-zoom", "displayName": "Zoom", "appRoleAssignmentRequired": false, "notes": "Video calls", "tags": ["WindowsAzureActiveDirectoryIntegratedApp"] } ]"""
                    : "[]";
                return JsonNode.Parse($$"""{ "value": {{found}} }""")!.AsObject();
            }
            if (path.StartsWith("v1.0/servicePrincipals/dst-zoom?", StringComparison.Ordinal))
                return JsonNode.Parse("""{ "id": "dst-zoom", "appRoleAssignmentRequired": false }""")!.AsObject();
            return null;
        });
        if (zoomThere)
            graph.Collection("v1.0/servicePrincipals/dst-zoom/appRoleAssignedTo", $$"""{ "id": "d1", "principalType": "User", "principalId": "dst-anna", "appRoleId": "{{Role}}" }""");
        return graph;
    }

    static Task<AppPreflightReport> PreflightAsync(FakeGraph destination, IReadOnlyList<ExportedResource> apps, MappingPlan plan, ExistingAppChoice existing = ExistingAppChoice.Skip) =>
        AppPreflight.RunAsync(new TenantClients(destination), Destination, apps, plan, new AppPreflightChoices { ExistingApps = existing });

    [Fact]
    public async Task Export_reads_other_organizations_apps_with_their_access_and_leaves_out_the_rest()
    {
        var apps = await ExportAsync();

        Assert.Equal(new[] { "Miro", "Zoom" }, apps.Select(a => a.DisplayName));
        var zoom = apps.Single(a => a.DisplayName == "Zoom");
        Assert.Equal(Anna, zoom.Settings["assignedUsers"]![0]!["principalId"]!.GetValue<string>());
        Assert.Equal(Sales, zoom.Settings["assignedGroups"]![0]!["principalId"]!.GetValue<string>());
        Assert.Equal(Anna, zoom.Settings["owners"]![0]!.GetValue<string>());
        Assert.Equal(
            new[] { "Microsoft Graph: openid, User.Read (delegated)", "Microsoft Graph: User.Read.All (application)" },
            AppPreflight.Strings(zoom.Settings["apiPermissions"]));
        var notMigrated = AppPreflight.Strings(zoom.Settings["notMigrated"]);
        Assert.Contains(notMigrated, n => n.StartsWith("Assigned to 1 other app", StringComparison.Ordinal));
        Assert.Contains(notMigrated, n => n.StartsWith("Client secrets (1)", StringComparison.Ordinal));
        Assert.Contains(zoom.Dependencies, d => d.TargetType == ResourceRegistry.User && d.Value == Anna);
        Assert.Contains(zoom.Dependencies, d => d.TargetType == ResourceRegistry.Group && d.Value == Sales);
        Assert.Null(zoom.Settings["passwordCredentials"]);
    }

    [Fact]
    public void Apps_are_never_listed_with_the_policies()
    {
        Assert.DoesNotContain(ResourceRegistry.For(M365Service.Entra), t => t.Id == ResourceRegistry.EnterpriseApp);
    }

    [Fact]
    public async Task Preflight_blocks_an_app_whose_group_has_no_match()
    {
        var apps = await ExportAsync();

        var report = await PreflightAsync(DestinationGraph(), apps, Plan(salesMatched: false));

        Assert.Equal(Outcome.Blocked, report.Apps.Single(a => a.Source.DisplayName == "Zoom").Outcome);
        Assert.Equal(Outcome.Create, report.Apps.Single(a => a.Source.DisplayName == "Miro").Outcome);
        Assert.True(report.HasBlockers);
        Assert.Contains(report.Findings, f => f.Severity == Severity.Blocker && f.Items.Any(i => i.Contains("\"Sales\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Preflight_lists_permissions_for_consent_and_what_to_set_up_by_hand()
    {
        var report = await PreflightAsync(DestinationGraph(), await ExportAsync(), Plan());

        var consent = Assert.Single(report.Findings, f => f.Title.StartsWith("API permissions need admin consent", StringComparison.Ordinal));
        Assert.Contains("Zoom: Microsoft Graph: User.Read.All (application)", consent.Items);
        Assert.Contains(report.Findings, f => f.Title.StartsWith("Set up by hand", StringComparison.Ordinal) && f.Items.Any(i => i.Contains("Client secrets", StringComparison.Ordinal)));
        Assert.Equal(2, report.CreateCount);
        Assert.Equal(2, report.AssignmentCount);
        Assert.True(report.CanDeploy);
    }

    [Fact]
    public async Task Preflight_skips_an_app_already_there_or_updates_it_with_only_what_it_lacks()
    {
        var apps = await ExportAsync();

        var skipped = await PreflightAsync(DestinationGraph(zoomThere: true), apps, Plan());
        Assert.Equal(Outcome.Skip, skipped.Apps.Single(a => a.Source.DisplayName == "Zoom").Outcome);
        Assert.Contains(skipped.Findings, f => f.Choice == FindingChoice.ExistingApps);

        var updated = await PreflightAsync(DestinationGraph(zoomThere: true), apps, Plan(), ExistingAppChoice.Update);
        var zoom = updated.Apps.Single(a => a.Source.DisplayName == "Zoom");
        Assert.True(zoom.IsUpdate);
        Assert.Equal(new[] { "appRoleAssignmentRequired" }, zoom.ChangedSettings);
        // Anna is already assigned; only Sales is added. Anna isn't an owner yet.
        Assert.Equal("dst-sales", Assert.Single(zoom.Assignments)!["principalId"]!.GetValue<string>());
        Assert.Equal("dst-anna", Assert.Single(zoom.Owners)!.GetValue<string>());
    }

    [Fact]
    public async Task Deploy_creates_every_app_before_giving_access_and_never_grants_consent()
    {
        var destination = DestinationGraph();
        var report = await PreflightAsync(destination, await ExportAsync(), Plan());
        var run = AppDeployer.Plan(report, Source, Destination);

        await AppDeployer.RunAsync(destination, run, () => Task.CompletedTask);

        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
        var paths = destination.Posts.Select(p => p.Path).ToList();
        var lastApp = paths.FindLastIndex(p => p == "v1.0/servicePrincipals");
        var firstAccess = paths.FindIndex(p => p.Contains("/appRoleAssignedTo", StringComparison.Ordinal) || p.Contains("/owners/", StringComparison.Ordinal));
        Assert.True(lastApp >= 0 && firstAccess > lastApp, string.Join(", ", paths));

        var zoom = run.Steps.Single(s => s.DisplayName == "Zoom");
        var created = destination.Posts.Single(p => p.Path == "v1.0/servicePrincipals" && p.Body["appId"]?.GetValue<string>() == "app-zoom").Body;
        Assert.True(created["appRoleAssignmentRequired"]!.GetValue<bool>());
        Assert.Null(created["apiPermissions"]);
        var group = destination.Posts.Single(p => p.Path == $"v1.0/servicePrincipals/{zoom.DestinationId}/appRoleAssignedTo" && p.Body["principalId"]?.GetValue<string>() == "dst-sales");
        Assert.Equal(zoom.DestinationId, group.Body["resourceId"]!.GetValue<string>());
        Assert.Contains(destination.Posts, p => p.Path == $"v1.0/servicePrincipals/{zoom.DestinationId}/owners/$ref");

        // Consent is never granted: no delegated grants, and no app roles given to the app itself.
        Assert.DoesNotContain(paths, p => p.Contains("oauth2PermissionGrants", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Contains("/appRoleAssignments", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(destination.Patches, p => p.Path.Contains("oauth2PermissionGrants", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Rollback_deletes_the_apps_the_run_created()
    {
        var destination = DestinationGraph();
        var run = AppDeployer.Plan(await PreflightAsync(destination, await ExportAsync(), Plan()), Source, Destination);
        await AppDeployer.RunAsync(destination, run, () => Task.CompletedTask);

        await AppDeployer.RollbackAsync(destination, run, () => Task.CompletedTask);

        Assert.All(run.Steps, s => Assert.Equal(StepStatus.RolledBack, s.Status));
        Assert.Equal(run.Steps.Select(s => $"v1.0/servicePrincipals/{s.DestinationId}").Reverse(), destination.Deletes);
        Assert.NotNull(run.RolledBack);
    }

    [Fact]
    public async Task Rollback_of_an_updated_app_removes_only_what_the_run_added_and_puts_its_settings_back()
    {
        var destination = DestinationGraph(zoomThere: true);
        var report = await PreflightAsync(destination, (await ExportAsync()).Where(a => a.DisplayName == "Zoom").ToList(), Plan(), ExistingAppChoice.Update);
        var run = AppDeployer.Plan(report, Source, Destination);
        await AppDeployer.RunAsync(destination, run, () => Task.CompletedTask);
        var step = Assert.Single(run.Steps);
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal("dst-zoom", step.DestinationId);
        Assert.DoesNotContain(destination.Posts, p => p.Path == "v1.0/servicePrincipals");

        await AppDeployer.RollbackAsync(destination, run, () => Task.CompletedTask);

        Assert.Equal(StepStatus.RolledBack, step.Status);
        Assert.Contains("v1.0/servicePrincipals/dst-zoom/owners/dst-anna/$ref", destination.Deletes);
        Assert.Contains(destination.Deletes, d => d.StartsWith("v1.0/servicePrincipals/dst-zoom/appRoleAssignedTo/new-", StringComparison.Ordinal));
        Assert.DoesNotContain("v1.0/servicePrincipals/dst-zoom", destination.Deletes);
        Assert.False(destination.Patches[^1].Body["appRoleAssignmentRequired"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_interrupted_create_uses_the_app_found_by_app_id()
    {
        var destination = DestinationGraph();
        var run = AppDeployer.Plan(await PreflightAsync(destination, (await ExportAsync()).Where(a => a.DisplayName == "Miro").ToList(), Plan()), Source, Destination);
        run.Steps[0].Status = StepStatus.Creating;
        var resumed = new FakeGraph().Answer(path => path.StartsWith("v1.0/servicePrincipals?", StringComparison.Ordinal)
            ? JsonNode.Parse("""{ "value": [ { "id": "found-miro" } ] }""")!.AsObject()
            : null);

        await AppDeployer.RunAsync(resumed, run, () => Task.CompletedTask);

        Assert.Equal("found-miro", run.Steps[0].DestinationId);
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Empty(resumed.Posts);
    }

    [Fact]
    public async Task App_runs_and_policy_runs_are_kept_apart()
    {
        var destination = DestinationGraph();
        var run = AppDeployer.Plan(await PreflightAsync(destination, await ExportAsync(), Plan()), Source, Destination);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Deployer.RunAsync(destination, run, () => Task.CompletedTask));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Deployer.RollbackAsync(destination, run, () => Task.CompletedTask));

        var root = Path.Combine(Path.GetTempPath(), "movewise-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            run.Folder = RunStore.NewRunFolder("contoso", "fabrikam", run.RunId, root);
            await RunStore.SaveAsync(run);
            var policies = new DeployRun
            {
                RunId = "policyrun", Started = DateTimeOffset.Now, SourceTenantId = Source.TenantId, SourceName = "Contoso",
                DestinationTenantId = Destination.TenantId, DestinationName = "Fabrikam", Steps = [],
            };
            policies.Folder = RunStore.NewRunFolder("contoso", "fabrikam", policies.RunId, root);
            await RunStore.SaveAsync(policies);

            Assert.Equal("policyrun", Assert.Single(RunStore.List(Destination.TenantId, root)).RunId);
            Assert.Equal(run.RunId, Assert.Single(RunStore.List(Destination.TenantId, root, RunKind.Applications)).RunId);
            Assert.Equal(RunKind.Applications, RunStore.Load(Path.Combine(run.Folder, RunStore.FileName)).Kind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
