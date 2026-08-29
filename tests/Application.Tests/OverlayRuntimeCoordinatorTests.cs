using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.RealtimeAnalysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Application.Tests;

public sealed class OverlayRuntimeCoordinatorTests
{
    [Fact]
    public async Task DispatchProcessesEventsInSubmissionOrder()
    {
        await using var coordinator = new OverlayRuntimeCoordinator();

        Task<OverlayRuntimeState> first = coordinator.DispatchAsync(new OverlayModeChanged(1, true));
        Task<OverlayRuntimeState> second = coordinator.DispatchAsync(new VisibilityPolicyChanged(2, "paused-only"));

        OverlayRuntimeState firstState = await first;
        OverlayRuntimeState secondState = await second;

        Assert.Equal(1, firstState.Version);
        Assert.Equal(2, secondState.Version);
        Assert.True(secondState.OverlayMode);
        Assert.Equal("paused-only", secondState.VisibilityPolicy);
        Assert.Equal(2, coordinator.Current.LastEventSequence);
    }

    [Fact]
    public async Task StaleSequenceIsIgnoredWithoutAdvancingStateVersion()
    {
        await using var coordinator = new OverlayRuntimeCoordinator();

        await coordinator.DispatchAsync(new OverlayModeChanged(10, true));
        OverlayRuntimeState stale = await coordinator.DispatchAsync(new OverlayModeChanged(9, false));

        Assert.True(stale.OverlayMode);
        Assert.Equal(1, stale.Version);
        Assert.Equal(10, stale.LastEventSequence);
    }

