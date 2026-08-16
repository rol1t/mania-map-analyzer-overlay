[CmdletBinding()]
param(
    [string]$OutputDirectory = "artifacts\payload",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$projectPath = Join-Path $repoRoot "src\Avalonia\ManiaMapAnalyzerOverlay.Avalonia.csproj"
$repoPrefix = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
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
Copy-Item (Join-Path $repoRoot "scripts\Update-ManiaMapAnalyzerOverlay.ps1") $outputPath -Force
Copy-Item (Join-Path $repoRoot "scripts\Update-Now.cmd") $outputPath -Force
Copy-Item (Join-Path $repoRoot "scripts\Check-Updates.cmd") $outputPath -Force
Copy-Item (Join-Path $repoRoot "assets\overlay-custom.css") $outputPath -Force
Copy-Item (Join-Path $repoRoot "README.md") $outputPath -Force
Copy-Item (Join-Path $repoRoot "LICENSE") $outputPath -Force
Copy-Item (Join-Path $repoRoot "LICENSES") $outputPath -Recurse -Force
Copy-Item (Join-Path $repoRoot "docs") $outputPath -Recurse -Force

Write-Host "Avalonia launcher 2.0.0 built at: $outputPath"
Write-Host "Run Install-or-Update.cmd to download the pinned tosu and ManiaMapAnalyser components."
