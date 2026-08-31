# Architecture completion plan

Status: approved backlog; incremental implementation continues after release
`v2.4.0` on `architecture-completion`.

Baseline re-audited: release `v2.4.0` at `0e8c331` on 2026-08-28. This document
is the execution source of truth for completing the runtime migration. The
broader rationale and original migration history remain in
[ARCHITECTURE.md](ARCHITECTURE.md).

## Purpose

Finish the existing incremental architecture migration without adding product
features or rewriting working code for aesthetics.

The intended result is one deterministic owner for overlay runtime semantics:

```text
external inputs
    -> typed adapters and normalization
    -> OverlayRuntimeEvent
    -> OverlayRuntimeCoordinator
    -> OverlayRuntimeReducer
    -> immutable OverlayRuntimeState
    -> OverlayViewStateComposer
    -> versioned OverlayViewState
    -> presentation effects and presenters
```

This runtime owns gameplay, beatmap, analysis freshness, overlay policy and the
presentation contract. It is deliberately **not** a global state container for
the updater, dialogs, files, or every pixel of window geometry.

## Scope

In scope:

- finish the Application runtime contract and authority cutover;
- separate realtime analysis from replay analysis;
- establish one C# Pause Coach business implementation;
- make browser code presentation-focused;
- extract runtime, presentation and lifecycle responsibilities from
  `MainWindow`;
- protect the application from stale asynchronous results and old presentation
  surfaces;
- introduce structured failures where strings currently control behavior;
- decompose the updater along existing responsibilities;
- centralize the application version;
- preserve and extend CI, deterministic tests, documentation and manual
  acceptance instructions.

Out of scope unless required to preserve behavior:

- new widgets, metrics, presets or analyzer algorithms;
- new replay formats or new Tosu claims;
- a general-purpose mediator, event-sourcing, CQRS or dependency-injection
  framework;
- a separate Infrastructure assembly without a demonstrated second host;
- cosmetic folder moves;
- changing GitHub repository settings;
- automating real osu!/Windows visual acceptance.

## Verified current state

The repository already has useful foundations:

- a serialized `OverlayRuntimeCoordinator` and immutable reducer state;
- a pure visibility derivation;
- C# Tosu normalization and `RealtimePlayAnalyzer`;
- a latest-wins snapshot publisher with recreation and failure tests;
- a dedicated offscreen WebView for headless analysis;
- generation-aware headless analysis scheduling;
- independent exact replay parsing and analysis;
- desktop and fullscreen view-state delivery;
- presentation-contract change publication and an initial native
  presentation-surface generation boundary;
- runtime JavaScript tests for Pause Coach, adapter integration and renderer
  precedence.

The migration is nevertheless still transitional:

- `MainWindow` mirrors runtime state into legacy mutable flags and still owns
  visibility, browser lifecycle, publication, sizing and orchestration; native
  realtime polling/normalization now belongs to `TosuRealtimeRuntimeHost`.
- C# and JavaScript both implement Pause Coach lifecycle and metrics;
- the browser adapter and native host both consume Tosu realtime data;
- the renderer still arbitrates native/browser authority and merges business
  snapshots;
- headless analysis now carries an explicit causal request identity, while
  replay requests and presentation surfaces still need their own generations;
- realtime/Pause Coach and exact replay now live in separate domain projects;
- raw Tosu JSON normalization now lives in Avalonia infrastructure;
- documentation disagrees with the production authority model.

Updater state persistence is now an injected boundary and writes a complete
temporary JSON document before replacing the primary file. Installer tests use
temporary roots and injected process-stop callbacks; UI orchestration and
failure-injection coverage remain transitional.

The current Pause Coach transition is now explicit: native C# is authoritative
whenever an application view-state has realtime data; JavaScript remains a
temporary compatibility/preview fallback and receives the same C# threshold
contract. Desktop overlay declares that native authority before adapter
startup, preventing a second browser coach session; preview/fullscreen remain
fallback surfaces until native delivery is verified. This is a containment
step, not the final browser removal.

## Current architecture

```text
Tosu HTTP
  +-> TosuRealtimeRuntimeHost / polling controller
  |     -> Avalonia Tosu normalizer
  |     -> RealtimeAnalysis / RealtimePlayAnalyzer
  |     -> OverlayRuntimeCoordinator
  |     -> OverlayViewState
  |     -> desktop/fullscreen publishers
  |
  +-> MainWindow legacy gameplay/visibility flags

Tosu HTTP + WebSocket
  -> adapter.js
  -> pause-coach.js
  -> renderer.js producer/session arbitration

Headless WebView
  -> HeadlessAnalysisController
  -> AnalysisSnapshot
  -> Application runtime plus compatibility presentation paths

Stable replay file
  -> ReplayAnalysisSession
  -> exact ReplayAnalysis snapshot
  -> presentation compatibility path
```

## Target architecture

```text
                    +-----------------------------+
raw Tosu ---------->| Tosu infrastructure adapter |
                    +--------------+--------------+
                                   |
                                   v
                    +-----------------------------+
                    | normalized realtime sample  |
                    +--------------+--------------+
                                   |
                                   v
                    +-----------------------------+
                    | C# realtime / Pause Coach   |
                    | attempt domain              |
                    +--------------+--------------+
                                   |
headless analysis -----------------+
exact replay analysis -------------+--> OverlayRuntimeEvent
window/process events -------------+           |
presentation feedback -------------+           v
                                      serialized coordinator
                                               |
                                               v
                                      immutable runtime state
                                               |
                                               v
                                      versioned view state
                                               |
                            +------------------+------------------+
                            |                  |                  |
                            v                  v                  v
                      desktop presenter  preview presenter  fullscreen sink
                            |                  |                  |
                            +------------------+------------------+
                                               |
                                               v
                                         DOM rendering only
```

