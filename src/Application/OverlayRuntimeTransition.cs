namespace ManiaMapAnalyzerOverlay.Application;

public enum OverlayRuntimeRejectionKind
{
    None,
    StaleSequence,
    StaleTosuTransportGeneration,
    StalePresentationSurfaceGeneration,
    StaleAnalysisBeatmapGeneration,
    UnknownAnalysisRequest,
    StaleAnalysisRequest,
    AnalysisRequestMetadataMismatch,
    InvalidAnalysisRequestId,
    UnknownReplayRequest,
    StaleReplayRequest,
    StaleReplayBeatmapGeneration,
    ReplayRequestMetadataMismatch,
    InvalidReplayRequestId,
    NoStateChange
}

/// <summary>
/// Structured explanation for a reducer invocation that did not advance the
/// immutable runtime state. Keeping causal identities here makes stale-event
/// diagnostics useful without parsing log strings.
/// </summary>
public sealed record OverlayRuntimeRejection(
    OverlayRuntimeRejectionKind Kind,
    long EventSequence,
    long CurrentSequence,
    long? EventGeneration = null,
    long? CurrentGeneration = null,
    string? EventBeatmapId = null,
    string? CurrentBeatmapId = null);

/// <summary>
/// Describes one reducer invocation for shadow diagnostics. The previous and
/// next states are immutable, so consumers can compare the legacy path with
/// the coordinator without acquiring locks or observing a half-written state.
/// </summary>
public sealed record OverlayRuntimeTransition(
    OverlayRuntimeEvent Event,
    OverlayRuntimeState Previous,
    OverlayRuntimeState Next,
    bool Accepted,
    OverlayRuntimeRejection? Rejection = null);

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
