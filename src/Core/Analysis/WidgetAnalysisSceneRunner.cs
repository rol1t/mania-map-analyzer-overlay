using System.Collections.Concurrent;

namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Runs every widget in a scene against one shared generation. Scene snapshots
/// are published atomically only after all widgets have completed.
/// </summary>
public sealed class WidgetAnalysisSceneRunner : IDisposable
{
    private readonly WidgetAnalysisRunner _widgetRunner;
    private readonly IAnalysisDiagnostics _diagnostics;
    private readonly ConcurrentDictionary<string, AnalysisRunScope> _sceneScopes =
        new(StringComparer.Ordinal);
    private int _disposed;

    public WidgetAnalysisSceneRunner(
        AnalyzerExecutionCoordinator coordinator,
        WidgetAnalysisComposer? composer = null,
        IAnalysisDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);

        _diagnostics = diagnostics ?? NullAnalysisDiagnostics.Instance;
        _widgetRunner = new WidgetAnalysisRunner(coordinator, composer, _diagnostics);
    }

    /// <summary>
    /// Raised once for a complete, current scene generation. Individual widget
    /// completion is deliberately not forwarded as partial scene publication.
    /// </summary>
    public event Action<WidgetAnalysisSceneSnapshot>? SnapshotComposed;

    /// <summary>
    /// Starts the latest generation for <see cref="WidgetAnalysisSceneSpec.SceneId"/>.
    /// A newer call for the same scene invalidates the previous generation.
    /// </summary>
    public Task<WidgetAnalysisSceneSnapshot> RunAsync(
        WidgetAnalysisSceneSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        var scope = _sceneScopes.GetOrAdd(
            spec.SceneId,
            sceneId => new AnalysisRunScope($"scene:{sceneId}", _diagnostics));
        if (Volatile.Read(ref _disposed) != 0)
        {
            if (_sceneScopes.TryRemove(spec.SceneId, out var removedScope))
            {
                removedScope.Dispose();
            }

            ThrowIfDisposed();
        }

        var generation = scope.BeginGeneration();
        return RunCoreAsync(spec, generation, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _widgetRunner.Dispose();
        foreach (var scope in _sceneScopes.Values)
        {
            scope.Dispose();
        }

        _sceneScopes.Clear();
        GC.SuppressFinalize(this);
    }

    private async Task<WidgetAnalysisSceneSnapshot> RunCoreAsync(
        WidgetAnalysisSceneSpec spec,
        AnalysisRunGeneration generation,
        CancellationToken cancellationToken)
    {
        using var runCancellation = generation.CreateLinkedCancellation(cancellationToken);
        runCancellation.Token.ThrowIfCancellationRequested();

        var widgetTasks = spec.Widgets
            .Select(widget => _widgetRunner.RunAsync(widget, generation, runCancellation.Token))
            .ToArray();
        var orderedSnapshots = await Task.WhenAll(widgetTasks).ConfigureAwait(false);
        runCancellation.Token.ThrowIfCancellationRequested();

        var snapshot = new WidgetAnalysisSceneSnapshot(
            spec.SceneId,
            generation.Generation,
            orderedSnapshots);
        if (!generation.TryPublish(
            cancellationToken,
            () => SnapshotComposed?.Invoke(snapshot)))
        {
            throw new OperationCanceledException(
                $"Analysis generation {generation.Generation} for scene '{spec.SceneId}' was superseded.",
                runCancellation.Token);
        }

        return snapshot;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
