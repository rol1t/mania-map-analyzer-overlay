using System;
using System.IO;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

public static class AppPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory;
    public static string TosuDirectory => Path.Combine(BaseDirectory, "tosu");
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ManiaMapAnalyzerOverlay");
    public static string SettingsPath => Path.Combine(DataDirectory, "launcher-settings.json");
    public static string CustomCssPath => Path.Combine(BaseDirectory, "overlay-custom.css");
}
