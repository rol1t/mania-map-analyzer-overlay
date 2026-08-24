using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>
/// Composes the current application slots into one immutable presentation
/// state. Realtime data is authoritative for gameplay and Pause Coach, while
/// headless/replay analysis remains in its own slots.
/// </summary>
public static class OverlayViewStateComposer
{
    public static OverlayViewState Compose(OverlayRuntimeState runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        AnalysisSnapshot? analysis = runtime.LatestAnalysis;
        RealtimeAnalysisSnapshot? realtime = runtime.LatestRealtime;
        string beatmapId = FirstNonEmpty(realtime?.BeatmapId, analysis?.Beatmap.Id, runtime.BeatmapId);
        // The reducer intentionally retains the previous completed analysis
        // through transient song-select ids. Never project its difficulty or
        // skills onto a different realtime map while it is being retained.
        bool analysisMatchesBeatmap = analysis is not null &&
            (string.IsNullOrWhiteSpace(analysis.Beatmap.Id) ||
             string.IsNullOrWhiteSpace(beatmapId) ||
             string.Equals(analysis.Beatmap.Id, beatmapId, StringComparison.Ordinal));
        AnalysisSnapshot? presentationAnalysis = analysisMatchesBeatmap ? analysis : null;
        var gameplay = runtime.GameplayStateKnown
            ? new GameplaySnapshot
            {
                State = runtime.GameplayState.ToString(),
                IsPlaying = runtime.IsPlaying,
                IsPaused = runtime.IsPaused
            }
            : presentationAnalysis?.Gameplay ?? new GameplaySnapshot();

        return new OverlayViewState
        {
            Version = runtime.Version,
            BeatmapGeneration = runtime.BeatmapGeneration,
            Producer = realtime is not null
                ? "native"
                : analysis is not null
                    ? "headless"
                    : "application",
            BeatmapId = beatmapId,
            Beatmap = presentationAnalysis?.Beatmap is { } beatmap &&
                      (string.IsNullOrWhiteSpace(beatmap.Id) || string.Equals(beatmap.Id, beatmapId, StringComparison.Ordinal))
                ? beatmap
                : new BeatmapSnapshot { Id = beatmapId },
            Gameplay = gameplay,
            Difficulty = presentationAnalysis?.Difficulty ?? new DifficultySnapshot(),
            Ranks = presentationAnalysis?.Ranks ?? Array.Empty<RankEstimate>(),
            Skills = presentationAnalysis?.Skills ?? Array.Empty<SkillMetric>(),
            Replay = presentationAnalysis?.Replay,
            RealtimeReplay = realtime is not null ? BuildRealtimeReplay(realtime) : null,
            PauseCoach = realtime is not null
                ? PauseCoachSnapshotMapper.ToSnapshot(realtime)
                : presentationAnalysis?.PauseCoach,
            Realtime = realtime,
            Presentation = new OverlayPresentationViewState
            {
                OverlayMode = runtime.OverlayMode,
                VisibilityPolicy = runtime.VisibilityPolicy,
                OsuWindowMinimized = runtime.OsuWindowMinimized,
                Ready = runtime.PresentationReady,
                Visible = runtime.PresentationVisible
            }
        };
    }

    /// <summary>
    /// Creates the compatibility AnalysisSnapshot consumed by the current
    /// renderer while the versioned presenter is being migrated. The
    /// composition itself remains application-owned; this adapter can be
    /// removed once the renderer accepts OverlayViewState directly.
    /// </summary>
    public static AnalysisSnapshot ToNativeAnalysisSnapshot(OverlayViewState view)
    {
        ArgumentNullException.ThrowIfNull(view);
        RealtimeAnalysisSnapshot realtime = view.Realtime
            ?? throw new ArgumentException("A native view state requires realtime data.", nameof(view));

        bool? isPlaying = realtime.State switch
        {
            RealtimePlayState.Playing or RealtimePlayState.Paused => true,
            RealtimePlayState.Menu or RealtimePlayState.Results or RealtimePlayState.Replay or RealtimePlayState.Spectating => false,
            _ => null
        };
        bool? isPaused = realtime.State switch
        {
            RealtimePlayState.Paused => true,
            RealtimePlayState.Playing => false,
            _ => null
        };

        return new AnalysisSnapshot
        {
            SchemaVersion = AnalysisSnapshot.CurrentSchemaVersion,
            SourceId = "mania-map-analyser",
            Beatmap = view.Beatmap,
            Gameplay = view.Gameplay with
            {
                State = realtime.State.ToString(),
                IsPlaying = isPlaying,
                IsPaused = isPaused,
                IsFocused = null
            },
            Difficulty = view.Difficulty,
            Ranks = view.Ranks,
            Skills = view.Skills,
            Replay = view.RealtimeReplay ?? view.Replay,
            PauseCoach = view.PauseCoach ?? PauseCoachSnapshotMapper.ToSnapshot(realtime),
            Extensions = new Dictionary<string, object?>
            {
                ["nativePauseCoach"] = true,
                ["realtimeProducer"] = "native",
                ["runtimeVersion"] = view.Version
            }
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static ReplayOverlaySnapshot BuildRealtimeReplay(RealtimeAnalysisSnapshot realtime) => new()
    {
        MapProgressMs = realtime.MapTimeMs,
        Score = realtime.Score,
        Accuracy = realtime.Accuracy,
        Ur = realtime.UnstableRate,
        MeanMs = realtime.Timing.MeanMs,
        MedianMs = realtime.Timing.MedianMs,
        SdMs = realtime.Timing.StandardDeviationMs,
        SampleCount = realtime.Timing.SampleCount,
        RecentOffsets = realtime.RecentOffsets,
        IsProvisional = true,
        Fidelity = "provisional",
        Reason = "Native Tosu v2 realtime telemetry."
    };
}
