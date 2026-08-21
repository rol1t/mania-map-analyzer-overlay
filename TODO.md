# Mania Map Analyzer Overlay — TODO

This file tracks the remaining work after introducing the analyzer-engine foundation. The current overlay behavior is intentionally preserved until the new runtime path reaches feature parity.

## Analyzer runtime integration

- [x] Connect `AnalyzerEngineScriptBridge` to the Avalonia main window and the active Tosu/WebView host (`WebViewAnalyzerScriptHost.cs`, `MainWindow.axaml.cs:725`).
- [x] Add an analyzer-engine supervisor that starts, probes, monitors, and restarts selected engines (`AnalyzerEngineSupervisor.cs`).
- [x] Route Tosu beatmap snapshots, rate, and mods through the typed analysis coordinator (`TosuBeatmapSource.cs` -> `AnalyzerEngineSupervisor.AnalyzeAsync` -> `AnalyzerExecutionCoordinator`).
- [x] Keep the legacy DOM adapter as an explicit, clearly reported fallback when the headless engine is unavailable (`FallbackCode engine.fallback_to_dom_adapter`, `IsFallbackActive`, `Status.headless_fallback`).
- [x] Surface engine compatibility, partial results, failures, and diagnostics in the application UI and logs (`StateChanged` + `PollHeadlessBeatmapAsync`, `AppLogger`, `application.log`).

## Multi-analyzer and multi-widget configuration

- [ ] Add settings for selecting more than one analyzer source for a widget.
- [ ] Add settings for composing multiple widgets from shared analyzer results.
- [ ] Add a visual mapping editor for connecting semantic metrics to widget fields.
- [ ] Persist effective analysis configuration separately from visual preset/profile configuration.
- [ ] Refresh and invalidate scene generations when the map, rate, mods, or selected sources change.

## Rendering and presets

- [ ] Make the existing overlay renderer consume only the domain-level analysis contract.
- [ ] Remove the remaining primary-path dependencies on Tosu/MMA DOM selectors.
- [ ] Finish the external preset template/resource pipeline for HTML, CSS, and optional JavaScript.
- [ ] Add a WYSIWYG preview for composed widgets, including live resize and scale changes.
- [ ] Keep preset errors visible and actionable; never silently fall back to an unrelated layout.

## Compatibility and correctness

- [ ] Add golden parity tests against the official ManiaMapAnalyser 2.0.0 output for Sunny, Daniel, Mixed, Roxy, and Companella.
- [ ] Add transport round-trip fixtures for scalar, structured, array, and future series metric values.
- [ ] Add tests for engine-version, upstream-version, model-version, and effective-configuration cache identity.
- [ ] Verify concurrent requests from different widgets and cancellation on stale map/config generations.
- [ ] Add Linux/macOS smoke coverage for the Avalonia host and WebView message bridge.

## Packaging, updates, and security

- [ ] Include analyzer-engine packages in installer and update artifacts with manifest validation.
- [ ] Add engine package version checks and an update/migration path independent from the main application release.
- [ ] Document and enforce permissions/sandbox boundaries for user-provided analyzer packages and JavaScript.
- [ ] Add integrity checks and clear warnings for untrusted analyzer or preset resources.

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
