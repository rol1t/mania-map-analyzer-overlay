namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Provisional live coaching snapshot produced when osu!mania transitions from
/// playing to paused. It deliberately stays aggregate: no per-column, per-object,
/// per-finger or LN-release claims. Values are null when telemetry is missing.
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

    public PauseCoachPerformanceSnapshot Performance { get; init; } = new();

    public PauseCoachSectionSnapshot Section { get; init; } = new();

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

public sealed record PauseCoachInsightSnapshot
{
    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public double? Confidence
    {
        get; init;
    }
}
