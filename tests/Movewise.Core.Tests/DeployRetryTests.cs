using System.Net;
using System.Text.Json.Nodes;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

/// <summary>Retrying, stopping and rolling back when Microsoft 365 didn't answer, or the answer can't be trusted.</summary>
public class DeployRetryTests
{
    static readonly ResourceType AntiPhish = ResourceRegistry.Get(ResourceRegistry.AntiPhishPolicy);
    static readonly ResourceType Autopilot = ResourceRegistry.Get(ResourceRegistry.AutopilotProfile);
    static readonly ResourceType Location = ResourceRegistry.Get(ResourceRegistry.NamedLocation);

    static readonly TenantInfo Source = new("src-tenant", "Contoso", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false, [], [], []);
    static readonly TenantInfo Destination = new("dst-tenant", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false, [], [], []);

    static ExportedResource Item(ResourceType type, string id, string json)
    {
        var settings = Normalizer.Normalize(JsonNode.Parse(json)!.AsObject(), type);
        return new ExportedResource(type, id, settings[type.IdentityProperty]!.GetValue<string>(), settings, DependencyExtractor.Extract(settings, type));
    }

    static ExportedResource Phish(string name = "Execs") =>
        Item(AntiPhish, "id-" + name, $$"""{ "Name": "{{name}}", "rules": [ { "Name": "{{name}}", "Enabled": true } ] }""");

    static DeployRun PlanRun(MappingPlan plan, params ExportedResource[] items)
    {
        var report = new PreflightReport(
            items.Select(i => new PolicyResult(Transformer.Transform(i, plan, new TransformOptions()), Outcome.Create, [])).ToList(), []);
        return DeployPlanner.Plan(report, plan, Source, Destination);
    }

    static DeployRun PlanRun(params ExportedResource[] items) => PlanRun(new MappingPlan([]), items);

    static Task Run(TenantClients tenant, DeployRun run, CancellationToken ct = default) =>
        Deployer.RunAsync(tenant, run, () => Task.CompletedTask, null, ct);

    [Fact]
    public async Task The_source_graph_can_only_be_read()
    {
        var graph = new ReadOnlyGraph(new FakeGraph().Collection(Location.ListPath, """{ "id": "loc-1", "displayName": "HQ" }"""));
        var tenant = new TenantClients(graph);

        Assert.IsNotAssignableFrom<IGraphWriter>(graph);
        Assert.Throws<InvalidOperationException>(() => tenant.GraphWriter);
        Assert.Single(await graph.GetCollectionAsync(Location.ListPath));

        var run = PlanRun(Item(Location, "loc-1", """{ "@odata.type": "#microsoft.graph.ipNamedLocation", "displayName": "HQ", "ipRanges": [] }"""));
        await Run(tenant, run);

        Assert.Equal(StepStatus.Failed, run.Steps[0].Status);
        Assert.Contains("read-only", run.Steps[0].Message);
    }

    [Fact]
    public async Task A_powershell_timeout_is_checked_on_retry_and_the_policy_it_made_is_used()
    {
        var shell = new FakePowerShell().Failing("New-AntiPhishPolicy", new PowerShellException("The operation has timed out."), times: 1);
        var tenant = new TenantClients(new FakeGraph(), Exchange: shell);
        var run = PlanRun(Phish());

        await Run(tenant, run);

        var step = run.Steps[0];
        Assert.Equal(StepStatus.Creating, step.Status);
        Assert.Contains("may or may not", step.Message);
        Assert.Empty(shell.CallsTo("New-AntiPhishRule"));

        // It had been created after all.
        shell.Returns("Get-AntiPhishPolicy", """{ "Name": "Execs", "Guid": "g-made" }""");
        await Run(tenant, run);

        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal("g-made", step.DestinationId);
        Assert.Single(shell.CallsTo("New-AntiPhishPolicy"));
        Assert.Single(shell.CallsTo("New-AntiPhishRule"));
    }

    [Fact]
    public async Task An_interrupted_create_is_not_repeated_when_the_destination_cant_be_checked()
    {
        var shell = new FakePowerShell().Failing("Get-AntiPhishPolicy", new PowerShellException("Access is denied."));
        var run = PlanRun(Phish());
        run.Steps[0].Status = StepStatus.Creating;

        await Run(new TenantClients(new FakeGraph(), Exchange: shell), run);

        Assert.Equal(StepStatus.Creating, run.Steps[0].Status);
        Assert.Contains("couldn't check", run.Steps[0].Message);
        Assert.Empty(shell.CallsTo("New-AntiPhishPolicy"));
        Assert.Equal(1, run.RemainingCount);
    }

