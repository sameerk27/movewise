using System.Net;
using System.Text.Json.Nodes;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

public class DeployTests
{
    static readonly ResourceType Location = ResourceRegistry.Get(ResourceRegistry.NamedLocation);
    static readonly ResourceType ConditionalAccess = ResourceRegistry.Get(ResourceRegistry.ConditionalAccessPolicy);
    static readonly ResourceType DeviceConfiguration = ResourceRegistry.Get(ResourceRegistry.DeviceConfiguration);
    static readonly ResourceType IosProtection = ResourceRegistry.Get(ResourceRegistry.IosAppProtectionPolicy);

    static readonly TenantInfo Source = new("src-tenant", "Contoso", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false, [], [], []);
    static readonly TenantInfo Destination = new("dst-tenant", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false, [], [], []);

    static ExportedResource Export(ResourceType type, string id, string json, string? assignments = null)
    {
        var settings = Normalizer.Normalize(JsonNode.Parse(json)!.AsObject(), type);
        if (assignments is not null)
            settings["assignments"] = JsonNode.Parse(assignments);
        return new ExportedResource(type, id, settings[type.IdentityProperty]!.GetValue<string>(), settings, DependencyExtractor.Extract(settings, type));
    }

    static ExportedResource HqLocation() => Export(Location, Samples.HqLocation, $$"""
        { "@odata.type": "#microsoft.graph.ipNamedLocation", "id": "{{Samples.HqLocation}}", "displayName": "HQ", "isTrusted": true, "ipRanges": [] }
        """);

    static ExportedResource CaPolicy() => Export(ConditionalAccess, "ca-1", Samples.ConditionalAccessPolicy().ToJsonString());

    static ExportedResource WifiProfile() => Export(DeviceConfiguration, "dc-1", """
        { "@odata.type": "#microsoft.graph.windows10GeneralConfiguration", "id": "dc-1", "displayName": "Windows baseline", "roleScopeTagIds": ["0"] }
        """, $$"""
        [ { "target": { "@odata.type": "#microsoft.graph.groupAssignmentTarget", "groupId": "{{Samples.AllStaffGroup}}",
            "deviceAndAppManagementAssignmentFilterId": null, "deviceAndAppManagementAssignmentFilterType": "none" } } ]
        """);

    /// <summary>All staff is created by the run, HQ is migrated with the policies, everything else exists in the destination.</summary>
    static MappingPlan PlanFor(IEnumerable<ExportedResource> items) => new(items
        .SelectMany(i => i.Dependencies)
        .DistinctBy(d => (d.TargetType, d.Value))
        .Select(d =>
        {
            var mapping = new Mapping.Mapping
            {
                TargetType = d.TargetType,
                Source = new ObjectRef(d.Value, d.Value == Samples.AllStaffGroup ? "All staff" : d.Value == Samples.HqLocation ? "HQ" : d.Value),
                UsedBy = [],
                SourceDetails = d.Value == Samples.AllStaffGroup
                    ? JsonNode.Parse("""{ "displayName": "All staff", "mailNickname": "allstaff", "securityEnabled": true, "mailEnabled": false, "groupTypes": [] }""")!.AsObject()
                    : new JsonObject(),
            };
            if (d.Value == Samples.AllStaffGroup)
                mapping.CreateInDestination();
            else if (d.Value == Samples.HqLocation)
                mapping.Resolve(MatchKind.CreatedByMigration, new ObjectRef("", "HQ"));
            else
                mapping.MapTo(new ObjectRef("dest-" + d.Value, "existing"));
            return mapping;
        }));

    static DeployRun PlanRun(params (ExportedResource Item, Outcome Outcome)[] items)
    {
        var plan = PlanFor(items.Select(i => i.Item));
        var report = new PreflightReport(
            items.Select(i => new PolicyResult(Transformer.Transform(i.Item, plan, new TransformOptions()), i.Outcome, [])).ToList(),
            []);
        return DeployPlanner.Plan(report, plan, Source, Destination);
    }

    static DeployRun FullRun() => PlanRun((CaPolicy(), Outcome.Create), (WifiProfile(), Outcome.Create), (HqLocation(), Outcome.Create));

    static Task Deploy(FakeGraph graph, DeployRun run) => Deployer.RunAsync(graph, run, () => Task.CompletedTask);

    static DeployStep Step(DeployRun run, string type) => run.Steps.Single(s => s.TargetType == type);

    static JsonNode BodyPostedTo(FakeGraph graph, string path) => graph.Posts.Single(p => p.Path == path).Body;

