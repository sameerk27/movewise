using System.Text.Json.Nodes;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

public class AppRegistrationTests
{
    static readonly ResourceType EnterpriseApp = ResourceRegistry.Get(ResourceRegistry.EnterpriseApp);
    static readonly ResourceType Registration = ResourceRegistry.Get(ResourceRegistry.AppRegistration);
    static readonly ResourceType ConditionalAccess = ResourceRegistry.Get(ResourceRegistry.ConditionalAccessPolicy);

    static readonly TenantInfo Source = new("src-tenant", "Contoso", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false, [], [], []);
    static readonly TenantInfo Destination = new("dst-tenant", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false,
        [Access.DirectoryRoles.ApplicationAdministrator], [], []);

    const string Anna = "user-anna";
    const string Sales = "group-sales";
    const string HrAdmin = "role-hr-admin";

    /// <summary>
    /// Contoso registered the HR Portal (with its own service principal, a group assigned, an owner, a secret, an API URI
    /// made from its app ID and one on its own domain) and Payroll Sync (never used, so no service principal), and made
    /// Salesforce from a gallery template, which isn't migrated yet.
    /// </summary>
    static FakeGraph SourceGraph() => new FakeGraph()
        .Collection(EnterpriseApp.ListPath,
            """{ "id": "sp-hr", "appId": "app-hr", "displayName": "Contoso HR Portal", "appOwnerOrganizationId": "src-tenant", "servicePrincipalType": "Application", "appRoleAssignmentRequired": true }""",
            """{ "id": "sp-sf", "appId": "app-sf", "displayName": "Salesforce", "appOwnerOrganizationId": "src-tenant", "servicePrincipalType": "Application", "applicationTemplateId": "template-sf" }""")
        .Collection(Registration.ListPath,
            $$"""
            { "id": "reg-hr", "appId": "app-hr", "displayName": "Contoso HR Portal", "signInAudience": "AzureADMyOrg",
              "identifierUris": ["api://app-hr", "https://hr.contoso.com/api"],
              "web": { "redirectUris": ["https://hr.contoso.com/signin-oidc"] },
              "appRoles": [ { "id": "{{HrAdmin}}", "value": "HR.Admin", "allowedMemberTypes": ["User"], "isEnabled": true } ],
              "api": { "preAuthorizedApplications": [ { "appId": "app-mobile" } ] },
              "requiredResourceAccess": [ { "resourceAppId": "00000003-0000-0000-c000-000000000000", "resourceAccess": [ { "id": "scope-user-read", "type": "Scope" } ] },
                                          { "resourceAppId": "app-pay", "resourceAccess": [ { "id": "scope-pay", "type": "Scope" } ] } ],
              "passwordCredentials": [ { "keyId": "k1" } ], "keyCredentials": [] }
            """,
            """{ "id": "reg-pay", "appId": "app-pay", "displayName": "Payroll Sync" }""",
            """{ "id": "reg-sf", "appId": "app-sf", "displayName": "Salesforce" }""")
        .Collection("v1.0/servicePrincipals/sp-hr/appRoleAssignedTo",
            $$"""{ "id": "a1", "principalType": "Group", "principalId": "{{Sales}}", "appRoleId": "{{HrAdmin}}" }""")
        .Collection("v1.0/applications/reg-hr/owners?$select=id", $$"""{ "@odata.type": "#microsoft.graph.user", "id": "{{Anna}}" }""")
        .Answer(path => path.StartsWith("v1.0/servicePrincipals?", StringComparison.Ordinal) && path.Contains("app-hr", StringComparison.Ordinal)
            ? JsonNode.Parse("""{ "value": [ { "id": "sp-hr", "appId": "app-hr", "displayName": "Contoso HR Portal" } ] }""")!.AsObject()
            : null);

    static MappingPlan Plan()
    {
        var anna = new Mapping.Mapping { TargetType = ResourceRegistry.User, Source = new ObjectRef(Anna, "Anna"), UsedBy = ["Contoso HR Portal"] };
        anna.MapTo(new ObjectRef("dst-anna", "Anna"));
        var sales = new Mapping.Mapping { TargetType = ResourceRegistry.Group, Source = new ObjectRef(Sales, "Sales"), UsedBy = ["Contoso HR Portal"] };
        sales.MapTo(new ObjectRef("dst-sales", "Sales"));
        return new MappingPlan([anna, sales]);
    }

