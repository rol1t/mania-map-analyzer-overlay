# Mania Map Analyzer Overlay — active TODO

The current priority is to finish the incremental runtime architecture
migration without adding product features.

The detailed execution source of truth is
[docs/ARCHITECTURE_COMPLETION_PLAN.md](docs/ARCHITECTURE_COMPLETION_PLAN.md).
Architecture rationale and the original migration design remain in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Completed checkpoints were moved
to [docs/history/architecture-refactor-2026-08.md](docs/history/architecture-refactor-2026-08.md).

## Execution rules

- [ ] Work in small, reviewable PRs; keep every intermediate state buildable.
- [ ] Add characterization or failing regression coverage before replacing an
  authority path.
- [ ] Preserve user-visible behavior unless the change fixes a confirmed bug.
- [ ] Do not delete legacy behavior until replacement parity is demonstrated.
- [ ] Do not add new metrics, presets or other product features during this
  architecture pass.
- [ ] Keep exact replay analysis independent from realtime Pause Coach.
- [ ] Leave real Windows/osu! acceptance to the human tester and report it
  separately from automated tests.

## Luna-ready execution queue

This section turns the remaining work packages into bounded implementation
tasks that can be assigned to GPT-5.6 Luna one at a time. Each task is one
reviewable branch/PR. Do not combine adjacent tasks merely because they touch
the same files.

Common instructions for every Luna task:

- inspect the current branch before editing; paths below are starting points,
  not permission to assume the implementation is unchanged;
- preserve user-visible behavior unless a failing regression test proves a bug;
- add characterization/failing coverage before changing an authority path;
- keep realtime and exact replay state separate;
- do not commit, push, merge, tag or release without explicit user approval;
- review the complete diff and report changed files, exact test commands,
  results, unverified manual behavior and remaining risks;
- for any C# source change, finish with the repository format, Release build
  and full .NET test gates; run affected Runtime JavaScript tests when the
  presentation contract or browser assets change.

Canonical full validation commands:

```powershell
dotnet restore ManiaMapAnalyzerOverlay.sln
dotnet format ManiaMapAnalyzerOverlay.sln --no-restore
dotnet build ManiaMapAnalyzerOverlay.sln --configuration Release --no-restore --nologo
dotnet test ManiaMapAnalyzerOverlay.sln --configuration Release --no-build --nologo
node tests/Runtime/pause-coach-runtime.test.js
node tests/Runtime/adapter-pause-coach-integration.test.js
node tests/Runtime/renderer-native-precedence.test.js
```

### Wave A — deterministic Application contracts

- [x] **LUNA-01 — stable/lazer lifecycle characterization fixtures**
  - **Depends on:** WP1, already complete.
  - **Goal:** establish raw-payload-to-view-state regression coverage before
    changing runtime authority.
  - **Primary files:**
    `tests/Avalonia.Tests/TosuRealtimeCollectorTests.cs`,
    `tests/Avalonia.Tests/NativeRealtimeApplicationScenarioTests.cs`,
    `tests/Avalonia.Tests/CurrentPipelineCharacterizationTests.cs`, and test
    fixtures under `tests/` if reusable payload files are preferable.
  - **Implement:** realistic stable and lazer sequences for menu/song select,
    play, pause, resume, retry, fail and results; partial packets with missing
    beatmap identity; and disconnect/reconnect transport generations. Leave
    presentation recreation while an attempt is active to LUNA-07. The normal
    lazer pause fixture must use `state.name=Play`, `state.number=2`,
    `game.paused=true`.
  - **Assertions:** map time and telemetry advance while presentation is
    hidden; pause/resume retain one attempt; positive retry evidence creates a
    new attempt; partial packets do not erase known identity; and an old
    transport generation cannot publish after reconnect.
  - **Do not:** call `RealtimePlayAnalyzer.Process` directly for integration
    scenarios, invent unsupported Tosu fields, or change production behavior
    merely to simplify fixtures.
  - **Done when:** WP0 fixture gaps are covered deterministically and the full
    .NET suite passes.

