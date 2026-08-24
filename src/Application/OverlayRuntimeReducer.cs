using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>
/// Pure reducer for application runtime state. It intentionally has no
/// dispatcher, WebView, window, or network dependencies.
/// </summary>
public static class OverlayRuntimeReducer
{
    public static OverlayRuntimeState Apply(
        OverlayRuntimeState current,
        OverlayRuntimeEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(runtimeEvent);

        if (runtimeEvent.Sequence <= current.LastEventSequence)
        {
            return current;
        }

        OverlayRuntimeState next = runtimeEvent switch
        {
            RealtimeTelemetryReceived telemetry => ApplyRealtime(current, telemetry.Telemetry),
            AnalysisSnapshotReceived analysis => ApplyAnalysis(current, analysis.Snapshot),
            PresentationAvailabilityChanged presentation => current with
            {
                PresentationReady = presentation.Ready,
                PresentationVisible = presentation.Visible
            },
            OverlayModeChanged mode => current with { OverlayMode = mode.Enabled },
            OsuWindowStateChanged window => current with { OsuWindowMinimized = window.Minimized },
            VisibilityPolicyChanged policy => current with
            {
                VisibilityPolicy = OverlayVisibilityPolicy.Normalize(policy.Policy)
            },
            RuntimeReset => OverlayRuntimeState.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(runtimeEvent), runtimeEvent, "Unknown runtime event.")
        };

        return next with
        {
            Version = current.Version + 1,
            LastEventSequence = runtimeEvent.Sequence
        };
    }

    private static OverlayRuntimeState ApplyRealtime(
        OverlayRuntimeState current,
        TosuRealtimeTelemetry telemetry)
    {
        RealtimeAnalysisSnapshot snapshot = telemetry.Snapshot;
        string currentBeatmapId = !string.IsNullOrWhiteSpace(current.BeatmapId)
            ? current.BeatmapId
            : current.LatestAnalysis?.Beatmap.Id ?? string.Empty;
        bool beatmapChanged = !string.IsNullOrWhiteSpace(snapshot.BeatmapId)
            && (string.IsNullOrWhiteSpace(currentBeatmapId)
                || !string.Equals(currentBeatmapId, snapshot.BeatmapId, StringComparison.Ordinal));
        bool isPlaying = snapshot.State is RealtimePlayState.Playing or RealtimePlayState.Paused;
        bool? isPaused = snapshot.State switch
        {
            RealtimePlayState.Playing => false,
            RealtimePlayState.Paused => true,
            _ => null
        };
        AnalysisSnapshot? pendingAnalysis = current.PendingAnalysis;
        bool pendingMatchesRealtime = pendingAnalysis is not null
            && !string.IsNullOrWhiteSpace(snapshot.BeatmapId)
            && string.Equals(
                pendingAnalysis.Beatmap.Id,
                snapshot.BeatmapId,
                StringComparison.Ordinal);
        // A song-select transition may briefly report an unrelated carousel
        // entry before returning to the selected map. Keep the last completed
        // analysis until a matching replacement is available: the composer
        // will not combine it with a different realtime beatmap, while a
        // return to the original id can reuse it without requiring another
        // headless run.
        AnalysisSnapshot? latestAnalysis = beatmapChanged && pendingMatchesRealtime
            ? pendingAnalysis
            : current.LatestAnalysis;

        return current with
        {
            BeatmapGeneration = beatmapChanged ? current.BeatmapGeneration + 1 : current.BeatmapGeneration,
            GameplayStateKnown = snapshot.State != RealtimePlayState.Unknown,
            IsPlaying = isPlaying,
            IsPaused = isPaused,
            GameplayState = snapshot.State,
            BeatmapId = snapshot.BeatmapId,
            SessionId = snapshot.SessionId,
            LastRealtimeSource = telemetry.Source,
            LatestRealtime = snapshot,
            LatestAnalysis = latestAnalysis,
            // Tosu can briefly report several carousel entries while the
            // user changes selection. Do not discard an already completed
            // headless result merely because an unrelated intermediate id
            // arrived: the analyzer caches that result and may not emit it a
            // second time when the intended map becomes current. Keep it
            // pending until its own beatmap is confirmed (or a newer
            // analysis result replaces it).
            PendingAnalysis = pendingMatchesRealtime ? null : pendingAnalysis
        };
    }

    private static OverlayRuntimeState ApplyAnalysis(
        OverlayRuntimeState current,
        AnalysisSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string analysisBeatmapId = snapshot.Beatmap.Id;
        if (!string.IsNullOrWhiteSpace(current.BeatmapId)
            && !string.IsNullOrWhiteSpace(analysisBeatmapId)
            && !string.Equals(current.BeatmapId, analysisBeatmapId, StringComparison.Ordinal))
        {
            // Headless polling and realtime polling are independent. The
            // analyzer can finish the newly selected map a few milliseconds
            // before the native realtime source reports its identity. Keep
            // that result out of the active presentation slot until realtime
            // confirms it; this also prevents a late previous-map completion
            // from rolling the current card back.
            return current with
            {
                PendingAnalysis = snapshot
            };
        }

        if (!string.IsNullOrWhiteSpace(current.BeatmapId)
            && string.IsNullOrWhiteSpace(analysisBeatmapId)
            && current.LatestAnalysis is not null)
        {
            // Identity-free partial analysis cannot displace a current
            // beatmap's authoritative analysis slot.
            return current;
        }

        return current with
        {
            LatestAnalysis = snapshot,
            PendingAnalysis = null
        };
    }
}
