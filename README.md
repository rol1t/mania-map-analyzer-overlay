# Mania Map Analyzer Overlay

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows)
![Version](https://img.shields.io/badge/version-1.2.0-ff4f9b)
![License](https://img.shields.io/badge/launcher-MIT-4cbe89)
![AI assisted](https://img.shields.io/badge/development-AI%20assisted-7357ff)

A lightweight Windows launcher that starts **tosu**, displays live osu!mania map analysis, provides a clean desktop overlay, and guarantees that tosu is closed with the application.

## Highlights

- Works with osu!stable and osu!lazer.
- Starts, monitors, updates, and stops tosu automatically.
- Transparent always-on-top overlay with no frame or black background.
- Automatically hides the overlay while a map is being played.
- Default, horizontal, Companella-inspired, and custom-CSS layouts.
- Live preset preview in the normal application window.
- Native 50–180% sizing and Per-Monitor DPI V2 rendering without bitmap blur.
- Beatmap cover art, SR, BPM, Set ID, Map ID, difficulty/Dan label, LN ratio, key count, and pattern details.
- English and Russian interface; switch with the **EN/RU** button.
- One WebView2 instance and one local WebSocket connection for low resource use.

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

Exclusive fullscreen prevents ordinary Windows overlays from being visible. Use tosu's official In-Game Overlay for that mode; the lightweight desktop overlay is intended for windowed and borderless osu!.

## Appearance and custom CSS

Open **Appearance** to select a preset and size. The normal application window immediately previews the same layout used by overlay mode.

For complete control, select **Custom CSS** and edit `overlay-custom.css` next to the executable. The updater preserves this file.

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
docs/                      release and attribution notes
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
