using Microsoft.Identity.Client;
using Movewise.Core.Access;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Registry;
using Movewise.Core.Tenants;

namespace Movewise.App.State;

/// <summary>A restore from a snapshot that pre-flight has checked: what it would create or put back, and the matches it uses.</summary>
public sealed record RestorePlan(Movewise.Core.Compare.Snapshot Snapshot, PreflightReport Report, MappingPlan Mapping);

/// <summary>A signed-in tenant: who signed in, what the tenant is, and whether their roles are enough.</summary>
public sealed class TenantConnection
{
    public required TenantRole Role { get; init; }

    /// <summary>The signed-in account, or null for a demo tenant.</summary>
    public IAccount? Account { get; init; }

    public required TenantInfo Info { get; init; }
    public required RoleCheck Roles { get; init; }

    /// <summary>Graph, and the Exchange Online, Security &amp; Compliance and Teams PowerShell sessions (which connect on first use).</summary>
    public required TenantClients Clients { get; init; }

    /// <summary>True for the built-in sample tenants: nothing leaves the app.</summary>
    public bool IsDemo { get; init; }

    /// <summary>
    /// For a source that's a saved baseline rather than a signed-in tenant: its policies and what they point at come from
    /// the file. There's no account, no session and nothing to read from Microsoft 365.
    /// </summary>
    public Movewise.Core.Baselines.Baseline? Baseline { get; init; }

    public bool IsBaseline => Baseline is not null;

    /// <summary>Sessions to close when the tenant is signed out.</summary>
    public IReadOnlyList<IAsyncDisposable> Sessions { get; init; } = [];

    public IGraphReader Graph => Clients.Graph;

    public async Task CloseAsync()
    {
        foreach (var session in Sessions)
            await session.DisposeAsync();
    }
}

/// <summary>The migration in progress. One source, one destination.</summary>
public sealed class MigrationState
{
    public TenantConnection? Source { get; private set; }
    public TenantConnection? Destination { get; private set; }

    public IReadOnlyList<ExportedResource> Exported { get; private set; } = [];

    /// <summary>Policy types that couldn't be read in the last discovery.</summary>
    public IReadOnlyList<ExportWarning> ExportWarnings { get; private set; } = [];

    public IReadOnlyList<ManualSetupItem> ManualSetup { get; private set; } = [];
    public IReadOnlyList<string> ManualSetupWarnings { get; private set; } = [];

    /// <summary>
    /// The services chosen on Connect. Discovery reads only these. SharePoint starts off, as in the design.
    /// </summary>
    public HashSet<M365Service> Services { get; } =
    [
        M365Service.Entra, M365Service.Intune, M365Service.Purview, M365Service.Defender,
        M365Service.DefenderEndpoint, M365Service.Exchange, M365Service.Teams,
    ];

    /// <summary>The services the last read covered. A service ticked after that read hasn't been read yet.</summary>
    public IReadOnlySet<M365Service> ReadServices { get; private set; } = new HashSet<M365Service>();

    /// <summary>
    /// Unticks a service on Connect. Its policies and hand-setup items are dropped with it, so they can't be selected
    /// or deployed after the service was unticked.
    /// </summary>
    public void RemoveService(M365Service service)
    {
        Services.Remove(service);
        Selected.ExceptWith(Exported.Where(e => e.Type.Service == service).Select(Key).ToList());
        Exported = Exported.Where(e => e.Type.Service != service).ToList();
        ExportWarnings = ExportWarnings.Where(w => w.Type.Service != service).ToList();
        ReadServices = ReadServices.Where(s => s != service).ToHashSet();
        if (service == M365Service.Exchange)
        {
            ManualSetup = [];
            ManualSetupWarnings = [];
        }
        Changed?.Invoke();
    }

    /// <summary>Keys (see <see cref="Key"/>) of the exported policies selected for migration.</summary>
    public HashSet<string> Selected { get; } = [];

    public string? SavedExportFolder { get; set; }

