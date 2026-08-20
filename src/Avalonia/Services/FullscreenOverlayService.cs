using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ManiaMapAnalyzerOverlay.Avalonia.Models;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

/// <summary>Windows-only configuration adapter for tosu's official in-game overlay.</summary>
public sealed class FullscreenOverlayService
{
    private const string EnvironmentKey = "ENABLE_INGAME_OVERLAY";
    private const string CounterName = "ManiaMapAnalyzerOverlay";
    private const string BaseUrl = "http://127.0.0.1:24050";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool ReadEnabled(bool fallback)
    {
        var path = EnvironmentPath;
        if (!File.Exists(path))
            return fallback;
        var match = Regex.Match(File.ReadAllText(path),
            "(?im)^\\s*" + Regex.Escape(EnvironmentKey) + "\\s*=\\s*(?<value>[^#\\r\\n]*)");
        return match.Success
            ? string.Equals(match.Groups["value"].Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)
            : fallback;
    }

    public void SetEnabled(bool enabled)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("The official tosu in-game overlay is currently available on Windows only.");
        if (!File.Exists(EnvironmentPath))
            throw new FileNotFoundException("tosu.env was not found. Run the installer to restore components.", EnvironmentPath);

        var content = File.ReadAllText(EnvironmentPath);
        var replacement = EnvironmentKey + "=" + (enabled ? "true" : "false");
        var pattern = "(?im)^\\s*" + Regex.Escape(EnvironmentKey) + "\\s*=[^\\r\\n]*";
        var updated = Regex.IsMatch(content, pattern)
            ? Regex.Replace(content, pattern, replacement)
            : content.TrimEnd('\r', '\n') + Environment.NewLine + replacement + Environment.NewLine;
        File.WriteAllText(EnvironmentPath, updated, new UTF8Encoding(false));
    }

    public void EnsureProfile(LauncherSettings settings, AnalyzerDescriptor analyzer, bool updateBounds)
    {
        var size = GetOverlaySize(settings);
        EnsureAssets(size.Width, size.Height, analyzer);
        var settingsDirectory = Path.Combine(AppPaths.TosuDirectory, "settings");
        var settingsPath = Path.Combine(settingsDirectory, "__ingame__.values.json");
        InGameOverlayConfiguration configuration;

        try
        {
            configuration = File.Exists(settingsPath)
                ? JsonSerializer.Deserialize<InGameOverlayConfiguration>(File.ReadAllText(settingsPath), JsonOptions) ?? new()
                : new();
        }
        catch (Exception exception)
        {
            AppLogger.Error($"Loading fullscreen overlay settings '{settingsPath}'", exception);
            configuration = new();
        }

        configuration.ingame_profile = string.IsNullOrWhiteSpace(configuration.ingame_profile) ? "default" : configuration.ingame_profile;
        configuration.obs_profile = string.IsNullOrWhiteSpace(configuration.obs_profile) ? "default" : configuration.obs_profile;
        configuration.profiles ??= new();
        var active = configuration.profiles.FirstOrDefault(p => p?.id == configuration.ingame_profile)
            ?? configuration.profiles.FirstOrDefault();
        if (active is null)
        {
            active = new InGameOverlayProfile { id = "default", name = "default", overlays = new() };
            configuration.profiles.Add(active);
        }
        active.id = string.IsNullOrWhiteSpace(active.id) ? "default" : active.id;
        active.name = string.IsNullOrWhiteSpace(active.name) ? active.id : active.name;
        active.overlays ??= new();
        configuration.ingame_profile = active.id;

        var overlay = active.overlays.FirstOrDefault(x =>
            string.Equals(x?.folderName, CounterName, StringComparison.OrdinalIgnoreCase));
        var migrated = false;
        if (overlay is null)
        {
            overlay = active.overlays.FirstOrDefault(x =>
                string.Equals(x?.folderName, "ManiaMapAnalyser", StringComparison.OrdinalIgnoreCase));
            migrated = overlay is not null;
        }
        if (overlay is null)
        {
            overlay = new InGameOverlayItem { id = 1, top = 24, left = 24, scale = 1, z_index = 1 };
            active.overlays.Add(overlay);
            updateBounds = true;
        }

        overlay._enabled = true;
        overlay._settings = false;
        overlay.folderName = CounterName;
        overlay.url = BaseUrl + "/" + CounterName;
        if (updateBounds || migrated || overlay.width <= 0 || overlay.height <= 0)
        {
            overlay.width = size.Width;
            overlay.height = size.Height;
        }

        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(configuration, JsonOptions), new UTF8Encoding(false));
    }

    public void WriteRuntime(
        LauncherSettings settings,
        AnalyzerDescriptor analyzer,
        string setupScript,
        string observerScript)
    {
        var size = GetOverlaySize(settings);
        EnsureAssets(size.Width, size.Height, analyzer);
        var runtime = setupScript + Environment.NewLine + observerScript;
        var runtimePath = Path.Combine(CounterDirectory, "runtime.js");
        if (File.Exists(runtimePath) && File.ReadAllText(runtimePath) == runtime)
            return;
        File.WriteAllText(runtimePath, runtime, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(CounterDirectory, "runtime.version"),
            DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), new UTF8Encoding(false));
    }

    private static (int Width, int Height) GetOverlaySize(LauncherSettings settings)
    {
        var scale = Math.Clamp(settings.OverlayScalePercent, 50, 180) / 100d;
        var size = settings.OverlayLayoutMode?.ToLowerInvariant() switch
        {
            "horizontal" => (960, 650),
            "companella" => (760, 470),
            "custom" => (620, 700),
            _ => (520, 640)
        };
        return (Math.Max(240, (int)Math.Ceiling(size.Item1 * scale)),
            Math.Max(180, (int)Math.Ceiling(size.Item2 * scale)));
    }

    private static void EnsureAssets(int width, int height, AnalyzerDescriptor analyzer)
    {
        Directory.CreateDirectory(CounterDirectory);
        var metadata = "Usecase: ingame\r\nName: Mania Map Analyzer Overlay\r\nAuthor: rol1t\r\n" +
            "CompatibleWith: tosu\r\nResolution: " + width + "x" + height + "\r\nVersion: 1.0\r\n" +
            "Notes: Applies the launcher-selected preset to " + analyzer.Name + " in tosu's In-Game Overlay.\r\n";
        File.WriteAllText(Path.Combine(CounterDirectory, "metadata.txt"), metadata, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(CounterDirectory, "index.html"), BuildIndexHtml(analyzer), new UTF8Encoding(false));
        var fullscreenCss = Path.Combine(AppPaths.BaseDirectory, "Assets", "overlay", "runtime", "fullscreen.css");
        if (File.Exists(fullscreenCss))
            File.Copy(fullscreenCss, Path.Combine(CounterDirectory, "fullscreen.css"), overwrite: true);
        var runtimePath = Path.Combine(CounterDirectory, "runtime.js");
        var versionPath = Path.Combine(CounterDirectory, "runtime.version");
        if (!File.Exists(runtimePath))
            File.WriteAllText(runtimePath, "(function(){})();", new UTF8Encoding(false));
        if (!File.Exists(versionPath))
            File.WriteAllText(versionPath, "0", new UTF8Encoding(false));
    }

    private static string EnvironmentPath => Path.Combine(AppPaths.TosuDirectory, "tosu.env");
    private static string CounterDirectory => Path.Combine(AppPaths.TosuDirectory, "static", CounterName);

    private static string BuildIndexHtml(AnalyzerDescriptor analyzer)
    {
        var sourcePath = System.Net.WebUtility.HtmlEncode(analyzer.FullscreenPath);
        return """
<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/ManiaMapAnalyzerOverlay/fullscreen.css">
</head><body><iframe id="overlay-source" src="__OVERLAY_ANALYZER_SOURCE__" allowtransparency="true"></iframe>
<script>(function(){const frame=document.getElementById('overlay-source');const base='/ManiaMapAnalyzerOverlay/';let appliedVersion='';
async function apply(version){try{const doc=frame.contentDocument;if(!doc||!doc.head)throw new Error('Analyzer frame document is unavailable.');const previous=doc.getElementById('overlay-fullscreen-runtime');if(previous)previous.remove();const script=doc.createElement('script');script.id='overlay-fullscreen-runtime';script.src=base+'runtime.js?v='+encodeURIComponent(version);doc.head.appendChild(script);appliedVersion=version;}catch(exception){console.error('Applying fullscreen overlay runtime failed',exception);}}
async function refresh(){try{const response=await fetch(base+'runtime.version?t='+Date.now(),{cache:'no-store'});if(!response.ok)throw new Error('Runtime version request failed with HTTP '+response.status+'.');const version=(await response.text()).trim();if(version&&version!==appliedVersion)await apply(version);}catch(exception){console.error('Refreshing fullscreen overlay runtime failed',exception);}}
frame.addEventListener('load',function(){appliedVersion='';refresh();});setInterval(refresh,1000);refresh();})();</script></body></html>
""".Replace("__OVERLAY_ANALYZER_SOURCE__", sourcePath, StringComparison.Ordinal);
    }

    private sealed class InGameOverlayConfiguration
    {
        public string? obs_profile
        {
            get; set;
        }
        public string? ingame_profile
        {
            get; set;
        }
        public List<InGameOverlayProfile>? profiles
        {
            get; set;
        }
    }
    private sealed class InGameOverlayProfile
    {
        public string? id
        {
            get; set;
        }
        public string? name
        {
            get; set;
        }
        public List<InGameOverlayItem>? overlays
        {
            get; set;
        }
    }
    private sealed class InGameOverlayItem
    {
        public bool _enabled
        {
            get; set;
        }
        public bool _settings
        {
            get; set;
        }
        public int id
        {
            get; set;
        }
        public string? folderName
        {
            get; set;
        }
        public string? url
        {
            get; set;
        }
        public int width
        {
            get; set;
        }
        public int height
        {
            get; set;
        }
        public int top
        {
            get; set;
        }
        public int left
        {
            get; set;
        }
        public double scale
        {
            get; set;
        }
        public int z_index
        {
            get; set;
        }
    }
}
