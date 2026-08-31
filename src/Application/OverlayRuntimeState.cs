using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.RealtimeAnalysis;

namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>
/// Immutable application-owned state shared by telemetry, analysis, and
/// presentation coordinators. UI controls and WebView instances are not part
/// of this state.
/// </summary>
public sealed record OverlayRuntimeState
{
    public long Version
    {
        get; init;
    }

    public long LastEventSequence
    {
        get; init;
    } = -1;

    public long BeatmapGeneration
    {
        get; init;
    }

    public TosuConnectionState TosuConnection
    {
        get; init;
    }

    public long TosuTransportGeneration
    {
        get; init;
    }

    public bool OverlayMode
    {
        get; init;
    }

    public string VisibilityPolicy { get; init; } = OverlayVisibilityPolicy.Always;

    public bool OsuWindowMinimized
    {
        get; init;
    }

    public bool GameplayStateKnown
    {
        get; init;
    }

    public bool IsPlaying
    {
        get; init;
    }

    public bool? IsPaused
    {
        get; init;
    }

    public RealtimePlayState GameplayState
    {
        get; init;
    } = RealtimePlayState.Unknown;

    public string BeatmapId { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    public string LastRealtimeSource { get; init; } = string.Empty;

    public bool PresentationReady
    {
        get; init;
    }

    /// <summary>
    /// Actual visibility feedback from the current presentation surface. It
    /// is not the policy decision; compare it with <see cref="DesiredVisibility"/>
    /// when diagnosing a platform effect that has not applied yet.
    /// </summary>
    public bool PresentationVisible
    {
        get; init;
    }

    /// <summary>
    /// Policy intent for the native overlay. This is deliberately computed
    /// from application state and is independent from platform feedback.
    /// </summary>
    public bool DesiredVisibility => OverlayVisibilityDerivation.ComputeDesiredVisibility(this);

    /// <summary>Whether the current presentation surface is ready to receive state.</summary>
    public bool SurfaceReady => PresentationReady;

    /// <summary>Actual visibility reported by the current presentation surface.</summary>
    public bool ActualVisibility => PresentationVisible;

    /// <summary>
    /// Identifies the logical presentation document/window that reported
    /// readiness. A zero value represents an older unversioned producer during
    /// the migration; positive generations reject feedback from an older
    /// surface after WebView recreation.
    /// </summary>
    public long PresentationSurfaceGeneration
    {
        get; init;
    }

    public RealtimeAnalysisSnapshot? LatestRealtime
    {
        get; init;
    }

    public AnalysisSnapshot? LatestAnalysis
    {
        get; init;
    }

    /// <summary>
    /// Causal slot for the latest headless analysis execution. The slot
    /// records who produced the result and whether it is still running,
    /// waiting for realtime confirmation, complete, or failed.
    /// </summary>
    public AnalysisRequestSlot? AnalysisRequest
    {
        get; init;
    }

    /// <summary>
    /// Causal slot for the latest exact replay execution. Replay analysis is
    /// intentionally independent from headless map analysis and realtime
    /// Pause Coach state.
    /// </summary>
    public ReplayAnalysisRequestSlot? ReplayRequest
    {
        get; init;
    }

    public static OverlayRuntimeState Empty { get; } = new();
}
