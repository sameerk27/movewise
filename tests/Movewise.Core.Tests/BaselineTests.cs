using Movewise.Core.Access;
using Movewise.Core.Baselines;
using Movewise.Core.Compare;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

/// <summary>A baseline saved from a template tenant and applied to another, without the template signed in.</summary>
public class BaselineTests
{
    const string CaPath = "v1.0/identity/conditionalAccess/policies";
    const string Groups = "v1.0/groups";
    const string GroupSelect = "id,displayName,mailNickname,description,groupTypes,securityEnabled,mailEnabled,membershipRule,membershipRuleProcessingState";

    static readonly ResourceType ConditionalAccess = ResourceRegistry.Get(ResourceRegistry.ConditionalAccessPolicy);
    static readonly TenantInfo Template = new("tpl", "Template", "template.onmicrosoft.com", "template.com", "admin@template.com", "Admin", false, [], [], []);
    static readonly TenantInfo Client = new("client", "Client A", "clienta.onmicrosoft.com", "clienta.com", "admin@clienta.com", "Admin", false,
        [DirectoryRoles.GlobalAdministrator], [], []) { ServicePlans = new HashSet<string>(["AAD_PREMIUM"], StringComparer.OrdinalIgnoreCase) };

    const string Policy = """
        { "id": "p1", "displayName": "BASE-01 MFA for everyone", "state": "enabled",
          "conditions": { "users": { "includeUsers": ["All"], "excludeGroups": ["g-tpl"] }, "applications": { "includeApplications": ["All"] } },
          "grantControls": { "operator": "OR", "builtInControls": ["mfa"] } }
        """;

    static string GroupByName(string name) => GraphQuery.Where(Groups, $"displayName eq {GraphQuery.Literal(name)}", "id,displayName,mailNickname", top: 5);

    static FakeGraph Tenant(string groupId) => new FakeGraph()
        .Object(GraphQuery.Item(Groups, groupId, GroupSelect),
            $$"""{ "id": "{{groupId}}", "displayName": "Break glass", "mailNickname": "breakglass", "securityEnabled": true, "mailEnabled": false, "groupTypes": [] }""")
        .Object(GroupByName("Break glass"), $$"""{ "value": [ { "id": "{{groupId}}", "displayName": "Break glass", "mailNickname": "breakglass" } ] }""");

    static async Task<Baseline> SaveAndLoadAsync(string folder)
    {
        var template = Tenant("g-tpl").Collection(CaPath, Policy);
        var clients = new TenantClients(new ReadOnlyGraph(template));
        var items = (await Exporter.ExportAsync(clients, [ConditionalAccess])).Items;

        var baseline = await BaselineStore.CaptureAsync("Standard security", clients, Template, false, items, items);
        await BaselineStore.SaveAsync(baseline, folder);
        return Assert.Single(await BaselineStore.ListAsync(folder));
    }

    [Fact]
    public async Task A_baseline_applies_to_another_tenant_with_its_groups_matched_without_the_template_signed_in()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mw-base-" + Guid.NewGuid().ToString("N"));
        try
        {
            var baseline = await SaveAndLoadAsync(folder);
            Assert.Equal("Standard security", baseline.Name);
            Assert.Equal("BASE-01 MFA for everyone", Assert.Single(baseline.Items).DisplayName);

            // Only the client is signed in; the template's answers come from the file.
            var source = new OfflineGraph(baseline.Responses);
            var client = Tenant("g-client").Collection(CaPath);
            var plan = await Matcher.BuildAsync(source, new TenantClients(client), baseline.Items, baseline.Items);
            var group = Assert.Single(plan.OfType(ResourceRegistry.Group));
            Assert.Equal("g-client", group.Destination!.Id);

            var report = await PreflightCheck.RunAsync(new TenantClients(client), Client, baseline.Items, plan, new PreflightChoices());
            var run = DeployPlanner.Plan(report, plan, Template with { DisplayName = "Baseline: Standard security" }, Client);
            await Deployer.RunAsync(client, run, () => Task.CompletedTask);

            var (_, body) = Assert.Single(client.Posts, p => p.Path == CaPath);
            Assert.Equal("g-client", body["conditions"]!["users"]!["excludeGroups"]![0]!.GetValue<string>());
            Assert.Equal("enabledForReportingButNotEnforced", body["state"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task A_tenant_is_compared_with_a_baseline_in_its_own_terms()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mw-base-" + Guid.NewGuid().ToString("N"));
        try
        {
            var baseline = await SaveAndLoadAsync(folder);
            // The client has the policy, excluding its own Break glass group: that's the same.
            var client = Tenant("g-client").Collection(CaPath, Policy.Replace("g-tpl", "g-client").Replace("\"p1\"", "\"c9\""));

            var report = await TenantComparer.CompareTenantsAsync(new TenantClients(new OfflineGraph(baseline.Responses)), "Baseline", new TenantClients(client), "Client A",
                [ConditionalAccess], sourceItems: baseline.Items);

            Assert.Equal(Difference.Same, Assert.Single(report.Items).Difference);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task A_search_the_baseline_didnt_record_finds_nothing_but_a_missing_object_fails()
    {
        var graph = new OfflineGraph(new Dictionary<string, System.Text.Json.Nodes.JsonObject>());

        var page = await graph.GetObjectAsync(GraphQuery.Where("v1.0/servicePrincipals", "appId eq 'x'", "appId,displayName"));
        Assert.Empty(page["value"]!.AsArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => graph.GetObjectAsync("v1.0/groups/g1"));
    }
}
