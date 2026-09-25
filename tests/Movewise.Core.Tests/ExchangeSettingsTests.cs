using System.Text.Json.Nodes;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

/// <summary>Quarantine policies, the Tenant Allow/Block List, remote domains, mailbox retention and journal rules.</summary>
public class ExchangeSettingsTests
{
    static readonly ResourceType Quarantine = ResourceRegistry.Get(ResourceRegistry.QuarantinePolicy);
    static readonly ResourceType AntiSpam = ResourceRegistry.Get(ResourceRegistry.AntiSpamPolicy);
    static readonly ResourceType Senders = ResourceRegistry.Get(ResourceRegistry.BlockedSender);
    static readonly ResourceType RemoteDomain = ResourceRegistry.Get(ResourceRegistry.RemoteDomain);
    static readonly ResourceType RetentionTag = ResourceRegistry.Get(ResourceRegistry.MailboxRetentionTag);
    static readonly ResourceType MailboxRetention = ResourceRegistry.Get(ResourceRegistry.MailboxRetentionPolicy);
    static readonly ResourceType Journal = ResourceRegistry.Get(ResourceRegistry.JournalRule);

    static readonly TenantInfo Source = new("src-tenant", "Contoso", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false, [], [], []);
    static readonly TenantInfo Destination = new("dst-tenant", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false, [], [], []);

    static async Task<IReadOnlyList<ExportedResource>> Export(FakePowerShell shell, params ResourceType[] types)
    {
        var result = await Exporter.ExportAsync(new TenantClients(new FakeGraph(), Exchange: shell), types);
        Assert.Empty(result.Warnings);
        return result.Items;
    }

    static DeployRun PlanRun(MappingPlan plan, params ExportedResource[] items)
    {
        var results = items.Select(i => new PolicyResult(Transformer.Transform(i, plan, new TransformOptions()), Outcome.Create, [])).ToList();
        return DeployPlanner.Plan(new PreflightReport(results, []), plan, Source, Destination);
    }

    [Fact]
    public async Task Quarantine_policies_are_named_by_name_and_built_in_ones_stay_out()
    {
        var shell = new FakePowerShell()
            .Returns("Get-QuarantinePolicy",
                """{ "Name": "AdminOnlyAccessPolicy", "Guid": "q1" }""",
                """{ "Name": "DefaultGlobalTag", "Guid": "q2", "QuarantinePolicyType": "GlobalQuarantinePolicy" }""",
                """{ "Name": "Finance release", "Guid": "q3", "EndUserQuarantinePermissionsValue": 23, "QuarantinePolicyType": "QuarantinePolicy" }""")
            .Returns("Get-HostedContentFilterPolicy",
                """{ "Name": "Finance spam", "Guid": "s1", "SpamQuarantineTag": "Finance release", "PhishQuarantineTag": "AdminOnlyAccessPolicy" }""");

        var items = await Export(shell, Quarantine, AntiSpam);

        var quarantine = Assert.Single(items, i => i.Type == Quarantine);
        Assert.Equal("Finance release", quarantine.SourceId);
        var spam = Assert.Single(items, i => i.Type == AntiSpam);
        var dependency = Assert.Single(spam.Dependencies, d => d.TargetType == ResourceRegistry.QuarantinePolicy);
        Assert.Equal("Finance release", dependency.Value);
    }

