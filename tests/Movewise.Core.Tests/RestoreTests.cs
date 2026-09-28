using Movewise.Core.Access;
using Movewise.Core.Compare;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

/// <summary>Restoring a tenant from its own snapshot: removed policies recreated, changed settings put back.</summary>
public class RestoreTests
{
    const string CaPath = "v1.0/identity/conditionalAccess/policies";
    const string UserSettings = "v1.0/policies/authorizationPolicy";

    static readonly ResourceType ConditionalAccess = ResourceRegistry.Get(ResourceRegistry.ConditionalAccessPolicy);
    static readonly ResourceType EntraUserSettings = ResourceRegistry.Get(ResourceRegistry.EntraUserSettings);
    static readonly TenantInfo Tenant = new("t1", "Contoso", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false,
        [DirectoryRoles.GlobalAdministrator], [], []) { ServicePlans = new HashSet<string>(["AAD_PREMIUM"], StringComparer.OrdinalIgnoreCase) };

    const string Policy = """
        { "id": "p1", "displayName": "CA001 MFA", "state": "enabled",
          "conditions": { "users": { "includeUsers": ["All"] }, "applications": { "includeApplications": ["All"] } },
          "grantControls": { "operator": "OR", "builtInControls": ["mfa"] } }
        """;

    static async Task<(ComparisonReport Drift, FakeGraph Tenant)> DriftAsync()
    {
        // Then: the policy exists and guests can't invite. Now: the policy is gone and everyone can invite.
        var then = new FakeGraph().Collection(CaPath, Policy).Object(UserSettings, """{ "displayName": "Authorization Policy", "allowInvitesFrom": "adminsAndGuestInviters" }""");
        var types = new[] { ConditionalAccess, EntraUserSettings };
        var snapshot = new Snapshot("t1", "Contoso", DateTimeOffset.Now.AddDays(-1), types, (await Exporter.ExportAsync(then, types)).Items);

        var now = new FakeGraph().Collection(CaPath).Object(UserSettings, """{ "displayName": "Authorization Policy", "allowInvitesFrom": "everyone" }""");
        return (await TenantComparer.CompareWithSnapshotAsync(snapshot, new TenantClients(new ReadOnlyGraph(now)), "Contoso"), now);
    }

    static async Task<PreflightReport> CheckAsync(ComparisonReport drift, FakeGraph tenant, bool keepOriginalState)
    {
        var restoring = drift.Items.Where(i => i.CanRestore).Select(i => i.FirstSource!).ToList();
        var plan = await Matcher.BuildAsync(new ReadOnlyGraph(tenant), new TenantClients(tenant), restoring, drift.Snapshot!.Items);
        return await PreflightCheck.RunAsync(new TenantClients(tenant), Tenant, restoring, plan, new PreflightChoices { KeepOriginalState = keepOriginalState });
    }

    [Fact]
    public async Task Removed_policies_and_changed_settings_can_be_restored_and_policies_changed_since_cant()
    {
        var (drift, _) = await DriftAsync();

        Assert.True(Assert.Single(drift.Items, i => i.Name == "CA001 MFA").CanRestore);
        Assert.True(Assert.Single(drift.Items, i => i.Type == EntraUserSettings).CanRestore);
        Assert.NotNull(drift.Snapshot);

        var changedPolicy = new ComparedItem(ConditionalAccess, "CA009", Difference.Different, new(), new(), ["state"], []) { FirstSource = drift.Items[0].FirstSource };
        Assert.False(changedPolicy.CanRestore);
        var added = new ComparedItem(ConditionalAccess, "CA010", Difference.OnlyInSecond, null, new(), [], []);
        Assert.False(added.CanRestore);
    }

    [Fact]
    public async Task A_restore_recreates_the_policy_in_report_only_unless_asked_and_sets_the_setting_back()
    {
        var (drift, tenant) = await DriftAsync();

        var safe = await CheckAsync(drift, tenant, keepOriginalState: false);
        Assert.Equal("enabledForReportingButNotEnforced", Assert.Single(safe.Policies, p => p.Source.Type == ConditionalAccess).Policy.Desired["state"]!.GetValue<string>());

        var report = await CheckAsync(drift, tenant, keepOriginalState: true);
        Assert.Equal(2, report.CreateCount);
        Assert.Equal("enabled", Assert.Single(report.Policies, p => p.Source.Type == ConditionalAccess).Policy.Desired["state"]!.GetValue<string>());
        Assert.Equal(["allowInvitesFrom"], Assert.Single(report.Policies, p => p.Source.Type == EntraUserSettings).ChangedSettings);

        var run = DeployPlanner.Plan(report, new MappingPlan([]), Tenant, Tenant);
        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);

        Assert.All(run.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
        Assert.Single(tenant.Posts, p => p.Path == CaPath);
        Assert.Equal("adminsAndGuestInviters", Assert.Single(tenant.Patches).Body["allowInvitesFrom"]!.GetValue<string>());

        // And the restore can be undone like any run.
        await Deployer.RollbackAsync(new TenantClients(tenant), run, () => Task.CompletedTask);
        Assert.Single(tenant.Deletes);
        Assert.Equal("everyone", tenant.Patches.Last().Body["allowInvitesFrom"]!.GetValue<string>());
    }
}