The desired visibility decision belongs to Application. Creating, showing,
hiding, focusing and sizing the physical surface are effects owned by platform
and presentation controllers.

## Non-negotiable invariants

- [ ] One real play has one canonical native `AttemptId`.
- [ ] Browser-generated session IDs never compete with native attempt identity.
- [ ] A global queue sequence orders dispatch but does not substitute for
  beatmap, attempt, analysis-request or surface generations.
- [ ] Late analysis for an old request cannot replace a newer result even when
  it enters the event queue later.
- [ ] A transient carousel beatmap cannot destroy a still-valid analysis for
  the confirmed map.
- [ ] Partial Tosu packets cannot start a new attempt without positive reset or
  identity evidence.
- [ ] Retry/reset on the same beatmap starts a new attempt only when cumulative
  counters or gameplay time positively establish the reset.
- [ ] Collection and Pause Coach analysis continue while every presentation is
  hidden or unavailable.
- [ ] WebView recreation changes presentation generation only; it does not
  reset beatmap, attempt, Pause Coach, headless or replay state.
- [ ] The first frame published to a newly visible/recreated surface is the
  latest accepted state, never an older in-flight frame.
- [ ] Headless, realtime and exact replay results occupy independent state
  slots and are composed; one cannot erase another by omission.
- [ ] Exact replay analysis remains independent from realtime Pause Coach
  semantics and fidelity claims.
- [ ] JavaScript cannot parse Tosu or decide attempt lifecycle after the C#
  cutover.
- [ ] Errors used for control flow are structured; human-readable messages are
  for diagnostics and UI only.
- [ ] No test reads or writes the developer's installed Tosu directory.
- [ ] Every legacy path is removed only after deterministic replacement
  coverage or explicitly recorded manual parity.

## Identity and ordering model

The implementation must define these concepts before authority is switched:

| Identity | Owner | Purpose |
| --- | --- | --- |
| `RuntimeVersion` | coordinator | Monotonic accepted state transition version. |
| `ViewStateVersion` | composer/coordinator | Monotonic presentation contract version. |
| `BeatmapGeneration` | runtime | Changes on a positively accepted beatmap transition. |
| `AttemptId` / `AttemptGeneration` | C# realtime domain | Identifies retry/new play independently from beatmap. |
| `AnalysisRequestId` | analysis coordinator | Correlates completion/failure with the request that launched it. |
| `ReplayRequestId` | replay controller | Prevents an old import/discovery result from replacing a newer request. |
| `PresentationSurfaceId` | presentation controller | Identifies one logical desktop, preview or fullscreen surface. |
| `PresentationGeneration` | presentation controller | Invalidates navigation/recreation work from an older document. |
| `SourceRevision` | external adapter, when available | Rejects out-of-order updates from one producer. |

Rules:

- [ ] `OverlayRuntimeEvent.Sequence` remains an enqueue-order safeguard.
- [x] Analysis snapshot events carry the observed beatmap generation and the
  reducer rejects a completion from an older map generation.
- [x] Extend the identity through an explicit headless analysis request
  contract; replay completion identity remains part of Work Package 2/LUNA-03.
- [x] The reducer checks headless request identity, target generation and map
  metadata before changing the analysis slot.
- [x] Rejected events emit typed diagnostics containing the rejection reason,
  sequence/generation values and beatmap identities where applicable
  (`OverlayRuntimeRejection`).
- [ ] State does not compare independently generated native and browser session
  IDs.

## Work package 0 — baseline and characterization

Goal: freeze existing behavior and make later deletions reviewable.

Dependencies: none.

Risk: low. Production behavior must not change.

### Tasks

- [x] Record an ownership matrix for current and target owners of beatmap,
  attempt, realtime metrics, headless result, replay result, desired visibility,
  surface readiness and rendered version in `docs/ARCHITECTURE.md`.
- [x] Update `docs/ARCHITECTURE.md` to describe the current merged state rather
  than old "implemented locally" PR checkpoints.
- [ ] Add or update ADRs for:
  - [ ] dedicated realtime bounded context;
  - [x] causal identity and stale-event rejection (`docs/adr/0002`,
    `docs/adr/0004`, plus typed runtime rejection diagnostics);
  - [x] desired visibility versus actual presentation state (`docs/adr/0002`);
  - [x] C# Pause Coach authority (`docs/adr/0001`);
  - [x] WebView presentation-only responsibility (`docs/adr/0003`);
  - [x] fullscreen view-state transport (documented in `docs/adr/0003` and
    `docs/ARCHITECTURE.md`; dedicated ADR remains optional while the sink is
    still transitional).
- [x] Build a test-only production-path harness that accepts raw Tosu payloads
  and records the final `OverlayViewState` delivered to a fake presenter.
  `NativeRealtimeApplicationScenarioTests` covers hidden 0/5/10/20/30-second
  collection, a Tosu lazer `Play + paused=true` transition, same-session
  continuity, recent timing data and latest-wins flush ordering.
- [x] Add stable and lazer fixtures for menu/select, play, pause, resume,
  failure, results, retry, replay, spectator and partial packets. Presentation
  recreation remains a later surface-delivery fixture.
- [x] Include the real lazer pause representation:
  `state.name=Play`, `state.number=2`, `game.paused=true`.
- [ ] Characterize current desktop, preview and fullscreen publication behavior
  before replacing it.
- [x] Add initial assembly dependency tests and JavaScript convention checks
  (`ArchitectureConventionTests`).
- [x] Record the existing compiler-warning baseline (94 warnings, 0 errors in
  the local Release build). Do not suppress warnings globally or allow touched
  code to add new warning classes.

### Automated acceptance

