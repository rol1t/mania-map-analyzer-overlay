using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.RealtimeAnalysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

/// <summary>
/// Exercises the production-shaped raw Tosu -> normalizer/collector ->
/// application coordinator -> latest-wins presenter path without invoking the
/// browser Pause Coach runtime directly.
/// </summary>
public sealed class NativeRealtimeApplicationScenarioTests
{
    [Fact]
    public async Task HiddenDesktopFlushesMinimizedRuntimeStateBeforeBecomingVisible()
    {
        JsonElement payload = Payload(
            "Play",
            paused: false,
            mapTimeMs: 12_000,
            score: 12_000,
            accuracy: 98,
            hits: 20,
            offsets: [1, -1, 2, -2]);
        var source = new TosuRealtimeTelemetrySource(
            _ => Task.FromResult<JsonElement?>(payload),
            source: "native-http",
            options: new PauseCoachOptions { MinimumTimingSamples = 1 });
        await using var coordinator = new OverlayRuntimeCoordinator();
        var published = new List<OverlayViewState>();
        var publisher = new LatestWinsSnapshotPublisher<OverlayViewState>(snapshot =>
        {
            published.Add(snapshot);
            return Task.CompletedTask;
        });
        publisher.BeginPresentationSession();
        publisher.SetBrowserReady(true);
        publisher.SetPresentationVisible(false);
        coordinator.ViewStateChanged += (_, args) => publisher.Submit(args.ViewState);

        await coordinator.DispatchAsync(new OverlayModeChanged(1, true));
        await coordinator.DispatchAsync(new PresentationAvailabilityChanged(2, true, false, 1));
        RealtimeTelemetryUpdate telemetry = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());
        await coordinator.DispatchAsync(new RealtimeTelemetryReceived(3, telemetry));
        OverlayRuntimeState minimized = await coordinator.DispatchAsync(
            new OsuWindowStateChanged(4, true));

        Assert.Empty(published);
        Assert.True(minimized.OsuWindowMinimized);

        publisher.SetPresentationVisible(true);
        await EventuallyAsync(() => published.Count == 1);

