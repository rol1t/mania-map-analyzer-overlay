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
    public double? LnPercent
    {
        get; init;
    }
    public int? Keys
    {
        get; init;
    }
}

public sealed record RankEstimate
{
    public string SystemId { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
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
