using Movewise.Core.Access;

namespace Movewise.M365.Auth;

/// <summary>
/// Delegated permissions requested for each side. The source is only asked for Graph read permissions, but that alone
/// doesn't make it read-only: one app registration serves both sides, so the source's approval covers its write
/// permissions too, and the Exchange and Teams tokens (.default) carry whatever was approved. What keeps the source
/// unchanged is the app: its Graph client can only read (<see cref="Movewise.Core.Export.ReadOnlyGraph"/>) and its
/// PowerShell sessions refuse anything but Get cmdlets.
/// </summary>
public static class Scopes
{
    const string Graph = "https://graph.microsoft.com/";

    static readonly string[] SourceGraph =
    [
        "User.Read",
        "Directory.Read.All",
        "Policy.Read.All",
        "DeviceManagementConfiguration.Read.All",
        "DeviceManagementApps.Read.All",
        "DeviceManagementServiceConfig.Read.All",
        "DeviceManagementRBAC.Read.All",
        "Sites.Read.All",
        "SharePointTenantSettings.Read.All",
        "CustomDetection.Read.All",
    ];

    static readonly string[] DestinationGraph =
    [
        "User.Read",
        "Directory.Read.All",
        "Policy.Read.All",
        // Also covers creating custom authentication strengths.
        "Policy.ReadWrite.ConditionalAccess",
        // Entra ID user settings (the authorization policy).
        "Policy.ReadWrite.Authorization",
        // Which sign-in methods users may use (the authentication methods policy).
        "Policy.ReadWrite.AuthenticationMethod",
        // Cross-tenant access defaults and partners.
        "Policy.ReadWrite.CrossTenantAccess",
        // Custom Entra admin roles.
        "RoleManagement.ReadWrite.Directory",
        "Application.Read.All",
        "Group.ReadWrite.All",
        "DeviceManagementConfiguration.ReadWrite.All",
        "DeviceManagementApps.ReadWrite.All",
        "DeviceManagementServiceConfig.ReadWrite.All",
        "DeviceManagementRBAC.ReadWrite.All",
        "Sites.Read.All",
        "SharePointTenantSettings.ReadWrite.All",
        // Defender custom detection rules.
        "CustomDetection.ReadWrite.All",
    ];

    /// <summary>Every delegated Microsoft Graph permission the app registration needs, for either side.</summary>
    public static IReadOnlyList<string> GraphPermissions { get; } = SourceGraph.Union(DestinationGraph).Order(StringComparer.Ordinal).ToList();

    public static IReadOnlyList<string> GraphFor(TenantRole role) =>
        (role == TenantRole.Source ? SourceGraph : DestinationGraph).Select(s => Graph + s).ToList();

    /// <summary>Exchange Online PowerShell (needs Exchange.Manage on the app registration).</summary>
    public static IReadOnlyList<string> Exchange { get; } = ["https://outlook.office365.com/.default"];

    /// <summary>
    /// Security &amp; Compliance PowerShell takes an Exchange Online token (its app-only setup, too, grants Exchange.Manage
    /// on the Office 365 Exchange Online API). If it's refused, Movewise falls back to the module's own sign-in.
    /// </summary>
    public static IReadOnlyList<string> Compliance => Exchange;

    /// <summary>
    /// Microsoft Teams PowerShell ("Skype and Teams Tenant Admin API"). Connect-MicrosoftTeams also takes a Graph token.
    /// If the app registration has no permission for it, the Teams module signs the same account in itself.
    /// </summary>
    public static IReadOnlyList<string> Teams { get; } = ["48ac35b8-9aa8-4d74-927d-1f4a14a0b239/.default"];

    /// <summary>The Defender for Endpoint API ("WindowsDefenderATP"), for indicators.</summary>
    public static IReadOnlyList<string> DefenderEndpoint { get; } = ["https://api.securitycenter.microsoft.com/.default"];
}
