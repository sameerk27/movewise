using System.Text.Json.Nodes;
using Movewise.Core.Access;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

/// <summary>Pre-flight findings about policies that would do more, or less, in the destination than the admin expects.</summary>
public class PreflightSafetyTests
{
    static readonly ResourceType AntiPhish = ResourceRegistry.Get(ResourceRegistry.AntiPhishPolicy);
    static readonly ResourceType ConditionalAccess = ResourceRegistry.Get(ResourceRegistry.ConditionalAccessPolicy);

    static readonly TenantInfo Destination = new("dest-tenant", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false,
        [DirectoryRoles.GlobalAdministrator], [], [])
    {
        ServicePlans = new HashSet<string>(["AAD_PREMIUM", "ATP_ENTERPRISE"], StringComparer.OrdinalIgnoreCase),
    };

    static ExportedResource Item(ResourceType type, string json)
    {
        var settings = Normalizer.Normalize(JsonNode.Parse(json)!.AsObject(), type);
        return new ExportedResource(type, "id-1", settings[type.IdentityProperty]!.GetValue<string>(), settings, DependencyExtractor.Extract(settings, type));
    }

    static MappingPlan Plan(params (string Type, string Value, Action<Mapping.Mapping> Decide)[] items) => new(items.Select(i =>
    {
        var mapping = new Mapping.Mapping
        {
            TargetType = i.Type,
            Source = new ObjectRef(i.Value, "name-" + i.Value),
            UsedBy = [],
            SourceDetails = new JsonObject(),
        };
        i.Decide(mapping);
        return mapping;
    }));

    static ExportedResource ExecsRule() => Item(AntiPhish, """
        { "Name": "Execs", "rules": [ { "Name": "Execs", "SentToMemberOf": ["execs@contoso.com"], "ExceptIfSentTo": ["ceo@contoso.com"] } ] }
        """);

    [Fact]
    public void Removing_every_recipient_a_rule_applies_to_is_a_problem()
    {
        var removeAll = Plan(
            (ResourceRegistry.Recipient, "execs@contoso.com", m => m.RemoveFromPolicies()),
            (ResourceRegistry.Recipient, "ceo@contoso.com", m => m.RemoveFromPolicies()));

        var result = Transformer.Transform(ExecsRule(), removeAll, new TransformOptions());

        var emptied = Assert.Single(result.EmptiedConditions);
        Assert.Contains("SentToMemberOf", emptied);
        Assert.Contains("all mail", emptied);
        Assert.Contains(result.Problems, p => p.Contains("SentToMemberOf"));
    }

    [Fact]
    public void Emptying_only_an_exception_is_not_a_problem()
    {
        var plan = Plan(
            (ResourceRegistry.Recipient, "execs@contoso.com", m => m.Resolve(MatchKind.SameMail, new ObjectRef("execs@fabrikam.com", "Execs"))),
            (ResourceRegistry.Recipient, "ceo@contoso.com", m => m.RemoveFromPolicies()));

        var result = Transformer.Transform(ExecsRule(), plan, new TransformOptions());

        Assert.Empty(result.EmptiedConditions);
        Assert.Empty(result.Problems);
        Assert.Contains(result.Changes, c => c.WidensPolicy);
    }

    [Fact]
    public async Task A_rule_that_would_apply_to_all_mail_blocks_deploying()
    {
        var plan = Plan(
            (ResourceRegistry.Recipient, "execs@contoso.com", m => m.RemoveFromPolicies()),
            (ResourceRegistry.Recipient, "ceo@contoso.com", m => m.RemoveFromPolicies()));

        var report = await PreflightCheck.RunAsync(new TenantClients(new FakeGraph(), Exchange: new FakePowerShell()), Destination, [ExecsRule()], plan, new PreflightChoices());

        Assert.Equal(Outcome.Blocked, Assert.Single(report.Policies).Outcome);
        Assert.Contains(report.Findings, f => f.Severity == Severity.Blocker && f.Title == "Rules that would apply to all mail");
        Assert.False(report.CanDeploy);
    }

    static ExportedResource ExcludesGroup() => Item(ConditionalAccess, """
        { "displayName": "CA001", "state": "enabled", "conditions": { "users": { "includeUsers": ["All"], "excludeGroups": ["g-1"] } } }
        """);

    [Fact]
    public async Task Excluding_a_group_this_migration_creates_is_flagged()
    {
        var plan = Plan((ResourceRegistry.Group, "g-1", m => m.CreateInDestination()));

        var report = await PreflightCheck.RunAsync(new FakeGraph().Collection(ConditionalAccess.ListPath), Destination, [ExcludesGroup()], plan, new PreflightChoices());

        Assert.Contains(report.Policies[0].Policy.Changes, c => c.ExcludesNewGroup);
        var finding = Assert.Single(report.Findings, f => f.Title == "Exclusions that start empty");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("CA001", Assert.Single(finding.Items));
    }

