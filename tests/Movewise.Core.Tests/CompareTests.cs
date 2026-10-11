using System.Text.Json.Nodes;
using Movewise.Core.Compare;
using Movewise.Core.Export;
using Movewise.Core.Registry;

namespace Movewise.Core.Tests;

public class CompareTests
{
    const string CaPath = "v1.0/identity/conditionalAccess/policies";
    const string Groups = "v1.0/groups";
    static readonly ResourceType ConditionalAccess = ResourceRegistry.Get(ResourceRegistry.ConditionalAccessPolicy);
    static readonly ResourceType NamedLocation = ResourceRegistry.Get(ResourceRegistry.NamedLocation);

    static string Policy(string name, string state, string excludeGroup, int? minutes = null) => $$"""
        { "id": "{{name}}-id", "displayName": "{{name}}", "state": "{{state}}", "modifiedDateTime": "2026-0{{(minutes ?? 1)}}-01T00:00:00Z",
          "conditions": { "users": { "includeUsers": ["All"], "excludeGroups": ["{{excludeGroup}}"] }, "applications": { "includeApplications": ["All"] } },
          "grantControls": { "operator": "OR", "builtInControls": ["mfa"] } }
        """;

    static FakeGraph Tenant(string groupId, params string[] policies)
    {
        var graph = new FakeGraph()
            .Collection(CaPath, policies)
            .Object(GraphQuery.Item(Groups, groupId, "id,displayName,mailNickname,description,groupTypes,securityEnabled,mailEnabled,membershipRule,membershipRuleProcessingState"),
                $$"""{ "id": "{{groupId}}", "displayName": "Break glass", "mailNickname": "breakglass", "securityEnabled": true, "mailEnabled": false, "groupTypes": [] }""");
        return graph;
    }

    [Fact]
    public async Task Tenants_are_compared_in_the_destinations_terms_so_different_group_ids_arent_a_difference()
    {
        // The same policy in both, excluding each tenant's own "Break glass" group, plus one policy only on each side
        // and one that differs in its state.
        var source = Tenant("g-src",
            Policy("CA001 MFA", "enabled", "g-src"),
            Policy("CA002 Only in source", "enabled", "g-src"),
            Policy("CA003 State", "enabled", "g-src"));
        var destination = Tenant("g-dst",
            Policy("CA001 MFA", "enabled", "g-dst", minutes: 5),
            Policy("CA003 State", "enabledForReportingButNotEnforced", "g-dst"),
            Policy("CA004 Only in destination", "enabled", "g-dst"));
        destination.Object(MatcherGroupQuery("Break glass"), """{ "value": [ { "id": "g-dst", "displayName": "Break glass", "mailNickname": "breakglass" } ] }""");

        var report = await TenantComparer.CompareTenantsAsync(
            new TenantClients(new ReadOnlyGraph(source)), "Contoso", new TenantClients(destination), "Fabrikam", [ConditionalAccess]);

        Assert.Equal(Difference.Same, Item(report, "CA001 MFA").Difference);
        Assert.Equal(Difference.OnlyInFirst, Item(report, "CA002 Only in source").Difference);
        var changed = Item(report, "CA003 State");
        Assert.Equal(Difference.Different, changed.Difference);
        Assert.Equal(["state"], changed.ChangedSettings);
        Assert.Equal(Difference.OnlyInSecond, Item(report, "CA004 Only in destination").Difference);
        Assert.False(report.IsDrift);
    }

    [Fact]
    public async Task A_type_that_cant_be_read_now_isnt_shown_as_deleted()
    {
        var then = new FakeGraph().Collection(CaPath, Policy("CA001 MFA", "enabled", "g1"));
        var snapshot = new Snapshot("t1", "Contoso", DateTimeOffset.Now.AddDays(-7), [ConditionalAccess],
            (await Exporter.ExportAsync(then, [ConditionalAccess])).Items);

        // Throttled, or the admin lost the role: nothing comes back for the type.
        var report = await TenantComparer.CompareWithSnapshotAsync(snapshot, new TenantClients(new ReadOnlyGraph(new FakeGraph().Failing(CaPath))), "Contoso");

        Assert.Empty(report.Items);
        Assert.NotEmpty(report.Warnings);
    }

