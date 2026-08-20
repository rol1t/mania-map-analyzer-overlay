[CmdletBinding()]
param(
    [string]$Version = '2.2.0',
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsPath = Join-Path $repoRoot 'artifacts'
$payloadPath = Join-Path $artifactsPath 'payload'
$stagingPath = Join-Path $artifactsPath ('installer-stage-' + $Version + '-' + [guid]::NewGuid().ToString('N'))
$archiveExtension = if ($RuntimeIdentifier.StartsWith('linux-', [StringComparison]::OrdinalIgnoreCase)) { '.tar.gz' } else { '.zip' }
$archivePath = Join-Path $artifactsPath ("Mania-Map-Analyzer-Overlay-$Version-$RuntimeIdentifier$archiveExtension")
$launcherName = if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)) {
    'Mania Map Analyzer Overlay.exe'
} else {
    'Mania Map Analyzer Overlay'
}

if (-not (Test-Path -LiteralPath (Join-Path $payloadPath $launcherName))) {
    throw 'Build the launcher payload before packaging the application package.'
}

New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null

# The application itself is now the only user-facing entry point. Component
# installation and update checks run in the Avalonia GUI, so no cmd/PowerShell
# launcher is included in the release archive.
Copy-Item -Path (Join-Path $payloadPath '*') -Destination $stagingPath -Recurse -Force

try {
    if ($archiveExtension -eq '.tar.gz') {
        if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
        & tar -czf $archivePath -C $stagingPath .
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE." }
    }
    else {
        Compress-Archive -Path (Join-Path $stagingPath '*') -DestinationPath $archivePath -Force
    }
    Write-Host "Application package created: $archivePath"
}
finally {
    Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
}
