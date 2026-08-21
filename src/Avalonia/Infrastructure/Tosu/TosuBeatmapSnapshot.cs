using System;
using System.Collections.Immutable;
using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;

/// <summary>
/// Immutable analyzer input captured from one consistent tosu map snapshot.
/// The raw osu! file is kept intact so different analyzer engines can parse it
/// independently, while the metadata is available to the domain coordinator.
/// </summary>
public sealed record TosuBeatmapSnapshot
{
    public TosuBeatmapSnapshot(
        BeatmapIdentity identity,
        string rawBeatmap,
        TosuBeatmapMetadata metadata,
        double rate,
        ImmutableArray<string> mods,
        DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(rawBeatmap);
        ArgumentNullException.ThrowIfNull(metadata);

        if (string.IsNullOrWhiteSpace(rawBeatmap))
        {
            throw new ArgumentException("The raw beatmap content cannot be empty.", nameof(rawBeatmap));
        }

        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "The rate must be finite and positive.");
        }

        Identity = identity;
        RawBeatmap = rawBeatmap;
        Metadata = metadata;
        Rate = rate;
        Mods = mods.IsDefault ? [] : mods;
        CapturedAt = capturedAt;
    }

    public BeatmapIdentity Identity
    {
        get;
    }

    public string RawBeatmap
    {
        get;
    }

    public TosuBeatmapMetadata Metadata
    {
        get;
    }

    public double Rate
    {
        get;
    }

    public ImmutableArray<string> Mods
    {
        get;
    }

    public DateTimeOffset CapturedAt
    {
        get;
    }
}

/// <summary>
/// Metadata copied from tosu's v2 payload. These values are optional because
/// tosu can emit partial packets while changing screens.
/// </summary>
public sealed record TosuBeatmapMetadata
{
    public string Artist { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Mapper { get; init; } = string.Empty;
    public double? Bpm
    {
        get; init;
    }
    public double? OverallDifficulty
    {
        get; init;
    }
    public double? CircleSize
    {
        get; init;
    }
    public double? ApproachRate
    {
        get; init;
    }
    public double? HealthDrain
    {
        get; init;
    }
    public string Mode { get; init; } = string.Empty;
    public string BackgroundPath { get; init; } = string.Empty;
}