    [Fact]
    public async Task Drift_pairs_by_id_so_a_renamed_policy_shows_as_changed_not_removed_and_added()
    {
        var then = new FakeGraph().Collection(CaPath, Policy("CA001 MFA", "enabled", "g1"), Policy("CA002 Legacy", "enabled", "g1"));
        var snapshot = new Snapshot("t1", "Contoso", DateTimeOffset.Now.AddDays(-7), [ConditionalAccess],
            (await Exporter.ExportAsync(then, [ConditionalAccess])).Items);

        var now = new FakeGraph().Collection(CaPath,
            Policy("CA001 MFA", "disabled", "g1").Replace("\"CA001 MFA-id\"", "\"CA001 MFA-id\""),
            Policy("CA001 MFA renamed", "enabled", "g1").Replace("\"CA001 MFA renamed-id\"", "\"CA002 Legacy-id\""),
            Policy("CA009 New", "enabled", "g1"));

        var report = await TenantComparer.CompareWithSnapshotAsync(snapshot, new TenantClients(new ReadOnlyGraph(now)), "Contoso");

        Assert.True(report.IsDrift);
        Assert.Equal(["state"], Item(report, "CA001 MFA").ChangedSettings);
        var renamed = Item(report, "CA001 MFA renamed");
        Assert.Equal(Difference.Different, renamed.Difference);
        Assert.Contains("displayName", renamed.ChangedSettings);
        Assert.Equal(Difference.OnlyInSecond, Item(report, "CA009 New").Difference);
        Assert.Equal(3, report.Items.Count);
    }

