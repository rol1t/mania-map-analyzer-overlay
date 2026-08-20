# Analyzer adapters

The application core does not depend on tosu, ManiaMapAnalyser, a browser DOM, or a preset. It consumes the versioned `AnalysisSnapshot` contract from `src/Core/Analysis`.

An analyzer integration is a trusted package under `Assets/analyzers/<adapter-id>` containing:

- `manifest.json` — identity, analyzer routes, host selectors, capabilities, and snapshot schema version.
- `adapter.js` — source-specific extraction code. DOM selectors and source text parsing belong only here.

The bridge emits normalized data in two ways:

```text
window event: analysis:snapshot
native message: analysis:<adapter-id>:<snapshot-json>
```

`AnalyzerCoordinator` accepts messages only from the selected adapter and only when the source ID and schema version match. Preset renderers consume the normalized snapshot rather than querying analyzer-specific elements.

Preset visibility is configured independently in the preset's `manifest.json`
with `visibilityPolicy`. Supported values are `always`, `outside-play`,
`during-play`, `paused-only`, and `never`. The application evaluates this
policy against `Gameplay.IsPlaying` and `Gameplay.IsPaused`; an analyzer
adapter only supplies those normalized values and never decides which preset
should be visible.

The presentation namespace is deliberately analyzer-neutral: runtime hooks use `overlay-*` and domain data events use `analysis:*`. No analyzer-branded namespace is part of the shipped styles or bridge protocol. `overlay-comp-*` is limited to the Companella preset's own template and renderer, while source-specific DOM selectors remain isolated inside the adapter package.

To add another bundled adapter:

1. Add its package directory and manifest under `assets/analyzers`.
2. Convert its native output to `AnalysisSnapshot` in `adapter.js`.
3. Declare the analyzer page, optional settings/fullscreen routes, and the host/anchor selectors in the manifest.
4. Add fixture-based normalization tests, then verify the package in the Appearance dialog.

Analyzer scripts execute in the local analyzer page. Only adapters shipped and reviewed with the application should be distributed as trusted packages.
