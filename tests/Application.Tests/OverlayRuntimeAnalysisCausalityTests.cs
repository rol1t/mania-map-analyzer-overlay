using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Application.Tests;

public sealed class OverlayRuntimeAnalysisCausalityTests
{
    [Fact]
    public void CurrentMapAnalysisUsesCurrentGeneration()
    {
        var current = new OverlayRuntimeState
        {
            BeatmapId = "674175",
            BeatmapGeneration = 4
        };

        long generation = OverlayRuntimeAnalysisCausality.ResolveBeatmapGeneration(
            current,
            Analysis("674175"));

        Assert.Equal(4, generation);
    }

    [Fact]
    public void FutureMapAnalysisDoesNotBorrowPreviousMapGeneration()
    {
        var current = new OverlayRuntimeState
        {
            BeatmapId = "674175",
            BeatmapGeneration = 4
        };

        long generation = OverlayRuntimeAnalysisCausality.ResolveBeatmapGeneration(
            current,
            Analysis("1540669"));

        Assert.Equal(0, generation);
    }

    private static AnalysisSnapshot Analysis(string beatmapId)
    {
        return new AnalysisSnapshot
        {
            SourceId = "headless",
            Beatmap = new BeatmapSnapshot { Id = beatmapId }
        };
    }
}
