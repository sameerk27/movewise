using System.Text.Json.Nodes;
using Movewise.Core.Access;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Preflight;

/// <summary>What to do with an app the destination already has.</summary>
public enum ExistingAppChoice
{
    Skip,

    /// <summary>Change its settings to match, and add the owners and assignments it lacks. Nothing is taken away.</summary>
    Update,
}

public sealed record AppPreflightChoices
{
    public ExistingAppChoice ExistingApps { get; init; } = ExistingAppChoice.Skip;

    /// <summary>Deploy the apps that can be migrated and leave the blocked ones out.</summary>
    public bool SkipBlocked { get; init; }
}

/// <summary>One app as pre-flight found it, and what deploying would do with it.</summary>
/// <param name="Desired">The app as it would be in the destination: its settings, with the destination's users and groups.</param>
public sealed record AppResult(ExportedResource Source, JsonObject Desired, Outcome Outcome, IReadOnlyList<string> Reasons)
{
    /// <summary>The destination's service principal for the same app, when it already has one.</summary>
    public string? ExistingId { get; init; }

    /// <summary>For an app registration migrated before: its app ID in the destination (see the app ID map).</summary>
    public string? DestinationAppId { get; init; }

    /// <summary>For an app updated in place: the settings whose value differs, and so will be changed.</summary>
    public IReadOnlyList<string> ChangedSettings { get; init; } = [];

    /// <summary>Assignments to make: each a principalId, principalType and appRoleId. For an existing app, only those it lacks.</summary>
    public JsonArray Assignments { get; init; } = [];

    /// <summary>Users to add as owners. For an existing app, only those who aren't already.</summary>
    public JsonArray Owners { get; init; } = [];

    /// <summary>Changes made to fit the destination, such as an assigned group left out.</summary>
    public IReadOnlyList<PolicyChange> Changes { get; init; } = [];

    public string Key => MappingPlan.KeyOf(Source.Type.Id, Source.SourceId);
    public string AppId => Source.Settings["appId"]?.GetValue<string>() ?? "";
    public bool IsUpdate => ExistingId is not null && Outcome == Outcome.Create;
    /// <summary>True for the source's own apps (registrations and gallery apps): recreated with a new app ID.</summary>
    public bool IsOwnApp => AppPreflight.IsOwn(Source.Type);

    /// <summary>True when the app signs users in with SAML, which is set up again with a new signing certificate.</summary>
    public bool UsesSaml => Source.Settings["saml"] is JsonObject;
}

public sealed record AppPreflightReport(IReadOnlyList<AppResult> Apps, IReadOnlyList<Finding> Findings)
{
    public int CreateCount => Apps.Count(a => a.Outcome == Outcome.Create && !a.IsUpdate);
    public int UpdateCount => Apps.Count(a => a.IsUpdate);
    public int SkipCount => Apps.Count(a => a.Outcome == Outcome.Skip);
    public int BlockedCount => Apps.Count(a => a.Outcome == Outcome.Blocked);
    public int AssignmentCount => Apps.Where(a => a.Outcome == Outcome.Create).Sum(a => a.Assignments.Count);
    public bool HasBlockers => Findings.Any(f => f.Severity == Severity.Blocker);

    /// <summary>The admin chose to deploy the rest and leave the blocked apps out.</summary>
    public bool BlockedSkipped { get; init; }

    public bool CanDeploy => (!HasBlockers || BlockedSkipped) && CreateCount + UpdateCount > 0;
}

/// <summary>
/// A dry run of an app migration against the destination: which apps would be created, updated or skipped, and what
/// the admin must know first. Reads the destination only; nothing is changed, and no consent is ever granted.
/// </summary>
public static class AppPreflight
{
    /// <summary>The service principal's own settings that are copied. Everything else comes with the app itself.</summary>
    public static IReadOnlyList<string> CopiedSettings { get; } =
    [
        "accountEnabled", "appRoleAssignmentRequired", "description", "homepage", "loginUrl", "logoutUrl", "notes",
        "notificationEmailAddresses", "tags",
    ];

    /// <summary>
    /// An app registration's own settings that are copied when it's recreated. Its app ID and application ID URI are
    /// new, and its secrets, certificates and pre-authorized apps can't be copied.
    /// </summary>
    public static IReadOnlyList<string> CopiedRegistrationSettings { get; } =
    [
        "displayName", "description", "signInAudience", "web", "spa", "publicClient", "isFallbackPublicClient", "appRoles",
        "optionalClaims", "api", "requiredResourceAccess", "groupMembershipClaims", "info", "notes", "tags",
    ];

