using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;
using ManiaMapAnalyzerOverlay.Avalonia.Services;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.RealtimeAnalysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class OverlayGameplayPollingControllerTests
{
    [Fact]
    public async Task StartPerformsImmediatePollAndStopEndsTheGeneration()
    {
        var timers = new Queue<FakeTimer>();
        var readCount = 0;
        var timer = new FakeTimer();
        timers.Enqueue(timer);

        using var controller = new OverlayGameplayPollingController(
            _ =>
            {
                Interlocked.Increment(ref readCount);
                return Task.FromResult<RealtimeTelemetryUpdate?>(null);
            },
            _ => { },
            TimeSpan.FromSeconds(1),
            timerFactory: _ => timers.Dequeue(),
            dispatch: action => action());

        controller.Start();
        await EventuallyAsync(() => Volatile.Read(ref readCount) == 1);

        Assert.True(controller.IsRunning);
        controller.Stop();
        Assert.False(controller.IsRunning);
        Assert.False(timer.IsRunning);
    }

    [Fact]
    public async Task QueuedCallbackFromStoppedGenerationCannotApplyTelemetry()
    {
        var readStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedDispatches = new List<Action>();
        var applied = new List<RealtimeTelemetryUpdate>();
        var timer = new FakeTimer();

        using var controller = new OverlayGameplayPollingController(
            async _ =>
            {
                readStarted.TrySetResult(null);
                await releaseRead.Task.ConfigureAwait(false);
                return CreateTelemetry(30_000);
            },
            applied.Add,
            TimeSpan.FromSeconds(1),
            timerFactory: _ => timer,
            dispatch: action => queuedDispatches.Add(action));

        controller.Start();
        await readStarted.Task;
        controller.Stop();
        releaseRead.TrySetResult(null);

        await EventuallyAsync(() => queuedDispatches.Count == 1);
        foreach (var dispatch in queuedDispatches)
        {
            dispatch();
        }

        Assert.Empty(applied);
    }

    [Fact]
    public async Task ReenteredGenerationAcceptsNewTelemetryAfterOldRequestCompletes()
    {
        var firstReadStarted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRead = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<RealtimeTelemetryUpdate>();
        var timers = new Queue<FakeTimer>([new FakeTimer(), new FakeTimer()]);
        var readCount = 0;

        using var controller = new OverlayGameplayPollingController(
            async _ =>
            {
                if (Interlocked.Increment(ref readCount) == 1)
                {
                    firstReadStarted.TrySetResult(null);
                    await releaseFirstRead.Task.ConfigureAwait(false);
                    return CreateTelemetry(10_000);
                }

                return CreateTelemetry(40_000);
            },
            applied.Add,
            TimeSpan.FromSeconds(1),
            timerFactory: _ => timers.Dequeue(),
            dispatch: action => action());

        controller.Start();
        await firstReadStarted.Task;
        controller.Stop();

        controller.Start();
        await EventuallyAsync(() => applied.Count == 1);

        releaseFirstRead.TrySetResult(null);
        await EventuallyAsync(() => !controller.IsRequestInFlight);

        Assert.Equal(40_000, applied[0].Snapshot.MapTimeMs);
    }

    [Fact]
    public void QueuedStateFromDetachedTosuCannotStopTheCurrentPollingGeneration()
    {
        var lifecycleCallbacks = new List<Action>();
        var timer = new FakeTimer();
        using var host = new TosuRealtimeRuntimeHost(
            _ => Task.FromResult<JsonElement?>(null),
            _ => { },
            TimeSpan.FromSeconds(1),
            timerFactory: _ => timer,
            dispatch: action => action(),
            lifecycleDispatch: action => lifecycleCallbacks.Add(action));

        var detached = new FakeTosuLifecycle(TosuConnectionState.Stopped, 1);
        host.Attach(detached);
        detached.Emit(TosuConnectionState.Stopped, 1);

        var current = new FakeTosuLifecycle(TosuConnectionState.Running, 1);
        host.Attach(current);
        Assert.True(host.IsRunning);

        Assert.Single(lifecycleCallbacks);
        lifecycleCallbacks[0]();

        Assert.True(host.IsRunning);
        Assert.True(timer.IsRunning);
    }

    private static RealtimeTelemetryUpdate CreateTelemetry(int mapTimeMs)
    {
        var snapshot = new RealtimeAnalysisSnapshot(
            "session-A",
            "674175",
            RealtimePlayState.Playing,
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
        return new RealtimeTelemetryUpdate(
            "native-http",
            "Play",
            2,
            false,
            new RealtimeTelemetrySample("674175", RealtimePlayState.Playing, mapTimeMs),
            snapshot);
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.True(condition(), "Expected asynchronous polling condition was not reached.");
    }

    private sealed class FakeTimer : IOverlayGameplayPollingTimer
    {
        public event EventHandler? Tick;

        public TimeSpan Interval
        {
            get;
            set;
        }

        public bool IsRunning
        {
            get;
            private set;
        }

        public void Start() => IsRunning = true;

        public void Stop() => IsRunning = false;

        public void Dispose() => IsRunning = false;

        public void RaiseTick() => Tick?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeTosuLifecycle : ITosuRealtimeLifecycle
    {
        public FakeTosuLifecycle(TosuConnectionState state, long generation)
        {
            ConnectionState = state;
            IsRunning = state == TosuConnectionState.Running;
            TransportGeneration = generation;
        }

        public event EventHandler<TosuStateChangedEventArgs>? StateChanged;

        public bool IsRunning
        {
            get;
            private set;
        }

        public TosuConnectionState ConnectionState
        {
            get;
            private set;
        }

        public long TransportGeneration
        {
            get;
            private set;
        }

        public void Emit(TosuConnectionState state, long generation)
        {
            ConnectionState = state;
            IsRunning = state == TosuConnectionState.Running;
            TransportGeneration = generation;
            StateChanged?.Invoke(
                this,
                new TosuStateChangedEventArgs(
                    "test",
                    state,
                    generation));
        }
    }
}
