using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

/// <summary>
/// Installs and updates the external tosu and ManiaMapAnalyser components.
/// The GUI is the only runtime entry point; PowerShell and cmd files are not
/// required for first launch, component repair, or compatibility checks.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string LauncherRepository = "rol1t/mania-map-analyzer-overlay";
    private const string TosuRepository = "tosuapp/tosu";
    private const string AddonRepository = "LeoBlackMT/osumania_map_analyser";
    private static string ProductVersion =>
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    private static string UserAgent => $"ManiaMapAnalyzerOverlay/{ProductVersion}";

    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GitHubReleaseClient _releaseClient;
    private readonly ComponentDownloader _componentDownloader;
    private readonly ComponentInstaller _componentInstaller;
    private readonly UpdateStateStore _stateStore;
    private bool _disposed;

    public UpdateService()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ManiaMapAnalyzerOverlay", ProductVersion));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _releaseClient = new GitHubReleaseClient(_httpClient, _jsonOptions);
        _componentDownloader = new ComponentDownloader(_httpClient, UserAgent);
        _componentInstaller = new ComponentInstaller(
            AppPaths.TosuDirectory,
            GetTosuExecutableName(),
            _componentDownloader);
        _stateStore = new UpdateStateStore(
            AppPaths.InstallStatePath,
            Path.Combine(AppPaths.BaseDirectory, "install-state.json"),
            AppPaths.DataDirectory,
            _jsonOptions);
    }

    // Kept for compatibility with callers of the previous script-backed service.
    public bool IsInstalled => true;

    public async Task<UpdateResult> CheckComponentsAsync(
        CancellationToken cancellationToken = default,
        IProgress<UpdateProgress>? progress = null)
    {
        ThrowIfDisposed();
        var result = new UpdateResult();
        var state = await _stateStore.LoadAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            progress?.Report(new UpdateProgress("status.update_checking"));

            var launcherVersion = GetCurrentLauncherVersion();
            result.InstalledLauncherVersion = launcherVersion.ToString();

            // A rate-limited launcher endpoint must never prevent already-installed
            // components from starting.
            try
            {
                var launcherRelease = await _releaseClient.GetLatestAsync(LauncherRepository, cancellationToken);
                var latestLauncherVersion = ParseVersion(launcherRelease.TagName);
                result.LatestLauncherVersion = latestLauncherVersion.ToString();
                result.LauncherUpdateAvailable = latestLauncherVersion > launcherVersion;
            }
            catch (Exception exception) when (IsRecoverableNetworkError(exception))
            {
                AppLogger.Warning("Checking launcher release", "The launcher release check failed.", exception);
                result.Warning = "update.warning_launcher";
            }

            if (!TryGetTosuAssetPattern(out var tosuPattern))
            {
                result.Compatibility = "unsupported-platform";
                result.Success = true;
                result.Warning = "update.warning_platform";
                var unsupportedState = state.Clone();
                unsupportedState.LastCheckUtc = DateTime.UtcNow;
                await _stateStore.SaveAsync(unsupportedState, cancellationToken);
                return result;
            }

            GitHubRelease tosuRelease;
            GitHubRelease addonRelease;
            try
            {
                tosuRelease = await _releaseClient.GetLatestAsync(TosuRepository, cancellationToken);
                addonRelease = await _releaseClient.GetLatestAsync(AddonRepository, cancellationToken);
            }
            catch (Exception exception) when (IsRecoverableNetworkError(exception))
            {
                AppLogger.Warning("Checking component releases", "The component release check failed.", exception);
                if (HasUsableComponents())
                {
                    result.Success = true;
                    result.Warning = "update.warning_offline_components";
                    result.InstalledTosu = state.TosuVersion;
                    result.InstalledAddon = state.AddonVersion;
                    return result;
                }

                throw new InvalidOperationException(
                    "The component releases could not be reached. Connect to the internet and try again.", exception);
            }

            var tosuAsset = FindAsset(tosuRelease, tosuPattern);
            var addonAsset = FindAsset(addonRelease, "^ManiaMapAnalyser\\.by\\.Leo_Black\\.zip$");
            if (tosuAsset is null)
            {
                throw new InvalidOperationException("The latest tosu release does not contain a compatible archive for this platform.");
            }

            if (addonAsset is null)
            {
                throw new InvalidOperationException("The latest ManiaMapAnalyser release does not contain its archive.");
            }

            result.LatestTosu = tosuRelease.TagName;
            result.LatestAddon = addonRelease.TagName;
            result.InstalledTosu = state.TosuVersion;
            result.InstalledAddon = state.AddonVersion;

            var tosuExecutable = Path.Combine(AppPaths.TosuDirectory, GetTosuExecutableName());
            var addonMetadata = Path.Combine(AppPaths.TosuDirectory, "static", "ManiaMapAnalyser", "metadata.txt");
            // A legacy portable executable remains an offline fallback, but a
            // successful online bootstrap always migrates to the writable
            // per-user component directory.
            result.TosuUpdateAvailable = !File.Exists(tosuExecutable) ||
                !string.Equals(state.TosuVersion, tosuRelease.TagName, StringComparison.OrdinalIgnoreCase);
            result.AddonUpdateAvailable = !File.Exists(addonMetadata) ||
                !string.Equals(state.AddonVersion, addonRelease.TagName, StringComparison.OrdinalIgnoreCase);

            result.LazerVersion = DetectLazerVersion();
            var offsetStatus = await CheckLazerOffsetsAsync(result.LazerVersion, cancellationToken);
            result.Compatibility = offsetStatus.Status;
            result.OffsetsSource = offsetStatus.Source;

            var needsInstall = result.TosuUpdateAvailable || result.AddonUpdateAvailable || !File.Exists(AppPaths.TosuEnvironmentPath);
            if (needsInstall)
            {
                var temporaryRoot = Path.Combine(Path.GetTempPath(), "ManiaMapAnalyzerOverlay-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(temporaryRoot);
                    if (result.TosuUpdateAvailable)
                    {
                        progress?.Report(new UpdateProgress($"status.update_tosu_download|{tosuRelease.TagName}", 0));
                        await _componentInstaller.InstallTosuAsync(
                            tosuAsset,
                            temporaryRoot,
                            new Progress<int>(p => progress?.Report(new UpdateProgress("status.update_tosu_download", p))),
                            cancellationToken);
                        result.UpdatedTosu = true;
                        result.InstalledTosu = tosuRelease.TagName;
                    }

                    if (result.AddonUpdateAvailable)
                    {
                        progress?.Report(new UpdateProgress($"status.update_analyser_download|{addonRelease.TagName}", 0));
                        await _componentInstaller.InstallAddonAsync(
                            addonAsset,
                            temporaryRoot,
                            new Progress<int>(p => progress?.Report(new UpdateProgress("status.update_analyser_download", p))),
                            cancellationToken);
                        result.UpdatedAddon = true;
                        result.InstalledAddon = addonRelease.TagName;
                    }
                }
                finally
                {
                    TryDeleteDirectory(temporaryRoot);
                }
            }

            EnsureTosuEnvironment();
            EnsureLightweightAddonDefaults();
            var savedState = state.Clone();
            savedState.LauncherVersion = GetCurrentLauncherVersion().ToString();
            savedState.TosuVersion = result.InstalledTosu;
            savedState.AddonVersion = result.InstalledAddon;
            savedState.LazerVersion = result.LazerVersion ?? "";
            savedState.Compatibility = result.Compatibility;
            savedState.OffsetsSource = result.OffsetsSource;
            savedState.LastCheckUtc = DateTime.UtcNow;
            if (result.UpdatedTosu || result.UpdatedAddon)
            {
                savedState.UpdatedUtc = DateTime.UtcNow;
            }

            await _stateStore.SaveAsync(savedState, cancellationToken);

            result.Success = true;
            progress?.Report(new UpdateProgress(
                result.UpdatedTosu || result.UpdatedAddon ? "status.update_ready" : "status.update_current", 100));
        }
        catch (OperationCanceledException exception)
        {
            AppLogger.Info("Preparing external components", $"Operation canceled: {exception.Message}");
            throw;
        }
        catch (Exception exception)
        {
            AppLogger.Error("Preparing external components", exception);
            result.Success = false;
            result.Error = exception.Message;
            progress?.Report(new UpdateProgress("status.update_failed"));
        }

        return result;
    }

    /// <summary>Starts a helper which waits for this process and applies a launcher update.</summary>
    public bool StartSelfUpdate()
    {
        // Windows release archives can currently be replaced safely by the
        // helper. Linux packages are updated by replacing the AppImage/tar
        // package until a package-manager-specific updater is introduced.
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!File.Exists(AppPaths.UpdaterExecutablePath))
        {
            return false;
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.UpdaterExecutablePath,
                Arguments = "--pid " + Environment.ProcessId + " --install-dir " + Quote(AppPaths.BaseDirectory),
                WorkingDirectory = AppPaths.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return process is not null;
        }
        catch (Exception exception)
        {
            AppLogger.Error("Starting launcher update", exception);
            return false;
        }
    }

    private async Task<OffsetStatus> CheckLazerOffsetsAsync(string? version, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return new OffsetStatus("not-detected", "");
        }

        foreach (var source in new[] { $"https://tosu.app/offsets/{version}.json", $"https://osuck.net/offsets/{version}.json" })
        {
            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                using var response = await _httpClient.GetAsync(source, requestTimeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var offsets = await JsonSerializer.DeserializeAsync<OffsetResponse>(stream, _jsonOptions, cancellationToken);
                if (offsets is not null && string.Equals(offsets.OsuVersion, version, StringComparison.OrdinalIgnoreCase))
                {
                    return new OffsetStatus("supported", source);
                }
            }
            catch (HttpRequestException exception)
            {
                AppLogger.Warning("Checking osu!lazer compatibility", "The compatibility endpoint could not be reached.", exception);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                AppLogger.Warning("Checking osu!lazer compatibility", "The compatibility request timed out.", exception);
            }
        }
        return new OffsetStatus("unsupported", "");
    }

    private void EnsureTosuEnvironment()
    {
        var path = AppPaths.TosuEnvironmentPath;
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(AppPaths.TosuDirectory);
        File.WriteAllText(path, """
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
""", new UTF8Encoding(false));
    }

    private void EnsureLightweightAddonDefaults()
    {
        var folder = Path.Combine(AppPaths.TosuDirectory, "settings");
        var path = Path.Combine(folder, "ManiaMapAnalyser.values.json");
        var shouldSeed = !File.Exists(path);
        if (!shouldSeed)
        {
            try
            {
                shouldSeed = File.ReadAllText(path).Trim() is "" or "{}";
            }
            catch (Exception exception)
            {
                AppLogger.Error("Reading ManiaMapAnalyser defaults", exception);
                shouldSeed = true;
            }
        }
        if (!shouldSeed)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        File.WriteAllText(path, """
{
  "enableFloatingTriangles": false,
  "enableCoverArt": false,
  "cardBgBlur": "Off",
  "enableStatusMarquee": false,
  "enableUpdateCheck": false
}
""", new UTF8Encoding(false));
    }

    private bool HasUsableComponents() =>
        File.Exists(Path.Combine(AppPaths.TosuDirectory, GetTosuExecutableName())) ||
        File.Exists(Path.Combine(AppPaths.LegacyTosuDirectory, GetTosuExecutableName()));

    private static GitHubAsset? FindAsset(GitHubRelease release, string pattern)
    {
        var assets = release.Assets.Where(asset => System.Text.RegularExpressions.Regex.IsMatch(asset.Name, pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
        return assets.Length == 1 ? assets[0] : null;
    }

    private static bool TryGetTosuAssetPattern(out string pattern)
    {
        if (OperatingSystem.IsWindows())
        {
            pattern = "^tosu-windows-v.*\\.zip$";
            return true;
        }
        if (OperatingSystem.IsLinux())
        {
            pattern = "^tosu-linux-v.*\\.zip$";
            return true;
        }
        pattern = "";
        return false;
    }

    private static string GetTosuExecutableName() => OperatingSystem.IsWindows() ? "tosu.exe" : "tosu";

    private static string DetectLazerVersion()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                foreach (var process in Process.GetProcessesByName("osu!"))
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (path?.Contains("osulazer", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            candidates.Add(path);
                        }
                    }
                    catch (Exception exception)
                    {
                        AppLogger.Warning("Detecting osu!lazer version", "A process module could not be inspected.", exception);
                    }
                    finally { process.Dispose(); }
                }
            }
            catch (Exception exception)
            {
                AppLogger.Warning("Detecting osu!lazer version", "The osu!lazer process list could not be read.", exception);
            }
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(localAppData, "osulazer", "current", "osu!.exe"));
        }

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "";
                var match = System.Text.RegularExpressions.Regex.Match(productVersion, @"(?<version>\d{4}\.\d+\.\d+)");
                if (match.Success)
                {
                    return match.Groups["version"].Value;
                }
            }
            catch (Exception exception)
            {
                AppLogger.Warning("Reading osu!lazer version", "The executable version could not be read.", exception);
            }
        }
        return "";
    }

    private static Version GetCurrentLauncherVersion() => ParseVersion(Assembly.GetExecutingAssembly().GetName().Version?.ToString());

    private static Version ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Version(0, 0, 0, 0);
        }

        var normalized = value.Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out var version) ? version : new Version(0, 0, 0, 0);
    }

    private static bool IsRecoverableNetworkError(Exception exception) => exception is HttpRequestException or TaskCanceledException or IOException;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) { AppLogger.Warning($"Deleting file '{path}'", "Cleanup failed.", exception); }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) { AppLogger.Warning($"Deleting directory '{path}'", "Cleanup failed.", exception); }
    }
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(UpdateService));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }

    private sealed record OffsetStatus(string Status, string Source);

    private sealed class OffsetResponse
    {
        [JsonPropertyName("OsuVersion")]
        public string? OsuVersion
        {
            get; set;
        }
    }
}

