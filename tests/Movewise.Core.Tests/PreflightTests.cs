using System.Text.Json.Nodes;
using Movewise.Core.Access;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

public class TransformerTests
{
    static readonly ResourceType ConditionalAccess = ResourceRegistry.Get(ResourceRegistry.ConditionalAccessPolicy);
    static readonly ResourceType SettingsCatalog = ResourceRegistry.Get(ResourceRegistry.SettingsCatalogPolicy);

    static ExportedResource Export(ResourceType type, JsonObject raw)
    {
        var settings = Normalizer.Normalize(raw, type);
        return new ExportedResource(type, "src-1", settings[type.IdentityProperty]!.GetValue<string>(), settings, DependencyExtractor.Extract(settings, type));
    }

    static MappingPlan PlanFor(ExportedResource item, Action<string, string, Mapping.Mapping> decide)
    {
        var mappings = item.Dependencies.DistinctBy(d => (d.TargetType, d.Value)).Select(d =>
        {
            var mapping = new Mapping.Mapping
            {
                TargetType = d.TargetType,
                Source = new ObjectRef(d.Value, $"name-{d.Value[..4]}"),
                UsedBy = [item.DisplayName],
                SourceDetails = new JsonObject(),
            };
            decide(d.TargetType, d.Value, mapping);
            return mapping;
        });
        return new MappingPlan(mappings);
    }

