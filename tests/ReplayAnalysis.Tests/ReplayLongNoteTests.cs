using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class ReplayLongNoteTests
{
    private static OsuManiaBeatmap LnBeatmap()
    {
        string osu = """
            [Difficulty]
            CircleSize:4
            [HitObjects]
            64,192,1000,128,0,1500:0:0:0:0:
            192,192,2000,1,0,0:0:0:0:0:
            """;
        return OsuBeatmapParser.Parse(osu, "ln-hash");
    }

    [Fact]
    public void LnHeadAndTailAreSeparateWithDroppedHoldDiagnostic()
    {
        OsuManiaBeatmap beatmap = LnBeatmap();
        // Head press at 1002 (hit), tail release missed (no release near 1500).
        var inputs = StableReplayDecoder.DecodeFrames(
        [
            (1002, 1), (1010, 0),
            (2002, 2), (2010, 0)
        ]);

        ReplayJudgeResult result = ReplayJudge.Judge(
            beatmap,
            inputs,
            new ReplayJudgeOptions { RejectLongNotes = false });

        // LN produces 2 events (head+tail) + 1 rice = 3 judged hits.
        Assert.Equal(3, result.JudgedHits.Length);
        JudgedHitEvent head = result.JudgedHits.First(item => item.Phase == ReplayHitPhase.LnHead);
        JudgedHitEvent tail = result.JudgedHits.First(item => item.Phase == ReplayHitPhase.LnTail);
        Assert.Equal(2, head.OffsetMs);
        Assert.True(tail.IsMiss);
        Assert.Contains(result.Diagnostics, item => item.Code == "replay.ln_dropped");
    }

    [Fact]
    public void LnBothOffsetsSeparate()
    {
        OsuManiaBeatmap beatmap = LnBeatmap();
        var inputs = StableReplayDecoder.DecodeFrames(
        [
            (1005, 1), (1505, 0),
            (2003, 2), (2010, 0)
        ]);

        ReplayJudgeResult result = ReplayJudge.Judge(
            beatmap,
            inputs,
            new ReplayJudgeOptions { RejectLongNotes = false });

        JudgedHitEvent head = result.JudgedHits.First(item => item.Phase == ReplayHitPhase.LnHead);
        JudgedHitEvent tail = result.JudgedHits.First(item => item.Phase == ReplayHitPhase.LnTail);
        Assert.Equal(5, head.OffsetMs);
        Assert.Equal(5, tail.OffsetMs);
        Assert.All(new[] { head, tail }, item => Assert.True(item.IsHit));
    }

    [Fact]
    public void RateModPreservesAudioAndMapTime()
    {
        OsuManiaBeatmap beatmap = LnBeatmap();
        var inputs = new[]
        {
            new ReplayInputEvent(mapTimeMs: 1005, column: 0, kind: ReplayInputKind.Press, sourceSequence: 0, keyMask: 1, audioTimeMs: 670, rate: 1.5),
            new ReplayInputEvent(mapTimeMs: 1505, column: 0, kind: ReplayInputKind.Release, sourceSequence: 1, keyMask: 0, audioTimeMs: 1003, rate: 1.5)
        };

        ReplayJudgeResult result = ReplayJudge.Judge(beatmap, inputs, new ReplayJudgeOptions { RejectLongNotes = false });
        JudgedHitEvent head = result.JudgedHits.First(item => item.Phase == ReplayHitPhase.LnHead);
        Assert.Equal(1.5, inputs[0].Rate);
        Assert.Equal(670, inputs[0].AudioTimeMs);
        Assert.Equal(1005, head.ObservedMapTimeMs);
        Assert.True(ReplayModPolicy.RequiresRateNormalization(["DT"]));
    }

    [Fact]
    public void LazerRequiresFixturesAndIsMarkedUnsupported()
    {
        Assert.Throws<ReplayUnsupportedException>(() => LazerReplayDecoder.Decode([0x01, 0x02]));
        ReplayDiagnostic diagnostic = LazerReplayDecoder.CreateUnsupportedDiagnostic(clientVersion: "lazer 2024.1000", mods: "HD");
        Assert.Equal("replay.lazer_not_supported", diagnostic.Code);

        ReplayDiagnostic? modDiagnostic = ReplayModPolicy.ValidateMods(["RD"], clientVersion: "stable 2024");
        Assert.NotNull(modDiagnostic);
        Assert.Equal("replay.mod_unsupported", modDiagnostic!.Code);
    }

    [Fact]
    public void LnScoreV2CarriesDistinctVersion()
    {
        OsuManiaBeatmap beatmap = LnBeatmap();
        var inputs = StableReplayDecoder.DecodeFrames([(1002, 1), (1502, 0)]);
        ReplayJudgeResult legacy = ReplayJudge.Judge(beatmap, inputs, new ReplayJudgeOptions { RejectLongNotes = false, LnPolicy = LnScoringPolicy.Legacy });
        ReplayJudgeResult scoreV2 = ReplayJudge.Judge(beatmap, inputs, new ReplayJudgeOptions { RejectLongNotes = false, LnPolicy = LnScoringPolicy.ScoreV2 });
        Assert.Contains("scorev2", scoreV2.Provenance.RulesetVersion, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scorev2", legacy.Provenance.RulesetVersion, StringComparison.OrdinalIgnoreCase);
    }
}
