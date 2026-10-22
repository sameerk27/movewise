using System.Text.Json.Nodes;
using Movewise.Core.Export;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.Core.Deploy;

/// <summary>
/// Migrates enterprise apps: creates each app's service principal (or updates the destination's), then adds its
/// owners and assignments, and can take all of it out again. Only users and groups are given access to apps:
/// API permissions are never granted, whatever the source had.
/// </summary>
public static class AppDeployer
{
    const string ServicePrincipals = "v1.0/servicePrincipals";

    // Recorded in a step's CreatedRules once its app exists (or has been updated), so a retry goes on to its access.
    const string AppDone = "app";
    const string OwnerPrefix = "owner:";
    const string AssignmentPrefix = "assignment:";

    /// <summary>The run for a checked app migration: one step per app to create or update, in name order.</summary>
    public static DeployRun Plan(AppPreflightReport report, TenantInfo source, TenantInfo destination)
    {
        var steps = report.Apps
            .Where(a => a.Outcome == Outcome.Create)
            .OrderBy(a => a.Source.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(a =>
            {
                var settings = a.IsUpdate ? a.ChangedSettings : AppPreflight.CopiedSettings.Where(a.Desired.ContainsKey);
                var body = SettingsDiff.Only(a.Desired, settings);
                if (!a.IsUpdate)
                    body["appId"] = a.AppId;
                return new DeployStep
                {
                    Key = a.Key,
                    TargetType = ResourceRegistry.EnterpriseApp,
                    SourceId = a.Source.SourceId,
                    DisplayName = a.Source.DisplayName,
                    Body = body,
                    Assignments = (JsonArray)a.Assignments.DeepClone(),
                    Owners = (JsonArray)a.Owners.DeepClone(),
                    UpdateOf = a.IsUpdate ? a.ExistingId : null,
                };
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
    /// Creates or updates every app that isn't finished, then gives each its owners and assignments, so every app
    /// exists before anyone is given access. Safe to call again: each app, owner and assignment is recorded once
    /// made, an interrupted create is looked up by app ID first, and a step that fails doesn't stop the rest.
    /// </summary>
    /// <param name="ct">Stops between apps, owners and assignments; the request in progress is always finished first.</param>
    public static async Task RunAsync(IGraphWriter graph, DeployRun run, Func<Task> save, Action<DeployStep>? changed = null, CancellationToken ct = default)
    {
        if (run.RolledBack is not null)
            throw new InvalidOperationException("This run was rolled back. Start a new deployment instead.");
        if (run.Kind != RunKind.Applications)
            throw new InvalidOperationException("This run migrates policies. Open it in Migrations.");

        foreach (var step in run.Steps.Where(s => s.IsUnfinished && !s.CreatedRules.Contains(AppDone)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            await AppAsync(graph, step, Saver(step, save, changed));
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

    /// <summary>Creates the app's service principal, or changes the existing one's settings.</summary>
    static async Task AppAsync(IGraphWriter graph, DeployStep step, Func<Task> save)
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
                    step.DestinationId = existingId;
                }
                await Finish(step, StepStatus.Creating, null, save);
                if (step.Body.Count > 0)
                    await graph.PatchAsync(ItemPath(existingId), step.Body.DeepClone(), none);
                step.CreatedRules.Add(AppDone);
                await Finish(step, StepStatus.Created, null, save);
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
            await Finish(step, StepStatus.Failed, step.UpdateOf is null ? ex.Message : $"Not updated: {ex.Message}", save);
        }
    }

    /// <summary>
    /// Adds the app's owners, then its assignments, each recorded once made. One found already there is taken as
    /// this run's own only for a new app, or when this run sent it without hearing back; otherwise it was there
    /// before, and rollback leaves it.
    /// </summary>
    static async Task AccessAsync(IGraphWriter graph, DeployStep step, Func<Task> save, CancellationToken ct)
    {
        var none = CancellationToken.None;
        var id = step.DestinationId!;
        var isNew = step.UpdateOf is null;
        try
        {
            var owners = AppPreflight.Strings(step.Owners);
            if (owners.Any(o => !Recorded(step, OwnerPrefix + o)))
            {
                var present = (await graph.GetCollectionAsync($"{ItemPath(id)}/owners?$select=id", none))
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
                        await graph.PostAsync($"{ItemPath(id)}/owners/$ref",
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
                var present = (await graph.GetCollectionAsync($"{ItemPath(id)}/appRoleAssignedTo", none))
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
                        var created = await graph.PostAsync($"{ItemPath(id)}/appRoleAssignedTo", new JsonObject
                        {
                            ["principalId"] = assignment["principalId"]?.DeepClone(),
                            ["resourceId"] = id,
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
    /// Takes out what the run made, newest first: apps it created are deleted (Entra ID keeps deleted apps for 30 days),
    /// and apps it updated get their settings put back and lose only the owners and assignments it added.
    /// </summary>
    public static async Task RollbackAsync(IGraphWriter graph, DeployRun run, Func<Task> save, Action<DeployStep>? changed = null, CancellationToken ct = default)
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
                            ? $"{ItemPath(updatedId)}/owners/{Uri.EscapeDataString(entry[OwnerPrefix.Length..])}/$ref"
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
                    step.DestinationId = await FindAsync(graph, step.Body["appId"]?.GetValue<string>() ?? "", none);
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
                // Its owners and assignments go with it.
                await graph.DeleteAsync(ItemPath(step.DestinationId), none);
                await Finish(step, StepStatus.RolledBack, "Deleted from the destination, with its owners and assignments. Entra ID keeps it in deleted apps for 30 days.", stepSave);
            }
            catch (Exception ex) when (Deployer.IsNotFound(ex))
            {
                await Finish(step, StepStatus.RolledBack, "Already gone from the destination.", stepSave);
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

    static bool Recorded(DeployStep step, string key) =>
        step.CreatedAssignments.Any(a => a.Equals(key, StringComparison.OrdinalIgnoreCase) || a.StartsWith(key + "|", StringComparison.OrdinalIgnoreCase));

    static string Who(JsonObject assignment) =>
        $"{assignment["principalId"]?.GetValue<string>()}|{assignment["appRoleId"]?.GetValue<string>()}";

    static string ItemPath(string id) => $"{ServicePrincipals}/{Uri.EscapeDataString(id)}";
}
