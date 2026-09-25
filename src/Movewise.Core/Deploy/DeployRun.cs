using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Movewise.Core.Export;
using Movewise.Core.Registry;

namespace Movewise.Core.Deploy;

/// <summary>Write access to Microsoft Graph. Only the destination is ever given one.</summary>
public interface IGraphWriter : IGraphReader
{
    Task<JsonObject?> PostAsync(string path, JsonNode body, CancellationToken ct = default);

    /// <summary>Changes the given properties of an existing object, such as a tenant-wide settings object.</summary>
    Task PatchAsync(string path, JsonNode body, CancellationToken ct = default);

    Task DeleteAsync(string path, CancellationToken ct = default);
}

public enum StepStatus
{
    /// <summary>Not started.</summary>
    Pending,

    /// <summary>The create request was sent but no answer came back. On resume, Movewise looks for the object by name first.</summary>
    Creating,

    /// <summary>The object exists; assignments or apps still have to be sent.</summary>
    Created,

    Done,
    Failed,

    /// <summary>Not attempted, because something it needs isn't being created.</summary>
    Skipped,

    RolledBack,
    RollbackFailed,
}

/// <summary>One object to create in the destination: a group, or a policy.</summary>
public sealed class DeployStep
{
    public required string Key { get; init; }
    public required string TargetType { get; init; }
    public required string SourceId { get; init; }

    /// <summary>The name it's created with (which may have "(migrated)" added).</summary>
    public required string DisplayName { get; init; }

    /// <summary>What gets posted, with placeholders for objects this run creates.</summary>
    public required JsonObject Body { get; init; }

    /// <summary>Intune assignments, sent after the policy exists.</summary>
    public JsonArray? Assignments { get; init; }

    /// <summary>Apps an app protection policy covers, sent after the policy exists.</summary>
    public JsonArray? Apps { get; init; }

    /// <summary>Rules of an Exchange, Defender or Purview policy, created after it.</summary>
    public JsonArray? Rules { get; init; }

    /// <summary>Items sent once the object exists, such as an administrative template's settings (see <see cref="ChildItems"/>).</summary>
    public JsonArray? Children { get; init; }

    /// <summary>Names of the rules created so far, so a retry creates only the rest and rollback removes them.</summary>
    public List<string> CreatedRules { get; init; } = [];

    /// <summary>
    /// Assignments made so far, so a retry makes only the rest: the groups a Teams policy is assigned to (which rollback
    /// removes), or the targets of an Autopilot profile's assignments.
    /// </summary>
    public List<string> CreatedAssignments { get; init; } = [];

    /// <summary>True when Movewise created it in test mode, report-only or switched off, so it has to be turned on afterwards.</summary>
    public bool InTestMode { get; init; }

    /// <summary>
    /// For settings: the destination's values of <see cref="Body"/>'s settings just before this run changed them.
    /// Kept before anything is changed, so rolling back can put them back.
    /// </summary>
    public JsonObject? Previous { get; set; }

    [JsonIgnore] public bool IsSettings => !IsGroup && ResourceRegistry.Get(TargetType).IsSettings;

    public StepStatus Status { get; set; }
    public string? DestinationId { get; set; }
    public string? Message { get; set; }

    /// <summary>Follow-up work for the admin, such as adding members to a new group.</summary>
    public List<string> Notes { get; init; } = [];

    public DateTimeOffset? Finished { get; set; }

    [JsonIgnore] public bool IsGroup => TargetType == ResourceRegistry.Group;

    [JsonIgnore] public string TypeName => IsGroup ? "Group" : ResourceRegistry.Get(TargetType).DisplayName;

    /// <summary>True while this run's object is in the destination.</summary>
    [JsonIgnore] public bool ExistsInDestination => DestinationId is not null && Status is not (StepStatus.RolledBack);

    [JsonIgnore] public bool IsFinished => Status is StepStatus.Done or StepStatus.Skipped or StepStatus.RolledBack;

    /// <summary>What deploying again would try: not started, interrupted, or failed. A failed rollback is left for rolling back.</summary>
    [JsonIgnore] public bool IsUnfinished => !IsFinished && Status != StepStatus.RollbackFailed;
}

/// <summary>
/// One deployment: every object to create, in order, and what happened to each. Saved after every step,
/// so an interrupted run can be resumed and a finished one rolled back, even after restarting Movewise.
/// </summary>
public sealed class DeployRun
{
    public required string RunId { get; init; }
    public required DateTimeOffset Started { get; init; }
    public required string SourceTenantId { get; init; }
    public required string SourceName { get; init; }
    public required string DestinationTenantId { get; init; }
    public required string DestinationName { get; init; }
    public required List<DeployStep> Steps { get; init; }

    public DateTimeOffset? Completed { get; set; }
    public DateTimeOffset? RolledBack { get; set; }

    /// <summary>Folder holding run.json and the copy of the destination taken before deploying.</summary>
    [JsonIgnore] public string? Folder { get; set; }

    [JsonIgnore] public int DoneCount => Steps.Count(s => s.Status == StepStatus.Done);
    [JsonIgnore] public int FailedCount => Steps.Count(s => s.Status is StepStatus.Failed or StepStatus.RollbackFailed);
    [JsonIgnore] public int SkippedCount => Steps.Count(s => s.Status == StepStatus.Skipped);
    [JsonIgnore] public int CreatedInDestination => Steps.Count(s => s.ExistsInDestination);

    /// <summary>Steps still to try: not started, interrupted, or failed.</summary>
    [JsonIgnore] public int RemainingCount => RolledBack is null ? Steps.Count(s => s.IsUnfinished) : 0;

    [JsonIgnore] public bool CanRollBack => Steps.Any(s => s.ExistsInDestination || s.Status is StepStatus.Creating or StepStatus.RollbackFailed);
}
