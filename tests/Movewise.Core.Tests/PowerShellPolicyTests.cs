using System.Text.Json.Nodes;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

/// <summary>Exchange, Defender and Purview policies, which go through PowerShell.</summary>
public class PowerShellPolicyTests
{
    static readonly ResourceType AntiPhish = ResourceRegistry.Get(ResourceRegistry.AntiPhishPolicy);
    static readonly ResourceType Dlp = ResourceRegistry.Get(ResourceRegistry.DlpPolicy);
    static readonly ResourceType Retention = ResourceRegistry.Get(ResourceRegistry.RetentionPolicy);
    static readonly ResourceType TransportRule = ResourceRegistry.Get(ResourceRegistry.TransportRule);

    static readonly TenantInfo Source = new("src-tenant", "Contoso", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false, [], [], []);
    static readonly TenantInfo Destination = new("dst-tenant", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false, [], [], []);

    static FakePowerShell DefenderSource() => new FakePowerShell()
        .Returns("Get-AntiPhishPolicy",
            """{ "Name": "Office365 AntiPhish Default", "IsDefault": true, "Guid": "g0" }""",
            """{ "Name": "Standard Preset Security Policy1700000000000", "Guid": "g1" }""",
            """{ "Name": "Execs", "Guid": "g2", "Identity": "Execs", "WhenChanged": "2026-01-01", "EnableMailboxIntelligence": true, "TargetedDomainsToProtect": ["contoso.com"] }""")
        .Returns("Get-AntiPhishRule",
            """{ "Name": "Execs", "AntiPhishPolicy": "Execs", "State": "Enabled", "Priority": 0, "SentToMemberOf": ["execs@contoso.com"], "ExceptIfSentTo": ["partner@example.org"] }""",
            """{ "Name": "Preset", "AntiPhishPolicy": "Standard Preset Security Policy1700000000000", "State": "Enabled" }""");

    static async Task<ExportedResource> ExportOne(FakePowerShell shell, ResourceType type)
    {
        var result = await Exporter.ExportAsync(new TenantClients(new FakeGraph(), Exchange: shell, Compliance: shell), [type]);
        Assert.Empty(result.Warnings);
        return Assert.Single(result.Items);
    }

    // ---------- Reading ----------

    [Fact]
    public async Task Reads_a_defender_policy_with_its_rule_and_leaves_out_built_in_ones()
    {
        var item = await ExportOne(DefenderSource(), AntiPhish);

        Assert.Equal("Execs", item.DisplayName);
        Assert.Equal("g2", item.SourceId);
        Assert.False(item.Settings.ContainsKey("WhenChanged"));
        Assert.False(item.Settings.ContainsKey("Identity"));

        var rule = Assert.Single(item.Settings["rules"]!.AsArray())!.AsObject();
        Assert.True(rule["Enabled"]!.GetValue<bool>());
        Assert.False(rule.ContainsKey("State"));
        Assert.False(rule.ContainsKey("AntiPhishPolicy"));

        Assert.Contains(item.Dependencies, d => d.TargetType == ResourceRegistry.Recipient && d.Value == "execs@contoso.com");
        Assert.Contains(item.Dependencies, d => d.TargetType == ResourceRegistry.Recipient && d.Value == "partner@example.org");
        Assert.Contains(item.Dependencies, d => d.TargetType == ResourceRegistry.Domain && d.Value == "contoso.com");
    }

