# osu!mania Replay Analysis — Research and Implementation TODO

## Decision

Replay analysis belongs in this repository as a first-party, isolated analyzer domain. It must not be implemented in preset CSS, overlay HTML, or Avalonia UI code.

The existing analyzer composition model can combine map-side metrics from ManiaMapAnalyser with performance-side metrics from replay analysis in one widget. The replay domain should remain independently testable and removable, so it can be extracted into a separate package later if it gains external consumers.

## Evidence status

- **Verified** — directly documented by an upstream API or source file.
- **Candidate** — supported by an external library claim and requires fixture validation before adoption.
- **Experiment required** — not sufficiently represented by the documented API.

## Data-source capability matrix

| Capability | tosu v2 | tosu v2 precise | stable `.osr` | lazer replay | Decision |
| --- | --- | --- | --- | --- | --- |
| Current beatmap, gameplay state, aggregate score and judgements | Verified | — | After parsing | After parsing | Use tosu for live context. |
| Current map time | — | Verified | Replay frame time | Replay frame time | Use a map-time clock internally. |
| Current key state | — | Verified for documented generic key overlay fields | Reconstructable from frames | Candidate | Do not assume it maps directly to mania columns. |
| Individual input transitions | Not exposed | Experiment required: poll/diff state may lose transitions | Reconstructable from encoded key-state frames | Candidate | Stable replay is the first authoritative input source. |
| Individual hit errors, object identity and column | Not exposed | Only an undocumented association is absent from the payload | Needs re-judge | Needs re-judge | Build a judge; never infer exact columns from aggregate hit errors. |
| LN head and release events | Not exposed | Not exposed | Needs re-judge from press/release transitions | Candidate | Treat as a separate later milestone. |
| Completed-play reconstruction from tosu alone | Not available | Not available | N/A | N/A | Locate/import the saved replay file instead. |
| Replay-watch analysis | State is available | Clock/key overlay may be available | Import the watched replay when accessible | Candidate | Research separately after post-play MVP. |

The documented precise payload contains `currentTime`, generic key states and a `hitErrors` array, but no event identifier, mania-column mapping, press/release timestamp history, or LN phase. It is therefore insufficient for authoritative per-column, pattern, or LN diagnostics on its own.

## Architectural boundary

```text
Replay source (.osr / lazer replay / provisional live telemetry)
        ↓
Replay input decoder
        ↓
NormalizedInputEvent stream
        ↓
Versioned replay judge + beatmap objects
        ↓
JudgedHitEvent stream
        ↓
Timing, column, LN, pattern and section analyzers
        ↓
Deterministic insights + semantic metrics
        ↓
IAnalyzerEngine → widget composition → preset renderer
```

### Required domain types

- `ReplayArtifact`: immutable source bytes, source kind, player/mod/version metadata and provenance.
- `ReplayInputEvent`: map-clock timestamp, column/key mask transition, `Press` or `Release`, sequence number and source precision.
- `JudgedHitEvent`: beatmap-object identity, expected map-clock time, observed input time when available, signed offset, column, phase (`Note`, `LnHead`, `LnTail`), judgement and confidence.
- `ReplayAnalysisSnapshot`: immutable result containing metrics, sections, diagnostics and the rule-set/version used.
- `ReplayAnalysisProvenance`: separates exact replay reconstruction from provisional live estimates.

`InputEvent` and `JudgedHitEvent` must stay separate. Chords, jacks, holds, missed notes and rate/mod differences make a one-to-one assumption incorrect.

## Reuse assessment

| Project | Potential use | Decision |
| --- | --- | --- |
| `replayviewer-js` | TypeScript parser and headless re-judge for stable/lazer replays, including mania. | First technical spike. Validate with golden fixtures before making it a runtime dependency. |
| `osu-parsers` | `.osu` and stable `.osr` decoding primitives. It does not itself calculate ruleset scoring. | Parser fallback/reference for the spike. |
| `ppy/osu` / lazer | Reference implementation for legacy-frame conversion and mania judgement behaviour. | Use as the correctness oracle and source of golden tests; do not embed the whole client. |
| ManiaMapAnalyser | Existing map difficulty, pattern and strain source. | Keep as a separate map-side engine; correlate its metrics with replay events only through the common semantic contract. |
| Companella / Mania Replay Master | Product ideas and replay-analysis UX references. | Inspect individual algorithms and licences before any code reuse. |
| abraker `osu_analysis` | Replay-data exploration and analytical terminology. | Reference only; Python/pandas is not the application runtime. |
| osu!Pacemaker | Overlay lifecycle and replay synchronization ideas. | UX reference only. |

