using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
        HintText.Text = L("mapping.hint");
        OpenFileButton.Content = L("mapping.open_file");
        CancelButton.Content = L("appearance.cancel");
        SaveButton.Content = L("appearance.apply");
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
