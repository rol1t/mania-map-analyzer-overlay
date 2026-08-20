using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManiaMapAnalyzerOverlay.Updater;

internal static class Program
{
    private const string Repository = "rol1t/mania-map-analyzer-overlay";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            if (!options.TryGetValue("pid", out var pidText) || !int.TryParse(pidText, out var pid) ||
                !options.TryGetValue("install-dir", out var installDirectory)) return 2;

            await WaitForProcessAsync(pid);
            var release = await GetLatestReleaseAsync();
            var assetSuffix = OperatingSystem.IsWindows() ? "-win-x64.zip" : "-linux-x64.tar.gz";
            var asset = release.Assets.SingleOrDefault(a =>
                a.Name.StartsWith("Mania-Map-Analyzer-Overlay-", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(assetSuffix, StringComparison.OrdinalIgnoreCase));
            if (asset is null) throw new InvalidOperationException("No launcher archive was found in the latest release.");

            var temporaryRoot = Path.Combine(Path.GetTempPath(), "ManiaMapAnalyzerOverlayUpdater-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temporaryRoot);
                var archivePath = Path.Combine(temporaryRoot, "release.zip");
                await DownloadAsync(asset, archivePath);
                var extractPath = Path.Combine(temporaryRoot, "release");
                if (!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("Automatic launcher updates are not enabled for this package format yet.");
                ZipFile.ExtractToDirectory(archivePath, extractPath, overwriteFiles: true);
                var payloadExecutables = Directory.EnumerateFiles(extractPath, GetLauncherExecutableName(), SearchOption.AllDirectories).ToArray();
                if (payloadExecutables.Length != 1) throw new InvalidOperationException("The launcher archive does not contain a unique executable.");

                var payloadRoot = Path.GetDirectoryName(payloadExecutables[0])!;
                var targetLauncher = Path.Combine(installDirectory, GetLauncherExecutableName());
                var payloadVersion = ParseVersion(FileVersionInfo.GetVersionInfo(payloadExecutables[0]).FileVersion);
                var installedVersion = File.Exists(targetLauncher)
                    ? ParseVersion(FileVersionInfo.GetVersionInfo(targetLauncher).FileVersion)
                    : new Version(0, 0, 0, 0);
                if (payloadVersion <= installedVersion) return 0;

                CopyLauncherFiles(payloadRoot, installDirectory);
                if (File.Exists(targetLauncher)) Process.Start(new ProcessStartInfo
                {
                    FileName = targetLauncher,
                    WorkingDirectory = installDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            finally
            {
                TryDeleteDirectory(temporaryRoot);
            }
            return 0;
        }
        catch
        {
            // The helper is intentionally silent. The main app will report a failed
            // update on its next start while preserving the installed version.
            return 1;
        }
    }

    private static async Task<GitHubRelease> GetLatestReleaseAsync()
    {
        using var client = CreateHttpClient();
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions)
            ?? throw new InvalidOperationException("The launcher release response was empty.");
    }

    private static async Task DownloadAsync(GitHubAsset asset, string destination)
    {
        using var client = CreateHttpClient();
        using var response = await client.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(destination);
        await input.CopyToAsync(output);
        await output.FlushAsync();
        if (new FileInfo(destination).Length == 0) throw new InvalidOperationException("The launcher archive is empty.");
        if (string.IsNullOrWhiteSpace(asset.Digest) ||
            !asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GitHub did not provide a SHA-256 digest for the launcher archive.");

        await using var hashStream = File.OpenRead(destination);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(hashStream));
        if (!string.Equals(actual, asset.Digest["sha256:".Length..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The launcher archive failed its SHA-256 integrity check.");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ManiaMapAnalyzerOverlay.Updater", "2.1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static async Task WaitForProcessAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (TimeoutException) { throw new InvalidOperationException("The launcher did not close in time."); }
    }

    private static void CopyLauncherFiles(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var item in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, item);
            var destination = Path.Combine(target, relative);
            if (Path.GetFileName(item).Equals("overlay-custom.css", StringComparison.OrdinalIgnoreCase) && File.Exists(destination)) continue;
            if (Path.GetFileName(item).Equals(GetUpdaterExecutableName(), StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(item)) Directory.CreateDirectory(destination);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(item, destination, overwrite: true);
            }
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            options[args[i][2..]] = args[++i];
        }
        return options;
    }

    private static Version ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new Version(0, 0, 0, 0);
        return Version.TryParse(value.Trim().TrimStart('v', 'V'), out var version)
            ? version : new Version(0, 0, 0, 0);
    }

    private static string GetLauncherExecutableName() => OperatingSystem.IsWindows()
        ? "Mania Map Analyzer Overlay.exe"
        : "Mania Map Analyzer Overlay";

    private static string GetUpdaterExecutableName() => OperatingSystem.IsWindows()
        ? "Mania Map Analyzer Overlay.Updater.exe"
        : "Mania Map Analyzer Overlay.Updater";

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = new();
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