- [x] Raw stable and lazer lifecycle fixtures reach the expected normalized
  gameplay state.
- [x] Existing Application, Core, ReplayAnalysis, Avalonia and Runtime tests
  remain green; the fixture suite adds coverage without removing existing tests.
- [ ] The harness covers hidden collection, pause visibility, resume, results
  and presentation recreation without a real WebView.

### Exit gate

- [ ] No production branch is deleted.
- [ ] Current behavior and known inconsistencies are documented.
- [ ] Later PRs can prove parity using deterministic fixtures.

## Work package 1 — realtime bounded context

Goal: stop using `ReplayAnalysis` as the home of realtime Tosu/Pause Coach
logic while preserving exact behavior.

Dependencies: work package 0.

Risk: medium because namespaces, test projects and Application references move.

### Tasks

- [x] Create a focused `RealtimeAnalysis` domain assembly, unless a dependency
  audit recorded in an ADR proves `Core/Realtime` is materially simpler.
- [x] Move pure realtime contracts and behavior:
  - [x] `RealtimePlayState`;
  - [x] normalized sample and snapshot contracts;
  - [x] attempt/session tracker;
  - [x] `RealtimePlayAnalyzer`;
  - [x] Pause Coach options, thresholds, insight ranking and bounded windows.
- [x] Keep raw `JsonElement` Tosu parsing out of the realtime domain.
- [x] Move `TosuRealtimePayloadNormalizer` and the collector adapter to
  `Avalonia/Infrastructure/Tosu` or another existing host boundary.
- [x] Move realtime tests out of `ReplayAnalysis.Tests` into the matching domain
  or infrastructure test project.
- [x] Ensure exact replay analysis does not reference realtime lifecycle or
  Pause Coach thresholds.
- [x] Update solution/project references so Application depends on realtime
  contracts directly, not on ReplayAnalysis for those types.
- [x] Preserve serialized names and view-state compatibility during the move.

### Automated acceptance

- [x] Existing realtime fixtures produce field-equivalent snapshots.
- [x] Replay tests run without referencing Tosu payload normalization.
- [x] Dependency tests reject Tosu JSON parsing inside realtime/replay domain
  assemblies.
- [x] No product behavior or threshold changes are included in this package.

### Exit gate

- [x] Replay and realtime are distinct bounded contexts.
- [x] C# realtime behavior remains fully covered and unchanged.

## Work package 2 — complete the Application runtime contract

Goal: make Application capable of expressing every semantic state required for
authority before switching production ownership.

Dependencies: work packages 0 and 1.

Risk: medium. This package should remain shadow/observational where practical.

### Tasks

- [ ] Add typed runtime state for:
  - [ ] Tosu connection state and reconnect generation;
  - [ ] osu! process/window presence and minimize state;
  - [ ] confirmed beatmap identity and generation;
  - [ ] current gameplay/attempt identity;
  - [ ] independent difficulty, realtime and exact replay slots;
  - [ ] overlay mode and semantic preset/scale settings;
  - [ ] desired visibility;
  - [ ] presentation surfaces and their generations;
  - [ ] structured runtime diagnostics.
- [ ] Add typed events for connection, analysis start/completion/failure,
  replay start/completion/failure, presentation create/ready/destroy, preset
  changes and application shutdown.
- [ ] Keep drag coordinates, raw pointer movement and physical resize pixels out
  of the reducer.
- [x] Add `AnalysisRequestId`, beatmap generation and configuration identity to
  analysis start/completion/failure events.
  - [x] Add `ReplayRequestId` to replay start/completion/failure/cancellation
    events and keep it in an independent Application replay slot.
  - [x] Model `DesiredVisibility`, `SurfaceReady` and `ActualVisibility` as
    distinct concepts.
- [x] Ensure `ViewStateChanged` fires whenever the composed presentation
  contract changes, not only when realtime/analysis object references change.
- [x] Preserve confirmed beatmap and attempt identity when a partial realtime
  frame omits those fields; the reducer applies the same boundary guarantee as
  the Tosu normalizer for direct/compatibility producers.
- [x] Add the first native presentation-surface generation boundary and reject
  feedback from an older surface in the reducer. Desktop/preview/fullscreen
  surface unification remains part of work package 4.
- [x] Add the initial typed Tosu connection state and transport-generation
  boundary. The transport host still remains in `MainWindow` until WP3.
  - [x] Give `OverlayViewState` its own monotonic version. The coordinator
    allocates the rendered-contract version independently from runtime event
    versions while retaining `RuntimeVersion` for diagnostics.
- [x] Define equality/change detection from the actual presentation
    contract, not incidental object reference identity. Version/epoch metadata
    are excluded from content equality, while nested serialized contract data
    remains covered.
  - [x] Replace the single unversioned pending-analysis projection with an
    explicit `AnalysisRequestSlot`; replay now keeps its independent
    `ReplayAnalysisRequestSlot` for LUNA-03.
- [ ] Add reducer diagnostics for every stale/rejected completion.
- [ ] Define reset semantics for application restart separately from gameplay
  retry and presentation recreation.

### Required scenario tests

- [ ] menu -> play 30s -> pause;
- [ ] play -> pause -> resume -> play -> pause with one attempt;
- [ ] retry on the same beatmap creates another attempt;
- [ ] results retain the final diagnosis;
- [ ] replay/spectating are unavailable without destroying other state;
- [ ] partial packet preserves known identity and attempt;
- [ ] map A -> transient B -> A retains valid A analysis;
- [ ] old analysis A completes after newer analysis B and is rejected;
- [ ] two analysis requests for the same map/config complete out of order;
- [ ] presentation policy change publishes a new view-state even without new
  telemetry;
- [ ] WebView recreation preserves runtime state;
- [ ] Tosu disconnect/reconnect does not reuse an invalid transport generation.

