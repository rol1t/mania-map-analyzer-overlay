# Mania Map Analyzer Overlay

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows)
![Version](https://img.shields.io/badge/version-1.3.0-ff4f9b)
![License](https://img.shields.io/badge/launcher-MIT-4cbe89)
![AI assisted](https://img.shields.io/badge/development-AI%20assisted-7357ff)

A lightweight Windows launcher that starts **tosu**, displays live osu!mania map analysis, provides a clean desktop overlay, and guarantees that tosu is closed with the application.

## Highlights

- Works with osu!stable and osu!lazer.
- Starts, monitors, updates, and stops tosu automatically.
- Transparent always-on-top overlay with no frame or black background.
- Optional official tosu In-Game Overlay for osu!stable exclusive fullscreen.
- Automatically hides the lightweight desktop overlay while a map is being played.
- Default, horizontal, Companella-inspired, and custom-CSS layouts.
- Live preset preview in the normal application window.
- Native 50–180% sizing and Per-Monitor DPI V2 rendering without bitmap blur.
- Beatmap cover art, SR, BPM, Set ID, Map ID, difficulty/Dan label, LN ratio, key count, and pattern details.
- English and Russian interface; switch with the **EN/RU** button.
- The default desktop mode uses one WebView2 instance and one local WebSocket connection for low resource use.

## Displayed data reference

The exact values depend on the selected beatmap, key count, ManiaMapAnalyser settings, and active layout. A field that is not available for the current map is shown as `—` or omitted.

### Map and difficulty data

| Field | Meaning |
|---|---|
| Map header | Artist, title, and difficulty name of the selected beatmap. |
| Mapper | Beatmap creator. Shown explicitly by the Companella preset. |
| Star rating | The selected rating source: Rework SR, Interlude SR, MSD, or Pattern rating. |
| DAN / Reform | Estimated rank label, for example `Reform 4 mid/low`. |
| Numeric difficulty | Floating-point estimate paired with the DAN label, for example `≈ 3.82`. |
| BPM | Current or map BPM range. |
| Set / Map | Beatmap set ID and beatmap ID. |
| LN% | Percentage of long-note objects in the map. |
| Keys | Mania key count, such as 4K, 6K, or 7K. |
| Mode tag | Main map category: `RC`, `LN`, `HB`, or `Mix`. An additional `SV` tag may appear. |
| Difficulty graph | Difficulty over map time, including the live position cursor when available. |
| Pause data | Detected pause count and pause markers on the graph. |

Mode tags:

- `RC` — rice/tap-focused map with little or no long-note emphasis.
- `LN` — long-note-focused map.
- `HB` — hybrid map combining substantial rice and long-note gameplay.
- `Mix` — mixed pattern profile without one dominant category.
- `SV` — significant scroll-velocity changes were detected.

### Analysis modules

| Module | Values it can show |
|---|---|
| Pattern | Core groups: Stream, Chordstream, Jacks, Coordination, Density, and Wildcard. The analyser may add subtypes such as Trills, Rolls, Jumpstream, Handstream, Chordjacks, Minijacks, Longjacks, Release, Shield, Inverse, density variants, and wildcard variants. |
| Etterna | Overall MSD plus Stream, Jumpstream, Handstream, Stamina, JackSpeed, Chordjack, and Technical skill values. |
| Graph | Time-based difficulty curve, current play position, and detected pause markers. |
| Full | Pattern, Etterna, and Graph modules together. |

### Configurable analyser slots

These values are selected in ManiaMapAnalyser settings, which can be opened from the analyser page inside tosu.

| Setting | Available values |
|---|---|
| Card Body Content | `None`, `Auto`, `Pattern`, `Etterna`, `Graph`, `Full` |
| Top-left Capsule Text | `Auto`, `ReworkSR`, `InterludeSR`, `MSD`, `Pattern` |
| Top-right Content | `None`, `Graph`, `Difficulty`, `MSD`, `Pattern`, `ReworkSR`, `InterludeSR` |
| Estimator Algorithm | `Mixed`, `Azusa`, `Roxy`, `Sunny`, `Daniel`, `Companella` |
| Map Tag Capsule | `RC`, `LN`, `HB`, `Mix`, with optional `SV` detection |

## Installation

Download the **Installer** archive from GitHub Releases, extract it, and run `Install-or-Update.cmd`. The installer creates or updates the application folder and downloads compatible official component versions.

Do not start `tosu.exe` separately. The launcher owns its lifecycle and terminates it on exit.

## Overlay controls

| Action | Shortcut |
|---|---|
| Toggle click-through | `Ctrl+Shift+F9` |
| Leave overlay mode | `Ctrl+Shift+F10` |
| Resize overlay | `Ctrl` + mouse wheel |
| Move overlay | Drag anywhere while click-through is disabled |

The **Overlay** button opens the lightweight desktop widget for windowed and borderless osu!. The **Stable FS** button is the second option for osu!stable exclusive fullscreen:

1. Click **Stable FS: Off** and confirm the tosu restart.
2. `ManiaMapAnalyser` is added automatically. In the editor that opens, adjust its position and size if needed.
3. Use `Ctrl+Shift+Space` in osu!stable to open the in-game overlay editor when needed.
4. Click **Stable FS: On** in the launcher to disable this mode again.

The fullscreen layer uses tosu's official In-Game Overlay. Its position remains independently adjustable in the in-game editor, while the launcher automatically synchronizes the selected Default, Horizontal, Companella, or Custom CSS appearance and scale. Leave it disabled when it is not needed for the lowest resource use.

## Appearance and custom CSS

Open **Appearance** to select a preset and size. The normal application window immediately previews the same layout used by overlay mode.

For complete control, select **Custom CSS** and edit `overlay-custom.css` next to the executable. The updater preserves this file.

### Using a CSS example

1. Open **Appearance** and select **Custom CSS**.
2. Open `overlay-custom.css` from the appearance window.
3. Replace its contents with an example or copy only the rules you need.
4. Save the file and click **Apply**. The application window previews the result immediately.

The size slider is still applied on top of custom CSS. Use selectors beginning with `html.mma-layout-custom` so the rules do not affect other presets. `!important` is normally required because the stylesheet overrides ManiaMapAnalyser's built-in theme.

CSS can rearrange and restyle existing analyser elements, but it cannot create a new live data source. Select the built-in **Companella** preset when you want the exact launcher-generated summary cards for SR, BPM, Set, Map, and DAN.

### Useful CSS selectors

| Selector | Element |
|---|---|
| `.card.main-card` | Entire analysis card, background, border, padding, and layout. |
| `.status-row`, `.status` | Map title and status header. |
| `.star-block` | Rating, LN/key metadata, and estimated difficulty area. |
| `.star-value` | Main SR/MSD/Pattern value. |
| `.star-meta` | LN percentage and key count. |
| `.star-subtitle` | DAN/Reform or other selected top-right value. |
| `.star-caption` | Numeric difficulty caption and estimator text. |
| `.cluster-bars`, `.cluster-item` | Pattern analysis list and each row. |
| `.cluster-label` | Pattern group name. |
| `.cluster-track`, `.cluster-fill` | Pattern bar track and filled value. |
| `.cluster-subtype` | Detailed pattern subtype text. |
| `.ett-skill-bars`, `.ett-skill-item` | Etterna skill list and each row. |
| `.ett-skill-label`, `.ett-skill-head` | Etterna skill name and numeric value. |
| `.ett-skill-track`, `.ett-skill-fill` | Etterna bar track and filled value. |
| `.body-graph-wrap` | Difficulty graph container. |
| `.mode-tag-group`, `.mode-tag` | RC/LN/HB/Mix/SV capsules. |
| `.pause-count` | Pause detection status or count. |

### Small color example

```css
html.mma-layout-custom {
    --accent: #66e3c4;
    --panel: rgba(9, 13, 24, 0.90);
}

html.mma-layout-custom .card.main-card {
    background: var(--panel) !important;
    border: 1px solid rgba(102, 227, 196, 0.45) !important;
    border-radius: 14px !important;
}

html.mma-layout-custom .star-value,
html.mma-layout-custom .cluster-fill,
html.mma-layout-custom .ett-skill-fill {
    background: var(--accent) !important;
}
```

Complete copy-ready examples:

- [Compact Companella-inspired layout](docs/css/compact-companella-inspired.css) — wide compact card, cover-art tint, left rating column, and wide analysis bars.
- [Minimal glass layout](docs/css/minimal-glass.css) — keeps the default structure while changing colors, typography, borders, and bar styling.

The Companella example is an approximation for **Custom CSS** using ManiaMapAnalyser's standard DOM. The built-in **Companella** preset can display additional launcher-generated summary elements that CSS alone cannot add.

## Updates and osu!lazer compatibility

At startup the launcher checks its own latest GitHub Release and offers a one-click update when a newer version is available. The external updater safely replaces the application after shutdown, preserves settings and `overlay-custom.css`, and automatically restarts it. The launcher also checks official tosu and ManiaMapAnalyser releases and verifies a matching offsets file for osu!lazer. Analysis data stays on `127.0.0.1:24050`.

## Building from source

Requirements: Windows 10/11, Windows PowerShell 5.1, .NET Framework 4.8, and Microsoft Edge WebView2 Runtime.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

The build script downloads the pinned WebView2 SDK package from NuGet, compiles the WinForms launcher, and prepares the installer payload in `artifacts\payload`.

## Project structure

```text
src/Program.cs             application entry point and single-instance guard
src/Models/                persisted launcher settings
src/Properties/            assembly metadata and version
src/Services/              custom CSS and overlay style generation
src/Views/MainForm.cs      window construction and primary commands
src/Views/MainForm.*.cs    browser, updates, overlay, and tosu responsibilities
src/Views/OverlayStyleDialog.cs
assets/                    editable CSS template
scripts/                   build and component updater scripts
LICENSES/                  bundled third-party license texts
docs/                      release notes and copy-ready CSS examples
.github/workflows/         Windows CI build
```

## Credits and source disclosure

This project integrates, but does not claim authorship of:

- [tosu](https://github.com/tosuapp/tosu) — LGPL-3.0.
- [ManiaMapAnalyser by Leo_Black](https://github.com/LeoBlackMT/osumania_map_analyser) — MIT.
- [Microsoft Edge WebView2](https://github.com/MicrosoftEdge/WebView2Feedback) and the [`Microsoft.Web.WebView2`](https://www.nuget.org/packages/Microsoft.Web.WebView2) SDK package.
- osu!lazer compatibility offsets from [tosu.app](https://tosu.app/) with [osuck.net](https://osuck.net/) as the updater fallback.

The Companella preset is an original CSS/DOM adaptation inspired by a visual reference supplied by the project owner. No Companella source code or assets are bundled.

Exact license texts are included in the `LICENSES` directory.

## AI disclosure

The launcher integration, UI iterations, localization, documentation, and packaging were developed with assistance from **OpenAI Codex**. Product decisions and acceptance testing were performed by the project owner.

## License

The launcher-specific source in `src/` and repository-authored scripts/documentation are available under the [MIT License](LICENSE). Bundled third-party components remain under their respective licenses in `LICENSES/`.
