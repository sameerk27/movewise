using System.Text.Json.Nodes;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

public class SensitivityLabelTests
{
    static readonly TenantInfo Source = new("src-tenant", "Contoso", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false, [], [], []);
    static readonly TenantInfo Destination = new("dst-tenant", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false, [], [], []);

    const string ParentGuid = "11111111-0000-4000-8000-000000000001";
    const string ChildGuid = "11111111-0000-4000-8000-000000000002";

    // What Get-Label returns: actions as JSON strings, advanced settings as "[key, value]".
    static string Label(string guid, string name, string? parent = null) => $$"""
        {
          "Guid": "{{guid}}", "Name": "{{name}}", "DisplayName": "{{name}} (display)", "Tooltip": "Tip", "ContentType": "File, Email",
          "ParentId": {{(parent is null ? "null" : $"\"{parent}\"")}}, "Priority": 3, "IsParent": {{(parent is null ? "true" : "false")}},
          "LabelActions": [
            "{\"Type\":\"encrypt\",\"SubType\":null,\"Settings\":[{\"Key\":\"disabled\",\"Value\":\"false\"},{\"Key\":\"protectiontype\",\"Value\":\"template\"},{\"Key\":\"offlineaccessdays\",\"Value\":\"7\"},{\"Key\":\"rightsdefinitions\",\"Value\":\"[{\\\"Identity\\\":\\\"finance@contoso.com\\\",\\\"Rights\\\":\\\"VIEW,EDIT\\\"},{\\\"Identity\\\":\\\"AuthenticatedUsers\\\",\\\"Rights\\\":\\\"VIEW\\\"}]\"},{\"Key\":\"newthing\",\"Value\":\"x\"}]}",
            "{\"Type\":\"applycontentmarking\",\"SubType\":\"header\",\"Settings\":[{\"Key\":\"text\",\"Value\":\"Confidential\"},{\"Key\":\"fontsize\",\"Value\":\"10\"},{\"Key\":\"alignment\",\"Value\":\"center\"},{\"Key\":\"placement\",\"Value\":\"Header\"}]}",
            "{\"Type\":\"applydynamicwatermarking\",\"SubType\":null,\"Settings\":[]}"
          ],
          "Settings": [ "[color, #FF0000]", "[isparent, True]", "[defaultsublabelid, {{ChildGuid}}]" ]
        }
        """;

    [Fact]
    public void Turns_label_actions_into_new_label_parameters()
    {
        var label = JsonNode.Parse(Label(ParentGuid, "Confidential"))!.AsObject();

        SensitivityLabels.FromGetLabel(label);

        Assert.True(label["EncryptionEnabled"]!.GetValue<bool>());
        Assert.Equal("Template", label["EncryptionProtectionType"]!.GetValue<string>());
        Assert.Equal(7, label["EncryptionOfflineAccessDays"]!.GetValue<int>());
        Assert.Equal("finance@contoso.com", label["EncryptionRightsDefinitions"]![0]!["Identity"]!.GetValue<string>());
        Assert.True(label["ApplyContentMarkingHeaderEnabled"]!.GetValue<bool>());
        Assert.Equal("Confidential", label["ApplyContentMarkingHeaderText"]!.GetValue<string>());
        Assert.Equal(10, label["ApplyContentMarkingHeaderFontSize"]!.GetValue<int>());
        Assert.Equal("Center", label["ApplyContentMarkingHeaderAlignment"]!.GetValue<string>());
        Assert.Equal("#FF0000", label["AdvancedSettings"]!["Color"]!.GetValue<string>());
        Assert.Single(label["AdvancedSettings"]!.AsObject());

        // Reserved for Microsoft: never sent.
        Assert.False(label.ContainsKey("LabelActions"));
        Assert.False(label.ContainsKey("Settings"));

        var notCopied = label[SensitivityLabels.NotCopied]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("encryption: newthing", notCopied);
        Assert.Contains("applydynamicwatermarking settings", notCopied);
        Assert.Contains(notCopied, n => n.Contains("DefaultSubLabelId"));
    }

    [Fact]
    public void Sends_encryption_rights_in_the_format_new_label_takes()
    {
        var label = JsonNode.Parse("""{ "EncryptionRightsDefinitions": [ { "Identity": "a@fabrikam.com", "Rights": "VIEW,EDIT" }, { "Identity": "fabrikam.com", "Rights": "VIEW" } ] }""")!.AsObject();

        SensitivityLabels.PrepareLabelForCreate(label);

        Assert.Equal("a@fabrikam.com:VIEW,EDIT;fabrikam.com:VIEW", label["EncryptionRightsDefinitions"]!.GetValue<string>());
    }

    static FakePowerShell Purview() => new FakePowerShell()
        .Returns("Get-Label", Label(ChildGuid, "Confidential - Finance", ParentGuid), Label(ParentGuid, "Z Confidential"))
        .Returns("Get-LabelPolicy", """{ "Name": "Everyone", "Guid": "p1", "Labels": ["Z Confidential", "Confidential - Finance"], "ExchangeLocation": [ { "Name": "All" } ], "Settings": [ "[mandatory, true]" ] }""");

