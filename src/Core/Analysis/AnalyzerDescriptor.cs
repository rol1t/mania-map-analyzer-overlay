namespace ManiaMapAnalyzerOverlay.Core.Analysis;

public sealed record AnalyzerDescriptor(
    string Id,
    string Name,
    string AnalysisPath,
    string FullscreenPath,
    string? SettingsPath,
    bool SupportsFullscreen,
    int SnapshotSchemaVersion = AnalysisSnapshot.CurrentSchemaVersion);
