using System.Collections.Concurrent;
using System.Text.Json;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Core.Tests;

public sealed class AnalyzerExecutionCoordinatorTests
{
    [Fact]
    public async Task DeduplicatesIdenticalInFlightRequestsAndFansOutResult()
    {
        var engine = new TestEngine("engine");
        using var coordinator = CreateCoordinator(engine);
        var request = new AnalysisRequest(
            "engine",
            new BeatmapIdentity("map-a", "hash-map-a"),
            "osu file contents for map-a",
            "Mixed",
            "default",
            rate: 1.0,
            mods: ["HD", "DT"],
            options:
            [
                JsonOption("ln", true),
                JsonOption("profile-option", "value")
            ]);
        var equivalentRequest = new AnalysisRequest(
            " ENGINE ",
            new BeatmapIdentity("map-a", "hash-map-a"),
            "osu file contents for map-a",
            "Mixed",
            "companella-widget",
            rate: 1.0,
            mods: ["dt", "hd"],
            options:
            [
                JsonOption("profile-option", "value"),
                JsonOption("ln", true)
            ]);

        Assert.NotEqual(request.ProfileId, equivalentRequest.ProfileId);
        Assert.Equal(request.Key, equivalentRequest.Key);

        var first = coordinator.AnalyzeAsync(request);
        var second = coordinator.AnalyzeAsync(equivalentRequest);
        var pending = await engine.WaitForCallAsync();

        Assert.Equal(1, engine.CallCount);
        pending.Complete(CreateResult(request, engine, "Sunny"));

        var results = await Task.WhenAll(first, second);

        Assert.Same(results[0], results[1]);
        Assert.Equal(request.Key, results[0].RequestKey);
    }

    [Fact]
    public async Task PreservesRequestedAndActualAlgorithm()
    {
        var engine = new TestEngine("engine");
        using var coordinator = CreateCoordinator(engine);
        var request = CreateRequest("engine", "map-a", requestedAlgorithm: "Mixed");
        var task = coordinator.AnalyzeAsync(request);
        var pending = await engine.WaitForCallAsync();

        pending.Complete(CreateResult(request, engine, "Roxy"));
        var result = await task;

        Assert.Equal("Mixed", result.RequestedAlgorithm);
        Assert.Equal("Roxy", result.ActualAlgorithm);
    }

    [Fact]
    public void RequestKeyPreservesCaseSensitiveAlgorithmAndOptionNames()
    {
        var original = CreateRequestWithOption("Mixed", "withPattern");
        var changedAlgorithmCase = CreateRequestWithOption("mixed", "withPattern");
        var changedOptionCase = CreateRequestWithOption("Mixed", "WITHPATTERN");

        Assert.NotEqual(original.Key, changedAlgorithmCase.Key);
        Assert.NotEqual(original.Key, changedOptionCase.Key);
    }

    [Fact]
    public void ExecutionKeyIncludesEngineUpstreamAndConfigurationVersions()
    {
        var versionOne = new TestEngine("engine", version: "1.0", upstreamVersion: "mma-1");
        var versionTwo = new TestEngine("engine", version: "2.0", upstreamVersion: "mma-1");
        var upstreamTwo = new TestEngine("engine", version: "1.0", upstreamVersion: "mma-2");
        var requestVersionOne = CreateRequest("engine", "map-a", configurationVersion: "config-1");
        var requestVersionTwo = CreateRequest("engine", "map-a", configurationVersion: "config-2");

        var firstKey = new AnalyzerExecutionPlanner([versionOne]).CreatePlan(requestVersionOne).ExecutionKey;
        var engineVersionKey = new AnalyzerExecutionPlanner([versionTwo]).CreatePlan(requestVersionOne).ExecutionKey;
        var upstreamVersionKey = new AnalyzerExecutionPlanner([upstreamTwo]).CreatePlan(requestVersionOne).ExecutionKey;
        var configurationVersionKey = new AnalyzerExecutionPlanner([versionOne]).CreatePlan(requestVersionTwo).ExecutionKey;

        Assert.NotEqual(firstKey, engineVersionKey);
        Assert.NotEqual(firstKey, upstreamVersionKey);
        Assert.NotEqual(firstKey, configurationVersionKey);
    }

