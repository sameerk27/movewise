using Movewise.Core.Export;
using Movewise.Core.Registry;

namespace Movewise.Core.Tests;

public class DependencyTests
{
    static readonly ResourceType ConditionalAccess = ResourceRegistry.Get(ResourceRegistry.ConditionalAccessPolicy);

    [Fact]
    public void Finds_groups_locations_apps_and_strengths()
    {
        var dependencies = DependencyExtractor.Extract(Samples.ConditionalAccessPolicy(), ConditionalAccess);

        Assert.Contains(dependencies, d => d is { TargetType: ResourceRegistry.Group, Value: Samples.AllStaffGroup });
        Assert.Contains(dependencies, d => d is { TargetType: ResourceRegistry.Group, Value: Samples.BreakGlassGroup });
        Assert.Contains(dependencies, d => d is { TargetType: ResourceRegistry.NamedLocation, Value: Samples.HqLocation });
        Assert.Contains(dependencies, d => d is { TargetType: ResourceRegistry.AuthenticationStrength, Value: Samples.CustomStrength });
        Assert.Contains(dependencies, d => d.TargetType == ResourceRegistry.Application);
    }

    [Fact]
    public void Ignores_values_that_mean_the_same_in_every_tenant()
    {
        var values = DependencyExtractor.Extract(Samples.ConditionalAccessPolicy(), ConditionalAccess).Select(d => d.Value);

        Assert.DoesNotContain("All", values);
        Assert.DoesNotContain("AllTrusted", values);
        Assert.DoesNotContain("GuestsOrExternalUsers", values);
    }

    [Fact]
    public void Built_in_authentication_strengths_are_not_dependencies()
    {
        var policy = Samples.ConditionalAccessPolicy();
        policy["grantControls"]!["authenticationStrength"]!["id"] = "00000000-0000-0000-0000-000000000002";

        var dependencies = DependencyExtractor.Extract(policy, ConditionalAccess);

        Assert.DoesNotContain(dependencies, d => d.TargetType == ResourceRegistry.AuthenticationStrength);
    }

    [Fact]
    public void Policy_needs_mapping_only_for_objects_outside_the_export()
    {
        var type = ConditionalAccess;
        var policy = Normalizer.Normalize(Samples.ConditionalAccessPolicy(), type);
        var item = new ExportedResource(type, "p1", "CA001", policy, DependencyExtractor.Extract(policy, type));

        // Groups are never part of an export, so this policy needs mapping.
        Assert.True(ExportAnalysis.NeedsMapping(item, [item]));

        policy["conditions"]!["users"]!["includeGroups"] = new System.Text.Json.Nodes.JsonArray();
        policy["conditions"]!["users"]!["excludeGroups"] = new System.Text.Json.Nodes.JsonArray();
        var location = new ExportedResource(ResourceRegistry.Get(ResourceRegistry.NamedLocation), Samples.HqLocation, "HQ", new(), []);
        var strength = new ExportedResource(ResourceRegistry.Get(ResourceRegistry.AuthenticationStrength), Samples.CustomStrength, "Strong", new(), []);
        var withoutGroups = item with { Dependencies = DependencyExtractor.Extract(policy, type) };

        // The location and strength are exported alongside it, and app IDs don't block.
        Assert.False(ExportAnalysis.NeedsMapping(withoutGroups, [withoutGroups, location, strength]));
    }

    [Fact]
    public void Describes_dependencies_in_plain_words()
    {
        var policy = Normalizer.Normalize(Samples.ConditionalAccessPolicy(), ConditionalAccess);
        var item = new ExportedResource(ConditionalAccess, "p1", "CA001", policy, DependencyExtractor.Extract(policy, ConditionalAccess));

        var text = ExportAnalysis.DescribeDependencies(item);

        Assert.Contains("2 groups", text);
        Assert.Contains("1 named location", text);
    }

    [Fact]
    public void An_on_or_off_value_is_not_a_recipient()
    {
        var dlp = ResourceRegistry.Get(ResourceRegistry.DlpPolicy);
        var policy = System.Text.Json.Nodes.JsonNode.Parse("""
            { "Name": "Block SS", "rules": [ { "Name": "r1", "GenerateAlert": ["true"], "NotifyUser": ["SiteAdmin", "alerts@contoso.com"] } ] }
            """)!.AsObject();

        var recipients = DependencyExtractor.Extract(policy, dlp).Where(d => d.TargetType == ResourceRegistry.Recipient).Select(d => d.Value);

        Assert.Equal(["alerts@contoso.com"], recipients);
    }
}
