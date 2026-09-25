<#
.SYNOPSIS
    Builds a Movewise release: a self-contained app, a Setup.exe installer, and the update packages.

.DESCRIPTION
    1. Runs the tests; a failing test stops the release.
    2. Bundles the tested versions of the PowerShell modules (tools\Save-Modules.ps1), and removes any other version.
    3. Publishes the app self-contained for 64-bit Windows, so the admin's PC needs no .NET install.
    4. Packs it with Velopack (vpk, restored from .config\dotnet-tools.json): Movewise-win-Setup.exe, a portable zip,
       and the full and delta update packages.
    5. Signs Movewise.exe and the installer when signing details are given. Unsigned builds trigger a Windows
       SmartScreen warning, so sign every build you hand to an admin.

    Upload everything in the output folder to the address in appsettings.json UpdateUrl; installed copies
    pick the new version up from there. See docs\releasing.md.

.PARAMETER Version
    The release version, such as 1.0.0. Must be higher than the last release for updates to find it.

.PARAMETER SignParams
    Arguments for signtool.exe, for example:
    '/fd sha256 /tr http://timestamp.digicert.com /td sha256 /sha1 <certificate thumbprint>'

.PARAMETER AzureTrustedSignFile
    A metadata.json for Azure Trusted Signing, instead of SignParams.

.PARAMETER ExchangeVersion
    The ExchangeOnlineManagement version to ship. Defaults to the one Movewise was tested with.

.PARAMETER TeamsVersion
    The MicrosoftTeams version to ship. Defaults to the one Movewise was tested with.

.PARAMETER SkipModules
    Packs without bundling the PowerShell modules. Only for trying the packaging: such a build can't migrate
    Defender, Exchange, Purview or Teams policies.

.EXAMPLE
    .\tools\Publish.ps1 -Version 1.0.0 -SignParams '/fd sha256 /tr http://timestamp.digicert.com /td sha256 /sha1 0123456789ABCDEF'
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $SignParams,
    [string] $AzureTrustedSignFile,
    [string] $ExchangeVersion = '3.10.1',
    [string] $TeamsVersion = '8.0.0',
    [switch] $SkipModules,
    [string] $Output = (Join-Path $PSScriptRoot '..\artifacts\releases')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$app = Join-Path $root 'src\Movewise.App'
$publish = Join-Path $root 'artifacts\publish'

Write-Host "Running the tests ..."
dotnet test (Join-Path $root 'Movewise.sln') -c Release
if ($LASTEXITCODE) { throw "The tests failed, so nothing was packed." }

if (-not $SkipModules) {
    # Exactly the tested versions: Save-Modules downloads what's missing and removes any other version beside it.
    & (Join-Path $PSScriptRoot 'Save-Modules.ps1') -ExchangeVersion $ExchangeVersion -TeamsVersion $TeamsVersion
    foreach ($module in @{ ExchangeOnlineManagement = $ExchangeVersion; MicrosoftTeams = $TeamsVersion }.GetEnumerator()) {
        $versions = @(Get-ChildItem (Join-Path $app "Modules\$($module.Key)") -Directory | ForEach-Object Name)
        if ($versions.Count -ne 1 -or $versions[0] -ne $module.Value) {
            throw "$($module.Key) should be $($module.Value) only, but src\Movewise.App\Modules has: $($versions -join ', ')."
        }
    }
}

Write-Host "Publishing Movewise $Version ..."
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $app -c Release -r win-x64 --self-contained -p:Version=$Version -o $publish
if ($LASTEXITCODE) { throw "dotnet publish failed." }

# The modules run in Movewise's process, where .NET loads one version of each assembly. For the sign-in libraries,
# which Movewise ships itself (PowerShell doesn't), the app's copy has to be at least as new as any module's, or the
# module fails with "Could not load file or assembly". Only the modules' .NET Core copies are loaded, so only those count.
$signIn = '^(Microsoft\.Identity\.Client.*|Microsoft\.IdentityModel\..*|System\.IdentityModel\.Tokens\.Jwt)\.dll$'
$tooOld = foreach ($dll in Get-ChildItem (Join-Path $publish 'Modules') -Recurse -Filter '*.dll' -ErrorAction SilentlyContinue) {
    if ($dll.Name -notmatch $signIn -or $dll.FullName -match '\\(netFramework|net4\d+)\\') { continue }
    $appCopy = Join-Path $publish $dll.Name
    if (-not (Test-Path $appCopy)) { continue }
    try {
        $need = [Reflection.AssemblyName]::GetAssemblyName($dll.FullName).Version
        $have = [Reflection.AssemblyName]::GetAssemblyName($appCopy).Version
        if ($need -gt $have) { "$($dll.Name): Movewise has $have, $($dll.Directory.FullName.Substring($publish.Length)) needs $need" }
    } catch { }
}
if ($tooOld) {
    throw "Update these package references in src\Movewise.App\Movewise.App.csproj:`n" + (($tooOld | Sort-Object -Unique) -join "`n")
}

Push-Location $root
try {
    dotnet tool restore
    if ($LASTEXITCODE) { throw "Couldn't restore the vpk tool." }

    $pack = @(
        'vpk', 'pack',
        '--packId', 'Movewise',
        '--packVersion', $Version,
        '--packDir', $publish,
        '--mainExe', 'Movewise.exe',
        '--packTitle', 'Movewise',
        '--packAuthors', 'Movewise',
        '--framework', 'webview2',
        '--outputDir', $Output
    )
    if ($SignParams) { $pack += @('--signParams', $SignParams) }
    elseif ($AzureTrustedSignFile) { $pack += @('--azureTrustedSignFile', $AzureTrustedSignFile) }
    else { Write-Warning 'Not signed: Windows SmartScreen will warn admins who run this installer.' }

    dotnet @pack
    if ($LASTEXITCODE) { throw "vpk pack failed." }
}
finally {
    Pop-Location
}

Write-Host "Release $Version is in $Output"
