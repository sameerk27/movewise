using System.Text.Json.Nodes;
using Movewise.Core.Export;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Deploy;

/// <summary>
/// Migrates apps: creates each enterprise app's service principal (or updates the destination's), recreates each app
/// registration with a new app ID, then adds owners and assignments, and can take all of it out again. Only users and
/// groups are given access to apps: API permissions are never granted, whatever the source had.
/// </summary>
public static class AppDeployer
{
    const string ServicePrincipals = "v1.0/servicePrincipals";
    const string Applications = "v1.0/applications";
    const string ClaimsMappingPolicies = "v1.0/policies/claimsMappingPolicies";

    // Recorded in a step's CreatedRules: its app exists (or has been updated), so a retry goes on to its access;
    // and a registration's application ID URI has been set.
    const string AppDone = "app";
    const string UriDone = "uri";
    const string ManifestDone = "manifest";
    const string SettingsDone = "settings";
    const string SsoDone = "sso";
    const string CertificateDone = "certificate";
    const string ClaimsPolicyDone = "claimsPolicy";
    // A claims mapping policy made ("claims:{n}|{id}"), then assigned to the app ("claimsAssigned:{n}").
    const string ClaimsPrefix = "claims:";
    const string AssignedPrefix = "claimsAssigned:";
    const string OwnerPrefix = "owner:";
    const string AssignmentPrefix = "assignment:";

