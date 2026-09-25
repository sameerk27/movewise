using System.Net;
using System.Text.Json.Nodes;
using Movewise.Core.Export;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;

namespace Movewise.Core.Deploy;

/// <summary>
/// Creates a run's objects in the destination, one at a time and in order, and can take them out again.
/// Only ever deletes objects this run created; anything that was in the destination before is left alone.
/// </summary>
public static class Deployer
{
    public static Task RunAsync(IGraphWriter graph, DeployRun run, Func<Task> save, Action<DeployStep>? changed = null, CancellationToken ct = default) =>
        RunAsync(new TenantClients(graph), run, save, changed, ct);

    /// <summary>
    /// Creates every step that isn't finished. Safe to call again on the same run: finished steps are skipped,
    /// objects that exist only get what's still missing (assignments, apps, rules), and an interrupted create
    /// is looked up by name before retrying. A step that fails doesn't stop the rest.
    /// </summary>
    /// <param name="save">Called after every change, so the run on disk always matches the destination.</param>
    /// <param name="changed">Called after every change, for the screen.</param>
    /// <param name="ct">
    /// Stops the run between objects. The object in progress is always finished first (created, assigned, its rules
    /// added), so nothing is left half made; that's why the token isn't passed on to the requests themselves.
    /// </param>
    public static async Task RunAsync(TenantClients tenant, DeployRun run, Func<Task> save, Action<DeployStep>? changed = null, CancellationToken ct = default)
    {
        if (run.RolledBack is not null)
            throw new InvalidOperationException("This run was rolled back. Start a new deployment instead.");

        foreach (var step in run.Steps.Where(s => s.IsUnfinished).ToList())
        {
            ct.ThrowIfCancellationRequested();
            await RunStepAsync(tenant, run, step, save, changed, CancellationToken.None);
        }

        run.Completed = DateTimeOffset.Now;
        await save();
    }

