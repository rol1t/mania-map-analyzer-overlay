using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>
/// Versioned presentation contract composed from independent runtime slots.
/// It is deliberately free of WebView, dispatcher, and platform types so a
/// presenter can be recreated without resetting the runtime attempt.
/// </summary>
public sealed record OverlayViewState
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Wire-contract version. This is independent from <see cref="Version"/>,
    /// which orders state instances within one presentation epoch.
    /// </summary>
    public int SchemaVersion
    {
        get; init;
    } = CurrentSchemaVersion;

    /// <summary>
    /// Changes on every application process start. Presentation documents can
    /// outlive the process, so Version alone is not a globally ordered value.
    /// </summary>
    public string PresentationEpoch { get; init; } = string.Empty;

    public long Version
    {
        get; init;
    }

    public long BeatmapGeneration
    {
        get; init;
    }

    public string Producer { get; init; } = "application";

    public string BeatmapId { get; init; } = string.Empty;

    public BeatmapSnapshot Beatmap { get; init; } = new();

    public GameplaySnapshot Gameplay { get; init; } = new();

    public DifficultySnapshot Difficulty { get; init; } = new();

    public IReadOnlyList<RankEstimate> Ranks { get; init; } = Array.Empty<RankEstimate>();

    public IReadOnlyList<SkillMetric> Skills { get; init; } = Array.Empty<SkillMetric>();

    public ReplayOverlaySnapshot? Replay
    {
        get; init;
    }

    /// <summary>
    /// Provisional replay metrics derived from the realtime attempt. This is
    /// separate from exact replay analysis so a live frame cannot overwrite
    /// an imported .osr result by accident.
    /// </summary>
    public ReplayOverlaySnapshot? RealtimeReplay
    {
        get; init;
    }

    public PauseCoachSnapshot? PauseCoach
    {
        get; init;
    }

    public RealtimeAnalysisSnapshot? Realtime
    {
        get; init;
    }

    public OverlayPresentationViewState Presentation { get; init; } = new();
}

public sealed record OverlayPresentationViewState
{
    public bool OverlayMode
    {
        get; init;
    }

    public string VisibilityPolicy { get; init; } = OverlayVisibilityPolicy.Always;

    public bool OsuWindowMinimized
    {
        get; init;
    }

    public bool Ready
    {
        get; init;
    }

    public bool Visible
    {
        get; init;
    }

    public long SurfaceGeneration
    {
        get; init;
    }
}
