using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class ReplayJudgeTests
{
    private static OsuManiaBeatmap RiceBeatmap(params (int time, int column)[] notes)
    {
        string lines = string.Join("\n", notes.Select(item => $"{item.column * 128 + 64},192,{item.time},1,0,0:0:0:0:"));
        string osu = $"""
            [Difficulty]
            CircleSize:4
            [HitObjects]
            {lines}
            """;
        return OsuBeatmapParser.Parse(osu, "test-hash");
    }

    [Fact]
    public void JudgeMatchesSingleNotesAndMeanBias()
    {
        OsuManiaBeatmap beatmap = RiceBeatmap((1000, 0), (2000, 1));
        var inputs = StableReplayDecoder.DecodeFrames(
        [
            (995, 1),
            (1000, 0),
            (2010, 2),
            (2020, 0)
        ]);

        ReplayJudgeResult result = ReplayJudge.JudgeRice(beatmap, inputs);
        Assert.Equal(2, result.JudgedHits.Length);
        Assert.Equal(-5, result.JudgedHits[0].OffsetMs);
        Assert.Equal(10, result.JudgedHits[1].OffsetMs);
        Assert.Equal(ReplayJudgement.Perfect, result.JudgedHits[0].Judgement);
        Assert.Equal(ReplayJudgement.Perfect, result.JudgedHits[1].Judgement);
        Assert.Equal(ReplayAnalysisFidelity.Exact, result.Provenance.Fidelity);
    }

    [Fact]
    public void JudgeHandlesSameTimestampChordWithColumnSeparation()
    {
        OsuManiaBeatmap beatmap = RiceBeatmap((1000, 0), (1000, 1), (1000, 2));
        var inputs = StableReplayDecoder.DecodeFrames(
        [
            (998, 1),
            (998, 3),
            (1003, 7),
            (1010, 0)
        ]);

        ReplayJudgeResult result = ReplayJudge.JudgeRice(beatmap, inputs);
        Assert.Equal(3, result.JudgedHits.Length);
        Assert.All(result.JudgedHits, item => Assert.NotEqual(ReplayJudgement.Miss, item.Judgement));
        Assert.Equal(3, result.JudgedHits.Select(item => item.Column).Distinct().Count());
    }

    [Fact]
    public void JudgeHandlesRepeatedColumnJacksWithClosestMatch()
    {
        OsuManiaBeatmap beatmap = RiceBeatmap((1000, 0), (1100, 0), (1200, 0));
        var inputs = StableReplayDecoder.DecodeFrames(
        [
            (1002, 1), (1010, 0),
            (1105, 1), (1110, 0),
            (1203, 1), (1210, 0)
        ]);

        ReplayJudgeResult result = ReplayJudge.JudgeRice(beatmap, inputs);
        Assert.Equal(3, result.JudgedHits.Length);
        Assert.Equal(2, result.JudgedHits[0].OffsetMs);
        Assert.Equal(5, result.JudgedHits[1].OffsetMs);
        Assert.Equal(3, result.JudgedHits[2].OffsetMs);
    }

    [Fact]
    public void JudgeHandlesDenseStreamAndMisses()
    {
        OsuManiaBeatmap beatmap = RiceBeatmap((1000, 0), (1050, 1), (1100, 2), (1150, 3));
        var inputs = StableReplayDecoder.DecodeFrames(
        [
            (1001, 1), (1005, 0),
            (1103, 4), (1110, 0)
        ]);

        ReplayJudgeResult result = ReplayJudge.JudgeRice(beatmap, inputs);
        Assert.Equal(4, result.JudgedHits.Length);
        Assert.Equal(2, result.JudgedHits.Count(item => item.IsMiss));
        Assert.Equal(2, result.JudgedHits.Count(item => item.IsHit));
    }

    [Fact]
    public void JudgeHandlesNegativeLeadInFrames()
    {
        OsuManiaBeatmap beatmap = RiceBeatmap((500, 0));
        var inputs = StableReplayDecoder.DecodeFrames(
        [
            (-100, 1), (-50, 0),
            (505, 1), (510, 0)
        ]);

        ReplayJudgeResult result = ReplayJudge.JudgeRice(beatmap, inputs);
        Assert.Single(result.JudgedHits);
        Assert.Equal(5, result.JudgedHits[0].OffsetMs);
        Assert.Contains(result.Diagnostics, item => item.Code == "replay.unmatched_input");
    }

    [Fact]
    public void JudgeHandlesDuplicateFrameTimesAndEarlyPressMisses()
    {
        OsuManiaBeatmap beatmap = RiceBeatmap((1000, 0), (1000, 1));
        // Early press 200ms before chord → outside 150ms window → should be unmatched, chord missed.
        var inputs = StableReplayDecoder.DecodeFrames(
        [
            (800, 1), (810, 0),
            (800, 2), (810, 0)
        ]);

        ReplayJudgeResult result = ReplayJudge.JudgeRice(beatmap, inputs);
        Assert.Equal(2, result.JudgedHits.Count(item => item.IsMiss));
        Assert.Contains(result.Diagnostics, item => item.Code == "replay.unmatched_input");
    }

    [Fact]
    public void JudgePreservesAmbiguousMatchAsDiagnostic()
    {
        OsuManiaBeatmap beatmap = RiceBeatmap((1000, 0));
        // Two presses equally distant (±10ms) → ambiguous.
        var inputs = new[]
        {
            new ReplayInputEvent(mapTimeMs: 990, column: 0, kind: ReplayInputKind.Press, sourceSequence: 0, keyMask: 1),
            new ReplayInputEvent(mapTimeMs: 1010, column: 0, kind: ReplayInputKind.Press, sourceSequence: 1, keyMask: 1)
        };

        ReplayJudgeResult result = ReplayJudge.JudgeRice(beatmap, inputs);
        Assert.Single(result.JudgedHits);
        Assert.Contains(result.Diagnostics, item => item.Code == "replay.ambiguous_match");
        Assert.Equal(ReplayAnalysisFidelity.Partial, result.Provenance.Fidelity);
    }

    [Fact]
    public void JudgeRejectsLongNotesWithUnsupported()
    {
        string osu = """
            [Difficulty]
            CircleSize:4
            [HitObjects]
            64,192,1000,128,0,1500:0:0:0:0:
            """;
        OsuManiaBeatmap beatmap = OsuBeatmapParser.Parse(osu, "h");
        var inputs = StableReplayDecoder.DecodeFrames([(1005, 1)]);
        ReplayJudgeResult result = ReplayJudge.JudgeRice(beatmap, inputs);
        Assert.Empty(result.JudgedHits);
        Assert.Equal(ReplayAnalysisFidelity.Unsupported, result.Provenance.Fidelity);
        Assert.Contains(result.Diagnostics, item => item.Code == "replay.ln_not_supported");
    }

    [Fact]
    public void JudgeHandlesRateModVariantMapTimeUnchanged()
    {
        OsuManiaBeatmap beatmap = RiceBeatmap((2000, 0));
        var inputs = new[]
        {
            new ReplayInputEvent(mapTimeMs: 2005, column: 0, kind: ReplayInputKind.Press, sourceSequence: 0, keyMask: 1, audioTimeMs: 1336, rate: 1.5)
        };

        ReplayJudgeResult result = ReplayJudge.JudgeRice(beatmap, inputs);
        Assert.Equal(5, result.JudgedHits[0].OffsetMs);
        Assert.Equal(2005, result.JudgedHits[0].ObservedMapTimeMs);
    }
}
