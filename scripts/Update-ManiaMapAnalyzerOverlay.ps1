[CmdletBinding()]
param(
    [string]$InstallPath = '',
    [switch]$CheckOnly,
    [switch]$ComponentsOnly,
    [switch]$Force,
    [switch]$Launch,
    [switch]$SelfUpdate,
    [int]$WaitForProcessId = 0,
    [switch]$Json,
    [switch]$Quiet
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if ($Quiet) { $ProgressPreference = 'SilentlyContinue' }

$updaterVersion = '2.0.0'
$githubHeaders = @{
    'User-Agent' = "ManiaMapAnalyzerOverlayUpdater/$updaterVersion"
    'Accept' = 'application/vnd.github+json'
}

function Write-Info([string]$Message) {
    if (-not $Quiet -and -not $Json) {
        Write-Host $Message
    }
}

function Get-FullSafeInstallPath([string]$PathValue) {
    $full = [System.IO.Path]::GetFullPath($PathValue).TrimEnd('\')
    $root = [System.IO.Path]::GetPathRoot($full).TrimEnd('\')
    $blocked = @(
        $root,
        [System.IO.Path]::GetFullPath($env:USERPROFILE).TrimEnd('\'),
        [System.IO.Path]::GetFullPath($env:WINDIR).TrimEnd('\')
    )

    foreach ($item in $blocked) {
        if ([string]::Equals($full, $item, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Небезопасный путь установки: $full"
        }
    }
    return $full
}

function Get-PropertyValue($Object, [string]$Name, $DefaultValue = $null) {
    if ($null -eq $Object) { return $DefaultValue }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $DefaultValue }
    return $property.Value
}

function Get-InstallState([string]$StatePath) {
    if (-not (Test-Path -LiteralPath $StatePath)) { return $null }
    try {
        return (Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json)
    }
    catch {
        return $null
    }
}

function Save-InstallState([string]$StatePath, $StateObject) {
    $jsonText = $StateObject | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($StatePath, $jsonText, (New-Object System.Text.UTF8Encoding($false)))
}

function Get-LazerVersion {
    $candidatePaths = New-Object System.Collections.Generic.List[string]

    try {
        $running = Get-CimInstance Win32_Process -Filter "Name='osu!.exe'" -ErrorAction SilentlyContinue
        foreach ($process in @($running)) {
            if ($process.ExecutablePath -and $process.ExecutablePath -match '(?i)osulazer') {
                $candidatePaths.Add([string]$process.ExecutablePath)
            }
        }
    }
    catch { }

    if ($env:LOCALAPPDATA) {
        $candidatePaths.Add((Join-Path $env:LOCALAPPDATA 'osulazer\current\osu!.exe'))
    }

    foreach ($path in $candidatePaths | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        try {
            $productVersion = (Get-Item -LiteralPath $path).VersionInfo.ProductVersion
            if ($productVersion -match '(?<version>\d{4}\.\d+\.\d+)') {
                return $Matches.version
            }
        }
        catch { }
    }
    return ''
}

function Get-LatestRelease([string]$Repository) {
    return Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases/latest" -Headers $githubHeaders -TimeoutSec 20
}

function Get-ReleaseAsset($Release, [string]$NamePattern) {
    $assets = @($Release.assets | Where-Object { $_.name -match $NamePattern })
    if ($assets.Count -ne 1) {
        throw "Не удалось однозначно выбрать файл релиза по шаблону: $NamePattern"
    }
    return $assets[0]
}

function ConvertTo-Version([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return [version]'0.0.0.0' }
    $normalized = $Value.Trim().TrimStart('v', 'V')
    try { return [version]$normalized }
    catch { return [version]'0.0.0.0' }
}

function Get-InstalledLauncherVersion([string]$TargetRoot) {
    $launcherPath = Join-Path $TargetRoot 'Mania Map Analyzer Overlay.exe'
    if (-not (Test-Path -LiteralPath $launcherPath)) { return [version]'0.0.0.0' }
    return ConvertTo-Version ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($launcherPath).FileVersion)
}

function Copy-LauncherFiles([string]$PayloadRoot, [string]$TargetRoot) {
    New-Item -ItemType Directory -Path $TargetRoot -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $PayloadRoot -Force) {
        $target = Join-Path $TargetRoot $item.Name
        if ($item.Name -eq 'overlay-custom.css' -and (Test-Path -LiteralPath $target)) {
            continue
        }
        Copy-Item -LiteralPath $item.FullName -Destination $target -Recurse -Force
    }
}

function Install-LatestLauncherRelease([string]$TargetRoot, [int]$ProcessId) {
    $release = Get-LatestRelease 'rol1t/mania-map-analyzer-overlay'
    $asset = Get-ReleaseAsset $release '^Mania-Map-Analyzer-Overlay-Installer-.*\.zip$'
    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ManiaMapAnalyzerOverlaySelfUpdate-' + [guid]::NewGuid().ToString('N'))
    $archivePath = Join-Path $temporaryRoot 'release.zip'
    $extractPath = Join-Path $temporaryRoot 'release'

    try {
        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
        Invoke-Download ([string]$asset.browser_download_url) $archivePath

        $expectedDigest = [string](Get-PropertyValue $asset 'digest' '')
        if ($expectedDigest -match '^sha256:(?<hash>[0-9a-fA-F]{64})$') {
            $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
            if (-not [string]::Equals($actualHash, $Matches.hash, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw 'SHA-256 скачанного обновления не совпадает с данными GitHub Release.'
            }
        }

        Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath -Force
        $payloadExecutables = @(Get-ChildItem -LiteralPath $extractPath -Recurse -File -Filter 'Mania Map Analyzer Overlay.exe')
        if ($payloadExecutables.Count -ne 1) { throw 'В обновлении не найден единственный исполняемый файл приложения.' }

        $payloadRoot = $payloadExecutables[0].Directory.FullName
        $payloadVersion = ConvertTo-Version $payloadExecutables[0].VersionInfo.FileVersion
        $releaseVersion = ConvertTo-Version ([string]$release.tag_name)
        if ($payloadVersion -lt $releaseVersion) { throw 'Версия приложения внутри архива ниже версии GitHub Release.' }
        if ($releaseVersion -le (Get-InstalledLauncherVersion $TargetRoot)) {
            throw 'Установленная версия приложения уже актуальна.'
        }

        if ($ProcessId -gt 0) {
            Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue
            if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
                throw 'Приложение не завершилось за 60 секунд.'
            }
        }

        Copy-LauncherFiles $payloadRoot $TargetRoot
        $launcherPath = Join-Path $TargetRoot 'Mania Map Analyzer Overlay.exe'
        Start-Process -FilePath $launcherPath -WorkingDirectory $TargetRoot
    }
    finally {
        if (Test-Path -LiteralPath $temporaryRoot) {
            Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Test-LazerOffsets([string]$Version) {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        return [pscustomobject]@{ Status = 'not-detected'; Source = ''; Error = '' }
    }

    $sources = @(
        "https://tosu.app/offsets/$Version.json",
        "https://osuck.net/offsets/$Version.json"
    )
    $errors = New-Object System.Collections.Generic.List[string]

    foreach ($source in $sources) {
        try {
            $offsets = Invoke-RestMethod -Uri $source -Headers @{ 'User-Agent' = "ManiaMapAnalyzerOverlayUpdater/$updaterVersion" } -TimeoutSec 12
            if ([string]::Equals([string]$offsets.OsuVersion, $Version, [System.StringComparison]::OrdinalIgnoreCase)) {
                return [pscustomobject]@{ Status = 'supported'; Source = $source; Error = '' }
            }
            $errors.Add("$source вернул другую версию")
        }
        catch {
            $errors.Add("${source}: $($_.Exception.Message)")
        }
    }

    return [pscustomobject]@{
        Status = 'unsupported'
        Source = ''
        Error = ($errors -join '; ')
    }
}

function Invoke-Download([string]$Url, [string]$Destination) {
    Write-Info "Скачивание: $Url"
    Invoke-WebRequest -UseBasicParsing -Uri $Url -Headers $githubHeaders -OutFile $Destination -TimeoutSec 120
    if (-not (Test-Path -LiteralPath $Destination) -or (Get-Item -LiteralPath $Destination).Length -eq 0) {
        throw "Скачан пустой файл: $Url"
    }
}

function Stop-OwnedProcess([string]$ExecutablePath) {
    if (-not (Test-Path -LiteralPath $ExecutablePath)) { return }
    $expected = [System.IO.Path]::GetFullPath($ExecutablePath)
    try {
        $processes = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
            $_.ExecutablePath -and [string]::Equals(
                [System.IO.Path]::GetFullPath([string]$_.ExecutablePath),
                $expected,
                [System.StringComparison]::OrdinalIgnoreCase)
        }
        foreach ($process in @($processes)) {
            Write-Info "Остановка процесса $($process.Name) ($($process.ProcessId))"
            Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
        }
        if (@($processes).Count -gt 0) { Start-Sleep -Milliseconds 700 }
    }
    catch {
        throw "Не удалось остановить процесс $expected`: $($_.Exception.Message)"
    }
}

function Test-WindowsEmbeddedSignature([string]$FilePath) {
    if (-not ('ManiaMapAnalyzerOverlay.WinTrust' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace ManiaMapAnalyzerOverlay
{
    public static class WinTrust
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WINTRUST_DATA trustData);

        public static bool Verify(string filePath)
        {
            IntPtr pathPointer = IntPtr.Zero;
            IntPtr fileInfoPointer = IntPtr.Zero;
            try
            {
                pathPointer = Marshal.StringToCoTaskMemUni(filePath);
                var fileInfo = new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                    pcwszFilePath = pathPointer,
                    hFile = IntPtr.Zero,
                    pgKnownSubject = IntPtr.Zero
                };

                fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

                var trustData = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    pPolicyCallbackData = IntPtr.Zero,
                    pSIPClientData = IntPtr.Zero,
                    dwUIChoice = 2,
                    fdwRevocationChecks = 0,
                    dwUnionChoice = 1,
                    pFile = fileInfoPointer,
                    dwStateAction = 0,
                    hWVTStateData = IntPtr.Zero,
                    pwszURLReference = IntPtr.Zero,
                    dwProvFlags = 0,
                    dwUIContext = 0
                };

                Guid policyGuid = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
                return WinVerifyTrust(IntPtr.Zero, ref policyGuid, ref trustData) == 0;
            }
            finally
            {
                if (fileInfoPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfoPointer);
                if (pathPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPointer);
            }
        }
    }
}
'@
    }

    return [ManiaMapAnalyzerOverlay.WinTrust]::Verify($FilePath)
}

function Install-Tosu($Asset, [string]$TargetRoot, [string]$TemporaryRoot) {
    $archive = Join-Path $TemporaryRoot 'tosu.zip'
    $extract = Join-Path $TemporaryRoot 'tosu-extract'
    Invoke-Download ([string]$Asset.browser_download_url) $archive
    Expand-Archive -LiteralPath $archive -DestinationPath $extract -Force

    $sourceFiles = @(Get-ChildItem -LiteralPath $extract -Recurse -File -Filter 'tosu.exe')
    if ($sourceFiles.Count -ne 1) { throw 'В архиве tosu не найден единственный файл tosu.exe.' }

    if (-not (Test-WindowsEmbeddedSignature $sourceFiles[0].FullName)) {
        throw 'Цифровая подпись tosu.exe не прошла проверку Windows.'
    }

    $tosuFolder = Join-Path $TargetRoot 'tosu'
    $targetExe = Join-Path $tosuFolder 'tosu.exe'
    New-Item -ItemType Directory -Path $tosuFolder -Force | Out-Null
    Stop-OwnedProcess $targetExe

    $newExe = Join-Path $tosuFolder 'tosu.exe.new'
    $previousExe = Join-Path $tosuFolder 'tosu.exe.previous'
    Copy-Item -LiteralPath $sourceFiles[0].FullName -Destination $newExe -Force
    if (Test-Path -LiteralPath $previousExe) { Remove-Item -LiteralPath $previousExe -Force }
    if (Test-Path -LiteralPath $targetExe) { Move-Item -LiteralPath $targetExe -Destination $previousExe -Force }

    try {
        Move-Item -LiteralPath $newExe -Destination $targetExe -Force
        if (Test-Path -LiteralPath $previousExe) { Remove-Item -LiteralPath $previousExe -Force }
    }
    catch {
        if ((Test-Path -LiteralPath $previousExe) -and -not (Test-Path -LiteralPath $targetExe)) {
            Move-Item -LiteralPath $previousExe -Destination $targetExe -Force
        }
        throw
    }
}

function Install-Addon($Asset, [string]$TargetRoot, [string]$TemporaryRoot) {
    $archive = Join-Path $TemporaryRoot 'addon.zip'
    $extract = Join-Path $TemporaryRoot 'addon-extract'
    Invoke-Download ([string]$Asset.browser_download_url) $archive
    Expand-Archive -LiteralPath $archive -DestinationPath $extract -Force

    $metadataFiles = @(Get-ChildItem -LiteralPath $extract -Recurse -File -Filter 'metadata.txt' | Where-Object {
        (Get-Content -LiteralPath $_.FullName -Raw) -match '(?im)^Name:\s*ManiaMapAnalyser\s*$'
    })
    if ($metadataFiles.Count -ne 1) { throw 'В архиве не найден единственный корень ManiaMapAnalyser.' }
    $sourceRoot = $metadataFiles[0].Directory.FullName

    $staticRoot = Join-Path $TargetRoot 'tosu\static'
    $target = Join-Path $staticRoot 'ManiaMapAnalyser'
    $staged = Join-Path $staticRoot 'ManiaMapAnalyser.new'
    $backupRoot = Join-Path $TargetRoot '.update-backup'
    $backup = Join-Path $backupRoot ('ManiaMapAnalyser-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

    New-Item -ItemType Directory -Path $staticRoot -Force | Out-Null
    if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Recurse -Force }
    New-Item -ItemType Directory -Path $staged -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceRoot '*') -Destination $staged -Recurse -Force

    if (Test-Path -LiteralPath $target) {
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        Move-Item -LiteralPath $target -Destination $backup
    }

    try {
        Move-Item -LiteralPath $staged -Destination $target
    }
    catch {
        if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $target)) {
            Move-Item -LiteralPath $backup -Destination $target
        }
        throw
    }
}

