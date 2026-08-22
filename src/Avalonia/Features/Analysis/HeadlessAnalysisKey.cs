using System;
using System.Collections.Immutable;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

/// <summary>
/// Immutable typed beatmap identity used for headless analysis deduplication.
/// Includes the raw beatmap length so a re-fetched map with different content
/// is treated as a new analysis target.
/// </summary>
public sealed record HeadlessBeatmapKey(
    string StableKey,
    double Rate,
    ImmutableArray<string> Mods,
    int RawBeatmapLength);

/// <summary>
/// Immutable typed scene generation identity. It intentionally omits the raw
/// beatmap length so a content-only refresh does not force scene recomposition.
/// </summary>
public sealed record HeadlessSceneKey(
    string StableKey,
    double Rate,
    ImmutableArray<string> Mods,
    string ConfigurationVersion,
    string DefaultEngineId,
    string DefaultAlgorithm,
    int WidgetCount);

/// <summary>
/// Immutable typed key that identifies a full beatmap + effective configuration
/// analysis run. Equivalent to the legacy combined string key.
/// </summary>
public sealed record HeadlessAnalysisKey(
    HeadlessBeatmapKey BeatmapKey,
    HeadlessSceneKey SceneKey);
