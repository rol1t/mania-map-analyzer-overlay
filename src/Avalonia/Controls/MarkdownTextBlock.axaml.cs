using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ManiaMapAnalyzerOverlay.Avalonia.Services;

namespace ManiaMapAnalyzerOverlay.Avalonia.Controls;

public partial class MarkdownTextBlock : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownTextBlock()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
        {
            Render();
        }
    }

    private void Render()
    {
        if (ContentPanel is null)
        {
            return;
        }

        ContentPanel.Children.Clear();
        var text = Markdown ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Simple inline parsing: links, bold, code
        var linkPattern = new Regex(@"\[(.+?)\]\((.+?)\)", RegexOptions.Compiled);
        var codePattern = new Regex(@"`(.+?)`", RegexOptions.Compiled);
        var boldPattern = new Regex(@"\*\*(.+?)\*\*", RegexOptions.Compiled);

        var segments = new List<Segment> { new Segment { Text = text, Type = SegmentType.Plain } };
        segments = SplitByPattern(segments, linkPattern, match => new Segment { Text = match.Groups[1].Value, Url = match.Groups[2].Value, Type = SegmentType.Link });
        segments = SplitByPattern(segments, codePattern, match => new Segment { Text = match.Groups[1].Value, Type = SegmentType.Code });
        segments = SplitByPattern(segments, boldPattern, match => new Segment { Text = match.Groups[1].Value, Type = SegmentType.Bold });

        foreach (var segment in segments)
        {
            switch (segment.Type)
            {
                case SegmentType.Link:
                    var link = new DocumentationLink
                    {
                        NavigateUri = segment.Url,
                        Text = segment.Text,
                        Margin = new Thickness(0, 0, 2, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    ContentPanel.Children.Add(link);
                    break;
                case SegmentType.Code:
                    var codeBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#242836")),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 1, 4, 1),
                        Margin = new Thickness(2, 0, 2, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    codeBorder.Child = new TextBlock
                    {
                        Text = segment.Text,
                        FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                        FontSize = FontSize - 1
                    };
                    ContentPanel.Children.Add(codeBorder);
                    break;
                case SegmentType.Bold:
                    ContentPanel.Children.Add(new TextBlock
                    {
                        Text = segment.Text,
                        FontWeight = FontWeight.Bold,
                        FontSize = FontSize,
                        Margin = new Thickness(0, 0, 2, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    break;
                default:
                    // Split plain text by words to allow wrapping, but keep as single TextBlock for simplicity
                    // Use TextBlock with Wrapping
                    ContentPanel.Children.Add(new TextBlock
                    {
                        Text = segment.Text,
                        FontSize = FontSize,
                        Margin = new Thickness(0, 0, 2, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap
                    });
                    break;
            }
        }
    }

    private List<Segment> SplitByPattern(List<Segment> segments, Regex pattern, Func<Match, Segment> creator)
    {
        var result = new List<Segment>();
        foreach (var segment in segments)
        {
            if (segment.Type != SegmentType.Plain)
            {
                result.Add(segment);
                continue;
            }

            var text = segment.Text;
            var lastIndex = 0;
            foreach (Match match in pattern.Matches(text))
            {
                if (match.Index > lastIndex)
                {
                    result.Add(new Segment { Text = text.Substring(lastIndex, match.Index - lastIndex), Type = SegmentType.Plain });
                }

                result.Add(creator(match));
                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < text.Length)
            {
                result.Add(new Segment { Text = text.Substring(lastIndex), Type = SegmentType.Plain });
            }
        }

        return result;
    }

    private enum SegmentType
    {
        Plain,
        Bold,
        Code,
        Link
    }

    private sealed class Segment
    {
        public string Text { get; init; } = string.Empty;
        public string? Url
        {
            get; init;
        }
        public SegmentType Type
        {
            get; init;
        }
    }
}
