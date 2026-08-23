namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>Provenance of a realtime metric. Realtime telemetry is not an .osr replay.</summary>
public enum AnalysisDataQuality
{
    Exact,
    Observed,
    Reconstructed,
    Estimated,
    Unavailable
}

public enum PauseCoachWidgetState
{
    Unavailable,
    WaitingForGame,
    Playing,
    Paused,
    InsufficientData,
    Ready,
    Error
}

public enum PauseCoachInsightType
{
    InsufficientData,
    TimingEarly,
    TimingLate,
    TimingUnstable,
    AccuracyDrop,
    MissSpike,
    WeakColumn,
    HandImbalance,
    PatternDifficulty,
    SectionCollapse
}

public enum PauseCoachInsightSeverity
{
    Info,
    Warning,
    Critical
}

/// <summary>
/// Provisional live coaching snapshot produced when osu!mania transitions from
/// playing to paused. It deliberately stays aggregate when Tosu has no reliable
/// key/object correlation. Values are null when telemetry is missing.
/// </summary>
public sealed record PauseCoachSnapshot
{
    public bool HasData => MapProgressMs.HasValue
        || Score.HasValue
        || Accuracy.HasValue
        || (Timing?.SampleCount ?? 0) > 0
        || (Performance?.WholeHits ?? 0) > 0
        || (Insights?.Count ?? 0) > 0;

    public string Fidelity { get; init; } = string.Empty;

    public string State { get; init; } = nameof(PauseCoachWidgetState.Unavailable);

    public string SessionId { get; init; } = string.Empty;

    public string DataQuality { get; init; } = nameof(AnalysisDataQuality.Unavailable);

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string Reason { get; init; } = string.Empty;

    public bool IsProvisional { get; init; } = true;

    public int? MapProgressMs
    {
        get; init;
    }

    public int? Score
    {
        get; init;
    }

    public double? Accuracy
    {
        get; init;
    }

    public double? Health
    {
        get; init;
    }

    public int? Combo
    {
        get; init;
    }

    public int? MaxCombo
    {
        get; init;
    }

    public bool Failed
    {
        get; init;
    }

    public IReadOnlyList<string> Mods { get; init; } = Array.Empty<string>();

    public PauseCoachTimingSnapshot Timing { get; init; } = new();

    public PauseCoachOverallSnapshot Overall { get; init; } = new();

    public PauseCoachRecentSnapshot Recent { get; init; } = new();

    public PauseCoachPerformanceSnapshot Performance { get; init; } = new();

    public PauseCoachSectionSnapshot Section { get; init; } = new();

    public IReadOnlyList<PauseCoachColumnSnapshot> Columns { get; init; } = Array.Empty<PauseCoachColumnSnapshot>();

    public IReadOnlyList<PauseCoachSectionSnapshot> Sections { get; init; } = Array.Empty<PauseCoachSectionSnapshot>();

    public IReadOnlyList<PauseCoachInsightSnapshot> Insights { get; init; } = Array.Empty<PauseCoachInsightSnapshot>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed record PauseCoachTimingSnapshot
{
    public int SampleCount
    {
        get; init;
    }

    public double? MedianMs
    {
        get; init;
    }

    public double? MeanMs
    {
        get; init;
    }

    public double? UnstableRate
    {
        get; init;
    }

    public double? EarlyLateRatio
    {
        get; init;
    }

    public double? DriftMs
    {
        get; init;
    }

    public double? PreviousBaselineMs
    {
        get; init;
    }

    public string TimingMargin { get; init; } = "unknown";

    public IReadOnlyList<double> RecentOffsets { get; init; } = Array.Empty<double>();

    public string DataQuality { get; init; } = nameof(AnalysisDataQuality.Unavailable);
}

public sealed record PauseCoachOverallSnapshot
{
    public double? Accuracy
    {
        get; init;
    }

    public int? Combo
    {
        get; init;
    }

    public int? MaxCombo
    {
        get; init;
    }

    public int? Score
    {
        get; init;
    }

    public int? Hits
    {
        get; init;
    }

    public int? Misses
    {
        get; init;
    }

    public string DataQuality { get; init; } = nameof(AnalysisDataQuality.Unavailable);
}

public sealed record PauseCoachRecentSnapshot
{
    public int WindowSeconds
    {
        get; init;
    }

    public double? Accuracy
    {
        get; init;
    }

    public double? MeanTimingMs
    {
        get; init;
    }

    public double? TimingDeviationMs
    {
        get; init;
    }

    public int? Hits
    {
        get; init;
    }

    public int? Misses
    {
        get; init;
    }

    public string DataQuality { get; init; } = nameof(AnalysisDataQuality.Unavailable);
}

public sealed record PauseCoachPerformanceSnapshot
{
    public double? RecentAccuracy
    {
        get; init;
    }

    public double? WholeAccuracy
    {
        get; init;
    }

    public int? RecentMisses
    {
        get; init;
    }

    public int? WholeMisses
    {
        get; init;
    }

    public int? RecentHits
    {
        get; init;
    }

    public int? WholeHits
    {
        get; init;
    }
}

public sealed record PauseCoachSectionSnapshot
{
    public string Id { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public IReadOnlyList<string> PatternTypes { get; init; } = Array.Empty<string>();

    public double? AccuracyDelta
    {
        get; init;
    }

    public int? Misses
    {
        get; init;
    }

    public double? MeanHitErrorMs
    {
        get; init;
    }

    public double? TimingDeviationMs
    {
        get; init;
    }

    public string DataQuality { get; init; } = nameof(AnalysisDataQuality.Unavailable);

    public int? Index
    {
        get; init;
    }

    public int? StartTimeMs
    {
        get; init;
    }

    public int? EndTimeMs
    {
        get; init;
    }

    public double? Nps
    {
        get; init;
    }

    public double? LocalRelativeStrain
    {
        get; init;
    }

    public string? DominantPatternKind
    {
        get; init;
    }
}

public sealed record PauseCoachColumnSnapshot
{
    public int Column
    {
        get; init;
    }

    public int? PressCount
    {
        get; init;
    }

    public int? ReleaseCount
    {
        get; init;
    }

    public int? HitCount
    {
        get; init;
    }

    public int? MissCount
    {
        get; init;
    }

    public double? MeanTimingErrorMs
    {
        get; init;
    }

    public double? TimingDeviationMs
    {
        get; init;
    }

    public double? ErrorRate
    {
        get; init;
    }

    public string DataQuality { get; init; } = nameof(AnalysisDataQuality.Unavailable);
}

public sealed record PauseCoachInsightSnapshot
{
    public string Code { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Severity { get; init; } = nameof(PauseCoachInsightSeverity.Info);

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Evidence { get; init; } = string.Empty;

    public double? Confidence
    {
        get; init;
    }

    public string ConfidenceLabel { get; init; } = string.Empty;

    public string DataQuality { get; init; } = nameof(AnalysisDataQuality.Unavailable);

    public string Message { get; init; } = string.Empty;
}