    [Fact]
    public async Task An_interrupted_create_is_not_repeated_when_two_objects_have_its_name()
    {
        var shell = new FakePowerShell().Returns("Get-AntiPhishPolicy",
            """{ "Name": "Execs", "Guid": "g-1" }""", """{ "Name": "Execs", "Guid": "g-2" }""");
        var run = PlanRun(Phish());
        run.Steps[0].Status = StepStatus.Creating;

        await Run(new TenantClients(new FakeGraph(), Exchange: shell), run);

        Assert.Equal(StepStatus.Creating, run.Steps[0].Status);
        Assert.Null(run.Steps[0].DestinationId);
        Assert.Contains("More than one", run.Steps[0].Message);
        Assert.Empty(shell.CallsTo("New-AntiPhishPolicy"));
    }

    [Fact]
    public async Task Already_exists_after_an_interrupted_create_uses_the_one_that_appeared()
    {
        var inner = new FakePowerShell()
            .Failing("New-AntiPhishPolicy", new PowerShellException("A policy with the name Execs already exists."), times: 1);
        var shell = new AppearsWhenCreated(inner, "Get-AntiPhishPolicy", """{ "Name": "Execs", "Guid": "g-late" }""");
        var run = PlanRun(Phish());
        run.Steps[0].Status = StepStatus.Creating;

        await Run(new TenantClients(new FakeGraph(), Exchange: shell), run);

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Equal("g-late", run.Steps[0].DestinationId);
        Assert.Contains(run.Steps[0].Notes, n => n.Contains("interrupted"));
    }

    [Fact]
    public async Task Stopping_finishes_the_object_in_progress_first()
    {
        var shell = new FakePowerShell();
        var run = PlanRun(Phish("Execs"), Phish("Sales"));
        using var stop = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Deployer.RunAsync(
            new TenantClients(new FakeGraph(), Exchange: shell), run, () => Task.CompletedTask,
            step =>
            {
                if (step.Status == StepStatus.Created)
                    stop.Cancel();
            },
            stop.Token));