    static async Task<IReadOnlyList<ExportedResource>> ExportLabels(FakePowerShell shell)
    {
        var result = await Exporter.ExportAsync(new TenantClients(new FakeGraph(), Compliance: shell),
            [ResourceRegistry.Get(ResourceRegistry.SensitivityLabel), ResourceRegistry.Get(ResourceRegistry.LabelPolicy)]);
        Assert.Empty(result.Warnings);
        return result.Items;
    }

    [Fact]
    public async Task A_label_policy_that_names_its_labels_points_at_their_ids()
    {
        var items = await ExportLabels(Purview());

        var policy = items.Single(i => i.Type.Id == ResourceRegistry.LabelPolicy);
        Assert.Equal(new[] { ParentGuid, ChildGuid }, policy.Settings["Labels"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray());
        Assert.Equal(2, policy.Dependencies.Count(d => d.TargetType == ResourceRegistry.SensitivityLabel));
        Assert.Equal("true", policy.Settings["AdvancedSettings"]!["mandatory"]!.GetValue<string>());

        var child = items.Single(i => i.SourceId == ChildGuid);
        Assert.Contains(child.Dependencies, d => d.TargetType == ResourceRegistry.SensitivityLabel && d.Value == ParentGuid);
    }

    [Fact]
    public async Task Creates_a_parent_label_before_its_sublabel_and_points_the_sublabel_at_it()
    {
        var items = await ExportLabels(Purview());
        var labels = items.Where(i => i.Type.Id == ResourceRegistry.SensitivityLabel).ToList();
        var plan = new MappingPlan(labels.SelectMany(l => l.Dependencies)
            .Where(d => d.TargetType == ResourceRegistry.SensitivityLabel)
            .DistinctBy(d => d.Value)
            .Select(d =>
            {
                var mapping = new Mapping.Mapping { TargetType = d.TargetType, Source = new ObjectRef(d.Value, d.Value), UsedBy = [] };
                mapping.Resolve(MatchKind.CreatedByMigration, new ObjectRef("", d.Value));
                return mapping;
            })
            .Concat(labels.SelectMany(l => l.Dependencies).Where(d => d.TargetType == ResourceRegistry.Recipient).DistinctBy(d => d.Value).Select(d =>
            {
                var mapping = new Mapping.Mapping { TargetType = d.TargetType, Source = new ObjectRef(d.Value, d.Value), UsedBy = [] };
                mapping.Resolve(MatchKind.SameMail, new ObjectRef("finance@fabrikam.com", "Finance"));
                return mapping;
            })));
        var report = new PreflightReport(labels.Select(l => new PolicyResult(Transformer.Transform(l, plan, new TransformOptions()), Outcome.Create, [])).ToList(), []);
        var run = DeployPlanner.Plan(report, plan, Source, Destination);

        // Alphabetically the sublabel comes first; it has to wait for its parent.
        Assert.Equal(new[] { "Z Confidential", "Confidential - Finance" }, run.Steps.Select(s => s.DisplayName).ToArray());

        var shell = new FakePowerShell();
        await Deployer.RunAsync(new TenantClients(new FakeGraph(), Compliance: shell), run, () => Task.CompletedTask);

        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
        var created = shell.CallsTo("New-Label").ToList();
        Assert.False(created[0].ContainsKey("ParentId"));
        Assert.Equal(run.Steps[0].DestinationId, created[1]["ParentId"]!.GetValue<string>());
        Assert.Equal("finance@fabrikam.com:VIEW,EDIT;AuthenticatedUsers:VIEW", created[0]["EncryptionRightsDefinitions"]!.GetValue<string>());
        Assert.All(created, c => Assert.False(c.ContainsKey("LabelActions")));
        Assert.Contains(run.Steps[0].Notes, n => n.Contains("encryption: newthing"));
    }

    [Fact]
    public async Task A_whole_domain_in_encryption_rights_follows_the_domains_match()
    {
        const string domains = "v1.0/domains?$select=id,isVerified,isInitial,isDefault";
        var source = new FakeGraph().Collection(domains, """{ "id": "contoso.com", "isVerified": true }""");
        var destination = new FakeGraph().Collection(domains, """{ "id": "fabrikam.com", "isVerified": true }""");
        var policy = new ExportedResource(ResourceRegistry.Get(ResourceRegistry.SensitivityLabel), "l1", "Confidential", new JsonObject(),
            [new Dependency(ResourceRegistry.Recipient, "contoso.com", "x"), new Dependency(ResourceRegistry.Recipient, "partner.example", "x")]);
        var decided = new Mapping.Mapping { TargetType = ResourceRegistry.Domain, Source = new ObjectRef("contoso.com", "contoso.com"), UsedBy = [] };
        decided.MapTo(new ObjectRef("fabrikam.com", "fabrikam.com"));

        var plan = await Matcher.BuildAsync(source, destination, [policy], [policy], new MappingPlan([decided]));

        var own = plan.Find(ResourceRegistry.Recipient, "contoso.com")!;
        Assert.Equal((MatchKind.SameName, "fabrikam.com"), (own.Kind, own.Destination!.Id));
        Assert.Equal(MatchKind.External, plan.Find(ResourceRegistry.Recipient, "partner.example")!.Kind);
    }
}