    [Fact]
    public async Task A_policy_names_the_quarantine_policy_created_before_it()
    {
        var quarantine = Item(Quarantine, """{ "Name": "Finance release", "EndUserQuarantinePermissionsValue": 23 }""", "Finance release");
        var spam = Item(AntiSpam, """{ "Name": "Finance spam", "SpamQuarantineTag": "Finance release" }""", "s1");
        var mapping = new Mapping.Mapping { TargetType = ResourceRegistry.QuarantinePolicy, Source = new ObjectRef("Finance release", "Finance release"), UsedBy = ["Finance spam"] };
        mapping.Resolve(MatchKind.CreatedByMigration, new ObjectRef("", "Finance release"));
        var plan = new MappingPlan([mapping]);
        var shell = new FakePowerShell();

        var run = PlanRun(plan, spam, quarantine);
        await Deployer.RunAsync(new TenantClients(new FakeGraph(), Exchange: shell), run, () => Task.CompletedTask);

        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
        Assert.Equal(new[] { "New-QuarantinePolicy", "New-HostedContentFilterPolicy" }, shell.Calls.Where(c => c.Cmdlet.StartsWith("New-")).Select(c => c.Cmdlet).ToArray());
        Assert.Equal("Finance release", run.Steps[0].DestinationId);
        Assert.Equal("Finance release", Assert.Single(shell.CallsTo("New-HostedContentFilterPolicy"))["SpamQuarantineTag"]!.GetValue<string>());
    }