- [x] **LUNA-02 — versioned headless-analysis request slot**
  - **Depends on:** LUNA-01.
  - **Goal:** reject stale headless completions using causal request identity,
    including two requests for the same beatmap/configuration.
  - **Primary files:** `src/Application/OverlayRuntimeEvent.cs`,
    `OverlayRuntimeState.cs`, `OverlayRuntimeReducer.cs`,
    `OverlayRuntimeAnalysisCausality.cs`,
    `src/Avalonia/Features/Analysis/HeadlessAnalysisController.cs`,
    `RuntimeAnalysisSnapshotPresenter.cs`, and matching Application/Avalonia
    tests.
  - **Implement:** a typed `AnalysisRequestId`; explicit analysis
    start/completion/failure events carrying request ID, beatmap generation and
    effective configuration identity; replace the unversioned pending-analysis
    state with a slot that records the active request and its status. Allocate
    the ID before work starts and preserve it through every completion path.
  - **Assertions:** an older completion for another map is rejected; an older
    completion for the same map/config is rejected; completion/failure for an
    unknown request is rejected with a typed diagnostic; the accepted request
    updates the composed view exactly once.
  - **Do not:** add replay identity here, rely on dispatcher/queue order, or
    compare display strings to decide causality.
  - **Completed:** `AnalysisRequestId` now flows from request allocation through
    start, success and failure events into the Application reducer. The runtime
    owns an explicit `AnalysisRequestSlot` with request, map-generation,
    canonical full-effective-configuration identity, status and diagnostic
    fields; stale/unknown/mismatched completions are rejected with typed
    diagnostics. The presenter, navigation replay and temporary replay
    enrichment paths preserve request causality. Anonymous snapshots remain
    observational after versioned authority, except for the explicitly tagged
    browser fallback used only while the headless producer is inactive.
    Application and Avalonia tests cover same-target out-of-order completion,
    unknown/failing requests, configuration mismatch, fallback recovery,
    generation rejection and request-aware delivery.
  - **Done when:** `AnalysisRequestSlot` is the only production analysis
    completion slot and all analysis causality tests pass.

- [ ] **LUNA-03 — independent versioned replay request slot**
  - **Depends on:** LUNA-02.
  - **Goal:** represent exact replay execution separately from headless map
    analysis and realtime Pause Coach.
  - **Primary files:** Application runtime event/state/reducer/composer files,
    `src/Avalonia/Services/ReplayAnalysisSession.cs`, replay command forwarding
    in `MainWindow.axaml.cs`, and replay/Application tests.
  - **Implement:** typed `ReplayRequestId`; replay start/completion/failure and
    cancellation semantics; a dedicated replay slot carrying request ID,
    beatmap generation and result/error state. Forward the same ID from request
    creation through completion rather than converting replay output into an
    anonymous headless snapshot.
  - **Assertions:** replay and headless requests can be active concurrently;
    one cannot overwrite the other's slot; stale replay completion after a map
    or newer replay request is rejected; replay failure leaves current realtime
    state intact; view-state exposes only the accepted replay result.
  - **Do not:** infer realtime per-column metrics from replay data or reset the
    live attempt when replay import starts/completes.
  - **Done when:** replay causality is explicit in Application and has no shared
    pending/completion slot with headless analysis.

- [ ] **LUNA-04 — explicit visibility state and view-state versioning**
  - **Depends on:** LUNA-02; may be completed before LUNA-03 only if replay
    contracts are untouched.
  - **Goal:** separate policy intent from platform feedback and publish only
    meaningful presentation changes.
  - **Primary files:** `OverlayRuntimeState.cs`, `OverlayRuntimeEvent.cs`,
    `OverlayRuntimeReducer.cs`, `OverlayVisibilityDerivation.cs`,
    `OverlayViewState.cs`, `OverlayViewStateComposer.cs`, coordinator tests and
    composer tests.
  - **Implement:** explicit desired visibility, surface readiness and actual
    visibility values/events; an `OverlayViewState` monotonic version owned by
    the presentation contract rather than copied blindly from runtime event
    count; equality/change detection based on rendered contract fields.
  - **Assertions:** policy/preset/visibility changes publish without telemetry;
    semantically identical packets do not churn view-state; readiness does not
    masquerade as visibility; stale feedback cannot roll a generation back;
    WebView recreation preserves semantic runtime state while allowing the new
    surface to receive the current view.
  - **Do not:** put HWND/WebView types or drag/resize pixels in Application.
  - **Done when:** desired/ready/actual are independently observable and tests
    prove monotonic, content-based view-state publication.

### Wave B — transport and presentation ownership

