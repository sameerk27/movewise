using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Movewise.Core.Access;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Preflight;

public enum Severity { Blocker, Warning, Info }

/// <summary>What to do with a policy whose name is already taken in the destination.</summary>
public enum ConflictChoice { Skip, CreateWithSuffix }

/// <summary>What to do with risk-based Conditional Access policies when the destination lacks Entra ID P2.</summary>
public enum LicenseChoice { CreateDisabled, Skip }

public sealed record PreflightChoices
{
    public ConflictChoice NameConflicts { get; init; } = ConflictChoice.Skip;
    public LicenseChoice RiskPoliciesWithoutP2 { get; init; } = LicenseChoice.CreateDisabled;

    /// <summary>Deploy the policies that can be created and leave the blocked ones out, instead of fixing every blocker first.</summary>
    public bool SkipBlocked { get; init; }
}

/// <summary>Which choice a finding lets the admin make on the Pre-flight screen.</summary>
public enum FindingChoice { None, NameConflicts, RiskPoliciesWithoutP2 }

public sealed record Finding(Severity Severity, string Title, string Detail, IReadOnlyList<string> Items)
{
    public FindingChoice Choice { get; init; } = FindingChoice.None;
}

public enum Outcome
{
    /// <summary>Will be created in the destination.</summary>
    Create,

    /// <summary>An existing destination object with the same name will be used instead (named locations, filters, …).</summary>
    UseExisting,

    /// <summary>Left out by the admin's choice, for example because the name is taken.</summary>
    Skip,

    /// <summary>Can't be created until a blocker is fixed.</summary>
    Blocked,
}

public sealed record PolicyResult(TransformedPolicy Policy, Outcome Outcome, IReadOnlyList<string> Reasons)
{
    /// <summary>For settings: the destination's values now, which deploying changes.</summary>
    public JsonObject? Current { get; init; }

    /// <summary>For settings: the ones whose value differs in the destination, and so will be changed.</summary>
    public IReadOnlyList<string> ChangedSettings { get; init; } = [];

    public ExportedResource Source => Policy.Source;
    public string Key => MappingPlan.KeyOf(Source.Type.Id, Source.SourceId);
}

public sealed record PreflightReport(IReadOnlyList<PolicyResult> Policies, IReadOnlyList<Finding> Findings)
{
    public int CreateCount => Policies.Count(p => p.Outcome == Outcome.Create);
    public int SkipCount => Policies.Count(p => p.Outcome is Outcome.Skip or Outcome.UseExisting);
    public int BlockedCount => Policies.Count(p => p.Outcome == Outcome.Blocked);
    public int WarningCount => Findings.Count(f => f.Severity == Severity.Warning);
    public bool HasBlockers => Findings.Any(f => f.Severity == Severity.Blocker);

    /// <summary>The admin chose to deploy the rest and leave the blocked policies out.</summary>
    public bool BlockedSkipped { get; init; }

    public bool CanDeploy => (!HasBlockers || BlockedSkipped) && CreateCount > 0;
}

/// <summary>
/// A dry run against the destination: works out exactly what deploying would create, and everything that
/// would go wrong or needs a decision. Reads the destination only; nothing is changed.
/// </summary>
public static partial class PreflightCheck
{
    // Entra ID P1 or P2 is needed for any Conditional Access; P2 for risk-based conditions; Intune for Intune policies.
    const string EntraP1 = "AAD_PREMIUM";
    const string EntraP2 = "AAD_PREMIUM_P2";
    const string IntunePlan = "INTUNE_A";

    // Defender for Office 365 Plan 1 or Plan 2.
    static readonly string[] DefenderPlans = ["ATP_ENTERPRISE", "THREAT_INTELLIGENCE"];
    const int Parallelism = 4;

    static readonly string[] RiskConditions = ["signInRiskLevels", "userRiskLevels", "servicePrincipalRiskLevels", "insiderRiskLevels"];

