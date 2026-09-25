using System.Net;
using System.Text.Json.Nodes;
using Movewise.Core.Deploy;
using Movewise.Core.Export;

namespace Movewise.App.Demo;

/// <summary>
/// An in-memory Microsoft Graph for the demo tenants. It understands the requests Movewise makes: collections,
/// items by ID (including Intune's <c>('id')</c> form), assignments, simple <c>$filter</c>s (eq, startswith, and, or),
/// <c>$top</c>, and SharePoint sites by address. Writes change the in-memory tenant, so deploy and rollback can be tried.
/// </summary>
sealed class DemoGraph : IGraphWriter
{
    const string Hidden = "__";
    readonly Dictionary<string, List<JsonObject>> _collections = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, JsonObject> _objects = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();

    public DemoGraph Add(string collection, params JsonObject[] items)
    {
        if (!_collections.TryGetValue(collection, out var list))
            _collections[collection] = list = [];
        list.AddRange(items);
        return this;
    }

    /// <summary>Makes each collection exist, empty unless it has items, so reading a type the demo has none of finds nothing.</summary>
    public DemoGraph EnsureCollections(IEnumerable<string> collections)
    {
        foreach (var collection in collections)
            _collections.TryAdd(collection, []);
        return this;
    }

    public DemoGraph Object(string path, JsonObject value)
    {
        _objects[path] = value;
        return this;
    }

    /// <summary>
    /// Entra ID and SharePoint settings every tenant has. Contoso and Fabrikam differ in a few, so the demo shows
    /// settings being changed, a sign-in method switched off, and settings that already match being skipped.
    /// </summary>
    public DemoGraph Settings(bool contoso)
    {
        const string methods = "v1.0/policies/authenticationMethodsPolicy/authenticationMethodConfigurations/";
        static JsonObject Method(string type, string id, bool enabled) => JsonNode.Parse($$"""
            { "@odata.type": "#microsoft.graph.{{type}}AuthenticationMethodConfiguration", "id": "{{id}}", "state": "{{(enabled ? "enabled" : "disabled")}}",
              "includeTargets": [ { "targetType": "group", "id": "all_users" } ] }
            """)!.AsObject();

        return Object("v1.0/policies/authorizationPolicy", JsonNode.Parse($$"""
                { "id": "authorizationPolicy", "displayName": "Authorization Policy", "allowInvitesFrom": "{{(contoso ? "adminsAndGuestInviters" : "everyone")}}",
                  "blockMsolPowerShell": true, "defaultUserRolePermissions": { "allowedToCreateApps": {{(contoso ? "false" : "true")}},
                  "allowedToCreateSecurityGroups": false, "allowedToCreateTenants": false, "allowedToReadOtherUsers": true } }
                """)!.AsObject())
            .Object("v1.0/policies/authenticationMethodsPolicy", JsonNode.Parse("""
                { "id": "authenticationMethodsPolicy", "displayName": "Authentication Methods Policy", "policyVersion": "1.5",
                  "registrationEnforcement": { "authenticationMethodsRegistrationCampaign": { "state": "enabled", "snoozeDurationInDays": 1,
                  "includeTargets": [ { "id": "all_users", "targetType": "group", "targetedAuthenticationMethod": "microsoftAuthenticator" } ], "excludeTargets": [] } } }
                """)!.AsObject())
            .Object(methods + "MicrosoftAuthenticator", Method("microsoftAuthenticator", "MicrosoftAuthenticator", true))
            .Object(methods + "Fido2", Method("fido2", "Fido2", contoso))
            .Object(methods + "Sms", Method("sms", "Sms", !contoso))
            .Object(methods + "Voice", Method("voice", "Voice", false))
            .Object(methods + "Email", Method("email", "Email", true))
            .Object(methods + "SoftwareOath", Method("softwareOath", "SoftwareOath", false))
            .Object(methods + "TemporaryAccessPass", Method("temporaryAccessPass", "TemporaryAccessPass", contoso))
            .Object("v1.0/policies/crossTenantAccessPolicy/default", JsonNode.Parse($$"""
                { "isServiceDefault": false, "inboundTrust": { "isMfaAccepted": {{(contoso ? "true" : "false")}}, "isCompliantDeviceAccepted": false, "isHybridAzureADJoinedDeviceAccepted": false },
                  "b2bCollaborationInbound": { "usersAndGroups": { "accessType": "allowed", "targets": [ { "target": "AllUsers", "targetType": "user" } ] } } }
                """)!.AsObject())
            .Object("v1.0/admin/sharepoint/settings", JsonNode.Parse($$"""
                { "sharingCapability": "{{(contoso ? "existingExternalUserSharingOnly" : "externalUserAndGuestSharing")}}",
                  "sharingDomainRestrictionMode": "{{(contoso ? "allowList" : "none")}}",
                  "sharingAllowedDomainList": {{(contoso ? "[\"northwind.example\"]" : "[]")}}, "sharingBlockedDomainList": [],
                  "isResharingByExternalUsersEnabled": false, "deletedUserPersonalSiteRetentionPeriodInDays": 30,
                  "allowedDomainGuidsForSyncApp": ["{{(contoso ? "11111111-1111-1111-1111-111111111111" : "22222222-2222-2222-2222-222222222222")}}"] }
                """)!.AsObject());
    }

