# Architecture and migration roadmap

Status: approved for incremental migration; PRs A-D are implemented locally,
PR E native visibility/snapshot cutover is in progress, PR F view-state
composition has started locally, and the versioned transport bridge for PR G
now carries both native realtime and headless analysis through one publisher;
renderer compatibility handling remains, while the browser Pause Coach
producer now yields after native authority is established and native
view-state frames bypass producer reconciliation in the renderer. PR H has
started locally with a versioned static-file transport for Tosu fullscreen;
the fullscreen document polls the latest application view state and uses the
same renderer event as the desktop WebView. View-state composition now occurs
inside the serialized runtime coordinator, so MainWindow only forwards the
already-composed contract to presenters. PR I has started locally with a
dedicated offscreen WebView for the headless analyzer runtime.

The coordinator owns the native gameplay/visibility decision and accepted
realtime snapshot handoff during this cutover, while the legacy browser
presentation remains in place until the full runtime path is proven.

Approved: 2026-08-23.

This document records the architecture review prompted by PR #8,
`feature/realtime-pause-coach`. It is a migration contract, not approval for a
big-bang rewrite. Each implementation PR starts only after explicit approval
and must preserve user-visible behaviour.

## Objective

The goal is to reduce coupling, make state ownership explicit, and make the
important application lifecycle deterministic and testable without a real
WebView, Windows HWND, Tosu process, or osu! instance.

The governing rule is:

> A fact must have exactly one owner.

In particular:

- gameplay state has one application owner;
- beatmap identity has one application owner;
- Pause Coach has one canonical attempt/session;
- realtime metrics have one domain owner;
- overlay visibility is derived from runtime state;
- the rendered DOM owns no business state.

The migration is successful when changing overlay visibility cannot change
Pause Coach analysis, changing WebView lifecycle cannot reset an attempt, Tosu
payload changes are caught by adapter contract tests, and DOM/CSS changes
cannot alter application state.

## Current architecture

PR #8 exposed three overlapping runtime paths:

```mermaid
flowchart TD
    T[Tosu]

    T -->|HTTP /json/v2| MW[MainWindow]
    MW --> NC[TosuRealtimeCollector]
    NC --> CA[RealtimePlayAnalyzer C#]
    CA --> LP[LatestWinsSnapshotPublisher]
    LP --> WV[WebView InvokeScript]

    T -->|WebSocket and HTTP fallback| BA[adapter.js]
    BA --> JP[pause-coach.js]
    JP --> R[renderer.js]

    HC[HeadlessAnalysisController] --> TB[TosuBeatmapSource]
    TB --> HE[Headless analyzer hosted through WebView]
    HE --> HP[WebViewAnalysisSnapshotPresenter]
    HP --> R

    WV --> R
    R -->|mergeSnapshot authority rules| DOM[DOM]
    R -->|play pause focus size drag| MW
    MW --> VP[Visibility policy]
    MW --> WO[WindowsOverlayController]
```

The C# and JavaScript Pause Coach implementations independently own sessions,
retry detection, rolling windows, metrics, and insights. Native desktop mode
prefers the C# producer, while preview/fullscreen can use the browser producer.
The renderer then reconciles producers, headless analysis, and replay data.

This is the primary architectural blocker. Renderer reconciliation can reduce
visible symptoms, but it cannot make two independent state machines share one
canonical attempt.

## Problems by priority

### Blocker

1. C# and JavaScript both implement Pause Coach lifecycle and metrics.
2. `renderer.js` owns business reconciliation and producer authority.
3. `MainWindow` owns application state, presentation lifecycle, polling,
   native window behaviour, analysis, and user-interface concerns together.
4. No application-level test exercises the complete production workflow from
   normalized telemetry to desired visibility and delivered view state.

### High

1. Tosu state and payloads are normalized in C#, JavaScript, and a separate
   `TosuService` state path.
2. Beatmap identity is duplicated across Tosu, realtime, headless, snapshots,
   and renderer globals.
3. Headless, replay, and realtime results compete through a shared partial
   snapshot instead of independent application state slots.
4. The headless analyzer production host is coupled to presentation WebView
   navigation and recreation.
5. Fullscreen presentation still requires browser-side analysis and therefore
   needs a state transport before JavaScript business logic can be removed.

### Medium

1. `ReplayAnalysis` contains both deterministic domain behaviour and Tosu JSON
   infrastructure.
2. `Core` contains domain contracts, orchestration contracts, and presentation
   snapshot contracts.
