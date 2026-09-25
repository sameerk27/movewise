using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Movewise.Core.Export;
using Movewise.Core.Registry;

namespace Movewise.Core.Mapping;

/// <summary>
/// Finds, for every object the selected policies point at, the matching object in the destination.
/// Groups match by name, then mail nickname; users by email, then username and name together; apps by app ID;
/// locations, strengths, filters and scope tags by name, or are created along with the policies;
/// domains, email recipients and SharePoint sites follow the source domain's match, and anything outside
/// the source tenant (a partner's domain or address) is kept as it is.
/// </summary>
public static class Matcher
{
    const int Parallelism = 4;
    const string GroupSelect = "id,displayName,mailNickname,description,groupTypes,securityEnabled,mailEnabled,membershipRule,membershipRuleProcessingState";
    const string UserSelect = "id,displayName,userPrincipalName,mail";
    const string AppSelect = "id,appId,displayName";

    sealed record Context(
        IGraphReader Source,
        IGraphReader Destination,
        IReadOnlyCollection<ExportedResource> Exported,
        HashSet<(string Type, string Id)> Selected,
        TenantClients DestinationClients)
    {
        public ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<JsonObject>>>> DestinationLists { get; } = new();

        public Lazy<Task<IReadOnlyList<JsonObject>>> SourceDomains { get; } = new(() => ListDomainsAsync(Source, CancellationToken.None));
        public Lazy<Task<IReadOnlyList<JsonObject>>> DestinationDomains { get; } = new(() => ListDomainsAsync(Destination, CancellationToken.None));
        public Lazy<Task<string?>> SourceSharePoint { get; } = new(() => SharePointHostAsync(Source, CancellationToken.None));
        public Lazy<Task<string?>> DestinationSharePoint { get; } = new(() => SharePointHostAsync(Destination, CancellationToken.None));

        /// <summary>Source domain → destination domain, once domains are matched. Null where the domain was removed or isn't matched.</summary>
        public ConcurrentDictionary<string, string?> DomainMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <param name="selected">Policies chosen for migration.</param>
    /// <param name="exported">Everything read from the source, selected or not.</param>
    /// <param name="previous">An earlier plan whose manual decisions are kept.</param>
    public static Task<MappingPlan> BuildAsync(
        IGraphReader source,
        IGraphReader destination,
        IReadOnlyCollection<ExportedResource> selected,
        IReadOnlyCollection<ExportedResource> exported,
        MappingPlan? previous = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        BuildAsync(source, new TenantClients(destination), selected, exported, previous, progress, ct);

    /// <param name="destination">The destination, with PowerShell for objects that live there (sensitivity labels).</param>
    public static async Task<MappingPlan> BuildAsync(
        IGraphReader source,
        TenantClients destination,
        IReadOnlyCollection<ExportedResource> selected,
        IReadOnlyCollection<ExportedResource> exported,
        MappingPlan? previous = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var references = selected
            .SelectMany(policy => policy.Dependencies.Select(d => (d.TargetType, d.Value, Policy: policy.DisplayName)))
            .ToList();
        var context = new Context(source, destination.Graph, exported, selected.Select(e => (e.Type.Id, e.SourceId)).ToHashSet(), destination);

        // An address's domain is mapped once, and every address in it follows. So the source's own domains
        // behind recipient addresses are matched too, even when no policy names the domain itself.
        if (references.Any(r => r.TargetType == ResourceRegistry.Recipient))
        {
            var own = (await context.SourceDomains.Value).Select(d => Text(d, "id")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            references.AddRange(references
                .Where(r => r.TargetType == ResourceRegistry.Recipient && RecipientDomain(r.Value) is { } domain && own.Contains(domain))
                .Select(r => (ResourceRegistry.Domain, RecipientDomain(r.Value)!.ToLowerInvariant(), r.Policy))
                .ToList());
        }

        var needed = references
            .GroupBy(x => (x.TargetType, Value: x.Value.ToLowerInvariant()))
            .Select(g => (Type: g.Key.TargetType, Id: g.First().Value, UsedBy: (IReadOnlyList<string>)g.Select(x => x.Policy).Distinct().Order().ToList()))
            .ToList();

        var done = 0;
        async Task<Mapping[]> MatchAllAsync(IReadOnlyList<(string Type, string Id, IReadOnlyList<string> UsedBy)> items)
        {
            var results = new Mapping[items.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, items.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
                async (index, token) =>
                {
                    var (type, id, usedBy) = items[index];
                    var earlier = previous?.Find(type, id);
                    results[index] = earlier is { IsDecision: true }
                        ? earlier.WithUsedBy(usedBy)
                        : await MatchAsync(type, id, usedBy, context, token);
                    progress?.Report($"Matched {Interlocked.Increment(ref done)} of {needed.Count} objects…");
                });
            return results;
        }

        // Domains first: addresses and OneDrive sites are rewritten with the domains' matches.
        var domains = await MatchAllAsync(needed.Where(n => n.Type == ResourceRegistry.Domain).ToList());
        foreach (var domain in domains)
            context.DomainMap[domain.Source.Id] = domain.IsResolved && domain.Kind != MatchKind.RemoveFromPolicies ? domain.Destination?.Id : null;
        var rest = await MatchAllAsync(needed.Where(n => n.Type != ResourceRegistry.Domain).ToList());

        return new MappingPlan(domains.Concat(rest));
    }

    static Task<Mapping> MatchAsync(string type, string id, IReadOnlyList<string> usedBy, Context context, CancellationToken ct) => type switch
    {
        ResourceRegistry.Group => MatchGroupAsync(id, usedBy, context, ct),
        ResourceRegistry.User => MatchUserAsync(id, usedBy, context, ct),
        ResourceRegistry.Application => MatchAppAsync(id, usedBy, context, ct),
        ResourceRegistry.Domain => MatchDomainAsync(id, usedBy, context),
        ResourceRegistry.Recipient => MatchRecipientAsync(id, usedBy, context, ct),
        ResourceRegistry.Site => MatchSiteAsync(id, usedBy, context, ct),
        ResourceRegistry.IntuneApp => MatchIntuneAppAsync(id, usedBy, context, ct),
        _ => MatchExportedAsync(type, id, usedBy, context, ct),
    };

    const string MobileApps = "beta/deviceAppManagement/mobileApps";

    /// <summary>
    /// An Intune app has a different ID in every tenant, and its content can't be copied: it's matched to the destination's
    /// app with the same name and kind, or the admin adds it there first.
    /// </summary>
    static async Task<Mapping> MatchIntuneAppAsync(string id, IReadOnlyList<string> usedBy, Context context, CancellationToken ct)
    {
        var source = await TryGetAsync(context.Source, $"{MobileApps}/{Uri.EscapeDataString(id)}?$select=id,displayName", ct);
        if (source is null)
            return MissingInSource(ResourceRegistry.IntuneApp, id, usedBy);

        var name = Text(source, "displayName");
        var mapping = new Mapping
        {
            TargetType = ResourceRegistry.IntuneApp,
            Source = new ObjectRef(id, name, source["@odata.type"]?.GetValue<string>()?.Replace("#microsoft.graph.", "")),
            UsedBy = usedBy,
            Note = "Intune apps aren't copied. Add this app to Intune in the destination, then match again, or remove it from the policies.",
        };

        var sameName = (await FindAsync(context.Destination, MobileApps, $"displayName eq {GraphQuery.Literal(name)}", "id,displayName", ct))
            .Where(a => source["@odata.type"] is null || Text(a, "@odata.type") == Text(source, "@odata.type"))
            .ToList();
        return sameName.Count == 1
            ? Resolved(mapping, MatchKind.SameName, new ObjectRef(Text(sameName[0], "id"), Text(sameName[0], "displayName")))
            : mapping;
    }

    /// <summary>
    /// A domain the source doesn't own (a partner's) is kept. The same verified domain in the destination is a match,
    /// as is the destination's .onmicrosoft.com domain for the source's. Otherwise the admin chooses.
    /// </summary>
    static async Task<Mapping> MatchDomainAsync(string domain, IReadOnlyList<string> usedBy, Context context)
    {
        var mapping = new Mapping { TargetType = ResourceRegistry.Domain, Source = new ObjectRef(domain, domain), UsedBy = usedBy };
        var own = (await context.SourceDomains.Value).FirstOrDefault(d => Is(d, "id", domain));
        if (own is null)
            return Resolved(mapping, MatchKind.External, new ObjectRef(domain, domain));

        var destinationDomains = await context.DestinationDomains.Value;
        if (destinationDomains.Any(d => Is(d, "id", domain) && d["isVerified"]?.GetValue<bool>() == true))
            return Resolved(mapping, MatchKind.SameName, new ObjectRef(domain, domain));

        if (own["isInitial"]?.GetValue<bool>() == true && destinationDomains.FirstOrDefault(d => d["isInitial"]?.GetValue<bool>() == true) is { } initial)
            return Resolved(mapping, MatchKind.InitialDomain, new ObjectRef(Text(initial, "id"), Text(initial, "id")));

        return new Mapping
        {
            TargetType = ResourceRegistry.Domain,
            Source = mapping.Source,
            UsedBy = usedBy,
            Note = "Not verified in the destination. Choose the domain that replaces it; addresses in it follow. If you'll move this domain later, verify it there first and match again.",
        };
    }

    /// <summary>An address in the source's domains is rewritten with the domain's match and looked up in the destination.</summary>
    static async Task<Mapping> MatchRecipientAsync(string value, IReadOnlyList<string> usedBy, Context context, CancellationToken ct)
    {
        var source = new ObjectRef(value, value);
        var domain = DomainOf(value);

        // A whole domain, as in label encryption rights ("contoso.com: VIEW"): it follows the domain's match.
        if (domain is null && LooksLikeDomain(value))
        {
            var ownDomain = (await context.SourceDomains.Value).Any(d => Is(d, "id", value));
            if (!ownDomain)
                return Resolved(new Mapping { TargetType = ResourceRegistry.Recipient, Source = source, UsedBy = usedBy }, MatchKind.External, source);
            return context.DomainMap.TryGetValue(value, out var matched) && matched is not null
                ? Resolved(new Mapping { TargetType = ResourceRegistry.Recipient, Source = source, UsedBy = usedBy }, MatchKind.SameName, new ObjectRef(matched, matched))
                : new Mapping { TargetType = ResourceRegistry.Recipient, Source = source, UsedBy = usedBy, Note = $"Match the domain {value} first, then match again." };
        }

        if (domain is null)
        {
            // A name rather than an address.
            var byName = await FindRecipientsAsync(context.Destination, $"displayName eq {GraphQuery.Literal(value)}", ct);
            return byName.Count == 1
                ? Resolved(new Mapping { TargetType = ResourceRegistry.Recipient, Source = source, UsedBy = usedBy }, MatchKind.SameName, Recipient(byName[0]))
                : new Mapping { TargetType = ResourceRegistry.Recipient, Source = source, UsedBy = usedBy, Note = "Couldn't find exactly one mailbox or group with this name in the destination." };
        }

        var own = (await context.SourceDomains.Value).Any(d => Is(d, "id", domain));
        if (!own)
            return Resolved(new Mapping { TargetType = ResourceRegistry.Recipient, Source = source, UsedBy = usedBy }, MatchKind.External, source);

        var mappedDomain = context.DomainMap.TryGetValue(domain, out var mapped) && mapped is not null ? mapped : domain;
        var address = value[..value.LastIndexOf('@')] + "@" + mappedDomain;
        if (await FindRecipientAsync(context.Destination, address, ct) is { } found)
            return Resolved(new Mapping { TargetType = ResourceRegistry.Recipient, Source = source, UsedBy = usedBy }, MatchKind.SameMail, Recipient(found));

        return new Mapping
        {
            TargetType = ResourceRegistry.Recipient,
            Source = source,
            UsedBy = usedBy,
            Note = mapped is null
                ? $"No mailbox or group with this address in the destination, and the domain {domain} isn't matched yet. Match the domain, then match again."
                : $"No mailbox or group with {address} in the destination. Pick one, or remove it from the policies.",
        };

        static ObjectRef Recipient(JsonObject r) => new(RecipientAddress(r), Text(r, "displayName"), RecipientAddress(r));
    }

    /// <summary>
    /// A site in the source's SharePoint or OneDrive is looked for at the same path in the destination's.
    /// OneDrive paths carry the owner's domain (…/personal/anna_contoso_com), which follows the domain's match.
    /// </summary>
    static async Task<Mapping> MatchSiteAsync(string url, IReadOnlyList<string> usedBy, Context context, CancellationToken ct)
    {
        var source = new ObjectRef(url, url);
        Mapping Unmatched(string note) => new() { TargetType = ResourceRegistry.Site, Source = source, UsedBy = usedBy, Note = note };

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Unmatched("This isn't a site address.");

        var sourceHost = await context.SourceSharePoint.Value;
        var destinationHost = await context.DestinationSharePoint.Value;
        if (sourceHost is null || destinationHost is null)
            return Unmatched("Couldn't read the tenants' SharePoint addresses. Check that the app has the Sites.Read.All permission.");

        string? host = null;
        if (uri.Host.Equals(sourceHost, StringComparison.OrdinalIgnoreCase))
            host = destinationHost;
        else if (uri.Host.Equals(OneDriveHost(sourceHost), StringComparison.OrdinalIgnoreCase))
            host = OneDriveHost(destinationHost);
        if (host is null)
            return Resolved(new Mapping { TargetType = ResourceRegistry.Site, Source = source, UsedBy = usedBy }, MatchKind.External, source);

        var path = uri.AbsolutePath.TrimEnd('/');
        foreach (var (from, to) in context.DomainMap)
        {
            if (to is not null)
                path = path.Replace("_" + from.Replace('.', '_'), "_" + to.Replace('.', '_'), StringComparison.OrdinalIgnoreCase);
        }

        var target = $"https://{host}{path}";
        return await FindSiteAsync(context.Destination, target, ct) is { } site
            ? Resolved(new Mapping { TargetType = ResourceRegistry.Site, Source = source, UsedBy = usedBy }, MatchKind.SamePath,
                new ObjectRef(Text(site, "webUrl") is { Length: > 0 } web ? web : target, Text(site, "displayName"), Text(site, "webUrl")))
            : Unmatched($"No site at {target} in the destination. Create or migrate the site, then match again; or pick another one, or remove it from the policies.");
    }

    static async Task<Mapping> MatchGroupAsync(string id, IReadOnlyList<string> usedBy, Context context, CancellationToken ct)
    {
        var group = await TryGetAsync(context.Source, GraphQuery.Item("v1.0/groups", id, GroupSelect), ct);
        if (group is null)
            return MissingInSource(ResourceRegistry.Group, id, usedBy);

        var name = Text(group, "displayName");
        var nickname = Text(group, "mailNickname");
        var byName = await FindAsync(context.Destination, "v1.0/groups", $"displayName eq {GraphQuery.Literal(name)}", "id,displayName,mailNickname", ct);
        var mapping = new Mapping
        {
            TargetType = ResourceRegistry.Group,
            Source = new ObjectRef(id, name, NullIfEmpty(nickname)),
            UsedBy = usedBy,
            SourceDetails = group,
            Note = byName.Count > 1 ? $"{byName.Count} groups in the destination have this name. Choose the right one." : null,
        };

        if (byName.Count == 1)
            return Resolved(mapping, MatchKind.SameName, Group(byName[0]));

        if (!string.IsNullOrEmpty(nickname))
        {
            var byNickname = await FindAsync(context.Destination, "v1.0/groups", $"mailNickname eq {GraphQuery.Literal(nickname)}", "id,displayName,mailNickname", ct);
            if (byNickname.Count == 1)
                return Resolved(mapping, MatchKind.SameMailNickname, Group(byNickname[0]));
        }

        return mapping;

        static ObjectRef Group(JsonObject g) => new(Text(g, "id"), Text(g, "displayName"), NullIfEmpty(Text(g, "mailNickname")));
    }

    static async Task<Mapping> MatchUserAsync(string id, IReadOnlyList<string> usedBy, Context context, CancellationToken ct)
    {
        var user = await TryGetAsync(context.Source, GraphQuery.Item("v1.0/users", id, UserSelect), ct);
        if (user is null)
            return MissingInSource(ResourceRegistry.User, id, usedBy);

        var upn = Text(user, "userPrincipalName");
        var mail = Text(user, "mail");
        var mapping = new Mapping
        {
            TargetType = ResourceRegistry.User,
            Source = new ObjectRef(id, Text(user, "displayName"), upn),
            UsedBy = usedBy,
            SourceDetails = user,
        };

        if (!string.IsNullOrEmpty(mail))
        {
            var byMail = await FindAsync(context.Destination, "v1.0/users", $"mail eq {GraphQuery.Literal(mail)}", UserSelect, ct);
            if (byMail.Count == 1)
                return Resolved(mapping, MatchKind.SameMail, User(byMail[0]));
        }

        // The same username alone could be a different person (john@ is a common one), so the name must match too.
        var username = upn.Split('@')[0];
        if (!string.IsNullOrEmpty(username))
        {
            var byUsername = await FindAsync(context.Destination, "v1.0/users", $"startswith(userPrincipalName,{GraphQuery.Literal(username + "@")})", UserSelect, ct);
            if (byUsername.Count == 1)
            {
                var candidate = User(byUsername[0]);
                if (string.Equals(candidate.DisplayName.Trim(), mapping.Source.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase))
                    return Resolved(mapping, MatchKind.SameUsername, candidate);
                return new Mapping
                {
                    TargetType = mapping.TargetType,
                    Source = mapping.Source,
                    UsedBy = usedBy,
                    SourceDetails = user,
                    Note = $"{candidate.Detail} in the destination has the same username but a different name ({candidate.DisplayName}). If it's the same person, search for them and pick them.",
                };
            }
        }

        return mapping;

        static ObjectRef User(JsonObject u) => new(Text(u, "id"), Text(u, "displayName"), Text(u, "userPrincipalName"));
    }

    /// <summary>Policies reference apps by app ID, which is the same in every tenant that has the app.</summary>
    static async Task<Mapping> MatchAppAsync(string appId, IReadOnlyList<string> usedBy, Context context, CancellationToken ct)
    {
        var filter = $"appId eq {GraphQuery.Literal(appId)}";
        var inDestination = await FindAsync(context.Destination, "v1.0/servicePrincipals", filter, AppSelect, ct);
        var inSource = inDestination.Count > 0 ? inDestination : await FindAsync(context.Source, "v1.0/servicePrincipals", filter, AppSelect, ct);
        var name = inSource.Count > 0 ? Text(inSource[0], "displayName") : appId;

        var mapping = new Mapping
        {
            TargetType = ResourceRegistry.Application,
            Source = new ObjectRef(appId, name, appId),
            UsedBy = usedBy,
            Note = inDestination.Count > 0 ? null : "This app isn't in the destination tenant. Add it there and match again, or remove it from the policies.",
        };

        return inDestination.Count > 0
            ? Resolved(mapping, MatchKind.SameAppId, new ObjectRef(appId, name, appId))
            : mapping;
    }

    /// <summary>Named locations, authentication strengths, filters and scope tags: Movewise exports these itself.</summary>
    static async Task<Mapping> MatchExportedAsync(string type, string id, IReadOnlyList<string> usedBy, Context context, CancellationToken ct)
    {
        var resourceType = ResourceRegistry.Get(type);
        var exported = context.Exported.FirstOrDefault(e => e.Type.Id == type && e.SourceId == id);
        var mapping = new Mapping
        {
            TargetType = type,
            Source = new ObjectRef(id, exported?.DisplayName ?? id),
            UsedBy = usedBy,
            SourceDetails = exported?.Settings,
            Note = exported is null
                ? "Movewise couldn't read this object in the source."
                : context.Selected.Contains((type, id))
                    ? null
                    : "Not selected on the Discover screen. Create it with the policies, or match it to an existing one.",
        };

        if (exported is not null)
        {
            var list = await context.DestinationLists.GetOrAdd(type, _ => new Lazy<Task<IReadOnlyList<JsonObject>>>(
                () => ResourceReader.ListAsync(context.DestinationClients, resourceType, ct))).Value;
            var sameName = list
                .Where(item => string.Equals(Text(item, resourceType.IdentityProperty), exported.DisplayName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sameName.Count == 1)
                return Resolved(mapping, MatchKind.SameName, new ObjectRef(Text(sameName[0], "id"), Text(sameName[0], resourceType.IdentityProperty)));
        }

        if (context.Selected.Contains((type, id)))
            return Resolved(mapping, MatchKind.CreatedByMigration, new ObjectRef("", mapping.Source.DisplayName));

        return mapping;
    }

    public static Task<IReadOnlyList<JsonObject>> ListDestinationAsync(IGraphReader destination, ResourceType type, CancellationToken ct) =>
        ResourceReader.ListAsync(new TenantClients(destination), type, ct);

    static Mapping Resolved(Mapping mapping, MatchKind kind, ObjectRef destination)
    {
        mapping.Resolve(kind, destination);
        return mapping;
    }

    static Mapping MissingInSource(string type, string id, IReadOnlyList<string> usedBy) => new()
    {
        TargetType = type,
        Source = new ObjectRef(id, id),
        UsedBy = usedBy,
        Note = "This object couldn't be read in the source. It may have been deleted; if so, remove it from the policies.",
    };

    /// <summary>First page of a filtered query; a match needs to be unique, so a handful of results is plenty.</summary>
    static async Task<IReadOnlyList<JsonObject>> FindAsync(IGraphReader graph, string collection, string filter, string select, CancellationToken ct)
    {
        var page = await graph.GetObjectAsync(GraphQuery.Where(collection, filter, select, top: 5), ct);
        return page["value"]?.AsArray().OfType<JsonObject>().ToList() ?? [];
    }

    static async Task<JsonObject?> TryGetAsync(IGraphReader graph, string path, CancellationToken ct)
    {
        try
        {
            return await graph.GetObjectAsync(path, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // ---------- Domains, recipients and sites (also used by pre-flight and search) ----------

    const string RecipientSelect = "id,displayName,mail,userPrincipalName";

    internal static async Task<IReadOnlyList<JsonObject>> ListDomainsAsync(IGraphReader graph, CancellationToken ct)
    {
        try
        {
            return await graph.GetCollectionAsync("v1.0/domains?$select=id,isVerified,isInitial,isDefault", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>The tenant's SharePoint host, such as contoso.sharepoint.com, or null if it can't be read.</summary>
    internal static async Task<string?> SharePointHostAsync(IGraphReader graph, CancellationToken ct)
    {
        try
        {
            var root = await graph.GetObjectAsync("v1.0/sites/root?$select=siteCollection", ct);
            return root["siteCollection"]?["hostname"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>contoso.sharepoint.com → contoso-my.sharepoint.com, where OneDrive accounts live.</summary>
    internal static string OneDriveHost(string sharePointHost)
    {
        var dot = sharePointHost.IndexOf('.');
        return dot < 0 ? sharePointHost : sharePointHost[..dot] + "-my" + sharePointHost[dot..];
    }

    /// <summary>The domain an address is in, or the value itself when it's a whole domain.</summary>
    static string? RecipientDomain(string value) => DomainOf(value) ?? (LooksLikeDomain(value) ? value : null);

    static bool LooksLikeDomain(string value) =>
        value.Contains('.') && !value.Contains('@') && !value.Any(char.IsWhiteSpace) && !value.Contains('/');

    internal static string? DomainOf(string address)
    {
        var at = address.LastIndexOf('@');
        return at > 0 && at < address.Length - 1 ? address[(at + 1)..] : null;
    }

    /// <summary>The one user or group with this address in the tenant, or null.</summary>
    internal static async Task<JsonObject?> FindRecipientAsync(IGraphReader graph, string address, CancellationToken ct)
    {
        var literal = GraphQuery.Literal(address);
        foreach (var filter in new[] { $"mail eq {literal}", $"userPrincipalName eq {literal}" })
        {
            var found = await FindRecipientsAsync(graph, filter, ct);
            if (found.Count == 1)
                return found[0];
        }
        return null;
    }

    /// <summary>Users and groups matching a filter. Groups don't have a username, so that filter only checks users.</summary>
    internal static async Task<IReadOnlyList<JsonObject>> FindRecipientsAsync(IGraphReader graph, string filter, CancellationToken ct)
    {
        var users = await TryFindAsync(graph, "v1.0/users", filter, ct);
        var groups = filter.Contains("userPrincipalName", StringComparison.Ordinal) ? [] : await TryFindAsync(graph, "v1.0/groups", filter, ct);
        return users.Concat(groups).ToList();
    }

    static async Task<IReadOnlyList<JsonObject>> TryFindAsync(IGraphReader graph, string collection, string filter, CancellationToken ct)
    {
        try
        {
            return await FindAsync(graph, collection, filter, RecipientSelect, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>What Exchange and Purview policies use to name a recipient: its email address, or its username.</summary>
    internal static string RecipientAddress(JsonObject recipient) =>
        Text(recipient, "mail") is { Length: > 0 } mail ? mail : Text(recipient, "userPrincipalName");

    /// <summary>The site at this address in the tenant, or null.</summary>
    internal static async Task<JsonObject?> FindSiteAsync(IGraphReader graph, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        try
        {
            return await graph.GetObjectAsync($"v1.0/sites/{uri.Host}{(path.Length > 0 ? ":" + path : "")}?$select=id,webUrl,displayName", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    static bool Is(JsonObject obj, string property, string value) => string.Equals(Text(obj, property), value, StringComparison.OrdinalIgnoreCase);

    static string Text(JsonObject obj, string property) => obj[property]?.GetValue<string>() ?? "";

    static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
