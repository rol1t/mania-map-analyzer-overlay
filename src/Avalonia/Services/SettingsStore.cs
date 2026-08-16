using System.IO;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Models;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public LauncherSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsPath))
                return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(AppPaths.SettingsPath), Options)
                    ?? new LauncherSettings();
        }
        catch
        {
            // A damaged settings file should not prevent startup.
        }

        return new LauncherSettings();
    }

    public void Save(LauncherSettings settings)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        File.WriteAllText(AppPaths.SettingsPath, JsonSerializer.Serialize(settings, Options));
    }
}
