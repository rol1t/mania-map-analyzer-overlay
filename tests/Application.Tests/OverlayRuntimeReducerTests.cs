using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Application.Tests;

public sealed class OverlayRuntimeReducerTests
{
    [Fact]
    public void AppliesMonotonicEventsAndIgnoresStaleCallbacks()
    {
        OverlayRuntimeState initial = OverlayRuntimeState.Empty;
        OverlayRuntimeState current = OverlayRuntimeReducer.Apply(
            initial,
            new OverlayModeChanged(10, true));
        current = OverlayRuntimeReducer.Apply(
            current,
            new OverlayModeChanged(9, false));

        Assert.True(current.OverlayMode);
        Assert.Equal(1, current.Version);
        Assert.Equal(10, current.LastEventSequence);
    }

    [Fact]
    public void RealtimePauseKeepsSessionAndPublishesNormalizedState()
    {
        RealtimeAnalysisSnapshot playing = Snapshot(
            "session-A",
            RealtimePlayState.Playing,
            29_000);
        RealtimeAnalysisSnapshot paused = Snapshot(
            "session-A",
            RealtimePlayState.Paused,
            30_000);
        var telemetry = new TosuRealtimeTelemetry(
            "native-http",
            "Play",
            2,
            true,
            new RealtimeTelemetrySample("674175", RealtimePlayState.Paused, 30_000),
            paused);

        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeReducer.Apply(
                OverlayRuntimeState.Empty,
                new RealtimeTelemetryReceived(1, telemetry with
                {
                    Snapshot = playing
                })),
            new RealtimeTelemetryReceived(2, telemetry));

