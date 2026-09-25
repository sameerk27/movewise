using System.Text.Json.Nodes;
using Movewise.Core.Tenants;

namespace Movewise.Core.Registry;

public enum M365Service { Entra, Intune, Purview, Defender, Exchange, Teams, SharePoint, DefenderEndpoint }

/// <summary>What removing a referenced object from a policy means.</summary>
public enum RemoveBehavior
{
    /// <summary>Take the value out of its array, or set a single value to null.</summary>
    RemoveValue,

    /// <summary>Drop the whole item of the first array on the path, such as the assignment that targets a group.</summary>
    RemoveContainingItem,

    /// <summary>Set the value to null and leave the rest in place, such as an assignment's filter.</summary>
    ClearValue,

    /// <summary>Set the object holding the value to null, such as a Conditional Access authentication strength.</summary>
    ClearParent,
}

/// <summary>A property whose value points at another object in the tenant, such as a group ID.</summary>
/// <param name="Path">Dotted property path. A segment ending in "[]" is an array.</param>
/// <param name="TargetType">Registry ID (or object kind) of the referenced object.</param>
public sealed record ReferenceRule(string Path, string TargetType)
{
    /// <summary>Special values that look like references but are the same in every tenant, such as "All".</summary>
    public IReadOnlySet<string> Ignore { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public RemoveBehavior OnRemove { get; init; } = RemoveBehavior.RemoveValue;

    /// <summary>A property next to the value to set when it's cleared, such as a filter type back to "none".</summary>
    public (string Property, string Value)? ClearedSibling { get; init; }

    /// <summary>True when the reference narrows who a policy applies to, so removing it widens the policy.</summary>
    /// <summary>
    /// True when the values say who a rule applies to, such as SentTo: take them all out and the condition is dropped,
    /// so the rule applies to all mail. Pre-flight blocks a policy that would lose every value of such a condition.
    /// </summary>
    public bool LimitsScope { get; init; }

    public bool IsExclusion =>
        Path.Contains("exclude", StringComparison.OrdinalIgnoreCase) || Path.Contains("ExceptIf", StringComparison.OrdinalIgnoreCase) || Path.Contains("Exception", StringComparison.Ordinal);
}

/// <summary>Which API reads and writes a policy type.</summary>
public enum Backend
{
    Graph,

    /// <summary>Exchange Online PowerShell: Exchange and Defender for Office 365 policies.</summary>
    ExchangePowerShell,

    /// <summary>Security &amp; Compliance PowerShell: Purview policies.</summary>
    CompliancePowerShell,

    /// <summary>Microsoft Teams PowerShell: Teams policies.</summary>
    TeamsPowerShell,

    /// <summary>The Defender for Endpoint API: same JSON and paging as Graph, with a token of its own.</summary>
    DefenderApi,
}

/// <summary>Cmdlets that list, create and delete one PowerShell policy type.</summary>
/// <param name="GetSwitches">Switches for the Get cmdlet, such as DistributionDetail to include locations.</param>
/// <param name="NameParameter">The New cmdlet parameter that names the policy (Teams cmdlets use Identity), or null when the name comes with the settings (a sensitive information type rule package names itself).</param>
public sealed record PolicyCmdlets(string Get, string New, string Remove, IReadOnlyList<string>? GetSwitches = null, string? NameParameter = "Name")
{
    /// <summary>
    /// A Set cmdlet for the settings New doesn't take (New-RemoteDomain takes only a name and domain). Run with the
    /// new object's identity once it exists, with the settings it accepts that New didn't.
    /// </summary>
    public string? Set { get; init; }

    /// <summary>Parameters passed to Get, New and Remove alike, such as the list an allow/block entry belongs to.</summary>
    public IReadOnlyDictionary<string, string>? Scope { get; init; }

