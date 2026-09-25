using System.Text.Json.Nodes;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

/// <summary>Scripts, administrative templates, notification templates and other Intune objects with their own quirks.</summary>
public class IntuneExtrasTests
{
    const string DM = "beta/deviceManagement";

    static readonly TenantInfo Source = new("src-tenant", "Contoso", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false, [], [], []);
    static readonly TenantInfo Destination = new("dst-tenant", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false, [], [], []);

    static async Task<IReadOnlyList<ExportedResource>> Export(FakeGraph graph, params string[] types)
    {
        var result = await Exporter.ExportAsync(graph, types.Select(ResourceRegistry.Get));
        Assert.Empty(result.Warnings);
        return result.Items;
    }

    static DeployRun Plan(MappingPlan plan, params ExportedResource[] items) =>
        DeployPlanner.Plan(new PreflightReport(items.Select(i => new PolicyResult(Transformer.Transform(i, plan, new TransformOptions()), Outcome.Create, [])).ToList(), []),
            plan, Source, Destination);

    [Fact]
    public async Task A_script_is_read_with_its_content_and_assigned_with_its_own_property()
    {
        var source = new FakeGraph()
            .Collection($"{DM}/deviceManagementScripts", """{ "id": "s1", "displayName": "Set wallpaper" }""")
            .Object($"{DM}/deviceManagementScripts/s1", """{ "id": "s1", "displayName": "Set wallpaper", "scriptContent": "V3JpdGUtSG9zdA==", "runAsAccount": "system" }""")
            .Collection($"{DM}/deviceManagementScripts/s1/assignments",
                """{ "id": "a1", "target": { "@odata.type": "#microsoft.graph.allDevicesAssignmentTarget" } }""");
        var item = Assert.Single(await Export(source, ResourceRegistry.PowerShellScript));
        Assert.Equal("V3JpdGUtSG9zdA==", item.Settings["scriptContent"]!.GetValue<string>());

        var destination = new FakeGraph();
        var run = Plan(new MappingPlan([]), item);
        await Deployer.RunAsync(destination, run, () => Task.CompletedTask);

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        var assign = Assert.Single(destination.Posts, p => p.Path.EndsWith("/assign"));
        Assert.Equal($"{DM}/deviceManagementScripts/new-1/assign", assign.Path);
        Assert.NotNull(assign.Body["deviceManagementScriptAssignments"]);
    }

    [Fact]
    public async Task Microsoft_published_remediations_stay_out()
    {
        var source = new FakeGraph()
            .Collection($"{DM}/deviceHealthScripts",
                """{ "id": "r0", "displayName": "Restart stopped Office C2R svc", "isGlobalScript": true }""",
                """{ "id": "r1", "displayName": "Clear temp files", "isGlobalScript": false }""")
            .Object($"{DM}/deviceHealthScripts/r1", """{ "id": "r1", "displayName": "Clear temp files", "detectionScriptContent": "ZXhpdCAx" }""");

        Assert.Equal("Clear temp files", Assert.Single(await Export(source, ResourceRegistry.Remediation)).DisplayName);
    }