    /// <summary>
    /// A destination where a new registration gets the app ID "dst-app-hr", and where that app's service principal can be
    /// found once Movewise made it.
    /// </summary>
    static FakeGraph DestinationGraph() => new FakeGraph().Answer(path =>
        path.StartsWith("v1.0/applications/new-", StringComparison.Ordinal) ? JsonNode.Parse("""{ "appId": "dst-app-hr" }""")!.AsObject()
        : path.StartsWith("v1.0/servicePrincipals?", StringComparison.Ordinal) && path.Contains("dst-app-hr", StringComparison.Ordinal) && Made
            ? JsonNode.Parse("""{ "value": [ { "id": "dst-sp-hr", "appId": "dst-app-hr", "displayName": "Contoso HR Portal" } ] }""")!.AsObject()
        : path.StartsWith("v1.0/", StringComparison.Ordinal) && path.Contains('?') ? JsonNode.Parse("""{ "value": [] }""")!.AsObject()
        : null);

    // Set once the deployment has made the HR Portal's service principal, so lookups after that find it.
    static bool Made;

    static async Task<ExportedResource> HrPortalAsync() =>
        (await Exporter.ExportAppsAsync(SourceGraph(), Source.TenantId)).Items.Single(a => a.DisplayName == "Contoso HR Portal");

