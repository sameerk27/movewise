using System.Text.Json.Nodes;
using Microsoft.Identity.Client;
using Movewise.Core.Export;
using Movewise.M365.Graph;

namespace Movewise.M365.Auth;

/// <summary>What creating (or repairing) the app registration did.</summary>
/// <param name="ClientId">The Application (client) ID to use.</param>
/// <param name="Tenant">The tenant it was created in.</param>
/// <param name="Reused">True when a Movewise registration was already there and was updated instead.</param>
/// <param name="ConsentGranted">True when consent was granted for the whole tenant it was created in.</param>
/// <param name="Notes">Anything the admin should know, such as a permission that couldn't be added.</param>
/// <param name="SignedInAs">The admin who set it up, so the next sign-in can suggest the same account.</param>
public sealed record AppRegistrationResult(string ClientId, string Tenant, bool Reused, bool ConsentGranted, IReadOnlyList<string> Notes)
{
    public string? SignedInAs { get; init; }

    /// <summary>The ID of the tenant it's in, so the sign-in that follows can be checked against it.</summary>
    public string? TenantId { get; init; }
}

/// <summary>
/// Creates Movewise's app registration from inside the app, so the admin doesn't have to use the Entra admin center.
/// Movewise can't sign in without a registration, so for this one step it signs in with Microsoft Graph Command Line
/// Tools, Microsoft's own public client that exists in every tenant, in the system browser. The registration is tagged,
/// so running this again updates the same registration instead of creating another.
/// </summary>
public sealed class AppRegistrationCreator
{
    /// <summary>Microsoft Graph Command Line Tools (the client Microsoft Graph PowerShell signs in with).</summary>
    const string BootstrapClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    const string Tag = "movewise";
    const string DisplayName = "Movewise";

    const string GraphAppId = "00000003-0000-0000-c000-000000000000";
    const string ExchangeAppId = "00000002-0000-0ff1-ce00-000000000000";
    const string TeamsAppId = "48ac35b8-9aa8-4d74-927d-1f4a14a0b239";
    const string DefenderEndpointAppId = "fc780465-2017-40d4-a0c5-307022471b92";

    static readonly string[] BootstrapScopes =
    [
        "https://graph.microsoft.com/Application.ReadWrite.All",
        "https://graph.microsoft.com/DelegatedPermissionGrant.ReadWrite.All",
        "https://graph.microsoft.com/User.Read",
    ];

    /// <summary>The permissions Movewise needs, by API: Graph, Exchange Online, Teams, and Defender for Endpoint.</summary>
    static IReadOnlyList<(string AppId, string Name, IReadOnlyList<string> Scopes)> Apis { get; } =
    [
        (GraphAppId, "Microsoft Graph", Scopes.GraphPermissions),
        (ExchangeAppId, "Office 365 Exchange Online", ["Exchange.Manage"]),
        (TeamsAppId, "Skype and Teams Tenant Admin API", ["user_impersonation"]),
        (DefenderEndpointAppId, "WindowsDefenderATP", ["Ti.ReadWrite"]),
    ];

    /// <param name="savedClientId">The registration Movewise set up before on this PC, if any: that one is reused when it's in this tenant.</param>
    public async Task<AppRegistrationResult> CreateAsync(string? savedClientId, IProgress<string> progress, CancellationToken ct = default)
    {
        progress.Report("Setting Movewise up in your tenant: sign in in your browser as a Global Administrator…");
        var client = PublicClientApplicationBuilder.Create(BootstrapClientId)
            .WithAuthority(AadAuthorityAudience.AzureAdMultipleOrgs)
            .WithRedirectUri("http://localhost")
            .Build();
        var signIn = await client.AcquireTokenInteractive(BootstrapScopes)
            .WithPrompt(Prompt.SelectAccount)
            .WithUseEmbeddedWebView(false)
            .WithSystemWebViewOptions(new SystemWebViewOptions
            {
                HtmlMessageSuccess = "<p style='font-family:Segoe UI,sans-serif'>Signed in. Go back to Movewise.</p>",
            })
            .ExecuteAsync(ct);

        try
        {
            var graph = new GraphClient(async token => (await client.AcquireTokenSilent(BootstrapScopes, signIn.Account).ExecuteAsync(token)).AccessToken);
            return await CreateWithAsync(graph, savedClientId, progress, ct) with { SignedInAs = signIn.Account.Username };
        }
        finally
        {
            // The bootstrap sign-in was only for this; nothing of it is kept.
            await client.RemoveAsync(signIn.Account);
        }
    }

