namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>
/// Describes one reducer invocation for shadow diagnostics. The previous and
/// next states are immutable, so consumers can compare the legacy path with
/// the coordinator without acquiring locks or observing a half-written state.
/// </summary>
public sealed record OverlayRuntimeTransition(
    OverlayRuntimeEvent Event,
    OverlayRuntimeState Previous,
    OverlayRuntimeState Next,
    bool Accepted);

public sealed class OverlayViewStateChangedEventArgs : EventArgs
{
    public OverlayViewStateChangedEventArgs(OverlayViewState viewState)
    {
        ViewState = viewState ?? throw new ArgumentNullException(nameof(viewState));
    }

    public OverlayViewState ViewState
    {
        get;
    }
}