    [Fact]
    public void Swaps_every_source_id_for_its_match_and_turns_on_report_only()
    {
        var item = Export(ConditionalAccess, Samples.ConditionalAccessPolicy());
        var plan = PlanFor(item, (type, id, m) => m.MapTo(new ObjectRef("dest-" + id, "Destination " + type)));

        var result = Transformer.Transform(item, plan, new TransformOptions());

        var json = result.Desired.ToJsonString();
        Assert.DoesNotContain(Samples.AllStaffGroup, json.Replace("dest-" + Samples.AllStaffGroup, ""));
        Assert.Equal("dest-" + Samples.BreakGlassGroup, result.Desired["conditions"]!["users"]!["excludeGroups"]![0]!.GetValue<string>());
        Assert.Equal("dest-" + Samples.CustomStrength, result.Desired["grantControls"]!["authenticationStrength"]!["id"]!.GetValue<string>());
        Assert.Equal("enabledForReportingButNotEnforced", result.Desired["state"]!.GetValue<string>());
        Assert.Empty(result.Problems);

        // Values that mean the same everywhere stay as they are.
        Assert.Equal("All", result.Desired["conditions"]!["users"]!["includeUsers"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Leaves_the_source_export_untouched()
    {
        var item = Export(ConditionalAccess, Samples.ConditionalAccessPolicy());
        var before = Normalizer.ToStableJson(item.Settings);

        Transformer.Transform(item, PlanFor(item, (_, id, m) => m.MapTo(new ObjectRef("x" + id, "x"))), new TransformOptions());

        Assert.Equal(before, Normalizer.ToStableJson(item.Settings));
    }

    [Fact]
    public void Unmatched_references_are_problems()
    {
        var item = Export(ConditionalAccess, Samples.ConditionalAccessPolicy());
        var plan = PlanFor(item, (type, id, m) =>
        {
            if (type != ResourceRegistry.Group)
                m.MapTo(new ObjectRef(id, "same"));
        });

        var result = Transformer.Transform(item, plan, new TransformOptions());

        Assert.Equal(2, result.Problems.Count);
        Assert.All(result.Problems, p => Assert.Contains("group", p));
    }

    [Fact]
    public void Removing_an_excluded_group_is_flagged_as_widening_the_policy()
    {
        var item = Export(ConditionalAccess, Samples.ConditionalAccessPolicy());
        var plan = PlanFor(item, (type, id, m) =>
        {
            if (id == Samples.BreakGlassGroup) m.RemoveFromPolicies();
            else m.MapTo(new ObjectRef(id, "same"));
        });

        var result = Transformer.Transform(item, plan, new TransformOptions());

        Assert.Empty(result.Desired["conditions"]!["users"]!["excludeGroups"]!.AsArray());
        Assert.Contains(result.Changes, c => c.WidensPolicy);
    }

    [Fact]
    public void Removing_an_authentication_strength_clears_the_whole_grant()
    {
        var item = Export(ConditionalAccess, Samples.ConditionalAccessPolicy());
        var plan = PlanFor(item, (type, id, m) =>
        {
            if (type == ResourceRegistry.AuthenticationStrength) m.RemoveFromPolicies();
            else m.MapTo(new ObjectRef(id, "same"));
        });

        var result = Transformer.Transform(item, plan, new TransformOptions());

        Assert.Null(result.Desired["grantControls"]!["authenticationStrength"]);
    }

    [Fact]
    public void Objects_created_by_the_migration_get_a_placeholder()
    {
        var item = Export(ConditionalAccess, Samples.ConditionalAccessPolicy());
        var plan = PlanFor(item, (type, id, m) =>
        {
            if (type == ResourceRegistry.NamedLocation) m.Resolve(MatchKind.CreatedByMigration, new ObjectRef("", "HQ"));
            else if (type == ResourceRegistry.Group) m.CreateInDestination();
            else m.MapTo(new ObjectRef(id, "same"));
        });

        var result = Transformer.Transform(item, plan, new TransformOptions());

        var location = result.Desired["conditions"]!["locations"]!["excludeLocations"]![0]!.GetValue<string>();
        Assert.Equal(Transformer.Placeholder(ResourceRegistry.NamedLocation, Samples.HqLocation), location);
        Assert.True(Transformer.IsPlaceholder(location));
        Assert.True(Transformer.IsPlaceholder(result.Desired["conditions"]!["users"]!["includeGroups"]![0]!.GetValue<string>()));
    }

    [Fact]
    public void Removing_an_assigned_group_drops_that_assignment_and_removing_a_filter_clears_it()
    {
        var raw = JsonNode.Parse("""
            {
              "id": "p1", "name": "WIN - BitLocker", "roleScopeTagIds": ["0"],
              "assignments": [
                { "target": { "@odata.type": "#microsoft.graph.groupAssignmentTarget", "groupId": "keep-group",
                              "deviceAndAppManagementAssignmentFilterId": "filter-1", "deviceAndAppManagementAssignmentFilterType": "include" } },
                { "target": { "@odata.type": "#microsoft.graph.exclusionGroupAssignmentTarget", "groupId": "drop-group" } }
              ]
            }
            """)!.AsObject();
        var item = Export(SettingsCatalog, raw);
        var plan = PlanFor(item, (type, id, m) =>
        {
            if (id is "drop-group" or "filter-1") m.RemoveFromPolicies();
            else m.MapTo(new ObjectRef("dest-" + id, "same"));
        });

        var result = Transformer.Transform(item, plan, new TransformOptions());

        var assignment = Assert.Single(result.Desired["assignments"]!.AsArray())!["target"]!;
        Assert.Equal("dest-keep-group", assignment["groupId"]!.GetValue<string>());
        Assert.Null(assignment["deviceAndAppManagementAssignmentFilterId"]);
        Assert.Equal("none", assignment["deviceAndAppManagementAssignmentFilterType"]!.GetValue<string>());
        Assert.Contains(result.Changes, c => c.WidensPolicy);
    }

    [Fact]
    public void Disable_and_rename_options_apply()
    {
        var item = Export(ConditionalAccess, Samples.ConditionalAccessPolicy());
        var plan = PlanFor(item, (_, id, m) => m.MapTo(new ObjectRef(id, "same")));
        var key = MappingPlan.KeyOf(item.Type.Id, item.SourceId);

        var result = Transformer.Transform(item, plan, new TransformOptions { Disable = new HashSet<string> { key }, Rename = new HashSet<string> { key } });

        Assert.Equal("disabled", result.Desired["state"]!.GetValue<string>());
        Assert.Equal("CA001 - Require MFA for all users (migrated)", result.Desired["displayName"]!.GetValue<string>());
    }
}

public class PreflightTests
{
    static readonly ResourceType ConditionalAccess = ResourceRegistry.Get(ResourceRegistry.ConditionalAccessPolicy);

    static TenantInfo Destination(IEnumerable<string> roles, params string[] plans) =>
        new("dest-tenant", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false, roles.ToList(), [], [])
        {
            ServicePlans = plans.ToHashSet(StringComparer.OrdinalIgnoreCase),
        };

    static ExportedResource Policy(string id, string name, string json)
    {
        var settings = Normalizer.Normalize(JsonNode.Parse(json)!.AsObject(), ConditionalAccess);
        return new ExportedResource(ConditionalAccess, id, name, settings, DependencyExtractor.Extract(settings, ConditionalAccess));
    }

    static readonly ExportedResource Simple = Policy("p1", "CA004 - Block legacy auth", """
        { "displayName": "CA004 - Block legacy auth", "state": "enabled",
          "conditions": { "users": { "includeUsers": ["All"], "excludeUsers": [], "excludeGroups": [] } } }
        """);

    static readonly ExportedResource Risky = Policy("p2", "CA010 - Risky sign-ins", """
        { "displayName": "CA010 - Risky sign-ins", "state": "enabled",
          "conditions": { "signInRiskLevels": ["high"], "users": { "includeUsers": ["All"], "excludeUsers": ["breakglass"] } } }
        """);

    static FakeGraph EmptyDestination() => new FakeGraph().Collection(ConditionalAccess.ListPath);

    static MappingPlan Plan(params Mapping.Mapping[] mappings) => new(mappings);

    static Mapping.Mapping UserMapping(string id, bool resolved)
    {
        var mapping = new Mapping.Mapping { TargetType = ResourceRegistry.User, Source = new ObjectRef(id, "Break glass"), UsedBy = ["CA010 - Risky sign-ins"] };
        if (resolved) mapping.Resolve(MatchKind.SameUsername, new ObjectRef("dest-" + id, "Break glass"));
        return mapping;
    }

    [Fact]
    public async Task A_clean_policy_is_created_in_report_only()
    {
        var report = await PreflightCheck.RunAsync(EmptyDestination(), Destination([DirectoryRoles.GlobalAdministrator], "AAD_PREMIUM"), [Simple], Plan(), new PreflightChoices());

        var policy = Assert.Single(report.Policies);
        Assert.Equal(Outcome.Create, policy.Outcome);
        Assert.False(report.HasBlockers);
        Assert.True(report.CanDeploy);
        Assert.Contains(report.Findings, f => f.Severity == Severity.Info && f.Title.Contains("report-only"));
        Assert.Contains(report.Findings, f => f.Title == "Policies that exclude nobody");
    }

    [Fact]
    public async Task Missing_role_blocks_the_policies_of_that_service()
    {
        var report = await PreflightCheck.RunAsync(EmptyDestination(), Destination([DirectoryRoles.GlobalReader], "AAD_PREMIUM"), [Simple], Plan(), new PreflightChoices());

        Assert.Equal(Outcome.Blocked, Assert.Single(report.Policies).Outcome);
        Assert.Contains(report.Findings, f => f.Severity == Severity.Blocker && f.Title.Contains("Conditional Access Administrator"));
        Assert.False(report.CanDeploy);
    }

    [Fact]
    public async Task No_entra_premium_blocks_conditional_access()
    {
        var report = await PreflightCheck.RunAsync(EmptyDestination(), Destination([DirectoryRoles.GlobalAdministrator]), [Simple], Plan(), new PreflightChoices());

        Assert.Equal(Outcome.Blocked, Assert.Single(report.Policies).Outcome);
    }

    [Fact]
    public async Task Risk_policies_without_P2_are_disabled_or_skipped_by_choice()
    {
        var destination = Destination([DirectoryRoles.GlobalAdministrator], "AAD_PREMIUM");
        var plan = Plan(UserMapping("breakglass", resolved: true));

        var disabled = await PreflightCheck.RunAsync(EmptyDestination(), destination, [Risky], plan, new PreflightChoices());
        var skipped = await PreflightCheck.RunAsync(EmptyDestination(), destination, [Risky], plan, new PreflightChoices { RiskPoliciesWithoutP2 = LicenseChoice.Skip });

        Assert.Equal(Outcome.Create, disabled.Policies[0].Outcome);
        Assert.Equal("disabled", disabled.Policies[0].Policy.Desired["state"]!.GetValue<string>());
        Assert.Equal(Outcome.Skip, skipped.Policies[0].Outcome);
        Assert.Contains(disabled.Findings, f => f.Choice == FindingChoice.RiskPoliciesWithoutP2);
    }

    [Fact]
    public async Task Unmatched_objects_block_the_policies_that_use_them()
    {
        var plan = Plan(UserMapping("breakglass", resolved: false));

        var report = await PreflightCheck.RunAsync(EmptyDestination(), Destination([DirectoryRoles.GlobalAdministrator], "AAD_PREMIUM_P2"), [Risky, Simple], plan, new PreflightChoices());

        Assert.Equal(Outcome.Blocked, report.Policies.Single(p => p.Source.SourceId == "p2").Outcome);
        Assert.Equal(Outcome.Create, report.Policies.Single(p => p.Source.SourceId == "p1").Outcome);
        Assert.Contains(report.Findings, f => f.Severity == Severity.Blocker && f.Title.Contains("no match"));
    }

    [Fact]
    public async Task Taken_names_are_skipped_or_renamed_by_choice()
    {
        var destination = new FakeGraph().Collection(ConditionalAccess.ListPath, """{ "id": "x", "displayName": "ca004 - block legacy auth" }""");
        var info = Destination([DirectoryRoles.GlobalAdministrator], "AAD_PREMIUM");

        var skip = await PreflightCheck.RunAsync(destination, info, [Simple], Plan(), new PreflightChoices());
        var rename = await PreflightCheck.RunAsync(destination, info, [Simple], Plan(), new PreflightChoices { NameConflicts = ConflictChoice.CreateWithSuffix });

        Assert.Equal(Outcome.Skip, skip.Policies[0].Outcome);
        Assert.Equal(Outcome.Create, rename.Policies[0].Outcome);
        Assert.EndsWith("(migrated)", rename.Policies[0].Policy.Desired["displayName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Manual_matches_that_no_longer_exist_are_blockers()
    {
        var mapping = UserMapping("breakglass", resolved: false);
        mapping.MapTo(new ObjectRef("deleted-user", "Old break glass"));
        var destination = EmptyDestination().Failing(GraphQuery.Item("v1.0/users", "deleted-user", "id"));

        var report = await PreflightCheck.RunAsync(destination, Destination([DirectoryRoles.GlobalAdministrator], "AAD_PREMIUM_P2"), [Risky], Plan(mapping), new PreflightChoices());

        Assert.Equal(Outcome.Blocked, Assert.Single(report.Policies).Outcome);
        Assert.Contains(report.Findings, f => f.Title.Contains("not found in the destination"));
    }

    [Fact]
    public async Task Empty_passwords_are_reported()
    {
        var wifi = ResourceRegistry.Get(ResourceRegistry.DeviceConfiguration);
        var settings = Normalizer.Normalize(JsonNode.Parse("""{ "displayName": "Corp Wi-Fi", "ssid": "corp", "preSharedKey": null }""")!.AsObject(), wifi);
        var item = new ExportedResource(wifi, "w1", "Corp Wi-Fi", settings, []);
        var destination = new FakeGraph().Collection(wifi.ListPath);

        var report = await PreflightCheck.RunAsync(destination, Destination([DirectoryRoles.GlobalAdministrator], "INTUNE_A"), [item], Plan(), new PreflightChoices());

        var secrets = Assert.Single(report.Findings, f => f.Title == "Secrets aren't copied");
        Assert.Equal("Corp Wi-Fi: preSharedKey", Assert.Single(secrets.Items));
    }

    [Fact]
    public async Task A_service_the_destination_doesnt_offer_blocks_its_policies()
    {
        var dlp = ResourceRegistry.Get(ResourceRegistry.DlpPolicy);
        var settings = Normalizer.Normalize(JsonNode.Parse("""{ "Name": "Block SS", "Mode": "Enable" }""")!.AsObject(), dlp);
        var item = new ExportedResource(dlp, "d1", "Block SS", settings, []);
        var compliance = new FakePowerShell().Failing("Get-DlpCompliancePolicy",
            new PowerShellException("The term 'Get-DlpCompliancePolicy' is not recognized as a name of a cmdlet, function, script file, or executable program."));

        var report = await PreflightCheck.RunAsync(new TenantClients(new FakeGraph(), Compliance: compliance),
            Destination([DirectoryRoles.GlobalAdministrator]), [item], Plan(), new PreflightChoices());

        Assert.Equal(Outcome.Blocked, Assert.Single(report.Policies).Outcome);
        Assert.Contains(report.Findings, f => f.Severity == Severity.Blocker && f.Title == "DLP policies aren't available in the destination");
        Assert.DoesNotContain(report.Findings, f => f.Title.StartsWith("Couldn't check names"));
    }

    [Fact]
    public async Task Blocked_policies_can_be_left_out_to_deploy_the_rest()
    {
        var blocked = Policy("p1", "CA001", """{ "displayName": "CA001", "state": "enabled", "conditions": { "users": { "includeUsers": ["All"], "excludeUsers": ["u-missing"] } } }""");
        var fine = Policy("p2", "CA002", """{ "displayName": "CA002", "state": "enabled", "conditions": { "users": { "includeUsers": ["All"] } } }""");
        var plan = Plan(UserMapping("u-missing", resolved: false));

        var strict = await PreflightCheck.RunAsync(EmptyDestination(), Destination([DirectoryRoles.GlobalAdministrator], "AAD_PREMIUM"), [blocked, fine], plan, new PreflightChoices());
        var lenient = await PreflightCheck.RunAsync(EmptyDestination(), Destination([DirectoryRoles.GlobalAdministrator], "AAD_PREMIUM"), [blocked, fine], plan, new PreflightChoices { SkipBlocked = true });

        Assert.True(strict.HasBlockers);
        Assert.False(strict.CanDeploy);
        Assert.True(lenient.CanDeploy);
        Assert.Equal(1, lenient.CreateCount);
        Assert.Equal(1, lenient.BlockedCount);
    }

    [Fact]
    public async Task A_policy_using_a_blocked_policy_is_blocked_too()
    {
        var label = ResourceRegistry.Get(ResourceRegistry.SensitivityLabel);
        var labelPolicy = ResourceRegistry.Get(ResourceRegistry.LabelPolicy);
        var labelSettings = Normalizer.Normalize(JsonNode.Parse("""{ "Name": "Secret", "DisplayName": "Secret" }""")!.AsObject(), label);
        var policySettings = Normalizer.Normalize(JsonNode.Parse("""{ "Name": "Everyone", "Labels": ["l1"] }""")!.AsObject(), labelPolicy);
        var labelItem = new ExportedResource(label, "l1", "Secret", labelSettings, []);
        var policyItem = new ExportedResource(labelPolicy, "lp1", "Everyone", policySettings, DependencyExtractor.Extract(policySettings, labelPolicy));
        Assert.Contains(policyItem.Dependencies, d => d.TargetType == ResourceRegistry.SensitivityLabel);

        // Labels can't be read in the destination, so the label is blocked; its label policy has to follow.
        var compliance = new FakePowerShell().Failing("Get-Label",
            new PowerShellException("The term 'Get-Label' is not recognized as a name of a cmdlet, function, script file, or executable program."));
        var report = await PreflightCheck.RunAsync(new TenantClients(new FakeGraph(), Compliance: compliance),
            Destination([DirectoryRoles.GlobalAdministrator]), [labelItem, policyItem], LabelCreated(), new PreflightChoices());

        var policy = Assert.Single(report.Policies, p => p.Source.SourceId == "lp1");
        Assert.Equal(Outcome.Blocked, policy.Outcome);
        Assert.True(policy.Reasons.Any(r => r.Contains("\"Secret\"")), string.Join(" | ", policy.Reasons));
    }

    static MappingPlan LabelCreated()
    {
        var mapping = new Mapping.Mapping { TargetType = ResourceRegistry.SensitivityLabel, Source = new ObjectRef("l1", "Secret"), UsedBy = ["Everyone"] };
        mapping.Resolve(MatchKind.CreatedByMigration, new ObjectRef("", "Secret"));
        return Plan(mapping);
    }

    [Fact]
    public async Task Unlicensed_policies_dont_get_their_names_checked()
    {
        var wifi = ResourceRegistry.Get(ResourceRegistry.DeviceConfiguration);
        var settings = Normalizer.Normalize(JsonNode.Parse("""{ "displayName": "Corp Wi-Fi", "ssid": "corp" }""")!.AsObject(), wifi);
        var item = new ExportedResource(wifi, "w1", "Corp Wi-Fi", settings, []);

        // No Intune license, and Graph refuses the list (FakeGraph has nothing at that path).
        var report = await PreflightCheck.RunAsync(new FakeGraph(), Destination([DirectoryRoles.GlobalAdministrator]), [item], Plan(), new PreflightChoices());

        Assert.Contains(report.Findings, f => f.Title == "No Intune license in the destination");
        Assert.DoesNotContain(report.Findings, f => f.Title.StartsWith("Couldn't check names"));
    }
}

public class LineDiffTests
{
    [Fact]
    public void Marks_removed_and_added_lines()
    {
        var diff = LineDiff.Compare("a\nb\nc", "a\nx\nc");

        Assert.Equal(
            new[] { (DiffKind.Same, "a"), (DiffKind.Removed, "b"), (DiffKind.Added, "x"), (DiffKind.Same, "c") },
            diff.Select(d => (d.Kind, d.Text)));
    }

    [Fact]
    public void Identical_text_has_no_changes()
    {
        Assert.All(LineDiff.Compare("{\n  \"a\": 1\n}", "{\n  \"a\": 1\n}"), d => Assert.Equal(DiffKind.Same, d.Kind));
    }
}
