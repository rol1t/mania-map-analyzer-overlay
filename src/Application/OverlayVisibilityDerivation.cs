using ManiaMapAnalyzerOverlay.Core.Analysis;

namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>
/// Pure native overlay visibility decision. Presentation adapters consume the
/// result; they do not reimplement this policy.
/// </summary>
public static class OverlayVisibilityDerivation
{
    public static bool ShouldShowNativeOverlay(OverlayRuntimeState state)
    {
        return ComputeDesiredVisibility(state);
    }

    /// <summary>
    /// Computes policy intent without observing whether a presentation surface
    /// has actually applied it. The result is application state and therefore
    /// remains meaningful while the HWND/WebView is unavailable or hidden.
    /// </summary>
    public static bool ComputeDesiredVisibility(OverlayRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.OverlayMode)
        {
            return false;
        }

        if (state.OsuWindowMinimized)
        {
            return true;
        }

        return state.GameplayStateKnown
            ? OverlayVisibilityPolicy.ShouldShow(state.VisibilityPolicy, state.IsPlaying, state.IsPaused)
            : OverlayVisibilityPolicy.ShouldShowBeforeGameplayStateIsKnown(state.VisibilityPolicy);
    }
}
