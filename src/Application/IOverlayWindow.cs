namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>
/// Minimal native-window port used by the runtime visibility effect. Platform
/// adapters own HWND/Win32 details; the application only asks whether the
/// surface is minimized/visible and requests a visibility transition.
/// </summary>
public interface IOverlayWindow
{
    bool IsMinimized
    {
        get;
    }

    bool IsVisible
    {
        get;
    }

    void SetVisible(bool visible);
}
