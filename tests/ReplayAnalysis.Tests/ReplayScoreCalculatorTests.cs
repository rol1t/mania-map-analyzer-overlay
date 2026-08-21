using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class ReplayScoreCalculatorTests
{
    [Fact]
    public void FidelityGateRequiresExactJudgementCountsAndCombo()
    {
        OsuManiaBeatmap beatmap = ParseRice((1000, 0), (1100, 1), (1200, 2));
        var inputs = StableReplayDecoder.DecodeFrames(
        [
            (1002, 1), (1010, 0),
            (1103, 2), (1110, 0),
            (1201, 4), (1210, 0)
        ]);

        ReplayJudgeResult result = ReplayJudge.JudgeRice(beatmap, inputs);
        ReplayScoreSummary actual = ReplayScoreCalculator.Summarize(result.JudgedHits, ReplayScorePolicy.StableClassic);

        // Expected fixture: 3 hits, 0 misses, maxCombo 3.
        var expected = new ReplayScoreSummary(perfect: 3, great: 0, good: 0, ok: 0, meh: 0, miss: 0, combo: 3, maxCombo: 3, accuracy: 1.0);
        Assert.True(ReplayScoreCalculator.ValidateFidelityGate(actual, expected));

        // Mismatch in perfect count → gate fails, becomes diagnostic not corrected result.
        var mismatched = new ReplayScoreSummary(perfect: 2, great: 1, good: 0, ok: 0, meh: 0, miss: 0, combo: 3, maxCombo: 3, accuracy: 1.0);
        Assert.False(ReplayScoreCalculator.ValidateFidelityGate(actual, mismatched));
    }

    [Fact]
    public void AccuracyToleranceIsDocumentedPerFixture()
    {
        var actual = new ReplayScoreSummary(1, 1, 0, 0, 0, 0, combo: 2, maxCombo: 2, accuracy: 0.96875);
        var expected = new ReplayScoreSummary(1, 1, 0, 0, 0, 0, combo: 2, maxCombo: 2, accuracy: 0.97);

        // Within 0.01 → pass, tighter tolerance → fail.
        Assert.True(ReplayScoreCalculator.ValidateFidelityGate(actual, expected, accuracyTolerance: 0.01));
        Assert.False(ReplayScoreCalculator.ValidateFidelityGate(actual, expected, accuracyTolerance: 0.001));
    }

    private static OsuManiaBeatmap ParseRice(params (int time, int column)[] notes)
    {
        string lines = string.Join("\n", notes.Select(item => $"{item.column * 128 + 64},192,{item.time},1,0,0:0:0:0:"));
        string osu = $"""
            [Difficulty]
            CircleSize:4
            [HitObjects]
            {lines}
            """;
        return OsuBeatmapParser.Parse(osu, "hash");
    }
}
