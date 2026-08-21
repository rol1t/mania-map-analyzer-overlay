namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

/// <summary>
/// Evidence-first insights with sample-size and confidence thresholds.
/// Categorical claims are suppressed when sampleCount &lt; MinimumSampleCount
/// or when confidence is below threshold.
/// </summary>
public sealed record ReplayInsight
{
    public ReplayInsight(
        string code,
        string message,
        double confidence,
        int sampleCount,
        Dictionary<string, string> evidence)
    {
        Code = code;
        Message = message;
        Confidence = confidence;
        SampleCount = sampleCount;
        Evidence = evidence;
    }

    public string Code
    {
        get;
    }
    public string Message
    {
        get;
    }
    public double Confidence
    {
        get;
    }
    public int SampleCount
    {
        get;
    }
    public Dictionary<string, string> Evidence
    {
        get;
    }
}

public static class ReplayInsights
{
    public const int MinimumSampleCount = 30;
    public const double MinimumConfidence = 0.6;

    /// <summary>
    /// Conservative rule: column UR vs median of eligible columns.
    /// Only eligible columns (sampleCount &gt;= MinimumSampleCount) are considered;
    /// a categorical claim requires the column's UR to exceed the median by
    /// at least urMargin.
    /// </summary>
    public static IReadOnlyList<ReplayInsight> BuildColumnUrInsights(
        IReadOnlyList<ReplayColumnStats> columnStats,
        double urMargin = 20,
        int minimumSampleCount = MinimumSampleCount)
    {
        ArgumentNullException.ThrowIfNull(columnStats);

        double[] eligibleUrs = columnStats
            .Where(item => item.Timing is not null && item.Timing.SampleCount >= minimumSampleCount)
            .Select(item => item.Timing!.UnstableRate)
            .ToArray();

        if (eligibleUrs.Length < 2)
        {
            return [];
        }

        double medianUr = Median(eligibleUrs);
        List<ReplayInsight> insights = [];

        foreach (ReplayColumnStats column in columnStats)
        {
            if (column.Timing is null || column.Timing.SampleCount < minimumSampleCount)
            {
                continue;
            }

            double ur = column.Timing.UnstableRate;
            double delta = ur - medianUr;

            if (delta < urMargin)
            {
                continue;
            }

            double confidence = Math.Clamp(delta / (medianUr == 0 ? 1 : medianUr), 0, 1);
            if (confidence < MinimumConfidence)
            {
                continue;
            }

            insights.Add(new ReplayInsight(
                code: "replay.column.ur_high",
                message: $"Column {column.Column} UR {ur:F1} exceeds median {medianUr:F1} by {delta:F1} (n={column.Timing.SampleCount}).",
                confidence: confidence,
                sampleCount: column.Timing.SampleCount,
                evidence: new Dictionary<string, string>
                {
                    ["column"] = column.Column.ToString(),
                    ["ur"] = ur.ToString("F1"),
                    ["medianUr"] = medianUr.ToString("F1"),
                    ["delta"] = delta.ToString("F1"),
                    ["sampleCount"] = column.Timing.SampleCount.ToString()
                }));
        }

        return insights;
    }

    private static double Median(double[] values)
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
