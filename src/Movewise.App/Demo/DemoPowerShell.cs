using System.Text.Json.Nodes;
using Movewise.Core.Export;

namespace Movewise.App.Demo;

/// <summary>
/// An in-memory Exchange Online, Security &amp; Compliance or Teams PowerShell for the demo tenants. Get, New and Remove
/// cmdlets work on the same list per noun (New-AntiPhishRule adds to what Get-AntiPhishRule returns). Like the real
/// source session, the demo source refuses anything but Get cmdlets.
/// </summary>
sealed class DemoPowerShell(bool readOnly) : IPowerShell
{
    readonly Dictionary<string, List<JsonObject>> _objects = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();

    public DemoPowerShell Add(string noun, params JsonObject[] items)
    {
        if (!_objects.TryGetValue(noun, out var list))
            _objects[noun] = list = [];
        list.AddRange(items);
        return this;
    }

    public async Task<IReadOnlyList<JsonObject>> InvokeAsync(string cmdlet, IReadOnlyDictionary<string, JsonNode?>? parameters = null, CancellationToken ct = default)
    {
        await Task.Delay(40, ct);
        var dash = cmdlet.IndexOf('-');
        var verb = cmdlet[..dash];
        var noun = cmdlet[(dash + 1)..];
        parameters ??= new Dictionary<string, JsonNode?>();
        if (readOnly && !verb.Equals("Get", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Movewise only reads from the source tenant; {cmdlet} isn't allowed there.");

        lock (_gate)
        {
            if (!_objects.TryGetValue(noun, out var list))
                _objects[noun] = list = [];

            switch (verb.ToLowerInvariant())
            {
                case "get":
                    return list.Where(item => parameters.All(p => Matches(item, p.Key, p.Value))).Select(i => (JsonObject)i.DeepClone()).ToList();

                case "new":
                    var created = new JsonObject();
                    foreach (var (name, value) in parameters)
                        created[name] = value?.DeepClone();
                    Complete(noun, created);
                    if (list.Any(i => SameName(i, created)))
                        throw new PowerShellException($"{Text(created, "Name")} already exists in the demo tenant.");
                    list.Add(created);
                    return [(JsonObject)created.DeepClone()];

                case "remove":
                    var removed = noun.Equals("CsGroupPolicyAssignment", StringComparison.OrdinalIgnoreCase)
                        ? list.RemoveAll(i => Is(i, "GroupId", parameters.GetValueOrDefault("GroupId")) && Is(i, "PolicyType", parameters.GetValueOrDefault("PolicyType")))
                        : list.RemoveAll(i => IsIdentity(i, IdentityOf(parameters)));
                    if (removed == 0)
                        throw new PowerShellException($"The operation couldn't be performed because object '{IdentityOf(parameters)}' couldn't be found.");
                    return [];

                case "set":
                    var target = list.FirstOrDefault(i => IsIdentity(i, IdentityOf(parameters)))
                        ?? throw new PowerShellException($"The operation couldn't be performed because object '{IdentityOf(parameters)}' couldn't be found.");
                    foreach (var (name, value) in parameters.Where(p => !p.Key.Equals("Identity", StringComparison.OrdinalIgnoreCase)))
                        target[name] = value?.DeepClone();
                    return [];

                case "enable" or "disable":
                    var rule = list.FirstOrDefault(i => IsIdentity(i, IdentityOf(parameters)))
                        ?? throw new PowerShellException($"The operation couldn't be performed because object '{IdentityOf(parameters)}' couldn't be found.");
                    rule["State"] = verb.Equals("enable", StringComparison.OrdinalIgnoreCase) ? "Enabled" : "Disabled";
                    return [];

                default:
                    throw new PowerShellException($"The demo tenant doesn't support {cmdlet}.");
            }
        }
    }

    public Task<IReadOnlySet<string>> ParametersOfAsync(string cmdlet, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(new AnyParameter());

    // Remove-TenantAllowBlockListItems takes Ids, a list; the others take Identity.
    static string? IdentityOf(IReadOnlyDictionary<string, JsonNode?> parameters) =>
        parameters.GetValueOrDefault("Identity")?.ToString()
        ?? (parameters.GetValueOrDefault("Ids") is JsonArray ids ? ids.FirstOrDefault()?.ToString() : parameters.GetValueOrDefault("Ids")?.ToString());

    /// <summary>Fills in what the service would: an ID, the name forms, a rule's link to its policy.</summary>
    static void Complete(string noun, JsonObject created)
    {
        created["Guid"] = Guid.NewGuid().ToString();
        // An allow/block entry is named by its value, which New takes as Entries.
        if (noun.Equals("TenantAllowBlockListItems", StringComparison.OrdinalIgnoreCase) && created["Entries"] is { } entries)
        {
            created["Value"] = entries is JsonArray list ? list.FirstOrDefault()?.ToString() : entries.ToString();
            created["Action"] = created["Allow"] is not null ? "Allow" : "Block";
            created["Identity"] = created["Guid"]!.DeepClone();
        }
        if (noun.StartsWith("Cs", StringComparison.OrdinalIgnoreCase) && Text(created, "Identity") is { Length: > 0 } identity)
        {
            created["Name"] = identity;
            created["Identity"] = "Tag:" + identity;
        }
        else if (Text(created, "Name") is { Length: > 0 } name)
        {
            created["Identity"] = name;
        }
        if (noun.EndsWith("ComplianceRule", StringComparison.OrdinalIgnoreCase) && Text(created, "Policy") is { Length: > 0 } policy)
            created["ParentPolicyName"] = policy;
    }

    /// <summary>Get filters: Identity, a rule's policy, a Teams policy type. Switches such as DistributionDetail are ignored.</summary>
    static bool Matches(JsonObject item, string name, JsonNode? value)
    {
        if (value is JsonValue v && v.TryGetValue<bool>(out _))
            return true;
        return name.ToLowerInvariant() switch
        {
            "identity" => IsIdentity(item, value?.ToString()),
            "policy" => Is(item, "ParentPolicyName", value) || Is(item, "Policy", value),
            _ => Is(item, name, value),
        };
    }

    static bool IsIdentity(JsonObject item, string? identity) =>
        identity is not null && new[] { "Guid", "Name", "Identity" }.Any(p => string.Equals(Text(item, p), identity, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Text(item, p), "Tag:" + identity, StringComparison.OrdinalIgnoreCase));

    static bool SameName(JsonObject a, JsonObject b) =>
        Text(b, "Name").Length > 0 && string.Equals(Text(a, "Name"), Text(b, "Name"), StringComparison.OrdinalIgnoreCase)
        || Text(b, "GroupId").Length > 0 && Is(a, "GroupId", b["GroupId"]) && Is(a, "PolicyType", b["PolicyType"]);

    static bool Is(JsonObject item, string name, JsonNode? value) =>
        string.Equals(Text(item, name), value?.ToString(), StringComparison.OrdinalIgnoreCase);

    static string Text(JsonObject item, string name) =>
        item[name] is JsonValue value ? value.ToString() : "";

    /// <summary>The demo cmdlets take any parameter.</summary>
    sealed class AnyParameter : HashSet<string>, IReadOnlySet<string>
    {
        bool IReadOnlySet<string>.Contains(string item) => true;
    }
}
