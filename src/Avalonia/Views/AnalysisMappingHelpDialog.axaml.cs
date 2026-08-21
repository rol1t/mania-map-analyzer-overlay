using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;
using ManiaMapAnalyzerOverlay.Avalonia.Services;

namespace ManiaMapAnalyzerOverlay.Avalonia.Views;

public partial class AnalysisMappingHelpDialog : Window
{
    private readonly AnalyzerEngineCatalog _catalog = new();

    public AnalysisMappingHelpDialog()
    {
        InitializeComponent();
        ApplyLanguage();
        BuildHelpText();
    }

    private void ApplyLanguage()
    {
        Title = L("mapping.help_title");
        HeadingText.Text = Title;
        CloseButton.Content = L("button.ok");
    }

    private string L(string key) => ManiaMapAnalyzerOverlay.UiText.Get(key);

    private void BuildHelpText()
    {
        var isRu = ManiaMapAnalyzerOverlay.UiText.IsEnglish == false;
        var builder = new StringBuilder();

        if (isRu)
        {
            builder.AppendLine("Привязка анализа — как настраивать");
            builder.AppendLine();
            builder.AppendLine("Файл: %LOCALAPPDATA%\\ManiaMapAnalyzerOverlay\\analysis-configuration.json");
            builder.AppendLine("Отдельно от визуальных пресетов (CSS). Пресет меняет вид, анализ — данные.");
            builder.AppendLine();
            builder.AppendLine("Верхний уровень:");
            builder.AppendLine("  schemaVersion — сейчас 1, не меняйте.");
            builder.AppendLine("  defaultEngineId — id движка, напр. mania-map-analyser-headless");
            builder.AppendLine("  defaultAlgorithm — Sunny | Daniel | Azusa | Roxy | Mixed | Companella");
            builder.AppendLine("  configurationVersion — версия конфига, влияет на кэш (меняйте при смене Options)");
            builder.AppendLine("  defaultOptions — словарь опций анализатора (JSON-объект), напр. {\"withEtterna\": true}");
            builder.AppendLine("  widgets — массив виджетов");
            builder.AppendLine();
            builder.AppendLine("Виджет (widgets[]):");
            builder.AppendLine("  widgetId — уникальный id виджета, напр. headless-overlay");
            builder.AppendLine("  sources[] — источники анализа для этого виджета");
            builder.AppendLine("  bindings[] — привязки метрик");
            builder.AppendLine();
            builder.AppendLine("Источник (sources[]):");
            builder.AppendLine("  sourceId — уникальный id источника внутри виджета, напр. headless-primary");
            builder.AppendLine("  engineId — должен совпадать с активным движком");
            builder.AppendLine("  requestedAlgorithm — алгоритм для этого источника (может отличаться от defaultAlgorithm)");
            builder.AppendLine("  configurationVersion — версия именно этого источника");
            builder.AppendLine("  options — доп. опции источника (перекрывают defaultOptions)");
            builder.AppendLine("  → rate/mods и содержимое карты подставляются автоматически из tosu, не указывайте speedRate вручную");
            builder.AppendLine();
            builder.AppendLine("Привязка (bindings[]):");
            builder.AppendLine("  targetMetricId — куда в виджете писать результат, напр. difficulty.star");
            builder.AppendLine("  candidates[] — приоритетный список откуда брать метрику: { sourceId, metricId }");
            builder.AppendLine("  allowsNull — если true, JSON null считается валидным значением, иначе ищется следующий кандидат");
            builder.AppendLine("  Порядок candidates важен: первая успешная метрика используется, при fallback пишется warning в лог");
            builder.AppendLine();
            builder.AppendLine("Как собрать:");
            builder.AppendLine("  • Один источник → один виджет: widgets=[{ widgetId, sources=[{sourceId, engineId}], bindings=[{targetMetricId, candidates:[{sourceId, metricId}]}] }]");
            builder.AppendLine("  • Несколько источников на виджет: sources=[primary, secondary], bindings с candidates=[{primary, metric}, {secondary, metric}]");
            builder.AppendLine("  • Несколько виджетов с шарингом: widgets=[widgetA, widgetB] с sources, указывающими на один и тот же движок — координатор дедуплицирует одинаковые AnalysisRequest");
            builder.AppendLine("  • Сцена headless-scene: все widgets выполняются атомарно в одном Generation; смена карты/rate/mods/config сбрасывает Generation и отменяет старые задачи");
            builder.AppendLine();
            builder.AppendLine("Пример — два источника, один виджет с fallback:");
            builder.AppendLine("  {");
            builder.AppendLine("    \"defaultEngineId\": \"mania-map-analyser-headless\",");
            builder.AppendLine("    \"defaultAlgorithm\": \"Mixed\",");
            builder.AppendLine("    \"configurationVersion\": \"1\",");
            builder.AppendLine("    \"widgets\": [{");
            builder.AppendLine("      \"widgetId\": \"headless-overlay\",");
            builder.AppendLine("      \"sources\": [");
            builder.AppendLine("        { \"sourceId\": \"primary\", \"engineId\": \"mania-map-analyser-headless\", \"requestedAlgorithm\": \"Mixed\", \"configurationVersion\": \"1\" },");
            builder.AppendLine("        { \"sourceId\": \"fallback\", \"engineId\": \"mania-map-analyser-headless\", \"requestedAlgorithm\": \"Sunny\", \"configurationVersion\": \"1\" }");
            builder.AppendLine("      ],");
            builder.AppendLine("      \"bindings\": [{");
            builder.AppendLine("        \"targetMetricId\": \"difficulty.star\",");
            builder.AppendLine("        \"candidates\": [ { \"sourceId\": \"primary\", \"metricId\": \"difficulty.star\" }, { \"sourceId\": \"fallback\", \"metricId\": \"difficulty.star\" } ]");
            builder.AppendLine("      }]");
            builder.AppendLine("    }]");
            builder.AppendLine("  }");
            builder.AppendLine();
            builder.AppendLine("Валидация:");
            builder.AppendLine("  • widgetId, sourceId, targetMetricId — обязательны, дубликаты запрещены");
            builder.AppendLine("  • candidates должны ссылаться на существующие sourceId этого же виджета");
            builder.AppendLine("  • При ошибке — лог application.log и статус [DOM Fallback], тихого fallback на другой пресет нет");
            builder.AppendLine();
            builder.AppendLine("Где смотреть доступное:");
            builder.AppendLine("  Ниже в этом окне — обнаруженные движки и их capabilities.");
            builder.AppendLine("  rate/mods берутся из tosu автоматически, не настраиваются в JSON.");
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("Analysis mapping — how to configure");
            builder.AppendLine();
            builder.AppendLine("File: %LOCALAPPDATA%\\ManiaMapAnalyzerOverlay\\analysis-configuration.json");
            builder.AppendLine("Separate from visual presets (CSS). Preset changes appearance, mapping changes data.");
            builder.AppendLine();
            builder.AppendLine("Top level:");
            builder.AppendLine("  schemaVersion — currently 1, do not change.");
            builder.AppendLine("  defaultEngineId — engine id, e.g. mania-map-analyser-headless");
            builder.AppendLine("  defaultAlgorithm — Sunny | Daniel | Azusa | Roxy | Mixed | Companella");
            builder.AppendLine("  configurationVersion — config version, affects cache (bump when Options change)");
            builder.AppendLine("  defaultOptions — analyzer options dictionary, e.g. {\"withEtterna\": true}");
            builder.AppendLine("  widgets — array of widgets");
            builder.AppendLine();
            builder.AppendLine("Widget (widgets[]):");
            builder.AppendLine("  widgetId — unique widget id, e.g. headless-overlay");
            builder.AppendLine("  sources[] — analysis sources for this widget");
            builder.AppendLine("  bindings[] — metric bindings");
            builder.AppendLine();
            builder.AppendLine("Source (sources[]):");
            builder.AppendLine("  sourceId — unique source id inside widget, e.g. headless-primary");
            builder.AppendLine("  engineId — must match active engine");
            builder.AppendLine("  requestedAlgorithm — algorithm for this source (may differ from defaultAlgorithm)");
            builder.AppendLine("  configurationVersion — version for this source");
            builder.AppendLine("  options — extra source options (override defaultOptions)");
            builder.AppendLine("  → rate/mods and beatmap content are injected automatically from tosu, do not set speedRate manually");
            builder.AppendLine();
            builder.AppendLine("Binding (bindings[]):");
            builder.AppendLine("  targetMetricId — widget field to write, e.g. difficulty.star");
            builder.AppendLine("  candidates[] — prioritized list of { sourceId, metricId }");
            builder.AppendLine("  allowsNull — if true, JSON null is a valid value, otherwise next candidate is tried");
            builder.AppendLine("  Order matters: first successful metric wins, fallback is logged as warning");
            builder.AppendLine();
            builder.AppendLine("How to compose:");
            builder.AppendLine("  • One source → one widget: widgets=[{ widgetId, sources=[{sourceId, engineId}], bindings=[{targetMetricId, candidates:[{sourceId, metricId}]}] }]");
            builder.AppendLine("  • Many sources per widget: sources=[primary, secondary], bindings with candidates=[{primary, metric}, {secondary, metric}]");
            builder.AppendLine("  • Many widgets sharing results: widgets=[widgetA, widgetB] with sources pointing to same engine — coordinator de-duplicates identical AnalysisRequest");
            builder.AppendLine("  • Scene headless-scene: all widgets run atomically in one Generation; map/rate/mods/config change cancels previous generation");
            builder.AppendLine();
            builder.AppendLine("Example — two sources, one widget with fallback:");
            builder.AppendLine("  {");
            builder.AppendLine("    \"defaultEngineId\": \"mania-map-analyser-headless\",");
            builder.AppendLine("    \"defaultAlgorithm\": \"Mixed\",");
            builder.AppendLine("    \"configurationVersion\": \"1\",");
            builder.AppendLine("    \"widgets\": [{");
            builder.AppendLine("      \"widgetId\": \"headless-overlay\",");
            builder.AppendLine("      \"sources\": [");
            builder.AppendLine("        { \"sourceId\": \"primary\", \"engineId\": \"mania-map-analyser-headless\", \"requestedAlgorithm\": \"Mixed\", \"configurationVersion\": \"1\" },");
            builder.AppendLine("        { \"sourceId\": \"fallback\", \"engineId\": \"mania-map-analyser-headless\", \"requestedAlgorithm\": \"Sunny\", \"configurationVersion\": \"1\" }");
            builder.AppendLine("      ],");
            builder.AppendLine("      \"bindings\": [{");
            builder.AppendLine("        \"targetMetricId\": \"difficulty.star\",");
            builder.AppendLine("        \"candidates\": [ { \"sourceId\": \"primary\", \"metricId\": \"difficulty.star\" }, { \"sourceId\": \"fallback\", \"metricId\": \"difficulty.star\" } ]");
            builder.AppendLine("      }]");
            builder.AppendLine("    }]");
            builder.AppendLine("  }");
            builder.AppendLine();
            builder.AppendLine("Validation:");
            builder.AppendLine("  • widgetId, sourceId, targetMetricId are required, duplicates are rejected");
            builder.AppendLine("  • candidates must reference existing sourceId of the same widget");
            builder.AppendLine("  • On error — application.log and status [DOM Fallback], no silent preset fallback");
            builder.AppendLine();
            builder.AppendLine("Where to see what's available:");
            builder.AppendLine("  Below — discovered engines and their capabilities.");
            builder.AppendLine("  rate/mods are taken from tosu automatically, do not configure in JSON.");
            builder.AppendLine();
        }

        builder.AppendLine("—");
        builder.AppendLine(isRu ? "Обнаруженные движки:" : "Discovered engines:");
        var engines = _catalog.List();
        if (engines.Count == 0)
        {
            builder.AppendLine(isRu ? "  (не найдено, проверьте Assets/analyzer-engines)" : "  (none, check Assets/analyzer-engines)");
        }
        else
        {
            foreach (var package in engines)
            {
                var available = package.IsAvailable ? "available" : "unavailable";
                builder.AppendLine($"  • {package.Id ?? "(no id)"} v{package.Version ?? "?"} [{available}]");
                if (package.Manifest is not null)
                {
                    var caps = package.Manifest.Capabilities;
                    if (caps?.Algorithms is not null)
                    {
                        builder.AppendLine($"    algorithms: {string.Join(", ", caps.Algorithms)}");
                    }

                    if (caps?.SemanticMetricIds is not null)
                    {
                        var preview = caps.SemanticMetricIds.Count > 12
                            ? string.Join(", ", caps.SemanticMetricIds.Take(12)) + ", …"
                            : string.Join(", ", caps.SemanticMetricIds);
                        builder.AppendLine($"    metricIds: {preview}");
                    }

                    if (caps?.OptionalAlgorithms is not null && caps.OptionalAlgorithms.Count > 0)
                    {
                        builder.AppendLine($"    optional: {string.Join(", ", caps.OptionalAlgorithms.Keys)}");
                    }

                    if (package.Diagnostics.Count > 0)
                    {
                        builder.AppendLine($"    diagnostics: {string.Join("; ", package.Diagnostics.Take(3).Select(diagnostic => diagnostic.Code))}");
                    }
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine(isRu
            ? "Подсказка: нажмите Open file чтобы править JSON во внешнем редакторе, затем Save."
            : "Tip: click Open file to edit JSON externally, then Save.");

        HelpText.Text = builder.ToString();
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
