using System.Collections.Concurrent;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Core.Tests;

public sealed class AnalyzerExecutionPlannerTests
{
    [Fact]
    public void SupportedAlgorithmsPreserveCaseSensitiveIdentifiers()
    {
        var capabilities = new AnalyzerEngineCapabilities(
            supportedAlgorithms: ["mixed", "Mixed", "Mixed"]);

        Assert.Equal(new[] { "Mixed", "mixed" }, capabilities.SupportedAlgorithms.ToArray());
    }

    [Fact]
    public async Task AllowsAdvertisedCapabilitiesAndDoesNotInterpretVisualProfileAsEngineProfile()
    {
        var engine = CreateEngine(new AnalyzerEngineCapabilities(
            supportsProfiles: false,
            supportsMods: true,
            supportsRate: true,
            supportedAlgorithms: ["Mixed"]));
        using var coordinator = CreateCoordinator(engine);
        var request = CreateRequest(
            requestedAlgorithm: "Mixed",
            profileId: "visual-preset-not-known-to-engine",
            rate: 1.25,
            mods: ["DT"]);

        var result = await coordinator.AnalyzeAsync(request);

        Assert.Equal(AnalysisOutcome.Success, result.Outcome);
        Assert.Equal("Mixed", result.ActualAlgorithm);
        Assert.Equal(1, engine.CallCount);
    }

    [Fact]
    public async Task AllowsAnyAlgorithmWhenEngineDoesNotAdvertiseAnAlgorithmList()
    {
        var engine = CreateEngine(new AnalyzerEngineCapabilities());
        using var coordinator = CreateCoordinator(engine);
        var request = CreateRequest(requestedAlgorithm: "CustomCaseSensitiveAlgorithm");

        var result = await coordinator.AnalyzeAsync(request);

        Assert.Equal(AnalysisOutcome.Success, result.Outcome);
        Assert.Equal("CustomCaseSensitiveAlgorithm", result.ActualAlgorithm);
        Assert.Equal(1, engine.CallCount);
    }

    [Fact]
    public async Task RejectsRequestedAlgorithmWithUnsupportedCase()
    {
        var engine = CreateEngine(new AnalyzerEngineCapabilities(
            supportedAlgorithms: ["Mixed"]));
        var request = CreateRequest(requestedAlgorithm: "mixed");

        await AssertPlanningFailureAsync(engine, request, "case-sensitive algorithm 'mixed'");
    }

    [Fact]
    public async Task RejectsNonDefaultRateWhenEngineDoesNotSupportRateChanges()
    {
        var engine = CreateEngine(new AnalyzerEngineCapabilities(
            supportsRate: false,
            supportedAlgorithms: ["Mixed"]));
        var request = CreateRequest(rate: 1.25);

        await AssertPlanningFailureAsync(engine, request, "does not support rate changes");
    }

    [Fact]
    public async Task RejectsModsWhenEngineDoesNotSupportMods()
    {
        var engine = CreateEngine(new AnalyzerEngineCapabilities(
            supportsMods: false,
            supportedAlgorithms: ["Mixed"]));
        var request = CreateRequest(mods: ["DT"]);

        await AssertPlanningFailureAsync(engine, request, "does not support mods");
    }

    [Fact]
    public async Task RejectsSpeedRateOptionBecauseRequestRateIsCanonical()
    {
        var engine = CreateEngine(new AnalyzerEngineCapabilities(
            supportsRate: true,
            supportedAlgorithms: ["Mixed"]));
        var request = CreateRequest(
            rate: 1.25,
            options:
            [
                new KeyValuePair<string, JsonElement>(
                    AnalysisRequest.ReservedSpeedRateOptionName,
                    JsonSerializer.SerializeToElement(1.0))
            ]);

        await AssertPlanningFailureAsync(engine, request, "option 'speedRate' is reserved");
    }

    private static ImmediateEngine CreateEngine(AnalyzerEngineCapabilities capabilities)
    {
        return new ImmediateEngine(new AnalyzerEngineDescriptor(
            "engine",
            "Engine",
            "1.0",
            capabilities,
            supportedProfiles: ["engine-profile"]));
    }

    private static AnalyzerExecutionCoordinator CreateCoordinator(
        ImmediateEngine engine,
        IAnalysisDiagnostics? diagnostics = null)
    {
        return new AnalyzerExecutionCoordinator(
            new AnalyzerExecutionPlanner([engine]),
            diagnostics);
    }

    private static AnalysisRequest CreateRequest(
        string requestedAlgorithm = "Mixed",
        string profileId = "visual-preset",
        double rate = AnalysisRequest.DefaultRate,
        IEnumerable<string>? mods = null,
        IEnumerable<KeyValuePair<string, JsonElement>>? options = null)
    {
        return new AnalysisRequest(
            "engine",
            new BeatmapIdentity("map-a", "hash-map-a"),
            "osu file contents for map-a",
            requestedAlgorithm,
            profileId,
            rate,
            mods,
            options);
    }

    private static async Task AssertPlanningFailureAsync(
        ImmediateEngine engine,
        AnalysisRequest request,
        string expectedTechnicalDetails)
    {
        var diagnostics = new RecordingDiagnostics();
        using var coordinator = CreateCoordinator(engine, diagnostics);

        var result = await coordinator.AnalyzeAsync(request);

        Assert.Equal(AnalysisOutcome.Failed, result.Outcome);
        Assert.Null(result.ActualAlgorithm);
        Assert.Equal(0, engine.CallCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("analysis.plan_failed", diagnostic.Code);
        Assert.Contains(expectedTechnicalDetails, diagnostic.TechnicalDetails, StringComparison.Ordinal);
        Assert.Contains(diagnostics.Entries, entry => entry.Code == "analysis.plan_failed");
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

        public int CallCount
        {
            get;
            private set;
        }

        public Task<AnalysisResult> AnalyzeAsync(
            AnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new AnalysisResult(
                request.Key,
                Descriptor.Id,
                request.RequestedAlgorithm,
                request.RequestedAlgorithm));
        }
    }

    private sealed class RecordingDiagnostics : IAnalysisDiagnostics
    {
        public ConcurrentBag<AnalysisDiagnostic> Entries { get; } = [];

        public void Report(AnalysisDiagnostic diagnostic)
        {
            Entries.Add(diagnostic);
        }
    }
}
