[CmdletBinding()]
param(
    [string]$Version = '2.0.0'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsPath = Join-Path $repoRoot 'artifacts'
$payloadPath = Join-Path $artifactsPath 'payload'
$stagingPath = Join-Path $artifactsPath 'installer-stage'
$archivePath = Join-Path $artifactsPath ("Mania-Map-Analyzer-Overlay-Installer-$Version.zip")

if (-not (Test-Path -LiteralPath (Join-Path $payloadPath 'Mania Map Analyzer Overlay.exe'))) {
    throw 'Build the launcher payload before packaging the installer.'
}

if (Test-Path -LiteralPath $stagingPath) {
    Remove-Item -LiteralPath $stagingPath -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-or-Update.cmd') -Destination $stagingPath -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Update-ManiaMapAnalyzerOverlay.ps1') -Destination $stagingPath -Force
Copy-Item -LiteralPath $payloadPath -Destination (Join-Path $stagingPath 'payload') -Recurse -Force

Compress-Archive -Path (Join-Path $stagingPath '*') -DestinationPath $archivePath -Force
Write-Host "Installer package created: $archivePath"
