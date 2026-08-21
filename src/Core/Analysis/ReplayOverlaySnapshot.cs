namespace ManiaMapAnalyzerOverlay.Core.Analysis;

public sealed record ReplayOverlaySnapshot
{
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
    public double? Ur
    {
        get; init;
    }
    public double? MeanMs
    {
        get; init;
    }
    public double? MedianMs
    {
        get; init;
    }
    public double? SdMs
    {
        get; init;
    }
    public int? EarlyCount
    {
        get; init;
    }
    public int? LateCount
    {
        get; init;
    }
    public int? SampleCount
    {
        get; init;
    }
    public IReadOnlyList<int> RecentOffsets { get; init; } = Array.Empty<int>();
    public bool IsProvisional
    {
        get; init;
    }
    public string Fidelity { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public IReadOnlyList<ReplayColumnSnapshot> Columns { get; init; } = Array.Empty<ReplayColumnSnapshot>();
    public IReadOnlyList<ReplaySectionSnapshot> Sections { get; init; } = Array.Empty<ReplaySectionSnapshot>();
    public IReadOnlyList<ReplayInsightSnapshot> Insights { get; init; } = Array.Empty<ReplayInsightSnapshot>();
    public bool HasData => MapProgressMs.HasValue || Score.HasValue || SampleCount.HasValue || Columns.Count > 0;
}

public sealed record ReplayColumnSnapshot
{
    public int Column
    {
        get; init;
    }
    public double? BiasMs
    {
        get; init;
    }
    public double? Ur
    {
        get; init;
    }
    public int? MissCount
    {
        get; init;
    }
    public int? HitCount
    {
        get; init;
    }
}

public sealed record ReplaySectionSnapshot
{
    public int Index
    {
        get; init;
    }
    public double? Accuracy
    {
        get; init;
    }
    public double? Ur
    {
        get; init;
    }
}

public sealed record ReplayInsightSnapshot
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public double? Confidence
    {
        get; init;
    }
}
