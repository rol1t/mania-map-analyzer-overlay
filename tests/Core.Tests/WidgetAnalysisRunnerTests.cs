using System.Collections.Concurrent;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Core.Tests;

public sealed class WidgetAnalysisRunnerTests
{
    [Fact]
    public async Task ExecutesDifferentEnginesConcurrentlyAndComposesSnapshot()
    {
        var mmaEngine = new ControlledEngine("mma-engine", "2.0", "mma-5");
        var lnEngine = new ControlledEngine("ln-engine", "1.0", "ln-2");
        using var coordinator = CreateCoordinator(mmaEngine, lnEngine);
        using var runner = new WidgetAnalysisRunner(coordinator);
        var mma = CreateSource("mma", mmaEngine, requestedAlgorithm: "Mixed");
        var ln = CreateSource("ln", lnEngine, requestedAlgorithm: "LnRank");
        var spec = new WidgetAnalysisSpec(
            "combined-widget",
            [mma, ln],
            [
                Binding("difficulty.star", ("mma", "difficulty.star")),
                Binding("difficulty.ln", ("ln", "difficulty.ln"))
            ]);

        var runTask = runner.RunAsync(spec);
        var mmaCall = await mmaEngine.WaitForCallAsync();
        var lnCall = await lnEngine.WaitForCallAsync();

        Assert.Equal(1, mmaEngine.CallCount);
        Assert.Equal(1, lnEngine.CallCount);
        mmaCall.Complete(Result(mmaCall.Request, mmaEngine, "Roxy", "difficulty.star", 6.2));
        lnCall.Complete(Result(lnCall.Request, lnEngine, "LnRank", "difficulty.ln", 7.0));

        var snapshot = await runTask;

        Assert.Equal(AnalysisOutcome.Success, snapshot.Outcome);
        Assert.Equal(6.2, snapshot.Metrics["difficulty.star"].Metric.Value.GetDouble());
        Assert.Equal(7.0, snapshot.Metrics["difficulty.ln"].Metric.Value.GetDouble());
    }

    [Fact]
    public async Task ComposesSourcesWithDifferentRatesAndModsWithoutCancellingEitherSource()
    {
        var engine = new ControlledEngine("shared-engine", "1.0", "shared-1");
        using var coordinator = CreateCoordinator(engine);
        using var runner = new WidgetAnalysisRunner(coordinator);
        var baseSource = CreateSourceWithRevision(
            "base-rate",
            engine,
            rate: 1.0,
            mods: ["HD"],
            requestedAlgorithm: "Mixed");
        var modifiedSource = CreateSourceWithRevision(
            "modified-rate",
            engine,
            rate: 1.25,
            mods: ["DT"],
            requestedAlgorithm: "Mixed");
        var spec = new WidgetAnalysisSpec(
            "mixed-revision-widget",
            [baseSource, modifiedSource],
            [
                Binding("difficulty.base", ("base-rate", "difficulty.star")),
                Binding("difficulty.modified", ("modified-rate", "difficulty.star"))
            ]);

        var runTask = runner.RunAsync(spec);
        var firstCall = await engine.WaitForCallAsync();
        var secondCall = await engine.WaitForCallAsync();

        Assert.False(firstCall.CancellationToken.IsCancellationRequested);
        Assert.False(secondCall.CancellationToken.IsCancellationRequested);
        Assert.NotEqual(firstCall.Request.Key, secondCall.Request.Key);

        var firstSourceResult = firstCall.Request.Rate == 1.0 ? firstCall : secondCall;
        var secondSourceResult = firstCall.Request.Rate == 1.0 ? secondCall : firstCall;
        firstSourceResult.Complete(Result(
            firstSourceResult.Request,
            engine,
            "Roxy",
            "difficulty.star",
            5.2));
        secondSourceResult.Complete(Result(
            secondSourceResult.Request,
            engine,
            "Roxy",
            "difficulty.star",
            7.4));

        var snapshot = await runTask;

        Assert.Equal(AnalysisOutcome.Success, snapshot.Outcome);
        Assert.Equal(5.2, snapshot.Metrics["difficulty.base"].Metric.Value.GetDouble());
        Assert.Equal("base-rate", snapshot.Metrics["difficulty.base"].Provenance.SourceId);
        Assert.Equal("difficulty.star", snapshot.Metrics["difficulty.base"].Provenance.SourceMetricId);
        Assert.Equal("shared-engine", snapshot.Metrics["difficulty.base"].Provenance.EngineId);
        Assert.Equal("config-1", snapshot.Metrics["difficulty.base"].Provenance.ConfigurationVersion);
        Assert.Equal(7.4, snapshot.Metrics["difficulty.modified"].Metric.Value.GetDouble());
        Assert.Equal("modified-rate", snapshot.Metrics["difficulty.modified"].Provenance.SourceId);
        Assert.Equal("difficulty.star", snapshot.Metrics["difficulty.modified"].Provenance.SourceMetricId);
        Assert.Equal("shared-engine", snapshot.Metrics["difficulty.modified"].Provenance.EngineId);
        Assert.Equal("Roxy", snapshot.Metrics["difficulty.modified"].Provenance.ActualAlgorithm);
        Assert.Equal(1.0, baseSource.Request.Rate);
        Assert.Equal("HD", Assert.Single(baseSource.Request.Mods));
        Assert.Equal(1.25, modifiedSource.Request.Rate);
        Assert.Equal("DT", Assert.Single(modifiedSource.Request.Mods));
    }

