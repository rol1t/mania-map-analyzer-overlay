using System;
using System.Linq;
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
            snapshot.Identity.Id,
            snapshot.Identity.StableKey,
            snapshot.Rate,
            CreateModsKey(snapshot.Mods),
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
            CreateModsKey(snapshot.Mods),
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

    /// <summary>
    /// Returns whether two analysis keys refer to the same verified beatmap
    /// file. Rate, modifiers and effective configuration are deliberately not
    /// part of this comparison: changing those values is an explicit user
    /// action and can be analysed immediately, while a carousel map change
    /// still needs the controller's transient-observation guard.
    /// </summary>
    public static bool IsSameBeatmapRevision(
        HeadlessAnalysisKey left,
        HeadlessAnalysisKey right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return string.Equals(
                left.BeatmapKey.StableKey,
                right.BeatmapKey.StableKey,
                StringComparison.OrdinalIgnoreCase)
            && left.BeatmapKey.RawBeatmapLength == right.BeatmapKey.RawBeatmapLength;
    }

    private static string CreateModsKey(System.Collections.Immutable.ImmutableArray<string> mods)
    {
        return string.Join(',', mods.OrderBy(static mod => mod, StringComparer.OrdinalIgnoreCase));
    }
}
