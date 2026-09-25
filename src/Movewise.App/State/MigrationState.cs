using Microsoft.Identity.Client;
using Movewise.Core.Access;
using Movewise.Core.Deploy;
using Movewise.Core.Export;
using Movewise.Core.Mapping;
using Movewise.Core.Preflight;
using Movewise.Core.Tenants;

namespace Movewise.App.State;

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

    public void SetPreflight(PreflightReport report)
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

    public void Set(TenantRole role, TenantConnection? connection)
    {
        // Choices made for one pair of tenants don't carry over to another.
        PreflightChoices = new PreflightChoices();
        if (role == TenantRole.Source)
        {
            Source = connection;
            SetExported(new ExportResult([], []));
        }
        else
        {
            Destination = connection;
            if (Run is not null && Run.DestinationTenantId != connection?.Info.TenantId)
                Run = null;
        }
        Changed?.Invoke();
    }

    public void SetExported(ExportResult result)
    {
        Exported = result.Items;
        ExportWarnings = result.Warnings;
        SavedExportFolder = null;
        Mapping = null;
        _mappingSelection = null;
        Preflight = null;
        Selected.Clear();
        Selected.UnionWith(result.Items.Select(Key));
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