3. `MainViewModel` creates infrastructure services instead of receiving them
   from a composition root.
4. Concurrency is coordinated through unrelated locks, semaphores, interlocked
   flags, dispatcher callbacks, and generation counters.
5. Logs are primarily free text rather than structured runtime transitions.

## State ownership

| Fact | Current owners | Target owner |
| --- | --- | --- |
| Gameplay state | C# normalizer, MainWindow flags, browser adapter | `OverlayRuntimeCoordinator` |
| Beatmap identity | Tosu source, collector, browser adapter, snapshots, renderer | `BeatmapState` |
| Attempt/session ID | C# analyzer and JavaScript runtime | C# Pause Coach domain engine |
| Realtime metrics | C# and JavaScript | C# Pause Coach domain engine |
| Headless result | controller, window cache, renderer | versioned `DifficultyAnalysisState` |
| Replay result | replay session and renderer | versioned `ReplayAnalysisState` |
| Desired visibility | MainWindow flags and native state | pure application derivation |
| Browser readiness | MainWindow and publisher | `PresentationSurfaceState` |
| Latest rendered state | native publisher and JavaScript globals | application presenter/coalescer |
| DOM | renderer globals and elements | no business ownership |

## Target architecture

The target is a modular monolith with inward dependency direction:

```mermaid
flowchart TD
    TS[IRealtimeTelemetrySource]
    BA[IBeatmapAnalysisService]
    RA[IReplayAnalysisService]
    WE[Window environment events]

    TS --> Q[Serialized runtime event loop]
    BA --> Q
    RA --> Q
    WE --> Q

    Q --> PE[PauseCoachEngine]
    PE --> RS[OverlayRuntimeState]
    Q --> RS

    RS --> VM[OverlayViewStateMapper]
    VM --> VS[Versioned OverlayViewState]

    VS --> WP[IOverlayPresenter]
    RS --> OW[IOverlayWindow]

    WP --> WEB[WebView renderer]
    WP --> FS[Fullscreen state sink]
    OW --> WIN[WindowsOverlayController]
```

```text
Avalonia / Infrastructure
        -> Application
        -> Core + ReplayAnalysis
```

The WebView, Tosu JSON, filesystem, Dispatcher, and Windows APIs must not cross
into Application or Domain.

## Domain boundary

`RealtimeTelemetrySample` and `RealtimePlayAnalyzer` are the foundation for the
authoritative C# Pause Coach implementation. The domain owns:

- attempt lifecycle and canonical attempt ID;
- map change and retry detection;
- pause, resume, result, and failure transitions;
- gameplay-time rolling windows;
- judgement deltas and accuracy calculations;
- timing statistics and insight generation;
- bounded history;
- replay/spectating unavailability.

Raw Tosu JSON must be translated before entering this boundary. JavaScript
must not reproduce these rules.

## Application runtime state

The proposed immutable state contains:

```text
OverlayRuntimeState
    RuntimeVersion
    ConnectionState
    BeatmapState + BeatmapGeneration
    GameplayState
    RealtimeAttemptState
    DifficultyAnalysisState
    ReplayAnalysisState
    OverlayModeState
    WindowEnvironmentState
    PresentationSurfaceState
    SettingsSnapshot
```

Visibility is derived from gameplay, preset policy, overlay mode, and the osu!
window environment. It is not synchronized through multiple mutable booleans.

Headless, replay, and realtime data occupy independent state slots. The
application composes them into one presentation contract; they never replace
one another directly.

## Events and concurrency

Runtime state changes should be serialized through one explicit event loop,
preferably `Channel<OverlayRuntimeEvent>` with one consumer.

Initial event set:

- `TelemetryReceived`;
- `TelemetryDisconnected`;
- `BeatmapAnalysisCompleted`;
- `ReplayAnalysisCompleted`;
- `OverlayModeEntered` / `OverlayModeExited`;
- `PresentationCreated` / `PresentationReady` /
  `PresentationDestroyed`;
- `OsuProcessChanged`;
- `OsuWindowMinimizedChanged`;
- `PresetChanged`;
- `ScaleChanged`;
- `ApplicationStopping`.

Rules:

- only the event-loop consumer mutates runtime state;
- UI, Tosu, WebView, and Windows callbacks enqueue events only;
- slow I/O effects never block the event loop;
- async completions include the beatmap or presentation generation that
  created them;
- stale completions are rejected deterministically;
- gameplay telemetry is not blindly dropped or coalesced;
- presentation output uses latest-wins coalescing;
- cancellation is owned by the coordinator and its child lifecycles.

