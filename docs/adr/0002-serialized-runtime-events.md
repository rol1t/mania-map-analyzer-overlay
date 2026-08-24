# ADR 0002: Serialize runtime state transitions

- Status: Accepted for the incremental migration
- Date: 2026-08-23
- Scope: telemetry, visibility, analysis completion, and presentation events

## Decision

All events that can change the overlay runtime state will be processed by one
serialized coordinator/event loop. Raw callbacks may arrive concurrently, but
they are converted to immutable events before state transitions are applied.

Each accepted event produces a monotonic runtime version. Async work may finish
out of order, but a completion can update state only when its map/attempt key
and request version are still current.

## Consequences

- Pause, results, retry, map switch, WebView recreation, and shutdown become
  explicit transitions instead of side effects spread across callbacks.
- Cancellation and stale completion handling are testable without a desktop
  window.
- Existing production wiring remains unchanged until the coordinator is run in
  shadow mode and its decisions are compared with the legacy path.

## Rejected alternatives

- Adding more locks around `MainWindow` fields while retaining several event
  loops.
- Letting each producer invoke the renderer directly.
- Treating dispatcher ordering as a correctness guarantee.