function Ensure-TosuEnvironment([string]$TargetRoot) {
    $envPath = Join-Path $TargetRoot 'tosu\tosu.env'
    if (Test-Path -LiteralPath $envPath) { return }
    $content = @'
DEBUG_LOG=false
ENABLE_AUTOUPDATE=false
OPEN_DASHBOARD_ON_STARTUP=false

SHOW_MP_COMMANDS=false
CALCULATE_PP=true
READ_MANIA_SCROLL_SPEED=true

ENABLE_KEY_OVERLAY=false
ENABLE_INGAME_OVERLAY=false

POLL_RATE=150
PRECISE_DATA_POLL_RATE=25

INGAME_OVERLAY_KEYBIND=Control + Shift + Space
INGAME_OVERLAY_MAX_FPS=30

SERVER_IP=127.0.0.1
SERVER_PORT=24050
ALLOWED_IPS=127.0.0.1,localhost,absolute

STATIC_FOLDER_PATH=./static
'@
    New-Item -ItemType Directory -Path (Split-Path $envPath) -Force | Out-Null
    [System.IO.File]::WriteAllText($envPath, $content, (New-Object System.Text.UTF8Encoding($false)))
}

function Ensure-LightweightAddonDefaults([string]$TargetRoot) {
    $settingsFolder = Join-Path $TargetRoot 'tosu\settings'
    $settingsPath = Join-Path $settingsFolder 'ManiaMapAnalyser.values.json'
    $shouldSeed = -not (Test-Path -LiteralPath $settingsPath)

    if (-not $shouldSeed) {
        try {
            $existing = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            $shouldSeed = @($existing.PSObject.Properties).Count -eq 0
        }
        catch {
            $shouldSeed = $true
        }
    }

    if (-not $shouldSeed) { return }

    New-Item -ItemType Directory -Path $settingsFolder -Force | Out-Null
    $defaults = [ordered]@{
        enableFloatingTriangles = $false
        enableCoverArt = $false
        cardBgBlur = 'Off'
        enableStatusMarquee = $false
        enableUpdateCheck = $false
    }
    Save-InstallState $settingsPath $defaults
}

