using System.Text.Json.Nodes;
using Movewise.Core.Registry;

namespace Movewise.Core.Mapping;

/// <summary>How a source object was matched to the destination.</summary>
public enum MatchKind
{
    Unresolved,

    // Found automatically
    SameName,
    SameMailNickname,
    SameMail,
    SameUsername,
    SameAppId,
    CreatedByMigration,

    /// <summary>Outside the source tenant (a partner's domain or address), so it's kept as it is.</summary>
    External,

    /// <summary>The source's .onmicrosoft.com domain, matched to the destination's.</summary>
    InitialDomain,

    /// <summary>A SharePoint or OneDrive site found at the same path in the destination.</summary>
    SamePath,

    // Chosen by the admin
    Manual,
    CreateInDestination,
    RemoveFromPolicies,
}

/// <summary>An object in either tenant, as shown to the admin.</summary>
/// <param name="Detail">A second line to tell look-alikes apart, such as a mail nickname or username.</param>
public sealed record ObjectRef(string Id, string DisplayName, string? Detail = null);

/// <summary>One source object that selected policies point at, and what it becomes in the destination.</summary>
public sealed class Mapping
{
    public required string TargetType { get; init; }
    public required ObjectRef Source { get; init; }
    public required IReadOnlyList<string> UsedBy { get; init; }

    /// <summary>The source object as Graph returned it, kept so it can be recreated in the destination.</summary>
    public JsonObject? SourceDetails { get; init; }

    /// <summary>Why it couldn't be matched automatically, or anything else the admin should know.</summary>
    public string? Note { get; init; }

    public MatchKind Kind { get; private set; }
    public ObjectRef? Destination { get; private set; }

    public string Key => MappingPlan.KeyOf(TargetType, Source.Id);
    public bool IsResolved => Kind != MatchKind.Unresolved;
    public bool IsDecision => Kind is MatchKind.Manual or MatchKind.CreateInDestination or MatchKind.RemoveFromPolicies;

    /// <summary>
    /// Groups, and policy objects Movewise exports (locations, strengths, filters, scope tags), can be created
    /// from the source's settings. Users and apps can't.
    /// </summary>
    public bool CanCreate =>
        SourceDetails is not null
        && TargetType is not (ResourceRegistry.User or ResourceRegistry.Application or ResourceRegistry.Domain or ResourceRegistry.Recipient or ResourceRegistry.Site);

    public void Resolve(MatchKind kind, ObjectRef? destination)
    {
        Kind = kind;
        Destination = destination;
    }

    public void MapTo(ObjectRef destination) => Resolve(MatchKind.Manual, destination);

    public void CreateInDestination()
    {
        if (!CanCreate)
            throw new InvalidOperationException($"{MappingNames.Singular(TargetType)} objects can't be created by Movewise.");
        Resolve(MatchKind.CreateInDestination, new ObjectRef("", Source.DisplayName, Source.Detail));
    }

    public void RemoveFromPolicies() => Resolve(MatchKind.RemoveFromPolicies, null);

    public void Clear() => Resolve(MatchKind.Unresolved, null);

    /// <summary>A copy that keeps this mapping's decision, for a re-run where the using policies changed.</summary>
    public Mapping WithUsedBy(IReadOnlyList<string> usedBy)
    {
        var copy = new Mapping { TargetType = TargetType, Source = Source, UsedBy = usedBy, SourceDetails = SourceDetails, Note = Note };
        copy.Resolve(Kind, Destination);
        return copy;
    }
}

/// <summary>Every mapping for one migration.</summary>
public sealed class MappingPlan(IEnumerable<Mapping> mappings)
{
    // Case doesn't matter: IDs are GUIDs, and domains and addresses ignore case.
    readonly Dictionary<string, Mapping> _items = mappings.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<Mapping> Items => _items.Values;

    public int UnresolvedCount => _items.Values.Count(m => !m.IsResolved);

    public Mapping? Find(string targetType, string sourceId) =>
        _items.TryGetValue(KeyOf(targetType, sourceId), out var mapping) ? mapping : null;

    public IEnumerable<Mapping> OfType(string targetType) =>
        _items.Values.Where(m => m.TargetType == targetType).OrderBy(m => m.Source.DisplayName, StringComparer.OrdinalIgnoreCase);

    public static string KeyOf(string targetType, string sourceId) => $"{targetType}:{sourceId}";
}

/// <summary>Names of the object kinds shown on the Map screen, in the order they're shown.</summary>
public static class MappingNames
{
    public static IReadOnlyList<string> Order { get; } =
    [
        ResourceRegistry.Group,
        ResourceRegistry.User,
        ResourceRegistry.Application,
        ResourceRegistry.NamedLocation,
        ResourceRegistry.AuthenticationStrength,
        ResourceRegistry.AssignmentFilter,
        ResourceRegistry.ScopeTag,
        ResourceRegistry.SensitivityLabel,
        ResourceRegistry.QuarantinePolicy,
        ResourceRegistry.MailboxRetentionTag,
        ResourceRegistry.IntuneApp,
        ResourceRegistry.Domain,
        ResourceRegistry.Recipient,
        ResourceRegistry.Site,
    ];

    public static string Plural(string targetType) => targetType switch
    {
        ResourceRegistry.Group => "Groups",
        ResourceRegistry.User => "Users",
        ResourceRegistry.Application => "Apps",
        ResourceRegistry.NamedLocation => "Named locations",
        ResourceRegistry.AuthenticationStrength => "Authentication strengths",
        ResourceRegistry.AssignmentFilter => "Assignment filters",
        ResourceRegistry.ScopeTag => "Scope tags",
        ResourceRegistry.SensitivityLabel => "Sensitivity labels",
        ResourceRegistry.Domain => "Domains",
        ResourceRegistry.Recipient => "Mailboxes and groups",
        ResourceRegistry.Site => "Sites",
        _ => ResourceRegistry.All.FirstOrDefault(t => t.Id == targetType)?.PluralName ?? targetType,
    };

    public static string Singular(string targetType) => targetType switch
    {
        ResourceRegistry.Group => "Group",
        ResourceRegistry.User => "User",
        ResourceRegistry.Application => "App",
        ResourceRegistry.NamedLocation => "Named location",
        ResourceRegistry.AuthenticationStrength => "Authentication strength",
        ResourceRegistry.AssignmentFilter => "Assignment filter",
        ResourceRegistry.ScopeTag => "Scope tag",
        ResourceRegistry.SensitivityLabel => "Sensitivity label",
        ResourceRegistry.Domain => "Domain",
        ResourceRegistry.Recipient => "Mailbox or group",
        ResourceRegistry.Site => "Site",
        _ => ResourceRegistry.All.FirstOrDefault(t => t.Id == targetType)?.DisplayName ?? targetType,
    };

    public static string Describe(MatchKind kind) => kind switch
    {
        MatchKind.SameName => "Auto · same name",
        MatchKind.SameMailNickname => "Auto · mail nickname",
        MatchKind.SameMail => "Auto · same email",
        MatchKind.SameUsername => "Auto · same username",
        MatchKind.SameAppId => "Same app",
        MatchKind.CreatedByMigration => "Migrated with policies",
        MatchKind.External => "External · kept",
        MatchKind.InitialDomain => "Auto · .onmicrosoft.com",
        MatchKind.SamePath => "Auto · same path",
        MatchKind.Manual => "Manual",
        MatchKind.CreateInDestination => "Create in destination",
        MatchKind.RemoveFromPolicies => "Remove from policies",
        _ => "Unresolved",
    };
}
