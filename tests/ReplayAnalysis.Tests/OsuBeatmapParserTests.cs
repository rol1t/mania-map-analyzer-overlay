using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class OsuBeatmapParserTests
{
    [Fact]
    public void ParsesRiceAndLongNotesWithColumnFromX()
    {
        string osu = """
            osu file format v14
            [Difficulty]
            CircleSize:4
            [HitObjects]
            64,192,1000,1,0,0:0:0:0:
            192,192,1000,1,0,0:0:0:0:
            320,192,1200,128,0,1500:0:0:0:0:
            """;

        OsuManiaBeatmap beatmap = OsuBeatmapParser.Parse(osu, beatmapHash: "hash1");
        Assert.Equal(4, beatmap.KeyCount);
        Assert.Equal(3, beatmap.HitObjects.Length);
        Assert.True(beatmap.HasLongNotes);
        Assert.Equal(2, beatmap.RiceNotes.Count);

        // x 64 → col 0, x 192 → col 1 for 4K (512/4=128)
        Assert.Equal(0, beatmap.HitObjects[0].Column);
        Assert.Equal(1, beatmap.HitObjects[1].Column);
        Assert.True(beatmap.HitObjects[2].IsLongNote);
        Assert.Equal(1500, beatmap.HitObjects[2].EndTimeMs);
    }

    [Fact]
    public void ParsesRateModVariantsPreserveMapTime()
    {
        string osu = """
            [Difficulty]
            CircleSize:4
            [HitObjects]
            64,192,2000,1,0,0:0:0:0:
            """;

        OsuManiaBeatmap beatmap = OsuBeatmapParser.Parse(osu, "h2");
        Assert.Single(beatmap.HitObjects);
        Assert.Equal(2000, beatmap.HitObjects[0].StartTimeMs);
    }
}
