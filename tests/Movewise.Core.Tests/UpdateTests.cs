using System.Text.Json.Nodes;
using Movewise.Core.Access;
using Movewise.Core.Compare;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

/// <summary>Updating a policy already in the destination instead of creating it, and rolling that back.</summary>
public class UpdateTests
{
    const string CaPath = "v1.0/identity/conditionalAccess/policies";
    const string CatalogPath = "beta/deviceManagement/configurationPolicies";

    static readonly ResourceType ConditionalAccess = ResourceRegistry.Get(ResourceRegistry.ConditionalAccessPolicy);
    static readonly ResourceType SettingsCatalog = ResourceRegistry.Get(ResourceRegistry.SettingsCatalogPolicy);
    static readonly TenantInfo Source = new("src", "Contoso", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false, [], [], []);
    static readonly TenantInfo Destination = new("dst", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false,
        [DirectoryRoles.GlobalAdministrator], [], []) { ServicePlans = new HashSet<string>(["AAD_PREMIUM", "INTUNE_A"], StringComparer.OrdinalIgnoreCase) };

    static string Ca(string id, string name, string state, string control) => $$"""
        { "id": "{{id}}", "displayName": "{{name}}", "state": "{{state}}",
          "conditions": { "users": { "includeUsers": ["All"] }, "applications": { "includeApplications": ["All"] } },
          "grantControls": { "operator": "OR", "builtInControls": ["{{control}}"] } }
        """;

    static ExportedResource Item(ResourceType type, string json, string id)
    {
        var settings = Normalizer.Normalize(JsonNode.Parse(json)!.AsObject(), type);
        return new ExportedResource(type, id, settings[type.IdentityProperty]!.GetValue<string>(), settings, DependencyExtractor.Extract(settings, type));
    }

    static Task<PreflightReport> Preflight(FakeGraph destination, ExportedResource item, PreflightChoices choices) =>
        PreflightCheck.RunAsync(new TenantClients(destination), Destination, [item], new MappingPlan([]), choices);

    [Fact]
    public async Task A_name_conflict_can_update_the_existing_policy_and_rollback_puts_it_back()
    {
        var existing = Ca("dst-1", "CA001 MFA", "enabled", "compliantDevice");
        var destination = new FakeGraph().Collection(CaPath, existing).Object($"{CaPath}/dst-1", existing);
        var source = Item(ConditionalAccess, Ca("src-1", "CA001 MFA", "enabled", "mfa"), "src-1");

        var report = await Preflight(destination, source, new PreflightChoices { NameConflicts = ConflictChoice.Update });

        var policy = Assert.Single(report.Policies);
        Assert.Equal(Outcome.Create, policy.Outcome);
        Assert.Equal("dst-1", policy.UpdatesId);
        Assert.Equal(["grantControls"], policy.ChangedSettings);
        Assert.Contains(report.Findings, f => f.Title == "Policies that will be updated in the destination");

        var run = DeployPlanner.Plan(report, new MappingPlan([]), Source, Destination);
        await Deployer.RunAsync(destination, run, () => Task.CompletedTask);

        var step = Assert.Single(run.Steps);
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Empty(destination.Posts);
        var (path, body) = Assert.Single(destination.Patches);
        Assert.Equal($"{CaPath}/dst-1", path);
        Assert.Equal(["grantControls"], body.AsObject().Select(p => p.Key));
        // An existing policy keeps the source's state: it isn't moved to report-only.
        Assert.Equal("enabled", policy.Policy.Desired["state"]!.GetValue<string>());

        await Deployer.RollbackAsync(new TenantClients(destination), run, () => Task.CompletedTask);

        Assert.Empty(destination.Deletes);
        Assert.Equal("compliantDevice", destination.Patches.Last().Body["grantControls"]!["builtInControls"]![0]!.GetValue<string>());
        Assert.Equal(StepStatus.RolledBack, step.Status);
        Assert.Equal("Put back as it was.", step.Message);
    }

    [Fact]
    public async Task A_policy_already_the_same_is_skipped_and_skipping_stays_the_default()
    {
        var existing = Ca("dst-1", "CA001 MFA", "enabled", "mfa");
        var destination = new FakeGraph().Collection(CaPath, existing);
        var source = Item(ConditionalAccess, Ca("src-1", "CA001 MFA", "enabled", "mfa"), "src-1");

        var same = await Preflight(destination, source, new PreflightChoices { NameConflicts = ConflictChoice.Update });
        Assert.Equal(Outcome.Skip, Assert.Single(same.Policies).Outcome);
        Assert.Contains("Already the same in the destination.", same.Policies[0].Reasons);

        var byDefault = await Preflight(destination, source, new PreflightChoices());
        Assert.Null(Assert.Single(byDefault.Policies).UpdatesId);
        Assert.Equal(Outcome.Skip, byDefault.Policies[0].Outcome);
    }

