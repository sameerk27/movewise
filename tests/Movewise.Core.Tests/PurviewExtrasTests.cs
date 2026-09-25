using System.Text.Json.Nodes;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

/// <summary>Retention labels, custom sensitive information types, auto-labeling, alert and audit retention policies.</summary>
public class PurviewExtrasTests
{
    static readonly TenantInfo Source = new("src-tenant", "Contoso", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false, [], [], []);
    static readonly TenantInfo Destination = new("dst-tenant", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false, [], [], []);

    static async Task<IReadOnlyList<ExportedResource>> Export(FakePowerShell shell, params string[] types)
    {
        var result = await Exporter.ExportAsync(new TenantClients(new FakeGraph(), Compliance: shell), types.Select(ResourceRegistry.Get));
        Assert.Empty(result.Warnings);
        return result.Items;
    }

    static async Task<(DeployRun Run, FakePowerShell Destination)> Deploy(params ExportedResource[] items)
    {
        var shell = new FakePowerShell();
        var plan = new MappingPlan([]);
        var run = DeployPlanner.Plan(new PreflightReport(items.Select(i => new PolicyResult(Transformer.Transform(i, plan, new TransformOptions()), Outcome.Create, [])).ToList(), []),
            plan, Source, Destination);
        await Deployer.RunAsync(new TenantClients(new FakeGraph(), Compliance: shell), run, () => Task.CompletedTask);
        return (run, shell);
    }

    [Fact]
    public async Task A_custom_sensitive_information_type_package_is_created_from_its_file_and_microsofts_stay_out()
    {
        var source = new FakePowerShell().Returns("Get-DlpSensitiveInformationTypeRulePackage",
            """{ "Identity": "p0", "RuleCollectionName": "Microsoft Rule Package", "Publisher": "Microsoft Corporation", "SerializedClassificationRuleCollection": [1] }""",
            """{ "Identity": "p1", "RuleCollectionName": "Contoso employee IDs", "Publisher": "Contoso", "SerializedClassificationRuleCollection": [60, 63, 120] }""");

        var item = Assert.Single(await Export(source, ResourceRegistry.SensitiveInfoTypePackage));
        var (run, destination) = await Deploy(item);

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        var created = Assert.Single(destination.CallsTo("New-DlpSensitiveInformationTypeRulePackage"));
        Assert.False(created.ContainsKey("Name"));
        Assert.Equal([60, 63, 120], created["FileData"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.False(created.ContainsKey("SerializedClassificationRuleCollection"));
    }

    [Fact]
    public async Task An_audit_retention_policy_keeps_its_priority()
    {
        var source = new FakePowerShell().Returns("Get-UnifiedAuditLogRetentionPolicy",
            """{ "Name": "Keep Exchange audit 3 years", "Guid": "a1", "RecordTypes": ["ExchangeAdmin"], "RetentionDuration": "ThreeYears", "Priority": 100 }""");

        var (run, destination) = await Deploy(Assert.Single(await Export(source, ResourceRegistry.AuditRetentionPolicy)));

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Equal(100, Assert.Single(destination.CallsTo("New-UnifiedAuditLogRetentionPolicy"))["Priority"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_auto_labeling_policy_is_created_in_simulation_with_its_rules()
    {
        var source = new FakePowerShell()
            .Returns("Get-AutoSensitivityLabelPolicy", """{ "Name": "Label credit cards", "Guid": "al1", "Mode": "Enable", "ApplySensitivityLabel": "Confidential", "ExchangeLocation": ["All"] }""")
            .Returns("Get-AutoSensitivityLabelRule|Label credit cards", """{ "Name": "Cards", "ParentPolicyName": "Label credit cards", "Workload": "Exchange" }""");

        var item = Assert.Single(await Export(source, ResourceRegistry.AutoLabelPolicy));
        var result = Transformer.Transform(item, new MappingPlan([]), new TransformOptions());

        Assert.Equal("TestWithoutNotifications", result.Desired["Mode"]!.GetValue<string>());
        Assert.Contains(item.Dependencies, d => d is { TargetType: ResourceRegistry.SensitivityLabel, Value: "Confidential" });
        Assert.Single(item.Settings["rules"]!.AsArray());
    }

    [Fact]
    public async Task System_alert_policies_stay_out()
    {
        var source = new FakePowerShell().Returns("Get-ProtectionAlert",
            """{ "Name": "Elevation of Exchange admin privilege", "IsSystemRule": true }""",
            """{ "Name": "Mass download", "IsSystemRule": false, "NotifyUser": ["secops@contoso.com"], "Severity": "High" }""");

        var item = Assert.Single(await Export(source, ResourceRegistry.AlertPolicy));

        Assert.Equal("Mass download", item.DisplayName);
        Assert.Contains(item.Dependencies, d => d is { TargetType: ResourceRegistry.Recipient, Value: "secops@contoso.com" });
    }

    [Fact]
    public void Retention_labels_are_created_before_retention_policies()
    {
        var order = ResourceRegistry.All.Select(t => t.Id).ToList();
        Assert.True(order.IndexOf(ResourceRegistry.RetentionLabel) < order.IndexOf(ResourceRegistry.RetentionPolicy));
        Assert.True(order.IndexOf(ResourceRegistry.SensitiveInfoTypePackage) < order.IndexOf(ResourceRegistry.DlpPolicy));
        Assert.True(ResourceRegistry.Get(ResourceRegistry.RetentionLabel).IdIsName);
    }
}
