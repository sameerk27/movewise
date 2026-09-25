using System.Collections;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json.Nodes;
using Movewise.Core.Export;
using Movewise.Core.Tenants;

namespace Movewise.M365.PowerShell;

public enum PowerShellEndpoint
{
    /// <summary>Exchange Online PowerShell: Exchange and Defender for Office 365.</summary>
    Exchange,

    /// <summary>Security &amp; Compliance PowerShell: Purview.</summary>
    Compliance,

    /// <summary>Microsoft Teams PowerShell: Teams policies.</summary>
    Teams,
}

/// <summary>An access token and when it runs out.</summary>
public sealed record SessionToken(string Value, DateTimeOffset ExpiresOn);

/// <summary>
/// One tenant's Exchange Online, Security &amp; Compliance or Microsoft Teams PowerShell, run inside the app with the
/// bundled ExchangeOnlineManagement or MicrosoftTeams module (PowerShell 7 SDK). Each session has its own runspace, so the source and
/// destination never mix. Connects on first use, checks that it reached the right tenant, and reconnects before the
/// access token runs out (the modules can't refresh a token they were given).
/// </summary>
/// <param name="readOnly">True for the source: only Get cmdlets are allowed.</param>
/// <param name="tenant">The tenant the admin signed in to. Every connection is checked against its ID.</param>
/// <param name="getToken">
/// The admin's access token for the endpoint, or null when there isn't one. Security &amp; Compliance and Teams
/// then fall back to the module's own sign-in for the same account.
/// </param>
/// <param name="getGraphToken">Teams only: a Microsoft Graph token, which Connect-MicrosoftTeams needs alongside the Teams one.</param>
public sealed class PowerShellSession(
    PowerShellEndpoint endpoint,
    bool readOnly,
    TenantInfo tenant,
    Func<CancellationToken, Task<SessionToken?>> getToken,
    Func<CancellationToken, Task<SessionToken?>>? getGraphToken = null) : IPowerShell, IAsyncDisposable
{
    public const string ModuleName = "ExchangeOnlineManagement";
    public const string TeamsModuleName = "MicrosoftTeams";

    /// <summary>Connect-IPPSSession accepts an access token from this version on.</summary>
    public static readonly Version MinimumModuleVersion = new(3, 8, 0);

    // A session made with the module's own sign-in refreshes its own tokens; still, start afresh now and then.
    static readonly TimeSpan ReconnectAfter = TimeSpan.FromMinutes(45);

    // A session made with the admin's token is replaced this long before the token runs out.
    static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    public static string ModulesFolder { get; } = Path.Combine(AppContext.BaseDirectory, "Modules");

    public static bool IsModuleBundled => IsBundled(ModuleName);

    public static bool IsBundled(string module) => Directory.Exists(Path.Combine(ModulesFolder, module));

    string Module => endpoint == PowerShellEndpoint.Teams ? TeamsModuleName : ModuleName;

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Dictionary<string, IReadOnlySet<string>> _parameters = new(StringComparer.OrdinalIgnoreCase);
    Runspace? _runspace;
    DateTimeOffset _reconnectAt;

    public PowerShellEndpoint Endpoint => endpoint;

    public async Task<IReadOnlyList<JsonObject>> InvokeAsync(string cmdlet, IReadOnlyDictionary<string, JsonNode?>? parameters = null, CancellationToken ct = default)
    {
        if (readOnly && !cmdlet.StartsWith("Get-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Movewise only reads from the source tenant; {cmdlet} isn't allowed there.");

        await _gate.WaitAsync(ct);
        try
        {
            var runspace = await ConnectedRunspaceAsync(ct);
            try
            {
                return await Task.Run(() => Run(runspace, cmdlet, parameters, ct), ct);
            }
            catch (PowerShellException ex) when (ex.IsAuthFailure)
            {
                // The token was refused (it may have run out early): connect again with a fresh one and try once more.
                // Nothing was done, so running the cmdlet again is safe.
                await CloseAsync();
                runspace = await ConnectedRunspaceAsync(ct);
                return await Task.Run(() => Run(runspace, cmdlet, parameters, ct), ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlySet<string>> ParametersOfAsync(string cmdlet, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_parameters.TryGetValue(cmdlet, out var cached))
                return cached;

            var runspace = await ConnectedRunspaceAsync(ct);
            var names = await Task.Run(() =>
            {
                using var ps = System.Management.Automation.PowerShell.Create(runspace);
                ps.AddCommand("Get-Command").AddParameter("Name", cmdlet).AddParameter("ErrorAction", "Stop");
                var command = Invoke(ps, ct).Select(o => o.BaseObject).OfType<CommandInfo>().FirstOrDefault()
                    ?? throw new PowerShellException($"The {cmdlet} cmdlet isn't available. The account may be missing an admin role for it.");
                return (IReadOnlySet<string>)new HashSet<string>(command.Parameters.Keys, StringComparer.OrdinalIgnoreCase);
            }, ct);
            _parameters[cmdlet] = names;
            return names;
        }
        finally
        {
            _gate.Release();
        }
    }

    const int JsonDepth = 20;

    static IReadOnlyList<JsonObject> Run(Runspace runspace, string cmdlet, IReadOnlyDictionary<string, JsonNode?>? parameters, CancellationToken ct)
    {
        using var ps = System.Management.Automation.PowerShell.Create(runspace);
        ps.AddCommand(cmdlet);
        foreach (var (name, value) in parameters ?? new Dictionary<string, JsonNode?>())
            ps.AddParameter(name, ToPowerShell(value));
        ps.AddParameter("ErrorAction", "Stop");

        // JSON is what the engine works with; enums as names, so they can be passed back to New cmdlets.
        // Deep enough for DLP rules' nested sensitive info type groups.
        ps.AddCommand("ConvertTo-Json")
            .AddParameter("Depth", JsonDepth)
            .AddParameter("Compress")
            .AddParameter("EnumsAsStrings")
            .AddParameter("AsArray");

        var text = string.Concat(Invoke(ps, ct).Select(o => o.BaseObject as string));

        // ConvertTo-Json only warns when it cuts settings off. A policy read with settings missing would be recreated wrong,
        // so that's an error.
        if (ps.Streams.Warning.Any(w => w.Message.Contains("truncated", StringComparison.OrdinalIgnoreCase)))
            throw new PowerShellException($"What {cmdlet} returned is nested more than {JsonDepth} levels deep, so Movewise can't read all of it.");
        if (string.IsNullOrWhiteSpace(text))
            return [];
        return JsonNode.Parse(text) is JsonArray array ? array.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList() : [];
    }

    /// <summary>Runs the pipeline, turning cmdlet errors into <see cref="PowerShellException"/>.</summary>
    static IReadOnlyList<PSObject> Invoke(System.Management.Automation.PowerShell ps, CancellationToken ct)
    {
        using var cancel = ct.Register(() => ps.BeginStop(null, null));
        Collection<PSObject> output;
        try
        {
            output = ps.Invoke();
        }
        catch (PipelineStoppedException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (RuntimeException ex)
        {
            throw new PowerShellException(ex.ErrorRecord?.Exception?.Message ?? ex.Message);
        }

        if (ps.HadErrors && ps.Streams.Error.FirstOrDefault() is { } error)
            throw new PowerShellException(error.Exception?.Message ?? error.ToString());
        return output;
    }

    async Task<Runspace> ConnectedRunspaceAsync(CancellationToken ct)
    {
        if (_runspace is not null && DateTimeOffset.UtcNow < _reconnectAt)
            return _runspace;

        await CloseAsync();
        if (!IsBundled(Module))
            throw new InvalidOperationException($"The {Module} module isn't bundled in this build. Run tools\\Save-Modules.ps1, then rebuild.");

        var token = await getToken(ct);
        var graphToken = getGraphToken is null ? null : await getGraphToken(ct);
        var connectedAt = DateTimeOffset.UtcNow;
        var (runspace, usedToken) = await Task.Run(() => Connect(token?.Value, graphToken?.Value, ct), ct);
        _runspace = runspace;

        // With the admin's token, the session lasts as long as the token(s) it was given; the module's own sign-in refreshes itself.
        _reconnectAt = connectedAt + ReconnectAfter;
        if (usedToken && token is not null)
        {
            var expires = graphToken is null || token.ExpiresOn < graphToken.ExpiresOn ? token.ExpiresOn : graphToken.ExpiresOn;
            // AuthService hands out tokens with at least 15 minutes left, but never reconnect more often than every minute.
            if (expires - ExpiryMargin < _reconnectAt)
                _reconnectAt = expires - ExpiryMargin > connectedAt.AddMinutes(1) ? expires - ExpiryMargin : connectedAt.AddMinutes(1);
        }
        return _runspace;
    }

    /// <summary>Connects a new runspace. Returns it, and whether it was connected with the admin's token (not the module's own sign-in).</summary>
    (Runspace Runspace, bool UsedToken) Connect(string? token, string? graphToken, CancellationToken ct)
    {
        PutBundledModulesFirst();
        var session = InitialSessionState.CreateDefault2();
        session.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        var runspace = RunspaceFactory.CreateRunspace(session);
        runspace.Open();

        try
        {
            using (var ps = System.Management.Automation.PowerShell.Create(runspace))
            {
                ps.AddCommand("Import-Module").AddParameter("Name", Module).AddParameter("ErrorAction", "Stop");
                if (endpoint != PowerShellEndpoint.Teams)
                    ps.AddParameter("MinimumVersion", MinimumModuleVersion.ToString());
                Invoke(ps, ct);
            }

            var usedToken = ConnectToTenant(runspace, token, graphToken, ct);
            CheckTenant(runspace, ct);
            return (runspace, usedToken);
        }
        catch
        {
            Disconnect(runspace);
            runspace.Dispose();
            throw;
        }
    }

    /// <summary>Signs the runspace in. Returns true when the admin's token was accepted, false when the module signed in itself.</summary>
    bool ConnectToTenant(Runspace runspace, string? token, string? graphToken, CancellationToken ct)
    {
        if (endpoint == PowerShellEndpoint.Exchange)
        {
            // A delegated token goes with UserPrincipalName only: with Organization, the module takes it for an
            // app-only token and Exchange answers "UnAuthorized".
            string? exchangeError = token is null ? "Movewise couldn't get a token for it." : null;
            if (token is not null)
            {
                try
                {
                    ConnectWith(runspace, "Connect-ExchangeOnline", ct, ("AccessToken", token), ("UserPrincipalName", tenant.UserPrincipalName));
                    return true;
                }
                catch (PowerShellException ex)
                {
                    exchangeError = ex.Message;
                }
            }
            Fallback(() => ConnectWith(runspace, "Connect-ExchangeOnline", ct, ("UserPrincipalName", tenant.UserPrincipalName), ("DisableWAM", true)), exchangeError);
            return false;
        }

        if (endpoint == PowerShellEndpoint.Teams)
        {
            // The admin's Graph and Teams tokens first; otherwise the module signs the same account in itself.
            string? tokenError = token is null || graphToken is null ? "Movewise couldn't get a Teams token." : null;
            if (tokenError is null)
            {
                try
                {
                    ConnectWith(runspace, "Connect-MicrosoftTeams", ct, ("AccessTokens", new[] { graphToken!, token! }));
                    return true;
                }
                catch (PowerShellException ex)
                {
                    tokenError = ex.Message;
                }
            }
            // DisableWAM: the Windows sign-in broker needs a window handle, which a hosted runspace doesn't have.
            // TenantId keeps the module's sign-in to this tenant; CheckTenant confirms it afterwards either way.
            Fallback(() => ConnectWith(runspace, "Connect-MicrosoftTeams", ct, ("AccountId", tenant.UserPrincipalName), ("TenantId", tenant.TenantId), ("DisableWAM", true)), tokenError);
            return false;
        }

        // Security & Compliance: the admin's token first. If it isn't accepted, the module signs the same
        // account in itself (a Microsoft sign-in window may appear).
        string? error = token is null ? "Movewise couldn't get a token for it." : null;
        if (token is not null)
        {
            try
            {
                ConnectWith(runspace, "Connect-IPPSSession", ct, ("AccessToken", token), ("UserPrincipalName", tenant.UserPrincipalName));
                return true;
            }
            catch (PowerShellException ex)
            {
                error = ex.Message;
            }
        }
        Fallback(() => ConnectWith(runspace, "Connect-IPPSSession", ct, ("UserPrincipalName", tenant.UserPrincipalName), ("DisableWAM", true)), error);
        return false;
    }

    /// <summary>
    /// Makes sure the session reached the tenant the admin signed in to. The username is only a hint to the module's own
    /// sign-in, where another account (and so another tenant) can be picked; a destination session connected to the
    /// source would write to it. Throws when the tenant is a different one, or can't be told.
    /// </summary>
    void CheckTenant(Runspace runspace, CancellationToken ct)
    {
        var found = ConnectedTenantIds(runspace, ct);
        var product = endpoint switch
        {
            PowerShellEndpoint.Exchange => "Exchange Online PowerShell",
            PowerShellEndpoint.Compliance => "Security & Compliance PowerShell",
            _ => "Microsoft Teams PowerShell",
        };
        if (found.Count == 0)
            throw new PowerShellException($"Movewise couldn't confirm which tenant {product} connected to, so it didn't use the connection. Try again.");
        if (found.Any(id => !string.Equals(id, tenant.TenantId, StringComparison.OrdinalIgnoreCase)))
            throw new PowerShellException($"{product} signed in to a different tenant than {tenant.DisplayName}, so Movewise closed the connection. Try again, and sign in with {tenant.UserPrincipalName}.");
    }

    /// <summary>The tenant IDs the runspace's connections are to.</summary>
    IReadOnlyList<string> ConnectedTenantIds(Runspace runspace, CancellationToken ct)
    {
        var ids = new List<string>();
        void Collect(string command, params string[] properties)
        {
            try
            {
                using var ps = System.Management.Automation.PowerShell.Create(runspace);
                ps.AddCommand(command).AddParameter("ErrorAction", "Stop");
                foreach (var item in Invoke(ps, ct))
                {
                    var id = properties.Select(p => item.Properties[p]?.Value?.ToString()).FirstOrDefault(v => Guid.TryParse(v, out _));
                    if (id is not null)
                        ids.Add(id);
                }
            }
            catch (PowerShellException)
            {
            }
        }

        if (endpoint == PowerShellEndpoint.Teams)
        {
            // Get-CsTenant needs a Teams admin role, which Movewise needs anyway; the module's own context doesn't.
            Collect("Get-CsTenant", "TenantId");
        }
        else
        {
            // Get-ConnectionInformation lists every Exchange connection in the process, the other tenant's too. This
            // session's own is the one whose temporary module is loaded here; failing that, the one for its account.
            try
            {
                using var ps = System.Management.Automation.PowerShell.Create(runspace);
                ps.AddScript("""
                    param($Upn, $Eop)
                    $mine = @(Get-Module | ForEach-Object Name)
                    $all = @(Get-ConnectionInformation -ErrorAction Stop)
                    $ours = @($all | Where-Object { $_.ModuleName -and $mine -contains $_.ModuleName })
                    if ($ours.Count -eq 0) { $ours = @($all | Where-Object { $_.UserPrincipalName -eq $Upn -and [bool]$_.IsEopSession -eq $Eop }) }
                    $ours
                    """)
                    .AddParameter("Upn", tenant.UserPrincipalName)
                    .AddParameter("Eop", endpoint == PowerShellEndpoint.Compliance);
                foreach (var item in Invoke(ps, ct))
                {
                    var id = new[] { "TenantID", "TenantId" }.Select(p => item.Properties[p]?.Value?.ToString()).FirstOrDefault(v => Guid.TryParse(v, out _));
                    if (id is not null)
                        ids.Add(id);
                }
            }
            catch (PowerShellException)
            {
            }
        }
        return ids;
    }

    /// <summary>The module's own sign-in, after the admin's token was refused. If that fails too, both reasons are reported.</summary>
    static void Fallback(Action connect, string? firstError)
    {
        try
        {
            connect();
        }
        catch (PowerShellException ex)
        {
            throw new PowerShellException($"{ex.Message} (Before that, signing in with Movewise's own token failed: {firstError})");
        }
    }

    static void ConnectWith(Runspace runspace, string cmdlet, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        using var ps = System.Management.Automation.PowerShell.Create(runspace);
        ps.AddCommand(cmdlet);
        foreach (var (name, value) in parameters)
            ps.AddParameter(name, value);
        if (cmdlet != "Connect-MicrosoftTeams")
            ps.AddParameter("ShowBanner", false);
        ps.AddParameter("ErrorAction", "Stop");
        Invoke(ps, ct);
    }

    /// <summary>JSON values as PowerShell takes them: objects become hashtables, lists become arrays.</summary>
    static object? ToPowerShell(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        JsonValue value when value.TryGetValue<long>(out var whole) => whole is >= int.MinValue and <= int.MaxValue ? (int)whole : whole,
        JsonValue value when value.TryGetValue<double>(out var number) => number,
        JsonValue value => value.ToString(),
        JsonArray array when array.All(e => e is JsonValue v && v.TryGetValue<string>(out _)) => array.Select(e => e!.GetValue<string>()).ToArray(),
        JsonArray array when array.All(e => e is JsonObject) => array.Select(e => (Hashtable)ToPowerShell(e)!).ToArray(),
        JsonArray array => array.Select(ToPowerShell).ToArray(),
        JsonObject obj => new Hashtable(obj.ToDictionary(p => p.Key, p => ToPowerShell(p.Value)), StringComparer.OrdinalIgnoreCase),
        _ => node.ToJsonString(),
    };

    // The bundled, tested module versions must win over anything installed on the PC.
    static void PutBundledModulesFirst()
    {
        var current = Environment.GetEnvironmentVariable("PSModulePath") ?? "";
        if (!current.StartsWith(ModulesFolder, StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PSModulePath", ModulesFolder + Path.PathSeparator + current);
    }

    async Task CloseAsync()
    {
        if (_runspace is not { } runspace)
            return;
        _runspace = null;
        _parameters.Clear();
        await Task.Run(() =>
        {
            Disconnect(runspace);
            runspace.Dispose();
        });
    }

    void Disconnect(Runspace runspace)
    {
        try
        {
            using var ps = System.Management.Automation.PowerShell.Create(runspace);
            if (endpoint == PowerShellEndpoint.Teams)
                ps.AddCommand("Disconnect-MicrosoftTeams");
            else
                ps.AddCommand("Disconnect-ExchangeOnline").AddParameter("Confirm", false);
            ps.Invoke();
        }
        catch (RuntimeException)
        {
            // The runspace is discarded anyway.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await CloseAsync();
        }
        finally
        {
            _gate.Release();
        }
    }
}
