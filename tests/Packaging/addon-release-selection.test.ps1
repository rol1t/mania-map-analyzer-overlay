$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $repoRoot 'scripts/Update-ManiaMapAnalyzerOverlay.ps1'
$parseTokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$parseTokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }

# Load only the pure selectors; never run the updater's network/install entry point.
foreach ($functionName in @('Get-ReleaseAsset', 'Get-AddonReleaseAsset')) {
    $definition = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false) |
        Where-Object Name -eq $functionName
    if (@($definition).Count -ne 1) { throw "Missing or ambiguous function: $functionName" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

foreach ($case in @(
    @{ Tag = 'v2.0.0'; Name = 'ManiaMapAnalyser.by.Leo_Black.zip' },
    @{ Tag = 'v3.0.0'; Name = 'ManiaMapAnalyser-by-Leo_Black-v3.0.0.zip' },
    @{ Tag = '3.0.0'; Name = 'ManiaMapAnalyser-by-Leo_Black-v3.0.0.zip' },
    @{ Tag = 'v3.1.0-beta.1+build.2'; Name = 'ManiaMapAnalyser-by-Leo_Black-v3.1.0-beta.1+build.2.zip' }
)) {
    $release = [pscustomobject]@{
        tag_name = $case.Tag
        assets = @(
            [pscustomobject]@{ name = 'bridges.zip' },
            [pscustomobject]@{ name = 'ManiaMapAnalyser-by-Leo_Black-v3.0.0-with-shell.zip' },
            [pscustomobject]@{ name = $case.Name }
        )
    }
    if ((Get-AddonReleaseAsset $release).name -ne $case.Name) { throw "Wrong archive selected for $($case.Tag)" }
}

foreach ($names in @(
    ,@('ManiaMapAnalyser-by-Leo_Black-v3.0.0-with-shell.zip')
    ,@('ManiaMapAnalyser-by-Leo_Black-v3.0.1.zip')
    ,@('ManiaMapAnalyser-by-Leo_Black-v3x0x0.zip')
    ,@('ManiaMapAnalyser.by.Leo_Black.zip', 'ManiaMapAnalyser-by-Leo_Black-v3.0.0.zip')
)) {
    $release = [pscustomobject]@{
        tag_name = 'v3.0.0'
        assets = @($names | ForEach-Object { [pscustomobject]@{ name = $_ } })
    }
    $rejected = $false
    try { $null = Get-AddonReleaseAsset $release }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Expected rejection for: $($names -join ', ')" }
}
Write-Output 'PowerShell addon release selection tests passed.'
