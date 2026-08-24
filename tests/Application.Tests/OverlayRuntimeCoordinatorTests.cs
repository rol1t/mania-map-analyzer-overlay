using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;
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
        Assert.Equal(10, transitions[1].Previous.LastEventSequence);
        Assert.Same(transitions[1].Previous, transitions[1].Next);
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

        Assert.Equal(["transition", "view-state", "transition"], order);
        var viewState = Assert.Single(viewStates);
        Assert.Equal(1, viewState.Version);
        Assert.Equal("native", viewState.Producer);
        Assert.Equal("674175", viewState.BeatmapId);
        Assert.Equal("session-A", viewState.PauseCoach?.SessionId);
        Assert.Equal(nameof(PauseCoachWidgetState.Paused), viewState.PauseCoach?.State);
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

    private static TosuRealtimeTelemetry CreateTelemetry(
        RealtimePlayState state,
        int mapTimeMs,
        string sessionId)
    {
        var snapshot = new RealtimeAnalysisSnapshot(
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
            []);
        return new TosuRealtimeTelemetry(
            "native-http",
            "Play",
            2,
            state == RealtimePlayState.Paused,
            new RealtimeTelemetrySample("674175", state, mapTimeMs),
            snapshot);
    }
}
