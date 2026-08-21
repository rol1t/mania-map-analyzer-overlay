using System;
using System.IO;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Models;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

public sealed class EffectiveAnalysisConfigurationStore
{
    private static readonly JsonSerializerOptions _readOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions _writeOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public EffectiveAnalysisConfiguration Load()
    {
        try
        {
            var path = AppPaths.EffectiveAnalysisConfigurationPath;
            if (!File.Exists(path))
            {
                return CreateDefault();
            }

            var json = File.ReadAllText(path);
            var configuration = JsonSerializer.Deserialize<EffectiveAnalysisConfiguration>(json, _readOptions);
            if (configuration is null)
            {
                return CreateDefault();
            }

            return configuration.Normalize();
        }
        catch (Exception exception)
        {
            AppLogger.Error("Loading effective analysis configuration", exception);
            return CreateDefault();
        }
    }

    public void Save(EffectiveAnalysisConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var normalized = configuration.Normalize();
        var path = AppPaths.EffectiveAnalysisConfigurationPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(normalized, _writeOptions);
            File.WriteAllText(path, json);
            AppLogger.Info("Effective analysis configuration", $"Saved effective configuration with {normalized.Widgets.Length} widget(s) to '{path}'.");
        }
        catch (Exception exception)
        {
            AppLogger.Error("Saving effective analysis configuration", exception);
        }
    }

    public static EffectiveAnalysisConfiguration CreateDefault()
    {
        return new EffectiveAnalysisConfiguration().Normalize();
    }
}