    [Fact]
    public async Task Reads_purview_rules_per_policy_and_flattens_locations()
    {
        var shell = new FakePowerShell()
            .Returns("Get-DlpCompliancePolicy", """
                { "Name": "PII", "Guid": "d1", "Mode": "Enable",
                  "ExchangeLocation": [ { "Name": "All", "DisplayName": "All" } ],
                  "SharePointLocation": [ { "Name": "https://contoso.sharepoint.com/sites/hr", "DisplayName": "HR" } ] }
                """)
            .Returns("Get-DlpComplianceRule|PII", """{ "Name": "PII rule", "ParentPolicyName": "PII", "Disabled": false, "BlockAccess": true }""");

        var item = await ExportOne(shell, Dlp);

        Assert.Equal("All", item.Settings["ExchangeLocation"]![0]!.GetValue<string>());
        Assert.Equal("https://contoso.sharepoint.com/sites/hr", item.Settings["SharePointLocation"]![0]!.GetValue<string>());
        Assert.Equal("PII rule", item.Settings["rules"]![0]!["Name"]!.GetValue<string>());
        Assert.Single(shell.CallsTo("Get-DlpCompliancePolicy"), p => p["DistributionDetail"]!.GetValue<bool>());

        // "All" means the same everywhere; the site has to be matched.
        var dependency = Assert.Single(item.Dependencies);
        Assert.Equal((ResourceRegistry.Site, "https://contoso.sharepoint.com/sites/hr"), (dependency.TargetType, dependency.Value));
    }

    // ---------- Rewriting ----------

    static MappingPlan Plan(params (string Type, string Value, Action<Mapping.Mapping> Decide)[] items) => new(items.Select(i =>
    {
        var mapping = new Mapping.Mapping { TargetType = i.Type, Source = new ObjectRef(i.Value, i.Value), UsedBy = [] };
        i.Decide(mapping);
        return mapping;
    }));

    static ExportedResource Item(ResourceType type, string json)
    {
        var settings = Normalizer.Normalize(JsonNode.Parse(json)!.AsObject(), type);
        return new ExportedResource(type, "id-1", settings["Name"]!.GetValue<string>(), settings, DependencyExtractor.Extract(settings, type));
    }

    [Fact]
    public void Rewrites_addresses_and_domains_and_keeps_partners()
    {
        var item = Item(AntiPhish, """
            { "Name": "Execs", "TargetedDomainsToProtect": ["contoso.com"],
              "rules": [ { "Name": "Execs", "SentToMemberOf": ["execs@contoso.com"], "ExceptIfSentTo": ["partner@example.org"] } ] }
            """);
        var plan = Plan(
            (ResourceRegistry.Domain, "contoso.com", m => m.Resolve(MatchKind.Manual, new ObjectRef("fabrikam.com", "fabrikam.com"))),
            (ResourceRegistry.Recipient, "execs@contoso.com", m => m.Resolve(MatchKind.SameMail, new ObjectRef("execs@fabrikam.com", "Executives"))),
            (ResourceRegistry.Recipient, "partner@example.org", m => m.Resolve(MatchKind.External, new ObjectRef("partner@example.org", "partner@example.org"))));

        var result = Transformer.Transform(item, plan, new TransformOptions());

        Assert.Empty(result.Problems);
        Assert.Equal("fabrikam.com", result.Desired["TargetedDomainsToProtect"]![0]!.GetValue<string>());
        var rule = result.Desired["rules"]![0]!;
        Assert.Equal("execs@fabrikam.com", rule["SentToMemberOf"]![0]!.GetValue<string>());
        Assert.Equal("partner@example.org", rule["ExceptIfSentTo"]![0]!.GetValue<string>());
        Assert.Contains(result.Changes, c => c.Description == "Mailbox or group execs@contoso.com → execs@fabrikam.com.");
        Assert.DoesNotContain(result.Changes, c => c.Description.Contains("partner@example.org"));
    }

    [Fact]
    public void Creates_mail_flow_rules_and_dlp_policies_in_test_mode()
    {
        var rule = Transformer.Transform(Item(TransportRule, """{ "Name": "Block exe", "Mode": "Enforce" }"""), Plan(), new TransformOptions());
        var dlp = Transformer.Transform(Item(Dlp, """{ "Name": "PII", "Mode": "Enable" }"""), Plan(), new TransformOptions());

        Assert.Equal("Audit", rule.Desired["Mode"]!.GetValue<string>());
        Assert.Equal("TestWithoutNotifications", dlp.Desired["Mode"]!.GetValue<string>());
        Assert.All(new[] { rule, dlp }, t => Assert.Contains(t.Changes, c => c.IsTestMode));
    }