    [Fact]
    public async Task NewBeatmapCancelsAndDiscardsStaleGeneration()
    {
        var engine = new TestEngine("engine");
        using var coordinator = CreateCoordinator(engine);
        var firstRequest = CreateRequest("engine", "map-a");
        var secondRequest = CreateRequest("engine", "map-b");

        var staleTask = coordinator.AnalyzeAsync(firstRequest);
        var staleCall = await engine.WaitForCallAsync();
        var currentTask = coordinator.AnalyzeAsync(secondRequest);
        var currentCall = await engine.WaitForCallAsync();

        await staleCall.WaitForCancellationAsync();
        Assert.True(staleCall.CancellationToken.IsCancellationRequested);

        currentCall.Complete(CreateResult(secondRequest, engine, "Sunny"));
        Assert.NotNull(await currentTask);

        staleCall.Complete(CreateResult(firstRequest, engine, "Sunny"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => staleTask);
    }

    [Fact]
    public async Task ChangedBeatmapContentCancelsPreviousGeneration()
    {
        var engine = new TestEngine("engine");
        using var coordinator = CreateCoordinator(engine);
        var beatmap = new BeatmapIdentity("map-a", "hash-map-a");
        var firstRequest = new AnalysisRequest(
            "engine",
            beatmap,
            "osu file contents - revision one",
            "Mixed",
            "default");
        var secondRequest = new AnalysisRequest(
            "engine",
            beatmap,
            "osu file contents - revision two",
            "Mixed",
            "default");

        var staleTask = coordinator.AnalyzeAsync(firstRequest);
        var staleCall = await engine.WaitForCallAsync();
        var currentTask = coordinator.AnalyzeAsync(secondRequest);
        var currentCall = await engine.WaitForCallAsync();

        await staleCall.WaitForCancellationAsync();
        Assert.True(staleCall.CancellationToken.IsCancellationRequested);

        currentCall.Complete(CreateResult(secondRequest, engine, "Sunny"));
        Assert.Equal(AnalysisOutcome.Success, (await currentTask).Outcome);

        staleCall.Complete(CreateResult(firstRequest, engine, "Sunny"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => staleTask);
    }

    [Fact]
    public async Task DifferentRatesAndModsShareBeatmapGenerationWithoutSupersession()
    {
        var engine = new TestEngine("engine");
        using var coordinator = CreateCoordinator(engine);
        var baseRequest = CreateSourceRevisionRequest(rate: 1.0, mods: ["HD"]);
        var rateRequest = CreateSourceRevisionRequest(rate: 1.1, mods: ["HD"]);
        var modsRequest = CreateSourceRevisionRequest(rate: 1.1, mods: ["DT"]);

        var baseTask = coordinator.AnalyzeAsync(baseRequest);
        var baseCall = await engine.WaitForCallAsync();
        var rateTask = coordinator.AnalyzeAsync(rateRequest);
        var rateCall = await engine.WaitForCallAsync();

        var modsTask = coordinator.AnalyzeAsync(modsRequest);
        var modsCall = await engine.WaitForCallAsync();

        Assert.False(baseCall.CancellationToken.IsCancellationRequested);
        Assert.False(rateCall.CancellationToken.IsCancellationRequested);
        Assert.False(modsCall.CancellationToken.IsCancellationRequested);

        baseCall.Complete(CreateResult(baseRequest, engine, "Mixed"));
        rateCall.Complete(CreateResult(rateRequest, engine, "Mixed"));
        modsCall.Complete(CreateResult(modsRequest, engine, "Mixed"));

        Assert.Equal(AnalysisOutcome.Success, (await baseTask).Outcome);
        Assert.Equal(AnalysisOutcome.Success, (await rateTask).Outcome);
        Assert.Equal(AnalysisOutcome.Success, (await modsTask).Outcome);
    }

    [Fact]
    public async Task DifferentAlgorithmsAndOptionsShareSourceRevisionWithoutSupersession()
    {
        var engine = new TestEngine("engine");
        using var coordinator = CreateCoordinator(engine);
        var mixedRequest = CreateSourceRevisionRequest(
            rate: 1.0,
            mods: ["HD"],
            algorithm: "Mixed",
            optionName: "withPattern");
        var sunnyRequest = CreateSourceRevisionRequest(
            rate: 1.0,
            mods: ["HD"],
            algorithm: "Sunny",
            optionName: "withLns");

        var mixedTask = coordinator.AnalyzeAsync(mixedRequest);
        var sunnyTask = coordinator.AnalyzeAsync(sunnyRequest);
        var mixedCall = await engine.WaitForCallAsync();
        var sunnyCall = await engine.WaitForCallAsync();

        Assert.False(mixedCall.CancellationToken.IsCancellationRequested);
        Assert.False(sunnyCall.CancellationToken.IsCancellationRequested);

        foreach (var call in new[] { mixedCall, sunnyCall })
        {
            var request = call.Request.RequestedAlgorithm == "Mixed"
                ? mixedRequest
                : sunnyRequest;
            var actualAlgorithm = request.RequestedAlgorithm == "Mixed"
                ? "Roxy"
                : "Sunny";
            call.Complete(CreateResult(request, engine, actualAlgorithm));
        }

        Assert.Equal("Roxy", (await mixedTask).ActualAlgorithm);
        Assert.Equal("Sunny", (await sunnyTask).ActualAlgorithm);
    }

    [Fact]
    public async Task SubscriberCancellationDoesNotCancelOtherSubscribers()
    {
        var engine = new TestEngine("engine");
        using var coordinator = CreateCoordinator(engine);
        var request = CreateRequest("engine", "map-a");
        using var subscriberCancellation = new CancellationTokenSource();

        var canceledSubscriber = coordinator.AnalyzeAsync(request, subscriberCancellation.Token);
        var activeSubscriber = coordinator.AnalyzeAsync(request);
        var pending = await engine.WaitForCallAsync();

        subscriberCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledSubscriber);
        Assert.False(pending.CancellationToken.IsCancellationRequested);

        pending.Complete(CreateResult(request, engine, "Sunny"));
        Assert.NotNull(await activeSubscriber);
    }

    [Fact]
    public async Task OneEngineFailureDoesNotCancelIndependentEngine()
    {
        var failingEngine = new TestEngine("failing");
        var healthyEngine = new TestEngine("healthy");
        var diagnostics = new RecordingDiagnostics();
        using var coordinator = new AnalyzerExecutionCoordinator(
            new AnalyzerExecutionPlanner([failingEngine, healthyEngine]),
            diagnostics);
        var failingRequest = CreateRequest("failing", "map-a");
        var healthyRequest = CreateRequest("healthy", "map-a");

        var failedTask = coordinator.AnalyzeAsync(failingRequest);
        var healthyTask = coordinator.AnalyzeAsync(healthyRequest);
        var failedCall = await failingEngine.WaitForCallAsync();
        var healthyCall = await healthyEngine.WaitForCallAsync();

        failedCall.Fail(new InvalidOperationException("test failure"));
        healthyCall.Complete(CreateResult(healthyRequest, healthyEngine, "Sunny"));

        var failedResult = await failedTask;
        var healthyResult = await healthyTask;

        Assert.True(failedResult.HasErrors);
        Assert.Equal(AnalysisOutcome.Failed, failedResult.Outcome);
        Assert.Null(failedResult.ActualAlgorithm);
        Assert.Contains(failedResult.Diagnostics, diagnostic => diagnostic.Code == "analysis.engine_failed");
        Assert.False(healthyResult.HasErrors);
        Assert.Contains(diagnostics.Entries, diagnostic => diagnostic.Code == "analysis.engine_failed");
    }

    [Fact]
    public async Task RejectsResultThatDoesNotEchoRequestedAlgorithm()
    {
        var engine = new TestEngine("engine");
        using var coordinator = CreateCoordinator(engine);
        var request = CreateRequest("engine", "map-a", requestedAlgorithm: "Mixed");
        var task = coordinator.AnalyzeAsync(request);
        var pending = await engine.WaitForCallAsync();

        pending.Complete(new AnalysisResult(
            request.Key,
            engine.Descriptor.Id,
            requestedAlgorithm: "Sunny",
            actualAlgorithm: "Sunny"));
        var result = await task;

        Assert.Equal(AnalysisOutcome.Failed, result.Outcome);
        Assert.Null(result.ActualAlgorithm);
        Assert.Equal("Mixed", result.RequestedAlgorithm);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "analysis.engine_failed");
    }

