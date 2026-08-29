using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.RealtimeAnalysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

/// <summary>
/// Runs recorded Tosu payloads through the same source/collector adapter used
/// by the native runtime. These fixtures deliberately do not call the domain
/// analyzer directly, so adapter regressions remain visible at the boundary.
/// </summary>
public sealed class TosuRealtimeFixtureTests
{
    [Fact]
    public async Task StableFixturesPreservePlayPauseResultsLifecycle()
    {
        var source = CreateSource(
            "native-http",
            "stable-select-play",
            "stable-playing",
            "stable-paused",
            "stable-resumed",
            "stable-results");

        RealtimeTelemetryUpdate menu = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());
        RealtimeTelemetryUpdate playing = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());
        RealtimeTelemetryUpdate paused = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());
        RealtimeTelemetryUpdate resumed = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());
        RealtimeTelemetryUpdate results = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());

        Assert.Equal(RealtimePlayState.Menu, menu.Snapshot.State);
        Assert.Equal(PauseCoachWidgetState.WaitingForGame, menu.Snapshot.WidgetState);
        Assert.Equal("Playing", playing.RawStateName);
        Assert.False(playing.RawPaused);
        Assert.Equal(RealtimePlayState.Playing, playing.Snapshot.State);
        Assert.Equal(PauseCoachWidgetState.Playing, playing.Snapshot.WidgetState);
        Assert.NotEmpty(playing.Snapshot.SessionId);

        Assert.Equal("Paused", paused.RawStateName);
        Assert.True(paused.RawPaused);
        Assert.Equal(RealtimePlayState.Paused, paused.Snapshot.State);
        Assert.Equal(PauseCoachWidgetState.Paused, paused.Snapshot.WidgetState);
        Assert.Equal(playing.Snapshot.SessionId, paused.Snapshot.SessionId);
        Assert.Equal(30_000, paused.Snapshot.MapTimeMs);
        Assert.True(paused.Snapshot.Timing.SampleCount > 0);

        Assert.Equal(RealtimePlayState.Playing, resumed.Snapshot.State);
        Assert.Equal(playing.Snapshot.SessionId, resumed.Snapshot.SessionId);
        Assert.Equal(31_000, resumed.Snapshot.MapTimeMs);

        Assert.Equal(RealtimePlayState.Results, results.Snapshot.State);
        Assert.Equal(PauseCoachWidgetState.Ready, results.Snapshot.WidgetState);
        Assert.Equal(playing.Snapshot.SessionId, results.Snapshot.SessionId);
        Assert.NotEmpty(results.Snapshot.Insights);
    }

    [Fact]
    public async Task FailureFixturePreservesTheNormalizedFailureSignal()
    {
        var source = CreateSource("native-http", "stable-playing", "stable-failed");

        _ = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());
        RealtimeTelemetryUpdate failed = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());

        Assert.Equal(RealtimePlayState.Playing, failed.Snapshot.State);
        Assert.True(failed.Sample.Failed);
        Assert.Equal(30_000, failed.Snapshot.MapTimeMs);
        Assert.NotEmpty(failed.Snapshot.SessionId);
    }

    [Fact]
    public async Task LazerPlayAndPauseUseTheNormalPlayNumberTwoRepresentation()
    {
        var source = CreateSource("native-http", "lazer-playing", "lazer-paused");

        RealtimeTelemetryUpdate playing = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());
        RealtimeTelemetryUpdate paused = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());

        Assert.Equal("Play", playing.RawStateName);
        Assert.Equal(2, playing.RawStateNumber);
        Assert.False(playing.RawPaused);
        Assert.Equal(RealtimePlayState.Playing, playing.Snapshot.State);

        Assert.Equal("Play", paused.RawStateName);
        Assert.Equal(2, paused.RawStateNumber);
        Assert.True(paused.RawPaused);
        Assert.Equal(RealtimePlayState.Paused, paused.Snapshot.State);
        Assert.Equal(PauseCoachWidgetState.Paused, paused.Snapshot.WidgetState);
        Assert.Equal(playing.Snapshot.SessionId, paused.Snapshot.SessionId);
        Assert.Equal(30_000, paused.Snapshot.MapTimeMs);
        Assert.True(paused.Snapshot.Performance.RecentHits > 0);
    }

    [Fact]
    public async Task PartialPauseRetainsTelemetryAndRetryStartsANewAttempt()
    {
        var source = CreateSource(
            "native-http",
            "stable-playing",
            "partial-payload",
            "retry-reset");

        RealtimeTelemetryUpdate playing = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());
        RealtimeTelemetryUpdate partialPause = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());
        RealtimeTelemetryUpdate retry = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());

        Assert.Equal(RealtimePlayState.Paused, partialPause.Snapshot.State);
        Assert.Equal(PauseCoachWidgetState.Paused, partialPause.Snapshot.WidgetState);
        Assert.Equal(playing.Snapshot.SessionId, partialPause.Snapshot.SessionId);
        Assert.Equal(25_000, partialPause.Snapshot.MapTimeMs);
        Assert.Equal(25_000, partialPause.Snapshot.Score);
        Assert.True(partialPause.Snapshot.Timing.SampleCount > 0);
        Assert.Equal(6, partialPause.HitErrorSampleCount);

        Assert.Equal(RealtimePlayState.Playing, retry.Snapshot.State);
        Assert.NotEqual(playing.Snapshot.SessionId, retry.Snapshot.SessionId);
        Assert.Equal(0, retry.Snapshot.MapTimeMs);
        Assert.Equal(1, retry.Snapshot.Timing.SampleCount);
    }

    [Fact]
    public async Task ReplayAndSpectatingDoNotCarryThePreviousAttempt()
    {
        var source = CreateSource(
            "native-http",
            "stable-playing",
            "replay",
            "spectating");

        RealtimeTelemetryUpdate playing = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());
        RealtimeTelemetryUpdate replay = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());
        RealtimeTelemetryUpdate spectating = Assert.IsType<RealtimeTelemetryUpdate>(await source.ReadAsync());

        Assert.NotEmpty(playing.Snapshot.SessionId);
        Assert.Equal(RealtimePlayState.Replay, replay.Snapshot.State);
        Assert.Equal(PauseCoachWidgetState.Unavailable, replay.Snapshot.WidgetState);
        Assert.Empty(replay.Snapshot.SessionId);

        Assert.Equal(RealtimePlayState.Spectating, spectating.Snapshot.State);
        Assert.Equal(PauseCoachWidgetState.Unavailable, spectating.Snapshot.WidgetState);
        Assert.Empty(spectating.Snapshot.SessionId);
    }

    [Fact]
    public async Task RecordedFixturesNormalizeEquivalentlyForHttpAndWebSocketSources()
    {
        var http = CreateSource("native-http", "lazer-playing", "lazer-paused");
        var websocket = CreateSource("websocket", "lazer-playing", "lazer-paused");

        for (var index = 0; index < 2; index++)
        {
            RealtimeTelemetryUpdate expected = Assert.IsType<RealtimeTelemetryUpdate>(await http.ReadAsync());
            RealtimeTelemetryUpdate actual = Assert.IsType<RealtimeTelemetryUpdate>(await websocket.ReadAsync());

            Assert.Equal(expected.RawStateName, actual.RawStateName);
            Assert.Equal(expected.RawStateNumber, actual.RawStateNumber);
            Assert.Equal(expected.RawPaused, actual.RawPaused);
            Assert.Equal(expected.Snapshot.BeatmapId, actual.Snapshot.BeatmapId);
            Assert.Equal(expected.Snapshot.State, actual.Snapshot.State);
            Assert.Equal(expected.Snapshot.WidgetState, actual.Snapshot.WidgetState);
            Assert.Equal(expected.Snapshot.MapTimeMs, actual.Snapshot.MapTimeMs);
            Assert.Equal(expected.Snapshot.Score, actual.Snapshot.Score);
            Assert.Equal(expected.Snapshot.Accuracy, actual.Snapshot.Accuracy);
            Assert.Equal(expected.Snapshot.Timing.SampleCount, actual.Snapshot.Timing.SampleCount);
            Assert.Equal(expected.Snapshot.Performance.RecentHits, actual.Snapshot.Performance.RecentHits);
            Assert.Equal("native-http", expected.Source);
            Assert.Equal("websocket", actual.Source);
        }
    }

    private static TosuRealtimeTelemetrySource CreateSource(string source, params string[] fixtureNames)
    {
        JsonElement[] payloads = new JsonElement[fixtureNames.Length];
        for (var index = 0; index < fixtureNames.Length; index++)
        {
            payloads[index] = ReadFixture(fixtureNames[index]);
        }

        var payloadIndex = 0;
        return new TosuRealtimeTelemetrySource(
            _ => Task.FromResult<JsonElement?>(payloads[payloadIndex++]),
            source,
            new PauseCoachOptions { MinimumTimingSamples = 1 });
    }

    private static JsonElement ReadFixture(string fixtureName)
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Tosu",
            fixtureName + ".json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
}