    [Fact]
    public void Creates_retention_policies_switched_off_and_never_locked()
    {
        var result = Transformer.Transform(Item(Retention, """{ "Name": "Keep 7 years", "Enabled": true, "RestrictiveRetention": true }"""), Plan(), new TransformOptions());

        Assert.False(result.Desired["Enabled"]!.GetValue<bool>());
        Assert.False(result.Desired.ContainsKey("RestrictiveRetention"));
        Assert.Equal(2, result.Changes.Count(c => c.IsTestMode));
    }

    [Fact]
    public void Renaming_a_policy_renames_its_rules()
    {
        var item = Item(AntiPhish, """{ "Name": "Execs", "rules": [ { "Name": "Execs" } ] }""");

        var result = Transformer.Transform(item, Plan(), new TransformOptions { Rename = new HashSet<string> { MappingPlan.KeyOf(AntiPhish.Id, "id-1") } });

        Assert.Equal("Execs (migrated)", result.Desired["Name"]!.GetValue<string>());
        Assert.Equal("Execs (migrated)", result.Desired["rules"]![0]!["Name"]!.GetValue<string>());
    }

    // ---------- Matching ----------

    const string Domains = "v1.0/domains?$select=id,isVerified,isInitial,isDefault";
    const string SiteRoot = "v1.0/sites/root?$select=siteCollection";
    const string RecipientSelect = "id,displayName,mail,userPrincipalName";

    static ExportedResource Uses(params Dependency[] dependencies) =>
        new(Dlp, "p1", "PII", new JsonObject(), dependencies);

    static FakeGraph SourceTenant() => new FakeGraph()
        .Collection(Domains,
            """{ "id": "contoso.com", "isVerified": true, "isInitial": false }""",
            """{ "id": "contoso.onmicrosoft.com", "isVerified": true, "isInitial": true }""")
        .Object(SiteRoot, """{ "siteCollection": { "hostname": "contoso.sharepoint.com" } }""");

    static FakeGraph DestinationTenant() => new FakeGraph()
        .Collection(Domains,
            """{ "id": "fabrikam.com", "isVerified": true, "isInitial": false }""",
            """{ "id": "fabrikam.onmicrosoft.com", "isVerified": true, "isInitial": true }""")
        .Object(SiteRoot, """{ "siteCollection": { "hostname": "fabrikam.sharepoint.com" } }""");

    [Fact]
    public async Task Matches_domains_by_what_the_source_owns()
    {
        var policy = Uses(
            new Dependency(ResourceRegistry.Domain, "contoso.com", "x"),
            new Dependency(ResourceRegistry.Domain, "contoso.onmicrosoft.com", "x"),
            new Dependency(ResourceRegistry.Domain, "partner.example", "x"));

        var plan = await Matcher.BuildAsync(SourceTenant(), DestinationTenant(), [policy], [policy]);

        Assert.Equal(MatchKind.Unresolved, plan.Find(ResourceRegistry.Domain, "contoso.com")!.Kind);
        var initial = plan.Find(ResourceRegistry.Domain, "contoso.onmicrosoft.com")!;
        Assert.Equal((MatchKind.InitialDomain, "fabrikam.onmicrosoft.com"), (initial.Kind, initial.Destination!.Id));
        Assert.Equal(MatchKind.External, plan.Find(ResourceRegistry.Domain, "partner.example")!.Kind);
    }

