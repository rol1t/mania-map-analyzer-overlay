using System.IO;
using System.Text;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

public static class CustomCssService
{
    public static string Path => AppPaths.CustomCssPath;

    public static void EnsureExists()
    {
        if (File.Exists(Path)) return;
        var bundledTemplate = System.IO.Path.Combine(AppPaths.BaseDirectory, "Assets", "overlay-custom.css");
        var content = File.Exists(bundledTemplate)
            ? File.ReadAllText(bundledTemplate, Encoding.UTF8)
            : "/* Custom Mania Map Analyzer Overlay CSS */\n";
        File.WriteAllText(Path, content, new UTF8Encoding(false));
    }

    public static string Read()
    {
        EnsureExists();
        return File.ReadAllText(Path, Encoding.UTF8);
    }
}
