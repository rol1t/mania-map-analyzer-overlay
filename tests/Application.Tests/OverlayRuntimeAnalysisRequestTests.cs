using System.Collections.Generic;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.RealtimeAnalysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Application.Tests;

public sealed class OverlayRuntimeAnalysisRequestTests
{
    [Fact]
    public async Task AcceptedCompletionPublishesOneComposedViewState()
    {
        await using var coordinator = new OverlayRuntimeCoordinator();
        var viewStates = new List<OverlayViewState>();
        coordinator.ViewStateChanged += (_, args) => viewStates.Add(args.ViewState);
        var requestId = new AnalysisRequestId(21);
        var snapshot = Analysis("674175", 4.2);

        await coordinator.DispatchAsync(new AnalysisRequestStarted(
            1,
            requestId,
            0,
            "674175",
            "config-A"));
        await coordinator.DispatchAsync(new AnalysisSnapshotReceived(
            2,
            snapshot,
            0,
            requestId,
            "config-A",
            AnalysisSnapshotProducer.Headless));

        var viewState = Assert.Single(viewStates);
        Assert.Equal(4.2, viewState.Difficulty.StarRating);
        Assert.Equal(AnalysisRequestStatus.Completed, coordinator.Current.AnalysisRequest?.Status);
    }

    [Fact]
    public void OlderCompletionForTheSameTargetCannotReplaceTheCurrentRequest()
    {
        var first = new AnalysisRequestId(1);
        var second = new AnalysisRequestId(2);
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new AnalysisRequestStarted(1, first, 1, "674175", "config-A"));
        state = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisRequestStarted(2, second, 1, "674175", "config-A"));

        AnalysisSnapshot staleSnapshot = Analysis("674175", 3.1);
        OverlayRuntimeState afterStale = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisSnapshotReceived(
                3,
                staleSnapshot,
                1,
                first,
                "config-A",
                AnalysisSnapshotProducer.Headless));

        Assert.Same(state, afterStale);
        Assert.Equal(OverlayRuntimeRejectionKind.StaleAnalysisRequest,
            OverlayRuntimeReducer.DescribeRejection(state,
                new AnalysisSnapshotReceived(
                    3,
                    staleSnapshot,
                    1,
                    first,
                    "config-A",
                    AnalysisSnapshotProducer.Headless)).Kind);

        AnalysisSnapshot currentSnapshot = Analysis("674175", 4.2);
        OverlayRuntimeState completed = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisSnapshotReceived(
                4,
                currentSnapshot,
                1,
                second,
                "config-A",
                AnalysisSnapshotProducer.Headless));

        Assert.Same(currentSnapshot, completed.LatestAnalysis);
        Assert.Equal(second, completed.AnalysisRequest?.RequestId);
        Assert.Equal(AnalysisRequestStatus.Completed, completed.AnalysisRequest?.Status);

        var staleStart = new AnalysisRequestStarted(5, first, 1, "674175", "config-A");
        Assert.Same(completed, OverlayRuntimeReducer.Apply(completed, staleStart));
        Assert.Equal(OverlayRuntimeRejectionKind.StaleAnalysisRequest,
            OverlayRuntimeReducer.DescribeRejection(completed, staleStart).Kind);
    }

    [Fact]
    public void UnknownCompletionProducesTypedDiagnosticAndLeavesStateUntouched()
    {
        var requestId = new AnalysisRequestId(9);
        var snapshot = Analysis("674175", 4.2);
        OverlayRuntimeState state = OverlayRuntimeState.Empty;
        var completion = new AnalysisSnapshotReceived(1, snapshot, 1, requestId);

        Assert.Same(state, OverlayRuntimeReducer.Apply(state, completion));
        OverlayRuntimeRejection rejection = OverlayRuntimeReducer.DescribeRejection(state, completion);
        Assert.Equal(OverlayRuntimeRejectionKind.UnknownAnalysisRequest, rejection.Kind);
        Assert.Equal(requestId.Value, completion.RequestId?.Value);
    }

    [Fact]
    public void RequestFailureIsAcceptedOnlyForTheCurrentRunningRequest()
    {
        var requestId = new AnalysisRequestId(7);
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new AnalysisRequestStarted(1, requestId, 0, "674175", "config-A"));

        state = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisRequestFailed(2, requestId, "worker_crashed", "worker stopped"));

        Assert.Equal(AnalysisRequestStatus.Failed, state.AnalysisRequest?.Status);
        Assert.Equal("worker_crashed", state.AnalysisRequest?.FailureCode);
        Assert.Equal("worker stopped", state.AnalysisRequest?.FailureMessage);

        var duplicate = new AnalysisRequestFailed(3, requestId, "worker_crashed");
        Assert.Same(state, OverlayRuntimeReducer.Apply(state, duplicate));
        Assert.Equal(OverlayRuntimeRejectionKind.StaleAnalysisRequest,
            OverlayRuntimeReducer.DescribeRejection(state, duplicate).Kind);
    }

    [Fact]
    public void FailureCannotEraseAnAlreadyCompletedRequest()
    {
        var requestId = new AnalysisRequestId(8);
        var snapshot = Analysis("674175", 4.2);
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new AnalysisRequestStarted(1, requestId, 0, "674175", "config-A"));
        state = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisSnapshotReceived(
                2,
                snapshot,
                0,
                requestId,
                "config-A",
                AnalysisSnapshotProducer.Headless));

        var failure = new AnalysisRequestFailed(
            3,
            requestId,
            "worker_crashed",
            "late failure",
            BeatmapId: "674175",
            ConfigurationIdentity: "config-A");

        Assert.Same(state, OverlayRuntimeReducer.Apply(state, failure));
        Assert.Equal(OverlayRuntimeRejectionKind.StaleAnalysisRequest,
            OverlayRuntimeReducer.DescribeRejection(state, failure).Kind);
        Assert.Same(snapshot, state.AnalysisRequest?.Snapshot);
    }

    [Fact]
    public void FailureForAnOlderGenerationIsRejected()
    {
        var requestId = new AnalysisRequestId(10);
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new RealtimeTelemetryReceived(
                1,
                new RealtimeTelemetryUpdate(
                    "native-http",
                    "Play",
                    2,
                    false,
                    new RealtimeTelemetrySample("674175", RealtimePlayState.Playing, 1_000),
                    Realtime("session-A", "674175", RealtimePlayState.Playing, 1_000))));
        state = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisRequestStarted(2, requestId, 1, "674175", "config-A"));
        state = OverlayRuntimeReducer.Apply(
            state,
            new RealtimeTelemetryReceived(
                3,
                new RealtimeTelemetryUpdate(
                    "native-http",
                    "Play",
                    2,
                    false,
                    new RealtimeTelemetrySample("776655", RealtimePlayState.Playing, 500),
                    Realtime("session-B", "776655", RealtimePlayState.Playing, 500))));

        var failure = new AnalysisRequestFailed(
            4,
            requestId,
            "worker_crashed",
            BeatmapGeneration: 1,
            BeatmapId: "674175",
            ConfigurationIdentity: "config-A");

        Assert.Same(state, OverlayRuntimeReducer.Apply(state, failure));
        Assert.Equal(OverlayRuntimeRejectionKind.StaleAnalysisBeatmapGeneration,
            OverlayRuntimeReducer.DescribeRejection(state, failure).Kind);
    }

    [Fact]
    public void ExplicitBrowserFallbackCanReplaceACompletedVersionedRequest()
    {
        var requestId = new AnalysisRequestId(12);
        AnalysisSnapshot headless = Analysis("674175", 4.2);
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new AnalysisRequestStarted(1, requestId, 0, "674175", "config-A"));
        state = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisSnapshotReceived(
                2,
                headless,
                RequestId: requestId,
                ConfigurationIdentity: "config-A",
                Producer: AnalysisSnapshotProducer.Headless));

        AnalysisSnapshot fallback = Analysis("674175", 3.8) with
        {
            SourceId = "browser"
        };
        OverlayRuntimeState rejectedAnonymous = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisSnapshotReceived(3, fallback));
        Assert.Same(state, rejectedAnonymous);

        OverlayRuntimeState acceptedFallback = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisSnapshotReceived(
                4,
                fallback,
                Producer: AnalysisSnapshotProducer.BrowserFallback));

        Assert.Same(fallback, acceptedFallback.LatestAnalysis);
        Assert.False(acceptedFallback.AnalysisRequest?.IsVersioned);
        Assert.Equal(AnalysisRequestStatus.Completed, acceptedFallback.AnalysisRequest?.Status);
    }

    [Fact]
    public void ExplicitBrowserFallbackCanRecoverAFailedVersionedRequest()
    {
        var requestId = new AnalysisRequestId(13);
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new AnalysisRequestStarted(1, requestId, 0, "674175", "config-A"));
        state = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisRequestFailed(
                2,
                requestId,
                "worker_crashed",
                BeatmapId: "674175",
                ConfigurationIdentity: "config-A"));

        AnalysisSnapshot fallback = Analysis("674175", 3.8) with
        {
            SourceId = "browser"
        };
        state = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisSnapshotReceived(
                3,
                fallback,
                Producer: AnalysisSnapshotProducer.BrowserFallback));

        Assert.Same(fallback, state.LatestAnalysis);
        Assert.False(state.AnalysisRequest?.IsVersioned);
        Assert.Equal(AnalysisRequestStatus.Completed, state.AnalysisRequest?.Status);
    }

    [Fact]
    public void CompletionForAnotherEffectiveConfigurationIsRejected()
    {
        var requestId = new AnalysisRequestId(14);
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new AnalysisRequestStarted(1, requestId, 0, "674175", "config-A"));
        var completion = new AnalysisSnapshotReceived(
            2,
            Analysis("674175", 4.2),
            RequestId: requestId,
            ConfigurationIdentity: "config-B",
            Producer: AnalysisSnapshotProducer.Headless);

        Assert.Same(state, OverlayRuntimeReducer.Apply(state, completion));
        Assert.Equal(
            OverlayRuntimeRejectionKind.AnalysisRequestMetadataMismatch,
            OverlayRuntimeReducer.DescribeRejection(state, completion).Kind);
    }

    [Fact]
    public void VersionedCompletionForAnOldMapIsRejectedBySlotGeneration()
    {
        var requestId = new AnalysisRequestId(11);
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new RealtimeTelemetryReceived(
                1,
                new RealtimeTelemetryUpdate(
                    "native-http",
                    "Play",
                    2,
                    false,
                    new RealtimeTelemetrySample("674175", RealtimePlayState.Playing, 1_000),
                    Realtime("session-A", "674175", RealtimePlayState.Playing, 1_000))));
        state = OverlayRuntimeReducer.Apply(
            state,
            new AnalysisRequestStarted(2, requestId, state.BeatmapGeneration, "674175", "config-A"));
        state = OverlayRuntimeReducer.Apply(
            state,
            new RealtimeTelemetryReceived(
                3,
                new RealtimeTelemetryUpdate(
                    "native-http",
                    "Play",
                    2,
                    false,
                    new RealtimeTelemetrySample("776655", RealtimePlayState.Playing, 500),
                    Realtime("session-B", "776655", RealtimePlayState.Playing, 500))));

        var stale = new AnalysisSnapshotReceived(
            4,
            Analysis("674175", 9.9),
            BeatmapGeneration: 1,
            RequestId: requestId,
            ConfigurationIdentity: "config-A",
            Producer: AnalysisSnapshotProducer.Headless);

        Assert.Same(state, OverlayRuntimeReducer.Apply(state, stale));
        Assert.Equal(OverlayRuntimeRejectionKind.StaleAnalysisBeatmapGeneration,
            OverlayRuntimeReducer.DescribeRejection(state, stale).Kind);
    }

    private static AnalysisSnapshot Analysis(string beatmapId, double starRating) => new()
    {
        SourceId = "headless",
        Beatmap = new BeatmapSnapshot { Id = beatmapId },
        Difficulty = new DifficultySnapshot { StarRating = starRating }
    };

    private static RealtimeAnalysisSnapshot Realtime(
        string sessionId,
        string beatmapId,
        RealtimePlayState state,
        int mapTimeMs) => new(
            sessionId,
            beatmapId,
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
            PauseCoachWidgetState.Playing,
            false,
            []);
}
