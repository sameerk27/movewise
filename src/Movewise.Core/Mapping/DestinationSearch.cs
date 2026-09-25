using System.Text.Json.Nodes;
using Movewise.Core.Export;
using Movewise.Core.Registry;

namespace Movewise.Core.Mapping;

/// <summary>Finds destination objects by the start of their name, for choosing a match by hand.</summary>
public static class DestinationSearch
{
    const int Top = 20;

    public static Task<IReadOnlyList<ObjectRef>> SearchAsync(IGraphReader destination, string targetType, string text, CancellationToken ct = default) =>
        SearchAsync(new TenantClients(destination), targetType, text, ct);

    public static async Task<IReadOnlyList<ObjectRef>> SearchAsync(TenantClients tenant, string targetType, string text, CancellationToken ct = default)
    {
        var destination = tenant.Graph;
        text = text.Trim();
        if (text.Length == 0)
            return [];

        var literal = GraphQuery.Literal(text);
        switch (targetType)
        {
            case ResourceRegistry.Group:
                return (await FirstPageAsync(destination, GraphQuery.Where("v1.0/groups", $"startswith(displayName,{literal})", "id,displayName,mailNickname", Top), ct))
                    .Select(g => new ObjectRef(Text(g, "id"), Text(g, "displayName"), NullIfEmpty(Text(g, "mailNickname"))))
                    .ToList();

            case ResourceRegistry.User:
                return (await FirstPageAsync(destination, GraphQuery.Where("v1.0/users",
                        $"startswith(displayName,{literal}) or startswith(userPrincipalName,{literal})", "id,displayName,userPrincipalName", Top), ct))
                    .Select(u => new ObjectRef(Text(u, "id"), Text(u, "displayName"), Text(u, "userPrincipalName")))
                    .ToList();

            case ResourceRegistry.Application:
                // Policies point at apps by app ID, so that's the destination "ID" here.
                return (await FirstPageAsync(destination, GraphQuery.Where("v1.0/servicePrincipals", $"startswith(displayName,{literal})", "appId,displayName", Top), ct))
                    .Select(a => new ObjectRef(Text(a, "appId"), Text(a, "displayName"), Text(a, "appId")))
                    .ToList();

            case ResourceRegistry.Domain:
                return (await Matcher.ListDomainsAsync(destination, ct))
                    .Where(d => d["isVerified"]?.GetValue<bool>() == true && Text(d, "id").Contains(text, StringComparison.OrdinalIgnoreCase))
                    .Select(d => new ObjectRef(Text(d, "id"), Text(d, "id")))
                    .Take(Top)
                    .ToList();

            case ResourceRegistry.Recipient:
                // Exchange and Purview policies name recipients by email address, so that's the destination "ID" here.
                return (await Matcher.FindRecipientsAsync(destination, $"startswith(displayName,{literal}) or startswith(mail,{literal})", ct))
                    .Select(r => new ObjectRef(Matcher.RecipientAddress(r), Text(r, "displayName"), Matcher.RecipientAddress(r)))
                    .Where(r => r.Id.Length > 0)
                    .Take(Top)
                    .ToList();

            case ResourceRegistry.Site:
                // Purview policies name sites by address.
                return (await FirstPageAsync(destination, $"v1.0/sites?search={Uri.EscapeDataString(text)}&$select=id,webUrl,displayName&$top={Top}", ct))
                    .Select(s => new ObjectRef(Text(s, "webUrl"), Text(s, "displayName"), Text(s, "webUrl")))
                    .ToList();

            default:
                var type = ResourceRegistry.Get(targetType);
                return (await ResourceReader.ListAsync(tenant, type, ct))
                    .Where(item => Text(item, type.IdentityProperty).Contains(text, StringComparison.OrdinalIgnoreCase))
                    .Select(item => new ObjectRef(Text(item, "id"), Text(item, type.IdentityProperty)))
                    .OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Take(Top)
                    .ToList();
        }
    }

    static async Task<IReadOnlyList<JsonObject>> FirstPageAsync(IGraphReader graph, string path, CancellationToken ct)
    {
        var page = await graph.GetObjectAsync(path, ct);
        return page["value"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
    }

    static string Text(JsonObject obj, string property) => obj[property]?.GetValue<string>() ?? "";

    static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
