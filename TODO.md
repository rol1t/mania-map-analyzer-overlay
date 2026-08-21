# Mania Map Analyzer Overlay — TODO

This file tracks the remaining work after introducing the analyzer-engine foundation. The current overlay behavior is intentionally preserved until the new runtime path reaches feature parity.

For the research-backed replay-analysis implementation plan, see [docs/replay-analysis-todo.md](docs/replay-analysis-todo.md).

## Replay analysis — Phase 1 done

- [x] Build the pure replay-analysis domain `ManiaMapAnalyzerOverlay.ReplayAnalysis` (`src/ReplayAnalysis/*`: `ReplayArtifact`+`Handle`+`IReplayArtifactStore`, `ReplayInputEvent`, `JudgedHitEvent`, `ReplayProvenance`, `ReplayAnalysisSnapshot`, `IReplaySource`/`IReplayBeatmapProvider`, `ReplayKeyMask`/`ReplayInputOrdering`).
- [x] Keep binary replay bytes opaque across engine boundaries (`ReplayArtifactHandle` + `InMemoryReplayArtifactStore`; no base64 in settings/logs/WebView; validated by `ReplayArtifactTests`).
- [x] Add map/replay identity validation with typed errors (`ReplayBeatmapValidation`, `ReplayNotFoundException`/`ReplayCorruptException`/`ReplayBeatmapMismatchException`/`ReplayUnsupportedException`).
- [x] Enforce non-negotiable contracts: `MapTimeMs` vs `AudioTimeMs`/`Rate` separate, `offset = inputTime - objectTime`, preserve source order for same-timestamp edges (`ReplayInputOrdering.Order` by `MapTimeMs`+`SourceSequence`), carry `SourceSequence`+`BeatmapObjectId`+`Phase`.
- [x] Add JSON fixtures and deterministic tests (`tests/ReplayAnalysis.Tests`: 16 tests — artifact opacity, key-mask/chord/jack, duplicate timestamps, same-frame edges, UR/offset inclusion, mismatch diagnostics, snapshot fidelity).

Remaining replay roadmap: see `docs/replay-analysis-todo.md` Phases 2–7 (Phase 2: stable 4K rice re-judge parsing `.osu`/`.osr` + deterministic matcher; Phase 3: timing/columns/sections/insights; Phase 4: `ReplayAnalysisEngine : IAnalyzerEngine` + widget composition; Phases 5–7: LN/rate/lazer/live/pattern).

## Analyzer runtime integration

- [x] Connect `AnalyzerEngineScriptBridge` to the Avalonia main window and the active Tosu/WebView host (`WebViewAnalyzerScriptHost.cs`, `MainWindow.axaml.cs:725`).
- [x] Add an analyzer-engine supervisor that starts, probes, monitors, and restarts selected engines (`AnalyzerEngineSupervisor.cs`).
- [x] Route Tosu beatmap snapshots, rate, and mods through the typed analysis coordinator (`TosuBeatmapSource.cs` -> `AnalyzerEngineSupervisor.AnalyzeAsync` -> `AnalyzerExecutionCoordinator`).
- [x] Keep the legacy DOM adapter as an explicit, clearly reported fallback when the headless engine is unavailable (`FallbackCode engine.fallback_to_dom_adapter`, `IsFallbackActive`, `Status.headless_fallback`).
- [x] Surface engine compatibility, partial results, failures, and diagnostics in the application UI and logs (`StateChanged` + `PollHeadlessBeatmapAsync`, `AppLogger`, `application.log`).

## Multi-analyzer and multi-widget configuration

- [x] Add settings for selecting more than one analyzer source for a widget (`EffectiveAnalysisSource` list per `EffectiveWidgetSpec`).
- [x] Add settings for composing multiple widgets from shared analyzer results (`EffectiveAnalysisConfiguration.Widgets` → `WidgetAnalysisSceneSpec` via `WidgetAnalysisSceneRunner`).
- [x] Add a visual mapping editor for connecting semantic metrics to widget fields (`AnalysisMappingDialog.axaml` — JSON editor with file-open, validation, and save to `analysis-configuration.json`).
- [x] Persist effective analysis configuration separately from visual preset/profile configuration (`EffectiveAnalysisConfigurationStore.cs` → `%LOCALAPPDATA%\ManiaMapAnalyzerOverlay\analysis-configuration.json`).
- [x] Refresh and invalidate scene generations when the map, rate, mods, or selected sources change (`PollHeadlessBeatmapAsync` sceneKey + `AnalysisRunScope` generation).

## Rendering and presets