    [Fact]
    public async Task Export_reads_the_sources_own_registrations_and_what_cant_be_copied()
    {
        var apps = (await Exporter.ExportAppsAsync(SourceGraph(), Source.TenantId)).Items;

        // Salesforce came from a gallery template: left for later.
        Assert.Equal(new[] { "Contoso HR Portal", "Payroll Sync" }, apps.Select(a => a.DisplayName));
        var hr = apps[0];
        Assert.Equal(ResourceRegistry.AppRegistration, hr.Type.Id);
        Assert.True(hr.Settings["servicePrincipal"]!["appRoleAssignmentRequired"]!.GetValue<bool>());
        Assert.Equal(Sales, hr.Settings["assignedGroups"]![0]!["principalId"]!.GetValue<string>());
        Assert.Equal(Anna, hr.Settings["owners"]![0]!.GetValue<string>());
        Assert.Equal("00000003-0000-0000-c000-000000000000", Assert.Single(hr.Settings["requiredResourceAccess"]!.AsArray())!["resourceAppId"]!.GetValue<string>());
        Assert.Null(hr.Settings["api"]!["preAuthorizedApplications"]);
        Assert.Null(hr.Settings["passwordCredentials"]);

        var notMigrated = AppPreflight.Strings(hr.Settings["notMigrated"]);
        Assert.Contains(notMigrated, n => n.StartsWith("Client secrets (1)", StringComparison.Ordinal));
        Assert.Contains(notMigrated, n => n.Contains("https://hr.contoso.com/api", StringComparison.Ordinal));
        Assert.Contains(notMigrated, n => n.StartsWith("Pre-authorized", StringComparison.Ordinal));
        Assert.Contains(notMigrated, n => n.Contains("app ID app-pay", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_recreates_a_registration_with_a_new_app_id_records_it_and_rollback_takes_it_out()
    {
        Made = false;
        var root = Path.Combine(Path.GetTempPath(), "movewise-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var destination = DestinationGraph();
            var report = await AppPreflight.RunAsync(new TenantClients(destination), Destination, [await HrPortalAsync()], Plan(), new AppPreflightChoices());
            Assert.Contains(report.Findings, f => f.Title.StartsWith("New app IDs", StringComparison.Ordinal));
            var run = AppDeployer.Plan(report, Source, Destination);
            var map = AppIdMap.Load(Destination.TenantId, root);

            await AppDeployer.RunAsync(destination, run, () => Task.CompletedTask, s => Made |= s.ServicePrincipalId is not null, appIds: map);

            var step = Assert.Single(run.Steps);
            Assert.Equal(StepStatus.Done, step.Status);
            Assert.Equal("dst-app-hr", step.AppId);
            var paths = destination.Posts.Select(p => p.Path).ToList();
            Assert.True(paths.IndexOf("v1.0/applications") < paths.IndexOf("v1.0/servicePrincipals"), string.Join(", ", paths));
            var created = destination.Posts.Single(p => p.Path == "v1.0/applications").Body;
            Assert.Equal("Contoso HR Portal", created["displayName"]!.GetValue<string>());
            Assert.Null(created["identifierUris"]);
            Assert.Null(created["assignedGroups"]);
            Assert.Equal(HrAdmin, created["appRoles"]![0]!["id"]!.GetValue<string>());
            Assert.Equal("dst-app-hr", destination.Posts.Single(p => p.Path == "v1.0/servicePrincipals").Body["appId"]!.GetValue<string>());
            Assert.Equal("api://dst-app-hr", destination.Patches.Single(p => p.Path == $"v1.0/applications/{step.DestinationId}").Body["identifierUris"]![0]!.GetValue<string>());
            // Owners on the registration, assignments on its service principal, with the same app role.
            Assert.Contains($"v1.0/applications/{step.DestinationId}/owners/$ref", paths);
            var assignment = destination.Posts.Single(p => p.Path == $"v1.0/servicePrincipals/{step.ServicePrincipalId}/appRoleAssignedTo").Body;
            Assert.Equal(HrAdmin, assignment["appRoleId"]!.GetValue<string>());
            Assert.DoesNotContain(paths, p => p.Contains("oauth2PermissionGrants", StringComparison.OrdinalIgnoreCase) || p.Contains("/appRoleAssignments", StringComparison.OrdinalIgnoreCase));

            Assert.Equal("dst-app-hr", AppIdMap.Load(Destination.TenantId, root).For(Source.TenantId)["app-hr"]);

            await AppDeployer.RollbackAsync(destination, run, () => Task.CompletedTask, appIds: map);

            Assert.Equal(StepStatus.RolledBack, step.Status);
            Assert.Contains($"v1.0/applications/{step.DestinationId}", destination.Deletes);
            Assert.Empty(AppIdMap.Load(Destination.TenantId, root).For(Source.TenantId));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Policies_match_a_migrated_registration_and_are_rewritten_with_its_new_app_id()
    {
        Made = true;
        var settings = Normalizer.Normalize(JsonNode.Parse("""
            { "id": "ca-hr", "displayName": "CA005 - MFA for the HR Portal", "state": "enabled",
              "conditions": { "users": { "includeUsers": ["All"] }, "applications": { "includeApplications": ["app-hr"] } },
              "grantControls": { "operator": "OR", "builtInControls": ["mfa"] } }
            """)!.AsObject(), ConditionalAccess);
        var policy = new ExportedResource(ConditionalAccess, "ca-hr", "CA005 - MFA for the HR Portal", settings, DependencyExtractor.Extract(settings, ConditionalAccess));

        var unmatched = await Matcher.BuildAsync(SourceGraph(), DestinationGraph(), [policy], [policy]);
        var plan = await Matcher.BuildAsync(SourceGraph(), DestinationGraph(), [policy], [policy],
            migratedAppIds: new Dictionary<string, string> { ["app-hr"] = "dst-app-hr" });

        Assert.Equal(MatchKind.Unresolved, unmatched.Find(ResourceRegistry.Application, "app-hr")!.Kind);
        Assert.Contains("Migrate it in Applications", unmatched.Find(ResourceRegistry.Application, "app-hr")!.Note);
        var app = plan.Find(ResourceRegistry.Application, "app-hr")!;
        Assert.Equal(MatchKind.MigratedApp, app.Kind);
        Assert.Equal("Migrated by Movewise", MappingNames.Describe(app.Kind));
        var rewritten = Transformer.Transform(policy, plan, new TransformOptions());
        Assert.Equal("dst-app-hr", rewritten.Desired["conditions"]!["applications"]!["includeApplications"]![0]!.GetValue<string>());
        Assert.Empty(rewritten.Problems);
    }

    [Fact]
    public async Task A_registration_migrated_before_is_found_through_the_app_id_map()
    {
        Made = true;
        var report = await AppPreflight.RunAsync(new TenantClients(DestinationGraph()), Destination, [await HrPortalAsync()], Plan(), new AppPreflightChoices(),
            migratedAppIds: new Dictionary<string, string> { ["app-hr"] = "dst-app-hr" });

        var hr = Assert.Single(report.Apps);
        Assert.Equal(Outcome.Skip, hr.Outcome);
        Assert.Equal("dst-sp-hr", hr.ExistingId);
        Assert.Contains("dst-app-hr", hr.Reasons[0]);
    }
}