## MVP boundary

The first user-facing version must be **post-play stable `.osr` analysis for 4K rice maps**. It should not claim live reconstruction or LN accuracy.

Included:

- explicit replay-file selection and automatic post-play discovery only after consent;
- map/replay hash validation and a clear mismatch error;
- normalized press events and re-judged standard notes;
- hit-error scatter, mean/median bias, standard deviation and UR;
- per-column note count, judgement distribution, bias, UR and misses;
- rolling metrics for 50 notes and 10-second windows;
- fixed section summary and evidence-based observations with sample-size thresholds;
- a result source badge: `Exact replay analysis`.

Excluded until validated:

- claiming per-column results from tosu live telemetry;
- LN head/tail scoring, lazer replay import, rate-changing replay parity and arbitrary key counts;
- automatic psychological explanations, LLM-generated conclusions and cloud upload;
- global keyboard hooks.

## Implementation TODO

### Phase 0 — evidence and fixtures

- [ ] Capture and archive anonymised raw packets from `/websocket/v2` and `/websocket/v2/precise` for stable and lazer: menu, gameplay, pause, result screen and replay watch.
- [ ] Document the observed field schema, update rate, resets and dropped-packet behaviour in `docs/tosu-telemetry-fixtures.md`.
- [ ] Obtain consented 4K fixture pairs: `.osu` + stable `.osr` for rice, chords, jacks, dense stream, simple LN and dropped LN.
- [ ] Record the original osu! client version, mods, clock rate and official result totals for every fixture.
- [ ] Establish the signed offset convention: `inputTime - objectTime`; negative is early, positive is late.
- [ ] Evaluate `replayviewer-js` and `osu-parsers` against the same fixture corpus; record licensing, bundle size and fidelity.
- [ ] Choose one parser/judge path only after golden tests reproduce score totals, combo and judgement counts for the rice fixtures.

**Exit criterion:** the project can prove which source is authoritative for every MVP metric and can reproduce a known rice replay without silent discrepancies.

### Phase 1 — replay domain and storage

- [ ] Add a pure `ReplayAnalysis.Core` project with no Avalonia, WebView, Tosu, filesystem or preset dependencies.
- [ ] Define `ReplayArtifact`, `ReplayInputEvent`, `JudgedHitEvent`, provenance and versioned result contracts.
- [ ] Add a source interface for stable files, lazer files and future live telemetry.
- [ ] Add map/replay identity validation and typed, user-visible errors for missing, mismatched or corrupt files.
- [ ] Store only explicit user-selected replay paths or in-memory bytes; do not scan or upload user files silently.
- [ ] Add JSON fixtures and deterministic tests for serialization, ordering, duplicate timestamps and key-mask transitions.

**Exit criterion:** any source can emit normalized input events without the analytics layer knowing whether it came from stable, lazer or live capture.

### Phase 2 — stable 4K rice re-judge

- [ ] Parse `.osu` notes into map-clock objects and columns.
- [ ] Decode stable `.osr` frame/key-mask data into press and release transitions.
- [ ] Implement a versioned, deterministic matcher for standard notes; distinguish unmatched input from a missed object.
- [ ] Preserve all ambiguous matches as diagnostics instead of silently picking a note.
- [ ] Validate score totals, combo and judgement counts against every golden fixture.
- [ ] Reject LN-containing maps for this phase with a clear `LN analysis is not enabled` status rather than producing misleading results.

**Exit criterion:** fixture results match the original result totals within an explicitly documented tolerance and every analysed hit has provenance.

### Phase 3 — timing, columns, sections and insights

- [ ] Implement mean, median, standard deviation, early/late ratio and `UR = standard deviation × 10` with documented inclusion rules.
- [ ] Implement per-column statistics for arbitrary key counts; hand grouping remains configurable and is not inferred automatically.
- [ ] Add rolling windows of 50 notes and 10 seconds, each labelled with its sample count.
- [ ] Add deterministic fixed-duration sections with local note count, accuracy, bias, UR and misses.
- [ ] Add conservative insight rules such as a column's UR compared with the median of eligible columns.
- [ ] Define sample-size and confidence thresholds; suppress categorical claims for insufficient samples.

**Exit criterion:** every visual value links to a filtered set of `JudgedHitEvent` records and every insight contains its evidence and confidence.

### Phase 4 — application and widget integration