    public async Task<JsonObject> GetObjectAsync(string path, CancellationToken ct = default)
    {
        await Pause(ct);
        lock (_gate)
        {
            var (basePath, query) = Split(path);
            if (_objects.TryGetValue(basePath, out var found))
                return Visible(found);

            // SharePoint: v1.0/sites/{host}:{path}, or a search.
            if (basePath.StartsWith("v1.0/sites/", StringComparison.OrdinalIgnoreCase))
            {
                var address = "https://" + basePath["v1.0/sites/".Length..].Replace(":", "");
                return Visible(Items("v1.0/sites").FirstOrDefault(s => string.Equals(Text(s, "webUrl"), address, StringComparison.OrdinalIgnoreCase))
                    ?? throw NotFound(path));
            }
            if (basePath.Equals("v1.0/sites", StringComparison.OrdinalIgnoreCase) && query.TryGetValue("search", out var search))
                return Page(Items("v1.0/sites").Where(s => Text(s, "displayName").Contains(search, StringComparison.OrdinalIgnoreCase) || Text(s, "webUrl").Contains(search, StringComparison.OrdinalIgnoreCase)), query);

            if (_collections.ContainsKey(basePath))
                return Page(Filter(Items(basePath), query), query);

            var (item, sub) = Find(basePath) ?? throw NotFound(path);
            return sub is null ? Visible(item) : new JsonObject { ["value"] = (item[Hidden + sub] ?? new JsonArray()).DeepClone() };
        }
    }

    public async Task<IReadOnlyList<JsonObject>> GetCollectionAsync(string path, CancellationToken ct = default)
    {
        var page = await GetObjectAsync(path, ct);
        return page["value"]?.AsArray().OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList() ?? [];
    }

    public async Task<JsonObject?> PostAsync(string path, JsonNode body, CancellationToken ct = default)
    {
        await Pause(ct);
        lock (_gate)
        {
            var (basePath, _) = Split(path);
            if (_collections.TryGetValue(basePath, out var list))
            {
                var created = (JsonObject)body.DeepClone();
                created["id"] = Guid.NewGuid().ToString();
                created["createdDateTime"] = DateTimeOffset.UtcNow.ToString("o");
                // What the service sets itself: a new authentication strength is always a custom one.
                if (basePath.EndsWith("authenticationStrengthPolicies", StringComparison.OrdinalIgnoreCase))
                    created["policyType"] = "custom";
                list.Add(created);
                return Visible(created);
            }

            var (item, sub) = Find(basePath) ?? throw NotFound(path);
            switch (sub?.ToLowerInvariant())
            {
                case "assign":
                    // Most take "assignments"; scripts and enrollment settings name the list after themselves.
                    item[Hidden + "assignments"] = body.AsObject().Select(p => p.Value).OfType<JsonArray>().FirstOrDefault()?.DeepClone() ?? new JsonArray();
                    return null;
                case "targetapps":
                    item["apps"] = body["apps"]?.DeepClone();
                    return null;
                case "assignments" or "localizednotificationmessages":
                    // Autopilot and terms: one assignment at a time. Notification templates: one message at a time.
                    if (item[Hidden + sub] is not JsonArray added)
                        item[Hidden + sub] = added = [];
                    added.Add(body.DeepClone());
                    return (JsonObject)body.DeepClone();
                case "updatedefinitionvalues":
                    // Administrative templates: the settings, all at once.
                    item[Hidden + "definitionValues"] = body["added"]?.DeepClone() ?? new JsonArray();
                    return null;
                default:
                    throw new GraphException(HttpStatusCode.BadRequest, "BadRequest", $"The demo tenant can't handle POST {path}.");
            }
        }
    }

    /// <summary>Changes a settings object, or an item of a collection, in place.</summary>
    public async Task PatchAsync(string path, JsonNode body, CancellationToken ct = default)
    {
        await Pause(ct);
        lock (_gate)
        {
            var (basePath, _) = Split(path);
            var target = _objects.TryGetValue(basePath, out var found) ? found
                : Find(basePath) is ({ } item, null) ? item
                : throw NotFound(path);
            foreach (var (name, value) in body.AsObject())
                target[name] = value?.DeepClone();
        }
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        await Pause(ct);
        lock (_gate)
        {
            var (basePath, _) = Split(path);
            foreach (var (collection, list) in _collections)
            {
                var removed = list.RemoveAll(i => Matches(basePath, collection, Text(i, "id")));
                if (removed > 0)
                    return;
            }
            throw NotFound(path);
        }
    }

