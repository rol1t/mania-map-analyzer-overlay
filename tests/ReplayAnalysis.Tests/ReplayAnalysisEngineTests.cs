using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.ReplayAnalysis.Tests;

public sealed class ReplayAnalysisEngineTests
{
    [Fact]
    public async Task EngineExposesReplayMetricsWithProvenanceDiagnostics()
    {
        OsuManiaBeatmap beatmap = ParseRice((1000, 0), (1100, 1));
        var inputs = StableReplayDecoder.DecodeFrames([(1002, 1), (1010, 0), (1105, 1), (1110, 0)]);

        InMemoryReplayArtifactStore store = new();
        ReplayArtifactHandle handle = store.Create([0x01, 0x02], fileName: "test.osr");
        ReplayArtifact artifact = new(handle, ReplaySourceKind.StableOsr);

        ReplayAnalysisEngine engine = new(
            store,
            _ => inputs);

        string osu = BeatmapContent((1000, 0), (1100, 1));
        BeatmapIdentity identity = new("hash", "hash");
        var request = new AnalysisRequest(
            engine.Descriptor.Id,
            identity,
            osu,
            new AnalysisConfiguration("replay.rice", "1", new Dictionary<string, JsonElement>
            {
                ["replayArtifactId"] = JsonSerializer.SerializeToElement(handle.ArtifactId)
            }),
            profileId: "default");

        AnalysisResult result = await engine.AnalyzeAsync(request);

        Assert.Equal(AnalysisOutcome.Success, result.Outcome);
        Assert.Contains(result.Metrics.Keys, key => key == ReplayMetrics.TimingUr);
        Assert.Contains(result.Metrics.Keys, key => key == ReplayMetrics.ColumnBias(0));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "replay.not_found");
        _ = artifact;
    }

    [Fact]
    public async Task EngineSurfacesLnUnsupportedAsFailed()
    {
        InMemoryReplayArtifactStore store = new();
        ReplayArtifactHandle handle = store.Create([0x01], fileName: "ln.osr");

        ReplayAnalysisEngine engine = new(store, _ => []);
        string osu = """
            [Difficulty]
            CircleSize:4
            [HitObjects]
            64,192,1000,128,0,1500:0:0:0:0:
            """;
        BeatmapIdentity identity = new("hash-ln", "hash-ln");
        var request = new AnalysisRequest(
            engine.Descriptor.Id,
            identity,
            osu,
            new AnalysisConfiguration("replay.rice", "1", new Dictionary<string, JsonElement>
            {
                ["replayArtifactId"] = JsonSerializer.SerializeToElement(handle.ArtifactId)
            }),
            profileId: "default");

        AnalysisResult result = await engine.AnalyzeAsync(request);
        Assert.Equal(AnalysisOutcome.Failed, result.Outcome);
        Assert.Contains(result.Diagnostics, item => item.Code == "replay.ln_not_supported");
    }

    [Fact]
    public async Task EngineAllowsCombiningReplayWithMapMetricsViaComposition()
    {
        // WidgetAnalysisComposition already allows multiple sources; verify replay engine
        // metrics coexist with a map engine's metrics under the semantic contract.
        InMemoryReplayArtifactStore store = new();
        ReplayArtifactHandle handle = store.Create([0x01], fileName: "combo.osr");
        var inputs = StableReplayDecoder.DecodeFrames([(1002, 1), (1010, 0)]);

        ReplayAnalysisEngine replayEngine = new(store, _ => inputs);

        var fakeMapEngine = new FakeMapEngine();
        string osu = BeatmapContent((1000, 0));
        BeatmapIdentity identity = new("hash2", "hash2");

        var replayRequest = new AnalysisRequest(
            replayEngine.Descriptor.Id,
            identity,
            osu,
            new AnalysisConfiguration("replay.rice", "1", new Dictionary<string, JsonElement>
            {
                ["replayArtifactId"] = JsonSerializer.SerializeToElement(handle.ArtifactId)
            }),
            profileId: "default");

        var mapRequest = new AnalysisRequest(
            fakeMapEngine.Descriptor.Id,
            identity,
            osu,
            new AnalysisConfiguration("Mixed", "1"),
            profileId: "default");

        AnalysisResult replayResult = await replayEngine.AnalyzeAsync(replayRequest);
        AnalysisResult mapResult = await fakeMapEngine.AnalyzeAsync(mapRequest);

        Assert.Contains(replayResult.Metrics.Keys, key => key.StartsWith("replay.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mapResult.Metrics.Keys, key => key == "difficulty.star");
    }

    private static string BeatmapContent(params (int time, int column)[] notes)
    {
        string lines = string.Join("\n", notes.Select(item => $"{item.column * 128 + 64},192,{item.time},1,0,0:0:0:0:"));
        return $"""
            [Difficulty]
            CircleSize:4
            [HitObjects]
            {lines}
            """;
    }

    private static OsuManiaBeatmap ParseRice(params (int time, int column)[] notes)
    {
        string osu = BeatmapContent(notes);
        return OsuBeatmapParser.Parse(osu, "hash");
    }

    private sealed class FakeMapEngine : IAnalyzerEngine
    {
        public AnalyzerEngineDescriptor Descriptor
        {
            get;
        } = new(
            id: "map.analyser",
            name: "ManiaMapAnalyser",
            version: "2.0.0",
            capabilities: new AnalyzerEngineCapabilities(
                supportedAlgorithms: ["Mixed"],
                supportedMetricIds: ["difficulty.star"]));

        public Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken = default)
        {
            SemanticMetric metric = SemanticMetric.FromValue("difficulty.star", 5.42);
            return Task.FromResult(new AnalysisResult(
                request.Key,
                Descriptor.Id,
                request.RequestedAlgorithm,
                actualAlgorithm: "Mixed",
                metrics: [metric]));
        }
    }
}