### Exit gate

- [ ] Every production semantic decision has a typed runtime representation or
  is explicitly documented as a presentation/platform effect.
- [ ] The Application test suite covers all causal identities.

## Work package 3 — Tosu runtime host and lifecycle

Goal: make native telemetry collection continuous and independent of
`MainWindow` and presentation visibility.

Dependencies: work package 2.

Risk: high because collection lifecycle affects every overlay mode.

### Tasks

- [ ] Introduce a focused Tosu realtime host/controller owning:
  - [ ] polling/WebSocket transport selected by existing capability;
  - [ ] timer/task lifecycle;
  - [ ] cancellation token source;
  - [ ] transport generation;
  - [ ] reconnect/backoff policy;
  - [ ] raw payload diagnostics;
  - [ ] normalization and dispatch to Application.
- [x] Introduce `TosuRealtimeRuntimeHost` as the focused owner of the native
  Tosu payload source, normalization and polling controller. Its lifecycle
  trigger is now connected directly to Tosu process events.
- [x] Bind the host to `TosuService.StateChanged` and keep collection alive
  across overlay enter/leave and presentation recreation; `MainWindow` no
  longer starts or stops native collection.
- [ ] Start collection from application lifecycle, not overlay visibility.
- [x] Continue collecting while desktop, preview and fullscreen surfaces are
  hidden, unavailable or being recreated; host lifetime follows Tosu, not a
  presentation surface.
- [x] Guard the current native polling boundary against a queued callback
  reading a disposed cancellation source; the guard is now owned by the
  polling controller.
- [x] Ensure a queued callback from a stopped generation cannot publish into a
  new lifecycle.
- [x] Reject a queued `StateChanged` callback from a detached/replaced Tosu
  service by source identity as well as transport generation; the host tests
  this through its injectable lifecycle contract.
- [x] Remove the unused duplicate `TosuService` state-only endpoint path; the
  native host now consumes one full `/json/v2` payload and one normalizer.
- [x] Handle Tosu unavailable, transient HTTP failures and malformed/partial
  realtime payloads with a typed `TosuRealtimePayloadResult` at the native
  transport boundary; collector behavior remains unchanged.
- [ ] Preserve the latest valid beatmap/analysis during transient carousel and
  endpoint failures according to explicit policy.
- [x] Move native gameplay polling timer, cancellation, generation and
  in-flight guards into `OverlayGameplayPollingController`; transport and
  reconnect ownership remains in the host for the next slice.
- [x] Move native collection start/stop and polling ownership out of
  `MainWindow`; remaining reconnect/backoff policy is the Tosu service
  boundary work below.
- [ ] Gate the browser Tosu source explicitly per host mode while migration is
  active; never allow two producers to compete silently.
- [ ] Add bounded, throttled transition logging. Do not log every packet.

### Automated acceptance

- [x] Hidden play at 0s/5s/10s/20s/30s produces no presentation calls but does
  update runtime state (`NativeRealtimeApplicationScenarioTests`).
- [x] Pause at 30s exposes the same attempt and newest score/map time/timing
  (`NativeRealtimeApplicationScenarioTests`).
- [x] Resume for 10s and pause again exposes newer values for the same attempt
  (`NativeRealtimeApplicationScenarioTests`).
- [ ] Stop/restart rejects callbacks from the previous transport generation.
- [ ] Stable and lazer fixtures normalize identically through HTTP and
  WebSocket adapter paths where both exist.
- [x] No test accesses the installed `%LOCALAPPDATA%` Tosu directory;
  analyzer supervisor tests inject a temporary deployment root.

### Exit gate

- [ ] `MainWindow` no longer owns realtime transport lifecycle.
- [ ] Collection behavior does not depend on WebView readiness or visibility.

## Work package 4 — presentation and effects layer

Goal: isolate desired application state from physical window/WebView/file
effects and make every surface latest-wins.

Dependencies: work packages 2 and 3.

Risk: high because recreation, hidden state and stale in-flight calls caused
previous regressions.

### Tasks

- [ ] Define a presentation controller around existing presenter semantics,
  without creating an interface for every implementation detail.
- [x] Introduce `OverlayPresentationDeliveryController` to own desktop and
  fullscreen latest-wins publisher instances; platform/WebView/file effects
  remain injected from the host.
- [x] Allocate controller-owned logical generations for desktop and fullscreen
  sessions. The desktop generation is fed back into the Application runtime;
  preview identity and cross-surface event unification remain follow-up work.
- [ ] Provide explicit logical surfaces for desktop overlay, launcher preview
  and fullscreen sink.
- [ ] Assign every created/recreated surface a generation.
- [ ] Submit the newest view-state before changing a surface from hidden to
  visible.
- [ ] While hidden, retain only the newest pending state and perform no WebView
  realtime invocation.
- [ ] After an in-flight publish completes, immediately publish the newest
  pending state if it changed.
- [ ] Recover after invocation exception without losing the latest state.
- [ ] Ignore completion from an old surface generation.
- [ ] Replay the latest state after navigation/reinjection/recreation.
- [ ] Preserve the collector's latest state when replacing the browser.
- [ ] Route desired visibility through an effect runner using `IOverlayWindow`.
- [ ] Report actual surface readiness/visibility back as typed feedback.
- [ ] Keep physical drag/resize/focus/click-through behavior in focused platform
  or interaction controllers.
- [ ] Retain current latest-wins publisher semantics, moving ownership rather
  than redesigning a proven algorithm.

### Required publisher tests

