using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>
/// Resolves the causal generation carried by a completed analysis. An
/// analysis for the currently reduced map can be tied to that generation. An
/// analysis for another map must remain unversioned because its matching
/// realtime transition may already be queued ahead of it but not applied yet.
/// </summary>
public static class OverlayRuntimeAnalysisCausality
{
    public static long ResolveBeatmapGeneration(
        OverlayRuntimeState current,
        AnalysisSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(snapshot);

        string currentBeatmapId = current.BeatmapId?.Trim() ?? string.Empty;
        string analysisBeatmapId = snapshot.Beatmap.Id?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(currentBeatmapId)
            && string.Equals(currentBeatmapId, analysisBeatmapId, StringComparison.Ordinal)
                ? current.BeatmapGeneration
                : 0;
    }
}
