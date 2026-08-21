using System;
using System.IO;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

public static class AppPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory;
    /// <summary>
    /// Mutable application data is deliberately kept outside the installation folder.
    /// This lets a normal (non-administrator) user update tosu and edit CSS even when the
    /// launcher was installed under Program Files or /Applications.
    /// </summary>
    public static string DataDirectory => Path.Combine(GetDataRoot(), "ManiaMapAnalyzerOverlay");

    // New installs and all updates always use a writable per-user directory.
    // TosuService still falls back to LegacyTosuDirectory when an offline user
    // starts an older portable installation before the first successful bootstrap.
    public static string TosuDirectory => Path.Combine(DataDirectory, "tosu");
    public static string LegacyTosuDirectory => Path.Combine(BaseDirectory, "tosu");
    public static string TosuEnvironmentPath => Path.Combine(TosuDirectory, "tosu.env");
    public static string SettingsPath => Path.Combine(DataDirectory, "launcher-settings.json");
    public static string EffectiveAnalysisConfigurationPath => Path.Combine(DataDirectory, "analysis-configuration.json");
    public static string InstallStatePath => Path.Combine(DataDirectory, "install-state.json");
    public static string CustomCssPath => Path.Combine(DataDirectory, "overlay-custom.css");
    public static string LegacyCustomCssPath => Path.Combine(BaseDirectory, "overlay-custom.css");
    public static string UpdaterExecutablePath => Path.Combine(BaseDirectory,
        OperatingSystem.IsWindows() ? "Mania Map Analyzer Overlay.Updater.exe" : "Mania Map Analyzer Overlay.Updater");

    private static string GetDataRoot()
    {
        if (OperatingSystem.IsWindows())
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome))
            return xdgDataHome;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }
}