`RuntimeVersion` increases for accepted transitions. `ViewStateVersion`
increases only when the presentation contract changes.

## Presentation contract

JavaScript should receive an explicit `OverlayViewState` containing only data
needed for rendering:

```text
OverlayViewState
    ViewStateVersion
    BeatmapViewState
    DifficultyViewState
    ReplayViewState
    PauseCoachViewState
    PresentationMetadata
```

Renderer responsibilities are limited to rendering these blocks, honoring the
version, and reporting presentation measurements or input gestures. It does
not parse Tosu, create sessions, calculate insights, or merge producers.

WebView recreation changes only the presentation generation. Runtime state and
the active Pause Coach attempt survive, and the latest view state is replayed
when the new document becomes ready.

## Headless and replay relationship

On a beatmap transition:

1. `BeatmapGeneration` increases.
2. The Pause Coach domain receives the normalized transition.
3. Headless analysis starts with the new beatmap key and generation.
4. A completion is accepted only if both still match current state.
5. The view-state mapper composes only current-generation results.

Replay analysis follows the same versioned completion rule. The renderer never
decides whether an analysis result is stale.

## Application ports

Only boundaries that isolate real volatility should become interfaces:

- `IRealtimeTelemetrySource`;
- `IBeatmapAnalysisService`;
- `IReplayAnalysisService`;
- `IOverlayPresenter`;
- `IOverlayWindow`;
- `IClock` when wall-clock behaviour must be controlled in tests;
- structured runtime diagnostics.

No interface-per-class, mediator framework, event sourcing, CQRS framework, or
general-purpose dependency injection container is planned.

## Solution structure

One new assembly is justified:

```text
src/Core
src/Application
src/ReplayAnalysis
src/Avalonia
src/Updater
```

- `Core`: shared domain and analyzer contracts.
- `Application`: runtime state, events, coordinator, ports, versioning, and
  view-state composition.
- `ReplayAnalysis`: deterministic replay and current Pause Coach domain logic.
- `Avalonia`: composition root, ViewModels/Views, Tosu/WebView/filesystem
  implementations, and Windows adapters.
- `Updater`: unchanged.

A separate Infrastructure assembly is not required initially. Infrastructure
folders inside Avalonia are sufficient until another host needs to reuse them.
The migration must not begin with project/folder renames.

## Existing components to preserve

- `RealtimeTelemetrySample`;
- `RealtimePlayAnalyzer` and `PauseCoachInsightEngine`;
- `OverlayVisibilityPolicy`;
- `LatestWinsSnapshotPublisher` semantics and tests;
- `AnalysisRunScope`, execution generations, and typed headless keys;
- `IAnalyzerEngine`, `IAnalyzerScriptHost`, `ITosuBeatmapSource`, and
  `IAnalysisSnapshotPresenter` boundaries;
- replay parsing, judging, rolling windows, and insight logic;
- `WindowsOverlayController` as a platform adapter;
- preset catalog, templates, and CSS assets.

## Code to remove after proven replacement

- Pause Coach business logic in `assets/overlay/runtime/pause-coach.js`;
- Pause Coach processing and session creation in `adapter.js`;
- browser-side Tosu polling/WebSocket as a Pause Coach source;
- `renderer.mergeSnapshot()` authority and reconciliation rules;
- `window.__overlayNativePauseCoachSnapshot`;
- browser-generated Pause Coach session IDs;
- MainWindow gameplay flags and polling/publisher plumbing;
- browser play/pause messages that mutate application gameplay state;
- duplicate `TosuService.GetGameplayStateAsync` normalization;
- direct headless publication into WebView outside application composition.

Removal happens only after the replacement path has characterization and
application scenario coverage.

## Testing strategy

### Domain tests

Fast tests cover attempt lifecycle, retry, pause/resume/results, rolling
windows, bounded history, timing, judgement deltas, accuracy, insights, and
replay/spectating behaviour.

### Application scenario tests

This is the primary regression suite. It uses fake telemetry, headless/replay
services, window, presenter, and clock. It must cover:

1. menu -> play 30 seconds -> pause;
2. play -> pause -> resume -> play -> pause;
3. retry on the same beatmap;
4. map A -> map B with stale analysis completion;
5. WebView recreation during an attempt;
6. hidden presentation while telemetry continues;
7. temporarily unavailable presentation;
8. replay and spectating;
9. partial Tosu payloads and explicit reset;
10. results with retained final diagnosis.

