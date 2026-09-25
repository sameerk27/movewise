using Movewise.Core.Export;
using Movewise.Core.Registry;

namespace Movewise.Core.Tests;

public class ExporterTests
{
    const string PolicyId = "11111111-0000-4000-8000-000000000001";
    const string PilotGroup = "22222222-0000-4000-8000-000000000002";
    const string ExcludedGroup = "33333333-0000-4000-8000-000000000003";
    const string WindowsFilter = "44444444-0000-4000-8000-000000000004";

    static readonly ResourceType SettingsCatalog = ResourceRegistry.Get(ResourceRegistry.SettingsCatalogPolicy);
    static readonly ResourceType ScopeTags = ResourceRegistry.Get(ResourceRegistry.ScopeTag);
    static readonly ResourceType Compliance = ResourceRegistry.Get(ResourceRegistry.CompliancePolicy);

    static FakeGraph SettingsCatalogTenant() => new FakeGraph()
        .Collection("beta/deviceManagement/configurationPolicies",
            $$"""{ "id": "{{PolicyId}}", "name": "WIN - BitLocker baseline", "settingCount": 2 }""")
        .Object($"beta/deviceManagement/configurationPolicies('{PolicyId}')?$expand=settings", $$"""
            {
              "@odata.context": "https://graph.microsoft.com/beta/$metadata#deviceManagement/configurationPolicies/$entity",
              "id": "{{PolicyId}}",
              "name": "WIN - BitLocker baseline",
              "platforms": "windows10",
              "technologies": "mdm",
              "settingCount": 2,
              "createdDateTime": "2025-02-01T00:00:00Z",
              "lastModifiedDateTime": "2026-09-01T00:00:00Z",
              "roleScopeTagIds": [ "0", "7" ],
              "settings": [ { "id": "0", "settingInstance": { "settingDefinitionId": "device_vendor_msft_bitlocker_requiredeviceencryption" } } ]
            }
            """)
        .Collection($"beta/deviceManagement/configurationPolicies('{PolicyId}')/assignments",
            $$"""
            { "id": "{{PolicyId}}_{{PilotGroup}}", "source": "direct", "sourceId": "{{PolicyId}}",
              "target": { "@odata.type": "#microsoft.graph.groupAssignmentTarget", "groupId": "{{PilotGroup}}",
                          "deviceAndAppManagementAssignmentFilterId": "{{WindowsFilter}}", "deviceAndAppManagementAssignmentFilterType": "include" } }
            """,
            $$"""
            { "id": "{{PolicyId}}_{{ExcludedGroup}}", "source": "direct", "sourceId": "{{PolicyId}}",
              "target": { "@odata.type": "#microsoft.graph.exclusionGroupAssignmentTarget", "groupId": "{{ExcludedGroup}}",
                          "deviceAndAppManagementAssignmentFilterId": "00000000-0000-0000-0000-000000000000" } }
            """,
            """
            { "id": "x_all", "source": "direct", "sourceId": "x",
              "target": { "@odata.type": "#microsoft.graph.allDevicesAssignmentTarget" } }
            """);

    [Fact]
    public async Task Reads_full_details_and_assignments_for_each_policy()
    {
        var graph = SettingsCatalogTenant();

        var result = await Exporter.ExportAsync(graph, [SettingsCatalog]);

        var policy = Assert.Single(result.Items);
        Assert.Equal("WIN - BitLocker baseline", policy.DisplayName);
        Assert.Equal(PolicyId, policy.SourceId);
        Assert.NotNull(policy.Settings["settings"]);
        Assert.Equal(3, policy.Settings["assignments"]!.AsArray().Count);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Strips_ids_that_mean_nothing_in_another_tenant()
    {
        var result = await Exporter.ExportAsync(SettingsCatalogTenant(), [SettingsCatalog]);

        var settings = Assert.Single(result.Items).Settings;
        Assert.False(settings.ContainsKey("id"));
        Assert.False(settings.ContainsKey("settingCount"));
        Assert.False(settings.ContainsKey("@odata.context"));
        foreach (var assignment in settings["assignments"]!.AsArray())
        {
            Assert.False(assignment!.AsObject().ContainsKey("id"));
            Assert.False(assignment.AsObject().ContainsKey("sourceId"));
        }
    }

    [Fact]
    public async Task Finds_assigned_groups_filters_and_custom_scope_tags()
    {
        var result = await Exporter.ExportAsync(SettingsCatalogTenant(), [SettingsCatalog]);

        var dependencies = Assert.Single(result.Items).Dependencies;
        Assert.Contains(dependencies, d => d is { TargetType: ResourceRegistry.Group, Value: PilotGroup });
        Assert.Contains(dependencies, d => d is { TargetType: ResourceRegistry.Group, Value: ExcludedGroup });
        Assert.Contains(dependencies, d => d is { TargetType: ResourceRegistry.AssignmentFilter, Value: WindowsFilter });
        Assert.Contains(dependencies, d => d is { TargetType: ResourceRegistry.ScopeTag, Value: "7" });

        // The default scope tag and the empty filter ID are the same everywhere.
        Assert.DoesNotContain(dependencies, d => d.Value is "0" or "00000000-0000-0000-0000-000000000000");
    }

    [Fact]
    public async Task Exporting_twice_gives_identical_json()
    {
        var first = await Exporter.ExportAsync(SettingsCatalogTenant(), [SettingsCatalog]);
        var second = await Exporter.ExportAsync(SettingsCatalogTenant(), [SettingsCatalog]);

        Assert.Equal(
            Normalizer.ToStableJson(first.Items[0].Settings),
            Normalizer.ToStableJson(second.Items[0].Settings));
    }

    [Fact]
    public async Task Skips_built_in_scope_tags()
    {
        var graph = new FakeGraph().Collection("beta/deviceManagement/roleScopeTags",
            """{ "id": "0", "displayName": "Default", "isBuiltIn": true }""",
            """{ "id": "7", "displayName": "Contoso EMEA", "isBuiltIn": false }""");

        var result = await Exporter.ExportAsync(graph, [ScopeTags]);

        Assert.Equal("Contoso EMEA", Assert.Single(result.Items).DisplayName);
    }

    [Fact]
    public async Task An_unreadable_type_becomes_a_warning_and_the_rest_still_export()
    {
        var graph = SettingsCatalogTenant().Failing("beta/deviceManagement/deviceCompliancePolicies");

        var result = await Exporter.ExportAsync(graph, [Compliance, SettingsCatalog]);

        Assert.Single(result.Items);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(Compliance, warning.Type);
        Assert.Contains("403", warning.Message);
    }

    [Fact]
    public async Task Items_are_sorted_by_name()
    {
        var graph = new FakeGraph().Collection("v1.0/identity/conditionalAccess/namedLocations",
            """{ "id": "b", "displayName": "Zurich office" }""",
            """{ "id": "a", "displayName": "amsterdam office" }""",
            """{ "id": "c", "displayName": "Madrid office" }""");

        var result = await Exporter.ExportAsync(graph, [ResourceRegistry.Get(ResourceRegistry.NamedLocation)]);

        Assert.Equal(new[] { "amsterdam office", "Madrid office", "Zurich office" }, result.Items.Select(i => i.DisplayName));
    }

    [Fact]
    public void Every_type_is_created_after_the_types_it_depends_on()
    {
        var order = ResourceRegistry.All.Select(t => t.Id).ToList();

        foreach (var type in ResourceRegistry.All)
            foreach (var dependency in type.DependsOn)
                Assert.True(order.IndexOf(dependency) < order.IndexOf(type.Id), $"{type.Id} is listed before {dependency}");
    }
}
