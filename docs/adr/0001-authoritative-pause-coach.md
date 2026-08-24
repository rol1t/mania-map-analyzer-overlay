# ADR 0001: C# owns the authoritative Pause Coach state

- Status: Accepted for the incremental migration
- Date: 2026-08-23
- Scope: realtime gameplay analysis and native overlay presentation

## Decision

The C# realtime collector and analyzer are the authoritative source for the
Pause Coach state used by the native overlay. Browser-side Tosu data may remain
useful for preview and compatibility, but it must not overwrite an
authoritative native snapshot for the same attempt.

Authority is based on producer identity and positive beatmap/attempt evidence;
the analyzer `sourceId` alone is not a producer identity. A native snapshot is
kept until another beatmap or a newer native attempt is positively observed.

## Consequences

- The native path must continue collecting while the overlay HWND is hidden.
- Native snapshots need a canonical attempt/session identity.
- Renderer merge rules must preserve native Pause Coach, gameplay, and replay
  blocks when a browser snapshot is partial or uses an independently generated
  session ID.
- Browser scripts remain a presentation/input adapter during the migration.

## Rejected alternatives

- Treating browser and native session IDs as comparable because they share an
  analyzer `sourceId`.
- Selecting whichever snapshot arrived last.
- Making the WebView the source of truth for native overlay diagnosis.
