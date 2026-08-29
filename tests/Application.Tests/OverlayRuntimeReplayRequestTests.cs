using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.RealtimeAnalysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Application.Tests;

public sealed class OverlayRuntimeReplayRequestTests
{
    [Fact]
    public void ReplayAndHeadlessRequestsUseIndependentSlots()
    {
        var headlessRequest = new AnalysisRequestId(11);
        var replayRequest = new ReplayRequestId(21);
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            new OverlayRuntimeState
            {
                BeatmapId = "674175",
                BeatmapGeneration = 1
            },
            new AnalysisRequestStarted(1, headlessRequest, 1, "674175", "config-A"));

        state = OverlayRuntimeReducer.Apply(
            state,
            new ReplayAnalysisRequestStarted(2, replayRequest, 1, "674175", "hash-A"));
        state = OverlayRuntimeReducer.Apply(
            state,
            new ReplayAnalysisCompleted(
                3,
                replayRequest,
                1,
                "674175",
                "hash-A",
                Replay()));

        Assert.Equal(AnalysisRequestStatus.Running, state.AnalysisRequest?.Status);
        Assert.Equal(replayRequest, state.ReplayRequest?.RequestId);
        Assert.Equal(ReplayRequestStatus.Completed, state.ReplayRequest?.Status);
        Assert.Same(state.ReplayRequest?.Snapshot, OverlayViewStateComposer.Compose(state).Replay);
    }

    [Fact]
    public void OlderReplayCompletionCannotReplaceTheCurrentRequest()
    {
        var first = new ReplayRequestId(31);
        var second = new ReplayRequestId(32);
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new ReplayAnalysisRequestStarted(1, first, 1, "674175", "hash-A"));
        state = OverlayRuntimeReducer.Apply(
            state,
            new ReplayAnalysisRequestStarted(2, second, 1, "674175", "hash-A"));

        OverlayRuntimeState afterStale = OverlayRuntimeReducer.Apply(
            state,
            new ReplayAnalysisCompleted(3, first, 1, "674175", "hash-A", Replay(1)));

        Assert.Same(state, afterStale);
        Assert.Equal(ReplayRequestStatus.Running, afterStale.ReplayRequest?.Status);
        Assert.Equal(
            OverlayRuntimeRejectionKind.StaleReplayRequest,
            OverlayRuntimeReducer.DescribeRejection(
                state,
                new ReplayAnalysisCompleted(3, first, 1, "674175", "hash-A", Replay(1))).Kind);
    }

    [Fact]
    public void ReplayCompletionForAnOldBeatmapGenerationIsRejected()
    {
        var request = new ReplayRequestId(41);
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new ReplayAnalysisRequestStarted(1, request, 1, "674175", "hash-A"));
        state = state with
        {
            BeatmapId = "776655",
            BeatmapGeneration = 2
        };

        OverlayRuntimeState afterStale = OverlayRuntimeReducer.Apply(
            state,
            new ReplayAnalysisCompleted(2, request, 1, "674175", "hash-A", Replay()));

        Assert.Same(state, afterStale);
        Assert.Equal(
            OverlayRuntimeRejectionKind.StaleReplayBeatmapGeneration,
            OverlayRuntimeReducer.DescribeRejection(
                state,
                new ReplayAnalysisCompleted(2, request, 1, "674175", "hash-A", Replay())).Kind);
    }

    [Fact]
    public void ReplayFailureDoesNotAlterRealtimeState()
    {
        var request = new ReplayRequestId(51);
        var realtime = new RealtimeAnalysisSnapshot(
            "session-A",
            "674175",
            RealtimePlayState.Paused,
            30_000,
            DateTimeOffset.UnixEpoch.AddSeconds(30),
            new RealtimeTimingStats(1, 1, 1, 1, 1, 1, 1, 1, AnalysisDataQuality.Reconstructed),
            new RealtimePerformanceStats(.98, 123, 1, 0, 1, 30, AnalysisDataQuality.Reconstructed),
            null,
            [],
            [],
            [],
            AnalysisDataQuality.Reconstructed,
            PauseCoachWidgetState.Paused,
            true,
            []);
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            new OverlayRuntimeState
            {
                BeatmapId = "674175",
                BeatmapGeneration = 1,
                LatestRealtime = realtime,
                GameplayStateKnown = true,
                GameplayState = RealtimePlayState.Paused,
                IsPlaying = true,
                IsPaused = true
            },
            new ReplayAnalysisRequestStarted(1, request, 1, "674175", "hash-A"));

        state = OverlayRuntimeReducer.Apply(
            state,
            new ReplayAnalysisRequestFailed(
                2,
                request,
                "replay.corrupt",
                "bad replay",
                1,
                "674175",
                "hash-A"));

        Assert.Same(realtime, state.LatestRealtime);
        Assert.Equal(RealtimePlayState.Paused, state.GameplayState);
        Assert.Equal(ReplayRequestStatus.Failed, state.ReplayRequest?.Status);
        Assert.Equal("replay.corrupt", state.ReplayRequest?.FailureCode);
        Assert.Null(OverlayViewStateComposer.Compose(state).Replay);
    }

    [Fact]
    public void CancelledReplayRequestIsTerminalAndCannotAcceptCompletion()
    {
        var request = new ReplayRequestId(61);
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new ReplayAnalysisRequestStarted(1, request, 0, "674175", "hash-A"));
        state = OverlayRuntimeReducer.Apply(
            state,
            new ReplayAnalysisRequestCancelled(2, request, "cancelled", 0, "674175", "hash-A"));

        OverlayRuntimeState afterLateCompletion = OverlayRuntimeReducer.Apply(
            state,
            new ReplayAnalysisCompleted(3, request, 0, "674175", "hash-A", Replay()));

        Assert.Equal(ReplayRequestStatus.Cancelled, state.ReplayRequest?.Status);
        Assert.Same(state, afterLateCompletion);
        Assert.Equal(
            OverlayRuntimeRejectionKind.StaleReplayRequest,
            OverlayRuntimeReducer.DescribeRejection(
                state,
                new ReplayAnalysisCompleted(3, request, 0, "674175", "hash-A", Replay())).Kind);
    }

    private static ReplayOverlaySnapshot Replay(int score = 100) => new()
    {
        MapProgressMs = 30_000,
        Score = score,
        Accuracy = .98,
        Fidelity = "exact",
        SampleCount = 32,
        Columns = [new ReplayColumnSnapshot { Column = 1, BiasMs = 2.5 }]
    };
}
