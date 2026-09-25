using System.Text.Json.Nodes;
using Movewise.Core.Export;
using Movewise.Core.Registry;

namespace Movewise.Core.Tests;

public class NormalizerTests
{
    static readonly ResourceType ConditionalAccess = ResourceRegistry.Get(ResourceRegistry.ConditionalAccessPolicy);

    [Fact]
    public void Removes_read_only_properties_and_odata_noise()
    {
        var result = Normalizer.Normalize(Samples.ConditionalAccessPolicy(), ConditionalAccess);

        Assert.False(result.ContainsKey("id"));
        Assert.False(result.ContainsKey("createdDateTime"));
        Assert.False(result.ContainsKey("modifiedDateTime"));
        Assert.False(result.ContainsKey("templateId"));
        Assert.False(result.ContainsKey("@odata.context"));
        Assert.Equal("CA001 - Require MFA for all users", result["displayName"]!.GetValue<string>());
    }

    [Fact]
    public void Keeps_odata_type_because_create_needs_it()
    {
        var raw = JsonNode.Parse("""{ "@odata.type": "#microsoft.graph.ipNamedLocation", "@odata.etag": "W/1", "id": "x", "displayName": "HQ" }""")!.AsObject();

        var result = Normalizer.Normalize(raw, ResourceRegistry.Get(ResourceRegistry.NamedLocation));

        Assert.Equal("#microsoft.graph.ipNamedLocation", result["@odata.type"]!.GetValue<string>());
        Assert.False(result.ContainsKey("@odata.etag"));
    }

    [Fact]
    public void Same_settings_in_a_different_key_order_give_identical_json()
    {
        var a = JsonNode.Parse("""{ "displayName": "P", "state": "enabled", "conditions": { "b": 1, "a": [ { "y": 2, "x": 1 } ] } }""")!.AsObject();
        var b = JsonNode.Parse("""{ "conditions": { "a": [ { "x": 1, "y": 2 } ], "b": 1 }, "state": "enabled", "displayName": "P" }""")!.AsObject();

        Assert.Equal(
            Normalizer.ToStableJson(Normalizer.Normalize(a, ConditionalAccess)),
            Normalizer.ToStableJson(Normalizer.Normalize(b, ConditionalAccess)));
    }

    [Fact]
    public void Normalizing_twice_changes_nothing()
    {
        var once = Normalizer.Normalize(Samples.ConditionalAccessPolicy(), ConditionalAccess);
        var twice = Normalizer.Normalize(once, ConditionalAccess);

        Assert.Equal(Normalizer.ToStableJson(once), Normalizer.ToStableJson(twice));
    }

    [Fact]
    public void Does_not_modify_the_input()
    {
        var raw = Samples.ConditionalAccessPolicy();

        Normalizer.Normalize(raw, ConditionalAccess);

        Assert.True(raw.ContainsKey("id"));
    }
}
