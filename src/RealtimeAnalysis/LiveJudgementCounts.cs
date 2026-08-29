namespace ManiaMapAnalyzerOverlay.RealtimeAnalysis;

/// <summary>
/// Cumulative judgement counts parsed from tosu telemetry. The values are
/// cumulative over the current attempt; deltas are derived by the timeline.
/// </summary>
public sealed record LiveJudgementCounts
{
    public LiveJudgementCounts(
        int count300 = 0,
        int count200 = 0,
        int count100 = 0,
        int count50 = 0,
        int countMiss = 0,
        int? countGeki = null,
        int? countKatu = null)
    {
        Count300 = count300;
        Count200 = count200;
        Count100 = count100;
        Count50 = count50;
        CountMiss = countMiss;
        CountGeki = countGeki;
        CountKatu = countKatu;
    }

    public int Count300
    {
        get;
    }

    public int Count200
    {
        get;
    }

    public int Count100
    {
        get;
    }

    public int Count50
    {
        get;
    }

    public int CountMiss
    {
        get;
    }

    public int? CountGeki
    {
        get;
    }

    public int? CountKatu
    {
        get;
    }

    public int Total => Count300 + Count200 + Count100 + Count50 + (CountGeki ?? 0) + (CountKatu ?? 0) + CountMiss;

    public int HitTotal => Count300 + Count200 + Count100 + Count50 + (CountGeki ?? 0) + (CountKatu ?? 0);

    public int NonMissTotal => Count300 + Count200 + Count100 + Count50 + (CountGeki ?? 0) + (CountKatu ?? 0);

    /// <summary>
    /// Computes the delta from an earlier cumulative count, treating a reset
    /// (any category decreasing) as a new baseline so deltas never go negative.
    /// </summary>
    public LiveJudgementCounts DeltaFrom(LiveJudgementCounts previous)
    {
        if (previous is null)
        {
            return this;
        }

        bool reset = Count300 < previous.Count300
            || Count200 < previous.Count200
            || Count100 < previous.Count100
            || Count50 < previous.Count50
            || CountGeki.HasValue && previous.CountGeki.HasValue && CountGeki < previous.CountGeki
            || CountKatu.HasValue && previous.CountKatu.HasValue && CountKatu < previous.CountKatu
            || CountMiss < previous.CountMiss;

        if (reset)
        {
            return this;
        }

        return new LiveJudgementCounts(
            Count300 - previous.Count300,
            Count200 - previous.Count200,
            Count100 - previous.Count100,
            Count50 - previous.Count50,
            CountMiss - previous.CountMiss,
            DeltaOrNull(CountGeki, previous.CountGeki),
            DeltaOrNull(CountKatu, previous.CountKatu));
    }

    private static int? DeltaOrNull(int? current, int? previous)
    {
        if (!current.HasValue || !previous.HasValue)
        {
            return current;
        }

        if (current.Value < previous.Value)
        {
            return current;
        }

        return current.Value - previous.Value;
    }
}
