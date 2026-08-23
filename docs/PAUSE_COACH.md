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

## Provenance

Each snapshot includes an overall `dataQuality` and per-block quality labels:

- `Observed` — directly received from Tosu, such as score, state, combo or cumulative hit counts.
- `Reconstructed` — derived from observed arrays or bounded windows, such as mean timing and the recent section window.
- `Exact` — reserved for replay-backed data; realtime Pause Coach does not claim this today.
- `Estimated` — reserved for future explicitly labelled estimates.
- `Unavailable` — no reliable source exists, currently including per-column hit attribution and canonical live pattern labels.

## Session lifecycle

`pause-coach.js` and `RealtimePlayAnalyzer` both model an attempt as a bounded session. A session starts on gameplay, survives pause/resume, ends at results/menu/failure, and is replaced when the beatmap, map time, score or cumulative counters reset. Replay playback and spectating are explicitly marked unavailable.

## Insight rules

The first implementation emits at most four deduplicated findings, ranked by severity and confidence:

- early or late timing bias;
- timing instability / UR increase;
- recent accuracy drop;
- recent miss spike;
- collapse in the reconstructed recent section;
- an explicit insufficient-data notice.

Every finding carries a title, description, evidence, confidence and data quality. No per-column or pattern claim is generated when Tosu cannot support it.

## Visual variants

Three standalone presets are included:

- `pause-coach-card` — evidence card with a primary finding, secondary findings and metric cards;
- `pause-coach-minimal` — compact strip for a small overlay window;
- `pause-coach-signal` — high-contrast severity-focused view.

The existing `companella-replay` preset also renders the Pause Coach block below replay information.
