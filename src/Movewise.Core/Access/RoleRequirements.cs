namespace Movewise.Core.Access;

public enum TenantRole { Source, Destination }

/// <summary>Entra built-in role template IDs. These are the same in every tenant.</summary>
public static class DirectoryRoles
{
    public const string GlobalAdministrator = "62e90394-69f5-4237-9190-012177145e10";
    public const string GlobalReader = "f2ef992c-3afb-46b9-b7cf-a126ee74c451";
    public const string ConditionalAccessAdministrator = "b1be1c3e-b65d-4f19-8427-f6fa0d97feb9";
    public const string IntuneAdministrator = "3a2c62db-5318-420d-8d74-23affee5d9d5";
    public const string ExchangeAdministrator = "29232cdf-9323-42fd-ade2-1d097af3e4de";
    public const string ComplianceAdministrator = "17315797-102d-40b4-93e0-432062caca18";
    public const string TeamsAdministrator = "69091246-20e8-4a56-aa4d-066075b2a7a8";
    public const string SecurityAdministrator = "194ae4cb-b126-40b2-bd5b-6091b380977d";
    public const string PrivilegedRoleAdministrator = "e8611ab8-c189-46e8-94e1-60213ab1f814";
    public const string AuthenticationPolicyAdministrator = "0526716b-113d-4c15-b2c8-68e3c22b9f80";
    public const string SharePointAdministrator = "f28a1f50-f6e7-4571-818b-6a12f2af6b6c";

    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        [GlobalAdministrator] = "Global Administrator",
        [GlobalReader] = "Global Reader",
        [ConditionalAccessAdministrator] = "Conditional Access Administrator",
        [IntuneAdministrator] = "Intune Administrator",
        [ExchangeAdministrator] = "Exchange Administrator",
        [ComplianceAdministrator] = "Compliance Administrator",
        [TeamsAdministrator] = "Teams Administrator",
        [SecurityAdministrator] = "Security Administrator",
        [PrivilegedRoleAdministrator] = "Privileged Role Administrator",
        [AuthenticationPolicyAdministrator] = "Authentication Policy Administrator",
        [SharePointAdministrator] = "SharePoint Administrator",
    };

    public static string NameOf(string templateId) => Names.TryGetValue(templateId, out var name) ? name : templateId;
}

public sealed record RoleCheck(bool IsSufficient, IReadOnlyList<string> Missing, string Summary);

/// <summary>Decides whether the signed-in account has the admin roles its side of the migration needs.</summary>
public static class RoleRequirements
{
    static readonly string[] DestinationRoles =
    [
        DirectoryRoles.ConditionalAccessAdministrator,
        DirectoryRoles.IntuneAdministrator,
        DirectoryRoles.ExchangeAdministrator,
        DirectoryRoles.ComplianceAdministrator,
        DirectoryRoles.TeamsAdministrator,
    ];

    /// <summary>The destination role needed to create or change one type of policy or settings, or null when it's covered.</summary>
    public static string? MissingFor(Registry.ResourceType type, IEnumerable<string> heldTemplateIds) =>
        type.AdminRoles is { } roles ? Missing(roles, heldTemplateIds) : MissingFor(type.Service, heldTemplateIds);

    /// <summary>The destination role needed to create one service's policies, or null when it's covered.</summary>
    public static string? MissingFor(Registry.M365Service service, IEnumerable<string> heldTemplateIds) =>
        Missing(service switch
        {
            Registry.M365Service.Entra => [DirectoryRoles.ConditionalAccessAdministrator],
            Registry.M365Service.Intune => [DirectoryRoles.IntuneAdministrator],
            Registry.M365Service.Purview => [DirectoryRoles.ComplianceAdministrator],
            Registry.M365Service.Teams => [DirectoryRoles.TeamsAdministrator],
            Registry.M365Service.Defender => [DirectoryRoles.SecurityAdministrator, DirectoryRoles.ExchangeAdministrator],
            Registry.M365Service.SharePoint => [DirectoryRoles.SharePointAdministrator],
            Registry.M365Service.DefenderEndpoint => [DirectoryRoles.SecurityAdministrator],
            _ => [DirectoryRoles.ExchangeAdministrator],
        }, heldTemplateIds);

    // Any one role in the list is enough, and Global Administrator covers them all.
    static string? Missing(IReadOnlyList<string> needed, IEnumerable<string> heldTemplateIds)
    {
        var held = new HashSet<string>(heldTemplateIds, StringComparer.OrdinalIgnoreCase);
        if (held.Contains(DirectoryRoles.GlobalAdministrator))
            return null;
        return needed.Any(held.Contains) ? null : string.Join(" or ", needed.Select(DirectoryRoles.NameOf));
    }

    public static RoleCheck Check(TenantRole role, IEnumerable<string> heldTemplateIds)
    {
        var held = new HashSet<string>(heldTemplateIds, StringComparer.OrdinalIgnoreCase);
        if (held.Contains(DirectoryRoles.GlobalAdministrator))
            return new RoleCheck(true, [], "Global Administrator covers everything Movewise needs here.");

        return role == TenantRole.Source ? CheckSource(held) : CheckDestination(held);
    }

    static RoleCheck CheckSource(HashSet<string> held) =>
        held.Contains(DirectoryRoles.GlobalReader)
            ? new RoleCheck(true, [], "Global Reader can read the policies to migrate.")
            : new RoleCheck(false, [DirectoryRoles.NameOf(DirectoryRoles.GlobalReader)],
                "This account can't read every service. Assign Global Reader, then sign in again.");

    static RoleCheck CheckDestination(HashSet<string> held)
    {
        var missing = DestinationRoles.Where(r => !held.Contains(r)).Select(DirectoryRoles.NameOf).ToList();
        return missing.Count == 0
            ? new RoleCheck(true, [], "All admin roles needed to create policies are assigned.")
            : new RoleCheck(false, missing,
                $"{missing.Count} admin role{(missing.Count == 1 ? " is" : "s are")} missing. Policies for those services can't be created.");
    }
}