- [ ] Implement `ReplayAnalysisEngine : IAnalyzerEngine` using the typed core contracts.
- [ ] Expose replay metrics through semantic ids such as `replay.timing.ur`, `replay.column.3.biasMs` and `replay.section.*`.
- [ ] Add a user configuration for post-play analysis and selected replay source; keep it separate from visual presets.
- [ ] Allow widget bindings to combine replay metrics with ManiaMapAnalyser metrics (for example, local MSD versus rolling UR).
- [ ] Add a results view first; keep the in-game widget compact and focused on a small set of selected metrics.
- [ ] Log and show parser/judge/version failures instead of hiding them behind default values.

**Exit criterion:** a user can analyse one local stable replay and view a compact overlay summary plus a detailed post-play panel.

### Phase 5 — long notes, rate, mods and lazer

- [ ] Implement LN head/body/tail state tracking with a separate test matrix for legacy scoring and ScoreV2/lazer behaviour.
- [ ] Analyse head and release offsets separately; report dropped holds as a distinct event type.
- [ ] Add rate/mod normalisation and record both audio time and map time where required.
- [ ] Implement lazer replay ingestion only after obtaining stable, version-pinned fixture coverage.
- [ ] Add explicit compatibility policies for client version and unsupported mods rather than guessing.

**Exit criterion:** LN and lazer outputs are marked exact only when their applicable scoring-policy fixtures pass.

### Phase 6 — provisional live mode

- [ ] Build a `TosuLiveReplaySource` that records only documented telemetry and marks every output provisional.
- [ ] Use live mode for current score, aggregate UR, recent hit-error display and map progress.
- [ ] Do not display per-column, exact object offset, LN release or pattern-performance conclusions unless a validated event source exists.
- [ ] Use bounded ring buffers, backpressure and background processing so the overlay cannot affect gameplay.
- [ ] Finalise or replace provisional results with replay-file analysis after the play.

**Exit criterion:** live mode remains useful without making unsupported precision claims or causing measurable game/overlay stutter.

### Phase 7 — pattern correlation and advanced diagnostics

- [ ] Add map-side pattern/strain annotations through ManiaMapAnalyser or a dedicated classifier.
- [ ] Allow multiple pattern memberships with weights; do not force every note into one label.
- [ ] Correlate event metrics with NPS, pattern, column and local difficulty only when sample thresholds are met.
- [ ] Add evidence-first statements such as `minijacks contain 54% of misses from 220 eligible notes`.
- [ ] Add opt-in comparison across a user's own stored analyses only after privacy/storage design is approved.

## Quality, performance and safety checklist

- [ ] Every parser, matcher and source failure is logged and shown with a recoverable next action.
- [ ] No automatic replay-folder scan, global input hook or network upload without explicit user consent.
- [ ] No analysis runs on the UI thread; large maps are processed off-thread with cancellation.
- [ ] Use bounded memory for live buffers and retain raw replay data only as long as required by the chosen user setting.
- [ ] Add regression fixtures before changing ruleset behaviour, matching policy or statistic definitions.
- [ ] Benchmark large maps before enabling automatic analysis at map end.

## Extraction criterion

Keep this domain in the current solution until at least one external application needs the replay core or a stable public package API exists. If that happens, extract `ReplayAnalysis.Core` with its fixtures and golden tests as a separately versioned library; leave the current project as the overlay host and integration consumer.

## Sources consulted

- [tosu v2 API payload](https://github.com/tosuapp/tosu/wiki/v2-websocket-api-response)
- [tosu precise API payload](https://github.com/tosuapp/tosu/wiki/v2-precise-websocket-api-response)
- [tosu changelog](https://github.com/tosuapp/tosu/blob/master/CHANGELOG.md)
- [osu!lazer legacy replay decoder](https://github.com/ppy/osu/blob/master/osu.Game/Scoring/Legacy/LegacyScoreDecoder.cs)
- [osu!mania judgement mechanics](https://osu.ppy.sh/wiki/en/Gameplay/Judgement/osu!mania)
- [replayviewer-js](https://github.com/daladal/replayviewer-js)
- [osu-parsers](https://github.com/kionell/osu-parsers)
- [ManiaMapAnalyser](https://github.com/LeoBlackMT/osumania_map_analyser)
- [Companella](https://github.com/Leinadix/companella)
- [abraker osu_analysis announcement](https://osu.ppy.sh/community/forums/topics/2025063)
- [osu!Pacemaker](https://osu.ppy.sh/community/forums/topics/2219431)