    /// <summary>What went wrong, in terms the admin can act on, or null for anything unexpected.</summary>
    public static string? Describe(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("AADSTS65001", StringComparison.Ordinal) || message.Contains("AADSTS90094", StringComparison.Ordinal)
            || message.Contains("AADSTS90095", StringComparison.Ordinal))
            return "Your account can't approve the permissions needed to create the registration. Sign in as a Global Administrator, or ask one to create it.";
        if (message.Contains("AADSTS53003", StringComparison.Ordinal) || message.Contains("AADSTS50105", StringComparison.Ordinal))
            return "Your tenant blocked the sign-in to Microsoft Graph Command Line Tools, which Movewise uses to set itself up. Ask your admin to allow it, or to create Movewise's app registration by hand (docs/app-registration.md).";
        if (ex is GraphException { Status: System.Net.HttpStatusCode.Forbidden })
            return "Your account can't create app registrations. Sign in as a Global Administrator, Application Administrator or Cloud Application Administrator.";
        return null;
    }

    static async Task<AppRegistrationResult> CreateWithAsync(GraphClient graph, string? savedClientId, IProgress<string> progress, CancellationToken ct)
    {
        var notes = new List<string>();
        var org = (await graph.GetObjectAsync("v1.0/organization?$select=id,displayName", ct))["value"]?[0];
        var organization = org?["displayName"]?.GetValue<string>() ?? "your tenant";
        var tenantId = org?["id"]?.GetValue<string>();

        // The APIs' service principals in this tenant, which hold the IDs of their permissions.
        progress.Report("Looking up the permissions…");
        var resources = new List<(string AppId, string Name, string ServicePrincipalId, IReadOnlyList<string> Scopes, JsonArray Access)>();
        foreach (var (appId, name, scopes) in Apis)
        {
            var principal = await FindServicePrincipalAsync(graph, appId, "id,appId,oauth2PermissionScopes", ct);
            if (principal is null)
            {
                notes.Add($"{name} isn't in {organization}, so its permissions weren't added. Its policies can't be migrated until they are.");
                continue;
            }

            // A tenant can list the same permission name more than once; the first enabled one is used.
            var available = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scope in (principal["oauth2PermissionScopes"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(s => s["value"] is not null && s["id"] is not null)
                .OrderBy(s => s["isEnabled"]?.GetValue<bool>() == false))
            {
                available.TryAdd(scope["value"]!.GetValue<string>(), scope["id"]!.GetValue<string>());
            }
            var access = new JsonArray();
            var granted = new List<string>();
            foreach (var scope in scopes)
            {
                if (available.TryGetValue(scope, out var id))
                {
                    access.Add(new JsonObject { ["id"] = id, ["type"] = "Scope" });
                    granted.Add(scope);
                }
                else
                {
                    notes.Add($"{name} has no permission called {scope} in {organization}; it wasn't added.");
                }
            }
            resources.Add((appId, name, principal["id"]!.GetValue<string>(), granted, access));
        }

        var requiredResourceAccess = new JsonArray(resources
            .Where(r => r.Access.Count > 0)
            .Select(r => (JsonNode?)new JsonObject { ["resourceAppId"] = r.AppId, ["resourceAccess"] = r.Access.DeepClone() })
            .ToArray());

        // Update a registration made earlier by Movewise, or create one.
        progress.Report("Looking for a Movewise registration set up before…");
        var (existing, untrusted) = await FindReusableAsync(graph, savedClientId, ct);
        if (untrusted > 0)
            notes.Add($"{untrusted} other registration{(untrusted == 1 ? " is" : "s are")} tagged \"{Tag}\" in {organization}, but neither made by you nor used by Movewise on this PC, so {(untrusted == 1 ? "it was" : "they were")} left alone.");
        string objectId, clientId;
        var reused = existing is not null;
        if (existing is not null)
        {
            progress.Report($"Updating the Movewise registration already in {organization}…");
            objectId = existing["id"]!.GetValue<string>();
            clientId = existing["appId"]!.GetValue<string>();
        }
        else
        {
            progress.Report($"Creating the Movewise registration in {organization}…");
            var created = await graph.PostAsync("v1.0/applications", new JsonObject
            {
                ["displayName"] = DisplayName,
                ["description"] = "Migrates Microsoft 365 policies between tenants. Created by the Movewise app.",
                ["signInAudience"] = "AzureADMultipleOrgs",
                ["tags"] = new JsonArray(Tag),
            }, ct) ?? throw new InvalidOperationException("Microsoft Graph didn't return the new registration.");
            objectId = created["id"]!.GetValue<string>();
            clientId = created["appId"]!.GetValue<string>();
        }

        // The Windows sign-in broker's address needs the app's own ID, so it's set once the app exists.
        await graph.PatchAsync($"v1.0/applications/{objectId}", new JsonObject
        {
            ["isFallbackPublicClient"] = true,
            ["publicClient"] = new JsonObject
            {
                ["redirectUris"] = new JsonArray("http://localhost", $"ms-appx-web://microsoft.aad.brokerplugin/{clientId}"),
            },
            ["requiredResourceAccess"] = requiredResourceAccess,
        }, ct);

        progress.Report("Adding it to the tenant…");
        var servicePrincipal = await FindServicePrincipalAsync(graph, clientId, "id", ct)
            ?? await CreateServicePrincipalAsync(graph, clientId, ct);
        var servicePrincipalId = servicePrincipal["id"]!.GetValue<string>();

        // Consent for everyone in this tenant, so nobody here is asked. Other tenants are asked at their first sign-in.
        // Not just for this admin: the admin who then signs in to Movewise may be another one (the account picker allows
        // it), and most of these permissions need an administrator's consent, which they may not be able to give.
        // That's why only a registration Movewise can trust is reused (see FindReusableAsync).
        progress.Report("Granting consent in this tenant…");
        var consentGranted = true;
        foreach (var resource in resources.Where(r => r.Scopes.Count > 0))
        {
            try
            {
                await GrantAsync(graph, servicePrincipalId, resource.ServicePrincipalId, resource.Scopes, ct);
            }
            catch (GraphException ex)
            {
                consentGranted = false;
                notes.Add($"Consent for {resource.Name} wasn't granted ({ex.Code}). You'll be asked to consent when you first sign in, or a Global Administrator can grant it under Enterprise applications → Movewise → Permissions.");
            }
        }

        return new AppRegistrationResult(clientId, organization, reused, consentGranted, notes) { TenantId = tenantId };
    }

    /// <summary>
    /// The registration to reuse, if there's one Movewise can trust: the one it saved on this PC, or else the oldest one
    /// tagged "movewise" that the signed-in admin owns. Anyone can tag a registration, and reusing one means patching it
    /// and granting it consent for the whole tenant, so others are left alone. Also returns how many were left alone.
    /// A lookup that fails stops the setup: taking it for "none found" would create a duplicate.
    /// </summary>
    static async Task<(JsonObject? Registration, int Untrusted)> FindReusableAsync(GraphClient graph, string? savedClientId, CancellationToken ct)
    {
        try
        {
            var page = await graph.GetObjectAsync(GraphQuery.Where("v1.0/applications", $"tags/any(t:t eq '{Tag}')", "id,appId,createdDateTime"), ct);
            var tagged = page["value"]?.AsArray().OfType<JsonObject>().OrderBy(a => a["createdDateTime"]?.GetValue<string>()).ToList() ?? [];
            if (tagged.Count == 0)
                return (null, 0);

            if (savedClientId is not null
                && tagged.FirstOrDefault(a => string.Equals(a["appId"]?.GetValue<string>(), savedClientId, StringComparison.OrdinalIgnoreCase)) is { } saved)
                return (saved, tagged.Count - 1);

            // Whoever creates a registration becomes its owner, so the admin owns the ones Movewise made while they were signed in.
            var me = (await graph.GetObjectAsync("v1.0/me?$select=id", ct))["id"]?.GetValue<string>();
            foreach (var registration in tagged)
            {
                var owners = await graph.GetCollectionAsync($"v1.0/applications/{registration["id"]!.GetValue<string>()}/owners?$select=id", ct);
                if (me is not null && owners.Any(o => string.Equals(o["id"]?.GetValue<string>(), me, StringComparison.OrdinalIgnoreCase)))
                    return (registration, tagged.Count - 1);
            }
            return (null, tagged.Count);
        }
        catch (GraphException ex) when (ex.Status != System.Net.HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                $"Movewise couldn't check whether it was set up in this tenant before, so it didn't create another registration. Try signing in again. ({ex.Message})", ex);
        }
    }

    static async Task<JsonObject?> FindServicePrincipalAsync(GraphClient graph, string appId, string select, CancellationToken ct)
    {
        var page = await graph.GetObjectAsync(GraphQuery.Where("v1.0/servicePrincipals", $"appId eq {GraphQuery.Literal(appId)}", select, top: 1), ct);
        return page["value"]?.AsArray().OfType<JsonObject>().FirstOrDefault();
    }

    /// <summary>A brand-new app can take a few seconds to be known everywhere, so this retries briefly.</summary>
    static async Task<JsonObject> CreateServicePrincipalAsync(GraphClient graph, string appId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await graph.PostAsync("v1.0/servicePrincipals", new JsonObject { ["appId"] = appId }, ct)
                    ?? throw new InvalidOperationException("Microsoft Graph didn't return the new service principal.");
            }
            catch (GraphException) when (attempt < 6)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    static async Task GrantAsync(GraphClient graph, string clientId, string resourceId, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        var scope = string.Join(' ', scopes);
        var filter = $"clientId eq {GraphQuery.Literal(clientId)} and resourceId eq {GraphQuery.Literal(resourceId)} and consentType eq 'AllPrincipals'";
        var existing = (await graph.GetObjectAsync(GraphQuery.Where("v1.0/oauth2PermissionGrants", filter, "id,scope"), ct))["value"]?.AsArray()
            .OfType<JsonObject>().FirstOrDefault();

        if (existing is not null)
            await graph.PatchAsync($"v1.0/oauth2PermissionGrants/{existing["id"]!.GetValue<string>()}", new JsonObject { ["scope"] = scope }, ct);
        else
            await graph.PostAsync("v1.0/oauth2PermissionGrants", new JsonObject
            {
                ["clientId"] = clientId,
                ["consentType"] = "AllPrincipals",
                ["resourceId"] = resourceId,
                ["scope"] = scope,
            }, ct);
    }
}
