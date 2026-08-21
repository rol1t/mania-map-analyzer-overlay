namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Coordinates headless analysis requests. Identical requests share one
/// in-flight task, while a new beatmap advances the generation and cancels
/// work that can no longer be displayed.
/// </summary>
public sealed class AnalyzerExecutionCoordinator : IDisposable
{
    private readonly AnalyzerExecutionPlanner _planner;
    private readonly IAnalysisDiagnostics _diagnostics;
    private readonly object _sync = new();
    private readonly Dictionary<AnalysisExecutionKey, InFlightAnalysis> _inFlight = [];
    private readonly Dictionary<string, EngineExecutionGate> _engineGates = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _generationCancellation = new();
    private string? _activeBeatmapGenerationKey;
    private long _generation;
    private bool _disposed;

    public AnalyzerExecutionCoordinator(
        AnalyzerExecutionPlanner planner,
        IAnalysisDiagnostics? diagnostics = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _diagnostics = diagnostics ?? NullAnalysisDiagnostics.Instance;
    }

    public long CurrentGeneration
    {
        get
        {
            lock (_sync)
            {
                return _generation;
            }
        }
    }

    /// <summary>
    /// Starts or joins an analysis. Cancellation belongs to the individual
    /// subscriber; canceling one waiter does not cancel other subscribers.
    /// Generation cancellation is owned by the coordinator and is shared.
    /// </summary>
    public Task<AnalysisResult> AnalyzeAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return AnalyzeCoreAsync(request, cancellationToken);
        }
        catch (Exception exception)
        {
            var diagnostic = AnalysisDiagnostic.Error(
                "analysis.plan_failed",
                $"Could not create an analysis plan for engine '{request.EngineId}'.",
                exception,
                [new KeyValuePair<string, string>("engineId", request.EngineId)]);
            _diagnostics.Report(diagnostic);
            return Task.FromResult(CreatePlanningFailure(request, diagnostic));
        }
    }

    public void Dispose()
    {
        CancellationTokenSource cancellation;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellation = _generationCancellation;
            _inFlight.Clear();
        }

        cancellation.Cancel();
        cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<AnalysisResult> AnalyzeCoreAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken)
    {
        InFlightAnalysis? existing;
        InFlightAnalysis created;
        CancellationTokenSource? supersededCancellation = null;

        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();

                var beatmapGenerationKey = CreateBeatmapGenerationKey(request);
                if (!string.Equals(_activeBeatmapGenerationKey, beatmapGenerationKey, StringComparison.Ordinal))
                {
                    _activeBeatmapGenerationKey = beatmapGenerationKey;
                    _generation++;
                    supersededCancellation = _generationCancellation;
                    _generationCancellation = new CancellationTokenSource();
                    _inFlight.Clear();
                }

                var plan = _planner.CreatePlan(request);
                if (_inFlight.TryGetValue(plan.ExecutionKey, out existing))
                {
                    return existing.Task.WaitAsync(cancellationToken);
                }

                var engineGate = ResolveEngineGate(plan.Engine.Descriptor);
                created = new InFlightAnalysis(
                    plan.ExecutionKey,
                    _generation,
                    ExecuteAsync(plan, engineGate, _generation, _generationCancellation.Token));
                _inFlight.Add(plan.ExecutionKey, created);
            }

            return created.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            CancelSupersededGeneration(supersededCancellation);
        }
    }

    private async Task<AnalysisResult> ExecuteAsync(
        AnalyzerExecutionPlan plan,
        EngineExecutionGate engineGate,
        long executionGeneration,
        CancellationToken generationToken)
    {
        var enteredGate = false;
        try
        {
            await Task.Yield();
            await engineGate.WaitAsync(generationToken).ConfigureAwait(false);
            enteredGate = true;
            generationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(executionGeneration, plan.ExecutionKey))
            {
                throw new OperationCanceledException(
                    "The queued analysis belongs to a stale beatmap generation.",
                    generationToken);
            }

            var result = await plan.Engine.AnalyzeAsync(plan.Request, generationToken).ConfigureAwait(false);
            ValidateResult(plan, result);

            if (!IsCurrent(executionGeneration, plan.ExecutionKey))
            {
                throw new OperationCanceledException("The analysis result belongs to a stale beatmap generation.");
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                _diagnostics.Report(diagnostic);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (!IsCurrent(executionGeneration, plan.ExecutionKey))
            {
                throw new OperationCanceledException(
                    "The analyzer failure belongs to a stale beatmap generation.",
                    exception);
            }

            var diagnostic = AnalysisDiagnostic.Error(
                "analysis.engine_failed",
                $"Analyzer engine '{plan.Engine.Descriptor.Id}' failed while analyzing the beatmap.",
                exception,
                [
                    new KeyValuePair<string, string>("engineId", plan.Engine.Descriptor.Id),
                    new KeyValuePair<string, string>("requestKey", plan.Request.Key.Value)
                ]);
            _diagnostics.Report(diagnostic);
            return AnalysisResult.Failure(plan.Request, plan.Engine.Descriptor, diagnostic);
        }
        finally
        {
            if (enteredGate)
            {
                engineGate.Release();
            }

            RemoveCompleted(plan.ExecutionKey, executionGeneration);
        }
    }

    private EngineExecutionGate ResolveEngineGate(AnalyzerEngineDescriptor descriptor)
    {
        if (_engineGates.TryGetValue(descriptor.Id, out var gate))
        {
            return gate;
        }

        var concurrency = descriptor.ThreadSafety == AnalyzerEngineThreadSafety.Serialized
            ? 1
            : descriptor.MaxConcurrency;
        gate = new EngineExecutionGate(concurrency);
        _engineGates.Add(descriptor.Id, gate);
        return gate;
    }

    private void ValidateResult(AnalyzerExecutionPlan plan, AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.RequestKey != plan.Request.Key)
        {
            throw new InvalidOperationException(
                $"Analyzer engine '{plan.Engine.Descriptor.Id}' returned a result for a different request.");
        }

        if (!string.Equals(result.EngineId, plan.Engine.Descriptor.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Analyzer engine '{plan.Engine.Descriptor.Id}' returned a result for engine '{result.EngineId}'.");
        }

        if (!string.Equals(result.RequestedAlgorithm, plan.Request.RequestedAlgorithm, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Analyzer engine '{plan.Engine.Descriptor.Id}' returned requested algorithm " +
                $"'{result.RequestedAlgorithm}' instead of '{plan.Request.RequestedAlgorithm}'.");
        }
    }

    private bool IsCurrent(long executionGeneration, AnalysisExecutionKey executionKey)
    {
        lock (_sync)
        {
            return !_disposed
                && _generation == executionGeneration
                && _inFlight.TryGetValue(executionKey, out var current)
                && current.Generation == executionGeneration;
        }
    }

    private void RemoveCompleted(AnalysisExecutionKey executionKey, long executionGeneration)
    {
        lock (_sync)
        {
            if (_inFlight.TryGetValue(executionKey, out var current) && current.Generation == executionGeneration)
            {
                _inFlight.Remove(executionKey);
            }
        }
    }

    private void CancelSupersededGeneration(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (Exception exception)
        {
            _diagnostics.Report(AnalysisDiagnostic.Error(
                "analysis.generation_cancel_failed",
                "A previous analyzer generation could not be canceled cleanly.",
                exception));
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static string CreateBeatmapGenerationKey(AnalysisRequest request)
    {
        // Rate and mods are execution dimensions of an individual source. They
        // must not advance the shared beatmap generation because one widget can
        // intentionally compose sources using different revisions.
        return string.Join(
            '|',
            request.Beatmap.StableKey,
            request.BeatmapContentHash);
    }

    private static AnalysisResult CreatePlanningFailure(
        AnalysisRequest request,
        AnalysisDiagnostic diagnostic)
    {
        return new AnalysisResult(
            request.Key,
            request.EngineId,
            request.RequestedAlgorithm,
            actualAlgorithm: null,
            diagnostics: [diagnostic],
            outcome: AnalysisOutcome.Failed);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record InFlightAnalysis(
        AnalysisExecutionKey Key,
        long Generation,
        Task<AnalysisResult> Task);

    /// <summary>
    /// Intentionally not disposable: an engine task may still release its gate
    /// after the coordinator itself has been disposed.
    /// </summary>
    private sealed class EngineExecutionGate
    {
        private readonly SemaphoreSlim _semaphore;

        public EngineExecutionGate(int concurrency)
        {
            _semaphore = new SemaphoreSlim(concurrency, concurrency);
        }

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            return _semaphore.WaitAsync(cancellationToken);
        }

        public void Release()
        {
            _semaphore.Release();
        }
    }
}