- [ ] **LUNA-05 — finish Tosu host reconnect/backoff ownership**
  - **Depends on:** LUNA-01.
  - **Goal:** make `TosuRealtimeRuntimeHost` the complete owner of realtime
    transport lifecycle without changing normalization/domain rules.
  - **Primary files:**
    `src/Avalonia/Features/Analysis/TosuRealtimeRuntimeHost.cs`,
    `OverlayGameplayPollingController.cs`,
    `src/Avalonia/Services/TosuService.cs`, Tosu infrastructure adapters and
    their tests.
  - **Implement:** move remaining reconnect/backoff/retry policy from the Tosu
    service boundary into the host/controller; use injectable time/delay where
    deterministic tests need it; retain typed failure kinds; emit bounded
    transition diagnostics rather than per-packet logs.
  - **Assertions:** stop cancels pending delay/request; callbacks from an old
    transport generation are ignored; transient failures back off and recover;
    not-ready/no-beatmap do not become fatal; malformed payloads remain visible
    typed failures; collection remains independent from presentation state.
  - **Do not:** redesign state normalization, add another polling source, or
    silently swallow transport errors.
  - **Done when:** `MainWindow` and `TosuService` contain no realtime reconnect
    policy and host lifecycle tests are deterministic.

- [ ] **LUNA-06 — logical surface registry and causal generations**
  - **Depends on:** LUNA-04.
  - **Goal:** model desktop overlay, launcher preview and fullscreen as explicit
    independent presentation surfaces.
  - **Primary files:** Application presentation contracts,
    `src/Avalonia/Features/Presentation/OverlayPresentationDeliveryController.cs`,
    `OverlayPresentationService.cs`, `FullscreenViewStateFileSink.cs`,
    preview wiring in `MainWindow.axaml.cs`, and presentation tests.
  - **Implement:** typed surface identity; per-surface generation, readiness and
    actual-visibility feedback; creation/recreation increments only that
    surface; Application contains platform-neutral values only.
  - **Assertions:** feedback from an old desktop/preview/fullscreen generation
    is rejected; recreation of one surface does not invalidate another;
    surfaces can independently become ready/unavailable while consuming the
    same current `OverlayViewState`.
  - **Do not:** implement window show/hide effects or remove browser fallback in
    this task.
  - **Done when:** no single desktop generation is reused as an implicit global
    presentation generation.

- [ ] **LUNA-07 — latest-wins delivery for every surface**
  - **Depends on:** LUNA-06.
  - **Goal:** make delivery resilient across hidden state, slow publication,
    exceptions, navigation and recreation.
  - **Primary files:** `OverlayPresentationDeliveryController.cs`,
    `LatestWinsSnapshotPublisher.cs`, `OverlayPresentationService.cs`,
    `FullscreenViewStateFileSink.cs`, corresponding publisher/controller/sink
    tests.
  - **Implement:** one latest-wins queue per logical surface; retain the newest
    pending view-state while hidden/unavailable; perform no realtime WebView
    invocation while hidden; flush newest immediately after ready/visible;
    isolate stale in-flight completions by surface generation; keep fullscreen
    file replacement atomic and monotonic.
  - **Assertions:** hidden Playing(29s) then Paused(30s) publishes Paused first;
    the same holds for Results; slow publish plus many frames emits current then
    newest only; invocation exception recovers; recreation/navigation/re-entry
    replays latest; preview and desktop progress independently.
  - **Do not:** clear the only latest snapshot during browser replacement or
    tie collection lifetime to delivery readiness.
  - **Done when:** every publisher scenario listed in WP4 passes for all three
    surfaces.

- [ ] **LUNA-08 — visibility platform-effect controller**
  - **Depends on:** LUNA-04 and LUNA-06.
  - **Goal:** remove native show/hide/focus/click-through decisions from
    `MainWindow` while keeping physical behavior platform-specific.
  - **Primary files:** `src/Application/IOverlayWindow.cs`, Avalonia
    presentation/platform services, `OverlayPresentationDeliveryController.cs`,
    `MainWindow.axaml.cs`, and new controller unit tests using fake windows.
  - **Implement:** a focused effect controller consuming desired visibility and
    surface generation; apply idempotent show/hide and supported focus/input
    policy; report actual visibility/readiness back through typed runtime events;
    own cancellation/disposal explicitly.
  - **Assertions:** repeated desired state is idempotent; stale effect completion
    cannot update a new generation; closing osu! restores launcher interaction;
    minimize/restore does not stop collection; disposal executes once.
  - **Do not:** move physical drag coordinates or resize math into Application,
    and do not claim manual Windows behavior from unit tests.
  - **Done when:** `MainWindow` forwards state/effects but no longer decides
    semantic overlay visibility.

