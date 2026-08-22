namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

/// <summary>
/// High-level state of a single beatmap source query from the headless polling
/// loop. The UI layer maps these values to localized status text.
/// </summary>
public enum HeadlessBeatmapSourceState
{
    OK,
    OsuNotRunning,
    NoBeatmap,
    UnsupportedMode,
    Error
}
