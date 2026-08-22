using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class PauseCoachAnalyzerTests
{
    private static LiveTelemetryFrame Frame(
        int mapTimeMs,
        bool isPaused,
        bool isPlaying,
        LiveJudgementCounts? judgements = null,
        double[]? hitErrors = null,
        int? score = null,
        double? accuracy = null,
        double? health = null,
        int? combo = null,
        int? maxCombo = null,
        bool failed = false,
        string[]? mods = null)
    {
        return new LiveTelemetryFrame(
            mapTimeMs: mapTimeMs,
            isPaused: isPaused,
            isFocused: true,
            isPlaying: isPlaying,
            judgements: judgements ?? new LiveJudgementCounts(),
            score: score,
            accuracy: accuracy,
            health: health,
            combo: combo,
            maxCombo: maxCombo,
            hitErrorArray: hitErrors,
            failed: failed,
            mods: mods);
    }

    [Fact]
    public void ReturnsNullWhenNoFrames()
    {
        Assert.Null(PauseCoachAnalyzer.Analyze([]));
        Assert.Null(PauseCoachAnalyzer.Analyze(Array.Empty<LiveTelemetryFrame>()));
    }

    [Fact]
    public void ReturnsNullWhenNoTransitionToPaused()
    {
        var frames = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true),
            Frame(1100, isPaused: false, isPlaying: true),
        };

        Assert.Null(PauseCoachAnalyzer.Analyze(frames));
    }

    [Fact]
    public void ReturnsNullWhenOnlyPausedWithoutPlayingPrevious()
    {
        var frames = new[]
        {
            Frame(1000, isPaused: true, isPlaying: false),
            Frame(1100, isPaused: true, isPlaying: false),
        };

        Assert.Null(PauseCoachAnalyzer.Analyze(frames));
    }

    [Fact]
    public void ReturnsSnapshotForLatestPlayingToPausedTransition()
    {
        var frames = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true, judgements: new LiveJudgementCounts(10, 0, 0, 0, 1), hitErrors: [-5, 5]),
            Frame(1100, isPaused: true, isPlaying: false, judgements: new LiveJudgementCounts(10, 0, 0, 0, 1), hitErrors: [-5, 5]),
        };

        PauseCoachSnapshot? snapshot = PauseCoachAnalyzer.Analyze(frames);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.IsProvisional);
        Assert.Equal("provisional", snapshot.Fidelity);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Reason));
    }

    [Fact]
    public void UsesLatestTransitionWhenMultiplePauses()
    {
        var frames = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true, score: 1000),
            Frame(1100, isPaused: true, isPlaying: false, score: 2000),
            Frame(1200, isPaused: false, isPlaying: true, score: 3000),
            Frame(1300, isPaused: true, isPlaying: false, score: 9999),
        };

        PauseCoachSnapshot? snapshot = PauseCoachAnalyzer.Analyze(frames);

        Assert.NotNull(snapshot);
        Assert.Equal(1300, snapshot!.MapProgressMs);
        Assert.Equal(9999, snapshot.Score);
    }

    [Fact]
    public void CalculatesMeanMedianSdUrEarlyLateFromLatestOffsets()
    {
        double[] offsets = [-10, 0, 10];
        var frames = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true, hitErrors: [1, 2]),
            Frame(1100, isPaused: true, isPlaying: false, hitErrors: offsets),
        };

        PauseCoachSnapshot? snapshot = PauseCoachAnalyzer.Analyze(frames);

        Assert.NotNull(snapshot);
        PauseCoachTimingSnapshot timing = snapshot!.Timing;
        Assert.Equal(3, timing.SampleCount);
        Assert.Equal(0, timing.MeanMs!.Value, precision: 5);
        Assert.Equal(0, timing.MedianMs!.Value, precision: 5);
        Assert.True(timing.UnstableRate.HasValue);
        double expectedSd = Math.Sqrt((100 + 0 + 100) / 3.0);
        Assert.Equal(expectedSd * 10, timing.UnstableRate!.Value, precision: 4);
        Assert.Equal(1, offsets.Count(x => x < 0));
        Assert.Equal(1, offsets.Count(x => x > 0));
        Assert.Equal(1.0, timing.EarlyLateRatio!.Value, precision: 5);
    }

    [Fact]
    public void NullEmptyOffsetsStayUnavailable()
    {
        var frames = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true, hitErrors: null),
            Frame(1100, isPaused: true, isPlaying: false, hitErrors: []),
        };

        PauseCoachSnapshot? snapshot = PauseCoachAnalyzer.Analyze(frames);

        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot!.Timing.SampleCount);
        Assert.Null(snapshot.Timing.MeanMs);
        Assert.Null(snapshot.Timing.MedianMs);
        Assert.Null(snapshot.Timing.UnstableRate);
        Assert.Null(snapshot.Timing.EarlyLateRatio);
        Assert.Equal("unknown", snapshot.Timing.TimingMargin);
        Assert.Empty(snapshot.Timing.RecentOffsets);
        Assert.Contains(snapshot.Diagnostics, d => d.Contains("no_offsets"));
    }

    [Fact]
    public void DuplicatePollingFramesDoNotInflateSampleCount()
    {
        double[] same = [1, 2, 3, 4, 5];
        var frames = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true, hitErrors: same),
            Frame(1010, isPaused: false, isPlaying: true, hitErrors: same),
            Frame(1020, isPaused: true, isPlaying: false, hitErrors: same),
            Frame(1030, isPaused: true, isPlaying: false, hitErrors: same),
        };

        PauseCoachSnapshot? snapshot = PauseCoachAnalyzer.Analyze(frames);

        Assert.NotNull(snapshot);
        // Must be latest array length, not aggregated across 4 polling frames.
        Assert.Equal(5, snapshot!.Timing.SampleCount);
        Assert.Equal(same.Length, snapshot.Timing.RecentOffsets.Count);
    }

    [Fact]
    public void CalculatesWholeAndRecentHitMissTotalsFromDelta()
    {
        var prev = new LiveJudgementCounts(count300: 10, count200: 2, count100: 1, count50: 0, countMiss: 1);
        var curr = new LiveJudgementCounts(count300: 12, count200: 2, count100: 1, count50: 0, countMiss: 2);

        var frames = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true, judgements: prev),
            Frame(1100, isPaused: true, isPlaying: false, judgements: curr),
        };

        PauseCoachSnapshot? snapshot = PauseCoachAnalyzer.Analyze(frames);

        Assert.NotNull(snapshot);
        Assert.Equal(15, snapshot!.Performance.WholeHits);
        Assert.Equal(2, snapshot.Performance.WholeMisses);
        Assert.Equal(2, snapshot.Performance.RecentHits);
        Assert.Equal(1, snapshot.Performance.RecentMisses);
    }

    [Fact]
    public void RecentPerformanceUsesPlaySegmentDeltaWhenPausedPollingContinues()
    {
        var prev = new LiveJudgementCounts(count300: 10, count200: 2, count100: 1, count50: 0, countMiss: 1);
        var curr = new LiveJudgementCounts(count300: 12, count200: 2, count100: 1, count50: 0, countMiss: 2);

        var frames = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true, judgements: prev, hitErrors: [1, 2]),
            Frame(1100, isPaused: true, isPlaying: false, judgements: curr, hitErrors: [-5, 5]),
            Frame(1200, isPaused: true, isPlaying: false, judgements: curr, hitErrors: [-5, 5, 10]),
        };

        PauseCoachSnapshot? snapshot = PauseCoachAnalyzer.Analyze(frames);

        Assert.NotNull(snapshot);
        // Latest paused frame provides current values/timing.
        Assert.Equal(1200, snapshot!.MapProgressMs);
        Assert.Equal(3, snapshot.Timing.SampleCount);
        // Recent must be delta from the last playing frame before the transition (prev), not from the previous paused polling frame.
        // If incorrectly using targetIndex-1 (paused), recent would be 0.
        Assert.Equal(15, snapshot.Performance.WholeHits);
        Assert.Equal(2, snapshot.Performance.WholeMisses);
        Assert.Equal(2, snapshot.Performance.RecentHits);
        Assert.Equal(1, snapshot.Performance.RecentMisses);

        // Also verify latest timing comes from latest paused polling frame, not transition frame.
        Assert.Equal(3, snapshot.Timing.RecentOffsets.Count);
    }

    [Fact]
    public void HandlesResetSafelyViaDeltaFrom()
    {
        var prev = new LiveJudgementCounts(count300: 100, count200: 5, count100: 0, count50: 0, countMiss: 10);
        var curr = new LiveJudgementCounts(count300: 5, count200: 0, count100: 0, count50: 0, countMiss: 1);

        var frames = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true, judgements: prev),
            Frame(1100, isPaused: true, isPlaying: false, judgements: curr),
        };

        PauseCoachSnapshot? snapshot = PauseCoachAnalyzer.Analyze(frames);

        Assert.NotNull(snapshot);
        // Reset → delta returns whole current without negative.
        Assert.Equal(5, snapshot!.Performance.WholeHits);
        Assert.Equal(1, snapshot.Performance.WholeMisses);
        Assert.Equal(5, snapshot.Performance.RecentHits);
        Assert.Equal(1, snapshot.Performance.RecentMisses);
    }

    [Fact]
    public void IncludesMapProgressScoreAccuracyHealthComboModsAndFidelity()
    {
        var frames = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true),
            Frame(1100, isPaused: true, isPlaying: false,
                judgements: new LiveJudgementCounts(5, 0, 0, 0, 0),
                hitErrors: [2, 3],
                score: 12345,
                accuracy: 0.987,
                health: 0.75,
                combo: 42,
                maxCombo: 100,
                failed: true,
                mods: ["HD", "DT"]),
        };

        PauseCoachSnapshot? snapshot = PauseCoachAnalyzer.Analyze(frames);

        Assert.NotNull(snapshot);
        Assert.Equal(1100, snapshot!.MapProgressMs);
        Assert.Equal(12345, snapshot.Score);
        Assert.Equal(0.987, snapshot.Accuracy!.Value, precision: 5);
        Assert.Equal(0.75, snapshot.Health!.Value, precision: 5);
        Assert.Equal(42, snapshot.Combo);
        Assert.Equal(100, snapshot.MaxCombo);
        Assert.True(snapshot.Failed);
        Assert.Equal(["HD", "DT"], snapshot.Mods);
        Assert.True(snapshot.IsProvisional);
        Assert.Contains("aggregate", snapshot.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TimingMarginAndInsightsConservativeWithThreshold()
    {
        // Below threshold → unknown, no insights.
        var small = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true, hitErrors: [100, 100, 100]),
            Frame(1100, isPaused: true, isPlaying: false, hitErrors: [100, 100, 100]),
        };

        PauseCoachSnapshot? smallSnap = PauseCoachAnalyzer.Analyze(small);
        Assert.NotNull(smallSnap);
        Assert.Equal("unknown", smallSnap!.Timing.TimingMargin);
        Assert.Empty(smallSnap.Insights);

        // At threshold with large mean → insight emitted, margin not unknown.
        double[] many = Enumerable.Repeat(15.0, 12).ToArray();
        var large = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true, hitErrors: many),
            Frame(1100, isPaused: true, isPlaying: false, hitErrors: many),
        };

        PauseCoachSnapshot? largeSnap = PauseCoachAnalyzer.Analyze(large);
        Assert.NotNull(largeSnap);
        Assert.NotEqual("unknown", largeSnap!.Timing.TimingMargin);
        Assert.NotEmpty(largeSnap.Insights);
    }

    [Fact]
    public void SectionValuesRemainNullAndNoPerColumnClaims()
    {
        double[] many = Enumerable.Range(0, 15).Select(i => (double)i).ToArray();
        var frames = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true, hitErrors: many),
            Frame(1100, isPaused: true, isPlaying: false, hitErrors: many),
        };

        PauseCoachSnapshot? snapshot = PauseCoachAnalyzer.Analyze(frames);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Section.Index);
        Assert.Null(snapshot.Section.StartTimeMs);
        Assert.Null(snapshot.Section.EndTimeMs);
        Assert.Null(snapshot.Section.Nps);
        Assert.Null(snapshot.Section.LocalRelativeStrain);
        Assert.Null(snapshot.Section.DominantPatternKind);

        // No insight or diagnostic mentions column/object/finger/LN.
        string combined = string.Join(" ", snapshot.Insights.Select(i => i.Code + " " + i.Message))
            + " " + string.Join(" ", snapshot.Diagnostics);
        Assert.DoesNotContain("column", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("per-column", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("finger", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" LN", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiagnosticsForMissingCounts()
    {
        var frames = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true, judgements: new LiveJudgementCounts()),
            Frame(1100, isPaused: true, isPlaying: false, judgements: new LiveJudgementCounts(), hitErrors: [1, 2]),
        };

        PauseCoachSnapshot? snapshot = PauseCoachAnalyzer.Analyze(frames);

        Assert.NotNull(snapshot);
        Assert.Contains(snapshot!.Diagnostics, d => d.Contains("no_counts"));
    }

    [Fact]
    public void RecentOffsetsContainsAtMost20FromLatest()
    {
        double[] many = Enumerable.Range(0, 50).Select(i => (double)i).ToArray();
        var frames = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true, hitErrors: many),
            Frame(1100, isPaused: true, isPlaying: false, hitErrors: many),
        };

        PauseCoachSnapshot? snapshot = PauseCoachAnalyzer.Analyze(frames);

        Assert.NotNull(snapshot);
        Assert.Equal(20, snapshot!.Timing.RecentOffsets.Count);
        Assert.Equal(many[^20..], snapshot.Timing.RecentOffsets);
    }

    [Fact]
    public void EarlyLateRatioIsNullWhenAllEarlyAndSerializationSucceeds()
    {
        double[] allEarly = Enumerable.Repeat(-5.0, 12).ToArray();
        var frames = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true, hitErrors: allEarly),
            Frame(1100, isPaused: true, isPlaying: false, hitErrors: allEarly),
        };

        PauseCoachSnapshot? snapshot = PauseCoachAnalyzer.Analyze(frames);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Timing.EarlyLateRatio);
        string json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("Infinity", json);
        PauseCoachSnapshot? roundTrip = JsonSerializer.Deserialize<PauseCoachSnapshot>(json);
        Assert.NotNull(roundTrip);
        Assert.Null(roundTrip!.Timing.EarlyLateRatio);
    }

    [Fact]
    public void EarlyLateRatioIsNullWhenAllLateAndSerializationSucceeds()
    {
        double[] allLate = Enumerable.Repeat(7.0, 12).ToArray();
        var frames = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true, hitErrors: allLate),
            Frame(1100, isPaused: true, isPlaying: false, hitErrors: allLate),
        };

        PauseCoachSnapshot? snapshot = PauseCoachAnalyzer.Analyze(frames);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Timing.EarlyLateRatio);
        string json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("Infinity", json);
        PauseCoachSnapshot? roundTrip = JsonSerializer.Deserialize<PauseCoachSnapshot>(json);
        Assert.NotNull(roundTrip);
        Assert.Null(roundTrip!.Timing.EarlyLateRatio);
    }

    [Fact]
    public void EarlyLateRatioIsNullWhenNoEarlyLateEventsAndSerializationSucceeds()
    {
        double[] allZero = Enumerable.Repeat(0.0, 12).ToArray();
        var frames = new[]
        {
            Frame(1000, isPaused: false, isPlaying: true, hitErrors: allZero),
            Frame(1100, isPaused: true, isPlaying: false, hitErrors: allZero),
        };

        PauseCoachSnapshot? snapshot = PauseCoachAnalyzer.Analyze(frames);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Timing.EarlyLateRatio);
        string json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("Infinity", json);
        PauseCoachSnapshot? roundTrip = JsonSerializer.Deserialize<PauseCoachSnapshot>(json);
        Assert.NotNull(roundTrip);
        Assert.Null(roundTrip!.Timing.EarlyLateRatio);
    }
}