    [Fact]
    public async Task Rewrites_an_address_with_its_domains_match_and_looks_it_up()
    {
        var destination = DestinationTenant()
            .Object(GraphQuery.Where("v1.0/users", "mail eq 'anna@fabrikam.com'", RecipientSelect, top: 5),
                """{ "value": [ { "id": "u1", "displayName": "Anna", "mail": "anna@fabrikam.com" } ] }""");
        var policy = Uses(new Dependency(ResourceRegistry.Recipient, "anna@contoso.com", "x"));
        var previous = new MappingPlan([Decided(ResourceRegistry.Domain, "contoso.com", "fabrikam.com")]);

        var plan = await Matcher.BuildAsync(SourceTenant(), destination, [policy], [policy], previous);

        // The domain behind the address is listed too, so it can be matched once for every address in it.
        Assert.Equal(MatchKind.Manual, plan.Find(ResourceRegistry.Domain, "contoso.com")!.Kind);
        var anna = plan.Find(ResourceRegistry.Recipient, "anna@contoso.com")!;
        Assert.Equal((MatchKind.SameMail, "anna@fabrikam.com"), (anna.Kind, anna.Destination!.Id));
    }

    [Fact]
    public async Task Finds_a_site_at_the_same_path_in_the_destinations_sharepoint()
    {
        var destination = DestinationTenant()
            .Object("v1.0/sites/fabrikam.sharepoint.com:/sites/hr?$select=id,webUrl,displayName",
                """{ "id": "s1", "webUrl": "https://fabrikam.sharepoint.com/sites/hr", "displayName": "HR" }""");
        var policy = Uses(
            new Dependency(ResourceRegistry.Site, "https://contoso.sharepoint.com/sites/hr", "x"),
            new Dependency(ResourceRegistry.Site, "https://contoso.sharepoint.com/sites/gone", "x"));

        var plan = await Matcher.BuildAsync(SourceTenant(), destination, [policy], [policy]);

        var hr = plan.Find(ResourceRegistry.Site, "https://contoso.sharepoint.com/sites/hr")!;
        Assert.Equal((MatchKind.SamePath, "https://fabrikam.sharepoint.com/sites/hr"), (hr.Kind, hr.Destination!.Id));
        var gone = plan.Find(ResourceRegistry.Site, "https://contoso.sharepoint.com/sites/gone")!;
        Assert.Equal(MatchKind.Unresolved, gone.Kind);
        Assert.Contains("https://fabrikam.sharepoint.com/sites/gone", gone.Note);
    }

    static Mapping.Mapping Decided(string type, string value, string destination)
    {
        var mapping = new Mapping.Mapping { TargetType = type, Source = new ObjectRef(value, value), UsedBy = [] };
        mapping.MapTo(new ObjectRef(destination, destination));
        return mapping;
    }

    // ---------- Deploying ----------

    static DeployRun PlanRun(ExportedResource item)
    {
        var report = new PreflightReport([new PolicyResult(Transformer.Transform(item, new MappingPlan([]), new TransformOptions()), Outcome.Create, [])], []);
        return DeployPlanner.Plan(report, new MappingPlan([]), Source, Destination);
    }

    static ExportedResource PhishPolicy() => Item(AntiPhish, """
        { "Name": "Execs", "EnableMailboxIntelligence": true, "Unsettable": 5, "ImpersonationProtectionState": { "Mode": "x" },
          "rules": [ { "Name": "Execs", "Priority": 3, "Enabled": true, "SentTo": ["a@fabrikam.com"], "Comments": "" },
                     { "Name": "Execs 2", "Enabled": false } ] }
        """);