    [Fact]
    public async Task An_administrative_template_gets_its_settings_bound_to_the_built_in_definitions()
    {
        var source = new FakeGraph()
            .Collection($"{DM}/groupPolicyConfigurations", """{ "id": "g1", "displayName": "Edge homepage" }""")
            .Collection($"{DM}/groupPolicyConfigurations/g1/definitionValues?$expand=definition($select=id),presentationValues($expand=presentation($select=id))",
                """
                { "id": "dv1", "enabled": true, "definition": { "id": "def-1" },
                  "presentationValues": [ { "@odata.type": "#microsoft.graph.groupPolicyPresentationValueText", "id": "pv1", "value": "https://intranet", "presentation": { "id": "pres-1" } } ] }
                """);
        var item = Assert.Single(await Export(source, ResourceRegistry.AdministrativeTemplate));

        var destination = new FakeGraph();
        var run = Plan(new MappingPlan([]), item);
        await Deployer.RunAsync(destination, run, () => Task.CompletedTask);

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.False(destination.Posts[0].Body.AsObject().ContainsKey("definitionValues"));
        var update = Assert.Single(destination.Posts, p => p.Path.EndsWith("/updateDefinitionValues"));
        Assert.Equal($"{DM}/groupPolicyConfigurations/new-1/updateDefinitionValues", update.Path);
        var added = Assert.Single(update.Body["added"]!.AsArray())!;
        Assert.Equal("https://graph.microsoft.com/beta/deviceManagement/groupPolicyDefinitions('def-1')", added["definition@odata.bind"]!.GetValue<string>());
        var value = Assert.Single(added["presentationValues"]!.AsArray())!;
        Assert.Equal("https://intranet", value["value"]!.GetValue<string>());
        Assert.EndsWith("/presentations('pres-1')", value["presentation@odata.bind"]!.GetValue<string>());
        Assert.Null(value["id"]);
    }

    [Fact]
    public async Task A_compliance_policy_points_at_the_notification_template_created_before_it()
    {
        var template = new ExportedResource(ResourceRegistry.Get(ResourceRegistry.NotificationTemplate), "t1", "Noncompliant device",
            Normalizer.Normalize(JsonNode.Parse("""
                { "displayName": "Noncompliant device", "localizedNotificationMessages": [ { "id": "m1", "locale": "en-us", "subject": "Fix your device", "messageTemplate": "…", "isDefault": true } ] }
                """)!.AsObject(), ResourceRegistry.Get(ResourceRegistry.NotificationTemplate)), []);
        var complianceType = ResourceRegistry.Get(ResourceRegistry.CompliancePolicy);
        var complianceSettings = Normalizer.Normalize(JsonNode.Parse("""
            { "@odata.type": "#microsoft.graph.windows10CompliancePolicy", "displayName": "Windows", "scheduledActionsForRule": [ { "ruleName": "PasswordRequired",
              "scheduledActionConfigurations": [ { "actionType": "notification", "gracePeriodHours": 0, "notificationTemplateId": "t1" } ] } ] }
            """)!.AsObject(), complianceType);
        var compliance = new ExportedResource(complianceType, "c1", "Windows", complianceSettings, DependencyExtractor.Extract(complianceSettings, complianceType));
        Assert.Contains(compliance.Dependencies, d => d is { TargetType: ResourceRegistry.NotificationTemplate, Value: "t1" });

        var mapping = new Mapping.Mapping { TargetType = ResourceRegistry.NotificationTemplate, Source = new ObjectRef("t1", "Noncompliant device"), UsedBy = ["Windows"] };
        mapping.Resolve(MatchKind.CreatedByMigration, new ObjectRef("", "Noncompliant device"));
        var destination = new FakeGraph();
        var run = Plan(new MappingPlan([mapping]), compliance, template);
        await Deployer.RunAsync(destination, run, () => Task.CompletedTask);

        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
        var message = Assert.Single(destination.Posts, p => p.Path.EndsWith("/localizedNotificationMessages"));
        Assert.Equal($"{DM}/notificationMessageTemplates/new-1/localizedNotificationMessages", message.Path);
        Assert.Null(message.Body["id"]);
        var policy = Assert.Single(destination.Posts, p => p.Path == $"{DM}/deviceCompliancePolicies");
        Assert.Equal("new-1", policy.Body["scheduledActionsForRule"]![0]!["scheduledActionConfigurations"]![0]!["notificationTemplateId"]!.GetValue<string>());
    }

    [Fact]
    public void Intune_apps_are_matched_but_never_read_for_migrating()
    {
        Assert.DoesNotContain(ResourceRegistry.For(M365Service.Intune), t => t.Id == ResourceRegistry.IntuneApp);
        Assert.True(ResourceRegistry.Get(ResourceRegistry.IntuneApp).ReferenceOnly);
    }
}
