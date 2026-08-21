using System.Text.Json;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

public static class ReplayMetrics
{
    public const string TimingUr = "replay.timing.ur";
    public const string TimingMean = "replay.timing.meanMs";
    public const string TimingMedian = "replay.timing.medianMs";
    public const string TimingSd = "replay.timing.sdMs";
    public const string TimingEarly = "replay.timing.earlyCount";
    public const string TimingLate = "replay.timing.lateCount";
    public const string TimingSample = "replay.timing.sampleCount";

    public static string ColumnBias(int column) => $"replay.column.{column}.biasMs";
    public static string ColumnUr(int column) => $"replay.column.{column}.ur";
    public static string ColumnMiss(int column) => $"replay.column.{column}.missCount";
    public static string ColumnHit(int column) => $"replay.column.{column}.hitCount";

    public const string SectionPrefix = "replay.section";
    public static string SectionAccuracy(int index) => $"replay.section.{index}.accuracy";
    public static string SectionUr(int index) => $"replay.section.{index}.ur";

    public static Dictionary<string, JsonElement> BuildMetrics(
        ReplayTimingStats? timing,
        IReadOnlyList<ReplayColumnStats> columns,
        IReadOnlyList<ReplaySection> sections,
        IReadOnlyList<ReplayInsight> insights)
    {
        Dictionary<string, JsonElement> metrics = new(StringComparer.OrdinalIgnoreCase);

        if (timing is not null)
        {
            metrics[TimingUr] = JsonSerializer.SerializeToElement(timing.UnstableRate);
            metrics[TimingMean] = JsonSerializer.SerializeToElement(timing.MeanMs);
            metrics[TimingMedian] = JsonSerializer.SerializeToElement(timing.MedianMs);
            metrics[TimingSd] = JsonSerializer.SerializeToElement(timing.StandardDeviationMs);
            metrics[TimingEarly] = JsonSerializer.SerializeToElement(timing.EarlyCount);
            metrics[TimingLate] = JsonSerializer.SerializeToElement(timing.LateCount);
            metrics[TimingSample] = JsonSerializer.SerializeToElement(timing.SampleCount);
        }

        foreach (ReplayColumnStats column in columns)
        {
            if (column.Timing is not null)
            {
                metrics[ColumnBias(column.Column)] = JsonSerializer.SerializeToElement(column.Timing.MeanMs);
                metrics[ColumnUr(column.Column)] = JsonSerializer.SerializeToElement(column.Timing.UnstableRate);
            }

            metrics[ColumnMiss(column.Column)] = JsonSerializer.SerializeToElement(column.MissCount);
            metrics[ColumnHit(column.Column)] = JsonSerializer.SerializeToElement(column.HitCount);
        }

        for (int index = 0; index < sections.Count; index++)
        {
            ReplaySection section = sections[index];
            metrics[SectionAccuracy(index)] = JsonSerializer.SerializeToElement(section.Accuracy);
            if (section.UnstableRate is not null)
            {
                metrics[SectionUr(index)] = JsonSerializer.SerializeToElement(section.UnstableRate.Value);
            }
        }

        if (insights.Count > 0)
        {
            metrics["replay.insights.count"] = JsonSerializer.SerializeToElement(insights.Count);
        }

        return metrics;
    }
}