    [Fact]
    public async Task A_settings_catalog_policy_is_replaced_whole_with_put()
    {
        const string existing = """
            { "id": "sc-1", "name": "BitLocker", "platforms": "windows10", "technologies": "mdm",
              "settings": [ { "id": "0", "settingInstance": { "settingDefinitionId": "bitlocker_requiredeviceencryption", "choiceSettingValue": { "value": "off" } } } ] }
            """;
        var destination = new FakeGraph()
            .Collection(CatalogPath, existing)
            .Object($"{CatalogPath}('sc-1')?$expand=settings", existing)
            .Collection($"{CatalogPath}('sc-1')/assignments")
            .Object($"{CatalogPath}/sc-1", existing);
        var source = Item(SettingsCatalog, existing.Replace("\"off\"", "\"on\"").Replace("sc-1", "src-sc"), "src-sc");

        var report = await Preflight(destination, source, new PreflightChoices { NameConflicts = ConflictChoice.Update });
        Assert.Equal(["settings"], Assert.Single(report.Policies).ChangedSettings);

        var run = DeployPlanner.Plan(report, new MappingPlan([]), Source, Destination);
        await Deployer.RunAsync(destination, run, () => Task.CompletedTask);

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        var (path, body) = Assert.Single(destination.Puts);
        Assert.Equal($"{CatalogPath}/sc-1", path);
        Assert.Equal("BitLocker", body["name"]!.GetValue<string>());
        Assert.Equal("on", body["settings"]![0]!["settingInstance"]!["choiceSettingValue"]!["value"]!.GetValue<string>());
        Assert.Null(body["settings"]![0]!["id"]);
        Assert.Empty(destination.Patches);

        await Deployer.RollbackAsync(new TenantClients(destination), run, () => Task.CompletedTask);
        Assert.Equal("off", destination.Puts.Last().Body["settings"]![0]!["settingInstance"]!["choiceSettingValue"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Restoring_puts_back_a_policy_renamed_and_changed_since_by_its_id()
    {
        var then = new FakeGraph().Collection(CaPath, Ca("p1", "CA001 MFA", "enabled", "mfa"));
        var snapshot = new Snapshot("dst", "Fabrikam", DateTimeOffset.Now.AddDays(-1), [ConditionalAccess],
            (await Exporter.ExportAsync(then, [ConditionalAccess])).Items);
        var changed = Ca("p1", "CA001 renamed", "disabled", "mfa");
        var now = new FakeGraph().Collection(CaPath, changed).Object($"{CaPath}/p1", changed);

        var drift = await TenantComparer.CompareWithSnapshotAsync(snapshot, new TenantClients(new ReadOnlyGraph(now)), "Fabrikam");
        var item = Assert.Single(drift.Items);
        Assert.Equal(Difference.Different, item.Difference);
        Assert.True(item.CanRestore);

        var plan = await Matcher.BuildAsync(new ReadOnlyGraph(now), new TenantClients(now), [item.FirstSource!], snapshot.Items);
        var report = await PreflightCheck.RunAsync(new TenantClients(now), Destination, [item.FirstSource!], plan,
            new PreflightChoices { UpdateById = true, KeepOriginalState = true });
        var policy = Assert.Single(report.Policies);
        Assert.Equal("p1", policy.UpdatesId);

        var run = DeployPlanner.Plan(report, plan, Destination, Destination);
        await Deployer.RunAsync(now, run, () => Task.CompletedTask);

        var body = Assert.Single(now.Patches).Body;
        Assert.Equal("CA001 MFA", body["displayName"]!.GetValue<string>());
        Assert.Equal("enabled", body["state"]!.GetValue<string>());
        Assert.Empty(now.Posts);
    }

    [Fact]
    public async Task Kinds_that_cant_be_updated_in_place_are_skipped_when_update_is_chosen()
    {
        var strength = ResourceRegistry.Get(ResourceRegistry.AuthenticationStrength);
        var destination = new FakeGraph().Collection(strength.ListPath,
            """{ "id": "s-dst", "displayName": "Phishing-resistant plus", "allowedCombinations": ["fido2"] }""");
        var source = Item(strength, """{ "id": "s-src", "displayName": "Phishing-resistant plus", "allowedCombinations": ["fido2", "windowsHelloForBusiness"] }""", "s-src");

        var report = await Preflight(destination, source, new PreflightChoices { NameConflicts = ConflictChoice.Update });

        var policy = Assert.Single(report.Policies);
        Assert.Equal(Outcome.Skip, policy.Outcome);
        Assert.Null(policy.UpdatesId);
        Assert.Contains(policy.Reasons, r => r.Contains("can't be changed in place"));
    }

    // ---------- PowerShell policies ----------

    static readonly ResourceType AntiPhish = ResourceRegistry.Get(ResourceRegistry.AntiPhishPolicy);

    static FakePowerShell PhishDestination() => new FakePowerShell()
        .Returns("Get-AntiPhishPolicy", """{ "Name": "Execs", "Guid": "ap-dst", "Identity": "Execs", "PhishThresholdLevel": 1, "EnableMailboxIntelligence": false }""")
        .Returns("Get-AntiPhishRule", """{ "Name": "Execs", "AntiPhishPolicy": "Execs", "State": "Enabled", "Comments": "Old" }""")
        .Accepts("Set-AntiPhishPolicy", "Identity", "Name", "PhishThresholdLevel", "EnableMailboxIntelligence")
        .Accepts("Set-AntiPhishRule", "Identity", "Name", "Comments")
        .Accepts("New-AntiPhishRule", "Name", "AntiPhishPolicy", "Comments");

    static ExportedResource PhishSource() => Item(AntiPhish, """
        { "Name": "Execs", "PhishThresholdLevel": 3, "EnableMailboxIntelligence": false,
          "rules": [ { "Name": "Execs", "Enabled": true, "Comments": "Executives and the board" },
                     { "Name": "Execs - assistants", "Enabled": true, "Comments": "Their assistants" } ] }
        """, "ap-src");

    [Fact]
    public async Task A_powershell_policy_is_updated_with_set_its_rules_changed_or_added_and_rollback_undoes_all_of_it()
    {
        var shell = PhishDestination();
        var tenant = new TenantClients(new FakeGraph(), Exchange: shell);

        var report = await PreflightCheck.RunAsync(tenant, Destination, [PhishSource()], new MappingPlan([]), new PreflightChoices { NameConflicts = ConflictChoice.Update });
        var policy = Assert.Single(report.Policies);
        Assert.Equal("ap-dst", policy.UpdatesId);
        Assert.Equal(["PhishThresholdLevel", "rules"], policy.ChangedSettings);

        var run = DeployPlanner.Plan(report, new MappingPlan([]), Source, Destination);
        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);

        var step = Assert.Single(run.Steps);
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Empty(shell.CallsTo("New-AntiPhishPolicy"));
        var set = Assert.Single(shell.CallsTo("Set-AntiPhishPolicy"));
        Assert.Equal("ap-dst", set["Identity"]!.GetValue<string>());
        Assert.Equal(3, set["PhishThresholdLevel"]!.GetValue<int>());
        Assert.False(set.ContainsKey("Name"));
        var ruleSet = Assert.Single(shell.CallsTo("Set-AntiPhishRule"));
        Assert.Equal("Execs", ruleSet["Identity"]!.GetValue<string>());
        Assert.Equal("Executives and the board", ruleSet["Comments"]!.GetValue<string>());
        var ruleNew = Assert.Single(shell.CallsTo("New-AntiPhishRule"));
        Assert.Equal("Execs - assistants", ruleNew["Name"]!.GetValue<string>());
        // Attached to the existing policy by its ID, which an update never renames.
        Assert.Equal("ap-dst", ruleNew["AntiPhishPolicy"]!.GetValue<string>());

        await Deployer.RollbackAsync(tenant, run, () => Task.CompletedTask);

        Assert.Equal(1, shell.CallsTo("Set-AntiPhishPolicy").Last()["PhishThresholdLevel"]!.GetValue<int>());
        Assert.Equal("Old", shell.CallsTo("Set-AntiPhishRule").Last()["Comments"]!.GetValue<string>());
        Assert.Equal("Execs - assistants", Assert.Single(shell.CallsTo("Remove-AntiPhishRule"))["Identity"]!.GetValue<string>());
        Assert.Empty(shell.CallsTo("Remove-AntiPhishPolicy"));
        Assert.Equal(StepStatus.RolledBack, step.Status);
    }

    [Fact]
    public async Task Settings_a_set_cmdlet_doesnt_take_are_named_and_left_as_they_are()
    {
        var shell = PhishDestination().Accepts("Set-AntiPhishPolicy", "Identity", "PhishThresholdLevel");
        var source = Item(AntiPhish, """{ "Name": "Execs", "PhishThresholdLevel": 1, "EnableMailboxIntelligence": true }""", "ap-src");

        var report = await PreflightCheck.RunAsync(new TenantClients(new FakeGraph(), Exchange: shell), Destination, [source], new MappingPlan([]),
            new PreflightChoices { NameConflicts = ConflictChoice.Update });

        var policy = Assert.Single(report.Policies);
        Assert.Equal(Outcome.Skip, policy.Outcome);
        Assert.Contains(policy.Reasons, r => r.Contains("EnableMailboxIntelligence"));
    }
}