### Wave C — Pause Coach and WebView authority cutover

- [ ] **LUNA-09 — prove native Pause Coach delivery on all surfaces**
  - **Depends on:** LUNA-01 and LUNA-07.
  - **Goal:** demonstrate that the canonical C# Pause Coach reaches desktop,
    preview and fullscreen before deleting JavaScript fallback authority.
  - **Primary files:** `src/RealtimeAnalysis/**`, Application composer,
    presentation delivery services, `assets/overlay/runtime/pause-coach.js`,
    Runtime fixtures and native scenario tests.
  - **Implement:** inventory C#/JS lifecycle, thresholds, recent windows and
    insight ordering; encode representative shared fixtures; route native
    `AttemptId` and Pause Coach snapshot through every surface; explicitly gate
    the browser producer only after native delivery is proven for that surface.
  - **Assertions:** all surfaces show the same attempt/session metrics; partial
    browser data cannot replace native state; recreation does not create an
    attempt; stable/lazer play/pause/resume/retry/results agree with canonical
    fixtures; memory/window bounds remain enforced.
  - **Do not:** add unsupported per-column claims, choose outputs merely to make
    parity pass, or delete fallback code in this task.
  - **Done when:** native delivery and producer gating are deterministic for all
    surfaces and remaining intentional differences are documented.

- [ ] **LUNA-10 — remove browser Pause Coach authority and simplify renderer**
  - **Depends on:** LUNA-09 plus the user's manual native-delivery acceptance.
  - **Goal:** make JavaScript presentation-only for Pause Coach and consume one
    complete versioned view-state.
  - **Primary files:** `assets/overlay/runtime/pause-coach.js`, `renderer.js`,
    `host.js`, WebView bridge/publication services, Runtime tests and
    `docs/PAUSE_COACH.md`.
  - **Implement:** remove browser attempt lifecycle, recent-window metrics,
    producer/session arbitration, native snapshot globals and business-block
    merge/preservation; keep localization, formatting, DOM binding, CSS,
    measurement and purely visual input; parse bridge messages into typed DTOs
    at one boundary.
  - **Assertions:** `render(viewState)` deterministically replaces obsolete DOM;
    older view-state versions are ignored; a new surface generation accepts the
    current state; preset replacement retains no old widget state; repository
    convention tests reject Tosu/session/business logic in renderer assets.
  - **Do not:** remove compatibility transport until all hosts use the typed
    envelope, or weaken native-precedence tests before replacing them with the
    simpler authoritative contract.
  - **Done when:** JavaScript owns no gameplay/Pause Coach semantics and all
    Runtime/preset rendering tests pass.

- [ ] **LUNA-11 — switch Application runtime to sole production authority**
  - **Depends on:** LUNA-03, LUNA-08 and LUNA-10.
  - **Goal:** finish the legacy -> dual-run -> new-authoritative cutover.
  - **Primary files:** `OverlayRuntimeCoordinator.cs`,
    `OverlayRuntimeParityComparer.cs`, `MainWindow.axaml.cs`, presentation and
    telemetry callbacks, Application/Avalonia scenario tests.
  - **Implement:** route gameplay, desired visibility, accepted analysis/replay
    and composed view-state exclusively through Application; make legacy
    projection observational during final parity checks; then remove duplicate
    `_overlay*` gameplay/visibility flags, shadow mutations and direct snapshot
    publication. Retain parity comparer only as deterministic diagnostic/test
    infrastructure if it still provides value.
  - **Assertions:** raw payload -> reducer -> composed view -> fake presenter
    passes representative lifecycles; no legacy callback can roll state back;
    every semantic slot has one documented owner; search finds no duplicate
    production gameplay authority.
  - **Do not:** delete a branch before equivalent tests pass or keep a silent
    fallback that can regain authority.
  - **Done when:** Application is the only production runtime authority and WP7
    exit gates pass.

### Wave D — shell reduction and hardening