### Infrastructure contract tests

Recorded raw fixtures:

- `stable-playing.json`;
- `stable-paused.json`;
- `stable-results.json`;
- `lazer-playing.json`;
- `lazer-paused.json`;
- `partial-payload.json`;
- `retry-reset.json`;
- `replay.json`;
- `spectating.json`.

HTTP and WebSocket inputs must normalize to equivalent domain samples.

### Presentation tests

Tests validate `OverlayViewState -> DOM` without Tosu or Pause Coach business
logic in JavaScript. They cover block rendering, version ordering, preset DOM
replacement, scaling, and size reporting.

### E2E and manual acceptance

Keep a small set of critical stable/lazer, WebView recreation, fullscreen,
DPI, and Win32 visibility checks. Manual runtime acceptance remains required.

## Architecture rules

CI should enforce:

- Core does not reference Avalonia, WebView, Windows, or Infrastructure;
- ReplayAnalysis does not reference Avalonia;
- Application does not reference Avalonia or `MainWindow`;
- after the Tosu migration, ReplayAnalysis does not parse Tosu JSON;
- MainWindow does not reference realtime collector/analyzer implementations;
- renderer assets do not contain Tosu routes, session lifecycle, retry logic,
  or metrics calculations;
- only Infrastructure implements raw Tosu and presentation ports.

Assembly dependency rules may use `NetArchTest.Rules`. JavaScript restrictions
can be enforced with a small repository convention test.

## Observability

Important transitions should emit structured data containing:

```text
eventType
runtimeVersion
viewStateVersion
beatmapId / beatmapGeneration
attemptId
mapTimeMs
gameplayState
presentationGeneration
```

Log transitions, session changes, rejected stale completions, and throttled
boundary failures. Do not log every telemetry packet indefinitely.

## Incremental migration

### PR A — architecture contract and characterization tests

- Objective: freeze intended behaviour before changing production wiring.
- Files: `docs/ARCHITECTURE.md`, ADRs, architecture tests, test-only current
  pipeline scenario harness.
- New abstractions: test-only harness; no production framework.
- Preserve: all user-visible behaviour.
- Tests: play/pause, resume, retry, results, partial payload, hidden/visible,
  publisher failure/recreation, initial dependency rules.
- Removed code: none.
- Risk: low.
- Rollback: remove docs/tests.
- Dependency: none.

### PR B — single Tosu normalization boundary

- Objective: one native raw-Tosu adapter and one normalized sample contract.
- Files: `Avalonia/Infrastructure/Tosu`, `TosuService`, current collector and
  fixtures.
- New abstractions: `IRealtimeTelemetrySource`,
  `TosuRealtimeTelemetrySource`.
- Preserve: polling interval, stable/lazer behaviour, preview and overlay
  updates.
- Tests: all recorded Tosu fixtures and HTTP/WebSocket equivalence.
- Removed code: duplicate native gameplay-state normalization.
- Risk: medium.
- Rollback: temporary unified-source feature switch.
- Dependency: PR A.

### PR C — Application state and events

- Objective: introduce the application assembly, immutable state, events,
  versions, and pure visibility derivation without changing production UI.
- Files: new `src/Application` and tests.
- New abstractions: runtime state, event model, ports, view-state contract.
- Preserve: production path remains unchanged.
- Tests: all ten application scenarios using fake ports.
- Removed code: none.
- Risk: low to medium.
- Rollback: remove the isolated assembly.
- Dependency: PR A and normalized domain model from PR B.

### PR D — serialized coordinator in shadow mode

- Objective: run the coordinator against production telemetry without owning
  presentation or HWND behaviour yet.
- Files: Application coordinator and Avalonia composition root.
- New abstractions: event loop and structured comparison diagnostics.
- Preserve: legacy path remains authoritative.
- Tests: event ordering, cancellation, stale completion, lifecycle shutdown.
- Removed code: none.
- Risk: medium.
- Rollback: disable shadow registration.
- Dependency: PR B and PR C.

### PR E — native runtime and visibility cutover

- Objective: make coordinator authoritative for gameplay, Pause Coach, and
  desired native visibility.
- Files: coordinator, MainWindow wiring, window adapter, telemetry source.
- New abstractions: `IOverlayWindow` implementation.
- Preserve: visibility policy, minimized editing behaviour, hotkeys, native
  click-through, launcher preview.
- Tests: full scenario suite plus window-state effect tests.
- Removed code: MainWindow gameplay flags, polling guards, browser authority
  fallback, and direct native snapshot mapping.