        OverlayViewState firstVisible = Assert.Single(published);
        Assert.True(firstVisible.Presentation.OsuWindowMinimized);
        Assert.Equal(12_000, firstVisible.Realtime?.MapTimeMs);
        Assert.Equal(minimized.Version, firstVisible.Version);
    }

    [Fact]
    public async Task HiddenHttpCollectionFlushesTheLatestLazerPauseFrame()
    {
        var payloads = new List<JsonElement>
        {
            Payload("SelectPlay", paused: false, mapTimeMs: 0, score: 0, accuracy: 100, hits: 0, offsets: []),
            Payload("Play", paused: false, mapTimeMs: 0, score: 100, accuracy: 100, hits: 2, offsets: [0, 1]),
            Payload("Play", paused: false, mapTimeMs: 5_000, score: 5_000, accuracy: 99.5, hits: 8, offsets: [0, 1, -1, 2]),
            Payload("Play", paused: false, mapTimeMs: 10_000, score: 10_000, accuracy: 99, hits: 16, offsets: [1, 0, -2, 2]),
            Payload("Play", paused: false, mapTimeMs: 20_000, score: 20_000, accuracy: 98.5, hits: 28, offsets: [2, 1, -1, 3]),
            Payload("Play", paused: false, mapTimeMs: 30_000, score: 30_000, accuracy: 98, hits: 40, offsets: [3, 2, -2, 4]),
            Payload("Play", paused: true, mapTimeMs: 30_000, score: 30_000, accuracy: 98, hits: 40, offsets: [3, 2, -2, 4])
        };
        var index = 0;
        var source = new TosuRealtimeTelemetrySource(
            _ => Task.FromResult<JsonElement?>(payloads[index++]),
            source: "native-http",
            options: new PauseCoachOptions { MinimumTimingSamples = 1 });
        await using var coordinator = new OverlayRuntimeCoordinator();
        var published = new List<OverlayViewState>();
        var publisher = new LatestWinsSnapshotPublisher<OverlayViewState>(snapshot =>
        {
            published.Add(snapshot);
            return Task.CompletedTask;
        });
        publisher.BeginPresentationSession();
        publisher.SetBrowserReady(true);
        publisher.SetPresentationVisible(false);
        coordinator.ViewStateChanged += (_, args) => publisher.Submit(args.ViewState);

        await coordinator.DispatchAsync(new OverlayModeChanged(1, true));
        await coordinator.DispatchAsync(new PresentationAvailabilityChanged(2, false, false, 1));

        OverlayRuntimeState? firstPlaying = null;
        OverlayRuntimeState? latest = null;
        var telemetrySequence = 3L;
        for (; index < payloads.Count; telemetrySequence++)
        {
            RealtimeTelemetryUpdate telemetry = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());
            latest = await coordinator.DispatchAsync(new RealtimeTelemetryReceived(telemetrySequence, telemetry));
            if (telemetry.Snapshot.State == RealtimePlayState.Playing && firstPlaying is null)
            {
                firstPlaying = latest;
            }
        }

        Assert.NotNull(firstPlaying);
        Assert.NotNull(latest);
        Assert.NotEmpty(firstPlaying!.SessionId);
        Assert.Equal(firstPlaying.SessionId, latest!.SessionId);
        Assert.Equal(30_000, latest.LatestRealtime!.MapTimeMs);
        Assert.Equal(RealtimePlayState.Paused, latest.GameplayState);
        Assert.NotEmpty(latest.LatestRealtime.RecentOffsets);
        Assert.Empty(published);

        publisher.SetPresentationVisible(true);
        await EventuallyAsync(() => published.Count == 1);

        var rendered = Assert.Single(published);
        Assert.Equal(30_000, rendered.Realtime?.MapTimeMs);
        Assert.Equal(latest.SessionId, rendered.PauseCoach?.SessionId);
        Assert.Equal(nameof(PauseCoachWidgetState.Paused), rendered.PauseCoach?.State);
        Assert.NotEqual(nameof(PauseCoachWidgetState.WaitingForGame), rendered.PauseCoach?.State);

        // The same attempt must survive a resume and a second pause. This is
        // the production failure mode that used to reset the native session
        // when presentation was recreated or became visible again.
        payloads.Add(Payload("Play", paused: false, mapTimeMs: 40_000, score: 40_000, accuracy: 97.5, hits: 52, offsets: [4, 3, -1, 2]));
        payloads.Add(Payload("Play", paused: true, mapTimeMs: 40_000, score: 40_000, accuracy: 97.5, hits: 52, offsets: [4, 3, -1, 2]));
        for (; index < payloads.Count; telemetrySequence++)
        {
            RealtimeTelemetryUpdate telemetry = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());
            latest = await coordinator.DispatchAsync(new RealtimeTelemetryReceived(telemetrySequence, telemetry));
        }

        await EventuallyAsync(() => published.Any(snapshot =>
            snapshot.Realtime?.MapTimeMs == 40_000
            && snapshot.PauseCoach?.State == nameof(PauseCoachWidgetState.Paused)));
        Assert.Equal(firstPlaying!.SessionId, latest!.SessionId);
        Assert.Equal(40_000, latest.LatestRealtime!.MapTimeMs);
        Assert.Equal(RealtimePlayState.Paused, latest.GameplayState);
    }

    private static JsonElement Payload(
        string state,
        bool paused,
        int mapTimeMs,
        int score,
        double accuracy,
        int hits,
        int[] offsets)
    {
        string offsetJson = string.Join(",", offsets);
        return JsonDocument.Parse($$"""
            {
              "state": { "name": "{{state}}", "number": 2 },
              "game": { "paused": {{paused.ToString().ToLowerInvariant()}}, "focused": true },
              "beatmap": { "id": "674175", "hash": "application-scenario-map", "time": { "live": {{mapTimeMs}} } },
              "play": {
                "score": {{score}},
                "accuracy": {{accuracy.ToString(CultureInfo.InvariantCulture)}},
                "combo": { "current": {{hits}}, "max": {{hits}} },
                "hits": { "0": 0, "50": 0, "100": 1, "300": {{hits}}, "geki": 0, "katu": 0 },
                "hitErrorArray": [{{offsetJson}}]
              }
            }
            """).RootElement.Clone();
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "Expected the presenter to flush its latest snapshot.");
    }
}