    /// <summary>The Remove cmdlet parameter that takes the object's ID (Remove-TenantAllowBlockListItems takes Ids).</summary>
    public string RemoveIdentityParameter { get; init; } = "Identity";
}

/// <summary>
/// Settings that already exist in every tenant, such as the tenant-wide Safe Links settings or the Default anti-spam
/// policy. They can't be created or deleted, only changed: deploying changes the ones that differ, and rolling back
/// puts back the values the destination had.
/// </summary>
/// <param name="Get">Reads them. Returns the object, or several of which <paramref name="Pick"/> chooses one.</param>
/// <param name="Set">Changes them, given the object's Identity.</param>
/// <param name="Pick">Which of the objects Get returns holds the settings, such as the one marked IsDefault.</param>
/// <param name="Missing">What the admin can do when the destination doesn't have them yet.</param>
public sealed record SettingsCmdlets(string Get, string Set, Func<JsonObject, bool> Pick, string Missing)
{
    /// <summary>Settings in Microsoft Graph: one object, read with GET and changed with PATCH at the same path.</summary>
    public static SettingsCmdlets Graph(string path, string missing) => new(path, path, _ => true, missing);

    public IReadOnlyDictionary<string, string>? GetParameters { get; init; }

    /// <summary>Cmdlets that switch it on and off, where Set can't (preset security policies).</summary>
    public (string Enable, string Disable)? Switch { get; init; }

