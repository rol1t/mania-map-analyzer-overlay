using System;
using System.Threading.Tasks;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

/// <summary>
/// Delivers snapshots to a presentation surface using latest-wins semantics.
/// The producer may run continuously while the surface is hidden; only the
/// newest snapshot is retained until the surface is ready and visible.
/// </summary>
public sealed class LatestWinsSnapshotPublisher<T>
{
    private readonly Func<T, Task> _publishAsync;
    private readonly Action<Exception>? _publishFailed;
    private readonly object _gate = new();
    private T? _latestSnapshot;
    private bool _hasLatestSnapshot;
    private bool _presentationVisible;
    private bool _browserReady;
    private bool _publishInFlight;
    private long _generation;
    private long _latestVersion;
    private long _publishedVersion;

    public LatestWinsSnapshotPublisher(
        Func<T, Task> publishAsync,
        Action<Exception>? publishFailed = null)
    {
        _publishAsync = publishAsync ?? throw new ArgumentNullException(nameof(publishAsync));
        _publishFailed = publishFailed;
    }

    public T? LatestSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _hasLatestSnapshot ? _latestSnapshot : default;
            }
        }
    }

    public bool HasPendingSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _hasLatestSnapshot && _latestVersion > _publishedVersion;
            }
        }
    }

    /// <summary>
    /// Starts a new browser/document session. The latest snapshot is kept so
    /// it can be replayed after navigation or WebView recreation. Any publish
    /// still running against the old surface is isolated by the generation.
    /// </summary>
    public void BeginPresentationSession()
    {
        lock (_gate)
        {
            _generation++;
            _browserReady = false;
            _presentationVisible = false;
            _publishInFlight = false;
            _publishedVersion = 0;
        }
    }

    public void SetBrowserReady(bool ready)
    {
        PublishWork? work = null;
        lock (_gate)
        {
            if (!ready)
            {
                if (_browserReady)
                {
                    _generation++;
                    _publishInFlight = false;
                    _publishedVersion = 0;
                }

                _browserReady = false;
            }
            else
            {
                _browserReady = true;
                work = TakeNextWorkLocked();
            }
        }

        Start(work);
    }

    public void SetPresentationVisible(bool visible)
    {
        PublishWork? work = null;
        lock (_gate)
        {
            _presentationVisible = visible;
            if (visible)
            {
                work = TakeNextWorkLocked();
            }
        }

        Start(work);
    }

    public void Submit(T snapshot)
    {
        PublishWork? work = null;
        lock (_gate)
        {
            _latestSnapshot = snapshot;
            _hasLatestSnapshot = true;
            _latestVersion++;
            work = TakeNextWorkLocked();
        }

        Start(work);
    }

    private PublishWork? TakeNextWorkLocked()
    {
        if (!_presentationVisible || !_browserReady || _publishInFlight || !_hasLatestSnapshot ||
            _latestVersion <= _publishedVersion)
        {
            return null;
        }

        _publishInFlight = true;
        return new PublishWork(_latestSnapshot!, _generation, _latestVersion);
    }

    private void Start(PublishWork? work)
    {
        if (work is not null)
        {
            _ = PublishCoreAsync(work);
        }
    }

    private async Task PublishCoreAsync(PublishWork work)
    {
        Exception? failure = null;
        try
        {
            await _publishAsync(work.Snapshot).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            try
            {
                _publishFailed?.Invoke(exception);
            }
            catch
            {
                // Diagnostics must never break the coalescer's recovery path.
            }
        }

        PublishWork? next = null;
        lock (_gate)
        {
            // A new browser/document session owns the state now. The old
            // completion must not clear its in-flight flag or publish into it.
            if (work.Generation != _generation)
            {
                return;
            }

            _publishInFlight = false;
            if (failure is null)
            {
                _publishedVersion = Math.Max(_publishedVersion, work.Version);
                next = TakeNextWorkLocked();
            }
            else if (_latestVersion > work.Version)
            {
                // A newer frame arrived while the old call failed. It is safe
                // and useful to continue with that newest frame immediately.
                next = TakeNextWorkLocked();
            }
        }

        Start(next);
    }

    private sealed record PublishWork(T Snapshot, long Generation, long Version);
}