    [Fact]
    public async Task Allow_block_entries_are_read_per_list_and_expired_ones_stay_out()
    {
        var shell = new FakePowerShell().Returns("Get-TenantAllowBlockListItems",
            """{ "Identity": "t1", "Value": "bad@example.net", "Action": "Block", "ExpirationDate": null }""",
            """{ "Identity": "t2", "Value": "old@example.net", "Action": "Block", "ExpirationDate": "2020-01-01T00:00:00Z" }""");

        var item = Assert.Single(await Export(shell, Senders));

        Assert.Equal("bad@example.net", item.DisplayName);
        Assert.Equal("Sender", Assert.Single(shell.CallsTo("Get-TenantAllowBlockListItems"))["ListType"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_allow_block_entry_is_created_with_its_list_and_removed_by_its_id()
    {
        var entry = Item(Senders, """{ "Value": "bad@example.net", "Action": "Block", "Notes": "fraud" }""", "t1");
        var shell = new FakePowerShell().Returns("New-TenantAllowBlockListItems", """{ "Identity": "new-1", "Value": "bad@example.net" }""");
        var tenant = new TenantClients(new FakeGraph(), Exchange: shell);
        var run = PlanRun(new MappingPlan([]), entry);

        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);

        var created = Assert.Single(shell.CallsTo("New-TenantAllowBlockListItems"));
        Assert.Equal("Sender", created["ListType"]!.GetValue<string>());
        Assert.Equal("bad@example.net", created["Entries"]!.GetValue<string>());
        Assert.True(created["Block"]!.GetValue<bool>());
        Assert.True(created["NoExpiration"]!.GetValue<bool>());
        Assert.False(created.ContainsKey("Action"));
        Assert.Equal("new-1", run.Steps[0].DestinationId);

        await Deployer.RollbackAsync(tenant, run, () => Task.CompletedTask);

        var removed = Assert.Single(shell.CallsTo("Remove-TenantAllowBlockListItems"));
        Assert.Equal("new-1", removed["Ids"]!.GetValue<string>());
        Assert.Equal("Sender", removed["ListType"]!.GetValue<string>());
        Assert.Equal(StepStatus.RolledBack, run.Steps[0].Status);
    }

    [Fact]
    public async Task A_remote_domain_gets_the_rest_of_its_settings_with_set_once_it_exists()
    {
        var domain = Item(RemoteDomain, """{ "Name": "Northwind", "DomainName": "northwind.example", "AutoReplyEnabled": true, "TNEFEnabled": false }""", "r1");
        var shell = new FakePowerShell()
            .Accepts("New-RemoteDomain", "Name", "DomainName")
            .Accepts("Set-RemoteDomain", "Identity", "Name", "AutoReplyEnabled", "TNEFEnabled");
        var run = PlanRun(new MappingPlan([]), domain);

        await Deployer.RunAsync(new TenantClients(new FakeGraph(), Exchange: shell), run, () => Task.CompletedTask);

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Equal(new[] { "DomainName", "Name" }, Assert.Single(shell.CallsTo("New-RemoteDomain")).Keys.Order().ToArray());
        var set = Assert.Single(shell.CallsTo("Set-RemoteDomain"));
        Assert.Equal(new[] { "AutoReplyEnabled", "Identity", "TNEFEnabled" }, set.Keys.Order().ToArray());
        Assert.Equal(run.Steps[0].DestinationId, set["Identity"]!.GetValue<string>());
        Assert.DoesNotContain(run.Steps[0].Notes, n => n.Contains("AutoReplyEnabled"));
    }

    [Fact]
    public async Task A_failed_set_leaves_the_remote_domain_created_and_a_retry_only_sets()
    {
        var domain = Item(RemoteDomain, """{ "Name": "Northwind", "DomainName": "northwind.example", "AutoReplyEnabled": true }""", "r1");
        var shell = new FakePowerShell()
            .Accepts("New-RemoteDomain", "Name", "DomainName")
            .Accepts("Set-RemoteDomain", "Identity", "AutoReplyEnabled")
            .Failing("Set-RemoteDomain", new PowerShellException("Something went wrong"), times: 1);
        var tenant = new TenantClients(new FakeGraph(), Exchange: shell);
        var run = PlanRun(new MappingPlan([]), domain);

        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);
        Assert.Equal(StepStatus.Failed, run.Steps[0].Status);
        Assert.Contains("couldn't be set", run.Steps[0].Message);

        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Single(shell.CallsTo("New-RemoteDomain"));
        Assert.Equal(2, shell.CallsTo("Set-RemoteDomain").Count());
    }

    [Fact]
    public async Task Retention_tags_keep_their_period_and_built_in_ones_stay_out()
    {
        var shell = new FakePowerShell()
            .Returns("Get-RetentionPolicyTag",
                """{ "Name": "1 Week Delete", "Guid": "t0", "AgeLimitForRetention": { "Days": 7, "Ticks": 6048000000000 } }""",
                """{ "Name": "AutoGroup", "Guid": "t1", "SystemTag": true }""",
                """{ "Name": "Finance 7 years", "Guid": "t2", "Type": "All", "RetentionAction": "MoveToArchive", "AgeLimitForRetention": { "Days": 2555, "Ticks": 2207520000000000 } }""")
            .Returns("Get-RetentionPolicy",
                """{ "Name": "Default MRM Policy", "Guid": "p0", "IsDefault": true }""",
                """{ "Name": "Finance", "Guid": "p1", "RetentionPolicyTagLinks": ["Finance 7 years", "1 Week Delete"] }""");

        var items = await Export(shell, RetentionTag, MailboxRetention);

        var tag = Assert.Single(items, i => i.Type == RetentionTag);
        Assert.Equal("Finance 7 years", tag.SourceId);
        Assert.Equal("2555.00:00:00", tag.Settings["AgeLimitForRetention"]!.GetValue<string>());
        var policy = Assert.Single(items, i => i.Type == MailboxRetention);
        Assert.Equal("Finance 7 years", Assert.Single(policy.Dependencies).Value);
    }

    [Fact]
    public void Journal_rules_are_created_switched_off()
    {
        var rule = Item(Journal, """{ "Name": "Journal execs", "Recipient": "execs@contoso.com", "JournalEmailAddress": "vault@archive.example", "Enabled": true }""", "j1");

        var result = Transformer.Transform(rule, new MappingPlan([]), new TransformOptions());

        Assert.False(result.Desired["Enabled"]!.GetValue<bool>());
        Assert.Contains(result.Changes, c => c.IsTestMode);
    }

    static ExportedResource Item(ResourceType type, string json, string id)
    {
        var settings = Normalizer.Normalize(JsonNode.Parse(json)!.AsObject(), type);
        return new ExportedResource(type, id, settings[type.IdentityProperty]!.GetValue<string>(), settings, DependencyExtractor.Extract(settings, type));
    }
}
