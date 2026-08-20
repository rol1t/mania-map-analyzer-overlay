# tosu game states

The overlay reads the current osu! screen from the `state` object exposed by
tosu's v2 API:

```json
{
  "state": {
    "number": 5,
    "name": "SelectPlay"
  }
}
```

The same object is available from `GET /json/v2` and
`/websocket/v2`. The official response example is documented in the
[tosu v2 WebSocket API wiki](https://github.com/tosuapp/tosu/wiki/v2-websocket-api-response),
and the enum is defined in
[`packages/common/enums/osu.ts`](https://github.com/tosuapp/tosu/blob/master/packages/common/enums/osu.ts).

Pause is not a separate `GameState` value. tosu reports it as
`game.paused`, so a paused map normally has `state.name == "Play"`,
`state.number == 2`, and `game.paused == true`.

## State enum

The numeric values below follow the order of tosu's `GameState` TypeScript
enum. Names are shown as source identifiers; the API can return a different
case, so consumers must compare them case-insensitively.

| Number | tosu name | Meaning |
|---:|---|---|
| 0 | `menu` | Main menu |
| 1 | `edit` | Beatmap editor |
| 2 | `play` | Active gameplay |
| 3 | `exit` | osu! is exiting |
| 4 | `selectEdit` | Editor beatmap selection |
| 5 | `selectPlay` | Solo song selection |
| 6 | `selectDrawings` | Drawings selection |
| 7 | `resultScreen` | Results screen |
| 8 | `update` | Update screen |
| 9 | `busy` | Busy/loading state |
| 10 | `unknown` | State is not recognized by tosu |
| 11 | `lobby` | Multiplayer lobby |
| 12 | `matchSetup` | Multiplayer match setup |
| 13 | `selectMulti` | Multiplayer beatmap selection |
| 14 | `rankingVs` | Versus ranking screen |
| 15 | `onlineSelection` | Online selection screen |
| 16 | `optionsOffsetWizard` | Offset wizard |
| 17 | `rankingTagCoop` | Tag co-op ranking screen |
| 18 | `rankingTeam` | Team ranking screen |
| 19 | `beatmapImport` | Beatmap import screen |
| 20 | `packageUpdater` | Package updater |
| 21 | `benchmark` | Benchmark screen |
| 22 | `tourney` | Tournament client screen |
| 23 | `charts` | Charts screen |

Replay and spectating are not separate values in the current public
`GameState` enum. tosu exposes replay and multi-spectating information through
additional global fields. The overlay still accepts `spectating`,
`watchingReplay`, and `replay` as compatibility aliases if an integration emits
one of those names.

## Runtime observations

The following transitions were confirmed with osu!lazer in borderless mode and
the local tosu v2 API:

| osu! screen | Normalized name | Number | Overlay policy |
|---|---|---:|---|
| Main menu | `menu` | 0 | Show the last analyzed map |
| Solo song selection | `selectplay` | 5 | Show the selected map |
| Map gameplay | `play` | 2 | Hide the native overlay window |
| Map gameplay paused | `play` | 2 | Show the overlay while paused |
| Return from gameplay to song selection | `selectplay` | 5 | Show the overlay again |

Only the `play` state is treated as active gameplay by the numeric fallback.
All other official numeric states are non-gameplay states. A usable state name
takes precedence over its numeric value because older stable-compatible
integrations can expose different numeric behavior.

The `outside-play` policy used by Companella evaluates as:

```text
show = !isPlaying || isPaused == true
```

If `game.paused` is absent, the safe fallback remains the previous behavior:
active gameplay hides the native overlay until tosu provides a definitive pause
value.

The final decision is then combined with the selected preset's
`visibilityPolicy` from `manifest.json`. A preset can use `always`,
`outside-play`, `during-play`, `paused-only`, or `never`. For example, the
shipped Companella preset uses `outside-play`, while the Default preset uses
`always`.

The application normalizes names to lowercase and removes non-letter
characters. It recognizes these compatibility groups:

- Gameplay: `play`, `gameplay`, `playing`, `spectating`, `watchingreplay`,
  `replay`.
- Non-gameplay: `menu`, `edit`, `selectplay`, `selectedit`,
  `selectdrawings`, `resultscreen`, `result`, `options`, `songselect`.
- Unknown name: use `state.number == 2` as the fallback.
- Missing or unreadable state: do not turn an unknown transition into a menu
  state. Preserve the last browser state while reconnecting and wait for the
  native poll to recover.

## Visibility ownership

Game state, window focus, analyzer-page visibility, and native window
visibility are separate concerns:

- `state` decides whether gameplay is active.
- `game.paused` allows the widget to remain visible while gameplay is paused;
  pause does not change the `state` enum.
- `game.focused` controls whether the overlay may accept mouse input; it does
  not decide whether the widget is displayed.
- An analyzer's own DOM classes must not control application-level visibility.
  The ManiaMapAnalyser adapter removes its source-specific menu-hiding class,
  while presets continue to render only the normalized domain snapshot.
- On Windows, gameplay visibility is applied with native `ShowWindow` calls.
  Setting Avalonia opacity to zero alone did not hide this transparent,
  click-through top-level window reliably.
- Closing osu! is detected separately from `state.exit`. The application leaves
  overlay mode and restores its main window when the osu! process disappears.

## Data sources and diagnostics

The launcher polls `http://127.0.0.1:24050/json/v2` every 350 ms while overlay
mode is active. The analyzer adapter also listens to `/websocket/v2` and uses a
400 ms browser-side `/json/v2` fallback. Once the native HTTP source has
responded successfully, it is authoritative for native window visibility; the
browser sources remain useful for normalized snapshots and diagnostics.

State changes are written to `application.log` as `Gameplay state trace`
records. Each record includes the source, normalized name, number, derived
`isPlaying` value, focus value, and current native-visibility decision. On
Windows the log is stored in `%LOCALAPPDATA%\ManiaMapAnalyzerOverlay`.

Typical verified records are:

```text
source=native-http; name=menu; number=0; isPlaying=False
source=native-http; name=selectplay; number=5; isPlaying=False
source=native-http; name=play; number=2; isPlaying=True
```
