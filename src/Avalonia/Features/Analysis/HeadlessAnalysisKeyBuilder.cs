using System;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Avalonia.Models;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

/// <summary>
/// Pure helper that builds the stable typed deduplication keys used by the
/// headless polling loop. Extracted so the key logic can be unit tested without
/// a running controller.
/// </summary>
public static class HeadlessAnalysisKeyBuilder
{
    public static HeadlessBeatmapKey BuildBeatmapKey(TosuBeatmapSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new HeadlessBeatmapKey(
            snapshot.Identity.StableKey,
            snapshot.Rate,
            snapshot.Mods,
            snapshot.RawBeatmap.Length);
    }

    public static HeadlessSceneKey BuildSceneKey(
        TosuBeatmapSnapshot snapshot,
        EffectiveAnalysisConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(configuration);

        return new HeadlessSceneKey(
            snapshot.Identity.StableKey,
            snapshot.Rate,
            snapshot.Mods,
            configuration.ConfigurationVersion,
            configuration.DefaultEngineId,
            configuration.DefaultAlgorithm,
            configuration.Widgets.Length);
    }

    public static HeadlessAnalysisKey BuildAnalysisKey(
        TosuBeatmapSnapshot snapshot,
        EffectiveAnalysisConfiguration configuration)
    {
        return new HeadlessAnalysisKey(BuildBeatmapKey(snapshot), BuildSceneKey(snapshot, configuration));
    }

    public static bool IsSameBeatmapAndConfig(
        TosuBeatmapSnapshot snapshot,
        EffectiveAnalysisConfiguration configuration,
        HeadlessAnalysisKey? lastAnalysisKey,
        HeadlessSceneKey? lastSceneKey)
    {
        var analysisKey = BuildAnalysisKey(snapshot, configuration);
        var sceneKey = analysisKey.SceneKey;

        return analysisKey.Equals(lastAnalysisKey) && sceneKey.Equals(lastSceneKey);
    }

    public static bool IsNewSceneGeneration(
        TosuBeatmapSnapshot snapshot,
        EffectiveAnalysisConfiguration configuration,
        HeadlessSceneKey? lastSceneKey)
    {
        var sceneKey = BuildSceneKey(snapshot, configuration);
        return !sceneKey.Equals(lastSceneKey);
    }
}
