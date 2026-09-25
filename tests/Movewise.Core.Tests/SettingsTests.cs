using System.Text.Json.Nodes;
using Movewise.Core.Access;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

/// <summary>Settings every tenant has, which are changed to match the source rather than created.</summary>
public class SettingsTests
{
    static readonly ResourceType AntiSpam = ResourceRegistry.Get(ResourceRegistry.DefaultAntiSpam);
    static readonly ResourceType StandardPreset = ResourceRegistry.Get(ResourceRegistry.StandardPresetEop);

    static readonly TenantInfo Source = new("src-tenant", "Contoso", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false, [], [], []);
    static readonly TenantInfo Destination = new("dst-tenant", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false,
        [DirectoryRoles.GlobalAdministrator], [], []);

    const string SourceDefault = """{ "Name": "Default", "Identity": "Default", "Guid": "s0", "IsDefault": true, "BulkThreshold": 5, "SpamAction": "Quarantine", "AllowedSenders": [] }""";
    const string SourceCustom = """{ "Name": "Finance", "Guid": "s1", "BulkThreshold": 4 }""";

    static FakePowerShell DestinationWith(string defaultPolicy) => new FakePowerShell()
        .Returns("Get-HostedContentFilterPolicy", defaultPolicy)
        .Accepts("Set-HostedContentFilterPolicy", "Identity", "Name", "BulkThreshold", "SpamAction", "AllowedSenders");

    static async Task<ExportedResource> ExportDefault()
    {
        var source = new FakePowerShell().Returns("Get-HostedContentFilterPolicy", SourceDefault, SourceCustom);
        var result = await Exporter.ExportAsync(new TenantClients(new FakeGraph(), Exchange: source), [AntiSpam]);
        return Assert.Single(result.Items);
    }

    static Task<PreflightReport> Preflight(ExportedResource item, FakePowerShell destination) =>
        PreflightCheck.RunAsync(new TenantClients(new FakeGraph(), Exchange: destination), Destination, [item], new MappingPlan([]), new PreflightChoices());

    [Fact]
    public async Task Reads_the_default_policy_as_settings_known_by_their_type()
    {
        var item = await ExportDefault();

        Assert.Equal(ResourceRegistry.DefaultAntiSpam, item.SourceId);
        Assert.Equal("Default anti-spam policy", item.DisplayName);
        Assert.Equal(5, item.Settings["BulkThreshold"]!.GetValue<int>());
    }

    [Fact]
    public async Task Preflight_changes_only_the_settings_that_differ()
    {
        var destination = DestinationWith("""{ "Name": "Default", "Identity": "Default", "IsDefault": true, "BulkThreshold": 7, "SpamAction": "Quarantine", "AllowedSenders": null }""");

        var report = await Preflight(await ExportDefault(), destination);

        var policy = Assert.Single(report.Policies);
        Assert.Equal(Outcome.Create, policy.Outcome);
        Assert.Equal(["BulkThreshold"], policy.ChangedSettings);
        Assert.Equal(7, policy.Current!["BulkThreshold"]!.GetValue<int>());
        Assert.Contains(report.Findings, f => f.Title == "Settings that will be changed in the destination");
        Assert.DoesNotContain(report.Findings, f => f.Title.StartsWith("Name conflicts"));
    }

    [Fact]
    public async Task Settings_already_the_same_are_skipped()
    {
        var destination = DestinationWith("""{ "Name": "Default", "IsDefault": true, "BulkThreshold": 5, "SpamAction": "quarantine" }""");

        var report = await Preflight(await ExportDefault(), destination);

        Assert.Equal(Outcome.Skip, Assert.Single(report.Policies).Outcome);
    }

    [Fact]
    public async Task Settings_the_destination_doesnt_have_block()
    {
        var report = await Preflight(await ExportDefault(), new FakePowerShell());

        Assert.Equal(Outcome.Blocked, Assert.Single(report.Policies).Outcome);
        Assert.Contains(report.Findings, f => f.Severity == Severity.Blocker && f.Title == "Default anti-spam policy isn't in the destination");
    }

    [Fact]
    public async Task Deploy_sets_what_differs_and_rollback_puts_the_old_values_back()
    {
        var destination = DestinationWith("""{ "Name": "Default", "Identity": "Default", "IsDefault": true, "BulkThreshold": 7, "SpamAction": "MoveToJmf" }""");
        var tenant = new TenantClients(new FakeGraph(), Exchange: destination);
        var report = await Preflight(await ExportDefault(), destination);
        var run = DeployPlanner.Plan(report, new MappingPlan([]), Source, Destination);

        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);

        var step = Assert.Single(run.Steps);
        Assert.Equal(StepStatus.Done, step.Status);
        var set = Assert.Single(destination.CallsTo("Set-HostedContentFilterPolicy"));
        Assert.Equal("Default", set["Identity"]!.GetValue<string>());
        Assert.Equal(5, set["BulkThreshold"]!.GetValue<int>());
        Assert.Equal("Quarantine", set["SpamAction"]!.GetValue<string>());
        Assert.False(set.ContainsKey("Name"));
        Assert.Equal(7, step.Previous!["BulkThreshold"]!.GetValue<int>());
        Assert.Empty(destination.CallsTo("New-HostedContentFilterPolicy"));