    [Fact]
    public async Task NewerWidgetRunSuppressesOlderSnapshotAfterRateAndModsChange()
    {
        var engine = new ControlledEngine("shared-engine", "1.0", "shared-1");
        using var coordinator = CreateCoordinator(engine);
        using var runner = new WidgetAnalysisRunner(coordinator);
        var oldSource = CreateSourceWithRevision(
            "source",
            engine,
            rate: 1.0,
            mods: ["HD"]);
        var newSource = CreateSourceWithRevision(
            "source",
            engine,
            rate: 1.25,
            mods: ["DT"]);
        var oldSpec = SingleMetricSpec("live-widget", oldSource, "difficulty.star");
        var newSpec = SingleMetricSpec("live-widget", newSource, "difficulty.star");
        var published = new ConcurrentBag<ComposedWidgetSnapshot>();
        runner.SnapshotComposed += published.Add;

        var oldRun = runner.RunAsync(oldSpec);
        var oldCall = await engine.WaitForCallAsync();
        var newRun = runner.RunAsync(newSpec);
        var newCall = await engine.WaitForCallAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldRun);
        Assert.False(oldCall.CancellationToken.IsCancellationRequested);

        newCall.Complete(Result(newCall.Request, engine, "Roxy", "difficulty.star", 7.4));
        var currentSnapshot = await newRun;

        oldCall.Complete(Result(oldCall.Request, engine, "Roxy", "difficulty.star", 5.2));