- [ ] **LUNA-12 — extract composition, ownership and shutdown from MainWindow**
  - **Depends on:** LUNA-11.
  - **Goal:** make `MainWindow` a view/composition shell without an arbitrary
    line-count target.
  - **Primary files:** `src/Avalonia/App.axaml.cs`, `MainWindow.axaml.cs`,
    analysis/presentation/replay/update services and controller tests.
  - **Implement as separately reviewable slices within one PR:** establish a
    composition root; inject runtime/telemetry/presentation/replay/headless/update owners;
    extract replay command workflow and update orchestration; extract remaining
    overlay interaction ownership where it is a real responsibility boundary;
    define one owner and one disposal path for each timer, CTS, semaphore,
    WebView and child process. Leave bindings, dialogs and UI forwarding in the
    view.
  - **Assertions:** controllers can be tested with fake ports; shutdown cancels
    and disposes each owner exactly once; no unobserved tasks survive; existing
    drag/resize/Ctrl+wheel/DPI behavior is unchanged at the contract level.
  - **Do not:** create arbitrary helper classes solely to shorten the file,
    reintroduce WinForms, or move UI/platform details into domain projects.
  - **Done when:** `MainWindow` contains no major runtime business lifecycle and
    ownership/disposal tests pass without constructing a real window.

- [ ] **LUNA-13 — deterministic historical race regression suite and fixes**
  - **Depends on:** LUNA-11; individual fixtures may be added earlier by
    LUNA-01/LUNA-02/LUNA-07.
  - **Goal:** close every confirmed production race against the authoritative
    architecture.
  - **Primary files:** Application/Avalonia/Runtime tests and only the production
    owners implicated by a failing test.
  - **Cover:** carousel A -> transient B -> A; confirmed B replacing A; stale
    rate/mod/config completion; mod change recalculation; partial beatmap packet;
    positive retry reset; `paused=true` with `isPlaying=false`; minimize/restore;
    osu!/Tosu restart; WebView/preset recreation; initial sizing; repeated map
    changes preserving art/graphs/key count/LN data.
  - **Assertions:** each confirmed historical bug has a failing-before test;
    fixes occur at the causal boundary rather than with timers/delays; no map or
    analysis state rolls backwards; no manual resize/reload is required by the
    modeled contracts.
  - **Do not:** add arbitrary debounce offsets, refresh loops or message-string
    special cases to mask deterministic ordering bugs.
  - **Done when:** WP9 automated exit gate passes and genuinely visual/platform
    checks are listed for the user rather than claimed automated.

- [ ] **LUNA-14 — finish structured runtime error boundaries**
  - **Depends on:** LUNA-11; can run after LUNA-05 where files do not overlap.
  - **Goal:** remove remaining message-based decisions from business/runtime
    control flow while preserving readable logs and UI errors.
  - **Primary files:** analyzer bridge/supervisor diagnostics, WebView transport,
    replay session, update workflow, `rg "Message\\.Contains|StartsWith" src` hits,
    and focused tests.
  - **Implement:** small enums/results for analyzer deployment/boot/worker/
    protocol/analysis failures, presentation navigation/invocation/schema
    failures, replay not-found/corrupt/mismatch/unsupported failures and update
    network/integrity/install/state failures; translate external text once at
    the boundary; retain original exception detail for logs.
  - **Assertions:** retry/fallback/localization tests assert typed codes; changing
    English text does not change behavior; malformed/unexpected input remains a
    visible failure; no huge exception hierarchy is introduced.
  - **Do not:** replace harmless display-only formatting checks or hide errors
    behind `Unknown` when a stable typed category is available.
  - **Done when:** no important runtime decision depends on exception message
    wording.

- [ ] **LUNA-15 — finish UpdateService orchestration decomposition**
  - **Depends on:** LUNA-14's update diagnostic contract.
  - **Goal:** leave update networking, downloading, installation, state and
    UI-facing workflow with separate tested owners.
  - **Primary files:** `UpdateService.cs`, `GitHubReleaseClient.cs`,
    `ComponentDownloader.cs`, `ComponentInstaller.cs`, `UpdateStateStore.cs`,
    updater tests and composition wiring.
  - **Implement:** keep/add a thin `UpdateCoordinator`; remove residual download
    or install mechanics from UI orchestration; inject filesystem/temp roots,
    process operations and clock only where tests require them; preserve
    offline startup with installed components, platform detection, rollback and
    self-update behavior.
  - **Assertions:** release/rate-limit/asset selection; digest success/failure;
    interrupted download; install/rollback failure; state schema round-trip;
    offline installed-components startup; all tests use isolated temporary
    roots and never the real Tosu/app installation.
  - **Do not:** change release channels or installer UX as a feature.
  - **Done when:** `UpdateService` no longer mixes the five responsibilities and
    package/update smoke tests remain green.

