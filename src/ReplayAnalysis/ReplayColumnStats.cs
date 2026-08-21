namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

/// <summary>
/// Per-column stats for arbitrary key counts. Hand grouping is not inferred;
/// callers configure grouping externally. Each column's stats are suppressed
/// when its sample count is below the caller-supplied threshold.
/// </summary>
public sealed record ReplayColumnStats
{
    public ReplayColumnStats(
        int column,
        int noteCount,
        int hitCount,
        int missCount,
        Dictionary<ReplayJudgement, int> judgementCounts,
        ReplayTimingStats? timing)
    {
        Column = column;
        NoteCount = noteCount;
        HitCount = hitCount;
        MissCount = missCount;
        JudgementCounts = judgementCounts;
        Timing = timing;
    }

    public int Column
    {
        get;
    }
    public int NoteCount
    {
        get;
    }
    public int HitCount
    {
        get;
    }
    public int MissCount
    {
        get;
    }
    public Dictionary<ReplayJudgement, int> JudgementCounts
    {
        get;
    }
    public ReplayTimingStats? Timing
    {
        get;
    }

    public static IReadOnlyList<ReplayColumnStats> Calculate(
        IReadOnlyList<JudgedHitEvent> judgedHits,
        int keyCount)
    {
        ArgumentNullException.ThrowIfNull(judgedHits);
        if (keyCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(keyCount));
        }

        List<ReplayColumnStats> result = new(capacity: keyCount);

        for (int column = 0; column < keyCount; column++)
        {
            JudgedHitEvent[] slice = judgedHits.Where(item => item.Column == column).ToArray();
            Dictionary<ReplayJudgement, int> counts = Enum.GetValues<ReplayJudgement>()
                .ToDictionary(judgement => judgement, _ => 0);
            foreach (JudgedHitEvent hit in slice)
            {
                counts[hit.Judgement]++;
            }

            int hits = slice.Count(item => item.OffsetMs is not null);
            int misses = slice.Count(item => item.IsMiss);
            ReplayTimingStats? timing = ReplayTimingStats.Calculate(slice);

            result.Add(new ReplayColumnStats(column, slice.Length, hits, misses, counts, timing));
        }

        return result;
    }
}