- [ ] hidden -> collect -> pause -> visible flush;
- [ ] first visible frame is Paused/Results, never previous Playing;
- [ ] slow publish plus multiple newer snapshots publishes only the newest;
- [ ] invocation exception recovery;
- [ ] WebView recreation while publish is in flight;
- [ ] leave and re-enter overlay mode;
- [ ] navigation/reinjection;
- [ ] stale completion from an old presentation generation;
- [ ] preview and desktop receive the same current view-state independently;
- [ ] fullscreen file publication is atomic and monotonic.

### Exit gate

- [ ] `MainWindow` does not call business snapshot publication directly.
- [ ] Presentation failure cannot stop collection or roll runtime state back.

## Work package 5 — canonical C# Pause Coach

Goal: eliminate independently evolving Pause Coach business rules.

Dependencies: work packages 1-4.

Risk: high. Browser fallback must not be removed before every supported surface
receives the native view-state reliably.

### Tasks

- [x] Document native `RealtimePlayAnalyzer` as the authoritative native
  implementation and mark the browser runtime fallback-only.
- [x] Inject `PauseCoachOptions` into fallback documents to prevent threshold
  and window drift during the transition.
- [ ] Inventory every threshold, rolling-window rule, retry rule, confidence
  gate and insight ranking in C# and JavaScript.
- [ ] Create canonical lifecycle/metric fixtures shared by C# and JavaScript
  during migration.
- [ ] Resolve differences deliberately; do not silently choose whichever output
  makes a test pass.
- [ ] Document Tosu-observed versus reconstructed/unavailable data claims.
- [ ] Verify C# delivery for desktop, launcher preview and fullscreen.
- [ ] Make native `AttemptId` the only attempt identity in emitted view-state.
- [ ] Ensure a browser session ID cannot release native authority.
- [ ] Disable browser Pause Coach processing once the surface has native
  authority, using an explicit migration gate.
- [ ] Remove browser attempt lifecycle, recent-window computation, timing
  thresholds, insight ranking and browser-generated session IDs after parity.
- [ ] Move remaining JS fallback fixtures to historical/compatibility coverage
  or remove them with the implementation they test.
- [ ] Update `docs/PAUSE_COACH.md` at each authority change so it never claims
  the wrong source of truth.

### Automated acceptance

- [ ] Stable and lazer play/pause/resume/retry/results sequences produce one
  canonical attempt identity and expected widget state.
- [ ] A partial browser snapshot cannot replace native pause/gameplay/replay
  state for the same beatmap.
- [ ] Presentation recreation does not start another attempt.
- [ ] Bounded memory/window behavior remains proven.
- [ ] No unsupported per-column or canonical pattern claims are introduced.

### Manual gate before JS deletion

- [ ] Windows desktop overlay accepted on stable and lazer.
- [ ] Launcher preview accepted while gameplay updates continue.
- [ ] Fullscreen state transport accepted on supported stable/lazer modes.

### Exit gate

- [ ] C# is the only Pause Coach business implementation.
- [ ] JavaScript is presentation-only for Pause Coach.

## Work package 6 — dumb renderer and typed WebView protocol

Goal: reduce the WebView to a versioned DOM presenter.

Dependencies: work package 5.

Risk: medium because every preset shares renderer behavior.

### Tasks

- [x] Define one versioned `OverlayViewState` transport envelope with explicit
  schema version 1.
- [x] Validate schema/version at the renderer boundary and reject unsupported
  future versions without replacing the last valid state.
- [ ] Replace string-prefix WebView commands with structured messages where
  they affect behavior; retain a temporary parser only at the compatibility
  boundary.
- [ ] Reduce the core renderer entry point to `render(viewState)` plus a
  version-order guard.
- [ ] Remove producer arbitration, native snapshot globals, browser/native
  session comparisons and `mergeSnapshot()` business behavior.
- [ ] Remove browser-side preservation of omitted business blocks; composition
  must already be complete in Application.
- [ ] Keep only formatting, localization, DOM binding, CSS application,
  measurement and visual input reporting.
- [ ] Ensure preset replacement cannot retain DOM/state from the previous
  preset.
- [ ] Remove the legacy `AnalysisSnapshot` compatibility mapper when no surface
  consumes it.
- [ ] Add a repository convention test preventing Tosu routes, session logic,
  retry thresholds and metric calculation from returning to renderer assets.

### Automated acceptance

- [ ] `OverlayViewState -> DOM` tests for every built-in preset.
- [ ] Older `ViewStateVersion` is ignored.
- [ ] New surface generation accepts the latest state even if its version was
  rendered by an old surface.
- [ ] Preset replacement removes obsolete nodes and state.
- [ ] Scale/measurement messages remain stable and typed.
- [ ] Renderer tests no longer need fake Tosu gameplay payloads.

### Exit gate

- [ ] JavaScript contains no gameplay authority or snapshot reconciliation.

## Work package 7 — authority cutover and legacy removal

Goal: make the Application runtime the sole owner of overlay semantics and
remove the production dual-run state.

Dependencies: work packages 2-6.

Risk: high. Removal is allowed only after parity evidence.

### Tasks

- [ ] Run legacy and new paths in diagnostic comparison for representative
  lifecycle fixtures.
- [ ] Record every intentional parity difference and its rationale.
- [ ] Switch gameplay, desired visibility and composed view-state authority to
  Application behind one temporary rollback switch if necessary.
- [ ] Make legacy projection observational only.
- [ ] Remove after parity:
  - [ ] `_shadow*` state and production parity callbacks;
  - [ ] mirrored `_overlayPlayState*` and `_overlayNativePlayState*` fields;
  - [ ] `SetOverlaySuppressedByPlay`;
  - [ ] legacy browser play/pause application mutations;
  - [ ] duplicate native/browser realtime polling;
  - [ ] direct `InvokeScript` snapshot publication from `MainWindow`;
  - [ ] obsolete compatibility snapshot conversion;
  - [ ] temporary rollback switch after one verified migration cycle.
