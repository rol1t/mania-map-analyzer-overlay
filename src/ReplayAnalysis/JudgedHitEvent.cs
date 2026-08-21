namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public sealed record JudgedHitEvent
{
    public JudgedHitEvent(
        string beatmapObjectId,
        int expectedMapTimeMs,
        int column,
        ReplayHitPhase phase,
        ReplayJudgement judgement,
        double confidence,
        long sourceSequence,
        int? observedMapTimeMs = null,
        int? offsetMs = null,
        string? sourcePrecision = null)
    {
        if (string.IsNullOrWhiteSpace(beatmapObjectId))
        {
            throw new ArgumentException("A beatmap object id is required.", nameof(beatmapObjectId));
        }

        if (column < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(column), "Column cannot be negative.");
        }

        if (confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be within [0, 1].");
        }

        if (observedMapTimeMs is not null && offsetMs is null)
        {
            throw new ArgumentException("Offset is required when observed map time is present.", nameof(offsetMs));
        }

        if (offsetMs is not null && observedMapTimeMs is null)
        {
            throw new ArgumentException("Observed map time is required when offset is present.", nameof(observedMapTimeMs));
        }

        if (offsetMs is not null && observedMapTimeMs is not null)
        {
            int expectedOffset = observedMapTimeMs.Value - expectedMapTimeMs;
            if (expectedOffset != offsetMs.Value)
            {
                throw new ArgumentException(
                    $"Offset must equal inputTime - objectTime ({expectedOffset}), but was {offsetMs.Value}.",
                    nameof(offsetMs));
            }
        }

        BeatmapObjectId = beatmapObjectId.Trim();
        ExpectedMapTimeMs = expectedMapTimeMs;
        ObservedMapTimeMs = observedMapTimeMs;
        OffsetMs = offsetMs;
        Column = column;
        Phase = phase;
        Judgement = judgement;
        Confidence = confidence;
        SourceSequence = sourceSequence;
        SourcePrecision = sourcePrecision?.Trim() ?? string.Empty;
    }

    public string BeatmapObjectId
    {
        get;
    }

    public int ExpectedMapTimeMs
    {
        get;
    }

    public int? ObservedMapTimeMs
    {
        get;
    }

    public int? OffsetMs
    {
        get;
    }

    public int Column
    {
        get;
    }

    public ReplayHitPhase Phase
    {
        get;
    }

    public ReplayJudgement Judgement
    {
        get;
    }

    public double Confidence
    {
        get;
    }

    public long SourceSequence
    {
        get;
    }

    public string SourcePrecision
    {
        get;
    }

    public bool IsHit => Judgement != ReplayJudgement.Miss;

    public bool IsMiss => Judgement == ReplayJudgement.Miss;
}