        Assert.Equal(RealtimePlayState.Paused, state.GameplayState);
        Assert.True(state.IsPlaying);
        Assert.True(state.IsPaused);
        Assert.Equal("674175", state.BeatmapId);
        Assert.Equal("session-A", state.SessionId);
        Assert.Equal(30_000, state.LatestRealtime!.MapTimeMs);
    }

    [Theory]
    [InlineData("always", false, false, true)]
    [InlineData("during-play", true, false, true)]
    [InlineData("during-play", true, true, false)]
    [InlineData("paused-only", true, true, true)]
    [InlineData("paused-only", true, false, false)]
    [InlineData("never", true, true, false)]
    public void DerivesVisibilityFromState(
        string policy,
        bool isPlaying,
        bool isPaused,
        bool expected)
    {
        OverlayRuntimeState state = new()
        {
            OverlayMode = true,
            VisibilityPolicy = policy,
            GameplayStateKnown = true,
            IsPlaying = isPlaying,
            IsPaused = isPaused
        };

        Assert.Equal(expected, OverlayVisibilityDerivation.ShouldShowNativeOverlay(state));
    }

    [Fact]
    public void MinimizedOsuKeepsOverlayVisibleAndUnknownStateUsesPolicyFallback()
    {
        OverlayRuntimeState minimized = new()
        {
            OverlayMode = true,
            VisibilityPolicy = "never",
            OsuWindowMinimized = true
        };
        OverlayRuntimeState unknownNever = minimized with
        {
            OsuWindowMinimized = false
        };
        OverlayRuntimeState unknownDuringPlay = unknownNever with
        {
            VisibilityPolicy = "during-play"
        };

        Assert.True(OverlayVisibilityDerivation.ShouldShowNativeOverlay(minimized));
        Assert.False(OverlayVisibilityDerivation.ShouldShowNativeOverlay(unknownNever));
        Assert.True(OverlayVisibilityDerivation.ShouldShowNativeOverlay(unknownDuringPlay));
    }

    [Fact]
    public void ParityComparerAcceptsEquivalentLegacyProjection()
    {
        OverlayRuntimeState shadow = new()
        {
            OverlayMode = true,
            GameplayStateKnown = true,
            IsPlaying = true,
            IsPaused = true,
            VisibilityPolicy = "paused-only",
            OsuWindowMinimized = false,
            PresentationReady = true,
            PresentationVisible = true
        };
        var legacy = new OverlayRuntimeLegacyProjection(
            true,
            true,
            true,
            true,
            "paused-only",
            false,
            true,
            true,
            true);

        OverlayRuntimeParityResult result = OverlayRuntimeParityComparer.Compare(shadow, legacy);

        Assert.True(result.IsMatch);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void ParityComparerReportsVisibilityAndGameplayDivergence()
    {
        OverlayRuntimeState shadow = new()
        {
            OverlayMode = true,
            GameplayStateKnown = true,
            IsPlaying = true,
            IsPaused = true,
            VisibilityPolicy = "paused-only",
            PresentationReady = true,
            PresentationVisible = true
        };
        var legacy = new OverlayRuntimeLegacyProjection(
            true,
            true,
            true,
            false,
            "paused-only",
            false,
            true,
            false,
            false);

        OverlayRuntimeParityResult result = OverlayRuntimeParityComparer.Compare(shadow, legacy);

        Assert.False(result.IsMatch);
        Assert.Contains("isPaused", result.Differences[0] + string.Join('|', result.Differences));
        Assert.Contains(result.Differences, difference => difference.StartsWith("presentationVisible", StringComparison.Ordinal));
        Assert.Contains(result.Differences, difference => difference.StartsWith("nativeWindowVisible", StringComparison.Ordinal));
    }

    [Fact]
    public void PresentationRecreationDoesNotDiscardLatestRealtimeState()
    {
        var telemetry = new TosuRealtimeTelemetry(
            "native-http",
            "Play",
            2,
            true,
            new RealtimeTelemetrySample("674175", RealtimePlayState.Paused, 30_000),
            Snapshot("session-A", RealtimePlayState.Paused, 30_000));
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new RealtimeTelemetryReceived(1, telemetry));
        state = OverlayRuntimeReducer.Apply(
            state,
            new PresentationAvailabilityChanged(2, false, false));
        state = OverlayRuntimeReducer.Apply(
            state,
            new PresentationAvailabilityChanged(3, true, true));

        Assert.Equal(RealtimePlayState.Paused, state.LatestRealtime!.State);
        Assert.Equal(30_000, state.LatestRealtime.MapTimeMs);
        Assert.True(state.PresentationReady);
        Assert.True(state.PresentationVisible);
    }

    [Fact]
    public void MapTransitionRetainsOldAnalysisWithoutProjectingItOntoTheNewMap()
    {
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new RealtimeTelemetryReceived(1, Telemetry("674175", RealtimePlayState.Playing, 1_000, "session-A")));
        state = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisSnapshotReceived(2, Analysis("674175")));
        Assert.Equal("674175", state.LatestAnalysis!.Beatmap.Id);
        Assert.Equal(1, state.BeatmapGeneration);

        state = OverlayRuntimeReducer.Apply(
            state,
            new RealtimeTelemetryReceived(3, Telemetry("776655", RealtimePlayState.Playing, 500, "session-B")));
        Assert.Equal(2, state.BeatmapGeneration);
        Assert.Equal("674175", state.LatestAnalysis!.Beatmap.Id);
        Assert.Equal("776655", OverlayViewStateComposer.Compose(state).BeatmapId);
        Assert.Empty(OverlayViewStateComposer.Compose(state).Skills);

        state = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisSnapshotReceived(4, Analysis("674175")));
        Assert.Equal("674175", state.LatestAnalysis!.Beatmap.Id);

        state = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisSnapshotReceived(5, Analysis("776655")));
        Assert.Equal("776655", state.LatestAnalysis!.Beatmap.Id);
        Assert.Null(state.PendingAnalysis);
    }

    [Fact]
    public void FirstRealtimeFrameForCurrentAnalyzedMapDoesNotDiscardAnalysis()
    {
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new AnalysisSnapshotReceived(1, Analysis("674175")));

        state = OverlayRuntimeReducer.Apply(
            state,
            new RealtimeTelemetryReceived(2, Telemetry("674175", RealtimePlayState.Playing, 1_000, "session-A")));

        Assert.Equal("674175", state.LatestAnalysis!.Beatmap.Id);
        Assert.Equal("674175", state.BeatmapId);
    }

    [Fact]
    public void AnalysisThatFinishesAheadOfRealtimeIsPromotedOnMatchingMapTransition()
    {
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new RealtimeTelemetryReceived(1, Telemetry("674175", RealtimePlayState.Menu, 1_000, "session-A")));
        state = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisSnapshotReceived(2, Analysis("674175")));

        AnalysisSnapshot nextMapAnalysis = Analysis("1540669") with
        {
            Difficulty = new DifficultySnapshot { StarRating = 7.25 }
        };
        state = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisSnapshotReceived(3, nextMapAnalysis));

        Assert.Equal("674175", state.LatestAnalysis!.Beatmap.Id);
        Assert.Equal("1540669", state.PendingAnalysis!.Beatmap.Id);
        Assert.Equal("674175", OverlayViewStateComposer.Compose(state).BeatmapId);

        state = OverlayRuntimeReducer.Apply(
            state,
            new RealtimeTelemetryReceived(4, Telemetry("1540669", RealtimePlayState.Menu, 500, "session-B")));

        Assert.Equal("1540669", state.BeatmapId);
        Assert.Equal("1540669", state.LatestAnalysis!.Beatmap.Id);
        Assert.Equal(7.25, state.LatestAnalysis.Difficulty.StarRating);
        Assert.Null(state.PendingAnalysis);
        OverlayViewState view = OverlayViewStateComposer.Compose(state);
        Assert.Equal("1540669", view.BeatmapId);
        Assert.Equal(7.25, view.Difficulty.StarRating);
    }

    [Fact]
    public void PendingAnalysisSurvivesIntermediateCarouselBeatmap()
    {
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new RealtimeTelemetryReceived(1, Telemetry("674175", RealtimePlayState.Menu, 1_000, "session-A")));
        state = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisSnapshotReceived(2, Analysis("1540669") with
            {
                Difficulty = new DifficultySnapshot { StarRating = 7.25 }
            }));

        state = OverlayRuntimeReducer.Apply(
            state,
            new RealtimeTelemetryReceived(3, Telemetry("998877", RealtimePlayState.Menu, 500, "session-B")));

        Assert.Equal("1540669", state.PendingAnalysis!.Beatmap.Id);
        Assert.Null(state.LatestAnalysis);

        state = OverlayRuntimeReducer.Apply(
            state,
            new RealtimeTelemetryReceived(4, Telemetry("1540669", RealtimePlayState.Menu, 600, "session-C")));

        Assert.Equal("1540669", state.LatestAnalysis!.Beatmap.Id);
        Assert.Equal(7.25, state.LatestAnalysis.Difficulty.StarRating);
        Assert.Null(state.PendingAnalysis);
    }

    [Fact]
    public void ComposerKeepsIndependentAnalysisAndRealtimeSlots()
    {
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new RealtimeTelemetryReceived(1, Telemetry("674175", RealtimePlayState.Paused, 30_000, "session-A")));
        state = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisSnapshotReceived(2, Analysis("674175")));
        state = OverlayRuntimeReducer.Apply(
            state,
            new OverlayModeChanged(3, true));

        OverlayViewState view = OverlayViewStateComposer.Compose(state);

        Assert.Equal(state.Version, view.Version);
        Assert.Equal(state.BeatmapGeneration, view.BeatmapGeneration);
        Assert.Equal("674175", view.BeatmapId);
        Assert.Equal("674175", view.Beatmap.Id);
        Assert.Equal("session-A", view.PauseCoach!.SessionId);
        Assert.Equal(nameof(PauseCoachWidgetState.Paused), view.PauseCoach.State);
        Assert.Equal(30_000, view.Realtime!.MapTimeMs);
        Assert.True(view.Presentation.OverlayMode);
    }

    private static RealtimeAnalysisSnapshot Snapshot(
        string sessionId,
        RealtimePlayState state,
        int mapTimeMs)
    {
        return new RealtimeAnalysisSnapshot(
            sessionId,
            "674175",
            state,
            mapTimeMs,
            DateTimeOffset.UnixEpoch.AddMilliseconds(mapTimeMs),
            new RealtimeTimingStats(0, null, null, null, null, null, null, null, AnalysisDataQuality.Reconstructed),
            new RealtimePerformanceStats(null, null, 0, 0, 0, 0, AnalysisDataQuality.Reconstructed),
            null,
            [],
            [],
            [],
            AnalysisDataQuality.Reconstructed,
            state == RealtimePlayState.Paused
                ? PauseCoachWidgetState.Paused
                : PauseCoachWidgetState.Playing,
            false,
            [])
        {
            Score = mapTimeMs,
            Accuracy = .98
        };
    }

    private static TosuRealtimeTelemetry Telemetry(
        string beatmapId,
        RealtimePlayState state,
        int mapTimeMs,
        string sessionId)
    {
        RealtimeAnalysisSnapshot snapshot = Snapshot(sessionId, state, mapTimeMs) with
        {
            BeatmapId = beatmapId
        };
        return new TosuRealtimeTelemetry(
            "native-http",
            "Play",
            2,
            state == RealtimePlayState.Paused,
            new RealtimeTelemetrySample(beatmapId, state, mapTimeMs),
            snapshot);
    }

    private static AnalysisSnapshot Analysis(string beatmapId) => new()
    {
        SourceId = "headless",
        Beatmap = new BeatmapSnapshot { Id = beatmapId },
        Difficulty = new DifficultySnapshot { StarRating = 4.2 }
    };
}