- [ ] Keep parity comparer only as deterministic test/diagnostic infrastructure
  if it still catches meaningful regressions; otherwise delete it.
- [ ] Verify no fallback relies on a deleted legacy mutation.

### Automated acceptance

- [ ] Full raw-payload-to-presenter scenarios pass with legacy authority
  disabled.
- [ ] Searching production code finds no duplicate gameplay authority fields or
  browser Pause Coach state machine.
- [ ] Visibility effects come from the reducer-derived desired state.
- [ ] All stale event and surface generation tests pass.

### Exit gate

- [ ] There is exactly one documented authority for every runtime state slot.
- [ ] No production shadow runtime competes with it.

## Work package 8 — reduce MainWindow to a composition/UI shell

Goal: remove major orchestration and business ownership from
`MainWindow.axaml.cs` after the replacement components are proven.

Dependencies: work package 7. Small preparatory extractions are allowed earlier
when they do not change authority.

Risk: medium. Avoid arbitrary file splitting.

### Tasks

- [ ] Move application construction into a clear composition root in `App` or a
  focused application host.
- [ ] Inject runtime, telemetry, presentation, replay, headless, updater and
  platform controllers rather than constructing them throughout the view.
- [ ] Extract actual responsibility owners:
  - [ ] overlay window lifecycle controller;
  - [ ] presentation surface/navigation controller;
  - [ ] overlay interaction/geometry controller;
  - [ ] launcher appearance/preset command handler;
  - [ ] replay workflow controller;
  - [ ] update coordinator;
  - [ ] application shutdown/disposal coordinator.
- [ ] Keep dialog invocation and Avalonia event wiring in the view where that is
  the simplest boundary.
- [ ] Convert UI handlers into command forwarding without moving business logic
  into a ViewModel merely to reduce code-behind size.
- [ ] Define explicit ownership and disposal for every timer, CTS, semaphore,
  WebView, process/job and event subscription.
- [x] Dispose the current Application runtime coordinator from the window
  shutdown path. Full shutdown ownership extraction remains in this package.
- [ ] Ensure `MainWindow` no longer references realtime collector/analyzer
  implementations.
- [ ] Preserve drag, Ctrl+wheel scale, DPI, focus, click-through and fullscreen
  behavior.

### Automated acceptance

- [ ] Controller tests cover lifecycle and disposal without constructing a real
  `MainWindow`.
- [ ] Commands can be tested with fake ports.
- [ ] Closing the window cancels and disposes every owned child lifecycle once.
- [ ] No unobserved tasks survive shutdown in deterministic tests.

### Exit gate

- [ ] `MainWindow` contains primarily initialization, bindings, UI forwarding,
  dialogs and composition/disposal glue.
- [ ] Reduction is substantial because responsibilities moved to owners, not
  because methods were mechanically split.

## Work package 9 — lifecycle and race hardening

Goal: prove all confirmed race conditions at their true boundary.

Dependencies: continuous; every earlier package adds its relevant tests. This
package closes remaining gaps after authority cutover.

### Tasks and required tests

- [ ] Beatmap carousel A -> B -> A does not destroy valid A analysis.
- [ ] Confirmed next map B eventually replaces A and cannot be rolled back by
  late A data.
- [ ] Analysis for old rate/mod/config cannot replace the current selection.
- [ ] Mod change starts a new analysis request and invalidates only the matching
  analysis slot.
- [ ] Missing beatmap identity in a partial packet cannot release native
  authority.
- [ ] Retry reset requires positive counter/time evidence.
- [ ] Pause payload combinations such as `paused=true`, `isPlaying=false` are
  normalized from recorded Tosu behavior, not guessed per UI path.
- [ ] Minimize/restore does not stop collection or leave the widget stale.
- [ ] Closing osu! hides/closes the overlay according to policy and returns an
  interactive launcher.
- [ ] Restarting osu!/Tosu creates a fresh transport generation without stale
  callbacks.
- [x] A queued `Process.Exited` callback from an old Tosu instance cannot stop
  the replacement transport; the service checks process identity.
- [ ] WebView navigation and preset changes preserve current data and repaint
  the newest state.
- [ ] Initial/recreated widget sizing does not require a manual resize to render
  correctly.
- [ ] Map background, graphs, key count, LN data and modifier recalculation
  remain present across map transitions when their source data is available.

### Exit gate

- [ ] Every confirmed historical race has a deterministic test or is explicitly
  marked human-only with a reason.

## Work package 10 — structured error model

Goal: stop using user-facing strings to control important behavior without
creating an oversized exception hierarchy.

Dependencies: can proceed after the affected boundary is stable.

### Tasks

- [ ] Introduce small typed categories/results for:
  - [x] Tosu unavailable/not-ready/no-beatmap/HTTP/malformed payload;
  - [ ] analyzer deployment/boot/worker/transport/protocol/analysis failure;
  - [ ] presentation unavailable/navigation/invocation/schema failure;
  - [ ] replay not-found/corrupt/mismatch/unsupported failures;
  - [ ] update network/integrity/install/state failures.
- [x] Replace the main Tosu retry/fallback classification with typed failure
  kinds and route/status metadata.
- [x] Headless Tosu source-state decisions now consume the typed failure kind,
  including nested typed failures, without matching exception text.
- [ ] Replace remaining `exception.Message.Contains(...)` where it changes retry,
  fallback, state or severity.
- [ ] Replace update-progress localization based on message prefixes with typed
  progress codes and arguments.
- [ ] Parse legacy WebView string messages into typed DTOs at one boundary, then
  remove the legacy protocol when all senders migrate.
- [ ] Keep free-form exception text and stack traces for logs.
- [ ] Surface unexpected/malformed input visibly; do not silently fall back.

