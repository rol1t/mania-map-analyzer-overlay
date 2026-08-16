using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ManiaMapAnalyzerOverlay.Avalonia.Models;
using ManiaMapAnalyzerOverlay.Avalonia.Services;

namespace ManiaMapAnalyzerOverlay.Avalonia.Views;

public partial class AppearanceDialog : Window
{
    private readonly bool english;
    public bool OpenAnalyzerSettings { get; private set; }

    public AppearanceDialog() => InitializeComponent();

    public AppearanceDialog(LauncherSettings settings, bool english) : this()
    {
        this.english = english;
        Title = Pick("Оформление оверлея", "Overlay appearance");
        HeadingText.Text = Title;
        LayoutLabel.Text = Pick("Расположение", "Layout");
        ScaleLabel.Text = Pick("Размер", "Size");
        EditCssButton.Content = Pick("Открыть CSS", "Open CSS");
        AnalyzerSettingsButton.Content = Pick("Параметры анализатора", "Analyser settings");
        CancelButton.Content = Pick("Отмена", "Cancel");
        ApplyButton.Content = Pick("Применить", "Apply");
        var labels = new[]
        {
            Pick("По умолчанию", "Default"), Pick("Горизонтальный", "Horizontal"),
            "Companella", Pick("Пользовательский CSS", "Custom CSS")
        };
        for (var i = 0; i < LayoutBox.ItemCount; i++)
            ((ComboBoxItem)LayoutBox.Items[i]!).Content = labels[i];
        var selected = LayoutBox.Items.Cast<ComboBoxItem>().FirstOrDefault(x =>
            string.Equals(x.Tag?.ToString(), OverlayPresentationService.NormalizeLayout(settings.OverlayLayoutMode)));
        LayoutBox.SelectedItem = selected ?? LayoutBox.Items[0];
        ScaleSlider.Value = settings.OverlayScalePercent;
        UpdateDescription();
    }

    public string LayoutMode => (LayoutBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "default";
    public int ScalePercent => (int)ScaleSlider.Value;

    private string Pick(string ru, string en) => english ? en : ru;
    private void LayoutBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateDescription();
    private void ScaleSlider_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ScaleValueText is not null)
            ScaleValueText.Text = ((int)e.NewValue) + "%";
    }

    private void UpdateDescription()
    {
        if (DescriptionText is null) return;
        DescriptionText.Text = LayoutMode switch
        {
            "horizontal" => Pick("Широкая компактная панель для верхней или нижней части экрана.", "A wide compact panel for the top or bottom of the screen."),
            "companella" => Pick("Компактная карточка с фоном карты, сводкой и широкими графиками.", "Compact card with beatmap artwork, summary data and wide charts."),
            "custom" => Pick("Использует редактируемый overlay-custom.css рядом с приложением.", "Uses the editable overlay-custom.css next to the application."),
            _ => Pick("Классическое вертикальное оформление анализатора.", "Classic vertical analyser layout.")
        };
        EditCssButton.IsEnabled = LayoutMode == "custom";
    }

    private void EditCss_Click(object? sender, RoutedEventArgs e)
    {
        CustomCssService.EnsureExists();
        Process.Start(new ProcessStartInfo(CustomCssService.Path) { UseShellExecute = true });
    }
    private void AnalyzerSettings_Click(object? sender, RoutedEventArgs e)
    {
        OpenAnalyzerSettings = true;
        Close(true);
    }
    private void Apply_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
