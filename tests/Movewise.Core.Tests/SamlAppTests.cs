using System.Text.Json.Nodes;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

public class SamlAppTests
{
    static readonly ResourceType EnterpriseApp = ResourceRegistry.Get(ResourceRegistry.EnterpriseApp);
    static readonly ResourceType Registration = ResourceRegistry.Get(ResourceRegistry.AppRegistration);

    static readonly TenantInfo Source = new("src-tenant", "Contoso", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false, [], [], []);
    static readonly TenantInfo Destination = new("dst-tenant", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false,
        [Access.DirectoryRoles.GlobalAdministrator], [], []);

    const string Sales = "group-sales";
    const string StandardRole = "role-standard";
    const string EntityId = "https://saml.salesforce.com";

    /// <summary>Contoso added Salesforce from the gallery and set up SAML: entity ID, reply URL, relay state, an app role, claims.</summary>
    static FakeGraph SourceGraph(bool templateFound = true)
    {
        var graph = new FakeGraph()
            .Collection(EnterpriseApp.ListPath, """
                { "id": "sp-sf", "appId": "app-sf", "displayName": "Salesforce", "appOwnerOrganizationId": "src-tenant", "servicePrincipalType": "Application",
                  "applicationTemplateId": "template-sf", "preferredSingleSignOnMode": "saml", "loginUrl": "https://contoso.my.salesforce.com",
                  "samlSingleSignOnSettings": { "relayState": "https://contoso.my.salesforce.com/home" }, "appRoleAssignmentRequired": true,
                  "keyCredentials": [ { "keyId": "k1", "usage": "Sign" } ] }
                """)
            .Collection(Registration.ListPath, $$"""
                { "id": "reg-sf", "appId": "app-sf", "displayName": "Salesforce", "identifierUris": ["{{EntityId}}"],
                  "web": { "redirectUris": ["https://contoso.my.salesforce.com/?so=00D"] },
                  "appRoles": [ { "id": "{{StandardRole}}", "value": "Standard", "allowedMemberTypes": ["User"], "isEnabled": true } ] }
                """)
            .Collection("v1.0/servicePrincipals/sp-sf/appRoleAssignedTo",
                $$"""{ "id": "a1", "principalType": "Group", "principalId": "{{Sales}}", "appRoleId": "{{StandardRole}}" }""")
            .Collection("v1.0/servicePrincipals/sp-sf/claimsMappingPolicies",
                """{ "id": "cm1", "displayName": "Salesforce claims", "definition": ["{\"ClaimsMappingPolicy\":{\"Version\":1}}"] }""")
            .Object("beta/servicePrincipals/sp-sf/claimsPolicy",
                """{ "id": "cp1", "includeBasicClaimSet": true, "claims": [ { "@odata.type": "#microsoft.graph.customClaim", "name": "role", "configurations": [] } ] }""");
        if (templateFound)
            graph.Object(GraphQuery.Item("v1.0/applicationTemplates", "template-sf", "id,displayName"), """{ "id": "template-sf", "displayName": "Salesforce" }""");
        return graph;
    }

    static MappingPlan Plan()
    {
        var sales = new Mapping.Mapping { TargetType = ResourceRegistry.Group, Source = new ObjectRef(Sales, "Sales"), UsedBy = ["Salesforce"] };
        sales.MapTo(new ObjectRef("dst-sales", "Sales"));
        return new MappingPlan([sales]);
    }

    /// <summary>A destination where the template makes the app with the "User" role, and signing certificates get thumbprint ABC123.</summary>
    static FakeGraph DestinationGraph() => new FakeGraph()
        .PostAnswer((path, _) =>
            path == "v1.0/applicationTemplates/template-sf/instantiate"
                ? JsonNode.Parse("""{ "application": { "id": "dst-reg-sf", "appId": "dst-app-sf" }, "servicePrincipal": { "id": "dst-sp-sf" } }""")!.AsObject()
            : path.EndsWith("/addTokenSigningCertificate", StringComparison.Ordinal)
                ? JsonNode.Parse("""{ "thumbprint": "ABC123", "keyId": "k-new" }""")!.AsObject()
            : null)
        .Answer(path =>
            path.StartsWith("v1.0/applications/dst-reg-sf?$select=appRoles", StringComparison.Ordinal)
                ? JsonNode.Parse("""{ "appRoles": [ { "id": "role-user", "value": "User", "allowedMemberTypes": ["User"], "isEnabled": true } ] }""")!.AsObject()
            : path.Contains('?') ? JsonNode.Parse("""{ "value": [] }""")!.AsObject()
            : null);

    [Fact]
    public async Task Export_reads_a_gallery_apps_template_single_sign_on_and_claims()
    {
        var app = Assert.Single((await Exporter.ExportAppsAsync(SourceGraph(), Source.TenantId)).Items);

        Assert.Equal(ResourceRegistry.GalleryApp, app.Type.Id);
        Assert.Equal("Salesforce", app.Settings["template"]!["displayName"]!.GetValue<string>());
        var saml = app.Settings["saml"]!;
        Assert.Equal("https://contoso.my.salesforce.com/home", saml["samlSingleSignOnSettings"]!["relayState"]!.GetValue<string>());
        Assert.Equal("Salesforce claims", saml["claimsMappingPolicies"]![0]!["displayName"]!.GetValue<string>());
        Assert.Equal("role", saml["claimsPolicy"]!["claims"]![0]!["name"]!.GetValue<string>());
        Assert.Null(saml["claimsPolicy"]!["id"]);
        // Its entity ID is kept, and SAML is no longer something to set up by hand.
        Assert.Empty(AppPreflight.Strings(app.Settings["notMigrated"]));
    }