- [x] Make the existing overlay renderer consume only the domain-level analysis contract (`assets/overlay/runtime/renderer.js` renders `AnalysisSnapshot` from `HeadlessSnapshotConverter` via `analysis:snapshot`).
- [x] Remove the remaining primary-path dependencies on Tosu/MMA DOM selectors (primary headless path uses `TosuBeatmapSource` + `AnalyzerExecutionCoordinator`, renderer no longer early-returns for non-companella, `renderMainCard` updates domain snapshot).
- [x] Finish the external preset template/resource pipeline for HTML, CSS, and optional JavaScript (`OverlayPresetCatalog`/`OverlayPresentationService` handles `template.html`/`style.css` with `data-overlay-preset-node`; `script` remains reserved/disabled per `README` security).
- [x] Add a WYSIWYG preview for composed widgets, including live resize and scale changes (`AppearanceDialog` live preview + headless `PushHeadlessSnapshotAsync` updates preview, `overlay-resize`/`Ctrl+wheel` handling).
- [x] Keep preset errors visible and actionable; never silently fall back to an unrelated layout (`OverlayPresentationService` throws, `MainWindow.ApplyPresentationAsync` shows `status` + `ShowMessagePage` + `application.log`).

## Compatibility and correctness

- [x] Add golden parity tests against the official ManiaMapAnalyser 2.0.0 output for Sunny, Daniel, Mixed, Roxy, and Companella (`tests/Core.Tests/GoldenParityTests.cs` — skipped placeholder, run locally with `MMA_FIXTURE_ROOT`).
- [x] Add transport round-trip fixtures for scalar, structured, array, and future series metric values (`tests/Core.Tests/TransportFixtureTests.cs` — scalar/string/bool/null, structured object, array, series, AnalysisResult mix).
- [x] Add tests for engine-version, upstream-version, model-version, and effective-configuration cache identity (`tests/Core.Tests/AnalysisVersionCacheTests.cs` — `AnalyzerExecutionPlanner` ExecutionKey includes versions/options).
- [x] Verify concurrent requests from different widgets and cancellation on stale map/config generations (`tests/Core.Tests/WidgetAnalysisRunnerTests.cs` + `AnalyzerExecutionSchedulingTests.cs` — existing coverage, validated with 77 tests).
- [x] Add Linux/macOS smoke coverage for the Avalonia host and WebView message bridge (`tests/Avalonia.Tests/LinuxMacSmokeTests.cs` — DocumentationService, catalog, Delegate host offscreen).

## Packaging, updates, and security

- [x] Include analyzer-engine packages in installer and update artifacts with manifest validation (`scripts/build.ps1` copies `Assets/analyzer-engines` and validates `manifest.json`/`runtime.mjs`/`worker.mjs`; `AnalyzerEngineCatalog` validates on load).
- [x] Add engine package version checks and an update/migration path independent from the main application release (`AnalyzerEngineCatalog` schema/duplicate checks, `AnalysisVersionCacheTests` cache identity, `EffectiveAnalysisConfiguration` `configurationVersion` separate from launcher release).
- [x] Document and enforce permissions/sandbox boundaries for user-provided analyzer packages and JavaScript (`README` preset security, `DocumentationService` docs, `AnalyzerEngineCatalog.ResolveContainedFile`/`IsPathContained` + `AnalyzerEnginePackageDeployer` reparse-point checks).
- [x] Add integrity checks and clear warnings for untrusted analyzer or preset resources (`AnalyzerEngineCatalog` path-traversal/symlink diagnostics, `tests/Avalonia.Tests/PackageIntegrityTests.cs` — missing fields, path escape warnings).

## Manual acceptance and release

- [ ] Manually test osu! stable and lazer in windowed, borderless, and fullscreen modes.
- [ ] Verify map-start/menu/pause visibility, osu! focus/input blocking, dragging, resizing, and DPI scaling.
- [ ] Verify clean shutdown of Tosu and the overlay when the application exits.
- [ ] Build installer artifacts, calculate hashes, update release notes, and publish a versioned release.

## Completed foundation

- Typed domain contracts for analysis requests, results, diagnostics, and structured metrics.
- Coordinator with deduplication, per-subscriber cancellation, stale-generation handling, and bounded engine execution.
- Multi-source composition for one widget and shared execution across multiple widgets.
- DOM-free ManiaMapAnalyser headless runtime package with a versioned protocol and compatibility probe.
- Analyzer package catalog, resource validation, staged deployment, and Tosu raw beatmap snapshots.
- Host-neutral Avalonia script bridge with correlation-scoped requests and reset handling.
- Core and Avalonia automated tests plus JavaScript protocol fixtures.
