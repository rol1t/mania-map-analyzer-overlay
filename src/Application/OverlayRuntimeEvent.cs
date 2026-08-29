using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.RealtimeAnalysis;

namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>Base event processed by the application state reducer.</summary>
public abstract record OverlayRuntimeEvent(long Sequence);

public enum AnalysisSnapshotProducer
{
    Unspecified,
    Headless,
    BrowserFallback
}

public sealed record RealtimeTelemetryReceived(
    long Sequence,
    RealtimeTelemetryUpdate Telemetry) : OverlayRuntimeEvent(Sequence);

public sealed record TosuConnectionChanged(
    long Sequence,
    TosuConnectionState State,
    long TransportGeneration) : OverlayRuntimeEvent(Sequence);

public sealed record AnalysisRequestStarted(
    long Sequence,
    AnalysisRequestId RequestId,
    long BeatmapGeneration,
    string BeatmapId,
    string ConfigurationIdentity) : OverlayRuntimeEvent(Sequence);

public sealed record AnalysisSnapshotReceived(
    long Sequence,
    AnalysisSnapshot Snapshot,
    long BeatmapGeneration = 0,
    AnalysisRequestId? RequestId = null,
    string ConfigurationIdentity = "",
    AnalysisSnapshotProducer Producer = AnalysisSnapshotProducer.Unspecified) : OverlayRuntimeEvent(Sequence);

public sealed record AnalysisRequestFailed(
    long Sequence,
    AnalysisRequestId RequestId,
    string FailureCode,
    string? FailureMessage = null,
    long BeatmapGeneration = 0,
    string BeatmapId = "",
    string ConfigurationIdentity = "") : OverlayRuntimeEvent(Sequence);

public sealed record PresentationAvailabilityChanged(
    long Sequence,
    bool Ready,
    bool Visible,
    long SurfaceGeneration = 0) : OverlayRuntimeEvent(Sequence);

public sealed record OverlayModeChanged(
    long Sequence,
    bool Enabled) : OverlayRuntimeEvent(Sequence);

public sealed record OsuWindowStateChanged(
    long Sequence,
    bool Minimized) : OverlayRuntimeEvent(Sequence);

public sealed record VisibilityPolicyChanged(
    long Sequence,
    string Policy) : OverlayRuntimeEvent(Sequence);

public sealed record RuntimeReset(
    long Sequence) : OverlayRuntimeEvent(Sequence);
