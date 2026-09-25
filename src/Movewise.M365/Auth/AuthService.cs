using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace Movewise.M365.Auth;

/// <summary>
/// Signs admins in through the Windows sign-in broker (WAM), which handles MFA, passkeys and Conditional Access.
/// Each tenant is a separate account; tokens stay with MSAL and Windows, never with Movewise.
/// </summary>
public sealed class AuthService(MovewiseOptions options, Func<IntPtr> parentWindow)
{
    IPublicClientApplication? _app;

    IPublicClientApplication App => _app ??= Build();

    /// <summary>Shows the account picker so the admin chooses which account (and so which tenant) to use.</summary>
    /// <param name="loginHint">An account to suggest, such as the one that just set Movewise up.</param>
    public Task<AuthenticationResult> SignInAsync(IEnumerable<string> scopes, CancellationToken ct, string? loginHint = null)
    {
        var request = App.AcquireTokenInteractive(scopes);
        return (loginHint is null ? request.WithPrompt(Prompt.SelectAccount) : request.WithLoginHint(loginHint)).ExecuteAsync(ct);
    }

    // A token handed to PowerShell can't be refreshed by the module, so it must have at least this long left.
    static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(15);

    public async Task<string> GetTokenAsync(IAccount account, IEnumerable<string> scopes, CancellationToken ct) =>
        (await AcquireAsync(account, scopes, ct)).AccessToken;

    /// <summary>
    /// A token with its expiry time. MSAL hands back a cached token until a few minutes before it runs out, so one that's
    /// close to expiring is swapped for a fresh one: PowerShell sessions keep the token they connect with.
    /// </summary>
    public async Task<AuthenticationResult> AcquireAsync(IAccount account, IEnumerable<string> scopes, CancellationToken ct)
    {
        try
        {
            var result = await App.AcquireTokenSilent(scopes, account).ExecuteAsync(ct);
            if (result.ExpiresOn - DateTimeOffset.UtcNow < MinimumLifetime)
                result = await App.AcquireTokenSilent(scopes, account).WithForceRefresh(true).ExecuteAsync(ct);
            return result;
        }
        catch (MsalUiRequiredException)
        {
            // New permissions (for example Exchange) or an expired session: ask again for the same account.
            return await App.AcquireTokenInteractive(scopes).WithAccount(account).ExecuteAsync(ct);
        }
    }

    public Task SignOutAsync(IAccount account) => App.RemoveAsync(account);

    /// <summary>Starts over with the current client ID, after the admin changed it. Only while nobody is signed in.</summary>
    public void Reset() => _app = null;

    /// <summary>Explains the sign-in errors that mean the app registration itself is wrong, or returns null.</summary>
    public static string? DescribeRegistrationError(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("AADSTS700016", StringComparison.Ordinal))
            return "Microsoft can't find Movewise's app registration. If it was just created, wait a minute and try again; if it was deleted, sign out of the destination and sign in again, which sets Movewise up afresh.";
        if (message.Contains("AADSTS50011", StringComparison.Ordinal) || message.Contains("redirect", StringComparison.OrdinalIgnoreCase) && message.Contains("AADSTS", StringComparison.Ordinal))
            return "Movewise's app registration is missing its redirect addresses. Sign out of the destination and sign in again: that repairs the registration.";
        if (message.Contains("AADSTS7000218", StringComparison.Ordinal))
            return "Movewise's app registration doesn't allow public client flows. Sign out of the destination and sign in again: that repairs the registration.";
        return null;
    }

    public static bool IsUserCancel(Exception ex) =>
        ex is MsalClientException { ErrorCode: MsalError.AuthenticationCanceledError };

    IPublicClientApplication Build()
    {
        if (!options.IsConfigured)
            throw new InvalidOperationException("No app registration is set up. Add its client ID on the Connect screen.");

        return PublicClientApplicationBuilder.Create(options.ClientId)
            .WithAuthority(AadAuthorityAudience.AzureAdMultipleOrgs)
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "Movewise" })
            .WithParentActivityOrWindow(parentWindow)
            .Build();
    }
}