    [Fact]
    public void Plans_groups_first_then_policies_in_dependency_order()
    {
        var run = FullRun();

        Assert.Equal(
            new[] { ResourceRegistry.Group, ResourceRegistry.NamedLocation, ResourceRegistry.ConditionalAccessPolicy, ResourceRegistry.DeviceConfiguration },
            run.Steps.Select(s => s.TargetType).ToArray());
        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Pending, s.Status));

        var group = Step(run, ResourceRegistry.Group);
        Assert.Equal("allstaff", group.Body["mailNickname"]!.GetValue<string>());
        Assert.Contains(group.Notes, n => n.Contains("no members"));
    }

    [Fact]
    public void Takes_assignments_out_of_the_policy_to_send_after_it_exists()
    {
        var step = Step(FullRun(), ResourceRegistry.DeviceConfiguration);

        Assert.False(step.Body.ContainsKey("assignments"));
        Assert.Single(step.Assignments!);
        Assert.Equal(Transformer.Placeholder(ResourceRegistry.Group, Samples.AllStaffGroup), step.Assignments![0]!["target"]!["groupId"]!.GetValue<string>());
    }

    [Fact]
    public void Creates_only_groups_that_a_policy_being_created_uses()
    {
        var run = PlanRun((CaPolicy(), Outcome.Skip), (WifiProfile(), Outcome.Skip), (HqLocation(), Outcome.Create));

        Assert.Equal(ResourceRegistry.NamedLocation, Assert.Single(run.Steps).TargetType);
    }

    [Fact]
    public void Skips_a_policy_whose_new_dependency_is_not_being_created()
    {
        var run = PlanRun((CaPolicy(), Outcome.Create), (HqLocation(), Outcome.Skip));

        var policy = Step(run, ResourceRegistry.ConditionalAccessPolicy);
        Assert.Equal(StepStatus.Skipped, policy.Status);
        Assert.Contains("\"HQ\"", policy.Message);
    }

    [Fact]
    public async Task Creates_in_order_and_swaps_placeholders_for_the_new_ids()
    {
        var graph = new FakeGraph();
        var run = FullRun();

        await Deploy(graph, run);

        Assert.Equal(
            new[] { "v1.0/groups", Location.CreatePath, ConditionalAccess.CreatePath, DeviceConfiguration.CreatePath, $"{DeviceConfiguration.CreatePath}/new-4/assign" },
            graph.Posts.Select(p => p.Path).ToArray());
        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
        Assert.NotNull(run.Completed);

        var policy = BodyPostedTo(graph, ConditionalAccess.CreatePath);
        Assert.Equal("new-1", policy["conditions"]!["users"]!["includeGroups"]![0]!.GetValue<string>());
        Assert.Contains("new-2", policy["conditions"]!["locations"]!["excludeLocations"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.DoesNotContain("movewise:new", policy.ToJsonString());

        var assign = BodyPostedTo(graph, $"{DeviceConfiguration.CreatePath}/new-4/assign");
        Assert.Equal("new-1", assign["assignments"]![0]!["target"]!["groupId"]!.GetValue<string>());
        Assert.False(BodyPostedTo(graph, DeviceConfiguration.CreatePath).AsObject().ContainsKey("assignments"));
    }

    [Fact]
    public async Task Sends_only_the_authentication_strength_id_with_a_conditional_access_policy()
    {
        var graph = new FakeGraph();

        await Deploy(graph, FullRun());

        var strength = BodyPostedTo(graph, ConditionalAccess.CreatePath)["grantControls"]!["authenticationStrength"]!.AsObject();
        Assert.Equal("dest-" + Samples.CustomStrength, strength["id"]!.GetValue<string>());
        Assert.Single(strength);
    }

    [Fact]
    public async Task Sets_app_protection_apps_after_creating_the_policy()
    {
        var policy = Export(IosProtection, "ios-1", """
            { "id": "ios-1", "displayName": "iOS MAM", "roleScopeTagIds": ["0"],
              "apps": [ { "id": "com.microsoft.outlook.ios", "version": "3", "mobileAppIdentifier": { "@odata.type": "#microsoft.graph.iosMobileAppIdentifier", "bundleId": "com.microsoft.outlook" } } ] }
            """, "[]");
        var graph = new FakeGraph();

        await Deploy(graph, PlanRun((policy, Outcome.Create)));

        Assert.False(BodyPostedTo(graph, IosProtection.CreatePath).AsObject().ContainsKey("apps"));
        var app = Assert.Single(BodyPostedTo(graph, $"{IosProtection.CreatePath}/new-1/targetApps")["apps"]!.AsArray())!.AsObject();
        Assert.Equal("com.microsoft.outlook", app["mobileAppIdentifier"]!["bundleId"]!.GetValue<string>());
        Assert.Single(app);
    }

    [Fact]
    public async Task A_failed_step_does_not_stop_the_rest_and_what_needs_it_is_skipped()
    {
        var graph = new FakeGraph().FailingWrite(Location.CreatePath, new GraphException(HttpStatusCode.BadRequest, "BadRequest", "Invalid IP range"));
        var run = FullRun();

        await Deploy(graph, run);

        Assert.Equal(StepStatus.Failed, Step(run, ResourceRegistry.NamedLocation).Status);
        Assert.Contains("Invalid IP range", Step(run, ResourceRegistry.NamedLocation).Message);
        Assert.Equal(StepStatus.Skipped, Step(run, ResourceRegistry.ConditionalAccessPolicy).Status);
        Assert.Equal(StepStatus.Done, Step(run, ResourceRegistry.DeviceConfiguration).Status);
    }

    [Fact]
    public async Task Resuming_an_interrupted_create_uses_the_object_it_finds_by_name()
    {
        var graph = new FakeGraph().Collection(Location.ListPath, """{ "id": "existing-loc", "displayName": "HQ" }""");
        var run = FullRun();
        Step(run, ResourceRegistry.NamedLocation).Status = StepStatus.Creating;

        await Deploy(graph, run);

        Assert.DoesNotContain(graph.Posts, p => p.Path == Location.CreatePath);
        var location = Step(run, ResourceRegistry.NamedLocation);
        Assert.Equal("existing-loc", location.DestinationId);
        Assert.Contains(location.Notes, n => n.Contains("interrupted"));
        Assert.Contains("existing-loc", BodyPostedTo(graph, ConditionalAccess.CreatePath).ToJsonString());
    }

    [Fact]
    public async Task A_create_with_no_answer_is_checked_before_retrying()
    {
        var graph = new FakeGraph().FailingWrite(ConditionalAccess.CreatePath, new HttpRequestException("Connection reset"), times: 1);
        var run = FullRun();

        await Deploy(graph, run);

        var policy = Step(run, ResourceRegistry.ConditionalAccessPolicy);
        Assert.Equal(StepStatus.Creating, policy.Status);
        Assert.NotNull(policy.Message);
        Assert.Equal(1, run.RemainingCount);

        // Not found by name, so it's created on retry.
        await Deploy(graph, run);

        Assert.Equal(StepStatus.Done, policy.Status);
        Assert.Contains(ConditionalAccess.ListPath, graph.Requests);
        Assert.Single(graph.Posts, p => p.Path == ConditionalAccess.CreatePath);
    }

    [Fact]
    public async Task Retrying_after_a_failed_assignment_only_assigns()
    {
        var assign = $"{DeviceConfiguration.CreatePath}/new-4/assign";
        var graph = new FakeGraph().FailingWrite(assign, new GraphException(HttpStatusCode.BadRequest, "BadRequest", "Bad target"), times: 1);
        var run = FullRun();

        await Deploy(graph, run);

        var profile = Step(run, ResourceRegistry.DeviceConfiguration);
        Assert.Equal(StepStatus.Failed, profile.Status);
        Assert.Equal("new-4", profile.DestinationId);
        Assert.StartsWith("Created, but", profile.Message);

        await Deploy(graph, run);

        Assert.Equal(StepStatus.Done, profile.Status);
        Assert.Single(graph.Posts, p => p.Path == DeviceConfiguration.CreatePath);
        Assert.Single(graph.Posts, p => p.Path == assign);
    }

    [Fact]
    public async Task A_skipped_policy_is_created_on_resume_once_what_it_needs_exists()
    {
        var graph = new FakeGraph().FailingWrite(Location.CreatePath, new GraphException(HttpStatusCode.TooManyRequests, "TooManyRequests", "Slow down"), times: 1);
        var run = FullRun();
        await Deploy(graph, run);
        var policy = Step(run, ResourceRegistry.ConditionalAccessPolicy);
        Assert.Equal(StepStatus.Skipped, policy.Status);
        Assert.Equal(2, run.RemainingCount);

        await Deploy(graph, run);

        Assert.Equal(StepStatus.Done, Step(run, ResourceRegistry.NamedLocation).Status);
        Assert.Equal(StepStatus.Done, policy.Status);
        Assert.Contains("new-", BodyPostedTo(graph, ConditionalAccess.CreatePath).ToJsonString());
        Assert.Equal(0, run.RemainingCount);
    }

    static string GroupLookup(string name) =>
        GraphQuery.Where("v1.0/groups", $"displayName eq {GraphQuery.Literal(name)}", "id,createdDateTime", top: 10);

    static DeployStep InterruptedGroup(DeployRun run)
    {
        var group = Step(run, ResourceRegistry.Group);
        group.Status = StepStatus.Creating;
        group.CreateSent = DateTimeOffset.UtcNow;
        return group;
    }

    [Fact]
    public async Task Rolling_back_an_interrupted_group_create_leaves_an_older_group_with_its_name()
    {
        var graph = new FakeGraph().Object(GroupLookup("All staff"), """{ "value": [ { "id": "theirs", "createdDateTime": "2020-01-01T00:00:00Z" } ] }""");
        var run = FullRun();
        var group = InterruptedGroup(run);

        await Deployer.RollbackAsync(graph, run, () => Task.CompletedTask);

        Assert.Empty(graph.Deletes);
        Assert.Equal(StepStatus.RolledBack, group.Status);
        Assert.Null(group.DestinationId);
    }

    [Fact]
    public async Task Resuming_an_interrupted_group_create_makes_a_new_group_rather_than_using_an_older_one()
    {
        var graph = new FakeGraph().Object(GroupLookup("All staff"), """{ "value": [ { "id": "theirs", "createdDateTime": "2020-01-01T00:00:00Z" } ] }""");
        var run = FullRun();
        var group = InterruptedGroup(run);

        await Deploy(graph, run);

        Assert.Single(graph.Posts, p => p.Path == "v1.0/groups");
        Assert.NotEqual("theirs", group.DestinationId);
        Assert.DoesNotContain("theirs", BodyPostedTo(graph, ConditionalAccess.CreatePath).ToJsonString());
    }

    [Fact]
    public async Task Resuming_an_interrupted_group_create_uses_the_group_it_made()
    {
        var run = FullRun();
        var group = InterruptedGroup(run);
        var madeNow = DateTimeOffset.UtcNow.ToString("o");
        var graph = new FakeGraph().Object(GroupLookup("All staff"), $$"""{ "value": [ { "id": "ours", "createdDateTime": "{{madeNow}}" } ] }""");

        await Deploy(graph, run);

        Assert.DoesNotContain(graph.Posts, p => p.Path == "v1.0/groups");
        Assert.Equal("ours", group.DestinationId);
    }

    [Fact]
    public async Task Rollback_deletes_what_the_run_created_newest_first()
    {
        var graph = new FakeGraph();
        var run = FullRun();
        await Deploy(graph, run);

        await Deployer.RollbackAsync(graph, run, () => Task.CompletedTask);

        Assert.Equal(
            new[] { $"{DeviceConfiguration.CreatePath}/new-4", $"{ConditionalAccess.CreatePath}/new-3", $"{Location.CreatePath}/new-2", "v1.0/groups/new-1" },
            graph.Deletes.ToArray());
        Assert.All(run.Steps, s => Assert.Equal(StepStatus.RolledBack, s.Status));
        Assert.NotNull(run.RolledBack);
        Assert.False(run.CanRollBack);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Deploy(graph, run));
    }

    [Fact]
    public async Task Rollback_counts_an_object_already_deleted_as_gone_and_reports_other_failures()
    {
        var graph = new FakeGraph()
            .FailingWrite($"{ConditionalAccess.CreatePath}/new-3", new GraphException(HttpStatusCode.NotFound, "NotFound", "Gone"))
            .FailingWrite("v1.0/groups/new-1", new GraphException(HttpStatusCode.Forbidden, "Forbidden", "Not allowed"));
        var run = FullRun();
        await Deploy(graph, run);

        await Deployer.RollbackAsync(graph, run, () => Task.CompletedTask);

        Assert.Equal(StepStatus.RolledBack, Step(run, ResourceRegistry.ConditionalAccessPolicy).Status);
        Assert.Equal(StepStatus.RollbackFailed, Step(run, ResourceRegistry.Group).Status);
        Assert.Null(run.RolledBack);
        Assert.True(run.CanRollBack);
    }

    [Fact]
    public async Task A_saved_run_loads_back_the_same()
    {
        var folder = Path.Combine(Path.GetTempPath(), "movewise-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var run = FullRun();
            run.Folder = folder;
            Directory.CreateDirectory(folder);
            await Deploy(new FakeGraph().FailingWrite(ConditionalAccess.CreatePath, new GraphException(HttpStatusCode.BadRequest, "BadRequest", "Nope")), run);
            await RunStore.SaveAsync(run);

            var loaded = RunStore.Load(Path.Combine(folder, RunStore.FileName));

            Assert.Equal(run.RunId, loaded.RunId);
            Assert.Equal(folder, loaded.Folder);
            Assert.Equal(run.Steps.Select(s => (s.Key, s.Status, s.DestinationId)), loaded.Steps.Select(s => (s.Key, s.Status, s.DestinationId)));
            Assert.Equal(run.Steps[3].Body.ToJsonString(), loaded.Steps[3].Body.ToJsonString());
            Assert.Equal(run.Steps[3].Assignments!.ToJsonString(), loaded.Steps[3].Assignments!.ToJsonString());
            Assert.Single(RunStore.List(Destination.TenantId, Path.GetDirectoryName(folder)), r => r.RunId == run.RunId);
            Assert.Empty(RunStore.List("another-tenant", Path.GetDirectoryName(folder)));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
