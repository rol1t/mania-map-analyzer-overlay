using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ManiaMapAnalyzerOverlay.Avalonia.Services;

public sealed record DocumentationEntry(string Id, string Title, string TitleRu, string FileName);

public sealed class DocumentationService
{
    private static readonly IReadOnlyList<DocumentationEntry> _entries =
    [
        new("overview", "Overview", "Обзор", "README.md"),
        new("mapping", "Analysis mapping", "Привязка анализа", "MAPPING.md"),
        new("presets", "Presets", "Пресеты", "PRESETS.md"),
        new("analyzer-adapters", "Analyzer adapters", "Адаптеры анализа", "ANALYZER_ADAPTERS.md"),
        new("tosu-game-states", "Tosu game states", "Состояния игры tosu", "TOSU_GAME_STATES.md"),
        new("overlay", "Overlay controls", "Управление оверлеем", "OVERLAY.md"),
    ];

    public IReadOnlyList<DocumentationEntry> Entries => _entries;

    public DocumentationEntry? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _entries.FirstOrDefault(entry =>
            string.Equals(entry.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public string GetTitle(string id)
    {
        var entry = Find(id);
        if (entry is null)
        {
            return id;
        }

        return ManiaMapAnalyzerOverlay.UiText.IsEnglish ? entry.Title : entry.TitleRu;
    }

    public string LoadContent(string id)
    {
        var entry = Find(id);
        if (entry is null)
        {
            return $"# Not found\n\nDocument '{id}' was not found.";
        }

        // Try file system first (docs/ and root).
        var candidates = new[]
        {
            Path.Combine(AppPaths.BaseDirectory, "docs", entry.FileName),
            Path.Combine(AppPaths.BaseDirectory, entry.FileName),
            Path.Combine(Directory.GetCurrentDirectory(), "docs", entry.FileName),
            Path.Combine(Directory.GetCurrentDirectory(), entry.FileName),
        };

        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }
            }
            catch
            {
                // Fall through to embedded.
            }
        }

