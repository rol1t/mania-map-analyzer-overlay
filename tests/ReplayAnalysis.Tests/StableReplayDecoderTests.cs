using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class StableReplayDecoderTests
{
    [Fact]
    public void DecodeFrameStringHandlesNegativeLeadInAndReconstructsKeyMask()
    {
        string decompressed = "-100|0|0|0,50|0|0|1,50|0|0|3,0|0|0|2,0|0|0|0";
        IReadOnlyList<ReplayInputEvent> events = StableReplayDecoder.DecodeFrameString(decompressed);

        // w=-100 at 0, +50 → -50, +50 → 0, +0 → 0, +0 → 0
        Assert.Contains(events, e => e.MapTimeMs == -50 && e.Column == 0 && e.Kind == ReplayInputKind.Press);
        Assert.Contains(events, e => e.MapTimeMs == 0 && e.Column == 1 && e.Kind == ReplayInputKind.Press);
        Assert.Contains(events, e => e.MapTimeMs == 0 && e.Column == 0 && e.Kind == ReplayInputKind.Release);
        Assert.Contains(events, e => e.MapTimeMs == 0 && e.Column == 1 && e.Kind == ReplayInputKind.Release);
    }

    [Fact]
    public void DecodeFramesPreservesDuplicateTimestampOrder()
    {
        var frames = new List<(int mapTimeMs, int keyMask)>
        {
            (1000, 1),
            (1000, 3),
            (1000, 0)
        };

        IReadOnlyList<ReplayInputEvent> events = StableReplayDecoder.DecodeFrames(frames);
        Assert.Equal(4, events.Count);
        Assert.Equal(1000, events[0].MapTimeMs);
        Assert.Equal(1000, events[1].MapTimeMs);
        ReplayInputOrdering.ValidateSequenceMonotonic(events);
    }
}