- [ ] **LUNA-16 — version, CI and documentation closeout**
  - **Depends on:** LUNA-11 through LUNA-15.
  - **Goal:** make repository metadata and documentation match the architecture
    that actually ships, then execute the complete automated release gate.
  - **Primary files:** `VERSION`, `Directory.Build.props`, packaging scripts,
    `.github/workflows/build.yml`, README, TODO, architecture/Pause Coach docs
    and `docs/history/`.
  - **Implement:** remove avoidable active hardcoded product versions while
    preserving clearly historical records; inject the canonical version into
    the standalone updater/package paths; add a duplication check; archive
    completed migration checkpoints; document before/after flow, removed legacy
    paths, any remaining transition and rollback condition; update the manual
    acceptance checklist without marking it executed.
  - **Assertions:** one version bump drives assemblies/updater/packages/artifact
    names; packaging remains after all tests in CI; Windows/Linux jobs retain
    every .NET and Runtime suite; deployment/update tests use isolated roots;
    no new warning is introduced relative to the recorded baseline.
  - **Do not:** change GitHub branch-protection settings, fabricate manual test
    results, or delete historical version mentions that are intentionally dated.
  - **Done when:** full canonical validation and packaging smoke pass, TODO/docs
    describe current code, and the remaining human matrix is handed to the user.

### Human-only completion gate

- [ ] **HUMAN-01 — Windows/osu! acceptance.** Run WP15 after LUNA-16 on real
  osu!stable and osu!lazer. Record client/display mode, exact scenario, result,
  logs and build commit. Luna may prepare builds and diagnose failures, but must
  not mark this gate complete without the user's observed results.

## Architecture completion backlog

### P0 — establish safe authority prerequisites

- [ ] [WP0 — baseline and characterization](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-0--baseline-and-characterization)
  - [x] Refresh architecture ownership documentation against release `v2.4.0`.
  - [x] Record current/target ownership and explicit migration gates in
    `docs/ARCHITECTURE.md`.
  - [x] Add raw-Tosu-to-fake-presenter scenario harness covering hidden
    collection and latest paused-frame delivery (`NativeRealtimeApplicationScenarioTests`).
  - [ ] Add stable/lazer lifecycle, partial-packet and recreation fixtures;
    stable/lazer adapter fixtures are now recorded, while presentation
    recreation coverage remains in LUNA-07.
  - [x] Add dependency and JavaScript-responsibility convention tests
    (`ArchitectureConventionTests`): Application/RealtimeAnalysis/ReplayAnalysis
    stay platform independent and renderer does not open Tosu transports.
- [x] [WP1 — realtime bounded context](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-1--realtime-bounded-context)
  - [x] Separate realtime/Pause Coach domain from exact `ReplayAnalysis`.
  - [x] Keep raw Tosu JSON at the infrastructure boundary.
  - [x] Move tests and project dependencies without behavior changes.
- [ ] [WP2 — complete Application runtime contract](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-2--complete-the-application-runtime-contract)
  - [ ] Add beatmap, attempt, analysis, replay and presentation causal identity.
  - [ ] Add connection, process, replay and surface lifecycle state/events.
  - [x] Add typed Tosu connection state and transport-generation events;
    stale older-generation callbacks are rejected by the reducer.
  - [ ] Separate desired visibility, surface readiness and actual visibility.
  - [x] Publish view-state for every presentation-contract change (first
    regression fix landed on this branch; policy/visibility scenario coverage
    remains part of WP2).
  - [x] Preserve confirmed beatmap/attempt identity across partial realtime
    frames at the reducer boundary.
  - [x] Add an explicit version-1 `OverlayViewState` wire schema and reject
    unsupported future versions before they can replace the last valid render.
  - [x] Reject older native presentation-surface feedback (initial generation
    boundary landed; cross-surface causal identity remains part of WP4).
  - [ ] Reject stale async completions by request/generation, not queue order
    alone.
  - [x] Rejected runtime events expose typed stale-sequence, transport,
    presentation-surface and analysis-generation diagnostics with causal IDs.
  - [x] Analysis snapshot events now carry the observed beatmap generation;
    reducer rejects a causally older completion after a map transition.
  - [ ] Extend the same identity through explicit headless request and replay
    completion contracts.