### Automated acceptance

- [ ] Control-flow tests assert typed codes, not English message text.
- [ ] Localization can change without changing update/runtime behavior.
- [ ] Retry and fallback policies are explicit and separately testable.

The analyzer bridge now attaches a structured `failureKind=beatmap_parse`
property for legacy parse errors, and probe/headless retry decisions use
`AnalyzerDiagnosticClassifier` rather than matching the human-readable
message. The message check remains only where the external engine protocol
does not provide a typed failure code.

## Work package 11 — UpdateService decomposition

Goal: separate existing updater responsibilities while preserving installation
and update behavior.

Dependencies: independent of runtime cutover; schedule after the high-risk
runtime path is stable so release plumbing is not changed simultaneously.

Risk: medium to high because updater failures can make the application
unusable.

### Tasks

- [x] Extract `GitHubReleaseClient` for GitHub release lookup and asset
  metadata.
- [x] Extract `ComponentDownloader` for streaming, progress, digest validation
  and temporary files; the boundary has isolated HTTP/temp-path tests.
- [x] Extract `ComponentInstaller` for Tosu/addon installation, executable
  handling, process stop and staged replacement; its filesystem/process
  boundary uses injected roots and callbacks in tests.
- [x] Extract `UpdateStateStore` for `InstallState` persistence; paths are
  injected and covered by temporary-root tests. Saves serialize and flush a
  temporary document before replacing the primary file. Schema migration
  remains.
- [ ] Keep `UpdateCoordinator` as the UI-facing workflow and compatibility
  policy owner.
- [ ] Inject filesystem/temp roots, clock/process operations where tests require
  isolation; do not add a general DI framework.
- [ ] Preserve offline-with-installed-components behavior.
- [ ] Preserve platform compatibility detection and self-update behavior.
- [ ] Ensure tests use temporary roots and never modify real Tosu/application
  installation directories.
- [ ] Convert `UpdateResult.Error/Warning` control flow to typed diagnostics
  while keeping localization keys stable.

### Automated acceptance

- [ ] Release lookup, rate-limit/network failure and asset selection tests.
- [ ] Digest success/failure and interrupted download tests.
- [x] Staged executable/addon replacement and temporary-directory cleanup
  tests; failure-injection and rollback-path coverage remains.
- [ ] Persisted state round-trip and schema compatibility tests.
- [ ] Offline startup with usable existing components.
- [ ] Package/update smoke test on Windows and Linux CI fixtures.

## Work package 12 — single version source

Goal: derive binaries, scripts, CI artifacts and updater identity from one
canonical version.

Dependencies: independent; preferably land before updater decomposition or in
a small dedicated PR.

### Tasks

- [x] Put the canonical product version in the root `VERSION` file and import
  it through `Directory.Build.props`.
- [x] Remove duplicated version metadata from Avalonia and Updater projects.
- [x] Derive runtime User-Agent versions from assembly/package metadata.
- [x] Make build/package scripts and CI read the canonical version instead of
  keeping independent defaults.
- [ ] Remove remaining historical release-number examples from prose.
- [x] Export the canonical version once in CI for package names, command
  arguments and artifact names.
- [ ] Generate or inject the standalone PowerShell updater version during
  packaging; do not require the installed script to find a source checkout.
- [x] Replace the hardcoded README badge with release-derived metadata.
- [ ] Add a test/script check that fails on avoidable hardcoded product-version
  duplicates.

### Automated acceptance

- [ ] Assemblies, updater, package filenames and CI artifact names report the
  same version.
- [ ] Windows and Linux packaging scripts work without an explicit version
  argument.
- [ ] A version bump changes one authoritative source.

## Work package 13 — CI and quality gates

Goal: keep the repository continuously mergeable throughout the migration.

Dependencies: continuous.

### Tasks

- [ ] Preserve the existing sequence:
  restore -> format -> build -> C# tests -> runtime JS tests -> package.
- [ ] Keep both Windows and Linux configured jobs green.
- [x] Add the current realtime/application/presentation architecture tests to
  the solution; the workflow retains all three production runtime JS tests.
- [ ] Keep packaging after every required test succeeds.
- [ ] Add isolated filesystem roots to every deployment/update test.
- [x] Add dependency-direction and JavaScript responsibility convention tests.
  `ArchitectureConventionTests` keeps Application/ReplayAnalysis independent
  from Avalonia and keeps Tosu HTTP/WebSocket access at the adapter boundary;
  it intentionally does not claim that the temporary browser Pause Coach
  fallback has already been removed.
- [ ] Keep the golden MMA parity test explicitly documented while its external
  fixtures are not bundled; do not present a skipped test as executed parity.
- [ ] Track the current warning baseline and fail on newly introduced warnings
  in touched projects where practical.
- [ ] Prioritize correctness warnings involving disposal, cancellation and
  ignored return values; do not suppress the analyzer set wholesale.

### Canonical validation commands

```powershell
dotnet restore ManiaMapAnalyzerOverlay.sln
dotnet format ManiaMapAnalyzerOverlay.sln `
  --no-restore `
  --verify-no-changes
dotnet build ManiaMapAnalyzerOverlay.sln `
  --configuration Release `
  --no-restore `
  --nologo
dotnet test ManiaMapAnalyzerOverlay.sln `
  --configuration Release `
  --no-build `
  --nologo
node tests/Runtime/pause-coach-runtime.test.js
node tests/Runtime/adapter-pause-coach-integration.test.js
node tests/Runtime/renderer-native-precedence.test.js
```

Tests that become obsolete after JS business logic is removed must be replaced
by renderer contract tests before deletion.

## Work package 14 — documentation and backlog cleanup

Goal: ensure repository documentation states the architecture that actually
runs.

Dependencies: updated incrementally; final pass after work package 13.

