# Mania Map Analyzer Overlay

![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows)
![Avalonia](https://img.shields.io/badge/UI-Avalonia-8b5cf6)
![Version](https://img.shields.io/badge/version-2.1.0-ff4f9b)
![License](https://img.shields.io/badge/license-MIT-4cbe89)

**Mania Map Analyzer Overlay** is a lightweight Avalonia desktop launcher for osu!mania. It starts and supervises [tosu](https://github.com/tosuapp/tosu), displays live beatmap analysis, and provides a customizable overlay without requiring the user to start a console script.

## Features

- Automatic tosu and [ManiaMapAnalyser](https://github.com/LeoBlackMT/osumania_map_analyser) setup, compatibility checks, hash verification, and lifecycle management.
- Works with osu!stable and osu!lazer; lazer compatibility offsets are checked during setup.
- Live SR, BPM, Set/Map IDs, DAN/Reform estimate, numeric difficulty, LN%, key count, pattern bars, Etterna skills, and difficulty graphs.
- Lightweight transparent overlay for windowed or borderless osu!, with automatic hiding while a map is being played.
- Default, Horizontal, Companella, and Custom CSS layouts with live preview in the launcher.
- Resizable overlay with DPI-aware rendering; resize by dragging an edge/corner or with `Ctrl` + mouse wheel.
- Optional tosu In-Game Overlay integration for osu!stable exclusive fullscreen on Windows.
- Startup update checks for the launcher and bundled analysis components; settings and custom CSS are preserved.
- English and Russian UI; switch languages with the `EN`/`RU` button.

## Installation and first launch

Download the application package for your platform from [GitHub Releases](https://github.com/rol1t/mania-map-analyzer-overlay/releases), then extract it to a folder.

Windows:

1. Open the extracted folder.
2. Run **`Mania Map Analyzer Overlay.exe`**.

Linux (experimental):

1. Extract the `.tar.gz` package.
2. Run **`./Mania Map Analyzer Overlay`** from the extracted folder.

The application is the only user-facing entry point. Do not start `tosu` separately. On first launch, the GUI downloads the compatible tosu and ManiaMapAnalyser components, verifies their SHA-256 hashes, and starts tosu automatically. A network connection is required for this preparation step.

The launcher owns the tosu process and stops it when the launcher exits. If preparation fails, use **Restart** to retry it from the GUI.

On later launches, the launcher checks for newer application and component releases and offers the update from the GUI.

## Overlay controls

Use **Overlay** for windowed or borderless osu!. Use **Stable FS** only when osu!stable is running in exclusive fullscreen.

| Action | Control |
|---|---|
| Toggle click-through/input | `Ctrl+Shift+F9` |
| Leave overlay mode | `Ctrl+Shift+F10` |
| Move the widget | Drag the center while osu! is inactive and input is enabled |
| Resize the widget | Drag an edge/corner while osu! is inactive, or hold `Ctrl` and use the mouse wheel |

When osu! is the active window, overlay interaction is disabled so an accidental click cannot focus the widget or minimize the game. Disable the overlay when it is not needed to keep resource use low.

For osu!stable exclusive fullscreen, enable **Stable FS**, confirm the tosu restart, and use tosu's in-game editor (`Ctrl+Shift+Space`) to position the official in-game overlay. The launcher applies the selected layout and scale to that overlay.

## Appearance and CSS

Open **Appearance** to choose `Default`, `Horizontal`, `Companella`, or `Custom CSS`, then adjust the scale. The launcher previews the selected style immediately and applies it to the desktop overlay.

For custom styling, open `overlay-custom.css` from the Appearance window. The editable copy is kept in the per-user application data directory and survives updates. Copy-ready examples are available in:

- [`docs/css/compact-companella-inspired.css`](docs/css/compact-companella-inspired.css) — compact wide card with cover-art tint and analysis bars.
- [`docs/css/minimal-glass.css`](docs/css/minimal-glass.css) — minimal glass-style colors, borders, and typography.

CSS can restyle and rearrange existing analyser elements; it cannot add a new live data source. The built-in Companella preset supplies its additional summary cards directly. Useful selectors include `.card.main-card`, `.star-block`, `.cluster-item`, `.ett-skill-item`, `.body-graph-wrap`, and `.mode-tag`.

## Screenshots

![Launcher with Companella preview](docs/images/launcher.png)

![Overlay running over osu!](docs/images/overlay-in-osu.png)

![Appearance settings](docs/images/appearance.png)

## Platform notes

- Windows 10/11 is the primary supported platform. The desktop overlay and osu!stable exclusive-fullscreen integration are Windows-specific. Windows may require the Microsoft Edge WebView2 Runtime.
- Linux packages are experimental. The Avalonia desktop shell and tosu lifecycle are available, but desktop environments, WebKit dependencies, fullscreen behavior, and overlay capabilities can vary.
- macOS is not currently shipped because tosu does not provide an official macOS binary for this project.

## Development

Requirements: the .NET 8 SDK version pinned in [`global.json`](global.json). Released packages are self-contained and do not require a separate .NET runtime.

Open [`ManiaMapAnalyzerOverlay.sln`](ManiaMapAnalyzerOverlay.sln) in Visual Studio or JetBrains Rider. Select **`ManiaMapAnalyzerOverlay.Avalonia`** as the startup project; the updater project is a helper and should not be started directly.

From a terminal:

```powershell
dotnet restore .\ManiaMapAnalyzerOverlay.sln
dotnet build .\ManiaMapAnalyzerOverlay.sln --configuration Debug
dotnet run --project .\src\Avalonia\ManiaMapAnalyzerOverlay.Avalonia.csproj --configuration Debug
```

Build a Windows release package with PowerShell:

```powershell
.\scripts\build.ps1
.\scripts\package-installer.ps1 -Version 2.1.0 -RuntimeIdentifier win-x64
```

Build and package Linux with Bash:

```bash
./scripts/build.sh --runtime linux-x64 --output artifacts/payload
./scripts/package.sh --version 2.1.0 --runtime linux-x64
```

Runtime setup and updates are implemented in C#. The PowerShell and Bash files under `scripts/` are developer and CI packaging tools, not user launchers.

## Credits and licensing

This project integrates [tosu](https://github.com/tosuapp/tosu), [ManiaMapAnalyser](https://github.com/LeoBlackMT/osumania_map_analyser), [Avalonia](https://avaloniaui.net/), and [Avalonia.Controls.WebView](https://github.com/AvaloniaUI/Avalonia.Controls.WebView). Their license texts are included in [`LICENSES/`](LICENSES/). The Companella layout is an original adaptation based on a visual reference supplied by the project owner; it does not include Companella source code or assets.

Launcher source, repository-authored scripts, and documentation are available under the [MIT License](LICENSE).

## AI disclosure

The launcher integration, UI iterations, localization, packaging, and documentation were developed with assistance from **OpenAI Codex**. Product decisions and acceptance testing were performed by the project owner.
