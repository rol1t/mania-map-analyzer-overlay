[CmdletBinding()]
param(
    [string]$OutputDirectory = "artifacts\payload"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $repoRoot $OutputDirectory
$packageRoot = Join-Path $repoRoot ".packages"
$sdkVersion = "1.0.4129.50"
$sdkRoot = Join-Path $packageRoot "Microsoft.Web.WebView2.$sdkVersion"
$nupkg = Join-Path $packageRoot "Microsoft.Web.WebView2.$sdkVersion.nupkg"

if (-not (Test-Path $sdkRoot)) {
    New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/$sdkVersion" -OutFile $nupkg
    $zip = [IO.Path]::ChangeExtension($nupkg, ".zip")
    Copy-Item $nupkg $zip -Force
    Expand-Archive $zip -DestinationPath $sdkRoot -Force
}

$frameworkRoots = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319"
)
$csc = $frameworkRoots | ForEach-Object { Join-Path $_ "csc.exe" } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw ".NET Framework C# compiler was not found." }

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$core = Join-Path $sdkRoot "lib\net462\Microsoft.Web.WebView2.Core.dll"
$forms = Join-Path $sdkRoot "lib\net462\Microsoft.Web.WebView2.WinForms.dll"
$exe = Join-Path $outputPath "Mania Map Analyzer Overlay.exe"
$sourceFiles = @(Get-ChildItem (Join-Path $repoRoot "src") -Filter "*.cs" -Recurse | ForEach-Object { $_.FullName })
if ($sourceFiles.Count -eq 0) { throw "No C# source files were found." }

& $csc /nologo /target:winexe /platform:x64 /optimize+ /out:$exe `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll `
    /reference:$core /reference:$forms `
    $sourceFiles
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE." }

Copy-Item $core, $forms -Destination $outputPath -Force
Copy-Item (Join-Path $sdkRoot "runtimes\win-x64\native\WebView2Loader.dll") $outputPath -Force
Copy-Item (Join-Path $repoRoot "scripts\Update-ManiaMapAnalyzerOverlay.ps1") $outputPath -Force
Copy-Item (Join-Path $repoRoot "scripts\Update-Now.cmd") $outputPath -Force
Copy-Item (Join-Path $repoRoot "scripts\Check-Updates.cmd") $outputPath -Force
Copy-Item (Join-Path $repoRoot "assets\overlay-custom.css") $outputPath -Force
Copy-Item (Join-Path $repoRoot "README.md") $outputPath -Force
Copy-Item (Join-Path $repoRoot "LICENSE") $outputPath -Force
Copy-Item (Join-Path $repoRoot "LICENSES") $outputPath -Recurse -Force

Write-Host "Launcher 1.2.0 built at: $outputPath"
Write-Host "Run Update-ManiaMapAnalyzerOverlay.ps1 to download pinned runtime components."
