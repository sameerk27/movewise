# Register Movewise in Entra ID

Movewise signs in as the admin, so it needs one app registration: a public client with delegated permissions only. It holds no secrets or certificates.

The registration lives in the **destination** (target) tenant. The source tenant only approves Movewise once, which adds Movewise to its **Enterprise applications**. Movewise never changes the source's policies: the app itself only ever reads from it (see [Why the source stays unchanged](#why-the-source-stays-unchanged)).

## Normally: nothing to do

On the Connect screen, sign in to the **destination** first, as a Global Administrator. Your browser opens once for Movewise to set itself up in that tenant. It:

1. creates a multitenant registration called **Movewise**, tagged `movewise`, with both redirect addresses, public client flows turned on, and every permission below (looked up by name in the tenant). A Movewise registration made earlier is reused and repaired (missing permissions or redirect addresses put back) instead of duplicated, but only one that Movewise can trust: the one whose client ID Movewise saved on this PC, or one owned by the admin signing in. Anything else tagged `movewise` is left alone, and a new registration is made. If the tenant's registrations can't be looked up, setup stops instead of creating a duplicate,
2. adds it to the destination and grants consent there for the whole organization, so nobody in the destination is asked, including a different admin who signs in later,
3. saves the client ID, waits a few seconds for Microsoft to publish a brand-new registration, and carries on with the normal sign-in, suggesting the same account.

This happens at every destination sign-in, so a registration that was changed or deleted is put right automatically.

Then sign in to the **source**. The button is available once the destination is connected. The first time, Microsoft asks you to approve Movewise for the source tenant: accept as a Global Administrator, with **Consent on behalf of your organization** ticked. Microsoft lists every permission the registration has, including the write permissions Movewise uses in the destination; in the source, Movewise only reads.

Movewise can't sign in before its own registration exists, so the setup sign-in uses **Microsoft Graph Command Line Tools**, Microsoft's public app that Microsoft Graph PowerShell also uses, with `Application.ReadWrite.All` and `DelegatedPermissionGrant.ReadWrite.All`. That sign-in is discarded afterwards.

## By hand

For organizations that create app registrations themselves, or that block Microsoft Graph Command Line Tools. Create the registration as below, then give Movewise its client ID in one of the ways in step 4. Movewise then uses it as it is, never creates one of its own, and lets you sign in to either tenant first.
### 1. Create the registration

