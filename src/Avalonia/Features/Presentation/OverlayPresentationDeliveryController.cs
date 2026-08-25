using System;
using System.Threading.Tasks;
using ManiaMapAnalyzerOverlay.Application;
using ManiaMapAnalyzerOverlay.Avalonia.Features.Analysis;

namespace ManiaMapAnalyzerOverlay.Avalonia.Features.Presentation;

/// <summary>
/// Owns latest-wins delivery for the logical desktop and fullscreen surfaces.
/// It does not decide visibility or render DOM; callers provide the platform
/// effects for each surface.
/// </summary>
public sealed class OverlayPresentationDeliveryController
{
    private readonly LatestWinsSnapshotPublisher<OverlayViewState> _desktop;
    private readonly LatestWinsSnapshotPublisher<OverlayViewState> _fullscreen;
    private readonly Action _clearFullscreen;
    private long _desktopSurfaceGeneration;
    private long _fullscreenSurfaceGeneration;

    public OverlayPresentationDeliveryController(
        Func<OverlayViewState, Task> publishDesktopAsync,
        Func<OverlayViewState, Task> publishFullscreenAsync,
        Action clearFullscreen,
        Action<Exception>? desktopPublishFailed = null,
        Action<Exception>? fullscreenPublishFailed = null,
        TimeSpan? desktopPublishTimeout = null)
    {
        _desktop = new LatestWinsSnapshotPublisher<OverlayViewState>(
            publishDesktopAsync,
            desktopPublishFailed,
            desktopPublishTimeout);
        _fullscreen = new LatestWinsSnapshotPublisher<OverlayViewState>(
            publishFullscreenAsync,
            fullscreenPublishFailed);
        _clearFullscreen = clearFullscreen ?? throw new ArgumentNullException(nameof(clearFullscreen));
    }

    /// <summary>
    /// Generation of the current desktop presentation surface. A new value is
    /// allocated before the publisher accepts readiness/visible feedback, so
    /// callbacks from a replaced WebView cannot be mistaken for the current
    /// surface by the application runtime.
    /// </summary>
    public long DesktopSurfaceGeneration =>
        System.Threading.Volatile.Read(ref _desktopSurfaceGeneration);

    /// <summary>
    /// Generation of the current fullscreen presentation surface.
    /// </summary>
    public long FullscreenSurfaceGeneration =>
        System.Threading.Volatile.Read(ref _fullscreenSurfaceGeneration);

    public void Submit(OverlayViewState viewState)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        SubmitDesktop(viewState);
        _fullscreen.Submit(viewState);
    }

    public void SubmitDesktop(OverlayViewState viewState)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        _desktop.Submit(viewState);
    }

    public long BeginDesktopPresentationSession()
    {
        long generation = System.Threading.Interlocked.Increment(ref _desktopSurfaceGeneration);
        _desktop.BeginPresentationSession();
        return generation;
    }

    public void SetDesktopBrowserReady(bool ready) => _desktop.SetBrowserReady(ready);

    public void SetDesktopPresentationVisible(bool visible) => _desktop.SetPresentationVisible(visible);

    public void SetFullscreenPresentationEnabled(bool enabled)
    {
        if (!enabled)
        {
            _fullscreen.SetPresentationVisible(false);
            _fullscreen.SetBrowserReady(false);
            _clearFullscreen();
            return;
        }

        System.Threading.Interlocked.Increment(ref _fullscreenSurfaceGeneration);
        _fullscreen.BeginPresentationSession();
        _fullscreen.SetBrowserReady(true);
        _fullscreen.SetPresentationVisible(true);
    }

    public void SubmitFullscreen(OverlayViewState viewState)
    {
        ArgumentNullException.ThrowIfNull(viewState);
        _fullscreen.Submit(viewState);
    }
}
