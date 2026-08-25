using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

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

    public bool PresentationVisible
    {
        get; init;
    }

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
    /// A completed headless result whose beatmap is ahead of the latest
    /// realtime frame. It is promoted only when realtime confirms the same
    /// beatmap, so a fast analysis cannot be lost without allowing a stale
    /// completion to replace the currently rendered map.
    /// </summary>
    public AnalysisSnapshot? PendingAnalysis
    {
        get; init;
    }

    public static OverlayRuntimeState Empty { get; } = new();
}
