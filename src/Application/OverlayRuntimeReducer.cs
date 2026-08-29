using ManiaMapAnalyzerOverlay.Core.Analysis;
using ManiaMapAnalyzerOverlay.RealtimeAnalysis;

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

        OverlayRuntimeRejection? analysisRejection = DescribeAnalysisRejection(current, runtimeEvent);
        if (analysisRejection is not null)
        {
            return analysisRejection;
        }

        OverlayRuntimeRejection? replayRejection = DescribeReplayRejection(current, runtimeEvent);
        if (replayRejection is not null)
        {
            return replayRejection;
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
            AnalysisRequestStarted started
                when started.BeatmapGeneration > 0
                    && current.BeatmapGeneration > started.BeatmapGeneration
                => new OverlayRuntimeRejection(
                    OverlayRuntimeRejectionKind.StaleAnalysisBeatmapGeneration,
                    runtimeEvent.Sequence,
                    current.LastEventSequence,
                    started.BeatmapGeneration,
                    current.BeatmapGeneration,
                    started.BeatmapId,
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
            AnalysisRequestStarted started => ApplyAnalysisRequestStarted(current, started),
            AnalysisSnapshotReceived analysis => ApplyAnalysis(current, analysis),
            AnalysisRequestFailed failure => ApplyAnalysisRequestFailed(current, failure),
            ReplayAnalysisRequestStarted replayStarted => ApplyReplayRequestStarted(current, replayStarted),
            ReplayAnalysisCompleted replayCompleted => ApplyReplayCompleted(current, replayCompleted),
            ReplayAnalysisRequestFailed replayFailure => ApplyReplayRequestFailed(current, replayFailure),
            ReplayAnalysisRequestCancelled replayCancelled => ApplyReplayRequestCancelled(current, replayCancelled),
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
        RealtimeTelemetryUpdate telemetry)
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
        AnalysisRequestSlot? analysisRequest = current.AnalysisRequest;
        AnalysisSnapshot? pendingAnalysis = analysisRequest?.Status == AnalysisRequestStatus.Pending
            ? analysisRequest.Snapshot
            : null;
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

        if (pendingMatchesRealtime && analysisRequest is not null)
        {
            analysisRequest = analysisRequest with
            {
                Status = AnalysisRequestStatus.Completed,
                Snapshot = pendingAnalysis,
                FailureCode = null,
                FailureMessage = null
            };
        }

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
            AnalysisRequest = analysisRequest
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
        AnalysisRequestStarted started => started.BeatmapId,
        AnalysisRequestFailed failure => failure.BeatmapId,
        ReplayAnalysisRequestStarted replayStarted => replayStarted.BeatmapId,
        ReplayAnalysisCompleted replayCompleted => replayCompleted.BeatmapId,
        ReplayAnalysisRequestFailed replayFailure => replayFailure.BeatmapId,
        ReplayAnalysisRequestCancelled replayCancelled => replayCancelled.BeatmapId,
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

    private static OverlayRuntimeState ApplyAnalysisRequestStarted(
        OverlayRuntimeState current,
        AnalysisRequestStarted started)
    {
        if (!started.RequestId.IsValid
            || (started.BeatmapGeneration > 0
                && current.BeatmapGeneration > started.BeatmapGeneration)
            || (current.AnalysisRequest is { IsVersioned: true } active
                && started.RequestId.Value <= active.RequestId.Value))
        {
            return current;
        }

        return current with
        {
            AnalysisRequest = new AnalysisRequestSlot(
                started.RequestId,
                started.BeatmapGeneration,
                started.BeatmapId?.Trim() ?? string.Empty,
                started.ConfigurationIdentity ?? string.Empty,
                AnalysisRequestStatus.Running)
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
            // advanced to another beatmap. Keep the legacy compatibility path
            // subject to the same generation guard as versioned requests.
            return current;
        }

        if (analysis.RequestId is { } requestId && requestId.IsValid)
        {
            return ApplyVersionedAnalysis(current, analysis, requestId, snapshot);
        }

        // Anonymous compatibility snapshots remain observational after native
        // authority is established. The host may explicitly activate the DOM
        // fallback when headless presentation is unavailable; that transition
        // replaces the old causal slot so late native completions stay stale.
        bool isExplicitBrowserFallback = analysis.Producer == AnalysisSnapshotProducer.BrowserFallback;
        if (current.AnalysisRequest?.IsVersioned == true && !isExplicitBrowserFallback)
        {
            return current;
        }

        OverlayRuntimeState next = ApplyAnalysisPayload(current, snapshot);
        if (ReferenceEquals(next, current))
        {
            return current;
        }

        bool pending = !ReferenceEquals(next.LatestAnalysis, snapshot);
        return next with
        {
            AnalysisRequest = new AnalysisRequestSlot(
                default,
                analysis.BeatmapGeneration,
                snapshot.Beatmap.Id?.Trim() ?? string.Empty,
                isExplicitBrowserFallback ? "browser-fallback" : "legacy",
                pending ? AnalysisRequestStatus.Pending : AnalysisRequestStatus.Completed,
                snapshot)
        };
    }

    private static OverlayRuntimeState ApplyVersionedAnalysis(
        OverlayRuntimeState current,
        AnalysisSnapshotReceived analysis,
        AnalysisRequestId requestId,
        AnalysisSnapshot snapshot)
    {
        AnalysisRequestSlot? slot = current.AnalysisRequest;
        if (slot is null
            || !slot.IsVersioned
            || slot.RequestId != requestId
            || slot.Status == AnalysisRequestStatus.Failed)
        {
            return current;
        }

        if (slot.BeatmapGeneration > 0
            && current.BeatmapGeneration > slot.BeatmapGeneration)
        {
            return current;
        }

        if (analysis.BeatmapGeneration > 0
            && current.BeatmapGeneration > analysis.BeatmapGeneration)
        {
            return current;
        }

        string analysisBeatmapId = snapshot.Beatmap.Id?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(slot.BeatmapId)
            && !string.IsNullOrWhiteSpace(analysisBeatmapId)
            && !string.Equals(slot.BeatmapId, analysisBeatmapId, StringComparison.Ordinal))
        {
            return current;
        }

        if (!string.Equals(
                analysis.ConfigurationIdentity ?? string.Empty,
                slot.ConfigurationIdentity,
                StringComparison.Ordinal))
        {
            return current;
        }

        OverlayRuntimeState next = ApplyAnalysisPayload(current, snapshot);
        if (ReferenceEquals(next, current))
        {
            return current;
        }

        bool pending = !ReferenceEquals(next.LatestAnalysis, snapshot);
        return next with
        {
            AnalysisRequest = slot with
            {
                Status = pending ? AnalysisRequestStatus.Pending : AnalysisRequestStatus.Completed,
                Snapshot = snapshot,
                FailureCode = null,
                FailureMessage = null
            }
        };
    }

    private static OverlayRuntimeState ApplyAnalysisPayload(
        OverlayRuntimeState current,
        AnalysisSnapshot snapshot)
    {
        string analysisBeatmapId = snapshot.Beatmap.Id?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(current.BeatmapId)
            && !string.IsNullOrWhiteSpace(analysisBeatmapId)
            && !string.Equals(current.BeatmapId, analysisBeatmapId, StringComparison.Ordinal))
        {
            return current with
            {
                AnalysisRequest = new AnalysisRequestSlot(
                    current.AnalysisRequest?.RequestId ?? default,
                    current.AnalysisRequest?.BeatmapGeneration ?? current.BeatmapGeneration,
                    analysisBeatmapId,
                    current.AnalysisRequest?.ConfigurationIdentity ?? "legacy",
                    AnalysisRequestStatus.Pending,
                    snapshot)
            };
        }

        if (!string.IsNullOrWhiteSpace(current.BeatmapId)
            && string.IsNullOrWhiteSpace(analysisBeatmapId)
            && current.LatestAnalysis is not null)
        {
            return current;
        }

        return current with
        {
            LatestAnalysis = snapshot
        };
    }

    private static OverlayRuntimeState ApplyAnalysisRequestFailed(
        OverlayRuntimeState current,
        AnalysisRequestFailed failure)
    {
        if (!failure.RequestId.IsValid)
        {
            return current;
        }

        AnalysisRequestSlot? slot = current.AnalysisRequest;
        if (slot is null
            || !slot.IsVersioned
            || slot.RequestId != failure.RequestId
            || slot.Status != AnalysisRequestStatus.Running
            || (slot.BeatmapGeneration > 0
                && current.BeatmapGeneration > slot.BeatmapGeneration)
            || (failure.BeatmapGeneration > 0
                && current.BeatmapGeneration > failure.BeatmapGeneration)
            || (!string.IsNullOrWhiteSpace(failure.BeatmapId)
                && !string.IsNullOrWhiteSpace(slot.BeatmapId)
                && !string.Equals(
                    failure.BeatmapId.Trim(),
                    slot.BeatmapId,
                    StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(failure.ConfigurationIdentity)
                && !string.Equals(
                    failure.ConfigurationIdentity,
                    slot.ConfigurationIdentity,
                    StringComparison.Ordinal)))
        {
            return current;
        }

        return current with
        {
            AnalysisRequest = slot with
            {
                Status = AnalysisRequestStatus.Failed,
                FailureCode = string.IsNullOrWhiteSpace(failure.FailureCode)
                    ? "analysis_failed"
                    : failure.FailureCode.Trim(),
                FailureMessage = failure.FailureMessage,
                Snapshot = null
            }
        };
    }

    private static OverlayRuntimeState ApplyReplayRequestStarted(
        OverlayRuntimeState current,
        ReplayAnalysisRequestStarted started)
    {
        if (!started.RequestId.IsValid
            || (started.BeatmapGeneration > 0
                && current.BeatmapGeneration > started.BeatmapGeneration)
            || (current.ReplayRequest is { IsVersioned: true } active
                && started.RequestId.Value <= active.RequestId.Value))
        {
            return current;
        }

        return current with
        {
            ReplayRequest = new ReplayAnalysisRequestSlot(
                started.RequestId,
                started.BeatmapGeneration,
                started.BeatmapId?.Trim() ?? string.Empty,
                started.BeatmapHash?.Trim() ?? string.Empty,
                ReplayRequestStatus.Running)
        };
    }

    private static OverlayRuntimeState ApplyReplayCompleted(
        OverlayRuntimeState current,
        ReplayAnalysisCompleted completed)
    {
        if (completed.Snapshot is null
            || DescribeReplayRejection(current, completed) is not null)
        {
            return current;
        }

        ReplayAnalysisRequestSlot slot = current.ReplayRequest!;
        return current with
        {
            ReplayRequest = slot with
            {
                Status = ReplayRequestStatus.Completed,
                Snapshot = completed.Snapshot,
                FailureCode = null,
                FailureMessage = null
            }
        };
    }

    private static OverlayRuntimeState ApplyReplayRequestFailed(
        OverlayRuntimeState current,
        ReplayAnalysisRequestFailed failure)
    {
        if (DescribeReplayRejection(current, failure) is not null)
        {
            return current;
        }

        ReplayAnalysisRequestSlot slot = current.ReplayRequest!;
        return current with
        {
            ReplayRequest = slot with
            {
                Status = ReplayRequestStatus.Failed,
                Snapshot = null,
                FailureCode = string.IsNullOrWhiteSpace(failure.FailureCode)
                    ? "replay.analysis_failed"
                    : failure.FailureCode.Trim(),
                FailureMessage = failure.FailureMessage
            }
        };
    }

    private static OverlayRuntimeState ApplyReplayRequestCancelled(
        OverlayRuntimeState current,
        ReplayAnalysisRequestCancelled cancelled)
    {
        if (DescribeReplayRejection(current, cancelled) is not null)
        {
            return current;
        }

        ReplayAnalysisRequestSlot slot = current.ReplayRequest!;
        return current with
        {
            ReplayRequest = slot with
            {
                Status = ReplayRequestStatus.Cancelled,
                Snapshot = null,
                FailureCode = "replay.cancelled",
                FailureMessage = cancelled.CancellationMessage
            }
        };
    }

    private static OverlayRuntimeRejection? DescribeReplayRejection(
        OverlayRuntimeState current,
        OverlayRuntimeEvent runtimeEvent)
    {
        switch (runtimeEvent)
        {
            case ReplayAnalysisRequestStarted started when !started.RequestId.IsValid:
                return new OverlayRuntimeRejection(
                    OverlayRuntimeRejectionKind.InvalidReplayRequestId,
                    runtimeEvent.Sequence,
                    current.LastEventSequence,
                    EventGeneration: started.BeatmapGeneration,
                    CurrentGeneration: current.BeatmapGeneration,
                    EventBeatmapId: started.BeatmapId,
                    CurrentBeatmapId: current.BeatmapId);
            case ReplayAnalysisRequestStarted started
                when started.BeatmapGeneration > 0
                    && current.BeatmapGeneration > started.BeatmapGeneration:
                return new OverlayRuntimeRejection(
                    OverlayRuntimeRejectionKind.StaleReplayBeatmapGeneration,
                    runtimeEvent.Sequence,
                    current.LastEventSequence,
                    EventGeneration: started.BeatmapGeneration,
                    CurrentGeneration: current.BeatmapGeneration,
                    EventBeatmapId: started.BeatmapId,
                    CurrentBeatmapId: current.BeatmapId);
            case ReplayAnalysisRequestStarted started
                when current.ReplayRequest is { IsVersioned: true } active
                    && started.RequestId.Value <= active.RequestId.Value:
                return new OverlayRuntimeRejection(
                    OverlayRuntimeRejectionKind.StaleReplayRequest,
                    runtimeEvent.Sequence,
                    current.LastEventSequence,
                    EventGeneration: started.BeatmapGeneration,
                    CurrentGeneration: active.BeatmapGeneration,
                    EventBeatmapId: started.BeatmapId,
                    CurrentBeatmapId: active.BeatmapId);
            case ReplayAnalysisCompleted completed:
                return DescribeReplayResultRejection(
                    current,
                    completed.RequestId,
                    completed.BeatmapGeneration,
                    completed.BeatmapId,
                    completed.BeatmapHash,
                    runtimeEvent.Sequence);
            case ReplayAnalysisRequestFailed failure:
                return DescribeReplayResultRejection(
                    current,
                    failure.RequestId,
                    failure.BeatmapGeneration,
                    failure.BeatmapId,
                    failure.BeatmapHash,
                    runtimeEvent.Sequence);
            case ReplayAnalysisRequestCancelled cancelled:
                return DescribeReplayResultRejection(
                    current,
                    cancelled.RequestId,
                    cancelled.BeatmapGeneration,
                    cancelled.BeatmapId,
                    cancelled.BeatmapHash,
                    runtimeEvent.Sequence);
        }

        return null;
    }

    private static OverlayRuntimeRejection? DescribeReplayResultRejection(
        OverlayRuntimeState current,
        ReplayRequestId requestId,
        long eventGeneration,
        string eventBeatmapId,
        string eventBeatmapHash,
        long eventSequence)
    {
        if (!requestId.IsValid)
        {
            return new OverlayRuntimeRejection(
                OverlayRuntimeRejectionKind.InvalidReplayRequestId,
                eventSequence,
                current.LastEventSequence,
                EventGeneration: eventGeneration,
                CurrentGeneration: current.BeatmapGeneration,
                EventBeatmapId: eventBeatmapId,
                CurrentBeatmapId: current.BeatmapId);
        }

        ReplayAnalysisRequestSlot? slot = current.ReplayRequest;
        if (slot is null)
        {
            return new OverlayRuntimeRejection(
                OverlayRuntimeRejectionKind.UnknownReplayRequest,
                eventSequence,
                current.LastEventSequence,
                EventGeneration: eventGeneration,
                CurrentGeneration: current.BeatmapGeneration,
                EventBeatmapId: eventBeatmapId,
                CurrentBeatmapId: current.BeatmapId);
        }

        if (slot.RequestId != requestId
            || !slot.IsVersioned
            || slot.Status != ReplayRequestStatus.Running)
        {
            return new OverlayRuntimeRejection(
                OverlayRuntimeRejectionKind.StaleReplayRequest,
                eventSequence,
                current.LastEventSequence,
                EventGeneration: eventGeneration,
                CurrentGeneration: slot.BeatmapGeneration,
                EventBeatmapId: eventBeatmapId,
                CurrentBeatmapId: slot.BeatmapId);
        }

        if ((slot.BeatmapGeneration > 0 && current.BeatmapGeneration > slot.BeatmapGeneration)
            || (eventGeneration > 0 && current.BeatmapGeneration > eventGeneration))
        {
            return new OverlayRuntimeRejection(
                OverlayRuntimeRejectionKind.StaleReplayBeatmapGeneration,
                eventSequence,
                current.LastEventSequence,
                EventGeneration: eventGeneration > 0 ? eventGeneration : slot.BeatmapGeneration,
                CurrentGeneration: current.BeatmapGeneration,
                EventBeatmapId: eventBeatmapId,
                CurrentBeatmapId: current.BeatmapId);
        }

        if (slot.BeatmapGeneration > 0
            && eventGeneration > 0
            && slot.BeatmapGeneration != eventGeneration)
        {
            return new OverlayRuntimeRejection(
                OverlayRuntimeRejectionKind.ReplayRequestMetadataMismatch,
                eventSequence,
                current.LastEventSequence,
                EventGeneration: eventGeneration,
                CurrentGeneration: slot.BeatmapGeneration,
                EventBeatmapId: eventBeatmapId,
                CurrentBeatmapId: slot.BeatmapId);
        }

        if (!string.IsNullOrWhiteSpace(eventBeatmapId)
            && !string.IsNullOrWhiteSpace(slot.BeatmapId)
            && !string.Equals(eventBeatmapId.Trim(), slot.BeatmapId, StringComparison.Ordinal))
        {
            return new OverlayRuntimeRejection(
                OverlayRuntimeRejectionKind.ReplayRequestMetadataMismatch,
                eventSequence,
                current.LastEventSequence,
                EventGeneration: eventGeneration,
                CurrentGeneration: slot.BeatmapGeneration,
                EventBeatmapId: eventBeatmapId,
                CurrentBeatmapId: slot.BeatmapId);
        }

        if (!string.IsNullOrWhiteSpace(eventBeatmapHash)
            && !string.IsNullOrWhiteSpace(slot.BeatmapHash)
            && !string.Equals(eventBeatmapHash.Trim(), slot.BeatmapHash, StringComparison.OrdinalIgnoreCase))
        {
            return new OverlayRuntimeRejection(
                OverlayRuntimeRejectionKind.ReplayRequestMetadataMismatch,
                eventSequence,
                current.LastEventSequence,
                EventGeneration: eventGeneration,
                CurrentGeneration: slot.BeatmapGeneration,
                EventBeatmapId: eventBeatmapId,
                CurrentBeatmapId: slot.BeatmapId);
        }

        if (!string.IsNullOrWhiteSpace(current.BeatmapId)
            && !string.IsNullOrWhiteSpace(slot.BeatmapId)
            && !string.Equals(current.BeatmapId, slot.BeatmapId, StringComparison.Ordinal))
        {
            return new OverlayRuntimeRejection(
                OverlayRuntimeRejectionKind.StaleReplayBeatmapGeneration,
                eventSequence,
                current.LastEventSequence,
                EventGeneration: slot.BeatmapGeneration,
                CurrentGeneration: current.BeatmapGeneration,
                EventBeatmapId: slot.BeatmapId,
                CurrentBeatmapId: current.BeatmapId);
        }

        return null;
    }

    private static OverlayRuntimeRejection? DescribeAnalysisRejection(
        OverlayRuntimeState current,
        OverlayRuntimeEvent runtimeEvent)
    {
        switch (runtimeEvent)
        {
            case AnalysisRequestStarted started when !started.RequestId.IsValid:
                return new OverlayRuntimeRejection(
                    OverlayRuntimeRejectionKind.InvalidAnalysisRequestId,
                    runtimeEvent.Sequence,
                    current.LastEventSequence,
                    EventBeatmapId: started.BeatmapId,
                    CurrentBeatmapId: current.BeatmapId);
            case AnalysisRequestStarted started
                when current.AnalysisRequest is { IsVersioned: true } active
                    && started.RequestId.Value <= active.RequestId.Value:
                return new OverlayRuntimeRejection(
                    OverlayRuntimeRejectionKind.StaleAnalysisRequest,
                    runtimeEvent.Sequence,
                    current.LastEventSequence,
                    EventGeneration: started.BeatmapGeneration,
                    CurrentGeneration: active.BeatmapGeneration,
                    EventBeatmapId: started.BeatmapId,
                    CurrentBeatmapId: active.BeatmapId);
            case AnalysisSnapshotReceived completion
                when completion.RequestId is null
                    && current.AnalysisRequest is { IsVersioned: true } active
                    && completion.Producer != AnalysisSnapshotProducer.BrowserFallback:
                return new OverlayRuntimeRejection(
                    OverlayRuntimeRejectionKind.StaleAnalysisRequest,
                    runtimeEvent.Sequence,
                    current.LastEventSequence,
                    EventGeneration: completion.BeatmapGeneration,
                    CurrentGeneration: active.BeatmapGeneration,
                    EventBeatmapId: completion.Snapshot.Beatmap.Id,
                    CurrentBeatmapId: active.BeatmapId);
            case AnalysisSnapshotReceived completion when completion.RequestId is { } requestId:
                if (!requestId.IsValid)
                {
                    return new OverlayRuntimeRejection(
                        OverlayRuntimeRejectionKind.InvalidAnalysisRequestId,
                        runtimeEvent.Sequence,
                        current.LastEventSequence,
                        EventBeatmapId: completion.Snapshot.Beatmap.Id,
                        CurrentBeatmapId: current.BeatmapId);
                }

                if (current.AnalysisRequest is null)
                {
                    return new OverlayRuntimeRejection(
                        OverlayRuntimeRejectionKind.UnknownAnalysisRequest,
                        runtimeEvent.Sequence,
                        current.LastEventSequence,
                        EventGeneration: completion.BeatmapGeneration,
                        CurrentGeneration: current.BeatmapGeneration,
                        EventBeatmapId: completion.Snapshot.Beatmap.Id,
                        CurrentBeatmapId: current.BeatmapId);
                }

                if (current.AnalysisRequest.RequestId != requestId
                    || !current.AnalysisRequest.IsVersioned
                    || current.AnalysisRequest.Status == AnalysisRequestStatus.Failed)
                {
                    return new OverlayRuntimeRejection(
                        OverlayRuntimeRejectionKind.StaleAnalysisRequest,
                        runtimeEvent.Sequence,
                        current.LastEventSequence,
                        EventGeneration: completion.BeatmapGeneration,
                        CurrentGeneration: current.AnalysisRequest.BeatmapGeneration,
                        EventBeatmapId: completion.Snapshot.Beatmap.Id,
                        CurrentBeatmapId: current.AnalysisRequest.BeatmapId);
                }

                if (current.AnalysisRequest.BeatmapGeneration > 0
                    && current.BeatmapGeneration > current.AnalysisRequest.BeatmapGeneration)
                {
                    return new OverlayRuntimeRejection(
                        OverlayRuntimeRejectionKind.StaleAnalysisBeatmapGeneration,
                        runtimeEvent.Sequence,
                        current.LastEventSequence,
                        EventGeneration: current.AnalysisRequest.BeatmapGeneration,
                        CurrentGeneration: current.BeatmapGeneration,
                        EventBeatmapId: completion.Snapshot.Beatmap.Id,
                        CurrentBeatmapId: current.BeatmapId);
                }

                if (completion.BeatmapGeneration > 0
                    && current.BeatmapGeneration > completion.BeatmapGeneration)
                {
                    return null;
                }

                string completionBeatmapId = completion.Snapshot.Beatmap.Id?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(current.AnalysisRequest.BeatmapId)
                    && !string.IsNullOrWhiteSpace(completionBeatmapId)
                    && !string.Equals(
                        current.AnalysisRequest.BeatmapId,
                        completionBeatmapId,
                        StringComparison.Ordinal))
                {
                    return new OverlayRuntimeRejection(
                        OverlayRuntimeRejectionKind.AnalysisRequestMetadataMismatch,
                        runtimeEvent.Sequence,
                        current.LastEventSequence,
                        EventGeneration: completion.BeatmapGeneration,
                        CurrentGeneration: current.AnalysisRequest.BeatmapGeneration,
                        EventBeatmapId: completionBeatmapId,
                        CurrentBeatmapId: current.AnalysisRequest.BeatmapId);
                }

                if (!string.Equals(
                        completion.ConfigurationIdentity ?? string.Empty,
                        current.AnalysisRequest.ConfigurationIdentity,
                        StringComparison.Ordinal))
                {
                    return new OverlayRuntimeRejection(
                        OverlayRuntimeRejectionKind.AnalysisRequestMetadataMismatch,
                        runtimeEvent.Sequence,
                        current.LastEventSequence,
                        EventGeneration: completion.BeatmapGeneration,
                        CurrentGeneration: current.AnalysisRequest.BeatmapGeneration,
                        EventBeatmapId: completionBeatmapId,
                        CurrentBeatmapId: current.AnalysisRequest.BeatmapId);
                }

                return null;
            case AnalysisRequestFailed failure when !failure.RequestId.IsValid:
                return new OverlayRuntimeRejection(
                    OverlayRuntimeRejectionKind.InvalidAnalysisRequestId,
                    runtimeEvent.Sequence,
                    current.LastEventSequence,
                    EventBeatmapId: failure.BeatmapId,
                    CurrentBeatmapId: current.BeatmapId);
            case AnalysisRequestFailed failure:
                if (current.AnalysisRequest is null)
                {
                    return new OverlayRuntimeRejection(
                        OverlayRuntimeRejectionKind.UnknownAnalysisRequest,
                        runtimeEvent.Sequence,
                        current.LastEventSequence,
                        EventBeatmapId: failure.BeatmapId,
                        CurrentBeatmapId: current.BeatmapId);
                }

                if (current.AnalysisRequest.RequestId != failure.RequestId
                    || current.AnalysisRequest.Status != AnalysisRequestStatus.Running)
                {
                    return new OverlayRuntimeRejection(
                        OverlayRuntimeRejectionKind.StaleAnalysisRequest,
                        runtimeEvent.Sequence,
                        current.LastEventSequence,
                        EventBeatmapId: failure.BeatmapId,
                        CurrentBeatmapId: current.AnalysisRequest.BeatmapId);
                }

                if (current.AnalysisRequest.BeatmapGeneration > 0
                    && current.BeatmapGeneration > current.AnalysisRequest.BeatmapGeneration)
                {
                    return new OverlayRuntimeRejection(
                        OverlayRuntimeRejectionKind.StaleAnalysisBeatmapGeneration,
                        runtimeEvent.Sequence,
                        current.LastEventSequence,
                        EventGeneration: current.AnalysisRequest.BeatmapGeneration,
                        CurrentGeneration: current.BeatmapGeneration,
                        EventBeatmapId: failure.BeatmapId,
                        CurrentBeatmapId: current.BeatmapId);
                }

                if (failure.BeatmapGeneration > 0
                    && current.BeatmapGeneration > failure.BeatmapGeneration)
                {
                    return new OverlayRuntimeRejection(
                        OverlayRuntimeRejectionKind.StaleAnalysisBeatmapGeneration,
                        runtimeEvent.Sequence,
                        current.LastEventSequence,
                        EventGeneration: failure.BeatmapGeneration,
                        CurrentGeneration: current.BeatmapGeneration,
                        EventBeatmapId: failure.BeatmapId,
                        CurrentBeatmapId: current.BeatmapId);
                }

                if (!string.IsNullOrWhiteSpace(failure.BeatmapId)
                    && !string.IsNullOrWhiteSpace(current.AnalysisRequest.BeatmapId)
                    && !string.Equals(
                        failure.BeatmapId.Trim(),
                        current.AnalysisRequest.BeatmapId,
                        StringComparison.Ordinal))
                {
                    return new OverlayRuntimeRejection(
                        OverlayRuntimeRejectionKind.AnalysisRequestMetadataMismatch,
                        runtimeEvent.Sequence,
                        current.LastEventSequence,
                        EventBeatmapId: failure.BeatmapId,
                        CurrentBeatmapId: current.AnalysisRequest.BeatmapId);
                }

                if (!string.IsNullOrWhiteSpace(failure.ConfigurationIdentity)
                    && !string.Equals(
                        failure.ConfigurationIdentity,
                        current.AnalysisRequest.ConfigurationIdentity,
                        StringComparison.Ordinal))
                {
                    return new OverlayRuntimeRejection(
                        OverlayRuntimeRejectionKind.AnalysisRequestMetadataMismatch,
                        runtimeEvent.Sequence,
                        current.LastEventSequence,
                        CurrentGeneration: current.AnalysisRequest.BeatmapGeneration,
                        EventBeatmapId: failure.BeatmapId,
                        CurrentBeatmapId: current.AnalysisRequest.BeatmapId);
                }

                return null;
        }

        return null;
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
