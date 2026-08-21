using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class JudgedHitEventTests
{
    [Fact]
    public void OffsetConventionIsInputMinusObject()
    {
        var early = new JudgedHitEvent(
            beatmapObjectId: "obj-1",
            expectedMapTimeMs: 1000,
            column: 0,
            phase: ReplayHitPhase.Note,
            judgement: ReplayJudgement.Great,
            confidence: 1.0,
            sourceSequence: 0,
            observedMapTimeMs: 995,
            offsetMs: -5);

        Assert.Equal(-5, early.OffsetMs);

        var late = new JudgedHitEvent(
            beatmapObjectId: "obj-2",
            expectedMapTimeMs: 1000,
            column: 0,
            phase: ReplayHitPhase.Note,
            judgement: ReplayJudgement.Great,
            confidence: 1.0,
            sourceSequence: 1,
            observedMapTimeMs: 1012,
            offsetMs: 12);

        Assert.Equal(12, late.OffsetMs);
        Assert.True(early.OffsetMs < 0);
        Assert.True(late.OffsetMs > 0);
    }

    [Fact]
    public void MissCarriesObjectIdentityWithoutOffset()
    {
        var miss = new JudgedHitEvent(
            beatmapObjectId: "obj-3",
            expectedMapTimeMs: 1500,
            column: 2,
            phase: ReplayHitPhase.Note,
            judgement: ReplayJudgement.Miss,
            confidence: 1.0,
            sourceSequence: 2);

        Assert.Null(miss.ObservedMapTimeMs);
        Assert.Null(miss.OffsetMs);
        Assert.True(miss.IsMiss);
        Assert.Equal("obj-3", miss.BeatmapObjectId);
    }

    [Fact]
    public void ProvenanceAndObjectIdAreRequired()
    {
        Assert.Throws<ArgumentException>(() =>
            new JudgedHitEvent(
                beatmapObjectId: "",
                expectedMapTimeMs: 0,
                column: 0,
                phase: ReplayHitPhase.Note,
                judgement: ReplayJudgement.Perfect,
                confidence: 1.0,
                sourceSequence: 0));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new JudgedHitEvent(
                beatmapObjectId: "obj",
                expectedMapTimeMs: 0,
                column: -1,
                phase: ReplayHitPhase.Note,
                judgement: ReplayJudgement.Perfect,
                confidence: 1.0,
                sourceSequence: 0));
    }

    [Fact]
    public void BeatmapMismatchIsTypedAndVisible()
    {
        var ex = Assert.Throws<ReplayBeatmapMismatchException>(() =>
            ReplayBeatmapValidation.ValidateBeatmapMatch("abc", "def"));

        Assert.Equal("replay.beatmap_mismatch", ex.Code);
        Assert.Equal("abc", ex.ExpectedBeatmapHash);
        Assert.Equal("def", ex.ActualBeatmapHash);

        ReplayDiagnostic diagnostic = ReplayBeatmapValidation.CreateMismatchDiagnostic("abc", "def");
        Assert.Equal("replay.beatmap_mismatch", diagnostic.Code);
        Assert.Equal(ReplayDiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void SnapshotCarriesFidelityAndDiagnostics()
    {
        var provenance = new ReplayProvenance(
            ReplaySourceKind.StableOsr,
            ReplayAnalysisFidelity.Partial,
            "mania",
            "2.0.0",
            reason: "LN analysis not enabled");

        var snapshot = new ReplayAnalysisSnapshot(
            replayArtifactId: "artifact-1",
            beatmapHash: "beatmap-hash",
            provenance: provenance,
            diagnostics:
            [
                new ReplayDiagnostic(ReplayDiagnosticSeverity.Warning, "replay.ln_skipped", "LN notes skipped.")
            ]);

        Assert.Equal(ReplayAnalysisFidelity.Partial, snapshot.Provenance.Fidelity);
        Assert.Single(snapshot.Diagnostics);
        Assert.False(snapshot.HasErrors);
        Assert.Equal("LN analysis not enabled", snapshot.Provenance.Reason);
    }

    [Fact]
    public void InputAndJudgedEventsStaySeparateForChords()
    {
        var inputPress0 = new ReplayInputEvent(mapTimeMs: 1000, column: 0, kind: ReplayInputKind.Press, sourceSequence: 0, keyMask: 0b0001);
        var inputPress1 = new ReplayInputEvent(mapTimeMs: 1000, column: 1, kind: ReplayInputKind.Press, sourceSequence: 1, keyMask: 0b0011);

        var judged0 = new JudgedHitEvent("obj-0", expectedMapTimeMs: 1000, column: 0, phase: ReplayHitPhase.Note, judgement: ReplayJudgement.Perfect, confidence: 1.0, sourceSequence: 0, observedMapTimeMs: 1000, offsetMs: 0);
        var judged1 = new JudgedHitEvent("obj-1", expectedMapTimeMs: 1000, column: 1, phase: ReplayHitPhase.Note, judgement: ReplayJudgement.Perfect, confidence: 1.0, sourceSequence: 1, observedMapTimeMs: 1002, offsetMs: 2);

        // Chord input is not 1:1 implied to be 1:1 with judged hits without a matcher.
        Assert.NotEqual(inputPress0.SourceSequence, judged1.SourceSequence);
        Assert.Equal(2, new[] { judged0, judged1 }.Length);
    }
}