    [Fact]
    public async Task PlanningFailureHasFailedOutcomeWithoutActualAlgorithm()
    {
        var engine = new TestEngine("registered");
        using var coordinator = CreateCoordinator(engine);
        var request = CreateRequest("missing", "map-a");

        var result = await coordinator.AnalyzeAsync(request);

        Assert.Equal(AnalysisOutcome.Failed, result.Outcome);
        Assert.Null(result.ActualAlgorithm);
        Assert.Equal("Mixed", result.RequestedAlgorithm);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "analysis.plan_failed");
    }

    private static AnalyzerExecutionCoordinator CreateCoordinator(TestEngine engine)
    {
        return new AnalyzerExecutionCoordinator(new AnalyzerExecutionPlanner([engine]));
    }

    private static AnalysisRequest CreateRequest(
        string engineId,
        string mapId,
        string requestedAlgorithm = "Mixed",
        string configurationVersion = "1")
    {
        return new AnalysisRequest(
            engineId,
            new BeatmapIdentity(mapId, $"hash-{mapId}"),
            $"osu file contents for {mapId}",
            requestedAlgorithm,
            "default",
            rate: 1.0,
            mods: ["HD", "DT"],
            options: [JsonOption("ln", true)],
            configurationVersion: configurationVersion);
    }

    private static AnalysisRequest CreateRequestWithOption(string algorithm, string optionName)
    {
        return new AnalysisRequest(
            "engine",
            new BeatmapIdentity("map-a", "hash-map-a"),
            "osu file contents for map-a",
            algorithm,
            "default",
            options: [JsonOption(optionName, true)]);
    }

    private static AnalysisRequest CreateSourceRevisionRequest(
        double rate,
        IEnumerable<string> mods,
        string algorithm = "Mixed",
        string optionName = "withPattern")
    {
        return new AnalysisRequest(
            "engine",
            new BeatmapIdentity("map-a", "hash-map-a"),
            "osu file contents for map-a",
            algorithm,
            "widget",
            rate,
            mods,
            options: [JsonOption(optionName, true)]);
    }

    private static AnalysisResult CreateResult(
        AnalysisRequest request,
        TestEngine engine,
        string actualAlgorithm)
    {
        return new AnalysisResult(
            request.Key,
            engine.Descriptor.Id,
            request.RequestedAlgorithm,
            actualAlgorithm,
            [SemanticMetric.FromValue("difficulty.star", 5.25, unit: "SR")]);
    }

    private static KeyValuePair<string, JsonElement> JsonOption<T>(string key, T value)
    {
        return new KeyValuePair<string, JsonElement>(key, JsonSerializer.SerializeToElement(value));
    }

    private sealed class TestEngine : IAnalyzerEngine
    {
        private readonly ConcurrentQueue<PendingCall> _calls = new();
        private readonly SemaphoreSlim _callSignal = new(0);

        public TestEngine(
            string id,
            string version = "test",
            string upstreamVersion = "upstream-test")
        {
            Descriptor = new AnalyzerEngineDescriptor(
                id,
                id,
                version,
                new AnalyzerEngineCapabilities(
                    supportsProfiles: true,
                    supportsMods: true,
                    supportsRate: true,
                    supportsCancellation: true,
                    supportedAlgorithms: ["Mixed", "Sunny", "Roxy"],
                    supportedMetricIds: ["difficulty.star"]),
                upstreamVersion: upstreamVersion,
                maxConcurrency: 8,
                threadSafety: AnalyzerEngineThreadSafety.Concurrent);
        }

        public AnalyzerEngineDescriptor Descriptor
        {
            get;
        }

        public int CallCount
        {
            get; private set;
        }

        public Task<AnalysisResult> AnalyzeAsync(
            AnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            var call = new PendingCall(request, cancellationToken);
            _calls.Enqueue(call);
            CallCount++;
            _callSignal.Release();
            return call.Completion.Task;
        }

        public async Task<PendingCall> WaitForCallAsync()
        {
            await _callSignal.WaitAsync().ConfigureAwait(false);
            Assert.True(_calls.TryDequeue(out var call));
            return call!;
        }
    }

    private sealed class PendingCall
    {
        public PendingCall(AnalysisRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            CancellationToken = cancellationToken;
            cancellationToken.Register(() => CancellationObserved.TrySetResult(true));
        }

        public AnalysisRequest Request
        {
            get;
        }

        public TaskCompletionSource<AnalysisResult> Completion
        {
            get;
        } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> CancellationObserved
        {
            get;
        } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken CancellationToken
        {
            get;
        }

        public void Complete(AnalysisResult result) => Completion.TrySetResult(result);

        public void Fail(Exception exception) => Completion.TrySetException(exception);

        public Task WaitForCancellationAsync() => CancellationObserved.Task;
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