    /// <summary>The run for a checked app migration: one step per app to create or update, in name order.</summary>
    public static DeployRun Plan(AppPreflightReport report, TenantInfo source, TenantInfo destination)
    {
        var steps = report.Apps
            .Where(a => a.Outcome == Outcome.Create)
            .OrderBy(a => a.Source.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(a => new DeployStep
            {
                Key = a.Key,
                TargetType = a.Source.Type.Id,
                SourceId = a.Source.SourceId,
                DisplayName = a.Source.DisplayName,
                Body = Body(a),
                Assignments = (JsonArray)a.Assignments.DeepClone(),
                Owners = (JsonArray)a.Owners.DeepClone(),
                UpdateOf = a.IsUpdate ? a.ExistingId : null,
                AppId = a.IsOwnApp ? a.DestinationAppId : null,
            })
            .ToList();

        return new DeployRun
        {
            RunId = Guid.NewGuid().ToString("N")[..12],
            Started = DateTimeOffset.Now,
            SourceTenantId = source.TenantId,
            SourceName = source.DisplayName,
            DestinationTenantId = destination.TenantId,
            DestinationName = destination.DisplayName,
            Steps = steps,
            Kind = RunKind.Applications,
        };
    }

    /// <summary>
    /// What's sent. An update: the service principal's settings that differ. A new enterprise app: its app ID and
    /// settings. A new registration: the registration ("application"), its service principal's settings
    /// ("servicePrincipal"), the source's app ID, and whether it had an application ID URI made from it ("apiUri").
    /// </summary>
    static JsonObject Body(AppResult app)
    {
        if (app.IsUpdate)
        {
            var settings = app.IsOwnApp ? app.Desired["servicePrincipal"] as JsonObject ?? [] : app.Desired;
            return SettingsDiff.Only(settings, app.ChangedSettings);
        }
        if (!app.IsOwnApp)
        {
            var enterprise = SettingsDiff.Only(app.Desired, AppPreflight.CopiedSettings.Where(app.Desired.ContainsKey));
            enterprise["appId"] = app.AppId;
            return enterprise;
        }
        var principal = app.Desired["servicePrincipal"] as JsonObject ?? [];
        var template = app.Desired["template"] as JsonObject;
        var copied = template is not null ? AppPreflight.CopiedGallerySettings : AppPreflight.CopiedRegistrationSettings;
        var uris = AppPreflight.Strings(app.Desired["identifierUris"]);
        var body = new JsonObject
        {
            ["application"] = SettingsDiff.Only(app.Desired, copied.Where(name => app.Desired.ContainsKey(name) && name != "identifierUris")),
            ["servicePrincipal"] = SettingsDiff.Only(principal, AppPreflight.CopiedSettings.Where(principal.ContainsKey)),
            ["sourceAppId"] = app.AppId,
            ["apiUri"] = uris.Contains($"api://{app.AppId}", StringComparer.OrdinalIgnoreCase),
            // A SAML app's entity IDs are kept; other URIs need a domain verified in the destination.
            ["identifierUris"] = new JsonArray((app.UsesSaml ? uris.Where(u => !u.Equals($"api://{app.AppId}", StringComparison.OrdinalIgnoreCase)) : [])
                .Select(u => (JsonNode?)u).ToArray()),
        };
        if (template is not null)
            body["template"] = template.DeepClone();
        if (app.Desired["saml"] is JsonObject saml)
            body["saml"] = saml.DeepClone();
        return body;
    }

    /// <summary>
    /// Creates or updates every app that isn't finished, then gives each its owners and assignments, so every app
    /// exists before anyone is given access. Safe to call again: each app, owner and assignment is recorded once
    /// made, an interrupted create is looked up first, and a step that fails doesn't stop the rest.
    /// </summary>
    /// <param name="ct">Stops between apps, owners and assignments; the request in progress is always finished first.</param>
    /// <param name="appIds">Where each recreated registration's new app ID is recorded, for the policy migration to find.</param>
    public static async Task RunAsync(IGraphWriter graph, DeployRun run, Func<Task> save, Action<DeployStep>? changed = null, CancellationToken ct = default,
        AppIdMap? appIds = null)
    {
        if (run.RolledBack is not null)
            throw new InvalidOperationException("This run was rolled back. Start a new deployment instead.");
        if (run.Kind != RunKind.Applications)
            throw new InvalidOperationException("This run migrates policies. Open it in Migrations.");

        foreach (var step in run.Steps.Where(s => s.IsUnfinished && !s.CreatedRules.Contains(AppDone)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            await AppAsync(graph, run, step, Saver(step, save, changed), appIds);
        }
        foreach (var step in run.Steps.Where(s => s.IsUnfinished && s.CreatedRules.Contains(AppDone)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            await AccessAsync(graph, step, Saver(step, save, changed), ct);
        }

        run.Completed = DateTimeOffset.Now;
        await save();
    }

    static Func<Task> Saver(DeployStep step, Func<Task> save, Action<DeployStep>? changed) => async () =>
    {
        await save();
        changed?.Invoke(step);
    };

    static async Task Finish(DeployStep step, StepStatus status, string? message, Func<Task> save)
    {
        step.Status = status;
        step.Message = message;
        if (status is StepStatus.Done or StepStatus.Failed)
            step.Finished = DateTimeOffset.Now;
        await save();
    }

    static bool IsOwnApp(DeployStep step) => step.TargetType is ResourceRegistry.AppRegistration or ResourceRegistry.GalleryApp;

    /// <summary>The service principal assignments are made on: a registration's own, or the enterprise app itself.</summary>
    static string PrincipalId(DeployStep step) => step.ServicePrincipalId ?? step.DestinationId!;

    /// <summary>Where owners are added: a registration's owners are the registration's.</summary>
    static string OwnersPath(DeployStep step) =>
        IsOwnApp(step) ? $"{Applications}/{Uri.EscapeDataString(step.DestinationId!)}" : ItemPath(step.DestinationId!);

    /// <summary>Creates the app, or changes the existing one's settings.</summary>
    static async Task AppAsync(IGraphWriter graph, DeployRun run, DeployStep step, Func<Task> save, AppIdMap? appIds)
    {
        var none = CancellationToken.None;
        try
        {
            if (step.UpdateOf is { } existingId)
            {
                // What it is now is kept before anything changes, so rolling back can put it back. A retry keeps the first copy.
                if (step.Previous is null)
                {
                    var current = await graph.GetObjectAsync($"{ItemPath(existingId)}?$select={string.Join(",", AppPreflight.CopiedSettings)}", none);
                    step.Previous = SettingsDiff.Only(Normalizer.Canonicalize(current)!.AsObject(), step.Body.Select(p => p.Key));
                    if (IsOwnApp(step))
                    {
                        step.ServicePrincipalId = existingId;
                        step.DestinationId = await RegistrationIdAsync(graph, step.AppId!, none)
                            ?? throw new InvalidOperationException("Its registration is no longer in the destination.");
                    }
                    else
                    {
                        step.DestinationId = existingId;
                    }
                }
                await Finish(step, StepStatus.Creating, null, save);
                if (step.Body.Count > 0)
                    await graph.PatchAsync(ItemPath(existingId), step.Body.DeepClone(), none);
                step.CreatedRules.Add(AppDone);
                await Finish(step, StepStatus.Created, null, save);
                return;
            }

            if (IsOwnApp(step))
            {
                await OwnAppAsync(graph, run, step, save, appIds);
                return;
            }

            // An earlier attempt sent the create request but never heard back: the app may exist.
            if (step.DestinationId is null && step.Status == StepStatus.Creating)
            {
                var found = await FindAsync(graph, step.Body["appId"]?.GetValue<string>() ?? "", none);
                if (found is not null)
                {
                    step.DestinationId = found;
                    step.Notes.Add("Found in the destination after an interrupted run, and used instead of creating it again.");
                }
            }
            if (step.DestinationId is null)
            {
                step.CreateSent = DateTimeOffset.UtcNow;
                await Finish(step, StepStatus.Creating, null, save);
                try
                {
                    var created = await graph.PostAsync(ServicePrincipals, step.Body.DeepClone(), none);
                    step.DestinationId = created?["id"]?.GetValue<string>()
                        ?? throw new InvalidOperationException("Microsoft Graph didn't return the new app's ID.");
                }
                catch (Exception ex) when (Deployer.IsUncertain(ex, none))
                {
                    // Left as Creating: the next attempt looks for it before creating it again.
                    step.Message = $"No answer from Microsoft 365, so it may or may not have been created. Retry to check. ({ex.Message})";
                    await save();
                    return;
                }
            }
            step.CreatedRules.Add(AppDone);
            await Finish(step, StepStatus.Created, null, save);
        }
        catch (Exception ex)
        {
            await Finish(step, StepStatus.Failed,
                step.UpdateOf is not null ? $"Not updated: {ex.Message}"
                : step.DestinationId is not null ? $"Created, but something after that failed: {ex.Message}"
                : ex.Message, save);
        }
    }

    /// <summary>
    /// Recreates one of the source's own apps, each part kept on the step once made so a retry does only the rest:
    /// the app itself (a registration posted as it was, or a gallery app made from its template, either way with a
    /// new app ID), its application ID URIs, its service principal and settings, then for a SAML app its single
    /// sign-on, a new signing certificate and its claims. The new app ID is recorded in the app ID map once it's set up.
    /// </summary>
    static async Task OwnAppAsync(IGraphWriter graph, DeployRun run, DeployStep step, Func<Task> save, AppIdMap? appIds)
    {
        var none = CancellationToken.None;
        var template = step.Body["template"] as JsonObject;
        if (step.DestinationId is null)
        {
            // An interrupted create: the app is looked for by name, made after the request was sent.
            if (step.Status == StepStatus.Creating)
            {
                var found = await FindRegistrationsAsync(graph, step, none);
                if (found.Count > 1)
                {
                    step.Message = "More than one app in the destination has this name and was made during this run, so Movewise can't tell which is its own. Delete the extra one in the admin center, then retry.";
                    await save();
                    return;
                }
                if (found.Count == 1)
                {
                    (step.DestinationId, step.AppId) = found[0];
                    step.Notes.Add("Found in the destination after an interrupted run, and used instead of creating it again.");
                }
            }
            if (step.DestinationId is null)
            {
                step.CreateSent = DateTimeOffset.UtcNow;
                await Finish(step, StepStatus.Creating, null, save);
                try
                {
                    if (template is not null)
                    {
                        // The template makes the registration and its service principal together.
                        var made = await graph.PostAsync($"v1.0/applicationTemplates/{Uri.EscapeDataString(Text(template, "id"))}/instantiate",
                            new JsonObject { ["displayName"] = step.DisplayName }, none);
                        step.AppId = made?["application"]?["appId"]?.GetValue<string>();
                        step.ServicePrincipalId = made?["servicePrincipal"]?["id"]?.GetValue<string>();
                        step.DestinationId = made?["application"]?["id"]?.GetValue<string>()
                            ?? throw new InvalidOperationException("Microsoft Graph didn't return the new app's ID.");
                    }
                    else
                    {
                        var created = await graph.PostAsync(Applications, step.Body["application"]!.DeepClone(), none);
                        step.AppId = created?["appId"]?.GetValue<string>();
                        step.DestinationId = created?["id"]?.GetValue<string>()
                            ?? throw new InvalidOperationException("Microsoft Graph didn't return the new registration's ID.");
                    }
                }
                catch (Exception ex) when (Deployer.IsUncertain(ex, none))
                {
                    step.Message = $"No answer from Microsoft 365, so it may or may not have been created. Retry to check. ({ex.Message})";
                    await save();
                    return;
                }
                catch (Exception ex) when (template is not null && ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException($"It couldn't be made from the gallery template \"{Text(template, "displayName")}\": {ex.Message} Add it from the gallery by hand.");
                }
                await save();
            }
        }
        var registration = $"{Applications}/{Uri.EscapeDataString(step.DestinationId)}";
        step.AppId ??= (await graph.GetObjectAsync($"{registration}?$select=appId", none))["appId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Microsoft Graph didn't return the new app ID.");

        // A gallery app is set up as the source's was. Its app roles are added to the template's, keeping their IDs,
        // so assignments keep their roles; roles the template already has stay, since enabled roles can't be removed.
        if (template is not null && !Done(step, ManifestDone))
        {
            var settings = (JsonObject)(step.Body["application"]?.DeepClone() ?? new JsonObject());
            if (settings["appRoles"] is JsonArray wanted)
            {
                var roles = (await graph.GetObjectAsync($"{registration}?$select=appRoles", none))["appRoles"] as JsonArray ?? [];
                var ids = roles.OfType<JsonObject>().Select(r => Text(r, "id")).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var merged = (JsonArray)roles.DeepClone();
                foreach (var role in wanted.OfType<JsonObject>().Where(r => !ids.Contains(Text(r, "id"))))
                    merged.Add(role.DeepClone());
                settings["appRoles"] = merged;
            }
            if (settings.Count > 0)
                await graph.PatchAsync(registration, settings, none);
            step.CreatedRules.Add(ManifestDone);
            await save();
        }

        // Application ID URIs: one made from the new app ID in place of the old one's, and a SAML app's entity IDs.
        var uris = new JsonArray();
        if (step.Body["apiUri"]?.GetValue<bool>() == true)
            uris.Add($"api://{step.AppId}");
        foreach (var uri in AppPreflight.Strings(step.Body["identifierUris"]))
            uris.Add(uri);
        if (uris.Count > 0 && !Done(step, UriDone))
        {
            await graph.PatchAsync(registration, new JsonObject { ["identifierUris"] = uris }, none);
            step.CreatedRules.Add(UriDone);
            await save();
        }

        // Its app ID is new, so any service principal for it is this run's.
        if (step.ServicePrincipalId is null)
        {
            step.ServicePrincipalId = await FindAsync(graph, step.AppId, none);
            if (step.ServicePrincipalId is null)
            {
                var body = (JsonObject)(step.Body["servicePrincipal"]?.DeepClone() ?? new JsonObject());
                body["appId"] = step.AppId;
                var created = await graph.PostAsync(ServicePrincipals, body, none);
                step.ServicePrincipalId = created?["id"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Microsoft Graph didn't return the new service principal's ID.");
                step.CreatedRules.Add(SettingsDone);
            }
            await save();
        }
        var principal = ItemPath(step.ServicePrincipalId);
        if (!Done(step, SettingsDone) && step.Body["servicePrincipal"] is JsonObject { Count: > 0 } principalSettings)
        {
            await graph.PatchAsync(principal, principalSettings.DeepClone(), none);
            step.CreatedRules.Add(SettingsDone);
            await save();
        }

        if (step.Body["saml"] is JsonObject saml)
            await SamlAsync(graph, step, principal, saml, save);

        var sourceAppId = step.Body["sourceAppId"]?.GetValue<string>() ?? "";
        if (appIds is not null)
            await appIds.RecordAsync(new AppIdEntry(run.SourceTenantId, sourceAppId, step.AppId, step.DisplayName, run.RunId, DateTimeOffset.Now));
        var note = $"New app ID {step.AppId} (it was {sourceAppId}). Change anything that signs in with the old one.";
        if (!step.Notes.Contains(note))
            step.Notes.Add(note);
        step.CreatedRules.Add(AppDone);
        await Finish(step, StepStatus.Created, null, save);
    }

    /// <summary>
    /// SAML single sign-on on a new app: the mode and relay state, a new signing certificate made the one in use, the
    /// claims mapping policies (each made anew and assigned to the app) and its custom claims.
    /// </summary>
    static async Task SamlAsync(IGraphWriter graph, DeployStep step, string principal, JsonObject saml, Func<Task> save)
    {
        var none = CancellationToken.None;
        if (!Done(step, SsoDone))
        {
            var settings = new JsonObject { ["preferredSingleSignOnMode"] = "saml" };
            if (saml["samlSingleSignOnSettings"] is JsonObject relay)
                settings["samlSingleSignOnSettings"] = relay.DeepClone();
            await graph.PatchAsync(principal, settings, none);
            step.CreatedRules.Add(SsoDone);
            await save();
        }

        if (!Done(step, CertificateDone))
        {
            var expires = DateTimeOffset.UtcNow.AddYears(3);
            var certificate = await graph.PostAsync($"{principal}/addTokenSigningCertificate", new JsonObject
            {
                ["displayName"] = $"CN={step.DisplayName}",
                ["endDateTime"] = expires.ToString("o"),
            }, none);
            var thumbprint = certificate?["thumbprint"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Microsoft Graph didn't return the new signing certificate.");
            await graph.PatchAsync(principal, new JsonObject { ["preferredTokenSigningKeyThumbprint"] = thumbprint }, none);
            step.CreatedRules.Add($"{CertificateDone}|{thumbprint}");
            step.Notes.Add($"New SAML signing certificate (thumbprint {thumbprint}, valid until {expires:d MMM yyyy}). Give it, or the app's federation metadata, to its vendor before moving users over.");
            await save();
        }

        var policies = (saml["claimsMappingPolicies"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        for (var i = 0; i < policies.Count; i++)
        {
            var key = $"{ClaimsPrefix}{i}";
            var policyId = step.CreatedRules.FirstOrDefault(r => r.StartsWith(key + "|", StringComparison.Ordinal))?[(key.Length + 1)..];
            if (policyId is null)
            {
                var created = await graph.PostAsync(ClaimsMappingPolicies, new JsonObject
                {
                    ["displayName"] = policies[i]["displayName"]?.DeepClone() ?? step.DisplayName,
                    ["definition"] = policies[i]["definition"]?.DeepClone(),
                }, none);
                policyId = created?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Microsoft Graph didn't return the new claims mapping policy's ID.");
                step.CreatedRules.Add($"{key}|{policyId}");
                await save();
            }
            if (!Done(step, $"{AssignedPrefix}{i}"))
            {
                await graph.PostAsync($"{principal}/claimsMappingPolicies/$ref",
                    new JsonObject { ["@odata.id"] = $"https://graph.microsoft.com/{ClaimsMappingPolicies}/{policyId}" }, none);
                step.CreatedRules.Add($"{AssignedPrefix}{i}");
                await save();
            }
        }

        if (saml["claimsPolicy"] is JsonObject claims && !Done(step, ClaimsPolicyDone))
        {
            await graph.PatchAsync($"beta/servicePrincipals/{Uri.EscapeDataString(step.ServicePrincipalId!)}/claimsPolicy", claims.DeepClone(), none);
            step.CreatedRules.Add(ClaimsPolicyDone);
            await save();
        }
    }

    static bool Done(DeployStep step, string key) =>
        step.CreatedRules.Any(r => r == key || r.StartsWith(key + "|", StringComparison.Ordinal));

    static string Text(JsonObject item, string name) =>
        item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    /// <summary>
    /// Adds the app's owners, then its assignments, each recorded once made. One found already there is taken as
    /// this run's own only for a new app, or when this run sent it without hearing back; otherwise it was there
    /// before, and rollback leaves it.
    /// </summary>
    static async Task AccessAsync(IGraphWriter graph, DeployStep step, Func<Task> save, CancellationToken ct)
    {
        var none = CancellationToken.None;
        var principal = PrincipalId(step);
        var isNew = step.UpdateOf is null;
        try
        {
            var owners = AppPreflight.Strings(step.Owners);
            if (owners.Any(o => !Recorded(step, OwnerPrefix + o)))
            {
                var present = (await graph.GetCollectionAsync($"{OwnersPath(step)}/owners?$select=id", none))
                    .Select(o => o["id"]?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var owner in owners.Where(o => !Recorded(step, OwnerPrefix + o)))
                {
                    ct.ThrowIfCancellationRequested();
                    var key = OwnerPrefix + owner;
                    if (present.Contains(owner))
                    {
                        if (isNew || step.PendingAssignment == key)
                            step.CreatedAssignments.Add(key);
                    }
                    else
                    {
                        step.PendingAssignment = key;
                        await save();
                        await graph.PostAsync($"{OwnersPath(step)}/owners/$ref",
                            new JsonObject { ["@odata.id"] = $"https://graph.microsoft.com/v1.0/directoryObjects/{owner}" }, none);
                        step.CreatedAssignments.Add(key);
                    }
                    step.PendingAssignment = null;
                    await save();
                }
            }

            var assignments = (step.Assignments ?? []).OfType<JsonObject>().ToList();
            if (assignments.Any(a => !Recorded(step, AssignmentPrefix + Who(a))))
            {
                var present = (await graph.GetCollectionAsync($"{ItemPath(principal)}/appRoleAssignedTo", none))
                    .GroupBy(a => $"{a["principalId"]?.GetValue<string>()}|{a["appRoleId"]?.GetValue<string>()}", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First()["id"]?.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
                foreach (var assignment in assignments.Where(a => !Recorded(step, AssignmentPrefix + Who(a))))
                {
                    ct.ThrowIfCancellationRequested();
                    var who = Who(assignment);
                    if (present.TryGetValue(who, out var existing))
                    {
                        if (isNew || step.PendingAssignment == AssignmentPrefix + who)
                            step.CreatedAssignments.Add($"{AssignmentPrefix}{who}|{existing}");
                    }
                    else
                    {
                        step.PendingAssignment = AssignmentPrefix + who;
                        await save();
                        var created = await graph.PostAsync($"{ItemPath(principal)}/appRoleAssignedTo", new JsonObject
                        {
                            ["principalId"] = assignment["principalId"]?.DeepClone(),
                            ["resourceId"] = principal,
                            ["appRoleId"] = assignment["appRoleId"]?.DeepClone(),
                        }, none);
                        step.CreatedAssignments.Add($"{AssignmentPrefix}{who}|{created?["id"]?.GetValue<string>()}");
                    }
                    step.PendingAssignment = null;
                    await save();
                }
            }

            await Finish(step, StepStatus.Done, Summary(step), save);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A clear refusal means nothing was made; only an unanswered request may have been.
            if (!Deployer.IsUncertain(ex, none))
                step.PendingAssignment = null;
            await Finish(step, StepStatus.Failed, $"{(isNew ? "Created" : "Updated")}, but its owners and assignments couldn't all be added: {ex.Message}", save);
        }
    }

    static string Summary(DeployStep step)
    {
        var owners = step.CreatedAssignments.Count(a => a.StartsWith(OwnerPrefix, StringComparison.Ordinal));
        var assignments = step.CreatedAssignments.Count(a => a.StartsWith(AssignmentPrefix, StringComparison.Ordinal));
        var parts = new List<string>();
        if (step.UpdateOf is not null && step.Body.Count > 0)
            parts.Add($"Changed: {string.Join(", ", step.Body.Select(p => p.Key))}");
        parts.Add(assignments == 0 ? "no assignments" : $"{assignments} assignment{(assignments == 1 ? "" : "s")}");
        parts.Add(owners == 0 ? "no owners added" : $"{owners} owner{(owners == 1 ? "" : "s")} added");
        return string.Join(" · ", parts) + ".";
    }

    /// <summary>
    /// Takes out what the run made, newest first: apps and registrations it created are deleted (Entra ID keeps
    /// deleted apps for 30 days) and their new app IDs taken out of the app ID map; apps it updated get their settings
    /// put back and lose only the owners and assignments it added.
    /// </summary>
    public static async Task RollbackAsync(IGraphWriter graph, DeployRun run, Func<Task> save, Action<DeployStep>? changed = null, CancellationToken ct = default,
        AppIdMap? appIds = null)
    {
        if (run.Kind != RunKind.Applications)
            throw new InvalidOperationException("This run migrated policies. Roll it back in Migrations.");
        var none = CancellationToken.None;

        foreach (var step in Enumerable.Reverse(run.Steps).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var stepSave = Saver(step, save, changed);

            if (step.UpdateOf is { } updatedId)
            {
                if (step.Previous is null || step.Status is StepStatus.Pending or StepStatus.Skipped or StepStatus.RolledBack)
                    continue;
                try
                {
                    foreach (var entry in step.CreatedAssignments.ToList())
                    {
                        var path = entry.StartsWith(OwnerPrefix, StringComparison.Ordinal)
                            ? $"{OwnersPath(step)}/owners/{Uri.EscapeDataString(entry[OwnerPrefix.Length..])}/$ref"
                            : entry.Split('|') is [_, _, { Length: > 0 } assignmentId]
                                ? $"{ItemPath(updatedId)}/appRoleAssignedTo/{Uri.EscapeDataString(assignmentId)}"
                                : null;
                        if (path is not null)
                        {
                            try
                            {
                                await graph.DeleteAsync(path, none);
                            }
                            catch (Exception ex) when (Deployer.IsNotFound(ex))
                            {
                            }
                        }
                        step.CreatedAssignments.Remove(entry);
                        await save();
                    }
                    if (step.Previous.Count > 0)
                        await graph.PatchAsync(ItemPath(updatedId), step.Previous.DeepClone(), none);
                    await Finish(step, StepStatus.RolledBack, "Put back as it was: its settings, and without the owners and assignments this run added.", stepSave);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await Finish(step, StepStatus.RollbackFailed, $"Couldn't put it back as it was: {ex.Message}", stepSave);
                }
                continue;
            }

            if (step.DestinationId is null && step.Status is StepStatus.Creating or StepStatus.RollbackFailed)
            {
                // An interrupted create: delete it only if it's clearly there.
                try
                {
                    if (IsOwnApp(step))
                    {
                        var found = await FindRegistrationsAsync(graph, step, none);
                        if (found.Count > 1)
                        {
                            await Finish(step, StepStatus.RollbackFailed, "Check the destination by hand: more than one registration with this name was made during this run.", stepSave);
                            continue;
                        }
                        step.DestinationId = found.Count == 1 ? found[0].Id : null;
                    }
                    else
                    {
                        step.DestinationId = await FindAsync(graph, step.Body["appId"]?.GetValue<string>() ?? "", none);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await Finish(step, StepStatus.RollbackFailed, $"Check the destination by hand: Movewise couldn't check whether it was created. ({ex.Message})", stepSave);
                    continue;
                }
                if (step.DestinationId is null)
                {
                    await Finish(step, StepStatus.RolledBack, "Not found in the destination, so it was probably never created. Check the admin center to be sure.", stepSave);
                    continue;
                }
            }
            if (step.DestinationId is null || step.Status == StepStatus.RolledBack)
                continue;

            try
            {
                // Deleting a registration deletes its service principal; owners and assignments go with the app.
                var path = IsOwnApp(step) ? $"{Applications}/{Uri.EscapeDataString(step.DestinationId)}" : ItemPath(step.DestinationId);
                try
                {
                    await graph.DeleteAsync(path, none);
                    step.Message = "Deleted from the destination, with its owners and assignments. Entra ID keeps it in deleted apps for 30 days.";
                }
                catch (Exception ex) when (Deployer.IsNotFound(ex))
                {
                    step.Message = "Already gone from the destination.";
                }
                if (IsOwnApp(step) && appIds is not null)
                    await appIds.RemoveAsync(run.SourceTenantId, step.Body["sourceAppId"]?.GetValue<string>() ?? "", run.RunId);
                // Claims mapping policies are objects of their own, made for this app: they go too.
                foreach (var entry in step.CreatedRules.Where(r => r.StartsWith(ClaimsPrefix, StringComparison.Ordinal)).ToList())
                {
                    try
                    {
                        await graph.DeleteAsync($"{ClaimsMappingPolicies}/{Uri.EscapeDataString(entry.Split('|')[1])}", none);
                    }
                    catch (Exception ex) when (Deployer.IsNotFound(ex))
                    {
                    }
                    step.CreatedRules.Remove(entry);
                    await save();
                }
                await Finish(step, StepStatus.RolledBack, step.Message, stepSave);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Finish(step, StepStatus.RollbackFailed, $"Couldn't delete it: {ex.Message}", stepSave);
            }
        }

        if (!run.Steps.Any(s => s.Status == StepStatus.RollbackFailed))
            run.RolledBack = DateTimeOffset.Now;
        await save();
    }

    /// <summary>The destination's service principal for an app ID, or null.</summary>
    static async Task<string?> FindAsync(IGraphReader graph, string appId, CancellationToken ct)
    {
        var page = await graph.GetObjectAsync(GraphQuery.Where(ServicePrincipals, $"appId eq {GraphQuery.Literal(appId)}", "id", top: 5), ct);
        return page["value"]?.AsArray().OfType<JsonObject>().Select(o => o["id"]?.GetValue<string>()).FirstOrDefault(i => !string.IsNullOrEmpty(i));
    }

    /// <summary>The destination's registration for an app ID, or null.</summary>
    static async Task<string?> RegistrationIdAsync(IGraphReader graph, string appId, CancellationToken ct)
    {
        var page = await graph.GetObjectAsync(GraphQuery.Where(Applications, $"appId eq {GraphQuery.Literal(appId)}", "id", top: 1), ct);
        return page["value"]?.AsArray().OfType<JsonObject>().Select(o => o["id"]?.GetValue<string>()).FirstOrDefault(i => !string.IsNullOrEmpty(i));
    }

    /// <summary>
    /// Registrations with this step's name made since its create request was sent. One that was there before isn't
    /// this run's, whatever its name; five minutes' leeway covers a difference between this PC's clock and Microsoft's.
    /// </summary>
    static async Task<IReadOnlyList<(string Id, string? AppId)>> FindRegistrationsAsync(IGraphReader graph, DeployStep step, CancellationToken ct)
    {
        var page = await graph.GetObjectAsync(GraphQuery.Where(Applications, $"displayName eq {GraphQuery.Literal(step.DisplayName)}", "id,appId,createdDateTime", top: 10), ct);
        var since = step.CreateSent?.AddMinutes(-5);
        return (page["value"]?.AsArray().OfType<JsonObject>() ?? [])
            .Where(a => since is null || a["createdDateTime"] is not JsonValue created
                || !DateTimeOffset.TryParse(created.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                || at >= since)
            .Select(a => (a["id"]?.GetValue<string>() ?? "", a["appId"]?.GetValue<string>()))
            .Where(a => a.Item1.Length > 0)
            .ToList();
    }

    static bool Recorded(DeployStep step, string key) =>
        step.CreatedAssignments.Any(a => a.Equals(key, StringComparison.OrdinalIgnoreCase) || a.StartsWith(key + "|", StringComparison.OrdinalIgnoreCase));

    static string Who(JsonObject assignment) =>
        $"{assignment["principalId"]?.GetValue<string>()}|{assignment["appRoleId"]?.GetValue<string>()}";

    static string ItemPath(string id) => $"{ServicePrincipals}/{Uri.EscapeDataString(id)}";
}
