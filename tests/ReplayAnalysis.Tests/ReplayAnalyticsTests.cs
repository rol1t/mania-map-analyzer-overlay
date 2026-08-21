using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class ReplayAnalyticsTests
{
    private static IReadOnlyList<JudgedHitEvent> SampleHits()
    {
        // 6 hits with offsets -10, -5, 0, 5, 10, 20 and 1 miss.
        OsuManiaBeatmap beatmap = ParseRice((1000, 0), (1100, 1), (1200, 2), (1300, 0), (1400, 1), (1500, 2), (1600, 0));
        var inputs = StableReplayDecoder.DecodeFrames(
        [
            (990, 1), (995, 0),
            (1095, 2), (1100, 0),
            (1200, 4), (1205, 0),
            (1305, 1), (1310, 0),
            (1410, 2), (1415, 0),
            (1520, 4), (1525, 0)
        ]);
        ReplayJudgeResult result = ReplayJudge.JudgeRice(beatmap, inputs);
        return result.JudgedHits;
    }

    [Fact]
    public void TimingStatsCalculateMeanMedianSdUrEarlyLate()
    {
        IReadOnlyList<JudgedHitEvent> hits = SampleHits();
        ReplayTimingStats? stats = ReplayTimingStats.Calculate(hits);
        Assert.NotNull(stats);
        Assert.Equal(6, stats!.SampleCount);
        Assert.True(stats.MeanMs > 0);
        Assert.True(stats.StandardDeviationMs > 0);
        Assert.Equal(stats.StandardDeviationMs * 10, stats.UnstableRate, precision: 5);
        Assert.Equal(2, stats.EarlyCount);
        Assert.Equal(3, stats.LateCount);
    }

    [Fact]
    public void TimingStatsExcludeMisses()
    {
        IReadOnlyList<JudgedHitEvent> hits = SampleHits();
        Assert.Contains(hits, item => item.IsMiss);
        ReplayTimingStats? stats = ReplayTimingStats.Calculate(hits);
        Assert.NotNull(stats);
        // 7 total, 1 miss → 6 samples.
        Assert.Equal(6, stats!.SampleCount);
    }

    [Fact]
    public void ColumnStatsCoverArbitraryKeyCounts()
    {
        IReadOnlyList<JudgedHitEvent> hits = SampleHits();
        IReadOnlyList<ReplayColumnStats> columns = ReplayColumnStats.Calculate(hits, keyCount: 4);
        Assert.Equal(4, columns.Count);
        Assert.Equal(3, columns[0].NoteCount);
        Assert.NotNull(columns[0].Timing);
        Assert.NotNull(columns[1].Timing);
        // Empty column still has entry but suppressed timing if no hits.
        Assert.Equal(0, columns[3].NoteCount);
        Assert.Null(columns[3].Timing);
    }

    [Fact]
    public void RollingWindowsLabelSampleCount()
    {
        // Build 60 notes linearly.
        OsuManiaBeatmap beatmap = ParseRice(Enumerable.Range(0, 60).Select(index => (1000 + index * 100, index % 4)).ToArray());
        var inputs = StableReplayDecoder.DecodeFrames(
            Enumerable.Range(0, 60).SelectMany(index => new[] { (1000 + index * 100 + 2, 1 << (index % 4)), (1000 + index * 100 + 10, 0) }).ToArray());

        ReplayJudgeResult result = ReplayJudge.JudgeRice(beatmap, inputs);
        IReadOnlyList<ReplayRollingWindow> byNotes = ReplayRollingWindow.ByNoteCount(result.JudgedHits, windowSize: 50);
        Assert.Equal(11, byNotes.Count);
        Assert.All(byNotes, window => Assert.Equal(50, window.SampleCount));

        IReadOnlyList<ReplayRollingWindow> byDuration = ReplayRollingWindow.ByDuration(result.JudgedHits, windowDurationMs: 10000);
        Assert.True(byDuration.Count >= 1);
        Assert.All(byDuration, window => Assert.True(window.SampleCount >= 0));
    }

    [Fact]
    public void SectionsContainEvidenceAndMetrics()
    {
        IReadOnlyList<JudgedHitEvent> hits = SampleHits();
        IReadOnlyList<ReplaySection> sections = ReplaySection.Build(hits, sectionDurationMs: 10000);
        Assert.Single(sections);
        ReplaySection section = sections[0];
        Assert.Equal(7, section.NoteCount);
        Assert.Equal(1, section.MissCount);
        Assert.Equal(7, section.Hits.Count);
        Assert.NotNull(section.BiasMs);
        Assert.NotNull(section.UnstableRate);
        Assert.True(section.Accuracy > 0 && section.Accuracy <= 1);

        // Every visual value links to filtered hits.
        Assert.Equal(section.Hits.Count(item => item.IsMiss), section.MissCount);
    }

    [Fact]
    public void InsightsRequireSampleThresholdAndConfidence()
    {
        // Column 0 high UR, others low UR, with sufficient samples.
        List<JudgedHitEvent> hits = [];
        for (int index = 0; index < 40; index++)
        {
            int offset = index < 20 ? (index % 2 == 0 ? 30 : -30) : (index % 2 == 0 ? 5 : -5);
            int column = index < 20 ? 0 : 1;
            hits.Add(new JudgedHitEvent($"obj-{index}", 1000 + index * 100, column, ReplayHitPhase.Note, ReplayJudgement.Great, 1.0, index, 1000 + index * 100 + offset, offset));
        }

        IReadOnlyList<ReplayColumnStats> columns = ReplayColumnStats.Calculate(hits, keyCount: 2);
        IReadOnlyList<ReplayInsight> insights = ReplayInsights.BuildColumnUrInsights(columns, urMargin: 10, minimumSampleCount: 10);
        Assert.NotEmpty(insights);
        Assert.All(insights, insight => Assert.True(insight.Confidence >= ReplayInsights.MinimumConfidence));
        Assert.All(insights, insight => Assert.True(insight.SampleCount >= 10));

        // Insufficient samples → suppressed.
        IReadOnlyList<ReplayInsight> suppressed = ReplayInsights.BuildColumnUrInsights(columns, minimumSampleCount: 100);
        Assert.Empty(suppressed);
    }

    [Fact]
    public void ProvenanceLinksEveryValueToFilteredHits()
    {
        OsuManiaBeatmap beatmap = ParseRice((1000, 0), (1100, 0));
        var inputs = StableReplayDecoder.DecodeFrames([(1002, 1), (1010, 0), (1105, 1), (1110, 0)]);
        ReplayJudgeResult result = ReplayJudge.JudgeRice(beatmap, inputs);
        ReplayTimingStats? stats = ReplayTimingStats.Calculate(result.JudgedHits);
        Assert.NotNull(stats);
        Assert.Equal("mania", result.Provenance.RulesetId);
        Assert.Equal(2, result.JudgedHits.Length);
        foreach (JudgedHitEvent hit in result.JudgedHits)
        {
            Assert.False(string.IsNullOrWhiteSpace(hit.BeatmapObjectId));
        }
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
