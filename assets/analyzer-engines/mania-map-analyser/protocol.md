# Headless analyzer protocol

This package is a DOM-free runtime bridge for the official ManiaMapAnalyser
pipeline. It does not parse the analyzer page and it does not contain HTML or
CSS selectors.

## Configuration

Create a `HeadlessAnalyzerRuntime` from `runtime.mjs` and provide a `baseUrl`
where the official ManiaMapAnalyser modules are served as JavaScript modules:

```js
const runtime = new HeadlessAnalyzerRuntime({
  configuration: {
    baseUrl: "http://127.0.0.1:24050/ManiaMapAnalyser/",
    pipelinePath: "js/pipeline/runAnalysisPipeline.js",
    companellaPath: "js/estimator/companellaEstimator.js",
    mixedEstimatorPath: "js/estimator/mixedEstimator.js"
  }
});
await runtime.initialize();
```

`baseUrl` is intentionally required. The host must decide which trusted MMA
version is compatible with the package. The pipeline and Companella exports can
be overridden in the configuration when an upstream version moves a module.

`runtime.ready` is emitted only after importing and validating the required
pipeline export. It contains `capabilities` and a `compatibility` object. The
pipeline is mandatory; initialization fails visibly when it is unavailable.
Companella exports are probed as optional compatibility features. Missing
classifier or Mixed-finalizer exports downgrade the effective capabilities and
produce structured diagnostics instead of advertising full Companella support.

## Request and response

Each request has a unique `correlationId` and contains the raw `.osu` text,
the selected `requestedAlgorithm`, typed `speedRate`, normalized `mods`, and
the algorithm options. The response
contains both `requestedAlgorithm` and `actualAlgorithm`; `Mixed` can therefore
be rendered as the algorithm that actually handled the map.

Responses have one of these statuses:

- `ok`: pipeline and requested optional stages completed.
- `partial`: the pipeline completed but an optional stage or Companella
  post-processing returned a diagnostic.
- `error`: the request could not produce an analysis result. `error` always
  contains a machine-readable `code`, `stage`, `message`, and serialized
  exception details.
- `cancelled`: the request was cancelled or superseded by a newer request.

Requests with different correlation IDs execute concurrently by default, so
separate widgets and analysis profiles do not cancel each other. Cancellation
is correlation-scoped through `runtime.cancel(correlationId)`.

`speedRate` is also mirrored as `rate` for protocol consumers that use the
short name. Both values are positive finite numbers; `mods` is an uppercase
array. The worker forwards these execution dimensions to the upstream pipeline
without making them part of the widget or DOM contract.

For a latest-request-wins flow, the host can add `scopeId` and an integer
`generation`. A newer generation cancels only older jobs in that same scope.
The optional `supersedePending` runtime setting remains available for hosts
that deliberately want every new request to cancel all pending requests, but
it defaults to `false`.

## Semantic metrics

`analysis.metrics` is keyed by stable domain IDs rather than upstream fields or
DOM classes. A value is an object with `id`, `value`, and an optional `unit`:

```json
{
  "difficulty.star": { "id": "difficulty.star", "value": 5.17, "unit": "SR" },
  "algorithm.requested": { "id": "algorithm.requested", "value": "Mixed", "unit": "algorithm" },
  "algorithm.actual": { "id": "algorithm.actual", "value": "Roxy", "unit": "algorithm" },
  "skills.stream": { "id": "skills.stream", "value": 19.0, "unit": "MSD" },
  "dan.rc.label": { "id": "dan.rc.label", "value": "Reform 4 mid/high", "unit": "label" }
}
```

Widgets should use `availableMetricIds` and handle missing metrics explicitly.
The initial IDs are listed in `manifest.json`. Additional upstream output is
available in `analysis.rawResult` only when `includeRawResult` is requested.

## Companella compatibility

The pipeline itself returns the Sunny-backed result for Companella and exposes
the Etterna values needed by the ONNX classifier. The worker separately imports
`classifyCompanellaDifficulty` and runs it when the requested or actual
algorithm is Companella. Direct Companella replaces the final difficulty label
and numeric value with classifier output.

The runtime preserves option value types and forces `withEtterna` and
`withInterlude` on for Companella-capable profiles because those stages supply
the classifier features, matching the upstream UI behavior.

When `Mixed` returns a `mixedCompanellaPlan`, the worker also imports the
upstream `applyCompanellaToMixedResult` export. This preserves the official
composition of the Companella RC result with the existing LN result. Missing
classifier or Mixed-finalizer APIs produce a `partial` response with a
structured diagnostic. The exception is logged with `console.error`; no
failure is silently hidden.

Displayed `skills.*` metrics always come from the primary `ettResult`. A
different-version `companellaEttResult` is classifier input only and never
replaces the Etterna skill values shown by widgets.
