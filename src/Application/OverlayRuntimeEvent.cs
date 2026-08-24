using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>Base event processed by the application state reducer.</summary>
public abstract record OverlayRuntimeEvent(long Sequence);

public sealed record RealtimeTelemetryReceived(
    long Sequence,
    TosuRealtimeTelemetry Telemetry) : OverlayRuntimeEvent(Sequence);

public sealed record AnalysisSnapshotReceived(
    long Sequence,
    AnalysisSnapshot Snapshot) : OverlayRuntimeEvent(Sequence);

public sealed record PresentationAvailabilityChanged(
    long Sequence,
    bool Ready,
    bool Visible) : OverlayRuntimeEvent(Sequence);

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
