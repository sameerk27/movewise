using System.Text.Json.Nodes;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

public class TeamsPolicyTests
{
    static readonly ResourceType Meeting = ResourceRegistry.Get(ResourceRegistry.TeamsMeetingPolicy);
    static readonly TenantInfo Source = new("src-tenant", "Contoso", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false, [], [], []);
    static readonly TenantInfo Destination = new("dst-tenant", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false, [], [], []);

    const string Execs = "aaaaaaaa-0000-4000-8000-0000000000e1";
    const string Sales = "aaaaaaaa-0000-4000-8000-0000000000e2";

    static FakePowerShell TeamsSource() => new FakePowerShell()
        .Returns("Get-CsTeamsMeetingPolicy",
            """{ "Identity": "Global", "AllowCloudRecording": true }""",
            """{ "Identity": "Tag:AllOn", "AllowCloudRecording": true }""",
            """{ "Identity": "Tag:Execs", "Key": "[{urn:schema:Microsoft.Rtc.Management.Policy.Teams.2015}TeamsMeetingPolicy,Tag:Execs]", "AllowCloudRecording": false, "Description": "For executives" }""")
        .Returns("Get-CsGroupPolicyAssignment",
            $$"""{ "GroupId": "{{Sales}}", "PolicyType": "TeamsMeetingPolicy", "PolicyName": "Execs", "Rank": 2 }""",
            $$"""{ "GroupId": "{{Execs}}", "PolicyType": "TeamsMeetingPolicy", "PolicyName": "Execs", "Rank": 1 }""",
            """{ "GroupId": "other", "PolicyType": "TeamsMeetingPolicy", "PolicyName": "AllOn", "Rank": 1 }""");

    static async Task<ExportedResource> ExportExecs()
    {
        var result = await Exporter.ExportAsync(new TenantClients(new FakeGraph(), Teams: TeamsSource()), [Meeting]);
        Assert.Empty(result.Warnings);
        return Assert.Single(result.Items);
    }

    [Fact]
    public async Task Reads_custom_policies_with_their_group_assignments_in_order()
    {
        var item = await ExportExecs();

        Assert.Equal("Execs", item.DisplayName);
        Assert.False(item.Settings.ContainsKey("Identity"));
        Assert.False(item.Settings.ContainsKey("Key"));

        var assignments = item.Settings["groupAssignments"]!.AsArray();
        Assert.Equal(new[] { Execs, Sales }, assignments.Select(a => a!["GroupId"]!.GetValue<string>()).ToArray());
        Assert.Equal(2, item.Dependencies.Count(d => d.TargetType == ResourceRegistry.Group));
    }

    static DeployRun PlanRun(ExportedResource item)
    {
        // Execs exists in the destination; Sales is created by the run.
        var execs = new Mapping.Mapping { TargetType = ResourceRegistry.Group, Source = new ObjectRef(Execs, "Execs"), UsedBy = [] };
        execs.MapTo(new ObjectRef("dest-execs", "Execs"));
        var sales = new Mapping.Mapping
        {
            TargetType = ResourceRegistry.Group,
            Source = new ObjectRef(Sales, "Sales"),
            UsedBy = [],
            SourceDetails = JsonNode.Parse("""{ "displayName": "Sales", "mailNickname": "sales", "securityEnabled": true, "groupTypes": [] }""")!.AsObject(),
        };
        sales.CreateInDestination();
        var plan = new MappingPlan([execs, sales]);

        var report = new PreflightReport([new PolicyResult(Transformer.Transform(item, plan, new TransformOptions()), Outcome.Create, [])], []);
        return DeployPlanner.Plan(report, plan, Source, Destination);
    }

    [Fact]
    public async Task Creates_the_policy_by_identity_then_assigns_it_to_its_groups()
    {
        var run = PlanRun(await ExportExecs());
        var teams = new FakePowerShell().Accepts("New-CsTeamsMeetingPolicy", "Identity", "AllowCloudRecording", "Description");
        var graph = new FakeGraph();

        await Deployer.RunAsync(new TenantClients(graph, Teams: teams), run, () => Task.CompletedTask);

        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
        var created = Assert.Single(teams.CallsTo("New-CsTeamsMeetingPolicy"));
        Assert.Equal("Execs", created["Identity"]!.GetValue<string>());
        Assert.False(created["AllowCloudRecording"]!.GetValue<bool>());
        Assert.False(created.ContainsKey("groupAssignments"));

        var assigned = teams.CallsTo("New-CsGroupPolicyAssignment").ToList();
        Assert.Equal(new[] { "dest-execs", "new-1" }, assigned.Select(a => a["GroupId"]!.GetValue<string>()).ToArray());
        Assert.All(assigned, a => Assert.Equal("TeamsMeetingPolicy", a["PolicyType"]!.GetValue<string>()));
        Assert.All(assigned, a => Assert.Equal("Execs", a["PolicyName"]!.GetValue<string>()));
        Assert.Equal(1, assigned[0]["Rank"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_group_that_already_had_a_policy_of_this_type_is_never_counted_as_assigned()
    {
        var run = PlanRun(await ExportExecs());
        var teams = new FakePowerShell().Failing("New-CsGroupPolicyAssignment",
            new PowerShellException("The group already exists in another policy assignment of this type."), times: 2);
        var tenant = new TenantClients(new FakeGraph(), Teams: teams);
        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);

        // The retry gets the same refusal: it's the destination's own assignment, not one this run made.
        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);

        var policy = run.Steps.Single(s => s.TargetType == Meeting.Id);
        Assert.Equal(StepStatus.Failed, policy.Status);
        Assert.Contains("already exists", policy.Message);
        Assert.DoesNotContain("dest-execs", policy.CreatedAssignments);

        await Deployer.RollbackAsync(tenant, run, () => Task.CompletedTask);
        Assert.DoesNotContain(teams.CallsTo("Remove-CsGroupPolicyAssignment"), c => c["GroupId"]!.GetValue<string>() == "dest-execs");
    }

    [Fact]
    public async Task Rollback_removes_the_assignments_before_the_policy()
    {
        var run = PlanRun(await ExportExecs());
        var teams = new FakePowerShell();
        var tenant = new TenantClients(new FakeGraph(), Teams: teams);
        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);

        await Deployer.RollbackAsync(tenant, run, () => Task.CompletedTask);

        var removals = teams.Calls.Where(c => c.Cmdlet.StartsWith("Remove-")).Select(c => c.Cmdlet).ToArray();
        Assert.Equal(new[] { "Remove-CsGroupPolicyAssignment", "Remove-CsGroupPolicyAssignment", "Remove-CsTeamsMeetingPolicy" }, removals);
        Assert.NotNull(run.RolledBack);
    }
}