        return GetEmbeddedContent(entry.Id);
    }

    public static bool IsDocumentationLink(string url)
    {
        return url.StartsWith("doc://", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ExtractDocId(string url)
    {
        if (!IsDocumentationLink(url))
        {
            return null;
        }

        var id = url["doc://".Length..].Trim().Trim('/');
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private string GetEmbeddedContent(string id)
    {
        return id.ToLowerInvariant() switch
        {
            "overview" => GetOverviewContent(),
            "mapping" => GetMappingContent(),
            "presets" => GetPresetsContent(),
            "analyzer-adapters" => LoadFallbackFile("ANALYZER_ADAPTERS.md"),
            "tosu-game-states" => LoadFallbackFile("TOSU_GAME_STATES.md"),
            "overlay" => GetOverlayContent(),
            _ => $"# {id}\n\nNo embedded content for '{id}'."
        };
    }

    private string LoadFallbackFile(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppPaths.BaseDirectory, "docs", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "docs", fileName),
        };

        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }
            }
            catch
            {
            }
        }

        return $"# {fileName}\n\nDocumentation file '{fileName}' was not found in the application package.";
    }

    private static string GetOverviewContent()
    {
        var isRu = ManiaMapAnalyzerOverlay.UiText.IsEnglish == false;
        if (isRu)
        {
            return """
                # Mania Map Analyzer Overlay — справка

                Лёгкий Avalonia-лаунчер для osu!mania. Запускает и контролирует [tosu](https://github.com/tosuapp/tosu), показывает live-анализ карты и кастомный оверлей без консоли.

                ## Быстрый старт
                - Windows: запустите `Mania Map Analyzer Overlay.exe` из папки релиза.
                - Linux (экспериментально): `./Mania Map Analyzer Overlay`
                - Лаунчер сам скачает tosu и ManiaMapAnalyser, проверит хеши и запустит tosu.

                ## Где читать дальше
                - [Привязка анализа](doc://mapping) — как связать метрики с виджетом
                - [Пресеты](doc://presets) — создание своих шаблонов
                - [Адаптеры анализа](doc://analyzer-adapters) — как работает выделение данных
                - [Состояния игры](doc://tosu-game-states) — когда оверлей виден
                - [Оверлей](doc://overlay) — горячие клавиши, перетаскивание, ресайз

                ## Файлы данных
                - `%LOCALAPPDATA%\ManiaMapAnalyzerOverlay\launcher-settings.json` — настройки лаунчера
                - `%LOCALAPPDATA%\ManiaMapAnalyzerOverlay\analysis-configuration.json` — эффективный анализ (отдельно от пресетов)
                - `%LOCALAPPDATA%\ManiaMapAnalyzerOverlay\application.log` — лог

                См. также [README](https://github.com/rol1t/mania-map-analyzer-overlay) в репозитории.
                """;
        }

        return """
            # Mania Map Analyzer Overlay — help

            Lightweight Avalonia launcher for osu!mania. Starts and supervises [tosu](https://github.com/tosuapp/tosu), shows live beatmap analysis and a customizable overlay.

            ## Quick start
            - Windows: run `Mania Map Analyzer Overlay.exe` from the release folder.
            - Linux (experimental): `./Mania Map Analyzer Overlay`
            - The launcher downloads tosu and ManiaMapAnalyser, verifies hashes and starts tosu.

            ## Where to read next
            - [Analysis mapping](doc://mapping) — how to bind metrics to a widget
            - [Presets](doc://presets) — creating custom layouts
            - [Analyzer adapters](doc://analyzer-adapters) — how data is extracted
            - [Tosu game states](doc://tosu-game-states) — when the overlay is visible
            - [Overlay](doc://overlay) — hotkeys, dragging, resizing

            ## Data files
            - `%LOCALAPPDATA%\ManiaMapAnalyzerOverlay\launcher-settings.json` — launcher settings
            - `%LOCALAPPDATA%\ManiaMapAnalyzerOverlay\analysis-configuration.json` — effective analysis (separate from presets)
            - `%LOCALAPPDATA%\ManiaMapAnalyzerOverlay\application.log` — log

            See also [README](https://github.com/rol1t/mania-map-analyzer-overlay) in the repository.
            """;
    }

    private static string GetMappingContent()
    {
        var isRu = ManiaMapAnalyzerOverlay.UiText.IsEnglish == false;
        if (isRu)
        {
            return """
                # Привязка анализа

                Файл: `%LOCALAPPDATA%\ManiaMapAnalyzerOverlay\analysis-configuration.json` — отдельно от визуальных пресетов.

                ## Верхний уровень
                - `schemaVersion` — сейчас 1
                - `defaultEngineId` — `mania-map-analyser-headless`
                - `defaultAlgorithm` — `Sunny | Daniel | Azusa | Roxy | Mixed | Companella`
                - `configurationVersion` — версия конфига, влияет на кэш
                - `defaultOptions` — опции анализатора, напр. `{"withEtterna": true}`
                - `widgets[]` — массив виджетов

                ## Виджет
                - `widgetId` — уникальный id, напр. `headless-overlay`
                - `sources[]` — источники
                - `bindings[]` — привязки

                ## Источник
                - `sourceId` — id внутри виджета
                - `engineId` — должен совпадать с активным движком
                - `requestedAlgorithm` — алгоритм для этого источника
                - `configurationVersion` — версия источника
                - `options` — перекрывает `defaultOptions`
                - `rate`/`mods` и карта подставляются из tosu автоматически

                ## Привязка
                - `targetMetricId` — поле виджета, напр. `difficulty.star`
                - `candidates[]` — приоритет `{ sourceId, metricId }`
                - `allowsNull` — `true` разрешает `null` как валидное значение

                ## Примеры
                Один источник → один виджет:

                ```json
                {
                  "defaultEngineId": "mania-map-analyser-headless",
                  "defaultAlgorithm": "Mixed",
                  "configurationVersion": "1",
                  "widgets": [{
                    "widgetId": "headless-overlay",
                    "sources": [{ "sourceId": "primary", "engineId": "mania-map-analyser-headless", "requestedAlgorithm": "Mixed", "configurationVersion": "1" }],
                    "bindings": [{ "targetMetricId": "difficulty.star", "candidates": [{ "sourceId": "primary", "metricId": "difficulty.star" }] }]
                  }]
                }
                ```

                Два источника с fallback:

                ```json
                {
                  "widgets": [{
                    "widgetId": "headless-overlay",
                    "sources": [
                      { "sourceId": "primary", "engineId": "mania-map-analyser-headless", "requestedAlgorithm": "Mixed", "configurationVersion": "1" },
                      { "sourceId": "fallback", "engineId": "mania-map-analyser-headless", "requestedAlgorithm": "Sunny", "configurationVersion": "1" }
                    ],
                    "bindings": [{
                      "targetMetricId": "difficulty.star",
                      "candidates": [
                        { "sourceId": "primary", "metricId": "difficulty.star" },
                        { "sourceId": "fallback", "metricId": "difficulty.star" }
                      ]
                    }]
                  }]
                }
                ```

                ## Валидация
                - `widgetId`, `sourceId`, `targetMetricId` обязательны, дубликаты запрещены
                - `candidates` должны ссылаться на существующие `sourceId` того же виджета

                ## Доступные метрики
                Смотрите список в окне привязки — он строится из `manifest.json` движка (`semanticMetricIds`).

                ---
                Далее: [Пресеты](doc://presets) · [Адаптеры](doc://analyzer-adapters)
                """;
        }

        return """
            # Analysis mapping

            File: `%LOCALAPPDATA%\ManiaMapAnalyzerOverlay\analysis-configuration.json` — separate from visual presets.

            ## Top level
            - `schemaVersion` — currently 1
            - `defaultEngineId` — `mania-map-analyser-headless`
            - `defaultAlgorithm` — `Sunny | Daniel | Azusa | Roxy | Mixed | Companella`
            - `configurationVersion` — config version, affects cache
            - `defaultOptions` — analyzer options, e.g. `{"withEtterna": true}`
            - `widgets[]` — array of widgets

            ## Widget
            - `widgetId` — unique id, e.g. `headless-overlay`
            - `sources[]` — sources
            - `bindings[]` — bindings

            ## Source
            - `sourceId` — id inside widget
            - `engineId` — must match active engine
            - `requestedAlgorithm` — algorithm for this source
            - `configurationVersion` — version for this source
            - `options` — overrides `defaultOptions`
            - `rate`/`mods` and beatmap are injected from tosu

            ## Binding
            - `targetMetricId` — widget field, e.g. `difficulty.star`
            - `candidates[]` — prioritized `{ sourceId, metricId }`
            - `allowsNull` — `true` allows `null` as valid

            ## Examples
            One source → one widget:

            ```json
            {
              "defaultEngineId": "mania-map-analyser-headless",
              "defaultAlgorithm": "Mixed",
              "configurationVersion": "1",
              "widgets": [{
                "widgetId": "headless-overlay",
                "sources": [{ "sourceId": "primary", "engineId": "mania-map-analyser-headless", "requestedAlgorithm": "Mixed", "configurationVersion": "1" }],
                "bindings": [{ "targetMetricId": "difficulty.star", "candidates": [{ "sourceId": "primary", "metricId": "difficulty.star" }] }]
              }]
            }
            ```

            Two sources with fallback:

            ```json
            {
              "widgets": [{
                "widgetId": "headless-overlay",
                "sources": [
                  { "sourceId": "primary", "engineId": "mania-map-analyser-headless", "requestedAlgorithm": "Mixed", "configurationVersion": "1" },
                  { "sourceId": "fallback", "engineId": "mania-map-analyser-headless", "requestedAlgorithm": "Sunny", "configurationVersion": "1" }
                ],
                "bindings": [{
                  "targetMetricId": "difficulty.star",
                  "candidates": [
                    { "sourceId": "primary", "metricId": "difficulty.star" },
                    { "sourceId": "fallback", "metricId": "difficulty.star" }
                  ]
                }]
              }]
            }
            ```

            ## Validation
            - `widgetId`, `sourceId`, `targetMetricId` are required, duplicates rejected
            - `candidates` must reference existing `sourceId` of the same widget

            ## Available metrics
            See the list in the mapping window — built from engine `manifest.json` (`semanticMetricIds`).

            ---
            Next: [Presets](doc://presets) · [Adapters](doc://analyzer-adapters)
            """;
    }

    private static string GetPresetsContent()
    {
        var isRu = ManiaMapAnalyzerOverlay.UiText.IsEnglish == false;
        if (isRu)
        {
            return """
                # Пресеты

                Пресеты — папки с `manifest.json`, `template.html`, `style.css` в `%LOCALAPPDATA%\\ManiaMapAnalyzerOverlay\\presets\\<id>\\`.

                Пример `manifest.json`:

                ```json
                {
                  "id": "my-preset",
                  "name": "My Preset",
                  "description": "Compact",
                  "template": "template.html",
                  "stylesheet": "style.css",
                  "visibilityPolicy": "outside-play"
                }
                ```

                `visibilityPolicy`: `always | outside-play | during-play | paused-only | never`

                Шаблон: только элементы с `data-overlay-preset-node` вставляются в `.main-card`.

                Стили: скоуп `html.launcher-overlay-host`, переменные `--overlay-host-scale`, `--overlay-preset-width`.

                ---
                См. [Привязку](doc://mapping) и [Оверлей](doc://overlay).
                """;
        }

        return """
            # Presets

            Presets are folders with `manifest.json`, `template.html`, `style.css` in `%LOCALAPPDATA%\\ManiaMapAnalyzerOverlay\\presets\\<id>\\`.

            Example `manifest.json`:

            ```json
            {
              "id": "my-preset",
              "name": "My Preset",
              "description": "Compact",
              "template": "template.html",
              "stylesheet": "style.css",
              "visibilityPolicy": "outside-play"
            }
            ```

            `visibilityPolicy`: `always | outside-play | during-play | paused-only | never`

            Template: only elements with `data-overlay-preset-node` are inserted into `.main-card`.

            Styles: scope `html.launcher-overlay-host`, variables `--overlay-host-scale`, `--overlay-preset-width`.

            ---
            See [Mapping](doc://mapping) and [Overlay](doc://overlay).
            """;
    }

    private static string GetOverlayContent()
    {
        var isRu = ManiaMapAnalyzerOverlay.UiText.IsEnglish == false;
        if (isRu)
        {
            return """
                # Оверлей

                ## Горячие клавиши
                - `Ctrl+Shift+F9` — клик-сквозь
                - `Ctrl+Shift+F10` — выйти из оверлея
                - Перетаскивание центра — перемещение (когда osu! не в фокусе)
                - Перетаскивание края/угла или `Ctrl+колесо` — ресайз

                ## Видимость
                Зависит от `visibilityPolicy` пресета и `Gameplay.IsPlaying/IsPaused`.

                ## DPI
                Рендеринг учитывает `RenderScaling`, ресайз пересчитывает `OverlayScalePercent`.

                ---
                См. [Состояния игры](doc://tosu-game-states).
                """;
        }

        return """
            # Overlay

            ## Hotkeys
            - `Ctrl+Shift+F9` — click-through
            - `Ctrl+Shift+F10` — leave overlay
            - Drag center — move (when osu! not focused)
            - Drag edge/corner or `Ctrl+wheel` — resize

            ## Visibility
            Depends on preset `visibilityPolicy` and `Gameplay.IsPlaying/IsPaused`.

            ## DPI
            Rendering is DPI-aware, resize recalculates `OverlayScalePercent`.

            ---
            See [Game states](doc://tosu-game-states).
            """;
    }
}