    (JsonObject Item, string? Sub)? Find(string basePath)
    {
        foreach (var (collection, list) in _collections.OrderByDescending(c => c.Key.Length))
        {
            if (!basePath.StartsWith(collection, StringComparison.OrdinalIgnoreCase) || basePath.Length == collection.Length)
                continue;
            var rest = basePath[collection.Length..];
            string id, remainder;
            if (rest.StartsWith("('", StringComparison.Ordinal) && rest.IndexOf("')", StringComparison.Ordinal) is var close and > 0)
            {
                id = rest[2..close];
                remainder = rest[(close + 2)..];
            }
            else if (rest.StartsWith('/'))
            {
                var parts = rest[1..].Split('/', 2);
                id = parts[0];
                remainder = parts.Length > 1 ? "/" + parts[1] : "";
            }
            else
            {
                continue;
            }

            id = Uri.UnescapeDataString(id);
            var item = list.FirstOrDefault(i => string.Equals(Text(i, "id"), id, StringComparison.OrdinalIgnoreCase));
            if (item is not null)
                return (item, remainder.Length > 1 ? remainder[1..] : null);
        }
        return null;
    }

    static bool Matches(string basePath, string collection, string id) =>
        basePath.Equals($"{collection}/{id}", StringComparison.OrdinalIgnoreCase)
        || basePath.Equals($"{collection}/{Uri.EscapeDataString(id)}", StringComparison.OrdinalIgnoreCase)
        || basePath.Equals($"{collection}('{id}')", StringComparison.OrdinalIgnoreCase);

    IEnumerable<JsonObject> Items(string collection) => _collections.TryGetValue(collection, out var list) ? list : [];

    static JsonObject Page(IEnumerable<JsonObject> items, Dictionary<string, string> query)
    {
        if (query.TryGetValue("$top", out var top) && int.TryParse(top, out var count))
            items = items.Take(count);
        return new JsonObject { ["value"] = new JsonArray(items.Select(i => (JsonNode?)Visible(i)).ToArray()) };
    }

    /// <summary>Handles the filters Movewise sends: "a eq 'x'", "startswith(a,'x')", "tags/any(t:t eq 'x')", joined by and/or.</summary>
    static IEnumerable<JsonObject> Filter(IEnumerable<JsonObject> items, Dictionary<string, string> query)
    {
        if (!query.TryGetValue("$filter", out var filter))
            return items;
        var any = filter.Split(" or ", StringSplitOptions.TrimEntries)
            .Select(part => part.Split(" and ", StringSplitOptions.TrimEntries))
            .ToList();
        return items.Where(item => any.Any(all => all.All(condition => Test(item, condition))));
    }

    static bool Test(JsonObject item, string condition)
    {
        if (condition.StartsWith("startswith(", StringComparison.OrdinalIgnoreCase))
        {
            var inner = condition["startswith(".Length..].TrimEnd(')');
            var comma = inner.IndexOf(',');
            return Text(item, inner[..comma].Trim()).StartsWith(Literal(inner[(comma + 1)..]), StringComparison.OrdinalIgnoreCase);
        }
        if (condition.StartsWith("tags/any(", StringComparison.OrdinalIgnoreCase))
        {
            var value = Literal(condition[(condition.IndexOf(" eq ", StringComparison.Ordinal) + 4)..].TrimEnd(')'));
            return (item["tags"] as JsonArray ?? []).Any(t => string.Equals(t?.ToString(), value, StringComparison.OrdinalIgnoreCase));
        }
        var eq = condition.IndexOf(" eq ", StringComparison.Ordinal);
        if (eq < 0)
            return true;
        return string.Equals(Text(item, condition[..eq].Trim()), Literal(condition[(eq + 4)..]), StringComparison.OrdinalIgnoreCase);
    }

    static string Literal(string text)
    {
        text = text.Trim();
        return text.Length >= 2 && text[0] == '\'' && text[^1] == '\'' ? text[1..^1].Replace("''", "'") : text;
    }

    static (string BasePath, Dictionary<string, string> Query) Split(string path)
    {
        var question = path.IndexOf('?');
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (question < 0)
            return (path, query);
        foreach (var pair in path[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals > 0)
                query[pair[..equals]] = Uri.UnescapeDataString(pair[(equals + 1)..]);
        }
        return (path[..question], query);
    }

    /// <summary>A copy without the demo's own bookkeeping (stored assignments).</summary>
    static JsonObject Visible(JsonObject item)
    {
        var copy = (JsonObject)item.DeepClone();
        foreach (var key in copy.Select(p => p.Key).Where(k => k.StartsWith(Hidden, StringComparison.Ordinal)).ToList())
            copy.Remove(key);
        return copy;
    }

    static string Text(JsonObject item, string name) =>
        item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    static GraphException NotFound(string path) => new(HttpStatusCode.NotFound, "Request_ResourceNotFound", $"Nothing at {path} in the demo tenant.");

    // A little delay, so progress messages show as they would against a real tenant.
    static Task Pause(CancellationToken ct) => Task.Delay(25, ct);
}