        await Deployer.RollbackAsync(tenant, run, () => Task.CompletedTask);

        var restore = destination.CallsTo("Set-HostedContentFilterPolicy").Last();
        Assert.Equal(7, restore["BulkThreshold"]!.GetValue<int>());
        Assert.Equal("MoveToJmf", restore["SpamAction"]!.GetValue<string>());
        Assert.Empty(destination.CallsTo("Remove-HostedContentFilterPolicy"));
        Assert.Equal(StepStatus.RolledBack, step.Status);
        Assert.NotNull(run.RolledBack);
    }

    [Fact]
    public async Task Rollback_empties_a_list_that_was_empty_before()
    {
        var destination = new FakePowerShell()
            .Returns("Get-HostedContentFilterPolicy", """{ "Name": "Default", "Identity": "Default", "IsDefault": true, "BulkThreshold": 5, "SpamAction": "Quarantine" }""")
            .Accepts("Set-HostedContentFilterPolicy", "Identity", "AllowedSenders", "BulkThreshold", "SpamAction");
        var source = new FakePowerShell().Returns("Get-HostedContentFilterPolicy",
            """{ "Name": "Default", "IsDefault": true, "BulkThreshold": 5, "SpamAction": "Quarantine", "AllowedSenders": ["partner@example.org"] }""");
        var item = Assert.Single((await Exporter.ExportAsync(new TenantClients(new FakeGraph(), Exchange: source), [AntiSpam])).Items);
        var tenant = new TenantClients(new FakeGraph(), Exchange: destination);
        var report = await Preflight(item, destination);
        var run = DeployPlanner.Plan(report, new MappingPlan([]), Source, Destination);

        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);
        await Deployer.RollbackAsync(tenant, run, () => Task.CompletedTask);

        var restore = destination.CallsTo("Set-HostedContentFilterPolicy").Last();
        Assert.True(restore.ContainsKey("AllowedSenders"));
        Assert.Null(restore["AllowedSenders"]);
    }

    [Fact]
    public async Task A_preset_policy_is_switched_on_with_its_own_cmdlet()
    {
        var source = new FakePowerShell().Returns("Get-EOPProtectionPolicyRule",
            """{ "Name": "Standard Preset Security Policy", "State": "Enabled", "HostedContentFilterPolicy": "Standard Preset Security Policy1700000000001", "SentToMemberOf": ["all@contoso.com"] }""",
            """{ "Name": "Strict Preset Security Policy", "State": "Disabled" }""");
        var item = Assert.Single((await Exporter.ExportAsync(new TenantClients(new FakeGraph(), Exchange: source), [StandardPreset])).Items);
        var destination = new FakePowerShell()
            .Returns("Get-EOPProtectionPolicyRule", """{ "Name": "Standard Preset Security Policy", "Identity": "Standard Preset Security Policy", "State": "Disabled", "HostedContentFilterPolicy": "Standard Preset Security Policy1800000000002" }""")
            .Accepts("Set-EOPProtectionPolicyRule", "Identity", "SentToMemberOf", "HostedContentFilterPolicy");
        var mapping = new Mapping.Mapping { TargetType = ResourceRegistry.Recipient, Source = new ObjectRef("all@contoso.com", "all@contoso.com"), UsedBy = [item.DisplayName] };
        mapping.Resolve(MatchKind.SameMail, new ObjectRef("all@fabrikam.com", "all@fabrikam.com"));
        var plan = new MappingPlan([mapping]);
        var tenant = new TenantClients(new FakeGraph(), Exchange: destination);

        var report = await PreflightCheck.RunAsync(tenant, Destination, [item], plan, new PreflightChoices());
        var policy = Assert.Single(report.Policies);
        Assert.True(policy.Outcome == Outcome.Create, $"{policy.Outcome}: {string.Join(" | ", policy.Reasons)} || {string.Join(" | ", report.Findings.Select(f => f.Title + ": " + f.Detail))}");
        Assert.Equal(["Enabled", "SentToMemberOf"], policy.ChangedSettings);

        var run = DeployPlanner.Plan(report, plan, Source, Destination);
        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);

        var set = Assert.Single(destination.CallsTo("Set-EOPProtectionPolicyRule"));
        Assert.Equal("all@fabrikam.com", set["SentToMemberOf"]![0]!.GetValue<string>());
        Assert.False(set.ContainsKey("HostedContentFilterPolicy"));
        Assert.False(set.ContainsKey("Enabled"));
        Assert.Single(destination.CallsTo("Enable-EOPProtectionPolicyRule"));

        await Deployer.RollbackAsync(tenant, run, () => Task.CompletedTask);
        Assert.Single(destination.CallsTo("Disable-EOPProtectionPolicyRule"));
    }
}