function Install-LauncherPayload([string]$TargetRoot) {
    $payloadRoot = Join-Path $PSScriptRoot 'payload'
    $launcher = Join-Path $TargetRoot 'Mania Map Analyzer Overlay.exe'
    if (-not (Test-Path -LiteralPath $payloadRoot)) {
        throw 'Для первичной установки рядом со скриптом должна находиться папка payload.'
    }

    $payloadLauncher = Join-Path $payloadRoot 'Mania Map Analyzer Overlay.exe'
    if (-not (Test-Path -LiteralPath $payloadLauncher)) {
        throw 'В папке payload отсутствует Mania Map Analyzer Overlay.exe.'
    }

    if (Test-Path -LiteralPath $launcher) {
        try {
            $installedVersion = [version]([System.Diagnostics.FileVersionInfo]::GetVersionInfo($launcher).FileVersion)
            $payloadVersion = [version]([System.Diagnostics.FileVersionInfo]::GetVersionInfo($payloadLauncher).FileVersion)
            if ($installedVersion -ge $payloadVersion) { return $false }
            Write-Info "Обновление приложения: $installedVersion -> $payloadVersion"
        }
        catch {
            Write-Info 'Не удалось сравнить версии приложения; существующий файл сохранён.'
            return $false
        }
    }

    New-Item -ItemType Directory -Path $TargetRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $payloadRoot '*') -Destination $TargetRoot -Recurse -Force
    return $true
}

