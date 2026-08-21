[CmdletBinding()]
param(
    [string]$OutputDirectory = "artifacts/payload",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
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

& $dotnet publish $projectPath `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $outputPath `
    /p:PublishSingleFile=false `
    /p:PublishTrimmed=false `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Avalonia publish failed with exit code $LASTEXITCODE." }

# The updater helper is a hidden, self-contained process used only when the GUI
# replaces itself. It is not a user-facing launcher and never opens a console.
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
Copy-Item $updaterBinary $outputPath -Force
Remove-Item $updaterOutput -Recurse -Force
Copy-Item (Join-Path $repoRoot "assets\overlay-custom.css") $outputPath -Force
$overlayAssetsSource = Join-Path $repoRoot "assets\overlay"
$overlayAssetsDestination = Join-Path $outputPath "Assets\overlay"
$analyzerAssetsSource = Join-Path $repoRoot "assets\analyzers"
$analyzerAssetsDestination = Join-Path $outputPath "Assets\analyzers"
$analyzerEngineAssetsSource = Join-Path $repoRoot "assets\analyzer-engines"
$analyzerEngineAssetsDestination = Join-Path $outputPath "Assets\analyzer-engines"
$localizationAssetsSource = Join-Path $repoRoot "assets\localization"
$localizationAssetsDestination = Join-Path $outputPath "Assets\localization"
New-Item -ItemType Directory -Force -Path $overlayAssetsDestination | Out-Null
New-Item -ItemType Directory -Force -Path $analyzerAssetsDestination | Out-Null
New-Item -ItemType Directory -Force -Path $analyzerEngineAssetsDestination | Out-Null
New-Item -ItemType Directory -Force -Path $localizationAssetsDestination | Out-Null
Copy-Item (Join-Path $overlayAssetsSource "*") $overlayAssetsDestination -Recurse -Force
Copy-Item (Join-Path $analyzerAssetsSource "*") $analyzerAssetsDestination -Recurse -Force
Copy-Item (Join-Path $analyzerEngineAssetsSource "*") $analyzerEngineAssetsDestination -Recurse -Force
Copy-Item (Join-Path $localizationAssetsSource "*") $localizationAssetsDestination -Recurse -Force
$requiredOverlayAssets = @(
    "Assets\overlay\presets\default\manifest.json",
    "Assets\overlay\presets\horizontal\manifest.json",
    "Assets\overlay\presets\companella\manifest.json"
)
foreach ($asset in $requiredOverlayAssets) {
    if (-not (Test-Path -LiteralPath (Join-Path $outputPath $asset))) {
        throw "Published package is missing overlay resource: $asset"
    }
}
$requiredAnalyzerAssets = @(
    "Assets\analyzers\mania-map-analyser\manifest.json",
    "Assets\analyzers\mania-map-analyser\adapter.js"
)
foreach ($asset in $requiredAnalyzerAssets) {
    if (-not (Test-Path -LiteralPath (Join-Path $outputPath $asset))) {
        throw "Published package is missing analyzer adapter resource: $asset"
    }
}
$requiredAnalyzerEngineAssets = @(
    "Assets\analyzer-engines\mania-map-analyser\manifest.json",
    "Assets\analyzer-engines\mania-map-analyser\runtime.mjs",
    "Assets\analyzer-engines\mania-map-analyser\worker.mjs"
)
foreach ($asset in $requiredAnalyzerEngineAssets) {
    if (-not (Test-Path -LiteralPath (Join-Path $outputPath $asset))) {
        throw "Published package is missing analyzer engine resource: $asset"
    }
}
$requiredLocalizationAssets = @(
    "Assets\localization\manifest.json",
    "Assets\localization\en.json",
    "Assets\localization\ru.json"
)
foreach ($asset in $requiredLocalizationAssets) {
    if (-not (Test-Path -LiteralPath (Join-Path $outputPath $asset))) {
        throw "Published package is missing localization resource: $asset"
    }
}
Copy-Item (Join-Path $repoRoot "README.md") $outputPath -Force
Copy-Item (Join-Path $repoRoot "LICENSE") $outputPath -Force
Copy-Item (Join-Path $repoRoot "LICENSES") $outputPath -Recurse -Force
Copy-Item (Join-Path $repoRoot "docs") $outputPath -Recurse -Force

Write-Host "Mania Map Analyzer Overlay 2.3.0 built at: $outputPath"
Write-Host "Launch the application executable; component setup runs inside the GUI."
