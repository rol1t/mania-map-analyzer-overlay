using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
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
    private const string UserAgent = "ManiaMapAnalyzerOverlay/2.1.0";

    private readonly HttpClient httpClient;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    private bool disposed;

    public UpdateService()
    {
        httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ManiaMapAnalyzerOverlay", "2.1.0"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    // Kept for compatibility with callers of the previous script-backed service.
    public bool IsInstalled => true;

    public async Task<UpdateResult> CheckComponentsAsync(
        CancellationToken cancellationToken = default,
        IProgress<UpdateProgress>? progress = null)
    {
        ThrowIfDisposed();
        var result = new UpdateResult();
        var state = await LoadStateAsync(cancellationToken);

        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            progress?.Report(new UpdateProgress("Checking component releases…"));

            var launcherVersion = GetCurrentLauncherVersion();
            result.InstalledLauncherVersion = launcherVersion.ToString();

            // A rate-limited launcher endpoint must never prevent already-installed
            // components from starting.
            try
            {
                var launcherRelease = await GetLatestReleaseAsync(LauncherRepository, cancellationToken);
                var latestLauncherVersion = ParseVersion(launcherRelease.TagName);
                result.LatestLauncherVersion = latestLauncherVersion.ToString();
                result.LauncherUpdateAvailable = latestLauncherVersion > launcherVersion;
            }
            catch (Exception exception) when (IsRecoverableNetworkError(exception))
            {
                result.Warning = "The launcher update check could not be completed.";
            }

            if (!TryGetTosuAssetPattern(out var tosuPattern))
            {
                result.Compatibility = "unsupported-platform";
                result.Success = true;
                result.Warning = "No compatible tosu release is published for this platform yet.";
                var unsupportedState = state.Clone();
                unsupportedState.LastCheckUtc = DateTime.UtcNow;
                await SaveStateAsync(unsupportedState, cancellationToken);
                return result;
            }

            GitHubRelease tosuRelease;
            GitHubRelease addonRelease;
            try
            {
                tosuRelease = await GetLatestReleaseAsync(TosuRepository, cancellationToken);
                addonRelease = await GetLatestReleaseAsync(AddonRepository, cancellationToken);
            }
            catch (Exception exception) when (IsRecoverableNetworkError(exception))
            {
                if (HasUsableComponents())
                {
                    result.Success = true;
                    result.Warning = "Release check unavailable; using the installed components.";
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
                throw new InvalidOperationException("The latest tosu release does not contain a compatible archive for this platform.");
            if (addonAsset is null)
                throw new InvalidOperationException("The latest ManiaMapAnalyser release does not contain its archive.");

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
                        progress?.Report(new UpdateProgress($"Downloading tosu {tosuRelease.TagName}…", 0));
                        await InstallTosuAsync(tosuAsset, temporaryRoot, cancellationToken,
                            new Progress<int>(p => progress?.Report(new UpdateProgress("Downloading tosu…", p))));
                        result.UpdatedTosu = true;
                        result.InstalledTosu = tosuRelease.TagName;
                    }

                    if (result.AddonUpdateAvailable)
                    {
                        progress?.Report(new UpdateProgress($"Downloading ManiaMapAnalyser {addonRelease.TagName}…", 0));
                        await InstallAddonAsync(addonAsset, temporaryRoot, cancellationToken,
                            new Progress<int>(p => progress?.Report(new UpdateProgress("Downloading ManiaMapAnalyser…", p))));
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
            if (result.UpdatedTosu || result.UpdatedAddon) savedState.UpdatedUtc = DateTime.UtcNow;
            await SaveStateAsync(savedState, cancellationToken);

            result.Success = true;
            progress?.Report(new UpdateProgress(
                result.UpdatedTosu || result.UpdatedAddon ? "Components are ready." : "Components are up to date.", 100));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            result.Success = false;
            result.Error = exception.Message;
            progress?.Report(new UpdateProgress("Component preparation failed."));
        }

        return result;
    }

    /// <summary>Starts a helper which waits for this process and applies a launcher update.</summary>
    public bool StartSelfUpdate()
    {
        // Windows release archives can currently be replaced safely by the
        // helper. Linux packages are updated by replacing the AppImage/tar
        // package until a package-manager-specific updater is introduced.
        if (!OperatingSystem.IsWindows()) return false;
        if (!File.Exists(AppPaths.UpdaterExecutablePath)) return false;
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
        catch { return false; }
    }

    private async Task<GitHubRelease> GetLatestReleaseAsync(string repository, CancellationToken cancellationToken)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await httpClient.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", requestTimeout.Token);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(content, jsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"GitHub returned an empty release for {repository}.");
    }

    private async Task<OffsetStatus> CheckLazerOffsetsAsync(string? version, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version)) return new OffsetStatus("not-detected", "");
        foreach (var source in new[] { $"https://tosu.app/offsets/{version}.json", $"https://osuck.net/offsets/{version}.json" })
        {
            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                using var response = await httpClient.GetAsync(source, requestTimeout.Token);
                if (!response.IsSuccessStatusCode) continue;
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var offsets = await JsonSerializer.DeserializeAsync<OffsetResponse>(stream, jsonOptions, cancellationToken);
                if (offsets is not null && string.Equals(offsets.OsuVersion, version, StringComparison.OrdinalIgnoreCase))
                    return new OffsetStatus("supported", source);
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
        return new OffsetStatus("unsupported", "");
    }

    private async Task InstallTosuAsync(GitHubAsset asset, string temporaryRoot, CancellationToken cancellationToken, IProgress<int>? downloadProgress)
    {
        var archivePath = Path.Combine(temporaryRoot, "tosu.zip");
        var extractPath = Path.Combine(temporaryRoot, "tosu-extract");
        await DownloadAsync(asset, archivePath, cancellationToken, downloadProgress);
        ZipFile.ExtractToDirectory(archivePath, extractPath, overwriteFiles: true);
        var executableName = GetTosuExecutableName();
        var files = Directory.EnumerateFiles(extractPath, executableName, SearchOption.AllDirectories).ToArray();
        if (files.Length != 1) throw new InvalidOperationException($"The tosu archive does not contain a single {executableName} executable.");

        Directory.CreateDirectory(AppPaths.TosuDirectory);
        var target = Path.Combine(AppPaths.TosuDirectory, executableName);
        await StopOwnedProcessAsync(target, cancellationToken);
        var staged = target + ".new";
        var previous = target + ".previous";
        TryDeleteFile(staged);
        TryDeleteFile(previous);
        File.Copy(files[0], staged, overwrite: true);
        try
        {
            if (File.Exists(target)) File.Move(target, previous, overwrite: true);
            File.Move(staged, target, overwrite: true);
            TryDeleteFile(previous);
            MakeExecutableIfNeeded(target);
        }
        catch
        {
            if (!File.Exists(target) && File.Exists(previous)) File.Move(previous, target, overwrite: true);
            TryDeleteFile(staged);
            throw;
        }
    }

    private async Task InstallAddonAsync(GitHubAsset asset, string temporaryRoot, CancellationToken cancellationToken, IProgress<int>? downloadProgress)
    {
        var archivePath = Path.Combine(temporaryRoot, "addon.zip");
        var extractPath = Path.Combine(temporaryRoot, "addon-extract");
        await DownloadAsync(asset, archivePath, cancellationToken, downloadProgress);
        ZipFile.ExtractToDirectory(archivePath, extractPath, overwriteFiles: true);

        var metadataFiles = Directory.EnumerateFiles(extractPath, "metadata.txt", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("Name: ManiaMapAnalyser", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (metadataFiles.Length != 1) throw new InvalidOperationException("The addon archive does not contain a single ManiaMapAnalyser root.");

        var sourceRoot = Path.GetDirectoryName(metadataFiles[0])!;
        var staticRoot = Path.Combine(AppPaths.TosuDirectory, "static");
        var target = Path.Combine(staticRoot, "ManiaMapAnalyser");
        var staged = Path.Combine(staticRoot, "ManiaMapAnalyser.new");
        var backupRoot = Path.Combine(AppPaths.TosuDirectory, ".update-backup");
        var backup = Path.Combine(backupRoot, "ManiaMapAnalyser-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));

        Directory.CreateDirectory(staticRoot);
        TryDeleteDirectory(staged);
        CopyDirectory(sourceRoot, staged);
        if (Directory.Exists(target))
        {
            Directory.CreateDirectory(backupRoot);
            Directory.Move(target, backup);
        }
        try
        {
            Directory.Move(staged, target);
        }
        catch
        {
            if (Directory.Exists(backup) && !Directory.Exists(target)) Directory.Move(backup, target);
            TryDeleteDirectory(staged);
            throw;
        }
    }

    private async Task DownloadAsync(GitHubAsset asset, string destination, CancellationToken cancellationToken, IProgress<int>? progress)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            var buffer = new byte[64 * 1024];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                if (total is > 0) progress?.Report((int)Math.Clamp(copied * 100 / total.Value, 0, 100));
            }
            await output.FlushAsync(cancellationToken);
        }

        // The write stream must be disposed before opening the destination for
        // hashing. This matters on Windows, where the FileShare.None handle
        // otherwise remains open until the end of the method.
        if (new FileInfo(destination).Length == 0) throw new InvalidOperationException("The downloaded file is empty.");

        if (string.IsNullOrWhiteSpace(asset.Digest) ||
            !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GitHub did not provide a SHA-256 digest for the downloaded component.");

        await using var hashStream = File.OpenRead(destination);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken));
        var expected = asset.Digest["sha256:".Length..];
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The downloaded file failed its SHA-256 integrity check.");
    }

    private async Task StopOwnedProcessAsync(string executablePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(executablePath)) return;
        var expected = Path.GetFullPath(executablePath);
        foreach (var candidate in Process.GetProcessesByName("tosu"))
        {
            try
            {
                var path = candidate.MainModule?.FileName;
                if (path is null || !string.Equals(Path.GetFullPath(path), expected,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                if (!candidate.HasExited) candidate.Kill(entireProcessTree: true);
                await candidate.WaitForExitAsync(cancellationToken);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            finally { candidate.Dispose(); }
        }
    }

    private void EnsureTosuEnvironment()
    {
        var path = AppPaths.TosuEnvironmentPath;
        if (File.Exists(path)) return;
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
            try { shouldSeed = File.ReadAllText(path).Trim() is "" or "{}"; }
            catch { shouldSeed = true; }
        }
        if (!shouldSeed) return;
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

    private async Task<InstallState> LoadStateAsync(CancellationToken cancellationToken)
    {
        foreach (var path in new[] { AppPaths.InstallStatePath, Path.Combine(AppPaths.BaseDirectory, "install-state.json") })
        {
            if (!File.Exists(path)) continue;
            try
            {
                await using var stream = File.OpenRead(path);
                return await JsonSerializer.DeserializeAsync<InstallState>(stream, jsonOptions, cancellationToken) ?? new InstallState();
            }
            catch { }
        }
        return new InstallState();
    }

    private async Task SaveStateAsync(InstallState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        await using var stream = File.Create(AppPaths.InstallStatePath);
        await JsonSerializer.SerializeAsync(stream, state, jsonOptions, cancellationToken);
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
        if (OperatingSystem.IsWindows()) { pattern = "^tosu-windows-v.*\\.zip$"; return true; }
        if (OperatingSystem.IsLinux()) { pattern = "^tosu-linux-v.*\\.zip$"; return true; }
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
                        if (path?.Contains("osulazer", StringComparison.OrdinalIgnoreCase) == true) candidates.Add(path);
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
            catch { }
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(localAppData, "osulazer", "current", "osu!.exe"));
        }

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            try
            {
                var productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "";
                var match = System.Text.RegularExpressions.Regex.Match(productVersion, @"(?<version>\d{4}\.\d+\.\d+)");
                if (match.Success) return match.Groups["version"].Value;
            }
            catch { }
        }
        return "";
    }

    private static Version GetCurrentLauncherVersion() => ParseVersion(Assembly.GetExecutingAssembly().GetName().Version?.ToString());

    private static Version ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new Version(0, 0, 0, 0);
        var normalized = value.Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out var version) ? version : new Version(0, 0, 0, 0);
    }

    private static bool IsRecoverableNetworkError(Exception exception) => exception is HttpRequestException or TaskCanceledException or IOException;

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static void MakeExecutableIfNeeded(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(UpdateService));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        httpClient.Dispose();
    }

    private sealed record OffsetStatus(string Status, string Source);

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = new();
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }

    private sealed class OffsetResponse
    {
        [JsonPropertyName("OsuVersion")] public string? OsuVersion { get; set; }
    }
}

public sealed class UpdateProgress
{
    public UpdateProgress(string message, int? percent = null) { Message = message; Percent = percent; }
    public string Message { get; }
    public int? Percent { get; }
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
    public DateTime LastCheckUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

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
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Warning { get; set; }
    public string Compatibility { get; set; } = "not-detected";
    public string OffsetsSource { get; set; } = "";
    public bool LauncherUpdateAvailable { get; set; }
    public string? LatestLauncherVersion { get; set; }
    public string InstalledLauncherVersion { get; set; } = "";
    public bool UpdatedTosu { get; set; }
    public bool UpdatedAddon { get; set; }
    public string? LatestTosu { get; set; }
    public string? LatestAddon { get; set; }
    public string InstalledTosu { get; set; } = "";
    public string InstalledAddon { get; set; } = "";
    public bool TosuUpdateAvailable { get; set; }
    public bool AddonUpdateAvailable { get; set; }
    public string? LazerVersion { get; set; }
}
