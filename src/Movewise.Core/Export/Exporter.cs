using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Movewise.Core.Registry;

namespace Movewise.Core.Export;

/// <summary>Read access to Microsoft Graph, as raw JSON. Implemented by the Graph client; faked in tests.</summary>
public interface IGraphReader
{
    Task<JsonObject> GetObjectAsync(string path, CancellationToken ct = default);

    /// <summary>Every item of a collection, across all pages.</summary>
    Task<IReadOnlyList<JsonObject>> GetCollectionAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// Only the reads of another Graph client. The source is given one of these, so nothing that writes (deploy, rollback)
/// can be handed the source's Graph, even by mistake: <see cref="TenantClients.GraphWriter"/> refuses it.
/// </summary>
public sealed class ReadOnlyGraph(IGraphReader inner) : IGraphReader
{
    public Task<JsonObject> GetObjectAsync(string path, CancellationToken ct = default) => inner.GetObjectAsync(path, ct);

    public Task<IReadOnlyList<JsonObject>> GetCollectionAsync(string path, CancellationToken ct = default) => inner.GetCollectionAsync(path, ct);
}

/// <summary>A policy type that couldn't be read, for example because the service isn't licensed in the source.</summary>
public sealed record ExportWarning(ResourceType Type, string Message);

public sealed record ExportResult(IReadOnlyList<ExportedResource> Items, IReadOnlyList<ExportWarning> Warnings);

/// <summary>Source configuration that must be recreated outside Movewise's policy deployment flow.</summary>
public sealed record ManualSetupItem(string Area, string Name, string Details);

public sealed record ManualSetupResult(IReadOnlyList<ManualSetupItem> Items, IReadOnlyList<string> Warnings);

/// <summary>Reads every item of the given policy types from a tenant, with details and assignments, and normalizes it.</summary>
public static class Exporter
{
    const int Parallelism = 4;

    /// <summary>Reads the source's connectors and DKIM domains for the set-up-by-hand report.</summary>
    public static async Task<ManualSetupResult> ReadManualSetupAsync(TenantClients tenant, CancellationToken ct = default)
    {
        var items = new List<ManualSetupItem>();
        var warnings = new List<string>();
        if (tenant.Exchange is null)
            return new ManualSetupResult(items, ["Exchange Online PowerShell isn't connected; connectors and DKIM domains couldn't be read."]);

        await Read("Mail flow connectors", "Get-InboundConnector", "Get-OutboundConnector", (area, connector) =>
            items.Add(new ManualSetupItem(area, Value(connector, "Name") ?? "(unnamed)",
                string.Join("; ", new[] { Value(connector, "ConnectorType"), Value(connector, "SenderDomains"), Value(connector, "RecipientDomains") }.Where(v => !string.IsNullOrWhiteSpace(v))))));
        await Read("DKIM", "Get-DkimSigningConfig", null, (area, dkim) =>
        {
            var domain = Value(dkim, "Domain");
            if (!string.IsNullOrWhiteSpace(domain))
                items.Add(new ManualSetupItem(area, domain, $"Enabled: {Value(dkim, "Enabled") ?? "unknown"}"));
        });
        return new ManualSetupResult(items, warnings);

        async Task Read(string area, string first, string? second, Action<string, JsonObject> add)
        {
            foreach (var cmdlet in second is null ? new[] { first } : new[] { first, second })
            {
                try
                {
                    foreach (var row in await tenant.Exchange.InvokeAsync(cmdlet, null, ct))
                        add(area, row);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warnings.Add($"{cmdlet}: {ex.Message}");
                }
            }
        }

        static string? Value(JsonObject item, string name) => item[name] switch
        {
            null => null,
            JsonArray array => string.Join(", ", array.Select(v => v?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v))),
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            var value => value.ToString(),
        };
    }

    public static Task<ExportResult> ExportAsync(
        IGraphReader graph, IEnumerable<ResourceType> types, IProgress<string>? progress = null, CancellationToken ct = default) =>
        ExportAsync(new TenantClients(graph), types, progress, ct);

