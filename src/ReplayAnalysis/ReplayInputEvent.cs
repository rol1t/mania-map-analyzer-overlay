namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

/// <summary>
/// Normalized, ordered input transition derived from a replay source.
/// MapTimeMs is the map clock used for judgement; AudioTimeMs and Rate
/// are carried separately and never used as judgement inputs.
/// </summary>
public sealed record ReplayInputEvent
{
    public ReplayInputEvent(
        int mapTimeMs,
        int column,
        ReplayInputKind kind,
        long sourceSequence,
        int keyMask,
        double? audioTimeMs = null,
        double? rate = null,
        string? sourcePrecision = null)
    {
        if (column < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(column), "Column cannot be negative.");
        }

        if (sourceSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSequence), "Source sequence cannot be negative.");
        }

        MapTimeMs = mapTimeMs;
        Column = column;
        Kind = kind;
        SourceSequence = sourceSequence;
        KeyMask = keyMask;
        AudioTimeMs = audioTimeMs;
        Rate = rate;
        SourcePrecision = sourcePrecision?.Trim() ?? string.Empty;
    }

    public int MapTimeMs
    {
        get;
    }

    public int Column
    {
        get;
    }

    public ReplayInputKind Kind
    {
        get;
    }

    public long SourceSequence
    {
        get;
    }

    public int KeyMask
    {
        get;
    }

    public double? AudioTimeMs
    {
        get;
    }

    public double? Rate
    {
        get;
    }

    public string SourcePrecision
    {
        get;
    }
}
