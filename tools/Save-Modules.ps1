<#
.SYNOPSIS
    Downloads the PowerShell modules Movewise ships inside the app, so nothing has to be installed on the admin's PC.

.DESCRIPTION
    Saves these modules from the PowerShell Gallery into src\Movewise.App\Modules. The build copies that folder next to Movewise.exe.
    - ExchangeOnlineManagement: Exchange Online, Defender for Office 365, and Security & Compliance (Purview).
      Needs 3.8.0 or later (Security & Compliance sign-in with a token).
    - MicrosoftTeams: Teams policies.
    The defaults are the versions Movewise was tested with. Any other version of each module is removed first, so
    only one ships.

.EXAMPLE
    .\tools\Save-Modules.ps1
    .\tools\Save-Modules.ps1 -ExchangeVersion 3.11.0 -TeamsVersion 8.1.0
#>
param(
    [string] $ExchangeVersion = '3.10.1',
    [string] $TeamsVersion = '8.0.0'
)

$ErrorActionPreference = 'Stop'
$destination = Join-Path $PSScriptRoot '..\src\Movewise.App\Modules'
New-Item -ItemType Directory -Force -Path $destination | Out-Null

$modules = [ordered]@{ ExchangeOnlineManagement = $ExchangeVersion; MicrosoftTeams = $TeamsVersion }
foreach ($name in $modules.Keys) {
    $folder = Join-Path $destination $name
    if (Test-Path (Join-Path $folder $modules[$name])) {
        # Already there: just take out any other version beside it.
        Get-ChildItem $folder -Directory | Where-Object Name -ne $modules[$name] | ForEach-Object {
            Write-Host "Removing $name $($_.Name) ..."
            Remove-Item $_.FullName -Recurse -Force
        }
    }
    else {
        if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
        Write-Host "Saving $name $($modules[$name]) to $destination ..."
        Save-Module -Name $name -RequiredVersion $modules[$name] -Path $destination -Repository PSGallery -Force
    }

    Get-ChildItem $folder -Directory |
        ForEach-Object { Write-Host "Bundled $name $($_.Name)" }
}