if ($SelfUpdate) {
    try {
        if ([string]::IsNullOrWhiteSpace($InstallPath)) {
            throw 'Для самообновления не указан путь установки.'
        }
        $InstallPath = Get-FullSafeInstallPath $InstallPath
        Install-LatestLauncherRelease $InstallPath $WaitForProcessId
        exit 0
    }
    catch {
        $errorPath = if ([string]::IsNullOrWhiteSpace($InstallPath)) {
            Join-Path $PSScriptRoot 'self-update-error.log'
        } else {
            Join-Path $InstallPath 'self-update-error.log'
        }
        try {
            [IO.File]::WriteAllText($errorPath, (Get-Date).ToString('s') + [Environment]::NewLine + $_.Exception, (New-Object Text.UTF8Encoding($false)))
        }
        catch { }
        exit 1
    }
}

if ([string]::IsNullOrWhiteSpace($InstallPath)) {
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Mania Map Analyzer Overlay.exe')) {
        $InstallPath = $PSScriptRoot
    }
    else {
        $InstallPath = Join-Path $PSScriptRoot 'ManiaMapAnalyzerOverlay'
    }
}

$result = [ordered]@{
    Success = $false
    UpdaterVersion = $updaterVersion
    InstallPath = ''
    LazerVersion = ''
    Compatibility = 'not-detected'
    OffsetsSource = ''
    InstalledTosu = ''
    LatestTosu = ''
    InstalledAddon = ''
    LatestAddon = ''
    TosuUpdateAvailable = $false
    AddonUpdateAvailable = $false
    UpdatedTosu = $false
    UpdatedAddon = $false
    InstalledLauncher = $false
    InstalledLauncherVersion = ''
    LatestLauncherVersion = ''
    LauncherUpdateAvailable = $false
    Error = ''
}