- Risk: high.
- Rollback: one temporary legacy-native feature switch.
- Dependency: PR D parity evidence.

### PR F — versioned view-state composition

- Objective: compose independent beatmap, headless, replay, and realtime slots
  in Application.
- Files: Application mapper, headless/replay service adapters, presenters.
- New abstractions: `OverlayViewState`, versioned analysis completion.
- Preserve: all current presets and displayed fields.
- Tests: map A -> B stale results, replay races, partial analysis, unavailable
  presentation.
- Removed code: direct headless/replay publication outside Application.
- Risk: high.
- Rollback: temporary legacy snapshot presenter adapter.
- Dependency: PR E.

### PR G — dumb native renderer

- Objective: render one versioned view state without producer reconciliation.
- Files: `renderer.js`, WebView presenter, renderer tests.
- New abstractions: version-only stale-render guard.
- Preserve: DOM, preset CSS, scaling, and size reporting.
- Tests: `OverlayViewState -> DOM`, stale version, preset replacement.
- Removed code: native/browser/headless authority rules and `mergeSnapshot` for
  launcher/desktop overlay.
- Risk: medium.
- Rollback: restore previous renderer while Application still emits compatible
  state.
- Dependency: PR F.

### PR H — fullscreen state transport and JS analyzer removal

- Objective: deliver authoritative C# view state to the Tosu fullscreen page.
- Files: fullscreen service/sink and fullscreen runtime asset.
- New abstractions: versioned fullscreen view-state transport with freshness.
- Preserve: fullscreen preset and visibility behaviour.
- Tests: transport atomicity/freshness and fullscreen renderer fixture; manual
  stable/lazer fullscreen acceptance.
- Current increment: the application writes an atomic `view-state.json` into
  Tosu's static overlay directory and the fullscreen runtime polls it with a
  version guard. Browser Pause Coach remains a compatibility fallback until
  the transport has passed manual fullscreen acceptance.
- Planned removal: JavaScript Pause Coach engine, browser-generated sessions,
  and browser Tosu Pause Coach source.
- Risk: high and Windows-specific.
- Rollback: an explicit legacy fullscreen mode for one migration release; it
  must never compete with native state in the same surface.
- Dependency: PR G.

### PR I — headless host lifecycle isolation

- Objective: prevent presentation WebView navigation from owning analyzer
  runtime lifecycle.
- Current increment: headless analysis uses a separate 1×1 offscreen
  `NativeWebView`, with its own navigation and message lifecycle. The visible
  presentation WebView no longer receives headless engine scripts.
- Files: analyzer script-host composition and headless service adapter.
- New abstractions: dedicated implementation behind the existing script-host
  and analysis ports.
- Preserve: analyzer protocol and produced metrics.
- Tests: navigation/recreation while analysis is in flight, worker recovery,
  stale completion rejection.
- Removed code: direct dependency on the current presentation browser.
- Risk: medium.
- Rollback: keep the current WebView script-host implementation available.
- Dependency: PR F.

### PR J — MVVM/window cleanup and compatibility removal

- Objective: make MainWindow a thin view shell after runtime behaviour is
  already controlled elsewhere.
- Files: MainWindow, ViewModels, WebView/window/interaction/scale adapters.
- New abstractions: only adapters justified by tests and platform volatility.
- Preserve: launcher settings, update/localization workflow, drag/scale/DPI.
- Tests: ViewModel commands and platform adapter tests; manual UI acceptance.
- Removed code: proven compatibility paths and obsolete MainWindow logic.
- Risk: medium.
- Rollback: PR-level revert; no domain/state rollback required.
- Dependency: PR E through PR I.

## Required ADRs

The first migration PR should add concise ADRs for:

1. authoritative C# Pause Coach source of truth;
2. serialized runtime event processing;
3. WebView as presentation only;
4. canonical attempt/session identity;
5. single Tosu normalization boundary;
6. fullscreen view-state transport when its implementation is selected.

## First implementation gate

The first implementation PR is PR A. It must not change production behaviour.
No later migration PR starts until PR A is reviewed and approved.

Minimum PR A acceptance:

- current and target architecture are documented;
- five initial ADRs exist;
- a test-only current-pipeline scenario harness exists;
- deterministic tests cover play -> 30 seconds -> pause, resume -> pause,
  retry, results, partial payload, hidden collection, and presentation
  recreation;
- initial assembly dependency rules run in CI;
- the JavaScript/native authority conflict is recorded as an open migration
  blocker.
