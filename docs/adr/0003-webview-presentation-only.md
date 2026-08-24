# ADR 0003: WebView is a presentation surface

- Status: Accepted for the incremental migration
- Date: 2026-08-23
- Scope: preview, overlay, fullscreen, and widget documents

## Decision

The WebView receives an immutable view-state snapshot and renders it. It does
not own gameplay state, normalize raw Tosu payloads, calculate Pause Coach
metrics, decide native overlay visibility, or start a second attempt/session.

Native and browser presentation surfaces consume the same versioned view-state
contract. A surface may be unavailable or hidden without stopping collection
or analysis. When it becomes available again, the newest replayable state is
sent to it.

## Consequences

- Navigation, reinjection, and WebView recreation are presentation lifecycle
  events, not analyzer lifecycle events.
- Renderer code can be tested with a fake document and a snapshot fixture.
- The host must retain the latest native snapshot independently of any one
  WebView instance.

## Rejected alternatives

- Using DOM presence as proof that the overlay is visible.
- Clearing the only latest snapshot when replacing a browser.
- Calling browser-side analyzers to fill gaps in native authoritative state.