    [Fact]
    public async Task A_snapshot_is_saved_and_read_back_with_ids_names_and_settings()
    {
        var graph = new FakeGraph().Collection(CaPath, Policy("CA001 MFA", "enabled", "g1"));
        var items = (await Exporter.ExportAsync(graph, [ConditionalAccess])).Items;
        var folder = Path.Combine(Path.GetTempPath(), "mw-snap-" + Guid.NewGuid().ToString("N"));
        try
        {
            var saved = await SnapshotStore.SaveAsync(new Snapshot("t1", "Contoso", DateTimeOffset.Now, [ConditionalAccess, NamedLocation], items), folder);
            var loaded = Assert.Single(await SnapshotStore.ListAsync(folder));

            Assert.Equal(saved.Path, loaded.Path);
            var item = Assert.Single(loaded.Items);
            Assert.Equal("CA001 MFA-id", item.SourceId);
            Assert.Equal("CA001 MFA", item.DisplayName);
            Assert.Equal(ConditionalAccess, item.Type);
            Assert.Equal("enabled", item.Settings["state"]!.GetValue<string>());
            Assert.Contains(item.Dependencies, d => d.Value == "g1");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task A_type_the_tenant_had_none_of_is_read_again_so_new_items_show_as_added()
    {
        var snapshot = new Snapshot("t1", "Contoso", DateTimeOffset.Now.AddDays(-1), [ConditionalAccess, NamedLocation], []);
        var now = new FakeGraph()
            .Collection(CaPath)
            .Collection("v1.0/identity/conditionalAccess/namedLocations",
                """{ "@odata.type": "#microsoft.graph.ipNamedLocation", "id": "n1", "displayName": "HQ", "ipRanges": [ { "cidrAddress": "10.0.0.0/8" } ] }""");

        var report = await TenantComparer.CompareWithSnapshotAsync(snapshot, new TenantClients(new ReadOnlyGraph(now)), "Contoso");

        Assert.Equal(Difference.OnlyInSecond, Item(report, "HQ").Difference);
    }

    [Fact]
    public async Task The_types_a_snapshot_covered_are_saved_even_when_empty()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mw-snap-" + Guid.NewGuid().ToString("N"));
        try
        {
            await SnapshotStore.SaveAsync(new Snapshot("t1", "Contoso", DateTimeOffset.Now, [ConditionalAccess, NamedLocation], []), folder);
            var loaded = Assert.Single(await SnapshotStore.ListAsync(folder));
            Assert.Equal([ConditionalAccess, NamedLocation], loaded.Types);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Drift_compares_references_by_name_so_a_recreated_location_isnt_a_change_but_restore_keeps_ids()
    {
        const string Locations = "v1.0/identity/conditionalAccess/namedLocations";
        static string Location(string id) => $$"""{ "@odata.type": "#microsoft.graph.ipNamedLocation", "id": "{{id}}", "displayName": "HQ", "ipRanges": [ { "cidrAddress": "10.0.0.0/8" } ] }""";
        static string UsingLocation(string id) => $$"""
            { "id": "p1", "displayName": "CA005 Block outside HQ", "state": "enabled",
              "conditions": { "users": { "includeUsers": ["All"] }, "applications": { "includeApplications": ["All"] },
                "locations": { "includeLocations": ["All"], "excludeLocations": ["{{id}}"] } },
              "grantControls": { "operator": "OR", "builtInControls": ["block"] } }
            """;

        // The location was deleted and recreated (by a restore, say), so it has a new ID, and the policy points at that.
        var then = new FakeGraph().Collection(CaPath, UsingLocation("loc-old")).Collection(Locations, Location("loc-old"));
        var snapshot = new Snapshot("t1", "Contoso", DateTimeOffset.Now.AddDays(-1), [NamedLocation, ConditionalAccess],
            (await Exporter.ExportAsync(then, [NamedLocation, ConditionalAccess])).Items);
        var now = new FakeGraph().Collection(CaPath, UsingLocation("loc-new")).Collection(Locations, Location("loc-new"));

        var report = await TenantComparer.CompareWithSnapshotAsync(snapshot, new TenantClients(new ReadOnlyGraph(now)), "Contoso");

        var policy = Item(report, "CA005 Block outside HQ");
        Assert.Equal(Difference.Same, policy.Difference);
        Assert.Contains("loc-old", policy.FirstSource!.Settings.ToJsonString());
    }

    [Fact]
    public void Settings_the_service_sets_itself_arent_differences()
    {
        var first = JsonNode.Parse("""{ "displayName": "A", "modifiedDateTime": "2026-01-01", "id": "1", "list": ["b", "a"] }""")!.AsObject();
        var second = JsonNode.Parse("""{ "displayName": "A", "modifiedDateTime": "2026-05-01", "id": "2", "list": ["a", "b"] }""")!.AsObject();

        Assert.Empty(TenantComparer.Changes(ConditionalAccess, first, second));
    }

    [Fact]
    public void Empty_lists_deep_inside_arent_differences_but_real_values_are()
    {
        var first = JsonNode.Parse("""{ "conditions": { "users": { "includeUsers": ["All"], "excludeGroups": [], "excludeUsers": [] }, "platforms": null } }""")!.AsObject();
        var same = JsonNode.Parse("""{ "conditions": { "users": { "includeUsers": ["All"] } } }""")!.AsObject();
        var different = JsonNode.Parse("""{ "conditions": { "users": { "includeUsers": ["All"], "excludeGroups": ["g1"] } } }""")!.AsObject();

        Assert.Empty(TenantComparer.Changes(ConditionalAccess, first, same));
        Assert.Equal(["conditions"], TenantComparer.Changes(ConditionalAccess, first, different));
        Assert.False(TenantComparer.Pruned(first)["conditions"]!["users"]!.AsObject().ContainsKey("excludeGroups"));
    }

    static ComparedItem Item(ComparisonReport report, string name) => Assert.Single(report.Items, i => i.Name == name);

    static string MatcherGroupQuery(string name) => GraphQuery.Where(Groups, $"displayName eq {GraphQuery.Literal(name)}", "id,displayName,mailNickname", top: 5);
}
