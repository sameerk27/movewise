using System.Text.RegularExpressions;

namespace Movewise.Core.Diagnostics;

/// <summary>
/// Takes what identifies a tenant or a person out of text before it goes into a support log: access tokens,
/// email addresses and usernames, tenant names in Microsoft domains, the tenant's own domains, OneDrive owners,
/// and IP addresses (IPv4 and IPv6). IDs are shortened to their first characters, enough to tell them apart in
/// a log but not to look anything up. Names of tenants, policies and groups aren't recognisable as such, so they
/// can remain; the app says so when it saves a log.
/// </summary>
public static partial class Redactor
{
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*")]
    private static partial Regex Jwt();

    // Also JSON, where the name is quoted: "access_token":"…".
    [GeneratedRegex(@"(?i)\b(bearer|access_?token|refresh_?token|id_?token|client_?secret|password)(""?\s*[:=]\s*|\s+)(""?)[^\s"",;]+")]
    private static partial Regex Secret();

    [GeneratedRegex(@"[A-Za-z0-9._%+'-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    // Every label before the Microsoft domain goes, so contoso.mail.onmicrosoft.com doesn't keep "contoso".
    [GeneratedRegex(@"(?i)\b(?:[a-z0-9-]+\.)*[a-z0-9-]+?(-my)?\.(sharepoint\.com|onmicrosoft\.com)\b")]
    private static partial Regex TenantDomain();

    // OneDrive addresses carry the owner's username: /personal/anna_contoso_com.
    [GeneratedRegex(@"(?i)/personal/[^/\s""'?#]+")]
    private static partial Regex OneDriveOwner();

    // Lower case only, so .NET names in error details (System.Net.Http) are left alone.
    [GeneratedRegex(@"\b(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+(?:com|net|org|edu|gov|mil|int|info|biz|io|co|ai|app|dev|cloud|eu|uk|us|ca|au|nz|in|de|fr|nl|be|ch|at|es|it|pt|se|no|dk|fi|ie|pl|cz|jp|cn|sg|hk|za|br|mx)\b")]
    private static partial Regex DomainName();

    [GeneratedRegex(@"(?i)\b([0-9a-f]{8})-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b")]
    private static partial Regex Guid();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}(?:/\d{1,2})?\b")]
    private static partial Regex IpAddress();

    // Full (eight groups) or shortened with "::"; three groups without "::" is a time of day, and is left alone.
    [GeneratedRegex(@"(?i)(?<![0-9a-z:])(?:(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}|(?:[0-9a-f]{1,4}:){1,7}:(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,6})?|::[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,6})(?:/\d{1,3})?(?![0-9a-z:])")]
    private static partial Regex Ipv6Address();

    // Microsoft's own addresses say which service failed, and identify nobody.
    static readonly string[] MicrosoftDomains =
    [
        "microsoft.com", "microsoftonline.com", "office.com", "office365.com", "office.net", "windows.net", "azure.com",
        "live.com", "outlook.com", "powershellgallery.com", "nuget.org", "sharepoint.com", "onmicrosoft.com",
    ];

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var result = Jwt().Replace(text, "[token]");
        result = Secret().Replace(result, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{m.Groups[3].Value}[hidden]");
        result = Email().Replace(result, "[email]");
        result = OneDriveOwner().Replace(result, "/personal/[user]");
        result = TenantDomain().Replace(result, m => $"[tenant]{m.Groups[1].Value}.{m.Groups[2].Value.ToLowerInvariant()}");
        result = DomainName().Replace(result, m =>
            MicrosoftDomains.Any(d => m.Value.Equals(d, StringComparison.Ordinal) || m.Value.EndsWith("." + d, StringComparison.Ordinal)) ? m.Value : "[domain]");
        result = Guid().Replace(result, m => m.Groups[1].Value[..4] + "…");
        result = IpAddress().Replace(result, "[ip]");
        result = Ipv6Address().Replace(result, "[ip]");
        return result;
    }
}
