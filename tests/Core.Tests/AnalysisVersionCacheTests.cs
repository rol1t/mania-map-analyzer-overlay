using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Core.Tests;

public sealed class AnalysisVersionCacheTests
{
    [Fact]
    public void ExecutionKeyChangesWhenEngineVersionChanges()
    {
        var request = CreateRequest();
        var planV1 = CreatePlan(request, version: "1.0.0", upstreamVersion: "2.0.0");
        var planV2 = CreatePlan(request, version: "1.0.1", upstreamVersion: "2.0.0");
        Assert.NotEqual(planV1.ExecutionKey.Value, planV2.ExecutionKey.Value);
    }

    [Fact]
    public void ExecutionKeyChangesWhenUpstreamVersionChanges()
    {
        var request = CreateRequest();
        var planA = CreatePlan(request, version: "1.0.0", upstreamVersion: "2.0.0");
        var planB = CreatePlan(request, version: "1.0.0", upstreamVersion: "2.0.1");
        Assert.NotEqual(planA.ExecutionKey.Value, planB.ExecutionKey.Value);
    }

    [Fact]
    public void ExecutionKeyChangesWhenConfigurationVersionChanges()
    {
        var requestV1 = CreateRequest(configurationVersion: "1");
        var requestV2 = CreateRequest(configurationVersion: "2");
        var planV1 = CreatePlan(requestV1);
        var planV2 = CreatePlan(requestV2);
        Assert.NotEqual(planV1.ExecutionKey.Value, planV2.ExecutionKey.Value);
    }

    [Fact]
    public void ExecutionKeyIsStableForIdenticalEffectiveConfiguration()
    {
        var requestA = CreateRequest(configurationVersion: "1");
        var requestB = CreateRequest(configurationVersion: "1");
        var planA = CreatePlan(requestA);
        var planB = CreatePlan(requestB);
        Assert.Equal(planA.ExecutionKey.Value, planB.ExecutionKey.Value);
    }

    [Fact]
    public void RequestKeyIsIndependentFromEngineVersion()
    {
        var request = CreateRequest();
        var planV1 = CreatePlan(request, version: "1.0.0");
        var planV2 = CreatePlan(request, version: "9.9.9");
        Assert.Equal(request.Key.Value, CreateRequest().Key.Value);
        Assert.NotEqual(planV1.ExecutionKey.Value, planV2.ExecutionKey.Value);
    }

    [Fact]
    public void EffectiveConfigurationOptionsAffectCacheIdentity()
    {
        var requestDefault = CreateRequest(options: null);
        var requestWithOption = CreateRequest(options: new Dictionary<string, JsonElement>
        {
            ["withEtterna"] = JsonSerializer.SerializeToElement(true)
        });
        var planDefault = CreatePlan(requestDefault);
        var planWithOption = CreatePlan(requestWithOption);
        Assert.NotEqual(planDefault.ExecutionKey.Value, planWithOption.ExecutionKey.Value);
    }

    private static AnalyzerExecutionPlan CreatePlan(
        AnalysisRequest request,
        string version = "1.0.0",
        string upstreamVersion = "2.0.0")
    {
        var descriptor = CreateDescriptor(version, upstreamVersion);
        var engine = new ImmediateEngine(descriptor);
        var planner = new AnalyzerExecutionPlanner([engine]);
        return planner.CreatePlan(request);
    }

    private static AnalysisRequest CreateRequest(
        string configurationVersion = "1",
        Dictionary<string, JsonElement>? options = null)
    {
        IEnumerable<KeyValuePair<string, JsonElement>>? enumerable = options;
        return new AnalysisRequest(
            "test-engine",
            new BeatmapIdentity("map-1", "hash-1"),
            "osu file content",
            "Mixed",
            "profile-1",
            configurationVersion: configurationVersion,
            options: enumerable);
    }

    private static AnalyzerEngineDescriptor CreateDescriptor(
        string version = "1.0.0",
        string upstreamVersion = "2.0.0")
    {
        return new AnalyzerEngineDescriptor(
            "test-engine",
            "Test Engine",
            version,
            new AnalyzerEngineCapabilities(
                supportedAlgorithms: ["Mixed", "Sunny"],
                supportedMetricIds: ["difficulty.star"]),
            upstreamVersion: upstreamVersion);
    }

    private sealed class ImmediateEngine : IAnalyzerEngine
    {
        public ImmediateEngine(AnalyzerEngineDescriptor descriptor)
        {
            Descriptor = descriptor;
        }

        public AnalyzerEngineDescriptor Descriptor
        {
            get;
        }

        public Task<AnalysisResult> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AnalysisResult(request.Key, Descriptor.Id, request.RequestedAlgorithm, request.RequestedAlgorithm));
        }
    }
}
