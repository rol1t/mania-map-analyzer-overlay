# Realtime Pause Coach

Pause Coach is an optional, deterministic diagnosis layer for the current osu!mania attempt. It collects a bounded timeline while the map is playing and publishes a snapshot when the game pauses or reaches results. It does not replace exact `.osr` replay analysis.

## Data source

The shipped `mania-map-analyser` adapter currently uses the existing Tosu v2 WebSocket at `/websocket/v2` and its HTTP state fallback at `/json/v2`. The adapter requests:

- `state.number` and `state.name`;
- `game.focused` and `game.paused`;
- beatmap identity and `beatmap.time.live`;
- play score, accuracy, combo, max combo, health, mods, failed state;
- cumulative hit counts, `hitErrorArray`, and `unstableRate` when present.

The adapter does not assume that `/websocket/v2/precise` or key-state/object correlation is available. If a future Tosu integration supplies those fields, the realtime domain accepts column and section inputs without changing the widget contract.

## Realtime source of truth

The native application path is authoritative:

`Tosu v2 payload → TosuRealtimeCollector → RealtimePlayAnalyzer → RealtimeAnalysisSnapshot → OverlayViewState`.

Raw Tosu JSON is normalized by
`src/Avalonia/Infrastructure/Tosu/TosuRealtimeCollector.cs`. The normalized
sample then crosses into `src/RealtimeAnalysis`, where `RealtimePlayAnalyzer`
owns attempt lifecycle, bounded windows, insight thresholds and the native
Pause Coach snapshot. Exact `.osr` replay analysis remains in
`src/ReplayAnalysis` and does not own realtime lifecycle. The renderer receives
the resulting snapshot as a presenter payload and must not reinterpret it.

`assets/overlay/runtime/pause-coach.js` remains temporarily as a compatibility/preview fallback for documents that have not received an application view-state yet. The desktop overlay declares native authority before the adapter starts, so that surface never creates a second browser coach session. The fallback is not allowed to replace an authoritative native snapshot. Its options are injected from the same C# `PauseCoachOptions` contract (`window.__overlayPauseCoachOptions`) so thresholds and window sizes cannot drift during this transition. The fallback is covered by direct JavaScript fixture tests and is scheduled for removal after all presentation surfaces consume `OverlayViewState` directly.

The direct runtime checks are in `tests/Runtime/pause-coach-runtime.test.js` and use the realistic Tosu payload at `tests/fixtures/tosu-v2-pause-coach.json`.

## Provenance

Each snapshot includes an overall `dataQuality` and per-block quality labels:

- `Observed` — directly received from Tosu, such as score, state, combo or cumulative hit counts.
- `Reconstructed` — derived from observed arrays or bounded windows, such as mean timing and the recent section window.
- `Exact` — reserved for replay-backed data; realtime Pause Coach does not claim this today.
- `Estimated` — reserved for future explicitly labelled estimates.
- `Unavailable` — no reliable source exists, currently including per-column hit attribution and canonical live pattern labels.

## Session lifecycle

`RealtimePlayAnalyzer` models an attempt as a bounded session. A session starts on gameplay, survives pause/resume, ends at results/menu/failure, and is replaced when the beatmap identity or cumulative counters positively reset. Partial Tosu packets that omit beatmap id/hash do not reset the attempt. Recent and baseline windows are keyed by map/gameplay time, so wall-clock time spent paused cannot age telemetry out. Replay playback and spectating are explicitly marked unavailable. The compatibility browser runtime follows the same contract while it remains enabled.

## Insight rules

The first implementation emits at most four deduplicated findings, ranked by severity and confidence:

- early or late timing bias;
- timing instability / UR increase;
- recent accuracy drop;
- recent miss spike;
- collapse in the reconstructed recent section;
- an explicit insufficient-data notice.

Every finding carries a title, description, evidence, confidence and data quality. No per-column or pattern claim is generated when Tosu cannot support it.

## Visual presentation

`companella-replay` is the shipped Pause Coach presentation. It renders the
primary finding, secondary findings and large metric cards below replay
information. Settings that reference the retired standalone Pause Coach
presets are migrated to `companella-replay`.