public sealed class UpdateProgress
{
    public UpdateProgress(string message, int? percent = null)
    {
        Message = message;
        Percent = percent;
    }
    public string Message
    {
        get;
    }
    public int? Percent
    {
        get;
    }
}

public sealed class InstallState
{
    public int SchemaVersion { get; set; } = 1;
    public string LauncherVersion { get; set; } = "";
    public string TosuVersion { get; set; } = "";
    public string AddonVersion { get; set; } = "";
    public string LazerVersion { get; set; } = "";
    public string Compatibility { get; set; } = "not-detected";
    public string OffsetsSource { get; set; } = "";
    public DateTime LastCheckUtc
    {
        get; set;
    }
    public DateTime UpdatedUtc
    {
        get; set;
    }

    public InstallState Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        LauncherVersion = LauncherVersion,
        TosuVersion = TosuVersion,
        AddonVersion = AddonVersion,
        LazerVersion = LazerVersion,
        Compatibility = Compatibility,
        OffsetsSource = OffsetsSource,
        LastCheckUtc = LastCheckUtc,
        UpdatedUtc = UpdatedUtc
    };
}

public sealed class UpdateResult
{
    public bool Success
    {
        get; set;
    }
    public string? Error
    {
        get; set;
    }
    public string? Warning
    {
        get; set;
    }
    public string Compatibility { get; set; } = "not-detected";
    public string OffsetsSource { get; set; } = "";
    public bool LauncherUpdateAvailable
    {
        get; set;
    }
    public string? LatestLauncherVersion
    {
        get; set;
    }
    public string InstalledLauncherVersion { get; set; } = "";
    public bool UpdatedTosu
    {
        get; set;
    }
    public bool UpdatedAddon
    {
        get; set;
    }
    public string? LatestTosu
    {
        get; set;
    }
    public string? LatestAddon
    {
        get; set;
    }
    public string InstalledTosu { get; set; } = "";
    public string InstalledAddon { get; set; } = "";
    public bool TosuUpdateAvailable
    {
        get; set;
    }
    public bool AddonUpdateAvailable
    {
        get; set;
    }
    public string? LazerVersion
    {
        get; set;
    }
}
