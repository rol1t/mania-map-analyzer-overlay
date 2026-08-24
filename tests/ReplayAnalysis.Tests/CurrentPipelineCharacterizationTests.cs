using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

/// <summary>
/// Test-only characterization harness for the pre-coordinator pipeline.
/// It deliberately uses the production Tosu normalizer and collector while
/// keeping presentation lifecycle concerns in a deterministic fake.
/// </summary>
public sealed class CurrentPipelineCharacterizationTests
{
    private static readonly DateTimeOffset _start = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PlayPauseResumeRetryResultsAndPartialPayloadKeepLifecycleEvidence()
    {
        var harness = new CurrentPipelineHarness();

        RealtimeAnalysisSnapshot playing = harness.Push(Payload("Play", false, 0, 1000, 99.5, 2, [0, 1]));
        RealtimeAnalysisSnapshot atThirtySeconds = harness.Push(Payload("Play", false, 30_000, 30_000, 97, 30, [0, 1, -1, 2, 0, 1]));
        RealtimeAnalysisSnapshot paused = harness.Push(Payload("Play", true, 30_000, 30_000, 97, 30, [0, 1, -1, 2, 0, 1]));
        RealtimeAnalysisSnapshot resumed = harness.Push(Payload("Play", false, 31_000, 31_000, 96.9, 31, [0, 1, -1, 2, 0, 1, 3]));
        RealtimeAnalysisSnapshot pausedAgain = harness.Push(Payload("Play", true, 31_000, 31_000, 96.9, 31, [0, 1, -1, 2, 0, 1, 3]));
        RealtimeAnalysisSnapshot retried = harness.Push(Payload("Play", false, 0, 100, 100, 1, [0]));
        RealtimeAnalysisSnapshot results = harness.Push(Payload("Results", true, 5_000, 500, 99, 5, [0, 1, -1]));
        RealtimeAnalysisSnapshot partial = harness.Push(Raw("""
            {
              "state": { "name": "Play", "number": 2 },
              "game": { "paused": true }
            }
            """));

        Assert.Equal(PauseCoachWidgetState.Playing, playing.WidgetState);
        Assert.Equal(30_000, atThirtySeconds.MapTimeMs);
        Assert.Equal(PauseCoachWidgetState.Paused, paused.WidgetState);
        Assert.Equal(playing.SessionId, paused.SessionId);
        Assert.Equal(playing.SessionId, resumed.SessionId);
        Assert.Equal(playing.SessionId, pausedAgain.SessionId);
        Assert.NotEqual(playing.SessionId, retried.SessionId);
        Assert.Equal(PauseCoachWidgetState.Ready, results.WidgetState);
        Assert.Equal(retried.SessionId, results.SessionId);
        Assert.Equal(PauseCoachWidgetState.Paused, partial.WidgetState);
        Assert.Equal(results.SessionId, partial.SessionId);
        Assert.Equal(results.MapTimeMs, partial.MapTimeMs);
        Assert.True(partial.Timing.SampleCount > 0);
    }

    [Fact]
    public void HiddenCollectionAndPresentationRecreationReplayNewestSnapshot()
    {
        var harness = new CurrentPipelineHarness();
        harness.Presentation.SetVisible(false);

        harness.Push(Payload("Play", false, 1_000, 1000, 99, 2, [0, 1]));
        harness.Push(Payload("Play", false, 5_000, 5000, 98, 8, [0, 1, -1, 2]));
        harness.Push(Payload("Play", false, 20_000, 20_000, 97, 20, [0, 1, -1, 2, 0, 1]));
        RealtimeAnalysisSnapshot paused = harness.Push(Payload("Play", true, 20_000, 20_000, 97, 20, [0, 1, -1, 2, 0, 1]));

        Assert.Empty(harness.Presentation.Published);
        Assert.Same(paused, harness.Presentation.Pending);

        harness.Presentation.ReplaceDocument();
        harness.Presentation.SetVisible(true);

        Assert.Single(harness.Presentation.Published);
        Assert.Equal(PauseCoachWidgetState.Paused, harness.Presentation.Published[0].WidgetState);
        Assert.Equal(paused.MapTimeMs, harness.Presentation.Published[0].MapTimeMs);
        Assert.Equal(paused.SessionId, harness.Presentation.Published[0].SessionId);
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
        return Raw($$"""
            {
              "state": { "name": "{{state}}", "number": 2 },
              "game": { "paused": {{paused.ToString().ToLowerInvariant()}}, "focused": true },
              "beatmap": { "id": "674175", "hash": "characterization-map", "time": { "live": {{mapTime}} } },
              "play": {
                "score": {{score}},
                "accuracy": {{accuracy.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                "combo": { "current": {{hits}}, "max": {{hits}} },
                "hits": { "0": 0, "50": 0, "100": 1, "300": {{hits}}, "geki": 0, "katu": 0 },
                "hitErrorArray": [{{offsetJson}}]
              }
            }
            """);
    }

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class CurrentPipelineHarness
    {
        private readonly TosuRealtimeCollector _collector = new(new PauseCoachOptions { MinimumTimingSamples = 1 });
        private int _sampleNumber;

        public CurrentPipelineHarness()
        {
            Presentation = new FakePresentation();
        }

        public FakePresentation Presentation
        {
            get;
        }

        public RealtimeAnalysisSnapshot Push(JsonElement payload)
        {
            TosuRealtimeTelemetry telemetry = _collector.Process(
                payload,
                "characterization",
                _start.AddSeconds(_sampleNumber++))!;
            Presentation.Submit(telemetry.Snapshot);
            return telemetry.Snapshot;
        }
    }

    private sealed class FakePresentation
    {
        public List<RealtimeAnalysisSnapshot> Published { get; } = [];

        public RealtimeAnalysisSnapshot? Pending
        {
            get;
            private set;
        }

        private bool IsVisible
        {
            get;
            set;
        } = true;

        public void Submit(RealtimeAnalysisSnapshot snapshot)
        {
            Pending = snapshot;
            if (IsVisible)
            {
                Flush();
            }
        }

        public void SetVisible(bool visible)
        {
            IsVisible = visible;
            if (visible)
            {
                Flush();
            }
        }

        public void ReplaceDocument()
        {
            Published.Clear();
        }

        private void Flush()
        {
            if (Pending is null)
            {
                return;
            }

            Published.Add(Pending);
            Pending = null;
        }
    }
}