$temporaryRoot = ''
try {
    $InstallPath = Get-FullSafeInstallPath $InstallPath
    $result.InstallPath = $InstallPath
    $statePath = Join-Path $InstallPath 'install-state.json'
    $state = Get-InstallState $statePath

    $result.InstalledTosu = [string](Get-PropertyValue $state 'TosuVersion' '')
    $result.InstalledAddon = [string](Get-PropertyValue $state 'AddonVersion' '')
    $result.LazerVersion = Get-LazerVersion
    $installedLauncherVersion = Get-InstalledLauncherVersion $InstallPath
    $launcherRelease = Get-LatestRelease 'rol1t/mania-map-analyzer-overlay'
    $latestLauncherVersion = ConvertTo-Version ([string]$launcherRelease.tag_name)
    $result.InstalledLauncherVersion = $installedLauncherVersion.ToString()
    $result.LatestLauncherVersion = $latestLauncherVersion.ToString()
    $result.LauncherUpdateAvailable = $latestLauncherVersion -gt $installedLauncherVersion

    # The packaged launcher update is local and must not depend on GitHub being
    # reachable. This also lets an installer repair/update the shell while the
    # online component check is temporarily rate-limited or unavailable.
    if (-not $CheckOnly -and -not $ComponentsOnly) {
        $result.InstalledLauncher = Install-LauncherPayload $InstallPath
    }

    Write-Info 'Проверка последних официальных релизов...'
    $tosuRelease = Get-LatestRelease 'tosuapp/tosu'
    $addonRelease = Get-LatestRelease 'LeoBlackMT/osumania_map_analyser'
    $tosuAsset = Get-ReleaseAsset $tosuRelease '^tosu-windows-v.*\.zip$'
    $addonAsset = Get-ReleaseAsset $addonRelease '^ManiaMapAnalyser\.by\.Leo_Black\.zip$'

    $result.LatestTosu = [string]$tosuRelease.tag_name
    $result.LatestAddon = [string]$addonRelease.tag_name
    $tosuExe = Join-Path $InstallPath 'tosu\tosu.exe'
    $addonMetadata = Join-Path $InstallPath 'tosu\static\ManiaMapAnalyser\metadata.txt'
    $result.TosuUpdateAvailable = $Force -or -not (Test-Path -LiteralPath $tosuExe) -or $result.InstalledTosu -ne $result.LatestTosu
    $result.AddonUpdateAvailable = $Force -or -not (Test-Path -LiteralPath $addonMetadata) -or $result.InstalledAddon -ne $result.LatestAddon

    $offsetStatus = Test-LazerOffsets $result.LazerVersion
    $result.Compatibility = $offsetStatus.Status
    $result.OffsetsSource = $offsetStatus.Source

    if (-not $CheckOnly) {
        if ($ComponentsOnly -and -not (Test-Path -LiteralPath $InstallPath)) {
            New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
        }

        Ensure-TosuEnvironment $InstallPath
        $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ManiaMapAnalyzerOverlayUpdater-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null

        if ($result.TosuUpdateAvailable) {
            Write-Info "Обновление tosu: $($result.InstalledTosu) -> $($result.LatestTosu)"
            Install-Tosu $tosuAsset $InstallPath $temporaryRoot
            $result.UpdatedTosu = $true
            $result.InstalledTosu = $result.LatestTosu
        }
        if ($result.AddonUpdateAvailable) {
            Write-Info "Обновление ManiaMapAnalyser: $($result.InstalledAddon) -> $($result.LatestAddon)"
            Install-Addon $addonAsset $InstallPath $temporaryRoot
            $result.UpdatedAddon = $true
            $result.InstalledAddon = $result.LatestAddon
        }

        Ensure-LightweightAddonDefaults $InstallPath

        $updateScriptTarget = Join-Path $InstallPath 'Update-ManiaMapAnalyzerOverlay.ps1'
        if (-not [string]::Equals(
            [System.IO.Path]::GetFullPath($PSCommandPath),
            [System.IO.Path]::GetFullPath($updateScriptTarget),
            [System.StringComparison]::OrdinalIgnoreCase)) {
            Copy-Item -LiteralPath $PSCommandPath -Destination $updateScriptTarget -Force
        }

        $stateObject = [ordered]@{
            SchemaVersion = 1
            LauncherVersion = if (Test-Path -LiteralPath (Join-Path $InstallPath 'Mania Map Analyzer Overlay.exe')) {
                (Get-Item -LiteralPath (Join-Path $InstallPath 'Mania Map Analyzer Overlay.exe')).VersionInfo.ProductVersion
            } else { '' }
            TosuVersion = $result.InstalledTosu
            AddonVersion = $result.InstalledAddon
            LazerVersion = $result.LazerVersion
            Compatibility = $result.Compatibility
            OffsetsSource = $result.OffsetsSource
            LastCheckUtc = [DateTime]::UtcNow.ToString('o')
            UpdatedUtc = [DateTime]::UtcNow.ToString('o')
        }
        Save-InstallState $statePath $stateObject
    }

    $result.Success = $true
    if ($Launch -and -not $CheckOnly) {
        $launcherPath = Join-Path $InstallPath 'Mania Map Analyzer Overlay.exe'
        if (Test-Path -LiteralPath $launcherPath) {
            Start-Process -FilePath $launcherPath -WorkingDirectory $InstallPath
        }
    }
}
catch {
    $result.Error = $_.Exception.Message
    if (-not $Json) {
        Write-Host "Ошибка: $($result.Error)" -ForegroundColor Red
    }
}
finally {
    if ($temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) {
        $tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        $resolvedTemp = [System.IO.Path]::GetFullPath($temporaryRoot)
        if ($resolvedTemp.StartsWith($tempBase, [System.StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTemp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($Json) {
    $result | ConvertTo-Json -Depth 8 -Compress
}
elseif ($result.Success) {
    Write-Host ''
    Write-Host "Установка: $($result.InstallPath)"
    Write-Host "tosu: $($result.InstalledTosu) (последняя: $($result.LatestTosu))"
    Write-Host "ManiaMapAnalyser: $($result.InstalledAddon) (последняя: $($result.LatestAddon))"
    if ($result.LazerVersion) {
        Write-Host "osu!lazer: $($result.LazerVersion) — $($result.Compatibility)"
    }
    else {
        Write-Host 'osu!lazer: не обнаружен'
    }
}

if (-not $result.Success) { exit 1 }
