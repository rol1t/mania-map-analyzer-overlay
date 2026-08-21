using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ManiaMapAnalyzerOverlay.Avalonia.Services;

namespace ManiaMapAnalyzerOverlay.Avalonia.Controls;

public partial class DocumentationLink : UserControl
{
    public static readonly StyledProperty<string?> NavigateUriProperty =
        AvaloniaProperty.Register<DocumentationLink, string?>(nameof(NavigateUri));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<DocumentationLink, string?>(nameof(Text));

    public string? NavigateUri
    {
        get => GetValue(NavigateUriProperty);
        set => SetValue(NavigateUriProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public DocumentationLink()
    {
        InitializeComponent();
        UpdateText();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == NavigateUriProperty)
        {
            UpdateText();
        }
    }

    private void UpdateText()
    {
        if (LinkText is not null)
        {
            LinkText.Text = Text ?? NavigateUri ?? string.Empty;
        }
    }

    private void LinkButton_Click(object? sender, RoutedEventArgs e)
    {
        var uri = NavigateUri;
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        // Try to find owner window for modal dialog
        var owner = TopLevel.GetTopLevel(this) as Window;
        DocumentationNavigator.Open(uri, owner);
    }
}