    /// <summary>How each object the selected policies point at maps to the destination.</summary>
    public MappingPlan? Mapping { get; private set; }

    string? _mappingSelection;

    /// <summary>False when the selection changed since the mapping was built, so it has to be matched again.</summary>
    public bool MappingIsCurrent => Mapping is not null && _mappingSelection == SelectionSignature();

    public void SetMapping(MappingPlan plan)
    {
        Mapping = plan;
        _mappingSelection = SelectionSignature();
        Changed?.Invoke();
    }

    string SelectionSignature() => string.Join("|", Selected.Order(StringComparer.Ordinal));

    /// <summary>The last dry run against the destination. Run again whenever the Pre-flight screen opens.</summary>
    public PreflightReport? Preflight { get; private set; }

    public PreflightChoices PreflightChoices { get; set; } = new();

    /// <summary>Null while a dry run is under way, so a failed one doesn't leave the previous report on show.</summary>
    public void SetPreflight(PreflightReport? report)
    {
        Preflight = report;
        Changed?.Invoke();
    }

    /// <summary>The deployment running now, or the last one finished or opened.</summary>
    public DeployRun? Run { get; private set; }

    public void SetRun(DeployRun? run)
    {
        Run = run;
        Changed?.Invoke();
    }

    public event Action? Changed;

    public bool ReadyToDiscover => Source is not null && Destination is not null;

    public TenantConnection? Get(TenantRole role) => role == TenantRole.Source ? Source : Destination;

    /// <summary>The last comparison on the Compare screen. Cleared when either tenant changes.</summary>
    public Movewise.Core.Compare.ComparisonReport? Comparison { get; private set; }

    public void SetComparison(Movewise.Core.Compare.ComparisonReport? report)
    {
        Comparison = report;
        Restore = null;
        Changed?.Invoke();
    }

    /// <summary>The checked restore from a snapshot, waiting to be prepared as a run. Cleared with the comparison.</summary>
    public RestorePlan? Restore { get; private set; }

    public void SetRestore(RestorePlan? restore)
    {
        Restore = restore;
        Changed?.Invoke();
    }

    public void Set(TenantRole role, TenantConnection? connection)
    {
        // Choices made for one pair of tenants don't carry over to another, and neither does a comparison.
        PreflightChoices = new PreflightChoices();
        Comparison = null;
        Restore = null;
        if (role == TenantRole.Source)
        {
            Source = connection;
            SetExported(new ExportResult([], []));
        }
        else
        {
            var sameTenant = Destination?.Info.TenantId == connection?.Info.TenantId;
            Destination = connection;
            if (Run is not null && Run.DestinationTenantId != connection?.Info.TenantId)
                Run = null;
            // Matches and the dry run hold another tenant's IDs.
            if (!sameTenant)
            {
                Mapping = null;
                _mappingSelection = null;
                Preflight = null;
            }
        }
        Changed?.Invoke();
    }

    /// <param name="read">The services this read covered. Items of services no longer chosen are left out.</param>
    public void SetExported(ExportResult result, IReadOnlySet<M365Service>? read = null)
    {
        var items = result.Items.Where(i => Services.Contains(i.Type.Service)).ToList();
        Exported = items;
        ExportWarnings = result.Warnings.Where(w => Services.Contains(w.Type.Service)).ToList();
        SavedExportFolder = null;
        Mapping = null;
        _mappingSelection = null;
        Preflight = null;
        Selected.Clear();
        Selected.UnionWith(items.Select(Key));
        ReadServices = read?.Where(Services.Contains).ToHashSet() ?? new HashSet<M365Service>();
        Changed?.Invoke();
    }

    public void SetManualSetup(ManualSetupResult result)
    {
        ManualSetup = result.Items;
        ManualSetupWarnings = result.Warnings;
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>Same format as <see cref="MappingPlan.KeyOf"/>, so an exported object and its mapping share a key.</summary>
    public static string Key(ExportedResource item) => MappingPlan.KeyOf(item.Type.Id, item.SourceId);
}
