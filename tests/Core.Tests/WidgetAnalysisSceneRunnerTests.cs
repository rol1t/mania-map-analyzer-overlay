using System.Collections.Concurrent;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Core.Tests;

public sealed class WidgetAnalysisSceneRunnerTests
{
    [Fact]
    public void SceneSpecRejectsDuplicateWidgetIds()
    {
        var engine = new ControlledEngine("engine");
        var source = CreateSource("source", engine);
        var first = SingleMetricWidget("duplicate", source, "difficulty.star");
        var second = SingleMetricWidget("duplicate", source, "difficulty.star");

        var exception = Assert.Throws<ArgumentException>(
            () => new WidgetAnalysisSceneSpec("scene", [first, second]));

        Assert.Contains("duplicate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedRequestExecutesOnceAndPreservesWidgetOrderAndProvenance()
    {
        var engine = new ControlledEngine("shared-engine");
        using var coordinator = CreateCoordinator(engine);
        using var runner = new WidgetAnalysisSceneRunner(coordinator);
        var request = CreateRequest(engine, rate: 1.0, mods: ["HD"]);
        var firstSource = new AnalysisSourceSpec("z-source", request, engine.Descriptor);
        var secondSource = new AnalysisSourceSpec("a-source", request, engine.Descriptor);
        var spec = new WidgetAnalysisSceneSpec(
            "ordered-scene",
            [
                SingleMetricWidget("z-widget", firstSource, "difficulty.star"),
                SingleMetricWidget("a-widget", secondSource, "difficulty.star")
            ]);

        var runTask = runner.RunAsync(spec);
        var call = await engine.WaitForCallAsync();

        Assert.Equal(1, engine.CallCount);
        call.Complete(Success(call.Request, engine, "difficulty.star", 6.3));
        var snapshot = await runTask;

        Assert.Equal(["z-widget", "a-widget"], snapshot.OrderedSnapshots.Select(widget => widget.WidgetId));
        Assert.Equal(2, snapshot.SnapshotsByWidgetId.Count);
        Assert.Equal(
            "z-source",
            snapshot.SnapshotsByWidgetId["z-widget"].Metrics["difficulty.star"].Provenance.SourceId);
        Assert.Equal(
            "a-source",
            snapshot.SnapshotsByWidgetId["a-widget"].Metrics["difficulty.star"].Provenance.SourceId);
        Assert.Equal(
            "shared-engine",
            snapshot.SnapshotsByWidgetId["a-widget"].Metrics["difficulty.star"].Provenance.EngineId);
    }

    [Fact]
    public async Task WidgetCanComposeTwoEnginesInsideScene()
    {
        var starEngine = new ControlledEngine("star-engine");
        var lnEngine = new ControlledEngine("ln-engine");
        using var coordinator = CreateCoordinator(starEngine, lnEngine);
        using var runner = new WidgetAnalysisSceneRunner(coordinator);
        var starSource = CreateSource("star-source", starEngine, algorithm: "Mixed");
        var lnSource = CreateSource("ln-source", lnEngine, algorithm: "LnRank");
        var widget = new WidgetAnalysisSpec(
            "combined-widget",
            [starSource, lnSource],
            [
                Binding("difficulty.star", ("star-source", "difficulty.star")),
                Binding("difficulty.ln", ("ln-source", "difficulty.ln"))
            ]);
        var spec = new WidgetAnalysisSceneSpec("combined-scene", [widget]);

        var runTask = runner.RunAsync(spec);
        var starCall = await starEngine.WaitForCallAsync();
        var lnCall = await lnEngine.WaitForCallAsync();

        lnCall.Complete(Success(lnCall.Request, lnEngine, "difficulty.ln", 7.2));
        starCall.Complete(Success(starCall.Request, starEngine, "difficulty.star", 5.9));
        var snapshot = await runTask;
        var combined = snapshot.SnapshotsByWidgetId["combined-widget"];

        Assert.Equal(AnalysisOutcome.Success, combined.Outcome);
        Assert.Equal(5.9, combined.Metrics["difficulty.star"].Metric.Value.GetDouble());
        Assert.Equal("star-engine", combined.Metrics["difficulty.star"].Provenance.EngineId);
        Assert.Equal(7.2, combined.Metrics["difficulty.ln"].Metric.Value.GetDouble());
        Assert.Equal("ln-source", combined.Metrics["difficulty.ln"].Provenance.SourceId);
    }

    [Fact]
    public async Task AnalyzerFailureDoesNotPreventUnrelatedWidgetSnapshot()
    {
        var failingEngine = new ControlledEngine("failing-engine");
        var healthyEngine = new ControlledEngine("healthy-engine");
        using var coordinator = CreateCoordinator(failingEngine, healthyEngine);
        using var runner = new WidgetAnalysisSceneRunner(coordinator);
        var failingSource = CreateSource("failing-source", failingEngine);
        var healthySource = CreateSource("healthy-source", healthyEngine);
        var spec = new WidgetAnalysisSceneSpec(
            "resilient-scene",
            [
                SingleMetricWidget("failing-widget", failingSource, "difficulty.star"),
                SingleMetricWidget("healthy-widget", healthySource, "difficulty.star")
            ]);

        var runTask = runner.RunAsync(spec);
        var failingCall = await failingEngine.WaitForCallAsync();
        var healthyCall = await healthyEngine.WaitForCallAsync();

        failingCall.Complete(AnalysisResult.Failure(
            failingCall.Request,
            failingEngine.Descriptor,
            AnalysisDiagnostic.Error("test.failure", "Expected analyzer failure.")));
        healthyCall.Complete(Success(
            healthyCall.Request,
            healthyEngine,
            "difficulty.star",
            6.0));
        var snapshot = await runTask;

        Assert.Equal(
            AnalysisOutcome.Failed,
            snapshot.SnapshotsByWidgetId["failing-widget"].Outcome);
        Assert.Equal(
            AnalysisOutcome.Success,
            snapshot.SnapshotsByWidgetId["healthy-widget"].Outcome);
        Assert.Equal(
            6.0,
            snapshot.SnapshotsByWidgetId["healthy-widget"]
                .Metrics["difficulty.star"]
                .Metric.Value.GetDouble());
    }

    [Fact]
    public async Task NewerSceneGenerationInvalidatesOlderSceneWithoutPartialPublication()
    {
        var engine = new ControlledEngine("engine");
        using var coordinator = CreateCoordinator(engine);
        using var runner = new WidgetAnalysisSceneRunner(coordinator);
        var oldSource = CreateSource("source", engine, rate: 1.0, mods: ["HD"]);
        var newSource = CreateSource("source", engine, rate: 1.25, mods: ["DT"]);
        var oldSpec = new WidgetAnalysisSceneSpec(
            "live-scene",
            [SingleMetricWidget("widget", oldSource, "difficulty.star")]);
        var newSpec = new WidgetAnalysisSceneSpec(
            "live-scene",
            [SingleMetricWidget("widget", newSource, "difficulty.star")]);
        var published = new ConcurrentBag<WidgetAnalysisSceneSnapshot>();
        runner.SnapshotComposed += published.Add;

        var oldRun = runner.RunAsync(oldSpec);
        var oldCall = await engine.WaitForCallAsync();
        var newRun = runner.RunAsync(newSpec);
        var newCall = await engine.WaitForCallAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldRun);
        Assert.False(oldCall.CancellationToken.IsCancellationRequested);

        newCall.Complete(Success(newCall.Request, engine, "difficulty.star", 7.5));
        var currentSnapshot = await newRun;
        oldCall.Complete(Success(oldCall.Request, engine, "difficulty.star", 5.0));

        var publishedSnapshot = Assert.Single(published);
        Assert.Same(currentSnapshot, publishedSnapshot);
        Assert.Equal(2, publishedSnapshot.Generation);
        Assert.Equal(
            7.5,
            publishedSnapshot.SnapshotsByWidgetId["widget"]
                .Metrics["difficulty.star"]
                .Metric.Value.GetDouble());
    }

    [Fact]
    public async Task SubscriberCancellationDoesNotPublishPartialScene()
    {
        var completedEngine = new ControlledEngine("completed-engine");
        var pendingEngine = new ControlledEngine("pending-engine");
        using var coordinator = CreateCoordinator(completedEngine, pendingEngine);
        using var runner = new WidgetAnalysisSceneRunner(coordinator);
        using var cancellation = new CancellationTokenSource();
        var completedSource = CreateSource("completed-source", completedEngine);
        var pendingSource = CreateSource("pending-source", pendingEngine);
        var spec = new WidgetAnalysisSceneSpec(
            "cancelled-scene",
            [
                SingleMetricWidget("completed-widget", completedSource, "difficulty.star"),
                SingleMetricWidget("pending-widget", pendingSource, "difficulty.star")
            ]);
        var published = new ConcurrentBag<WidgetAnalysisSceneSnapshot>();
        runner.SnapshotComposed += published.Add;

        var runTask = runner.RunAsync(spec, cancellation.Token);
        var completedCall = await completedEngine.WaitForCallAsync();
        await pendingEngine.WaitForCallAsync();
        completedCall.Complete(Success(
            completedCall.Request,
            completedEngine,
            "difficulty.star",
            6.0));

        Assert.Empty(published);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Empty(published);
    }

    private static AnalyzerExecutionCoordinator CreateCoordinator(params ControlledEngine[] engines)
    {
        return new AnalyzerExecutionCoordinator(new AnalyzerExecutionPlanner(engines));
    }

    private static AnalysisSourceSpec CreateSource(
        string sourceId,
        ControlledEngine engine,
        string algorithm = "Mixed",
        double rate = 1.0,
        IEnumerable<string>? mods = null)
    {
        var request = CreateRequest(engine, algorithm, rate, mods);
        return new AnalysisSourceSpec(sourceId, request, engine.Descriptor);
    }

    private static AnalysisRequest CreateRequest(
        ControlledEngine engine,
        string algorithm = "Mixed",
        double rate = 1.0,
        IEnumerable<string>? mods = null)
    {
        return new AnalysisRequest(
            engine.Descriptor.Id,
            new BeatmapIdentity("map-a", "hash-map-a"),
            "osu file contents",
            algorithm,
            "scene-profile",
            rate,
            mods,
            configurationVersion: "config-1");
    }

    private static WidgetAnalysisSpec SingleMetricWidget(
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

    private static AnalysisResult Success<T>(
        AnalysisRequest request,
        ControlledEngine engine,
        string metricId,
        T value)
    {
        return new AnalysisResult(
            request.Key,
            engine.Descriptor.Id,
            request.RequestedAlgorithm,
            request.RequestedAlgorithm,
            [SemanticMetric.FromValue(metricId, value)]);
    }

    private sealed class ControlledEngine : IAnalyzerEngine
    {
        private readonly ConcurrentQueue<PendingCall> _calls = new();
        private readonly SemaphoreSlim _callSignal = new(0);
        private int _callCount;

        public ControlledEngine(string id)
        {
            Descriptor = new AnalyzerEngineDescriptor(
                id,
                id,
                "test",
                new AnalyzerEngineCapabilities(
                    supportsMods: true,
                    supportsRate: true),
                upstreamVersion: "upstream-test",
                maxConcurrency: 8,
                threadSafety: AnalyzerEngineThreadSafety.Concurrent);
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
            var signaled = await _callSignal
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            Assert.True(signaled, "The analyzer engine was not invoked before the timeout.");
            Assert.True(_calls.TryDequeue(out var call));
            return call!;
        }
    }

    private sealed class PendingCall
    {
        private readonly CancellationTokenRegistration _cancellationRegistration;

        public PendingCall(
            AnalysisRequest request,
            CancellationToken cancellationToken)
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
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(AnalysisResult result)
        {
            _cancellationRegistration.Dispose();
            Completion.TrySetResult(result);
        }
    }
}
