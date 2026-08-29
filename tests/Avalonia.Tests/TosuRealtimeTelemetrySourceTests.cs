using System.Globalization;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Avalonia.Infrastructure.Tosu;
using ManiaMapAnalyzerOverlay.RealtimeAnalysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Avalonia.Tests;

public sealed class TosuRealtimeTelemetrySourceTests
{
    [Fact]
    public async Task HttpAndWebSocketSequencesUseTheSameNormalizedContract()
    {
        JsonElement[] payloads =
        [
            Payload("SelectPlay", false, 0, 0, 100, 0, []),
            Payload("Play", false, 0, 1000, 99.5, 2, [0, 1]),
            Payload("Play", false, 5_000, 5000, 99, 8, [0, 1, -1, 2]),
            Payload("Play", true, 5_000, 5000, 99, 8, [0, 1, -1, 2])
        ];
        var http = new TosuRealtimeTelemetrySource(
            Sequence(payloads),
            "native-http",
            new PauseCoachOptions { MinimumTimingSamples = 1 });
        var websocket = new TosuRealtimeTelemetrySource(
            Sequence(payloads),
            "websocket",
            new PauseCoachOptions { MinimumTimingSamples = 1 });
        string? httpSession = null;
        string? websocketSession = null;

        for (var index = 0; index < payloads.Length; index++)
        {
            RealtimeTelemetryUpdate expected = (await http.ReadAsync())!;
            RealtimeTelemetryUpdate actual = (await websocket.ReadAsync())!;

            Assert.Equal("native-http", expected.Source);
            Assert.Equal("websocket", actual.Source);
            Assert.Equal(expected.RawStateName, actual.RawStateName);
            Assert.Equal(expected.RawStateNumber, actual.RawStateNumber);
            Assert.Equal(expected.RawPaused, actual.RawPaused);
            Assert.Equal(expected.Snapshot.State, actual.Snapshot.State);
            Assert.Equal(expected.Snapshot.WidgetState, actual.Snapshot.WidgetState);
            if (index > 0)
            {
                Assert.NotEmpty(expected.Snapshot.SessionId);
                Assert.NotEmpty(actual.Snapshot.SessionId);
                httpSession ??= expected.Snapshot.SessionId;
                websocketSession ??= actual.Snapshot.SessionId;
                Assert.Equal(httpSession, expected.Snapshot.SessionId);
                Assert.Equal(websocketSession, actual.Snapshot.SessionId);
            }
            Assert.Equal(expected.Snapshot.MapTimeMs, actual.Snapshot.MapTimeMs);
            Assert.Equal(expected.Snapshot.Score, actual.Snapshot.Score);
            Assert.Equal(expected.Snapshot.Accuracy, actual.Snapshot.Accuracy);
            Assert.Equal(expected.Snapshot.Timing.SampleCount, actual.Snapshot.Timing.SampleCount);
            Assert.Equal(expected.Snapshot.Performance.RecentHits, actual.Snapshot.Performance.RecentHits);
        }
    }

    [Fact]
    public void LazerPauseStateIsProjectedFromPlayNumberTwoAndPausedFlag()
    {
        var payload = Payload("Play", true, 25_000, 50_000, 96.2, 50, [0, 1]);

        Assert.True(TosuRealtimePayloadNormalizer.TryReadGameplayState(
            payload,
            DateTimeOffset.UtcNow,
            out TosuGameplayState gameplay));
        Assert.Equal("Play", gameplay.Name);
        Assert.Equal(2, gameplay.Number);
        Assert.True(gameplay.IsPlaying);
        Assert.True(gameplay.IsPaused);
    }

    [Fact]
    public void NamedMenuStateClearsAStalePausedFlag()
    {
        var payload = Payload("SelectPlay", true, 25_000, 50_000, 96.2, 50, [0, 1]);

        Assert.True(TosuRealtimePayloadNormalizer.TryReadGameplayState(
            payload,
            DateTimeOffset.UtcNow,
            out TosuGameplayState gameplay));
        Assert.False(gameplay.IsPlaying);
        Assert.False(gameplay.IsPaused);
    }

    private static Func<CancellationToken, Task<JsonElement?>> Sequence(IReadOnlyList<JsonElement> payloads)
    {
        var index = 0;
        return _ => Task.FromResult<JsonElement?>(payloads[index++]);
    }

    private static JsonElement Payload(
        string state,
        bool paused,
        int mapTime,
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
              "beatmap": { "id": "674175", "hash": "source-test-map", "time": { "live": {{mapTime}} } },
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
}
