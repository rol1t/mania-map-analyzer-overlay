# ADR 0005: One Tosu normalization boundary

- Status: Accepted for the incremental migration
- Date: 2026-08-23
- Scope: native HTTP polling, WebSocket updates, and browser fallback

## Decision

Raw Tosu v2 payloads are normalized once into the shared realtime telemetry
contract. The boundary preserves raw transition diagnostics alongside the
normalized sample, including state name/number, paused flag, beatmap ID, map
time, score, accuracy, judgement totals, and hit-error sample count.

The normalizer is responsible for partial payload retention and for the
osu!lazer pause representation:

```text
state.name = Play
state.number = 2
game.paused = false -> true
```

HTTP and WebSocket transports must feed the same normalizer and produce
equivalent normalized samples. No downstream analyzer or renderer may inspect
raw Tosu JSON to infer gameplay state.

## Consequences

- Stable and lazer state transitions are covered at one boundary.
- Transport differences can be tested independently from analysis rules.
- Any future Tosu schema change has one adapter and one contract-test surface.

## Rejected alternatives

- Maintaining separate native and browser state normalization.
- Treating `state.name = Pause` as the primary lazer pause transition.
- Dropping telemetry when the presentation surface is hidden.
