using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ManiaMapAnalyzerOverlay.Avalonia.Views;

public partial class MessageDialog : Window
{
    public MessageDialog() => InitializeComponent();

    public MessageDialog(string title, string message, string yesText, string? noText = null) : this()
    {
        Title = title;
        MessageText.Text = message;
        YesButton.Content = yesText;
        if (string.IsNullOrWhiteSpace(noText)) NoButton.IsVisible = false;
        else NoButton.Content = noText;
    }

    private void Yes_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void No_Click(object? sender, RoutedEventArgs e) => Close(false);
}