    public static async Task<ExportResult> ExportAsync(
        TenantClients tenant, IEnumerable<ResourceType> types, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var items = new List<ExportedResource>();
        var warnings = new List<ExportWarning>();

        foreach (var type in types)
        {
            progress?.Report($"Reading {type.PluralName.ToLowerInvariant()}…");
            try
            {
                items.AddRange(!type.UsesPowerShell && !type.IsSettings
                    ? await ExportTypeAsync(tenant.ApiFor(type), type, ct)
                    : await ExportPowerShellTypeAsync(tenant, type, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Microsoft has switched some policy types off in some tenants (Teams app permission policies, where
                // app-centric management replaced them). There's nothing to read, so it's not a problem to report.
                if (ex.Message.Contains("not currently enabled in flighting", StringComparison.OrdinalIgnoreCase))
                    continue;

                // One unreadable type (unlicensed service, missing permission) shouldn't hide everything else.
                warnings.Add(new ExportWarning(type, ex.Message));
            }
        }

        return new ExportResult(LinkByName(items), warnings);
    }

    /// <summary>
    /// Some policies name the objects they use instead of giving their ID (a label policy may list its labels by name).
    /// Where a name matches exactly one item read in the same export, it's replaced with that item's ID, so it's
    /// matched and created like any other reference.
    /// </summary>
    static List<ExportedResource> LinkByName(List<ExportedResource> items)
    {
        var byType = items.GroupBy(i => i.Type.Id).ToDictionary(g => g.Key, g => g.ToList());
        return items.Select(item =>
        {
            var ids = new Dictionary<(string Type, string Value), string>();
            foreach (var dependency in item.Dependencies)
            {
                if (!byType.TryGetValue(dependency.TargetType, out var targets)
                    || targets.Any(t => t.SourceId.Equals(dependency.Value, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var named = targets.Where(t => Named(t, dependency.Value)).ToList();
                if (named.Count == 1)
                    ids[(dependency.TargetType, dependency.Value)] = named[0].SourceId;
            }
            if (ids.Count == 0)
                return item;

            var settings = (JsonObject)item.Settings.DeepClone();
            foreach (var rule in item.Type.References)
                ReplaceAt(settings, rule.Path.Split('.'), 0, value => ids.GetValueOrDefault((rule.TargetType, value)));
            return item with { Settings = settings, Dependencies = DependencyExtractor.Extract(settings, item.Type) };
        }).ToList();

        static bool Named(ExportedResource target, string name) =>
            target.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)
            || (target.Settings["DisplayName"] is JsonValue display && display.TryGetValue<string>(out var text) && text.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Replaces the values at a reference path (such as "grantControls.authenticationStrength.id") for which <paramref name="replace"/> gives a new one.</summary>
    internal static void ReplaceAt(JsonNode? node, string[] segments, int index, Func<string, string?> replace)
    {
        var segment = segments[index];
        var isArray = segment.EndsWith("[]", StringComparison.Ordinal);
        var name = isArray ? segment[..^2] : segment;
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out var child) || child is null)
            return;

        if (index < segments.Length - 1)
        {
            foreach (var next in isArray && child is JsonArray array ? array.ToList() : [child])
                ReplaceAt(next, segments, index + 1, replace);
            return;
        }

        if (isArray && child is JsonArray values)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] is JsonValue v && v.TryGetValue<string>(out var text) && replace(text) is { } id)
                    values[i] = id;
            }
        }
        else if (child is JsonValue single && single.TryGetValue<string>(out var text) && replace(text) is { } id)
        {
            obj[name] = id;
        }
    }

    static async Task<IReadOnlyList<ExportedResource>> ExportTypeAsync(IGraphReader graph, ResourceType type, CancellationToken ct)
    {
        var listed = (await graph.GetCollectionAsync(type.ListPath, ct))
            .Where(item => type.Include?.Invoke(item) ?? true)
            .ToList();

        var results = new ExportedResource[listed.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, listed.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (index, token) => results[index] = await ReadOneAsync(graph, type, listed[index], token));

        return results.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static async Task<ExportedResource> ReadOneAsync(IGraphReader graph, ResourceType type, JsonObject listed, CancellationToken ct)
    {
        var id = listed[type.IdProperty]?.GetValue<string>() ?? "";
        var raw = type.ItemPath is null ? listed : await graph.GetObjectAsync(Expand(type.ItemPath, id), ct);
        var settings = Normalizer.Normalize(raw, type);

        if (type.AssignmentsPath is not null)
        {
            var assignments = await graph.GetCollectionAsync(Expand(type.AssignmentsPath, id), ct);
            settings["assignments"] = new JsonArray(assignments.Select(NormalizeAssignment).ToArray<JsonNode?>());
            settings = (JsonObject)Normalizer.Canonicalize(settings)!;
        }

        if (type.Children is { } children)
        {
            var items = await graph.GetCollectionAsync(Expand(children.ReadPath, id), ct);
            settings[children.Property] = new JsonArray(items.Select(i => (JsonNode?)i.DeepClone()).ToArray());
            settings = (JsonObject)Normalizer.Canonicalize(settings)!;
        }

        return new ExportedResource(
            type,
            id,
            raw[type.IdentityProperty]?.GetValue<string>() ?? "(unnamed)",
            settings,
            DependencyExtractor.Extract(settings, type));
    }

    /// <summary>
    /// Exchange, Defender and Purview policies: the policy with its rules under "rules". A rule's link back to its
    /// policy is left out, since deploying sets it to the new policy.
    /// </summary>
    static async Task<IReadOnlyList<ExportedResource>> ExportPowerShellTypeAsync(TenantClients tenant, ResourceType type, CancellationToken ct)
    {
        var listed = await ResourceReader.ListAsync(tenant, type, ct);

        // Settings are one object per tenant, known by their type rather than a name that differs between tenants.
        if (type.IsSettings)
        {
            return listed.Take(1).Select(raw =>
            {
                var shaped = (JsonObject)raw.DeepClone();
                type.AfterRead?.Invoke(shaped);
                var settings = Normalizer.Normalize(shaped, type);
                return new ExportedResource(type, type.Id, type.DisplayName, settings, DependencyExtractor.Extract(settings, type));
            }).ToList();
        }

        var names = listed.Select(item => item[type.IdentityProperty]?.GetValue<string>() ?? "").ToList();
        var rules = type.Rules is null ? null : await ResourceReader.RulesAsync(tenant, type, names, ct);
        var groupAssignments = type.TeamsPolicyType is null ? null : await TeamsGroupAssignmentsAsync(tenant.PowerShellFor(type), type.TeamsPolicyType, ct);

        return listed.Select((raw, index) =>
            {
                var shaped = (JsonObject)raw.DeepClone();
                type.AfterRead?.Invoke(shaped);
                var settings = Normalizer.Normalize(shaped, type);
                if (rules is not null)
                {
                    settings["rules"] = new JsonArray(rules[names[index]].Select(rule =>
                    {
                        var cleaned = Normalizer.Normalize(rule, type);
                        cleaned.Remove(type.Rules!.PolicyProperty);
                        cleaned.Remove(type.Rules.PolicyParameter);
                        return (JsonNode?)cleaned;
                    }).ToArray());
                    settings = (JsonObject)Normalizer.Canonicalize(settings)!;
                }
                if (groupAssignments is not null)
                {
                    settings["groupAssignments"] = new JsonArray(groupAssignments
                        .Where(a => string.Equals(a["PolicyName"]?.GetValue<string>(), names[index], StringComparison.OrdinalIgnoreCase))
                        .OrderBy(a => a["Rank"]?.GetValue<int>() ?? int.MaxValue)
                        .Select(a => (JsonNode?)new JsonObject { ["GroupId"] = a["GroupId"]?.DeepClone(), ["Rank"] = a["Rank"]?.DeepClone() })
                        .ToArray());
                    settings = (JsonObject)Normalizer.Canonicalize(settings)!;
                }
                return new ExportedResource(type, raw["id"]?.GetValue<string>() ?? names[index], names[index], settings, DependencyExtractor.Extract(settings, type));
            })
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Which groups each Teams policy of a type is assigned to, and in what order.</summary>
    static async Task<IReadOnlyList<JsonObject>> TeamsGroupAssignmentsAsync(IPowerShell teams, string policyType, CancellationToken ct) =>
        await teams.InvokeAsync("Get-CsGroupPolicyAssignment", new Dictionary<string, JsonNode?> { ["PolicyType"] = policyType }, ct);

    /// <summary>
    /// Reads the source's enterprise apps for the Applications section: other organizations' apps, such as multi-tenant
    /// SaaS apps, which have the same app ID in every tenant. Microsoft's own apps, managed identities and the source's
    /// own app registrations are left out. Each comes with the users and groups assigned to it ("assignedUsers",
    /// "assignedGroups", each item a principal and app role), its owners ("owners", users only), the API permissions
    /// granted to it ("apiPermissions", listed for the admin, never granted by Movewise) and what can't be copied
    /// ("notMigrated"). An app whose details can't be read is left out with a warning.
    /// </summary>
    public static async Task<ExportResult> ExportAppsAsync(
        IGraphReader graph, string sourceTenantId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var type = ResourceRegistry.Get(ResourceRegistry.EnterpriseApp);
        progress?.Report("Reading enterprise apps…");
        var listed = (await graph.GetCollectionAsync(type.ListPath, ct))
            .Where(item => type.Include!(item) && !string.Equals(Text(item, "appOwnerOrganizationId"), sourceTenantId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // The APIs apps are granted permissions on (Microsoft Graph, mostly), read once each for their names.
        var resources = new ConcurrentDictionary<string, Lazy<Task<JsonObject?>>>(StringComparer.OrdinalIgnoreCase);
        var results = new ExportedResource?[listed.Count];
        var warnings = new ExportWarning?[listed.Count];
        var done = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, listed.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (index, token) =>
            {
                try
                {
                    results[index] = await ReadAppAsync(graph, type, listed[index], resources, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warnings[index] = new ExportWarning(type, $"{Text(listed[index], "displayName")}: {ex.Message}");
                }
                progress?.Report($"Read {Interlocked.Increment(ref done)} of {listed.Count} apps…");
            });

        return new ExportResult(
            results.OfType<ExportedResource>().OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            warnings.OfType<ExportWarning>().ToList());
    }

    static async Task<ExportedResource> ReadAppAsync(
        IGraphReader graph, ResourceType type, JsonObject raw, ConcurrentDictionary<string, Lazy<Task<JsonObject?>>> resources, CancellationToken ct)
    {
        var id = Text(raw, "id");
        var app = $"v1.0/servicePrincipals/{Uri.EscapeDataString(id)}";
        var settings = Normalizer.Normalize(raw, type);

        // Assignment IDs mean nothing in another tenant: who, and with which app role, is what's kept.
        var assigned = await graph.GetCollectionAsync($"{app}/appRoleAssignedTo", ct);
        JsonArray Assigned(string principalType) => new(assigned
            .Where(a => Text(a, "principalType") == principalType)
            .Select(a => (JsonNode?)new JsonObject { ["principalId"] = Text(a, "principalId"), ["appRoleId"] = Text(a, "appRoleId") })
            .ToArray());
        settings["assignedUsers"] = Assigned("User");
        settings["assignedGroups"] = Assigned("Group");

        // Apps can own apps too; only users are matched in the destination.
        var owners = await graph.GetCollectionAsync($"{app}/owners?$select=id", ct);
        var ownerUsers = owners.Where(o => Text(o, "@odata.type") is "" or "#microsoft.graph.user").ToList();
        settings["owners"] = new JsonArray(ownerUsers.Select(o => (JsonNode?)Text(o, "id")).ToArray());

        var permissions = new List<string>();
        var delegated = await graph.GetCollectionAsync(
            $"v1.0/oauth2PermissionGrants?$filter={Uri.EscapeDataString($"clientId eq {GraphQuery.Literal(id)}")}", ct);
        foreach (var grant in delegated.GroupBy(g => Text(g, "resourceId")))
        {
            var scopes = grant.SelectMany(g => Text(g, "scope").Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
            permissions.Add($"{await ResourceNameAsync(grant.Key, null)}: {string.Join(", ", scopes)} (delegated)");
        }
        var application = await graph.GetCollectionAsync($"{app}/appRoleAssignments", ct);
        foreach (var grant in application.GroupBy(g => Text(g, "resourceId")))
        {
            var resource = await ResourceAsync(grant.Key);
            var roles = grant.Select(g => Text(g, "appRoleId"))
                .Select(roleId => (resource?["appRoles"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(r => Text(r, "id") == roleId) is { } role
                    ? Text(role, "value") : roleId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
            permissions.Add($"{await ResourceNameAsync(grant.Key, Text(grant.First(), "resourceDisplayName"))}: {string.Join(", ", roles)} (application)");
        }
        settings["apiPermissions"] = new JsonArray(permissions.Select(p => (JsonNode?)p).ToArray());

        var notMigrated = new List<string>();
        var otherApps = assigned.Count(a => Text(a, "principalType") == "ServicePrincipal");
        if (otherApps > 0)
            notMigrated.Add($"Assigned to {otherApps} other app{(otherApps == 1 ? "" : "s")}. Apps are different in the destination, so assign them again by hand.");
        if (owners.Count > ownerUsers.Count)
            notMigrated.Add("Owned by other apps as well as users. Only the users are added as owners.");
        if (raw["passwordCredentials"] is JsonArray { Count: > 0 } secrets)
            notMigrated.Add($"Client secrets ({secrets.Count}) can't be copied. Create new ones in the destination and update what uses them.");
        switch (Text(raw, "preferredSingleSignOnMode"))
        {
            case "saml":
                notMigrated.Add("SAML single sign-on isn't migrated yet. Set it up in the destination, with a new signing certificate, and give the app's vendor the new certificate or metadata.");
                break;
            case "password":
                notMigrated.Add("Password-based single sign-on: users' saved credentials can't be copied. Set it up again in the destination.");
                break;
            default:
                if (raw["keyCredentials"] is JsonArray { Count: > 0 } certificates)
                    notMigrated.Add($"Certificates ({certificates.Count}) can't be copied: their private keys can't be exported. Add new ones in the destination.");
                break;
        }
        settings["notMigrated"] = new JsonArray(notMigrated.Select(n => (JsonNode?)n).ToArray());

        settings = (JsonObject)Normalizer.Canonicalize(settings)!;
        return new ExportedResource(type, id, Text(raw, "displayName") is { Length: > 0 } name ? name : "(unnamed)", settings, DependencyExtractor.Extract(settings, type));

        Task<JsonObject?> ResourceAsync(string resourceId) => resources.GetOrAdd(resourceId, key => new Lazy<Task<JsonObject?>>(async () =>
        {
            try
            {
                return await graph.GetObjectAsync(GraphQuery.Item("v1.0/servicePrincipals", key, "id,displayName,appRoles"), CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        })).Value;

        async Task<string> ResourceNameAsync(string resourceId, string? known) =>
            (await ResourceAsync(resourceId)) is { } resource && Text(resource, "displayName") is { Length: > 0 } name ? name
            : !string.IsNullOrEmpty(known) ? known
            : resourceId;
    }

    static string Text(JsonObject item, string name) =>
        item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    // Assignment IDs are made up from the policy and group IDs, so they mean nothing in another tenant.
    static JsonNode NormalizeAssignment(JsonObject assignment)
    {
        var result = (JsonObject)Normalizer.Canonicalize(assignment)!;
        result.Remove("id");
        result.Remove("sourceId");
        return result;
    }

    static string Expand(string template, string id) => template.Replace("{id}", Uri.EscapeDataString(id));
}
