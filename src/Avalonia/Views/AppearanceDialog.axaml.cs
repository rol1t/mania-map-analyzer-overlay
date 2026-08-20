using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ManiaMapAnalyzerOverlay.Avalonia.Analyzers;
using ManiaMapAnalyzerOverlay.Avalonia.Models;
using ManiaMapAnalyzerOverlay.Avalonia.Services;

namespace ManiaMapAnalyzerOverlay.Avalonia.Views;

public partial class AppearanceDialog : Window
{
    private readonly OverlayPresetCatalog presets = new();
    private readonly AnalyzerAdapterCatalog analyzers = new();
    private bool resourcesAvailable;
    private bool analyzerResourcesAvailable;
    private bool closing;
    private LauncherSettings? previewBaseSettings;
    public bool OpenAnalyzerSettings
    {
        get; private set;
    }
    public event Action<LauncherSettings>? PreviewChanged;

    public AppearanceDialog() => InitializeComponent();

    public AppearanceDialog(LauncherSettings settings) : this()
    {
        previewBaseSettings = settings.Clone();
        Title = L("appearance.title");
        HeadingText.Text = Title;
        LayoutLabel.Text = L("appearance.layout");
        AnalyzerLabel.Text = L("appearance.analyzer");
        ScaleLabel.Text = L("appearance.size");
        EditCssButton.Content = L("appearance.open_css");
        AnalyzerSettingsButton.Content = L("appearance.analyzer_settings");
        CancelButton.Content = L("appearance.cancel");
        ApplyButton.Content = L("appearance.apply");
        LayoutBox.Items.Clear();
        AddLayoutOption("default", "appearance.layout_default");
        AddLayoutOption("horizontal", "appearance.layout_horizontal");
        AddLayoutOption("companella", "appearance.layout_companella");
        AddLayoutOption("custom", "appearance.layout_custom");
        var definitions = presets.List();
        resourcesAvailable = definitions.Count > 0;
        foreach (var preset in definitions)
        {
            if (LayoutBox.Items.Cast<ComboBoxItem>().Any(item =>
                    string.Equals(item.Tag?.ToString(), preset.Id, StringComparison.OrdinalIgnoreCase)))
                continue;
            var item = new ComboBoxItem
            {
                Tag = preset.Id,
                Content = UiText.IsEnglish ? preset.Name : preset.NameRu ?? preset.Name
            };
            LayoutBox.Items.Add(item);
        }

        if (!resourcesAvailable)
        {
            LayoutBox.Items.Add(new ComboBoxItem
            {
                Tag = "missing",
                Content = L("appearance.resources_missing")
            });
        }

        var requestedId = string.IsNullOrWhiteSpace(settings.OverlayPresetId) ||
                          (settings.OverlayPresetId == "default" && settings.OverlayLayoutMode != "default")
            ? settings.OverlayLayoutMode
            : settings.OverlayPresetId;
        var selected = LayoutBox.Items.Cast<ComboBoxItem>().FirstOrDefault(x =>
            string.Equals(x.Tag?.ToString(), requestedId, StringComparison.OrdinalIgnoreCase));
        LayoutBox.SelectedItem = selected ?? LayoutBox.Items[0];

        AnalyzerBox.Items.Clear();
        var analyzerPackages = analyzers.List();
        analyzerResourcesAvailable = analyzerPackages.Count > 0;
        foreach (var package in analyzerPackages)
        {
            AnalyzerBox.Items.Add(new ComboBoxItem
            {
                Tag = package.Descriptor.Id,
                Content = package.Descriptor.Name
            });
        }
        if (!analyzerResourcesAvailable)
        {
            AnalyzerBox.Items.Add(new ComboBoxItem
            {
                Tag = "missing",
                Content = L("appearance.analyzer_resources_missing")
            });
        }
        var selectedAnalyzer = AnalyzerBox.Items.Cast<ComboBoxItem>().FirstOrDefault(x =>
            string.Equals(x.Tag?.ToString(), settings.AnalyzerProviderId, StringComparison.OrdinalIgnoreCase));
        AnalyzerBox.SelectedItem = selectedAnalyzer ?? AnalyzerBox.Items[0];

        ApplyButton.IsEnabled = resourcesAvailable && analyzerResourcesAvailable;
        ScaleSlider.Value = settings.OverlayScalePercent;
        UpdateDescription();
        UpdateAnalyzerSettingsState();
    }

    public string LayoutMode => (LayoutBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "default";
    public string PresetId => LayoutMode;
    public string AnalyzerProviderId =>
        (AnalyzerBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "mania-map-analyser";
    public int ScalePercent => (int)ScaleSlider.Value;

    private string L(string key) => ManiaMapAnalyzerOverlay.UiText.Get(key);

    private void AddLayoutOption(string id, string labelKey) => LayoutBox.Items.Add(new ComboBoxItem
    {
        Tag = id,
        Content = L(labelKey)
    });
    private void LayoutBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateDescription();
        RaisePreviewChanged();
    }
    private void AnalyzerBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateAnalyzerSettingsState();
        RaisePreviewChanged();
    }
    private void ScaleSlider_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ScaleValueText is not null)
            ScaleValueText.Text = ((int)e.NewValue) + "%";
        RaisePreviewChanged();
    }

    private void ScaleDown_Click(object? sender, RoutedEventArgs e) =>
        ScaleSlider.Value = Math.Max(50, ScaleSlider.Value - 5);

    private void ScaleUp_Click(object? sender, RoutedEventArgs e) =>
        ScaleSlider.Value = Math.Min(180, ScaleSlider.Value + 5);

    private void RaisePreviewChanged()
    {
        if (closing || previewBaseSettings is null || LayoutBox is null || AnalyzerBox is null)
            return;
        var preview = previewBaseSettings.Clone();
        preview.OverlayLayoutMode = LayoutMode;
        preview.OverlayPresetId = PresetId;
        preview.AnalyzerProviderId = AnalyzerProviderId;
        preview.OverlayScalePercent = ScalePercent;
        PreviewChanged?.Invoke(preview);
    }

    private void UpdateDescription()
    {
        if (DescriptionText is null)
            return;
        if (!resourcesAvailable)
        {
            DescriptionText.Text = L("appearance.resources_missing_description");
            EditCssButton.IsEnabled = false;
            return;
        }
        var preset = presets.Get(LayoutMode);
        DescriptionText.Text = UiText.IsEnglish ? preset.Description : preset.DescriptionRu ?? preset.Description;
        EditCssButton.IsEnabled = true;
    }

    private void UpdateAnalyzerSettingsState()
    {
        if (AnalyzerSettingsButton is null)
            return;
        if (!analyzerResourcesAvailable)
        {
            AnalyzerSettingsButton.IsEnabled = false;
            return;
        }

        AnalyzerSettingsButton.IsEnabled = analyzers.List().Any(package =>
            string.Equals(package.Descriptor.Id, AnalyzerProviderId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(package.Descriptor.SettingsPath));
    }

    private void EditCss_Click(object? sender, RoutedEventArgs e)
    {
        var preset = presets.Get(LayoutMode);
        var path = Path.Combine(presets.ResolveDirectory(preset.Id), preset.Stylesheet);
        if (!File.Exists(path))
        {
            CustomCssService.EnsureExists();
            path = CustomCssService.Path;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    private void AnalyzerSettings_Click(object? sender, RoutedEventArgs e)
    {
        closing = true;
        OpenAnalyzerSettings = true;
        Close(true);
    }
    private void Apply_Click(object? sender, RoutedEventArgs e)
    {
        closing = true;
        Close(true);
    }
    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        closing = true;
        Close(false);
    }
}
