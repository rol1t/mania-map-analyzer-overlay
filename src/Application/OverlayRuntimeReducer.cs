using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.ReplayAnalysis;

namespace ManiaMapAnalyzerOverlay.Application;

/// <summary>
/// Pure reducer for application runtime state. It intentionally has no
/// dispatcher, WebView, window, or network dependencies.
/// </summary>
public static class OverlayRuntimeReducer
{
    public static OverlayRuntimeRejection DescribeRejection(
        OverlayRuntimeState current,
        OverlayRuntimeEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(runtimeEvent);

        if (runtimeEvent.Sequence <= current.LastEventSequence)
        {
            return new OverlayRuntimeRejection(
                OverlayRuntimeRejectionKind.StaleSequence,
                runtimeEvent.Sequence,
                current.LastEventSequence);
        }

        return runtimeEvent switch
        {
            TosuConnectionChanged connection
                when current.TosuTransportGeneration > 0
                    && (connection.TransportGeneration == 0
                        || connection.TransportGeneration < current.TosuTransportGeneration)
                => new OverlayRuntimeRejection(
                    OverlayRuntimeRejectionKind.StaleTosuTransportGeneration,
                    runtimeEvent.Sequence,
                    current.LastEventSequence,
                    connection.TransportGeneration,
                    current.TosuTransportGeneration),
            PresentationAvailabilityChanged presentation
                when presentation.SurfaceGeneration > 0
                    && current.PresentationSurfaceGeneration > 0
                    && presentation.SurfaceGeneration < current.PresentationSurfaceGeneration
                => new OverlayRuntimeRejection(
                    OverlayRuntimeRejectionKind.StalePresentationSurfaceGeneration,
                    runtimeEvent.Sequence,
                    current.LastEventSequence,
                    presentation.SurfaceGeneration,
                    current.PresentationSurfaceGeneration),
            AnalysisSnapshotReceived analysis
                when analysis.BeatmapGeneration > 0
                    && current.BeatmapGeneration > analysis.BeatmapGeneration
                => new OverlayRuntimeRejection(
                    OverlayRuntimeRejectionKind.StaleAnalysisBeatmapGeneration,
                    runtimeEvent.Sequence,
                    current.LastEventSequence,
                    analysis.BeatmapGeneration,
                    current.BeatmapGeneration,
                    analysis.Snapshot.Beatmap.Id,
                    current.BeatmapId),
            _ => new OverlayRuntimeRejection(
                OverlayRuntimeRejectionKind.NoStateChange,
                runtimeEvent.Sequence,
                current.LastEventSequence,
                EventBeatmapId: GetEventBeatmapId(runtimeEvent),
                CurrentBeatmapId: current.BeatmapId)
        };
    }

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
            TosuConnectionChanged connection => ApplyTosuConnection(current, connection),
            AnalysisSnapshotReceived analysis => ApplyAnalysis(current, analysis),
            PresentationAvailabilityChanged presentation => ApplyPresentationAvailability(current, presentation),
            OverlayModeChanged mode => current with { OverlayMode = mode.Enabled },
            OsuWindowStateChanged window => current with { OsuWindowMinimized = window.Minimized },
            VisibilityPolicyChanged policy => current with
            {
                VisibilityPolicy = OverlayVisibilityPolicy.Normalize(policy.Policy)
            },
            RuntimeReset => OverlayRuntimeState.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(runtimeEvent), runtimeEvent, "Unknown runtime event.")
        };

        if (ReferenceEquals(next, current))
        {
            // RuntimeReset is an accepted lifecycle boundary even when the
            // state is already empty. Preserve the ordering/version contract
            // for that boundary; other reference-equal results are explicit
            // reducer rejections (for example stale surface feedback).
            if (runtimeEvent is RuntimeReset)
            {
                return current with
                {
                    Version = current.Version + 1,
                    LastEventSequence = runtimeEvent.Sequence
                };
            }

            return current;
        }

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
        RealtimeAnalysisSnapshot incoming = telemetry.Snapshot;
        string currentBeatmapId = !string.IsNullOrWhiteSpace(current.BeatmapId)
            ? current.BeatmapId
            : current.LatestAnalysis?.Beatmap.Id ?? string.Empty;
        bool sameKnownBeatmap = string.IsNullOrWhiteSpace(incoming.BeatmapId)
            || string.IsNullOrWhiteSpace(currentBeatmapId)
            || string.Equals(currentBeatmapId, incoming.BeatmapId, StringComparison.Ordinal);
        string effectiveBeatmapId = !string.IsNullOrWhiteSpace(incoming.BeatmapId)
            ? incoming.BeatmapId
            : currentBeatmapId;
        // A transport can deliver a partial frame after a complete frame (for
        // example while Tosu transitions between Play and pause). The raw
        // adapter normally fills these fields, but the application boundary
        // must remain safe for every producer: an omitted identity cannot
        // erase the active map or attempt and make the next view state look
        // like a new session.
        bool preserveSession = sameKnownBeatmap
            && incoming.State is RealtimePlayState.Playing
                or RealtimePlayState.Paused
                or RealtimePlayState.Results;
        string effectiveSessionId = !string.IsNullOrWhiteSpace(incoming.SessionId)
            ? incoming.SessionId
            : preserveSession
                ? FirstNonEmpty(current.SessionId, current.LatestRealtime?.SessionId)
                : string.Empty;
        RealtimeAnalysisSnapshot snapshot = incoming with
        {
            BeatmapId = effectiveBeatmapId,
            SessionId = effectiveSessionId
        };
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

    private static string? GetEventBeatmapId(OverlayRuntimeEvent runtimeEvent) => runtimeEvent switch
    {
        RealtimeTelemetryReceived telemetry => telemetry.Telemetry.Snapshot.BeatmapId,
        AnalysisSnapshotReceived analysis => analysis.Snapshot.Beatmap.Id,
        _ => null
    };

    private static OverlayRuntimeState ApplyTosuConnection(
        OverlayRuntimeState current,
        TosuConnectionChanged connection)
    {
        if (current.TosuTransportGeneration > 0 && connection.TransportGeneration == 0)
        {
            // An unversioned compatibility callback cannot prove that it
            // belongs to the current Tosu process. Once a versioned transport
            // exists, keep it authoritative rather than allowing a legacy
            // callback to roll the lifecycle backwards.
            return current;
        }

        if (connection.TransportGeneration > 0
            && current.TosuTransportGeneration > 0
            && connection.TransportGeneration < current.TosuTransportGeneration)
        {
            return current;
        }

        return current with
        {
            TosuConnection = connection.State,
            TosuTransportGeneration = Math.Max(
                current.TosuTransportGeneration,
                connection.TransportGeneration)
        };
    }

    private static OverlayRuntimeState ApplyAnalysis(
        OverlayRuntimeState current,
        AnalysisSnapshotReceived analysis)
    {
        AnalysisSnapshot snapshot = analysis.Snapshot;
        ArgumentNullException.ThrowIfNull(snapshot);

        if (analysis.BeatmapGeneration > 0
            && current.BeatmapGeneration > analysis.BeatmapGeneration)
        {
            // Analysis is asynchronous and can finish after Tosu has already
            // advanced to another beatmap. The beatmap id check below protects
            // the normal path, while this causal generation rejects a late
            // completion even when the payload is partial or identity-free.
            return current;
        }

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

    private static OverlayRuntimeState ApplyPresentationAvailability(
        OverlayRuntimeState current,
        PresentationAvailabilityChanged presentation)
    {
        if (presentation.SurfaceGeneration > 0
            && current.PresentationSurfaceGeneration > 0
            && presentation.SurfaceGeneration < current.PresentationSurfaceGeneration)
        {
            return current;
        }

        long generation = presentation.SurfaceGeneration > 0
            ? presentation.SurfaceGeneration
            : current.PresentationSurfaceGeneration;
        return current with
        {
            PresentationReady = presentation.Ready,
            PresentationVisible = presentation.Visible,
            PresentationSurfaceGeneration = generation
        };
    }
}
