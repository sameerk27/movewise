using System.Text.Json.Nodes;
using Movewise.Core.Access;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Tests;

/// <summary>Cross-tenant access partners and custom Entra admin roles.</summary>
public class EntraExtrasTests
{
    const string Partners = "v1.0/policies/crossTenantAccessPolicy/partners";

    static readonly TenantInfo Source = new("src-tenant", "Contoso", "contoso.onmicrosoft.com", "contoso.com", "admin@contoso.com", "Admin", false, [], [], []);
    static readonly TenantInfo Destination = new("dst-tenant", "Fabrikam", "fabrikam.onmicrosoft.com", "fabrikam.com", "admin@fabrikam.com", "Admin", false,
        [DirectoryRoles.GlobalAdministrator], [], []);

    static async Task<IReadOnlyList<ExportedResource>> ExportPartners() =>
        (await Exporter.ExportAsync(new FakeGraph().Collection(Partners,
            """{ "tenantId": "dst-tenant", "inboundTrust": { "isMfaAccepted": true } }""",
            """{ "tenantId": "partner-tenant", "inboundTrust": { "isMfaAccepted": true, "isCompliantDeviceAccepted": true } }"""),
            [ResourceRegistry.Get(ResourceRegistry.CrossTenantPartner)])).Items;

    [Fact]
    public async Task A_partner_that_is_the_destination_itself_is_blocked()
    {
        var items = await ExportPartners();
        var destination = new FakeGraph().Collection(Partners);

        var report = await PreflightCheck.RunAsync(new TenantClients(destination), Destination, items, new MappingPlan([]), new PreflightChoices());

        Assert.Equal(Outcome.Blocked, Assert.Single(report.Policies, p => p.Source.SourceId == "dst-tenant").Outcome);
        Assert.Equal(Outcome.Create, Assert.Single(report.Policies, p => p.Source.SourceId == "partner-tenant").Outcome);
        Assert.Contains(report.Findings, f => f.Severity == Severity.Blocker && f.Title.StartsWith("Cross-tenant access partners that can't be created"));
    }

    [Fact]
    public async Task A_partner_is_known_by_its_tenant_id_once_created_and_deleted_by_it_on_rollback()
    {
        var partner = Assert.Single(await ExportPartners(), p => p.SourceId == "partner-tenant");
        var destination = new PartnerGraph(new FakeGraph());
        var plan = new MappingPlan([]);
        var run = DeployPlanner.Plan(new PreflightReport([new PolicyResult(Transformer.Transform(partner, plan, new TransformOptions()), Outcome.Create, [])], []),
            plan, Source, Destination);

        await Deployer.RunAsync(destination, run, () => Task.CompletedTask);
        Assert.Equal("partner-tenant", run.Steps[0].DestinationId);

        await Deployer.RollbackAsync(new TenantClients(destination), run, () => Task.CompletedTask);
        Assert.Equal($"{Partners}/partner-tenant", Assert.Single(destination.Inner.Deletes));
    }

    [Fact]
    public async Task Custom_entra_roles_need_privileged_role_administrator_and_built_in_ones_stay_out()
    {
        var source = new FakeGraph().Collection("v1.0/roleManagement/directory/roleDefinitions?$filter=isBuiltIn eq false",
            """{ "id": "r1", "displayName": "Helpdesk tier 1", "isBuiltIn": false, "rolePermissions": [ { "allowedResourceActions": ["microsoft.directory/users/password/update"] } ] }""");
        var item = Assert.Single((await Exporter.ExportAsync(source, [ResourceRegistry.Get(ResourceRegistry.DirectoryRole)])).Items);
        var limited = Destination with { RoleTemplateIds = [DirectoryRoles.ConditionalAccessAdministrator] };

        var report = await PreflightCheck.RunAsync(new TenantClients(new FakeGraph()), limited, [item], new MappingPlan([]), new PreflightChoices());

        Assert.Contains(report.Findings, f => f.Title == "Missing admin role: Privileged Role Administrator");
    }

    /// <summary>Graph as it answers for partners: the new object is known by tenantId, not id.</summary>
    sealed class PartnerGraph(FakeGraph inner) : IGraphWriter
    {
        public FakeGraph Inner => inner;

        public async Task<JsonObject?> PostAsync(string path, JsonNode body, CancellationToken ct = default)
        {
            await inner.PostAsync(path, body, ct);
            return new JsonObject { ["tenantId"] = body["tenantId"]?.DeepClone() };
        }

        public Task PatchAsync(string path, JsonNode body, CancellationToken ct = default) => inner.PatchAsync(path, body, ct);
        public Task DeleteAsync(string path, CancellationToken ct = default) => inner.DeleteAsync(path, ct);
        public Task<JsonObject> GetObjectAsync(string path, CancellationToken ct = default) => inner.GetObjectAsync(path, ct);
        public Task<IReadOnlyList<JsonObject>> GetCollectionAsync(string path, CancellationToken ct = default) => inner.GetCollectionAsync(path, ct);
    }
}
