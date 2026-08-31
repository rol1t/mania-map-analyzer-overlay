[CmdletBinding()]
param(
    [string]$OutputDirectory = "artifacts/payload",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $repoRoot "VERSION"
if (-not (Test-Path -LiteralPath $versionFile)) {
    throw "Canonical VERSION file was not found: $versionFile"
}
$version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Canonical VERSION file is empty: $versionFile"
}
$outputPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$projectPath = Join-Path $repoRoot "src\Avalonia\ManiaMapAnalyzerOverlay.Avalonia.csproj"
$updaterProjectPath = Join-Path $repoRoot "src\Updater\ManiaMapAnalyzerOverlay.Updater.csproj"
$repoPrefix = [IO.Path]::GetFullPath($repoRoot)
if (-not $repoPrefix.EndsWith([IO.Path]::DirectorySeparatorChar)) {
    $repoPrefix += [IO.Path]::DirectorySeparatorChar
}
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
}

if (-not $outputPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Output directory must be inside the repository."
}
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Avalonia project was not found: $projectPath"
}
if (-not (Test-Path -LiteralPath $updaterProjectPath)) {
    throw "Updater project was not found: $updaterProjectPath"
}
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw ".NET 8 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0"
}

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

# Build the helper first so its single binary can be embedded in the launcher.
$updaterOutput = Join-Path $outputPath ".updater-build"
New-Item -ItemType Directory -Force -Path $updaterOutput | Out-Null
& $dotnet publish $updaterProjectPath `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $updaterOutput `
    /p:PublishSingleFile=true `
    /p:PublishTrimmed=false `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Updater publish failed with exit code $LASTEXITCODE." }
$updaterName = if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)) {
    'Mania Map Analyzer Overlay.Updater.exe'
} else {
    'Mania Map Analyzer Overlay.Updater'
}
$updaterBinary = Join-Path $updaterOutput $updaterName
if (-not (Test-Path -LiteralPath $updaterBinary)) { throw "Published updater was not found: $updaterBinary" }

& $dotnet publish $projectPath `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $outputPath `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    /p:PublishTrimmed=false `
    /p:DebugSymbols=false `
    /p:DebugType=None `
    "/p:EmbeddedUpdaterPath=$updaterBinary" `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Avalonia publish failed with exit code $LASTEXITCODE." }
Remove-Item $updaterOutput -Recurse -Force

$launcherName = if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)) {
    'Mania Map Analyzer Overlay.exe'
} else {
    'Mania Map Analyzer Overlay'
}
$launcherPath = Join-Path $outputPath $launcherName
if (-not (Test-Path -LiteralPath $launcherPath)) { throw "Published launcher was not found: $launcherPath" }
$payloadEntries = @(Get-ChildItem -LiteralPath $outputPath -Force)
if ($payloadEntries.Count -ne 1 -or $payloadEntries[0].FullName -ne $launcherPath) {
    throw "Single-file payload must contain only '$launcherName'. Found: $($payloadEntries.Name -join ', ')"
}

$verificationRoot = Join-Path ([IO.Path]::GetTempPath()) ('ManiaMapAnalyzerOverlay-PackageCheck-' + [guid]::NewGuid().ToString('N'))
try {
    & $launcherPath --verify-runtime-package $verificationRoot
    if ($LASTEXITCODE -ne 0) { throw "Single-file runtime verification failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item -LiteralPath $verificationRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Mania Map Analyzer Overlay $version built at: $outputPath"
Write-Host "Launch the application executable; component setup runs inside the GUI."
