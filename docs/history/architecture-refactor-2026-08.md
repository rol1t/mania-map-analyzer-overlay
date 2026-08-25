# Architecture refactor history — August 2026

This document preserves completed and superseded checkpoints that previously
lived in the root `TODO.md`. Git history contains the original item-by-item
checklists. Active architecture work is tracked in
[../ARCHITECTURE_COMPLETION_PLAN.md](../ARCHITECTURE_COMPLETION_PLAN.md).

## Completed replay-analysis foundation

The deterministic replay domain was implemented with:

- opaque replay artifacts and typed replay failures;
- `.osu` parsing, stable `.osr` decoding and deterministic input ordering;
- rice and long-note judging with explicit scoring policies;
- timing, per-column, rolling-window, section and insight metrics;
- rate/mod normalization and separate map/audio clocks;
- replay provenance, fidelity gates and semantic metrics;
- stable replay import and a deliberately unsupported lazer ingestion stub;
- bounded provisional Tosu data that does not claim exact replay fidelity;
- pattern classification and conservative evidence-based correlations;
- deterministic domain and fixture tests.

Exact replay analysis remains a separate concern from realtime Pause Coach.

## Completed realtime Pause Coach foundation

The initial realtime domain and UI were implemented with:

- a bounded realtime session/analyzer;
- play, pause, resume, retry, result, replay and spectator handling;
- centralized options and deterministic insight ranking;
- explicit data-quality and unavailable diagnostics;
- the standalone Pause Coach Card and Companella Replay integration;
- C# and JavaScript lifecycle/adapter/renderer tests.

This work exposed the remaining architectural problem: C# and JavaScript still
contain overlapping Pause Coach behavior and must converge on one C# authority.

## Completed analyzer-engine foundation

The repository gained:

- typed analysis requests, results, diagnostics and structured metrics;
- analyzer planning, deduplication, cancellation and execution generations;
- multi-source/multi-widget composition;
- a DOM-free headless ManiaMapAnalyser package and versioned protocol;
- analyzer package discovery, validation and staged deployment;
- a host-neutral script bridge and worker supervisor;
- Tosu beatmap snapshots and headless analysis integration;
- explicit fallback and compatibility diagnostics;
- package integrity and transport contract tests.

## Completed maintainability work from PR #7

- Common MSBuild properties were centralized in `Directory.Build.props`.
- CI adopted restore -> format -> build -> test ordering on Windows and Linux.
- `.editorconfig` conventions and private-field naming were normalized.
- `HeadlessAnalysisController` was extracted from `MainWindow` with typed keys,
  lifecycle helpers, events and presenter interfaces.
- Headless analysis moved to a dedicated offscreen WebView so visible
  presentation navigation no longer owns the worker document.
- Focused headless key, polling, supervisor and package-deployment tests were
  added, including isolated temporary deployment roots.

## PR #8 runtime migration checkpoint

PR #8 introduced or improved:

- `OverlayRuntimeEvent`, coordinator, reducer and immutable runtime state;
- native C# realtime collection and Pause Coach snapshots;
- pure overlay visibility derivation;
- latest-wins publication and presentation recreation recovery;
- desktop/fullscreen view-state transport;
- native/browser precedence protections;
- additional stable/lazer adapter and renderer tests;
- fixes for map switching, startup rendering, resizing, Tosu failures and
  analyzer worker recovery.

The merged result is intentionally transitional. `MainWindow`, browser
adapter, renderer and JavaScript Pause Coach still retain compatibility
responsibilities. The remaining cutover is described in the active completion
plan.

## Superseded process notes

Previous TODO text referred to uncommitted working-tree files, old line counts,
PR numbers and local implementation status. Those statements became false
after the relevant changes were merged and are retained only through Git
history, not as live tasks.

## Baseline at the completion-plan audit

Audited on 2026-08-24 against `origin/main` at `1894845`:

- restore, formatting verification and Release build passed;
- 326 C# tests passed;
- one golden MMA parity test remained intentionally skipped because official
  external fixtures are not bundled;
- Pause Coach runtime, adapter integration and renderer precedence JavaScript
  suites passed;
- the build emitted an existing analyzer-warning backlog, which the active plan
  treats separately from architecture behavior.
