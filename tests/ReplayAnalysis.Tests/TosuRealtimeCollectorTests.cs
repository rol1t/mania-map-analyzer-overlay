using System;
using System.Linq;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class TosuRealtimeCollectorTests
{
    private static readonly DateTimeOffset _start = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("native-http")]
    [InlineData("websocket")]
    public void LazerPlayPauseUsesFullTosuPayloadAndKeepsSession(string source)
    {
        var collector = NewCollector();
        collector.Process(Payload("SelectPlay", 1, false, 0, 0, 0, 0, 0, []), source, _start);
        TosuRealtimeTelemetry first = collector.Process(Payload("Play", 2, false, 0, 1000, 98.5, 2, 0, [0, 1]), source, _start.AddSeconds(1))!;
        TosuRealtimeTelemetry fiveSeconds = collector.Process(Payload("Play", 2, false, 5_000, 10_000, 98, 10, 0, Enumerable.Range(0, 5).Select(index => index - 1).ToArray()), source, _start.AddSeconds(5))!;
        TosuRealtimeTelemetry tenSeconds = collector.Process(Payload("Play", 2, false, 10_000, 20_000, 97.5, 20, 1, Enumerable.Range(0, 8).Select(index => index - 2).ToArray()), source, _start.AddSeconds(10))!;
        TosuRealtimeTelemetry twentyFiveSeconds = collector.Process(Payload("Play", 2, false, 25_000, 50_000, 96.2, 50, 3, Enumerable.Range(0, 16).Select(index => index - 4).ToArray()), source, _start.AddSeconds(25))!;
        TosuRealtimeTelemetry paused = collector.Process(Payload("Play", 2, true, 25_000, 50_000, 96.2, 50, 3, Enumerable.Range(0, 16).Select(index => index - 4).ToArray()), source, _start.AddSeconds(26))!;

        Assert.Equal(PauseCoachWidgetState.Playing, first.Snapshot.WidgetState);
        Assert.Equal(PauseCoachWidgetState.Playing, fiveSeconds.Snapshot.WidgetState);
        Assert.Equal(PauseCoachWidgetState.Playing, tenSeconds.Snapshot.WidgetState);
        Assert.Equal(PauseCoachWidgetState.Playing, twentyFiveSeconds.Snapshot.WidgetState);
        Assert.Equal(RealtimePlayState.Paused, paused.Snapshot.State);
        Assert.Equal(PauseCoachWidgetState.Paused, paused.Snapshot.WidgetState);
        Assert.NotEqual(string.Empty, first.Snapshot.SessionId);
        Assert.Equal(first.Snapshot.SessionId, twentyFiveSeconds.Snapshot.SessionId);
        Assert.Equal(first.Snapshot.SessionId, paused.Snapshot.SessionId);
        Assert.Equal(25_000, paused.Snapshot.MapTimeMs);
        Assert.True(paused.Snapshot.Timing.SampleCount > 0);
        Assert.True(paused.Snapshot.Performance.RecentHits > 0);
        Assert.Equal(50_000, paused.Snapshot.Score);
        Assert.Equal(.962, paused.Snapshot.Accuracy!.Value, 3);
        Assert.Equal("Play", paused.RawStateName);
        Assert.Equal(true, paused.RawPaused);

        PauseCoachSnapshot domainSnapshot = PauseCoachSnapshotMapper.ToSnapshot(paused.Snapshot);
        Assert.Equal(nameof(PauseCoachWidgetState.Paused), domainSnapshot.State);
        Assert.Equal(paused.Snapshot.SessionId, domainSnapshot.SessionId);
        Assert.Equal(25_000, domainSnapshot.MapProgressMs);
        Assert.True(domainSnapshot.Timing.SampleCount > 0);
    }

    [Fact]
    public void PartialLazerPausePacketPreservesFullGameplayTelemetry()
    {
        var collector = NewCollector();
        TosuRealtimeTelemetry playing = collector.Process(Payload("Play", 2, false, 25_000, 50_000, 96.2, 50, 3, Enumerable.Range(0, 16).Select(index => index - 4).ToArray()), "native-http", _start)!;
        TosuRealtimeTelemetry paused = collector.Process(Raw("""
        {
          "state": { "number": 2, "name": "Play" },
          "game": { "paused": true, "focused": true }
        }
        """), "native-http", _start.AddSeconds(1))!;

        Assert.Equal(RealtimePlayState.Paused, paused.Snapshot.State);
        Assert.Equal(PauseCoachWidgetState.Paused, paused.Snapshot.WidgetState);
        Assert.Equal(playing.Snapshot.SessionId, paused.Snapshot.SessionId);
        Assert.Equal(25_000, paused.Snapshot.MapTimeMs);
        Assert.Equal(50_000, paused.Snapshot.Score);
        Assert.True(paused.Snapshot.Timing.SampleCount > 0);
        Assert.Equal(16, paused.HitErrorSampleCount);
    }

    [Fact]
    public void NumericBeatmapIdIsPreservedByNativePayloadNormalizer()
    {
        var collector = NewCollector();
        TosuRealtimeTelemetry telemetry = collector.Process(Raw("""
        {
          "state": { "number": 2, "name": "Play" },
          "game": { "paused": true },
          "beatmap": { "id": 674175, "time": { "live": 25000 } },
          "play": {
            "score": 50000,
            "accuracy": 96.2,
            "combo": { "current": 50, "max": 50 },
            "hits": { "0": 3, "50": 0, "100": 1, "300": 50 },
            "hitErrorArray": [0, 1, 2, 3]
          }
        }
        """), "native-http", _start)!;

        Assert.Equal("674175", telemetry.Sample.BeatmapId);
        Assert.NotEmpty(telemetry.Snapshot.SessionId);
        Assert.Equal(PauseCoachWidgetState.Paused, telemetry.Snapshot.WidgetState);
    }

    [Fact]
    public void PauseResumeKeepsSessionAndRetryStartsNewSession()
    {
        var collector = NewCollector();
        TosuRealtimeTelemetry playing = collector.Process(Payload("Play", 2, false, 25_000, 50_000, 96.2, 50, 3, [0, 1, 2, 3]), "native-http", _start)!;
        TosuRealtimeTelemetry paused = collector.Process(Payload("Play", 2, true, 25_000, 50_000, 96.2, 50, 3, [0, 1, 2, 3]), "native-http", _start.AddSeconds(25))!;
        TosuRealtimeTelemetry resumed = collector.Process(Payload("Play", 2, false, 26_000, 55_000, 96.1, 55, 4, [0, 1, 2, 3, 4]), "native-http", _start.AddSeconds(26))!;
        TosuRealtimeTelemetry retry = collector.Process(Payload("Play", 2, false, 0, 0, 100, 0, 0, [0]), "native-http", _start.AddSeconds(27))!;

        Assert.Equal(playing.Snapshot.SessionId, paused.Snapshot.SessionId);
        Assert.Equal(playing.Snapshot.SessionId, resumed.Snapshot.SessionId);
        Assert.NotEqual(playing.Snapshot.SessionId, retry.Snapshot.SessionId);
        Assert.Equal(1, retry.Snapshot.Timing.SampleCount);
    }

    [Fact]
    public void HitErrorArrayTruncateThenGrowDoesNotDuplicateKnownOffsets()
    {
        var collector = NewCollector();
        collector.Process(Payload("Play", 2, false, 5_000, 5_000, 98, 3, 0, [1, 2, 3]), "native-http", _start);
        collector.Process(Payload("Play", 2, false, 6_000, 6_000, 98, 3, 0, [1, 2]), "native-http", _start.AddSeconds(1));
        TosuRealtimeTelemetry grown = collector.Process(
            Payload("Play", 2, false, 7_000, 7_000, 98, 4, 0, [1, 2, 3, 4]),
            "native-http",
            _start.AddSeconds(2))!;

        Assert.Equal(4, grown.Snapshot.Timing.SampleCount);
        Assert.Equal([1d, 2d, 3d, 4d], grown.Snapshot.RecentOffsets);
    }

    [Fact]
    public void NamedMenuStateWinsOverStalePausedFlag()
    {
        var collector = NewCollector();
        collector.Process(Payload("Play", 2, false, 25_000, 50_000, 96.2, 50, 3, [0, 1, 2, 3]), "native-http", _start);

        // During a lazer transition Tosu can report the menu while the game
        // object still carries paused=true from the previous play frame.
        TosuRealtimeTelemetry menu = collector.Process(
            Payload("SelectPlay", 5, true, 25_000, 50_000, 96.2, 50, 3, [0, 1, 2, 3]),
            "native-http",
            _start.AddSeconds(26))!;

        Assert.Equal(RealtimePlayState.Menu, menu.Snapshot.State);
        Assert.NotEqual(PauseCoachWidgetState.Paused, menu.Snapshot.WidgetState);
    }

    [Fact]
    public void ResultsRetainFinalDiagnosisAndCollectorIsVisibilityIndependent()
    {
        // No presentation/WebView object is involved in this path. The same
        // collector keeps processing while the native HWND could be hidden.
        var collector = NewCollector();
        collector.Process(Payload("Play", 2, false, 0, 1000, 99, 1, 0, [1]), "native-http", _start);
        collector.Process(Payload("Play", 2, false, 25_000, 25_000, 94, 25, 2, [1, 2, 3, 4]), "native-http", _start.AddSeconds(25));
        TosuRealtimeTelemetry results = collector.Process(Payload("Results", 7, true, 25_000, 25_000, 94, 25, 2, [1, 2, 3, 4]), "native-http", _start.AddSeconds(26))!;

        Assert.Equal(RealtimePlayState.Results, results.Snapshot.State);
        Assert.Equal(PauseCoachWidgetState.Ready, results.Snapshot.WidgetState);
        Assert.NotEmpty(results.Snapshot.SessionId);
        Assert.NotEmpty(results.Snapshot.Insights);
    }

    private static TosuRealtimeCollector NewCollector() => new(new PauseCoachOptions { MinimumTimingSamples = 1 });

    private static JsonElement Payload(
        string state,
        int stateNumber,
        bool paused,
        int mapTime,
        int score,
        double accuracy,
        int hits,
        int misses,
        int[] offsets)
    {
        string offsetJson = string.Join(",", offsets);
        return Raw($$"""
        {
          "state": { "number": {{stateNumber}}, "name": "{{state}}" },
          "game": { "paused": {{paused.ToString().ToLowerInvariant()}}, "focused": true },
          "beatmap": { "id": "674175", "hash": "stable-map-hash", "time": { "live": {{mapTime}} } },
          "play": {
            "score": {{score}},
            "accuracy": {{accuracy.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
            "combo": { "current": {{hits}}, "max": {{hits}} },
            "healthBar": { "normal": 0.82, "smooth": 0.8 },
            "mods": { "array": [{ "acronym": "DT" }] },
            "hits": { "0": {{misses}}, "50": 0, "100": 1, "300": {{hits}}, "geki": 0, "katu": 0 },
            "hitErrorArray": [{{offsetJson}}],
            "unstableRate": 32.1
          }
        }
        """);
    }

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
