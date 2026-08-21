using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class TosuLiveReplaySourceTests
{
    [Fact]
    public void LiveSnapshotIsProvisionalAndSuppressesPerColumn()
    {
        TosuLiveReplaySource source = new(capacity: 10);
        source.RecordLiveFrame(new TosuLiveFrame(MapTimeMs: 1000, Score: 10000, HitOffsetMs: -5));
        source.RecordLiveFrame(new TosuLiveFrame(MapTimeMs: 1100, Score: 20000, HitOffsetMs: 10));
        source.RecordLiveFrame(new TosuLiveFrame(MapTimeMs: 1200, Score: 30000, HitOffsetMs: -12));

        ReplayLiveSnapshot snapshot = source.GetLiveSnapshot();
        Assert.Equal(ReplayAnalysisFidelity.Provisional, snapshot.Provenance.Fidelity);
        Assert.Equal(1200, snapshot.MapProgressMs);
        Assert.NotNull(snapshot.AggregateUr);
        Assert.Equal(3, snapshot.RecentOffsets.Count);
        Assert.Contains(snapshot.Diagnostics, item => item.Code == "replay.live.provisional");
        // No per-column metrics are produced live.
        Assert.DoesNotContain(snapshot.Diagnostics, item => item.Code == "replay.column.ur_high");
    }

    [Fact]
    public void BoundedBufferDropsOldestUnderBackpressure()
    {
        TosuLiveReplaySource source = new(capacity: 5);
        for (int index = 0; index < 10; index++)
        {
            source.RecordLiveFrame(new TosuLiveFrame(MapTimeMs: index * 100, Score: index * 1000));
        }

        Assert.Equal(5, source.BufferedCount);
        ReplayLiveSnapshot snapshot = source.GetLiveSnapshot();
        Assert.Equal(900, snapshot.MapProgressMs);
    }

    [Fact]
    public void FinalizeReplacesProvisionalWithExact()
    {
        TosuLiveReplaySource source = new();
        source.RecordLiveFrame(new TosuLiveFrame(MapTimeMs: 1000, Score: 5000, HitOffsetMs: 15));

        ReplayLiveSnapshot provisional = source.GetLiveSnapshot();
        Assert.Equal(ReplayAnalysisFidelity.Provisional, provisional.Provenance.Fidelity);

        OsuManiaBeatmap beatmap = ParseRice((1000, 0), (1100, 1));
        var inputs = StableReplayDecoder.DecodeFrames([(1002, 1), (1010, 0), (1105, 1), (1110, 0)]);
        ReplayJudgeResult exact = source.FinalizeWithReplayFile(beatmap, inputs);
        Assert.Equal(ReplayAnalysisFidelity.Exact, exact.Provenance.Fidelity);
        Assert.Equal(2, exact.JudgedHits.Length);
    }

    [Fact]
    public async Task ReadInputEventsReturnsEmptyForLive()
    {
        TosuLiveReplaySource source = new();
        var store = new InMemoryReplayArtifactStore();
        ReplayArtifactHandle handle = store.Create([0x01], fileName: "live.osr");
        ReplayArtifact artifact = new(handle, ReplaySourceKind.ProvisionalLive);

        IReadOnlyList<ReplayInputEvent> events = await source.ReadInputEventsAsync(artifact);
        Assert.Empty(events);
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
