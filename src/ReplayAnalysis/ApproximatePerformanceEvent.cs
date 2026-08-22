namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

/// <summary>
/// Approximate live event inferred from aggregate telemetry, never from exact
/// object-level data. Confidence is reduced because the event is derived from
/// polling deltas rather than replay-file evidence.
/// </summary>
public sealed record ApproximatePerformanceEvent
{
    public ApproximatePerformanceEvent(
        ApproximatePerformanceEventKind kind,
        int mapTimeMs,
        double confidence,
        int? previousCombo = null,
        int? comboAfter = null)
    {
        if (confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be within [0, 1].");
        }

        Kind = kind;
        MapTimeMs = mapTimeMs;
        Confidence = confidence;
        PreviousCombo = previousCombo;
        ComboAfter = comboAfter;
    }

    public ApproximatePerformanceEventKind Kind
    {
        get;
    }

    public int MapTimeMs
    {
        get;
    }

    public double Confidence
    {
        get;
    }

    public int? PreviousCombo
    {
        get;
    }

    public int? ComboAfter
    {
        get;
    }
}

public enum ApproximatePerformanceEventKind
{
    Miss = 0,
    ComboBreak = 1
}
