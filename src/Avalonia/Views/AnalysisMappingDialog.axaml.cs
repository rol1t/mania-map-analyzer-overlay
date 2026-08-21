using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;
using ManiaMapAnalyzerOverlay.Avalonia.Models;
using ManiaMapAnalyzerOverlay.Avalonia.Services;

namespace ManiaMapAnalyzerOverlay.Avalonia.Views;

public partial class AnalysisMappingDialog : Window
{
    private readonly EffectiveAnalysisConfigurationStore _store = new();
    private EffectiveAnalysisConfiguration _configuration = EffectiveAnalysisConfigurationStore.CreateDefault();

    public AnalysisMappingDialog()
    {
        InitializeComponent();
        ApplyLanguage();
        LoadConfiguration();
    }

    private void ApplyLanguage()
    {
        Title = L("mapping.title");
        HeadingText.Text = Title;
        HintText.Markdown = L("mapping.hint") + " " + L("mapping.hint_link");
        OpenFileButton.Content = L("mapping.open_file");
        CancelButton.Content = L("appearance.cancel");
        SaveButton.Content = L("appearance.apply");
        UpdateOptionsText();
    }

    private void UpdateOptionsText()
    {
        try
        {
            var catalog = new AnalyzerEngineCatalog();
            var engines = catalog.List();
            var builder = new StringBuilder();
            builder.AppendLine(L("mapping.available_title"));
            if (engines.Count == 0)
            {
                builder.AppendLine(L("mapping.no_engines"));
            }
            else
            {
                foreach (var package in engines)
                {
                    var available = package.IsAvailable ? L("mapping.available") : L("mapping.unavailable");
                    builder.AppendLine($"• {package.Id ?? "?"} v{package.Version ?? "?"} [{available}]");
                    if (package.Manifest?.Capabilities is not null)
                    {
                        var caps = package.Manifest.Capabilities;
                        if (caps.Algorithms is not null && caps.Algorithms.Count > 0)
                        {
                            builder.AppendLine($"  {L("mapping.algorithms")}: {string.Join(", ", caps.Algorithms)}");
                        }

                        if (caps.SemanticMetricIds is not null && caps.SemanticMetricIds.Count > 0)
                        {
                            var preview = caps.SemanticMetricIds.Count > 8
                                ? string.Join(", ", caps.SemanticMetricIds.Take(8)) + ", …"
                                : string.Join(", ", caps.SemanticMetricIds);
                            builder.AppendLine($"  {L("mapping.metrics")}: {preview}");
                        }
                    }
                }
            }

            builder.AppendLine();
            builder.AppendLine(L("mapping.config_fields"));
            builder.AppendLine(L("mapping.config_fields_details"));
            builder.AppendLine(L("mapping.hint_click_help"));
            OptionsText.Text = builder.ToString();
        }
        catch (Exception exception)
        {
            OptionsText.Text = L("mapping.options_error") + ": " + exception.Message;
        }
    }

    private async void Help_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new DocumentationDialog("mapping");
            await dialog.ShowDialog(this);
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
            ErrorText.IsVisible = true;
        }
    }

    private void LoadConfiguration()
    {
        try
        {
            _configuration = _store.Load();
            var json = JsonSerializer.Serialize(_configuration, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            JsonBox.Text = json;
            ErrorText.IsVisible = false;
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
            ErrorText.IsVisible = true;
        }
    }

    private string L(string key) => ManiaMapAnalyzerOverlay.UiText.Get(key);

    private void OpenFile_Click(object? sender, RoutedEventArgs e)
    {
        var path = AppPaths.EffectiveAnalysisConfigurationPath;
        try
        {
            if (!File.Exists(path))
            {
                _store.Save(_configuration);
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
            ErrorText.IsVisible = true;
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var text = JsonBox.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidDataException("Configuration JSON is empty.");
            }

            var parsed = JsonSerializer.Deserialize<EffectiveAnalysisConfiguration>(
                text,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip, PropertyNameCaseInsensitive = true });
            if (parsed is null)
            {
                throw new InvalidDataException("Deserialized configuration is null.");
            }

            var normalized = parsed.Normalize();
            _store.Save(normalized);
            Close(true);
        }
        catch (Exception exception)
        {
            ErrorText.Text = L("mapping.save_error") + ": " + exception.Message;
            ErrorText.IsVisible = true;
            AppLogger.Error("Saving effective analysis mapping", exception);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