    static async Task RunStepAsync(TenantClients tenant, DeployRun run, DeployStep step, Func<Task> save, Action<DeployStep>? changed, CancellationToken ct)
    {
        async Task Update(StepStatus status, string? message = null)
        {
            step.Status = status;
            step.Message = message;
            step.WaitsOnFailedStep = false;
            if (status is StepStatus.Done or StepStatus.Failed or StepStatus.Skipped)
                step.Finished = DateTimeOffset.Now;
            await save();
            changed?.Invoke(step);
        }

        // Kept for the next attempt, when what it needs may have been created.
        async Task Skip(List<string> missing)
        {
            step.Status = StepStatus.Skipped;
            step.Message = $"Needs {DeployPlanner.Names(missing, run.Steps, null)}, which couldn't be created.";
            step.WaitsOnFailedStep = true;
            step.Finished = DateTimeOffset.Now;
            await save();
            changed?.Invoke(step);
        }

        // Leaves the step as Creating, with what the admin should know: the next attempt checks the destination again.
        async Task StillUnknown(string message)
        {
            step.Message = message;
            await save();
            changed?.Invoke(step);
        }

        var isRetry = step.Status != StepStatus.Pending;
        var wasInterrupted = step.DestinationId is null && step.Status == StepStatus.Creating;
        try
        {
            if (step.IsSettings)
            {
                var type = ResourceRegistry.Get(step.TargetType);
                var values = (JsonObject)step.Body.DeepClone();
                var missing = Resolve(values, run);
                if (missing.Count > 0)
                {
                    await Skip(missing);
                    return;
                }

                // The destination's values are kept before anything changes, so rolling back can put them back.
                // A retry keeps the first ones: by then some settings may already have been changed.
                if (step.Previous is null)
                {
                    var found = await ResourceReader.ListAsync(tenant, type, ct);
                    if (found.Count == 0)
                    {
                        await Update(StepStatus.Failed, $"{type.DisplayName} isn't in the destination. {type.Settings!.Missing}");
                        return;
                    }
                    var raw = (JsonObject)found[0].DeepClone();
                    step.DestinationId = type.Backend == Backend.Graph ? type.Settings!.Set : Text(raw, "Identity") ?? Text(raw, "Name") ?? Text(raw, "id");
                    type.AfterRead?.Invoke(raw);
                    step.Previous = SettingsDiff.Only(Normalizer.Normalize(raw, type), values.Select(p => p.Key));
                }
                await Update(StepStatus.Creating);

                await SetSettingsAsync(tenant, type, step, step.DestinationId!, values, ct);
                await Update(StepStatus.Done, $"Changed: {string.Join(", ", values.Select(p => p.Key))}.");
                return;
            }

            // An earlier attempt sent the create request but never heard back: the object may exist. It's only
            // created again when the destination clearly doesn't have it.
            if (wasInterrupted)
            {
                var lookup = await FindByNameAsync(tenant, step, ct);
                if (lookup.Kind == LookupKind.Found)
                {
                    Adopt(step, lookup.Id!);
                }
                else if (lookup.Kind != LookupKind.NotFound)
                {
                    await StillUnknown(lookup.Describe());
                    return;
                }
            }

            if (step.DestinationId is null)
            {
                var body = (JsonObject)step.Body.DeepClone();
                var missing = Resolve(body, run);
                if (missing.Count > 0)
                {
                    await Skip(missing);
                    return;
                }
                var type = step.IsGroup ? null : ResourceRegistry.Get(step.TargetType);
                type?.PrepareForCreate?.Invoke(body);

                step.CreateSent = DateTimeOffset.UtcNow;
                await Update(StepStatus.Creating);
                try
                {
                    step.DestinationId = type is { UsesPowerShell: true }
                        ? await CreateWithPowerShellAsync(tenant.PowerShellFor(type), type, step, body, ct)
                        : await CreateWithGraphAsync(tenant.WriterFor(type), step, body, ct);
                }
                catch (Exception ex) when (IsUncertain(ex, ct))
                {
                    // Leave it as Creating: the next attempt checks the destination before creating again.
                    await StillUnknown($"No answer from Microsoft 365, so it may or may not have been created. Retry to check. ({ex.Message})");
                    return;
                }
                catch (PowerShellException ex) when (wasInterrupted && ex.IsAlreadyExists)
                {
                    // The interrupted attempt did create it, and it has only now become visible.
                    var again = await FindByNameAsync(tenant, step, ct);
                    if (again.Kind != LookupKind.Found)
                    {
                        await StillUnknown($"It already exists in the destination, but Movewise couldn't find it by name to use it. Retry to check again. ({ex.Message})");
                        return;
                    }
                    Adopt(step, again.Id!);
                }
                await Update(StepStatus.Created);
            }

            var failure = await AfterCreateAsync(tenant, run, step, isRetry, save, ct);
            if (failure is null)
                await Update(StepStatus.Done);
            else
                await Update(StepStatus.Failed, failure);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await Update(StepStatus.Failed, step.DestinationId is null
                ? ex.Message
                : $"Created, but something after that failed: {ex.Message}");
        }
    }

    static async Task<string> CreateWithGraphAsync(IGraphWriter graph, DeployStep step, JsonObject body, CancellationToken ct)
    {
        var created = await graph.PostAsync(CreatePath(step), body, ct);
        var idProperty = step.IsGroup ? "id" : ResourceRegistry.Get(step.TargetType).IdProperty;
        return created?[idProperty]?.GetValue<string>()
            ?? throw new InvalidOperationException("Microsoft Graph didn't return the new object's ID.");
    }