    [Fact]
    public void Excluding_a_new_dynamic_group_is_not_flagged()
    {
        var plan = new MappingPlan([new Mapping.Mapping
        {
            TargetType = ResourceRegistry.Group,
            Source = new ObjectRef("g-1", "Break glass"),
            UsedBy = [],
            SourceDetails = JsonNode.Parse("""{ "groupTypes": ["DynamicMembership"], "membershipRule": "user.department -eq \"IT\"" }""")!.AsObject(),
        }]);
        plan.Items.Single().CreateInDestination();

        var result = Transformer.Transform(ExcludesGroup(), plan, new TransformOptions());

        Assert.DoesNotContain(result.Changes, c => c.ExcludesNewGroup);
    }

    static FakePowerShell DestinationWithRule(params string[] ruleNames) => new FakePowerShell()
        .Returns("Get-AntiPhishRule", ruleNames.Select(n => $$"""{ "Name": "{{n}}", "AntiPhishPolicy": "Other" }""").ToArray());

    static ExportedResource PolicyWithRule() => Item(AntiPhish, """{ "Name": "Execs", "rules": [ { "Name": "Execs rule" } ] }""");

    [Fact]
    public async Task A_taken_rule_name_is_a_name_conflict()
    {
        var tenant = new TenantClients(new FakeGraph(), Exchange: DestinationWithRule("Execs rule"));

        var skip = await PreflightCheck.RunAsync(tenant, Destination, [PolicyWithRule()], Plan(), new PreflightChoices());
        var rename = await PreflightCheck.RunAsync(tenant, Destination, [PolicyWithRule()], Plan(), new PreflightChoices { NameConflicts = ConflictChoice.CreateWithSuffix });

        Assert.Equal(Outcome.Skip, skip.Policies[0].Outcome);
        Assert.Contains(skip.Findings, f => f.Choice == FindingChoice.NameConflicts && f.Items.Single().Contains("rule"));
        Assert.Equal(Outcome.Create, rename.Policies[0].Outcome);
        Assert.Equal("Execs rule (migrated)", rename.Policies[0].Policy.Desired["rules"]![0]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_retention_policy_naming_a_renamed_label_is_pointed_out()
    {
        var label = Item(ResourceRegistry.Get(ResourceRegistry.RetentionLabel), """{ "Name": "Confidential" }""");
        var policy = Item(ResourceRegistry.Get(ResourceRegistry.RetentionPolicy), """{ "Name": "Keep finance", "rules": [ { "Name": "Keep finance rule", "PublishComplianceTag": "Confidential" } ] }""");
        var tenant = new TenantClients(new FakeGraph(), Compliance: new FakePowerShell().Returns("Get-ComplianceTag", """{ "Name": "Confidential" }"""));

        var report = await PreflightCheck.RunAsync(tenant, Destination, [label, policy], Plan(), new PreflightChoices { NameConflicts = ConflictChoice.CreateWithSuffix });

        var finding = Assert.Single(report.Findings, f => f.Title == "Retention policies name a renamed label");
        Assert.Equal("Keep finance: label \"Confidential\"", Assert.Single(finding.Items));
    }

    [Fact]
    public async Task A_name_taken_even_with_migrated_added_is_skipped()
    {
        var tenant = new TenantClients(new FakeGraph(), Exchange: DestinationWithRule("Execs rule", "Execs rule (migrated)"));

        var report = await PreflightCheck.RunAsync(tenant, Destination, [PolicyWithRule()], Plan(), new PreflightChoices { NameConflicts = ConflictChoice.CreateWithSuffix });

        Assert.Equal(Outcome.Skip, report.Policies[0].Outcome);
        Assert.Contains(report.Findings, f => f.Title == "Names still taken after renaming");
    }

    [Fact]
    public async Task Policies_without_a_test_mode_are_listed_as_taking_effect_straight_away()
    {
        var report = await PreflightCheck.RunAsync(new TenantClients(new FakeGraph(), Exchange: new FakePowerShell()), Destination,
            [Item(AntiPhish, """{ "Name": "Execs" }""")], Plan(), new PreflightChoices());

        var finding = Assert.Single(report.Findings, f => f.Title == "Policies that take effect straight away");
        Assert.Equal("Execs (Anti-phishing policy)", Assert.Single(finding.Items));
    }
}
