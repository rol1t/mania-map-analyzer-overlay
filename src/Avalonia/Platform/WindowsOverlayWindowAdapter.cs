using ManiaMapAnalyzerOverlay.Application;

namespace ManiaMapAnalyzerOverlay.Avalonia.Platform;

/// <summary>
/// Application port adapter for the existing Windows overlay controller.
/// Interaction, hotkeys, and focus protection remain on the platform
/// controller; the runtime only receives this small visibility surface.
/// </summary>
public sealed class WindowsOverlayWindowAdapter : IOverlayWindow
{
    private readonly WindowsOverlayController _controller;

    public WindowsOverlayWindowAdapter(WindowsOverlayController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public bool IsMinimized => _controller.IsOsuMinimized;

    public bool IsVisible => _controller.IsWindowShown;

    public void SetVisible(bool visible) => _controller.SetWindowVisible(visible);
}