    [Fact]
    public async Task CancellationPreventsWaitingForDispatchResult()
    {
        await using var coordinator = new OverlayRuntimeCoordinator();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.DispatchAsync(new OverlayModeChanged(1, true), cancellation.Token));
        Assert.Equal(0, coordinator.Current.Version);
    }

    [Fact]
    public async Task StopCompletesLifecycleAndRejectsNewEvents()
    {
        await using var coordinator = new OverlayRuntimeCoordinator();

        await coordinator.DispatchAsync(new OverlayModeChanged(1, true));
        await coordinator.StopAsync();

        Assert.False(coordinator.TryPost(new OverlayModeChanged(2, false)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.DispatchAsync(new OverlayModeChanged(3, false)));
        Assert.True(coordinator.Current.OverlayMode);
    }

    [Fact]
    public async Task ConcurrentPostsRemainSerializableAndLatestSequenceWins()
    {
        await using var coordinator = new OverlayRuntimeCoordinator();
        Task[] posts = Enumerable.Range(1, 32)
            .Select(sequence => coordinator.DispatchAsync(new VisibilityPolicyChanged(sequence, sequence % 2 == 0 ? "always" : "never")))
            .ToArray();

        await Task.WhenAll(posts);

        Assert.Equal(32, coordinator.Current.Version);
        Assert.Equal(32, coordinator.Current.LastEventSequence);
        Assert.Equal("always", coordinator.Current.VisibilityPolicy);
    }

    [Fact]
    public async Task FactoryPostsAssignSequenceAtTheWriteBoundary()
    {
        await using var coordinator = new OverlayRuntimeCoordinator();
        Task<bool>[] posts = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => coordinator.TryPost(sequence =>
                new VisibilityPolicyChanged(sequence, "always"))))
            .ToArray();

        Assert.All(await Task.WhenAll(posts), Assert.True);
        await coordinator.StopAsync();

        Assert.Equal(32, coordinator.Current.Version);
        Assert.Equal(32, coordinator.Current.LastEventSequence);
    }

    [Fact]
    public async Task FactoryDispatchAssignsSequenceAndWaitsForReduction()
    {
        await using var coordinator = new OverlayRuntimeCoordinator();

        OverlayRuntimeState first = await coordinator.DispatchAsync(sequence =>
            new OverlayModeChanged(sequence, true));
        OverlayRuntimeState second = await coordinator.DispatchAsync(sequence =>
            new VisibilityPolicyChanged(sequence, "paused-only"));

        Assert.Equal(1, first.LastEventSequence);
        Assert.Equal(2, second.LastEventSequence);
        Assert.Equal(2, second.Version);
        Assert.True(second.OverlayMode);
        Assert.Equal("paused-only", second.VisibilityPolicy);
    }

    [Fact]
    public async Task QueuedRealtimeBeforeMatchingAnalysisKeepsNewMapAnalysisAcceptable()
    {
        await using var coordinator = new OverlayRuntimeCoordinator();
        await coordinator.DispatchAsync(sequence => new RealtimeTelemetryReceived(
            sequence,
            CreateTelemetry(RealtimePlayState.Menu, 1_000, string.Empty, "674175")));

        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.TransitionApplied += (_, transition) =>
        {
            if (transition.Event is OverlayModeChanged)
            {
                entered.TrySetResult(null);
                release.Task.GetAwaiter().GetResult();
            }
        };

        Task<OverlayRuntimeState> blocker = coordinator.DispatchAsync(sequence =>
            new OverlayModeChanged(sequence, true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(coordinator.TryPost(sequence => new RealtimeTelemetryReceived(
            sequence,
            CreateTelemetry(RealtimePlayState.Menu, 500, string.Empty, "1540669"))));
        var analysis = new AnalysisSnapshot
        {
            SourceId = "headless",
            Beatmap = new BeatmapSnapshot { Id = "1540669" }
        };
        long generation = OverlayRuntimeAnalysisCausality.ResolveBeatmapGeneration(
            coordinator.Current,
            analysis);
        Task<OverlayRuntimeState> accepted = coordinator.DispatchAsync(sequence =>
            new AnalysisSnapshotReceived(sequence, analysis, generation));

        Assert.Equal(0, generation);
        release.TrySetResult(null);
        await blocker;
        OverlayRuntimeState final = await accepted;

        Assert.Equal("1540669", final.BeatmapId);
        Assert.Same(analysis, final.LatestAnalysis);
        Assert.Equal(AnalysisRequestStatus.Completed, final.AnalysisRequest?.Status);
    }

    [Fact]
    public async Task TransitionDiagnosticsExposeAcceptedAndStaleEvents()
    {
        await using var coordinator = new OverlayRuntimeCoordinator();
        var transitions = new List<OverlayRuntimeTransition>();
        coordinator.TransitionApplied += (_, transition) => transitions.Add(transition);

        await coordinator.DispatchAsync(new OverlayModeChanged(10, true));
        await coordinator.DispatchAsync(new OverlayModeChanged(9, false));

        Assert.Equal(2, transitions.Count);
        Assert.True(transitions[0].Accepted);
        Assert.False(transitions[1].Accepted);
        OverlayRuntimeRejection rejection = Assert.IsType<OverlayRuntimeRejection>(transitions[1].Rejection);
        Assert.Equal(OverlayRuntimeRejectionKind.StaleSequence, rejection.Kind);
        Assert.Equal(9, rejection.EventSequence);
        Assert.Equal(10, rejection.CurrentSequence);
        Assert.Equal(10, transitions[1].Previous.LastEventSequence);
        Assert.Same(transitions[1].Previous, transitions[1].Next);
    }

    [Fact]
    public void RejectionDiagnosticsCarryTransportAndSurfaceIdentities()
    {
        OverlayRuntimeState transport = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new TosuConnectionChanged(1, TosuConnectionState.Running, 4));
        OverlayRuntimeRejection transportRejection = OverlayRuntimeReducer.DescribeRejection(
            transport,
            new TosuConnectionChanged(2, TosuConnectionState.Stopped, 3));

        Assert.Equal(OverlayRuntimeRejectionKind.StaleTosuTransportGeneration, transportRejection.Kind);
        Assert.Equal(3, transportRejection.EventGeneration);
        Assert.Equal(4, transportRejection.CurrentGeneration);

        OverlayRuntimeState surface = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new PresentationAvailabilityChanged(1, true, true, SurfaceGeneration: 7));
        OverlayRuntimeRejection surfaceRejection = OverlayRuntimeReducer.DescribeRejection(
            surface,
            new PresentationAvailabilityChanged(2, false, false, SurfaceGeneration: 6));

        Assert.Equal(OverlayRuntimeRejectionKind.StalePresentationSurfaceGeneration, surfaceRejection.Kind);
        Assert.Equal(6, surfaceRejection.EventGeneration);
        Assert.Equal(7, surfaceRejection.CurrentGeneration);
    }

    [Fact]
    public async Task FaultingDiagnosticObserverCannotBreakEventLoop()
    {
        await using var coordinator = new OverlayRuntimeCoordinator();
        coordinator.TransitionApplied += (_, _) => throw new InvalidOperationException("diagnostic failure");

        OverlayRuntimeState state = await coordinator.DispatchAsync(new OverlayModeChanged(1, true));

        Assert.True(state.OverlayMode);
        Assert.True(coordinator.Current.OverlayMode);
    }

    [Fact]
    public async Task PublishesOneComposedViewStateAfterRealtimeDataChanges()
    {
        await using var coordinator = new OverlayRuntimeCoordinator();
        var order = new List<string>();
        var viewStates = new List<OverlayViewState>();
        coordinator.TransitionApplied += (_, _) => order.Add("transition");
        coordinator.ViewStateChanged += (_, args) =>
        {
            order.Add("view-state");
            viewStates.Add(args.ViewState);
        };

        await coordinator.DispatchAsync(new RealtimeTelemetryReceived(
            1,
            CreateTelemetry(RealtimePlayState.Paused, 30_000, "session-A")));
        await coordinator.DispatchAsync(new PresentationAvailabilityChanged(2, true, true));

        Assert.Equal(["transition", "view-state", "transition", "view-state"], order);
        Assert.Equal(2, viewStates.Count);

        OverlayViewState realtimeViewState = viewStates[0];
        Assert.Equal(1, realtimeViewState.Version);
        Assert.Equal("native", realtimeViewState.Producer);
        Assert.Equal("674175", realtimeViewState.BeatmapId);
        Assert.Equal("session-A", realtimeViewState.PauseCoach?.SessionId);
        Assert.Equal(nameof(PauseCoachWidgetState.Paused), realtimeViewState.PauseCoach?.State);

        OverlayViewState presentationViewState = viewStates[1];
        Assert.Equal(2, presentationViewState.Version);
        Assert.True(presentationViewState.Presentation.Ready);
        Assert.True(presentationViewState.Presentation.Visible);
        Assert.Equal(0, presentationViewState.Presentation.SurfaceGeneration);
        Assert.Equal(realtimeViewState.BeatmapId, presentationViewState.BeatmapId);
        Assert.Equal(realtimeViewState.PauseCoach, presentationViewState.PauseCoach);
    }

    [Fact]
    public void IgnoresPresentationFeedbackFromAnOlderSurfaceGeneration()
    {
        OverlayRuntimeState state = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new PresentationAvailabilityChanged(1, true, true, SurfaceGeneration: 2));

        OverlayRuntimeState stale = OverlayRuntimeReducer.Apply(
            state,
            new PresentationAvailabilityChanged(2, false, false, SurfaceGeneration: 1));

        Assert.Same(state, stale);
        Assert.True(stale.PresentationReady);
        Assert.True(stale.PresentationVisible);
        Assert.Equal(2, stale.PresentationSurfaceGeneration);
        Assert.Equal(1, stale.Version);
        Assert.Equal(1, stale.LastEventSequence);
    }

    [Fact]
    public async Task PublishesViewStateWhenPresentationSurfaceGenerationChanges()
    {
        await using var coordinator = new OverlayRuntimeCoordinator();
        var viewStates = new List<OverlayViewState>();
        coordinator.ViewStateChanged += (_, args) => viewStates.Add(args.ViewState);

        await coordinator.DispatchAsync(new PresentationAvailabilityChanged(
            1,
            Ready: false,
            Visible: false,
            SurfaceGeneration: 1));
        viewStates.Clear();

        await coordinator.DispatchAsync(new PresentationAvailabilityChanged(
            2,
            Ready: false,
            Visible: false,
            SurfaceGeneration: 2));

        var viewState = Assert.Single(viewStates);
        Assert.Equal(2, viewState.Presentation.SurfaceGeneration);
        Assert.False(viewState.Presentation.Ready);
        Assert.False(viewState.Presentation.Visible);
    }

    [Fact]
    public void RuntimeResetAdvancesOrderingEvenWhenStateIsAlreadyEmpty()
    {
        OverlayRuntimeState reset = OverlayRuntimeReducer.Apply(
            OverlayRuntimeState.Empty,
            new RuntimeReset(1));

        Assert.Equal(1, reset.Version);
        Assert.Equal(1, reset.LastEventSequence);
        Assert.Equal(OverlayRuntimeState.Empty with
        {
            Version = 1,
            LastEventSequence = 1
        }, reset);
    }

    [Fact]
    public async Task CancellationAfterEnqueueDoesNotRaceRegistrationCleanup()
    {
        await using var coordinator = new OverlayRuntimeCoordinator();
        var entered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.TransitionApplied += (_, transition) =>
        {
            if (transition.Event.Sequence == 1)
            {
                entered.TrySetResult(null);
                release.Task.GetAwaiter().GetResult();
            }
        };

        Task<OverlayRuntimeState> first = coordinator.DispatchAsync(new OverlayModeChanged(1, true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        using var cancellation = new CancellationTokenSource();
        Task<OverlayRuntimeState> second = coordinator.DispatchAsync(
            new VisibilityPolicyChanged(2, "paused-only"),
            cancellation.Token);
        cancellation.Cancel();
        release.TrySetResult(null);

        await first;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(1, coordinator.Current.Version);
        Assert.Equal(ManiaMapAnalyzerOverlay.Core.Analysis.OverlayVisibilityPolicy.Always, coordinator.Current.VisibilityPolicy);
    }

    [Fact]
    public async Task LivePolicyChangeUpdatesDerivedVisibilityForKnownGameplay()
    {
        await using var coordinator = new OverlayRuntimeCoordinator();

        await coordinator.DispatchAsync(new OverlayModeChanged(1, true));
        await coordinator.DispatchAsync(new RealtimeTelemetryReceived(2, CreateTelemetry(
            RealtimePlayState.Playing,
            29_000,
            "session-A")));

        OverlayRuntimeState hiddenWhilePlaying = await coordinator.DispatchAsync(
            new VisibilityPolicyChanged(3, "paused-only"));

        Assert.False(OverlayVisibilityDerivation.ShouldShowNativeOverlay(hiddenWhilePlaying));

        OverlayRuntimeState visibleWhenPaused = await coordinator.DispatchAsync(
            new RealtimeTelemetryReceived(4, CreateTelemetry(
                RealtimePlayState.Paused,
                30_000,
                "session-A")));

        Assert.True(OverlayVisibilityDerivation.ShouldShowNativeOverlay(visibleWhenPaused));
    }

    [Fact]
    public async Task PublishesViewStateWhenVisibilityPolicyChangesWithoutTelemetry()
    {
        await using var coordinator = new OverlayRuntimeCoordinator();
        var viewStates = new List<OverlayViewState>();
        coordinator.ViewStateChanged += (_, args) => viewStates.Add(args.ViewState);

        await coordinator.DispatchAsync(new RealtimeTelemetryReceived(
            1,
            CreateTelemetry(RealtimePlayState.Playing, 29_000, "session-A")));
        viewStates.Clear();

        await coordinator.DispatchAsync(new VisibilityPolicyChanged(2, "paused-only"));

        var viewState = Assert.Single(viewStates);
        Assert.Equal("paused-only", viewState.Presentation.VisibilityPolicy);
        Assert.Equal("session-A", viewState.PauseCoach?.SessionId);
        Assert.Equal(29_000, viewState.Realtime?.MapTimeMs);
    }

    private static RealtimeTelemetryUpdate CreateTelemetry(
        RealtimePlayState state,
        int mapTimeMs,
        string sessionId,
        string beatmapId = "674175")
    {
        var snapshot = new RealtimeAnalysisSnapshot(
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
            state == RealtimePlayState.Paused
                ? PauseCoachWidgetState.Paused
                : PauseCoachWidgetState.Playing,
            false,
            []);
        return new RealtimeTelemetryUpdate(
            "native-http",
            "Play",
            2,
            state == RealtimePlayState.Paused,
            new RealtimeTelemetrySample(beatmapId, state, mapTimeMs),
            snapshot);
    }
}