### P0 — complete data and presentation cutover

- [ ] [WP3 — Tosu runtime host and lifecycle](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-3--tosu-runtime-host-and-lifecycle)
  - [x] Introduce `TosuRealtimeRuntimeHost` for the native payload source,
    normalization and polling lifecycle; it is bound to Tosu process state.
  - [x] Move native gameplay polling timer/CTS/generation/guards into
    `OverlayGameplayPollingController`; Tosu transport/reconnect ownership
    remains in the host for the next slice.
  - [x] Move native collection start/stop to the Tosu lifecycle host;
    `MainWindow` retains only status/UI handling.
  - [ ] Move remaining reconnect/backoff and transport policy out of the
    Tosu service boundary.
  - [x] Replace native realtime transport `null` ambiguity with typed
    `TosuRealtimePayloadResult` failure kinds/status metadata.
  - [x] Collect continuously while presentation is hidden/unavailable; the
    native realtime host follows the Tosu lifecycle rather than any presentation
    surface.
  - [x] Guard queued native polling callbacks against a disposed cancellation
    source (lifecycle hardening slice landed on this branch).
  - [x] Prevent stopped generations from feeding a later lifecycle, including
    a concurrent old request during overlay re-entry.
  - [x] Remove the unused second `TosuService` state-only polling path; native
    collection now has one full-payload normalization boundary.
  - [x] Desktop overlay explicitly gates the browser Pause Coach producer;
    preview/fullscreen gating remains transitional until native delivery is
    proven on those surfaces.
  - [ ] Complete producer gating across preview/fullscreen surfaces after
    native view-state delivery is verified there.
- [ ] [WP4 — presentation and effects layer](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-4--presentation-and-effects-layer)
  - [x] Move desktop/fullscreen latest-wins publisher ownership into
    `OverlayPresentationDeliveryController` while keeping platform effects in
    `MainWindow`.
  - [x] Allocate controller-owned logical generations for desktop and
    fullscreen sessions; MainWindow now reports the desktop generation instead
    of maintaining a parallel counter.
  - [ ] Give desktop, preview and fullscreen explicit surface generations.
  - [ ] Make all delivery latest-wins across hide/show, failure and recreation.
  - [ ] Apply desired visibility through a platform effect/controller.
  - [ ] Keep drag/resize/focus/click-through in focused presentation/platform
    controllers.