### Tasks

- [ ] Keep `TODO.md` limited to active work and links into this plan.
- [ ] Move completed/stale checkpoints into `docs/history/`.
- [ ] Update `docs/ARCHITECTURE.md` after every authority change.
- [ ] Update `docs/PAUSE_COACH.md` with the real current source of truth and
  transition state.
- [ ] Correct README preset count, supported resize controls, version examples
  and current presentation behavior.
- [ ] Document every removed legacy responsibility.
- [ ] Document every remaining transitional path with its authority, drift
  protection and deletion condition.
- [ ] Record exact automated commands/results for the final architecture pass.
- [ ] Record unresolved risks without describing human-only behavior as tested.

## Work package 15 — manual Windows/osu! acceptance

Goal: validate behaviors that deterministic tests cannot prove.

Owner: human tester. Automated output must never mark these complete without a
real session report.

### Clients and display modes

- [ ] osu!stable: windowed.
- [ ] osu!stable: borderless.
- [ ] osu!stable: exclusive fullscreen with Stable FS integration.
- [ ] osu!lazer: windowed.
- [ ] osu!lazer: borderless/fullscreen where supported.
- [ ] Mixed-DPI and non-100% Windows scaling where available.

### Lifecycle

- [ ] Start launcher before Tosu/osu!, then start dependencies.
- [ ] Song select -> play -> pause -> resume -> retry -> fail -> results.
- [ ] Change to the next map and confirm every data block changes once.
- [ ] Close osu! while overlay is active: overlay closes/hides and launcher is
  interactive.
- [ ] Restart osu!/Tosu and confirm recovery without restarting the launcher.
- [ ] Minimize and restore osu! without freezing map or Pause Coach updates.

### Overlay and presentation

- [ ] Show/hide policy for gameplay, pause, results and minimized osu!.
- [ ] No focus stealing or blocked launcher after overlay closes.
- [ ] Click-through/input behavior and hotkeys.
- [ ] Drag without jitter or crashes.
- [ ] Ctrl+wheel scaling without stale layout or extra native frame/gutter.
- [ ] Initial creation and recreation render correctly without a manual resize.
- [ ] Preset change removes old preset DOM and retains current data.
- [ ] Preview, desktop overlay and fullscreen show the same current map.
- [ ] Background art and graphs continue loading across repeated map changes.
- [ ] Modifier change recalculates the analysis.
- [ ] Key count, LN percentage/DAN and map metadata remain available when their
  source provides them.

### Pause Coach

- [ ] No previous-attempt data at the start of a new play.
- [ ] Pause snapshot contains current score, accuracy, combo, map time and
  timing data.
- [ ] Resume updates the same attempt.
- [ ] Retry creates a clean attempt.
- [ ] Results preserve the final diagnosis.
- [ ] No JS/native metric oscillation.
- [ ] Hidden gameplay collection is visible immediately on pause.

### Shutdown and release

- [ ] Clean WebView/headless worker disposal.
- [ ] Clean Tosu handling and owned-process behavior.
- [ ] No hanging process or unobserved exception in `application.log`.
- [ ] Installer/update path tested on a clean Windows environment.
- [ ] Package hashes and release notes verified.

## Deferred backlog

These items are intentionally not part of architecture completion:

- [ ] Evaluate precise/key-state Tosu APIs before any per-column realtime
  claims.
- [ ] Evaluate lazer replay ingestion only as a separate product feature.
- [ ] Extract replay/realtime packages for external consumers only when an
  actual consumer exists.
- [ ] Consider a separate Infrastructure assembly only when another host needs
  reuse.
- [ ] Consider repository branch protection settings after explicit owner
  authorization.

## Per-PR completion checklist

Every implementation PR must include:

- [ ] one coherent responsibility or migration slice;
- [ ] current owner and new owner stated in the PR description;
- [ ] user-visible behavior explicitly preserved or intentionally corrected;
- [ ] tests that fail before the production change where practical;
- [ ] stale/lifecycle/cancellation behavior covered;
- [ ] no unrelated feature work;
- [ ] no silent fallback;
- [ ] complete diff review;
- [ ] exact validation commands and results;
- [ ] documentation updated when authority/contracts change;
- [ ] legacy deletion list and rollback condition;
- [ ] manual checks clearly left to the user.

## Definition of done

Architecture completion is done only when:

- [ ] Application has one documented authority for overlay runtime semantics;
- [ ] production shadow/legacy state does not compete with it;
- [ ] C# is the only Pause Coach business implementation;
- [ ] realtime and exact replay are separate bounded contexts;
- [ ] WebView consumes one versioned view-state and primarily updates DOM;
- [ ] `MainWindow` owns no major runtime business lifecycle;
- [ ] causal generations prevent stale map, attempt, analysis, replay and
  presentation events from rolling state backwards;
- [ ] WebView recreation preserves active runtime state;
- [ ] desktop, preview and fullscreen use the same composed contract;
- [ ] critical lifecycle scenarios are deterministic tests;
- [ ] CI and packaging are green on configured platforms;
- [ ] one canonical version drives binaries and artifacts;
- [ ] updater tests use isolated paths;
- [ ] docs and TODO describe the code that actually ships;
- [ ] all required human Windows/osu! checks are reported separately;
- [ ] unresolved risks and transitional paths are explicitly listed.

## Required final report

The final architecture PR/series handoff must include:

1. architecture before and after diagrams;
2. changes grouped by runtime, MainWindow, Pause Coach, presentation,
   lifecycle, errors, updater, versioning, tests and docs;
3. explicit list of removed legacy paths;
4. explicit list of remaining transitional paths and deletion conditions;
5. exact test commands and results;
6. manual acceptance still required;
7. unresolved risks without minimizing them.
