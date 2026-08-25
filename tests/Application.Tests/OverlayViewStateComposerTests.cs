using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Application.Tests;

public sealed class OverlayViewStateComposerTests
{
    [Fact]
    public void UsesRuntimeIdentityWhenHeadlessAnalysisIsNotAvailable()
    {
        var runtime = new OverlayRuntimeState
        {
            Version = 7,
            BeatmapGeneration = 3,
            BeatmapId = "674175",
            GameplayStateKnown = true,
            GameplayState = RealtimePlayState.Playing,
            IsPlaying = true,
            IsPaused = false,
            VisibilityPolicy = "during-play"
        };

        OverlayViewState view = OverlayViewStateComposer.Compose(runtime);

        Assert.Equal(OverlayViewState.CurrentSchemaVersion, view.SchemaVersion);
        Assert.Equal(7, view.Version);
        Assert.Equal(3, view.BeatmapGeneration);
        Assert.Equal("674175", view.BeatmapId);
        Assert.Equal("674175", view.Beatmap.Id);
        Assert.Equal(nameof(RealtimePlayState.Playing), view.Gameplay.State);
        Assert.True(view.Gameplay.IsPlaying);
        Assert.False(view.Gameplay.IsPaused);
        Assert.Equal("during-play", view.Presentation.VisibilityPolicy);
    }

    [Fact]
    public void NativeCompatibilitySnapshotContainsComposedRealtimeAndAnalysisBlocks()
    {
        var realtime = new RealtimeAnalysisSnapshot(
            "session-A",
            "674175",
            RealtimePlayState.Paused,
            30_000,
            DateTimeOffset.UnixEpoch.AddSeconds(30),
            new RealtimeTimingStats((int)12.5, 11, 14, 3, 2, 1, 4, 512, AnalysisDataQuality.Reconstructed),
            new RealtimePerformanceStats(91.5, 123_456, 3, 0, 2, 120, AnalysisDataQuality.Reconstructed),
            null,
            [],
            [],
            [],
            AnalysisDataQuality.Reconstructed,
            PauseCoachWidgetState.Paused,
            false,
            [])
        {
            Score = 123_456,
            Accuracy = .915
        };
        var runtime = new OverlayRuntimeState
        {
            Version = 9,
            BeatmapGeneration = 2,
            BeatmapId = "674175",
            GameplayStateKnown = true,
            GameplayState = RealtimePlayState.Paused,
            IsPlaying = true,
            IsPaused = true,
            LatestRealtime = realtime,
            LatestAnalysis = new AnalysisSnapshot
            {
                Beatmap = new BeatmapSnapshot { Id = "674175", Title = "Map" },
                Difficulty = new DifficultySnapshot { StarRating = 4.5 }
            }
        };

        AnalysisSnapshot native = OverlayViewStateComposer.ToNativeAnalysisSnapshot(
            OverlayViewStateComposer.Compose(runtime));

        Assert.Equal("674175", native.Beatmap.Id);
        Assert.Equal("Map", native.Beatmap.Title);
        Assert.Equal(4.5, native.Difficulty.StarRating);
        Assert.Equal(30_000, native.Replay!.MapProgressMs);
        Assert.Equal(nameof(PauseCoachWidgetState.Paused), native.PauseCoach!.State);
        Assert.Equal(9L, native.Extensions["runtimeVersion"]);
        Assert.Equal(true, native.Extensions["nativePauseCoach"]);
    }

    [Fact]
    public void ExactReplayColumnsRemainAuthoritativeWhenRealtimeIsPresent()
    {
        var realtime = new RealtimeAnalysisSnapshot(
            "session-A",
            "674175",
            RealtimePlayState.Paused,
            30_000,
            DateTimeOffset.UnixEpoch.AddSeconds(30),
            new RealtimeTimingStats(4, 1, 2, 3, 4, 5, 6, 7, AnalysisDataQuality.Reconstructed),
            new RealtimePerformanceStats(.95, null, 7, 0, 7, 0, AnalysisDataQuality.Reconstructed),
            null,
            [],
            [],
            [],
            AnalysisDataQuality.Reconstructed,
            PauseCoachWidgetState.Paused,
            true,
            [])
        {
            Score = 123_456,
            Accuracy = .95
        };
        var exactReplay = new ReplayOverlaySnapshot
        {
            MapProgressMs = 29_000,
            Score = 120_000,
            Fidelity = "exact",
            Columns = [new ReplayColumnSnapshot { Column = 1, BiasMs = 8.5 }]
        };
        var runtime = new OverlayRuntimeState
        {
            BeatmapId = "674175",
            GameplayStateKnown = true,
            GameplayState = RealtimePlayState.Paused,
            IsPlaying = true,
            IsPaused = true,
            LatestRealtime = realtime,
            LatestAnalysis = new AnalysisSnapshot
            {
                Beatmap = new BeatmapSnapshot { Id = "674175" },
                Replay = exactReplay
            }
        };

        AnalysisSnapshot native = OverlayViewStateComposer.ToNativeAnalysisSnapshot(
            OverlayViewStateComposer.Compose(runtime));

        Assert.Same(exactReplay, native.Replay);
        Assert.Single(native.Replay!.Columns);
        Assert.Equal(8.5, native.Replay.Columns[0].BiasMs);
    }
}
