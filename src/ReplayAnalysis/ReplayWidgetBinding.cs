namespace ManiaMapAnalyzerOverlay.ReplayAnalysis;

/// <summary>
/// Allows widget bindings to combine replay metrics with ManiaMapAnalyser metrics
/// (e.g. local MSD versus rolling UR) via the shared semantic metric contract.
/// </summary>
public static class ReplayWidgetBinding
{
    public static IReadOnlyDictionary<string, string> SuggestBindings()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["localMsdVsUr"] = $"{ReplayMetrics.TimingUr} vs difficulty.star",
            ["biasVsColumn"] = $"{ReplayMetrics.ColumnBias(0)} / {ReplayMetrics.ColumnBias(1)}",
            ["sectionAccuracyVsUr"] = $"{ReplayMetrics.SectionAccuracy(0)} / {ReplayMetrics.SectionUr(0)}"
        };
    }
}
