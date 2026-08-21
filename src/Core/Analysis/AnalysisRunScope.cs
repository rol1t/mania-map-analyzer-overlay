namespace ManiaMapAnalyzerOverlay.Core.Analysis;

/// <summary>
/// Owns the live generation of an analysis scene or another caller-defined
/// batch. A generation can be shared by several widget runs. Beginning the
/// next generation invalidates every run that still uses the previous one.
/// </summary>
public sealed class AnalysisRunScope : IDisposable
{
    private readonly IAnalysisDiagnostics _diagnostics;
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCancellation;
    private long _generation;
    private bool _disposed;

    public AnalysisRunScope(
        string scopeId,
        IAnalysisDiagnostics? diagnostics = null)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            throw new ArgumentException("An analysis run scope id is required.", nameof(scopeId));
        }

        ScopeId = scopeId.Trim();
        _diagnostics = diagnostics ?? NullAnalysisDiagnostics.Instance;
    }

    public string ScopeId
    {
        get;
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
    /// Creates a generation token that may be passed to any number of widget
    /// runs in the same scene update. The previous generation is invalidated.
    /// </summary>
    public AnalysisRunGeneration BeginGeneration()
    {
        CancellationTokenSource? supersededCancellation;
        AnalysisRunGeneration generation;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            supersededCancellation = _activeCancellation;
            _activeCancellation = new CancellationTokenSource();
            _generation++;
            generation = new AnalysisRunGeneration(
                this,
                _generation);
        }

        CancelAndDispose(supersededCancellation);
        return generation;
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellation = _activeCancellation;
            _activeCancellation = null;
        }

        CancelAndDispose(cancellation);
        GC.SuppressFinalize(this);
    }

    internal bool IsCurrent(AnalysisRunGeneration generation)
    {
        lock (_sync)
        {
            return IsCurrentCore(generation);
        }
    }

    internal CancellationTokenSource CreateLinkedCancellation(
        AnalysisRunGeneration generation,
        CancellationToken subscriberCancellation)
    {
        lock (_sync)
        {
            if (!IsCurrentCore(generation))
            {
                throw new OperationCanceledException(
                    $"Analysis generation {generation.Generation} for scope '{ScopeId}' was superseded.");
            }

            return CancellationTokenSource.CreateLinkedTokenSource(
                subscriberCancellation,
                _activeCancellation!.Token);
        }
    }

    internal bool TryPublish(
        AnalysisRunGeneration generation,
        CancellationToken subscriberCancellation,
        Action publication)
    {
        ArgumentNullException.ThrowIfNull(publication);

        lock (_sync)
        {
            if (subscriberCancellation.IsCancellationRequested || !IsCurrentCore(generation))
            {
                return false;
            }

            // Publication is serialized with BeginGeneration so a generation
            // cannot become stale between the final check and event delivery.
            publication();
            return true;
        }
    }

    private bool IsCurrentCore(AnalysisRunGeneration generation)
    {
        return !_disposed
            && ReferenceEquals(generation.Scope, this)
            && generation.Generation == _generation
            && _activeCancellation is not null
            && !_activeCancellation.IsCancellationRequested;
    }

    private void CancelAndDispose(CancellationTokenSource? cancellation)
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
                "analysis.run_generation_cancel_failed",
                $"Analysis run scope '{ScopeId}' could not cancel its previous generation cleanly.",
                exception,
                [new KeyValuePair<string, string>("scopeId", ScopeId)]));
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}

/// <summary>
/// Immutable handle for one scene or caller-defined analysis generation.
/// Reuse the same handle for every widget that belongs to one batch.
/// </summary>
public sealed class AnalysisRunGeneration
{
    internal AnalysisRunGeneration(
        AnalysisRunScope scope,
        long generation)
    {
        Scope = scope;
        Generation = generation;
    }

    public string ScopeId => Scope.ScopeId;

    public long Generation
    {
        get;
    }

    public bool IsCurrent => Scope.IsCurrent(this);

    internal AnalysisRunScope Scope
    {
        get;
    }

    internal bool TryPublish(
        CancellationToken subscriberCancellation,
        Action publication)
    {
        return Scope.TryPublish(this, subscriberCancellation, publication);
    }

    internal CancellationTokenSource CreateLinkedCancellation(
        CancellationToken subscriberCancellation)
    {
        return Scope.CreateLinkedCancellation(this, subscriberCancellation);
    }
}