        var publishedSnapshot = Assert.Single(published);
        Assert.Same(currentSnapshot, publishedSnapshot);
        Assert.Equal(7.4, publishedSnapshot.Metrics["difficulty.star"].Metric.Value.GetDouble());
    }

    [Fact]
    public async Task ExplicitSceneGenerationCanBeSharedBySeveralWidgets()
    {
        var engine = new ControlledEngine("shared-engine", "1.0", "shared-1");
        using var coordinator = CreateCoordinator(engine);
        using var runner = new WidgetAnalysisRunner(coordinator);
        using var sceneScope = new AnalysisRunScope("main-scene");
        var generation = sceneScope.BeginGeneration();
        var request = CreateRequest(engine.Descriptor.Id, requestedAlgorithm: "Mixed");
        var firstSpec = SingleMetricSpec(
            "first-widget",
            new AnalysisSourceSpec("first-source", request, engine.Descriptor),
            "difficulty.star");
        var secondSpec = SingleMetricSpec(
            "second-widget",
            new AnalysisSourceSpec("second-source", request, engine.Descriptor),
            "difficulty.star");

        var firstRun = runner.RunAsync(firstSpec, generation);
        var secondRun = runner.RunAsync(secondSpec, generation);
        var call = await engine.WaitForCallAsync();

        call.Complete(Result(call.Request, engine, "Roxy", "difficulty.star", 6.1));
        var snapshots = await Task.WhenAll(firstRun, secondRun);

        Assert.True(generation.IsCurrent);
        Assert.Equal(2, snapshots.Length);
        Assert.All(
            snapshots,
            snapshot => Assert.Equal(
                6.1,
                snapshot.Metrics["difficulty.star"].Metric.Value.GetDouble()));
        Assert.Equal(1, engine.CallCount);
    }

    [Fact]
    public async Task ExplicitStaleGenerationIsRejectedBeforeStartingAnEngineCall()
    {
        var engine = new ControlledEngine("shared-engine", "1.0", "shared-1");
        using var coordinator = CreateCoordinator(engine);
        using var runner = new WidgetAnalysisRunner(coordinator);
        using var sceneScope = new AnalysisRunScope("main-scene");
        var staleGeneration = sceneScope.BeginGeneration();
        var currentGeneration = sceneScope.BeginGeneration();
        var source = CreateSource("source", engine);
        var spec = SingleMetricSpec("widget", source, "difficulty.star");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(spec, staleGeneration));

        Assert.False(staleGeneration.IsCurrent);
        Assert.True(currentGeneration.IsCurrent);
        Assert.Equal(0, engine.CallCount);
    }

    [Fact]
    public async Task IdenticalRequestsAcrossWidgetsShareOneEngineCall()
    {
        var engine = new ControlledEngine("shared-engine", "1.0", "shared-1");
        using var coordinator = CreateCoordinator(engine);
        using var runner = new WidgetAnalysisRunner(coordinator);
        var request = CreateRequest(engine.Descriptor.Id, requestedAlgorithm: "Mixed");
        var firstSource = new AnalysisSourceSpec("first-source", request, engine.Descriptor);
        var secondSource = new AnalysisSourceSpec("second-source", request, engine.Descriptor);
        var firstSpec = SingleMetricSpec("first-widget", firstSource, "difficulty.star");
        var secondSpec = SingleMetricSpec("second-widget", secondSource, "difficulty.star");

        var firstRun = runner.RunAsync(firstSpec);
        var secondRun = runner.RunAsync(secondSpec);
        var call = await engine.WaitForCallAsync();

        Assert.Equal(1, engine.CallCount);
        call.Complete(Result(call.Request, engine, "Roxy", "difficulty.star", 5.8));
        var snapshots = await Task.WhenAll(firstRun, secondRun);

        Assert.Equal("first-source", snapshots[0].Metrics["difficulty.star"].Provenance.SourceId);
        Assert.Equal("second-source", snapshots[1].Metrics["difficulty.star"].Provenance.SourceId);
        Assert.Equal(1, engine.CallCount);
    }

    [Fact]
    public async Task CancellationPropagatesWithoutPublishingSnapshot()
    {
        var engine = new ControlledEngine("engine", "1.0", "upstream-1");
        using var coordinator = CreateCoordinator(engine);
        using var runner = new WidgetAnalysisRunner(coordinator);
        var source = CreateSource("source", engine);
        var spec = SingleMetricSpec("cancelled-widget", source, "difficulty.star");
        using var cancellation = new CancellationTokenSource();
        var published = new ConcurrentBag<ComposedWidgetSnapshot>();
        runner.SnapshotComposed += published.Add;

        var runTask = runner.RunAsync(spec, cancellation.Token);
        await engine.WaitForCallAsync();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Empty(published);
    }

    [Fact]
    public async Task SourceOrderDoesNotChangeResultMapping()
    {
        var firstEngine = new ControlledEngine("first-engine", "1.0", "first-1");
        var secondEngine = new ControlledEngine("second-engine", "1.0", "second-1");
        using var coordinator = CreateCoordinator(firstEngine, secondEngine);
        using var runner = new WidgetAnalysisRunner(coordinator);
        var first = CreateSource("first", firstEngine, requestedAlgorithm: "First");
        var second = CreateSource("second", secondEngine, requestedAlgorithm: "Second");
        var spec = new WidgetAnalysisSpec(
            "reordered-widget",
            [second, first],
            [
                Binding("metric.first", ("first", "source.value")),
                Binding("metric.second", ("second", "source.value"))
            ]);

        var runTask = runner.RunAsync(spec);
        var firstCall = await firstEngine.WaitForCallAsync();
        var secondCall = await secondEngine.WaitForCallAsync();
        secondCall.Complete(Result(secondCall.Request, secondEngine, "Second", "source.value", 2));
        firstCall.Complete(Result(firstCall.Request, firstEngine, "First", "source.value", 1));

        var snapshot = await runTask;

        Assert.Equal(1, snapshot.Metrics["metric.first"].Metric.Value.GetInt32());
        Assert.Equal("first", snapshot.Metrics["metric.first"].Provenance.SourceId);
        Assert.Equal(2, snapshot.Metrics["metric.second"].Metric.Value.GetInt32());
        Assert.Equal("second", snapshot.Metrics["metric.second"].Provenance.SourceId);
    }

    private static AnalyzerExecutionCoordinator CreateCoordinator(params ControlledEngine[] engines)
    {
        return new AnalyzerExecutionCoordinator(new AnalyzerExecutionPlanner(engines));
    }

    private static AnalysisSourceSpec CreateSource(
        string sourceId,
        ControlledEngine engine,
        string requestedAlgorithm = "Mixed")
    {
        var request = CreateRequest(engine.Descriptor.Id, requestedAlgorithm);
        return new AnalysisSourceSpec(sourceId, request, engine.Descriptor);
    }

    private static AnalysisSourceSpec CreateSourceWithRevision(
        string sourceId,
        ControlledEngine engine,
        double rate,
        IEnumerable<string> mods,
        string requestedAlgorithm = "Mixed")
    {
        var request = new AnalysisRequest(
            engine.Descriptor.Id,
            new BeatmapIdentity("map-a", "hash-map-a"),
            "osu file contents",
            requestedAlgorithm,
            "widget-profile",
            rate,
            mods,
            configurationVersion: "config-1");
        return new AnalysisSourceSpec(sourceId, request, engine.Descriptor);
    }

    private static AnalysisRequest CreateRequest(
        string engineId,
        string requestedAlgorithm)
    {
        return new AnalysisRequest(
            engineId,
            new BeatmapIdentity("map-a", "hash-map-a"),
            "osu file contents",
            requestedAlgorithm,
            "widget-profile",
            configurationVersion: "config-1");
    }

    private static WidgetAnalysisSpec SingleMetricSpec(
        string widgetId,
        AnalysisSourceSpec source,
        string metricId)
    {
        return new WidgetAnalysisSpec(
            widgetId,
            [source],
            [Binding(metricId, (source.SourceId, metricId))]);
    }

    private static WidgetMetricBinding Binding(
        string targetMetricId,
        params (string SourceId, string MetricId)[] candidates)
    {
        return new WidgetMetricBinding(
            targetMetricId,
            candidates.Select(candidate => new SourceMetricCandidate(candidate.SourceId, candidate.MetricId)));
    }

    private static AnalysisResult Result<T>(
        AnalysisRequest request,
        ControlledEngine engine,
        string actualAlgorithm,
        string metricId,
        T value)
    {
        return new AnalysisResult(
            request.Key,
            engine.Descriptor.Id,
            request.RequestedAlgorithm,
            actualAlgorithm,
            [SemanticMetric.FromValue(metricId, value)]);
    }

    private sealed class ControlledEngine : IAnalyzerEngine
    {
        private readonly ConcurrentQueue<PendingCall> _calls = new();
        private readonly SemaphoreSlim _callSignal = new(0);
        private int _callCount;

        public ControlledEngine(string id, string version, string upstreamVersion)
        {
            Descriptor = new AnalyzerEngineDescriptor(
                id,
                id,
                version,
                new AnalyzerEngineCapabilities(
                    supportsMods: true,
                    supportsRate: true),
                upstreamVersion: upstreamVersion,
                threadSafety: AnalyzerEngineThreadSafety.Concurrent,
                maxConcurrency: 4);
        }

        public AnalyzerEngineDescriptor Descriptor
        {
            get;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<AnalysisResult> AnalyzeAsync(
            AnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            var call = new PendingCall(request, cancellationToken);
            _calls.Enqueue(call);
            Interlocked.Increment(ref _callCount);
            _callSignal.Release();
            return call.Completion.Task;
        }

        public async Task<PendingCall> WaitForCallAsync()
        {
            await _callSignal.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.True(_calls.TryDequeue(out var call));
            return call!;
        }
    }

    private sealed class PendingCall
    {
        private readonly CancellationTokenRegistration _cancellationRegistration;

        public PendingCall(AnalysisRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            CancellationToken = cancellationToken;
            _cancellationRegistration = cancellationToken.Register(
                () => Completion.TrySetCanceled(cancellationToken));
        }

        public AnalysisRequest Request
        {
            get;
        }

        public CancellationToken CancellationToken
        {
            get;
        }

        public TaskCompletionSource<AnalysisResult> Completion
        {
            get;
        } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(AnalysisResult result)
        {
            _cancellationRegistration.Dispose();
            Completion.TrySetResult(result);
        }
    }
}
