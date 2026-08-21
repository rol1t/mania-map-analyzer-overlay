using System.Collections.Concurrent;
using ManiaMapAnalyzerOverlay.Core.Analysis;
using Xunit;

namespace ManiaMapAnalyzerOverlay.Core.Tests;

public sealed class AnalyzerExecutionSchedulingTests
{
    [Fact]
    public async Task SerializedEngineRunsExactlyOneDistinctRequestAtATime()
    {
        var engine = new ScheduledTestEngine(
            "serialized",
            AnalyzerEngineThreadSafety.Serialized,
            maxConcurrency: 8);
        using var coordinator = CreateCoordinator(engine);
        var firstRequest = CreateRequest(engine, "map-a", "First");
        var secondRequest = CreateRequest(engine, "map-a", "Second");

        var firstTask = coordinator.AnalyzeAsync(firstRequest);
        var firstCall = await engine.WaitForCallAsync();
        var secondTask = coordinator.AnalyzeAsync(secondRequest);

        Assert.False(await engine.HasCallWithinAsync(TimeSpan.FromMilliseconds(150)));
        Assert.Equal(1, engine.CallCount);

        firstCall.Complete(Success(firstCall.Request, engine));
        var secondCall = await engine.WaitForCallAsync();
        secondCall.Complete(Success(secondCall.Request, engine));
        await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(1, engine.MaximumActiveCalls);
    }

    [Fact]
    public async Task ConcurrentEngineHonorsConfiguredMaximumConcurrency()
    {
        var engine = new ScheduledTestEngine(
            "concurrent",
            AnalyzerEngineThreadSafety.Concurrent,
            maxConcurrency: 2);
        using var coordinator = CreateCoordinator(engine);
        var firstTask = coordinator.AnalyzeAsync(CreateRequest(engine, "map-a", "First"));
        var secondTask = coordinator.AnalyzeAsync(CreateRequest(engine, "map-a", "Second"));
        var thirdTask = coordinator.AnalyzeAsync(CreateRequest(engine, "map-a", "Third"));

        var firstCall = await engine.WaitForCallAsync();
        var secondCall = await engine.WaitForCallAsync();
        Assert.False(await engine.HasCallWithinAsync(TimeSpan.FromMilliseconds(150)));
        Assert.Equal(2, engine.CallCount);
        Assert.Equal(2, engine.MaximumActiveCalls);

        firstCall.Complete(Success(firstCall.Request, engine));
        var thirdCall = await engine.WaitForCallAsync();
        secondCall.Complete(Success(secondCall.Request, engine));
        thirdCall.Complete(Success(thirdCall.Request, engine));
        await Task.WhenAll(firstTask, secondTask, thirdTask);

        Assert.Equal(2, engine.MaximumActiveCalls);
    }

    [Fact]
    public async Task QueuedStaleGenerationNeverInvokesEngine()
    {
        var engine = new ScheduledTestEngine(
            "serialized",
            AnalyzerEngineThreadSafety.Serialized,
            maxConcurrency: 1);
        using var coordinator = CreateCoordinator(engine);
        var runningRequest = CreateRequest(engine, "map-a", "Running");
        var queuedRequest = CreateRequest(engine, "map-a", "Queued");
        var currentRequest = CreateRequest(engine, "map-b", "Current");

        var runningTask = coordinator.AnalyzeAsync(runningRequest);
        await engine.WaitForCallAsync();
        var queuedTask = coordinator.AnalyzeAsync(queuedRequest);
        var currentTask = coordinator.AnalyzeAsync(currentRequest);

        var currentCall = await engine.WaitForCallAsync();
        Assert.Equal("Current", currentCall.Request.RequestedAlgorithm);
        currentCall.Complete(Success(currentCall.Request, engine));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runningTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedTask);
        Assert.Equal(AnalysisOutcome.Success, (await currentTask).Outcome);
        Assert.DoesNotContain("Queued", engine.InvokedAlgorithms);
        Assert.Equal(["Running", "Current"], engine.InvokedAlgorithms.ToArray());
    }

    private static AnalyzerExecutionCoordinator CreateCoordinator(ScheduledTestEngine engine)
    {
        return new AnalyzerExecutionCoordinator(new AnalyzerExecutionPlanner([engine]));
    }

    private static AnalysisRequest CreateRequest(
        ScheduledTestEngine engine,
        string mapId,
        string algorithm)
    {
        return new AnalysisRequest(
            engine.Descriptor.Id,
            new BeatmapIdentity(mapId, $"hash-{mapId}"),
            $"osu file contents for {mapId}",
            algorithm,
            "scheduler-test");
    }

    private static AnalysisResult Success(
        AnalysisRequest request,
        ScheduledTestEngine engine)
    {
        return new AnalysisResult(
            request.Key,
            engine.Descriptor.Id,
            request.RequestedAlgorithm,
            request.RequestedAlgorithm,
            [SemanticMetric.FromValue("difficulty.star", 5.0)]);
    }

    private sealed class ScheduledTestEngine : IAnalyzerEngine
    {
        private readonly ConcurrentQueue<PendingCall> _calls = new();
        private readonly SemaphoreSlim _callSignal = new(0);
        private readonly ConcurrentQueue<string> _invokedAlgorithms = new();
        private int _activeCalls;
        private int _callCount;
        private int _maximumActiveCalls;

        public ScheduledTestEngine(
            string id,
            AnalyzerEngineThreadSafety threadSafety,
            int maxConcurrency)
        {
            Descriptor = new AnalyzerEngineDescriptor(
                id,
                id,
                "test",
                upstreamVersion: "test-upstream",
                maxConcurrency: maxConcurrency,
                threadSafety: threadSafety);
        }

        public AnalyzerEngineDescriptor Descriptor
        {
            get;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public int MaximumActiveCalls => Volatile.Read(ref _maximumActiveCalls);

        public IReadOnlyCollection<string> InvokedAlgorithms => _invokedAlgorithms.ToArray();

        public async Task<AnalysisResult> AnalyzeAsync(
            AnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            var activeCalls = Interlocked.Increment(ref _activeCalls);
            UpdateMaximumActiveCalls(activeCalls);
            Interlocked.Increment(ref _callCount);
            _invokedAlgorithms.Enqueue(request.RequestedAlgorithm);
            var call = new PendingCall(request);
            _calls.Enqueue(call);
            _callSignal.Release();

            try
            {
                return await call.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
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

        public Task<bool> HasCallWithinAsync(TimeSpan timeout)
        {
            return _callSignal.WaitAsync(timeout);
        }

        private void UpdateMaximumActiveCalls(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumActiveCalls);
                if (candidate <= current
                    || Interlocked.CompareExchange(ref _maximumActiveCalls, candidate, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class PendingCall(AnalysisRequest request)
    {
        public AnalysisRequest Request { get; } = request;

        public TaskCompletionSource<AnalysisResult> Completion
        {
            get;
        } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(AnalysisResult result)
        {
            Completion.TrySetResult(result);
        }
    }
}