    [Fact]
    public async Task Deploy_makes_a_gallery_app_from_its_template_with_its_single_sign_on_a_new_certificate_and_claims()
    {
        var root = Path.Combine(Path.GetTempPath(), "movewise-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var app = Assert.Single((await Exporter.ExportAppsAsync(SourceGraph(), Source.TenantId)).Items);
            var destination = DestinationGraph();
            var report = await AppPreflight.RunAsync(new TenantClients(destination), Destination, [app], Plan(), new AppPreflightChoices());
            Assert.Contains(report.Findings, f => f.Title.StartsWith("New SAML signing certificates", StringComparison.Ordinal) && f.Items.Contains("Salesforce"));
            var run = AppDeployer.Plan(report, Source, Destination);
            var map = AppIdMap.Load(Destination.TenantId, root);

            await AppDeployer.RunAsync(destination, run, () => Task.CompletedTask, appIds: map);

            var step = Assert.Single(run.Steps);
            Assert.Equal(StepStatus.Done, step.Status);
            Assert.Equal(("dst-reg-sf", "dst-app-sf", "dst-sp-sf"), (step.DestinationId, step.AppId, step.ServicePrincipalId));
            var paths = destination.Posts.Select(p => p.Path).ToList();
            Assert.Equal("v1.0/applicationTemplates/template-sf/instantiate", paths[0]);
            Assert.Equal("Salesforce", destination.Posts[0].Body["displayName"]!.GetValue<string>());
            Assert.DoesNotContain("v1.0/applications", paths);
            Assert.DoesNotContain("v1.0/servicePrincipals", paths);

            // Set up as in the source: its roles added to the template's, its reply URL and entity ID.
            var manifest = destination.Patches.First(p => p.Path == "v1.0/applications/dst-reg-sf").Body;
            Assert.Equal(new[] { "role-user", StandardRole }, manifest["appRoles"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()));
            Assert.Equal("https://contoso.my.salesforce.com/?so=00D", manifest["web"]!["redirectUris"]![0]!.GetValue<string>());
            Assert.Contains(destination.Patches, p => p.Path == "v1.0/applications/dst-reg-sf" && p.Body["identifierUris"]?[0]?.GetValue<string>() == EntityId);

            var principal = destination.Patches.Where(p => p.Path == "v1.0/servicePrincipals/dst-sp-sf").Select(p => p.Body).ToList();
            Assert.Contains(principal, b => b["preferredSingleSignOnMode"]?.GetValue<string>() == "saml" && b["samlSingleSignOnSettings"]?["relayState"] is not null);
            Assert.Contains(principal, b => b["loginUrl"]?.GetValue<string>() == "https://contoso.my.salesforce.com");
            Assert.Contains(principal, b => b["preferredTokenSigningKeyThumbprint"]?.GetValue<string>() == "ABC123");
            Assert.Contains("v1.0/servicePrincipals/dst-sp-sf/addTokenSigningCertificate", paths);
            Assert.Contains(step.Notes, n => n.Contains("ABC123", StringComparison.Ordinal));

            var policy = destination.Posts.Single(p => p.Path == "v1.0/policies/claimsMappingPolicies");
            Assert.Equal("Salesforce claims", policy.Body["displayName"]!.GetValue<string>());
            Assert.Contains("v1.0/servicePrincipals/dst-sp-sf/claimsMappingPolicies/$ref", paths);
            Assert.Contains(destination.Patches, p => p.Path == "beta/servicePrincipals/dst-sp-sf/claimsPolicy" && p.Body["claims"] is JsonArray { Count: 1 });

            var assignment = destination.Posts.Single(p => p.Path == "v1.0/servicePrincipals/dst-sp-sf/appRoleAssignedTo").Body;
            Assert.Equal(StandardRole, assignment["appRoleId"]!.GetValue<string>());
            Assert.DoesNotContain(paths, p => p.Contains("oauth2PermissionGrants", StringComparison.OrdinalIgnoreCase) || p.Contains("/appRoleAssignments", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("dst-app-sf", map.For(Source.TenantId)["app-sf"]);

            await AppDeployer.RollbackAsync(destination, run, () => Task.CompletedTask, appIds: map);

            Assert.Equal(StepStatus.RolledBack, step.Status);
            Assert.Contains("v1.0/applications/dst-reg-sf", destination.Deletes);
            Assert.Contains(destination.Deletes, d => d.StartsWith("v1.0/policies/claimsMappingPolicies/new-", StringComparison.Ordinal));
            Assert.Empty(map.For(Source.TenantId));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task An_app_whose_gallery_template_cant_be_found_is_left_for_setting_up_by_hand()
    {
        var app = Assert.Single((await Exporter.ExportAppsAsync(SourceGraph(templateFound: false), Source.TenantId)).Items);

        var report = await AppPreflight.RunAsync(new TenantClients(DestinationGraph()), Destination, [app], Plan(), new AppPreflightChoices());

        Assert.Equal(Outcome.Skip, Assert.Single(report.Apps).Outcome);
        Assert.Contains(report.Findings, f => f.Title.StartsWith("Add from the gallery by hand", StringComparison.Ordinal) && f.Items.Contains("Salesforce"));
        Assert.False(report.CanDeploy);
    }
}
