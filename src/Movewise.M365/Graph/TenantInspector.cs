using System.Text.Json.Nodes;
using Movewise.Core.Tenants;

namespace Movewise.M365.Graph;

/// <summary>Reads the tenant's name and domains, the signed-in admin, their directory roles and the tenant's licenses.</summary>
public static class TenantInspector
{
    public static async Task<TenantInfo> InspectAsync(GraphClient graph, CancellationToken ct = default)
    {
        var organization = (await graph.GetCollectionAsync("v1.0/organization?$select=id,displayName,verifiedDomains", ct)).First();
        var me = await graph.GetObjectAsync("v1.0/me?$select=userPrincipalName,displayName,userType", ct);
        var roles = await graph.GetCollectionAsync("v1.0/me/transitiveMemberOf/microsoft.graph.directoryRole?$select=displayName,roleTemplateId", ct);
        var skus = await graph.GetCollectionAsync("v1.0/subscribedSkus?$select=skuPartNumber,capabilityStatus,servicePlans", ct);

        var domains = organization["verifiedDomains"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
        string DomainWhere(string flag) =>
            domains.FirstOrDefault(d => d[flag]?.GetValue<bool>() == true)?["name"]?.GetValue<string>() ?? "";

        return new TenantInfo(
            TenantId: Text(organization, "id"),
            DisplayName: Text(organization, "displayName"),
            InitialDomain: DomainWhere("isInitial"),
            DefaultDomain: DomainWhere("isDefault"),
            UserPrincipalName: Text(me, "userPrincipalName"),
            UserDisplayName: Text(me, "displayName"),
            IsGuest: string.Equals(Text(me, "userType"), "Guest", StringComparison.OrdinalIgnoreCase),
            RoleTemplateIds: roles.Select(r => Text(r, "roleTemplateId")).ToList(),
            RoleNames: roles.Select(r => Text(r, "displayName")).Order().ToList(),
            SkuPartNumbers: skus
                .Where(s => Text(s, "capabilityStatus") == "Enabled")
                .Select(s => Text(s, "skuPartNumber"))
                .Distinct()
                .Order()
                .ToList())
        {
            ServicePlans = skus
                .Where(s => Text(s, "capabilityStatus") == "Enabled")
                .SelectMany(s => s["servicePlans"]?.AsArray().OfType<JsonObject>() ?? [])
                .Where(p => Text(p, "provisioningStatus") is "Success" or "PendingActivation" or "PendingProvisioning")
                .Select(p => Text(p, "servicePlanName"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
        };
    }

    static string Text(JsonObject obj, string property) => obj[property]?.GetValue<string>() ?? "";
}
