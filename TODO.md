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

## Architecture completion backlog

### P0 — establish safe authority prerequisites

- [ ] [WP0 — baseline and characterization](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-0--baseline-and-characterization)
  - [ ] Refresh architecture ownership documentation against merged `main`.
  - [x] Record current/target ownership and explicit migration gates in
    `docs/ARCHITECTURE.md`.
  - [x] Add raw-Tosu-to-fake-presenter scenario harness covering hidden
    collection and latest paused-frame delivery (`NativeRealtimeApplicationScenarioTests`).
  - [ ] Add stable/lazer lifecycle, partial-packet and recreation fixtures.
  - [x] Add dependency and JavaScript-responsibility convention tests
    (`ArchitectureConventionTests`): Application/ReplayAnalysis stay platform
    independent and renderer does not open Tosu transports.
- [ ] [WP1 — realtime bounded context](docs/ARCHITECTURE_COMPLETION_PLAN.md#work-package-1--realtime-bounded-context)
  - [ ] Separate realtime/Pause Coach domain from exact `ReplayAnalysis`.
  - [ ] Keep raw Tosu JSON at the infrastructure boundary.
  - [ ] Move tests and project dependencies without behavior changes.
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
  - [ ] Collect continuously while presentation is hidden/unavailable.
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
