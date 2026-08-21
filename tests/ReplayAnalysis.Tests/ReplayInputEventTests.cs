using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class ReplayInputEventTests
{
    [Fact]
    public void KeyMaskDecodePreservesSourceOrderForSameTimestampEdges()
    {
        // Chord at 1000ms (col 0+1 press), same-frame press/release on same column would be
        // two frames with different masks at the same mapTimeMs.
        var frames = new List<(int mapTimeMs, int keyMask)>
        {
            (1000, ReplayKeyMask.EncodeMask(0, 1)),
            (1000, ReplayKeyMask.EncodeMask(0)),
            (1001, ReplayKeyMask.EncodeMask())
        };

        IReadOnlyList<ReplayInputEvent> events = ReplayKeyMask.DecodeTransitions(frames, sourcePrecision: "stable.osr.frames");
        Assert.Equal(4, events.Count);

        // SourceSequence must be strictly increasing; ordering keeps it.
        ReplayInputOrdering.ValidateSequenceMonotonic(events);

        IReadOnlyList<ReplayInputEvent> ordered = ReplayInputOrdering.Order(events);
        Assert.Equal(0, ordered[0].SourceSequence);
        Assert.Equal(1, ordered[1].SourceSequence);

        // Do not re-sort equal-time edges by kind: press before release is source-defined.
        Assert.Equal(ReplayInputKind.Press, ordered[0].Kind);
        Assert.Equal(ReplayInputKind.Press, ordered[1].Kind);
        Assert.Equal(ReplayInputKind.Release, ordered[2].Kind);
    }

    [Fact]
    public void DuplicateTimestampsKeepSequenceOrder()
    {
        var events = new[]
        {
            new ReplayInputEvent(mapTimeMs: 500, column: 1, kind: ReplayInputKind.Press, sourceSequence: 1, keyMask: 0b0010),
            new ReplayInputEvent(mapTimeMs: 500, column: 0, kind: ReplayInputKind.Press, sourceSequence: 0, keyMask: 0b0001),
            new ReplayInputEvent(mapTimeMs: 500, column: 2, kind: ReplayInputKind.Release, sourceSequence: 2, keyMask: 0b0000)
        };

        IReadOnlyList<ReplayInputEvent> ordered = ReplayInputOrdering.Order(events);
        Assert.Equal(0, ordered[0].Column);
        Assert.Equal(1, ordered[1].Column);
        Assert.Equal(2, ordered[2].Column);
    }

    [Fact]
    public void KeyMaskTransitionsHandleChordAndJack()
    {
        IReadOnlyList<ReplayInputEvent> events = ReplayKeyMask.DecodeTransitions(
        [
            (0, ReplayKeyMask.EncodeMask(0)),
            (0, ReplayKeyMask.EncodeMask(0, 1)),
            (10, ReplayKeyMask.EncodeMask(1)),
            (20, ReplayKeyMask.EncodeMask(1)),
            (20, ReplayKeyMask.EncodeMask())
        ]);

        // Frame 0: press 0, frame 0 same ts: press 1, frame 10: release 0, frame 20: release 1
        Assert.Contains(events, e => e.Column == 0 && e.Kind == ReplayInputKind.Press && e.MapTimeMs == 0);
        Assert.Contains(events, e => e.Column == 1 && e.Kind == ReplayInputKind.Press && e.MapTimeMs == 0);
        Assert.Contains(events, e => e.Column == 0 && e.Kind == ReplayInputKind.Release && e.MapTimeMs == 10);
    }

    [Fact]
    public void MapTimeAndAudioTimeRemainSeparate()
    {
        var input = new ReplayInputEvent(
            mapTimeMs: 1000,
            column: 0,
            kind: ReplayInputKind.Press,
            sourceSequence: 0,
            keyMask: 1,
            audioTimeMs: 995.5,
            rate: 1.5);

        Assert.Equal(1000, input.MapTimeMs);
        Assert.Equal(995.5, input.AudioTimeMs);
        Assert.Equal(1.5, input.Rate);
    }

    [Fact]
    public void ProvenanceRequiresReasonForNonExact()
    {
        Assert.Throws<ArgumentException>(() =>
            new ReplayProvenance(ReplaySourceKind.StableOsr, ReplayAnalysisFidelity.Provisional, "mania", "1.0.0"));

        var exact = ReplayProvenance.ExactStable("mania", "1.0.0");
        Assert.True(exact.IsExact);

        var provisional = new ReplayProvenance(ReplaySourceKind.ProvisionalLive, ReplayAnalysisFidelity.Provisional, "mania", "1.0.0", reason: "live-not-exact");
        Assert.False(provisional.IsExact);
    }
}
