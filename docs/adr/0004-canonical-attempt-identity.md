# ADR 0004: Canonical attempt and session identity

- Status: Accepted for the incremental migration
- Date: 2026-08-23
- Scope: live play, pause/resume, retry, results, and map changes

## Decision

An attempt is identified by the application-owned session identity together
with beatmap identity and lifecycle evidence. The identity is created when
play begins, retained through pause/resume and results, and replaced on retry
or a confirmed map change.

The identity must be available in normalized telemetry and every derived
Pause Coach snapshot. Producer-local IDs (for example independently generated
browser and native IDs) are not interchangeable merely because they share an
analyzer source ID.

## Consequences

- Partial payloads can retain the current attempt without manufacturing a new
  session.
- A lower map time or reset counters can be treated as retry evidence only in
  the context of the current lifecycle.
- Renderer authority decisions can use positive map/producer evidence instead
  of guessing from arrival order.

## Rejected alternatives

- Deriving identity from a timestamp in each adapter.
- Releasing native authority when a browser session ID differs.
- Treating every pause frame as a new attempt.
