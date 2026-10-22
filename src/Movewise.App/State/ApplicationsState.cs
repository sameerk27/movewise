using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;

namespace Movewise.App.State;

/// <summary>
/// The application migration in progress: its own apps, selection, matches, dry run and run, kept apart from the
/// policy migration's so neither overwrites the other. It uses the tenants signed in on Connect, and drops what it
/// read or matched when they change.
/// </summary>
public sealed class ApplicationsState
{
    TenantConnection? _source;
    TenantConnection? _destination;

    /// <summary>The source's enterprise apps, as last read.</summary>
    public IReadOnlyList<ExportedResource> Apps { get; private set; } = [];

    /// <summary>Apps whose details couldn't be read in the last read.</summary>
    public IReadOnlyList<ExportWarning> Warnings { get; private set; } = [];

    /// <summary>True once the apps have been read from the current source.</summary>
    public bool IsRead { get; private set; }

    /// <summary>Keys (see <see cref="MigrationState.Key"/>) of the apps chosen for migrating.</summary>
    public HashSet<string> Selected { get; } = [];

    public IReadOnlyList<ExportedResource> SelectedApps => Apps.Where(a => Selected.Contains(MigrationState.Key(a))).ToList();

    /// <summary>How each user and group the chosen apps are assigned to (or owned by) maps to the destination.</summary>
    public MappingPlan? Mapping { get; private set; }

    string? _mappingSelection;

    /// <summary>False when the selection changed since the mapping was built, so it has to be matched again.</summary>
    public bool MappingIsCurrent => Mapping is not null && _mappingSelection == SelectionSignature();

    /// <summary>The last dry run against the destination. Run again whenever the Pre-flight screen opens.</summary>
    public AppPreflightReport? Preflight { get; private set; }

    public AppPreflightChoices Choices { get; set; } = new();

    /// <summary>The app deployment running now, or the last one finished or opened.</summary>
    public DeployRun? Run { get; private set; }

    public event Action? Changed;

    public void SetApps(ExportResult result)
    {
        Apps = result.Items;
        Warnings = result.Warnings;
        IsRead = true;
        Selected.Clear();
        Selected.UnionWith(Apps.Select(MigrationState.Key));
        Mapping = null;
        _mappingSelection = null;
        Preflight = null;
        Changed?.Invoke();
    }

    public void SetMapping(MappingPlan plan)
    {
        Mapping = plan;
        _mappingSelection = SelectionSignature();
        Changed?.Invoke();
    }

    /// <summary>Null while a dry run is under way, so a failed one doesn't leave the previous report on show.</summary>
    public void SetPreflight(AppPreflightReport? report)
    {
        Preflight = report;
        Changed?.Invoke();
    }

    public void SetRun(DeployRun? run)
    {
        Run = run;
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>
    /// Keeps up with the tenants signed in on Connect: a new source drops the apps read from the old one, and a new
    /// destination drops matches, the dry run and a run that was for another tenant.
    /// </summary>
    public void Follow(MigrationState migration)
    {
        var sourceChanged = !ReferenceEquals(_source, migration.Source);
        var destinationChanged = !ReferenceEquals(_destination, migration.Destination);
        if (!sourceChanged && !destinationChanged)
            return;

        if (sourceChanged)
        {
            Apps = [];
            Warnings = [];
            IsRead = false;
            Selected.Clear();
        }
        Mapping = null;
        _mappingSelection = null;
        Preflight = null;
        if (destinationChanged)
        {
            Choices = new AppPreflightChoices();
            if (Run is not null && Run.DestinationTenantId != migration.Destination?.Info.TenantId)
                Run = null;
        }
        _source = migration.Source;
        _destination = migration.Destination;
        Changed?.Invoke();
    }

    string SelectionSignature() => string.Join("|", Selected.Order(StringComparer.Ordinal));
}
