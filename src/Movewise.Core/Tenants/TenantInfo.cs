namespace Movewise.Core.Tenants;

/// <summary>What Movewise learns about a tenant and the signed-in admin when connecting.</summary>
public sealed record TenantInfo(
    string TenantId,
    string DisplayName,
    string InitialDomain,
    string DefaultDomain,
    string UserPrincipalName,
    string UserDisplayName,
    bool IsGuest,
    IReadOnlyList<string> RoleTemplateIds,
    IReadOnlyList<string> RoleNames,
    IReadOnlyList<string> SkuPartNumbers)
{
    /// <summary>Service plans (licensed features) that are switched on, such as AAD_PREMIUM_P2 or INTUNE_A.</summary>
    public IReadOnlySet<string> ServicePlans { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