    /// <summary>A gallery app's settings that are set once it's made from its template: what it was set up with, not what the template gives.</summary>
    public static IReadOnlyList<string> CopiedGallerySettings { get; } =
    [
        "identifierUris", "web", "appRoles", "optionalClaims", "groupMembershipClaims", "notes",
    ];

    /// <summary>The source's own apps: app registrations, and apps made from a gallery template.</summary>
    public static bool IsOwn(ResourceType type) => type.Id is ResourceRegistry.AppRegistration or ResourceRegistry.GalleryApp;

    const string Select = "id,appId,displayName,accountEnabled,appRoleAssignmentRequired,description,homepage,loginUrl,logoutUrl,notes,notificationEmailAddresses,tags";

    public static async Task<AppPreflightReport> RunAsync(
        TenantClients destination,
        TenantInfo destinationInfo,
        IReadOnlyList<ExportedResource> apps,
        MappingPlan plan,
        AppPreflightChoices choices,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? migratedAppIds = null)
    {
        var graph = destination.Graph;
        var results = new List<AppResult>();
        var findings = new List<Finding>();
        var destinationName = destinationInfo.DisplayName;

        var type = ResourceRegistry.Get(ResourceRegistry.EnterpriseApp);
        if (RoleRequirements.MissingFor(type, destinationInfo.RoleTemplateIds) is { } role)
        {
            findings.Add(new Finding(Severity.Blocker,
                $"Missing admin role: {role}",
                $"{destinationInfo.UserPrincipalName} can't create or change apps in {destinationName}. Assign {role} (or Global Administrator), then sign in again.",
                []));
        }

        var sameName = new List<string>();
        var done = 0;
        foreach (var app in apps)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Checking {++done} of {apps.Count} apps in {destinationName}…");

            var transformed = Transformer.Transform(app, plan, new TransformOptions { ConditionalAccessReportOnly = false, MakeSafe = false });
            var desired = transformed.Desired;
            var appId = app.Settings["appId"]?.GetValue<string>() ?? "";

            // A registration is recreated with a new app ID, so it's only "already there" when Movewise migrated it before.
            var isRegistration = IsOwn(app.Type);
            var destinationAppId = isRegistration ? migratedAppIds?.GetValueOrDefault(appId) : appId;
            var existing = destinationAppId is null ? null : await FindAsync(graph, destinationAppId, ct);
            var assignments = Assignments(desired);
            var owners = Strings(desired["owners"]);
            var already = isRegistration ? $"Migrated before: it's in {destinationName} with app ID {destinationAppId}." : $"Already in {destinationName}.";

            if (existing is not null && choices.ExistingApps == ExistingAppChoice.Skip)
            {
                results.Add(new AppResult(app, desired, Outcome.Skip, [$"{already} Skipped, as chosen."]) { ExistingId = Text(existing, "id"), DestinationAppId = destinationAppId });
                continue;
            }
            if (transformed.Problems.Count > 0)
            {
                results.Add(new AppResult(app, desired, Outcome.Blocked, transformed.Problems) { ExistingId = existing is null ? null : Text(existing, "id"), Changes = transformed.Changes });
                continue;
            }
            if (existing is null && app.Settings["template"]?["missing"]?.GetValue<bool>() == true)
            {
                results.Add(new AppResult(app, desired, Outcome.Skip, ["Its gallery template can't be found, so it can't be made from it. Add it from the gallery by hand."]));
                continue;
            }
            if (existing is null)
            {
                if (isRegistration && await NameTakenAsync(graph, app.DisplayName, ct))
                    sameName.Add(app.DisplayName);
                results.Add(new AppResult(app, desired, Outcome.Create, isRegistration ? ["Created with a new app ID."] : [])
                {
                    Assignments = assignments,
                    Owners = new JsonArray(owners.Select(o => (JsonNode?)o).ToArray()),
                    Changes = transformed.Changes,
                });
                continue;
            }

            // Already there, and to be updated: only what differs is changed, and only missing owners and assignments added.
            var id = Text(existing, "id");
            var desiredSettings = isRegistration ? desired["servicePrincipal"] as JsonObject ?? [] : desired;
            var changed = CopiedSettings
                .Where(name => desiredSettings.ContainsKey(name) && !SettingsDiff.Same(desiredSettings[name], existing[name]))
                .ToList();
            var path = $"v1.0/servicePrincipals/{Uri.EscapeDataString(id)}";
            // A registration's owners are the registration's, not its service principal's.
            var ownersPath = isRegistration && await RegistrationIdAsync(graph, destinationAppId!, ct) is { } registrationId
                ? $"v1.0/applications/{Uri.EscapeDataString(registrationId)}"
                : path;
            var present = (await graph.GetCollectionAsync($"{path}/appRoleAssignedTo", ct))
                .Select(a => (Text(a, "principalId"), Text(a, "appRoleId")))
                .ToHashSet();
            var missingAssignments = new JsonArray(assignments.OfType<JsonObject>()
                .Where(a => !present.Contains((Text(a, "principalId"), Text(a, "appRoleId"))))
                .Select(a => (JsonNode?)a.DeepClone())
                .ToArray());
            var presentOwners = (await graph.GetCollectionAsync($"{ownersPath}/owners?$select=id", ct)).Select(o => Text(o, "id")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingOwners = owners.Where(o => !presentOwners.Contains(o)).ToList();

            if (changed.Count == 0 && missingAssignments.Count == 0 && missingOwners.Count == 0)
            {
                results.Add(new AppResult(app, desired, Outcome.Skip, [$"{already} Its settings, owners and assignments are the same."]) { ExistingId = id, DestinationAppId = destinationAppId });
                continue;
            }
            results.Add(new AppResult(app, desired, Outcome.Create,
                [$"{already} Updated in place: nothing is taken away.{(isRegistration ? " Its registration and single sign-on settings are left as they are." : "")}"])
            {
                ExistingId = id,
                DestinationAppId = destinationAppId,
                ChangedSettings = changed,
                Assignments = missingAssignments,
                Owners = new JsonArray(missingOwners.Select(o => (JsonNode?)o).ToArray()),
                Changes = transformed.Changes,
            });
        }

        AddFindings(results, findings, destinationInfo, choices);
        if (sameName.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                $"Same name already in {destinationName} · {sameName.Count} registration{Plural(sameName.Count)}",
                "Another registration has this name there. A new one is created beside it, with its own app ID; nothing is merged.",
                sameName));
        }
        var report = new AppPreflightReport(results, findings);
        return report with { BlockedSkipped = choices.SkipBlocked && report.BlockedCount > 0 };
    }

    static void AddFindings(List<AppResult> results, List<Finding> findings, TenantInfo destinationInfo, AppPreflightChoices choices)
    {
        var destinationName = destinationInfo.DisplayName;
        var migrated = results.Where(r => r.Outcome == Outcome.Create).ToList();

        var blocked = results.Where(r => r.Outcome == Outcome.Blocked).ToList();
        if (blocked.Count > 0)
        {
            findings.Add(new Finding(Severity.Blocker,
                $"Users or groups with no match · {blocked.Count} app{Plural(blocked.Count)} blocked",
                "Go back to Map users & groups and match or leave out each one.",
                blocked.SelectMany(r => r.Reasons.Select(reason => $"{r.Source.DisplayName}: {reason}")).ToList()));
        }

        var existing = results.Where(r => r.ExistingId is not null).ToList();
        if (existing.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                $"Already in {destinationName} · {existing.Count} app{Plural(existing.Count)}",
                choices.ExistingApps == ExistingAppChoice.Skip
                    ? "They're skipped. Or update them in place: their settings are changed to match, and missing owners and assignments added."
                    : "Their settings are changed to match, and missing owners and assignments added. Nothing is taken away.",
                existing.Select(r => r.Source.DisplayName).ToList())
            {
                Choice = FindingChoice.ExistingApps,
            });
        }

        var newIds = migrated.Where(r => r.IsOwnApp && !r.IsUpdate).ToList();
        if (newIds.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                $"New app IDs · {newIds.Count} app{Plural(newIds.Count)}",
                $"Each is recreated in {destinationName} with a new app ID. Anything that signs in with the old one, such as a script, service or mobile app, has to be changed. The results list every new app ID.",
                newIds.Select(r => $"{r.Source.DisplayName} (app ID {r.AppId} in the source)").ToList()));
        }

        var saml = migrated.Where(r => r.UsesSaml && r.IsOwnApp && !r.IsUpdate).ToList();
        if (saml.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                $"New SAML signing certificates · {saml.Count} app{Plural(saml.Count)}",
                $"Their single sign-on and claims are copied, with a new signing certificate in {destinationName}: the old one's private key can't be exported. Give each app's vendor the new certificate, or the app's new federation metadata, before moving users over.",
                saml.Select(r => r.Source.DisplayName).ToList()));
        }

        var gallery = results.Where(r => r.Source.Settings["template"]?["missing"]?.GetValue<bool>() == true).ToList();
        if (gallery.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                $"Add from the gallery by hand · {gallery.Count} app{Plural(gallery.Count)}",
                "Their gallery template can't be found, so Movewise can't make them from it. Add each one from the gallery in the destination, then set it up as it was in the source.",
                gallery.Select(r => r.Source.DisplayName).ToList()));
        }

        var leftOut = migrated.SelectMany(r => r.Changes.Where(c => c.Description.EndsWith("removed.", StringComparison.Ordinal))
            .Select(c => $"{r.Source.DisplayName}: {c.Description}")).ToList();
        if (leftOut.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                $"Users and groups left out · {leftOut.Count}",
                $"You chose to leave them out, so they won't be assigned or made owners in {destinationName}.",
                leftOut));
        }

        var consent = migrated.Where(r => Strings(r.Source.Settings["apiPermissions"]).Count > 0).ToList();
        if (consent.Count > 0)
        {
            var count = consent.Sum(r => Strings(r.Source.Settings["apiPermissions"]).Count);
            findings.Add(new Finding(Severity.Warning,
                $"API permissions need admin consent · {consent.Count} app{Plural(consent.Count)}",
                $"They were granted in the source. Movewise never grants consent: review each app, then grant its permissions in {destinationName} (Enterprise apps → the app → Permissions).",
                consent.SelectMany(r => Strings(r.Source.Settings["apiPermissions"]).Select(p => $"{r.Source.DisplayName}: {p}")).ToList()));
        }

        var byHand = migrated.SelectMany(r => Strings(r.Source.Settings["notMigrated"]).Select(n => $"{r.Source.DisplayName}: {n}")).ToList();
        if (byHand.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                $"Set up by hand · {migrated.Count(r => Strings(r.Source.Settings["notMigrated"]).Count > 0)} app{Plural(migrated.Count(r => Strings(r.Source.Settings["notMigrated"]).Count > 0))}",
                "The apps and their assignments are migrated. These parts of them aren't.",
                byHand));
        }

        if (migrated.Count > 0)
        {
            findings.Add(new Finding(Severity.Info,
                "Never migrated",
                "Provisioning (SCIM) settings and credentials, Application Proxy connectors and consent grants aren't copied. Check them in the source and set them up again where needed.",
                []));
        }
    }

    /// <summary>The destination's registration for this app ID, or null.</summary>
    static async Task<string?> RegistrationIdAsync(IGraphReader graph, string appId, CancellationToken ct)
    {
        var page = await graph.GetObjectAsync(GraphQuery.Where("v1.0/applications", $"appId eq {GraphQuery.Literal(appId)}", "id", top: 1), ct);
        return page["value"]?.AsArray().OfType<JsonObject>().Select(a => Text(a, "id")).FirstOrDefault(i => i.Length > 0);
    }

    static async Task<bool> NameTakenAsync(IGraphReader graph, string name, CancellationToken ct)
    {
        var page = await graph.GetObjectAsync(GraphQuery.Where("v1.0/applications", $"displayName eq {GraphQuery.Literal(name)}", "id", top: 1), ct);
        return page["value"] is JsonArray { Count: > 0 };
    }

    /// <summary>The destination's service principal for this app ID, or null.</summary>
    static async Task<JsonObject?> FindAsync(IGraphReader graph, string appId, CancellationToken ct)
    {
        var page = await graph.GetObjectAsync(GraphQuery.Where("v1.0/servicePrincipals", $"appId eq {GraphQuery.Literal(appId)}", Select, top: 5), ct);
        return page["value"]?.AsArray().OfType<JsonObject>().FirstOrDefault();
    }

    /// <summary>Every assignment of the app, users and groups together, as deploying sends them.</summary>
    static JsonArray Assignments(JsonObject desired) => new(
        new[] { ("assignedUsers", "User"), ("assignedGroups", "Group") }
            .SelectMany(p => (desired[p.Item1] as JsonArray ?? []).OfType<JsonObject>().Select(a => (JsonNode?)new JsonObject
            {
                ["principalId"] = Text(a, "principalId"),
                ["principalType"] = p.Item2,
                ["appRoleId"] = Text(a, "appRoleId"),
            }))
            .ToArray());

    /// <summary>The texts in a list, such as an app's "apiPermissions" or "notMigrated".</summary>
    public static IReadOnlyList<string> Strings(JsonNode? node) =>
        (node as JsonArray ?? []).Select(v => v is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>().ToList();

    static string Text(JsonObject item, string name) =>
        item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    static string Plural(int count) => count == 1 ? "" : "s";
}