        Assert.Equal(StepStatus.Done, run.Steps[0].Status);
        Assert.Equal(new[] { "Execs" }, run.Steps[0].CreatedRules);
        Assert.Equal(StepStatus.Pending, run.Steps[1].Status);
        Assert.Single(shell.CallsTo("New-AntiPhishPolicy"));
    }

    [Fact]
    public async Task Autopilot_retries_only_the_assignments_not_made_yet()
    {
        var profile = Item(Autopilot, "ap-1", """{ "@odata.type": "#microsoft.graph.azureADWindowsAutopilotDeploymentProfile", "displayName": "Kiosk" }""");
        profile.Settings["assignments"] = JsonNode.Parse("""
            [ { "target": { "@odata.type": "#microsoft.graph.groupAssignmentTarget", "groupId": "g-a" } },
              { "target": { "@odata.type": "#microsoft.graph.groupAssignmentTarget", "groupId": "g-b" } } ]
            """);
        var plan = new MappingPlan(new[] { "g-a", "g-b" }.Select(id =>
        {
            var mapping = new Mapping.Mapping { TargetType = ResourceRegistry.Group, Source = new ObjectRef(id, id), UsedBy = [] };
            mapping.MapTo(new ObjectRef("dest-" + id, id));
            return mapping;
        }));
        var item = new ExportedResource(Autopilot, "ap-1", "Kiosk", profile.Settings, DependencyExtractor.Extract(profile.Settings, Autopilot));
        var run = PlanRun(plan, item);
        var assignments = $"{Autopilot.CreatePath}/new-1/assignments";
        var graph = new FailsOnce(new FakeGraph(), assignments, failOnPost: 2);

        await Deployer.RunAsync(graph, run, () => Task.CompletedTask);

        var step = run.Steps[0];
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Single(step.CreatedAssignments);

        await Deployer.RunAsync(graph, run, () => Task.CompletedTask);

        Assert.Equal(StepStatus.Done, step.Status);
        var posted = graph.Inner.Posts.Where(p => p.Path == assignments).Select(p => p.Body["target"]!["groupId"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "dest-g-a", "dest-g-b" }, posted);
    }

    [Fact]
    public async Task Rollback_asks_for_a_manual_check_when_an_interrupted_create_cant_be_checked()
    {
        var shell = new FakePowerShell().Failing("Get-AntiPhishPolicy", new PowerShellException("Access is denied."));
        var run = PlanRun(Phish());
        run.Steps[0].Status = StepStatus.Creating;

        await Deployer.RollbackAsync(new TenantClients(new FakeGraph(), Exchange: shell), run, () => Task.CompletedTask);

        Assert.Equal(StepStatus.RollbackFailed, run.Steps[0].Status);
        Assert.Contains("by hand", run.Steps[0].Message);
        Assert.Null(run.RolledBack);
        Assert.True(run.CanRollBack);
        Assert.Equal(0, run.RemainingCount);
        Assert.DoesNotContain(shell.Calls, c => c.Cmdlet.StartsWith("Remove-"));
    }

    [Fact]
    public async Task Rollback_of_an_interrupted_create_that_isnt_there_finishes()
    {
        var shell = new FakePowerShell();
        var run = PlanRun(Phish());
        run.Steps[0].Status = StepStatus.Creating;

        await Deployer.RollbackAsync(new TenantClients(new FakeGraph(), Exchange: shell), run, () => Task.CompletedTask);

        Assert.Equal(StepStatus.RolledBack, run.Steps[0].Status);
        Assert.Contains("never created", run.Steps[0].Message);
        Assert.NotNull(run.RolledBack);
    }

    [Fact]
    public void Knows_which_powershell_errors_leave_the_outcome_unknown()
    {
        Assert.True(new PowerShellException("The operation has timed out.").IsUncertain);
        Assert.True(new PowerShellException("The remote server returned an error: (503) Server Unavailable.").IsUncertain);
        Assert.False(new PowerShellException("Execs already exists.").IsUncertain);
        Assert.False(new PowerShellException("Recipient not found").IsUncertain);
        Assert.True(new PowerShellException("UnAuthorized").IsAuthFailure);
        Assert.False(new PowerShellException("The operation has timed out.").IsAuthFailure);
    }

    [Fact]
    public void Remembers_whether_a_policy_was_created_in_test_mode()
    {
        var transportRule = ResourceRegistry.Get(ResourceRegistry.TransportRule);
        var run = PlanRun(Item(transportRule, "r1", """{ "Name": "Block exe", "Mode": "Enforce" }"""), Item(transportRule, "r2", """{ "Name": "Audit only", "Mode": "Audit" }"""));

        Assert.True(run.Steps.Single(s => s.DisplayName == "Block exe").InTestMode);
        Assert.False(run.Steps.Single(s => s.DisplayName == "Audit only").InTestMode);
    }

    /// <summary>A PowerShell where an object appears once its New cmdlet has been run, even if that cmdlet failed.</summary>
    sealed class AppearsWhenCreated(FakePowerShell inner, string get, string item) : IPowerShell
    {
        public async Task<IReadOnlyList<JsonObject>> InvokeAsync(string cmdlet, IReadOnlyDictionary<string, JsonNode?>? parameters = null, CancellationToken ct = default)
        {
            if (cmdlet.StartsWith("New-", StringComparison.OrdinalIgnoreCase) && cmdlet.EndsWith("Policy", StringComparison.OrdinalIgnoreCase))
                inner.Returns(get, item);
            return await inner.InvokeAsync(cmdlet, parameters, ct);
        }

        public Task<IReadOnlySet<string>> ParametersOfAsync(string cmdlet, CancellationToken ct = default) => inner.ParametersOfAsync(cmdlet, ct);
    }

    /// <summary>A Graph where the given post to one path fails, once.</summary>
    sealed class FailsOnce(FakeGraph inner, string path, int failOnPost) : IGraphWriter
    {
        int _posts;
        public FakeGraph Inner => inner;

        public Task<JsonObject?> PostAsync(string to, JsonNode body, CancellationToken ct = default)
        {
            if (to == path && ++_posts == failOnPost)
                throw new GraphException(HttpStatusCode.BadRequest, "BadRequest", "Bad target");
            return inner.PostAsync(to, body, ct);
        }

        public Task PatchAsync(string to, JsonNode body, CancellationToken ct = default) => inner.PatchAsync(to, body, ct);
        public Task DeleteAsync(string to, CancellationToken ct = default) => inner.DeleteAsync(to, ct);
        public Task<JsonObject> GetObjectAsync(string to, CancellationToken ct = default) => inner.GetObjectAsync(to, ct);
        public Task<IReadOnlyList<JsonObject>> GetCollectionAsync(string to, CancellationToken ct = default) => inner.GetCollectionAsync(to, ct);
    }
}