    [GeneratedRegex("password|presharedkey|sharedkey|sharedsecret|passphrase", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    public static Task<PreflightReport> RunAsync(
        IGraphReader destination,
        TenantInfo destinationInfo,
        IReadOnlyList<ExportedResource> selected,
        MappingPlan plan,
        PreflightChoices choices,
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        RunAsync(new TenantClients(destination), destinationInfo, selected, plan, choices, progress, ct);

    public static async Task<PreflightReport> RunAsync(
        TenantClients tenant,
        TenantInfo destinationInfo,
        IReadOnlyList<ExportedResource> selected,
        MappingPlan plan,
        PreflightChoices choices,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var destination = tenant.Graph;
        var findings = new List<Finding>();
        var blocked = new Dictionary<string, List<string>>();
        var skipped = new Dictionary<string, List<string>>();
        var useExisting = new HashSet<string>();
        var disable = new HashSet<string>();
        var rename = new HashSet<string>();

        void Add(Dictionary<string, List<string>> into, ExportedResource policy, string reason)
        {
            var key = MappingPlan.KeyOf(policy.Type.Id, policy.SourceId);
            if (!into.TryGetValue(key, out var reasons))
                into[key] = reasons = [];
            reasons.Add(reason);
        }

        // 1. Objects with no match, and matches that no longer exist in the destination.
        progress?.Report("Checking your matches…");
        var unresolved = plan.Items.Where(m => !m.IsResolved).ToList();
        if (unresolved.Count > 0)
        {
            findings.Add(new Finding(Severity.Blocker,
                $"{unresolved.Count} object{Plural(unresolved.Count)} with no match",
                "Go back to Map dependencies and match, create or remove each one.",
                unresolved.Select(Describe).ToList()));
        }

        var gone = await FindMissingMatchesAsync(tenant, plan, ct);
        if (gone.Count > 0)
        {
            findings.Add(new Finding(Severity.Blocker,
                $"{gone.Count} matched object{Plural(gone.Count)} not found in the destination",
                "They may have been deleted, or a CSV import pointed at the wrong ID. Match them again.",
                gone.Select(m => $"{Describe(m)} → {m.Destination?.DisplayName} ({m.Destination?.Id})").ToList()));
        }

        var badReferences = unresolved.Concat(gone).Select(m => (m.TargetType, m.Source.Id)).ToHashSet();
        foreach (var policy in selected.Where(p => p.Dependencies.Any(d => badReferences.Contains((d.TargetType, d.Value)))))
            Add(blocked, policy, "Points at an object with no match in the destination.");

        // 2. Admin roles in the destination.
        foreach (var group in selected
                     .Select(p => (Policy: p, Role: RoleRequirements.MissingFor(p.Type, destinationInfo.RoleTemplateIds)))
                     .Where(p => p.Role is not null)
                     .GroupBy(p => p.Role!))
        {
            var role = group.Key;
            var policies = group.Select(p => p.Policy).ToList();
            findings.Add(new Finding(Severity.Blocker,
                $"Missing admin role: {role}",
                $"{destinationInfo.UserPrincipalName} can't create or change these in {destinationInfo.DisplayName}. Assign {role} (or Global Administrator), then sign in again.",
                policies.Select(p => p.DisplayName).ToList()));
            foreach (var policy in policies)
                Add(blocked, policy, $"Needs the {role} role.");
        }

        // 3. Licenses in the destination.
        var plans = destinationInfo.ServicePlans;
        var conditionalAccess = selected.Where(p => p.Type.Id == ResourceRegistry.ConditionalAccessPolicy).ToList();
        if (conditionalAccess.Count > 0 && !plans.Contains(EntraP1) && !plans.Contains(EntraP2))
        {
            findings.Add(new Finding(Severity.Blocker,
                "No Entra ID P1 or P2 license in the destination",
                "Conditional Access needs Entra ID P1. Add a license that includes it, then run pre-flight again.",
                conditionalAccess.Select(p => p.DisplayName).ToList()));
            foreach (var policy in conditionalAccess)
                Add(blocked, policy, "Conditional Access needs Entra ID P1.");
        }
        else
        {
            var riskBased = conditionalAccess.Where(UsesRisk).ToList();
            if (riskBased.Count > 0 && !plans.Contains(EntraP2))
            {
                var skip = choices.RiskPoliciesWithoutP2 == LicenseChoice.Skip;
                findings.Add(new Finding(Severity.Warning,
                    $"License gap · {riskBased.Count} risk-based polic{(riskBased.Count == 1 ? "y" : "ies")}",
                    $"Sign-in and user risk conditions need Entra ID P2, which {destinationInfo.DisplayName} doesn't have. " +
                    (skip ? "They'll be skipped." : "They'll be created switched off."),
                    riskBased.Select(p => p.DisplayName).ToList()) { Choice = FindingChoice.RiskPoliciesWithoutP2 });
                foreach (var policy in riskBased)
                {
                    if (skip) Add(skipped, policy, "Needs Entra ID P2.");
                    else disable.Add(MappingPlan.KeyOf(policy.Type.Id, policy.SourceId));
                }
            }
        }

        var intune = selected.Where(p => p.Type.Service == M365Service.Intune).ToList();
        if (intune.Count > 0 && !plans.Contains(IntunePlan))
        {
            findings.Add(new Finding(Severity.Blocker,
                "No Intune license in the destination",
                "Intune policies can't be created without an Intune license. Add one, then run pre-flight again.",
                intune.Select(p => p.DisplayName).ToList()));
            foreach (var policy in intune)
                Add(blocked, policy, "Needs an Intune license.");
        }

        // Items that can't exist in this destination at all, such as a cross-tenant partner that is the destination itself.
        foreach (var group in selected
                     .Select(p => (Policy: p, Reason: p.Type.BlockWhen?.Invoke(p.Settings, destinationInfo)))
                     .Where(p => p.Reason is not null)
                     .GroupBy(p => p.Policy.Type))
        {
            findings.Add(new Finding(Severity.Blocker,
                $"{group.Key.PluralName} that can't be created in {destinationInfo.DisplayName}",
                string.Join(" ", group.Select(p => p.Reason).Distinct()),
                group.Select(p => p.Policy.DisplayName).ToList()));
            foreach (var (policy, reason) in group)
                Add(blocked, policy, reason!);
        }

        var needsDefender = selected.Where(p => p.Type.Id is ResourceRegistry.SafeLinksPolicy or ResourceRegistry.SafeAttachmentPolicy
            or ResourceRegistry.AtpSettings or ResourceRegistry.StandardPresetMdo or ResourceRegistry.StrictPresetMdo).ToList();
        if (needsDefender.Count > 0 && !DefenderPlans.Any(plans.Contains))
        {
            findings.Add(new Finding(Severity.Blocker,
                "No Defender for Office 365 license in the destination",
                "Safe Links, Safe Attachments and the Defender for Office 365 preset protection need Defender for Office 365 Plan 1 or 2. Add a license that includes it, then run pre-flight again.",
                needsDefender.Select(p => p.DisplayName).ToList()));
            foreach (var policy in needsDefender)
                Add(blocked, policy, "Needs Defender for Office 365.");
        }

        // 4. Names already taken in the destination: policies, and their rules (which must be unique too).
        progress?.Report("Checking names in the destination…");
        var conflicts = new List<ExportedResource>();
        var ruleConflicts = new HashSet<ExportedResource>();
        var takenNames = new Dictionary<string, HashSet<string>>();
        var takenRuleNames = new Dictionary<string, HashSet<string>>();
        // Policies already blocked (no license, say) can't be created anyway, so their names don't matter.
        // Settings aren't created, so they have no name to clash; they're compared with the destination's below.
        foreach (var group in selected
                     .Where(p => !p.Type.IsSettings && !blocked.ContainsKey(MappingPlan.KeyOf(p.Type.Id, p.SourceId)))
                     .GroupBy(p => p.Type))
        {
            var type = group.Key;
            IReadOnlyList<JsonObject> existing;
            try
            {
                existing = await ResourceReader.ListAsync(tenant, type, ct);
            }
            catch (PowerShellException ex) when (ex.IsMissingCommand)
            {
                // The destination doesn't offer the cmdlets: the service isn't licensed there, or the account has no role for it.
                findings.Add(new Finding(Severity.Blocker,
                    $"{type.PluralName} aren't available in the destination",
                    $"{destinationInfo.UserPrincipalName} can't read or create {type.PluralName.ToLowerInvariant()} in {destinationInfo.DisplayName}. " +
                    "The tenant may have no license that includes them, or the account no admin role for them. Add the license or role, sign in again, then run pre-flight again.",
                    group.Select(ShownName).ToList()));
                foreach (var policy in group)
                    Add(blocked, policy, $"{type.PluralName} aren't available in the destination.");
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                findings.Add(new Finding(Severity.Warning, $"Couldn't check names of {type.PluralName.ToLowerInvariant()}", ex.Message, []));
                continue;
            }

            var taken = existing.Select(e => e[type.IdentityProperty]?.GetValue<string>() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            takenNames[type.Id] = taken;

            var takenRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (type.Rules is { } rules)
            {
                try
                {
                    takenRules.UnionWith((await tenant.PowerShellFor(type).InvokeAsync(rules.Get, null, ct))
                        .Select(r => r["Name"] is JsonValue name && name.TryGetValue<string>(out var text) ? text : "")
                        .Where(n => n.Length > 0));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    findings.Add(new Finding(Severity.Warning, $"Couldn't check names of {type.DisplayName.ToLowerInvariant()} rules", ex.Message, []));
                }
            }
            takenRuleNames[type.Id] = takenRules;

            foreach (var policy in group)
            {
                var mapping = plan.Find(type.Id, policy.SourceId);
                if (mapping is { Kind: MatchKind.SameName or MatchKind.Manual })
                {
                    // Another policy points at this object and it was matched to one that exists: use that one.
                    useExisting.Add(MappingPlan.KeyOf(type.Id, policy.SourceId));
                }
                else if (taken.Contains(policy.DisplayName))
                {
                    conflicts.Add(policy);
                }
                else if (RuleNames(policy.Settings).Any(takenRules.Contains))
                {
                    conflicts.Add(policy);
                    ruleConflicts.Add(policy);
                }
            }
        }

        if (conflicts.Count > 0)
        {
            var suffix = choices.NameConflicts == ConflictChoice.CreateWithSuffix;
            findings.Add(new Finding(Severity.Warning,
                $"Name conflicts · {conflicts.Count} polic{(conflicts.Count == 1 ? "y" : "ies")}",
                "A policy or rule with the same name already exists in the destination. " +
                (suffix ? "These will be created with \" (migrated)\" added to the name, and to their rules' names." : "These will be skipped, and the existing ones left alone."),
                conflicts.Select(p => $"{p.DisplayName} ({p.Type.DisplayName}{(ruleConflicts.Contains(p) ? ", a rule's name is taken" : "")})").ToList()) { Choice = FindingChoice.NameConflicts });
            foreach (var policy in conflicts)
            {
                if (suffix) rename.Add(MappingPlan.KeyOf(policy.Type.Id, policy.SourceId));
                else Add(skipped, policy, "The name is taken in the destination.");
            }
        }

        // 5. Build every policy as it will be created, and look at the result.
        var options = new TransformOptions { Disable = disable, Rename = rename };
        var transformed = selected.Select(p => Transformer.Transform(p, plan, options)).ToList();

        // The names a renamed policy and its rules end up with must be free too.
        var stillTaken = new List<string>();
        foreach (var t in transformed.Where(t => rename.Contains(MappingPlan.KeyOf(t.Source.Type.Id, t.Source.SourceId))))
        {
            var type = t.Source.Type;
            var name = t.Desired[type.IdentityProperty] is JsonValue value && value.TryGetValue<string>(out var text) ? text : t.Source.DisplayName;
            var ruleTaken = takenRuleNames.TryGetValue(type.Id, out var ruleNames) ? RuleNames(t.Desired).FirstOrDefault(ruleNames.Contains) : null;
            var taken = takenNames.TryGetValue(type.Id, out var names) && names.Contains(name) ? $"\"{name}\""
                : ruleTaken is not null ? $"the rule name \"{ruleTaken}\""
                : null;
            if (taken is null)
                continue;
            stillTaken.Add($"{t.Source.DisplayName} ({type.DisplayName}): {taken} is taken too");
            Add(skipped, t.Source, $"Even with \"{options.RenameSuffix.Trim()}\" added, {taken} is taken in the destination.");
        }
        if (stillTaken.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                "Names still taken after renaming",
                "These can't be created with \" (migrated)\" added either, so they'll be skipped. Rename or remove the existing ones in the destination, then run pre-flight again.",
                stillTaken));
        }

        // Retention policies' rules name the labels they publish or apply by name, and nothing rewrites that name. A label
        // created as "X (migrated)" would leave them pointing at the destination's own "X".
        var renamedLabels = transformed
            .Where(t => t.Source.Type.Id == ResourceRegistry.RetentionLabel && rename.Contains(MappingPlan.KeyOf(t.Source.Type.Id, t.Source.SourceId)))
            .Select(t => t.Source.DisplayName)
            .ToList();
        var namingRenamed = transformed
            .Where(t => t.Source.Type.Id == ResourceRegistry.RetentionPolicy && t.Desired["rules"] is JsonArray)
            .SelectMany(t => renamedLabels
                .Where(label => ((JsonArray)t.Desired["rules"]!).ToJsonString().Contains(label, StringComparison.OrdinalIgnoreCase))
                .Select(label => $"{t.Source.DisplayName}: label \"{label}\""))
            .ToList();
        if (namingRenamed.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                "Retention policies name a renamed label",
                $"These labels are created with \"{options.RenameSuffix.Trim()}\" added, but the retention policies that publish or apply them still name the destination's existing label. Point each policy at the migrated label in the Purview portal after deploying, or rename the existing label in the destination and run pre-flight again.",
                namingRenamed));
        }

        // Settings that exist in every tenant: compared with the destination's, and only what differs is changed.
        var settingsChanges = new Dictionary<string, (JsonObject Current, IReadOnlyList<string> Changed)>();
        var changedSettings = new List<string>();
        foreach (var t in transformed.Where(t => t.Source.Type.IsSettings && !blocked.ContainsKey(MappingPlan.KeyOf(t.Source.Type.Id, t.Source.SourceId))))
        {
            var type = t.Source.Type;
            var key = MappingPlan.KeyOf(type.Id, t.Source.SourceId);
            try
            {
                var found = await ResourceReader.ListAsync(tenant, type, ct);
                if (found.Count == 0)
                {
                    findings.Add(new Finding(Severity.Blocker, $"{type.DisplayName} isn't in the destination", type.Settings!.Missing, [type.DisplayName]));
                    Add(blocked, t.Source, $"{type.DisplayName} isn't in the destination yet.");
                    continue;
                }
                var raw = (JsonObject)found[0].DeepClone();
                type.AfterRead?.Invoke(raw);
                var current = Normalizer.Normalize(raw, type);
                var takes = type.Backend == Backend.Graph ? null : await tenant.PowerShellFor(type).ParametersOfAsync(type.Settings!.Set, ct);
                var changed = SettingsDiff.Changes(type, t.Desired, current, takes);
                if (changed.Count == 0)
                {
                    Add(skipped, t.Source, "Already the same in the destination.");
                    continue;
                }
                settingsChanges[key] = (current, changed);
                changedSettings.Add($"{type.DisplayName}: {string.Join(", ", changed.Take(6))}{(changed.Count > 6 ? $" and {changed.Count - 6} more" : "")}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Without the destination's values there's nothing to compare with, or to put back on rollback.
                findings.Add(new Finding(Severity.Blocker, $"Couldn't read {type.DisplayName.ToLowerInvariant()} in the destination", ex.Message, [type.DisplayName]));
                Add(blocked, t.Source, $"Couldn't read the destination's {type.DisplayName.ToLowerInvariant()}.");
            }
        }
        if (changedSettings.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                "Settings that will be changed in the destination",
                "These exist in every tenant, so the destination's are changed to match the source, not created. Only the settings listed change, and they take effect straight away. " +
                "Rolling back puts the destination's current values back.",
                changedSettings));
        }

        // A sign-in method switched off, or narrowed to some groups, can leave users who rely on it unable to sign in.
        var signIn = transformed
            .Where(t => settingsChanges.TryGetValue(MappingPlan.KeyOf(t.Source.Type.Id, t.Source.SourceId), out var s)
                && t.Source.Type.Id.StartsWith("authMethod", StringComparison.Ordinal)
                && (s.Changed.Contains("state") && t.Desired["state"]?.ToString() == "disabled" || s.Changed.Contains("includeTargets")))
            .Select(t => t.Desired["state"]?.ToString() == "disabled"
                ? $"{t.Source.DisplayName}: switched off"
                : $"{t.Source.DisplayName}: limited to the groups the source uses")
            .ToList();
        if (signIn.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                "Sign-in methods that some users may lose",
                "Users in the destination who sign in, or reset their password, with these methods can lose that way in. " +
                "Check they have another method registered, and keep an emergency access account that doesn't depend on them.",
                signIn));
        }

        // What the rest of the findings describe: only policies that will actually be created.
        var creating = transformed.Where(t =>
        {
            var key = MappingPlan.KeyOf(t.Source.Type.Id, t.Source.SourceId);
            return !skipped.ContainsKey(key) && !useExisting.Contains(key) && !blocked.ContainsKey(key);
        }).ToList();

        var reportOnly = creating.Count(t => t.Changes.Any(c => c.Description.StartsWith("Created in report-only", StringComparison.Ordinal)));
        if (reportOnly > 0)
        {
            findings.Add(new Finding(Severity.Info,
                $"{reportOnly} Conditional Access polic{(reportOnly == 1 ? "y" : "ies")} in report-only mode",
                "Enabled policies are created in report-only mode. Check the sign-in logs, then turn them on.",
                []));
        }

        var testMode = creating
            .Where(t => t.Source.Type.Id != ResourceRegistry.ConditionalAccessPolicy)
            .SelectMany(t => t.Changes.Where(c => c.IsTestMode).Select(c => $"{t.Source.DisplayName}: {c.Description}"))
            .ToList();
        if (testMode.Count > 0)
        {
            findings.Add(new Finding(Severity.Info,
                "Created in test mode or switched off",
                "Mail flow rules, journal rules, DLP, auto-labeling and retention policies are created so they can't block, change, label, copy or delete anything yet. Turn each one on once you've checked it.",
                testMode));
        }

        var withRules = selected.Where(p => p.Type.Rules is not null || p.Type.Id == ResourceRegistry.TransportRule).ToList();
        if (withRules.Count > 0)
        {
            findings.Add(new Finding(Severity.Info,
                "Rule order isn't copied",
                "New rules are added after the destination's existing ones. If the order matters, set the priorities in the destination after deploying.",
                []));
        }

        var live = creating.Where(GoesLive).Select(t => $"{t.Source.DisplayName} ({t.Source.Type.DisplayName})").ToList();
        if (live.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                "Policies that take effect straight away",
                "These can't be created in test mode. Defender policies act on mail as soon as they exist; Intune and Teams policies apply to the groups they're assigned to; " +
                "label policies show their labels, and any mandatory or default label, as soon as users' Office apps pick them up. Deploy them when users are ready.",
                live));
        }

        var emptyExclusions = creating
            .SelectMany(t => t.Changes.Where(c => c.ExcludesNewGroup).Select(c => $"{t.Source.DisplayName}: {c.Description}"))
            .ToList();
        if (emptyExclusions.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                "Exclusions that start empty",
                "These policies exclude groups this migration creates. New groups have no members, so nobody is excluded until you add them. " +
                "Add the members (such as your emergency access accounts) before turning the policies on.",
                emptyExclusions));
        }

        var emptied = transformed
            .Where(t => !skipped.ContainsKey(MappingPlan.KeyOf(t.Source.Type.Id, t.Source.SourceId)))
            .SelectMany(t => t.EmptiedConditions.Select(c => $"{t.Source.DisplayName}: {c}"))
            .ToList();
        if (emptied.Count > 0)
        {
            findings.Add(new Finding(Severity.Blocker,
                "Rules that would apply to all mail",
                "Every recipient, group or domain in one of these rules' conditions was removed. Without them the condition is dropped, and the rule would act on all mail. " +
                "Match at least one of them on the Map screen, or leave the policy out.",
                emptied));
        }

        var teams = selected.Where(p => p.Type.TeamsPolicyType is not null).ToList();
        if (teams.Count > 0)
        {
            var unassigned = teams.Where(p => p.Settings["groupAssignments"] is not JsonArray { Count: > 0 }).Select(p => p.DisplayName).ToList();
            findings.Add(new Finding(Severity.Info,
                "Teams policies apply only to the groups they're assigned to",
                "Group assignments are copied with each policy. Policies assigned to single users aren't: assign them again in the destination. " +
                (unassigned.Count > 0 ? "These have no group assignment, so they won't apply to anyone yet:" : ""),
                unassigned));
        }

        var appPolicies = selected.Where(p => p.Type.Id is ResourceRegistry.TeamsAppSetupPolicy or ResourceRegistry.TeamsAppPermissionPolicy).ToList();
        if (appPolicies.Count > 0)
        {
            findings.Add(new Finding(Severity.Info,
                "Teams apps are referred to by app ID",
                "Apps from the Teams store have the same ID everywhere. Custom apps must be uploaded to the destination first, or they'll be missing from these policies.",
                appPolicies.Select(p => p.DisplayName).ToList()));
        }

        var untranslated = selected
            .SelectMany(p => (p.Settings[SensitivityLabels.NotCopied] as JsonArray ?? []).Select(n => $"{ShownName(p)}: {n}"))
            .ToList();
        if (untranslated.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                "Some label settings aren't copied",
                "Movewise can't set these through New-Label. The labels are created without them; set them in the Purview portal afterwards.",
                untranslated));
        }

        var widened = creating
            .SelectMany(t => t.Changes.Where(c => c.WidensPolicy).Select(c => $"{t.Source.DisplayName}: {c.Description}"))
            .ToList();
        if (widened.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                "Exclusions removed",
                "You chose to remove objects that were excluded. These policies will apply to more people or devices than in the source.",
                widened));
        }

        var noExclusions = creating
            .Where(t => t.Source.Type.Id == ResourceRegistry.ConditionalAccessPolicy && ExcludesNobody(t.Desired))
            .Select(t => t.Source.DisplayName)
            .ToList();
        if (noExclusions.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                "Policies that exclude nobody",
                "These apply to all users with no exclusions. Make sure your emergency access (break-glass) accounts can't be locked out.",
                noExclusions));
        }

        var secrets = creating
            .SelectMany(t => SecretProperties(t.Desired).Select(name => $"{t.Source.DisplayName}: {name}"))
            .ToList();
        if (secrets.Count > 0)
        {
            findings.Add(new Finding(Severity.Warning,
                "Secrets aren't copied",
                "Microsoft Graph doesn't return passwords and pre-shared keys. Enter them again in the destination after deploying.",
                secrets));
        }

        if (selected.Any(p => p.Type.Id == ResourceRegistry.AutopilotProfile))
        {
            findings.Add(new Finding(Severity.Info,
                "Autopilot devices aren't migrated",
                "Autopilot profiles are copied, but device hardware hashes aren't. Register the devices in the destination separately.",
                []));
        }

        // A policy that uses another selected policy that's blocked (a label policy and its label) can't be created either.
        var blockedIds = new HashSet<(string Type, string Id)>();
        bool grew;
        do
        {
            grew = false;
            foreach (var t in transformed)
            {
                var key = MappingPlan.KeyOf(t.Source.Type.Id, t.Source.SourceId);
                if (blocked.ContainsKey(key) || t.Problems.Count > 0)
                    grew |= blockedIds.Add((t.Source.Type.Id, t.Source.SourceId));
            }
            foreach (var t in transformed.Where(t => !blockedIds.Contains((t.Source.Type.Id, t.Source.SourceId))))
            {
                var uses = t.Source.Dependencies.FirstOrDefault(d => blockedIds.Contains((d.TargetType, d.Value)));
                if (uses is null)
                    continue;
                var name = selected.FirstOrDefault(p => p.Type.Id == uses.TargetType && p.SourceId == uses.Value)?.DisplayName ?? uses.Value;
                Add(blocked, t.Source, $"Uses \"{name}\", which is blocked.");
                grew = true;
            }
        } while (grew);

        // 6. One outcome per policy.
        var results = transformed.Select(t =>
        {
            var key = MappingPlan.KeyOf(t.Source.Type.Id, t.Source.SourceId);
            if (blocked.TryGetValue(key, out var blockReasons) || t.Problems.Count > 0)
                return new PolicyResult(t, Outcome.Blocked, [.. blockReasons ?? [], .. t.Problems]);
            if (useExisting.Contains(key))
                return new PolicyResult(t, Outcome.UseExisting, ["An object with this name already exists in the destination and will be used."]);
            if (skipped.TryGetValue(key, out var skipReasons))
                return new PolicyResult(t, Outcome.Skip, skipReasons);
            if (settingsChanges.TryGetValue(key, out var settings))
                return new PolicyResult(t, Outcome.Create, []) { Current = settings.Current, ChangedSettings = settings.Changed };
            return new PolicyResult(t, Outcome.Create, []);
        }).ToList();

        return new PreflightReport(results, findings.OrderBy(f => f.Severity).ToList()) { BlockedSkipped = choices.SkipBlocked };
    }

    /// <summary>Checks that objects the admin matched by hand (or by CSV) still exist in the destination.</summary>
    static async Task<IReadOnlyList<Mapping.Mapping>> FindMissingMatchesAsync(TenantClients tenant, MappingPlan plan, CancellationToken ct)
    {
        var destination = tenant.Graph;
        var manual = plan.Items.Where(m => m.Kind == MatchKind.Manual && m.Destination is not null).ToList();
        var missing = new System.Collections.Concurrent.ConcurrentBag<Mapping.Mapping>();
        var lists = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<JsonObject>>>>();

        await Parallel.ForEachAsync(manual, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (mapping, token) =>
        {
            var id = mapping.Destination!.Id;
            bool exists;
            try
            {
                exists = mapping.TargetType switch
                {
                    ResourceRegistry.Group => await ExistsAsync(destination, GraphQuery.Item("v1.0/groups", id, "id"), token),
                    ResourceRegistry.User => await ExistsAsync(destination, GraphQuery.Item("v1.0/users", id, "id"), token),
                    ResourceRegistry.Domain => (await Matcher.ListDomainsAsync(destination, token))
                        .Any(d => string.Equals(d["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase) && d["isVerified"]?.GetValue<bool>() == true),
                    ResourceRegistry.Recipient => await Matcher.FindRecipientAsync(destination, id, token) is not null,
                    ResourceRegistry.Site => await Matcher.FindSiteAsync(destination, id, token) is not null,
                    ResourceRegistry.Application => ((await destination.GetObjectAsync(
                        GraphQuery.Where("v1.0/servicePrincipals", $"appId eq {GraphQuery.Literal(id)}", "appId", top: 1), token))["value"]?.AsArray().Count ?? 0) > 0,
                    _ => (await lists.GetOrAdd(mapping.TargetType, type => new Lazy<Task<IReadOnlyList<JsonObject>>>(
                            () => ResourceReader.ListAsync(tenant, ResourceRegistry.Get(type), token))).Value)
                        .Any(item => item["id"]?.GetValue<string>() == id),
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                exists = false;
            }

            if (!exists)
                missing.Add(mapping);
        });

        return missing.OrderBy(m => m.Source.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static async Task<bool> ExistsAsync(IGraphReader graph, string path, CancellationToken ct)
    {
        try
        {
            await graph.GetObjectAsync(path, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>The names of a PowerShell policy's rules, as stored with it under "rules".</summary>
    static IEnumerable<string> RuleNames(JsonObject policy) =>
        (policy["rules"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(r => r["Name"] is JsonValue name && name.TryGetValue<string>(out var text) ? text : "")
            .Where(n => n.Length > 0);

    /// <summary>
    /// True for a policy that acts as soon as it's created, because it has no test mode: Defender policies, label
    /// policies, and Intune and Teams policies that are assigned to groups.
    /// </summary>
    static bool GoesLive(TransformedPolicy policy)
    {
        var type = policy.Source.Type;
        // Settings have a finding of their own.
        if (type.IsSettings)
            return false;
        return type.Service == M365Service.Defender
            || type.Id is ResourceRegistry.LabelPolicy or ResourceRegistry.RemoteDomain
            || type.TeamsPolicyType is not null && policy.Desired["groupAssignments"] is JsonArray { Count: > 0 }
            || type.Service == M365Service.Intune && policy.Desired["assignments"] is JsonArray { Count: > 0 };
    }

    static bool UsesRisk(ExportedResource policy) =>
        policy.Settings["conditions"] is JsonObject conditions
        && RiskConditions.Any(name => conditions[name] is JsonArray levels && levels.Count > 0);

    static bool ExcludesNobody(JsonObject policy)
    {
        if (policy["conditions"]?["users"] is not JsonObject users)
            return false;
        var includesAll = users["includeUsers"] is JsonArray include && include.Any(v => v?.GetValue<string>() == "All");
        static bool Empty(JsonNode? node) => node is not JsonArray array || array.Count == 0;
        return includesAll && Empty(users["excludeUsers"]) && Empty(users["excludeGroups"]) && Empty(users["excludeRoles"]);
    }

    /// <summary>Password-like settings that came back empty, anywhere in the policy.</summary>
    static IEnumerable<string> SecretProperties(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj)
                {
                    if (value is null && SecretName().IsMatch(name))
                        yield return name;
                    foreach (var nested in SecretProperties(value))
                        yield return nested;
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    foreach (var nested in SecretProperties(item))
                        yield return nested;
                break;
        }
    }

    // Default sensitivity labels are named with a GUID; their display name is the one admins know.
    public static string ShownName(ExportedResource policy) =>
        policy.Settings["DisplayName"] is JsonValue value && value.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : policy.DisplayName;

    static string Describe(Mapping.Mapping mapping) =>
        $"{MappingNames.Singular(mapping.TargetType)}: {mapping.Source.DisplayName} (used by {mapping.UsedBy.Count})";

    static string Plural(int count) => count == 1 ? "" : "s";
}
