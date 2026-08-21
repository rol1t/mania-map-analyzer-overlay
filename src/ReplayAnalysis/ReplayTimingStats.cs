namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

/// <summary>
/// Timing inclusion contract:
/// - Only JudgedHitEvent with OffsetMs != null (i.e. IsHit) are included.
/// - Misses are excluded from mean/median/SD/UR/early-late (they are reported separately).
/// - Empty inclusion yields null stats (suppressed), never a default zero.
/// </summary>
public sealed record ReplayTimingStats
{
    public ReplayTimingStats(
        int sampleCount,
        double meanMs,
        double medianMs,
        double standardDeviationMs,
        double unstableRate,
        int earlyCount,
        int lateCount,
        double earlyLateRatio)
    {
        SampleCount = sampleCount;
        MeanMs = meanMs;
        MedianMs = medianMs;
        StandardDeviationMs = standardDeviationMs;
        UnstableRate = unstableRate;
        EarlyCount = earlyCount;
        LateCount = lateCount;
        EarlyLateRatio = earlyLateRatio;
    }

    public int SampleCount
    {
        get;
    }
    public double MeanMs
    {
        get;
    }
    public double MedianMs
    {
        get;
    }
    public double StandardDeviationMs
    {
        get;
    }
    public double UnstableRate
    {
        get;
    }
    public int EarlyCount
    {
        get;
    }
    public int LateCount
    {
        get;
    }
    public double EarlyLateRatio
    {
        get;
    }

    public static ReplayTimingStats? Calculate(IReadOnlyList<JudgedHitEvent> judgedHits)
    {
        ArgumentNullException.ThrowIfNull(judgedHits);

        double[] offsets = judgedHits
            .Where(item => item.OffsetMs is not null)
            .Select(item => (double)item.OffsetMs!.Value)
            .ToArray();

        if (offsets.Length == 0)
        {
            return null;
        }

        double mean = offsets.Average();
        double median = MedianOf(offsets);
        double variance = offsets.Select(value => (value - mean) * (value - mean)).Average();
        double sd = Math.Sqrt(variance);
        double ur = sd * 10;

        int early = offsets.Count(value => value < 0);
        int late = offsets.Count(value => value > 0);
        double ratio = late == 0 ? (early == 0 ? 0 : double.PositiveInfinity) : (double)early / late;

        return new ReplayTimingStats(offsets.Length, mean, median, sd, ur, early, late, ratio);
    }

    private static double MedianOf(double[] values)
    {
        double[] sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int count = sorted.Length;
        if (count % 2 == 1)
        {
            return sorted[count / 2];
        }

        return (sorted[count / 2 - 1] + sorted[count / 2]) / 2.0;
    }
}
