using System.Text.Json.Nodes;
using Movewise.Core.Deploy;
using Movewise.Core.Registry;

namespace Movewise.Core.Export;

/// <summary>
/// A connected Exchange Online or Security &amp; Compliance PowerShell session. Implemented by the hosted
/// PowerShell runspace; faked in tests. The source's session refuses anything but Get cmdlets.
/// </summary>
public interface IPowerShell
{
    /// <summary>Runs one cmdlet and returns the objects it wrote, as JSON.</summary>
    Task<IReadOnlyList<JsonObject>> InvokeAsync(string cmdlet, IReadOnlyDictionary<string, JsonNode?>? parameters = null, CancellationToken ct = default);

    /// <summary>The parameters a cmdlet accepts, ignoring case.</summary>
    Task<IReadOnlySet<string>> ParametersOfAsync(string cmdlet, CancellationToken ct = default);
}

/// <summary>An error a cmdlet reported.</summary>
public sealed class PowerShellException(string message) : Exception(message)
{
    public bool IsNotFound =>
        Message.Contains("couldn't be found", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("could not be found", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("ManagementObjectNotFound", StringComparison.OrdinalIgnoreCase);

    /// <summary>The cmdlet doesn't exist in this session: the service isn't licensed, or the account has no role for it.</summary>
    public bool IsMissingCommand =>
        Message.Contains("is not recognized as a name of a cmdlet", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("is not recognized as the name of a cmdlet", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("cmdlet isn't available", StringComparison.OrdinalIgnoreCase);

    public bool IsAlreadyExists =>
        Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The request may have reached Microsoft 365 even though it failed: a timeout, a dropped connection, or a server
    /// error. A cmdlet that was refused (bad parameter, already exists, no permission) isn't uncertain.
    /// </summary>
    public bool IsUncertain => UncertainSigns.Any(sign => Message.Contains(sign, StringComparison.OrdinalIgnoreCase));

    /// <summary>The session's access token was refused or has run out; connecting again with a fresh one should help.</summary>
    public bool IsAuthFailure => AuthSigns.Any(sign => Message.Contains(sign, StringComparison.OrdinalIgnoreCase));

    static readonly string[] UncertainSigns =
    [
        "timed out", "timeout", "underlying connection was closed", "connection was forcibly closed", "error occurred while sending the request",
        "Internal Server Error", "Bad Gateway", "Service Unavailable", "Gateway Timeout", "(500)", "(502)", "(503)", "(504)", "server is busy",
    ];

    static readonly string[] AuthSigns =
    [
        "UnAuthorized", "(401)", "401 Unauthorized", "token has expired", "token is expired", "token expired", "AADSTS700082", "IDX10223", "lifetime validation failed",
    ];
}

/// <summary>Everything Movewise can use to reach one tenant.</summary>
/// <param name="Exchange">Exchange Online PowerShell, for Exchange and Defender policies. Null when not available.</param>
/// <param name="Compliance">Security &amp; Compliance PowerShell, for Purview policies. Null when not available.</param>
/// <param name="Teams">Microsoft Teams PowerShell, for Teams policies. Null when not available.</param>
/// <param name="DefenderApi">The Defender for Endpoint API, for indicators. Null when not available.</param>
public sealed record TenantClients(IGraphReader Graph, IPowerShell? Exchange = null, IPowerShell? Compliance = null, IPowerShell? Teams = null, IGraphReader? DefenderApi = null)
{
    /// <summary>The REST API a type is read from: Graph, or the Defender for Endpoint API.</summary>
    public IGraphReader ApiFor(ResourceType? type) => type?.Backend == Backend.DefenderApi
        ? DefenderApi ?? throw new InvalidOperationException("The Defender for Endpoint API isn't connected.")
        : Graph;

    /// <summary>The REST API a type is written to. Only the destination has write access.</summary>
    public IGraphWriter WriterFor(ResourceType? type) =>
        ApiFor(type) as IGraphWriter ?? throw new InvalidOperationException("This tenant is read-only: Movewise never writes to the source.");

    public IPowerShell PowerShellFor(ResourceType type) => type.Backend switch
    {
        Backend.ExchangePowerShell => Exchange ?? throw new InvalidOperationException("Exchange Online PowerShell isn't connected."),
        Backend.CompliancePowerShell => Compliance ?? throw new InvalidOperationException("Security & Compliance PowerShell isn't connected."),
        Backend.TeamsPowerShell => Teams ?? throw new InvalidOperationException("Microsoft Teams PowerShell isn't connected."),
        _ => throw new InvalidOperationException($"{type.PluralName} are managed through Microsoft Graph, not PowerShell."),
    };

    /// <summary>Graph with write access. Only the destination has it; the source's Graph is a <see cref="ReadOnlyGraph"/>.</summary>
    public IGraphWriter GraphWriter => Graph as IGraphWriter ?? throw new InvalidOperationException("This tenant is read-only: Movewise never writes to the source.");
}

/// <summary>Lists the policies of one type, whichever API holds them.</summary>
public static class ResourceReader
{
    /// <summary>
    /// Every item of the type, without built-in ones. Graph items are as the list returns them; PowerShell
    /// items are cleaned up (see <see cref="PowerShellShape"/>) and get an "id".
    /// </summary>
    public static async Task<IReadOnlyList<JsonObject>> ListAsync(TenantClients tenant, ResourceType type, CancellationToken ct = default)
    {
        IEnumerable<JsonObject> items;
        if (type is { Backend: Backend.Graph, Settings: { } graphSettings })
        {
            items = [await tenant.Graph.GetObjectAsync(graphSettings.Get, ct)];
        }
        else if (!type.UsesPowerShell)
        {
            items = await tenant.ApiFor(type).GetCollectionAsync(type.ListPath, ct);
        }
        else if (type.Settings is { } settings)
        {
            var parameters = settings.GetParameters?.ToDictionary(p => p.Key, p => (JsonNode?)JsonValue.Create(p.Value));
            items = (await tenant.PowerShellFor(type).InvokeAsync(settings.Get, parameters, ct)).Select(PowerShellShape.Clean).Where(settings.Pick).Take(1);
        }
        else
        {
            var cmdlets = type.Cmdlets!;
            var parameters = cmdlets.GetSwitches?.ToDictionary(s => s, _ => (JsonNode?)JsonValue.Create(true)) ?? [];
            foreach (var (name, value) in cmdlets.Scope ?? new Dictionary<string, string>())
                parameters[name] = value;
            items = (await tenant.PowerShellFor(type).InvokeAsync(cmdlets.Get, parameters.Count > 0 ? parameters : null, ct)).Select(PowerShellShape.Clean);
            if (type.IdIsName)
                items = items.Select(item => { item["id"] = item[type.IdentityProperty]?.DeepClone(); return item; });
        }
        return items.Where(item => type.Include?.Invoke(item) ?? true).ToList();
    }

    /// <summary>The rules of each policy, by policy name.</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<JsonObject>>> RulesAsync(
        TenantClients tenant, ResourceType type, IEnumerable<string> policyNames, CancellationToken ct = default)
    {
        var rules = type.Rules ?? throw new InvalidOperationException($"{type.PluralName} have no rules.");
        var shell = tenant.PowerShellFor(type);
        var result = new Dictionary<string, IReadOnlyList<JsonObject>>(StringComparer.OrdinalIgnoreCase);

        if (rules.ListPerPolicy)
        {
            foreach (var name in policyNames)
            {
                var found = await shell.InvokeAsync(rules.Get, new Dictionary<string, JsonNode?> { [rules.PolicyParameter] = name }, ct);
                result[name] = found.Select(PowerShellShape.Clean).ToList();
            }
            return result;
        }

        var all = (await shell.InvokeAsync(rules.Get, null, ct)).Select(PowerShellShape.Clean).ToList();
        foreach (var name in policyNames)
        {
            result[name] = all
                .Where(r => string.Equals(r[rules.PolicyProperty]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        return result;
    }
}

/// <summary>Puts what PowerShell returns into the same shape the New cmdlets take.</summary>
public static class PowerShellShape
{
    public static JsonObject Clean(JsonObject item)
    {
        var result = (JsonObject)item.DeepClone();

        // Get returns State: "Enabled"/"Disabled"; New takes Enabled: true/false.
        if (result["State"] is JsonValue state && state.TryGetValue<string>(out var text) && !result.ContainsKey("Enabled"))
        {
            result["Enabled"] = string.Equals(text, "Enabled", StringComparison.OrdinalIgnoreCase);
            result.Remove("State");
        }

        // Locations and recipients come back as objects; New takes their names.
        foreach (var name in result.Select(p => p.Key).ToList())
        {
            if (result[name] is JsonArray array && array.Count > 0 && array.All(e => e is JsonObject o && o["Name"] is JsonValue))
                result[name] = new JsonArray(array.Select(e => (JsonNode?)JsonValue.Create(e!["Name"]!.GetValue<string>())).ToArray());
        }

        // Teams policies are named by Identity ("Tag:Execs"); the part after "Tag:" is the name.
        if (Text(result, "Identity") is { } identity && identity.StartsWith("Tag:", StringComparison.OrdinalIgnoreCase) && !result.ContainsKey("Name"))
            result["Name"] = identity[4..];

        // Every item needs an ID: the GUID where there is one, otherwise the name.
        var id = Text(result, "Guid") ?? Text(result, "ExchangeObjectId") ?? Text(result, "Identity") ?? Text(result, "Name");
        if (id is not null)
            result["id"] = id;
        return result;
    }

    /// <summary>
    /// The parameters to pass to a New cmdlet: settings it accepts, without empty values. Settings it accepts
    /// but whose values are objects can't be passed through, and are returned so the admin can set them by hand.
    /// </summary>
    /// <param name="keepPriority">True for types that must be created with a priority (audit log retention policies).</param>
    public static (Dictionary<string, JsonNode?> Parameters, List<string> NotCopied) ToParameters(JsonObject settings, IReadOnlySet<string> accepted, bool keepPriority = false)
    {
        var parameters = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        var notCopied = new List<string>();
        foreach (var (name, value) in settings)
        {
            if (!accepted.Contains(name) || Skipped.Contains(name) && !(keepPriority && name.Equals("Priority", StringComparison.OrdinalIgnoreCase)))
                continue;
            switch (value)
            {
                case null:
                case JsonValue v when v.TryGetValue<string>(out var s) && s.Length == 0:
                case JsonArray { Count: 0 }:
                case JsonObject { Count: 0 }:
                    break;
                case JsonObject or JsonArray when !IsScalarList(value) && !HashtableParameters.Contains(name):
                    notCopied.Add(name);
                    break;
                default:
                    parameters[name] = value.DeepClone();
                    break;
            }
        }
        return (parameters, notCopied);
    }

    /// <summary>Parameters that take hashtables, which the JSON objects are passed as (DLP sensitive info type conditions, Endpoint DLP settings).</summary>
    static readonly HashSet<string> HashtableParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "ContentContainsSensitiveInformation", "ExceptIfContentContainsSensitiveInformation", "AdvancedSettings",
        "EndpointDlpGlobalSettings", "EndpointDlpBrowserRestrictions",
    };

    static bool IsScalarList(JsonNode node) => node is JsonArray array && array.All(e => e is JsonValue);

    // Priority is left out on purpose: a new rule's priority must be within the destination's existing rules.
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "Priority", "Confirm", "WhatIf", "Force" };

    static string? Text(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
}
