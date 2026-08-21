using System.Collections.Concurrent;

namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Application-level orchestrator that executes all analyzer sources required
/// by a widget and composes their results into one domain snapshot.
/// </summary>
public sealed class WidgetAnalysisRunner : IDisposable
{
    private readonly AnalyzerExecutionCoordinator _coordinator;
    private readonly WidgetAnalysisComposer _composer;
    private readonly IAnalysisDiagnostics _diagnostics;
    private readonly ConcurrentDictionary<string, AnalysisRunScope> _widgetScopes =
        new(StringComparer.Ordinal);
    private int _disposed;

    public WidgetAnalysisRunner(
        AnalyzerExecutionCoordinator coordinator,
        WidgetAnalysisComposer? composer = null,
        IAnalysisDiagnostics? diagnostics = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _composer = composer ?? new WidgetAnalysisComposer();
        _diagnostics = diagnostics ?? NullAnalysisDiagnostics.Instance;
    }

    /// <summary>
    /// Raised only after every source completed and a non-cancelled snapshot was
    /// composed. Cancellation and stale generations propagate to the caller.
    /// </summary>
    public event Action<ComposedWidgetSnapshot>? SnapshotComposed;

    /// <summary>
    /// Runs the latest generation of one widget. Starting another run with the
    /// same widget id invalidates the previous run before it can publish.
    /// </summary>
    public Task<ComposedWidgetSnapshot> RunAsync(
        WidgetAnalysisSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        var scope = _widgetScopes.GetOrAdd(
            spec.WidgetId,
            widgetId => new AnalysisRunScope($"widget:{widgetId}", _diagnostics));
        if (Volatile.Read(ref _disposed) != 0)
        {
            if (_widgetScopes.TryRemove(spec.WidgetId, out var removedScope))
            {
                removedScope.Dispose();
            }

            ThrowIfDisposed();
        }

        var generation = scope.BeginGeneration();
        return RunAsync(spec, generation, cancellationToken);
    }

    /// <summary>
    /// Runs a widget in an explicit scene or caller-owned generation. Reuse one
    /// generation for all widgets in a batch, then begin a new generation when
    /// the live state changes.
    /// </summary>
    public Task<ComposedWidgetSnapshot> RunAsync(
        WidgetAnalysisSpec spec,
        AnalysisRunGeneration generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(generation);
        ThrowIfDisposed();
        return RunCoreAsync(spec, generation, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var scope in _widgetScopes.Values)
        {
            scope.Dispose();
        }

        _widgetScopes.Clear();
        GC.SuppressFinalize(this);
    }

    private async Task<ComposedWidgetSnapshot> RunCoreAsync(
        WidgetAnalysisSpec spec,
        AnalysisRunGeneration generation,
        CancellationToken cancellationToken)
    {
        using var runCancellation = generation.CreateLinkedCancellation(cancellationToken);
        runCancellation.Token.ThrowIfCancellationRequested();

        var sourceTasks = spec.Sources
            .Select(source => AnalyzeSourceAsync(source, runCancellation.Token))
            .ToArray();
        var sourceResults = await Task.WhenAll(sourceTasks).ConfigureAwait(false);
        runCancellation.Token.ThrowIfCancellationRequested();

        var snapshot = _composer.Compose(spec, sourceResults);
        runCancellation.Token.ThrowIfCancellationRequested();
        if (!generation.TryPublish(
            cancellationToken,
            () => SnapshotComposed?.Invoke(snapshot)))
        {
            throw new OperationCanceledException(
                $"Analysis generation {generation.Generation} for scope '{generation.ScopeId}' was superseded.",
                runCancellation.Token);
        }

        return snapshot;
    }

    private async Task<AnalysisSourceResult> AnalyzeSourceAsync(
        AnalysisSourceSpec source,
        CancellationToken cancellationToken)
    {
        var result = await _coordinator
            .AnalyzeAsync(source.Request, cancellationToken)
            .ConfigureAwait(false);
        return new AnalysisSourceResult(source.SourceId, result);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