    /// <summary>False when Set changes the tenant's only object and takes no Identity (Set-PolicyConfig).</summary>
    public bool SetTakesIdentity { get; init; } = true;
}

/// <summary>
/// Items read with an object from their own path and sent once the object exists, such as the settings of an
/// administrative template or the messages of a notification template.
/// </summary>
/// <param name="ReadPath">Graph path template ("{id}") that lists them. They're stored under <paramref name="Property"/>.</param>
/// <param name="Requests">The requests that create them in the destination: a path template ("{id}") and a body for each.</param>
public sealed record ChildItems(string ReadPath, string Property, Func<JsonArray, IReadOnlyList<(string Path, JsonNode Body)>> Requests);

/// <summary>Cmdlets for the rules of a PowerShell policy (who it applies to, its conditions).</summary>
/// <param name="PolicyProperty">The rule property that names its policy, as the Get cmdlet returns it.</param>
/// <param name="PolicyParameter">The New cmdlet parameter that names the policy the rule belongs to.</param>
/// <param name="ListPerPolicy">
/// True when rules are read one policy at a time (Get with PolicyParameter); otherwise every rule is read at once
/// and matched to its policy by <paramref name="PolicyProperty"/>.
/// </param>
public sealed record RuleCmdlets(string Get, string New, string Remove, string PolicyProperty, string PolicyParameter, bool ListPerPolicy = false);

/// <summary>Everything the engine needs to know about one kind of policy.</summary>
/// <param name="ListPath">Graph path, relative to https://graph.microsoft.com/, that lists every item.</param>
/// <param name="IdentityProperty">Property that names an item uniquely within a tenant.</param>
/// <param name="ReadOnlyProperties">Top-level properties the service sets itself; stripped before comparing or creating.</param>
/// <param name="DependsOn">Registry IDs that must be created in the destination before this type.</param>
public sealed record ResourceType(
    string Id,
    M365Service Service,
    string DisplayName,
    string ListPath,
    string IdentityProperty,
    IReadOnlyList<string> ReadOnlyProperties,
    IReadOnlyList<ReferenceRule> References,
    IReadOnlyList<string> DependsOn)
{
    /// <summary>
    /// Path template ("{id}") that returns one item with everything needed to recreate it,
    /// for types whose list leaves details out (such as settings catalog settings).
    /// </summary>
    public string? ItemPath { get; init; }

    /// <summary>Path template ("{id}") that lists the item's assignments. They're stored under "assignments".</summary>
    public string? AssignmentsPath { get; init; }

    /// <summary>Returns false for items that must never be migrated, such as built-in ones.</summary>
    public Func<JsonObject, bool>? Include { get; init; }

    /// <summary>Where new items are posted: the list path without its query.</summary>
    public string CreatePath => ListPath.Split('?')[0];

    /// <summary>
    /// Path template ("{id}") of the action that sets every assignment at once. When <see cref="AssignOneByOne"/>
    /// is set, it's the assignments collection instead, and each assignment is posted on its own.
    /// Assignments are left out of the create request and sent here afterwards.
    /// </summary>
    public string? AssignPath { get; init; }

    public bool AssignOneByOne { get; init; }

    /// <summary>
    /// Path template ("{id}") that sets which apps an app protection policy covers. Apps can't be sent when
    /// creating the policy, so they're left out and sent here afterwards.
    /// </summary>
    public string? TargetAppsPath { get; init; }

    /// <summary>Takes out of a policy what the service won't accept when creating it.</summary>
    public Action<JsonObject>? PrepareForCreate { get; init; }

    /// <summary>Which API reads and writes this type. PowerShell types leave <see cref="ListPath"/> empty.</summary>
    public Backend Backend { get; init; } = Backend.Graph;

    /// <summary>The Get, New and Remove cmdlets, for types managed through PowerShell.</summary>
    public PolicyCmdlets? Cmdlets { get; init; }

    /// <summary>The rules that go with a PowerShell policy. They're stored with it under "rules".</summary>
    public RuleCmdlets? Rules { get; init; }

    /// <summary>
    /// Switches the policy to a mode that can't block, delete or reject anything yet, such as test mode.
    /// Returns what it changed, as shown to the admin. Applied to every policy of this type when it's rewritten.
    /// </summary>
    public Func<JsonObject, IEnumerable<string>>? MakeSafe { get; init; }

    /// <summary>Reshapes an item as read (PowerShell types) into the shape its New cmdlet takes, before it's normalized.</summary>
    public Action<JsonObject>? AfterRead { get; init; }

    /// <summary>
    /// For Teams policies: the policy type as group policy assignments name it (such as TeamsMeetingPolicy).
    /// The policy's group assignments are read with it, stored under "groupAssignments", and made again after it's created.
    /// </summary>
    public string? TeamsPolicyType { get; init; }

    /// <summary>
    /// True when other policies refer to these by name, not by ID (anti-spam policies name their quarantine policy).
    /// Their ID is then their name, in the source and in the destination.
    /// </summary>
    public bool IdIsName { get; init; }

    /// <summary>
    /// True when the New cmdlet needs the priority (audit log retention policies). Otherwise priority is left out,
    /// since a new rule's priority has to fit among the destination's rules.
    /// </summary>
    public bool KeepsPriority { get; init; }

    /// <summary>For settings that exist in every tenant and are changed, not created. See <see cref="SettingsCmdlets"/>.</summary>
    public SettingsCmdlets? Settings { get; init; }

    public bool IsSettings => Settings is not null;

    /// <summary>True for types read and written through a PowerShell session rather than a REST API.</summary>
    public bool UsesPowerShell => Backend is Backend.ExchangePowerShell or Backend.CompliancePowerShell or Backend.TeamsPowerShell;

    /// <summary>
    /// Destination roles (template IDs), any one of which can create or change this type, when that isn't the service's
    /// usual admin role: Entra user settings need Privileged Role Administrator, not Conditional Access Administrator.
    /// </summary>
    public IReadOnlyList<string>? AdminRoles { get; init; }

    /// <summary>The property the assign action takes the assignments in (scripts use deviceManagementScriptAssignments).</summary>
    public string AssignBodyProperty { get; init; } = "assignments";

    /// <summary>Items that belong to the object but are read and sent separately, such as an administrative template's settings.</summary>
    public ChildItems? Children { get; init; }

    /// <summary>
    /// Objects policies point at that Movewise matches but never creates or lists for migrating, such as Intune apps
    /// (their content can't be copied). Search on the Map screen still finds them.
    /// </summary>
    public bool ReferenceOnly { get; init; }

    /// <summary>The property that identifies a Graph object, as read and once created, when that isn't "id" (cross-tenant partners use tenantId).</summary>
    public string IdProperty { get; init; } = "id";

    /// <summary>Why one item can't be created in this destination, or null: a cross-tenant partner that is the destination itself.</summary>
    public Func<JsonObject, TenantInfo, string?>? BlockWhen { get; init; }

    public string PluralName => DisplayName.EndsWith("policy", StringComparison.Ordinal)
        ? DisplayName[..^1] + "ies"
        : DisplayName.EndsWith("settings", StringComparison.Ordinal) ? DisplayName
        : DisplayName + "s";
}
