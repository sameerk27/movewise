# Releasing Movewise

A release is a signed `Movewise-win-Setup.exe` for new installs, plus update packages that installed copies download on their own. Both are built by one script with [Velopack](https://velopack.io).

## One-time setup

1. **App registration.** Optionally, put the production client ID in `src/Movewise.App/appsettings.json` (see [app-registration.md](app-registration.md)) to build it into every release. Otherwise Movewise sets up its own registration in the destination tenant when the admin first signs in there.
2. **Where releases live.** Any HTTPS folder that serves static files works, such as an Azure Storage static website or an S3 bucket. Put its address in `appsettings.json`:
   ```json
   { "ClientId": "…", "UpdateUrl": "https://downloads.example.com/movewise" }
   ```
   With `UpdateUrl` empty, Movewise never checks for updates.
3. **Code signing.** Admins run Movewise with admin accounts to two tenants, so ship only signed builds. Unsigned installers also trigger a Windows SmartScreen warning. Either:
   - a code signing certificate (OV or EV) in the Windows certificate store, used through `signtool` with `-SignParams`, or
   - Azure Trusted Signing, with `-AzureTrustedSignFile metadata.json`.

## Each release

```powershell
.\tools\Publish.ps1 -Version 1.0.0 -SignParams '/fd sha256 /tr http://timestamp.digicert.com /td sha256 /sha1 <thumbprint>'
```

The script:

1. runs the tests, and stops if any fail,
2. bundles the tested versions of ExchangeOnlineManagement (3.10.1) and MicrosoftTeams (8.0.0) in `src\Movewise.App\Modules`, removing any other version, and stops if anything else is there (to ship newer ones once they're tested, pass `-ExchangeVersion` and `-TeamsVersion`, and update the defaults in `tools\Publish.ps1` and `tools\Save-Modules.ps1`),
3. publishes the app self-contained for 64-bit Windows, so no .NET install is needed,
4. packs it with `vpk` (restored from `.config/dotnet-tools.json`) into `artifacts\releases`, and has the installer install the WebView2 runtime where it's missing,
5. signs `Movewise.exe` and the installer.

Then upload **everything** in `artifacts\releases` to the `UpdateUrl` folder. Keep the older `.nupkg` files there as well: Velopack builds smaller delta updates from them.

The version must go up with every release, or installed copies won't see it.

## What admins see

- **Installing:** `Movewise-win-Setup.exe` installs for the current Windows user, with no admin rights needed, and adds a Start menu shortcut. A portable zip is also produced for locked-down PCs.
- **Updating:** at start, Movewise checks `UpdateUrl`. When there's a newer version, an **Update to x.y.z** button appears in the sidebar. The update downloads and Movewise restarts into it only when the admin clicks the button, and never while a deployment is running.
- **Support:** **Save support log** in the sidebar zips the last two weeks of logs from `%LOCALAPPDATA%\MovewiseData\Logs`. Settings are kept beside them, in `%LOCALAPPDATA%\MovewiseData`, outside the install folder, so updates, reinstalls and uninstalls don't touch them. Access tokens, email addresses, tenant names in Microsoft domains and IP addresses are removed from every line before it's written, and IDs are cut to their first four characters.

## Trying the packaging without signing

```powershell
.\tools\Publish.ps1 -Version 0.1.0 -SkipModules
```

This builds an unsigned release without the PowerShell modules, so it can't migrate Defender, Exchange, Purview or Teams policies. Use it only to check the packaging.

## Building on GitHub

The **Release** workflow (`.github/workflows/release.yml`) runs the same `tools\Publish.ps1` on a Windows runner, with the PowerShell modules bundled, and attaches everything in `artifacts\releases` to a **draft** release tagged `v<version>`. Start it from **Actions → Release → Run workflow**, give the version, then check the draft and press **Publish release**. It doesn't sign yet: once there's a certificate, pass `-SignParams` or `-AzureTrustedSignFile` in its "Build and pack" step, with the secrets that need.