1. Open the [Entra admin center](https://entra.microsoft.com) → **Identity** → **Applications** → **App registrations** → **New registration**.
2. **Name:** `Movewise`
3. **Supported account types:** *Accounts in any organizational directory (Any Microsoft Entra ID tenant – Multitenant)*
4. Leave **Redirect URI** empty for now and select **Register**.
5. Copy the **Application (client) ID**.

### 2. Add the redirect URIs

**Authentication** → **Add a platform** → **Mobile and desktop applications**, then add both:

- `ms-appx-web://microsoft.aad.brokerplugin/<client-id>`: used by the Windows sign-in broker (replace `<client-id>`)
- `http://localhost`: fallback when the broker isn't available

Under **Advanced settings**, set **Allow public client flows** to **Yes**. Save.

### 3. Add the delegated permissions

**API permissions** → **Add a permission**.

**Microsoft Graph** → **Delegated permissions**:

| Permission | Used for |
|---|---|
| `User.Read` | Signing in |
| `Directory.Read.All` | Tenant name, domains, the admin's roles, licenses |
| `Policy.Read.All` | Reading Conditional Access and other policies |
| `Policy.ReadWrite.ConditionalAccess` | Creating Conditional Access policies and custom authentication strengths (destination only) |
| `Application.Read.All` | Matching apps referenced by policies |
| `Group.ReadWrite.All` | Creating missing groups (destination only) |
| `DeviceManagementConfiguration.Read.All` / `.ReadWrite.All` | Intune configuration and compliance |
| `DeviceManagementApps.Read.All` / `.ReadWrite.All` | Intune app protection and configuration |
| `DeviceManagementServiceConfig.Read.All` / `.ReadWrite.All` | Intune enrollment and Autopilot |
| `DeviceManagementRBAC.Read.All` / `.ReadWrite.All` | Intune scope tags |
| `Sites.Read.All` | Finding the SharePoint and OneDrive sites that Purview policies apply to |
| `Policy.ReadWrite.Authorization` | Entra ID user settings (the authorization policy) (destination only) |
| `Policy.ReadWrite.AuthenticationMethod` | Which sign-in methods users may use (the authentication methods policy) (destination only) |
| `Policy.ReadWrite.CrossTenantAccess` | Cross-tenant access default settings and partners (destination only) |
| `RoleManagement.ReadWrite.Directory` | Creating custom Entra ID admin roles (destination only). The permission itself is broader: it also allows assigning directory roles, within what the signed-in admin can do |
| `SharePointTenantSettings.Read.All` / `.ReadWrite.All` | SharePoint and OneDrive sharing, sync and site creation settings |
| `CustomDetection.Read.All` / `.ReadWrite.All` | Defender custom detection rules |

**Office 365 Exchange Online** (under *APIs my organization uses*) → **Delegated permissions** → `Exchange.Manage`
(used by Exchange Online PowerShell for Exchange and Defender for Office 365 policies).

**Skype and Teams Tenant Admin API** (under *APIs my organization uses*) → **Delegated permissions** → `user_impersonation`
(used by Microsoft Teams PowerShell for Teams policies and their group assignments. If it isn't granted, the Teams module signs the same account in itself, and a Microsoft sign-in window may appear once per session.)

**WindowsDefenderATP** (under *APIs my organization uses*) → **Delegated permissions** → `Ti.ReadWrite`
(used for Defender for Endpoint indicators).

Where a permission is listed as `.Read.All` / `.ReadWrite.All`, the source uses the read one and the destination the write one. One app registration serves both tenants, so both admins consent to the whole list; Movewise itself only reads from the source (see [Why the source stays unchanged](#why-the-source-stays-unchanged)).

Purview policies go through Security & Compliance PowerShell. Movewise first tries the admin's own token for it; if the tenant doesn't accept that, the Exchange Online module signs the same account in itself, and a Microsoft sign-in window may appear once per session. This needs ExchangeOnlineManagement 3.8.0 or later, which `tools/Save-Modules.ps1` bundles.

### 4. Point Movewise at the registration

Either:

- **Build it in:** put the Application (client) ID in `src/Movewise.App/appsettings.json` before running `tools\Publish.ps1`, so every copy of that release uses it, or
- **Set it per PC:** set the `MOVEWISE_CLIENT_ID` environment variable. It overrides a built-in ID.

Either way, it takes precedence over any registration Movewise set up by itself (saved in `%LOCALAPPDATA%\MovewiseData\settings.json`), and Movewise stops setting one up.

## First sign-in to each tenant

The first time an admin signs in to a tenant, Entra asks them to consent to Movewise's permissions for their organization. A Global Administrator (or Privileged Role Administrator / Cloud Application Administrator for most permissions) has to accept. If the tenant blocks user consent, grant consent in advance from **Enterprise applications** → **Movewise** → **Permissions**.

## Why the source stays unchanged

One registration serves both tenants, so its permissions include the write ones the destination needs, and the source's approval covers them too. Movewise asks the source only for Graph read permissions when it signs in, but the Exchange Online and Teams tokens (`.default`) carry whatever was approved. So the permissions alone don't make the source read-only. The app does: the source's Microsoft Graph client has no way to send changes, and its Exchange Online, Security & Compliance and Teams PowerShell sessions refuse anything but Get cmdlets. Every PowerShell connection is also checked to be to the tenant you signed in to, so a destination session can't end up connected to the source.