    [Fact]
    public async Task Creates_the_policy_with_settings_the_cmdlet_accepts_then_its_rules()
    {
        var shell = new FakePowerShell()
            .Accepts("New-AntiPhishPolicy", "Name", "EnableMailboxIntelligence", "ImpersonationProtectionState")
            .Accepts("New-AntiPhishRule", "Name", "AntiPhishPolicy", "Enabled", "SentTo", "Priority", "Comments");
        var run = PlanRun(PhishPolicy());

        await Deployer.RunAsync(new TenantClients(new FakeGraph(), Exchange: shell), run, () => Task.CompletedTask);

        var step = Assert.Single(run.Steps);
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal("guid-1", step.DestinationId);

        var policy = Assert.Single(shell.CallsTo("New-AntiPhishPolicy"));
        Assert.Equal(new[] { "EnableMailboxIntelligence", "Name" }, policy.Keys.Order().ToArray());
        Assert.Contains(step.Notes, n => n.Contains("ImpersonationProtectionState"));

        var rules = shell.CallsTo("New-AntiPhishRule").ToList();
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.Equal("Execs", r["AntiPhishPolicy"]!.GetValue<string>()));
        Assert.False(rules[0].ContainsKey("Priority"));
        Assert.False(rules[0].ContainsKey("Comments"));
        Assert.Equal("a@fabrikam.com", rules[0]["SentTo"]![0]!.GetValue<string>());
        Assert.Equal(new[] { "Execs", "Execs 2" }, step.CreatedRules);
    }

    [Fact]
    public async Task A_retry_after_a_failed_rule_creates_only_the_rest()
    {
        var shell = new FakePowerShell().Failing("New-AntiPhishRule", new PowerShellException("Recipient not found"), times: 1);
        var tenant = new TenantClients(new FakeGraph(), Exchange: shell);
        var run = PlanRun(PhishPolicy());

        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);

        var step = run.Steps[0];
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Contains("\"Execs\"", step.Message);
        Assert.Empty(step.CreatedRules);

        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);

        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Single(shell.CallsTo("New-AntiPhishPolicy"));
        Assert.Equal(3, shell.CallsTo("New-AntiPhishRule").Count());
    }

    [Fact]
    public async Task Rollback_removes_the_rules_then_the_policy()
    {
        var shell = new FakePowerShell();
        var tenant = new TenantClients(new FakeGraph(), Exchange: shell);
        var run = PlanRun(PhishPolicy());
        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);

        await Deployer.RollbackAsync(tenant, run, () => Task.CompletedTask);

        var removals = shell.Calls.Where(c => c.Cmdlet.StartsWith("Remove-")).ToList();
        Assert.Equal(new[] { "Remove-AntiPhishRule", "Remove-AntiPhishRule", "Remove-AntiPhishPolicy" }, removals.Select(c => c.Cmdlet).ToArray());
        Assert.Equal("guid-1", removals[2].Parameters["Identity"]!.GetValue<string>());
        Assert.All(removals, r => Assert.False(r.Parameters["Confirm"]!.GetValue<bool>()));
        Assert.Equal(StepStatus.RolledBack, run.Steps[0].Status);
        Assert.NotNull(run.RolledBack);
    }

    [Fact]
    public async Task Rollback_counts_a_policy_already_removed_as_gone()
    {
        var shell = new FakePowerShell().Failing("Remove-AntiPhishPolicy", new PowerShellException("The operation couldn't be performed because object 'Execs' couldn't be found."));
        var tenant = new TenantClients(new FakeGraph(), Exchange: shell);
        var run = PlanRun(PhishPolicy());
        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);

        await Deployer.RollbackAsync(tenant, run, () => Task.CompletedTask);

        Assert.Equal(StepStatus.RolledBack, run.Steps[0].Status);
        Assert.Equal("Already gone from the destination.", run.Steps[0].Message);
    }

    [Fact]
    public void Passes_sensitive_info_conditions_as_hashtables_but_not_other_objects()
    {
        var settings = JsonNode.Parse("""
            { "ContentContainsSensitiveInformation": [ { "name": "Credit Card Number", "mincount": "1" } ],
              "SomeObject": { "a": 1 }, "Empty": [], "Missing": null, "Priority": 1 }
            """)!.AsObject();
        var accepted = new HashSet<string>(["ContentContainsSensitiveInformation", "SomeObject", "Empty", "Missing", "Priority"], StringComparer.OrdinalIgnoreCase);

        var (parameters, notCopied) = PowerShellShape.ToParameters(settings, accepted);

        Assert.Equal(new[] { "ContentContainsSensitiveInformation" }, parameters.Keys.ToArray());
        Assert.Equal(new[] { "SomeObject" }, notCopied.ToArray());
    }
}
