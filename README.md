# Movewise

A Windows app that migrates Microsoft 365 policies from one tenant to another. An admin signs in to the source tenant (read-only) and the destination tenant directly, picks policies, maps what they depend on, checks the result in a dry run, and deploys.

Services, in build order: Entra ID, Intune, Defender for Office 365, Exchange Online, Purview, Teams.

## Status

Phases 0 to 5 of the build plan (every service: Entra ID, Intune, Defender for Office 365, Exchange Online, Purview and Teams):

- [x] Windows app shell (WPF + Blazor Hybrid) with the mockup's layout
- [x] Sign-in to source and destination through the Windows sign-in broker, one account per tenant
- [x] Guest accounts rejected; source and destination must be different tenants
- [x] Admin role check for each side, and licenses in the source that the destination lacks
- [x] Exchange Online connection test through PowerShell 7 hosted inside the app
- [x] Discover Entra ID: Conditional Access policies, named locations, custom authentication strengths
- [x] Discover Intune: settings catalog, device configuration, compliance (with noncompliance actions), iOS and Android app protection, Autopilot profiles, assignment filters, custom scope tags, each with its assignments
- [x] Dependencies found for every policy (groups, users, apps, locations, strengths, filters, scope tags); a type that can't be read shows a warning instead of stopping discovery
- [x] Save the selected policies to a project folder as normalized JSON
- [x] Map dependencies: groups (by name, then mail nickname), users (by email, then username), apps (by app ID), named locations, authentication strengths, filters and scope tags (by name, or created with the policies); manual matching with destination search, create or remove; decisions kept when matching again; CSV export and import
- [x] Pre-flight dry run: every policy rewritten for the destination (mapped IDs, placeholders for objects created during deploy, removals, report-only Conditional Access), with checks for unmatched objects, manual matches that no longer exist, admin roles, Entra ID P1/P2 and Intune licenses, name conflicts (skip or rename), removed exclusions, policies that exclude nobody, and secrets Graph doesn't return; before/after view per policy; CSV report
- [x] Deploy: pre-flight runs once more, optional copy of the destination's current policies, then groups (created empty) and policies in dependency order with IDs filled in as objects are created, then Intune assignments and app protection apps; progress saved after every object; resume after a stop, crash or dropped connection (an unanswered create is looked up by name before retrying); rollback that deletes only what the run created; Windows kept awake; results CSV
- [x] Defender for Office 365: anti-phishing, anti-spam, outbound spam, anti-malware, Safe Links and Safe Attachments policies, each with its rule; preset and default policies left out
- [x] Defender for Office 365: quarantine policies (created before the policies that name them; built-in ones left out) and the Tenant Allow/Block List (senders, URLs, file hashes; expired entries left out)
- [x] Settings every tenant already has are changed to match the source instead of created: the default anti-phishing, anti-spam, outbound spam and anti-malware policies; the tenant-wide Safe Links and Safe Attachments settings; quarantine notification settings; the Standard and Strict preset security policies (on or off, and who they apply to); the Default remote domain; the default Outlook on the web and mobile device mailbox policies. Pre-flight compares them with the destination's and shows before and after for each setting that differs; only those are changed; rolling back puts the destination's earlier values back
- [x] More settings, through Microsoft Graph and PowerShell: Entra ID user settings (Privileged Role Administrator) and the authentication methods policy with each sign-in method (Authentication Policy Administrator; pre-flight warns when a method would be switched off or narrowed); SharePoint and OneDrive sharing, sync and site creation settings (SharePoint Administrator); Endpoint DLP's tenant-wide settings (only those, not the other Purview switches in the same object); Teams org-wide (Global) meeting, messaging, calling, app setup and channels policies, external access, guest access and cloud storage, meeting settings, and guest meeting, messaging and calling settings. Each type can need its own admin role, which pre-flight checks
- [x] Exchange Online: mail flow rules (created in test mode), journal rules (created switched off), remote domains (settings New-RemoteDomain doesn't take are set with Set-RemoteDomain), Outlook on the web policies, mailbox retention tags and mailbox retention policies (MRM; built-in tags left out), and mobile device mailbox policies
- [x] Purview: DLP policies (created in simulation mode) and retention policies (created switched off, never with Preservation Lock), each with its rules; policies using adaptive scopes left out
- [x] These go through Exchange Online and Security & Compliance PowerShell hosted in the app; the source's sessions refuse anything but Get cmdlets; sessions reconnect before the token runs out
- [x] Map domains (partner domains kept, .onmicrosoft.com matched automatically), mailboxes and groups by address (rewritten with the domain's match), and SharePoint and OneDrive sites (same path in the destination's SharePoint)
- [x] Only settings a New cmdlet accepts are sent; anything it can't take is listed for the admin to set by hand; rule order isn't copied
- [x] Purview sensitivity labels (encryption, headers and footers, watermarks, site and group protection, documented advanced settings; sublabels after their parents) and label policies (labels named in a policy are linked to the labels read with it). Settings New-Label doesn't document are listed for the admin rather than sent
- [x] Teams: meeting, messaging, calling, app setup, app permission, channels and update policies through Microsoft Teams PowerShell (bundled), each with its group assignments in the source's order; built-in and org-wide (Global) policies left out; policies assigned only to single users are listed
- [x] Support log: a daily log in `%LOCALAPPDATA%\MovewiseData\Logs` with tokens, addresses, tenant names and IPs removed from every line; unexpected errors logged; "Save support log" in the sidebar
- [x] Installer and updates with Velopack: self-contained build, Setup.exe (installs WebView2 where missing), portable zip, update packages; signing through signtool or Azure Trusted Signing; an update button in the sidebar that never interrupts a deployment. See [docs/releasing.md](docs/releasing.md)
- [x] Demo mode: "Try the demo" on the Connect screen connects two built-in sample tenants (Contoso and Fabrikam) that live only in memory, so every step, including deploy and rollback, can be tried without real tenants or an app registration. Demo runs are kept in a temporary folder
- [x] "Check services" on each tenant tests Microsoft Graph, Exchange Online, Security & Compliance and Teams, and says why any of them fails
- [ ] Before the first release: a signing certificate, a download location for UpdateUrl, and a test against real tenants

## Build and run

Requires the .NET 8 SDK on Windows 10 or 11 (WebView2 is built into Windows 11).

1. Run Movewise and sign in to the destination first: that sets up Movewise's app registration there. Then sign in to the source, which approves Movewise once. Movewise only reads from the source; the app enforces that (see [docs/app-registration.md](docs/app-registration.md#why-the-source-stays-unchanged)). To use a registration you made yourself, see [docs/app-registration.md](docs/app-registration.md).
2. Bundle the Exchange Online (3.8.0 or later) and Microsoft Teams modules, needed for Defender, Exchange, Purview and Teams policies.
   ```powershell
   .\tools\Save-Modules.ps1
   ```
3. Build, test and run:
   ```powershell
   dotnet build
   dotnet test
   dotnet run --project src/Movewise.App
   ```

## Layout

| Project | What it holds |
|---|---|
| `src/Movewise.Core` | The engine, tested without a tenant: policy type registry, export, mapping, pre-flight, deployment and rollback, project and run files |
| `src/Movewise.M365` | Talking to Microsoft 365: sign-in (MSAL + WAM), Graph client with throttling retries, tenant inspection, hosted Exchange Online, Security & Compliance and Teams PowerShell |
| `src/Movewise.App` | The Windows app: WPF window hosting Blazor screens |
| `tests/Movewise.Core.Tests` | Unit tests for the engine |

Adding a policy type means adding one entry to `ResourceRegistry`: where to read and create it, how to assign it, which fields are read-only, and which fields point at other objects.

Exports are saved under `%LOCALAPPDATA%\MovewiseData\Projects`. They hold full policy settings, so they're kept out of Documents, which is often synced to OneDrive.

Each deployment is saved under `%LOCALAPPDATA%\MovewiseData\Runs` in its own folder (runs that earlier versions saved under `Documents\Movewise\Runs` are still listed, and resumed or rolled back where they are): `run.json` holds every object, its new ID and status (it is what resume and rollback work from), and `destination-before` holds the destination's policies as they were before the run.
