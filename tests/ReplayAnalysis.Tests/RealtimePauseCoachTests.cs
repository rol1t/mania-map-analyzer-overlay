using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class RealtimePauseCoachTests
{
    private static readonly DateTimeOffset _start = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static RealtimeTelemetrySample Sample(
        int seconds,
        RealtimePlayState state,
        double[]? offsets = null,
        double? accuracy = null,
        int misses = 0,
        int hits = 0,
        int? score = null,
        PauseCoachSectionSnapshot? section = null,
        DateTimeOffset? receivedAt = null)
    {
        return new RealtimeTelemetrySample(
            beatmapId: "map-1",
            state: state,
            mapTimeMs: seconds * 1000,
            judgements: new LiveJudgementCounts(count300: hits, countMiss: misses, countGeki: 0, countKatu: 0),
            accuracy: accuracy,
            score: score,
            hitErrorArray: offsets,
            currentSection: section,
            receivedAt: receivedAt ?? _start.AddSeconds(seconds));
    }

    [Fact]
    public void PauseStartsOneSessionAndResumeKeepsIt()
    {
        var analyzer = new RealtimePlayAnalyzer();

        RealtimeAnalysisSnapshot first = analyzer.Process(Sample(0, RealtimePlayState.Playing, [10], accuracy: .98, hits: 1));
        RealtimeAnalysisSnapshot paused = analyzer.Process(Sample(20, RealtimePlayState.Paused, [10, 12], accuracy: .97, hits: 2));
        RealtimeAnalysisSnapshot resumed = analyzer.Process(Sample(21, RealtimePlayState.Playing, [10, 12, 14], accuracy: .97, hits: 3));

        Assert.Equal(PauseCoachWidgetState.Playing, first.WidgetState);
        Assert.Equal(PauseCoachWidgetState.Paused, paused.WidgetState);
        Assert.Equal(first.SessionId, paused.SessionId);
        Assert.Equal(paused.SessionId, resumed.SessionId);
        Assert.Equal(RealtimePlayState.Playing, resumed.State);
    }

    [Fact]
    public void MapTimeAndCountersResetStartsANewAttempt()
    {
        var analyzer = new RealtimePlayAnalyzer();
        RealtimeAnalysisSnapshot first = analyzer.Process(Sample(10, RealtimePlayState.Playing, [5], hits: 20, score: 2000));
        analyzer.Process(Sample(11, RealtimePlayState.Paused, [5, 6], hits: 21, score: 2100));
        RealtimeAnalysisSnapshot retry = analyzer.Process(Sample(1, RealtimePlayState.Playing, [1], hits: 1, score: 100));

        Assert.NotEqual(first.SessionId, retry.SessionId);
        Assert.Equal(1, retry.Timing.SampleCount);
        Assert.Equal(1, retry.Performance.Hits);
    }

    [Fact]
    public void LateTimingProducesEvidenceBackedInsight()
    {
        var analyzer = new RealtimePlayAnalyzer(new PauseCoachOptions { MinimumTimingSamples = 4 });
        analyzer.Process(Sample(0, RealtimePlayState.Playing, [3, 4, 5, 6], accuracy: .99, hits: 4));
        RealtimeAnalysisSnapshot paused = analyzer.Process(Sample(20, RealtimePlayState.Paused, [20, 21, 22, 23], accuracy: .98, hits: 8));

        PauseCoachInsightSnapshot insight = Assert.Single(paused.Insights, item => item.Type == nameof(PauseCoachInsightType.TimingLate));
        Assert.Contains(insight.ConfidenceLabel, new[] { "high", "medium" });
        Assert.Contains("mean", insight.Evidence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ms", insight.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccuracyDropAndMissSpikeAreRankedAboveInformationalData()
    {
        var analyzer = new RealtimePlayAnalyzer(new PauseCoachOptions { MinimumTimingSamples = 2 });
        analyzer.Process(Sample(0, RealtimePlayState.Playing, [0, 1], accuracy: .99, hits: 10));
        analyzer.Process(Sample(20, RealtimePlayState.Playing, [0, 1, 2], accuracy: .99, hits: 20));
        RealtimeAnalysisSnapshot paused = analyzer.Process(Sample(40, RealtimePlayState.Paused, [0, 1, 2, 3], accuracy: .90, hits: 21, misses: 4));

        Assert.NotEmpty(paused.Insights);
        Assert.Equal(nameof(PauseCoachInsightType.AccuracyDrop), paused.Insights[0].Type);
        Assert.Contains(paused.Insights, item => item.Type == nameof(PauseCoachInsightType.MissSpike));
    }

    [Fact]
    public void TooLittleTelemetryIsExplicitlyInsufficient()
    {
        var analyzer = new RealtimePlayAnalyzer(new PauseCoachOptions { MinimumTimingSamples = 12 });
        analyzer.Process(Sample(0, RealtimePlayState.Playing, [4], hits: 1));
        RealtimeAnalysisSnapshot paused = analyzer.Process(Sample(2, RealtimePlayState.Paused, [4], hits: 1));

        Assert.Equal(PauseCoachWidgetState.InsufficientData, paused.WidgetState);
        Assert.Contains(paused.Diagnostics, diagnostic => diagnostic.Contains("insufficient_timing", StringComparison.Ordinal));
        Assert.Contains(paused.Insights, insight => insight.Type == nameof(PauseCoachInsightType.InsufficientData));
    }

    [Fact]
    public void MissingColumnAndPatternInputIsNeverPresentedAsExact()
    {
        var analyzer = new RealtimePlayAnalyzer(new PauseCoachOptions { MinimumTimingSamples = 1 });
        analyzer.Process(Sample(0, RealtimePlayState.Playing, [15], hits: 1));
        RealtimeAnalysisSnapshot paused = analyzer.Process(Sample(2, RealtimePlayState.Paused, [15], hits: 1));

        Assert.Empty(paused.Columns);
        Assert.Empty(paused.Sections);
        Assert.Contains(paused.Diagnostics, diagnostic => diagnostic.Contains("columns.unavailable", StringComparison.Ordinal));
        Assert.Equal(AnalysisDataQuality.Reconstructed, paused.DataQuality);
    }

    [Fact]
    public void TimelineAndTimingBuffersAreBounded()
    {
        var analyzer = new RealtimePlayAnalyzer(new PauseCoachOptions { MaxTimelineEvents = 8, MaxTimingSamples = 5, MinimumTimingSamples = 1 });
        for (int index = 0; index < 20; index++)
        {
            analyzer.Process(Sample(index, RealtimePlayState.Playing, [index], hits: index + 1));
        }

        Assert.NotNull(analyzer.CurrentSession);
        Assert.True(analyzer.CurrentSession!.Timeline.Count <= 8);
        RealtimeAnalysisSnapshot paused = analyzer.Process(Sample(21, RealtimePlayState.Paused, [21], hits: 21));
        Assert.True(paused.Timing.SampleCount <= 5);
    }

    [Fact]
    public void ReplayAndSpectatorModesAreUnavailable()
    {
        var analyzer = new RealtimePlayAnalyzer();

        RealtimeAnalysisSnapshot replay = analyzer.Process(new RealtimeTelemetrySample("map-1", RealtimePlayState.Replay, 1000, isReplay: true));
        RealtimeAnalysisSnapshot spectator = analyzer.Process(new RealtimeTelemetrySample("map-1", RealtimePlayState.Spectating, 1000, isSpectating: true));

        Assert.Equal(PauseCoachWidgetState.Unavailable, replay.WidgetState);
        Assert.Equal(PauseCoachWidgetState.Unavailable, spectator.WidgetState);
        Assert.Contains("disabled", replay.Diagnostics[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecentCountsUseGameplayWindowAndRemainStableWhilePaused()
    {
        var analyzer = new RealtimePlayAnalyzer(new PauseCoachOptions { MinimumTimingSamples = 1 });
        analyzer.Process(Sample(0, RealtimePlayState.Playing, [0], hits: 1));
        analyzer.Process(Sample(10, RealtimePlayState.Playing, [0, 1], hits: 2, misses: 1));
        RealtimeAnalysisSnapshot paused = analyzer.Process(Sample(40, RealtimePlayState.Paused, [0, 1, 2], hits: 4, misses: 4));

        // The recent window starts at map time 20s, so all three misses after
        // the 10s sample are included; this is not a previous-packet delta.
        Assert.Equal(3, paused.Performance.RecentMisses);

        RealtimeAnalysisSnapshot laterPaused = analyzer.Process(
            Sample(40, RealtimePlayState.Paused, [0, 1, 2], hits: 4, misses: 4, receivedAt: _start.AddSeconds(70)));
        Assert.Equal(paused.Performance.RecentMisses, laterPaused.Performance.RecentMisses);
        Assert.Equal(paused.Timing.SampleCount, laterPaused.Timing.SampleCount);
    }

    [Fact]
    public void InsightsUpdateDuringGameplayBeforePause()
    {
        var analyzer = new RealtimePlayAnalyzer(new PauseCoachOptions { MinimumTimingSamples = 1 });
        RealtimeAnalysisSnapshot first = analyzer.Process(Sample(0, RealtimePlayState.Playing, [1], hits: 1));
        RealtimeAnalysisSnapshot live = analyzer.Process(Sample(20, RealtimePlayState.Playing, [1, 2], hits: 2, misses: 3));

        Assert.Equal(PauseCoachWidgetState.Playing, first.WidgetState);
        Assert.Equal(PauseCoachWidgetState.Playing, live.WidgetState);
        Assert.NotEmpty(live.Insights);
        Assert.Equal(3, live.Performance.RecentMisses);
    }
}
