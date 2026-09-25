using System.Text.Json.Nodes;
using Movewise.Core.Access;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

/// <summary>Settings in Microsoft Graph (Entra ID, SharePoint) and settings with their own quirks (Endpoint DLP).</summary>
public class GraphSettingsTests
{
    const string UserSettingsPath = "v1.0/policies/authorizationPolicy";
    const string SmsPath = "v1.0/policies/authenticationMethodsPolicy/authenticationMethodConfigurations/Sms";

    static readonly ResourceType UserSettings = ResourceRegistry.Get(ResourceRegistry.EntraUserSettings);
    static readonly ResourceType Sms = ResourceRegistry.Get(ResourceRegistry.AuthMethodSms);
    static readonly ResourceType EndpointDlp = ResourceRegistry.Get(ResourceRegistry.EndpointDlpSettings);

    static readonly TenantInfo Source = new("src-tenant", "Contoso", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false, [], [], []);

    static TenantInfo Destination(params string[] roles) =>
        new("dst-tenant", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false, roles.ToList(), [], []);

    static async Task<ExportedResource> ExportOne(TenantClients source, ResourceType type)
    {
        var result = await Exporter.ExportAsync(source, [type]);
        Assert.Empty(result.Warnings);
        return Assert.Single(result.Items);
    }

    [Fact]
    public async Task Entra_user_settings_are_changed_with_patch_and_put_back_on_rollback()
    {
        var source = new FakeGraph().Object(UserSettingsPath, """
            { "id": "authorizationPolicy", "displayName": "Authorization Policy", "allowInvitesFrom": "adminsAndGuestInviters",
              "defaultUserRolePermissions": { "allowedToCreateApps": false, "allowedToCreateSecurityGroups": false } }
            """);
        var destination = new FakeGraph().Object(UserSettingsPath, """
            { "id": "authorizationPolicy", "displayName": "Authorization Policy", "allowInvitesFrom": "everyone",
              "defaultUserRolePermissions": { "allowedToCreateApps": true, "allowedToCreateSecurityGroups": false } }
            """);
        var item = await ExportOne(new TenantClients(new ReadOnlyGraph(source)), UserSettings);
        var tenant = new TenantClients(destination);
        var info = Destination(DirectoryRoles.GlobalAdministrator);

        var report = await PreflightCheck.RunAsync(tenant, info, [item], new MappingPlan([]), new PreflightChoices());
        var policy = Assert.Single(report.Policies);
        Assert.Equal(["allowInvitesFrom", "defaultUserRolePermissions"], policy.ChangedSettings);

        var run = DeployPlanner.Plan(report, new MappingPlan([]), Source, info);
        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);

        var (path, body) = Assert.Single(destination.Patches);
        Assert.Equal(UserSettingsPath, path);
        Assert.Equal("adminsAndGuestInviters", body["allowInvitesFrom"]!.GetValue<string>());
        Assert.False(body["defaultUserRolePermissions"]!["allowedToCreateApps"]!.GetValue<bool>());
        Assert.False(body.AsObject().ContainsKey("displayName"));
        Assert.Equal(StepStatus.Done, run.Steps[0].Status);

        await Deployer.RollbackAsync(tenant, run, () => Task.CompletedTask);

        var restored = destination.Patches.Last().Body;
        Assert.Equal("everyone", restored["allowInvitesFrom"]!.GetValue<string>());
        Assert.True(restored["defaultUserRolePermissions"]!["allowedToCreateApps"]!.GetValue<bool>());
        Assert.Equal(StepStatus.RolledBack, run.Steps[0].Status);
    }

    [Fact]
    public async Task Entra_user_settings_need_privileged_role_administrator()
    {
        var source = new FakeGraph().Object(UserSettingsPath, """{ "allowInvitesFrom": "none" }""");
        var item = await ExportOne(new TenantClients(source), UserSettings);

        var report = await PreflightCheck.RunAsync(new TenantClients(new FakeGraph()), Destination(DirectoryRoles.ConditionalAccessAdministrator),
            [item], new MappingPlan([]), new PreflightChoices());

        Assert.Equal(Outcome.Blocked, Assert.Single(report.Policies).Outcome);
        Assert.Contains(report.Findings, f => f.Title == "Missing admin role: Privileged Role Administrator");
    }

    [Fact]
    public async Task A_sign_in_method_names_its_type_and_switching_it_off_is_warned_about()
    {
        var source = new FakeGraph().Object(SmsPath, """
            { "@odata.type": "#microsoft.graph.smsAuthenticationMethodConfiguration", "id": "Sms", "state": "disabled", "includeTargets": [] }
            """);
        var destination = new FakeGraph().Object(SmsPath, """
            { "@odata.type": "#microsoft.graph.smsAuthenticationMethodConfiguration", "id": "Sms", "state": "enabled",
              "includeTargets": [ { "targetType": "group", "id": "all_users", "isUsableForSignIn": true } ] }
            """);
        var item = await ExportOne(new TenantClients(source), Sms);
        var tenant = new TenantClients(destination);
        var info = Destination(DirectoryRoles.GlobalAdministrator);

        var report = await PreflightCheck.RunAsync(tenant, info, [item], new MappingPlan([]), new PreflightChoices());
        Assert.Contains(report.Findings, f => f.Title == "Sign-in methods that some users may lose" && f.Items.Contains("Sign-in method: SMS: switched off"));

        var run = DeployPlanner.Plan(report, new MappingPlan([]), Source, info);
        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);

        var body = Assert.Single(destination.Patches).Body;
        Assert.Equal("#microsoft.graph.smsAuthenticationMethodConfiguration", body["@odata.type"]!.GetValue<string>());
        Assert.Equal("disabled", body["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Endpoint_dlp_reads_only_its_own_settings_and_sets_them_without_an_identity()
    {
        const string setting = """[ { "Setting": "PathExclusion", "Value": "C:\\Build\\*" } ]""";
        var source = new FakePowerShell().Returns("Get-PolicyConfig",
            $$"""{ "Name": "PolicyConfig", "EnableLabelCoauth": true, "EnableSpoAipMigration": true, "EndpointDlpGlobalSettings": {{setting}} }""");
        var destination = new FakePowerShell()
            .Returns("Get-PolicyConfig", """{ "Name": "PolicyConfig", "EnableLabelCoauth": false, "EndpointDlpGlobalSettings": [] }""")
            .Accepts("Set-PolicyConfig", "EnableLabelCoauth", "EnableSpoAipMigration", "EndpointDlpGlobalSettings");
        var item = await ExportOne(new TenantClients(new FakeGraph(), Compliance: source), EndpointDlp);
        Assert.False(item.Settings.ContainsKey("EnableLabelCoauth"));

        var tenant = new TenantClients(new FakeGraph(), Compliance: destination);
        var info = Destination(DirectoryRoles.GlobalAdministrator);
        var report = await PreflightCheck.RunAsync(tenant, info, [item], new MappingPlan([]), new PreflightChoices());
        Assert.Equal(["EndpointDlpGlobalSettings"], Assert.Single(report.Policies).ChangedSettings);

        var run = DeployPlanner.Plan(report, new MappingPlan([]), Source, info);
        await Deployer.RunAsync(tenant, run, () => Task.CompletedTask);

        var set = Assert.Single(destination.CallsTo("Set-PolicyConfig"));
        Assert.Equal(["EndpointDlpGlobalSettings"], set.Keys.ToArray());
        Assert.Equal("PathExclusion", set["EndpointDlpGlobalSettings"]![0]!["Setting"]!.GetValue<string>());
    }
}
