using System.Text.Json;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

/// <summary>
/// Post-play replay source configuration, kept separate from visual presets/profiles.
/// Paths are explicit user selections; no silent scan or upload.
/// </summary>
public sealed record ReplayConfiguration
{
    public const string FileName = "replay-configuration.json";
    public const int CurrentVersion = 1;

    public ReplayConfiguration(
        int configurationVersion = CurrentVersion,
        ReplaySourceKind selectedSource = ReplaySourceKind.StableOsr,
        string? explicitReplayPath = null,
        bool allowPostPlayDiscovery = false,
        ReplayJudgeOptions? judgeOptions = null,
        JsonElement? options = null)
    {
        ConfigurationVersion = configurationVersion;
        SelectedSource = selectedSource;
        ExplicitReplayPath = explicitReplayPath?.Trim() ?? string.Empty;
        AllowPostPlayDiscovery = allowPostPlayDiscovery;
        JudgeOptions = judgeOptions ?? new ReplayJudgeOptions();
        Options = options ?? JsonDocument.Parse("{}").RootElement.Clone();
    }

    public int ConfigurationVersion
    {
        get;
    }
    public ReplaySourceKind SelectedSource
    {
        get;
    }
    public string ExplicitReplayPath
    {
        get;
    }
    public bool AllowPostPlayDiscovery
    {
        get;
    }
    public ReplayJudgeOptions JudgeOptions
    {
        get;
    }
    public JsonElement Options
    {
        get;
    }
}