- [ ] [WP5 — canonical C# Pause Coach](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-5--canonical-c-pause-coach)
  - [x] Native `RealtimePlayAnalyzer` is documented as the authoritative
    business implementation; the browser runtime is explicitly fallback-only.
  - [x] Inject the canonical C# threshold/window contract into the browser
    fallback so its temporary implementation cannot drift silently.
  - [x] Desktop overlay declares native realtime authority before adapter
    startup, preventing a second browser Pause Coach session on that surface.
  - [ ] Compare and resolve remaining C#/JS lifecycle and insight behavior.
  - [ ] Prove C# delivery for desktop, preview and fullscreen.
  - [ ] Remove JS attempt/session/metric authority after parity and manual gate.
- [ ] [WP6 — dumb renderer and typed WebView protocol](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-6--dumb-renderer-and-typed-webview-protocol)
  - [x] Render one complete versioned `OverlayViewState` envelope (schema
    version 1 is now explicit and guarded at the renderer boundary).
  - [ ] Remove producer/session arbitration and business snapshot merging.
  - [ ] Retain only DOM, CSS, formatting, measurement and visual input work.
- [ ] [WP7 — authority cutover and legacy removal](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-7--authority-cutover-and-legacy-removal)
  - [ ] Make Application the production runtime authority after parity.
  - [ ] Remove shadow flags, duplicate gameplay state and direct publication.
  - [ ] Retain parity tooling only if it remains useful as tests/diagnostics.

### P1 — finish maintainability boundaries

- [ ] [WP8 — reduce MainWindow](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-8--reduce-mainwindow-to-a-compositionui-shell)
  - [ ] Establish a clear composition root.
  - [ ] Extract runtime, presentation, interaction, replay, update and shutdown
    owners based on responsibility.
  - [x] Dispose the Application runtime coordinator from the window shutdown
    path (preparatory ownership slice).
  - [ ] Leave the view with initialization, binding, UI forwarding and dialogs.
- [ ] [WP9 — lifecycle and race hardening](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-9--lifecycle-and-race-hardening)
  - [ ] Cover carousel map changes, stale analysis, retries and partial packets.
  - [x] Ignore a queued `Process.Exited` callback from an old Tosu instance
    after restart; exit identity is checked by process instance.
  - [ ] Cover minimize/restore, osu!/Tosu restart and WebView recreation.
  - [ ] Preserve map art, graphs, modifier recalculation, key/LN metadata and
    initial sizing across transitions.
- [ ] [WP10 — structured error model](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-10--structured-error-model)
  - [x] Add typed Tosu beatmap-source failure kinds and use route/status
    metadata before compatibility message checks.
  - [x] Headless Tosu source-state decisions now use the typed failure kind
    (including nested typed failures) instead of matching exception text.
  - [x] Wrapped Tosu source failures preserve the original failure kind,
    route and HTTP status through the application boundary.
  - [x] Analyzer probe/runtime decisions now classify structured diagnostic
    codes and bridge-attached failure kinds; English analyzer error text is
    retained only at the external protocol boundary.
  - [ ] Replace remaining message-based runtime decisions with small typed
    outcomes.
  - [ ] Keep human-readable detail for UI/logging and preserve visible failures.
- [ ] [WP11 — UpdateService decomposition](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-11--updateservice-decomposition)
  - [x] Separate GitHub release lookup into `GitHubReleaseClient`.
  - [x] Separate archive streaming, progress reporting and SHA-256 integrity
    validation into `ComponentDownloader` with isolated HTTP/temp-path tests.
  - [x] Separate persisted install state into injected `UpdateStateStore` with
    isolated temporary-path tests.
  - [x] Persist install state through a temporary file and replace so an
    interrupted write cannot leave a partially serialized state document.
  - [ ] Separate download, install and UI-facing orchestration.
  - [x] Add isolated filesystem/process tests for the installer boundary;
    orchestration and rollback-failure coverage remains open.
- [ ] [WP12 — single version source](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-12--single-version-source)
  - [x] Define the product version once in the root `VERSION` file and import
    it into .NET assembly/package metadata.
  - [x] Derive updater user agents, packaging scripts, CI package names and
    artifact paths from the canonical version.
  - [ ] Remove remaining historical release-number examples from prose.

### P1 — close the migration

- [ ] [WP13 — CI and quality gates](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-13--ci-and-quality-gates)
  - [ ] Keep restore -> format -> build -> all tests -> package green on the
    configured Windows/Linux jobs.
  - [x] Add the current architecture/runtime/presentation suites to the
    solution and retain all three production runtime JS tests in CI.
  - [x] Track the local Release warning baseline (94 warnings, 0 errors)
    without blanket suppression.
- [ ] [WP14 — documentation cleanup](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-14--documentation-and-backlog-cleanup)
  - [ ] Keep source-of-truth and transitional-path documentation accurate.
  - [ ] Correct stale README, Pause Coach and migration statements.
  - [ ] Publish before/after architecture and removed-legacy lists.
- [ ] [WP15 — manual Windows/osu! acceptance](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-15--manual-windowsosu-acceptance)
  - [ ] Test stable/lazer and supported display modes.
  - [ ] Test full lifecycle, presentation, interaction and shutdown matrices.
  - [ ] Test Pause Coach authority with no stale/oscillating attempt data.

## Current non-architecture follow-ups

- [ ] Run the human Windows/osu! acceptance matrix before declaring the
  architecture series complete.
- [ ] Run the external official-MMA golden parity suite when pinned fixtures are
  available; the CI placeholder is intentionally skipped today.
- [ ] Build and verify installer artifacts, hashes and release notes after all
  architecture work and manual acceptance pass.

## Deferred product work

- [ ] Evaluate a precise/key-state Tosu source before enabling per-column or
  canonical realtime pattern claims.
- [ ] Revisit lazer replay ingestion as a separate feature.
- [ ] Consider external domain packages or another Infrastructure assembly only
  when a real additional consumer exists.
- [ ] Recommend branch protection, but do not change GitHub settings without
  explicit repository-owner authorization.