    /// <summary>
    /// Runs the New cmdlet with the settings it accepts. Returns the new policy's GUID, or its name when
    /// the cmdlet doesn't say.
    /// </summary>
    static async Task<string> CreateWithPowerShellAsync(IPowerShell shell, ResourceType type, DeployStep step, JsonObject body, CancellationToken ct)
    {
        var accepted = await shell.ParametersOfAsync(type.Cmdlets!.New, ct);
        var (parameters, notCopied) = PowerShellShape.ToParameters(body, accepted, type.KeepsPriority);
        // Settings that were already known not to translate when the policy was read (sensitivity labels).
        if (body[SensitivityLabels.NotCopied] is JsonArray known)
            notCopied.AddRange(known.Select(n => n?.ToString()).OfType<string>());
        // What a Set cmdlet takes afterwards isn't lost, even though New doesn't take it.
        if (type.Cmdlets.Set is { } set)
        {
            var later = await shell.ParametersOfAsync(set, ct);
            notCopied.RemoveAll(later.Contains);
        }
        NoteNotCopied(step, notCopied, null);
        if (type.Cmdlets.NameParameter is { } nameParameter)
            parameters[nameParameter] = step.DisplayName;
        foreach (var (name, value) in type.Cmdlets.Scope ?? new Dictionary<string, string>())
            parameters[name] = value;

        var output = await shell.InvokeAsync(type.Cmdlets.New, parameters, ct);
        if (type.IdIsName)
            return step.DisplayName;
        return output.Count > 0 && PowerShellShape.Clean(output[0])["id"]?.GetValue<string>() is { Length: > 0 } id
            ? id
            : step.DisplayName;
    }

    /// <summary>
    /// Changes existing settings to the given values: with PATCH in Graph, or through Set, and through the Enable or
    /// Disable cmdlet where Set can't switch them. A value that's empty clears the setting, which is how rollback
    /// empties a list again.
    /// </summary>
    static async Task SetSettingsAsync(TenantClients tenant, ResourceType type, DeployStep step, string identity, JsonObject values, CancellationToken ct)
    {
        var settings = type.Settings!;
        values = (JsonObject)values.DeepClone();

        if (type.Backend == Backend.Graph)
        {
            // Some settings objects need their type named in the request (authentication method configurations).
            type.PrepareForCreate?.Invoke(values);
            await tenant.GraphWriter.PatchAsync(identity, values, ct);
            return;
        }

        var shell = tenant.PowerShellFor(type);
        bool? enabled = null;
        if (settings.Switch is not null && values["Enabled"] is JsonValue state && state.TryGetValue<bool>(out var on))
        {
            enabled = on;
            values.Remove("Enabled");
        }

        var takes = await shell.ParametersOfAsync(settings.Set, ct);
        var (parameters, notCopied) = PowerShellShape.ToParameters(values, takes);
        foreach (var (name, value) in values)
        {
            if (!parameters.ContainsKey(name) && takes.Contains(name) && IsEmpty(value))
                parameters[name] = null;
        }
        NoteNotCopied(step, notCopied, null);

        if (parameters.Count > 0)
        {
            if (settings.SetTakesIdentity)
                parameters["Identity"] = identity;
            await shell.InvokeAsync(settings.Set, parameters, ct);
        }
        if (enabled is { } turnOn && settings.Switch is var (enable, disable))
            await shell.InvokeAsync(turnOn ? enable : disable, new Dictionary<string, JsonNode?> { ["Identity"] = identity }, ct);

        static bool IsEmpty(JsonNode? value) =>
            value is null || value is JsonArray { Count: 0 } || value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0;
    }

