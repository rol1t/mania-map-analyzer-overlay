namespace ManiaMapAnalyzerOverlay.Avalonia.Analyzers;

internal sealed class AnalyzerAdapterManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AnalysisPath { get; set; } = string.Empty;
    public string? FullscreenPath
    {
        get; set;
    }
    public string? SettingsPath
    {
        get; set;
    }
    public string HostSelector { get; set; } = "body";
    public string? PresetAnchorSelector
    {
        get; set;
    }
    public string Script { get; set; } = "adapter.js";
    public bool SupportsFullscreen
    {
        get; set;
    }
    public int SnapshotSchemaVersion { get; set; } = 1;
}
