using System.Text.Json.Serialization;

namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Analyzer-independent snapshot consumed by the application and overlay presets.
/// The contract is intentionally versioned because adapters and renderers may be
/// updated independently.
/// </summary>
public sealed record AnalysisSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string SourceId { get; init; } = string.Empty;
    public BeatmapSnapshot Beatmap { get; init; } = new();
    public GameplaySnapshot Gameplay { get; init; } = new();
    public DifficultySnapshot Difficulty { get; init; } = new();
    public IReadOnlyList<RankEstimate> Ranks { get; init; } = Array.Empty<RankEstimate>();
    public IReadOnlyList<SkillMetric> Skills { get; init; } = Array.Empty<SkillMetric>();
    public ReplayOverlaySnapshot? Replay
    {
        get; init;
    }

    public PauseCoachSnapshot? PauseCoach
    {
        get; init;
    }

    [JsonExtensionData]
    public IDictionary<string, object?> Extensions { get; init; } = new Dictionary<string, object?>();
}

public sealed record BeatmapSnapshot
{
    public string Id { get; init; } = string.Empty;
    public string SetId { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Mapper { get; init; } = string.Empty;
    public string BpmLabel { get; init; } = string.Empty;
    public double? OverallDifficulty
    {
        get; init;
    }
    public double? HealthDrain
    {
        get; init;
    }
    public string BackgroundUrl { get; init; } = string.Empty;
}

public sealed record GameplaySnapshot
{
    public string State { get; init; } = string.Empty;
    public bool? IsPlaying
    {
        get; init;
    }
    public bool? IsPaused
    {
        get; init;
    }
    public bool? IsFocused
    {
        get; init;
    }
}

public sealed record DifficultySnapshot
{
    public double? StarRating
    {
        get; init;
    }
    public string StarLabel { get; init; } = string.Empty;
    public string Unit { get; init; } = "SR";

    /// <summary>
    /// Analyzer engine that produced the displayed star estimate. This is
    /// presentation provenance, not an assertion of compatibility with the
    /// official osu! difficulty calculator.
    /// </summary>
    public string StarRatingProvider { get; init; } = string.Empty;

    /// <summary>
    /// Actual estimator selected by the engine. In particular, this may be
    /// different from a requested meta-algorithm such as Mixed.
    /// </summary>
    public string StarRatingAlgorithm { get; init; } = string.Empty;

    public double? LnPercent
    {
        get; init;
    }
    public int? Keys
    {
        get; init;
    }

    /// <summary>
    /// Difficulty sampled along the beatmap timeline. The samples are
    /// analyzer output (not a renderer-side estimate) and are optional for
    /// older/partial snapshots.
    /// </summary>
    public DifficultyTimelineSnapshot? Timeline
    {
        get; init;
    }

    /// <summary>
    /// Regular-note (Rice) difficulty sampled along the beatmap timeline.
    /// Timeline is retained as a backwards-compatible alias for this series.
    /// </summary>
    public DifficultyTimelineSnapshot? RiceTimeline
    {
        get; init;
    }

    /// <summary>
    /// Long-note (LN) difficulty sampled along the beatmap timeline. It is
    /// optional because maps without usable long-note content have no series.
    /// </summary>
    public DifficultyTimelineSnapshot? LnTimeline
    {
        get; init;
    }
}

/// <summary>
/// A bounded, time-ordered difficulty series used by the overlay presenter.
/// Times are milliseconds from the beginning of the map and values use the
/// analyzer's native difficulty scale.
/// </summary>
public sealed record DifficultyTimelineSnapshot
{
    public IReadOnlyList<DifficultyTimelinePoint> Points
    {
        get; init;
    } = Array.Empty<DifficultyTimelinePoint>();

    public double? DurationMs
    {
        get; init;
    }
}

public sealed record DifficultyTimelinePoint
{
    public double TimeMs
    {
        get; init;
    }

    public double Value
    {
        get; init;
    }
}

public sealed record RankEstimate
{
    public string SystemId { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    /// <summary>
    /// Identifies the ladder that describes the chart itself. Other ranks may
    /// still be carried as reference estimates, but presentation must not
    /// imply that a hybrid chart belongs to both ladders at once.
    /// </summary>
    public bool IsPrimary
    {
        get; init;
    }
    public double? NumericValue
    {
        get; init;
    }
}

public sealed record SkillMetric
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string ValueLabel { get; init; } = string.Empty;
    public double? Value
    {
        get; init;
    }
    public double NormalizedValue
    {
        get; init;
    }
    public string Detail { get; init; } = string.Empty;
}