    /// <summary>
    /// The settings New didn't take, through the type's Set cmdlet. Safe to run again: it sets the same values.
    /// Returns what went wrong, or null.
    /// </summary>
    static async Task<string?> SetAfterCreateAsync(IPowerShell shell, ResourceType type, DeployRun run, DeployStep step, CancellationToken ct)
    {
        var cmdlets = type.Cmdlets!;
        var body = (JsonObject)step.Body.DeepClone();
        if (Resolve(body, run) is { Count: > 0 } missing)
            return $"Created, but its settings couldn't be set: it needs {DeployPlanner.Names(missing, run.Steps, null)}, which couldn't be created.";
        type.PrepareForCreate?.Invoke(body);

        var takenByNew = await shell.ParametersOfAsync(cmdlets.New, ct);
        var accepted = (await shell.ParametersOfAsync(cmdlets.Set!, ct))
            .Where(p => !takenByNew.Contains(p) && !p.Equals("Name", StringComparison.OrdinalIgnoreCase) && !p.Equals("Identity", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (parameters, notCopied) = PowerShellShape.ToParameters(body, accepted);
        NoteNotCopied(step, notCopied, null);
        if (parameters.Count == 0)
            return null;

        parameters["Identity"] = step.DestinationId;
        try
        {
            await shell.InvokeAsync(cmdlets.Set!, parameters, ct);
            return null;
        }
        catch (PowerShellException ex)
        {
            return $"Created, but its settings couldn't be set: {ex.Message}";
        }
    }

    /// <summary>Sends what has to follow an object that now exists. Returns what went wrong, or null.</summary>
    static async Task<string?> AfterCreateAsync(TenantClients tenant, DeployRun run, DeployStep step, bool isRetry, Func<Task> save, CancellationToken ct)
    {
        if (step.IsGroup)
            return null;
        var type = ResourceRegistry.Get(step.TargetType);

        if (type.Cmdlets?.Set is not null && await SetAfterCreateAsync(tenant.PowerShellFor(type), type, run, step, ct) is { } setFailure)
            return setFailure;
        if (type.Rules is not null && step.Rules is { Count: > 0 })
            return await CreateRulesAsync(tenant.PowerShellFor(type), type, step, isRetry, save, ct);
        if (type.TeamsPolicyType is not null && step.Assignments is { Count: > 0 })
            return await AssignTeamsPolicyAsync(tenant.PowerShellFor(type), type, run, step, isRetry, save, ct);
        if (type.UsesPowerShell)
            return null;

        var graph = tenant.WriterFor(type);
        var id = step.DestinationId!;

        // Items that belong to it, one request each; each is recorded once sent, so a retry sends only the rest.
        if (step.Children is { Count: > 0 } && type.Children is { } childItems)
        {
            var children = (JsonArray)step.Children.DeepClone();
            if (Resolve(children, run) is { Count: > 0 } missingChildren)
                return $"Created, but its settings couldn't be added: {DeployPlanner.Names(missingChildren, run.Steps, null)} couldn't be created.";
            var requests = childItems.Requests(children);
            for (var i = 0; i < requests.Count; i++)
            {
                var key = $"child:{i}";
                if (step.CreatedRules.Contains(key))
                    continue;
                try
                {
                    await graph.PostAsync(Expand(requests[i].Path, id), requests[i].Body.DeepClone(), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return $"Created, but its settings couldn't all be added: {ex.Message}";
                }
                step.CreatedRules.Add(key);
                await save();
            }
        }

        if (step.Apps is { Count: > 0 } && type.TargetAppsPath is not null)
        {
            try
            {
                await graph.PostAsync(Expand(type.TargetAppsPath, id), new JsonObject { ["apps"] = step.Apps.DeepClone() }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return $"Created, but its apps couldn't be set: {ex.Message}";
            }
        }

        if (step.Assignments is { Count: > 0 } && type.AssignPath is not null)
        {
            var assignments = (JsonArray)step.Assignments.DeepClone();
            var missing = Resolve(assignments, run);
            if (missing.Count > 0)
                return $"Created, but not assigned: {DeployPlanner.Names(missing, run.Steps, null)} couldn't be created.";

            try
            {
                var path = Expand(type.AssignPath, id);
                if (type.AssignOneByOne)
                {
                    // Each one is recorded once it's made, so a retry only posts the rest.
                    foreach (var assignment in assignments.OfType<JsonObject>())
                    {
                        var target = assignment["target"]?.ToJsonString() ?? assignment.ToJsonString();
                        if (step.CreatedAssignments.Contains(target))
                            continue;
                        await graph.PostAsync(path, assignment.DeepClone(), ct);
                        step.CreatedAssignments.Add(target);
                        await save();
                    }
                }
                else
                {
                    await graph.PostAsync(path, new JsonObject { [type.AssignBodyProperty] = assignments }, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return $"Created, but it couldn't be assigned: {ex.Message}";
            }
        }

        return null;
    }

    /// <summary>
    /// Creates the policy's rules that don't exist yet, each pointing at the new policy. On a retry, a rule that
    /// "already exists" is taken to be the one the interrupted attempt created.
    /// </summary>
    static async Task<string?> CreateRulesAsync(IPowerShell shell, ResourceType type, DeployStep step, bool isRetry, Func<Task> save, CancellationToken ct)
    {
        var cmdlets = type.Rules!;
        var accepted = await shell.ParametersOfAsync(cmdlets.New, ct);

        foreach (var rule in step.Rules!.OfType<JsonObject>())
        {
            var name = rule["Name"]?.GetValue<string>() ?? step.DisplayName;
            if (step.CreatedRules.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            var (parameters, notCopied) = PowerShellShape.ToParameters(rule, accepted);
            parameters["Name"] = name;
            parameters[cmdlets.PolicyParameter] = step.DisplayName;
            NoteNotCopied(step, notCopied, name);

            try
            {
                await shell.InvokeAsync(cmdlets.New, parameters, ct);
            }
            catch (PowerShellException ex) when (isRetry && ex.IsAlreadyExists)
            {
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return $"Created, but its rule \"{name}\" couldn't be created: {ex.Message}";
            }

            step.CreatedRules.Add(name);
            await save();
        }
        return null;
    }

    /// <summary>
    /// Assigns a new Teams policy to its groups, in the source's order. On a retry, an assignment that "already exists"
    /// is taken to be the one an earlier attempt made only if that attempt got no answer. Otherwise the group already
    /// had a policy of this type, which isn't this run's, so rollback mustn't remove it.
    /// </summary>
    static async Task<string?> AssignTeamsPolicyAsync(IPowerShell shell, ResourceType type, DeployRun run, DeployStep step, bool isRetry, Func<Task> save, CancellationToken ct)
    {
        var assignments = (JsonArray)step.Assignments!.DeepClone();
        var missing = Resolve(assignments, run);
        if (missing.Count > 0)
            return $"Created, but not assigned: {DeployPlanner.Names(missing, run.Steps, null)} couldn't be created.";

        foreach (var assignment in assignments.OfType<JsonObject>())
        {
            var groupId = assignment["GroupId"]?.GetValue<string>();
            if (groupId is null || step.CreatedAssignments.Contains(groupId, StringComparer.OrdinalIgnoreCase))
                continue;

            var parameters = new Dictionary<string, JsonNode?>
            {
                ["GroupId"] = groupId,
                ["PolicyType"] = type.TeamsPolicyType,
                ["PolicyName"] = step.DisplayName,
            };
            if (assignment["Rank"] is JsonValue rank)
                parameters["Rank"] = rank.DeepClone();

            var sentBefore = isRetry && string.Equals(step.PendingAssignment, groupId, StringComparison.OrdinalIgnoreCase);
            step.PendingAssignment = groupId;
            await save();
            try
            {
                await shell.InvokeAsync("New-CsGroupPolicyAssignment", parameters, ct);
            }
            catch (PowerShellException ex) when (sentBefore && ex.IsAlreadyExists)
            {
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A clear refusal means nothing was assigned; only an unanswered request may have been.
                if (!IsUncertain(ex, ct))
                    step.PendingAssignment = null;
                return $"Created, but it couldn't be assigned to group {groupId}: {ex.Message}";
            }

            step.PendingAssignment = null;
            step.CreatedAssignments.Add(groupId);
            await save();
        }
        return null;
    }

    static void NoteNotCopied(DeployStep step, IReadOnlyList<string> settings, string? rule)
    {
        if (settings.Count == 0)
            return;
        var note = $"Not copied{(rule is null ? "" : $" to the rule \"{rule}\"")}: {string.Join(", ", settings)}. Set {(settings.Count == 1 ? "it" : "them")} in the destination by hand.";
        if (!step.Notes.Contains(note))
            step.Notes.Add(note);
    }

    public static Task RollbackAsync(IGraphWriter graph, DeployRun run, Func<Task> save, Action<DeployStep>? changed = null, CancellationToken ct = default) =>
        RollbackAsync(new TenantClients(graph), run, save, changed, ct);

    /// <summary>
    /// Deletes everything this run created, newest first, so policies go before the groups and locations they use,
    /// and rules before their policies. Groups go to the destination's deleted items, where they can be restored for 30 days.
    /// </summary>
    /// <param name="ct">Stops between objects, like <see cref="RunAsync(TenantClients, DeployRun, Func{Task}, Action{DeployStep}?, CancellationToken)"/>.</param>
    public static async Task RollbackAsync(TenantClients tenant, DeployRun run, Func<Task> save, Action<DeployStep>? changed = null, CancellationToken ct = default)
    {
        foreach (var step in Enumerable.Reverse(run.Steps).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var inFlight = CancellationToken.None;

            // Settings can't be deleted: the destination's earlier values are put back instead.
            if (step.IsSettings)
            {
                if (step.Previous is null || step.Status is StepStatus.Pending or StepStatus.Skipped or StepStatus.RolledBack)
                    continue;
                try
                {
                    var type = ResourceRegistry.Get(step.TargetType);
                    await SetSettingsAsync(tenant, type, step, step.DestinationId!, step.Previous, inFlight);
                    step.Status = StepStatus.RolledBack;
                    step.Message = "Settings put back as they were.";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    step.Status = StepStatus.RollbackFailed;
                    step.Message = $"Couldn't put the settings back: {ex.Message}";
                }
                await save();
                changed?.Invoke(step);
                continue;
            }

            if (step.DestinationId is null && step.Status is StepStatus.Creating or StepStatus.RollbackFailed)
            {
                // An interrupted create: delete it only if it's clearly there, and say so when that can't be told.
                var lookup = await FindByNameAsync(tenant, step, inFlight);
                if (lookup.Kind == LookupKind.Found)
                {
                    step.DestinationId = lookup.Id;
                }
                else
                {
                    step.Status = lookup.Kind == LookupKind.NotFound ? StepStatus.RolledBack : StepStatus.RollbackFailed;
                    step.Message = lookup.Kind == LookupKind.NotFound
                        ? "Not found in the destination, so it was probably never created. Check the admin center to be sure."
                        : $"Check the destination by hand: {lookup.Describe()}";
                    await save();
                    changed?.Invoke(step);
                    continue;
                }
            }
            if (step.DestinationId is null || step.Status == StepStatus.RolledBack)
                continue;

            try
            {
                var type = step.IsGroup ? null : ResourceRegistry.Get(step.TargetType);
                if (type is { UsesPowerShell: true })
                    await RemoveWithPowerShellAsync(tenant.PowerShellFor(type), type, step, save, inFlight);
                else
                    await tenant.WriterFor(type).DeleteAsync(DeletePath(step), inFlight);
                step.Status = StepStatus.RolledBack;
                step.Message = "Deleted from the destination.";
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                step.Status = StepStatus.RolledBack;
                step.Message = "Already gone from the destination.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                step.Status = StepStatus.RollbackFailed;
                step.Message = $"Couldn't delete it: {ex.Message}";
            }

            await save();
            changed?.Invoke(step);
        }

        if (!run.Steps.Any(s => s.Status == StepStatus.RollbackFailed))
            run.RolledBack = DateTimeOffset.Now;
        await save();
    }

    /// <summary>Removes the policy's rules this run created, then the policy.</summary>
    static async Task RemoveWithPowerShellAsync(IPowerShell shell, ResourceType type, DeployStep step, Func<Task> save, CancellationToken ct)
    {
        // A Teams policy can't be removed while it's assigned.
        if (type.TeamsPolicyType is { } policyType)
        {
            foreach (var groupId in step.CreatedAssignments.ToList())
            {
                try
                {
                    await shell.InvokeAsync("Remove-CsGroupPolicyAssignment", new Dictionary<string, JsonNode?>
                    {
                        ["GroupId"] = groupId,
                        ["PolicyType"] = policyType,
                    }, ct);
                }
                catch (PowerShellException ex) when (ex.IsNotFound)
                {
                }
                step.CreatedAssignments.Remove(groupId);
                await save();
            }
        }

        if (type.Rules is { } rules)
        {
            foreach (var name in step.CreatedRules.ToList())
            {
                try
                {
                    await shell.InvokeAsync(rules.Remove, await RemoveParametersAsync(shell, rules.Remove, name, ct), ct);
                }
                catch (PowerShellException ex) when (ex.IsNotFound)
                {
                }
                step.CreatedRules.Remove(name);
                await save();
            }
        }
        var cmdlets = type.Cmdlets!;
        var parameters = await RemoveParametersAsync(shell, cmdlets.Remove, step.DestinationId!, ct, cmdlets.RemoveIdentityParameter);
        foreach (var (name, value) in cmdlets.Scope ?? new Dictionary<string, string>())
            parameters[name] = value;
        await shell.InvokeAsync(cmdlets.Remove, parameters, ct);
    }

    /// <summary>Identity, and no confirmation prompt where the cmdlet would ask for one.</summary>
    static async Task<Dictionary<string, JsonNode?>> RemoveParametersAsync(
        IPowerShell shell, string cmdlet, string identity, CancellationToken ct, string identityParameter = "Identity")
    {
        var parameters = new Dictionary<string, JsonNode?> { [identityParameter] = identity };
        if ((await shell.ParametersOfAsync(cmdlet, ct)).Contains("Confirm"))
            parameters["Confirm"] = false;
        return parameters;
    }

    /// <summary>Swaps placeholders for the IDs of objects this run created. Returns the keys it couldn't resolve.</summary>
    static List<string> Resolve(JsonNode node, DeployRun run)
    {
        // A step that failed after its object was created (a label whose settings didn't all apply, say) still has it.
        var ids = run.Steps.Where(s => s.DestinationId is not null && s.Status is StepStatus.Created or StepStatus.Done or StepStatus.Failed)
            .ToDictionary(s => s.Key, s => s.DestinationId!);
        var missing = new List<string>();
        Walk(node);
        return missing.Distinct().ToList();

        void Walk(JsonNode? current)
        {
            switch (current)
            {
                case JsonObject obj:
                    foreach (var name in obj.Select(p => p.Key).ToList())
                    {
                        if (Swap(obj[name]) is { } id) obj[name] = id;
                        else Walk(obj[name]);
                    }
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (Swap(array[i]) is { } id) array[i] = id;
                        else Walk(array[i]);
                    }
                    break;
            }
        }

        string? Swap(JsonNode? value)
        {
            if (value is not JsonValue v || !v.TryGetValue<string>(out var text) || Transformer.PlaceholderKey(text) is not { } key)
                return null;
            if (ids.TryGetValue(key, out var id))
                return id;
            missing.Add(key);
            return null;
        }
    }

    static void Adopt(DeployStep step, string id)
    {
        step.DestinationId = id;
        step.Notes.Add("Found in the destination after an interrupted run, and used instead of creating it again.");
    }

    enum LookupKind { Found, NotFound, Ambiguous, CouldNotCheck }

    /// <summary>What looking for a step's object by name found. Only <see cref="LookupKind.NotFound"/> means it's safe to create it.</summary>
    readonly record struct Lookup(LookupKind Kind, string? Id = null, string? Problem = null)
    {
        public string Describe() => Kind == LookupKind.Ambiguous
            ? "More than one object in the destination has this name, so Movewise can't tell which one this run created. Delete the extra one in the admin center, then retry."
            : $"Movewise couldn't check whether an earlier attempt created it, so it wasn't created again. Retry to check again. ({Problem})";
    }

    /// <summary>Looks for the object in the destination with this step's name. A failed lookup is never taken to mean "not there".</summary>
    static async Task<Lookup> FindByNameAsync(TenantClients tenant, DeployStep step, CancellationToken ct)
    {
        List<string?> ids;
        try
        {
            if (step.IsGroup)
            {
                var page = await tenant.Graph.GetObjectAsync(GraphQuery.Where("v1.0/groups", $"displayName eq {GraphQuery.Literal(step.DisplayName)}", "id,createdDateTime", top: 10), ct);
                // A group with this name that was there before the request was sent isn't this run's, whatever its name.
                // Five minutes' leeway covers a difference between this PC's clock and Microsoft's.
                var since = step.CreateSent?.AddMinutes(-5);
                ids = (page["value"]?.AsArray().OfType<JsonObject>() ?? [])
                    .Where(g => since is null || g["createdDateTime"] is not JsonValue created
                        || !DateTimeOffset.TryParse(created.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                        || at >= since)
                    .Select(g => g["id"]?.GetValue<string>())
                    .ToList();
            }
            else
            {
                var type = ResourceRegistry.Get(step.TargetType);
                ids = (await ResourceReader.ListAsync(tenant, type, ct))
                    .Where(i => string.Equals(i[type.IdentityProperty]?.GetValue<string>(), step.DisplayName, StringComparison.OrdinalIgnoreCase))
                    .Select(i => i[type.IdProperty]?.GetValue<string>())
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Lookup(LookupKind.CouldNotCheck, Problem: ex.Message);
        }

        return ids.Count switch
        {
            0 => new Lookup(LookupKind.NotFound),
            1 when ids[0] is { Length: > 0 } id => new Lookup(LookupKind.Found, id),
            1 => new Lookup(LookupKind.CouldNotCheck, Problem: "the object found has no ID"),
            _ => new Lookup(LookupKind.Ambiguous),
        };
    }

    /// <summary>
    /// True when the request may have reached Microsoft 365 even though it failed: a timeout, a dropped
    /// connection, or a server error. A clear rejection (400, 403, 409, or a cmdlet error) means nothing was created.
    /// </summary>
    static bool IsUncertain(Exception ex, CancellationToken ct) => ex switch
    {
        HttpRequestException => true,
        TaskCanceledException when !ct.IsCancellationRequested => true,
        GraphException graph => (int)graph.Status >= 500,
        PowerShellException shell => shell.IsUncertain,
        _ => false,
    };

    static bool IsNotFound(Exception ex) => ex switch
    {
        GraphException graph => graph.Status == HttpStatusCode.NotFound,
        PowerShellException shell => shell.IsNotFound,
        _ => false,
    };

    static string CreatePath(DeployStep step) =>
        step.IsGroup ? "v1.0/groups" : ResourceRegistry.Get(step.TargetType).CreatePath;

    static string DeletePath(DeployStep step) => $"{CreatePath(step)}/{Uri.EscapeDataString(step.DestinationId!)}";

    static string Expand(string template, string id) => template.Replace("{id}", Uri.EscapeDataString(id));

    static string? Text(JsonObject item, string name) =>
        item[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
}
